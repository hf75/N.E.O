using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Neo.Agents.Core;

namespace Neo.Agents
{
    /// <summary>
    /// Claude Code's jobs on this computer, in one folder that every program can look into. Starting one hands it
    /// to Claude Code in the background; <see cref="WatchFinished"/> tells when one ends, whoever started it.
    /// </summary>
    public static class ClaudeCodeJobs
    {
        /// <summary>The folder with one folder per job.</summary>
        public static string Root => ClaudeCodeJobStore.Default.Root;

        /// <summary>Hands a task to Claude Code and returns at once. Throws <see cref="ClaudeCodeUnavailableException"/> when it is off or missing.</summary>
        public static ClaudeCodeJob Start(ClaudeCodeRequest request) => ClaudeCodeJobStore.Default.Start(request);

        public static ClaudeCodeJob? Find(string id) => ClaudeCodeJobStore.Default.Find(id);

        /// <summary>The newest jobs first.</summary>
        public static IReadOnlyList<ClaudeCodeJob> Recent(int count = 20) => ClaudeCodeJobStore.Default.Recent(count);

        /// <summary>
        /// Calls <paramref name="finished"/> once for every job that ends from now on, on a background thread.
        /// Dispose the result to stop. This is how N.E.O. learns of a job whose app is closed by now.
        /// </summary>
        public static IDisposable WatchFinished(Action<ClaudeCodeJob> finished) => ClaudeCodeJobStore.Default.WatchFinished(finished);
    }

    /// <summary>Data from a job, in a schema the end result has to fill in.</summary>
    public static class ClaudeCodeData
    {
        /// <summary>The schema for a <typeparamref name="T"/>: a record or class, or a list of them.</summary>
        public static string SchemaFor<T>() => JsonDataSchema.ForResult(typeof(T), JsonDataSchema.Options()).ToJsonString();

        /// <summary>The data the job filled in; the default when it filled in none.</summary>
        public static T? Read<T>(ClaudeCodeJobResult result)
            => string.IsNullOrWhiteSpace(result.StructuredOutputJson)
                ? default
                : JsonDataSchema.ReadResult<T>(result.StructuredOutputJson, JsonDataSchema.Options());
    }

    /// <summary>A folder of jobs, how Claude Code is found and what environment it gets. Tests make their own.</summary>
    internal sealed class ClaudeCodeJobStore(string root, Func<ClaudeCodeCommand> findCommand, Func<IReadOnlyDictionary<string, string>> environment)
    {
        public static ClaudeCodeJobStore Default { get; } = new(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Neo", "ClaudeCode", "Jobs"),
            ClaudeCodeSetup.FindCommand,
            ClaudeCodeSetup.ChildEnvironment);

        public string Root { get; } = root;

        public ClaudeCodeJob Start(ClaudeCodeRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.Prompt))
                throw new ArgumentException("There is no task for Claude Code.", nameof(request));

            // Before anything is written: when Claude Code is off or missing, nothing is left behind.
            var command = findCommand();

            var id = Guid.NewGuid().ToString("D");
            var folder = Path.Combine(Root, id);
            Directory.CreateDirectory(folder);

            var workingDirectory = string.IsNullOrWhiteSpace(request.WorkingDirectory)
                ? Path.Combine(folder, "work")
                : Path.GetFullPath(request.WorkingDirectory);
            Directory.CreateDirectory(workingDirectory);

            var prompt = Path.Combine(folder, ClaudeCodeJob.PromptFileName);
            File.WriteAllText(prompt, request.Prompt, new UTF8Encoding(false));

            var sessionId = string.IsNullOrWhiteSpace(request.ResumeSessionId) ? id : request.ResumeSessionId;
            var arguments = command.Arguments.Concat(ClaudeCodeArguments.For(request, sessionId, workingDirectory)).ToList();

            var started = DetachedProcess.Start(command.FileName, arguments, workingDirectory, environment(), prompt,
                Path.Combine(folder, ClaudeCodeJob.EventsFileName), Path.Combine(folder, ClaudeCodeJob.ErrorsFileName));

            var job = new JobFile
            {
                Id = id,
                Title = string.IsNullOrWhiteSpace(request.Title) ? ClaudeCodeTranscript.OneLine(request.Prompt, 80) : request.Title.Trim(),
                WorkingDirectory = workingDirectory,
                SessionId = sessionId,
                ResumedSessionId = request.ResumeSessionId,
                CreatedUtc = DateTime.UtcNow,
                ProcessId = started.ProcessId,
                ProcessStartUtc = started.StartTimeUtc,
                MaxMinutes = request.MaxMinutes,
                Model = request.Model,
                StartedBy = $"{Process.GetCurrentProcess().ProcessName} ({Environment.ProcessId})",
                Command = [command.FileName, .. arguments],
            };
            ClaudeCodeJob.WriteJobFile(folder, job);
            return new ClaudeCodeJob(folder, job);
        }

        public ClaudeCodeJob? Find(string id)
        {
            if (string.IsNullOrWhiteSpace(id) || id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || id.Contains(".."))
                return null;
            return Open(Path.Combine(Root, id));
        }

        public IReadOnlyList<ClaudeCodeJob> Recent(int count)
            => Folders().Select(Open).OfType<ClaudeCodeJob>().OrderByDescending(job => job.CreatedUtc).Take(count).ToList();

        internal IEnumerable<string> Folders()
        {
            try
            {
                return Directory.Exists(Root) ? Directory.GetDirectories(Root) : [];
            }
            catch (IOException) { return []; }
            catch (UnauthorizedAccessException) { return []; }
        }

        /// <summary>The job in that folder; null while its job.json is not written yet.</summary>
        internal static ClaudeCodeJob? Open(string folder)
            => ClaudeCodeJob.ReadJobFile(folder) is { } job ? new ClaudeCodeJob(folder, job) : null;

        public IDisposable WatchFinished(Action<ClaudeCodeJob> finished, TimeSpan? interval = null)
        {
            var watch = new FinishWatch(this, finished);
            watch.Begin(interval ?? TimeSpan.FromSeconds(2));
            return watch;
        }

        internal sealed class FinishWatch(ClaudeCodeJobStore store, Action<ClaudeCodeJob> finished) : IDisposable
        {
            private readonly Dictionary<string, ClaudeCodeJob> _running = new(StringComparer.OrdinalIgnoreCase);
            private readonly HashSet<string> _known = new(StringComparer.OrdinalIgnoreCase);
            private Timer? _timer;
            private int _busy;

            public void Begin(TimeSpan interval)
            {
                // What ended before is old news; what runs now is watched.
                foreach (var folder in store.Folders())
                {
                    if (Open(folder) is not { } job)
                        continue; // its job.json comes in a moment: then it is new
                    _known.Add(job.Id);
                    // The process first, as that reads nothing. It may still be on its way out after its result.
                    if (job.ProcessRunning && job.State == ClaudeCodeJobState.Running)
                        _running[job.Id] = job;
                }
                _timer = new Timer(_ => Tick(), null, interval, interval);
            }

            internal void Tick()
            {
                // A look that takes longer than the interval is not overtaken by the next one.
                if (Interlocked.Exchange(ref _busy, 1) == 1)
                    return;

                var ended = new List<ClaudeCodeJob>();
                try
                {
                    foreach (var folder in store.Folders())
                    {
                        if (_known.Contains(Path.GetFileName(folder)))
                            continue;
                        if (Open(folder) is not { } job)
                            continue;
                        _known.Add(job.Id);
                        _running[job.Id] = job;
                    }

                    foreach (var (id, job) in _running.ToList())
                    {
                        if (job.State == ClaudeCodeJobState.Running)
                            continue;
                        _running.Remove(id);
                        ended.Add(job);
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                finally
                {
                    Volatile.Write(ref _busy, 0);
                }

                foreach (var job in ended)
                {
                    try { finished(job); }
                    catch (Exception ex) { Debug.WriteLine($"[ClaudeCode] A notice about job {job.Id} failed: {ex.Message}"); }
                }
            }

            public void Dispose() => _timer?.Dispose();
        }
    }

    /// <summary>The command line options for one job.</summary>
    internal static class ClaudeCodeArguments
    {
        public static List<string> For(ClaudeCodeRequest request, string sessionId, string workingDirectory)
        {
            // The task comes on the standard input: no length limit, and nothing in it is read as an option.
            var arguments = new List<string> { "-p", "--output-format", "stream-json", "--verbose" };

            if (string.IsNullOrWhiteSpace(request.ResumeSessionId))
                arguments.AddRange(["--session-id", sessionId]);
            else
                arguments.AddRange(["--resume", request.ResumeSessionId]);

            arguments.AddRange(["--permission-mode", request.PermissionMode]);
            if (request.AllowedTools.Count > 0)
            {
                arguments.Add("--allowedTools");
                arguments.AddRange(request.AllowedTools);
            }
            // Nobody is there to answer.
            arguments.AddRange(["--disallowedTools", "AskUserQuestion"]);
            arguments.AddRange(["--append-system-prompt", SystemPrompt(workingDirectory)]);
            arguments.AddRange(["--max-budget-usd", request.MaxBudgetUsd.ToString("0.##", CultureInfo.InvariantCulture)]);

            if (!string.IsNullOrWhiteSpace(request.Model) && !request.Model.Equals("default", StringComparison.OrdinalIgnoreCase))
                arguments.AddRange(["--model", request.Model.Trim()]);
            if (!string.IsNullOrWhiteSpace(request.Effort))
                arguments.AddRange(["--effort", request.Effort.Trim()]);
            if (!string.IsNullOrWhiteSpace(request.JsonSchema))
                arguments.AddRange(["--json-schema", request.JsonSchema]);

            return arguments;
        }

        internal static string SystemPrompt(string workingDirectory) =>
            "You work for N.E.O., a program that builds small tools, on a task that is too big for it. "
            + "You run in the background: nobody watches this session and nobody can answer questions, so decide sensibly yourself and finish the task. "
            + $"Work in the folder {workingDirectory}, and create or change files only there. "
            + "Do not start programs that wait for a person, such as windows or servers: build and test them, and leave starting them to the user. "
            + "Your last message is shown to the user as the result: say briefly what you did and what came out of it, with paths, and if you built a program, how to start it.";
    }
}
