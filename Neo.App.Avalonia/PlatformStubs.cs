// Platform stubs for types that exist in the WPF project (Neo.App) but use WPF/Win32 APIs.
// These provide no-op or minimal implementations for the Avalonia host.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace Neo.App
{
    /// <summary>
    /// Stub replacement for the WPF AppInstaller which uses Win32 shell APIs.
    /// </summary>
    public static class AppInstaller
    {
        public static void InstallApplicationPerUser(
            string basePath,
            string appName,
            bool installStartMenu = false,
            bool installDesktop = false,
            string? displayVersion = null,
            string? publisher = null)
        {
            Debug.WriteLine($"[AppInstaller] InstallApplicationPerUser stub. AppName: {appName}");
        }

        public static void UninstallShortcuts(
            string appName,
            bool removeFromStartMenu = true,
            bool removeFromDesktop = true,
            bool throwOnError = false)
        {
            Debug.WriteLine($"[AppInstaller] UninstallShortcuts stub. AppName: {appName}");
        }
    }

    /// <summary>
    /// Stub for Win32IconInjector used by AppExportService.
    /// </summary>
    public static class Win32IconInjector
    {
        public static bool InjectIcon(string exePath, string icoPath)
        {
            Debug.WriteLine("[Win32IconInjector] Stub: InjectIcon not available on this platform.");
            return false;
        }
    }

    /// <summary>
    /// Shim for System.Windows.Forms.Application.StartupPath used by AppExportService.
    /// Named Application to match the unqualified reference in the linked source files.
    /// NOTE: This causes a name collision with Avalonia.Application. The App.axaml.cs
    /// must use fully-qualified `Avalonia.Application` to resolve correctly.
    /// </summary>
    public static class Application
    {
        public static string StartupPath => AppDomain.CurrentDomain.BaseDirectory.TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
