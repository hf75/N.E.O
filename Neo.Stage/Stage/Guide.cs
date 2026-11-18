using System.Runtime.InteropServices;

namespace Neo.Stage;

/// <summary>A step of a guide: the name of what to click, as the program shows it, and what the stage says when it lights up.</summary>
internal sealed record GuideStep(string Find, string Say);

/// <summary>
/// "Show me": the stage leads a person through a program, step by step. What to click first lights up at once; each next
/// step as soon as it can be seen (a tab opened, a menu dropped down), and the stage says what to do. It never clicks
/// itself: the person does, and learns where it is. A step that can already be seen is not shown twice: the guide goes on
/// from the furthest one in sight. It ends a while after the last step lit up, when its window goes, after a long pause,
/// or when the person speaks again.
/// </summary>
internal sealed class Guide
{
    private static readonly TimeSpan s_look = TimeSpan.FromMilliseconds(300), s_lastStepFor = TimeSpan.FromSeconds(30), s_patience = TimeSpan.FromSeconds(90);
    private readonly CancellationTokenSource _stop = new();
    private IReadOnlyList<string>? _whenLit;

    public Guide(IntPtr window, string title, IReadOnlyList<GuideStep> steps)
    {
        Window = window;
        Title = title;
        Steps = steps;
    }

    public IntPtr Window { get; }
    public string Title { get; }
    public IReadOnlyList<GuideStep> Steps { get; }

    /// <summary>What the person wanted, in their words: for asking how to go on once the steps shown are done.</summary>
    public string Goal { get; init; } = "";

    /// <summary>Steps the guides before this one showed, when it goes on from them: its numbers go on from there.</summary>
    public int Before { get; set; }

    /// <summary>The step that is lit, -1 before the first.</summary>
    public int Current { get; private set; } = -1;

    /// <summary>Stopped from outside (the person spoke again), not ended by itself.</summary>
    public bool Stopped => _stop.IsCancellationRequested;

    /// <summary>Where the lit step is now, in screen pixels; null while it cannot be seen.</summary>
    public FoundElement? Shown { get; private set; }

    /// <summary>A step lit up: which one, and where. Raised on the guide's own thread.</summary>
    public event Action<int, FoundElement>? StepShown;

    /// <summary>The guide is over. Raised once, on the guide's own thread.</summary>
    public event Action? Ended;

    /// <summary>
    /// The person did the last step shown (the window changed: new things to click, or the step went away): what it was,
    /// what can be clicked now that could not before, and what else. The guide ends; the brain may go on with a new one.
    /// Raised on the guide's own thread.
    /// </summary>
    public event Action<string, IReadOnlyList<string>, IReadOnlyList<string>>? Progressed;

    /// <summary>
    /// Looks for the furthest step that can be seen and lights it up; null when none can be. Then goes on by itself.
    /// Call off the UI thread.
    /// </summary>
    public FoundElement? Begin()
    {
        var seen = Uia.Look(Window);
        if (Furthest(seen, 0) is not { } first)
            return null;
        Show(first.Index, first.Element, seen!);
        _ = Task.Run(FollowAsync);
        return first.Element;
    }

    public void Stop()
    {
        try { _stop.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    // The furthest step from 'from' on among what can be seen.
    private (int Index, FoundElement Element)? Furthest(IReadOnlyList<FoundElement>? seen, int from)
    {
        for (var i = Steps.Count - 1; i >= from; i--)
        {
            if (Uia.Find(seen, Steps[i].Find) is { } found)
                return (i, found);
        }
        return null;
    }

    private void Show(int index, FoundElement element, IReadOnlyList<FoundElement> seen)
    {
        Current = index;
        Shown = element;
        // What can be clicked when the last step lights up: a change from this tells that the person did it.
        _whenLit = index == Steps.Count - 1 ? Uia.Names(seen, 400) : null;
        App.Log($"guide: step {index + 1} of {Steps.Count}, {element.Kind} '{element.Name}' at {element.Bounds}");
        StepShown?.Invoke(index, element);
    }

    private async Task FollowAsync()
    {
        var since = DateTime.Now;
        var goneSince = DateTime.MaxValue;
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                await Task.Delay(s_look, _stop.Token);
                if (!IsWindow(Window))
                {
                    App.Log("guide: its window is gone");
                    break;
                }
                // One look at the program for all questions: it stays quick for its person.
                var clock = System.Diagnostics.Stopwatch.StartNew();
                var seen = Uia.Look(Window);
                if (clock.ElapsedMilliseconds > 1000)
                    App.Log($"guide: a slow look, {clock.ElapsedMilliseconds} ms for {seen?.Count ?? 0} elements");
                if (seen == null)
                    continue; // busy for a moment
                // A later step in sight: the person did the one before.
                if (Current + 1 < Steps.Count && Furthest(seen, Current + 1) is { } next)
                {
                    Show(next.Index, next.Element, seen);
                    since = DateTime.Now;
                    continue;
                }
                // The lit step, where it is now (the window may have moved); it may be gone for a moment while a menu opens.
                Shown = Uia.Find(seen, Steps[Current].Find);
                goneSince = Shown != null ? DateTime.MaxValue : goneSince == DateTime.MaxValue ? DateTime.Now : goneSince;
                // The last step shown: did the person do it? The window offers new things to click, or the step is gone.
                if (Current == Steps.Count - 1 && Progressed != null)
                {
                    var now = Uia.Names(seen, 400);
                    var before = _whenLit ??= now;
                    var fresh = now.Where(name => !before.Contains(name)).ToList();
                    // Gone without anything new: give a dialog the step opens two seconds to show up.
                    if (fresh.Count >= 2 || DateTime.Now - goneSince > TimeSpan.FromSeconds(2))
                    {
                        App.Log($"guide: the person did step {Current + 1} ('{Steps[Current].Find}'), new to click: {string.Join(" | ", fresh.Take(8))}");
                        Progressed?.Invoke(Steps[Current].Find, fresh, [.. now.Except(fresh)]);
                        break;
                    }
                }
                var waited = DateTime.Now - since;
                if (Current == Steps.Count - 1 && waited > s_lastStepFor || waited > s_patience)
                    break;
            }
        }
        catch (OperationCanceledException) { }
        Shown = null;
        App.Log("guide: over");
        Ended?.Invoke();
    }

    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
}
