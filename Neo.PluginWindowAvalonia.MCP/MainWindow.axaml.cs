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

namespace Neo.PluginWindowAvalonia.MCP
{
    public partial class MainWindow : Window
    {
        private AssemblyLoadContext? pluginLoadContext;

        private readonly object _pluginUnloadGate = new();
        private bool _pluginUnloadScheduled;

        // Cache für Native-DLL-Temp-Pfade (Name -> absoluter Pfad im AC-Temp)
        private readonly Dictionary<string, string> _nativePathCache = new(StringComparer.OrdinalIgnoreCase);

        // Tracks whether a control was ever successfully loaded
        public bool HasEverLoadedControl { get; private set; }

        // Elapsed timer for wait overlay
        private readonly Stopwatch _waitStopwatch = new();
        private DispatcherTimer? _waitTimer;

        // Smart Edit (Ctrl+K)
        private ChatOverlay? _chatOverlay;
        private SmartCompiler? _smartCompiler;
        private ClaudeChat? _claudeChat;

        public MainWindow()
        {
            InitializeComponent();
            InitWaitTimer();
            InitSmartEdit();

            this.Activated += OnActivated;
            this.KeyDown += OnKeyDown;
        }

        private void InitSmartEdit()
        {
            _smartCompiler = new SmartCompiler();
            _claudeChat = new ClaudeChat();

            _chatOverlay = new ChatOverlay();
            _chatOverlay.PromptSubmitted += OnChatPromptSubmitted;

            // Add overlay to the main content grid
            // We wrap the existing content + overlay in a Grid
            var existingContent = this.Content as Avalonia.Controls.Control;
            if (existingContent != null)
            {
                this.Content = null;
                var grid = new Grid();
                grid.Children.Add(existingContent);
                grid.Children.Add(_chatOverlay);
                this.Content = grid;
            }
        }

        /// <summary>
        /// Adds the chat overlay on top of the loaded control after first load.
        /// </summary>
        private void EnsureChatOverlayAttached()
        {
            if (_chatOverlay == null) return;

            // Check if overlay is already in a Grid parent
            if (_chatOverlay.Parent != null) return;

            var existing = dynamicContent.Content as Avalonia.Controls.Control;
            if (existing == null) return;

            dynamicContent.Content = null;
            var grid = new Grid();
            grid.Children.Add(existing);
            grid.Children.Add(_chatOverlay);
            dynamicContent.Content = grid;
        }

        /// <summary>
        /// Returns the actual user control, skipping the ChatOverlay wrapper Grid.
        /// Used by IPC handlers (extract_code, inspect_visual_tree, etc.) to get the real content.
        /// </summary>
        internal Avalonia.Controls.Control? GetLoadedUserControl()
        {
            var content = dynamicContent.Content as Avalonia.Controls.Control;
            if (content is Grid overlayGrid)
            {
                return overlayGrid.Children
                    .OfType<Avalonia.Controls.Control>()
                    .FirstOrDefault(c => c is not ChatOverlay);
            }
            return content;
        }

        private async Task OnChatPromptSubmitted(string prompt)
        {
            if (_claudeChat == null || _smartCompiler == null || _chatOverlay == null)
                return;

            if (!_claudeChat.IsConfigured)
            {
                _chatOverlay.AddAssistantMessage(_claudeChat.ConfigError ?? "Not configured.", isError: true);
                return;
            }

            _chatOverlay.SetProcessing(true);

            try
            {
                // 1. Extract current code from visual tree
                var root = dynamicContent.Content as Avalonia.Controls.Control;

                // If content is a Grid (with overlay), get the first non-overlay child
                if (root is Grid overlayGrid)
                {
                    root = overlayGrid.Children
                        .OfType<Avalonia.Controls.Control>()
                        .FirstOrDefault(c => c is not ChatOverlay);
                }

                string currentCode;
                if (root != null && root is Avalonia.Controls.UserControl && Application.Current is App app)
                {
                    currentCode = app.ExtractCodeFromVisualTree(root);
                }
                else if (_smartCompiler.LastCompiledCode != null)
                {
                    currentCode = _smartCompiler.LastCompiledCode;
                }
                else
                {
                    _chatOverlay.AddAssistantMessage("No app loaded to modify.", isError: true);
                    _chatOverlay.SetProcessing(false);
                    return;
                }

                _chatOverlay.SetStatus("Asking Claude...");

                // 2. Send to Claude
                var chatResult = await _claudeChat.SendAsync(prompt, currentCode);

                if (!chatResult.Success)
                {
                    _chatOverlay.AddAssistantMessage($"Claude error: {chatResult.Error}", isError: true);
                    _chatOverlay.SetProcessing(false);
                    return;
                }

                _chatOverlay.SetStatus("Compiling...");

                // Seed the SmartCompiler with current code if it has no previous code
                // (happens when app was compiled via MCP server, not via SmartCompiler)
                if (_smartCompiler.LastCompiledCode == null)
                    _smartCompiler.SeedCode(currentCode);

                // 3. Compile — patch or full code
                SmartCompiler.CompileResult compileResult;
                if (chatResult.Patch != null)
                {
                    compileResult = await _smartCompiler.PatchAndCompileAsync(chatResult.Patch);
                }
                else if (chatResult.Code != null)
                {
                    compileResult = await _smartCompiler.CompileAsync(chatResult.Code);
                }
                else
                {
                    _chatOverlay.AddAssistantMessage("Claude returned no code or patch.", isError: true);
                    _chatOverlay.SetProcessing(false);
                    return;
                }

                if (!compileResult.Success)
                {
                    _chatOverlay.AddAssistantMessage($"Compilation failed:\n{compileResult.Error}", isError: true);
                    _chatOverlay.SetProcessing(false);
                    return;
                }

                // 4. Hot-reload
                _chatOverlay.SetStatus("Applying...");

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    HandleLoadUserControlFromBytes(
                        mainAssemblyBytes: compileResult.DllBytes!,
                        explicitControlTypeName: null,
                        managedAssembliesByFullName: new Dictionary<string, byte[]>(),
                        nativeLibrariesByBasename: new Dictionary<string, byte[]>());

                    // Re-attach overlay after control reload
                    EnsureChatOverlayAttached();
                });

                _chatOverlay.AddAssistantMessage($"Applied. ({compileResult.DllBytes!.Length:N0} bytes)");
            }
            catch (Exception ex)
            {
                _chatOverlay.AddAssistantMessage($"Error: {ex.Message}", isError: true);
            }
            finally
            {
                _chatOverlay.SetProcessing(false);
            }
        }

        private void OnKeyDown(object? sender, global::Avalonia.Input.KeyEventArgs e)
        {
            // Ctrl+K: Toggle chat overlay
            if (e.Key == global::Avalonia.Input.Key.K
                && e.KeyModifiers.HasFlag(global::Avalonia.Input.KeyModifiers.Control))
            {
                _chatOverlay?.Toggle();
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
                    SystemDecorations = SystemDecorations.Full;
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

            var plc = new SandboxPluginLoadContext(
                managedResolver: an =>
                {
                    // 1) FullName-Hit
                    if (!string.IsNullOrEmpty(an.FullName) &&
                        managedAssembliesByFullName.TryGetValue(an.FullName!, out var b1))
                        return b1;

                    // 2) Fallback: SimpleName -> best match
                    if (!string.IsNullOrEmpty(an.Name))
                    {
                        foreach (var kv in managedAssembliesByFullName)
                        {
                            var candidate = PeUtils.TryGetAssemblyName(kv.Value);
                            if (candidate?.Name != null &&
                                candidate.Name.Equals(an.Name, StringComparison.OrdinalIgnoreCase))
                                return kv.Value;
                        }
                    }

                    return null;
                },
                nativeResolver: baseName =>
                {
                    var key = baseName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? baseName : baseName + ".dll";
                    return nativeLibrariesByBasename.TryGetValue(key, out var nb) ? nb : null;
                },
                nativePathProvider: name => GetOrMaterializeNativeToTemp(name, nativeLibrariesByBasename)
            );

            pluginLoadContext = plc;

            using (var ms = new MemoryStream(mainAssemblyBytes, writable: false))
            {
                var pluginAssembly = plc.LoadFromStream(ms)
                    ?? throw new InvalidOperationException("Main assembly could not be loaded.");

                // Typ aufl�sen
                Type? controlType = null;
                if (!string.IsNullOrWhiteSpace(explicitControlTypeName))
                {
                    controlType = pluginAssembly.GetType(explicitControlTypeName!, throwOnError: false, ignoreCase: false);
                }

                if (controlType == null)
                {
                    try
                    {
                        controlType = pluginAssembly.GetTypes()
                            .FirstOrDefault(t => typeof(Avalonia.Controls.UserControl).IsAssignableFrom(t));
                    }
                    catch (ReflectionTypeLoadException ex)
                    {
                        controlType = ex.Types
                            .Where(t => t != null && typeof(Avalonia.Controls.UserControl).IsAssignableFrom(t))
                            .FirstOrDefault();
                    }
                }

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
            }

            _ = Task.Run(() =>
            {
                try { (oldPlc as SandboxPluginLoadContext)?.Dispose(); } catch (Exception ex) { Debug.WriteLine($"[UnloadPlugin] Dispose failed: {ex.Message}"); }
                try { oldPlc?.Unload(); } catch (Exception ex) { Debug.WriteLine($"[UnloadPlugin] Unload failed: {ex.Message}"); }

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

        // ==========================================================
        // 4) Native-Bytes -> Temp materialisieren
        // ==========================================================
        private string GetOrMaterializeNativeToTemp(string nameOrBase,
            IDictionary<string, byte[]> nativeByBasename)
        {
            var fileName = nameOrBase.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                ? nameOrBase
                : nameOrBase + ".dll";

            if (_nativePathCache.TryGetValue(fileName, out var existing))
                return existing;

            if (!nativeByBasename.TryGetValue(fileName, out var bytes))
            {
                var kv = nativeByBasename.FirstOrDefault(k => string.Equals(k.Key, fileName, StringComparison.OrdinalIgnoreCase));
                if (kv.Key == null) throw new DllNotFoundException($"Native library '{fileName}' not found in bundle.");
                bytes = kv.Value;
            }

            var tempRoot = Path.Combine(GetAcTempRoot(), "ac_native_cache");
            Directory.CreateDirectory(tempRoot);

            var hash = ComputeShortHash(bytes);
            var outPath = Path.Combine(tempRoot, $"{Path.GetFileNameWithoutExtension(fileName)}_{hash}.dll");

            if (!File.Exists(outPath))
            {
                var tmp = outPath + ".tmp_" + Guid.NewGuid().ToString("N");
                File.WriteAllBytes(tmp, bytes);
                File.Move(tmp, outPath, overwrite: true);
            }

            _nativePathCache[fileName] = outPath;
            return outPath;
        }

        private static string GetAcWorkRoot()
        {
            var ac = Environment.GetEnvironmentVariable("AC_WORK");
            if (!string.IsNullOrWhiteSpace(ac)) return ac;

            return Path.GetTempPath();
        }

        private static string GetAcTempRoot()
        {
            var root = GetAcWorkRoot();
            var temp = Path.Combine(root, "temp");
            try { Directory.CreateDirectory(temp); } catch (Exception ex) { Debug.WriteLine($"[GetAcTempRoot] {ex.Message}"); }
            return temp;
        }

        private static string ComputeShortHash(byte[] data)
        {
            using var sha = SHA256.Create();
            var h = sha.ComputeHash(data);
            return Convert.ToHexString(h.AsSpan(0, 6)); // 12 Hex-Zeichen reichen f�r Cache-Key
        }
    }

    // ==========================================================
    // 5) SandboxPluginLoadContext (Bytes-only)
    // ==========================================================
    internal sealed class SandboxPluginLoadContext : AssemblyLoadContext, IDisposable
    {
        private readonly Func<AssemblyName, byte[]?> _managedResolver;
        private readonly Func<string, byte[]?> _nativeBytesResolver;
        private readonly Func<string, string> _nativePathProvider;

        public SandboxPluginLoadContext(
            Func<AssemblyName, byte[]?> managedResolver,
            Func<string, byte[]?> nativeResolver,
            Func<string, string> nativePathProvider)
            : base(isCollectible: true)
        {
            _managedResolver = managedResolver;
            _nativeBytesResolver = nativeResolver;
            _nativePathProvider = nativePathProvider;

            Resolving += OnResolveManaged;
            ResolvingUnmanagedDll += OnResolveUnmanaged;
        }

        private Assembly? OnResolveManaged(AssemblyLoadContext alc, AssemblyName name)
        {
            var bytes = _managedResolver(name);
            if (bytes == null) return null;

            using var ms = new MemoryStream(bytes, writable: false);
            return LoadFromStream(ms);
        }

        private IntPtr OnResolveUnmanaged(Assembly assembly, string unmanagedDllName)
        {
            var path = _nativePathProvider(unmanagedDllName);
            if (File.Exists(path))
            {
                return LoadUnmanagedDllFromPath(path);
            }

            var bytes = _nativeBytesResolver(unmanagedDllName);
            if (bytes != null)
            {
                throw new InvalidOperationException("Unexpected native resolver path; provider should materialize.");
            }

            return IntPtr.Zero;
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            // Keine Default-Suchpfade � alles �ber Resolving-Event
            return null;
        }

        public void Dispose()
        {
            Resolving -= OnResolveManaged;
            ResolvingUnmanagedDll -= OnResolveUnmanaged;
        }
    }

}
