using System;
using System.Diagnostics;
using System.Threading.Tasks;

using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace Neo.App.Avalonia
{
    public partial class App : global::Avalonia.Application
    {
        private const int MINIMUM_SPLASH_TIME_MS = 4500;

        public override void Initialize()
        {
            AvaloniaXamlLoader.Load(this);
        }

        public override async void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                // A living app: this executable was exported with a project next to it (or was
                // started with --play). The host stays hidden behind the app window.
                var player = PlayerProject.Detect(desktop.Args ?? Array.Empty<string>(), AppContext.BaseDirectory);
                if (player != null)
                {
                    // Not the lifetime's MainWindow: the lifetime shows that one after start-up,
                    // and this one has to stay hidden until the user asks for it.
                    desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                    try
                    {
                        var mainWindow = new Neo.App.MainWindow { Player = player };
                        await mainWindow.RunAsPlayerAsync();
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[OnStartup] Player mode failed: {ex}");
                        desktop.Shutdown(1);
                    }
                    base.OnFrameworkInitializationCompleted();
                    return;
                }

                try
                {
                    // Show splash screen
                    var splash = new Neo.App.SplashScreenWindow();
                    desktop.MainWindow = splash;
                    splash.Show();

                    await Task.Delay(MINIMUM_SPLASH_TIME_MS);

                    // Create main window
                    var mainWindow = new Neo.App.MainWindow();
                    desktop.MainWindow = mainWindow;
                    mainWindow.Show();

                    // Close splash
                    splash.GlitchOutAndClose();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[OnStartup] Fatal error: {ex}");
                    try
                    {
                        var mainWindow = new Neo.App.MainWindow();
                        desktop.MainWindow = mainWindow;
                        mainWindow.Show();
                    }
                    catch (Exception ex2)
                    {
                        Debug.WriteLine($"[OnStartup] Cannot even create MainWindow: {ex2}");
                        desktop.Shutdown(1);
                    }
                }
            }

            base.OnFrameworkInitializationCompleted();
        }
    }
}
