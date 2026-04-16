namespace Neo.App
{
    /// <summary>
    /// Abstracts platform-specific operations that differ between WPF and Avalonia hosts.
    /// WPF: real Win32 implementations. Avalonia: no-ops / graceful degradation.
    /// </summary>
    public interface IPlatformServices
    {
        /// <summary>Returns the application's startup directory (exe location).</summary>
        string GetStartupPath();

        /// <summary>Injects an icon into an exported .exe file. Returns true on success.</summary>
        bool InjectIcon(string exePath, string iconPath);

        /// <summary>Creates per-user shortcuts (start menu, desktop) for an exported app.</summary>
        void InstallApplicationPerUser(string basePath, string appName,
            bool installStartMenu, bool installDesktop,
            string? displayVersion, string? publisher);

        /// <summary>Removes per-user shortcuts for an app.</summary>
        void UninstallShortcuts(string appName,
            bool removeFromStartMenu, bool removeFromDesktop,
            bool throwOnError);
    }
}
