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

namespace Neo.PluginWindowWPF.MCP
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

            MainWin = new MainWindow();
            if (isStandalone)
            {
                MainWin.Title = "N.E.O. \u2014 Live Preview (WPF MCP)";
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
                Json.ToJson(new HelloMessage("Child", Environment.ProcessId, Hwnd: hwnd.ToInt64()))
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

                case IpcTypes.SetDesignerMode:
                    {
                        var designerMsg = Json.FromJson<SetDesignerModeMessage>(env.PayloadJson)!;
                        await Dispatcher.BeginInvoke(() => MainWin.SetDesignerMode(designerMsg.Enabled));
                        await SafeSendAsync(new IpcEnvelope(
                            IpcTypes.Ack, env.CorrelationId,
                            Json.ToJson(new AckMessage("Designer mode updated."))
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
                                    MainWin.WaitOverlay.Visibility = Visibility.Visible;
                                    MainWin.WaitStatusText.Text = "Generating...";
                                    MainWin.StartWaitTimer();
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

        private string SerializeVisualTree(FrameworkElement element, int depth)
        {
            const int maxDepth = 15;
            const int maxNodes = 500;
            if (depth > maxDepth || _serializedNodeCount > maxNodes)
                return "{ \"type\": \"...\", \"truncated\": true }";
            _serializedNodeCount++;

            var sb = new StringBuilder();
            sb.Append("{ ");

            sb.Append($"\"type\": \"{element.GetType().Name}\"");

            if (!string.IsNullOrEmpty(element.Name))
                sb.Append($", \"name\": \"{EscapeJson(element.Name)}\"");

            var props = new Dictionary<string, string>();
            CollectKeyProperties(element, props);
            if (props.Count > 0)
            {
                sb.Append(", \"properties\": { ");
                sb.Append(string.Join(", ", props.Select(kv =>
                    $"\"{EscapeJson(kv.Key)}\": \"{EscapeJson(kv.Value)}\"")));
                sb.Append(" }");
            }

            if (element.ActualWidth > 0 || element.ActualHeight > 0)
            {
                var pos = element.TranslatePoint(new Point(0, 0), MainWin);
                sb.Append($", \"bounds\": {{ \"x\": {pos.X:F0}, \"y\": {pos.Y:F0}, \"w\": {element.ActualWidth:F0}, \"h\": {element.ActualHeight:F0} }}");
            }

            var children = new List<FrameworkElement>();
            int childCount = VisualTreeHelper.GetChildrenCount(element);
            for (int i = 0; i < childCount; i++)
            {
                if (VisualTreeHelper.GetChild(element, i) is FrameworkElement child)
                    children.Add(child);
            }

            if (children.Count > 0)
            {
                sb.Append(", \"children\": [ ");
                sb.Append(string.Join(", ", children.Select(c => SerializeVisualTree(c, depth + 1))));
                sb.Append(" ]");
            }

            sb.Append(" }");
            return sb.ToString();
        }

        private static void CollectKeyProperties(FrameworkElement element, Dictionary<string, string> props)
        {
            if (element is TextBlock tb)
            {
                if (tb.Text != null) props["Text"] = tb.Text;
                props["FontSize"] = tb.FontSize.ToString(CultureInfo.InvariantCulture);
                if (tb.Foreground != null) props["Foreground"] = tb.Foreground.ToString()!;
                if (tb.FontWeight != default) props["FontWeight"] = tb.FontWeight.ToString();
            }
            else if (element is Button btn)
            {
                if (btn.Content != null) props["Content"] = btn.Content.ToString()!;
                props["IsEnabled"] = btn.IsEnabled.ToString();
                if (btn.Background != null) props["Background"] = btn.Background.ToString()!;
            }
            else if (element is TextBox txb)
            {
                if (txb.Text != null) props["Text"] = txb.Text;
                props["IsReadOnly"] = txb.IsReadOnly.ToString();
            }
            else if (element is ListBox lb)
            {
                props["ItemCount"] = lb.Items.Count.ToString();
                if (lb.SelectedIndex >= 0) props["SelectedIndex"] = lb.SelectedIndex.ToString();
            }
            else if (element is ComboBox cb)
            {
                props["ItemCount"] = cb.Items.Count.ToString();
                if (cb.SelectedIndex >= 0) props["SelectedIndex"] = cb.SelectedIndex.ToString();
            }
            else if (element is CheckBox chk)
            {
                props["IsChecked"] = chk.IsChecked?.ToString() ?? "null";
                if (chk.Content != null) props["Content"] = chk.Content.ToString()!;
            }
            else if (element is Slider sl)
            {
                props["Value"] = sl.Value.ToString(CultureInfo.InvariantCulture);
                props["Minimum"] = sl.Minimum.ToString(CultureInfo.InvariantCulture);
                props["Maximum"] = sl.Maximum.ToString(CultureInfo.InvariantCulture);
            }
            else if (element is Image img)
            {
                props["Source"] = img.Source?.ToString() ?? "null";
            }
            else if (element is ProgressBar pb)
            {
                props["Value"] = pb.Value.ToString(CultureInfo.InvariantCulture);
                props["IsIndeterminate"] = pb.IsIndeterminate.ToString();
            }

            if (element.Visibility != Visibility.Visible) props["Visibility"] = element.Visibility.ToString();
            if (element.Opacity < 1.0) props["Opacity"] = element.Opacity.ToString("F2", CultureInfo.InvariantCulture);
            try
            {
                var bg = (element as Control)?.Background ?? (element as Panel)?.Background;
                if (bg != null && !props.ContainsKey("Background"))
                    props["Background"] = bg.ToString()!;
            }
            catch { }
        }

        // =======================================================
        // Live Property Editing (WPF)
        // =======================================================
        private SetPropertyResultMessage SetPropertyOnControl(SetPropertyRequest req)
        {
            var root = MainWin.GetLoadedUserControl();
            if (root == null)
                return new SetPropertyResultMessage(false, "No control loaded.");

            var target = FindControlInTree(root, req.Target);
            if (target == null)
                return new SetPropertyResultMessage(false, $"Control '{req.Target}' not found in visual tree.");

            // Try DependencyProperty first
            var dp = FindDependencyProperty(target, req.PropertyName);
            if (dp != null)
            {
                var oldVal = target.GetValue(dp)?.ToString();
                var newVal = ParsePropertyValue(dp.PropertyType, req.Value);
                target.SetValue(dp, newVal);
                return new SetPropertyResultMessage(true,
                    $"Set {target.GetType().Name}.{dp.Name} = {req.Value}",
                    oldVal, target.GetValue(dp)?.ToString());
            }

            // Fallback: CLR property via reflection
            var clrProp = target.GetType().GetProperty(req.PropertyName,
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
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

        private static DependencyProperty? FindDependencyProperty(DependencyObject obj, string propertyName)
        {
            var fieldName = propertyName + "Property";
            var field = obj.GetType().GetField(fieldName,
                BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
            return field?.GetValue(null) as DependencyProperty;
        }

        private static object? ParsePropertyValue(Type targetType, string value)
        {
            if (targetType == typeof(Brush) || targetType == typeof(SolidColorBrush))
            {
                try
                {
                    var converter = new BrushConverter();
                    return converter.ConvertFromString(value);
                }
                catch { }
            }

            if (targetType == typeof(Thickness))
            {
                var nums = value.Split(',').Select(s => double.Parse(s.Trim(), CultureInfo.InvariantCulture)).ToArray();
                return nums.Length switch
                {
                    1 => new Thickness(nums[0]),
                    2 => new Thickness(nums[0], nums[1], nums[0], nums[1]),
                    4 => new Thickness(nums[0], nums[1], nums[2], nums[3]),
                    _ => new Thickness(nums[0])
                };
            }

            if (targetType == typeof(CornerRadius))
            {
                var nums = value.Split(',').Select(s => double.Parse(s.Trim(), CultureInfo.InvariantCulture)).ToArray();
                return nums.Length switch
                {
                    1 => new CornerRadius(nums[0]),
                    4 => new CornerRadius(nums[0], nums[1], nums[2], nums[3]),
                    _ => new CornerRadius(nums[0])
                };
            }

            if (targetType == typeof(FontWeight))
            {
                var converter = new FontWeightConverter();
                try { return converter.ConvertFromString(value); } catch { }
            }

            if (targetType.IsEnum)
            {
                if (Enum.TryParse(targetType, value, true, out var enumVal))
                    return enumVal;
            }

            var underlying = Nullable.GetUnderlyingType(targetType);
            if (underlying != null)
                return ParsePropertyValue(underlying, value);

            if (targetType == typeof(double)) return double.Parse(value, CultureInfo.InvariantCulture);
            if (targetType == typeof(float)) return float.Parse(value, CultureInfo.InvariantCulture);
            if (targetType == typeof(int)) return int.Parse(value, CultureInfo.InvariantCulture);
            if (targetType == typeof(bool)) return bool.Parse(value);
            if (targetType == typeof(string)) return value;

            var tc = System.ComponentModel.TypeDescriptor.GetConverter(targetType);
            if (tc.CanConvertFrom(typeof(string)))
                return tc.ConvertFromString(value);

            return value;
        }

        private static FrameworkElement? FindControlInTree(FrameworkElement root, string target)
        {
            var all = new List<FrameworkElement>();
            CollectControls(root, all);

            var byName = all.FirstOrDefault(c => string.Equals(c.Name, target, StringComparison.OrdinalIgnoreCase));
            if (byName != null) return byName;

            var parts = target.Split(':', 2);
            var typeName = parts[0];
            int index = parts.Length > 1 && int.TryParse(parts[1], out var idx) ? idx : 0;

            var byType = all.Where(c => c.GetType().Name.Equals(typeName, StringComparison.OrdinalIgnoreCase)).ToList();
            if (index < byType.Count) return byType[index];
            if (byType.Count > 0) return byType[0];

            var partial = all.FirstOrDefault(c => c.Name != null &&
                c.Name.Contains(target, StringComparison.OrdinalIgnoreCase));
            return partial;
        }

        private static void CollectControls(DependencyObject parent, List<FrameworkElement> result)
        {
            if (parent is FrameworkElement fe)
                result.Add(fe);
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                CollectControls(child, result);
            }
        }

        // =======================================================
        // Data Injection + Reading (WPF)
        // =======================================================
        private InjectDataResult InjectDataOnControl(InjectDataRequest req)
        {
            var root = MainWin.GetLoadedUserControl();
            if (root == null)
                return new InjectDataResult(false, "No control loaded.");

            if (string.Equals(req.Mode, "fill", StringComparison.OrdinalIgnoreCase))
                return InjectFillMode(root, req);

            var target = string.Equals(req.Target, "root", StringComparison.OrdinalIgnoreCase)
                ? root
                : FindControlInTree(root, req.Target);

            if (target == null)
                return new InjectDataResult(false, $"Control '{req.Target}' not found.");

            if (target is not ItemsControl itemsControl)
                return new InjectDataResult(false,
                    $"Control '{target.GetType().Name}' is not an ItemsControl. Use mode 'fill' for form controls.");

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
                foreach (var item in items)
                    existing.Add(item);
            }
            else
            {
                var collection = new System.Collections.ObjectModel.ObservableCollection<Dictionary<string, object>>();
                if (isAppend && itemsControl.ItemsSource is System.Collections.IEnumerable oldItems)
                {
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

            int totalCount = 0;
            if (itemsControl.ItemsSource is System.Collections.IEnumerable countable)
                foreach (var _ in countable) totalCount++;

            return new InjectDataResult(true,
                $"{(isAppend ? "Appended" : "Replaced")} {items.Count} items on {target.GetType().Name}.",
                totalCount, fields);
        }

        private InjectDataResult InjectFillMode(FrameworkElement root, InjectDataRequest req)
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
                            case TextBox tb:
                                tb.Text = prop.Value.GetString() ?? prop.Value.GetRawText();
                                break;
                            case TextBlock tb:
                                tb.Text = prop.Value.GetString() ?? prop.Value.GetRawText();
                                break;
                            case CheckBox cb:
                                cb.IsChecked = prop.Value.ValueKind == System.Text.Json.JsonValueKind.True;
                                break;
                            case Slider sl:
                                sl.Value = prop.Value.GetDouble();
                                break;
                            case ComboBox combo:
                                if (prop.Value.ValueKind == System.Text.Json.JsonValueKind.Number)
                                    combo.SelectedIndex = prop.Value.GetInt32();
                                else
                                {
                                    var text = prop.Value.GetString();
                                    for (int i = 0; i < combo.Items.Count; i++)
                                        if (combo.Items[i]?.ToString() == text) { combo.SelectedIndex = i; break; }
                                }
                                break;
                            case System.Windows.Controls.Primitives.ToggleButton ts:
                                ts.IsChecked = prop.Value.ValueKind == System.Text.Json.JsonValueKind.True;
                                break;
                            default:
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
            var root = MainWin.GetLoadedUserControl();
            if (root == null)
                return new ReadDataResult(false, "No control loaded.");

            var target = string.Equals(req.Target, "root", StringComparison.OrdinalIgnoreCase)
                ? root
                : FindControlInTree(root, req.Target);

            if (target == null)
                return new ReadDataResult(false, $"Control '{req.Target}' not found.");

            var scope = req.Scope?.ToLowerInvariant();
            if (scope == null)
            {
                if (target is ItemsControl) scope = "items";
                else if (target is TextBox || target is CheckBox || target is Slider) scope = "value";
                else scope = "form";
            }

            switch (scope)
            {
                case "items": return ReadItemsData(target);
                case "form": return ReadFormData(target);
                case "value": return ReadSingleValue(target);
                default: return new ReadDataResult(false, $"Unknown scope '{scope}'. Use 'items', 'form', or 'value'.");
            }
        }

        private ReadDataResult ReadItemsData(FrameworkElement target)
        {
            if (target is not ItemsControl ic)
                return new ReadDataResult(false, $"{target.GetType().Name} is not an ItemsControl.");

            var items = new List<object>();
            if (ic.ItemsSource is System.Collections.IEnumerable source)
                foreach (var item in source)
                    items.Add(item is Dictionary<string, object> dict ? dict : item?.ToString() ?? "null");

            var json = System.Text.Json.JsonSerializer.Serialize(items);
            return new ReadDataResult(true, $"Read {items.Count} items.", json);
        }

        private ReadDataResult ReadFormData(FrameworkElement root)
        {
            var all = new List<FrameworkElement>();
            CollectControls(root, all);

            var data = new Dictionary<string, object>();
            foreach (var el in all.Where(c => !string.IsNullOrEmpty(c.Name)))
            {
                switch (el)
                {
                    case TextBox tb: data[el.Name!] = tb.Text ?? ""; break;
                    case TextBlock tb: data[el.Name!] = tb.Text ?? ""; break;
                    case CheckBox cb: data[el.Name!] = cb.IsChecked ?? false; break;
                    case Slider sl: data[el.Name!] = sl.Value; break;
                    case ComboBox combo:
                        data[el.Name!] = combo.SelectedItem?.ToString() ?? "";
                        break;
                }
            }

            var json = System.Text.Json.JsonSerializer.Serialize(data);
            return new ReadDataResult(true, $"Read {data.Count} form fields.", json);
        }

        private ReadDataResult ReadSingleValue(FrameworkElement target)
        {
            string? val = target switch
            {
                TextBox tb => tb.Text,
                TextBlock tb => tb.Text,
                CheckBox cb => cb.IsChecked?.ToString(),
                Slider sl => sl.Value.ToString(CultureInfo.InvariantCulture),
                ComboBox combo => combo.SelectedItem?.ToString(),
                _ => null
            };

            if (val == null)
            {
                var textProp = target.GetType().GetProperty("Text");
                val = textProp?.GetValue(target)?.ToString();
            }

            return new ReadDataResult(true, $"Value from {target.GetType().Name}.", val ?? "null");
        }

        // =======================================================
        // Extract Code from Visual Tree (best-effort)
        // =======================================================
        public string ExtractCodeFromVisualTree(FrameworkElement root)
        {
            // If SmartCompiler has last code, return that
            if (MainWin._smartCompiler?.LastCompiledCode != null)
                return MainWin._smartCompiler.LastCompiledCode;

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
            await SafeSendAsync(new IpcEnvelope(IpcTypes.NotifyFirstChildVisibility, "", Json.ToJson(new NotifyFirstChildVisibility(0))));
        }

        public async Task NotifyParentAboutActivation()
        {
            if (_client == null) return;
            await SafeSendAsync(new IpcEnvelope(IpcTypes.ChildActivated, "", Json.ToJson(new IAmActivated(0))));
        }

        public Task NotifyParentDesignerSelection(DesignerSelectionMessage selection)
        {
            if (_client == null) return Task.CompletedTask;
            return SafeSendAsync(new IpcEnvelope(IpcTypes.DesignerSelection, "", Json.ToJson(selection)));
        }

        private static Dictionary<string, object> JsonElementToDict(System.Text.Json.JsonElement elem)
        {
            var dict = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in elem.EnumerateObject())
            {
                dict[prop.Name] = prop.Value.ValueKind switch
                {
                    System.Text.Json.JsonValueKind.Number => prop.Value.TryGetInt64(out var l) ? l : prop.Value.GetDouble(),
                    System.Text.Json.JsonValueKind.True => true,
                    System.Text.Json.JsonValueKind.False => false,
                    _ => prop.Value.GetString() ?? prop.Value.GetRawText()
                };
            }
            return dict;
        }

        private static string EscapeJson(string s) =>
            s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");

        private void HardExitNow()
        {
            try { Dispatcher.Invoke(() => Shutdown()); } catch { }
            Environment.Exit(0);
        }
    }
}
