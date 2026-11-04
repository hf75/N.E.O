using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Neo.Shared;

/// <summary>
/// The windows of other programs as N.E.O. itself sees them: which ones a person has open, and what one looks like
/// to the model that writes a tool or an interface for it. The helper compiled into generated apps (ForeignApp)
/// finds a control by the addresses written here, over the same walk. Windows only: elsewhere there are none.
/// </summary>
public static class ForeignWindows
{
    /// <summary>A window a person sees: its handle, the program it belongs to, its title.</summary>
    public sealed record OpenWindow(IntPtr Handle, int ProcessId, string Process, string Title);

    /// <summary>How many controls a walk looks at, and how deep. ForeignApp counts "Edit:3" over the same walk.</summary>
    public const int MaxWalk = 600;
    public const int MaxDepth = 14;

    // Rows of a list beyond these stand for the rest.
    private const int RowsShown = 3;
    private const int MaxName = 40;
    private const int MaxOtherWindows = 2;

    /// <summary>The windows a person sees right now, topmost first, without this process's own.</summary>
    public static List<OpenWindow> Open()
    {
        var windows = new List<OpenWindow>();
        if (!OperatingSystem.IsWindows()) return windows;

        var self = Environment.ProcessId;
        EnumWindows((handle, _) =>
        {
            try
            {
                if (IsPersonsWindow(handle) && ProcessOf(handle) is { } process && process.Id != self)
                    windows.Add(new OpenWindow(handle, process.Id, process.Name, WindowTitle(handle)));
            }
            catch (Exception ex) { Debug.WriteLine("[ForeignWindows] " + ex.Message); }
            return true;
        }, IntPtr.Zero);
        return windows;
    }

    /// <summary>The window of a program the way ForeignApp finds it: exact process name, a part of it, a part of the title.</summary>
    public static OpenWindow? Find(string program)
    {
        if (string.IsNullOrWhiteSpace(program)) return null;
        var all = Open();
        return all.FirstOrDefault(w => w.Process.Equals(program, StringComparison.OrdinalIgnoreCase))
            ?? all.FirstOrDefault(w => w.Process.Contains(program, StringComparison.OrdinalIgnoreCase))
            ?? all.FirstOrDefault(w => w.Title.Contains(program, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The structure of a window for the model, one control per line, indented, and then the other windows the program
    /// shows (a dialog). A control goes by its name in quotes, its id, or the address in brackets: "Menge:>Edit" is the
    /// first field after the label "Menge:", "Edit:2" the second field of the window. Names only where they name a
    /// control: the labels of fields are shown, what a text, a row or a document says goes as its shape
    /// (<c>Total: 250,00</c> as <c>Xxxxx: 999,99</c>), and a password field never shows anything.
    /// </summary>
    public static string Describe(IntPtr window, int maxLines)
    {
        if (!OperatingSystem.IsWindows()) return "";

        // A minimised program shows nothing but its title bar: like ForeignApp, it is restored, without taking the keyboard.
        Restore(window);

        var text = new StringBuilder();
        Guarded(() => new Description(window, maxLines, text).Write());

        GetWindowThreadProcessId(window, out var processId);
        var others = Open().Where(w => w.ProcessId == (int)processId && w.Handle != window).Take(MaxOtherWindows).ToList();
        others.AddRange(OwnedWindows(window).Where(owned => others.All(o => o.Handle != owned.Handle)).Take(MaxOtherWindows - others.Count));
        foreach (var other in others)
        {
            text.Append("\nAnother window of the program, \"").Append(Shorten(other.Title)).Append("\":\n");
            Guarded(() => new Description(other.Handle, Math.Max(12, maxLines / 4), text).Write());
        }
        return text.ToString();
    }

    /// <summary>Restores a minimised window without taking the keyboard, and waits until it shows its controls again.</summary>
    public static void Restore(IntPtr window)
    {
        if (!OperatingSystem.IsWindows() || !IsIconic(window)) return;
        ShowWindow(window, SW_SHOWNOACTIVATE);
        for (int i = 0; i < 20 && IsIconic(window); i++) Thread.Sleep(50);
        Thread.Sleep(250);
    }

    // A dialog is owned by its window and often not called a window a person sees.
    private static IEnumerable<OpenWindow> OwnedWindows(IntPtr owner)
    {
        var owned = new List<OpenWindow>();
        EnumWindows((handle, _) =>
        {
            if (handle != owner && IsWindowVisible(handle) && GetWindow(handle, GW_OWNER) == owner)
            {
                GetWindowThreadProcessId(handle, out var id);
                owned.Add(new OpenWindow(handle, (int)id, "", WindowTitle(handle)));
            }
            return true;
        }, IntPtr.Zero);
        return owned;
    }

    /// <summary>
    /// One walk over a window, in the order ForeignApp walks it, writing what the model may see. A control without a name
    /// of its own and without an id gets an address that does not move when the rows of a list change: after the label
    /// before it ("Menge:>Edit"), else inside a control that has an address ("StatusBar:1>Text"), else its number ("Edit:2").
    /// ForeignApp finds "X>Type" as the first control of that type after the first control called X (or with the id X).
    /// </summary>
    private sealed class Description(IntPtr window, int maxLines, StringBuilder text)
    {
        // What a control is to the ones after it in the walk.
        private sealed class Frame
        {
            public string? Anchor;                     // how ForeignApp finds it: its name, its id or its number; null: not that way
            public HashSet<string> Seen = [];          // the types in it so far
            public string? Label;                      // the label among its children that the ones after it are found by
            public HashSet<string> AfterLabel = [];    // the types after that label so far
        }

        private readonly Dictionary<string, int> _ofType = new(StringComparer.Ordinal);
        private readonly HashSet<string> _names = new(StringComparer.OrdinalIgnoreCase);   // type and name, for "first of its kind"
        private readonly HashSet<string> _called = new(StringComparer.OrdinalIgnoreCase);  // every name and id so far
        private int _walked;
        private int _lines;
        private bool _full;

        public bool Write()
        {
            var automation = Automation;
            Visit(automation.ControlViewWalker, automation.ElementFromHandle(window), 0, [], print: true);
            if (_full) text.Append("… (more controls)\n");
            return true;
        }

        private void Visit(ITreeWalker walker, IElement element, int depth, List<Frame> ancestors, bool print)
        {
            if (_walked >= MaxWalk) { _full = true; return; }
            _walked++;

            var type = TypeName(element.CurrentControlType);
            // The system's title bar is the same in every program, and its buttons would count as the program's.
            if (type == "TitleBar") return;
            var name = (element.CurrentName ?? "").Trim();
            var id = element.CurrentAutomationId ?? "";
            var index = _ofType[type] = _ofType.TryGetValue(type, out var n) ? n + 1 : 1;
            var password = type == "Edit" && element.GetCurrentPropertyValue(IsPasswordPropertyId) is true;

            var shown = Shown(type, name);
            var line = new StringBuilder().Append(' ', depth).Append(type);
            if (shown.Length > 0) line.Append(" \"").Append(shown).Append('"');
            else if (name.Length > 0 && !password) line.Append(" ~\"").Append(Shape(name)).Append('"');
            if (password) line.Append(" (password)");
            if (id.Length > 0) line.Append(" id=").Append(id);

            // The first of its name and type goes by its name, one with an id by that; the others need an address.
            var byName = shown.Length > 0 && shown.Length == name.Length && _names.Add(type + "\n" + name);
            var firstCalled = (name.Length == 0 || !_called.Contains(name)) && (id.Length == 0 || !_called.Contains(id));
            string? address = null;
            if (!byName && id.Length == 0)
            {
                var parent = ancestors.Count > 0 ? ancestors[^1] : null;
                if (parent?.Label != null && !parent.AfterLabel.Contains(type)) address = $"{parent.Label}>{type}";
                else if (ancestors.LastOrDefault(a => a.Anchor != null && !a.Seen.Contains(type)) is { } around) address = $"{around.Anchor}>{type}";
                else address = $"{type}:{index}";
                line.Append(" [").Append(address).Append(']');
            }
            foreach (var a in ancestors) { a.Seen.Add(type); a.AfterLabel.Add(type); }

            // A label is found after by the ones beside it, as long as nothing before it was called the same.
            if (ancestors.Count > 0 && type == "Text" && LooksLikeLabel(name) && !name.Contains('>'))
            {
                ancestors[^1].Label = firstCalled ? name : null;
                ancestors[^1].AfterLabel = [];
            }

            // Not the window itself: its title changes with what is open in it, and with the next version.
            var self = new Frame
            {
                Anchor = depth == 0 ? null
                    : byName && firstCalled ? name
                    : id.Length > 0 && firstCalled ? id
                    : address != null && !address.Contains('>') ? address
                    : null,
            };
            if (name.Length > 0) _called.Add(name);
            if (id.Length > 0) _called.Add(id);

            if (print)
            {
                if (_lines < maxLines) { text.Append(line).Append('\n'); _lines++; }
                else _full = true;
            }

            if (depth >= MaxDepth) return;

            // Rows beyond the first few are walked, so that the numbers stay right, and not shown.
            var inside = new List<Frame>(ancestors) { self };
            var rows = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var child = walker.GetFirstChildElement(element); child != null && _walked < MaxWalk; child = walker.GetNextSiblingElement(child))
            {
                var childType = TypeName(child.CurrentControlType);
                var isRow = childType is "ListItem" or "DataItem" or "TreeItem";
                var seen = isRow ? rows[childType] = rows.TryGetValue(childType, out var r) ? r + 1 : 1 : 0;
                Visit(walker, child, depth + 1, inside, print && seen <= RowsShown);
            }
            if (!print) return;
            foreach (var (rowType, count) in rows)
            {
                if (count <= RowsShown) continue;
                if (_lines < maxLines) { text.Append(' ', depth + 1).Append("… ").Append(count - RowsShown).Append(" more ").Append(rowType).Append('\n'); _lines++; }
            }
        }
    }

    /// <summary>What of a control's name the model may read: the names of controls and the labels of fields; else nothing.</summary>
    internal static string Shown(string type, string name)
    {
        if (name.Length == 0) return "";
        switch (type)
        {
            case "Text":
                return LooksLikeLabel(name) ? name : "";
            case "Edit" or "ComboBox" or "Spinner" or "Slider":
                // Its name is the label beside it; a name with a number in it may be what was typed.
                return name.Length <= MaxName && !name.Any(char.IsDigit) ? name : "";
            case "ListItem" or "DataItem" or "TreeItem" or "Hyperlink" or "Image" or "Document" or "Custom" or "ToolTip" or "Control":
                return "";
            default:
                return Shorten(name);
        }
    }

    /// <summary>"Menge:" is a label; "Kunde: Müller" or "Total: 250" are what the program shows.</summary>
    internal static bool LooksLikeLabel(string text)
    {
        var t = text.Trim();
        return t.Length is > 1 and <= MaxName && (t.EndsWith(':') || t.EndsWith('：')) && !t.Any(char.IsDigit);
    }

    /// <summary>How a text looks, so that the model can read the format of a number from it, and nothing of what it says.</summary>
    internal static string Shape(string text)
    {
        var shape = new StringBuilder(Math.Min(text.Length, MaxName));
        foreach (var c in text.AsSpan(0, Math.Min(text.Length, MaxName)))
            shape.Append(char.IsDigit(c) ? '9' : char.IsLetter(c) ? (char.IsUpper(c) ? 'X' : 'x') : char.IsWhiteSpace(c) ? ' ' : c);
        return shape.ToString();
    }

    private static string Shorten(string text) => text.Length <= MaxName ? text : text[..MaxName];

    internal static string TypeName(int controlType) => (controlType - 50000) switch
    {
        0 => "Button", 1 => "Calendar", 2 => "CheckBox", 3 => "ComboBox", 4 => "Edit", 5 => "Hyperlink", 6 => "Image",
        7 => "ListItem", 8 => "List", 9 => "Menu", 10 => "MenuBar", 11 => "MenuItem", 12 => "ProgressBar", 13 => "RadioButton",
        14 => "ScrollBar", 15 => "Slider", 16 => "Spinner", 17 => "StatusBar", 18 => "Tab", 19 => "TabItem", 20 => "Text",
        21 => "ToolBar", 22 => "ToolTip", 23 => "Tree", 24 => "TreeItem", 25 => "Custom", 26 => "Group", 27 => "Thumb",
        28 => "DataGrid", 29 => "DataItem", 30 => "Document", 31 => "SplitButton", 32 => "Window", 33 => "Pane", 34 => "Header",
        35 => "HeaderItem", 36 => "Table", 37 => "TitleBar", 38 => "Separator",
        _ => "Control",
    };

    // The other program may end or refuse at any moment: that is an answer, not a failure.
    private static void Guarded(Func<bool> read)
    {
        try { read(); }
        catch (Exception ex) when (ex is COMException or InvalidCastException or NullReferenceException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            Debug.WriteLine("[ForeignWindows] " + ex.Message);
        }
    }

    // ── Windows ───────────────────────────────────────────────────────────

    private static bool IsPersonsWindow(IntPtr window) =>
        IsWindowVisible(window)
        && GetWindow(window, GW_OWNER) == IntPtr.Zero
        && (GetWindowLongPtr(window, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) == 0
        && GetWindowTextLength(window) > 0
        && !(DwmGetWindowAttribute(window, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0);

    private static (int Id, string Name)? ProcessOf(IntPtr window)
    {
        GetWindowThreadProcessId(window, out var id);
        using var process = Process.GetProcessById((int)id);
        if (process.ProcessName != "ApplicationFrameHost")
            return ((int)id, process.ProcessName);

        // A store app shows in a frame of the system: the program is who owns what is inside it.
        (int, string)? hosted = null;
        EnumChildWindows(window, (child, _) =>
        {
            GetWindowThreadProcessId(child, out var childId);
            if (childId == id) return true;
            using var inner = Process.GetProcessById((int)childId);
            hosted = ((int)childId, inner.ProcessName);
            return false;
        }, IntPtr.Zero);
        return hosted ?? ((int)id, process.ProcessName);
    }

    private static string WindowTitle(IntPtr window)
    {
        var title = new StringBuilder(GetWindowTextLength(window) + 1);
        GetWindowText(window, title, title.Capacity);
        return title.ToString();
    }

    private const int GWL_EXSTYLE = -20, DWMWA_CLOAKED = 14;
    private const long WS_EX_TOOLWINDOW = 0x00000080;
    private const uint GW_OWNER = 4;

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    private const int SW_SHOWNOACTIVATE = 4;

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int maxCount);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(IntPtr window);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern long GetWindowLongPtr(IntPtr window, int index);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);

    // ── UI Automation, declared here like in ForeignApp: its managed wrapper exists only for WPF programs ──
    // Slots that are not used still have to be there: the order is the interface. From UIAutomationClient.h.

    private const int IsPasswordPropertyId = 30019;

    private static IAutomation? s_automation;
    private static IAutomation Automation => s_automation ??= (IAutomation)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("ff48dba4-60ef-4201-aa87-54103eef594e"))!)!;

    [ComImport, Guid("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAutomation
    {
        void CompareElements();
        void CompareRuntimeIds();
        void GetRootElement();
        IElement ElementFromHandle(IntPtr window);
        void ElementFromPoint();
        void GetFocusedElement();
        void GetRootElementBuildCache();
        void ElementFromHandleBuildCache();
        void ElementFromPointBuildCache();
        void GetFocusedElementBuildCache();
        void CreateTreeWalker();
        ITreeWalker ControlViewWalker { get; }
    }

    [ComImport, Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IElement
    {
        void SetFocus();
        void GetRuntimeId();
        void FindFirst();
        void FindAll();
        void FindFirstBuildCache();
        void FindAllBuildCache();
        void BuildUpdatedCache();
        [return: MarshalAs(UnmanagedType.Struct)] object GetCurrentPropertyValue(int property);
        void GetCurrentPropertyValueEx();
        void GetCachedPropertyValue();
        void GetCachedPropertyValueEx();
        void GetCurrentPatternAs();
        void GetCachedPatternAs();
        void GetCurrentPattern();
        void GetCachedPattern();
        void GetCachedParent();
        void GetCachedChildren();
        int CurrentProcessId { get; }
        int CurrentControlType { get; }
        void get_CurrentLocalizedControlType();
        string CurrentName { [return: MarshalAs(UnmanagedType.BStr)] get; }
        void get_CurrentAcceleratorKey();
        void get_CurrentAccessKey();
        void get_CurrentHasKeyboardFocus();
        void get_CurrentIsKeyboardFocusable();
        int CurrentIsEnabled { get; }
        string CurrentAutomationId { [return: MarshalAs(UnmanagedType.BStr)] get; }
    }

    [ComImport, Guid("4042c624-389c-4afc-a630-9df854a541fc"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITreeWalker
    {
        void GetParentElement();
        IElement? GetFirstChildElement(IElement element);
        void GetLastChildElement();
        IElement? GetNextSiblingElement(IElement element);
    }
}
