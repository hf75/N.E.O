using System;
using System.Threading.Tasks;

using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;

namespace Neo.App
{
    public partial class SplashScreenWindow : Window
    {
        public SplashScreenWindow()
        {
            InitializeComponent();
        }

        public void FadeOutAndClose(int durationMs = 1000)
        {
            _ = FadeOutAndCloseAsync(durationMs);
        }

        public void GlitchOutAndClose(int durationMs = 1200)
        {
            _ = GlitchOutAndCloseAsync(durationMs);
        }

        private async Task FadeOutAndCloseAsync(int durationMs)
        {
            try
            {
                // Simple fade via opacity steps
                int steps = 20;
                int delay = durationMs / steps;
                for (int i = steps; i >= 0; i--)
                {
                    Opacity = (double)i / steps;
                    await Task.Delay(delay);
                }
            }
            catch { }
            finally
            {
                Close();
            }
        }

        private async Task GlitchOutAndCloseAsync(int durationMs)
        {
            try
            {
                // Glitch flicker effect
                await Task.Delay(durationMs * 7 / 12);
                Opacity = 0.2;
                await Task.Delay(durationMs / 20);
                Opacity = 0.9;
                await Task.Delay(durationMs / 8);
                Opacity = 0.1;
                await Task.Delay(durationMs / 12);
                Opacity = 0.4;

                // Final fade
                int fadeSteps = 10;
                int fadeDelay = (durationMs / 6) / fadeSteps;
                for (int i = 4; i >= 0; i--)
                {
                    Opacity = (double)i / 10;
                    await Task.Delay(fadeDelay);
                }
            }
            catch { }
            finally
            {
                Close();
            }
        }

        private void Grid_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            e.Handled = true;
        }
    }
}
