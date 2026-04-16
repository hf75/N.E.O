using System.IO;
using Neo.App;

namespace Neo.App.Services
{
    public interface IAppInstallerService
    {
        /// <summary>
        /// Exportiert und installiert eine Anwendung.
        /// </summary>
        /// <param name="assemblyName">Der Name der zu installierenden Anwendung.</param>
        /// <param name="installDesktop">Gibt an, ob eine Verknüpfung auf dem Desktop erstellt werden soll.</param>
        /// <param name="installStartMenu">Gibt an, ob eine Verknüpfung im Startmenü erstellt werden soll.</param>
        /// <param name="exportData">Die für den Export notwendigen Daten.</param>
        /// <returns>Ein Tupel mit Erfolg, dem Pfad zur installierten .exe und einer Fehlermeldung.</returns>
        Task<(bool Success, string? InstalledExePath, string? ErrorMessage)> InstallAsync(
            string assemblyName,
            bool installDesktop,
            bool installStartMenu,
            ExportData exportData);

        /// <summary>
        /// Deinstalliert eine Anwendung durch Entfernen ihrer Verknüpfungen.
        /// </summary>
        /// <param name="appName">Der Name der zu deinstallierenden Anwendung.</param>
        /// <returns>Ein Tupel mit Erfolg und einer Fehlermeldung.</returns>
        (bool Success, string? ErrorMessage) Uninstall(string appName);
    }

    public class AppInstallerService : IAppInstallerService
    {
        private readonly IAppExportService _appExportService;
        private readonly IPlatformServices _platform;

        public AppInstallerService(IAppExportService appExportService, IPlatformServices platformServices)
        {
            _appExportService = appExportService;
            _platform = platformServices ?? throw new ArgumentNullException(nameof(platformServices));
        }

        public async Task<(bool Success, string? InstalledExePath, string? ErrorMessage)> InstallAsync(
            string assemblyName,
            bool installDesktop,
            bool installStartMenu,
            ExportData exportData)
        {
            // 1. Anwendung exportieren
            var (exportSuccess, exportResult, exportError) = await _appExportService.ExportAsync(exportData);

            if (!exportSuccess || exportResult == null)
            {
                return (false, null, $"Installation failed because the export step failed: {exportError}");
            }

            // 2. Verknüpfungen erstellen (Installation)
            try
            {
                _platform.InstallApplicationPerUser(
                    basePath: exportData.ExportPath,
                    appName: exportResult.AssemblyName,
                    installStartMenu: installStartMenu,
                    installDesktop: installDesktop, 
                    displayVersion: "1.0.0", 
                    publisher: "Neo"
                );

                return (true, exportResult.ExportedOrSavedPath, null);
            }
            catch (Exception ex)
            {
                return (false, null, $"Failed to create shortcuts: {ex.Message}");
            }
        }

        public (bool Success, string? ErrorMessage) Uninstall(string appName)
        {
            try
            {
                _platform.UninstallShortcuts(
                    appName: appName,
                    removeFromStartMenu: true,
                    removeFromDesktop: true,
                    throwOnError: false);

                return (true, null);
            }
            catch (Exception ex)
            {
                return (false, $"An unexpected error occurred during uninstall: {ex.Message}");
            }

            //try
            //{
            //    // Die statische AppInstaller-Klasse wird hier direkt verwendet.
            //    // throwOnError ist false, um Fehler hier abzufangen und als ErrorMessage zurückzugeben.
            //    AppInstaller.PerformUninstallPerUser(
            //        appName: appName,
            //        removeFromStartMenu: true,
            //        removeFromDesktop: true,
            //        throwOnError: false // Wichtig, damit wir die Kontrolle behalten
            //    );

            //    // Da Uninstall keine Fehler wirft (throwOnError=false), gehen wir von Erfolg aus.
            //    // Eine robustere Implementierung könnte prüfen, ob die Links wirklich weg sind.
            //    return (true, null);
            //}
            //catch (Exception ex)
            //{
            //    // Sollte dank throwOnError=false nicht erreicht werden, aber als Sicherheitsnetz.
            //    return (false, $"An unexpected error occurred during uninstall: {ex.Message}");
            //}
        }
    }
}
