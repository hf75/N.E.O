using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;

using Neo.IPC;
using Neo.Shared;

namespace Neo.PluginWindowAvalonia
{
    public partial class App : Application
    {
        private PipeClient? _client;
        private readonly SemaphoreSlim _sendLock = new(1, 1);

        private CancellationTokenSource _ipcCts = new();
        private DispatcherTimer? _hb;

        public MainWindow MainWin { get; private set; } = null!;

        // Managed: Indexe nach FullName und SimpleName (Simple → Full für Fallback)
        private readonly ConcurrentDictionary<string, byte[]> _managedByFullName =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, string> _simpleToFull =
            new(StringComparer.OrdinalIgnoreCase);

        // Native: Pfad/Dateiname (Unterordner erlaubt) → Bytes
        private readonly ConcurrentDictionary<string, byte[]> _nativeByPath =
            new(StringComparer.OrdinalIgnoreCase);

        // Zusätzlich: Basename → Bytes (für LoadLibrary("sqlite3.dll"))
        private readonly ConcurrentDictionary<string, byte[]> _nativeByBasename =
            new(StringComparer.OrdinalIgnoreCase);

        // Für laufende BLOB-Transfers
        private readonly ConcurrentDictionary<Guid, MemoryStream> _blobStreams = new();
        private readonly ConcurrentDictionary<Guid, BlobStartMeta> _blobMetas = new();

        // Web Bridge
        private WebBridgeServer? _webBridge;

        // Live-MCP (Phase 1): manifest + dispatcher for this loaded UserControl.
        // Built once per LoadControl; cleared on UnloadControl.
        private AppManifestMessage? _liveMcpManifest;
        private LiveMcp.LiveMcpDispatcher? _liveMcpDispatcher;
        private LiveMcp.LiveMcpObservableSubscriptions? _liveMcpObservableSubs;
        private LiveMcp.LiveMcpInputDispatcher? _liveMcpInputDispatcher;

        public override void Initialize()
        {
            AvaloniaXamlLoader.Load(this);
        }

        public override void OnFrameworkInitializationCompleted()
        {
            // AC_TEMP (wie gehabt)
            var ac = Environment.GetEnvironmentVariable("AC_WORK");
            if (!string.IsNullOrWhiteSpace(ac))
            {
                var acTemp = Path.Combine(ac, "temp");
                try { Directory.CreateDirectory(acTemp); } catch (Exception ex) { Debug.WriteLine($"[Init] CreateDirectory failed: {ex.Message}"); }
                Environment.SetEnvironmentVariable("TEMP", acTemp);
                Environment.SetEnvironmentVariable("TMP", acTemp);
            }

            CleanupTempCacheRecursive();

            // Exception Hooks
            Dispatcher.UIThread.UnhandledException += Dispatcher_UnhandledException;
            TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;

            // Lifetime holen
            var lifetime = ApplicationLifetime as IClassicDesktopStyleApplicationLifetime
                ?? throw new InvalidOperationException("Classic desktop lifetime required.");

            // Check if running standalone (from Neo.App.Avalonia host) or embedded (from WPF host)
            var args = (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Args ?? Array.Empty<string>();
            bool isStandalone = args.Any(a => string.Equals(a, "--standalone", StringComparison.OrdinalIgnoreCase));

            var hostOptions = PluginHost.Options;

            MainWin = new MainWindow();
            hostOptions.MainWindowCreated?.Invoke(MainWin);
            if (isStandalone)
            {
                // Standalone mode: full window with decorations. In player mode the host passes
                // the app's own title and icon — the window then is the app, not a preview.
                MainWin.Title = GetArgValue(args, "--title") ?? hostOptions.StandaloneWindowTitle;
                MainWin.Width = 800;
                MainWin.Height = 600;
                MainWin.ShowInTaskbar = true;
                MainWin.WindowDecorations = WindowDecorations.Full;
                MainWin.CanResize = true;
                MainWin.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                try
                {
                    var iconPath = GetArgValue(args, "--icon");
                    if (iconPath != null && File.Exists(iconPath))
                        MainWin.Icon = new WindowIcon(iconPath);
                    else
                    {
                        var stream = typeof(App).Assembly.GetManifestResourceStream("icon.ico");
                        if (stream != null)
                            MainWin.Icon = new WindowIcon(stream);
                    }
                }
                catch { /* icon not critical */ }

                // The host may run hidden behind this window (player mode): it needs to know where
                // the window is to dock next to it.
                var boundsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
                boundsTimer.Tick += (_, _) => { boundsTimer.Stop(); _ = SendWindowBoundsAsync(); };
                void BoundsChanged(object? _, EventArgs __) { boundsTimer.Stop(); boundsTimer.Start(); }
                MainWin.PositionChanged += BoundsChanged;
                MainWin.SizeChanged += BoundsChanged;
                MainWin.Opened += BoundsChanged;
            }
            else
            {
                // Embedded mode: no decorations (WPF host controls position/size via HWND)
                MainWin.ShowInTaskbar = false;
                MainWin.WindowDecorations = WindowDecorations.None;
                MainWin.CanResize = false;
            }

            // Ctrl+Shift+N: the user calls the host up (or puts it away) — in any mode.
            MainWin.HostToggleRequested += () => _ = SafeSendAsync(new IpcEnvelope(IpcTypes.ToggleHostWindow, "", "{}"));

            lifetime.MainWindow = MainWin;

            // Opened: Hier existiert sicher ein Native Handle
            MainWin.Opened += async (_, __) =>
            {
                // Pipe-Name aus Args
                var pipeName = GetPipeNameFromArgs(lifetime.Args ?? Array.Empty<string>());
                if (string.IsNullOrWhiteSpace(pipeName))
                {
                    lifetime.Shutdown();
                    return;
                }

                _client = new PipeClient(pipeName);
                try
                {
                    await _client.ConnectAsync();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Startup] Pipe connect failed: {ex.Message}");
                    lifetime.Shutdown();
                    return;
                }

                // HWND/Handle (plattformabhängig) – unter Windows vorhanden
                var handle = MainWin.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;

                // Hello senden
                await SafeSendAsync(new IpcEnvelope(
                    IpcTypes.Hello,
                    Guid.NewGuid().ToString("N"),
                    Json.ToJson(new HelloMessage("Child", Environment.ProcessId, Hwnd: handle == IntPtr.Zero ? 0 : handle.ToInt64(),
                        HotReloadCapabilities: HotReloadRuntime.Capabilities))
                ));

                // Wire the Neo.Trigger API so generated app code can push prompts to Claude.
                // The hop count is read from LiveMcpCallContext (AsyncLocal) so triggers
                // emitted from inside an InvokeMethod call carry the correct loop counter.
                // Only the MCP head opts in — under the desktop hosts there is no Claude
                // session on the other end, so Ai.Trigger stays a no-op there.
                if (hostOptions.EnableMcpHostFeatures)
                    WireAiTrigger();

                // Live-MCP dispatcher resolves methods/observables against the currently
                // loaded UserControl; both reads are done at call time so it stays correct
                // across LoadControl / UnloadControl.
                _liveMcpDispatcher = new LiveMcp.LiveMcpDispatcher(
                    getUserControl: () => MainWin.GetLoadedUserControl(),
                    getManifest: () => _liveMcpManifest);

                // Phase 2B watch_observable: hooks INPC on the loaded UserControl, emits
                // ObservableValue IPC frames on every PropertyChanged for subscribed names.
                _liveMcpObservableSubs = new LiveMcp.LiveMcpObservableSubscriptions(
                    getControl: () => MainWin.GetLoadedUserControl(),
                    emit: (name, valueJson) => SafeSendAsync(new IpcEnvelope(
                        IpcTypes.ObservableValue, "",
                        Json.ToJson(new ObservableValueMessage(
                            Name: name,
                            ValueJson: valueJson,
                            Hops: LiveMcp.LiveMcpCallContext.CurrentHops)))));

                // Phase 3 raise_event / simulate_input: drives Avalonia events and synthesizes
                // input on named controls in the loaded UserControl.
                _liveMcpInputDispatcher = new LiveMcp.LiveMcpInputDispatcher(
                    getUserControl: () => MainWin.GetLoadedUserControl());

                // Framed-Listen-Loop starten
                _ = Task.Run(() => FramedListenLoopAsync(_ipcCts.Token));

                // Heartbeat-Timer
                _hb = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                _hb.Tick += (_, __2) =>
                {
                    try
                    {
                        if (_client == null) return;
                        var hb = new { Pid = Environment.ProcessId, WhenUtc = DateTime.UtcNow };
                        _ = SafeSendAsync(new IpcEnvelope(IpcTypes.Heartbeat, "", Json.ToJson(hb)));
                    }
                    catch (Exception ex) { Debug.WriteLine($"[Heartbeat] {ex.Message}"); }
                };
                _hb.Start();
            };

            // Exit/Shutdown
            lifetime.Exit += OnExit;

            // Anzeigen
            MainWin.Show();

            base.OnFrameworkInitializationCompleted();
        }

        private async void OnExit(object? sender, ControlledApplicationLifetimeExitEventArgs e)
        {
            try { _hb?.Stop(); } catch (Exception ex) { Debug.WriteLine($"[OnExit] Timer stop failed: {ex.Message}"); }
            try { _ipcCts.Cancel(); } catch (Exception ex) { Debug.WriteLine($"[OnExit] CTS cancel failed: {ex.Message}"); }
            try { if (_client != null) await _client.DisposeAsync(); } catch (Exception ex) { Debug.WriteLine($"[OnExit] Client dispose failed: {ex.Message}"); }
        }

        // =======================================================
        // Framed-Listen-Loop
        // =======================================================
        private async Task FramedListenLoopAsync(CancellationToken ct)
        {
            try
            {
                await _client!.Messenger.ReceiveLoopAsync(
                    onControl: async env => await OnControlAsync(env),
                    onBlobStart: async (corr, meta) =>
                    {
                        var ms = meta.Length is long L && L >= 0
                            ? new MemoryStream(capacity: (int)Math.Min(L, int.MaxValue))
                            : new MemoryStream();
                        _blobStreams[corr] = ms;
                        _blobMetas[corr] = meta;
                        await Task.CompletedTask;
                    },
                    onBlobChunk: async (corr, chunk) =>
                    {
                        if (_blobStreams.TryGetValue(corr, out var ms))
                            await ms.WriteAsync(chunk, CancellationToken.None);
                    },
                    onBlobEnd: async (corr) =>
                    {
                        if (_blobStreams.TryRemove(corr, out var ms))
                        {
                            try
                            {
                                ms.Position = 0;
                                if (_blobMetas.TryRemove(corr, out var meta))
                                {
                                    var bytes = ms.ToArray();
                                    var logicalPath = meta.Name.Replace('\\', '/').TrimStart('/');

                                    if (PeUtils.IsManagedAssembly(bytes))
                                    {
                                        var an = PeUtils.TryGetAssemblyName(bytes);
                                        if (an != null)
                                        {
                                            var full = an.FullName!;
                                            var simple = an.Name ?? Path.GetFileNameWithoutExtension(logicalPath);

                                            _managedByFullName[full] = bytes;
                                            _simpleToFull[simple] = full;

                                            await SendLogAsync(LogLevel.Info, $"Managed received: {simple} [{full}] ({bytes.Length} bytes)");
                                        }
                                        else
                                        {
                                            var simple = Path.GetFileNameWithoutExtension(logicalPath);
                                            var pseudoFull = simple;
                                            _managedByFullName[pseudoFull] = bytes;
                                            _simpleToFull[simple] = pseudoFull;

                                            await SendLogAsync(LogLevel.Warn, $"Managed (no AssemblyName) assumed: {simple} ({bytes.Length} bytes)");
                                        }
                                    }
                                    else
                                    {
                                        _nativeByPath[logicalPath] = bytes;
                                        var baseName = Path.GetFileName(logicalPath);
                                        _nativeByBasename[baseName] = bytes;

                                        await SendLogAsync(LogLevel.Info, $"Native received: {logicalPath} ({bytes.Length} bytes)");
                                    }
                                }
                            }
                            finally { ms.Dispose(); }
                        }
                    },
                    ct: ct);
            }
            catch (OperationCanceledException) { /* shutting down */ }
            catch (ObjectDisposedException) { /* stream closed */ }
            catch (Exception ex)
            {
                await SendErrorAsync("FramedListenLoop failed", ex);
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
                });
            }
            finally
            {
                HardExitNow();
            }
        }

        // =======================================================
        // Control-Handling
        // =======================================================
        private async Task OnControlAsync(IpcEnvelope env)
        {
            switch (env.Type)
            {
                case IpcTypes.Hello:
                    await SafeSendAsync(new IpcEnvelope(
                        IpcTypes.Ack, env.CorrelationId,
                        Json.ToJson(new AckMessage("Hello received by Child"))
                    ));
                    break;

                case IpcTypes.LoadControl:
                    {
                        var req = Json.FromJson<LoadControlRequest>(env.PayloadJson)!;
                        try
                        {
                            if (!TryBuildBundleFromAssets(req.AssemblyPath, out var mainBytes, out var managedByFull, out var nativeByBase) || mainBytes == null)
                                throw new InvalidOperationException("Failed to build assembly bundle from assets.");

                            await Dispatcher.UIThread.InvokeAsync(() =>
                            {
                                MainWin.HandleLoadUserControlFromBytes(
                                    mainAssemblyBytes: mainBytes,
                                    explicitControlTypeName: string.IsNullOrWhiteSpace(req.TypeName) ? null : req.TypeName,
                                    managedAssembliesByFullName: managedByFull,
                                    nativeLibrariesByBasename: nativeByBase
                                );
                                MainWin.Activate();
                            });

                            await SendLogAsync(LogLevel.Info, "LoadControl executed.", "Child", "Mode=Bytes(In-Memory)");
                            await SafeSendAsync(new IpcEnvelope(
                                IpcTypes.Ack, env.CorrelationId,
                                Json.ToJson(new AckMessage("Loaded (bytes/in-memory)"))
                            ));

                            // Emit the Live-MCP manifest so the host can route invoke_method etc.
                            // Awaited (it never throws, failures are logged): frames are handled one
                            // after the other, so an invoke_method that follows the load right away
                            // finds the manifest instead of "no UserControl loaded".
                            if (PluginHost.Options.EnableMcpHostFeatures)
                                await SendLiveMcpManifestAsync();
                        }
                        catch (Exception ex)
                        {
                            await _client!.SendAsync(new IpcEnvelope(
                                IpcTypes.Error, env.CorrelationId,
                                Json.ToJson(new ErrorMessage(ex.Message, ex.GetType().FullName, ex.ToString()))
                            ));
                        }
                        break;
                    }

                case IpcTypes.CursorVisible:
                    {
                        var msg = Json.FromJson<IsCursorVisible>(env.PayloadJson)!;

                        Dispatcher.UIThread.Post(() =>
                        {
                            MainWin.HandleFullscreenMouse(msg.isCursorVisible);
                        });

                        await SafeSendAsync(new IpcEnvelope(
                            IpcTypes.Ack, env.CorrelationId,
                            Json.ToJson(new AckMessage("Cursor Changed!"))
                        ));
                        break;
                    }

                case IpcTypes.SetChildModal:
                    {
                        var modalMsg = Json.FromJson<IsChildModal>(env.PayloadJson)!;

                        Dispatcher.UIThread.Post(() =>
                        {
                            MainWin.HandleChildModality(modalMsg.isModal);
                        });

                        await SafeSendAsync(new IpcEnvelope(
                            IpcTypes.Ack, env.CorrelationId,
                            Json.ToJson(new AckMessage("Modal Changed!"))
                        ));
                        break;
                    }

                case IpcTypes.ParentWindowBounds:
                    {
                        var bounds = Json.FromJson<ParentWindowBoundsMessage>(env.PayloadJson);
                        if (bounds != null)
                        {
                            Dispatcher.UIThread.Post(() =>
                            {
                                if (!bounds.IsVisible)
                                {
                                    MainWin.WaitOverlay.IsVisible = true;
                                    MainWin.WaitStatusText.Text = "Generating...";
                                    MainWin.StartWaitTimer();
                                }
                                else
                                {
                                    // Stop timer and restore loaded control if available
                                    MainWin.StopWaitTimer();
                                    MainWin.WaitTimerText.Text = "";
                                    if (MainWin.HasEverLoadedControl)
                                        MainWin.WaitOverlay.IsVisible = false;
                                    else
                                        MainWin.WaitStatusText.Text = "Waiting for code...";

                                    // Dock to the right edge of parent, same height — unless the
                                    // host follows this window instead (player mode).
                                    if (bounds.Dock)
                                    {
                                        int dockX = (int)(bounds.X + bounds.Width + 8); // 8px gap
                                        int dockY = (int)bounds.Y;
                                        int dockH = (int)bounds.Height;

                                        MainWin.Position = new PixelPoint(dockX, dockY);
                                        MainWin.Height = dockH;
                                    }

                                    if (!MainWin.IsVisible)
                                        MainWin.Show();
                                }
                            });
                        }
                        break;
                    }

                case IpcTypes.PositionWindow:
                    {
                        var bounds = Json.FromJson<ParentWindowBoundsMessage>(env.PayloadJson);
                        if (bounds != null)
                        {
                            Dispatcher.UIThread.Post(() =>
                            {
                                MainWin.Position = new PixelPoint((int)bounds.X, (int)bounds.Y);
                                MainWin.Width = bounds.Width;
                                MainWin.Height = bounds.Height;

                                if (!MainWin.IsVisible)
                                    MainWin.Show();

                                if (MainWin.HasEverLoadedControl)
                                    MainWin.WaitOverlay.IsVisible = false;
                            });
                        }

                        await SafeSendAsync(new IpcEnvelope(
                            IpcTypes.Ack, env.CorrelationId,
                            Json.ToJson(new AckMessage("Window positioned."))));
                        break;
                    }

                case IpcTypes.ToggleChildFullScreen:
                    {
                        Dispatcher.UIThread.Post(() =>
                        {
                            if (MainWin.WindowState == WindowState.FullScreen)
                            {
                                MainWin.WindowState = WindowState.Normal;
                                MainWin.WindowDecorations = WindowDecorations.Full;
                            }
                            else
                            {
                                MainWin.WindowDecorations = WindowDecorations.None;
                                MainWin.WindowState = WindowState.FullScreen;
                            }
                        });

                        await SafeSendAsync(new IpcEnvelope(
                            IpcTypes.Ack, env.CorrelationId,
                            Json.ToJson(new AckMessage("Fullscreen toggled."))
                        ));
                        break;
                    }

                case IpcTypes.HotReload:
                    {
                        HotReloadResultMessage result;
                        try
                        {
                            var request = Json.FromJson<HotReloadRequest>(env.PayloadJson)!;
                            var details = await Dispatcher.UIThread.InvokeAsync(() => MainWin.HandleHotReload(request));
                            result = new HotReloadResultMessage(true, Details: details);
                            await SendLogAsync(LogLevel.Info, "Hot reload applied: " + details, "Child");

                            // Callables and observables may have changed with the code. Before the
                            // result goes out, so the host knows the new ones when it hears "applied".
                            if (PluginHost.Options.EnableMcpHostFeatures)
                                await SendLiveMcpManifestAsync();
                        }
                        catch (Exception ex)
                        {
                            var cause = (ex as System.Reflection.TargetInvocationException)?.InnerException ?? ex;
                            result = new HotReloadResultMessage(false, Error: $"{cause.GetType().Name}: {cause.Message}");
                        }

                        await SafeSendAsync(new IpcEnvelope(IpcTypes.HotReloadResult, env.CorrelationId, Json.ToJson(result)));
                        break;
                    }

                case IpcTypes.UnloadControl:
                    {
                        await Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            MainWin.UnloadUserControlPlugin();
                        });
                        _liveMcpManifest = null;
                        _liveMcpObservableSubs?.ClearForControlUnload();

                        await SafeSendAsync(new IpcEnvelope(
                            IpcTypes.Ack, env.CorrelationId,
                            Json.ToJson(new AckMessage("Control unloaded."))
                        ));
                        break;
                    }

                case IpcTypes.CaptureScreenshot:
                    {
                        try
                        {
                            var base64 = await Dispatcher.UIThread.InvokeAsync(async () =>
                            {
                                var target = MainWin;
                                var pixelSize = new PixelSize(
                                    Math.Max(1, (int)target.Bounds.Width),
                                    Math.Max(1, (int)target.Bounds.Height));
                                var dpi = new Vector(96, 96);

                                using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(pixelSize, dpi);
                                bitmap.Render(target);

                                using var ms = new MemoryStream();
                                bitmap.Save(ms, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
                                return Convert.ToBase64String(ms.ToArray());
                            });

                            var result = new ScreenshotResultMessage(
                                base64,
                                (int)MainWin.Bounds.Width,
                                (int)MainWin.Bounds.Height);

                            await SafeSendAsync(new IpcEnvelope(
                                IpcTypes.ScreenshotResult, env.CorrelationId,
                                Json.ToJson(result)));
                        }
                        catch (Exception ex)
                        {
                            await SafeSendAsync(new IpcEnvelope(
                                IpcTypes.Error, env.CorrelationId,
                                Json.ToJson(new ErrorMessage(
                                    $"Screenshot failed: {ex.Message}",
                                    ex.GetType().FullName, ex.ToString()))));
                        }
                        break;
                    }

                case IpcTypes.SetProperty:
                    {
                        try
                        {
                            var req = Json.FromJson<SetPropertyRequest>(env.PayloadJson)!;
                            var result = await Dispatcher.UIThread.InvokeAsync(() =>
                                SetPropertyOnControl(req));

                            await SafeSendAsync(new IpcEnvelope(
                                IpcTypes.SetPropertyResult, env.CorrelationId,
                                Json.ToJson(result)));
                        }
                        catch (Exception ex)
                        {
                            await SafeSendAsync(new IpcEnvelope(
                                IpcTypes.SetPropertyResult, env.CorrelationId,
                                Json.ToJson(new SetPropertyResultMessage(
                                    false, $"SetProperty failed: {ex.Message}"))));
                        }
                        break;
                    }

                case IpcTypes.InspectVisualTree:
                    {
                        try
                        {
                            var json = await Dispatcher.UIThread.InvokeAsync(() =>
                            {
                                var root = MainWin.GetLoadedUserControl();
                                if (root == null) return "{\"error\": \"No control loaded.\"}";
                                _serializedNodeCount = 0;
                                return SerializeVisualTree(root, 0);
                            });

                            await SafeSendAsync(new IpcEnvelope(
                                IpcTypes.InspectVisualTreeResult, env.CorrelationId, json));
                        }
                        catch (Exception ex)
                        {
                            await SafeSendAsync(new IpcEnvelope(
                                IpcTypes.Error, env.CorrelationId,
                                Json.ToJson(new ErrorMessage(
                                    $"InspectVisualTree failed: {ex.Message}",
                                    ex.GetType().FullName, ex.ToString()))));
                        }
                        break;
                    }

                case IpcTypes.InjectData:
                    {
                        try
                        {
                            var req = Json.FromJson<InjectDataRequest>(env.PayloadJson)!;
                            var result = await Dispatcher.UIThread.InvokeAsync(() =>
                                InjectDataOnControl(req));
                            await SafeSendAsync(new IpcEnvelope(
                                IpcTypes.InjectDataResult, env.CorrelationId,
                                Json.ToJson(result)));
                        }
                        catch (Exception ex)
                        {
                            await SafeSendAsync(new IpcEnvelope(
                                IpcTypes.InjectDataResult, env.CorrelationId,
                                Json.ToJson(new InjectDataResult(false, $"InjectData failed: {ex.Message}"))));
                        }
                        break;
                    }

                case IpcTypes.ReadData:
                    {
                        try
                        {
                            var req = Json.FromJson<ReadDataRequest>(env.PayloadJson)!;
                            var result = await Dispatcher.UIThread.InvokeAsync(() =>
                                ReadDataFromControl(req));
                            await SafeSendAsync(new IpcEnvelope(
                                IpcTypes.ReadDataResult, env.CorrelationId,
                                Json.ToJson(result)));
                        }
                        catch (Exception ex)
                        {
                            await SafeSendAsync(new IpcEnvelope(
                                IpcTypes.ReadDataResult, env.CorrelationId,
                                Json.ToJson(new ReadDataResult(false, $"ReadData failed: {ex.Message}"))));
                        }
                        break;
                    }

                case IpcTypes.ExtractCode:
                    {
                        try
                        {
                            var code = await Dispatcher.UIThread.InvokeAsync(() =>
                            {
                                var root = MainWin.GetLoadedUserControl();
                                if (root == null) return "// No control loaded.";
                                return ExtractCodeFromVisualTree(root);
                            });
                            await SafeSendAsync(new IpcEnvelope(
                                IpcTypes.ExtractCodeResult, env.CorrelationId, code));
                        }
                        catch (Exception ex)
                        {
                            await SafeSendAsync(new IpcEnvelope(
                                IpcTypes.Error, env.CorrelationId,
                                Json.ToJson(new ErrorMessage($"ExtractCode failed: {ex.Message}"))));
                        }
                        break;
                    }

                case IpcTypes.StartWebBridge:
                    {
                        try
                        {
                            var req = Json.FromJson<StartWebBridgeRequest>(env.PayloadJson)!;

                            // Stop existing bridge if running
                            _webBridge?.Dispose();
                            _webBridge = new WebBridgeServer();

                            // Forward browser messages to MCP server via IPC log
                            _webBridge.MessageReceived += (msg) =>
                            {
                                _ = SendLogAsync(LogLevel.Info, $"[WebBridge] {msg}", "WebBridge");
                            };

                            var ok = _webBridge.Start(req.HtmlContent, req.Port);
                            var result = ok
                                ? new StartWebBridgeResult(true, _webBridge.Url, _webBridge.WsUrl, null)
                                : new StartWebBridgeResult(false, null, null, "Failed to start HttpListener.");

                            await SafeSendAsync(new IpcEnvelope(
                                IpcTypes.StartWebBridgeResult, env.CorrelationId,
                                Json.ToJson(result)));
                        }
                        catch (Exception ex)
                        {
                            await SafeSendAsync(new IpcEnvelope(
                                IpcTypes.StartWebBridgeResult, env.CorrelationId,
                                Json.ToJson(new StartWebBridgeResult(false, null, null, ex.Message))));
                        }
                        break;
                    }

                case IpcTypes.StopWebBridge:
                    {
                        _webBridge?.Dispose();
                        _webBridge = null;
                        await SafeSendAsync(new IpcEnvelope(
                            IpcTypes.Ack, env.CorrelationId,
                            Json.ToJson(new AckMessage("WebBridge stopped."))));
                        break;
                    }

                case IpcTypes.SendToWebBridge:
                    {
                        try
                        {
                            if (_webBridge?.IsRunning == true)
                            {
                                await _webBridge.SendToAllAsync(env.PayloadJson);
                                await SafeSendAsync(new IpcEnvelope(
                                    IpcTypes.Ack, env.CorrelationId,
                                    Json.ToJson(new AckMessage($"Sent to {_webBridge.ClientCount} client(s)."))));
                            }
                            else
                            {
                                await SafeSendAsync(new IpcEnvelope(
                                    IpcTypes.Ack, env.CorrelationId,
                                    Json.ToJson(new AckMessage("WebBridge not running."))));
                            }
                        }
                        catch (Exception ex)
                        {
                            await SafeSendAsync(new IpcEnvelope(
                                IpcTypes.Error, env.CorrelationId,
                                Json.ToJson(new ErrorMessage($"SendToWebBridge failed: {ex.Message}"))));
                        }
                        break;
                    }

                // ── Live-MCP (Phase 1) ────────────────────────────────────────
                case IpcTypes.InvokeMethod:
                    {
                        var req = Json.FromJson<InvokeMethodMessage>(env.PayloadJson);
                        MethodResultMessage result;
                        if (req == null || _liveMcpDispatcher == null)
                            result = new MethodResultMessage(false, null, "Dispatcher not ready or invalid request.", "invocation_failed");
                        else
                            result = await _liveMcpDispatcher.InvokeAsync(req);

                        await SafeSendAsync(new IpcEnvelope(
                            IpcTypes.MethodResult, env.CorrelationId,
                            Json.ToJson(result)));
                        break;
                    }

                case IpcTypes.ReadObservable:
                    {
                        var req = Json.FromJson<ReadObservableMessage>(env.PayloadJson);
                        ReadObservableResultMessage result;
                        if (req == null || _liveMcpDispatcher == null)
                            result = new ReadObservableResultMessage(false, null, "Dispatcher not ready or invalid request.");
                        else
                            result = await _liveMcpDispatcher.ReadObservableAsync(req.Name);

                        await SafeSendAsync(new IpcEnvelope(
                            IpcTypes.ReadObservableResult, env.CorrelationId,
                            Json.ToJson(result)));
                        break;
                    }

                case IpcTypes.SubscribeObservable:
                    {
                        var req = Json.FromJson<SubscribeObservableMessage>(env.PayloadJson);
                        if (req != null && _liveMcpObservableSubs != null)
                            await _liveMcpObservableSubs.SubscribeAsync(req.Name);
                        await SafeSendAsync(new IpcEnvelope(
                            IpcTypes.Ack, env.CorrelationId,
                            Json.ToJson(new AckMessage("SubscribeObservable accepted."))));
                        break;
                    }

                case IpcTypes.UnsubscribeObservable:
                    {
                        var req = Json.FromJson<UnsubscribeObservableMessage>(env.PayloadJson);
                        if (req != null && _liveMcpObservableSubs != null)
                            await _liveMcpObservableSubs.UnsubscribeAsync(req.Name);
                        await SafeSendAsync(new IpcEnvelope(
                            IpcTypes.Ack, env.CorrelationId,
                            Json.ToJson(new AckMessage("UnsubscribeObservable accepted."))));
                        break;
                    }

                case IpcTypes.RaiseEvent:
                    {
                        var req = Json.FromJson<RaiseEventMessage>(env.PayloadJson);
                        RaiseEventResultMessage result;
                        if (req == null || _liveMcpInputDispatcher == null)
                            result = new RaiseEventResultMessage(false, "Dispatcher not ready or invalid request.", "invocation_failed");
                        else
                            result = await _liveMcpInputDispatcher.RaiseEventAsync(req);
                        await SafeSendAsync(new IpcEnvelope(
                            IpcTypes.RaiseEventResult, env.CorrelationId,
                            Json.ToJson(result)));
                        break;
                    }

                case IpcTypes.SimulateInput:
                    {
                        var req = Json.FromJson<SimulateInputMessage>(env.PayloadJson);
                        SimulateInputResultMessage result;
                        if (req == null || _liveMcpInputDispatcher == null)
                            result = new SimulateInputResultMessage(false, "Dispatcher not ready or invalid request.", "invocation_failed");
                        else
                            result = await _liveMcpInputDispatcher.SimulateInputAsync(req);
                        await SafeSendAsync(new IpcEnvelope(
                            IpcTypes.SimulateInputResult, env.CorrelationId,
                            Json.ToJson(result)));
                        break;
                    }

                default:
                    await SafeSendAsync(new IpcEnvelope(
                        IpcTypes.Ack, env.CorrelationId,
                        Json.ToJson(new AckMessage($"Unknown command '{env.Type}' ignored by Child"))
                    ));
                    break;
            }
        }

        // =======================================================
        // Utility
        // =======================================================
        private static string? GetArgValue(string[] args, string name)
        {
            for (int i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            return null;
        }

        /// <summary>Where the app window is on screen (standalone mode), for a host that docks next to it.</summary>
        private Task SendWindowBoundsAsync()
        {
            if (_client == null || !MainWin.IsVisible) return Task.CompletedTask;
            // Same convention as the host's own bounds: position in screen pixels, size in DIPs.
            var bounds = new ParentWindowBoundsMessage(
                MainWin.Position.X, MainWin.Position.Y, MainWin.Width, MainWin.Height, IsVisible: true);
            return SafeSendAsync(new IpcEnvelope(IpcTypes.ChildWindowBounds, "", Json.ToJson(bounds)));
        }

        private static string? GetPipeNameFromArgs(string[] args)
        {
            for (int i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], "--pipe", StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            return null;
        }

        private async Task SafeSendAsync(IpcEnvelope env)
        {
            if (_client == null) return;
            await _sendLock.WaitAsync();
            try
            {
                await _client.SendAsync(env);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        private Task SendLogAsync(LogLevel level, string message, string? category = null, string? details = null)
        {
            if (_client == null) return Task.CompletedTask;
            var log = new LogMessage(level, message, category, details);
            return SafeSendAsync(new IpcEnvelope(IpcTypes.Log, "", Json.ToJson(log)));
        }

        /// <summary>
        /// Routes <c>Neo.App.Ai.Trigger</c> to the host as <c>AppEvent</c> frames.
        ///
        /// <para>Deliberately its own non-inlined method: the JIT resolves every type a method
        /// mentions when it compiles that method, whether or not the branch runs. The desktop
        /// hosts deploy the slim head without Neo.App.Api.dll, so the only code that names a
        /// <c>Neo.App</c> type must not be compiled unless the MCP host features are on.</para>
        /// </summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private void WireAiTrigger()
        {
            Neo.App.Ai.SetEmitter(prompt =>
            {
                _ = SendAppEventAsync("user_trigger", "Neo.Trigger", prompt,
                    hops: LiveMcp.LiveMcpCallContext.CurrentHops);
            });
        }

        /// <summary>Send a user-interaction event to the MCP server for channel push to Claude.</summary>
        private Task SendAppEventAsync(string eventType, string target, string? value = null, string? details = null, int hops = 0)
        {
            if (_client == null) return Task.CompletedTask;
            var evt = new AppEventMessage(eventType, target, value, details, hops);
            return SafeSendAsync(new IpcEnvelope(IpcTypes.AppEvent, "", Json.ToJson(evt)));
        }

        /// <summary>
        /// Build the Live-MCP manifest for the currently loaded UserControl and push it
        /// to the host server. Called from the LoadControl handler after a successful load.
        /// </summary>
        private async Task SendLiveMcpManifestAsync()
        {
            try
            {
                var ctrl = await Dispatcher.UIThread.InvokeAsync(() => MainWin.GetLoadedUserControl());
                if (ctrl is not Avalonia.Controls.Control control)
                {
                    _liveMcpManifest = null;
                    return;
                }

                // AppId is filled in by the server (it knows its windowId); we send empty here.
                var manifest = LiveMcp.LiveMcpManifestBuilder.Build(appId: "", control);
                _liveMcpManifest = manifest;
                await SafeSendAsync(new IpcEnvelope(IpcTypes.AppManifest, "", Json.ToJson(manifest)));
                await SendLogAsync(LogLevel.Info,
                    $"Live-MCP manifest emitted: {manifest.Callables.Count} callables, " +
                    $"{manifest.Observables.Count} observables, {manifest.Triggerables.Count} triggerables.",
                    "LiveMcp");
            }
            catch (Exception ex)
            {
                await SendLogAsync(LogLevel.Warn, $"Live-MCP manifest emission failed: {ex.Message}", "LiveMcp");
            }
        }

        private Task SendErrorAsync(string context, Exception? ex)
        {
            if (_client == null) return Task.CompletedTask;

            var err = new ErrorMessage(
                Message: ex?.Message ?? context,
                ExceptionType: ex?.GetType().FullName,
                StackTrace: ex?.ToString(),
                Context: context,
                Level: LogLevel.Error
            );
            return SafeSendAsync(new IpcEnvelope(IpcTypes.Error, "", Json.ToJson(err)));
        }

        private void CurrentDomain_UnhandledException(object? sender, UnhandledExceptionEventArgs e)
        {
            var ex = e.ExceptionObject as Exception ?? new Exception("Non-exception thrown.");
            TrySendErrorSynchronous_BestEffort("UnhandledException", ex, e.IsTerminating);
        }

        private void Dispatcher_UnhandledException(object? sender, DispatcherUnhandledExceptionEventArgs e)
        {
            // A delegate that still points at code a hot reload retired (a lambda the new version no
            // longer has). That is a stale subscription, not a broken app: do not report a crash.
            if (HotReloadRuntime.IsStaleCodeException(e.Exception))
            {
                Debug.WriteLine("[HotReload] Ignored call into retired code: " + e.Exception.Message);
                e.Handled = true;
                return;
            }

            TrySendErrorSynchronous_BestEffort("DispatcherUnhandledException", e.Exception, false);
            e.Handled = true;
        }

        private void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            TrySendErrorSynchronous_BestEffort("UnobservedTaskException", e.Exception, false);
            e.SetObserved();
        }

        private void TrySendErrorSynchronous_BestEffort(string context, Exception ex, bool isTerminating)
        {
            if (_client == null || !_client.IsConnected)
            {
                Debug.WriteLine($"[FATAL/UNSENDABLE] Context: {context}, Terminating: {isTerminating}\n{ex}");
                return;
            }

            try
            {
                var payload = new ErrorMessage(
                    Message: ex.Message,
                    ExceptionType: ex.GetType().FullName,
                    StackTrace: ex.ToString(),
                    Context: isTerminating ? $"{context} (IsTerminating=true)" : context,
                    Level: LogLevel.Error
                );
                var envelope = new IpcEnvelope(IpcTypes.Error, "", Json.ToJson(payload));

                _client.SendAsync(envelope).Wait(TimeSpan.FromMilliseconds(100));
            }
            catch (Exception sendEx)
            {
                Debug.WriteLine($"[FATAL/SEND_FAILED] Failed to send error report. Original error: {ex}\nSend error: {sendEx}");
            }
        }

        private void CleanupTempCacheRecursive()
        {
            try
            {
                var root = Path.Combine(Path.GetTempPath(), "ac_native_cache");
                if (!Directory.Exists(root)) return;

                foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
                {
                    try { Directory.Delete(dir, recursive: true); } catch (Exception ex) { Debug.WriteLine($"[Cleanup] Delete dir failed: {ex.Message}"); }
                }
                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                {
                    try { File.Delete(file); } catch (Exception ex) { Debug.WriteLine($"[Cleanup] Delete file failed: {ex.Message}"); }
                }
                try { Directory.Delete(root, recursive: true); } catch (Exception ex) { Debug.WriteLine($"[Cleanup] Delete root failed: {ex.Message}"); }
            }
            catch (Exception ex) { Debug.WriteLine($"[CleanupTempCache] {ex.Message}"); }
        }

        private bool TryBuildBundleFromAssets(
            string? desiredMainAssemblyFileName,
            out byte[]? mainAssemblyBytes,
            out IDictionary<string, byte[]> managedByFullName,
            out IDictionary<string, byte[]> nativeByBasename)
        {
            mainAssemblyBytes = null;
            // The live collections, not copies: the loaded plugin must also find dependencies that
            // arrive later — a hot reload that brings a new package streams them before the update.
            managedByFullName = _managedByFullName;
            nativeByBasename = _nativeByBasename;

            if (!string.IsNullOrWhiteSpace(desiredMainAssemblyFileName))
            {
                var fileOnly = Path.GetFileName(desiredMainAssemblyFileName);
                var simple = Path.GetFileNameWithoutExtension(fileOnly);

                if (_simpleToFull.TryGetValue(simple, out var full) &&
                    _managedByFullName.TryGetValue(full, out var b))
                { mainAssemblyBytes = b; return true; }

                foreach (var kv in _managedByFullName)
                {
                    var fn = PeUtils.TryGetAssemblyName(kv.Value)?.Name;
                    if (!string.IsNullOrEmpty(fn) && fn.Equals(simple, StringComparison.OrdinalIgnoreCase))
                    { mainAssemblyBytes = kv.Value; return true; }
                }
            }

            if (_managedByFullName.Count > 0)
            {
                mainAssemblyBytes = _managedByFullName.First().Value;
                return true;
            }

            return false;
        }

        public async Task NotifyFirstChildVisibility()
        {
            if (_client == null) return;

            var msg = new NotifyFirstChildVisibility(0);
            await SafeSendAsync(new IpcEnvelope(IpcTypes.NotifyFirstChildVisibility, "", Json.ToJson(msg)));
        }

        public async Task NotifyParentAboutActivation()
        {
            if (_client == null) return;

            var msg = new IAmActivated(0);
            await SafeSendAsync(new IpcEnvelope(IpcTypes.ChildActivated, "", Json.ToJson(msg)));
        }

        // =======================================================
        // Visual Tree Inspection
        // =======================================================
        private int _serializedNodeCount;

        private void HardExitNow()
        {
            try
            {
                Dispatcher.UIThread.Post(() =>
                    (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown()
                );
            }
            catch (Exception ex) { Debug.WriteLine($"[HardExit] {ex.Message}"); }

            Environment.Exit(0);
        }
    }
}
