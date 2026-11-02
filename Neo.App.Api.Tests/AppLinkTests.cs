using System.Globalization;
using FluentAssertions;
using Neo.App;
using Xunit;

namespace Avalonia.Media
{
    // What the toolkit holds: a brush is of no use to another program as an object graph.
    internal sealed class StandInBrush
    {
        public object Transform => this;
        public override string ToString() => "#ff336699";
    }
}

namespace Avalonia.Controls
{
    // The items of a list: text, or controls that show one.
    internal sealed class StandInItems : List<object?>;

    internal sealed class StandInText
    {
        public string? Text { get; init; }
        public object Parent => this;
    }

    internal sealed class StandInItem
    {
        public object? Content { get; init; }
    }
}

namespace Neo.App.Api.Tests
{
    /// <summary>Values on their way between two linked programs, and the order things happen in. With real windows: AppLinkSmokeTests.</summary>
    public class AppLinkTests
    {
        [Theory]
        [InlineData(3.5, "3.5")]
        [InlineData(42, "42")]
        [InlineData("text", "\"text\"")]
        [InlineData(true, "true")]
        [InlineData(null, "null")]
        public void Plain_values_go_as_they_are(object? value, string json)
        {
            LinkValue.ToJson(value).Should().Be(json);
        }

        [Fact]
        public void What_belongs_to_the_toolkit_goes_as_what_it_calls_itself()
        {
            LinkValue.ToJson(new Avalonia.Media.StandInBrush()).Should().Be("\"#ff336699\"");
        }

        [Fact]
        public void A_list_of_the_toolkit_goes_as_its_items_and_a_control_as_what_it_shows()
        {
            var items = new Avalonia.Controls.StandInItems
            {
                "plain",
                new Avalonia.Controls.StandInText { Text = "shown" },
                new Avalonia.Controls.StandInItem { Content = "in an item" },
                new Avalonia.Controls.StandInItem { Content = new Avalonia.Controls.StandInText { Text = "deep" } },
                new Avalonia.Media.StandInBrush(),
            };

            LinkValue.Convert<string[]>(LinkValue.ToJson(items)).Should().Equal("plain", "shown", "in an item", "deep", "#ff336699");
        }

        [Fact]
        public void Lists_stay_lists()
        {
            LinkValue.Convert<double[]>(LinkValue.ToJson(new List<double> { 1.5, 2 })).Should().Equal(1.5, 2);
            LinkValue.Convert<List<string>>(LinkValue.ToJson(new[] { "a", "b" })).Should().Equal("a", "b");
        }

        [Theory]
        [InlineData("\"42\"", 42.0)]
        [InlineData("\"1.5\"", 1.5)]
        [InlineData("\"1,234,567\"", 1234567.0)]
        [InlineData("7", 7.0)]
        public void What_one_program_shows_as_text_another_can_take_as_a_number(string json, double number)
        {
            LinkValue.Convert<double>(json).Should().Be(number);
        }

        [Fact]
        public void A_number_with_the_comma_of_this_PC_is_read_too_and_a_number_with_a_point_is_not_read_as_a_thousand()
        {
            var culture = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
                LinkValue.Convert<double>("\"1,5\"").Should().Be(1.5);
                LinkValue.Convert<double>("\"1.5\"").Should().Be(1.5);
                LinkValue.Convert<string>("1.5").Should().Be("1.5", "as JSON writes it, whatever this PC would do");
            }
            finally
            {
                CultureInfo.CurrentCulture = culture;
            }
        }

        [Fact]
        public void Numbers_and_truth_become_text_and_a_whole_number_is_rounded()
        {
            LinkValue.Convert<string>("12").Should().Be("12");
            LinkValue.Convert<string>("true").Should().Be("True");
            LinkValue.Convert<int>("2.6").Should().Be(3);
            LinkValue.Convert<decimal?>("2.5").Should().Be(2.5m);
            LinkValue.Convert<bool>("\"true\"").Should().BeTrue();
            LinkValue.Convert<DayOfWeek>("\"monday\"").Should().Be(DayOfWeek.Monday);
        }

        [Fact]
        public void Numbers_beyond_what_a_decimal_holds_still_fit_a_double()
        {
            LinkValue.Convert<double>("1E+300").Should().Be(1e300);
        }

        [Fact]
        public void Null_fits_what_can_be_null_and_nothing_else()
        {
            LinkValue.Convert<string?>("null").Should().BeNull();
            LinkValue.Convert<double?>("null").Should().BeNull();
            FluentActions.Invoking(() => LinkValue.Convert<double>("null")).Should().Throw<FormatException>();
        }

        [Theory]
        [InlineData("\"not a number\"")]
        [InlineData("true")]
        [InlineData("[1,2]")]
        public void What_cannot_be_a_number_says_so(string json)
        {
            FluentActions.Invoking(() => LinkValue.Convert<double>(json)).Should().Throw<FormatException>().WithMessage("*cannot be a Double*");
        }

        // A slider moved many times while the other side is busy.
        private static (LinkOutbox Outbox, List<string> Done, TaskCompletionSource Gate) BlockedOutbox()
        {
            var done = new List<string>();
            var gate = new TaskCompletionSource();
            var outbox = new LinkOutbox(_ => done.Add("failed"));
            var started = new ManualResetEventSlim();
            outbox.Post(null, async () => { started.Set(); await gate.Task; done.Add("first"); });
            started.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue("the first thing is under way before the rest is asked for");
            return (outbox, done, gate);
        }

        private static Func<Task> Note(List<string> done, string what) => () => { done.Add(what); return Task.CompletedTask; };

        [Fact]
        public async Task A_newer_value_takes_the_place_of_one_still_waiting()
        {
            var (outbox, done, gate) = BlockedOutbox();
            outbox.Post("slider", Note(done, "1"));
            outbox.Post("slider", Note(done, "2"));
            outbox.Post("slider", Note(done, "3"));
            gate.SetResult();
            await outbox.FlushAsync();

            done.Should().Equal("first", "3");
        }

        [Fact]
        public async Task Different_things_keep_their_order_and_their_values()
        {
            var (outbox, done, gate) = BlockedOutbox();
            outbox.Post("slider", Note(done, "slider 1"));
            outbox.Post("box", Note(done, "box 1"));
            outbox.Post("slider", Note(done, "slider 2"));
            gate.SetResult();
            await outbox.FlushAsync();

            done.Should().Equal("first", "slider 2", "box 1");
        }

        [Fact]
        public async Task A_value_set_and_a_button_pressed_after_it_arrive_in_that_order_every_time()
        {
            var (outbox, done, gate) = BlockedOutbox();
            outbox.Post("amount", Note(done, "amount 1"));
            outbox.Post(null, Note(done, "store"));
            outbox.Post("amount", Note(done, "amount 2"));
            outbox.Post(null, Note(done, "store"));
            outbox.Post("amount", Note(done, "amount 3"));
            outbox.Post("amount", Note(done, "amount 4"));
            gate.SetResult();
            await outbox.FlushAsync();

            done.Should().Equal("first", "amount 1", "store", "amount 2", "store", "amount 4");
        }

        [Fact]
        public async Task What_fails_is_reported_and_the_next_thing_still_happens()
        {
            var failures = new List<string>();
            var done = new List<string>();
            var outbox = new LinkOutbox(ex => failures.Add(ex.Message));
            outbox.Post(null, () => throw new InvalidOperationException("no such control"));
            outbox.Post(null, Note(done, "next"));
            await outbox.FlushAsync();

            failures.Should().Equal("no such control");
            done.Should().Equal("next");
        }

        [Fact]
        public async Task After_the_end_nothing_waiting_happens_and_nothing_new_is_taken()
        {
            var (outbox, done, gate) = BlockedOutbox();
            outbox.Post(null, Note(done, "waiting"));
            outbox.Close();
            outbox.Post(null, Note(done, "new"));
            gate.SetResult();
            await outbox.FlushAsync();
            await Task.Delay(50);

            done.Should().Equal("first");
        }
    }
}
