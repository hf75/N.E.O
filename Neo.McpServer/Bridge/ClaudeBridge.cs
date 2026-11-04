using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Neo.App;

/// <summary>
/// The Claude bridge, an exoskeleton for the Claude desktop app. Channels do not arrive there, so a prompt that a
/// N.E.O. app sends with Ai.Trigger would be lost; the neo-preview server hands it to this panel instead, and the
/// panel types it into the prompt box of its Claude Code session, as a channel would in the terminal: only while that
/// session is the one in front, Claude is not working and the box is empty. Every prompt says which app it is from.
/// The server compiles this file like any app (start_claude_bridge); it is never compiled into the server.
/// </summary>
public class DynamicUserControl : UserControl
{
    // What the Claude window calls its controls for the accessibility interface (Claude 4.4212, English).
    public const string PromptBox = "Prompt";
    public const string SendButton = "Send";
    public const string StopButton = "Stop";
    public const string SessionSuffix = ", rename session";

    private const int MaxWaiting = 20;
    private const int LogLines = 4;

    // After a prompt went off, Claude needs a moment before its Stop button shows.
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(3);

    // Bringing the session to the front: only while nobody uses mouse or keyboard (what someone types must not end up in
    // Claude). The link has this long to show the session; after a try that did not bring it, the next one waits.
    private static readonly TimeSpan Unused = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan LinkTakes = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan BetweenTries = TimeSpan.FromSeconds(30);

    private readonly ForeignApp _claude;
    private readonly bool _standIn;
    private readonly List<(string App, string Prompt)> _waiting = new();
    private readonly TextBlock _sessionLine = Line(12, FontWeight.SemiBold, "Not bound to a session");
    private readonly TextBlock _stateLine = Line(12, FontWeight.Normal, "");
    private readonly TextBlock _countLine = Line(11, FontWeight.Normal, "");
    private readonly StackPanel _log = new() { Spacing = 2 };
    private readonly Button _pause = Small("Pause");
    private readonly Button _open = Small("Open session");
    private readonly Button _bind = Small("Use the session in front");
    private readonly CheckBox _front = new() { Content = "Bring the session to the front", FontSize = 11, IsVisible = false };

    private string _session = "";
    private string? _link;
    private bool _paused;
    private bool _stepping;
    private int _delivered;
    private int _dropped;
    private int _opened;
    private DateTime _quietUntil;
    private DateTime _openedAt = DateTime.MinValue;
    private DateTime _failedAt = DateTime.MinValue;
    private bool _openPending;
    private string? _frontBeforeOpen;

    public DynamicUserControl()
    {
        // A test docks the bridge to a stand-in for the Claude window. Nobody sits at a stand-in, and it has no
        // link handler: links it would open are only counted.
        var target = Environment.GetEnvironmentVariable("NEO_CLAUDE_BRIDGE_TARGET");
        _standIn = !string.IsNullOrWhiteSpace(target);
        _claude = ForeignApp.Attach(_standIn ? target! : "claude", size: 230, length: 300);

        _pause.Click += (_, _) => { _paused = !_paused; _pause.Content = _paused ? "Resume" : "Pause"; _ = StepAsync(); };
        _open.IsVisible = false;
        _open.Click += (_, _) => OpenSession();
        _front.IsCheckedChanged += (_, _) => _ = StepAsync();
        _bind.Click += async (_, _) =>
        {
            var front = await FrontSessionsAsync();
            if (front.Count == 1) Bind(front[0], _link ?? "");
        };

        Content = new Border
        {
            Padding = new Thickness(10, 8),
            Child = new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    Line(13, FontWeight.Bold, "N.E.O. bridge"),
                    _sessionLine,
                    _stateLine,
                    _countLine,
                    new WrapPanel { Children = { _pause, _open, _bind } },
                    _front,
                    _log,
                },
            },
        };

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => _ = StepAsync();
        timer.Start();
        Show("Waiting for a session.");
    }

    [McpCallable("Binds the bridge to the Claude Code session it types into: its title as the Claude window shows it, and its claude:// link.")]
    public void Bind(string sessionTitle, string sessionLink)
    {
        _session = (sessionTitle ?? "").Trim();
        // Only a link into the Claude app: the button must never start anything else.
        _link = sessionLink is { } link && link.Trim().StartsWith("claude://", StringComparison.OrdinalIgnoreCase) ? link.Trim() : null;
        _sessionLine.Text = _session.Length > 0 ? "Session: " + _session : "Not bound to a session";
        _open.IsVisible = _link != null;
        _front.IsVisible = _link != null;
        _ = StepAsync();
    }

    [McpCallable("Turns on or off that the bridge brings its session to the front when a prompt waits and nobody uses the PC.")]
    public void BringToFront(bool on)
    {
        _front.IsChecked = on;
        _ = StepAsync();
    }

    [McpCallable("Hands over a prompt that a N.E.O. app sent with Ai.Trigger; the bridge types it into the session when it can.")]
    public void Deliver(string app, string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return;
        _waiting.Add((app, prompt));
        while (_waiting.Count > MaxWaiting)
        {
            _waiting.RemoveAt(0);
            _dropped++;
        }
        _ = StepAsync();
    }

    [McpObservable("How many prompts wait to be typed into the session.")]
    public int Waiting => _waiting.Count;

    [McpObservable("How many prompts the bridge typed into the session.")]
    public int Delivered => _delivered;

    [McpObservable("What the bridge is doing or waiting for.")]
    public string State => _stateLine.Text ?? "";

    [McpObservable("How often the bridge opened its session's link to bring it to the front.")]
    public int Opened => _opened;

    [McpObservable("The session the bridge types into, by its title.")]
    public string Session => _session;

    /// <summary>How a prompt arrives in the session: with the app it is from, like a channel event.</summary>
    public static string Label(string app, string prompt) => $"From the N.E.O. app \"{app}\":\n{prompt.Trim()}";

    /// <summary>The session a "…, rename session" button belongs to; null for any other name.</summary>
    public static string? SessionOf(string name) =>
        name.EndsWith(SessionSuffix, StringComparison.Ordinal) ? name[..^SessionSuffix.Length] : null;

    private async Task StepAsync()
    {
        if (_stepping) return;
        _stepping = true;
        try
        {
            Show(await NextAsync());
        }
        catch (Exception ex)
        {
            Show("Something went wrong: " + ex.Message);
        }
        finally
        {
            _stepping = false;
        }
    }

    // One look at the Claude window. Types the next prompt when everything allows it; says what it saw.
    private async Task<string> NextAsync()
    {
        if (!_claude.IsOpen) return "Waiting for the Claude app.";
        if (_session.Length == 0) return "Waiting: not bound to a session.";
        if (_waiting.Count == 0) return "Ready.";
        if (_paused) return "Paused.";
        if (DateTime.UtcNow < _quietUntil) return "Sent. Waiting for Claude to start.";

        var front = await FrontSessionsAsync();
        if (front.Count == 0) return "Waiting: Claude shows no session.";
        if (front.Count > 1) return "Waiting: Claude shows more than one session.";

        if (_openPending && front[0] != _session)
        {
            if (DateTime.UtcNow - _openedAt < LinkTakes && front[0] != _frontBeforeOpen)
            {
                // The link brought the session, with a title of its own: it was renamed.
                Bind(front[0], _link ?? "");
                AddToLog("bridge", $"The session is called \"{_session}\" now.");
            }
            else if (DateTime.UtcNow - _openedAt < LinkTakes)
                return "Bringing the session to the front.";
            else
            {
                _openPending = false;
                _failedAt = DateTime.UtcNow;
            }
        }
        if (front[0] == _session) _openPending = false;
        else
        {
            if (_front.IsChecked == true && _link != null)
            {
                var why = await WhyNotToTheFrontAsync();
                if (why == null)
                {
                    _frontBeforeOpen = front[0];
                    _openedAt = DateTime.UtcNow;
                    _openPending = true;
                    OpenSession();
                    return "Bringing the session to the front.";
                }
                return $"Waiting for \"{_session}\" to be in front (now \"{front[0]}\"); not brought to the front: {why}.";
            }
            return $"Waiting for \"{_session}\" to be in front (now \"{front[0]}\").";
        }

        if ((await _claude.FindAsync(StopButton)).Any(e => e.Type == "Button" && e.Name == StopButton))
            return "Waiting: Claude is working.";

        var draft = await _claude.ReadValueAsync(PromptBox);
        if (draft == null) return "Waiting: no prompt box.";
        if (!string.IsNullOrWhiteSpace(draft)) return "Waiting: the prompt box holds a draft.";

        var (app, prompt) = _waiting[0];
        var text = Label(app, prompt);
        if (!await _claude.SetValueAsync(PromptBox, text)) return "Waiting: the prompt box did not take the text.";

        // The person may have typed in the same moment: then the box is theirs.
        await Task.Delay(400);
        if (Normal(await _claude.ReadValueAsync(PromptBox)) != Normal(text))
        {
            _paused = true;
            _pause.Content = "Resume";
            return "Paused: the prompt box changed while the bridge wrote into it.";
        }

        var send = await _claude.FindAsync(SendButton);
        if (!send.Any(e => e.Type == "Button" && e.Name == SendButton && e.IsEnabled) || !await _claude.ClickAsync(SendButton))
        {
            await _claude.SetValueAsync(PromptBox, ""); // the bridge's own text, read back a moment ago
            return "Waiting: Claude offered no Send button.";
        }

        _waiting.RemoveAt(0);
        _delivered++;
        _quietUntil = DateTime.UtcNow + Settle;
        AddToLog(app, prompt);
        return "Sent.";
    }

    // Null when the bridge may take the session in front away from the person; else why not.
    private async Task<string?> WhyNotToTheFrontAsync()
    {
        if (DateTime.UtcNow - _failedAt < BetweenTries) return "the last try did not bring it";
        if (!_standIn && SinceLastInput() < Unused) return "someone is using the PC";
        if (!string.IsNullOrWhiteSpace(await _claude.ReadValueAsync(PromptBox))) return "the session in front holds a draft";
        return null;
    }

    private void OpenSession()
    {
        if (_link == null) return;
        _opened++;
        if (!_standIn) Process.Start(new ProcessStartInfo(_link) { UseShellExecute = true });
    }

    private static TimeSpan SinceLastInput()
    {
        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        return GetLastInputInfo(ref info) ? TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.Time)) : TimeSpan.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint Time;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);

    // The sessions the Claude window shows: one, or two side by side, or none (another page of the app).
    private async Task<List<string>> FrontSessionsAsync() =>
        (await _claude.FindAsync(SessionSuffix))
            .Where(e => e.Type == "Button")
            .Select(e => SessionOf(e.Name))
            .OfType<string>()
            .Distinct()
            .ToList();

    private static string Normal(string? text) => (text ?? "").Replace("\r\n", "\n").Trim();

    private void Show(string state)
    {
        _stateLine.Text = state;
        _countLine.Text = $"{_waiting.Count} waiting · {_delivered} sent" + (_dropped > 0 ? $" · {_dropped} dropped" : "");
    }

    private void AddToLog(string app, string prompt)
    {
        var line = prompt.Trim().Replace('\n', ' ');
        if (line.Length > 60) line = line[..60] + "…";
        _log.Children.Insert(0, Line(11, FontWeight.Normal, $"{DateTime.Now:HH:mm} {app}: {line}"));
        while (_log.Children.Count > LogLines) _log.Children.RemoveAt(_log.Children.Count - 1);
    }

    private static TextBlock Line(double size, FontWeight weight, string text) =>
        new() { FontSize = size, FontWeight = weight, Text = text, TextWrapping = TextWrapping.Wrap };

    private static Button Small(string text) =>
        new() { Content = text, FontSize = 11, Padding = new Thickness(8, 3), Margin = new Thickness(0, 0, 4, 4) };
}
