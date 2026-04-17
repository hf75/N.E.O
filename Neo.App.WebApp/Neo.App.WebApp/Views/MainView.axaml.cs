using System;
using System.Collections.Specialized;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Neo.App.WebApp.Services;
using Neo.App.WebApp.Services.Ai;
using Neo.App.WebApp.Services.Sessions;

namespace Neo.App.WebApp.Views;

public partial class MainView : UserControl
{
    private AppOrchestrator? _orchestrator;
    private ISessionStore? _sessionStore;
    private CancellationTokenSource? _running;

    public MainView()
    {
        InitializeComponent();
    }

    public void Bind(AppOrchestrator orchestrator, ISessionStore sessionStore)
    {
        _orchestrator = orchestrator;
        _sessionStore = sessionStore;

        ChatList.ItemsSource = orchestrator.History;
        orchestrator.History.CollectionChanged += OnHistoryChanged;
        orchestrator.StatusChanged += OnStatusChanged;
        orchestrator.PluginHost.ContentChanged += OnPluginChanged;

        SubmitBtn.Click += OnSubmit;
        NewBtn.Click += OnNew;
        LoadBtn.Click += OnLoad;
        SaveBtn.Click += OnSave;
        ApplyMonacoBtn.Click += OnApplyMonaco;
        PromptBox.KeyDown += OnPromptKey;

        _ = LoadProvidersAsync(orchestrator);
    }

    private async System.Threading.Tasks.Task LoadProvidersAsync(AppOrchestrator orch)
    {
        try
        {
            var providers = await orch.AiClient.GetProvidersAsync();

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                ProviderCombo.ItemsSource = providers;
                ProviderCombo.DisplayMemberBinding =
                    new Avalonia.Data.Binding(nameof(AiProviderInfo.Name));
                var pref = providers.FirstOrDefault(p => p.Available)
                           ?? providers.FirstOrDefault();
                if (pref is not null)
                {
                    ProviderCombo.SelectedItem = pref;
                    orch.CurrentProviderId = pref.Id;
                }
                ProviderCombo.SelectionChanged += (_, _) =>
                {
                    if (ProviderCombo.SelectedItem is AiProviderInfo p)
                        orch.CurrentProviderId = p.Id;
                };
            });
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() =>
                StatusBox.Text = "Failed to load providers: " + ex.Message);
        }
    }

    private void OnPromptKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (e.KeyModifiers & KeyModifiers.Control) != 0)
        {
            OnSubmit(sender, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    private async void OnSubmit(object? sender, RoutedEventArgs e)
    {
        if (_orchestrator is null) return;
        var prompt = PromptBox.Text?.Trim();
        if (string.IsNullOrEmpty(prompt)) return;

        SubmitBtn.IsEnabled = false;
        _running?.Cancel();
        _running = new CancellationTokenSource();
        try
        {
            var control = await _orchestrator.ExecutePromptAsync(prompt, _running.Token);
            if (control is not null)
            {
                MountSlot.Content = control;
                CodeBox.Text = _orchestrator.LastCode;
            }
            PromptBox.Text = "";
        }
        finally
        {
            SubmitBtn.IsEnabled = true;
        }
    }

    private void OnNew(object? sender, RoutedEventArgs e)
    {
        if (_orchestrator is null) return;
        _orchestrator.History.Clear();
        _orchestrator.PluginHost.Unload();
        MountSlot.Content = null;
        CodeBox.Text = "";
        StatusBox.Text = "New session. Describe what you want to build.";
    }

    private async void OnLoad(object? sender, RoutedEventArgs e)
    {
        if (_orchestrator is null || _sessionStore is null) return;
        var host = TopLevel.GetTopLevel(this) as Window;
        var dialog = new LoadSessionDialog(_sessionStore);
        string? chosen;
        if (host is not null) chosen = await dialog.ShowDialog<string?>(host);
        else { dialog.Show(); return; }
        if (string.IsNullOrEmpty(chosen)) return;
        var session = await _sessionStore.LoadAsync(chosen);
        if (session is null)
        {
            StatusBox.Text = $"Session '{chosen}' not found.";
            return;
        }
        var ok = await _orchestrator.LoadSessionAsync(session);
        if (ok) CodeBox.Text = _orchestrator.LastCode;
    }

    private async void OnSave(object? sender, RoutedEventArgs e)
    {
        if (_orchestrator is null || _sessionStore is null) return;
        var name = $"session-{DateTime.Now:yyyyMMdd-HHmmss}";
        var session = _orchestrator.ToSession(name);
        await _sessionStore.SaveAsync(session);
        StatusBox.Text = $"Saved as {name}.neo";
        NotifySessionJson(session);
    }

    private void NotifySessionJson(NeoSession s)
    {
        // Hook point: browser head overrides this to trigger a file download.
        var json = JsonSerializer.Serialize(s,
            new JsonSerializerOptions { WriteIndented = true });
        SessionJsonReady?.Invoke(s.Name, json);
    }

    /// <summary>Raised after a save. Host can observe to trigger a file download.</summary>
    public event Action<string, string>? SessionJsonReady;

    /// <summary>Set by the host so that this view can pull code out of the Monaco iframe.</summary>
    public Func<string?>? MonacoCodeProvider { get; set; }

    /// <summary>Set by the host so that this view can push current code into the Monaco iframe.</summary>
    public Action<string>? MonacoCodeSetter { get; set; }

    private async void OnApplyMonaco(object? sender, RoutedEventArgs e)
    {
        if (_orchestrator is null) return;
        // Prefer the Monaco provider if the host wired it; else use the CodeBox.
        var code = MonacoCodeProvider?.Invoke() ?? CodeBox.Text;
        if (string.IsNullOrWhiteSpace(code)) return;
        StatusBox.Text = "Recompiling…";

        // Preserve any NuGet deps the previous prompt pulled in — a naked
        // CompileAsync would compile without those refs and fail for code
        // that uses third-party types.
        System.Collections.Generic.IReadOnlyList<Microsoft.CodeAnalysis.MetadataReference>? extraRefs = null;
        if (_orchestrator.LastDepAssemblies is { Count: > 0 })
            extraRefs = _orchestrator.LastDepAssemblies.Values
                .Select(bytes => (Microsoft.CodeAnalysis.MetadataReference)
                    Microsoft.CodeAnalysis.MetadataReference.CreateFromImage(bytes))
                .ToList();

        var res = await _orchestrator.Compiler.CompileAsync(code, "Edit_" + DateTime.UtcNow.Ticks, extraRefs);
        if (!res.Success)
        {
            StatusBox.Text = "Recompile failed: " + string.Join("; ", res.Diagnostics);
            return;
        }
        try
        {
            var control = _orchestrator.PluginHost.LoadFromBytes(
                res.AssemblyBytes!, pdbBytes: null, _orchestrator.LastDepAssemblies);
            MountSlot.Content = control;
            CodeBox.Text = code;
            StatusBox.Text = $"Recompiled in {res.CompileTimeMs} ms.";
        }
        catch (Exception ex)
        {
            var full = ex;
            while (full.InnerException is not null) full = full.InnerException;
            StatusBox.Text = "Load failed: " + full.GetType().Name + ": " + full.Message;
        }
    }

    private void OnHistoryChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Dispatcher.UIThread.Post(() => ChatScroll.ScrollToEnd());
    }

    private void OnStatusChanged(OrchestratorEvent evt)
    {
        Dispatcher.UIThread.Post(() =>
        {
            StatusBox.Text = evt.Message ?? evt.Status.ToString();
        });
    }

    private void OnPluginChanged(Control? control)
    {
        Dispatcher.UIThread.Post(() => MountSlot.Content = control);
    }
}
