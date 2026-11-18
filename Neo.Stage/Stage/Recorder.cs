using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Channels;
using Avalonia;

namespace Neo.Stage;

/// <summary>
/// A step the person did while the stage watched: what they clicked (its name and kind, as the program names it), in
/// which program and window, and whether it is a field one types into.
/// </summary>
internal sealed record RecordedStep(string Find, string Kind, int ControlType, string Program, string Window, bool Typed)
{
    /// <summary>What the stage says when a guide shows this step.</summary>
    public string Say => Typed
        ? Language.Pick($"Klick in „{Find}“ und schreib hinein, was du brauchst.", $"Click into “{Find}” and type what you need.")
        : Language.Pick($"Klick auf „{Find}“.", $"Click “{Find}”.");
}

/// <summary>A way of doing something, shown once ("Schau mir zu") and kept by name, so that a guide can show it to anyone.</summary>
internal sealed record Recipe(string Name, string Description, string Program, IReadOnlyList<RecordedStep> Steps, DateTimeOffset Recorded)
{
    /// <summary>The steps in its program, as a guide shows them.</summary>
    public IReadOnlyList<GuideStep> Guide =>
        [.. Steps.Where(step => string.Equals(step.Program, Program, StringComparison.OrdinalIgnoreCase)).Select(step => new GuideStep(step.Find, step.Say))];
}

/// <summary>The recipes the person showed the stage, in Neo/Stage/recipes, one file each.</summary>
internal static class Recipes
{
    private static readonly JsonSerializerOptions s_json = new() { WriteIndented = true };

    private static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Neo", "Stage", "recipes");

    public static IReadOnlyList<Recipe> All()
    {
        if (!Directory.Exists(Folder))
            return [];
        var recipes = new List<Recipe>();
        foreach (var file in Directory.EnumerateFiles(Folder, "*.json").OrderBy(file => file, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (JsonSerializer.Deserialize<Recipe>(File.ReadAllText(file)) is { Name.Length: > 0, Steps.Count: > 0 } recipe)
                    recipes.Add(recipe);
            }
            catch (Exception exception) when (exception is JsonException or IOException) { }
        }
        return recipes;
    }

    public static Recipe? Find(string name) =>
        All().FirstOrDefault(recipe => string.Equals(recipe.Name, name, StringComparison.OrdinalIgnoreCase))
        ?? All().FirstOrDefault(recipe => recipe.Name.Contains(name.Trim(), StringComparison.OrdinalIgnoreCase));

    public static void Keep(Recipe recipe)
    {
        Directory.CreateDirectory(Folder);
        var file = new string([.. recipe.Name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-')]);
        File.WriteAllText(Path.Combine(Folder, file + ".json"), JsonSerializer.Serialize(recipe, s_json));
    }

    /// <summary>The recipes as the brain reads them with every question; empty when there are none.</summary>
    public static string Describe()
    {
        var all = All();
        return all.Count == 0 ? "" : Language.Pick("Vorgemachte Abläufe (guide mit recipe zeigt sie):", "Recorded ways of doing things (guide with recipe shows them):")
            + string.Concat(all.Select(recipe => $"{Environment.NewLine}- {recipe.Name} ({recipe.Program}, {recipe.Steps.Count} " +
                Language.Pick("Schritte", "steps") + $"): {recipe.Description}"));
    }
}

/// <summary>
/// "Schau mir zu": while the person shows how something is done, the stage notes every click by the name of what was
/// clicked (UI Automation, the eyes "Zeig mir…" uses) and never a key: of a field it notes only that one types there.
/// A low-level mouse hook sees the clicks; each element is looked up beside it at once, on the button going down,
/// before the click changes the window. Clicks into a large page or pane (the text of a document) are not steps.
/// </summary>
internal sealed class Recorder
{
    private const int MostSteps = 60;
    private readonly List<RecordedStep> _steps = [];
    private Channel<PixelPoint>? _clicks;
    private HookProc? _proc; // kept alive while hooked: the hook calls it
    private IntPtr _hook;
    private DateTime _lastAt;

    /// <summary>Whether it is noting clicks now.</summary>
    public bool Recording { get; private set; }

    /// <summary>Off in a check that feeds its own clicks: the person's real clicks stay out of it.</summary>
    public bool HookMouse { get; set; } = true;

    /// <summary>How many steps it noted so far.</summary>
    public int Count
    {
        get
        {
            lock (_steps)
                return _steps.Count;
        }
    }

    /// <summary>Recording began or ended. Raised on the UI thread.</summary>
    public event Action<bool>? Changed;

    /// <summary>A step was noted: its number, the step, and where its element was on the screen. Raised on a background thread.</summary>
    public event Action<int, RecordedStep, PixelRect>? Stepped;

    /// <summary>
    /// Begins: every click from now on is noted. On the UI thread, whose message loop the hook needs. False when Windows
    /// refused the hook; fed clicks (<see cref="Feed"/>) are noted all the same.
    /// </summary>
    public bool Start()
    {
        if (Recording)
            return _hook != IntPtr.Zero;
        lock (_steps)
            _steps.Clear();
        _clicks = Channel.CreateUnbounded<PixelPoint>();
        Recording = true;
        _ = Task.Run(() => FollowAsync(_clicks.Reader));
        Changed?.Invoke(true);
        if (!HookMouse)
        {
            App.Log("recorder: watching fed clicks only");
            return true;
        }
        _proc = Hooked;
        _hook = SetWindowsHookEx(WH_MOUSE_LL, _proc, GetModuleHandle(null), 0);
        App.Log(_hook != IntPtr.Zero ? "recorder: watching the clicks" : $"recorder: no mouse hook (error {Marshal.GetLastWin32Error()})");
        return _hook != IntPtr.Zero;
    }

    /// <summary>Ends and returns the steps noted. On the UI thread.</summary>
    public IReadOnlyList<RecordedStep> Stop()
    {
        if (_hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
        _proc = null;
        _clicks?.Writer.TryComplete();
        var was = Recording;
        Recording = false;
        if (was)
            Changed?.Invoke(false);
        lock (_steps)
        {
            App.Log($"recorder: stopped after {_steps.Count} steps");
            return [.. _steps];
        }
    }

    /// <summary>A click at <paramref name="point"/> (screen pixels), as the hook sees one; a check feeds its own.</summary>
    public void Feed(PixelPoint point)
    {
        if (Recording)
            _clicks?.Writer.TryWrite(point);
    }

    // On the UI thread, for every mouse event on the machine: only note where a button went down, and pass it on at once.
    private IntPtr Hooked(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0 && (message == WM_LBUTTONDOWN || message == WM_RBUTTONDOWN))
        {
            var info = Marshal.PtrToStructure<MouseInfo>(data);
            Feed(new PixelPoint(info.X, info.Y));
        }
        return CallNextHookEx(IntPtr.Zero, code, message, data);
    }

    private async Task FollowAsync(ChannelReader<PixelPoint> clicks)
    {
        await foreach (var point in clicks.ReadAllAsync())
        {
            var thing = Uia.At(point);
            if (thing == null || thing.Name.Trim().Length == 0 || Page(thing))
                continue;
            var top = Hands.TopWindow(thing);
            var step = new RecordedStep(thing.Name, thing.Kind, thing.ControlType, thing.Program, top == IntPtr.Zero ? "" : OpenWindows.Title(top),
                Typed: thing.ControlType == EditType);
            int number;
            lock (_steps)
            {
                // A double click, or a second click on the same thing: one step.
                if (_steps.Count > 0 && _steps[^1].Find == step.Find && _steps[^1].Window == step.Window && DateTime.Now - _lastAt < TimeSpan.FromSeconds(1.5))
                    continue;
                if (_steps.Count >= MostSteps)
                    continue;
                _steps.Add(step);
                _lastAt = DateTime.Now;
                number = _steps.Count;
            }
            App.Log($"recorder: step {number}, {step.Kind} '{step.Find}' in {step.Program} ('{step.Window}')");
            Stepped?.Invoke(number, step, thing.Bounds);
        }
    }

    // A click into the page of a document or a large pane is where one works, not a step one shows.
    private static bool Page(PointedThing thing)
        => thing.ControlType is DocumentType or PaneType or CustomType or GroupType or WindowType && (long)thing.Bounds.Width * thing.Bounds.Height > 250_000;

    private const int EditType = 50004, DocumentType = 50030, PaneType = 50033, CustomType = 50025, GroupType = 50026, WindowType = 50032;
    private const int WH_MOUSE_LL = 14;
    private static readonly IntPtr WM_LBUTTONDOWN = 0x0201, WM_RBUTTONDOWN = 0x0204;

    private delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInfo { public int X, Y; public uint MouseData, Flags, Time; public IntPtr ExtraInfo; }

    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int hook, HookProc proc, IntPtr module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
}
