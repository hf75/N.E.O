using System.Collections.ObjectModel;
using System.Diagnostics;
using FluentAssertions;
using Neo.Shared;
using Xunit;

namespace Neo.App.Core.Tests;

/// <summary>What of a running app can leave its process, and how it gets into another instance or another app.</summary>
public class AppStateImageTests
{
    public enum Mood { Low, High }

    public record Row(string Name, int Amount);

    public class Note
    {
        public string Title { get; set; } = "";
        public List<Row> Rows { get; set; } = new();
    }

    private class Holder
    {
        public int Number;
        public string Text = "";
        public decimal Money;
        public DateTime When;
        public Mood Feeling;
        public int? Maybe;
        public int[] Array = [];
        public (string, int) Pair;
        public Note Note = new();
        public HashSet<string> Tags = new();
        public Dictionary<string, Row> ByName = new();
        public readonly List<Row> Rows = new();
        public readonly ObservableCollection<Row> Shown = new();
        public int Property { get; set; }
        public static int Shared;
        public static readonly int Fixed = 5;
        public const int Constant = 7;

        // What belongs to the process.
        public Stopwatch Clock = new();
        public CancellationTokenSource Stop = new();
        public object Anything = new();
        public Action Handler = () => { };
    }

    private static Holder Filled()
    {
        var holder = new Holder
        {
            Number = 42,
            Text = "hello",
            Money = 12.50m,
            When = new DateTime(2026, 11, 1, 12, 0, 0),
            Feeling = Mood.High,
            Maybe = 3,
            Array = [1, 2],
            Pair = ("a", 1),
            Note = new Note { Title = "t", Rows = [new Row("r", 1)] },
            Tags = ["x"],
            ByName = new() { ["Anna"] = new Row("Anna", 2) },
            Property = 9,
        };
        holder.Rows.Add(new Row("Ben", 3));
        holder.Shown.Add(new Row("Cara", 4));
        Holder.Shared = 11;
        return holder;
    }

    private static AppStateImage ThroughTheWire(AppStateImage image) => AppStateImage.FromJson(image.ToJson())!;

    [Fact]
    public void Data_of_every_kind_arrives_in_a_new_instance()
    {
        var image = ThroughTheWire(AppStateImage.Capture(Filled()));
        Holder.Shared = 0;

        var target = new Holder();
        var restored = image.RestoreTo(target);

        restored.Failed.Should().BeEmpty();
        target.Number.Should().Be(42);
        target.Text.Should().Be("hello");
        target.Money.Should().Be(12.50m);
        target.When.Should().Be(new DateTime(2026, 11, 1, 12, 0, 0));
        target.Feeling.Should().Be(Mood.High);
        target.Maybe.Should().Be(3);
        target.Array.Should().Equal(1, 2);
        target.Pair.Should().Be(("a", 1));
        target.Note.Title.Should().Be("t");
        target.Note.Rows.Should().ContainSingle().Which.Should().Be(new Row("r", 1));
        target.Tags.Should().Contain("x");
        target.ByName["Anna"].Should().Be(new Row("Anna", 2));
        target.Rows.Should().ContainSingle().Which.Name.Should().Be("Ben");
        target.Shown.Should().ContainSingle().Which.Name.Should().Be("Cara");
        target.Property.Should().Be(9);
        Holder.Shared.Should().Be(11);
    }

    [Fact]
    public void What_belongs_to_the_process_stays_behind_and_is_named()
    {
        var image = AppStateImage.Capture(Filled());

        image.Fields.Keys.Should().NotContain(new[] { "Clock", "Stop", "Anything", "Handler", "Fixed", "Constant" });
        image.Left.Should().Contain(l => l.StartsWith("Clock")).And.Contain(l => l.StartsWith("Stop")).And.Contain(l => l.StartsWith("Anything"));
        image.Left.Should().NotContain(l => l.StartsWith("Handler"), "a delegate is how an app is built, nobody misses it");
    }

    [Fact]
    public void A_list_the_app_holds_stays_the_same_object_because_its_ui_may_be_bound_to_it()
    {
        var target = new Holder();
        var rows = target.Rows;
        var shown = target.Shown;
        var dictionary = target.ByName;

        AppStateImage.Capture(Filled()).RestoreTo(target);

        target.Rows.Should().BeSameAs(rows);
        target.Shown.Should().BeSameAs(shown);
        target.ByName.Should().BeSameAs(dictionary);
        shown.Should().ContainSingle();
    }

    [Fact]
    public void An_empty_list_empties_the_one_that_was_there()
    {
        var target = new Holder();
        target.Rows.Add(new Row("old", 1));

        AppStateImage.Capture(new Holder()).RestoreTo(target);

        target.Rows.Should().BeEmpty();
    }

    // The app that took another one over has its own classes of the same shape, and may have renamed fields.
    private class Merged
    {
        public record Row(string Name, int Amount);

        public int Number;
        public readonly List<Row> currency_Rows = new();
        public string _currency_Text = "";
        public string Text = "this app's own";
    }

    private class Dropped
    {
        public int Number = 1;
        public List<Row> Rows = [new Row("Ben", 3)];
        public string _Text = "dropped";
    }

    [Fact]
    public void Into_another_app_values_go_by_their_name_or_the_prefix_and_classes_by_their_shape()
    {
        var target = new Merged();

        var restored = AppStateImage.Capture(new Dropped()).RestoreTo(target, "currency");

        restored.Failed.Should().BeEmpty();
        target.Number.Should().Be(1);
        target.currency_Rows.Should().ContainSingle().Which.Should().Be(new Merged.Row("Ben", 3));
        target._currency_Text.Should().Be("dropped");
        target.Text.Should().Be("this app's own");
    }

    [Fact]
    public void An_auto_property_takes_the_prefix_too()
    {
        var target = new WithPrefixedProperty();

        var restored = AppStateImage.Capture(new WithProperty { Amount = 4 }).RestoreTo(target, "currency");

        restored.Failed.Should().BeEmpty();
        target.currency_Amount.Should().Be(4);
    }

    private class WithProperty { public int Amount { get; set; } }
    private class WithPrefixedProperty { public int currency_Amount { get; set; } }

    private class OtherShape
    {
        public string Number = "not a number";
        public Stopwatch Rows = new();
    }

    [Fact]
    public void A_value_that_does_not_fit_is_reported_and_the_others_still_arrive()
    {
        var restored = AppStateImage.Capture(new Dropped()).RestoreTo(new OtherShape());

        restored.Values.Should().Be(0);
        restored.Failed.Should().Contain(f => f.StartsWith("Number:"));
        restored.Failed.Should().Contain(f => f.StartsWith("Rows:") && f.Contains("something else"));
        restored.Failed.Should().Contain(f => f.StartsWith("_Text:") && f.Contains("no such field"));
    }

    private class Node { public Node? Next { get; set; } public string Name { get; set; } = ""; }
    private class WithCycle { public Node Start = new(); }

    [Fact]
    public void A_cycle_in_the_data_does_not_stop_it()
    {
        var app = new WithCycle { Start = new Node { Name = "a" } };
        app.Start.Next = app.Start;

        var image = AppStateImage.Capture(app);

        image.Fields.Should().ContainKey("Start");
        var target = new WithCycle();
        image.RestoreTo(target);
        target.Start.Name.Should().Be("a");
    }

    private class Huge { public string Big = new('x', 33 * 1024 * 1024); public int Small = 1; }

    [Fact]
    public void Too_much_data_stays_behind_and_the_rest_goes()
    {
        var image = AppStateImage.Capture(new Huge());

        image.Fields.Should().ContainKey("Small").And.NotContainKey("Big");
        image.Left.Should().Contain(l => l.StartsWith("Big") && l.Contains("too much"));
    }

    [Fact]
    public void It_says_how_much_goes_along()
    {
        AppStateImage.Capture(new Dropped()).Bytes.Should().BeGreaterThan(20);
    }

    // ── The UI state as data ──────────────────────────────────────────────

    [Theory]
    [InlineData("text")]
    [InlineData(5)]
    [InlineData(true)]
    [InlineData(1.5)]
    public void A_ui_value_keeps_its_type(object value)
    {
        var through = System.Text.Json.JsonSerializer.Deserialize<PortableValue>(System.Text.Json.JsonSerializer.Serialize(PortableValue.Of(value)))!;

        through.Value.Should().Be(value);
    }

    [Fact]
    public void Dates_and_money_in_the_ui_keep_their_type()
    {
        PortableValue.Of(1.5m).Value.Should().Be(1.5m);
        PortableValue.Of(new DateTime(2026, 11, 1)).Value.Should().Be(new DateTime(2026, 11, 1));
        PortableValue.Of(TimeSpan.FromMinutes(3)).Value.Should().Be(TimeSpan.FromMinutes(3));
    }

    [Fact]
    public void The_apps_own_objects_in_the_ui_stay_behind()
    {
        PortableValue.Of(new Row("selected", 1)).Value.Should().BeNull("a selection then goes by its position");
        PortableValue.Of(null).Value.Should().BeNull();
    }
}
