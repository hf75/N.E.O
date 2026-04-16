namespace Neo.App
{
    /// <summary>
    /// WPF/Windows implementation of IPlatformServices.
    /// Delegates to the real Win32 implementations that live in Neo.App.
    /// </summary>
    public class WpfPlatformServices : IPlatformServices
    {
        public string GetStartupPath() =>
            System.Windows.Forms.Application.StartupPath;

        public bool InjectIcon(string exePath, string iconPath)
        {
            Win32IconInjector.InjectIcon(exePath, iconPath);
            return true;
        }

        public void InstallApplicationPerUser(string basePath, string appName,
            bool installStartMenu, bool installDesktop,
            string? displayVersion, string? publisher) =>
            AppInstaller.InstallApplicationPerUser(basePath, appName, installStartMenu, installDesktop, displayVersion, publisher);

        public void UninstallShortcuts(string appName,
            bool removeFromStartMenu, bool removeFromDesktop,
            bool throwOnError) =>
            AppInstaller.UninstallShortcuts(appName, removeFromStartMenu, removeFromDesktop, throwOnError);
    }
}
