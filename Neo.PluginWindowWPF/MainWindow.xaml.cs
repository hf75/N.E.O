using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using Neo.IPC;
using Neo.Shared;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Neo.PluginWindowWPF
{
    public partial class MainWindow : Window
    {
        private AssemblyLoadContext? pluginLoadContext;

        private readonly object _pluginUnloadGate = new();
        private bool _pluginUnloadScheduled;

        // Cache für Native-DLL-Temp-Pfade (Name -> absoluter Pfad im AC-Temp)
        private readonly Dictionary<string, string> _nativePathCache = new(StringComparer.OrdinalIgnoreCase);

        private const string DesignNamePrefix = "__neo_";
        private const string DesignTagPrefix = "__neo:id=";

        private bool _designerModeEnabled;
        private FrameworkElement? _selectedElement;
        private string? _selectedDesignId;

        public MainWindow()
        {
            InitializeComponent();

            this.Activated += OnActivated;

            dynamicContent.PreviewMouseLeftButtonDown += DynamicContent_PreviewMouseLeftButtonDown;
            dynamicContent.LayoutUpdated += (_, _) =>
            {
                if (_designerModeEnabled && _selectedElement != null)
                    UpdateSelectionOverlay();
            };
        }

        private async void OnActivated(object? sender, EventArgs e)
        {
            if (Application.Current != null)
                await ((App)Application.Current).NotifyParentAboutActivation();
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

        public void SetDesignerMode(bool enabled)
        {
            _designerModeEnabled = enabled;
            if (!enabled)
                ClearSelection();
        }

        private void DynamicContent_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!_designerModeEnabled)
                return;

            if (TryGetDesignIdElement(e.OriginalSource as DependencyObject, out var element, out var designId))
            {
                e.Handled = true;
                SelectElement(element, designId);
                _ = SendSelectionAsync(element, designId);
            }
        }

        private void SelectElement(FrameworkElement element, string designId)
        {
            _selectedElement = element;
            _selectedDesignId = designId;
            UpdateSelectionOverlay();
        }

        private void ClearSelection()
        {
            _selectedElement = null;
            _selectedDesignId = null;
            SelectionBorder.Visibility = Visibility.Collapsed;
        }

        private void UpdateSelectionOverlay()
        {
            if (!_designerModeEnabled || _selectedElement == null)
            {
                SelectionBorder.Visibility = Visibility.Collapsed;
                return;
            }

            try
            {
                if (!SelectionOverlay.IsLoaded)
                    return;

                var transform = _selectedElement.TransformToVisual(SelectionOverlay);
                var rect = transform.TransformBounds(new Rect(new Point(0, 0), _selectedElement.RenderSize));

                Canvas.SetLeft(SelectionBorder, rect.Left);
                Canvas.SetTop(SelectionBorder, rect.Top);
                SelectionBorder.Width = Math.Max(0, rect.Width);
                SelectionBorder.Height = Math.Max(0, rect.Height);
                SelectionBorder.Visibility = Visibility.Visible;
            }
            catch
            {
                SelectionBorder.Visibility = Visibility.Collapsed;
            }
        }

        private static bool TryGetDesignIdElement(DependencyObject? start, out FrameworkElement element, out string designId)
        {
            element = null!;
            designId = string.Empty;

            DependencyObject? current = start;
            while (current != null)
            {
                if (current is FrameworkElement fe)
                {
                    if (!string.IsNullOrWhiteSpace(fe.Name) &&
                        fe.Name.StartsWith(DesignNamePrefix, StringComparison.Ordinal))
                    {
                        element = fe;
                        designId = fe.Name;
                        return true;
                    }

                    if (fe.Tag is string tag &&
                        tag.StartsWith(DesignTagPrefix, StringComparison.Ordinal))
                    {
                        element = fe;
                        designId = tag;
                        return true;
                    }
                }

                current = GetParent(current);
            }

            return false;
        }

        private static DependencyObject? GetParent(DependencyObject current)
        {
            if (current is FrameworkContentElement fce)
                return fce.Parent;

            return VisualTreeHelper.GetParent(current);
        }

        private Task SendSelectionAsync(FrameworkElement element, string designId)
        {
            if (Application.Current is not App app)
                return Task.CompletedTask;

            var props = ExtractDesignerProperties(element);
            var typeName = element.GetType().FullName ?? element.GetType().Name;
            return app.NotifyParentDesignerSelection(new DesignerSelectionMessage(designId, typeName, props));
        }

        private static Dictionary<string, string> ExtractDesignerProperties(FrameworkElement element)
        {
            var props = new Dictionary<string, string>(StringComparer.Ordinal);

            AddProperty(props, element, "Text", v => v as string);
            AddProperty(props, element, "Content", v => v as string);

            AddProperty(props, element, "FontFamily", v => v is FontFamily ff ? ff.Source : null);
            AddProperty(props, element, "FontSize", v => v is double d ? d.ToString(CultureInfo.InvariantCulture) : null);
            AddProperty(props, element, "FontWeight", v => v?.ToString());
            AddProperty(props, element, "FontStyle", v => v?.ToString());

            AddProperty(props, element, "Foreground", v => v is Brush b ? BrushToHex(b) : null);
            AddProperty(props, element, "Background", v => v is Brush b ? BrushToHex(b) : null);

            AddProperty(props, element, "Margin", v => v is Thickness t ? ThicknessToString(t) : null);
            AddProperty(props, element, "Padding", v => v is Thickness t ? ThicknessToString(t) : null);

            return props;
        }

        private static void AddProperty(
            IDictionary<string, string> props,
            FrameworkElement element,
            string propertyName,
            Func<object?, string?> converter)
        {
            try
            {
                var pi = element.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
                if (pi == null || !pi.CanRead)
                    return;

                string? value = null;
                try
                {
                    value = converter(pi.GetValue(element));
                }
                catch
                {
                    // ignore conversion failures
                }

                props[propertyName] = value ?? string.Empty;
            }
            catch
            {
                // ignore reflection failures
            }
        }

        private static string? BrushToHex(Brush brush)
        {
            if (brush is SolidColorBrush scb)
            {
                var c = scb.Color;
                return $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";
            }
            return null;
        }

        private static string ThicknessToString(Thickness t)
            => string.Join(",",
                t.Left.ToString(CultureInfo.InvariantCulture),
                t.Top.ToString(CultureInfo.InvariantCulture),
                t.Right.ToString(CultureInfo.InvariantCulture),
                t.Bottom.ToString(CultureInfo.InvariantCulture));

        // ================================
        // 1) NEU: Bytes-basierter Ladeweg
        // ================================
        /// <summary>
        /// Lädt ein UserControl vollständig aus Bytes (ohne Dateipfade).
        /// managedAssemblies: Key = AssemblyName.Name (ohne .dll), Value = Bytes
        /// nativeLibraries:   Key = Dateiname inkl. .dll,         Value = Bytes
        /// </summary>
        public void HandleLoadUserControlFromBytes(
            byte[] mainAssemblyBytes,
            string? explicitControlTypeName,
            IDictionary<string, byte[]> managedAssembliesByFullName, // FullName -> Bytes
            IDictionary<string, byte[]> nativeLibrariesByBasename    // "sqlite3.dll" -> Bytes
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

                    // 2) Fallback: SimpleName → best match (erstes FullName mit gleichem Simple)
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
                    // pure Basename, optional ohne ".dll"
                    var key = baseName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? baseName : baseName + ".dll";
                    return nativeLibrariesByBasename.TryGetValue(key, out var nb) ? nb : null;
                },
                nativePathProvider: name => GetOrMaterializeNativeToTemp(name, nativeLibrariesByBasename)
            );

            pluginLoadContext = plc;

            // Hauptassembly laden
            using (var ms = new MemoryStream(mainAssemblyBytes, writable: false))
            {
                var pluginAssembly = plc.LoadFromStream(ms)
                    ?? throw new InvalidOperationException("Main assembly could not be loaded.");

                // Typ auflösen
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
                    throw new InvalidOperationException("Kein passender UserControl-Typ gefunden.");

                // Instanz erzeugen
                if (!(Activator.CreateInstance(controlType) is UserControl userControl))
                    throw new InvalidCastException("Der erzeugte Typ ist kein UserControl.");

                // Auf UI-Thread einhängen
                if (Dispatcher.CheckAccess())
                    dynamicContent.Content = userControl;
                else
                    Dispatcher.Invoke(() => dynamicContent.Content = userControl, DispatcherPriority.Send);

                ClearSelection();
            }
        }

        // ==========================================================
        // 2) Unload
        // ==========================================================
        /// <summary>
        /// Trennt UI-Inhalte IMMER auf dem UI-Thread ab und entlädt den PluginLoadContext
        /// im Hintergrund-Thread. Reentrant-sicher, kein Cross-Thread-Zugriff.
        /// </summary>
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
            }
            catch (Exception ex) { Debug.WriteLine($"[UnloadPlugin] UI cleanup failed: {ex.Message}"); }

            ClearSelection();

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
        // 4) Native-Bytes -> AC-Temp materialisieren (einmalig)
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
                // letzte Chance: case-insensitive Suche
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

            // Fallback: letzter Ausweg – aber in AC oft verboten → nur für Nicht-AC-Betrieb
            return Path.GetTempPath();
        }

        private static string GetAcTempRoot()
        {
            // dein Parent legt <workRoot>\temp mit passender ACL an
            var root = GetAcWorkRoot();
            var temp = Path.Combine(root, "temp");
            try { Directory.CreateDirectory(temp); } catch (Exception ex) { Debug.WriteLine($"[GetAcTempRoot] {ex.Message}"); }
            return temp;
        }

        private static string ComputeShortHash(byte[] data)
        {
            using var sha = SHA256.Create();
            var h = sha.ComputeHash(data);
            return Convert.ToHexString(h.AsSpan(0, 6)); // 12 Hex-Zeichen reichen für Cache-Key
        }
    }

    // ==========================================================
    // 5) NEU: SandboxPluginLoadContext (Bytes-only)
    // ==========================================================
    /// <summary>
    /// AssemblyLoadContext, der managed Assemblies ausschließlich aus Bytes lädt
    /// und native DLLs über bereitgestellte Temp-Pfade (AppContainer-Temp) bindet.
    /// </summary>
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

        // Hauptassembly lädt man via LoadFromStream(). Dependencies kommen hier rein:
        private Assembly? OnResolveManaged(AssemblyLoadContext alc, AssemblyName name)
        {
            var bytes = _managedResolver(name);
            if (bytes == null) return null;

            using var ms = new MemoryStream(bytes, writable: false);
            return LoadFromStream(ms);
        }

        private IntPtr OnResolveUnmanaged(Assembly assembly, string unmanagedDllName)
        {
            // Variante A) Bytes liegen vor → in Temp materialisieren (einheitlich über Provider):
            var path = _nativePathProvider(unmanagedDllName);
            if (File.Exists(path))
            {
                return LoadUnmanagedDllFromPath(path);
            }

            // Variante B) (optional) Bytes direkt verfügbar? (würde hier nicht erreicht, weil Provider schon obendrüber checkt)
            var bytes = _nativeBytesResolver(unmanagedDllName);
            if (bytes != null)
            {
                throw new InvalidOperationException("Unexpected native resolver path; provider should materialize.");
            }

            return IntPtr.Zero;
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            // Kein Default-Suchpfad – alles über Resolving-Event
            return null;
        }

        public void Dispose()
        {
            Resolving -= OnResolveManaged;
            ResolvingUnmanagedDll -= OnResolveUnmanaged;
        }
    }

}
