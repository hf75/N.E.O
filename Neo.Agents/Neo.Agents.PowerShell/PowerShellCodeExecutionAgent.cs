using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Neo.Agents.Core;

namespace Neo.Agents
{
    /// <summary>
    /// An agent that executes PowerShell code and returns the output.
    /// Supports both Windows PowerShell (powershell.exe) and PowerShell Core (pwsh.exe).
    /// </summary>
    public class PowerShellCodeExecutionAgent : AgentBase
    {
        public override string Name => "PowerShellCodeExecutionAgent";

        protected override AgentMetadata CreateMetadata()
        {
            var metadata = new AgentMetadata
            {
                Name = "PowerShell Code Execution Agent",
                Description = "Executes PowerShell code and returns stdout, stderr and exit code."
            };

            metadata.InputParameters.Add(new InputParameter<string>(
                name: "Script",
                isRequired: true,
                description: "The PowerShell code to execute."
            ));

            metadata.InputParameters.Add(new InputParameter<List<string>>(
                name: "Arguments",
                isRequired: false,
                description: "Optional arguments passed to the script (available via $args)."
            ));

            metadata.InputParameters.Add(new InputParameter<string>(
                name: "WorkingDirectory",
                isRequired: false,
                description: "Working directory for execution. Default: current directory."
            ));

            metadata.InputParameters.Add(new InputParameter<int>(
                name: "TimeoutSeconds",
                isRequired: false,
                description: "Timeout in seconds. Default: 60. 0 = unlimited."
            ));

            metadata.InputParameters.Add(new InputParameter<bool>(
                name: "UsePowerShellCore",
                isRequired: false,
                description: "true = pwsh.exe (PowerShell Core), false = powershell.exe (Windows PowerShell). Default: false."
            ));

            metadata.OutputParameters.Add(new OutputParameter<string>(
                name: "StandardOutput",
                isAlwaysProvided: true,
                description: "The standard output (stdout) of the script."
            ));

            metadata.OutputParameters.Add(new OutputParameter<string>(
                name: "ErrorOutput",
                isAlwaysProvided: true,
                description: "The error output (stderr) of the script."
            ));

            metadata.OutputParameters.Add(new OutputParameter<int>(
                name: "ExitCode",
                isAlwaysProvided: true,
                description: "The exit code of the PowerShell process."
            ));

            metadata.OutputParameters.Add(new OutputParameter<bool>(
                name: "Success",
                isAlwaysProvided: true,
                description: "true if ExitCode == 0, false otherwise."
            ));

            return metadata;
        }

        public override void ValidateOptionsAndInputs()
        {
            var script = GetInput<string>("Script");
            if (string.IsNullOrWhiteSpace(script))
                throw new ArgumentException("The PowerShell script must not be empty.");

            var workingDir = GetInput<string>("WorkingDirectory");
            if (!string.IsNullOrWhiteSpace(workingDir) && !Directory.Exists(workingDir))
                throw new ArgumentException($"Working directory does not exist: {workingDir}");

            var timeout = GetInput<int>("TimeoutSeconds");
            if (timeout < 0)
                throw new ArgumentException("TimeoutSeconds must not be negative.");
        }

        public override async Task ExecuteAsync(CancellationToken? cancellationToken = null)
        {
            ValidateOptionsAndInputs();

            var ct = cancellationToken ?? CancellationToken.None;

            var script = GetInput<string>("Script") ?? throw new InvalidOperationException("Script is required.");
            var arguments = GetInput<List<string>>("Arguments") ?? new List<string>();
            var workingDirectory = GetInput<string>("WorkingDirectory");
            var timeoutSeconds = GetInput<int>("TimeoutSeconds");
            var usePowerShellCore = GetInput<bool>("UsePowerShellCore");

            if (timeoutSeconds == 0)
                timeoutSeconds = 60;
            if (string.IsNullOrWhiteSpace(workingDirectory))
                workingDirectory = Environment.CurrentDirectory;

            var fullScript = PrepareScriptWithArguments(script, arguments);

            var (stdout, stderr, exitCode) = await RunPowerShellAsync(
                fullScript,
                workingDirectory,
                usePowerShellCore,
                timeoutSeconds,
                ct);

            SetOutput("StandardOutput", stdout);
            SetOutput("ErrorOutput", stderr);
            SetOutput("ExitCode", exitCode);
            SetOutput("Success", exitCode == 0);
        }

        /// <summary>
        /// Prepares the script by injecting arguments as the $args array.
        /// </summary>
        private static string PrepareScriptWithArguments(string script, List<string> arguments)
        {
            if (arguments.Count == 0)
                return script;

            // Escape single quotes by doubling them
            var escapedArgs = arguments.Select(a => $"'{a.Replace("'", "''")}'");
            var argsInit = string.Join(",", escapedArgs);

            return $"$args = @({argsInit})\n{script}";
        }

        /// <summary>
        /// Executes PowerShell and returns stdout, stderr and exit code.
        /// </summary>
        private static async Task<(string stdout, string stderr, int exitCode)> RunPowerShellAsync(
            string script,
            string workingDirectory,
            bool usePowerShellCore,
            int timeoutSeconds,
            CancellationToken cancellationToken)
        {
            var executable = usePowerShellCore ? "pwsh" : "powershell";

            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = "-NoProfile -NonInteractive -Command -",
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = workingDirectory
            };

            using var process = new Process { StartInfo = startInfo };

            try
            {
                process.Start();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Could not start PowerShell ({executable}). " +
                    $"Is PowerShell installed and in PATH? Error: {ex.Message}", ex);
            }

            await process.StandardInput.WriteAsync(script);
            process.StandardInput.Close();

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            var timeoutMs = timeoutSeconds * 1000;
            using var timeoutCts = timeoutSeconds > 0
                ? new CancellationTokenSource(timeoutMs)
                : new CancellationTokenSource();

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, timeoutCts.Token);

            try
            {
                await process.WaitForExitAsync(linkedCts.Token);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                throw new TimeoutException(
                    $"PowerShell execution exceeded the timeout of {timeoutSeconds} seconds.");
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            return (stdout.TrimEnd(), stderr.TrimEnd(), process.ExitCode);
        }
    }
}
