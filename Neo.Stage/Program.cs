using System.Runtime.Versioning;
using Avalonia;

// The stage works through Windows itself: its windows, UI Automation, the shell, the search index, the sound devices.
[assembly: SupportedOSPlatform("windows")]

namespace Neo.Stage;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
