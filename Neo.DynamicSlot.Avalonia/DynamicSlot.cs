using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Neo.AssemblyForge.Slot;

namespace Neo.DynamicSlot.Avalonia;

/// <summary>
/// A dynamically programmable UserControl that end-users can fill with
/// AI-generated content at runtime. Place it like any regular control.
/// </summary>
public sealed class DynamicSlot : UserControl, IDisposable
{
    private readonly string _slotId = Guid.NewGuid().ToString("N");
    private readonly int _depth;

    // UI elements
    private Grid? _rootGrid;
    private Border? _placeholderBorder;
    private TextBox? _promptInput;
    private Button? _submitButton;
    private TextBlock? _statusText;
    private ProgressBar? _progressBar;
    private ContentControl? _slotContent;
    private Border? _errorBorder;
    private TextBlock? _errorText;
    private Button? _retryButton;

    // State
    private SlotLoadContext? _slotLoadContext;
    private CancellationTokenSource? _compileCts;
    private bool _disposed;

    private enum SlotState { Idle, Compiling, Loaded, Error }

    /// <summary>
    /// Placeholder text shown in the input field.
    /// </summary>
    public string Placeholder { get; set; } = "What should appear here?";

    /// <summary>
    /// Data from the parent application that should be available to the generated fragment.
    /// The AI is told about these entries so it can generate code that uses them.
    /// The fragment receives this dictionary as its DataContext.
    /// </summary>
    public Dictionary<string, object> SharedData { get; } = new();

    /// <summary>
    /// Optional delegate for executing database queries from the generated fragment.
    /// The parent app wires this to its database connection.
    /// Each returned row is a Dictionary with column names as keys.
    /// </summary>
    public Func<string, Task<List<Dictionary<string, object>>>>? QueryAsync { get; set; }

    /// <summary>
    /// Optional hint describing the database schema so the AI can write correct SQL.
    /// Example: "Tables: Sales(Id INT, Monat VARCHAR, Umsatz DECIMAL, Menge INT)"
    /// </summary>
    public string? SchemaHint { get; set; }

    public DynamicSlot() : this(0) { }

    internal DynamicSlot(int depth)
    {
        _depth = depth;
        AttachedToVisualTree += OnAttached;
        DetachedFromVisualTree += OnDetached;
    }

    private void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (_rootGrid == null)
            BuildUI();
    }

    private void OnDetached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        Dispose();
    }

    private void BuildUI()
    {
        _rootGrid = new Grid();
        _rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // --- Placeholder (Row 0) ---
        _placeholderBorder = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(45, 45, 48)),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12),
            Margin = new Thickness(4),
        };

        var inputPanel = new DockPanel();

        _submitButton = new Button
        {
            Content = "\u25B6",
            Width = 36,
            Height = 36,
            FontSize = 14,
            Background = new SolidColorBrush(Color.FromRgb(0, 122, 204)),
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        _submitButton.Click += OnSubmitClicked;
        DockPanel.SetDock(_submitButton, global::Avalonia.Controls.Dock.Right);
        inputPanel.Children.Add(_submitButton);

        _promptInput = new TextBox
        {
            FontSize = 14,
            Foreground = Brushes.White,
            Background = new SolidColorBrush(Color.FromRgb(30, 30, 30)),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromRgb(63, 63, 70)),
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(0, 0, 8, 0),
            Watermark = Placeholder,
            VerticalContentAlignment = global::Avalonia.Layout.VerticalAlignment.Center,
        };
        _promptInput.KeyDown += (s, e) =>
        {
            if (e.Key == Key.Enter)
                OnSubmitClicked(s, e);
        };
        inputPanel.Children.Add(_promptInput);

        _placeholderBorder.Child = inputPanel;
        Grid.SetRow(_placeholderBorder, 0);
        _rootGrid.Children.Add(_placeholderBorder);

        // --- Loading indicator (Row 0, overlays placeholder) ---
        _progressBar = new ProgressBar
        {
            IsIndeterminate = true,
            Height = 3,
            VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Bottom,
            IsVisible = false,
        };
        Grid.SetRow(_progressBar, 0);
        _rootGrid.Children.Add(_progressBar);

        _statusText = new TextBlock
        {
            Text = "Generating...",
            Foreground = new SolidColorBrush(Color.FromRgb(150, 150, 150)),
            FontSize = 12,
            HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center,
            IsVisible = false,
        };
        Grid.SetRow(_statusText, 1);
        _rootGrid.Children.Add(_statusText);

        // --- Content area (Row 1) ---
        _slotContent = new ContentControl();
        Grid.SetRow(_slotContent, 1);
        _rootGrid.Children.Add(_slotContent);

        // --- Error display (Row 1, overlays content) ---
        _errorBorder = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(230, 60, 20, 20)),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12),
            Margin = new Thickness(4),
            IsVisible = false,
        };

        var errorPanel = new StackPanel();
        _errorText = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(255, 120, 120)),
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
        };
        errorPanel.Children.Add(_errorText);

        _retryButton = new Button
        {
            Content = "Try again",
            Margin = new Thickness(0, 8, 0, 0),
            HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Left,
            Padding = new Thickness(12, 4, 12, 4),
        };
        _retryButton.Click += (_, _) => SetState(SlotState.Idle);
        errorPanel.Children.Add(_retryButton);

        _errorBorder.Child = errorPanel;
        Grid.SetRow(_errorBorder, 1);
        _rootGrid.Children.Add(_errorBorder);

        Content = _rootGrid;
    }

    private void SetState(SlotState state)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => SetState(state));
            return;
        }

        switch (state)
        {
            case SlotState.Idle:
                if (_placeholderBorder != null) { _placeholderBorder.IsVisible = true; _placeholderBorder.IsEnabled = true; }
                if (_progressBar != null) _progressBar.IsVisible = false;
                if (_statusText != null) _statusText.IsVisible = false;
                if (_errorBorder != null) _errorBorder.IsVisible = false;
                if (_slotContent != null) _slotContent.IsVisible = false;
                break;

            case SlotState.Compiling:
                if (_placeholderBorder != null) _placeholderBorder.IsEnabled = false;
                if (_progressBar != null) _progressBar.IsVisible = true;
                if (_statusText != null) _statusText.IsVisible = true;
                if (_errorBorder != null) _errorBorder.IsVisible = false;
                if (_slotContent != null) _slotContent.IsVisible = false;
                break;

            case SlotState.Loaded:
                if (_placeholderBorder != null) { _placeholderBorder.IsVisible = true; _placeholderBorder.IsEnabled = true; }
                if (_progressBar != null) _progressBar.IsVisible = false;
                if (_statusText != null) _statusText.IsVisible = false;
                if (_errorBorder != null) _errorBorder.IsVisible = false;
                if (_slotContent != null) _slotContent.IsVisible = true;
                break;

            case SlotState.Error:
                if (_placeholderBorder != null) { _placeholderBorder.IsVisible = true; _placeholderBorder.IsEnabled = true; }
                if (_progressBar != null) _progressBar.IsVisible = false;
                if (_statusText != null) _statusText.IsVisible = false;
                if (_errorBorder != null) _errorBorder.IsVisible = true;
                if (_slotContent != null) _slotContent.IsVisible = false;
                break;
        }
    }

    private async void OnSubmitClicked(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e)
    {
        var prompt = _promptInput?.Text?.Trim();
        if (string.IsNullOrWhiteSpace(prompt)) return;

        if (_depth >= DynamicSlotService.MaxDepth)
        {
            ShowError("DynamicSlot nesting limit reached.");
            return;
        }

        var compiler = DynamicSlotService.Compiler;
        if (compiler == null)
        {
            ShowError("AI not configured. Set the ANTHROPIC_API_KEY, OPENAI_API_KEY, or GEMINI_API_KEY environment variable.");
            return;
        }

        _compileCts?.Cancel();
        _compileCts = new CancellationTokenSource();
        var ct = _compileCts.Token;

        SetState(SlotState.Compiling);

        try
        {
            // Serialize typed data to framework-only types (Dict/List) so the fragment
            // can use it without referencing the parent assembly's custom types
            Dictionary<string, object>? serializedData = null;
            if (SharedData.Count > 0)
                serializedData = SlotDataSerializer.Serialize(SharedData);

            // Inject QueryAsync delegate into fragment data (after serialization to skip reflection)
            if (QueryAsync != null)
            {
                serializedData ??= new Dictionary<string, object>();
                serializedData["__queryAsync"] = QueryAsync;
            }

            // Augment prompt with data description so the slot AI knows what data is available
            var dataDesc = SlotDataDescriber.Describe(SharedData);
            var augmentedPrompt = string.IsNullOrEmpty(dataDesc) ? prompt : prompt + dataDesc;

            // Add database schema hint to prompt if available
            if (!string.IsNullOrWhiteSpace(SchemaHint))
                augmentedPrompt += "\n\nDATABASE ACCESS: A '__queryAsync' function is available in the DataContext dictionary for live SQL queries.\nSchema: " + SchemaHint;

            var result = await compiler.CompileAsync(augmentedPrompt, "Avalonia", ct);

            if (ct.IsCancellationRequested) return;

            if (!result.Success || result.DllBytes == null)
            {
                ShowError(result.ErrorMessage ?? "Compilation failed.");
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(() => LoadCompiledControl(result, serializedData));
        }
        catch (OperationCanceledException) { /* new request superseded this one */ }
        catch (Exception ex)
        {
            ShowError($"Error: {ex.Message}");
        }
    }

    private void LoadCompiledControl(SlotCompileResult result, Dictionary<string, object>? fragmentData)
    {
        UnloadSlotContent();

        var managedDlls = new ConcurrentDictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        if (result.NuGetDllPaths != null)
        {
            foreach (var dllPath in result.NuGetDllPaths)
            {
                if (File.Exists(dllPath))
                {
                    try
                    {
                        var bytes = File.ReadAllBytes(dllPath);
                        var an = AssemblyName.GetAssemblyName(dllPath);
                        if (an.FullName != null)
                            managedDlls[an.FullName] = bytes;
                    }
                    catch { }
                }
            }
        }

        var plc = new SlotLoadContext(
            managedResolver: an =>
            {
                if (!string.IsNullOrEmpty(an.FullName) && managedDlls.TryGetValue(an.FullName, out var b))
                    return b;
                if (!string.IsNullOrEmpty(an.Name))
                {
                    foreach (var kv in managedDlls)
                    {
                        try
                        {
                            var candidate = new AssemblyName(kv.Key);
                            if (string.Equals(candidate.Name, an.Name, StringComparison.OrdinalIgnoreCase))
                                return kv.Value;
                        }
                        catch { }
                    }
                }
                return null;
            });
        _slotLoadContext = plc;

        using var ms = new MemoryStream(result.DllBytes!, writable: false);
        var asm = plc.LoadFromStream(ms);

        Type? controlType = null;
        if (!string.IsNullOrWhiteSpace(result.TypeName))
            controlType = asm.GetType(result.TypeName);

        controlType ??= asm.GetTypes()
            .FirstOrDefault(t => typeof(UserControl).IsAssignableFrom(t) && !t.IsAbstract);

        if (controlType == null)
        {
            ShowError("No UserControl type found in compiled fragment.");
            return;
        }

        var control = (UserControl)Activator.CreateInstance(controlType)!;
        control.DataContext = fragmentData ?? DataContext;

        _slotContent!.Content = control;
        SetState(SlotState.Loaded);
    }

    private void UnloadSlotContent()
    {
        if (_slotContent != null)
            _slotContent.Content = null;

        var old = _slotLoadContext;
        _slotLoadContext = null;

        if (old != null)
        {
            Task.Run(() =>
            {
                try { old.Dispose(); } catch { }
                try { old.Unload(); } catch { }
                for (int i = 0; i < 2; i++)
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                }
            });
        }
    }

    private void ShowError(string message)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => ShowError(message));
            return;
        }

        if (_errorText != null)
            _errorText.Text = message;
        SetState(SlotState.Error);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _compileCts?.Cancel();
        _compileCts?.Dispose();
        UnloadSlotContent();
    }
}

/// <summary>
/// Collectible AssemblyLoadContext for slot-loaded fragments.
/// Same pattern as SandboxPluginLoadContext in the child process.
/// </summary>
internal sealed class SlotLoadContext : AssemblyLoadContext, IDisposable
{
    private readonly Func<AssemblyName, byte[]?> _managedResolver;

    public SlotLoadContext(Func<AssemblyName, byte[]?> managedResolver)
        : base(isCollectible: true)
    {
        _managedResolver = managedResolver;
        Resolving += OnResolveManaged;
    }

    private Assembly? OnResolveManaged(AssemblyLoadContext alc, AssemblyName name)
    {
        var bytes = _managedResolver(name);
        if (bytes == null) return null;
        using var ms = new MemoryStream(bytes, writable: false);
        return LoadFromStream(ms);
    }

    protected override Assembly? Load(AssemblyName assemblyName) => null;

    public void Dispose()
    {
        Resolving -= OnResolveManaged;
    }
}
