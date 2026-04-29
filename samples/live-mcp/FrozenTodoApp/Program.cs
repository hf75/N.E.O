// Phase 4 Frozen-Mode demo: a single-file Avalonia TODO app that, when launched with --mcp,
// also serves an embedded stdio MCP server exposing every [McpCallable] method and every
// [McpObservable(Watchable=true)] property on the TODO control.
//
// Launch modes (set on the command line):
//   frozen-todo-app                    → GUI only (legacy export behaviour)
//   frozen-todo-app --mcp              → GUI + stdio MCP server (Claude controls, user watches)
//   frozen-todo-app --mcp --headless   → stdio MCP server, no window (server deployment)
//   frozen-todo-app --mcp-help         → dump the manifest to stderr and exit
//
// Wire-up details:
//   • Logs go to stderr; stdout is reserved for MCP JSON-RPC framing in --mcp mode.
//   • The user control is created on the UI thread by App.OnFrameworkInitializationCompleted;
//     the MCP server starts on a background Task once the control reference is captured.
//   • A CancellationTokenSource ties the MCP host to the desktop lifetime — closing the
//     window cancels the host so stdin/stdout doesn't dangle.

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Themes.Fluent;
using Neo.App;
using Neo.App.Mcp;

namespace FrozenTodoApp;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // --mcp-help: dump manifest, exit before starting Avalonia. Cheap CI smoke test.
        if (args.Contains("--mcp-help"))
        {
            NeoAppMcp.DumpManifest(new TodoUserControl());
            return 0;
        }

        return BuildAvaloniaApp(args)
            .StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp(string[] args) =>
        AppBuilder.Configure(() => new App(args))
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}

public sealed class App : Application
{
    private readonly string[] _args;
    private readonly CancellationTokenSource _mcpCts = new();

    public App() : this(Array.Empty<string>()) { }
    public App(string[] args) { _args = args; }

    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var todo = new TodoUserControl();

            desktop.MainWindow = new Window
            {
                Content = todo,
                Title = "Frozen TODO — Live-MCP demo",
                Width = 480,
                Height = 640
            };

            // Tie the MCP host to the desktop lifetime so closing the window also tears down
            // the JSON-RPC loop. Without this, the host process keeps running until stdin closes.
            desktop.Exit += (_, _) => _mcpCts.Cancel();

            if (_args.Contains("--mcp"))
            {
                Console.Error.WriteLine($"[frozen-todo-app] --mcp mode: starting embedded MCP stdio server.");
                _ = Task.Run(async () =>
                {
                    try { await NeoAppMcp.RunStdioAsync(todo, options: null, _mcpCts.Token); }
                    catch (OperationCanceledException) { /* graceful shutdown */ }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[frozen-todo-app] MCP host crashed: {ex}");
                    }
                });
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// The TODO control — same shape as samples/live-mcp/01-todo-app.cs.
// ─────────────────────────────────────────────────────────────────────────────

public sealed class TodoItem : INotifyPropertyChanged
{
    private bool _isDone;
    public string Title { get; }
    public bool IsDone
    {
        get => _isDone;
        set
        {
            if (_isDone == value) return;
            _isDone = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsDone)));
        }
    }
    public TodoItem(string title) => Title = title;
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class TodoUserControl : UserControl, INotifyPropertyChanged
{
    public ObservableCollection<TodoItem> Items { get; } = new();

    public TodoUserControl()
    {
        var title = new TextBlock
        {
            Text = "Frozen TODO — driven by Claude over stdio",
            FontSize = 18,
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(0, 0, 0, 12)
        };

        var list = new ListBox
        {
            ItemsSource = Items,
            ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<TodoItem>(
                (item, _) =>
                {
                    var checkBox = new CheckBox { VerticalAlignment = VerticalAlignment.Center };
                    checkBox.Bind(CheckBox.IsCheckedProperty,
                        new Avalonia.Data.Binding(nameof(TodoItem.IsDone)) { Mode = Avalonia.Data.BindingMode.TwoWay });
                    var text = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
                    text.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding(nameof(TodoItem.Title)));
                    return new StackPanel { Orientation = Orientation.Horizontal, Children = { checkBox, text } };
                },
                supportsRecycling: true)
        };

        var status = new TextBlock { Margin = new Thickness(0, 8, 0, 0), Text = "0 item(s), 0 done" };
        Items.CollectionChanged += (_, _) =>
        {
            status.Text = $"{ItemCount} item(s), {CompletedCount} done";
            RaisePropertyChanged(nameof(ItemCount));
            RaisePropertyChanged(nameof(CompletedCount));
        };

        Content = new StackPanel
        {
            Margin = new Thickness(16),
            Children = { title, list, status }
        };
    }

    [McpObservable("Total number of TODO items currently in the list.")]
    public int ItemCount => Items.Count;

    [McpObservable("Number of completed TODO items.", Watchable = true)]
    public int CompletedCount => Items.Count(i => i.IsDone);

    [McpCallable("Adds a new TODO item to the list with the given title. Returns the new item count.")]
    public int AddItem(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException("Title must not be empty.", nameof(title));
        Items.Add(new TodoItem(title));
        return Items.Count;
    }

    [McpCallable("Marks the TODO item at the given zero-based index as completed.")]
    public void CompleteItem(int index)
    {
        if (index < 0 || index >= Items.Count) throw new ArgumentOutOfRangeException(nameof(index));
        Items[index].IsDone = true;
        RaisePropertyChanged(nameof(CompletedCount));
    }

    [McpCallable("Removes all completed items from the list. Returns the number of items that were removed.")]
    public int ClearCompleted()
    {
        var removed = 0;
        for (int i = Items.Count - 1; i >= 0; i--)
            if (Items[i].IsDone) { Items.RemoveAt(i); removed++; }
        return removed;
    }

    // Avalonia's AvaloniaObject also exposes a (different-typed) PropertyChanged event;
    // we shadow it deliberately with the System.ComponentModel one because Phase 2B's
    // observable hookup binds against System.ComponentModel.INotifyPropertyChanged.
    public new event PropertyChangedEventHandler? PropertyChanged;
    private void RaisePropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
