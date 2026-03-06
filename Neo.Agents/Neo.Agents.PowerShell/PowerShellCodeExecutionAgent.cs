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
    /// Ein Agent, der PowerShell-Code ausfuehrt und die Ausgaben zurueckliefert.
    /// Unterstuetzt sowohl Windows PowerShell (powershell.exe) als auch PowerShell Core (pwsh.exe).
    /// </summary>
    public class PowerShellCodeExecutionAgent : AgentBase
    {
        public override string Name => "PowerShellCodeExecutionAgent";

        protected override AgentMetadata CreateMetadata()
        {
            var metadata = new AgentMetadata
            {
                Name = "PowerShell Code Execution Agent",
                Description = "Fuehrt PowerShell-Code aus und gibt stdout, stderr und Exit-Code zurueck."
            };

            // Input: PowerShell-Script (Pflicht)
            metadata.InputParameters.Add(new InputParameter<string>(
                name: "Script",
                isRequired: true,
                description: "Der PowerShell-Code der ausgefuehrt werden soll."
            ));

            // Optionale Argumente fuer das Script (verfuegbar ueber $args)
            metadata.InputParameters.Add(new InputParameter<List<string>>(
                name: "Arguments",
                isRequired: false,
                description: "Optionale Argumente die an das Script uebergeben werden (verfuegbar ueber $args)."
            ));

            // Arbeitsverzeichnis
            metadata.InputParameters.Add(new InputParameter<string>(
                name: "WorkingDirectory",
                isRequired: false,
                description: "Arbeitsverzeichnis fuer die Ausfuehrung. Standard: aktuelles Verzeichnis."
            ));

            // Timeout
            metadata.InputParameters.Add(new InputParameter<int>(
                name: "TimeoutSeconds",
                isRequired: false,
                description: "Timeout in seconds. Default: 60. 0 = unlimited."
            ));

            // PowerShell-Variante
            metadata.InputParameters.Add(new InputParameter<bool>(
                name: "UsePowerShellCore",
                isRequired: false,
                description: "true = pwsh.exe (PowerShell Core), false = powershell.exe (Windows PowerShell). Default: false."
            ));

            // Outputs
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

            // Inputs lesen
            var script = GetInput<string>("Script") ?? throw new InvalidOperationException("Script is required.");
            var arguments = GetInput<List<string>>("Arguments") ?? new List<string>();
            var workingDirectory = GetInput<string>("WorkingDirectory");
            var timeoutSeconds = GetInput<int>("TimeoutSeconds");
            var usePowerShellCore = GetInput<bool>("UsePowerShellCore");

            // Defaults
            if (timeoutSeconds == 0)
                timeoutSeconds = 60; // Default: 60 Sekunden
            if (string.IsNullOrWhiteSpace(workingDirectory))
                workingDirectory = Environment.CurrentDirectory;

            // Script mit Argumenten vorbereiten
            var fullScript = PrepareScriptWithArguments(script, arguments);

            // PowerShell ausfuehren
            var (stdout, stderr, exitCode) = await RunPowerShellAsync(
                fullScript,
                workingDirectory,
                usePowerShellCore,
                timeoutSeconds,
                ct);

            // Outputs setzen
            SetOutput("StandardOutput", stdout);
            SetOutput("ErrorOutput", stderr);
            SetOutput("ExitCode", exitCode);
            SetOutput("Success", exitCode == 0);
        }

        /// <summary>
        /// Bereitet das Script vor und fuegt die Argumente als $args Array hinzu.
        /// </summary>
        private static string PrepareScriptWithArguments(string script, List<string> arguments)
        {
            if (arguments.Count == 0)
                return script;

            // Argumente escapen (einfache Anfuehrungszeichen verdoppeln)
            var escapedArgs = arguments.Select(a => $"'{a.Replace("'", "''")}'");
            var argsInit = string.Join(",", escapedArgs);

            return $"$args = @({argsInit})\n{script}";
        }

        /// <summary>
        /// Fuehrt PowerShell aus und gibt stdout, stderr und Exit-Code zurueck.
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
                // -NoProfile: Schnellerer Start, keine Profilscripte laden
                // -NonInteractive: Keine Benutzereingaben erwarten
                // -Command -: Script von stdin lesen
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
                    $"Konnte PowerShell nicht starten ({executable}). " +
                    $"Ist PowerShell installiert und im PATH? Fehler: {ex.Message}", ex);
            }

            // Script via stdin senden und stdin schliessen
            await process.StandardInput.WriteAsync(script);
            process.StandardInput.Close();

            // Stdout und stderr async lesen (um Deadlocks zu vermeiden)
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            // Auf Prozess-Ende warten mit Timeout
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
                // Timeout - Prozess beenden
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Ignorieren wenn Kill fehlschlaegt
                }

                throw new TimeoutException(
                    $"PowerShell-Ausfuehrung hat das Timeout von {timeoutSeconds} Sekunden ueberschritten.");
            }

            // Ausgaben lesen
            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            return (stdout.TrimEnd(), stderr.TrimEnd(), process.ExitCode);
        }
    }
}
