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
        public async Task ExportProjectAsync(string assemblyName, 
            string exportPath, 
            string selectedCreationMode,
            bool requiresFullExport, // false --> only save resx
            ExportSettings exportSettings,
            string? iconFullPath,
            bool installDesktop = false, bool installStartmenu = false)
        {
            if (CurrentStatus != AppStatus.Idle) return;

            try
            {
                ChildProcessService.HideChild();
                await SetStatusAsync(AppStatus.Exporting, false, "Exporting...");

                // 1. Daten für den Export sammeln
                var exportData = new ExportData(
                    AssemblyName: assemblyName,
                    ExportPath: exportPath,
                    RequiresFullExport: requiresFullExport,
                    ExportSettings: exportSettings,
                    SelectedCreationMode: selectedCreationMode,
                    VirtualProjectFiles: VirtualProjectFiles,
                    NuGetDlls: AppState.NuGetDlls,
                    PackageVersions: AppState.PackageVersions,
                    AdditionalDlls: AdditionalDlls,
                    AdditionalFilesToCopy: AdditionalFilesForExportCopy,
                    History: AppState.History,
                    IconFullPath: iconFullPath
                );

                // 2. Service aufrufen
                var (success, result, errorMessage) = await AppExportService.ExportAsync(exportData);

                string prefix = "Exported project to ";
                if (exportData.RequiresFullExport == false)
                    prefix = "Saved project to ";

                // 3. Ergebnis verarbeiten
                if (success && result != null)
                {
                    Logger.LogMessage(prefix + result.ExportedOrSavedPath, BubbleType.CompletionSuccess);

#if !HIDE_INTERNAL_CODE
                Logger.LogMessage(ExportWindowBaseCode.CreateBaseCodeForExport(result.AssemblyName), BubbleType.Answer);
#endif
                }
                else
                {
                    Logger.LogMessage($"Export failed: {errorMessage}", BubbleType.CompletionError);
#if !HIDE_INTERNAL_CODE
                Logger.LogMessage(AppState.LastCode, BubbleType.Answer);
                Logger.LogMessage(ExportWindowBaseCode.CreateBaseCodeForExport(assemblyName), BubbleType.Answer);
#endif
                }

                if (installDesktop == true || installStartmenu == true)
                {
                    _platformServices.InstallApplicationPerUser(
                        basePath: exportPath,
                        appName: assemblyName,
                        installStartMenu: installStartmenu,
                        installDesktop: installDesktop,
                        displayVersion: null,
                        publisher: null);
                }
            }
            finally
            {
                ChildProcessService.ShowChild();
                _pendingCrash = null;
                await SetStatusAsync(AppStatus.Idle, false);
            }
        }

        public async Task ImportProjectAsync(string assemblyPath)
        {
            // 0. Clear everything
            await ClearSessionAsync();

            // 1. Prüfen, ob die App bereit ist.
            if (CurrentStatus != AppStatus.Idle) return;

            try
            {
                // 2. Den Status setzen und show timer in child window.
                ChildProcessService.HideChild();
                await SetStatusAsync(AppStatus.Importing, false, "Importing...");

                // 3. Die eigentliche Logik
                var importResult = AppImportService.ImportFromAssembly(assemblyPath);

                if (!importResult.Success || importResult.Data == null)
                {
                    Logger.LogMessage(importResult.ErrorMessage ?? "An unknown import error occurred.", BubbleType.CompletionError);
                    return;
                }

                ResxData resxData = importResult.Data!;

                bool usePython = false;
                // Always keep UseAvalonia as-is (the host decides, not the imported project)
                bool useAvalonia = Settings.UseAvalonia;

                if (resxData.Nuget!.ContainsKey("pythonnet"))
                    usePython = true;

                _settings.UsePython = usePython;
                _settings.UseAvalonia = useAvalonia;

                SettingsModel newSettings = SettingsService.Load();
                newSettings.UsePython = usePython;
                newSettings.UseAvalonia = useAvalonia;
                SettingsService.Save(newSettings);

                HandlePythonFilesInVirtualProjectFiles(usePython);

                CrossplatformSettings cpes = new CrossplatformSettings()
                {
                    UsePython = usePython,
                    UseAvalonia = useAvalonia,
                };

                ChildProcessService.ConfigureCrossplatformSettings(cpes);
                // No need to restart — ClearSessionAsync already restarted the child.

                // NuGet-Pakete laden und anwenden
                await LoadAndApplyNugetPackages(resxData.Nuget);

                var changesToTest = new Dictionary<string, string>
                {
                    { "./currentcode.cs", resxData.Code! }
                };

                bool success = await CompileAndShowAsync( VirtualProjectFiles.GetSourceCodeAsStrings(changesToTest) );

                if (success)
                {
                    Logger.LogHistory(resxData.Code!);
                    Logger.LogMessage($"App '{Path.GetFileNameWithoutExtension(assemblyPath)}' successfully imported!", BubbleType.Answer);

                    if (!string.IsNullOrEmpty(resxData.History))
                    {
                        Logger.LogMessage("--- Imported History ---", BubbleType.Info);
                        Logger.LogMessageWithMarkdownFormating(resxData.History, BubbleType.Info);
                    }

                    // Harte Zustandsänderung mit unserer neuen, sauberen Helfermethode
                    ApplyStateNonUndoable(s =>
                    {
                        s.History = resxData.History ?? string.Empty;
                        s.LastCode = resxData.Code!;
                        // NuGetDlls und PackageVersions wurden bereits in LoadAndApplyNugetPackages aktualisiert
                        s.NuGetDlls = AppState.NuGetDlls;
                        s.PackageVersions = AppState.PackageVersions;
                    }, clearHistory: true);

                    VirtualProjectFiles.UpdateFileContent("./currentcode.cs", resxData.Code!);

                    // Sync the extracted prompt→DLL pipeline with the imported state/settings.
                    RecreatePromptToDllSession(preserveState: true);
                }
            }
            catch (Exception ex)
            {
                Logger.LogMessage($"A critical error occurred after importing: {ex.Message}", BubbleType.CompletionError);
            }
            finally
            {
                // 4. Restore child visibility and reset status.
                ChildProcessService.ShowChild();
                _pendingCrash = null;
                await SetStatusAsync(AppStatus.Idle, false);
            }
        }
    }
}
