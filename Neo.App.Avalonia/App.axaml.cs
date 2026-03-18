using System;
using System.Diagnostics;
using System.Threading.Tasks;

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
