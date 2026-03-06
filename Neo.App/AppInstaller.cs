using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Windows.Win32;
using Windows.Win32.System.Com;
using Windows.Win32.UI.Shell;

namespace Neo.App
{
    /// <summary>
    /// Per-User "Installer" für existierende EXE:
    /// - legt Startmenü/Desktop-Verknüpfungen im Benutzerprofil an
    /// - registriert einen Uninstall-Eintrag (HKCU → Apps & Features)
    /// - Deinstallation ohne Admin-Rechte (löscht Shortcuts + HKCU-Eintrag)
    /// </summary>
    public static class AppInstaller
    {
        private const string VendorPrefix = "Neo";
        private const string UninstallBasePath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

        // Manuell definierte GUIDs (ShellLink)
        private static readonly Guid CLSID_ShellLink = new("00021401-0000-0000-C000-000000000046");
        private static readonly Guid IID_IShellLinkW = new("000214F9-0000-0000-C000-000000000046");

        // ===========================
        // PUBLIC API
        // ===========================

        /// <summary>
        /// Führt eine reine per-User-"Installation" aus:
        /// - Shortcuts anlegen
        /// - Uninstall-Eintrag unter HKCU anlegen
        /// </summary>
        public static void InstallApplicationPerUser(
            string basePath,
            string appName,
            bool installStartMenu = false,
            bool installDesktop = false,
            string? displayVersion = null,
            string? publisher = null)
        {
            if (string.IsNullOrWhiteSpace(appName))
                throw new ArgumentNullException(nameof(appName));
            if (string.IsNullOrWhiteSpace(basePath))
                throw new ArgumentNullException(nameof(basePath));

            string appExePath = Path.Combine(basePath, appName, appName + ".exe");
            
            if (string.IsNullOrWhiteSpace(appExePath))
                throw new ArgumentNullException(nameof(appExePath));
            if (!File.Exists(appExePath))
                throw new FileNotFoundException("Executeable not found: ", appExePath);

            // 1) Shortcuts im Benutzerprofil
            if (installStartMenu)
            {
                string startMenuLink = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                    "Programs", VendorPrefix,
                    $"{appName}.lnk");
                CreateShortcut(appExePath, startMenuLink);
            }

            if (installDesktop)
            {
                string desktopLink = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                    $"{appName}.lnk");
                CreateShortcut(appExePath, desktopLink);
            }

            //// 2) Uninstall-Eintrag (HKCU)
            //string keyName = BuildDeterministicKeyName(appName);
            //string uninstallString = $"\"{appExePath}\" --uninstall";
            //string installLocation = Path.GetDirectoryName(appExePath) ?? AppDomain.CurrentDomain.BaseDirectory;
            //string displayIcon = appExePath;

            //long estimatedKb = 0;
            //try
            //{
            //    var fi = new FileInfo(appExePath);
            //    estimatedKb = Math.Max(1, fi.Length / 1024);
            //}
            //catch { /* ignore */ }

            //RegisterUninstallEntryHKCU(
            //    keyName: keyName,
            //    displayName: appName,
            //    uninstallCommand: uninstallString,
            //    installLocation: installLocation,
            //    displayIcon: displayIcon,
            //    publisher: string.IsNullOrWhiteSpace(publisher) ? Environment.UserName : publisher,
            //    displayVersion: displayVersion,
            //    estimatedSizeInKb: estimatedKb);

            //// 3) .installinfo ablegen, damit --uninstall den exakten Key kennt
            //WriteInstallInfoFile(installLocation, keyName);
        }

        /// <summary>
        /// Wird typischerweise von deiner EXE mit dem Argument "--uninstall" aufgerufen.
        /// Entfernt Shortcuts + HKCU-Uninstall-Eintrag. Keine Admin-Rechte nötig.
        /// </summary>
        public static void PerformUninstallPerUser(string appExePath, string appName, bool removeDesktop = true)
        {
            if (string.IsNullOrWhiteSpace(appExePath))
                throw new ArgumentNullException(nameof(appExePath));
            if (string.IsNullOrWhiteSpace(appName))
                throw new ArgumentNullException(nameof(appName));

            string installLocation = Path.GetDirectoryName(appExePath) ?? AppDomain.CurrentDomain.BaseDirectory;

            // 1) Shortcuts löschen (Benutzerprofil)
            UninstallShortcuts(appName, removeFromStartMenu: true, removeFromDesktop: removeDesktop, throwOnError: false);

            //// 2) HKCU\Uninstall-Eintrag entfernen (präzise via .installinfo)
            //string? keyName = ReadInstallInfoFile(installLocation);
            //if (string.IsNullOrWhiteSpace(keyName))
            //    keyName = BuildDeterministicKeyName(appName); // Fallback

            //UnregisterUninstallEntryHKCU(keyName!);

            //// 3) .installinfo aufräumen
            //TryDeleteInstallInfoFile(installLocation);
        }

        // ===========================
        // SHORTCUTS
        // ===========================

        /// <summary>
        /// Legt eine .lnk-Verknüpfung an (per-User Pfade).
        /// </summary>
        private static unsafe void CreateShortcut(string targetPath, string shortcutPath)
        {
            if (string.IsNullOrWhiteSpace(targetPath))
                throw new ArgumentNullException(nameof(targetPath));

            Directory.CreateDirectory(Path.GetDirectoryName(shortcutPath)!);

            // COM initialisieren
            PInvoke.CoInitializeEx(null, COINIT.COINIT_APARTMENTTHREADED);
            try
            {
                // ShellLink-Objekt erstellen
                PInvoke.CoCreateInstance(
                    in CLSID_ShellLink,
                    null,
                    CLSCTX.CLSCTX_INPROC_SERVER,
                    in IID_IShellLinkW,
                    out var shellLinkObj).ThrowOnFailure();

                var shellLink = (IShellLinkW)shellLinkObj;

                // Ziel setzen (Pfad)
                shellLink.SetPath(targetPath);

                // Optional: Arbeitsverzeichnis setzen
                string? workDir = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrEmpty(workDir))
                {
                    // IShellLinkW.SetWorkingDirectory exists on interface; invoke via COM vtable:
                    // CsWin32 generiert normalerweise SetWorkingDirectory:
                    shellLink.SetWorkingDirectory(workDir);
                }

                // Verknüpfung speichern
                ((IPersistFile)shellLink).Save(shortcutPath, false);
            }
            finally
            {
                PInvoke.CoUninitialize();
            }
        }

        /// <summary>
        /// Löscht per-User Startmenü-/Desktop-Shortcuts.
        /// </summary>
        public static void UninstallShortcuts(
            string appName,
            bool removeFromStartMenu = true,
            bool removeFromDesktop = true,
            bool throwOnError = false)
        {
            try
            {
                if (removeFromStartMenu)
                {
                    string startMenuPath = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                        "Programs",
                        VendorPrefix,
                        $"{appName}.lnk");
                    SafeDeleteFile(startMenuPath, throwOnError);
                }

                if (removeFromDesktop)
                {
                    string desktopPath = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                        $"{appName}.lnk");
                    SafeDeleteFile(desktopPath, throwOnError);
                }
            }
            catch when (!throwOnError)
            {
                // Fehler stillschweigend ignorieren
            }
        }

        private static void SafeDeleteFile(string path, bool throwOnError)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException ||
                                       ex is IOException ||
                                       ex is System.Security.SecurityException)
            {
                if (throwOnError)
                    throw new InvalidOperationException($"Fehler beim Löschen: {path}", ex);
            }
        }

        // ===========================
        // UNINSTALL (HKCU)
        // ===========================

        private static void RegisterUninstallEntryHKCU(
            string keyName,
            string displayName,
            string uninstallCommand,
            string? installLocation,
            string? displayIcon,
            string? publisher,
            string? displayVersion,
            long? estimatedSizeInKb)
        {
            using var baseKey = Registry.CurrentUser.CreateSubKey(UninstallBasePath);
            if (baseKey is null)
                throw new InvalidOperationException("Konnte HKCU-Uninstall-Basis nicht öffnen/anlegen.");

            using var sub = baseKey.CreateSubKey(keyName);
            if (sub is null)
                throw new InvalidOperationException($"Konnte HKCU-Uninstall-Subkey '{keyName}' nicht anlegen.");

            sub.SetValue("DisplayName", displayName, RegistryValueKind.String);
            sub.SetValue("UninstallString", uninstallCommand, RegistryValueKind.String);

            if (!string.IsNullOrWhiteSpace(displayIcon))
                sub.SetValue("DisplayIcon", displayIcon, RegistryValueKind.String);
            if (!string.IsNullOrWhiteSpace(publisher))
                sub.SetValue("Publisher", publisher, RegistryValueKind.String);
            if (!string.IsNullOrWhiteSpace(displayVersion))
                sub.SetValue("DisplayVersion", displayVersion, RegistryValueKind.String);
            if (!string.IsNullOrWhiteSpace(installLocation))
                sub.SetValue("InstallLocation", installLocation, RegistryValueKind.String);
            if (estimatedSizeInKb.HasValue)
                sub.SetValue("EstimatedSize", (int)Math.Max(0, estimatedSizeInKb.Value), RegistryValueKind.DWord);

            // Keine Modify-/Repair-Schaltfläche anzeigen
            sub.SetValue("NoModify", 1, RegistryValueKind.DWord);
            sub.SetValue("NoRepair", 1, RegistryValueKind.DWord);

            // Installationsdatum YYYYMMDD
            sub.SetValue("InstallDate", DateTime.UtcNow.ToString("yyyyMMdd"), RegistryValueKind.String);
        }

        private static void UnregisterUninstallEntryHKCU(string keyName)
        {
            using var baseKey = Registry.CurrentUser.OpenSubKey(UninstallBasePath, writable: true);
            if (baseKey == null) return;

            try
            {
                baseKey.DeleteSubKeyTree(keyName, throwOnMissingSubKey: false);
            }
            catch
            {
                // still ignore
            }
        }

        // ===========================
        // INSTALLINFO (.installinfo)
        // ===========================

        private static void WriteInstallInfoFile(string installLocation, string keyName)
        {
            try
            {
                string path = Path.Combine(installLocation, GetInstallInfoFileName(keyName: null));
                File.WriteAllText(path, keyName);
            }
            catch
            {
                // nicht kritisch
            }
        }

        private static string? ReadInstallInfoFile(string installLocation)
        {
            try
            {
                string path = Path.Combine(installLocation, GetInstallInfoFileName(keyName: null));
                if (File.Exists(path))
                    return File.ReadAllText(path).Trim();
            }
            catch
            {
                // ignore
            }
            return null;
        }

        private static void TryDeleteInstallInfoFile(string installLocation)
        {
            try
            {
                string path = Path.Combine(installLocation, GetInstallInfoFileName(keyName: null));
                if (File.Exists(path)) File.Delete(path);
            }
            catch
            {
                // ignore
            }
        }

        private static string GetInstallInfoFileName(string? keyName)
        {
            // Einheitlicher Dateiname – bewusst nicht vom Key abhängig, damit Fallback funktioniert
            return $"{VendorPrefix}.installinfo";
        }

        // ===========================
        // HELPERS
        // ===========================

        private static string BuildDeterministicKeyName(string appName)
        {
            return $"{VendorPrefix}.{SanitizeForRegistry(appName)}";
        }

        private static string SanitizeForRegistry(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "App";
            // Subkey-Namen sollten keine Backslashes o. ä. enthalten
            foreach (var c in Path.GetInvalidFileNameChars())
                raw = raw.Replace(c, '-');
            raw = raw.Replace('\\', '-').Replace('/', '-').Replace(':', '-').Trim();
            return string.IsNullOrWhiteSpace(raw) ? "App" : raw;
        }
    }
}

