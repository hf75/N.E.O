using Neo.Agents;
using Newtonsoft.Json;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Reflection;
using Neo.AssemblyForge;

namespace Neo.App
{
    public record ExportSettings(CrossPlatformExport Cpe, bool UsePython, bool UseAvalonia = false);

    /// <summary>
    /// Stellt das Ergebnis eines erfolgreichen Exportvorgangs dar.
    /// </summary>
    public record ExportResult(
        string AssemblyName,
        string ExportedOrSavedPath, // Der vollständige Pfad zur erstellten .exe
        string ExportDirectory  // Das Verzeichnis, in das exportiert wurde
    );

    /// <summary>
    /// Enthält die Daten, die für einen Export benötigt werden.
    /// </summary>
    public record ExportData(
        string AssemblyName,
        string ExportPath,
        string SelectedCreationMode,
        bool RequiresFullExport,
        ExportSettings ExportSettings,
        VirtualProject VirtualProjectFiles,
        List<string> NuGetDlls,
        Dictionary<string, string> PackageVersions,
        List<string> AdditionalDlls,
        List<string> AdditionalFilesToCopy,
        string History,
        string? IconFullPath
    );

    public interface IAppExportService
    {
        /// <summary>
        /// Exportiert die aktuelle Anwendung in ein eigenständiges Verzeichnis.
        /// </summary>
        /// <param name="assemblyName">Der gewünschte Name für die .exe-Datei (ohne Endung).</param>
        /// <param name="exportData">Die für den Export notwendigen Daten aus dem ApplicationState.</param>
        /// <returns>Ein Tupel mit dem Erfolg, dem Ergebnis bei Erfolg oder einer Fehlermeldung bei Misserfolg.</returns>
        Task<(bool Success, ExportResult? Result, string? ErrorMessage)> ExportAsync(ExportData exportData);
    }

    public class AppExportService : IAppExportService
    {
        private readonly string? _coreRefPath;
        private readonly string? _desktopRefPath;

        public AppExportService(string? coreRefPath, string? desktopRefPath)
        {
            _coreRefPath = coreRefPath;
            _desktopRefPath = desktopRefPath;
        }

        public async Task<(bool Success, ExportResult? Result, string? ErrorMessage)> ExportAsync(ExportData exportData)
        {
            if (string.IsNullOrWhiteSpace(exportData.AssemblyName))
            {
                return (false, null, "The specified assembly name for the export is not valid.");
            }
            if (string.IsNullOrWhiteSpace(exportData.ExportPath))
            {
                return (false, null, "The specified export path is not valid.");
            }
            if (string.IsNullOrEmpty(_coreRefPath))
            {
                return (false, null, "Core runtime reference path is not configured.");
            }

            try
            {
                string exportPath = Path.Combine(exportData.ExportPath, exportData.AssemblyName);

                if( exportData.SelectedCreationMode == "Cancel")
                {
                    if( Directory.Exists(exportPath) )
                    {
                        return (false, null, $"The export path exists already: {exportPath}");
                    }
                }

                Directory.CreateDirectory(exportPath);

                string? compiledExePath = null;

                if( exportData.RequiresFullExport == true )
                {
                    // 1. Code kompilieren
                    compiledExePath = await CompileExportedAppAsync(exportData.AssemblyName, exportPath, exportData);
                    if (string.IsNullOrEmpty(compiledExePath))
                    {
                        return (false, null, "Compilation failed. The output path was empty.");
                    }

                    // 2. Zusätzliche Dateien kopieren
                    CopyAdditionalFiles(exportPath, exportData.AdditionalFilesToCopy);

                    if (exportData.ExportSettings.Cpe == CrossPlatformExport.NONE)
                    {
                        CopyNuggetFiles(exportPath, exportData.NuGetDlls);
                    }
                    else
                    {
                        string tmpPath = CreateAppTempDirectory();

                        await NuGetBinaryCopier.RestoreAndCopyAsync(
                                    $"net{Environment.Version.Major}.0",
                                    GetTfmString(exportData.ExportSettings.Cpe),
                                    exportData.PackageVersions,
                                    tmpPath,
                                    exportPath);

                        // make sure that the nuget downloader disposed everything
                        // Directory.Delete(temp) might fail otherwise!
                        GC.Collect();
                        GC.WaitForPendingFinalizers();

                        if (Directory.Exists(tmpPath))
                        {
                            Directory.Delete(tmpPath, recursive: true);
                        }
                    }

                    // Python kopieren
                    if (exportData.ExportSettings.UsePython == true)
                    {
                        string platformFolder = exportData.ExportSettings.Cpe switch
                        {
                            CrossPlatformExport.NONE => "win-x64",
                            CrossPlatformExport.WINDOWS => "win-x64",
                            CrossPlatformExport.LINUX => "linux-x64",
                            CrossPlatformExport.OSX => "osx-arm64",
                            _ => throw new NotImplementedException($"Python export for {exportData.ExportSettings.Cpe} is not supported"),
                        };

                        // Try app-local first (legacy), then user-local (on-demand download)
                        string pythonSource = Path.Combine(Application.StartupPath, "python", platformFolder);
                        if (!Directory.Exists(pythonSource))
                        {
                            // Download target platform Python if not cached yet
                            if (!PythonDownloadService.IsPythonInstalledForPlatform(platformFolder))
                            {
                                await PythonDownloadService.DownloadAndExtractForPlatformAsync(platformFolder);
                            }
                            pythonSource = PythonDownloadService.GetPythonRootPathForPlatform(platformFolder);
                        }

                        PythonArchiveHelper.CopyUnpacked(pythonSource,
                            Path.Combine(exportPath, "python", platformFolder));
                    }

                    // 3. Icon injizieren (falls vorhanden)
                    if (!string.IsNullOrEmpty(exportData.IconFullPath))
                    {
                        Win32IconInjector.InjectIcon(compiledExePath, exportData.IconFullPath);
                    }
                }

                // 4. Metadaten (.resx) schreiben
                string resxFile = WriteResxData(exportPath, exportData);

                if( exportData.RequiresFullExport == false )
                    compiledExePath = resxFile;

                var result = new ExportResult(exportData.AssemblyName, compiledExePath!, exportPath);
                return (true, result, null);
            }
            catch (Exception ex)
            {
                return (false, null, $"An unexpected error occurred during export: {ex.Message}");
            }
        }

        string GetTfmString(CrossPlatformExport cpe)
        {
            switch (cpe)
            {
                case CrossPlatformExport.WINDOWS:
                    return "win-x64";
                case CrossPlatformExport.LINUX:
                    return "linux-x64";
                case CrossPlatformExport.OSX:
                    return "osx-arm64";
                default:
                    throw new NotImplementedException("Cpe value is not supported!");
            }
        }

        private string CreateAppTempDirectory()
        {
            string baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            string appRoot = Path.Combine(baseDir, "Neo", "Temp");
            string tempDir = Path.Combine(appRoot, Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(tempDir);

            return tempDir;
        }

        private async Task<string?> CompileExportedAppAsync(string assemblyName, string exportPath, ExportData data)
        {
            var agent = new CSharpCompileAgent();

            List<string> codes = data.VirtualProjectFiles.GetSourceCodeAsStrings();
            
            if (data.ExportSettings.UseAvalonia)
                codes.Add(ExportWindowBaseCode.CreateBaseCodeForExportAvalonia(assemblyName));
            else
                codes.Add(ExportWindowBaseCode.CreateBaseCodeForExport(assemblyName));

            // Select AppHost template: explicit target if set, otherwise current OS
            string appHostApp;
            if (data.ExportSettings.Cpe == CrossPlatformExport.LINUX)
                appHostApp = "apphost-template-linux";
            else if (data.ExportSettings.Cpe == CrossPlatformExport.OSX)
                appHostApp = "apphost-template-osx";
            else if (data.ExportSettings.Cpe == CrossPlatformExport.NONE && OperatingSystem.IsLinux())
                appHostApp = "apphost-template-linux";
            else if (data.ExportSettings.Cpe == CrossPlatformExport.NONE && OperatingSystem.IsMacOS())
                appHostApp = "apphost-template-osx";
            else
                appHostApp = "apphost-template-windows.exe";

            var dllPaths = new List<string> { _coreRefPath! };
            if (!string.IsNullOrEmpty(_desktopRefPath))
                dllPaths.Add(_desktopRefPath);

            string appNamePostfix = "App";
            if (data.ExportSettings.Cpe != CrossPlatformExport.NONE)
                appNamePostfix = "Program";

            agent.SetOption("CoreDllPath", dllPaths);
            agent.SetOption("NugetPackageVersions", data.PackageVersions);

            // Only include agent DLLs if the user's code actually references them.
            var additionalDlls = data.AdditionalDlls;
            var allCode = string.Join("\n", codes);
            if (!allCode.Contains("Neo.Agents"))
            {
                additionalDlls = additionalDlls
                    .Where(dll => !Path.GetFileName(dll).StartsWith("Neo.Agents.", StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            // Avalonia apps use CONSOLE → Microsoft.NETCore.App in runtimeconfig (all platforms).
            // WPF apps on Windows use WINDOWS → Microsoft.WindowsDesktop.App (no console window).
            string compileType = data.ExportSettings.UseAvalonia ? "CONSOLE" : "WINDOWS";


            agent.SetInput("Code", codes);
            agent.SetInput("ForceNamespace", "Neo");
            agent.SetInput("OutputPath", exportPath);
            agent.SetInput("AssemblyName", assemblyName);
            agent.SetInput("NuGetDlls", data.NuGetDlls);
            agent.SetInput("CompileType", compileType);
            agent.SetInput("AdditionalDlls", additionalDlls);
            agent.SetInput("MainTypeName", "Neo." + appNamePostfix);
            agent.SetInput("AppHostApp", Path.Combine(AppContext.BaseDirectory, "AppHostTemplates", appHostApp));

            await agent.ExecuteAsync();

            return agent.GetOutput<string>("CompiledPath");
        }

        private void CopyNuggetFiles(string exportPath, List<string> additionalFiles)
        {
            // Kopiere explizit angegebene Dateien
            foreach (var file in additionalFiles)
            {
                if (File.Exists(file))
                {
                    string destPath = Path.Combine(exportPath, Path.GetFileName(file));
                    File.Copy(file, destPath, overwrite: true);
                }
            }
        }

        private void CopyAdditionalFiles(string exportPath, List<string> additionalFiles)
        {
            string? sourceDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            if (string.IsNullOrEmpty(sourceDirectory)) return;

            // Kopiere explizit angegebene Dateien
            foreach (var file in additionalFiles)
            {
                string sourcePath = Path.Combine(sourceDirectory, file);
                if (File.Exists(sourcePath))
                {
                    string destPath = Path.Combine(exportPath, Path.GetFileName(file));
                    File.Copy(sourcePath, destPath, overwrite: true);
                }
            }
        }

        private string WriteResxData(string exportPath, ExportData data)
        {
            string? outputDir = exportPath;
            string? baseName = data.AssemblyName;

            if (string.IsNullOrEmpty(outputDir) || string.IsNullOrEmpty(baseName)) return string.Empty;

            var resxData = new ResxData
            {
                Version = 2,
                Code = data.VirtualProjectFiles.GetFileContent("./currentcode.cs") ,
                Nuget = data.PackageVersions,
                History = data.History
            };

            string resxFilePath = Path.Combine(outputDir, $"{baseName}.resx");
            File.WriteAllText(resxFilePath, JsonConvert.SerializeObject(resxData));
        
            return resxFilePath;
        }
    }
}
