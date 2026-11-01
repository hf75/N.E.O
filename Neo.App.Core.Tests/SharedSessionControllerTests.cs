using System.Collections.Concurrent;
using System.Text;
using FluentAssertions;
using Neo.Collab;
using Neo.IPC;
using Xunit;

namespace Neo.App.Core.Tests;

/// <summary>
/// Two N.E.O.s — two real <see cref="AppController"/>s, each on its own "UI thread" — share a
/// session over real WebRTC, set up with an invitation and a reply code. Everything is real down
/// to the compiler; only the windows are stand-ins (the app "window" records which build it was
/// given). Covers joining (the guest gets the host's app), changes both ways, the turn (nobody
/// changes the app while someone else does), undo, and leaving.
/// </summary>
[Collection("SettingsFolder")] // redirects the settings folder, which the settings tests read
[Trait("Category", "WebRTC")]
public sealed class SharedSessionControllerTests : IDisposable
{
    private readonly string _settings = Path.Combine(Path.GetTempPath(), "neo_session_settings_" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _network = Path.Combine(Path.GetTempPath(), "neo_session_network_" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string? _previousSettings = Environment.GetEnvironmentVariable(SettingsService.FolderEnvironmentVariable);
    private readonly string? _previousNetwork = Environment.GetEnvironmentVariable("NEO_APP_NETWORK_DIR");

    public SharedSessionControllerTests()
    {
        // Never the user's settings or app network.
        Directory.CreateDirectory(_settings);
        Environment.SetEnvironmentVariable(SettingsService.FolderEnvironmentVariable, _settings);
        Environment.SetEnvironmentVariable("NEO_APP_NETWORK_DIR", _network);
    }

    private static string App(string marker) => $$"""
        public class DynamicUserControl
        {
            public string Version() => "{{marker}}";
        }
        """;

    [Fact]
    public async Task Two_NEOs_share_a_session_and_every_change_reaches_the_other()
    {
        using var annaUi = new UiThread("Anna");
        using var benUi = new UiThread("Ben");
        var annaView = new FakeView("Neo.Tests.Session");
        var benView = new FakeView("Neo.Tests.Session");
        var anna = annaUi.Invoke(() => new AppController(annaView, new FakePlatform()));
        var ben = benUi.Invoke(() => new AppController(benView, new FakePlatform()));
        anna.DllPath.Should().NotBe(ben.DllPath, "two N.E.O.s on one machine build into folders of their own");

        // Anna has an app before she shares.
        await annaUi.Run(() => anna.ApplyManualCodeEditAsync(App("MARKER-ONE")));
        annaView.Child.Shows("MARKER-ONE").Should().BeTrue(string.Join(" | ", annaView.Log.Lines));

        // The codes, as people would pass them on.
        var invite = await annaUi.Run(() => anna.CreateSessionInviteAsync("Anna"));
        var reply = await benUi.Run(() => ben.JoinSessionAsync(invite, "Ben"));
        var joined = benUi.Run(() => ben.CompleteJoinAsync());
        await annaUi.Run(() => anna.AcceptGuestReplyAsync(reply));
        await joined;

        // Ben has Anna's app now.
        await WaitUntil(() => benView.Child.Shows("MARKER-ONE"), "Anna's app at Ben's", benView);
        benView.Log.Lines.Should().Contain(l => l.Contains("You joined Anna's session"));
        benUi.Invoke(() => ben.Sharing!.Participants).Should().Equal("Anna", "Ben");

        // Ben changes it — Anna gets the change.
        await benUi.Run(() => ben.ApplyManualCodeEditAsync(App("MARKER-TWO")));
        await WaitUntil(() => annaView.Child.Shows("MARKER-TWO"), "Ben's change at Anna's", annaView);
        annaView.Log.Lines.Should().Contain("Ben edited the code.");
        annaUi.Invoke(() => anna.AppState.LastCode).Should().Contain("MARKER-TWO");

        // While Ben has the turn, Anna cannot change the app — and her prompt text stays hers.
        var benSession = benUi.Invoke(() => ben.Sharing!);
        (await benUi.Run(() => benSession.TryTakeTurnAsync("thinking about colours"))).Granted.Should().BeTrue();
        await WaitUntil(() => annaUi.Invoke(() => anna.CurrentStatus) == AppStatus.Busy, "Anna locked", annaView);
        annaView.Log.Lines.Should().Contain(l => l.Contains("Ben is thinking about colours"));
        (await annaUi.Run(() => anna.ExecutePromptAsync("make it blue"))).Should().BeFalse();
        annaView.BusyOverlays.Should().Be(0, "the lock keeps the app usable: no overlay over it");
        await benUi.Run(() => benSession.ReleaseTurnAsync());
        await WaitUntil(() => annaUi.Invoke(() => anna.CurrentStatus) == AppStatus.Idle, "Anna free again", annaView);

        // Anna undoes Ben's change — Ben's app goes back too.
        (await annaUi.Run(() => anna.Undo())).Should().BeTrue();
        await WaitUntil(() => benView.Child.Shows("MARKER-ONE"), "the undo at Ben's", benView);
        benView.Log.Lines.Should().Contain("Anna undid the last change.");

        // Ben leaves; Anna is alone in her session and keeps her app.
        await benUi.Run(() => ben.LeaveSessionAsync());
        await WaitUntil(() => annaUi.Invoke(() => anna.Sharing?.Participants.Count) == 1, "Anna alone", annaView);
        annaView.Log.Lines.Should().Contain("Ben left the session.");
        benUi.Invoke(() => ben.Sharing).Should().BeNull();
        await annaUi.Run(() => anna.LeaveSessionAsync());
    }

    [Fact]
    public async Task The_running_app_moves_to_another_person_and_comes_home()
    {
        using var annaUi = new UiThread("Anna");
        using var benUi = new UiThread("Ben");
        var annaView = new FakeView("Neo.Tests.Session");
        var benView = new FakeView("Neo.Tests.Session");
        var anna = annaUi.Invoke(() => new AppController(annaView, new FakePlatform()));
        var ben = benUi.Invoke(() => new AppController(benView, new FakePlatform()));
        await annaUi.Run(() => anna.ApplyManualCodeEditAsync(App("MARKER-ONE")));

        // Nobody to send it to yet.
        var alone = await Assert.ThrowsAsync<InvalidOperationException>(() => annaUi.Run(() => anna.TeleportAppAsync("Ben")));
        alone.Message.Should().Contain("shared session");

        var invite = await annaUi.Run(() => anna.CreateSessionInviteAsync("Anna"));
        var reply = await benUi.Run(() => ben.JoinSessionAsync(invite, "Ben"));
        var joined = benUi.Run(() => ben.CompleteJoinAsync());
        await annaUi.Run(() => anna.AcceptGuestReplyAsync(reply));
        await joined;
        await WaitUntil(() => benView.Child.Shows("MARKER-ONE"), "Anna's app at Ben's", benView);
        annaView.Child.State = new AppStateCapturedMessage("ANNAS DATA", 3, 2048, ["_clock (DispatcherTimer)"]);

        // He says no: nothing left her PC, nothing was even taken.
        benView.Answer = false;
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => annaUi.Run(() => anna.TeleportAppAsync("Ben")));
        refused.Message.Should().Contain("Ben did not want to take it");
        benView.Questions.Should().ContainSingle().Which.Should().Contain("Anna wants to send you the running app").And.Contain("replaced");
        annaView.Child.Captures.Should().Be(0);
        annaView.Child.Windows.Should().BeEmpty();
        benView.Child.Restored.Should().BeEmpty();

        // He says yes: her window goes, the app is at his, as it was.
        benView.Answer = true;
        await annaUi.Run(() => anna.TeleportAppAsync("Ben"));
        benView.Child.Restored.Should().ContainSingle();
        var taken = benView.Child.Restored.Single();
        taken.Image.Should().Be("ANNAS DATA");
        taken.RestoreUi.Should().BeTrue("the text in the boxes goes along");
        taken.RebuildMethods.Should().Contain("BuildUi");
        benView.Child.Windows.Should().ContainSingle().Which.Should().Match<AppWindowMessage>(m => m.Visible == true && m.BringToFront && m.Banner!.Contains("came from Anna"));
        annaView.Child.Windows.Should().ContainSingle().Which.Visible.Should().BeFalse();
        annaUi.Invoke(() => anna.AppIsAt).Should().Be("Ben");
        benUi.Invoke(() => ben.AppIsAt).Should().BeNull();
        annaView.Log.Lines.Should().Contain(l => l.Contains("The app is at Ben's now") && l.Contains("3 value(s)") && l.Contains("2 KB") && l.Contains("_clock"));
        benView.Log.Lines.Should().Contain(l => l.Contains("Anna sent you the running app"));

        // While it is away it cannot go away again.
        var again = await Assert.ThrowsAsync<InvalidOperationException>(() => annaUi.Run(() => anna.TeleportAppAsync("Ben")));
        again.Message.Should().Contain("at Ben's");

        // A change still reaches her hidden copy, and does not bring it back.
        await benUi.Run(() => ben.ApplyManualCodeEditAsync(App("MARKER-TWO")));
        await WaitUntil(() => annaView.Child.Shows("MARKER-TWO"), "Ben's change at Anna's", annaView);
        annaView.Child.Windows.Should().ContainSingle();

        // A state of another version of the app is not taken.
        await benUi.Run(() => ben.TeleportMessageArrivedAsync(new TeleportMessage(TeleportPhases.State, "other", "Anna", "Ben") { Image = "OLD", CodeHash = "another version" }));
        benView.Child.Restored.Should().ContainSingle();
        benView.Log.Lines.Should().Contain(l => l.Contains("could not be taken here") && l.Contains("on its way"));

        // Home: it was hers, so nobody asks her.
        var questionsSoFar = annaView.Questions.Count;
        benView.Child.State = new AppStateCapturedMessage("BENS DATA", 4, 4096, []);
        await benUi.Run(() => ben.TeleportAppAsync("Anna"));
        annaView.Questions.Count.Should().Be(questionsSoFar);
        annaView.Child.Restored.Single().Image.Should().Be("BENS DATA");
        annaView.Child.Windows.Last().Visible.Should().BeTrue();
        annaUi.Invoke(() => anna.AppIsAt).Should().BeNull();
        benUi.Invoke(() => ben.AppIsAt).Should().Be("Anna");

        // He does not want to wait for it: his own copy comes back, as it was.
        await benUi.Run(() => ben.ShowMyCopyAsync());
        benUi.Invoke(() => ben.AppIsAt).Should().BeNull();
        benView.Child.Windows.Last().Visible.Should().BeTrue();

        // It cannot be taken over there: her window comes back, and she hears why.
        benView.Child.RestoreError = "no way";
        var failed = await Assert.ThrowsAsync<InvalidOperationException>(() => annaUi.Run(() => anna.TeleportAppAsync("Ben")));
        failed.Message.Should().Contain("no way");
        annaUi.Invoke(() => anna.AppIsAt).Should().BeNull();
        annaView.Child.Windows.Last().Should().Match<AppWindowMessage>(m => m.Visible == true && m.Banner!.StartsWith("Not sent:"));
        benView.Child.RestoreError = null;

        // A lot of data: 3 MB that do not compress.
        var large = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(1_500_000));
        annaView.Child.State = new AppStateCapturedMessage(large, 1, large.Length, []);
        await annaUi.Run(() => anna.TeleportAppAsync("Ben"));
        benView.Child.Restored.Last().Image.Should().Be(large);
        annaUi.Invoke(() => anna.AppIsAt).Should().Be("Ben");

        // He leaves with it: her copy comes back by itself.
        await benUi.Run(() => ben.LeaveSessionAsync());
        await WaitUntil(() => annaUi.Invoke(() => anna.AppIsAt) == null, "Anna's copy back", annaView);
        annaView.Child.Windows.Last().Should().Match<AppWindowMessage>(m => m.Visible == true && m.Banner!.Contains("Ben left the session"));
        await annaUi.Run(() => anna.LeaveSessionAsync());
    }

    [Fact]
    public async Task When_the_other_person_is_gone_nobody_waits_and_the_copy_comes_back()
    {
        using var annaUi = new UiThread("Anna");
        using var benUi = new UiThread("Ben");
        var annaView = new FakeView("Neo.Tests.Session");
        var benView = new FakeView("Neo.Tests.Session");
        var anna = annaUi.Invoke(() => new AppController(annaView, new FakePlatform()));
        var ben = benUi.Invoke(() => new AppController(benView, new FakePlatform()));
        await annaUi.Run(() => anna.ApplyManualCodeEditAsync(App("MARKER-ONE")));
        annaView.Child.State = new AppStateCapturedMessage("ANNAS DATA", 1, 2048, []);
        benView.Child.State = new AppStateCapturedMessage("BENS DATA", 1, 2048, []);

        async Task JoinAsync()
        {
            var invite = await annaUi.Run(() => anna.CreateSessionInviteAsync("Anna"));
            var reply = await benUi.Run(() => ben.JoinSessionAsync(invite, "Ben"));
            var joined = benUi.Run(() => ben.CompleteJoinAsync());
            await annaUi.Run(() => anna.AcceptGuestReplyAsync(reply));
            await joined;
            await WaitUntil(() => benView.Child.Shows("MARKER-ONE"), "Anna's app at Ben's", benView);
        }

        // He leaves while he is thinking about it: she hears at once, not after two minutes.
        await JoinAsync();
        benView.Thinking = new TaskCompletionSource<bool>();
        var waiting = annaUi.Run(() => anna.TeleportAppAsync("Ben"));
        await WaitUntil(() => benView.Questions.Count == 1, "the question at Ben's", benView);
        await benUi.Run(() => ben.LeaveSessionAsync());
        (await Task.WhenAny(waiting, Task.Delay(TimeSpan.FromSeconds(20)))).Should().Be(waiting, "she does not wait for someone who left");
        (await Assert.ThrowsAsync<InvalidOperationException>(() => waiting)).Message.Should().Contain("Ben did not answer");
        annaView.Child.Windows.Should().BeEmpty("nothing happened");
        benView.Thinking.TrySetResult(false);
        benView.Thinking = null;
        await annaUi.Run(() => anna.LeaveSessionAsync());

        // She ends the session while his app is at hers: his copy comes back.
        benView.Child.State = new AppStateCapturedMessage("BENS DATA", 1, 2048, []);
        await JoinAsync();
        await benUi.Run(() => ben.TeleportAppAsync("Anna"));
        benUi.Invoke(() => ben.AppIsAt).Should().Be("Anna");
        await annaUi.Run(() => anna.LeaveSessionAsync());
        await WaitUntil(() => benUi.Invoke(() => ben.AppIsAt) == null, "Ben's copy back", benView);
        benView.Child.Windows.Last().Visible.Should().BeTrue();

        // He leaves himself while the app is at hers: his copy comes back too.
        await JoinAsync();
        await benUi.Run(() => ben.TeleportAppAsync("Anna"));
        await benUi.Run(() => ben.LeaveSessionAsync());
        benUi.Invoke(() => ben.AppIsAt).Should().BeNull();
        benView.Child.Windows.Last().Visible.Should().BeTrue();
        await annaUi.Run(() => anna.LeaveSessionAsync());
    }

    [Theory]
    [InlineData("a\r\nb\r\n", "a\nb")]
    [InlineData("  code  ", "code")]
    public void Line_ends_do_not_make_two_versions_of_an_app(string one, string other)
    {
        AppController.CodeHash(one).Should().Be(AppController.CodeHash(other));
        AppController.CodeHash(one).Should().NotBe(AppController.CodeHash(other + " changed"));
    }

    private static async Task WaitUntil(Func<bool> condition, string what, FakeView view, int seconds = 60)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(50);
        condition().Should().BeTrue("expected within {0} s: {1}. Chat: {2}", seconds, what, string.Join(" | ", view.Log.Lines.TakeLast(12)));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(SettingsService.FolderEnvironmentVariable, _previousSettings);
        Environment.SetEnvironmentVariable("NEO_APP_NETWORK_DIR", _previousNetwork);
        try { Directory.Delete(_settings, true); } catch { }
        try { Directory.Delete(_network, true); } catch { }
    }

    // ── Stand-ins for the windows ────────────────────────────────────────────

    /// <summary>A thread with a message loop, like a UI thread: everything posted runs on it, in order.</summary>
    private sealed class UiThread : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();
        private readonly Thread _thread;

        public UiThread(string name)
        {
            _thread = new Thread(() =>
            {
                SetSynchronizationContext(this);
                foreach (var (callback, state) in _queue.GetConsumingEnumerable())
                    callback(state);
            }) { IsBackground = true, Name = name };
            _thread.Start();
        }

        public override void Post(SendOrPostCallback d, object? state)
        {
            if (!_queue.IsAddingCompleted) _queue.Add((d, state));
        }

        public override void Send(SendOrPostCallback d, object? state)
        {
            if (Thread.CurrentThread == _thread) { d(state); return; }
            using var done = new ManualResetEventSlim();
            Exception? error = null;
            Post(_ => { try { d(state); } catch (Exception ex) { error = ex; } finally { done.Set(); } }, null);
            done.Wait();
            if (error != null) throw error;
        }

        public T Invoke<T>(Func<T> func)
        {
            T result = default!;
            Send(_ => result = func(), null);
            return result;
        }

        public Task<T> Run<T>(Func<Task<T>> work)
        {
            var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            Post(async _ =>
            {
                try { done.SetResult(await work()); }
                catch (Exception ex) { done.SetException(ex); }
            }, null);
            return done.Task;
        }

        public Task Run(Func<Task> work) => Run(async () => { await work(); return true; });

        public void Dispose() => _queue.CompleteAdding();
    }

    private sealed class RecordingLog : IAppLogger
    {
        private readonly ConcurrentQueue<string> _lines = new();
        public IReadOnlyList<string> Lines => _lines.ToArray();
        public void LogMessage(string message, BubbleType bubbleType) => _lines.Enqueue(message);
        public void LogMessageWithMarkdownFormating(string msg, BubbleType bubbleType) => _lines.Enqueue(msg);
        public void LogHistory(string message) { }
        public void LogHistory(Neo.AssemblyForge.StructuredResponse response) { }
        public void Clear() { }
    }

    private sealed class FakeView(string appName) : IMainView
    {
        public RecordingLog Log { get; } = new();
        public FakeChild Child { get; } = new();
        public int BusyOverlays;
        public bool Answer = true;
        public TaskCompletionSource<bool>? Thinking;
        public List<string> Questions { get; } = new();
        public Task<bool> AskAsync(string title, string question, string yes, string no)
        {
            Questions.Add(question);
            return Thinking?.Task ?? Task.FromResult(Answer);
        }
        public string AppName { get; } = appName;
        public Task SetUiBusyState(bool isBusy, string? message = null, bool showCancel = false, bool showOverlay = true)
        {
            if (isBusy && showOverlay && message != null && message.Contains(" is changing the app")) Interlocked.Increment(ref BusyOverlays);
            return Task.CompletedTask;
        }
        public string PromptText { get; set; } = "";
        public void PromptToNextLine() { }
        public void ClearPrompt() { }
        public void FocusPrompt() { }
        public void ShowFrostedSnapshot(object snapshot) { }
        public Task HideFrostedSnapshotAsync() => Task.CompletedTask;
        public void HideFrostedSnapshot() { }
        public void ShowEmptyContent() { }
        public void ActivateWindow() { }
        public void ResetButtonMenu() { }
        public void ShowRepairOverlay() { }
        public void HideRepairOverlay() { }
        public void SetWaitIndicatorStatus(string text) { }
        public Task<CrashDialogResult> ShowCrashDialogAsync() => Task.FromResult(CrashDialogResult.Button3);
        public Task<PatchReviewDecision> ShowPatchReviewDialogAsync(string patchOrCode, IReadOnlyList<string>? nugetPackages, string? explanation, bool isPowerShellMode = false, bool isConsoleAppMode = false, string? webSearchReport = null)
            => Task.FromResult(PatchReviewDecision.Apply);
        public IChildProcessService CreateChildProcessService() => Child;
        public IAppLogger CreateLogger(ApplicationState appState) => Log;
        public bool CheckUIThreadAccess() => true;
        public void InvokeOnUIThread(Action action) => action();
        public void InvokeOnUIThreadAsync(Action action) => action();
        public T InvokeOnUIThread<T>(Func<T> func) => func();
    }

    /// <summary>The app window: remembers the build it was given last.</summary>
    private sealed class FakeChild : IChildProcessService
    {
        private volatile byte[] _loaded = [];

        public AppStateCapturedMessage? State;
        public string? RestoreError;
        public int Captures;
        public List<RestoreAppStateRequest> Restored { get; } = new();
        public List<AppWindowMessage> Windows { get; } = new();

        public Task<AppStateCapturedMessage?> CaptureAppStateAsync()
        {
            Captures++;
            return Task.FromResult(State);
        }

        public Task<AppStateRestoredMessage?> RestoreAppStateAsync(RestoreAppStateRequest request)
        {
            if (RestoreError != null) return Task.FromResult<AppStateRestoredMessage?>(new AppStateRestoredMessage(false, 0, 0, [], RestoreError));
            Restored.Add(request);
            return Task.FromResult<AppStateRestoredMessage?>(new AppStateRestoredMessage(true, 3, 0, []));
        }

        public Task SetAppWindowAsync(AppWindowMessage message)
        {
            Windows.Add(message);
            return Task.CompletedTask;
        }

        public bool Shows(string marker)
        {
            var needle = Encoding.Unicode.GetBytes(marker);
            return _loaded.AsSpan().IndexOf(needle) >= 0;
        }

        public Task<bool> DisplayControlAsync(string mainDllPath, IEnumerable<string> nugetDlls, IEnumerable<string> additionalDlls)
        {
            _loaded = File.ReadAllBytes(mainDllPath);
            HasLoadedControl = true;
            return Task.FromResult(true);
        }

        public Task RestartAsync() { HasLoadedControl = false; return Task.CompletedTask; }
        public Task UnloadControlAsync() { HasLoadedControl = false; return Task.CompletedTask; }
        public void UpdatePosition(bool useTopMostTrick = false) { }
        public bool IsFocusInsideChild() => false;
        public void ConfigureSandbox(bool useSandbox, SandboxSettings settings) { }
        public void ConfigureCrossplatformSettings(CrossplatformSettings settings) { }
        public void NotifyParentWindowStateChanged(HostWindowState newState) { }
        public Task SetCursorVisibilityAsync(bool isVisible) => Task.CompletedTask;
        public Task SetChildModalityAsync(bool isModal) => Task.CompletedTask;
        public string? HotReloadCapabilities => null;
        public Task<HotReloadResultMessage?> HotReloadAsync(HotReloadRequest request, IEnumerable<string> dependencyFiles) => Task.FromResult<HotReloadResultMessage?>(null);
        public object? CaptureChildScreenshot() => null;
        public bool HasLoadedControl { get; private set; }
        public int? ChildProcessId => null;
        public void HideChild() { }
        public void ShowChild() { }
        public event Func<CrashReason, ErrorMessage, Task>? ChildProcessCrashed { add { } remove { } }
        public event Action<LogMessage>? ChildLogReceived { add { } remove { } }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakePlatform : IPlatformServices
    {
        public string GetStartupPath() => AppContext.BaseDirectory;
        public bool InjectIcon(string exePath, string iconPath) => false;
        public void InstallApplicationPerUser(string basePath, string appName, bool installStartMenu, bool installDesktop, string? displayVersion, string? publisher) { }
        public void UninstallShortcuts(string appName, bool removeFromStartMenu, bool removeFromDesktop, bool throwOnError) { }
    }
}
