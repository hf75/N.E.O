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

namespace Neo.PluginWindowWPF
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

        // Für laufende BLOB-Transfers: CorrelationId -> MemoryStream
        private readonly ConcurrentDictionary<Guid, MemoryStream> _blobStreams = new();
        // Meta pro Transfer (Name, Length, Hash, ...)
        private readonly ConcurrentDictionary<Guid, BlobStartMeta> _blobMetas = new();

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

            CleanupTempCacheRecursive(); // ALLES weg (Dateien + Unterordner)

            // Exception Handler so früh wie möglich einrichten!
            this.DispatcherUnhandledException += MainWindow_DispatcherUnhandledException;
            TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;

            MainWin = new MainWindow
            {
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize
            };
            this.MainWindow = MainWin;

            // 1) Fenster anzeigen → HWND existiert sicher
            MainWin.Show();

            // 2) HWND ermitteln
            var helper = new System.Windows.Interop.WindowInteropHelper(MainWin);
            var hwnd = helper.EnsureHandle();
            if (hwnd == IntPtr.Zero)
            {
                Shutdown();
                return;
            }

            // 3) Pipe-Name aus Args
            string? pipeName = GetPipeNameFromArgs(e.Args);
            if (string.IsNullOrWhiteSpace(pipeName))
            {
                Shutdown();
                return;
            }

            // 4) Verbinden
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

            // 5) Hello mit HWND
            await SafeSendAsync(new IpcEnvelope(
                IpcTypes.Hello,
                Guid.NewGuid().ToString("N"),
                Neo.IPC.Json.ToJson(new HelloMessage("Child", Environment.ProcessId, Hwnd: hwnd.ToInt64()))
            ));

            // 6) DynamicSlot-Compiler konfigurieren (bevor irgendein Plugin geladen wird)
            ConfigureDynamicSlotCompiler();

            // 7) NEU: Framed Receive-Loop starten (Control + Blob)
            _ = Task.Run(() => FramedListenLoopAsync(_ipcCts.Token));

            // 7) Heartbeat
            _hb = new DispatcherTimer();
            _hb.Interval = TimeSpan.FromSeconds(2);

            // 2) Der Tick-Handler wird auf dem UI-Thread ausgeführt
            _hb.Tick += (sender, e) =>
            {
                try
                {
                    if (_client == null) return;
                    var hb = new { Pid = Environment.ProcessId, WhenUtc = DateTime.UtcNow };

                    // WICHTIG: Der Sende-Aufruf selbst darf die UI nicht blockieren.
                    _ = SafeSendAsync(new IpcEnvelope(IpcTypes.Heartbeat, "", Neo.IPC.Json.ToJson(hb)));
                }
                catch (Exception ex) { Debug.WriteLine($"[Heartbeat] {ex.Message}"); }
            };

            _hb.Start();
        }

        // =======================================================
        // DynamicSlot – Embedded AI Compiler Setup
        // =======================================================
        private void ConfigureDynamicSlotCompiler()
        {
            try
            {
                Neo.Agents.Core.IAgent? agent = null;

                // Priority: Claude → Gemini → OpenAI (same as host AppController)
                var anthropicKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY", EnvironmentVariableTarget.User);
                if (!string.IsNullOrWhiteSpace(anthropicKey))
                {
                    agent = new Neo.Agents.AnthropicTextChatAgent();
                    agent.SetOption("ApiKey", anthropicKey);
                    agent.SetOption("Model", Environment.GetEnvironmentVariable("NEO_SLOT_MODEL") ?? "claude-sonnet-4-20250514");
                }

                if (agent == null)
                {
                    var geminiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY", EnvironmentVariableTarget.User);
                    if (!string.IsNullOrWhiteSpace(geminiKey))
                    {
                        agent = new Neo.Agents.GeminiTextChatAgent();
                        agent.SetOption("ApiKey", geminiKey);
                        agent.SetOption("Model", Environment.GetEnvironmentVariable("NEO_SLOT_MODEL") ?? "gemini-2.0-flash");
                    }
                }

                if (agent == null)
                {
                    var openAiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY", EnvironmentVariableTarget.User);
                    if (!string.IsNullOrWhiteSpace(openAiKey))
                    {
                        agent = new Neo.Agents.OpenAiTextChatAgent();
                        agent.SetOption("ApiKey", openAiKey);
                        agent.SetOption("Model", Environment.GetEnvironmentVariable("NEO_SLOT_MODEL") ?? "gpt-4o");
                    }
                }

                if (agent == null)
                {
                    Debug.WriteLine("[DynamicSlot] No AI API key found. DynamicSlots will show an error message.");
                    return;
                }

                var completionProvider = new Neo.AssemblyForge.Completion.AiApiAgentCompletionProvider(agent);

                var dotnetMajor = Environment.Version.Major;
                var refDirs = new List<string>();
                var corePath = Neo.AssemblyForge.Utils.DotNetRuntimeFinder.GetHighestRuntimePath(
                    Neo.AssemblyForge.Utils.DotNetRuntimeType.NetCoreApp, dotnetMajor);
                if (!string.IsNullOrWhiteSpace(corePath)) refDirs.Add(corePath);
                var desktopPath = Neo.AssemblyForge.Utils.DotNetRuntimeFinder.GetHighestRuntimePath(
                    Neo.AssemblyForge.Utils.DotNetRuntimeType.WindowsDesktopApp, dotnetMajor);
                if (!string.IsNullOrWhiteSpace(desktopPath)) refDirs.Add(desktopPath);

                var workspacePath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Neo.Create", "Slots");

                Neo.AssemblyForge.Slot.DynamicSlotService.Compiler =
                    new Neo.AssemblyForge.Slot.EmbeddedSlotCompiler(completionProvider, refDirs, workspacePath);

                Debug.WriteLine($"[DynamicSlot] Compiler configured with {agent.GetType().Name}, {refDirs.Count} ref dirs.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DynamicSlot] Configuration failed: {ex.Message}");
            }
        }

        protected override async void OnExit(ExitEventArgs e)
        {
            try
            {
                // Prüfen, ob der Timer überhaupt initialisiert wurde
                if (_hb != null)
                {
                    _hb.Stop();
                }
            }
            catch (Exception ex) { Debug.WriteLine($"[OnExit] Timer stop failed: {ex.Message}"); }

            try { _ipcCts.Cancel(); } catch (Exception ex) { Debug.WriteLine($"[OnExit] CTS cancel failed: {ex.Message}"); }
            try { if (_client != null) await _client.DisposeAsync(); } catch (Exception ex) { Debug.WriteLine($"[OnExit] Client dispose failed: {ex.Message}"); }

            base.OnExit(e);
        }

        // =======================================================
        // NEU: zentrale Framed-Listen-Loop
        // =======================================================
        private async Task FramedListenLoopAsync(CancellationToken ct)
        {
            try
            {
                await _client!.Messenger.ReceiveLoopAsync(
                    onControl: async env => await OnControlAsync(env),
                    onBlobStart: async (corr, meta) =>
                    {
                        // Stream für diesen Transfer vorbereiten
                        var ms = meta.Length is long L && L >= 0 ? new MemoryStream(capacity: (int)Math.Min(L, int.MaxValue))
                                                                 : new MemoryStream();
                        _blobStreams[corr] = ms;
                        _blobMetas[corr] = meta;
                        await Task.CompletedTask;
                    },
                    onBlobChunk: async (corr, chunk) =>
                    {
                        // Chunk direkt in den Stream schreiben (kopiert nicht erneut)
                        if (_blobStreams.TryGetValue(corr, out var ms))
                        {
                            await ms.WriteAsync(chunk, CancellationToken.None);
                        }
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

                                    // Normalisiere logischen Pfad/Name (Unterordner erlaubt)
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
                                            // Fallback: kein Metadata — notfalls unter SimpleName indexieren
                                            var simple = Path.GetFileNameWithoutExtension(logicalPath);
                                            var pseudoFull = simple; // als Notnagel
                                            _managedByFullName[pseudoFull] = bytes;
                                            _simpleToFull[simple] = pseudoFull;

                                            await SendLogAsync(LogLevel.Warn, $"Managed (no AssemblyName) assumed: {simple} ({bytes.Length} bytes)");
                                        }
                                    }
                                    else
                                    {
                                        // Native: unter Pfad UND Basename ablegen
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
                // WICHTIG: Wenn wir hier landen, ist die Verbindung zum Parent weg (EOF/Fehler/Cancel).
                // Im AppContainer ist das unser zuverlässigstes Lebenszeichen -> sofort beenden.
                HardExitNow();
            }
        }

        // =======================================================
        // Dein bisheriges Control-Handling – nur als Methode
        // =======================================================
        private async Task OnControlAsync(IpcEnvelope env)
        {
            switch (env.Type)
            {
                case IpcTypes.Hello:
                    await SafeSendAsync(new IpcEnvelope(
                        IpcTypes.Ack, env.CorrelationId,
                        Neo.IPC.Json.ToJson(new AckMessage("Hello received by Child"))
                    ));
                    break;
                case IpcTypes.LoadControl:
                    {
                        var req = Neo.IPC.Json.FromJson<LoadControlRequest>(env.PayloadJson)!;

                        try
                        {
                            if (!TryBuildBundleFromAssets(req.AssemblyPath, out var mainBytes, out var managedByFull, out var nativeByBase) || mainBytes == null)
                                throw new InvalidOperationException("Failed to build assembly bundle from assets.");

                            await Dispatcher.InvokeAsync(() =>
                                MainWin.HandleLoadUserControlFromBytes(
                                    mainAssemblyBytes: mainBytes,
                                    explicitControlTypeName: string.IsNullOrWhiteSpace(req.TypeName) ? null : req.TypeName,
                                    managedAssembliesByFullName: managedByFull,
                                    nativeLibrariesByBasename: nativeByBase
                                ));

                            await SendLogAsync(LogLevel.Info, "LoadControl executed.", "Child", "Mode=Bytes(In-Memory)");
                            await SafeSendAsync(new IpcEnvelope(
                                IpcTypes.Ack, env.CorrelationId,
                                Neo.IPC.Json.ToJson(new AckMessage("Loaded (bytes/in-memory)"))
                            ));
                        }
                        catch (Exception ex)
                        {
                            await _client!.SendAsync(new IpcEnvelope(
                                IpcTypes.Error, env.CorrelationId,
                                Neo.IPC.Json.ToJson(new ErrorMessage(ex.Message, ex.GetType().FullName, ex.ToString()))
                            ));
                        }
                        break;
                    }
                case IpcTypes.CursorVisible:
                    var msg = Neo.IPC.Json.FromJson<IsCursorVisible>(env.PayloadJson)!;

                    await Dispatcher.BeginInvoke(() =>
                    {
                        MainWin.HandleFullscreenMouse(msg.isCursorVisible);
                    });

                    await SafeSendAsync(new IpcEnvelope(
                        IpcTypes.Ack, env.CorrelationId,
                        Neo.IPC.Json.ToJson(new AckMessage($"Cursor Changed!"))
                    ));
                    break;
                case IpcTypes.SetChildModal:
                    var modalMsg = Neo.IPC.Json.FromJson<IsChildModal>(env.PayloadJson)!;

                    await Dispatcher.BeginInvoke(() =>
                    {
                        MainWin.HandleChildModality(modalMsg.isModal);
                    });

                    await SafeSendAsync(new IpcEnvelope(
                        IpcTypes.Ack, env.CorrelationId,
                        Neo.IPC.Json.ToJson(new AckMessage($"Cursor Changed!"))
                    ));
                    break;
                case IpcTypes.SetDesignerMode:
                    var designerMsg = Neo.IPC.Json.FromJson<SetDesignerModeMessage>(env.PayloadJson)!;

                    await Dispatcher.BeginInvoke(() =>
                    {
                        MainWin.SetDesignerMode(designerMsg.Enabled);
                    });

                    await SafeSendAsync(new IpcEnvelope(
                        IpcTypes.Ack, env.CorrelationId,
                        Neo.IPC.Json.ToJson(new AckMessage("Designer mode updated."))
                    ));
                    break;
                case IpcTypes.UnloadControl:
                    await Dispatcher.InvokeAsync(() =>
                    {
                        MainWin.UnloadUserControlPlugin();
                    });

                    await SafeSendAsync(new IpcEnvelope(
                        IpcTypes.Ack, env.CorrelationId,
                        Neo.IPC.Json.ToJson(new AckMessage("Control unloaded."))
                    ));
                    break;
                default:
                    await SafeSendAsync(new IpcEnvelope(
                        IpcTypes.Ack, env.CorrelationId,
                        Neo.IPC.Json.ToJson(new AckMessage($"Unknown command '{env.Type}' ignored by Child"))
                    ));
                    break;
            }
        }

        // =======================================================
        // Utility (unverändert)
        // =======================================================
        private static string? GetPipeNameFromArgs(string[] args)
        {
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], "--pipe", StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            }
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
            return SafeSendAsync(new IpcEnvelope(IpcTypes.Log, CorrelationId: "", PayloadJson: Neo.IPC.Json.ToJson(log)));
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

            return SafeSendAsync(new IpcEnvelope(IpcTypes.Error, CorrelationId: "", PayloadJson: Neo.IPC.Json.ToJson(err)));
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
                var envelope = new IpcEnvelope(IpcTypes.Error, "", Neo.IPC.Json.ToJson(payload));

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

                // Best effort: rekursiv löschen
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

            // Main-Assembly bestimmen
            if (!string.IsNullOrWhiteSpace(desiredMainAssemblyFileName))
            {
                var fileOnly = Path.GetFileName(desiredMainAssemblyFileName);
                var simple = Path.GetFileNameWithoutExtension(fileOnly);

                if (_simpleToFull.TryGetValue(simple, out var full) &&
                    _managedByFullName.TryGetValue(full, out var b))
                { mainAssemblyBytes = b; return true; }

                // Fallbacks (sehr defensiv)
                foreach (var kv in _managedByFullName)
                {
                    var fn = PeUtils.TryGetAssemblyName(kv.Value)?.Name;
                    if (!string.IsNullOrEmpty(fn) && fn.Equals(simple, StringComparison.OrdinalIgnoreCase))
                    { mainAssemblyBytes = kv.Value; return true; }
                }
            }

            // Keine Vorgabe: nimm irgendeine Managed (stabil: zuerst empfangene ist egal)
            if (_managedByFullName.Count > 0)
            {
                mainAssemblyBytes = _managedByFullName.First().Value;
                return true;
            }

            return false;
        }

        public async Task NotifyFirstChildVisibility()
        {
            if (_client == null)
                return;

            var msg = new NotifyFirstChildVisibility(0);

            await SafeSendAsync(new IpcEnvelope(IpcTypes.NotifyFirstChildVisibility,
                CorrelationId: "",
                PayloadJson: Neo.IPC.Json.ToJson(msg)));
        }

        public async Task NotifyParentAboutActivation()
        {
            if (_client == null) 
                return;

            var msg = new IAmActivated(0);

            await SafeSendAsync(new IpcEnvelope(IpcTypes.ChildActivated, 
                CorrelationId: "", 
                PayloadJson: Neo.IPC.Json.ToJson(msg)));
        }

        public Task NotifyParentDesignerSelection(DesignerSelectionMessage selection)
        {
            if (_client == null)
                return Task.CompletedTask;

            return SafeSendAsync(new IpcEnvelope(
                IpcTypes.DesignerSelection,
                CorrelationId: "",
                PayloadJson: Neo.IPC.Json.ToJson(selection)));
        }

        private void HardExitNow()
        {
            try { Dispatcher.Invoke(() => Shutdown()); } catch (Exception ex) { Debug.WriteLine($"[HardExit] {ex.Message}"); }
            // Garantiert den Prozessabbruch – selbst wenn WPF-Shutdown hängt:
            Environment.Exit(0);
        }
    }
}
