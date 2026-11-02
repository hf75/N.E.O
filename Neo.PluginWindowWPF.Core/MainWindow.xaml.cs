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
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Neo.Shared;

namespace Neo.PluginWindowWPF
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
            if (!_waitOverlayEnabled)
                WaitOverlay.Visibility = Visibility.Collapsed;

            this.Activated += OnActivated;
            this.KeyDown += OnKeyDown;
        }

        /// <summary>
        /// The currently loaded user control, or <c>null</c> while nothing is loaded.
        /// IPC handlers (inspect_visual_tree, set_property, ...) resolve against this.
        /// </summary>
        public FrameworkElement? GetLoadedUserControl()
            => dynamicContent.Content as FrameworkElement;

        // The WPF desktop host renders its own placeholder while code is generated, so its
        // embedded child never showed one; heads opt out via PluginHostOptions.ShowWaitOverlay.
        private readonly bool _waitOverlayEnabled = PluginHost.Options.ShowWaitOverlay;

        public void ShowWaitOverlay(string statusText)
        {
            if (!_waitOverlayEnabled) return;
            WaitOverlay.Visibility = Visibility.Visible;
            WaitStatusText.Text = statusText;
            StartWaitTimer();
        }

        /// <summary>Ctrl+Shift+N: the user wants the host (N.E.O.) shown or hidden — in player mode the host hides behind this window.</summary>
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
            HintBanner.Background = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString(background)!;
            HintBanner.Visibility = Visibility.Visible;
            _bannerTimer?.Stop();
            if (seconds <= 0) return;
            _bannerTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
            _bannerTimer.Tick += (_, _) => { _bannerTimer.Stop(); HintBanner.Visibility = Visibility.Collapsed; };
            _bannerTimer.Start();
        }

        public void HideBanner()
        {
            _bannerTimer?.Stop();
            HintBanner.Visibility = Visibility.Collapsed;
        }

        // ── Heal in place: which action ran into the error, and doing it again after the fix ──

        private WeakReference<FrameworkElement>? _lastClicked;
        private DateTime _lastClickedAt;

        /// <summary>Remembers the last button the user clicked (every button, handled or not).</summary>
        public void TrackUserActions()
        {
            // A class handler: it runs before the app's own Click handlers, so the click is known even
            // when the handler throws and the event never bubbles up to this window.
            EventManager.RegisterClassHandler(typeof(System.Windows.Controls.Primitives.ButtonBase), System.Windows.Controls.Primitives.ButtonBase.ClickEvent, new RoutedEventHandler((_, e) =>
            {
                if (e.OriginalSource is FrameworkElement c) { _lastClicked = new WeakReference<FrameworkElement>(c); _lastClickedAt = DateTime.UtcNow; }
            }), handledEventsToo: true);
        }

        /// <summary>
        /// The address ("save" or "Button:2") of the button clicked just before an error was thrown —
        /// the action the host replays after the repair. Null when the error did not follow a click.
        /// </summary>
        public string? FailedActionAddress()
        {
            if (_lastClicked == null || (DateTime.UtcNow - _lastClickedAt) > TimeSpan.FromSeconds(1)) return null;
            if (!_lastClicked.TryGetTarget(out var control) || GetLoadedUserControl() is not FrameworkElement root) return null;
            if (!string.IsNullOrEmpty(control.Name) && control.TemplatedParent == null) return control.Name;
            var sameType = Descendants(root).Where(c => c.GetType() == control.GetType()).ToList();
            int index = sameType.IndexOf(control);
            return index >= 0 ? $"{control.GetType().Name}:{index}" : null;
        }

        /// <summary>Performs the action again on the repaired app. Returns what was replayed, or null.</summary>
        public string? ReplayAction(string address)
        {
            if (GetLoadedUserControl() is not FrameworkElement root) return null;
            if (FindControlInTree(root, address) is not System.Windows.Controls.Primitives.ButtonBase button) return null;
            button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            return address;
        }

        private static IEnumerable<FrameworkElement> Descendants(DependencyObject root)
        {
            var pending = new Stack<DependencyObject>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                var current = pending.Pop();
                int count = current is System.Windows.Media.Visual ? System.Windows.Media.VisualTreeHelper.GetChildrenCount(current) : 0;
                for (int i = 0; i < count; i++)
                {
                    var child = System.Windows.Media.VisualTreeHelper.GetChild(current, i);
                    if (child is FrameworkElement element) yield return element;
                    pending.Push(child);
                }
            }
        }

        private static FrameworkElement? FindControlInTree(FrameworkElement root, string target)
        {
            var all = Descendants(root).ToList();
            var byName = all.FirstOrDefault(c => string.Equals(c.Name, target, StringComparison.OrdinalIgnoreCase));
            if (byName != null) return byName;
            var parts = target.Split(':', 2);
            int index = parts.Length > 1 && int.TryParse(parts[1], out var i) ? i : 0;
            var byType = all.Where(c => c.GetType().Name.Equals(parts[0], StringComparison.OrdinalIgnoreCase)).ToList();
            return index < byType.Count ? byType[index] : null;
        }

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.N && Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            {
                HostToggleRequested?.Invoke();
                e.Handled = true;
                return;
            }

            // Embedded in a host window (no frame of its own): Alt+F4 would close only this
            // process and leave the host with a broken pipe. The host's own close button ends both.
            if (e.Key == Key.System && e.SystemKey == Key.F4 && WindowStyle == WindowStyle.None)
            {
                e.Handled = true;
                return;
            }

            // Escape or Ctrl+Shift+F exits fullscreen
            if (WindowState == WindowState.Maximized && WindowStyle == WindowStyle.None)
            {
                bool isEscape = e.Key == Key.Escape;
                bool isCtrlShiftF = e.Key == Key.F
                    && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)
                    && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);

                if (isEscape || isCtrlShiftF)
                {
                    WindowStyle = WindowStyle.SingleBorderWindow;
                    WindowState = WindowState.Normal;
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
            if (Application.Current is App app)
                await app.NotifyParentAboutActivation();
        }

        public void HandleFullscreenMouse(int val)
        {
            if (val == 0)
                InactivityCursorManager.Start();
            else
                InactivityCursorManager.Stop();
        }

        public void HandleChildModality(int val)
        {
            if (val == 0)
            {
                Topmost = false;
                IsEnabled = false;
            }
            else
            {
                Topmost = true;
                IsEnabled = true;
            }
        }

        // ================================
        // Hot reload
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
            if (plugin == null || dynamicContent.Content is not UserControl control)
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
                if (rebuild.NewRoot is UIElement newRoot && !ReferenceEquals(newRoot, control))
                    control.Content = newRoot;

                int restored = uiState!.Restore(control);
                ui = $", UI rebuilt ({restored} of {uiState.Count} UI value(s) carried over{rebuild.Cleanup})";

                // Scroll extents and virtualized items settle with the next layout pass.
                Dispatcher.BeginInvoke(new Action(() => { try { uiState.Restore(control); } catch { } }),
                    System.Windows.Threading.DispatcherPriority.Loaded);
            }

            HotReloadRuntime.InvokeOnHotReloaded(control);
            return applied + ui;
        }

        // ================================
        // The state of the running app as data (fusion, teleport)
        // ================================
        public AppStateImage CaptureAppState()
        {
            if (dynamicContent.Content is not UserControl control)
                throw new InvalidOperationException("No plugin is loaded.");
            return AppStateImage.Capture(control) with { Ui = UiStateSnapshot.Capture(control).ToPortable().ToJson() };
        }

        /// <summary>Puts the values in, builds the UI again so that it shows them, and puts back what the user did to the UI.</summary>
        public AppStateRestoredMessage RestoreAppState(AppStateImage image, RestoreAppStateRequest request)
        {
            if (dynamicContent.Content is not UserControl control)
                throw new InvalidOperationException("No plugin is loaded.");

            var restored = image.RestoreTo(control, request.FieldPrefix, request.KeepFields);

            if (HotReloadRuntime.FirstMethodOf(control, request.RebuildMethods) is { } buildUi)
            {
                var rebuild = HotReloadRuntime.Rebuild(control, buildUi, RoutedEventHandlers.Instance);
                if (rebuild.NewRoot is UIElement newRoot && !ReferenceEquals(newRoot, control))
                    control.Content = newRoot;
            }

            int uiValues = 0;
            if (request.RestoreUi && image.Ui != null && PortableUiState.FromJson(image.Ui) is { } portable)
            {
                var uiState = UiStateSnapshot.FromPortable(portable);
                uiValues = uiState.Restore(control);
                Dispatcher.BeginInvoke(new Action(() => { try { uiState.Restore(control); } catch { } }),
                    System.Windows.Threading.DispatcherPriority.Loaded);
            }

            HotReloadRuntime.InvokeOnHotReloaded(control);
            return new AppStateRestoredMessage(true, restored.Values, uiValues, restored.Failed);
        }

        // ================================
        // Bytes-basierter Ladeweg
        // ================================
        public void HandleLoadUserControlFromBytes(
            byte[] mainAssemblyBytes,
            string? explicitControlTypeName,
            IDictionary<string, byte[]> managedAssembliesByFullName,
            IDictionary<string, byte[]> nativeLibrariesByBasename)
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

                var controlType = SandboxPluginLoadContext.FindControlType(
                    pluginAssembly, explicitControlTypeName, typeof(UserControl));

                if (controlType == null)
                    throw new InvalidOperationException("No suitable UserControl type found.");

                if (!(Activator.CreateInstance(controlType) is UserControl userControl))
                    throw new InvalidCastException("Created type is not a UserControl.");

                if (Dispatcher.CheckAccess())
                {
                    dynamicContent.Content = userControl;
                    HasEverLoadedControl = true;
                    WaitOverlay.Visibility = Visibility.Collapsed;
                    StopWaitTimer();
                    ShowHintOnce();
                }
                else
                {
                    Dispatcher.Invoke(() =>
                    {
                        dynamicContent.Content = userControl;
                        HasEverLoadedControl = true;
                        WaitOverlay.Visibility = Visibility.Collapsed;
                        StopWaitTimer();
                        ShowHintOnce();
                    }, DispatcherPriority.Send);
                }
            }
        }

        // ==========================================================
        // Unload
        // ==========================================================
        public void UnloadUserControlPlugin()
        {
            lock (_pluginUnloadGate)
            {
                if (_pluginUnloadScheduled) return;
                _pluginUnloadScheduled = true;
            }

            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(UnloadUserControlPlugin), DispatcherPriority.Send);
                return;
            }

            // What this version watches and docks to (ForeignApp, compiled into the app) ends with it.
            AppDomain.CurrentDomain.SetData("Neo.ForeignApp.AppVersion", new object());

            try
            {
                if (dynamicContent.Content is UserControl)
                    dynamicContent.Content = null;
                ShowWaitOverlay("Generating new code...");
            }
            catch { }

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
