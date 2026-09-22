using Neo.IPC;
using Neo.Shared;
using System.Windows.Threading;
using System.Windows;

using System.Diagnostics;
using System.Collections.Concurrent;
using System.Text;
using System.Threading;
using System.IO;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Controls;
using System.Reflection;
using System.Globalization;

namespace Neo.PluginWindowWPF
{
    public partial class App : Application
    {
        private PipeClient? _client;
        private readonly SemaphoreSlim _sendLock = new(1, 1);

        private CancellationTokenSource _ipcCts = new();
        private DispatcherTimer? _hb;

        public MainWindow MainWin { get; private set; } = null!;

        // Managed: Indexe nach FullName und SimpleName (Simple -> Full für Fallback)
        private readonly ConcurrentDictionary<string, byte[]> _managedByFullName =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, string> _simpleToFull =
            new(StringComparer.OrdinalIgnoreCase);

        // Native: Pfad/Dateiname -> Bytes
        private readonly ConcurrentDictionary<string, byte[]> _nativeByPath =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, byte[]> _nativeByBasename =
            new(StringComparer.OrdinalIgnoreCase);

        // Für laufende BLOB-Transfers
        private readonly ConcurrentDictionary<Guid, MemoryStream> _blobStreams = new();
        private readonly ConcurrentDictionary<Guid, BlobStartMeta> _blobMetas = new();

        // Web Bridge
        private WebBridgeServer? _webBridge;

        // =======================================================

        // Embedded children must not pull focus away from the host's prompt box.
        private bool _isStandalone;

        protected override async void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            var ac = Environment.GetEnvironmentVariable("AC_WORK");
            if (!string.IsNullOrWhiteSpace(ac))
            {
                var acTemp = Path.Combine(ac, "temp");
                try { Directory.CreateDirectory(acTemp); } catch (Exception ex) { Debug.WriteLine($"[Init] CreateDirectory failed: {ex.Message}"); }
                Environment.SetEnvironmentVariable("TEMP", acTemp);
                Environment.SetEnvironmentVariable("TMP", acTemp);
            }

            CleanupTempCacheRecursive();

            // Exception Handler
            this.DispatcherUnhandledException += MainWindow_DispatcherUnhandledException;
            TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;

            // Check if running standalone (from MCP server) or embedded (from WPF host)
            bool isStandalone = e.Args.Any(a => string.Equals(a, "--standalone", StringComparison.OrdinalIgnoreCase));

            var hostOptions = PluginHost.Options;
            _isStandalone = isStandalone;

            MainWin = new MainWindow();
            hostOptions.MainWindowCreated?.Invoke(MainWin);
            if (isStandalone)
            {
                MainWin.Title = hostOptions.StandaloneWindowTitle;
                MainWin.Width = 800;
                MainWin.Height = 600;
                MainWin.ShowInTaskbar = true;
                MainWin.WindowStyle = WindowStyle.SingleBorderWindow;
                MainWin.ResizeMode = ResizeMode.CanResize;
                MainWin.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                try
                {
                    var stream = typeof(App).Assembly.GetManifestResourceStream("icon.ico");
                    if (stream != null)
                    {
                        var icon = new System.Windows.Media.Imaging.BitmapImage();
                        icon.BeginInit();
                        icon.StreamSource = stream;
                        icon.CacheOption = BitmapCacheOption.OnLoad;
                        icon.EndInit();
                        MainWin.Icon = icon;
                    }
                }
                catch { /* icon not critical */ }
            }
            else
            {
                MainWin.ShowInTaskbar = false;
                MainWin.WindowStyle = WindowStyle.None;
                MainWin.ResizeMode = ResizeMode.NoResize;
            }

            // Ctrl+Shift+N: the user calls the host up (or puts it away).
            MainWin.HostToggleRequested += () => _ = SafeSendAsync(new IpcEnvelope(IpcTypes.ToggleHostWindow, "", "{}"));
            for (int i = 0; i < e.Args.Length - 1; i++)
                if (string.Equals(e.Args[i], "--hint", StringComparison.OrdinalIgnoreCase))
                    MainWin.Hint = e.Args[i + 1];

            this.MainWindow = MainWin;
            MainWin.Show();

            // HWND ermitteln
            var helper = new System.Windows.Interop.WindowInteropHelper(MainWin);
            var hwnd = helper.EnsureHandle();
            if (hwnd == IntPtr.Zero)
            {
                Shutdown();
                return;
            }

            // Pipe-Name aus Args
            string? pipeName = GetPipeNameFromArgs(e.Args);
            if (string.IsNullOrWhiteSpace(pipeName))
            {
                Shutdown();
                return;
            }

            // Verbinden
            _client = new PipeClient(pipeName);
            try
            {
                await _client.ConnectAsync();
            }
            catch
            {
                Shutdown();
                return;
            }

            // Hello mit HWND
            await SafeSendAsync(new IpcEnvelope(
                IpcTypes.Hello,
                Guid.NewGuid().ToString("N"),
                Json.ToJson(new HelloMessage("Child", Environment.ProcessId, Hwnd: hwnd.ToInt64(),
                    HotReloadCapabilities: HotReloadRuntime.Capabilities))
            ));

            // Framed Receive-Loop starten
            _ = Task.Run(() => FramedListenLoopAsync(_ipcCts.Token));

            // Heartbeat
            _hb = new DispatcherTimer();
            _hb.Interval = TimeSpan.FromSeconds(2);
            _hb.Tick += (sender, e) =>
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
        }

        protected override async void OnExit(ExitEventArgs e)
        {
            try { _hb?.Stop(); } catch (Exception ex) { Debug.WriteLine($"[OnExit] Timer stop failed: {ex.Message}"); }
            try { _ipcCts.Cancel(); } catch (Exception ex) { Debug.WriteLine($"[OnExit] CTS cancel failed: {ex.Message}"); }
            try { if (_client != null) await _client.DisposeAsync(); } catch (Exception ex) { Debug.WriteLine($"[OnExit] Client dispose failed: {ex.Message}"); }
            base.OnExit(e);
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
                        var ms = meta.Length is long L && L >= 0 ? new MemoryStream(capacity: (int)Math.Min(L, int.MaxValue))
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
                await Dispatcher.InvokeAsync(Shutdown);
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

                            await Dispatcher.InvokeAsync(() =>
                            {
                                MainWin.HandleLoadUserControlFromBytes(
                                    mainAssemblyBytes: mainBytes,
                                    explicitControlTypeName: string.IsNullOrWhiteSpace(req.TypeName) ? null : req.TypeName,
                                    managedAssembliesByFullName: managedByFull,
                                    nativeLibrariesByBasename: nativeByBase
                                );
                                if (_isStandalone)
                                    MainWin.Activate();
                            });

                            await SendLogAsync(LogLevel.Info, "LoadControl executed.", "Child", "Mode=Bytes(In-Memory)");
                            await SafeSendAsync(new IpcEnvelope(
                                IpcTypes.Ack, env.CorrelationId,
                                Json.ToJson(new AckMessage("Loaded (bytes/in-memory)"))
                            ));
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
                        await Dispatcher.BeginInvoke(() => MainWin.HandleFullscreenMouse(msg.isCursorVisible));
                        await SafeSendAsync(new IpcEnvelope(
                            IpcTypes.Ack, env.CorrelationId,
                            Json.ToJson(new AckMessage("Cursor Changed!"))
                        ));
                        break;
                    }

                case IpcTypes.SetChildModal:
                    {
                        var modalMsg = Json.FromJson<IsChildModal>(env.PayloadJson)!;
                        await Dispatcher.BeginInvoke(() => MainWin.HandleChildModality(modalMsg.isModal));
                        await SafeSendAsync(new IpcEnvelope(
                            IpcTypes.Ack, env.CorrelationId,
                            Json.ToJson(new AckMessage("Modal Changed!"))
                        ));
                        break;
                    }

                case IpcTypes.PositionWindow:
                    {
                        var bounds = Json.FromJson<ParentWindowBoundsMessage>(env.PayloadJson);
                        if (bounds != null)
                        {
                            await Dispatcher.InvokeAsync(() =>
                            {
                                MainWin.Left = bounds.X;
                                MainWin.Top = bounds.Y;
                                MainWin.Width = bounds.Width;
                                MainWin.Height = bounds.Height;

                                if (!MainWin.IsVisible)
                                    MainWin.Show();

                                if (MainWin.HasEverLoadedControl)
                                    MainWin.WaitOverlay.Visibility = Visibility.Collapsed;
                            });
                        }

                        await SafeSendAsync(new IpcEnvelope(
                            IpcTypes.Ack, env.CorrelationId,
                            Json.ToJson(new AckMessage("Window positioned."))));
                        break;
                    }

                case IpcTypes.ParentWindowBounds:
                    {
                        var bounds = Json.FromJson<ParentWindowBoundsMessage>(env.PayloadJson);
                        if (bounds != null)
                        {
                            await Dispatcher.InvokeAsync(() =>
                            {
                                if (!bounds.IsVisible)
                                {
                                    MainWin.ShowWaitOverlay("Generating...");
                                }
                                else
                                {
                                    MainWin.StopWaitTimer();
                                    MainWin.WaitTimerText.Text = "";
                                    if (MainWin.HasEverLoadedControl)
                                        MainWin.WaitOverlay.Visibility = Visibility.Collapsed;
                                    else
                                        MainWin.WaitStatusText.Text = "Waiting for code...";

                                    int dockX = (int)(bounds.X + bounds.Width + 8);
                                    int dockY = (int)bounds.Y;
                                    int dockH = (int)bounds.Height;

                                    MainWin.Left = dockX;
                                    MainWin.Top = dockY;
                                    MainWin.Height = dockH;

                                    if (!MainWin.IsVisible)
                                        MainWin.Show();
                                }
                            });
                        }
                        break;
                    }

                case IpcTypes.ToggleChildFullScreen:
                    {
                        await Dispatcher.InvokeAsync(() =>
                        {
                            if (MainWin.WindowState == WindowState.Maximized && MainWin.WindowStyle == WindowStyle.None)
                            {
                                MainWin.WindowStyle = WindowStyle.SingleBorderWindow;
                                MainWin.WindowState = WindowState.Normal;
                            }
                            else
                            {
                                MainWin.WindowStyle = WindowStyle.None;
                                MainWin.WindowState = WindowState.Maximized;
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
                            var details = await Dispatcher.InvokeAsync(() => MainWin.HandleHotReload(request));
                            result = new HotReloadResultMessage(true, Details: details);
                            await SendLogAsync(LogLevel.Info, "Hot reload applied: " + details, "Child");
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
                        await Dispatcher.InvokeAsync(() => MainWin.UnloadUserControlPlugin());
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
                            var base64 = await Dispatcher.InvokeAsync(() =>
                            {
                                var target = MainWin;
                                int width = Math.Max(1, (int)target.ActualWidth);
                                int height = Math.Max(1, (int)target.ActualHeight);

                                var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                                rtb.Render(target);

                                var encoder = new PngBitmapEncoder();
                                encoder.Frames.Add(BitmapFrame.Create(rtb));

                                using var ms = new MemoryStream();
                                encoder.Save(ms);
                                return Convert.ToBase64String(ms.ToArray());
                            });

                            var result = new ScreenshotResultMessage(
                                base64,
                                (int)MainWin.ActualWidth,
                                (int)MainWin.ActualHeight);

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
                            var result = await Dispatcher.InvokeAsync(() => SetPropertyOnControl(req));
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
                            var json = await Dispatcher.InvokeAsync(() =>
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
                            var result = await Dispatcher.InvokeAsync(() => InjectDataOnControl(req));
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
                            var result = await Dispatcher.InvokeAsync(() => ReadDataFromControl(req));
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
                            var code = await Dispatcher.InvokeAsync(() =>
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
                            _webBridge?.Dispose();
                            _webBridge = new WebBridgeServer();
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

                default:
                    await SafeSendAsync(new IpcEnvelope(
                        IpcTypes.Ack, env.CorrelationId,
                        Json.ToJson(new AckMessage($"Unknown command '{env.Type}' ignored by Child"))
                    ));
                    break;
            }
        }

        // =======================================================
        // Visual Tree Inspection (WPF)
        // =======================================================
        private int _serializedNodeCount;

        // =======================================================
        // Extract Code from Visual Tree (best-effort)
        // =======================================================
        public string ExtractCodeFromVisualTree(FrameworkElement root)
        {
            // WPF has no visual-tree code extraction; a head that compiles code itself
            // (Smart Edit in the MCP head) can supply the last source it built.
            var lastCompiled = PluginHost.Options.LastCompiledCodeProvider?.Invoke();
            if (lastCompiled != null)
                return lastCompiled;

            return "// Code extraction not available — use extract_code via MCP when SmartCompiler has compiled code.";
        }

        // =======================================================
        // Utility
        // =======================================================
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
            try { await _client.SendAsync(env); }
            finally { _sendLock.Release(); }
        }

        private Task SendLogAsync(LogLevel level, string message, string? category = null, string? details = null)
        {
            if (_client == null) return Task.CompletedTask;
            var log = new LogMessage(level, message, category, details);
            return SafeSendAsync(new IpcEnvelope(IpcTypes.Log, "", Json.ToJson(log)));
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

        private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            var ex = e.ExceptionObject as Exception ?? new Exception("Non-exception thrown.");
            TrySendErrorSynchronous_BestEffort("UnhandledException", ex, e.IsTerminating);
        }

        private void MainWindow_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
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
                _client.SendAsync(new IpcEnvelope(IpcTypes.Error, "", Json.ToJson(payload)))
                    .Wait(TimeSpan.FromMilliseconds(100));
            }
            catch (Exception sendEx)
            {
                Debug.WriteLine($"[FATAL/SEND_FAILED] Original: {ex}\nSend error: {sendEx}");
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
                    try { Directory.Delete(dir, recursive: true); } catch { }
                }
                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                {
                    try { File.Delete(file); } catch { }
                }
                try { Directory.Delete(root, recursive: true); } catch { }
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
            await SafeSendAsync(new IpcEnvelope(IpcTypes.NotifyFirstChildVisibility, "", Json.ToJson(new NotifyFirstChildVisibility(0))));
        }

        public async Task NotifyParentAboutActivation()
        {
            if (_client == null) return;
            await SafeSendAsync(new IpcEnvelope(IpcTypes.ChildActivated, "", Json.ToJson(new IAmActivated(0))));
        }

        private void HardExitNow()
        {
            try { Dispatcher.Invoke(() => Shutdown()); } catch { }
            Environment.Exit(0);
        }
    }
}
