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
        private async Task HandleChildProcessCrashed(CrashReason reason, ErrorMessage err)
        {
            if (CurrentStatus != AppStatus.Idle)
            {
                // Wenn die App schon beschäftigt ist (z.B. beim Exportieren),
                // kann sie nicht gleichzeitig reparieren. Logge das Problem.
                Logger.LogMessage($"Child process crashed while app was busy ({CurrentStatus}). Recovery postponed.", BubbleType.CompletionError);
                _pendingCrash = (reason, err);
                return;
            }

            try
            {
                switch (reason)
                {
                    case CrashReason.UnhandledException:
                        await PerformAutomaticRepairAsync(err);
                        break;

                    case CrashReason.HeartbeatTimeout:
                    case CrashReason.PipeDisconnected:
                        await ShowRecoveryDialogAsync(err);
                        break;
                }
            }
            catch (Exception ex)
            {
                Logger.LogMessage($"CRITICAL: The recovery/repair process itself failed: {ex.Message}", BubbleType.CompletionError);
            }
        }

        private async Task PerformAutomaticRepairAsync(ErrorMessage err)
        {
            try
            {
                // 1. Status setzen und UI blockieren.
                _view.ShowRepairOverlay();
                await SetStatusAsync(AppStatus.Repairing, true, "Attempting automatic repair...");

                // 2. Logik ausführen
                await ChildProcessService.RestartAsync();
                ChildProcessService.HideChild();

                Logger.LogMessage("The application crashed. Attempting an automatic repair...", BubbleType.Info);
                string exceptionMessage = $"{err.Message}\n\nType: {err.ExceptionType}\n\nStack Trace:\n{err.StackTrace}";
                LastErrorMsg = exceptionMessage;

                string repairPrompt = "Please fix the previous error.";

                if (err.ExceptionType != null &&
                    err.ExceptionType.Contains("Python", StringComparison.OrdinalIgnoreCase))
                {
                    repairPrompt =
                        "The application crashed with a Python runtime error. " +
                        "Please fix the Python code. The error details are included below. " +
                        "Remember: Do not catch PythonException — let errors propagate for automatic repair.";
                }

                await ExecuteGeneralPrompt(repairPrompt, _cancellationSource.Token, showResultInChild: true);

                _view.ClearPrompt();

                Logger.LogMessage("Automatic repair successful. The application has been restored.", BubbleType.Info);                            
            }
            catch (OperationCanceledException)
            {
                Logger.LogMessage("Repair was aborted by user. Resetting the application.", BubbleType.Info);
                await ClearSessionAsync(); // Ruft unsere saubere Clear-Methode auf
            }
            finally
            {
                // 3. UI aufräumen und Status zurücksetzen
                ChildProcessService.ShowChild();
                _view.HideRepairOverlay();
                await SetStatusAsync(AppStatus.Idle, false);
            }
        }

        private async Task ShowRecoveryDialogAsync(ErrorMessage err)
        {
            await ChildProcessService.RestartAsync();

            // Der Dialog ist ein UI-Element, also muss die View ihn erstellen und anzeigen.
            // Wir fügen eine Helfermethode in der MainWindow hinzu.
            var result = await _view.ShowCrashDialogAsync();

            switch (result)
            {
                case CrashDialogResult.Button1: // Restore
                    Logger.LogMessage("Attempting to restore the last known good state...", BubbleType.Info);
                    await ChildProcessService.RestartAsync();
                    await CompileAndShowAsync(VirtualProjectFiles.GetSourceCodeAsStrings());
                    Logger.LogMessage("Restore successful.", BubbleType.Info);
                    break;

                case CrashDialogResult.Button2: // Reset
                    Logger.LogMessage("Resetting the application state...", BubbleType.Info);
                    await ClearSessionAsync();
                    break;

                case CrashDialogResult.Button3: // Do Nothing
                default:
                    Logger.LogMessage("Recovery cancelled by user.", BubbleType.Info);
                    break;
            }
        }
    }
}
