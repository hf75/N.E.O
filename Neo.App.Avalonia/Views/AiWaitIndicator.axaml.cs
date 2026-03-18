using System;
using System.Diagnostics;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Neo.App
{
    public partial class AiWaitIndicator : UserControl
    {
        private bool _isAnimationRunning;

        private readonly Stopwatch _elapsed = new();
        private readonly DispatcherTimer _timerTick;

        public static readonly StyledProperty<string> StatusTextProperty =
            AvaloniaProperty.Register<AiWaitIndicator, string>(
                nameof(StatusText), defaultValue: string.Empty);

        public string StatusText
        {
            get => GetValue(StatusTextProperty);
            set
            {
                SetValue(StatusTextProperty, value);
                if (StatusTextBlock != null)
                    StatusTextBlock.Text = value;
            }
        }

        public AiWaitIndicator()
        {
            InitializeComponent();

            _timerTick = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(10)
            };
            _timerTick.Tick += OnTimerTick;
        }

        public void Start()
        {
            if (_isAnimationRunning) return;

            _elapsed.Restart();
            _timerTick.Start();
            _isAnimationRunning = true;
        }

        public void Stop()
        {
            if (!_isAnimationRunning) return;

            _elapsed.Stop();
            _timerTick.Stop();
            _isAnimationRunning = false;
        }

        private void OnTimerTick(object? sender, EventArgs e)
        {
            var ts = _elapsed.Elapsed;
            if (TimerText != null)
                TimerText.Text = $"{ts.Minutes:D2}:{ts.Seconds:D2}.{ts.Milliseconds / 10:D2}";
        }
    }
}
