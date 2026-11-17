using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

namespace Neo.Stage;

/// <summary>
/// Neo.Stage has no window of its own: a stage over the screen and an icon in the notification area.
/// <list type="bullet">
///   <item>no arguments: the icon, and the dry run once after start (again from the icon);</item>
///   <item>--once: the dry run once, then the program ends;</item>
///   <item>--frames &lt;folder&gt;: moments of the dry run as pictures, nothing on the screen.</item>
/// </list>
/// </summary>
public partial class App : Application
{
    private StageOverlay? _stage;
    private bool _playing;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var arguments = desktop.Args ?? [];
            if (Argument(arguments, "--frames") is { } folder)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    KeyFrames.Render(folder);
                    desktop.Shutdown();
                });
            }
            else
            {
                var once = arguments.Contains("--once");
                if (!once)
                    AddTrayIcon(desktop);
                Dispatcher.UIThread.Post(async () =>
                {
                    await Task.Delay(1200);
                    await PlayAsync();
                    if (once)
                        desktop.Shutdown();
                });
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>What the stage does, into the file NEO_STAGE_LOG names (nothing when it is not set).</summary>
    internal static void Log(string line)
    {
        if (Environment.GetEnvironmentVariable("NEO_STAGE_LOG") is not { Length: > 0 } path)
            return;
        try { File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss.fff} {line}{Environment.NewLine}"); }
        catch (IOException) { }
    }

    private static string? Argument(string[] arguments, string name)
    {
        var index = Array.IndexOf(arguments, name);
        return index >= 0 && index + 1 < arguments.Length ? arguments[index + 1] : null;
    }

    /// <summary>The dry run on the primary screen, over the windows that are open there.</summary>
    private async Task PlayAsync()
    {
        if (_playing)
            return;
        _playing = true;
        try
        {
            var stage = _stage ??= new StageOverlay(PrimaryScreen());
            Log($"stage over {stage.ScreenBounds} at scaling {stage.Scaling}, {stage.StageSize}");
            var run = Prepare(stage);
            stage.Display(StageFrame.Empty);
            stage.Show();
            Log($"shown, handle {stage.Handle}");

            var started = Stopwatch.GetTimestamp();
            var frames = 0;
            var done = new TaskCompletionSource();
            void Tick(TimeSpan _)
            {
                var time = Stopwatch.GetElapsedTime(started).TotalSeconds;
                stage.Display(run.Frame(time));
                frames++;
                if (time < DryRun.Duration)
                    stage.RequestAnimationFrame(Tick);
                else
                    done.TrySetResult();
            }
            stage.RequestAnimationFrame(Tick);
            await done.Task;
            Log($"played: {frames} frames in {Stopwatch.GetElapsedTime(started).TotalSeconds:0.0} s");

            stage.Display(StageFrame.Empty);
            stage.Hide();
        }
        catch (Exception exception)
        {
            Log("the dry run failed: " + exception);
            Console.Error.WriteLine("[Neo.Stage] the dry run failed: " + exception);
        }
        finally
        {
            _playing = false;
        }
    }

    private static Screen PrimaryScreen()
    {
        var probe = new Window();
        try { return probe.Screens.Primary ?? probe.Screens.All[0]; }
        finally { probe.Close(); }
    }

    private static DryRun Prepare(StageOverlay stage)
    {
        var size = stage.StageSize;
        var windows = OperatingSystem.IsWindows() ? DesktopTargets.Find(stage.ScreenBounds) : [];
        if (windows.Count == 0)
        {
            var spot = new Rect(size.Width * 0.12, size.Height * 0.25, size.Width * 0.3, size.Height * 0.35);
            return new DryRun(size, spot, "Desktop", Opposite(size, spot), "Desktop", null);
        }

        var (first, second) = DesktopTargets.PickTwo(windows);
        Log($"{windows.Count} windows; first {first.Name} {first.Bounds}, second {second?.Name ?? "none"} {second?.Bounds}");
        var firstRect = stage.FromScreen(first.Bounds);
        var secondRect = second != null ? stage.FromScreen(second.Bounds) : Opposite(size, firstRect);
        return new DryRun(size, firstRect, Short(first.Name), secondRect, Short(second?.Name ?? "Desktop"), DesktopTargets.Picture(first));
    }

    // Where the second spot goes when there is no second window: the other side of the screen.
    private static Rect Opposite(Size size, Rect first)
    {
        var width = Math.Min(first.Width, size.Width * 0.3);
        var height = Math.Min(first.Height, size.Height * 0.35);
        var left = first.Center.X < size.Width / 2 ? size.Width * 0.6 : size.Width * 0.08;
        return new Rect(left, size.Height * 0.4, width, height);
    }

    private static string Short(string name) => name.Length <= 28 ? name : name[..27] + "…";

    private void AddTrayIcon(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var play = new NativeMenuItem("Probelauf abspielen");
        play.Click += (_, _) => _ = PlayAsync();
        var quit = new NativeMenuItem("Beenden");
        quit.Click += (_, _) => desktop.Shutdown();
        var menu = new NativeMenu();
        menu.Add(play);
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(quit);

        var icon = new TrayIcon { Icon = Icon(), ToolTipText = "N.E.O. Stage", Menu = menu };
        icon.Clicked += (_, _) => _ = PlayAsync();
        TrayIcon.SetIcons(this, [icon]);
    }

    // The ring, as the stage shows it.
    private static WindowIcon Icon()
    {
        using var bitmap = new RenderTargetBitmap(new PixelSize(64, 64), new Vector(96, 96));
        using (var context = bitmap.CreateDrawingContext())
        {
            var center = new Point(32, 32);
            context.DrawEllipse(new RadialGradientBrush
            {
                GradientStops = { new GradientStop(Palette.Listen.WithAlpha(0.6), 0), new GradientStop(Palette.Listen.WithAlpha(0), 1) },
            }, null, center, 31, 31);
            context.DrawEllipse(null, new Pen(new SolidColorBrush(Palette.Listen), 5), center, 18, 18);
            context.DrawEllipse(Brushes.White, null, center, 6, 6);
        }
        var stream = new MemoryStream();
        bitmap.Save(stream, PngBitmapEncoderOptions.Default);
        stream.Position = 0;
        return new WindowIcon(stream);
    }
}
