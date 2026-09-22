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

        // Heal in place: the app reported an error it survived. It keeps running with the user's
        // data while the AI fixes the code; the fix is hot-reloaded into the same instance and the
        // failed action is done again. Set for the duration of the repair; DisplayCompiledDllAsync
        // reads it when it hands the update to the window.
        private (bool Active, string? Action) _inPlaceRepair;

        private async Task PerformAutomaticRepairAsync(ErrorMessage err)
        {
            // In place only when the app is still there to be patched.
            bool inPlace = err.Recoverable && ChildProcessService.HasLoadedControl && _hotReload.HasRunningApp;
            try
            {
                // 1. Status setzen und UI blockieren.
                _view.ShowRepairOverlay();
                await SetStatusAsync(AppStatus.Repairing, true, inPlace ? "Repairing the running app..." : "Attempting automatic repair...");

                // 2. Logik ausführen
                if (inPlace)
                {
                    _inPlaceRepair = (true, err.FailedAction);
                    var where = err.FailedMethod != null ? $" in {err.FailedMethod}" : "";
                    Logger.LogMessage($"The app ran into an error{where}: {err.Message} — repairing it in place, your data stays.", BubbleType.Info);
                }
                else
                {
                    await ChildProcessService.RestartAsync();
                    ChildProcessService.HideChild();
                    Logger.LogMessage("The application crashed. Attempting an automatic repair...", BubbleType.Info);
                }

                string exceptionMessage = $"{err.Message}\n\nType: {err.ExceptionType}\n\nStack Trace:\n{err.StackTrace}";
                LastErrorMsg = exceptionMessage;

                string repairPrompt = inPlace
                    ? "Please fix the previous error. The app is still running with the user's data and will be patched in place: " +
                      "fix it inside the failing method (change method bodies only, add members if needed), so that the fix can be applied without a restart."
                    : "Please fix the previous error.";

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

                Logger.LogMessage(inPlace && !_inPlaceRepair.Active
                    ? "Repaired in place. The app kept running, and the failed action was done again."
                    : "Automatic repair successful. The application has been restored.", BubbleType.Info);                            
            }
            catch (OperationCanceledException)
            {
                Logger.LogMessage("Repair was aborted by user. Resetting the application.", BubbleType.Info);
                await ClearSessionAsync(); // Ruft unsere saubere Clear-Methode auf
            }
            finally
            {
                // 3. UI aufräumen und Status zurücksetzen
                _inPlaceRepair = default;
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
