using System.Reflection;
using FluentAssertions;
using Neo.Shared;
using Xunit;

namespace Neo.HotReload.Tests;

/// <summary>
/// Hot reload of code the compile agent rewrote so that no exception vanishes (Neo.ErrorSurfacing):
/// the session rewrites every generation the same way, so a dropped task or a guarded timer handler
/// is patched like any other code, and the rebuild still renews a guarded subscription once.
/// </summary>
public class LostExceptionsHotReloadTests
{
    private const string App = """
        using System;
        using System.Linq;
        using System.Threading.Tasks;
        using System.Timers;

        public class DynamicUserControl
        {
            private readonly Timer _timer = new Timer(60_000);
            public string Last = "";
            public string Ui = "";

            public DynamicUserControl() { BuildUi(); }

            private void BuildUi()
            {
                Ui = "v1";
                _timer.Elapsed += OnElapsed;
            }

            private void OnElapsed(object? sender, ElapsedEventArgs e) => Last = "tick v1";

            public void Start() { RunAsync(); }

            private async Task RunAsync() { await Task.Yield(); Last = "run v1"; }

            public void Elapse() => ElapsedHandlers()?.Invoke(_timer, null!);

            public ElapsedEventHandler? ElapsedHandlers()
                => (ElapsedEventHandler?)typeof(Timer).GetFields(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                    .Single(f => f.FieldType == typeof(ElapsedEventHandler)).GetValue(_timer);
        }
        """;

    [Fact]
    public async Task A_dropped_task_and_a_guarded_handler_are_patched_in_place()
    {
        using var app = await HotReloadHarness.StartAsync(App);
        var handler = app.Call("ElapsedHandlers") as Delegate;
        handler!.Method.DeclaringType!.FullName.Should().Be("NeoRuntime.LostExceptions", "the compile agent wrapped the handler");
        ((Delegate)handler.Target!).Method.Name.Should().Be("OnElapsed");

        var (update, _) = await app.UpdateAsync(App
            .Replace("\"tick v1\"", "\"tick v2\"")
            .Replace("\"run v1\"", "\"run v2\""));

        update.Status.Should().Be(HotReloadStatus.Ready, update.Reason);
        app.Call("Elapse");
        app.Get<string>("Last").Should().Be("tick v2", "the wrapper runs the patched handler");
        app.Call("Start");
        await WaitUntil(() => app.Get<string>("Last") == "run v2");
    }

    [Fact]
    public async Task The_rebuild_renews_a_guarded_subscription_once()
    {
        using var app = await HotReloadHarness.StartAsync(App);

        var (update, _) = await app.UpdateAsync(App.Replace("Ui = \"v1\";", "Ui = \"v2\";"));

        update.Status.Should().Be(HotReloadStatus.Ready, update.Reason);
        update.Request!.RebuildUi.Should().BeTrue();
        app.LastRebuild!.RemovedSubscriptions.Should().Be(1, "the old wrapper stands for OnElapsed and is replaced by the new one");
        ((Delegate)app.Call("ElapsedHandlers")!).GetInvocationList().Should().ContainSingle();
    }

    [Fact]
    public async Task New_dropped_tasks_and_new_timer_handlers_can_come_with_an_update()
    {
        using var app = await HotReloadHarness.StartAsync(App);

        var (update, _) = await app.UpdateAsync(App.Replace(
            "public void Start() { RunAsync(); }",
            "public void Start() { RunAsync(); _ = Task.Run(() => { }); _timer.Elapsed += (s, e) => Last = \"lambda\"; }"));

        update.Status.Should().Be(HotReloadStatus.Ready, update.Reason);
        app.Call("Start");
        await WaitUntil(() => app.Get<string>("Last") == "run v1");
        ((Delegate)app.Call("ElapsedHandlers")!).GetInvocationList().Should().HaveCount(2);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(20);
        condition().Should().BeTrue();
    }
}
