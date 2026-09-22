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

        private void ShowHintOnce()
        {
            if (_hintShown || string.IsNullOrWhiteSpace(Hint)) return;
            _hintShown = true;
            HintText.Text = Hint;
            HintBanner.Visibility = Visibility.Visible;
            HintBanner.MouseDown += (_, _) => HintBanner.Visibility = Visibility.Collapsed;
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
            timer.Tick += (_, _) => { timer.Stop(); HintBanner.Visibility = Visibility.Collapsed; };
            timer.Start();
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
