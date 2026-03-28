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
