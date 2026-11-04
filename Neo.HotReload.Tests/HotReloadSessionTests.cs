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

    private const string LoopApp = """
        using System;
        using System.Collections.Generic;
        using System.Threading.Tasks;

        public class DynamicUserControl
        {
            private double _zoom = 1;
            private int _clicks;
            public Func<double> Read = () => 0;
            public string Ui = "";
            public event EventHandler? Sized;
            public event EventHandler? Pressed;

            public DynamicUserControl()
            {
                Task.Run(() => Work(_zoom));
                InitializeComponents();
            }

            private void Work(double zoom) { }

            public void InitializeComponents()
            {
                var root = new List<int> { 1 };
                Sized += (s, e) => { _zoom = _zoom * 2; };
                Pressed += (s, e) => { _clicks = _clicks + 1; };
                Read = () => _zoom;
                Ui = "parts " + root.Count;
            }
        }
        """;

    [Fact]
    public async Task A_new_field_and_a_lambda_that_starts_capturing_a_local_can_come_in_one_change()
    {
        using var app = await HotReloadHarness.StartAsync(LoopApp);

        // What a merge of two programs writes: Roslyn's delta writer alone throws on this ("same key has already been added").
        var (update, _) = await app.UpdateAsync(LoopApp
            .Replace("private int _clicks;", "private int _clicks;\n    private double[] _hues = new double[] { 270, 315 };\n    public int Width { get; set; } = 500;")
            .Replace("Read = () => _zoom;", "Sized += (s, e) => { root.Add(root.Count); };\n        Read = () => _zoom + _hues[1] + Width + root.Count;"));

        update.Status.Should().Be(HotReloadStatus.Ready, update.Reason);
        app.Get<double[]>("_hues").Should().Equal(270, 315);
        app.Get<int>("Width").Should().Be(500);
        app.Get<Func<double>>("Read")().Should().Be(1 + 315 + 500 + 1);

        // And it goes on from there, with the source as the model wrote it.
        var (next, _) = await app.UpdateAsync(LoopApp
            .Replace("private int _clicks;", "private int _clicks;\n    private double[] _hues = new double[] { 270, 315 };\n    public int Width { get; set; } = 500;")
            .Replace("Read = () => _zoom;", "Sized += (s, e) => { root.Add(root.Count); };\n        Read = () => _zoom + _hues[1] + Width + root.Count + 1000;"));
        next.Status.Should().Be(HotReloadStatus.Ready, next.Reason);
        app.Get<Func<double>>("Read")().Should().Be(1 + 315 + 500 + 1 + 1000);
    }

    [Fact]
    public async Task An_array_written_in_a_patch_does_not_bring_the_process_down()
    {
        using var app = await HotReloadHarness.StartAsync(LoopApp);

        // The compiler keeps its data in a static struct, and the walk over the plugin's statics used to read that.
        var (update, _) = await app.UpdateAsync(LoopApp
            .Replace("private int _clicks;", "private int _clicks;\n    private double[] _hues = new double[] { 275, 315, 28, 46, 58, 12, 13, 14 };"));
        update.Status.Should().Be(HotReloadStatus.Ready, update.Reason);

        // Reflection goes to a fast path from the second read on: that is where it crashed, with no way to catch it.
        for (int i = 0; i < 5; i++)
            HotReloadRuntime.Rebuild(app.Root, "InitializeComponents");

        app.Get<double[]>("_hues").Should().HaveCount(8);
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
    public async Task Subscriptions_the_ui_method_makes_again_fire_once_after_the_rebuild()
    {
        // BuildUi subscribes to things that outlive the rebuild: an object in a field, a collection,
        // a static event. Building again subscribes again — without cleanup every handler would
        // then run twice.
        const string v1 = """
            using System;
            using System.Collections.ObjectModel;

            public class Ticker
            {
                public event EventHandler? Tick;
                public void Fire() => Tick?.Invoke(this, EventArgs.Empty);
            }

            public static class Bus
            {
                public static event Action? Changed;
                public static void Raise() => Changed?.Invoke();
            }

            public class DynamicUserControl
            {
                private readonly Ticker _ticker = new();
                private readonly ObservableCollection<string> _items = new();
                public int Ticks, Changes, BusHits;
                public string Title = "";

                public DynamicUserControl() { BuildUi(); }

                private void BuildUi()
                {
                    Title = "v1";
                    _ticker.Tick += OnTick;
                    _items.CollectionChanged += (s, e) => Changes++;
                    Bus.Changed += () => BusHits++;
                }

                private void OnTick(object? sender, EventArgs e) => Ticks++;

                public void Fire()
                {
                    _ticker.Fire();
                    _items.Add("x");
                    Bus.Raise();
                }
            }
            """;

        using var app = await HotReloadHarness.StartAsync(v1);
        app.Call("Fire");

        var (update, _) = await app.UpdateAsync(v1.Replace("Title = \"v1\";", "Title = \"v2\";"));

        update.Status.Should().Be(HotReloadStatus.Ready, update.Reason);
        update.Request!.RebuildUi.Should().BeTrue();
        app.Get<string>("Title").Should().Be("v2");
        app.LastRebuild!.RemovedSubscriptions.Should().Be(3, "one old subscription per event the rebuild subscribed to again");

        app.Call("Fire");
        app.Get<int>("Ticks").Should().Be(2);
        app.Get<int>("Changes").Should().Be(2);
        app.Get<int>("BusHits").Should().Be(2);
    }

    [Fact]
    public async Task Handlers_subscribed_outside_the_ui_method_are_left_alone()
    {
        // The constructor subscribes once, the UI method once more; after the rebuild both are
        // still there — exactly what a fresh start would have.
        const string v1 = """
            using System;

            public class Ticker
            {
                public event EventHandler? Tick;
                public void Fire() => Tick?.Invoke(this, EventArgs.Empty);
            }

            public class DynamicUserControl
            {
                private readonly Ticker _ticker = new();
                public int Ticks;
                public string Title = "";

                public DynamicUserControl()
                {
                    _ticker.Tick += OnTick;
                    BuildUi();
                }

                private void BuildUi()
                {
                    Title = "v1";
                    _ticker.Tick += OnTick;
                }

                private void OnTick(object? sender, EventArgs e) => Ticks++;

                public void Fire() => _ticker.Fire();
            }
            """;

        using var app = await HotReloadHarness.StartAsync(v1);
        app.Call("Fire");
        app.Get<int>("Ticks").Should().Be(2);

        var (update, _) = await app.UpdateAsync(v1.Replace("Title = \"v1\";", "Title = \"v2\";"));
        update.Status.Should().Be(HotReloadStatus.Ready, update.Reason);

        app.Call("Fire");
        app.Get<int>("Ticks").Should().Be(4, "two subscriptions before, two after");
    }

    [Fact]
    public async Task A_field_the_new_code_drops_stays_unused_and_the_app_keeps_its_state()
    {
        using var app = await HotReloadHarness.StartAsync(V1);
        app.Call("AddItem", "kept");

        // "Take the click counter out": its field goes with it, and Roslyn cannot take a field out of a running app (ENC0033).
        var v2 = V1.Replace("private int _clicks;", "")
                   .Replace(" _clicks++;", "")
                   .Replace("\"v1 clicks=\" + _clicks", "\"v2 items=\" + _items.Count");
        var (update, _) = await app.UpdateAsync(v2);

        update.Status.Should().Be(HotReloadStatus.Ready, update.Reason);
        update.KeptFields.Should().Equal("_clicks");
        app.Call("Describe").Should().Be("v2 items=1", "the item added before the change is still there");

        // The next change starts from the source as it was written, without the field.
        var (next, _) = await app.UpdateAsync(v2.Replace("\"v2 items=\"", "\"v3 items=\""));
        next.Status.Should().Be(HotReloadStatus.Ready, next.Reason);
        app.Call("Describe").Should().Be("v3 items=1");
    }

    [Fact]
    public async Task An_event_the_new_code_drops_stays_too()
    {
        using var app = await HotReloadHarness.StartAsync(LoopApp);

        var (update, _) = await app.UpdateAsync(LoopApp
            .Replace("public event EventHandler? Pressed;", "")
            .Replace("Pressed += (s, e) => { _clicks = _clicks + 1; };", ""));

        update.Status.Should().Be(HotReloadStatus.Ready, update.Reason);
        update.KeptFields.Should().Equal("Pressed");
    }

    [Fact]
    public async Task A_dropped_field_whose_type_went_too_still_means_a_full_reload()
    {
        const string withNote = """
            public class Note { public string Text = ""; }

            public class DynamicUserControl
            {
                private Note? _note = new Note();
                public string Ui = "";
                public DynamicUserControl() { BuildUi(); }
                private void BuildUi() { Ui = "note"; }
                public string Describe() => "v1";
            }
            """;
        using var app = await HotReloadHarness.StartAsync(withNote);

        var (update, _) = await app.UpdateAsync(withNote
            .Replace("public class Note { public string Text = \"\"; }", "")
            .Replace("private Note? _note = new Note();", "")
            .Replace("\"v1\"", "\"v2\""));

        update.Status.Should().Be(HotReloadStatus.NotPossible, "the field cannot stay without its type");
        update.Reason.Should().Contain("ENC");
        update.KeptFields.Should().BeEmpty();
    }

    // What a real Fission tear wrote: the bars leave a method with their lambdas, and the totals come back as new properties with lambdas.
    private const string Tracker = """
        using System;
        using System.Collections.Generic;
        using System.Linq;

        public class DynamicUserControl
        {
            private readonly List<(decimal Amount, string Category)> _expenses = new List<(decimal, string)>();
            public string Ui = "";
            public string Bars = "";
            public Action? Clicked;

            public DynamicUserControl() { BuildUi(); }

            private void BuildUi() { Clicked = () => Add(1m, "Click"); Ui = "tracker"; Refresh(); }

            public void Add(decimal amount, string category) { _expenses.Add((amount, category)); Refresh(); }

            private void Refresh()
            {
                Ui = "tracker " + _expenses.Count;
                var groups = _expenses.GroupBy(x => x.Category, StringComparer.OrdinalIgnoreCase)
                    .Select(x => (Name: x.Key, Sum: x.Sum(y => y.Amount))).OrderByDescending(x => x.Sum).ToList();
                var max = groups.Count > 0 ? groups.Max(x => x.Sum) : 1m;
                Bars = string.Join(",", groups.Select(g => g.Name + "=" + g.Sum / max));
            }
        }
        """;

    [Fact]
    public async Task Lambdas_that_leave_a_method_while_new_ones_come_in_new_members_are_patched()
    {
        using var app = await HotReloadHarness.StartAsync(Tracker);
        app.Call("Add", 12m, "Food");
        app.Call("Add", 40m, "Travel");

        var (update, _) = await app.UpdateAsync(Tracker
            .Replace("public class DynamicUserControl", "public sealed record CategoryTotal(string Category, decimal Sum);\n\npublic class DynamicUserControl")
            .Replace("private void BuildUi() { Clicked = () => Add(1m, \"Click\"); Ui = \"tracker\"; Refresh(); }", "private void BuildUi() { Clicked = () => Add(1m, \"Click\"); Refresh(); }")
            .Replace("""
                    var groups = _expenses.GroupBy(x => x.Category, StringComparer.OrdinalIgnoreCase)
                        .Select(x => (Name: x.Key, Sum: x.Sum(y => y.Amount))).OrderByDescending(x => x.Sum).ToList();
                    var max = groups.Count > 0 ? groups.Max(x => x.Sum) : 1m;
                    Bars = string.Join(",", groups.Select(g => g.Name + "=" + g.Sum / max));
            """, "")
            .Replace("private void Refresh()", """
                public decimal TotalAmount => _expenses.Sum(x => x.Amount);

                public List<CategoryTotal> CategoryTotals => _expenses.GroupBy(x => x.Category, StringComparer.OrdinalIgnoreCase)
                    .Select(x => new CategoryTotal(x.Key, x.Sum(y => y.Amount))).OrderByDescending(x => x.Sum).ToList();

                private void Refresh()
            """));

        update.Status.Should().Be(HotReloadStatus.Ready, update.Reason);
        app.Get<decimal>("TotalAmount").Should().Be(52m, "the expenses from before are still there");
        app.Get<string>("Ui").Should().Be("tracker 2", "the new code runs");
        app.Get<Action>("Clicked")();
        app.Get<decimal>("TotalAmount").Should().Be(53m, "the lambda that stayed still works");
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
