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

            MainWin = new MainWindow();
            if (isStandalone)
            {
                // Standalone mode: full window with decorations
                MainWin.Title = "N.E.O. — Live Preview";
                MainWin.Width = 800;
                MainWin.Height = 600;
                MainWin.ShowInTaskbar = true;
                MainWin.SystemDecorations = SystemDecorations.Full;
                MainWin.CanResize = true;
                MainWin.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                try
                {
                    var stream = typeof(App).Assembly.GetManifestResourceStream("icon.ico");
                    if (stream != null)
                        MainWin.Icon = new WindowIcon(stream);
                }
                catch { /* icon not critical */ }
            }
            else
            {
                // Embedded mode: no decorations (WPF host controls position/size via HWND)
                MainWin.ShowInTaskbar = false;
                MainWin.SystemDecorations = SystemDecorations.None;
                MainWin.CanResize = false;
            }

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
                    Json.ToJson(new HelloMessage("Child", Environment.ProcessId, Hwnd: handle == IntPtr.Zero ? 0 : handle.ToInt64()))
                ));

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

                case IpcTypes.SetDesignerMode:
                    {
                        var designerMsg = Json.FromJson<SetDesignerModeMessage>(env.PayloadJson)!;

                        Dispatcher.UIThread.Post(() =>
                        {
                            MainWin.SetDesignerMode(designerMsg.Enabled);
                        });

                        await SafeSendAsync(new IpcEnvelope(
                            IpcTypes.Ack, env.CorrelationId,
                            Json.ToJson(new AckMessage("Designer mode updated."))
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

                                    // Dock to the right edge of parent, same height
                                    int dockX = (int)(bounds.X + bounds.Width + 8); // 8px gap
                                    int dockY = (int)bounds.Y;
                                    int dockH = (int)bounds.Height;

                                    MainWin.Position = new PixelPoint(dockX, dockY);
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
                        Dispatcher.UIThread.Post(() =>
                        {
                            if (MainWin.WindowState == WindowState.FullScreen)
                            {
                                MainWin.WindowState = WindowState.Normal;
                                MainWin.SystemDecorations = SystemDecorations.Full;
                            }
                            else
                            {
                                MainWin.SystemDecorations = SystemDecorations.None;
                                MainWin.WindowState = WindowState.FullScreen;
                            }
                        });

                        await SafeSendAsync(new IpcEnvelope(
                            IpcTypes.Ack, env.CorrelationId,
                            Json.ToJson(new AckMessage("Fullscreen toggled."))
                        ));
                        break;
                    }

                case IpcTypes.UnloadControl:
                    {
                        await Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            MainWin.UnloadUserControlPlugin();
                        });

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
                                bitmap.Save(ms);
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
                                var root = MainWin.dynamicContent.Content as Avalonia.Controls.Control;
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
                                var root = MainWin.dynamicContent.Content as Avalonia.Controls.Control;
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
            out Dictionary<string, byte[]> managedByFullName,
            out Dictionary<string, byte[]> nativeByBasename)
        {
            mainAssemblyBytes = null;
            managedByFullName = new(_managedByFullName, StringComparer.OrdinalIgnoreCase);
            nativeByBasename = new(_nativeByBasename, StringComparer.OrdinalIgnoreCase);

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

        public Task NotifyParentDesignerSelection(DesignerSelectionMessage selection)
        {
            if (_client == null) return Task.CompletedTask;
            return SafeSendAsync(new IpcEnvelope(IpcTypes.DesignerSelection, "", Json.ToJson(selection)));
        }

        // =======================================================
        // Visual Tree Inspection
        // =======================================================
        private int _serializedNodeCount;

        private string SerializeVisualTree(Avalonia.Controls.Control control, int depth)
        {
            const int maxDepth = 15;
            const int maxNodes = 500;
            if (depth > maxDepth || _serializedNodeCount > maxNodes)
                return "{ \"type\": \"...\", \"truncated\": true }";
            _serializedNodeCount++;

            var sb = new StringBuilder();
            sb.Append("{ ");

            // Type
            sb.Append($"\"type\": \"{control.GetType().Name}\"");

            // Name
            if (!string.IsNullOrEmpty(control.Name))
                sb.Append($", \"name\": \"{EscapeJson(control.Name)}\"");

            // Key properties based on control type
            var props = new Dictionary<string, string>();
            CollectKeyProperties(control, props);
            if (props.Count > 0)
            {
                sb.Append(", \"properties\": { ");
                sb.Append(string.Join(", ", props.Select(kv =>
                    $"\"{EscapeJson(kv.Key)}\": \"{EscapeJson(kv.Value)}\"")));
                sb.Append(" }");
            }

            // Bounds
            var bounds = control.Bounds;
            if (bounds.Width > 0 || bounds.Height > 0)
                sb.Append($", \"bounds\": {{ \"x\": {bounds.X:F0}, \"y\": {bounds.Y:F0}, \"w\": {bounds.Width:F0}, \"h\": {bounds.Height:F0} }}");

            // Children
            var children = control.GetVisualChildren()
                .OfType<Avalonia.Controls.Control>()
                .ToList();

            if (children.Count > 0)
            {
                sb.Append(", \"children\": [ ");
                sb.Append(string.Join(", ", children.Select(c => SerializeVisualTree(c, depth + 1))));
                sb.Append(" ]");
            }

            sb.Append(" }");
            return sb.ToString();
        }

        private static void CollectKeyProperties(Avalonia.Controls.Control control, Dictionary<string, string> props)
        {
            // Text content
            if (control is Avalonia.Controls.TextBlock tb)
            {
                if (tb.Text != null) props["Text"] = tb.Text;
                props["FontSize"] = tb.FontSize.ToString();
                if (tb.Foreground != null) props["Foreground"] = tb.Foreground.ToString()!;
                if (tb.FontWeight != default) props["FontWeight"] = tb.FontWeight.ToString();
            }
            else if (control is Avalonia.Controls.Button btn)
            {
                if (btn.Content != null) props["Content"] = btn.Content.ToString()!;
                props["IsEnabled"] = btn.IsEnabled.ToString();
                if (btn.Background != null) props["Background"] = btn.Background.ToString()!;
            }
            else if (control is Avalonia.Controls.TextBox txb)
            {
                if (txb.Text != null) props["Text"] = txb.Text;
                if (txb.Watermark != null) props["Watermark"] = txb.Watermark;
                props["IsReadOnly"] = txb.IsReadOnly.ToString();
            }
            else if (control is Avalonia.Controls.ListBox lb)
            {
                props["ItemCount"] = lb.ItemCount.ToString();
                if (lb.SelectedIndex >= 0) props["SelectedIndex"] = lb.SelectedIndex.ToString();
            }
            else if (control is Avalonia.Controls.ComboBox cb)
            {
                props["ItemCount"] = cb.ItemCount.ToString();
                if (cb.SelectedIndex >= 0) props["SelectedIndex"] = cb.SelectedIndex.ToString();
            }
            else if (control is Avalonia.Controls.CheckBox chk)
            {
                props["IsChecked"] = chk.IsChecked?.ToString() ?? "null";
                if (chk.Content != null) props["Content"] = chk.Content.ToString()!;
            }
            else if (control is Avalonia.Controls.Slider sl)
            {
                props["Value"] = sl.Value.ToString();
                props["Minimum"] = sl.Minimum.ToString();
                props["Maximum"] = sl.Maximum.ToString();
            }
            else if (control is Avalonia.Controls.Image img)
            {
                props["Source"] = img.Source?.ToString() ?? "null";
            }
            else if (control is Avalonia.Controls.ProgressBar pb)
            {
                props["Value"] = pb.Value.ToString();
                props["IsIndeterminate"] = pb.IsIndeterminate.ToString();
            }

            // Common layout properties for all controls
            if (control.IsVisible == false) props["IsVisible"] = "false";
            if (control.Opacity < 1.0) props["Opacity"] = control.Opacity.ToString("F2");
            try
            {
                var bg = (control as Avalonia.Controls.Primitives.TemplatedControl)?.Background
                      ?? (control as Avalonia.Controls.Panel)?.Background;
                if (bg != null && !props.ContainsKey("Background"))
                    props["Background"] = bg.ToString()!;
            }
            catch { }
        }

        private static string EscapeJson(string s) =>
            s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");

        // =======================================================
        // Live Property Editing (no recompile)
        // =======================================================
        private SetPropertyResultMessage SetPropertyOnControl(SetPropertyRequest req)
        {
            // Find the loaded UserControl content
            var root = MainWin.dynamicContent.Content as Avalonia.Controls.Control;
            if (root == null)
                return new SetPropertyResultMessage(false, "No control loaded.");

            // Find target control(s) in visual tree
            var target = FindControlInTree(root, req.Target);
            if (target == null)
                return new SetPropertyResultMessage(false, $"Control '{req.Target}' not found in visual tree.");

            // Find the AvaloniaProperty
            var avProp = FindAvaloniaProperty(target, req.PropertyName);
            if (avProp == null)
            {
                // Fallback: try CLR property via reflection
                var clrProp = target.GetType().GetProperty(req.PropertyName,
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase);
                if (clrProp != null && clrProp.CanWrite)
                {
                    var oldVal = clrProp.GetValue(target)?.ToString();
                    var newVal = ParsePropertyValue(clrProp.PropertyType, req.Value);
                    clrProp.SetValue(target, newVal);
                    return new SetPropertyResultMessage(true, $"Set {clrProp.Name} via reflection.",
                        oldVal, newVal?.ToString());
                }
                return new SetPropertyResultMessage(false, $"Property '{req.PropertyName}' not found on {target.GetType().Name}.");
            }

            // Parse and set value
            var oldValue = target.GetValue(avProp)?.ToString();
            var parsed = ParsePropertyValue(avProp.PropertyType, req.Value);
            target.SetValue(avProp, parsed);
            var newValue = target.GetValue(avProp)?.ToString();

            return new SetPropertyResultMessage(true,
                $"Set {target.GetType().Name}.{avProp.Name} = {req.Value}",
                oldValue, newValue);
        }

        private static Avalonia.Controls.Control? FindControlInTree(Avalonia.Controls.Control root, string target)
        {
            // Collect all controls in the visual tree
            var all = new List<Avalonia.Controls.Control>();
            CollectControls(root, all);

            // 1. Try by Name
            var byName = all.FirstOrDefault(c => string.Equals(c.Name, target, StringComparison.OrdinalIgnoreCase));
            if (byName != null) return byName;

            // 2. Try by Type:Index (e.g. "TextBlock:0", "Button:2")
            var parts = target.Split(':', 2);
            var typeName = parts[0];
            int index = parts.Length > 1 && int.TryParse(parts[1], out var idx) ? idx : 0;

            var byType = all.Where(c => c.GetType().Name.Equals(typeName, StringComparison.OrdinalIgnoreCase)).ToList();
            if (index < byType.Count) return byType[index];

            // 3. Try by Type alone (first match)
            if (byType.Count > 0) return byType[0];

            // 4. Partial name match
            var partial = all.FirstOrDefault(c => c.Name != null &&
                c.Name.Contains(target, StringComparison.OrdinalIgnoreCase));
            if (partial != null) return partial;

            return null;
        }

        private static void CollectControls(Avalonia.Visual visual, List<Avalonia.Controls.Control> result)
        {
            if (visual is Avalonia.Controls.Control ctrl)
                result.Add(ctrl);
            var children = visual.GetVisualChildren();
            foreach (var child in children)
            {
                if (child is Avalonia.Visual v)
                    CollectControls(v, result);
            }
        }

        private static AvaloniaProperty? FindAvaloniaProperty(Avalonia.Controls.Control control, string propertyName)
        {
            // Search registered Avalonia properties on the control type
            var props = AvaloniaPropertyRegistry.Instance.GetRegistered(control.GetType());
            return props.FirstOrDefault(p => p.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase));
        }

        private static object? ParsePropertyValue(Type targetType, string value)
        {
            // IBrush / Color
            if (targetType == typeof(Avalonia.Media.IBrush) || targetType == typeof(Avalonia.Media.Brush)
                || targetType == typeof(Avalonia.Media.ISolidColorBrush))
            {
                if (Avalonia.Media.Color.TryParse(value, out var color))
                    return new Avalonia.Media.SolidColorBrush(color);
                // Try named brush via reflection (e.g. "Red" → Brushes.Red)
                var brushProp = typeof(Avalonia.Media.Brushes).GetProperty(value,
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.IgnoreCase);
                if (brushProp != null) return brushProp.GetValue(null);
                return new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(value));
            }

            // Thickness
            if (targetType == typeof(Avalonia.Thickness))
            {
                var nums = value.Split(',').Select(s => double.Parse(s.Trim())).ToArray();
                return nums.Length switch
                {
                    1 => new Avalonia.Thickness(nums[0]),
                    2 => new Avalonia.Thickness(nums[0], nums[1]),
                    4 => new Avalonia.Thickness(nums[0], nums[1], nums[2], nums[3]),
                    _ => new Avalonia.Thickness(nums[0])
                };
            }

            // CornerRadius
            if (targetType == typeof(Avalonia.CornerRadius))
            {
                var nums = value.Split(',').Select(s => double.Parse(s.Trim())).ToArray();
                return nums.Length switch
                {
                    1 => new Avalonia.CornerRadius(nums[0]),
                    4 => new Avalonia.CornerRadius(nums[0], nums[1], nums[2], nums[3]),
                    _ => new Avalonia.CornerRadius(nums[0])
                };
            }

            // FontWeight
            if (targetType == typeof(Avalonia.Media.FontWeight))
            {
                if (Enum.TryParse<Avalonia.Media.FontWeight>(value, true, out var fw))
                    return fw;
            }

            // Enum
            if (targetType.IsEnum)
            {
                if (Enum.TryParse(targetType, value, true, out var enumVal))
                    return enumVal;
            }

            // Nullable<T>
            var underlying = Nullable.GetUnderlyingType(targetType);
            if (underlying != null)
                return ParsePropertyValue(underlying, value);

            // Primitives
            if (targetType == typeof(double)) return double.Parse(value);
            if (targetType == typeof(float)) return float.Parse(value);
            if (targetType == typeof(int)) return int.Parse(value);
            if (targetType == typeof(bool)) return bool.Parse(value);
            if (targetType == typeof(string)) return value;

            // Last resort: TypeConverter
            var converter = System.ComponentModel.TypeDescriptor.GetConverter(targetType);
            if (converter.CanConvertFrom(typeof(string)))
                return converter.ConvertFromString(value);

            return value;
        }

        // =======================================================
        // Data Injection + Reading
        // =======================================================
        private InjectDataResult InjectDataOnControl(InjectDataRequest req)
        {
            var root = MainWin.dynamicContent.Content as Avalonia.Controls.Control;
            if (root == null)
                return new InjectDataResult(false, "No control loaded.");

            if (string.Equals(req.Mode, "fill", StringComparison.OrdinalIgnoreCase))
                return InjectFillMode(root, req);

            // replace / append mode — target must be an items control
            var target = string.Equals(req.Target, "root", StringComparison.OrdinalIgnoreCase)
                ? root
                : FindControlInTree(root, req.Target);

            if (target == null)
                return new InjectDataResult(false, $"Control '{req.Target}' not found.");

            if (target is not Avalonia.Controls.ItemsControl itemsControl)
                return new InjectDataResult(false,
                    $"Control '{target.GetType().Name}' is not an ItemsControl. Use mode 'fill' for form controls.");

            // Parse JSON array into List<Dictionary<string, object>>
            var items = new List<Dictionary<string, object>>();
            string[] fields;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(req.DataJson);
                if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array)
                    return new InjectDataResult(false, "DataJson must be a JSON array for replace/append mode.");

                foreach (var elem in doc.RootElement.EnumerateArray())
                {
                    if (elem.ValueKind == System.Text.Json.JsonValueKind.Object)
                        items.Add(JsonElementToDict(elem));
                    else
                        items.Add(new Dictionary<string, object> { ["value"] = elem.ToString() });
                }

                // Collect all unique field names
                var fieldSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in items)
                    foreach (var key in item.Keys)
                        fieldSet.Add(key);
                fields = fieldSet.ToArray();
            }
            catch (System.Text.Json.JsonException ex)
            {
                return new InjectDataResult(false, $"Invalid JSON: {ex.Message}");
            }

            bool isAppend = string.Equals(req.Mode, "append", StringComparison.OrdinalIgnoreCase);

            if (isAppend && itemsControl.ItemsSource is System.Collections.ObjectModel.ObservableCollection<Dictionary<string, object>> existing)
            {
                // Append to existing ObservableCollection
                foreach (var item in items)
                    existing.Add(item);
            }
            else
            {
                // Replace (or first-time set)
                var collection = new System.Collections.ObjectModel.ObservableCollection<Dictionary<string, object>>();
                if (isAppend && itemsControl.ItemsSource is System.Collections.IEnumerable oldItems)
                {
                    // Migrate existing items into new ObservableCollection
                    foreach (var old in oldItems)
                    {
                        if (old is Dictionary<string, object> dict)
                            collection.Add(dict);
                    }
                }
                foreach (var item in items)
                    collection.Add(item);
                itemsControl.ItemsSource = collection;
            }

            // Auto-generate ItemTemplate if needed
            if (req.AutoTemplate && itemsControl.ItemTemplate == null && fields.Length > 0)
            {
                var displayFields = !string.IsNullOrWhiteSpace(req.FocusFields)
                    ? req.FocusFields.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    : fields;

                itemsControl.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<object>((item, _) =>
                {
                    var panel = new Avalonia.Controls.StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        Spacing = 16,
                        Margin = new Avalonia.Thickness(4, 2)
                    };
                    foreach (var field in displayFields)
                    {
                        var tb = new Avalonia.Controls.TextBlock { FontSize = 13 };
                        tb.Bind(Avalonia.Controls.TextBlock.TextProperty,
                            new Avalonia.Data.Binding($"[{field}]"));
                        panel.Children.Add(tb);
                    }
                    return panel;
                }, supportsRecycling: true);
            }

            int totalCount = 0;
            if (itemsControl.ItemsSource is System.Collections.IEnumerable countable)
                foreach (var _ in countable) totalCount++;

            return new InjectDataResult(true,
                $"{(isAppend ? "Appended" : "Replaced")} {items.Count} items on {target.GetType().Name}.",
                totalCount, fields);
        }

        private InjectDataResult InjectFillMode(Avalonia.Controls.Control root, InjectDataRequest req)
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(req.DataJson);
                if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
                    return new InjectDataResult(false, "DataJson must be a JSON object for fill mode.");

                int filled = 0;
                var errors = new List<string>();

                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    var control = FindControlInTree(root, prop.Name);
                    if (control == null) { errors.Add($"'{prop.Name}' not found"); continue; }

                    try
                    {
                        switch (control)
                        {
                            case Avalonia.Controls.TextBox tb:
                                tb.Text = prop.Value.GetString() ?? prop.Value.GetRawText();
                                break;
                            case Avalonia.Controls.TextBlock tb:
                                tb.Text = prop.Value.GetString() ?? prop.Value.GetRawText();
                                break;
                            case Avalonia.Controls.CheckBox cb:
                                cb.IsChecked = prop.Value.ValueKind == System.Text.Json.JsonValueKind.True;
                                break;
                            case Avalonia.Controls.Slider sl:
                                sl.Value = prop.Value.GetDouble();
                                break;
                            case Avalonia.Controls.ComboBox combo:
                                if (prop.Value.ValueKind == System.Text.Json.JsonValueKind.Number)
                                    combo.SelectedIndex = prop.Value.GetInt32();
                                else
                                {
                                    var text = prop.Value.GetString();
                                    for (int i = 0; i < combo.ItemCount; i++)
                                        if (combo.Items[i]?.ToString() == text) { combo.SelectedIndex = i; break; }
                                }
                                break;
                            case Avalonia.Controls.Primitives.ToggleButton ts:
                                ts.IsChecked = prop.Value.ValueKind == System.Text.Json.JsonValueKind.True;
                                break;
                            default:
                                // Fallback: try Text property via reflection
                                var textProp = control.GetType().GetProperty("Text");
                                if (textProp != null && textProp.CanWrite)
                                    textProp.SetValue(control, prop.Value.GetString() ?? prop.Value.GetRawText());
                                else
                                    errors.Add($"'{prop.Name}' ({control.GetType().Name}): unsupported type");
                                continue;
                        }
                        filled++;
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"'{prop.Name}': {ex.Message}");
                    }
                }

                var msg = $"Filled {filled} control(s).";
                if (errors.Count > 0) msg += $" Issues: {string.Join("; ", errors)}";
                return new InjectDataResult(true, msg, filled);
            }
            catch (System.Text.Json.JsonException ex)
            {
                return new InjectDataResult(false, $"Invalid JSON: {ex.Message}");
            }
        }

        private ReadDataResult ReadDataFromControl(ReadDataRequest req)
        {
            var root = MainWin.dynamicContent.Content as Avalonia.Controls.Control;
            if (root == null)
                return new ReadDataResult(false, "No control loaded.");

            var target = string.Equals(req.Target, "root", StringComparison.OrdinalIgnoreCase)
                ? root
                : FindControlInTree(root, req.Target);

            if (target == null)
                return new ReadDataResult(false, $"Control '{req.Target}' not found.");

            var scope = req.Scope?.ToLowerInvariant();

            // Auto-detect scope
            if (scope == null)
            {
                if (target is Avalonia.Controls.ItemsControl) scope = "items";
                else if (target is Avalonia.Controls.TextBox || target is Avalonia.Controls.CheckBox
                    || target is Avalonia.Controls.Slider) scope = "value";
                else scope = "form"; // container → read all named children
            }

            switch (scope)
            {
                case "items":
                    return ReadItemsData(target);
                case "form":
                    return ReadFormData(target);
                case "value":
                    return ReadSingleValue(target);
                default:
                    return new ReadDataResult(false, $"Unknown scope '{scope}'. Use 'items', 'form', or 'value'.");
            }
        }

        private ReadDataResult ReadItemsData(Avalonia.Controls.Control target)
        {
            if (target is not Avalonia.Controls.ItemsControl ic)
                return new ReadDataResult(false, $"{target.GetType().Name} is not an ItemsControl.");

            var items = new List<object>();
            if (ic.ItemsSource is System.Collections.IEnumerable source)
            {
                foreach (var item in source)
                {
                    if (item is Dictionary<string, object> dict)
                        items.Add(dict);
                    else
                        items.Add(item?.ToString() ?? "null");
                }
            }

            var json = System.Text.Json.JsonSerializer.Serialize(items, Json.Options);
            return new ReadDataResult(true, $"{items.Count} items read.", json);
        }

        private ReadDataResult ReadFormData(Avalonia.Controls.Control target)
        {
            var all = new List<Avalonia.Controls.Control>();
            CollectControls(target, all);

            var data = new Dictionary<string, object>();
            foreach (var ctrl in all)
            {
                if (string.IsNullOrEmpty(ctrl.Name)) continue;
                // Skip internal Avalonia PART_ controls
                if (ctrl.Name.StartsWith("PART_", StringComparison.Ordinal)) continue;

                object? value = ctrl switch
                {
                    Avalonia.Controls.TextBox tb => tb.Text,
                    Avalonia.Controls.TextBlock tb => tb.Text,
                    Avalonia.Controls.CheckBox cb => cb.IsChecked,
                    Avalonia.Controls.Slider sl => sl.Value,
                    Avalonia.Controls.ComboBox combo => combo.SelectedItem?.ToString(),
                    Avalonia.Controls.Primitives.ToggleButton ts => ts.IsChecked,
                    Avalonia.Controls.ProgressBar pb => pb.Value,
                    _ => null
                };

                if (value != null)
                    data[ctrl.Name] = value;
            }

            var json = System.Text.Json.JsonSerializer.Serialize(data, Json.Options);
            return new ReadDataResult(true, $"{data.Count} controls read.", json);
        }

        private ReadDataResult ReadSingleValue(Avalonia.Controls.Control target)
        {
            object? value = target switch
            {
                Avalonia.Controls.TextBox tb => tb.Text,
                Avalonia.Controls.TextBlock tb => tb.Text,
                Avalonia.Controls.CheckBox cb => cb.IsChecked,
                Avalonia.Controls.Slider sl => sl.Value,
                Avalonia.Controls.ComboBox combo => combo.SelectedItem?.ToString(),
                Avalonia.Controls.ListBox lb => $"[{lb.ItemCount} items, selected={lb.SelectedIndex}]",
                _ => null
            };

            if (value == null)
                return new ReadDataResult(false, $"Cannot read value from {target.GetType().Name}.");

            var json = System.Text.Json.JsonSerializer.Serialize(value, Json.Options);
            return new ReadDataResult(true, $"Value: {value}", json);
        }

        private static Dictionary<string, object> JsonElementToDict(System.Text.Json.JsonElement element)
        {
            var dict = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in element.EnumerateObject())
            {
                dict[prop.Name] = prop.Value.ValueKind switch
                {
                    System.Text.Json.JsonValueKind.String => prop.Value.GetString()!,
                    System.Text.Json.JsonValueKind.Number => prop.Value.TryGetInt64(out var l) ? (object)l : prop.Value.GetDouble(),
                    System.Text.Json.JsonValueKind.True => true,
                    System.Text.Json.JsonValueKind.False => false,
                    System.Text.Json.JsonValueKind.Null => "(null)",
                    _ => prop.Value.GetRawText()
                };
            }
            return dict;
        }

        // =======================================================
        // Code Extraction (Reverse-Engineering)
        // =======================================================
        private string ExtractCodeFromVisualTree(Avalonia.Controls.Control root)
        {
            var sb = new StringBuilder();
            sb.AppendLine("using Avalonia;");
            sb.AppendLine("using Avalonia.Controls;");
            sb.AppendLine("using Avalonia.Layout;");
            sb.AppendLine("using Avalonia.Media;");
            sb.AppendLine("using Avalonia.Controls.Primitives;");
            sb.AppendLine("using System;");
            sb.AppendLine("using System.Collections.Generic;");
            sb.AppendLine();
            sb.AppendLine("namespace DynamicApp;");
            sb.AppendLine();
            sb.AppendLine("public class DynamicUserControl : UserControl");
            sb.AppendLine("{");
            sb.AppendLine("    public DynamicUserControl()");
            sb.AppendLine("    {");

            // The root IS the DynamicUserControl — extract its Content
            if (root is Avalonia.Controls.UserControl uc && uc.Content is Avalonia.Controls.Control content)
            {
                sb.Append("        Content = ");
                EmitControl(content, sb, 2);
                sb.AppendLine(";");
            }

            sb.AppendLine("    }");
            sb.AppendLine("}");
            return sb.ToString();
        }

        private void EmitControl(Avalonia.Controls.Control ctrl, StringBuilder sb, int indent)
        {
            var pad = new string(' ', indent * 4);
            var typeName = ctrl.GetType().Name;

            // Skip internal Avalonia template parts
            if (IsTemplatePart(ctrl)) return;

            sb.AppendLine($"new {typeName}");
            sb.AppendLine($"{pad}{{");

            // Name
            if (!string.IsNullOrEmpty(ctrl.Name) && !ctrl.Name.StartsWith("PART_"))
                sb.AppendLine($"{pad}    Name = \"{EscapeString(ctrl.Name)}\",");

            // Attached properties (Grid.Row, Grid.Column, DockPanel.Dock, Canvas.Left/Top)
            EmitAttachedProperties(ctrl, sb, pad + "    ");

            // Type-specific properties
            EmitTypeProperties(ctrl, sb, pad + "    ");

            // Common layout properties
            EmitLayoutProperties(ctrl, sb, pad + "    ");

            // Children (for panels/containers)
            var userChildren = GetUserChildren(ctrl);
            if (userChildren.Count > 0)
            {
                sb.AppendLine($"{pad}    Children =");
                sb.AppendLine($"{pad}    {{");
                for (int i = 0; i < userChildren.Count; i++)
                {
                    sb.Append($"{pad}        ");
                    EmitControl(userChildren[i], sb, indent + 2);
                    sb.AppendLine(",");
                }
                sb.AppendLine($"{pad}    }},");
            }

            // Border.Child
            if (ctrl is Avalonia.Controls.Border b && b.Child is Avalonia.Controls.Control borderChild)
            {
                if (!IsTemplatePart(borderChild))
                {
                    sb.Append($"{pad}    Child = ");
                    EmitControl(borderChild, sb, indent + 1);
                    sb.AppendLine(",");
                }
            }
            // Content property (for ContentControl like Button — but not Border which uses Child)
            else if (ctrl is Avalonia.Controls.ContentControl cc && cc.Content is Avalonia.Controls.Control contentChild
                && ctrl is not Avalonia.Controls.Border)
            {
                if (!IsTemplatePart(contentChild))
                {
                    sb.Append($"{pad}    Content = ");
                    EmitControl(contentChild, sb, indent + 1);
                    sb.AppendLine(",");
                }
            }

            sb.Append($"{pad}}}");
        }

        private void EmitTypeProperties(Avalonia.Controls.Control ctrl, StringBuilder sb, string pad)
        {
            switch (ctrl)
            {
                case Avalonia.Controls.TextBlock tb:
                    if (tb.Text != null) sb.AppendLine($"{pad}Text = \"{EscapeString(tb.Text)}\",");
                    if (tb.FontSize != 12) sb.AppendLine($"{pad}FontSize = {tb.FontSize.ToString(System.Globalization.CultureInfo.InvariantCulture)},");
                    if (tb.FontWeight != Avalonia.Media.FontWeight.Normal) sb.AppendLine($"{pad}FontWeight = FontWeight.{tb.FontWeight},");
                    EmitBrush(sb, pad, "Foreground", tb.Foreground);
                    if (tb.TextWrapping != Avalonia.Media.TextWrapping.NoWrap) sb.AppendLine($"{pad}TextWrapping = TextWrapping.{tb.TextWrapping},");
                    break;

                case Avalonia.Controls.CheckBox chk:
                    if (chk.Content is string chkText) sb.AppendLine($"{pad}Content = \"{EscapeString(chkText)}\",");
                    if (chk.IsChecked == true) sb.AppendLine($"{pad}IsChecked = true,");
                    break;

                case Avalonia.Controls.Button btn:
                    if (btn.Content is string btnText) sb.AppendLine($"{pad}Content = \"{EscapeString(btnText)}\",");
                    EmitBrush(sb, pad, "Background", btn.Background);
                    EmitBrush(sb, pad, "Foreground", btn.Foreground);
                    if (btn.FontWeight != Avalonia.Media.FontWeight.Normal) sb.AppendLine($"{pad}FontWeight = FontWeight.{btn.FontWeight},");
                    if (btn.Padding != default) EmitThickness(sb, pad, "Padding", btn.Padding);
                    break;

                case Avalonia.Controls.TextBox txb:
                    if (txb.Text != null) sb.AppendLine($"{pad}Text = \"{EscapeString(txb.Text)}\",");
                    if (txb.Watermark != null) sb.AppendLine($"{pad}Watermark = \"{EscapeString(txb.Watermark)}\",");
                    if (txb.IsReadOnly) sb.AppendLine($"{pad}IsReadOnly = true,");
                    break;

                case Avalonia.Controls.Slider sl:
                    sb.AppendLine($"{pad}Minimum = {sl.Minimum.ToString(System.Globalization.CultureInfo.InvariantCulture)},");
                    sb.AppendLine($"{pad}Maximum = {sl.Maximum.ToString(System.Globalization.CultureInfo.InvariantCulture)},");
                    sb.AppendLine($"{pad}Value = {sl.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)},");
                    break;

                case Avalonia.Controls.ListBox lb:
                    if (lb.Height > 0 && !double.IsNaN(lb.Height))
                        sb.AppendLine($"{pad}Height = {lb.Height.ToString(System.Globalization.CultureInfo.InvariantCulture)},");
                    break;

                case Avalonia.Controls.ProgressBar pb:
                    sb.AppendLine($"{pad}Value = {pb.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)},");
                    if (pb.IsIndeterminate) sb.AppendLine($"{pad}IsIndeterminate = true,");
                    break;

                case Avalonia.Controls.Border border:
                    EmitBrush(sb, pad, "Background", border.Background);
                    if (border.CornerRadius != default)
                        sb.AppendLine($"{pad}CornerRadius = new CornerRadius({FormatCornerRadius(border.CornerRadius)}),");
                    if (border.Padding != default) EmitThickness(sb, pad, "Padding", border.Padding);
                    if (border.BorderThickness != default) EmitThickness(sb, pad, "BorderThickness", border.BorderThickness);
                    EmitBrush(sb, pad, "BorderBrush", border.BorderBrush);
                    break;

                case Avalonia.Controls.StackPanel sp:
                    if (sp.Orientation == Avalonia.Layout.Orientation.Horizontal)
                        sb.AppendLine($"{pad}Orientation = Orientation.Horizontal,");
                    if (sp.Spacing > 0)
                        sb.AppendLine($"{pad}Spacing = {sp.Spacing.ToString(System.Globalization.CultureInfo.InvariantCulture)},");
                    break;

                case Avalonia.Controls.Grid grid:
                    if (grid.RowDefinitions.Count > 0)
                    {
                        sb.AppendLine($"{pad}RowDefinitions =");
                        sb.AppendLine($"{pad}{{");
                        foreach (var rd in grid.RowDefinitions)
                        {
                            if (rd.Height.IsAuto) sb.AppendLine($"{pad}    new RowDefinition {{ Height = GridLength.Auto }},");
                            else if (rd.Height.IsStar) sb.AppendLine($"{pad}    new RowDefinition {{ Height = new GridLength({rd.Height.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}, GridUnitType.Star) }},");
                            else sb.AppendLine($"{pad}    new RowDefinition {{ Height = new GridLength({rd.Height.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}, GridUnitType.Pixel) }},");
                        }
                        sb.AppendLine($"{pad}}},");
                    }
                    if (grid.ColumnDefinitions.Count > 0)
                    {
                        sb.AppendLine($"{pad}ColumnDefinitions =");
                        sb.AppendLine($"{pad}{{");
                        foreach (var cd in grid.ColumnDefinitions)
                        {
                            if (cd.Width.IsAuto) sb.AppendLine($"{pad}    new ColumnDefinition {{ Width = GridLength.Auto }},");
                            else if (cd.Width.IsStar) sb.AppendLine($"{pad}    new ColumnDefinition {{ Width = new GridLength({cd.Width.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}, GridUnitType.Star) }},");
                            else sb.AppendLine($"{pad}    new ColumnDefinition {{ Width = new GridLength({cd.Width.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}, GridUnitType.Pixel) }},");
                        }
                        sb.AppendLine($"{pad}}},");
                    }
                    break;

                case Avalonia.Controls.ComboBox combo:
                    if (combo.SelectedIndex >= 0) sb.AppendLine($"{pad}SelectedIndex = {combo.SelectedIndex},");
                    if (combo.PlaceholderText != null) sb.AppendLine($"{pad}PlaceholderText = \"{EscapeString(combo.PlaceholderText)}\",");
                    break;

                case Avalonia.Controls.Image img:
                    // Source can't be reliably extracted, emit as comment
                    if (img.Source != null) sb.AppendLine($"{pad}// Source = ... (runtime image, not extractable)");
                    if (img.Stretch != Avalonia.Media.Stretch.Uniform) sb.AppendLine($"{pad}Stretch = Stretch.{img.Stretch},");
                    break;

                case Avalonia.Controls.ScrollViewer sv:
                    if (sv.HorizontalScrollBarVisibility != Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled)
                        sb.AppendLine($"{pad}HorizontalScrollBarVisibility = ScrollBarVisibility.{sv.HorizontalScrollBarVisibility},");
                    if (sv.VerticalScrollBarVisibility != Avalonia.Controls.Primitives.ScrollBarVisibility.Auto)
                        sb.AppendLine($"{pad}VerticalScrollBarVisibility = ScrollBarVisibility.{sv.VerticalScrollBarVisibility},");
                    break;

                case Avalonia.Controls.WrapPanel wp:
                    if (wp.Orientation == Avalonia.Layout.Orientation.Vertical)
                        sb.AppendLine($"{pad}Orientation = Orientation.Vertical,");
                    if (wp.ItemWidth > 0 && !double.IsNaN(wp.ItemWidth))
                        sb.AppendLine($"{pad}ItemWidth = {wp.ItemWidth.ToString(System.Globalization.CultureInfo.InvariantCulture)},");
                    if (wp.ItemHeight > 0 && !double.IsNaN(wp.ItemHeight))
                        sb.AppendLine($"{pad}ItemHeight = {wp.ItemHeight.ToString(System.Globalization.CultureInfo.InvariantCulture)},");
                    break;

                case Avalonia.Controls.DockPanel dp:
                    if (dp.LastChildFill == false)
                        sb.AppendLine($"{pad}LastChildFill = false,");
                    break;

                case Avalonia.Controls.Canvas canvas:
                    // Canvas-specific: children use Canvas.Left/Top attached properties
                    break;
            }
        }

        private static void EmitAttachedProperties(Avalonia.Controls.Control ctrl, StringBuilder sb, string pad)
        {
            // Grid attached properties
            var row = Avalonia.Controls.Grid.GetRow(ctrl);
            var col = Avalonia.Controls.Grid.GetColumn(ctrl);
            var rowSpan = Avalonia.Controls.Grid.GetRowSpan(ctrl);
            var colSpan = Avalonia.Controls.Grid.GetColumnSpan(ctrl);
            if (row > 0) sb.AppendLine($"{pad}[Grid.RowProperty] = {row},");
            if (col > 0) sb.AppendLine($"{pad}[Grid.ColumnProperty] = {col},");
            if (rowSpan > 1) sb.AppendLine($"{pad}[Grid.RowSpanProperty] = {rowSpan},");
            if (colSpan > 1) sb.AppendLine($"{pad}[Grid.ColumnSpanProperty] = {colSpan},");

            // DockPanel attached property
            var dock = Avalonia.Controls.DockPanel.GetDock(ctrl);
            if (dock != Avalonia.Controls.Dock.Left) // Left is default
                sb.AppendLine($"{pad}[DockPanel.DockProperty] = Dock.{dock},");

            // Canvas attached properties
            var canvasLeft = Avalonia.Controls.Canvas.GetLeft(ctrl);
            var canvasTop = Avalonia.Controls.Canvas.GetTop(ctrl);
            if (!double.IsNaN(canvasLeft) && canvasLeft != 0)
                sb.AppendLine($"{pad}[Canvas.LeftProperty] = {canvasLeft.ToString(System.Globalization.CultureInfo.InvariantCulture)},");
            if (!double.IsNaN(canvasTop) && canvasTop != 0)
                sb.AppendLine($"{pad}[Canvas.TopProperty] = {canvasTop.ToString(System.Globalization.CultureInfo.InvariantCulture)},");
        }

        private void EmitLayoutProperties(Avalonia.Controls.Control ctrl, StringBuilder sb, string pad)
        {
            if (ctrl.HorizontalAlignment != Avalonia.Layout.HorizontalAlignment.Stretch)
                sb.AppendLine($"{pad}HorizontalAlignment = HorizontalAlignment.{ctrl.HorizontalAlignment},");
            if (ctrl.VerticalAlignment != Avalonia.Layout.VerticalAlignment.Stretch)
                sb.AppendLine($"{pad}VerticalAlignment = VerticalAlignment.{ctrl.VerticalAlignment},");
            if (ctrl.Margin != default) EmitThickness(sb, pad, "Margin", ctrl.Margin);
            if (!double.IsNaN(ctrl.Width) && ctrl.Width > 0)
                sb.AppendLine($"{pad}Width = {ctrl.Width.ToString(System.Globalization.CultureInfo.InvariantCulture)},");
            if (!double.IsNaN(ctrl.Height) && ctrl.Height > 0 && ctrl is not Avalonia.Controls.ListBox)
                sb.AppendLine($"{pad}Height = {ctrl.Height.ToString(System.Globalization.CultureInfo.InvariantCulture)},");
            if (!ctrl.IsVisible) sb.AppendLine($"{pad}IsVisible = false,");
            if (ctrl.Opacity < 1.0) sb.AppendLine($"{pad}Opacity = {ctrl.Opacity.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)},");

            // Background for panels/templated controls
            if (ctrl is Avalonia.Controls.Panel panel && panel.Background != null && ctrl is not Avalonia.Controls.Border)
                EmitBrush(sb, pad, "Background", panel.Background);
            else if (ctrl is Avalonia.Controls.Primitives.TemplatedControl tc && tc.Background != null
                && ctrl is not Avalonia.Controls.Button && ctrl is not Avalonia.Controls.Border)
                EmitBrush(sb, pad, "Background", tc.Background);
        }

        private static void EmitBrush(StringBuilder sb, string pad, string propName, Avalonia.Media.IBrush? brush)
        {
            if (brush == null) return;
            if (brush is Avalonia.Media.SolidColorBrush scb)
            {
                var color = scb.Color;
                // Try to match named colors
                var named = MatchNamedBrush(color);
                if (named != null)
                    sb.AppendLine($"{pad}{propName} = Brushes.{named},");
                else
                    sb.AppendLine($"{pad}{propName} = new SolidColorBrush(Color.Parse(\"{color}\")),");
            }
            else if (brush is Avalonia.Media.LinearGradientBrush lgb)
            {
                sb.AppendLine($"{pad}{propName} = new LinearGradientBrush");
                sb.AppendLine($"{pad}{{");
                sb.AppendLine($"{pad}    StartPoint = new RelativePoint({lgb.StartPoint.Point.X}, {lgb.StartPoint.Point.Y}, RelativeUnit.Relative),");
                sb.AppendLine($"{pad}    EndPoint = new RelativePoint({lgb.EndPoint.Point.X}, {lgb.EndPoint.Point.Y}, RelativeUnit.Relative),");
                sb.AppendLine($"{pad}    GradientStops =");
                sb.AppendLine($"{pad}    {{");
                foreach (var stop in lgb.GradientStops)
                    sb.AppendLine($"{pad}        new GradientStop(Color.Parse(\"{stop.Color}\"), {stop.Offset.ToString(System.Globalization.CultureInfo.InvariantCulture)}),");
                sb.AppendLine($"{pad}    }},");
                sb.AppendLine($"{pad}}},");
            }
        }

        private static void EmitThickness(StringBuilder sb, string pad, string propName, Avalonia.Thickness t)
        {
            if (t.Left == t.Right && t.Top == t.Bottom && t.Left == t.Top)
                sb.AppendLine($"{pad}{propName} = new Thickness({t.Left.ToString(System.Globalization.CultureInfo.InvariantCulture)}),");
            else if (t.Left == t.Right && t.Top == t.Bottom)
                sb.AppendLine($"{pad}{propName} = new Thickness({t.Left.ToString(System.Globalization.CultureInfo.InvariantCulture)}, {t.Top.ToString(System.Globalization.CultureInfo.InvariantCulture)}),");
            else
                sb.AppendLine($"{pad}{propName} = new Thickness({t.Left.ToString(System.Globalization.CultureInfo.InvariantCulture)}, {t.Top.ToString(System.Globalization.CultureInfo.InvariantCulture)}, {t.Right.ToString(System.Globalization.CultureInfo.InvariantCulture)}, {t.Bottom.ToString(System.Globalization.CultureInfo.InvariantCulture)}),");
        }

        private static string FormatCornerRadius(Avalonia.CornerRadius cr)
        {
            if (cr.TopLeft == cr.TopRight && cr.TopLeft == cr.BottomLeft && cr.TopLeft == cr.BottomRight)
                return cr.TopLeft.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return $"{cr.TopLeft.ToString(System.Globalization.CultureInfo.InvariantCulture)}, {cr.TopRight.ToString(System.Globalization.CultureInfo.InvariantCulture)}, {cr.BottomRight.ToString(System.Globalization.CultureInfo.InvariantCulture)}, {cr.BottomLeft.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        }

        private static string? MatchNamedBrush(Avalonia.Media.Color color)
        {
            // Check common named brushes
            var brushProps = typeof(Avalonia.Media.Brushes).GetProperties(
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            foreach (var prop in brushProps)
            {
                if (prop.GetValue(null) is Avalonia.Media.SolidColorBrush named && named.Color == color)
                    return prop.Name;
            }
            return null;
        }

        private static bool IsTemplatePart(Avalonia.Controls.Control ctrl)
        {
            // Skip Avalonia internal template parts
            if (ctrl.Name != null && ctrl.Name.StartsWith("PART_")) return true;
            if (ctrl.Name != null && ctrl.Name.StartsWith("PART_")) return true;
            var typeName = ctrl.GetType().Name;
            if (typeName is "ContentPresenter" or "ScrollContentPresenter" or "DataValidationErrors"
                or "AccessText" or "TextPresenter" or "ScrollBar")
                return true;
            // ScrollViewer and DockPanel only internal if unnamed
            if (typeName is "ScrollViewer" or "DockPanel" && string.IsNullOrEmpty(ctrl.Name))
                return true;
            return false;
        }

        private List<Avalonia.Controls.Control> GetUserChildren(Avalonia.Controls.Control ctrl)
        {
            // Only get direct children for panel types
            if (ctrl is Avalonia.Controls.Panel panel)
            {
                return panel.Children
                    .OfType<Avalonia.Controls.Control>()
                    .Where(c => !IsTemplatePart(c))
                    .ToList();
            }
            return new List<Avalonia.Controls.Control>();
        }

        private static string EscapeString(string s) =>
            s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");

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
