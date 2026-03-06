using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace Neo.App
{
    /// <summary>
    /// Interaktionslogik für SplashScreenWindow.xaml
    /// </summary>
    public partial class SplashScreenWindow : Window
    {
        public SplashScreenWindow()
        {
            InitializeComponent();
        }

        public void FadeOutAndClose(int durationMs = 1000)
        {
            // Erstelle eine Animation, die die Opacity (Deckkraft) des Fensters von 1 auf 0 ändert.
            var fadeOutAnimation = new DoubleAnimation
            {
                From = 1,
                To = 0.0,
                Duration = new Duration(TimeSpan.FromMilliseconds(durationMs))
            };

            // Wenn die Animation abgeschlossen ist, wird das Fenster geschlossen.
            fadeOutAnimation.Completed += (s, e) => this.Close();

            // Starte die Animation auf der Opacity-Eigenschaft des Fensters.
            this.BeginAnimation(Window.OpacityProperty, fadeOutAnimation);
        }

        public void GlitchOutAndClose(int durationMs = 1200) // Etwas längere Dauer für den Effekt
        {
            // Wir verwenden eine KeyFrame-Animation für unregelmäßige Sprünge der Deckkraft.
            var flickerAnimation = new DoubleAnimationUsingKeyFrames();
            flickerAnimation.Duration = new Duration(TimeSpan.FromMilliseconds(durationMs));

            // Die KeyFrames definieren den genauen Ablauf des Flackerns.
            // Wir verwenden "Discrete" für harte Sprünge und "Linear" für sanfte Übergänge.
            flickerAnimation.KeyFrames.Add(
                new DiscreteDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(0)))
            ); // Start: Volle Helligkeit

            flickerAnimation.KeyFrames.Add(
                new DiscreteDoubleKeyFrame(0.2, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(700)))
            ); // Erster starker Flackern nach unten

            flickerAnimation.KeyFrames.Add(
                new DiscreteDoubleKeyFrame(0.9, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(750)))
            ); // Kurzes "Erholen" des Signals

            flickerAnimation.KeyFrames.Add(
                new DiscreteDoubleKeyFrame(0.1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(900)))
            ); // Tieferer Einbruch, fast aus

            flickerAnimation.KeyFrames.Add(
                new DiscreteDoubleKeyFrame(0.4, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1000)))
            ); // Letztes Aufbäumen

            flickerAnimation.KeyFrames.Add(
                new LinearDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(durationMs)))
            ); // Finales, schnelles Ausblenden bis zum Tod

            // Wenn die Animation abgeschlossen ist, wird das Fenster geschlossen.
            flickerAnimation.Completed += (s, e) => this.Close();

            // Starte die Animation auf der Opacity-Eigenschaft des Fensters.
            this.BeginAnimation(Window.OpacityProperty, flickerAnimation);
        }

        private void Grid_MouseDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
        }
    }
}
