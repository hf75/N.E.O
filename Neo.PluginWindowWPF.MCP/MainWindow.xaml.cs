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

namespace Neo.PluginWindowWPF.MCP
{
    public partial class MainWindow : Window
    {
        private AssemblyLoadContext? pluginLoadContext;

        private readonly object _pluginUnloadGate = new();
        private bool _pluginUnloadScheduled;

        private readonly Dictionary<string, string> _nativePathCache = new(StringComparer.OrdinalIgnoreCase);

        // Tracks whether a control was ever successfully loaded
        public bool HasEverLoadedControl { get; private set; }

        // Elapsed timer for wait overlay
        private readonly Stopwatch _waitStopwatch = new();
        private DispatcherTimer? _waitTimer;

        // Smart Edit (Ctrl+K)
        internal ChatOverlay? _chatOverlay;
        internal SmartCompiler? _smartCompiler;
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
            var existingContent = this.Content as UIElement;
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
            if (_chatOverlay.Parent != null) return;

            var existing = dynamicContent.Content as UIElement;
            if (existing == null) return;

            dynamicContent.Content = null;
            var grid = new Grid();
            grid.Children.Add(existing);
            grid.Children.Add(_chatOverlay);
            dynamicContent.Content = grid;
        }

        /// <summary>
        /// Returns the actual user control, skipping the ChatOverlay wrapper Grid.
        /// </summary>
        internal FrameworkElement? GetLoadedUserControl()
        {
            var content = dynamicContent.Content as UIElement;
            if (content is Grid overlayGrid)
            {
                foreach (UIElement child in overlayGrid.Children)
                {
                    if (child is FrameworkElement fe && child is not ChatOverlay)
                        return fe;
                }
            }
            return content as FrameworkElement;
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
                // 1. Extract current code
                var root = GetLoadedUserControl();
                string currentCode;
                if (root != null && root is UserControl && Application.Current is App app)
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

                if (_smartCompiler.LastCompiledCode == null)
                    _smartCompiler.SeedCode(currentCode);

                // 3. Compile
                SmartCompiler.CompileResult compileResult;
                if (chatResult.Patch != null)
                    compileResult = await _smartCompiler.PatchAndCompileAsync(chatResult.Patch);
                else if (chatResult.Code != null)
                    compileResult = await _smartCompiler.CompileAsync(chatResult.Code);
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

                await Dispatcher.InvokeAsync(() =>
                {
                    HandleLoadUserControlFromBytes(
                        mainAssemblyBytes: compileResult.DllBytes!,
                        explicitControlTypeName: null,
                        managedAssembliesByFullName: new Dictionary<string, byte[]>(),
                        nativeLibrariesByBasename: new Dictionary<string, byte[]>());

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

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            // Ctrl+K: Toggle chat overlay
            if (e.Key == Key.K && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                _chatOverlay?.Toggle();
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
        // Bytes-basierter Ladeweg
        // ================================
        public void HandleLoadUserControlFromBytes(
            byte[] mainAssemblyBytes,
            string? explicitControlTypeName,
            IDictionary<string, byte[]> managedAssembliesByFullName,
            IDictionary<string, byte[]> nativeLibrariesByBasename)
        {
            UnloadUserControlPlugin();

            var plc = new SandboxPluginLoadContext(
                managedResolver: an =>
                {
                    if (!string.IsNullOrEmpty(an.FullName) &&
                        managedAssembliesByFullName.TryGetValue(an.FullName!, out var b1))
                        return b1;

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

                Type? controlType = null;
                if (!string.IsNullOrWhiteSpace(explicitControlTypeName))
                    controlType = pluginAssembly.GetType(explicitControlTypeName!, throwOnError: false, ignoreCase: false);

                if (controlType == null)
                {
                    try
                    {
                        controlType = pluginAssembly.GetTypes()
                            .FirstOrDefault(t => typeof(UserControl).IsAssignableFrom(t));
                    }
                    catch (ReflectionTypeLoadException ex)
                    {
                        controlType = ex.Types
                            .Where(t => t != null && typeof(UserControl).IsAssignableFrom(t))
                            .FirstOrDefault();
                    }
                }

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
                }
                else
                {
                    Dispatcher.Invoke(() =>
                    {
                        dynamicContent.Content = userControl;
                        HasEverLoadedControl = true;
                        WaitOverlay.Visibility = Visibility.Collapsed;
                        StopWaitTimer();
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
                WaitOverlay.Visibility = Visibility.Visible;
                WaitStatusText.Text = "Generating new code...";
                StartWaitTimer();
            }
            catch { }

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
        // Native-Bytes -> Temp materialisieren
        // ==========================================================
        private string GetOrMaterializeNativeToTemp(string nameOrBase,
            IDictionary<string, byte[]> nativeByBasename)
        {
            var fileName = nameOrBase.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                ? nameOrBase : nameOrBase + ".dll";

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
            return Convert.ToHexString(h.AsSpan(0, 6));
        }
    }

    // ==========================================================
    // SandboxPluginLoadContext (Bytes-only)
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
                return LoadUnmanagedDllFromPath(path);

            var bytes = _nativeBytesResolver(unmanagedDllName);
            if (bytes != null)
                throw new InvalidOperationException("Unexpected native resolver path; provider should materialize.");

            return IntPtr.Zero;
        }

        protected override Assembly? Load(AssemblyName assemblyName) => null;

        public void Dispose()
        {
            Resolving -= OnResolveManaged;
            ResolvingUnmanagedDll -= OnResolveUnmanaged;
        }
    }
}
