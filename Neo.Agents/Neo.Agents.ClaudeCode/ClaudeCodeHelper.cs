using System;
using System.Threading;
using System.Threading.Tasks;

using Neo.Agents;

namespace Neo.App
{
    /// <summary>How a task for Claude Code ended: its closing words and the folder it worked in.</summary>
    public sealed record ClaudeCodeResult(bool Succeeded, string Text, string Folder, string JobId, string SessionId, double CostUsd);

    /// <summary>How a task for Claude Code ended, with the data it filled in.</summary>
    public sealed record ClaudeCodeResult<T>(bool Succeeded, T? Data, string Text, string Folder, string JobId, string SessionId, double CostUsd);

    /// <summary>
    /// Hands tasks that are too big for this app to Claude Code, which works on them in the background.
    /// A task keeps running when the app closes; N.E.O. tells the user when it is done.
    /// </summary>
    public static class AIClaudeCode
    {
        private const string Model = "CLAUDE_CODE_MODEL_PLACEHOLDER";

        /// <summary>Hands a task to Claude Code and waits until it is done. A cancelled token stops it.</summary>
        public static async Task<ClaudeCodeResult> RunAsync(string task, IProgress<string>? progress = null, string? folder = null, CancellationToken cancellationToken = default)
        {
            var job = await Task.Run(() => ClaudeCodeJobs.Start(new ClaudeCodeRequest(task) { WorkingDirectory = folder, Model = Model }), cancellationToken);
            var end = await job.RunToEndAsync(progress, cancellationToken);
            return new ClaudeCodeResult(end.Succeeded, end.Text, job.WorkingDirectory, job.Id, end.SessionId ?? job.SessionId, end.CostUsd);
        }

        /// <summary>The same, with the outcome as data of the given type.</summary>
        public static async Task<ClaudeCodeResult<T>> RunAsync<T>(string task, IProgress<string>? progress = null, string? folder = null, CancellationToken cancellationToken = default)
        {
            var request = new ClaudeCodeRequest(task) { WorkingDirectory = folder, Model = Model, JsonSchema = ClaudeCodeData.SchemaFor<T>() };
            var job = await Task.Run(() => ClaudeCodeJobs.Start(request), cancellationToken);
            var end = await job.RunToEndAsync(progress, cancellationToken);
            return new ClaudeCodeResult<T>(end.Succeeded, ClaudeCodeData.Read<T>(end), end.Text, job.WorkingDirectory, job.Id, end.SessionId ?? job.SessionId, end.CostUsd);
        }

        /// <summary>A follow-up task in the same conversation and folder.</summary>
        public static async Task<ClaudeCodeResult> ContinueAsync(ClaudeCodeResult previous, string task, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
        {
            var request = new ClaudeCodeRequest(task) { WorkingDirectory = previous.Folder, ResumeSessionId = previous.SessionId, Model = Model };
            var job = await Task.Run(() => ClaudeCodeJobs.Start(request), cancellationToken);
            var end = await job.RunToEndAsync(progress, cancellationToken);
            return new ClaudeCodeResult(end.Succeeded, end.Text, job.WorkingDirectory, job.Id, end.SessionId ?? job.SessionId, end.CostUsd);
        }
    }
}
