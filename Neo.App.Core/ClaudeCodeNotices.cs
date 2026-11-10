using Neo.Agents;

namespace Neo.App
{
    /// <summary>
    /// What N.E.O.'s chat says when a Claude Code job ends. It also covers the jobs whose app is closed by now:
    /// a job runs on in the background, and this is where the user learns that it is done and where its result is.
    /// </summary>
    internal static class ClaudeCodeNotices
    {
        private const int LongestText = 1200;

        public static string Describe(ClaudeCodeJob job)
        {
            var result = job.Result;
            var lines = new List<string>
            {
                job.State switch
                {
                    ClaudeCodeJobState.Succeeded => $"Claude Code is done: {job.Title}",
                    ClaudeCodeJobState.Cancelled => $"Claude Code was cancelled: {job.Title}",
                    _ => $"Claude Code stopped: {job.Title}",
                },
            };

            var text = result?.Text.Trim() ?? "";
            if (text.Length > 0)
                lines.Add(text.Length <= LongestText ? text : text[..LongestText] + " …");

            lines.Add($"Folder: {job.WorkingDirectory}");
            if (result is { CostUsd: > 0 })
                lines.Add($"Cost: {result.CostUsd.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)} USD");

            return string.Join("\n", lines);
        }
    }

    public partial class AppController
    {
        private IDisposable? _claudeCodeWatch;

        /// <summary>With Claude Code switched on, the chat tells when one of its jobs ends, whoever started it.</summary>
        private void WatchClaudeCodeJobs()
        {
            // By its key, not its type: the discovery may have loaded the plugin's assembly by path.
            if (_claudeCodeWatch != null || !PluginAgents.Any(plugin => plugin.SettingsKey == new ClaudeCodeAgent().SettingsKey))
                return;

            try
            {
                _claudeCodeWatch = ClaudeCodeJobs.WatchFinished(job =>
                    _view.InvokeOnUIThreadAsync(() => Logger.LogMessage(ClaudeCodeNotices.Describe(job), BubbleType.Info)));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ClaudeCode] Cannot watch the jobs: {ex.Message}");
            }
        }
    }
}
