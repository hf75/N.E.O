using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace Neo.Stage;

/// <summary>
/// N.E.O. builds apps for the stage. Each app gets a hidden N.E.O. of its own (Neo.App.Avalonia --stage &lt;pipe&gt;),
/// started from a copy of N.E.O. beside the stage's own (Neo/Stage/neo) and asked through a named pipe of the same user.
/// Only the app window is seen; Ctrl+Shift+N shows N.E.O. beside it. A change goes to the N.E.O. of that app, which
/// patches it while it runs. The apps stay when the stage ends; closing an app window ends its N.E.O.
/// </summary>
internal sealed class NeoApps
{
    /// <summary>The N.E.O. to start, when not the copy beside the stage.</summary>
    public const string HostVariable = "NEO_STAGE_NEO";
    private static readonly UTF8Encoding s_utf8 = new(encoderShouldEmitUTF8Identifier: false);
    private readonly List<NeoApp> _apps = [];

    private sealed class NeoApp(string name, Process host, NamedPipeClientStream pipe)
    {
        private readonly SemaphoreSlim _one = new(1, 1);
        private readonly StreamReader _reader = new(pipe, s_utf8);
        private readonly StreamWriter _writer = new(pipe, s_utf8) { AutoFlush = true };

        public string Name { get; } = name;
        public Process Host { get; } = host;
        public bool Alive => !Host.HasExited && pipe.IsConnected;

        public async Task<Reply> AskAsync(string prompt, CancellationToken cancellationToken)
        {
            await _one.WaitAsync(cancellationToken);
            try
            {
                await _writer.WriteLineAsync(JsonSerializer.Serialize(new { Prompt = prompt }).AsMemory(), cancellationToken);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromMinutes(4));
                var line = await _reader.ReadLineAsync(timeout.Token) ?? throw new IOException("N.E.O. hung up.");
                return JsonSerializer.Deserialize<Reply>(line) ?? new Reply(false, false, "");
            }
            finally
            {
                _one.Release();
            }
        }
    }

    /// <summary>What N.E.O. answers: whether the prompt ran, whether its app is loaded, and what it wrote meanwhile.</summary>
    internal sealed record Reply(bool Done, bool AppLoaded, string Chat);

    /// <summary>The apps that are still open, the newest last.</summary>
    public IReadOnlyList<string> Names
    {
        get
        {
            lock (_apps)
            {
                _apps.RemoveAll(app => !app.Alive);
                return [.. _apps.Select(app => app.Name)];
            }
        }
    }

    /// <summary>The window of an app (by name, or the newest): the window of the program its N.E.O. started, titled as the app.</summary>
    public IntPtr? Window(string? name)
    {
        NeoApp? app;
        lock (_apps)
        {
            _apps.RemoveAll(candidate => !candidate.Alive);
            app = (name is { Length: > 0 } wanted ? _apps.LastOrDefault(candidate => candidate.Name.Contains(wanted, StringComparison.OrdinalIgnoreCase)) : null)
                ?? _apps.LastOrDefault();
        }
        return app == null ? null : OpenWindows.List(most: 64).FirstOrDefault(window => window.Title == app.Name)?.Handle;
    }

    /// <summary>Has a new app built from <paramref name="description"/>; its window is called <paramref name="name"/>.</summary>
    public async Task<string> BuildAsync(string name, string description, CancellationToken cancellationToken)
    {
        if (Host() is not { } host)
            return "N.E.O. ist auf diesem Rechner nicht zu finden; ohne N.E.O. baue ich keine Apps.";
        var pipeName = "neo-stage-" + Guid.NewGuid().ToString("N")[..12];
        var start = new ProcessStartInfo(host)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(host)!,
        };
        foreach (var argument in (string[])["--stage", pipeName, "--title", name])
            start.ArgumentList.Add(argument);
        // Not the stage's own variables for the apps' Claude: N.E.O. reads its keys from its settings.
        start.Environment.Remove("NEO_STAGE_LOG");

        var clock = Stopwatch.StartNew();
        var process = Process.Start(start) ?? throw new InvalidOperationException("N.E.O. did not start.");
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        // N.E.O. needs a moment: its packages, the empty app window. Then it listens.
        while (!pipe.IsConnected)
        {
            if (process.HasExited)
                return $"N.E.O. hat sich beim Start beendet (Code {process.ExitCode}).";
            if (clock.Elapsed > TimeSpan.FromSeconds(90))
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                return "N.E.O. ist nicht rechtzeitig bereit geworden.";
            }
            try { await pipe.ConnectAsync(500, cancellationToken); }
            catch (TimeoutException) { }
        }
        App.Log($"neo: N.E.O. for '{name}' ready after {clock.Elapsed.TotalSeconds:0.0} s (process {process.Id})");

        var app = new NeoApp(name, process, pipe);
        lock (_apps)
            _apps.Add(app);
        return Said(app, await app.AskAsync(description, cancellationToken), clock, built: true);
    }

    /// <summary>Has an app changed while it runs: the one called <paramref name="name"/>, or the newest.</summary>
    public async Task<string> ChangeAsync(string? name, string change, CancellationToken cancellationToken)
    {
        NeoApp? app;
        lock (_apps)
        {
            _apps.RemoveAll(candidate => !candidate.Alive);
            app = (name is { Length: > 0 } wanted ? _apps.LastOrDefault(candidate => candidate.Name.Contains(wanted, StringComparison.OrdinalIgnoreCase)) : null)
                ?? _apps.LastOrDefault();
        }
        if (app == null)
            return "Es ist keine App von N.E.O. offen, die ich ändern könnte.";
        var clock = Stopwatch.StartNew();
        return Said(app, await app.AskAsync(change, cancellationToken), clock, built: false);
    }

    private static string Said(NeoApp app, Reply reply, Stopwatch clock, bool built)
    {
        App.Log($"neo: '{app.Name}' {(built ? "built" : "changed")} after {clock.Elapsed.TotalSeconds:0.0} s, done={reply.Done}, loaded={reply.AppLoaded}");
        var chat = reply.Chat.Trim().Length > 0 ? $" N.E.O. schrieb dazu: {reply.Chat.Trim()}" : "";
        if (!reply.Done)
            return $"N.E.O. war mit „{app.Name}“ noch beschäftigt und hat nichts getan.{chat}";
        if (!reply.AppLoaded)
            return $"N.E.O. hat „{app.Name}“ nicht zum Laufen gebracht.{chat}";
        return built
            ? $"„{app.Name}“ ist fertig und offen, nach {clock.Elapsed.TotalSeconds:0} s.{chat}"
            : $"„{app.Name}“ ist geändert, nach {clock.Elapsed.TotalSeconds:0} s.{chat}";
    }

    // The N.E.O. to start: the one the variable names, else the copy beside the stage.
    private static string? Host()
    {
        var named = Environment.GetEnvironmentVariable(HostVariable) is { Length: > 0 } own ? own
            : OperatingSystem.IsWindows() ? Environment.GetEnvironmentVariable(HostVariable, EnvironmentVariableTarget.User) : null;
        var beside = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Neo", "Stage", "neo", "Neo.App.Avalonia.exe");
        return named is { } path && File.Exists(path) ? path : File.Exists(beside) ? beside : null;
    }
}
