using System.Text;
using System.Text.Json;

namespace Neo.Agents
{
    public enum ClaudeCodeJobState { Running, Succeeded, Failed, Cancelled }

    /// <summary>A task for Claude Code, and the limits it works within.</summary>
    /// <param name="Prompt">The complete task. Claude Code knows nothing else and cannot ask back.</param>
    public sealed record ClaudeCodeRequest(string Prompt)
    {
        /// <summary>A short name for lists and notices; the start of the task when left out.</summary>
        public string? Title { get; init; }

        /// <summary>Where Claude Code works. Left out, it gets a new folder of its own inside the job's folder.</summary>
        public string? WorkingDirectory { get; init; }

        /// <summary>Continues that earlier conversation. Give the folder it worked in, too.</summary>
        public string? ResumeSessionId { get; init; }

        /// <summary>A JSON schema the end result has to fill in (an object at the top).</summary>
        public string? JsonSchema { get; init; }

        /// <summary>opus, sonnet, haiku or a full model name; left out or "default", Claude Code's own setting.</summary>
        public string? Model { get; init; }

        public string? Effort { get; init; }

        /// <summary>Claude Code stops when the task has cost this much.</summary>
        public double MaxBudgetUsd { get; init; } = ClaudeCodeJob.DefaultBudgetUsd;

        /// <summary>Whoever watches the job stops it after this long.</summary>
        public double MaxMinutes { get; init; } = ClaudeCodeJob.DefaultMaxMinutes;

        /// <summary>
        /// acceptEdits: it changes files in its folder without asking. Anything else needs a rule in
        /// <see cref="AllowedTools"/>, as nobody is there to allow it.
        /// </summary>
        public string PermissionMode { get; init; } = "acceptEdits";

        /// <summary>Claude Code's permission rules for what it may do besides reading and writing its files.</summary>
        public IReadOnlyList<string> AllowedTools { get; init; } = ClaudeCodeJob.DefaultAllowedTools;
    }

    /// <summary>What is written about a job when it starts (job.json in its folder).</summary>
    internal sealed record JobFile
    {
        public required string Id { get; init; }
        public required string Title { get; init; }
        public required string WorkingDirectory { get; init; }
        public required string SessionId { get; init; }
        public string? ResumedSessionId { get; init; }
        public required DateTime CreatedUtc { get; init; }
        public required int ProcessId { get; init; }
        public required DateTime ProcessStartUtc { get; init; }
        public required double MaxMinutes { get; init; }
        public string? Model { get; init; }
        public string? StartedBy { get; init; }

        /// <summary>The program and its arguments, for whoever wants to know what ran.</summary>
        public required List<string> Command { get; init; }
    }

    /// <summary>
    /// A task Claude Code works on in the background. Everything about it lives in its folder, so any program can
    /// follow it: the app that started it, N.E.O., or an app started later. It keeps running when they end.
    /// </summary>
    public sealed class ClaudeCodeJob
    {
        public const double DefaultBudgetUsd = 5;
        public const double DefaultMaxMinutes = 60;

        /// <summary>Building and testing .NET programs; in PowerShell too, which Claude Code may use on Windows.</summary>
        public static IReadOnlyList<string> DefaultAllowedTools { get; } = ["Bash(dotnet *)", "PowerShell(dotnet *)"];

        internal const string JobFileName = "job.json";
        internal const string PromptFileName = "prompt.txt";
        internal const string EventsFileName = "events.jsonl";
        internal const string ErrorsFileName = "stderr.txt";
        internal const string StoppedFileName = "stopped.txt";

        internal static TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(400);

        private readonly JobFile _job;
        private readonly object _gate = new();
        private readonly ClaudeCodeTranscript _transcript = new();
        private long _readTo;
        private ClaudeCodeJobResult? _end;

        internal ClaudeCodeJob(string folder, JobFile job)
        {
            Folder = folder;
            _job = job;
        }

        public string Id => _job.Id;
        public string Title => _job.Title;

        /// <summary>The job's own folder: the task, Claude Code's events, its error output.</summary>
        public string Folder { get; }

        /// <summary>Where Claude Code works, and where what it makes ends up.</summary>
        public string WorkingDirectory => _job.WorkingDirectory;

        public DateTime CreatedUtc => _job.CreatedUtc;

        /// <summary>The conversation, for a follow-up task.</summary>
        public string SessionId
        {
            get { lock (_gate) return _transcript.SessionId ?? _job.SessionId; }
        }

        public ClaudeCodeJobState State
        {
            get
            {
                lock (_gate)
                {
                    Refresh();
                    return _end switch
                    {
                        null => ClaudeCodeJobState.Running,
                        { Succeeded: true } => ClaudeCodeJobState.Succeeded,
                        { Ending: "cancelled" } => ClaudeCodeJobState.Cancelled,
                        _ => ClaudeCodeJobState.Failed,
                    };
                }
            }
        }

        /// <summary>How it ended; null while it runs.</summary>
        public ClaudeCodeJobResult? Result
        {
            get { lock (_gate) { Refresh(); return _end; } }
        }

        /// <summary>Short lines on what Claude Code did so far.</summary>
        public IReadOnlyList<string> Progress
        {
            get { lock (_gate) { Refresh(); return _transcript.Progress.ToList(); } }
        }

        /// <summary>Whether its process still runs. Cheap: reads nothing.</summary>
        internal bool ProcessRunning => DetachedProcess.IsRunning(_job.ProcessId, _job.ProcessStartUtc);

        /// <summary>Waits until it ends. A cancelled token stops the waiting, not the job.</summary>
        public async Task<ClaudeCodeJobResult> WaitAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default)
        {
            var told = 0;
            while (true)
            {
                ClaudeCodeJobResult? end;
                List<string> news;
                lock (_gate)
                {
                    Refresh();
                    end = _end;
                    news = _transcript.Progress.Skip(told).ToList();
                }

                foreach (var line in news)
                    progress?.Report(line);
                told += news.Count;

                if (end != null)
                    return end;

                await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>Waits until it ends. A cancelled token stops the job too.</summary>
        public async Task<ClaudeCodeJobResult> RunToEndAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default)
        {
            try
            {
                return await WaitAsync(progress, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Cancel();
                throw;
            }
        }

        /// <summary>Stops Claude Code and whatever it started. Nothing happens once it has ended.</summary>
        public void Cancel()
        {
            lock (_gate)
            {
                Refresh();
                if (_end == null)
                    Stop("cancelled", "Cancelled.");
            }
        }

        private void Refresh()
        {
            if (_end != null)
                return;

            // Asked before reading: whatever it wrote before it ended is in the file by then.
            var running = ProcessRunning;
            ReadNewLines();
            if (_transcript.Result is { } result)
            {
                _end = result;
                return;
            }

            if (ReadStopped() is { } stopped)
            {
                if (running)
                    DetachedProcess.Stop(_job.ProcessId, _job.ProcessStartUtc);
                _end = Ended(stopped.Ending, stopped.Text);
                return;
            }

            if (running)
            {
                if (DateTime.UtcNow - _job.CreatedUtc > TimeSpan.FromMinutes(_job.MaxMinutes))
                    Stop("timeout", $"Claude Code was stopped after {_job.MaxMinutes.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)} minutes.");
                return;
            }

            _end = Ended("no result", "Claude Code ended without a result." + ErrorOutput());
        }

        private void Stop(string ending, string text)
        {
            // The mark first: whoever looks next sees why it ended, not a job that ended without a result.
            try
            {
                File.WriteAllText(Path.Combine(Folder, StoppedFileName), ending + "\n" + text, new UTF8Encoding(false));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            DetachedProcess.Stop(_job.ProcessId, _job.ProcessStartUtc);
            ReadNewLines();
            _end = _transcript.Result ?? Ended(ending, text); // it may have finished just before
        }

        private (string Ending, string Text)? ReadStopped()
        {
            try
            {
                var path = Path.Combine(Folder, StoppedFileName);
                if (!File.Exists(path))
                    return null;
                var text = File.ReadAllText(path);
                var lineEnd = text.IndexOf('\n');
                return lineEnd < 0 ? (text.Trim(), "") : (text[..lineEnd].Trim(), text[(lineEnd + 1)..]);
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        private ClaudeCodeJobResult Ended(string ending, string text)
            => new(false, text, null, _transcript.SessionId ?? _job.SessionId, 0, 0, DateTime.UtcNow - _job.CreatedUtc, ending);

        private string ErrorOutput()
        {
            try
            {
                var text = File.ReadAllText(Path.Combine(Folder, ErrorsFileName)).Trim();
                if (text.Length == 0)
                    return "";
                return "\n" + (text.Length <= 800 ? text : "…" + text[^800..]);
            }
            catch (IOException) { return ""; }
            catch (UnauthorizedAccessException) { return ""; }
        }

        private void ReadNewLines()
        {
            try
            {
                using var stream = new FileStream(Path.Combine(Folder, EventsFileName), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length <= _readTo)
                    return;

                stream.Position = _readTo;
                var bytes = new byte[stream.Length - _readTo];
                var count = stream.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
                if (count <= 0)
                    return;

                // Whole lines only. A line break never sits inside a UTF-8 character, so they decode on their own.
                var end = Array.LastIndexOf(bytes, (byte)'\n', count - 1);
                if (end < 0)
                    return;

                _readTo += end + 1;
                foreach (var line in Encoding.UTF8.GetString(bytes, 0, end + 1).Split('\n'))
                    _transcript.Add(line.TrimEnd('\r'));
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        internal static JobFile? ReadJobFile(string folder)
        {
            try
            {
                var path = Path.Combine(folder, JobFileName);
                return File.Exists(path) ? JsonSerializer.Deserialize<JobFile>(File.ReadAllText(path)) : null;
            }
            catch (JsonException) { return null; }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        internal static void WriteJobFile(string folder, JobFile job)
        {
            // Written whole or not at all: other programs may look at the folder at any moment.
            var path = Path.Combine(folder, JobFileName);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(job, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
            File.Move(temporary, path, overwrite: true);
        }
    }
}
