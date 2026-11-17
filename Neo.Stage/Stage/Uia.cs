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
        text = text.ReplaceLineEndings(" ").Trim();
        return text.Length <= 60 ? text : text[..59] + "…";
    }

    private static PixelRect? Bounds(IElement element)
    {
        if (element.GetCurrentPropertyValue(BoundingRectangleProperty) is not double[] { Length: 4 } r || double.IsInfinity(r[0]) || r[2] <= 0 || r[3] <= 0)
            return null;
        return new PixelRect((int)r[0], (int)r[1], (int)Math.Ceiling(r[2]), (int)Math.Ceiling(r[3]));
    }

    private static string Text(IElement element, int property) => element.GetCurrentPropertyValue(property) as string ?? "";

    private static double Number(IElement element, int property) => element.GetCurrentPropertyValue(property) switch
    {
        int whole => whole,
        long large => large,
        double real => real,
        _ => 0,
    };

    private const int BoundingRectangleProperty = 30001, ProcessIdProperty = 30002, ControlTypeProperty = 30003, LocalizedControlTypeProperty = 30004,
        NameProperty = 30005, NativeWindowHandleProperty = 30020, ValueProperty = 30045;
    private const int ImageType = 50006, TextType = 50020, PaneType = 50033, GroupType = 50026, CustomType = 50025, WindowType = 50032;

    // Elements whose text or picture is theirs: button, check box, link, list item, menu item, radio button, tab, tree item,
    // data item, split button, header item.
    private static readonly HashSet<int> s_owners = [50000, 50002, 50005, 50007, 50011, 50013, 50019, 50024, 50029, 50031, 50035];

    [ThreadStatic] private static IAutomation? s_automation;
    private static IAutomation Automation => s_automation ??= (IAutomation)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("ff48dba4-60ef-4201-aa87-54103eef594e"))!)!;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }

    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(NativePoint point);

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
