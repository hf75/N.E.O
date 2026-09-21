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

        private void OnKeyDown(object? sender, global::Avalonia.Input.KeyEventArgs e)
        {
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
                if (HotReloadRuntime.InvokeRebuild(control, request.RebuildMethodName, out var stoppedTimers) is Control newRoot &&
                    !ReferenceEquals(newRoot, control))
                    control.Content = newRoot;

                int restored = uiState!.Restore(control);
                ui = $", UI rebuilt ({restored} of {uiState.Count} UI value(s) carried over" +
                     (stoppedTimers > 0 ? $", {stoppedTimers} replaced timer(s) stopped)" : ")");

                // Scroll extents and virtualized items settle with the next layout pass.
                Dispatcher.UIThread.Post(() => { try { uiState.Restore(control); } catch { } }, DispatcherPriority.Loaded);
            }

            HotReloadRuntime.InvokeOnHotReloaded(control);
            return applied + ui;
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
                }
                else
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        dynamicContent.Content = userControl;
                        HasEverLoadedControl = true;
                        WaitOverlay.IsVisible = false;
                        StopWaitTimer();
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
