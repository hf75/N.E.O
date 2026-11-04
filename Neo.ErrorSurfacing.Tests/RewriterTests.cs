using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Neo.ErrorSurfacing.Tests;

/// <summary>
/// What the rewriting does to app code, line by line: which places it wraps, which it leaves
/// alone, that everything stays on its line and that the result still compiles.
/// </summary>
public class RewriterTests
{
    private const string Forget = "global::NeoRuntime.LostExceptions.Forget";
    private const string Guard = "global::NeoRuntime.LostExceptions.Guard";

    private const string DroppedTasks = """
        using System;
        using System.Threading;
        using System.Threading.Tasks;

        public class App
        {
            private Task _kept = Task.CompletedTask;

            public async Task LoadAsync() { await Task.Delay(1); }
            public ValueTask LoadValueAsync() => default;
            public static App? Maybe() => new App();

            public async Task Run()
            {
                LoadAsync();
                _ = LoadAsync();
                Task.Run(() => { });
                Task.Delay(1).ContinueWith(_ => { });
                LoadValueAsync();
                Maybe()?.LoadAsync();
                Maybe()?.LoadValueAsync();
                Task.Delay(1).ConfigureAwait(false);
                Action action = () => LoadAsync();
                Func<Task> kept = () => LoadAsync();
                var stored = LoadAsync();
                _kept = LoadAsync();
                await LoadAsync();
                Refresh();
            }

            private void Refresh() => LoadAsync();
            private Task Next() => LoadAsync();
        }
        """;

    [Fact]
    public void Dropped_tasks_are_handed_to_the_runtime_and_kept_ones_are_not()
    {
        var lines = Rewrite(DroppedTasks);

        lines.Should().Contain($"        {Forget}(LoadAsync());")
            .And.Contain($"        {Forget}(Task.Run(() => {{ }}));")
            .And.Contain($"        {Forget}(Task.Delay(1).ContinueWith(_ => {{ }}));")
            .And.Contain($"        {Forget}(LoadValueAsync());")
            .And.Contain($"        {Forget}(Maybe()?.LoadAsync());")
            .And.Contain($"        {Forget}(Maybe()?.LoadValueAsync().GetAwaiter(), static __neoAwaiter => __neoAwaiter.GetResult());")
            .And.Contain($"        {Forget}(Task.Delay(1).ConfigureAwait(false).GetAwaiter(), static __neoAwaiter => __neoAwaiter.GetResult());")
            .And.Contain($"        Action action = () => {Forget}(LoadAsync());")
            .And.Contain($"    private void Refresh() => {Forget}(LoadAsync());");

        lines.Count(l => l == $"        {Forget}(LoadAsync());").Should().Be(2, "\"_ = LoadAsync();\" drops the task as well");
        lines.Should().Contain("        Func<Task> kept = () => LoadAsync();")
            .And.Contain("        var stored = LoadAsync();")
            .And.Contain("        _kept = LoadAsync();")
            .And.Contain("        await LoadAsync();")
            .And.Contain("        Refresh();")
            .And.Contain("    private Task Next() => LoadAsync();");
    }

    private const string Timers = """
        using System;
        using Avalonia.Threading;

        public class App
        {
            private readonly DispatcherTimer _timer = new DispatcherTimer();
            private readonly System.Timers.Timer _elapsed = new System.Timers.Timer(100);

            public void Run()
            {
                _timer.Tick += OnTick;
                _timer.Tick -= OnTick;
                _timer.Tick += (s, e) => { };
                _elapsed.Elapsed += (s, e) => { };
                DispatcherTimer.RunOnce(() => { }, TimeSpan.FromSeconds(1));
                DispatcherTimer.Run(() => true, TimeSpan.FromSeconds(1));
                var callback = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Normal, OnTick);
                DispatcherTimer typed = new(TimeSpan.FromSeconds(1), DispatcherPriority.Normal, OnTick);
                Dispatcher.UIThread.InvokeAsync(() => { });
                Dispatcher.UIThread.Post(() => { });
                _timer.Start();
            }

            private void OnTick(object? sender, EventArgs e) { }
        }
        """;

    [Fact]
    public void Timer_handlers_are_guarded_and_InvokeAsync_is_not_dropped()
    {
        var lines = Rewrite(Timers);

        lines.Should().Contain($"        _timer.Tick += {Guard}<global::System.EventHandler>(OnTick);")
            .And.Contain($"        _timer.Tick -= {Guard}<global::System.EventHandler>(OnTick);")
            .And.Contain($"        _timer.Tick += {Guard}<global::System.EventHandler>((s, e) => {{ }});")
            .And.Contain($"        _elapsed.Elapsed += {Guard}<global::System.Timers.ElapsedEventHandler>((s, e) => {{ }});")
            .And.Contain($"        DispatcherTimer.RunOnce({Guard}<global::System.Action>(() => {{ }}), TimeSpan.FromSeconds(1));")
            .And.Contain($"        DispatcherTimer.Run({Guard}<global::System.Func<bool>>(() => true), TimeSpan.FromSeconds(1));")
            .And.Contain($"        var callback = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Normal, {Guard}<global::System.EventHandler>(OnTick));")
            .And.Contain($"        DispatcherTimer typed = new(TimeSpan.FromSeconds(1), DispatcherPriority.Normal, {Guard}<global::System.EventHandler>(OnTick));")
            .And.Contain($"        {Forget}(Dispatcher.UIThread.InvokeAsync(() => {{ }}).GetAwaiter(), static __neoAwaiter => __neoAwaiter.GetResult());");

        lines.Should().Contain("        Dispatcher.UIThread.Post(() => { });", "Post throws on the UI thread already")
            .And.Contain("        _timer.Start();");
    }

    [Fact]
    public void A_WPF_DispatcherTimer_is_guarded_as_well()
    {
        // WPF's timer lets the exception out, but stops for good: guarded, it keeps ticking and the repair runs the new code.
        if (TestCompiler.WindowsDesktopDirectory == null) return;   // WPF is there on Windows only

        var lines = Rewrite(TestCompiler.WpfCompilation("""
            using System;
            using System.Windows.Threading;

            public class App
            {
                private readonly DispatcherTimer _timer = new DispatcherTimer();

                public void Run()
                {
                    _timer.Tick += OnTick;
                    _timer.Tick -= OnTick;
                    var callback = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Normal, OnTick, Dispatcher.CurrentDispatcher);
                }

                private void OnTick(object? sender, EventArgs e) { }
            }
            """));

        lines.Should().Contain($"        _timer.Tick += {Guard}<global::System.EventHandler>(OnTick);")
            .And.Contain($"        _timer.Tick -= {Guard}<global::System.EventHandler>(OnTick);")
            .And.Contain($"        var callback = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Normal, {Guard}<global::System.EventHandler>(OnTick), Dispatcher.CurrentDispatcher);");
    }

    [Fact]
    public void Nested_places_are_all_rewritten()
    {
        var lines = Rewrite("""
            using System;
            using System.Threading.Tasks;
            using Avalonia.Threading;

            public class App
            {
                public async Task LoadAsync() { await Task.Delay(1); }

                public void Run(DispatcherTimer timer)
                {
                    timer.Tick += (s, e) => LoadAsync();
                }
            }
            """);

        lines.Should().Contain($"        timer.Tick += {Guard}<global::System.EventHandler>((s, e) => {Forget}(LoadAsync()));");
    }

    [Fact]
    public void Code_that_needs_nothing_comes_back_as_null_and_the_runtime_is_left_alone()
    {
        var compilation = TestCompiler.Compilation("public class App { public void Run() { System.Console.WriteLine(1); } }");
        LostExceptions.Rewrite(compilation.GetSemanticModel(compilation.SyntaxTrees[0])).Should().BeNull();

        var instrumented = LostExceptions.Instrument(compilation);
        instrumented.SyntaxTrees.Should().HaveCount(2);
        instrumented.SyntaxTrees.Single(LostExceptions.IsRuntime).ToString().Should().Be(LostExceptions.RuntimeSource);
        LostExceptions.Instrument(instrumented).SyntaxTrees.Should().HaveCount(2, "the runtime class is added once");
    }

    [Theory]
    [InlineData(DroppedTasks)]
    [InlineData(Timers)]
    public void Every_line_stays_where_it_was(string source)
    {
        var compilation = TestCompiler.Compilation(source);
        var rewritten = LostExceptions.Rewrite(compilation.GetSemanticModel(compilation.SyntaxTrees[0]))!;

        rewritten.Split('\n').Should().HaveSameCount(source.Split('\n'));
        StatementLines(rewritten).Should().Equal(StatementLines(source));
    }

    [Theory]
    [InlineData(DroppedTasks)]
    [InlineData(Timers)]
    public void Rewritten_code_compiles_without_a_word_from_the_compiler_about_the_runtime(string source)
    {
        var instrumented = LostExceptions.Instrument(TestCompiler.Compilation(source));

        var diagnostics = instrumented.GetDiagnostics();
        diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Should().BeEmpty();
        diagnostics.Where(d => d.Location.SourceTree is { } tree && LostExceptions.IsRuntime(tree)).Should().BeEmpty();
    }

    [Fact]
    public void Rewriting_is_deterministic()
    {
        var first = TestCompiler.Compilation(DroppedTasks, Timers);
        var second = TestCompiler.Compilation(DroppedTasks, Timers);

        LostExceptions.Instrument(first).SyntaxTrees.Select(t => t.ToString())
            .Should().Equal(LostExceptions.Instrument(second).SyntaxTrees.Select(t => t.ToString()));
    }

    [Fact]
    public void Rewritten_trees_keep_path_encoding_and_checksum_algorithm()
    {
        // As the compile agent parses in hot reload mode: the PDB's checksums have to match the workspace's.
        var text = Microsoft.CodeAnalysis.Text.SourceText.From(DroppedTasks, new System.Text.UTF8Encoding(false), Microsoft.CodeAnalysis.Text.SourceHashAlgorithm.Sha256);
        var compilation = TestCompiler.Compilation().AddSyntaxTrees(CSharpSyntaxTree.ParseText(text, path: @"C:\Temp\Source0.cs"));

        var instrumented = LostExceptions.Instrument(compilation, runtimePath: @"C:\Temp\NeoRuntime.LostExceptions.cs");

        var app = instrumented.SyntaxTrees.Single(t => !LostExceptions.IsRuntime(t));
        app.FilePath.Should().Be(@"C:\Temp\Source0.cs");
        app.GetText().ChecksumAlgorithm.Should().Be(Microsoft.CodeAnalysis.Text.SourceHashAlgorithm.Sha256);
        app.GetText().Encoding!.WebName.Should().Be("utf-8");
        var runtime = instrumented.SyntaxTrees.Single(LostExceptions.IsRuntime);
        runtime.FilePath.Should().Be(@"C:\Temp\NeoRuntime.LostExceptions.cs");
        runtime.GetText().ChecksumAlgorithm.Should().Be(Microsoft.CodeAnalysis.Text.SourceHashAlgorithm.Sha256);
    }

    private static string[] Rewrite(string source) => Rewrite(TestCompiler.Compilation(source));

    private static string[] Rewrite(CSharpCompilation compilation)
    {
        compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).Should().BeEmpty("the test's own code has to compile");
        var rewritten = LostExceptions.Rewrite(compilation.GetSemanticModel(compilation.SyntaxTrees[0]));
        rewritten.Should().NotBeNull();
        return rewritten!.Replace("\r\n", "\n").Split('\n');
    }

    private static List<int> StatementLines(string source)
        => CSharpSyntaxTree.ParseText(source).GetRoot().DescendantNodes().OfType<StatementSyntax>()
            .Select(s => s.GetLocation().GetLineSpan().StartLinePosition.Line).ToList();
}
