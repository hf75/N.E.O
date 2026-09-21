using FluentAssertions;
using Neo.Shared;
using Xunit;

namespace Neo.HotReload.Tests;

/// <summary>
/// The promise of hot reload: a generated app that is refined by another prompt keeps running —
/// same object, same data — and still becomes the new version. Every test loads an app, uses it,
/// hot reloads the next version of its source and checks both: nothing was lost, the new code is live.
/// (No UI toolkit here: "DynamicUserControl" is a plain class whose BuildUi() writes a string.)
/// </summary>
public class HotReloadSessionTests
{
    private const string V1 = """
        using System;
        using System.Collections.Generic;

        public class Item { public string Title = ""; }

        public class DynamicUserControl
        {
            private static readonly string Accent = "red";
            private readonly string _title = "Todo";
            private readonly List<Item> _items = new();
            private int _clicks;
            public string Ui = "";
            public Func<string> Tick;

            public DynamicUserControl()
            {
                Tick = () => "tick v1, " + _items.Count + " item(s)";
                BuildUi();
            }

            private void BuildUi() { Ui = _title + " [" + Accent + "] items=" + _items.Count; }

            public void AddItem(string title) { _items.Add(new Item { Title = title }); _clicks++; }
            public string Describe() => "v1 clicks=" + _clicks;
        }
        """;

    [Fact]
    public void The_test_host_can_hot_reload()
        => HotReloadRuntime.IsSupported.Should().BeTrue(
            "hotreload.runsettings sets DOTNET_MODIFIABLE_ASSEMBLIES=debug for the test host; without it none of these tests mean anything");

    [Fact]
    public async Task Changed_logic_is_live_and_the_running_instance_keeps_its_state()
    {
        using var app = await HotReloadHarness.StartAsync(V1);
        app.Call("AddItem", "loaded from the Excel file");
        var liveTick = app.Get<Func<string>>("Tick");

        var (update, applied) = await app.UpdateAsync(V1
            .Replace("\"v1 clicks=\"", "\"v2 clicks=\"")
            .Replace("\"tick v1, \"", "\"tick v2, \""));

        update.Status.Should().Be(HotReloadStatus.Ready, update.Reason);
        applied!.Generations.Should().Be(1);
        app.Call("Describe").Should().Be("v2 clicks=1", "new code, old state");
        liveTick().Should().Be("tick v2, 1 item(s)",
            "a delegate that is already subscribed (a timer tick, CollectionChanged) has to run the new body — Roslyn maps the lambda instead of retiring it");
        update.Request!.RebuildUi.Should().BeFalse("nothing that runs while the UI is built has changed");
    }

    [Fact]
    public async Task Added_fields_are_initialised_on_objects_that_already_exist()
    {
        using var app = await HotReloadHarness.StartAsync(V1);
        app.Call("AddItem", "first");

        // "Add tags": a new collection on the control, a new field and a new auto-property on the model.
        var v2 = V1
            .Replace("public class Item { public string Title = \"\"; }",
                     "public class Item { public string Title = \"\"; public List<string> Tags = new() { \"new\" }; public int Priority { get; set; } = 3; }")
            .Replace("private int _clicks;", "private int _clicks;\n    private readonly List<string> _tags = new() { \"a\", \"b\" };")
            .Replace("public string Describe() => \"v1 clicks=\" + _clicks;",
                     "public string Describe() => \"tags=\" + _tags.Count + \" firstItemTags=\" + _items[0].Tags.Count + \" prio=\" + _items[0].Priority;");

        var (update, applied) = await app.UpdateAsync(v2);

        update.Status.Should().Be(HotReloadStatus.Ready, update.Reason);
        applied!.FieldsInitialised.Should().Be(3);
        app.Call("Describe").Should().Be("tags=2 firstItemTags=1 prio=3",
            "initializers never run again for existing objects; without the state steps this is a NullReferenceException");
    }

    [Fact]
    public async Task Changed_constants_are_reapplied_and_the_ui_is_rebuilt_but_user_state_stays()
    {
        using var app = await HotReloadHarness.StartAsync(V1);
        app.Call("AddItem", "kept");

        // "Make it blue and call it Tasks" — and an attempt to reset mutable state, which must not happen.
        var (update, _) = await app.UpdateAsync(V1
            .Replace("Accent = \"red\"", "Accent = \"blue\"")
            .Replace("_title = \"Todo\"", "_title = \"Tasks\"")
            .Replace("private int _clicks;", "private int _clicks = 100;"));

        update.Status.Should().Be(HotReloadStatus.Ready, update.Reason);
        update.Request!.RebuildUi.Should().BeTrue();
        update.Request.RebuildMethodName.Should().Be("BuildUi");
        app.Get<string>("Ui").Should().Be("Tasks [blue] items=1");
        app.Call("Describe").Should().Be("v1 clicks=1", "a mutable field is what the user did, not configuration");
    }

    [Fact]
    public async Task Ui_code_changes_ask_for_a_rebuild_with_the_current_state()
    {
        using var app = await HotReloadHarness.StartAsync(V1);
        app.Call("AddItem", "one");
        app.Call("AddItem", "two");

        var (update, _) = await app.UpdateAsync(V1.Replace("\"] items=\" + _items.Count", "\"] entries: \" + _items.Count + Footer()")
            .Replace("public string Describe()", "private string Footer() => \" (footer)\";\n    public string Describe()"));

        update.Status.Should().Be(HotReloadStatus.Ready, update.Reason);
        update.Request!.RebuildUi.Should().BeTrue();
        app.Get<string>("Ui").Should().Be("Todo [red] entries: 2 (footer)");
    }

    [Fact]
    public async Task Statements_added_to_the_constructor_are_replayed_once()
    {
        using var app = await HotReloadHarness.StartAsync(V1);

        // "Add a clock": the feature needs start-up code, and the constructor will not run again.
        var (update, applied) = await app.UpdateAsync(V1
            .Replace("BuildUi();", "StartClock();\n        BuildUi();")
            .Replace("public string Describe()", "public int ClockStarts;\n    private void StartClock() { ClockStarts++; }\n    public string Describe()"));

        update.Status.Should().Be(HotReloadStatus.Ready, update.Reason);
        applied!.HelpersRun.Should().Be(1);
        app.Get<int>("ClockStarts").Should().Be(1);
    }

    [Fact]
    public async Task A_constructor_change_that_cannot_be_replayed_means_full_reload()
    {
        using var app = await HotReloadHarness.StartAsync(V1);

        var (update, _) = await app.UpdateAsync(V1.Replace("BuildUi();", "_clicks = 5;\n        BuildUi();")
            .Replace("Tick = () => \"tick v1, \" + _items.Count + \" item(s)\";", "Tick = null!;"));

        update.Status.Should().Be(HotReloadStatus.NotPossible);
        update.Reason.Should().Contain("constructor");
    }

    [Fact]
    public async Task An_app_without_a_rebuild_method_is_not_left_with_a_stale_ui()
    {
        var noHook = V1.Replace("BuildUi", "MakeScreen");
        using var app = await HotReloadHarness.StartAsync(noHook);

        var logicOnly = await app.UpdateAsync(noHook.Replace("\"v1 clicks=\"", "\"v2 clicks=\""));
        logicOnly.Update.Status.Should().Be(HotReloadStatus.Ready, "handlers and logic can always be patched");

        var uiChange = await app.UpdateAsync(noHook.Replace("\"v1 clicks=\"", "\"v2 clicks=\"").Replace("\"] items=\"", "\"] entries=\""));
        uiChange.Update.Status.Should().Be(HotReloadStatus.NotPossible);
        uiChange.Update.Reason.Should().Contain("BuildUi");
    }

    [Fact]
    public async Task Edits_the_runtime_cannot_apply_are_reported_with_roslyns_reason()
    {
        using var app = await HotReloadHarness.StartAsync(V1);

        var (update, _) = await app.UpdateAsync(V1
            .Replace("public class DynamicUserControl", "public class DynamicUserControl : IDisposable")
            .Replace("public string Describe()", "public void Dispose() { }\n    public string Describe()"));

        update.Status.Should().Be(HotReloadStatus.NotPossible);
        update.Reason.Should().Contain("ENC", "the host shows Roslyn's rude edit diagnostic to the user");
    }

    [Fact]
    public async Task A_timer_the_ui_method_replaces_is_stopped_instead_of_ticking_twice()
    {
        // A pattern generated code often has: the timer is created while building the UI.
        const string clock = """
            using System.Timers;

            public class DynamicUserControl
            {
                private Timer? _clock;
                public string Ui = "";

                public DynamicUserControl() { BuildUi(); }

                private void BuildUi()
                {
                    _clock = new Timer(60_000);
                    _clock.Start();
                    Ui = "clock v1";
                }
            }
            """;
        using var app = await HotReloadHarness.StartAsync(clock);
        var firstTimer = app.Get<System.Timers.Timer>("_clock");
        firstTimer.Enabled.Should().BeTrue();

        var (update, _) = await app.UpdateAsync(clock.Replace("\"clock v1\"", "\"clock v2\""));

        update.Status.Should().Be(HotReloadStatus.Ready, update.Reason);
        app.Get<string>("Ui").Should().Be("clock v2");
        var secondTimer = app.Get<System.Timers.Timer>("_clock");
        secondTimer.Should().NotBeSameAs(firstTimer);
        secondTimer.Enabled.Should().BeTrue();
        firstTimer.Enabled.Should().BeFalse("the rebuild replaced it; left alone it would keep ticking next to the new one");
        secondTimer.Dispose();
    }

    [Fact]
    public async Task Generations_stack_up()
    {
        using var app = await HotReloadHarness.StartAsync(V1);
        app.Call("AddItem", "kept");

        // Generation 1 injects helpers (new field); generation 2 no longer has them in its source.
        var v2 = V1.Replace("private int _clicks;", "private int _clicks;\n    private readonly List<int> _numbers = new() { 1, 2, 3 };")
                   .Replace("\"v1 clicks=\" + _clicks", "\"v2 numbers=\" + _numbers.Count");
        (await app.UpdateAsync(v2)).Update.Status.Should().Be(HotReloadStatus.Ready);
        app.Call("Describe").Should().Be("v2 numbers=3");

        var (third, _) = await app.UpdateAsync(v2.Replace("\"v2 numbers=\"", "\"v3 numbers=\""));
        third.Status.Should().Be(HotReloadStatus.Ready, third.Reason);
        app.Call("Describe").Should().Be("v3 numbers=3");
        app.Session.Generation.Should().Be(2);

        (await app.UpdateAsync(v2.Replace("\"v2 numbers=\"", "\"v3 numbers=\""))).Update.Status.Should().Be(HotReloadStatus.NoChanges);
    }
}
