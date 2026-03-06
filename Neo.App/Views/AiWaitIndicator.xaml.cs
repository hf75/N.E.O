using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Neo.App
{
    public partial class AiWaitIndicator : System.Windows.Controls.UserControl
    {
        private Storyboard? _blob1;
        private Storyboard? _blob2;
        private Storyboard? _blob3;
        private Storyboard? _blob4;
        private bool _isAnimationRunning;
        private readonly MainWindow? _mainWnd;

        private readonly Stopwatch _elapsed = new();
        private readonly DispatcherTimer _timerTick;

        public static readonly DependencyProperty StatusTextProperty =
            DependencyProperty.Register(
                nameof(StatusText),
                typeof(string),
                typeof(AiWaitIndicator),
                new PropertyMetadata(string.Empty));

        public string StatusText
        {
            get => (string)GetValue(StatusTextProperty);
            set => SetValue(StatusTextProperty, value);
        }

        public AiWaitIndicator()
        {
            InitializeComponent();

            _timerTick = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(10)
            };
            _timerTick.Tick += OnTimerTick;

            Loaded += AiWaitIndicator_Loaded;
        }

        public AiWaitIndicator(MainWindow mainWindow) : this()
        {
            _mainWnd = mainWindow;
        }

        private void AiWaitIndicator_Loaded(object? sender, RoutedEventArgs e)
        {
            _blob1 ??= (Storyboard)FindResource("Blob1Anim");
            _blob2 ??= (Storyboard)FindResource("Blob2Anim");
            _blob3 ??= (Storyboard)FindResource("Blob3Anim");
            _blob4 ??= (Storyboard)FindResource("Blob4Anim");

            if (!_isAnimationRunning)
            {
                StartInternal();
            }
        }

        public void Start()
        {
            _blob1 ??= (Storyboard)FindResource("Blob1Anim");
            _blob2 ??= (Storyboard)FindResource("Blob2Anim");
            _blob3 ??= (Storyboard)FindResource("Blob3Anim");
            _blob4 ??= (Storyboard)FindResource("Blob4Anim");

            if (_isAnimationRunning) return;
            StartInternal();
        }

        public void Stop()
        {
            if (!_isAnimationRunning) return;

            _blob1?.Stop(this);
            _blob2?.Stop(this);
            _blob3?.Stop(this);
            _blob4?.Stop(this);

            _elapsed.Stop();
            _timerTick.Stop();

            _isAnimationRunning = false;
        }

        private void StartInternal()
        {
            _blob1?.Begin(this, true);
            _blob2?.Begin(this, true);
            _blob3?.Begin(this, true);
            _blob4?.Begin(this, true);

            _elapsed.Restart();
            _timerTick.Start();

            _isAnimationRunning = true;
        }

        private void OnTimerTick(object? sender, EventArgs e)
        {
            var ts = _elapsed.Elapsed;
            TimerText.Text = $"{ts.Minutes:D2}:{ts.Seconds:D2}.{ts.Milliseconds / 10:D2}";
        }

        private void UserControl_Unloaded(object sender, RoutedEventArgs e)
        {
            Stop();
        }
    }
}
