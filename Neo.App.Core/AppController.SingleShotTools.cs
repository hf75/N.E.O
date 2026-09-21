using Neo.Agents;
using Neo.Agents.Core;
using Neo.IPC;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Newtonsoft.Json;
using Neo.AssemblyForge;
using Neo.AssemblyForge.Completion;
using Neo.App.Services;
using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Numerics;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Neo.App
{
    public partial class AppController
    {
        private static async Task<(string stdout, string stderr, int exitCode)> RunProcessAsync(string exePath, int timeoutSeconds, CancellationToken cancellationToken)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(exePath) ?? Environment.CurrentDirectory
            };

            using var process = new Process { StartInfo = startInfo };

            try
            {
                process.Start();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Could not start the compiled application: {ex.Message}", ex);
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            using var timeoutCts = timeoutSeconds > 0
                ? new CancellationTokenSource(timeoutSeconds * 1000)
                : new CancellationTokenSource();

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            try
            {
                await process.WaitForExitAsync(linkedCts.Token);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                throw new TimeoutException($"Console application exceeded the timeout of {timeoutSeconds} seconds.");
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            return (stdout.TrimEnd(), stderr.TrimEnd(), process.ExitCode);
        }

        // ── Single-Shot Tool Execution ────────────────────────────────────

        private async Task ExecuteSingleShotToolAsync(string originalPrompt, StructuredResponse response, CancellationToken cancellationToken)
        {
            AgentStepResult stepResult;

            if (!string.IsNullOrWhiteSpace(response.PowerShellScript))
                stepResult = await ExecutePowerShellStepAsync(response.PowerShellScript, response.Explanation, cancellationToken);
            else if (!string.IsNullOrWhiteSpace(response.ConsoleAppCode))
                stepResult = await ExecuteConsoleAppStepAsync(response.ConsoleAppCode, response.NuGetPackages?.ToList(), response.Explanation, cancellationToken);
            else
                return;

            if (stepResult.WasRejected)
                return;

            string preview = Truncate(ToSingleLine(stepResult.Stdout), 500);
            if (!string.IsNullOrWhiteSpace(stepResult.Stderr))
                preview += " | STDERR: " + Truncate(ToSingleLine(stepResult.Stderr), 200);
            Logger.LogMessage($"{stepResult.ToolName}: {preview}", BubbleType.Info);

            try
            {
                string summaryPrompt = $"The user asked: \"{originalPrompt}\"\n\n"
                    + $"A {stepResult.ToolName} was executed.\n\nSTDOUT:\n{Truncate(stepResult.Stdout, 8000)}\n\n"
                    + (string.IsNullOrEmpty(stepResult.Stderr) ? "" : $"STDERR:\n{Truncate(stepResult.Stderr, 4000)}\n\n")
                    + $"Exit Code: {stepResult.ExitCode}\n\n"
                    + "Please provide a brief, user-friendly explanation of the result in the language the user used.";

                AiAgent!.SetInput("Prompt", summaryPrompt);
                AiAgent.SetInput("SystemMessage", "You summarize tool execution results for the user.");
                AiAgent.SetInput("History", "");
                AiAgent.SetInput("JsonSchema", "");
                await AiAgent.ExecuteAsync(cancellationToken);

                var summary = AiAgent.GetOutput<string>("Result") ?? stepResult.Stdout;
                Logger.LogMessageWithMarkdownFormating(summary, BubbleType.Answer);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception)
            {
                Logger.LogMessage(Truncate(stepResult.Stdout, 2000), BubbleType.Answer);
            }

            LastErrorMsg = string.Empty;
        }

        // ── Agent Step infrastructure ────────────────────────────────────────

        private sealed class AgentStepResult
        {
            public string Stdout { get; init; } = string.Empty;
            public string Stderr { get; init; } = string.Empty;
            public int ExitCode { get; init; }
            public string ToolName { get; init; } = string.Empty;
            public bool WasRejected { get; init; }
        }

        private async Task<AgentStepResult> ExecutePowerShellStepAsync(string script, string? explanation, CancellationToken cancellationToken)
        {
            // Review if AcceptAutomatic is off
            if (!Settings.AcceptAutomatic)
            {
                PatchReviewDecision decision = await _view.ShowPatchReviewDialogAsync(script, null, explanation, isPowerShellMode: true);

                if (decision == PatchReviewDecision.Reject)
                {
                    Logger.LogMessage("PowerShell script rejected.", BubbleType.Info);
                    return new AgentStepResult { WasRejected = true, ToolName = "PowerShell" };
                }
            }

            Logger.LogMessage("Executing PowerShell script...", BubbleType.Info);

            try
            {
                PowerShellAgent.SetInput("Script", script);
                PowerShellAgent.SetInput("TimeoutSeconds", 60);
                await PowerShellAgent.ExecuteAsync(cancellationToken);

                return new AgentStepResult
                {
                    Stdout = PowerShellAgent.GetOutput<string>("StandardOutput") ?? string.Empty,
                    Stderr = PowerShellAgent.GetOutput<string>("ErrorOutput") ?? string.Empty,
                    ExitCode = PowerShellAgent.GetOutput<int>("ExitCode"),
                    ToolName = "PowerShell",
                };
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return new AgentStepResult
                {
                    Stdout = string.Empty,
                    Stderr = $"PowerShell error: {ex.Message}",
                    ExitCode = -1,
                    ToolName = "PowerShell",
                };
            }
        }

        private async Task<AgentStepResult> ExecuteConsoleAppStepAsync(string code, List<string>? nugetPackages, string? explanation, CancellationToken cancellationToken)
        {
            // Review if AcceptAutomatic is off
            if (!Settings.AcceptAutomatic)
            {
                PatchReviewDecision decision = await _view.ShowPatchReviewDialogAsync(code, nugetPackages, explanation, isConsoleAppMode: true);

                if (decision == PatchReviewDecision.Reject)
                {
                    Logger.LogMessage("Console app rejected.", BubbleType.Info);
                    return new AgentStepResult { WasRejected = true, ToolName = "ConsoleApp" };
                }
            }

            Logger.LogMessage("Compiling console application...", BubbleType.Info);

            string? exePath = null;
            string tempDir = Path.Combine(Path.GetTempPath(), "Neo.ConsoleApp", Guid.NewGuid().ToString("N"));

            try
            {
                // Load NuGet packages if needed
                List<string> nugetDlls = new();
                if (nugetPackages != null && nugetPackages.Count > 0)
                {
                    var packs = ConvertPackageListToDictionary(nugetPackages);
                    if (packs.Count > 0)
                    {
                        var nugetResult = await NugetPackageService.LoadPackagesAsync(packs, new List<string>());
                        nugetDlls = nugetResult.DllPaths;
                    }
                }

                // Compile
                exePath = await CompilationService.CompileToExeAsync(
                    new List<string> { code },
                    tempDir,
                    nugetDlls,
                    assemblyName: "ConsoleApp",
                    mainTypeName: "ConsoleApp.Program");

                if (string.IsNullOrWhiteSpace(exePath))
                    throw new InvalidOperationException("Compilation failed — no executable was produced.");

                Logger.LogMessage("Running console application...", BubbleType.Info);

                var (stdout, stderr, exitCode) = await RunProcessAsync(exePath, timeoutSeconds: 60, cancellationToken);

                return new AgentStepResult
                {
                    Stdout = stdout,
                    Stderr = stderr,
                    ExitCode = exitCode,
                    ToolName = "ConsoleApp",
                };
            }
            catch (Exception ex)
            {
                return new AgentStepResult
                {
                    Stdout = string.Empty,
                    Stderr = $"Console app error: {ex.Message}",
                    ExitCode = -1,
                    ToolName = "ConsoleApp",
                };
            }
            finally
            {
                try
                {
                    if (Directory.Exists(tempDir))
                        Directory.Delete(tempDir, recursive: true);
                }
                catch { /* best effort */ }
            }
        }
    }
}
