// Live-MCP Phase 1 sample: a TODO app fully drivable by Claude via the meta-tools.
//
// Demo flow (matches VISION.md "Demo Phase 1"):
//   1. compile_and_preview  → load this file
//   2. inspect_app_api      → see AddItem, CompleteItem, ClearCompleted, ItemCount, CompletedCount
//   3. invoke_method("AddItem", "[\"Buy milk\"]")  ×5
//   4. read_observable("ItemCount")  → "5"
//   5. invoke_method("CompleteItem", "[0]")
//   6. read_observable("CompletedCount") → "1"
//   7. capture_screenshot to verify the UI state
//
// Fully autonomous — no simulate_input needed.

using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

using Neo.App;

public class TodoItem : INotifyPropertyChanged
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

public class DynamicUserControl : UserControl, INotifyPropertyChanged
{
    private readonly ListBox _list;

    public ObservableCollection<TodoItem> Items { get; } = new();

    public DynamicUserControl()
    {
        var title = new TextBlock
        {
            Text = "TODO — driven by Claude via Live-MCP",
            FontSize = 18,
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(0, 0, 0, 12)
        };

        _list = new ListBox
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

                    return new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Children = { checkBox, text }
                    };
                },
                supportsRecycling: true)
        };

        var status = new TextBlock { Margin = new Thickness(0, 8, 0, 0) };
        Items.CollectionChanged += (_, _) =>
        {
            status.Text = $"{ItemCount} item(s), {CompletedCount} done";
            RaisePropertyChanged(nameof(ItemCount));
            RaisePropertyChanged(nameof(CompletedCount));
        };
        status.Text = "0 item(s), 0 done";

        Content = new StackPanel
        {
            Margin = new Thickness(16),
            Children = { title, _list, status }
        };
    }

    // ── Observables (read by Claude via read_observable) ─────────────────────

    [McpObservable("Total number of TODO items currently in the list.")]
    public int ItemCount => Items.Count;

    [McpObservable("Number of completed TODO items.", Watchable = true)]
    public int CompletedCount => Items.Count(i => i.IsDone);

    // ── Callables (invoked by Claude via invoke_method) ──────────────────────

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
        {
            if (Items[i].IsDone) { Items.RemoveAt(i); removed++; }
        }
        return removed;
    }

    // ── INotifyPropertyChanged plumbing for Watchable observables (Phase 2) ──

    public event PropertyChangedEventHandler? PropertyChanged;
    private void RaisePropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
