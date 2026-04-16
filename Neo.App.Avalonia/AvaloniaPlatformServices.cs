using System;
using System.Diagnostics;
using System.IO;

namespace Neo.App
{
    /// <summary>
    /// Avalonia/cross-platform implementation of IPlatformServices.
    /// Provides no-op or graceful fallback for Windows-only features.
    /// Replaces the static stubs in PlatformStubs.cs.
    /// </summary>
    public class AvaloniaPlatformServices : IPlatformServices
    {
        public string GetStartupPath() =>
            AppDomain.CurrentDomain.BaseDirectory.TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        public bool InjectIcon(string exePath, string iconPath)
        {
            Debug.WriteLine("[AvaloniaPlatformServices] InjectIcon: not available on this platform.");
            return false;
        }

        public void InstallApplicationPerUser(string basePath, string appName,
            bool installStartMenu, bool installDesktop,
            string? displayVersion, string? publisher)
        {
            Debug.WriteLine($"[AvaloniaPlatformServices] InstallApplicationPerUser stub. AppName: {appName}");
        }

        public void UninstallShortcuts(string appName,
            bool removeFromStartMenu, bool removeFromDesktop,
            bool throwOnError)
        {
            Debug.WriteLine($"[AvaloniaPlatformServices] UninstallShortcuts stub. AppName: {appName}");
        }
    }
}
