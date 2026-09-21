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
        private void HandleDesignerSelectionReceived(DesignerSelectionMessage selection)
        {
            if (CurrentStatus != AppStatus.Idle)
                return;

            if (!_view.CheckUIThreadAccess())
            {
                _view.InvokeOnUIThreadAsync(() => HandleDesignerSelectionReceived(selection));
                return;
            }

            _view.ShowDesignerPropertiesWindow(selection, DesignerPropertiesWindow_ApplyRequested);
        }

        private async void DesignerPropertiesWindow_ApplyRequested(object? sender, DesignerApplyRequestedEventArgs e)
        {
            await ApplyDesignerEditsAsync(e.Selection, e.Updates);
        }

        private async Task ApplyDesignerEditsAsync(DesignerSelectionMessage selection, IReadOnlyDictionary<string, string> updates)
        {
            if (CurrentStatus != AppStatus.Idle)
                return;

            var currentCode = VirtualProjectFiles.GetFileContent("./currentcode.cs") ?? AppState.LastCode ?? string.Empty;

            if (!DesignerCodeEditor.TryApplyDesignerEdits(
                code: currentCode,
                useAvalonia: Settings.UseAvalonia,
                designId: selection.DesignId,
                updates: updates,
                updatedCode: out var newCode,
                error: out var err))
            {
                Logger.LogMessage(err ?? "Designer edit failed.", BubbleType.CompletionError);
                return;
            }

            if (string.Equals(newCode, currentCode, StringComparison.Ordinal))
                return;

            try
            {
                await SetStatusAsync(AppStatus.Compiling, false, "Applying designer change...");

                var changesToTest = new Dictionary<string, string> { { "./currentcode.cs", newCode } };
                bool compiledOk = await CompileAndShowAsync(VirtualProjectFiles.GetSourceCodeAsStrings(changesToTest));

                if (!compiledOk)
                {
                    Logger.LogMessage("Designer change did not compile; not applied.", BubbleType.CompletionError);
                    return;
                }

                string changedKeys = string.Join(", ", updates.Keys.Take(6));
                if (updates.Count > 6)
                    changedKeys += ", …";

                ApplyState(
                    s => { s.LastCode = newCode; },
                    title: $"Designer edit: {selection.DesignId}",
                    description: updates.Count <= 0 ? "Designer edit" : $"{updates.Count} change(s): {changedKeys}",
                    skipIfUnchanged: true);
                VirtualProjectFiles.UpdateFileContent("./currentcode.cs", newCode);
            }
            finally
            {
                await SetStatusAsync(AppStatus.Idle, false);
            }
        }

        public async Task<bool> SetDesignerModeAsync(bool enabled)
        {
            if (CurrentStatus != AppStatus.Idle) return false;
            if (enabled && !ChildProcessService.HasLoadedControl) return false;

            if (enabled)
            {
                bool ok = await EnsureDesignIdsForClickToEditAsync();
                if (!ok)
                    return false;
            }

            if (!enabled)
            {
                await RemoveDesignIdsAsync();
            }

            await ChildProcessService.SetDesignerModeAsync(enabled);

            if (!enabled)
            {
                _view.CloseDesignerPropertiesWindow();
            }

            return true;
        }

        private async Task<bool> EnsureDesignIdsForClickToEditAsync()
        {
            if (CurrentStatus != AppStatus.Idle)
                return false;

            var currentCode = VirtualProjectFiles.GetFileContent("./currentcode.cs") ?? AppState.LastCode ?? string.Empty;

            if (!DesignerIdInjector.TryInjectDesignIds(
                code: currentCode,
                useAvalonia: Settings.UseAvalonia,
                updatedCode: out var newCode,
                injectedCount: out var injectedCount,
                error: out var injectErr))
            {
                Logger.LogMessage(injectErr ?? "Designer ID injection failed.", BubbleType.CompletionError);
                return false;
            }

            if (injectedCount <= 0 || string.Equals(newCode, currentCode, StringComparison.Ordinal))
                return true;

            try
            {
                await SetStatusAsync(AppStatus.Compiling, false, "Applying designer IDs...");

                var changesToTest = new Dictionary<string, string> { { "./currentcode.cs", newCode } };
                bool compiledOk = await CompileAndShowAsync(VirtualProjectFiles.GetSourceCodeAsStrings(changesToTest));

                if (!compiledOk)
                {
                    Logger.LogMessage("Failed to compile with injected designer IDs.", BubbleType.CompletionError);
                    return false;
                }

                ApplyState(
                    s => { s.LastCode = newCode; },
                    title: "Inject designer IDs",
                    description: $"Injected {injectedCount} design ID(s) for click-to-edit.",
                    skipIfUnchanged: true);
                VirtualProjectFiles.UpdateFileContent("./currentcode.cs", newCode);
                Logger.LogMessage($"Injected {injectedCount} design IDs for click-to-edit.", BubbleType.Info);
                return true;
            }
            catch (Exception ex)
            {
                // Designer ID compilation may fail for complex code patterns (e.g. tuples).
                // This must not crash the app — just report and disable designer for this code.
                Logger.LogMessage($"Designer mode unavailable for this code: {ex.Message}", BubbleType.CompletionError);
                return false;
            }
            finally
            {
                await SetStatusAsync(AppStatus.Idle, false);
            }
        }

        private async Task RemoveDesignIdsAsync()
        {
            var currentCode = VirtualProjectFiles.GetFileContent("./currentcode.cs") ?? AppState.LastCode ?? string.Empty;

            if (!DesignerIdInjector.TryRemoveDesignIds(
                code: currentCode,
                updatedCode: out var cleanCode,
                removedCount: out var removedCount,
                error: out var removeErr))
            {
                Logger.LogMessage(removeErr ?? "Designer ID removal failed.", BubbleType.CompletionError);
                return;
            }

            if (removedCount <= 0 || string.Equals(cleanCode, currentCode, StringComparison.Ordinal))
                return;

            try
            {
                await SetStatusAsync(AppStatus.Compiling, false, "Removing designer IDs...");

                var changesToTest = new Dictionary<string, string> { { "./currentcode.cs", cleanCode } };
                bool compiledOk = await CompileAndShowAsync(VirtualProjectFiles.GetSourceCodeAsStrings(changesToTest));

                if (!compiledOk)
                {
                    Logger.LogMessage("Failed to compile after removing designer IDs.", BubbleType.CompletionError);
                    return;
                }

                ApplyState(
                    s => { s.LastCode = cleanCode; },
                    title: "Remove designer IDs",
                    description: $"Removed {removedCount} design ID(s).",
                    skipIfUnchanged: true);
                VirtualProjectFiles.UpdateFileContent("./currentcode.cs", cleanCode);
            }
            finally
            {
                await SetStatusAsync(AppStatus.Idle, false);
            }
        }
    }
}
