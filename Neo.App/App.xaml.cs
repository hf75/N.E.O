using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace Neo.App
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : System.Windows.Application
    {
        private const int MINIMUM_SPLASH_TIME_MS = 4500;

        protected override async void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            try
            {
                // Run splash on its own STA thread so its animation is independent
                // of the main UI thread. MainWindow construction blocks the Dispatcher,
                // which would freeze the animation if both shared one thread.
                var splashTcs = new TaskCompletionSource<(SplashScreenWindow Splash, System.Windows.Threading.Dispatcher Dispatcher)>();

                var splashThread = new Thread(() =>
                {
                    try
                    {
                        var s = new SplashScreenWindow();
                        s.Closed += (_, _) => System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown();
                        s.Show();
                        splashTcs.SetResult((s, System.Windows.Threading.Dispatcher.CurrentDispatcher));
                        System.Windows.Threading.Dispatcher.Run();
                    }
                    catch (Exception ex)
                    {
                        splashTcs.TrySetException(ex);
                    }
                });
                splashThread.SetApartmentState(ApartmentState.STA);
                splashThread.IsBackground = true;
                splashThread.Start();

                var (splash, splashDispatcher) = await splashTcs.Task;

                await Task.Delay(MINIMUM_SPLASH_TIME_MS);

                var mainWindow = new MainWindow();
                this.MainWindow = mainWindow;
                mainWindow.Show();

                _ = splashDispatcher.BeginInvoke(() => splash.GlitchOutAndClose());
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[OnStartup] Fatal error: {ex}");
                System.Windows.MessageBox.Show($"Failed to start application: {ex.Message}", "Startup Error", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(1);
            }
        }
    }
}
