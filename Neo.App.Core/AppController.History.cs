using Neo.Agents;
using Neo.Agents.Core;
using Neo.IPC;
using Neo.Collab;
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
        public async Task HardResetWithLastStateInit()
        {
            await ChildProcessService.RestartAsync();
            await CompileAndShowAsync( VirtualProjectFiles.GetSourceCodeAsStrings() );
        }

        public Task<bool> Undo() => RunSharedChangeAsync(StateKinds.Undo, "undoing the last change", false, UndoCoreAsync);

        private async Task<bool> UndoCoreAsync()
        {
            if (CurrentStatus != AppStatus.Idle) return false;
            try
            {
                await SetStatusAsync(AppStatus.Busy, false, "Undoing...");
                return await _undoRedo.Undo(RecompileStateAsync); // Direkter Aufruf der lokalen Methode
            }
            finally
            {
                await SetStatusAsync(AppStatus.Idle, false);
            }
        }

        public Task<bool> Redo() => RunSharedChangeAsync(StateKinds.Redo, "redoing a change", false, RedoCoreAsync);

        private async Task<bool> RedoCoreAsync()
        {
            if (CurrentStatus != AppStatus.Idle) return false;
            try
            {
                await SetStatusAsync(AppStatus.Busy, false, "Redoing...");
                return await _undoRedo.Redo(RecompileStateAsync); // Direkter Aufruf
            }
            finally
            {
                await SetStatusAsync(AppStatus.Idle, false);
            }
        }

        public Task<bool> CheckoutHistoryAsync(HistoryNode node) =>
            RunSharedChangeAsync(StateKinds.Checkout, "going to another version from the history", false, () => CheckoutHistoryCoreAsync(node));

        private async Task<bool> CheckoutHistoryCoreAsync(HistoryNode node)
        {
            if (CurrentStatus != AppStatus.Idle) return false;

            try
            {
                await SetStatusAsync(AppStatus.Busy, false, "Checking out...");
                return await _undoRedo.CheckoutAsync(node, RecompileStateAsync);
            }
            finally
            {
                await SetStatusAsync(AppStatus.Idle, false);
            }
        }

        // In AppController.cs
        public async Task ClearSessionAsync(string reason = "Clearing...")
        {
            await RunSharedChangeAsync(StateKinds.Clear, "clearing the session", false, async () =>
            {
                await ClearSessionCoreAsync(reason);
                return true;
            });
        }

        private async Task ClearSessionCoreAsync(string reason = "Clearing...")
        {
            // 1. Prüfen, ob die App bereit ist.
            if (CurrentStatus != AppStatus.Idle) { Debug.WriteLine("[ClearSession] Skipped: not Idle"); return; }

            try
            {
                Debug.WriteLine("[ClearSession] Step 1: SetStatusAsync(Initializing)");
                await SetStatusAsync(AppStatus.Initializing, false, reason);

                Debug.WriteLine("[ClearSession] Step 2: Logger.Clear()");
                Logger.Clear();

                Debug.WriteLine("[ClearSession] Step 3: FileHelper.ClearDirectory");
                FileHelper.ClearDirectory(NuGetPackageDirectory);

                Debug.WriteLine("[ClearSession] Step 4: HandlePythonFiles + GetUserControlBaseCode");
                string freshHistory = "Code:\n\n";
                HandlePythonFilesInVirtualProjectFiles(_settings.UsePython);
                freshHistory += GetUserControlBaseCode();

                Debug.WriteLine("[ClearSession] Step 5: ApplyStateNonUndoable");
                ApplyStateNonUndoable(s =>
                {
                    s.History = freshHistory;
                    s.LastCode = GetUserControlBaseCode();
                    s.NuGetDlls = new();
                    s.PackageVersions = new();
                }, clearHistory: true);

                Debug.WriteLine("[ClearSession] Step 6: PreloadMandatoryNugetPacks");
                await PreloadMandatoryNugetPacks();

                Debug.WriteLine("[ClearSession] Step 7: RecreatePromptToDllSession");
                VirtualProjectFiles.UpdateFileContent("./currentcode.cs", GetUserControlBaseCode());
                RecreatePromptToDllSession(preserveState: true);

                Debug.WriteLine("[ClearSession] Step 8: ChildProcessService.RestartAsync");
                await ChildProcessService.RestartAsync();

                Debug.WriteLine("[ClearSession] Step 9: UI cleanup");
                _view.ShowEmptyContent();
                _view.HideFrostedSnapshot();
                ChildProcessService.HideChild();

                GrantedFolders.Clear();
                _view.ResetButtonMenu();

                _cancellationSource = new CancellationTokenSource();
                Debug.WriteLine("[ClearSession] Try-block completed successfully");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ClearSession] EXCEPTION in try-block: {ex}");
            }
            finally
            {
                Debug.WriteLine("[ClearSession] Finally: starting cleanup");
                ChildProcessService.ShowChild();
                _view.ActivateWindow();
                _view.PromptText = string.Empty;
                _view.FocusPrompt();

                _pendingCrash = null;

                Debug.WriteLine("[ClearSession] Finally: SetStatusAsync(Idle)");
                await SetStatusAsync(AppStatus.Idle, false);
                Debug.WriteLine("[ClearSession] Finally: DONE");
            }
        }

        public void ApplyState(
            Action<ApplicationState> mutate,
            string title = "Change",
            string description = "",
            bool skipIfUnchanged = true)
        {
            mutate(AppState);
            _undoRedo.CommitChange(title, description, skipIfUnchanged);
        }

        // Diese auch.
        public void ApplyStateNonUndoable(
            Action<ApplicationState> mutate,
            bool clearHistory = true,
            string title = "Session reset",
            string description = "")
        {
            mutate(AppState);

            if (clearHistory)
                _undoRedo.ResetToCurrentState(title, description);
        }

        public async Task ApplyManualCodeEditAsync(string newCode)
        {
            await RunSharedChangeAsync(StateKinds.Edit, "editing the code", false, async () =>
            {
                await ApplyManualCodeEditCoreAsync(newCode);
                return true;
            });
        }

        private async Task ApplyManualCodeEditCoreAsync(string newCode)
        {
            if (CurrentStatus != AppStatus.Idle) return;

            try
            {
                ChildProcessService.HideChild();
                await SetStatusAsync(AppStatus.Compiling, true, "Compiling manual edit...");

                ApplyState(s => s.LastCode = newCode, "Manual code edit");

                VirtualProjectFiles?.UpdateFileContent("./currentcode.cs", newCode);

                if (_promptToDllSession != null)
                    _promptToDllSession.State.CurrentCode = newCode;

                await CompileAndShowAsync(VirtualProjectFiles!.GetSourceCodeAsStrings());
            }
            catch (Exception ex)
            {
                Logger.LogMessage($"Compilation failed: {ex.Message}", BubbleType.CompletionError);
                Logger.LogMessage("Asking AI to fix the errors...", BubbleType.Info);

                // Hand off to the AI with the compile errors
                LastErrorMsg = ex.Message;
                await SetStatusAsync(AppStatus.Generating, true, "Generating fix...");
                await ExecuteGeneralPrompt("Please fix the compilation errors in the current code.", _cancellationSource.Token);
            }
            finally
            {
                ChildProcessService.ShowChild();
                _pendingCrash = null;
                await SetStatusAsync(AppStatus.Idle, false);
            }
        }

        public async Task<bool> RecompileStateAsync(string code) // Jetzt ohne Status-Management
        {
            var changesToTest = new Dictionary<string, string>
            {
                { "./currentcode.cs", code }
            };

            bool ok = await CompileAndShowAsync(VirtualProjectFiles.GetSourceCodeAsStrings(changesToTest));
            if (ok)
                VirtualProjectFiles.UpdateFileContent("./currentcode.cs", code);

            return ok;
        }
    }
}
