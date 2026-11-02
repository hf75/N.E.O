using Neo.IPC;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Threading.Tasks;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Neo.Shared;

namespace Neo.PluginWindowAvalonia
{
    public partial class MainWindow : Window
    {
        private AssemblyLoadContext? pluginLoadContext;

        private readonly object _pluginUnloadGate = new();
        private bool _pluginUnloadScheduled;

        // Native libraries from the plugin bundle, materialized to temp for the OS loader
        private readonly NativeLibraryMaterializer _nativeLibraries = new();

        // Tracks whether a control was ever successfully loaded
        public bool HasEverLoadedControl { get; private set; }

        // Elapsed timer for wait overlay
        private readonly Stopwatch _waitStopwatch = new();
        private DispatcherTimer? _waitTimer;

        public MainWindow()
        {
            InitializeComponent();
            InitWaitTimer();

            this.Activated += OnActivated;
            this.KeyDown += OnKeyDown;
        }

        /// <summary>
        /// The currently loaded user control, or <c>null</c> while nothing is loaded.
        /// IPC handlers (extract_code, inspect_visual_tree, Live-MCP, …) resolve against this.
        /// </summary>
        public Avalonia.Controls.Control? GetLoadedUserControl()
            => dynamicContent.Content as Avalonia.Controls.Control;

        /// <summary>
        /// Ctrl+Shift+N: the user wants the host (N.E.O.) shown or hidden. In player mode the
        /// host runs invisibly behind this window; this is how it is called up.
        /// </summary>
        public event Action? HostToggleRequested;

        /// <summary>Text shown over the app for a few seconds once it is first loaded (the host passes it); null: none.</summary>
        public string? Hint { get; set; }
        private bool _hintShown;
        private DispatcherTimer? _bannerTimer;

        private void ShowHintOnce()
        {
            if (_hintShown || string.IsNullOrWhiteSpace(Hint)) return;
            _hintShown = true;
            ShowBanner(Hint, "#D0202020", 10);
        }

        /// <summary>One line over the app: a hint, "repairing", "fixed". Goes away by itself or on click; 0 seconds: stays.</summary>
        public void ShowBanner(string text, string background, int seconds)
        {
            HintText.Text = text;
            HintBanner.Background = global::Avalonia.Media.Brush.Parse(background);
            HintBanner.IsVisible = true;
            _bannerTimer?.Stop();
            if (seconds <= 0) return;
            _bannerTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
            _bannerTimer.Tick += (_, _) => { _bannerTimer.Stop(); HintBanner.IsVisible = false; };
            _bannerTimer.Start();
        }

        public void HideBanner()
        {
            _bannerTimer?.Stop();
            HintBanner.IsVisible = false;
        }

        /// <summary>Another program is dragged over this one (its name), or not any more (null).</summary>
        /// <param name="link">It is over the half of this window where a drop links the two; the other half merges them.</param>
        public void ShowFusionTarget(string? name, bool link = false)
        {
            FusionText.Text = name == null ? "" : $"Drop to merge {name} into this app";
            LinkText.Text = name == null ? "" : $"Drop to link {name} with this app";
            ShowZone(MergeZone, MergePill, "#1565C0", on: !link);
            ShowZone(LinkZone, LinkPill, "#00796B", on: link);
            FusionOverlay.IsVisible = name != null;
        }

        // What the drop would do stands out; the app shows through both, because it is what keeps running.
        private static void ShowZone(Avalonia.Controls.Border zone, Avalonia.Controls.Border pill, string color, bool on)
        {
            zone.Background = global::Avalonia.Media.Brush.Parse(on ? "#50" + color[1..] : "#14" + color[1..]);
            zone.BorderThickness = new global::Avalonia.Thickness(on ? 4 : 1);
            pill.Opacity = on ? 1 : 0.55;
        }

        /// <summary>The chip of an app link: it says with whom, and its cross ends the link.</summary>
        public void ShowLinkChip(string id, string text, Action end)
        {
            var cross = new Avalonia.Controls.Button
            {
                Content = "✕", Padding = new global::Avalonia.Thickness(6, 0), Margin = new global::Avalonia.Thickness(6, 0, 0, 0), FontSize = 11,
                Foreground = global::Avalonia.Media.Brushes.White, Background = global::Avalonia.Media.Brushes.Transparent,
                VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center,
            };
            cross.Click += (_, _) => end();

            var row = new Avalonia.Controls.StackPanel { Orientation = global::Avalonia.Layout.Orientation.Horizontal };
            row.Children.Add(new Avalonia.Controls.TextBlock { Text = text, Foreground = global::Avalonia.Media.Brushes.White, FontSize = 12, VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center });
            row.Children.Add(cross);

            LinkChips.Children.Add(new Avalonia.Controls.Border
            {
                Name = "link-" + id, Child = row, Padding = new global::Avalonia.Thickness(10, 3, 3, 3), CornerRadius = new global::Avalonia.CornerRadius(12),
                Background = global::Avalonia.Media.Brush.Parse("#E000796B"), HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Right,
            });
        }

        public void RemoveLinkChip(string id)
        {
            var chip = LinkChips.Children.FirstOrDefault(c => c.Name == "link-" + id);
            if (chip != null) LinkChips.Children.Remove(chip);
        }

        /// <summary>What the host wants from this window.</summary>
        public void Apply(AppWindowMessage message)
        {
            if (message.Banner != null) ShowBanner(message.Banner, message.BannerColor ?? "#D0202020", message.BannerSeconds);
            if (message.Visible == false) Hide();
            if (message.Visible == true && !IsVisible) Show();
            if (message.GrowWidth > 0) Grow(message.GrowWidth, message.AtLeastHeight, message.StayInPlace);
            if (message.BringToFront) Activate();
            if (message.Close) Close();
        }

        // It took another program in: that one's room comes on top of its own, as far as the screen goes.
        private void Grow(double width, double atLeastHeight, bool stayInPlace)
        {
            var newWidth = ClientSize.Width + width;
            var newHeight = Math.Max(ClientSize.Height, atLeastHeight);
            if (Screens.ScreenFromPoint(Position) is { } screen)
            {
                var area = screen.WorkingArea;
                var scaling = screen.Scaling;
                var frame = FrameSize is { } outer ? outer - ClientSize : default;

                var beyond = Position.X + (int)((newWidth + frame.Width) * scaling) - area.Right;
                if (beyond > 0 && !stayInPlace)
                    Position = new PixelPoint(Math.Max(area.X, Position.X - beyond), Position.Y);

                newWidth = Math.Min(newWidth, (area.Right - Position.X) / scaling - frame.Width);
                newHeight = Math.Min(newHeight, (area.Bottom - Position.Y) / scaling - frame.Height);
            }
            Width = Math.Max(ClientSize.Width, newWidth);
            Height = Math.Max(ClientSize.Height, newHeight);
        }

        // ── Heal in place: which action ran into the error, and doing it again after the fix ──

        private WeakReference<Avalonia.Controls.Control>? _lastClicked;
        private DateTime _lastClickedAt;

        /// <summary>
        /// Remembers the last button the user clicked (every button, handled or not). A class handler:
        /// it runs before the app's own Click handlers, so the click is known even when the handler throws
        /// and the event never bubbles up to this window.
        /// </summary>
        public void TrackUserActions()
        {
            Avalonia.Controls.Button.ClickEvent.Raised.Subscribe(new global::Avalonia.Reactive.AnonymousObserver<(object, global::Avalonia.Interactivity.RoutedEventArgs)>(t =>
            {
                if (t.Item2.Source is Avalonia.Controls.Control c) { _lastClicked = new WeakReference<Avalonia.Controls.Control>(c); _lastClickedAt = DateTime.UtcNow; }
            }));
        }

        /// <summary>
        /// The address ("save" or "Button:2") of the button clicked just before an error was thrown —
        /// the action the host replays after the repair. Null when the error did not follow a click.
        /// </summary>
        public string? FailedActionAddress()
        {
            if (_lastClicked == null || (DateTime.UtcNow - _lastClickedAt) > TimeSpan.FromSeconds(1)) return null;
            if (!_lastClicked.TryGetTarget(out var control) || GetLoadedUserControl() is not { } root) return null;
            if (!string.IsNullOrEmpty(control.Name) && control.TemplatedParent == null) return control.Name;
            var sameType = root.GetVisualDescendants().OfType<Avalonia.Controls.Control>().Where(c => c.GetType() == control.GetType()).ToList();
            int index = sameType.IndexOf(control);
            return index >= 0 ? $"{control.GetType().Name}:{index}" : null;
        }

        /// <summary>Performs the action again on the repaired app. Returns what was replayed, or null.</summary>
        public string? ReplayAction(string address)
        {
            if (GetLoadedUserControl() is not { } root) return null;
            var target = FindControlInTree(root, address);
            if (target is not Avalonia.Controls.Button button) return null;
            button.RaiseEvent(new global::Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
            return address;
        }

        private static Avalonia.Controls.Control? FindControlInTree(Avalonia.Controls.Control root, string target)
        {
            var all = root.GetVisualDescendants().OfType<Avalonia.Controls.Control>().ToList();
            var byName = all.FirstOrDefault(c => string.Equals(c.Name, target, StringComparison.OrdinalIgnoreCase));
            if (byName != null) return byName;
            var parts = target.Split(':', 2);
            int index = parts.Length > 1 && int.TryParse(parts[1], out var i) ? i : 0;
            var byType = all.Where(c => c.GetType().Name.Equals(parts[0], StringComparison.OrdinalIgnoreCase)).ToList();
            return index < byType.Count ? byType[index] : null;
        }

        private void OnKeyDown(object? sender, global::Avalonia.Input.KeyEventArgs e)
        {
            if (e.Key == global::Avalonia.Input.Key.N
                && e.KeyModifiers.HasFlag(global::Avalonia.Input.KeyModifiers.Control)
                && e.KeyModifiers.HasFlag(global::Avalonia.Input.KeyModifiers.Shift))
            {
                HostToggleRequested?.Invoke();
                e.Handled = true;
                return;
            }

            // Escape or Ctrl+Shift+F exits fullscreen
            if (WindowState == WindowState.FullScreen)
            {
                bool isEscape = e.Key == global::Avalonia.Input.Key.Escape;
                bool isCtrlShiftF = e.Key == global::Avalonia.Input.Key.F
                    && e.KeyModifiers.HasFlag(global::Avalonia.Input.KeyModifiers.Control)
                    && e.KeyModifiers.HasFlag(global::Avalonia.Input.KeyModifiers.Shift);

                if (isEscape || isCtrlShiftF)
                {
                    WindowState = WindowState.Normal;
                    WindowDecorations = WindowDecorations.Full;
                    e.Handled = true;
                }
            }
        }

        private void InitWaitTimer()
        {
            _waitTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            _waitTimer.Tick += (_, _) =>
            {
                if (_waitStopwatch.IsRunning)
                {
                    var ts = _waitStopwatch.Elapsed;
                    WaitTimerText.Text = $"{ts.Minutes:D2}:{ts.Seconds:D2}.{ts.Milliseconds / 10:D2}";
                }
            };
        }

        public void StartWaitTimer()
        {
            _waitStopwatch.Restart();
            _waitTimer?.Start();
        }

        public void StopWaitTimer()
        {
            _waitStopwatch.Stop();
            _waitTimer?.Stop();
        }

        private async void OnActivated(object? sender, EventArgs e)
        {
            // Dein App-Typ in Avalonia:
            if (Application.Current is App app)
                await app.NotifyParentAboutActivation();
        }

        public void HandleFullscreenMouse(int val)
        {
            if (val == 0)
                InactivityCursorManager.Start(this);
            else
                InactivityCursorManager.Stop();
        }

        public void HandleChildModality(int val)
        {
            // Avalonia: Window selbst hat kein IsEnabled -> Root-Content (falls Control) (de)aktivieren
            if (val == 0)
            {
                Topmost = false;
                if (Content is Control c) c.IsEnabled = false;
            }
            else
            {
                Topmost = true;
                if (Content is Control c) c.IsEnabled = true;
            }
        }

        // ================================
        // 0) Hot reload
        // ================================
        private System.Reflection.Assembly? _pluginAssembly;

        /// <summary>
        /// Patches the running plugin in place instead of replacing it: the control instance — and
        /// with it everything the user loaded or entered — stays alive. Runs on the UI thread.
        /// Throws when the update cannot be applied; the host then falls back to a full reload.
        /// </summary>
        public string HandleHotReload(HotReloadRequest request)
        {
            var plugin = _pluginAssembly;
            if (plugin == null || dynamicContent.Content is not Avalonia.Controls.UserControl control)
                throw new InvalidOperationException("No plugin is loaded.");
            if (!HotReloadRuntime.IsSupported)
                throw new NotSupportedException(
                    $"This process was started without {HotReloadRuntime.EnvironmentVariable}={HotReloadRuntime.EnvironmentValue}.");

            // Snapshot first: once the UI is rebuilt, what the user typed or selected is gone.
            var uiState = request.RebuildUi ? UiStateSnapshot.Capture(control) : null;

            var applied = HotReloadRuntime.Apply(plugin, control, request);

            string ui = "";
            if (request.RebuildUi && !string.IsNullOrEmpty(request.RebuildMethodName))
            {
                // "void BuildUi()" assigns Content itself, "Content = BuildUI();" hands the new root back.
                var rebuild = HotReloadRuntime.Rebuild(control, request.RebuildMethodName, RoutedEventHandlers.Instance);
                if (rebuild.NewRoot is Control newRoot && !ReferenceEquals(newRoot, control))
                    control.Content = newRoot;

                int restored = uiState!.Restore(control);
                ui = $", UI rebuilt ({restored} of {uiState.Count} UI value(s) carried over{rebuild.Cleanup})";

                // Scroll extents and virtualized items settle with the next layout pass.
                Dispatcher.UIThread.Post(() => { try { uiState.Restore(control); } catch { } }, DispatcherPriority.Loaded);
            }

            HotReloadRuntime.InvokeOnHotReloaded(control);
            return applied + ui;
        }

        // ================================
        // 0b) The state of the running app as data (fusion, teleport)
        // ================================
        public AppStateImage CaptureAppState()
        {
            if (dynamicContent.Content is not Avalonia.Controls.UserControl control)
                throw new InvalidOperationException("No plugin is loaded.");
            return AppStateImage.Capture(control) with { Ui = UiStateSnapshot.Capture(control).ToPortable().ToJson() };
        }

        /// <summary>Puts the values in, builds the UI again so that it shows them, and puts back what the user did to the UI.</summary>
        public AppStateRestoredMessage RestoreAppState(AppStateImage image, RestoreAppStateRequest request)
        {
            if (dynamicContent.Content is not Avalonia.Controls.UserControl control)
                throw new InvalidOperationException("No plugin is loaded.");

            var restored = image.RestoreTo(control, request.FieldPrefix, request.KeepFields);

            if (HotReloadRuntime.FirstMethodOf(control, request.RebuildMethods) is { } buildUi)
            {
                var rebuild = HotReloadRuntime.Rebuild(control, buildUi, RoutedEventHandlers.Instance);
                if (rebuild.NewRoot is Control newRoot && !ReferenceEquals(newRoot, control))
                    control.Content = newRoot;
            }

            int uiValues = 0;
            if (request.RestoreUi && image.Ui != null && PortableUiState.FromJson(image.Ui) is { } portable)
            {
                var uiState = UiStateSnapshot.FromPortable(portable);
                uiValues = uiState.Restore(control);
                Dispatcher.UIThread.Post(() => { try { uiState.Restore(control); } catch { } }, DispatcherPriority.Loaded);
            }

            HotReloadRuntime.InvokeOnHotReloaded(control);
            return new AppStateRestoredMessage(true, restored.Values, uiValues, restored.Failed);
        }

        // ================================
        // 1) Bytes-basierter Ladeweg
        // ================================
        /// <summary>
        /// L�dt ein Avalonia-UserControl vollst�ndig aus Bytes (ohne Dateipfade).
        /// managedAssembliesByFullName: Assembly.FullName -> Bytes
        /// nativeLibrariesByBasename:   "sqlite3.dll" -> Bytes
        /// </summary>
        public void HandleLoadUserControlFromBytes(
            byte[] mainAssemblyBytes,
            string? explicitControlTypeName,
            IDictionary<string, byte[]> managedAssembliesByFullName,
            IDictionary<string, byte[]> nativeLibrariesByBasename
        )
        {
            UnloadUserControlPlugin();

            var plc = SandboxPluginLoadContext.ForBundle(
                managedAssembliesByFullName, nativeLibrariesByBasename, _nativeLibraries);

            pluginLoadContext = plc;

            using (var ms = new MemoryStream(mainAssemblyBytes, writable: false))
            {
                var pluginAssembly = plc.LoadFromStream(ms)
                    ?? throw new InvalidOperationException("Main assembly could not be loaded.");
                _pluginAssembly = pluginAssembly;

                // Typ aufl�sen
                var controlType = SandboxPluginLoadContext.FindControlType(
                    pluginAssembly, explicitControlTypeName, typeof(Avalonia.Controls.UserControl));

                if (controlType == null)
                    throw new InvalidOperationException("Kein passender Avalonia-UserControl-Typ gefunden.");

                if (!(Activator.CreateInstance(controlType) is Avalonia.Controls.UserControl userControl))
                    throw new InvalidCastException("Der erzeugte Typ ist kein Avalonia UserControl.");

                // Auf UI-Thread einhängen
                if (Dispatcher.UIThread.CheckAccess())
                {
                    dynamicContent.Content = userControl;
                    HasEverLoadedControl = true;
                    WaitOverlay.IsVisible = false;
                    StopWaitTimer();
                    ShowHintOnce();
                }
                else
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        dynamicContent.Content = userControl;
                        HasEverLoadedControl = true;
                        WaitOverlay.IsVisible = false;
                        StopWaitTimer();
                        ShowHintOnce();
                    });
                }
            }
        }

        // ==========================================================
        // 2) Unload (UI-sicher, GC-Pump)
        // ==========================================================
        public void UnloadUserControlPlugin()
        {
            lock (_pluginUnloadGate)
            {
                if (_pluginUnloadScheduled) return;
                _pluginUnloadScheduled = true;
            }

            if (!Dispatcher.UIThread.CheckAccess())
            {
                Dispatcher.UIThread.Post(UnloadUserControlPlugin);
                return;
            }

            // What this version watches and docks to (ForeignApp, compiled into the app) ends with it.
            AppDomain.CurrentDomain.SetData("Neo.ForeignApp.AppVersion", new object());

            try
            {
                if (dynamicContent.Content is Avalonia.Controls.UserControl)
                    dynamicContent.Content = null;
                WaitOverlay.IsVisible = true;
                WaitStatusText.Text = "Generating new code...";
                StartWaitTimer();
            }
            catch
            {
                // UI-Fehler hier niemals weiterblasen
            }

            AssemblyLoadContext? oldPlc;
            lock (_pluginUnloadGate)
            {
                oldPlc = pluginLoadContext;
                pluginLoadContext = null;
                _pluginAssembly = null;
            }

            _ = Task.Run(() =>
            {
                try { (oldPlc as SandboxPluginLoadContext)?.Dispose(); } catch (Exception ex) { Debug.WriteLine($"[UnloadPlugin] Dispose failed: {ex.Message}"); }
                // Not collectible when this process can hot reload (see SandboxPluginLoadContext):
                // the old plugin then stays loaded, and only its UI and objects are released.
                try { if (oldPlc?.IsCollectible == true) oldPlc.Unload(); } catch (Exception ex) { Debug.WriteLine($"[UnloadPlugin] Unload failed: {ex.Message}"); }

                try
                {
                    for (int i = 0; i < 2; i++)
                    {
                        GC.Collect();
                        GC.WaitForPendingFinalizers();
                    }
                }
                catch (Exception ex) { Debug.WriteLine($"[UnloadPlugin] GC failed: {ex.Message}"); }
                finally
                {
                    lock (_pluginUnloadGate) { _pluginUnloadScheduled = false; }
                }
            });
        }
    }
}
