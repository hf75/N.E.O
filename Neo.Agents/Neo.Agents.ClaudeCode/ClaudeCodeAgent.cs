using System.Runtime.CompilerServices;
using Neo.Agents.Core;

namespace Neo.Agents
{
    /// <summary>
    /// Hands a task that is too big for N.E.O. to the Claude Code CLI, which works on it in the background, and
    /// streams what it does. Off unless the environment variable NEO_CLAUDE_CODE is set.
    /// As a plugin it gives generated apps <c>AIClaudeCode</c>.
    /// </summary>
    public sealed class ClaudeCodeAgent : StreamingAgentBase, IAppIntegratedAgent
    {
        public override string Name => "ClaudeCodeAgent";

        /// <summary>Where the jobs go, how Claude Code is found. Tests put in their own.</summary>
        internal ClaudeCodeJobStore Store { get; init; } = ClaudeCodeJobStore.Default;

        protected override AgentMetadata CreateMetadata()
        {
            var metadata = new AgentMetadata
            {
                Name = "Claude Code Agent",
                Description = "Hands a task to Claude Code, which works on it in the background with files and programs, and reports what it does and how it ended.",
            };

            metadata.InputParameters.Add(new InputParameter<string>("Prompt", true, "The complete task. Claude Code cannot ask back."));
            metadata.InputParameters.Add(new InputParameter<string>("Title", false, "A short name for lists and notices."));
            metadata.InputParameters.Add(new InputParameter<string>("WorkingDirectory", false, "Where Claude Code works. Default: a new folder of its own."));
            metadata.InputParameters.Add(new InputParameter<string>("ResumeSessionId", false, "Continues that earlier conversation; give its folder as WorkingDirectory too."));
            metadata.InputParameters.Add(new InputParameter<string>("JsonSchema", false, "A JSON schema (object at the top) the end result has to fill in."));

            metadata.Options.Add(new Option<string>("Model", false, "", "opus, sonnet, haiku or a model name. Empty or 'default': Claude Code's own setting."));
            metadata.Options.Add(new Option<string>("Effort", false, "", "low, medium, high, xhigh or max. Empty: Claude Code's own setting."));
            metadata.Options.Add(new Option<double>("MaxBudgetUsd", false, ClaudeCodeJob.DefaultBudgetUsd, "Claude Code stops when the task has cost this much."));
            metadata.Options.Add(new Option<double>("MaxMinutes", false, ClaudeCodeJob.DefaultMaxMinutes, "The job is stopped after this long."));
            metadata.Options.Add(new Option<string>("PermissionMode", false, "acceptEdits", "Claude Code's permission mode."));
            metadata.Options.Add(new Option<List<string>>("AllowedTools", false, [.. ClaudeCodeJob.DefaultAllowedTools], "Permission rules for what it may do besides changing files in its folder."));

            metadata.OutputParameters.Add(new OutputParameter<string>("Result", true, "Claude Code's closing words, or what stopped it."));
            metadata.OutputParameters.Add(new OutputParameter<bool>("Succeeded", true, "Whether it finished the task without an error."));
            metadata.OutputParameters.Add(new OutputParameter<string>("JobId", true, "The job, for ClaudeCodeJobs.Find."));
            metadata.OutputParameters.Add(new OutputParameter<string>("JobFolder", true, "The job's folder: task, events, error output."));
            metadata.OutputParameters.Add(new OutputParameter<string>("WorkingDirectory", true, "Where Claude Code worked."));
            metadata.OutputParameters.Add(new OutputParameter<string>("SessionId", true, "The conversation, for a follow-up task."));
            metadata.OutputParameters.Add(new OutputParameter<double>("CostUsd", true, "What the task cost."));
            metadata.OutputParameters.Add(new OutputParameter<string>("StructuredOutput", false, "The data it filled in, as JSON, when a schema was given."));

            return metadata;
        }

        public override void ValidateOptionsAndInputs()
        {
            if (string.IsNullOrWhiteSpace(GetInput<string>("Prompt")))
                throw new ArgumentException("There is no task for Claude Code.");
            if (GetOption<double>("MaxBudgetUsd") <= 0)
                throw new ArgumentException("MaxBudgetUsd must be more than 0.");
            if (GetOption<double>("MaxMinutes") <= 0)
                throw new ArgumentException("MaxMinutes must be more than 0.");
        }

        protected override async IAsyncEnumerable<StreamChunk> ExecuteStreamingCoreAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var request = new ClaudeCodeRequest(GetInput<string>("Prompt"))
            {
                Title = GetInput<string>("Title"),
                WorkingDirectory = GetInput<string>("WorkingDirectory"),
                ResumeSessionId = GetInput<string>("ResumeSessionId"),
                JsonSchema = GetInput<string>("JsonSchema"),
                Model = GetOption<string>("Model"),
                Effort = GetOption<string>("Effort"),
                MaxBudgetUsd = GetOption<double>("MaxBudgetUsd"),
                MaxMinutes = GetOption<double>("MaxMinutes"),
                PermissionMode = GetOption<string>("PermissionMode"),
                AllowedTools = GetOption<List<string>>("AllowedTools") ?? [],
            };

            var job = Store.Start(request);
            SetOutput("JobId", job.Id);
            SetOutput("JobFolder", job.Folder);
            SetOutput("WorkingDirectory", job.WorkingDirectory);

            var told = 0;
            ClaudeCodeJobResult? end = null;
            try
            {
                while (end == null)
                {
                    end = job.Result;
                    var lines = job.Progress;
                    while (told < lines.Count)
                        yield return Chunk("Progress", lines[told++]);
                    if (end == null)
                        await Task.Delay(ClaudeCodeJob.PollInterval, cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                // Cancelled, or nobody reads on: the job is not left running unseen.
                if (end == null)
                    job.Cancel();
            }

            SetOutput("Result", end.Text);
            SetOutput("Succeeded", end.Succeeded);
            SetOutput("SessionId", end.SessionId ?? job.SessionId);
            SetOutput("CostUsd", end.CostUsd);
            SetOutput("StructuredOutput", end.StructuredOutputJson ?? "");

            yield return FinalChunk("Result", end.Text);
        }

        // ── IAppIntegratedAgent ────────────────────────────────────────

        public string DisplayName => "Claude Code";
        public string SettingsKey => "ClaudeCode";
        public string? RequiredEnvVar => ClaudeCodeSetup.SwitchVariable;

        /// <summary>Whatever the user set in Claude Code itself.</summary>
        public string DefaultModel => "default";

        public string? HelperTemplateCode =>
            AgentResourceLoader.LoadEmbeddedResource(typeof(ClaudeCodeAgent), "ClaudeCodeHelper.cs");

        public IReadOnlyDictionary<string, string> TemplatePlaceholders { get; } =
            new Dictionary<string, string> { ["CLAUDE_CODE_MODEL_PLACEHOLDER"] = "ClaudeCode" };

        public string AgentDllName => "Neo.Agents.ClaudeCode.dll";

        public string? SystemMessageDocs =>
            @"

            You also have Claude Code in the 'Neo.App' namespace: an AI coding agent that works on a big task in the background for minutes, with files and programs on this computer:

            // Hands a task to Claude Code and waits until it is done. progress gets short lines on what it does. folder: where it works; null gives it a new folder of its own. Takes minutes and costs up to a few US dollars.
            public static Task<ClaudeCodeResult> AIClaudeCode.RunAsync(string task, IProgress<string>? progress = null, string? folder = null, CancellationToken cancellationToken = default)
            // The same, with the outcome as data in Data. T: a record or class of strings, numbers, bool, DateTime and List<...>, or a List<...> of it.
            public static Task<ClaudeCodeResult<T>> AIClaudeCode.RunAsync<T>(string task, IProgress<string>? progress = null, string? folder = null, CancellationToken cancellationToken = default)
            // A follow-up task in the same conversation and folder, such as a change to what it built.
            public static Task<ClaudeCodeResult> AIClaudeCode.ContinueAsync(ClaudeCodeResult previous, string task, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
            public sealed record ClaudeCodeResult(bool Succeeded, string Text, string Folder, string JobId, string SessionId, double CostUsd);
            public sealed record ClaudeCodeResult<T>(bool Succeeded, T? Data, string Text, string Folder, string JobId, string SessionId, double CostUsd);

            Use it for what is too big for this app itself: building a larger program or a helper tool, working through many files or a whole project, a task of many steps. For a quick answer use AIQuery instead.
            Write the task as a complete instruction: Claude Code knows nothing about this app and cannot ask back.
            Start it on a user action such as a button, one task at a time; never on a timer, in a loop or on every keystroke.
            While it works, show the progress lines and a Cancel button that cancels the token. Then show Text (it may contain Markdown) and Folder, with a button that opens the folder.
            The task keeps running when this app closes, and N.E.O. tells the user when it is done.
            Catch exceptions and show their message: Claude Code may be missing or switched off.
            ";

        public Task<List<string>> FetchAvailableModelsAsync(string? apiKeyOrEndpoint)
            => Task.FromResult(new List<string> { "default", "opus", "sonnet", "haiku" });
    }
}
