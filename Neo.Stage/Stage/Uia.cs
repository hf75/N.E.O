using System.Runtime.InteropServices;
using Avalonia;

namespace Neo.Stage;

/// <summary>
/// Something on the screen the person pointed at, as UI Automation names it: what it is called, what kind of
/// thing it is, where it is (screen pixels) and which program shows it.
/// </summary>
internal sealed record PointedThing(string Name, string Kind, PixelRect Bounds, string Program, IntPtr Window, int ControlType, string? Value)
{
    /// <summary>For the stage: the name, or the program when the thing has none.</summary>
    public string Label => Name.Length > 0 ? Name : Program;

    public bool SameAs(PointedThing other) => other.Bounds == Bounds && other.Name == Name && other.ControlType == ControlType;
}

/// <summary>A control found by its name: what it is called and what kind it is, where it is (screen pixels).</summary>
internal sealed record FoundElement(string Name, string Kind, PixelRect Bounds, int ControlType);

/// <summary>
/// UI Automation through its COM interface (the managed wrapper exists only with WPF). Only reads; never clicks,
/// never types. Call it off the UI thread: asking the own process from its UI thread can deadlock.
/// </summary>
internal static class Uia
{
    /// <summary>What lies under <paramref name="point"/> (screen pixels), or null.</summary>
    public static PointedThing? At(PixelPoint point)
    {
        try
        {
            var automation = Automation;
            var element = automation.ElementFromPoint(new NativePoint { X = point.X, Y = point.Y });
            // A window that lets the input through still counts as on top for UI Automation: the stage itself.
            // Then start from the window Windows finds there and look for the deepest element that holds the point.
            if (element == null || Number(element, ProcessIdProperty) == Environment.ProcessId)
            {
                var window = WindowFromPoint(new NativePoint { X = point.X, Y = point.Y });
                if (window == IntPtr.Zero)
                    return null;
                element = Deepest(automation, automation.ElementFromHandle(window), point);
            }
            return Describe(automation, element, point);
        }
        catch (Exception exception) when (exception is COMException or InvalidCastException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// What can be seen in <paramref name="window"/> and in what it opened (a menu, a drop-down, a dialog), that first:
    /// every element's name, kind and place, in one trip to the program instead of one per question, so that a program
    /// asked often (a guide looks twice a second) stays quick for its person. Null when the program could not be asked.
    /// </summary>
    public static IReadOnlyList<FoundElement>? Look(IntPtr window)
    {
        if (window == IntPtr.Zero)
            return null;
        try
        {
            // Some programs answer lazily while they idle: Word took 1 to 3.7 s for a look after a pause of a tenth of a
            // second, 0.2 s right after another one. A message that does nothing (WM_NULL) every 15 ms keeps its message
            // loop awake for as long as the look takes: 0.2 s every time.
            using var nudge = new Timer(_ => PostMessage(window, WM_NULL, IntPtr.Zero, IntPtr.Zero), null, 0, 15);
            var automation = Automation;
            var request = automation.CreateCacheRequest();
            foreach (var property in s_fetched)
                request.AddProperty(property);
            var seen = new List<FoundElement>();
            foreach (var handle in WithOpened(window))
            {
                IElementArray found;
                try
                {
                    found = automation.ElementFromHandle(handle).FindAllBuildCache(Descendants, automation.ControlViewCondition, request);
                }
                catch (COMException) when (handle != window)
                {
                    continue; // a menu that closed meanwhile
                }
                for (var i = 0; i < Math.Min(found.Length, 4000); i++)
                {
                    var element = found.GetElement(i);
                    if (Number(element.GetCachedPropertyValue(IsOffscreenProperty)) != 0
                        || Bounds(element.GetCachedPropertyValue(BoundingRectangleProperty)) is not { Width: > 3, Height: > 3 } bounds)
                        continue;
                    seen.Add(new FoundElement(
                        Clean(element.GetCachedPropertyValue(NameProperty) as string ?? ""),
                        element.GetCachedPropertyValue(LocalizedControlTypeProperty) as string ?? "",
                        bounds,
                        (int)Number(element.GetCachedPropertyValue(ControlTypeProperty))));
                }
            }
            return seen;
        }
        catch (Exception exception) when (exception is COMException or InvalidCastException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// The element among <paramref name="seen"/> a person would mean by <paramref name="name"/>: a name that is the word
    /// itself before one that only holds it, something one clicks (a tab, a button, a menu item…) before mere text, the
    /// smaller of two alike. Invisible marks in names do not count (Word writes "Seiten­ränder"). Null when none holds it.
    /// </summary>
    public static FoundElement? Find(IReadOnlyList<FoundElement>? seen, string name)
    {
        name = Clean(name);
        if (seen == null || name.Length == 0)
            return null;
        FoundElement? best = null;
        var bestScore = int.MinValue;
        foreach (var element in seen)
        {
            if (!element.Name.Contains(name, StringComparison.OrdinalIgnoreCase))
                continue;
            var score = string.Equals(element.Name, name, StringComparison.OrdinalIgnoreCase) ? 300
                : element.Name.StartsWith(name, StringComparison.OrdinalIgnoreCase) ? 200 : 100;
            score += s_clickable.Contains(element.ControlType) ? 50 : element.ControlType == TextType ? 0 : 20;
            score -= Math.Min(40, element.Name.Length - name.Length); // the closer the name, the better
            if (score > bestScore || (score == bestScore && best != null && Area(element) < Area(best)))
            {
                bestScore = score;
                best = element;
            }
        }
        return best == null ? null : best with { Name = Shorten(best.Name) };

        static long Area(FoundElement element) => (long)element.Bounds.Width * element.Bounds.Height;
    }

    /// <summary>The element in <paramref name="window"/> a person would mean by <paramref name="name"/>, as it is on the screen now.</summary>
    public static FoundElement? Find(IntPtr window, string name) => Find(Look(window), name);

    /// <summary>
    /// What can be clicked among <paramref name="seen"/>, by name: tabs, menus, buttons, at most <paramref name="most"/>; what
    /// the window opened (a menu, a drop-down, a dialog) first.
    /// </summary>
    public static IReadOnlyList<string> Names(IReadOnlyList<FoundElement>? seen, int most = 40) =>
        seen == null ? [] : [.. seen.Where(element => s_clickable.Contains(element.ControlType) && element.Name.Length > 0)
            .Select(element => Shorten(element.Name)).Distinct().Take(most)];

    /// <summary>What can be clicked in <paramref name="window"/> right now, by name, at most <paramref name="most"/>.</summary>
    public static IReadOnlyList<string> Names(IntPtr window, int most = 40) => Names(Look(window), most);

    // The window's menus, drop-downs and dialogs, the top one first, then the window: what its thread shows that has no
    // button on the taskbar. Another document window of the same program (Excel's workbooks share a thread) has one.
    private static List<IntPtr> WithOpened(IntPtr window)
    {
        var windows = new List<IntPtr>();
        var thread = GetWindowThreadProcessId(window, out _);
        if (thread != 0)
        {
            EnumThreadWindows(thread, (other, _) =>
            {
                if (other == window || !IsWindowVisible(other) || !GetWindowRect(other, out var rect) || rect.Right - rect.Left < 8 || rect.Bottom - rect.Top < 8)
                    return true;
                var style = (long)GetWindowLongPtr(other, ExtendedStyle);
                var onTaskbar = (style & AppWindowStyle) != 0 || ((style & ToolWindowStyle) == 0 && GetWindow(other, Owner) == IntPtr.Zero);
                if (!onTaskbar)
                    windows.Add(other);
                return true;
            }, IntPtr.Zero);
        }
        windows.Add(window);
        return windows;
    }

    // Down the control view to the smallest element that still holds the point.
    private static IElement Deepest(IAutomation automation, IElement root, PixelPoint point)
    {
        var walker = automation.ControlViewWalker;
        var current = root;
        for (var depth = 0; depth < 24; depth++)
        {
            IElement? inside = null;
            for (var child = walker.GetFirstChildElement(current); child != null; child = walker.GetNextSiblingElement(child))
            {
                if (Bounds(child) is { } bounds && bounds.Contains(point))
                {
                    inside = child;
                    break;
                }
            }
            if (inside == null)
                return current;
            current = inside;
        }
        return current;
    }

    // What a person means by "this": a text or picture inside a button, list item, link or cell means that; otherwise the
    // smallest element with a name, not a nameless pane or group; when nothing has a name, the smallest one under the pointer.
    private static PointedThing? Describe(IAutomation automation, IElement element, PixelPoint point)
    {
        var walker = automation.ControlViewWalker;
        var chain = new List<IElement>();
        for (var link = element; link != null && chain.Count < 8; link = walker.GetParentElement(link))
        {
            chain.Add(link);
            if ((int)Number(link, ControlTypeProperty) == WindowType)
                break;
        }

        IElement? chosen = null;
        var firstType = (int)Number(chain[0], ControlTypeProperty);
        if (firstType is TextType or ImageType)
            chosen = chain.Skip(1).Take(2).FirstOrDefault(parent => s_owners.Contains((int)Number(parent, ControlTypeProperty)));
        chosen ??= chain.FirstOrDefault(candidate =>
            Text(candidate, NameProperty).Length > 0
            && (int)Number(candidate, ControlTypeProperty) is not (PaneType or GroupType or CustomType or WindowType)
            && Bounds(candidate) is { Width: > 2, Height: > 2 });
        chosen ??= chain.FirstOrDefault(candidate => Bounds(candidate) is { Width: > 2, Height: > 2 });
        if (chosen == null)
            return null;
        var current = chosen;

        if (Bounds(current) is not { Width: > 2, Height: > 2 } bounds)
            return null;
        var processId = (uint)Number(current, ProcessIdProperty);
        var window = (IntPtr)(long)Number(current, NativeWindowHandleProperty);
        if (window == IntPtr.Zero)
            window = WindowFromPoint(new NativePoint { X = point.X, Y = point.Y });
        string? value = null;
        try { value = Text(current, ValueProperty); }
        catch (COMException) { }
        return new PointedThing(
            Shorten(Text(current, NameProperty)),
            Text(current, LocalizedControlTypeProperty),
            bounds,
            DesktopTargets.ProgramName(processId) ?? "",
            window,
            (int)Number(current, ControlTypeProperty),
            string.IsNullOrEmpty(value) ? null : Shorten(value));
    }

    private static string Shorten(string text)
    {
        text = Clean(text);
        return text.Length <= 60 ? text : text[..59] + "…";
    }

    // One line, without the invisible marks some programs put around names (Word: "‪Dokument1‬").
    private static string Clean(string text) =>
        new string([.. text.ReplaceLineEndings(" ").Where(letter => char.GetUnicodeCategory(letter) != System.Globalization.UnicodeCategory.Format)]).Trim();

    private static PixelRect? Bounds(IElement element) => Bounds(element.GetCurrentPropertyValue(BoundingRectangleProperty));

    private static PixelRect? Bounds(object? value)
    {
        if (value is not double[] { Length: 4 } r || double.IsInfinity(r[0]) || r[2] <= 0 || r[3] <= 0)
            return null;
        return new PixelRect((int)r[0], (int)r[1], (int)Math.Ceiling(r[2]), (int)Math.Ceiling(r[3]));
    }

    private static string Text(IElement element, int property) => element.GetCurrentPropertyValue(property) as string ?? "";

    private static double Number(IElement element, int property) => Number(element.GetCurrentPropertyValue(property));

    private static double Number(object? value) => value switch
    {
        int whole => whole,
        long large => large,
        double real => real,
        bool flag => flag ? 1 : 0, // IsOffscreen
        _ => 0,
    };

    private const int BoundingRectangleProperty = 30001, ProcessIdProperty = 30002, ControlTypeProperty = 30003, LocalizedControlTypeProperty = 30004,
        NameProperty = 30005, NativeWindowHandleProperty = 30020, IsOffscreenProperty = 30022, ValueProperty = 30045;
    private const int Descendants = 4;

    // What the searches fetch along with each element.
    private static readonly int[] s_fetched = [NameProperty, ControlTypeProperty, LocalizedControlTypeProperty, BoundingRectangleProperty, IsOffscreenProperty];
    private const int ImageType = 50006, TextType = 50020, PaneType = 50033, GroupType = 50026, CustomType = 50025, WindowType = 50032;

    // Elements whose text or picture is theirs: button, check box, link, list item, menu item, radio button, tab, tree item,
    // data item, split button, header item.
    private static readonly HashSet<int> s_owners = [50000, 50002, 50005, 50007, 50011, 50013, 50019, 50024, 50029, 50031, 50035];

    // What a person clicks: button, check box, combo box, edit, link, list item, menu item, radio button, tab, tree item,
    // split button, menu bar item.
    private static readonly HashSet<int> s_clickable = [50000, 50002, 50003, 50004, 50005, 50007, 50011, 50013, 50019, 50024, 50031, 50010];

    [ThreadStatic] private static IAutomation? s_automation;
    private static IAutomation Automation => s_automation ??= (IAutomation)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("ff48dba4-60ef-4201-aa87-54103eef594e"))!)!;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    private delegate bool EachWindow(IntPtr window, IntPtr parameter);

    private const int ExtendedStyle = -20, Owner = 4;
    private const uint WM_NULL = 0;
    private const long ToolWindowStyle = 0x80, AppWindowStyle = 0x40000;

    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] private static extern bool EnumThreadWindows(uint thread, EachWindow each, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, int relation);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

    [ComImport, Guid("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAutomation
    {
        void CompareElements();
        void CompareRuntimeIds();
        void GetRootElement();
        IElement ElementFromHandle(IntPtr window);
        IElement? ElementFromPoint(NativePoint point);
        void GetFocusedElement();
        void GetRootElementBuildCache();
        void ElementFromHandleBuildCache();
        void ElementFromPointBuildCache();
        void GetFocusedElementBuildCache();
        void CreateTreeWalker();
        ITreeWalker ControlViewWalker { get; }
        void ContentViewWalker();
        void RawViewWalker();
        void RawViewCondition();
        ICondition ControlViewCondition { get; }
        void ContentViewCondition();
        ICacheRequest CreateCacheRequest();
    }

    [ComImport, Guid("352ffba8-0973-437c-a61f-f64cafd81df9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICondition
    {
    }

    [ComImport, Guid("b32a92b5-bc25-4078-9c08-d7ee95c48e03"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICacheRequest
    {
        void AddProperty(int property);
    }

    [ComImport, Guid("14314595-b4bc-4055-95f2-58f2e42c9855"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IElementArray
    {
        int Length { get; }
        IElement GetElement(int index);
    }

    [ComImport, Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IElement
    {
        void SetFocus();
        void GetRuntimeId();
        void FindFirst();
        IElementArray FindAll(int scope, ICondition condition);
        void FindFirstBuildCache();
        IElementArray FindAllBuildCache(int scope, ICondition condition, ICacheRequest request);
        void BuildUpdatedCache();
        [return: MarshalAs(UnmanagedType.Struct)] object GetCurrentPropertyValue(int property);
        void GetCurrentPropertyValueEx();
        [return: MarshalAs(UnmanagedType.Struct)] object GetCachedPropertyValue(int property);
    }

    [ComImport, Guid("4042c624-389c-4afc-a630-9df854a541fc"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITreeWalker
    {
        IElement? GetParentElement(IElement element);
        IElement? GetFirstChildElement(IElement element);
        void GetLastChildElement();
        IElement? GetNextSiblingElement(IElement element);
    }
}
