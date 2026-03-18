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

        // Cache f�r Native-DLL-Temp-Pfade (Name -> absoluter Pfad im AC-Temp)
        private readonly Dictionary<string, string> _nativePathCache = new(StringComparer.OrdinalIgnoreCase);

        public MainWindow()
        {
            InitializeComponent();

            // Entspricht WPFs Activated; in Avalonia vorhanden.
            this.Activated += OnActivated;
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
                    WaitOverlay.IsVisible = false;
                }
                else
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        dynamicContent.Content = userControl;
                        WaitOverlay.IsVisible = false;
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
