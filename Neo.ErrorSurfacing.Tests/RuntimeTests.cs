using System.Reflection;
using FluentAssertions;
using Xunit;

namespace Neo.ErrorSurfacing.Tests;

/// <summary>
/// Instrumented app code running: every exception that used to vanish arrives on the "UI thread"
/// (a <see cref="RecordingContext"/>) as itself, cancellation stays quiet, "-=" still removes what
/// "+=" added, and without a UI thread nothing changes.
/// </summary>
public class RuntimeTests : IClassFixture<RuntimeTests.App>
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(400);
    private readonly App _app;

    public RuntimeTests(App app) => _app = app;

    /// <summary>One compiled app with an entry per case; compiling once keeps the tests quick.</summary>
    public sealed class App
    {
        public const string Source = """
            using System;
            using System.Threading;
            using System.Threading.Tasks;

            public class Holder { public Task LoadAsync() => Cases.Fail("behind ?."); }

            public static class Cases
            {
                public static int Elapsed;

                public static async Task Fail(string what) { await Task.Delay(10); throw new InvalidOperationException(what); }
                public static async ValueTask FailValue(string what) { await Task.Delay(10); throw new InvalidOperationException(what); }

                public static void TaskRun() => Task.Run(new Action(() => throw new InvalidOperationException("Task.Run")));
                public static void AsyncMethod() { Fail("async method"); }
                public static void Discard() { _ = Fail("discard"); }
                public static void ContinueWith() { Task.Delay(10).ContinueWith(_ => throw new InvalidOperationException("ContinueWith")); }
                public static void VoidLambda() { Action action = () => Fail("void lambda"); action(); }
                public static void ConditionalAccess() { Holder? holder = new Holder(); holder?.LoadAsync(); }
                public static void ValueTask() { FailValue("ValueTask"); }
                public static void OtherAwaitable() { Task.FromException(new InvalidOperationException("ConfigureAwait")).ConfigureAwait(false); }
                public static void Canceled() { Task.FromCanceled(new CancellationToken(true)); Task.FromException(new OperationCanceledException()); }
                public static void Succeeds() { Task.Delay(10); }

                public static void ElapsedThrows()
                {
                    var timer = new System.Timers.Timer(10) { AutoReset = false };
                    timer.Elapsed += (s, e) => throw new InvalidOperationException("System.Timers.Timer");
                    timer.Start();
                }

                public static void Unsubscribed()
                {
                    var timer = new System.Timers.Timer(10) { AutoReset = false };
                    System.Timers.ElapsedEventHandler handler = (s, e) => Interlocked.Increment(ref Elapsed);
                    timer.Elapsed += handler;
                    timer.Elapsed -= handler;
                    timer.Elapsed += OnElapsed;
                    timer.Elapsed -= OnElapsed;
                    timer.Start();
                }

                private static void OnElapsed(object? sender, System.Timers.ElapsedEventArgs e) => Interlocked.Increment(ref Elapsed);
            }
            """;

        public Assembly Assembly { get; } = TestCompiler.Load(Source);

        internal void Run(string name, RecordingContext context)
            => context.Run(() => Assembly.GetType("Cases")!.GetMethod(name)!.Invoke(null, null));
    }

    [Theory]
    [InlineData("TaskRun", "Task.Run")]
    [InlineData("AsyncMethod", "async method")]
    [InlineData("Discard", "discard")]
    [InlineData("ContinueWith", "ContinueWith")]
    [InlineData("VoidLambda", "void lambda")]
    [InlineData("ConditionalAccess", "behind ?.")]
    [InlineData("ValueTask", "ValueTask")]
    [InlineData("OtherAwaitable", "ConfigureAwait")]
    [InlineData("ElapsedThrows", "System.Timers.Timer")]
    public void A_lost_exception_is_thrown_on_the_UI_thread_as_itself(string entry, string message)
    {
        var context = new RecordingContext();

        _app.Run(entry, context);

        var thrown = context.NextThrown(Wait);
        thrown.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Be(message);
        thrown!.StackTrace.Should().Contain("Cases", "the stack trace still names the app's code that failed");
    }

    [Theory]
    [InlineData("Canceled")]
    [InlineData("Succeeds")]
    public void Cancellation_and_success_stay_quiet(string entry)
    {
        var context = new RecordingContext();

        _app.Run(entry, context);

        context.NextThrown(Quiet).Should().BeNull();
    }

    [Fact]
    public void Minus_equals_removes_what_plus_equals_added()
    {
        var context = new RecordingContext();

        _app.Run("Unsubscribed", context);
        Thread.Sleep(Quiet);

        ((int)_app.Assembly.GetType("Cases")!.GetField("Elapsed")!.GetValue(null)!).Should().Be(0);
        context.NextThrown(TimeSpan.Zero).Should().BeNull();
    }

    [Fact]
    public void A_wrapper_is_a_delegate_over_the_handler_and_the_same_one_every_time()
    {
        // EventSubscriptionSnapshot (hot reload) relies on the target: a delegate over a delegate stands for it.
        var guard = _app.Assembly.GetType("NeoRuntime.LostExceptions")!.GetMethod("Guard")!.MakeGenericMethod(typeof(EventHandler));
        var owner = new object();
        EventHandler handler = (s, e) => owner.GetHashCode();

        var first = (EventHandler)guard.Invoke(null, [handler])!;
        var second = (EventHandler)guard.Invoke(null, [new EventHandler(handler.Invoke)])!;
        var again = (EventHandler)guard.Invoke(null, [handler])!;

        first.Target.Should().BeSameAs(handler);
        again.Should().BeSameAs(first);
        second.Should().NotBeSameAs(first, "a different delegate is a different handler");
    }

    [Fact]
    public void Every_delegate_the_losing_APIs_take_is_one_the_runtime_wraps()
    {
        var guard = _app.Assembly.GetType("NeoRuntime.LostExceptions")!.GetMethod("Guard")!;
        var delegateTypes = new HashSet<Type>();
        foreach (var (typeName, eventName) in SurfacingRewriter.LosingEvents)
            delegateTypes.Add(FindType(typeName).GetEvent(eventName)!.EventHandlerType!);
        foreach (var typeName in SurfacingRewriter.LosingCallbackTypes)
        {
            var type = FindType(typeName);
            var members = type.GetConstructors().Cast<MethodBase>().Concat(type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance));
            foreach (var parameter in members.SelectMany(m => m.GetParameters()))
                if (typeof(Delegate).IsAssignableFrom(parameter.ParameterType)) delegateTypes.Add(parameter.ParameterType);
        }

        delegateTypes.Should().NotBeEmpty();
        foreach (var delegateType in delegateTypes)
        {
            var handler = Delegate.CreateDelegate(delegateType, typeof(Handlers).GetMethod(HandlerFor(delegateType))!);
            var wrapped = (Delegate)guard.MakeGenericMethod(delegateType).Invoke(null, [handler])!;
            wrapped.Should().NotBeSameAs(handler, $"{delegateType} has to be wrapped");
        }
    }

    [Fact]
    public void Without_a_UI_thread_nothing_changes()
    {
        // A fresh app: the runtime has never seen a UI thread.
        var app = TestCompiler.Load(App.Source);
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { app.GetType("Cases")!.GetMethod("TaskRun")!.Invoke(null, null); }
            catch (Exception exception) { failure = exception; }
        });

        thread.Start();
        thread.Join();
        Thread.Sleep(Quiet);

        failure.Should().BeNull("the task is left as it was, and the process lives on");
    }

    private static Type FindType(string fullName)
        => AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(fullName)).FirstOrDefault(t => t != null)
           ?? Type.GetType(fullName + ", Avalonia.Base")
           ?? Type.GetType(fullName + ", System.ComponentModel.TypeConverter")
           ?? throw new InvalidOperationException($"{fullName} not found.");

    private static string HandlerFor(Type delegateType) => delegateType.GetMethod("Invoke")!.ReturnType == typeof(bool)
        ? nameof(Handlers.ReturnsTrue)
        : delegateType.GetMethod("Invoke")!.GetParameters().Length switch
        {
            0 => nameof(Handlers.Nothing),
            _ when delegateType == typeof(System.Timers.ElapsedEventHandler) => nameof(Handlers.Elapsed),
            _ => nameof(Handlers.Event),
        };

    private static class Handlers
    {
        public static bool ReturnsTrue() => true;
        public static void Nothing() { }
        public static void Event(object? sender, EventArgs e) { }
        public static void Elapsed(object? sender, System.Timers.ElapsedEventArgs e) { }
    }
}
