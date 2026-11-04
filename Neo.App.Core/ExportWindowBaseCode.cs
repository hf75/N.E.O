using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Neo.ErrorSurfacing;

namespace Neo.App
{
    public class ExportWindowBaseCode
    {
        public static string CreateBaseCodeForExport(string windowTitle)
        {
            // Erstellt den vollständigen C#-Code für die exportierte Anwendung. Das Fehlerfenster
            // (NeoErrorWindow) kommt aus Neo.ErrorSurfacing, wie bei den Avalonia-Exporten, und wird
            // direkt in den Namespace eingefügt, um keine zusätzlichen Dateien zu benötigen.
            return $@"
using System;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Runtime.InteropServices;
using System.Linq;
using System.Collections.Generic;

namespace Neo.App
{{
    public class MainWindow : Window
    {{
        public MainWindow()
        {{
            this.Title = ""{windowTitle}"";
            this.Width = 1024;
            this.Height = 768;

            // Füge das UserControl als Inhalt des Fensters hinzu
            DynamicUserControl control = new DynamicUserControl();
            this.Content = control;

            this.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }}
    }}

    // Die Anwendungs-Klasse mit Einstiegspunkt
    public class App : Application
    {{
        [DllImport(""kernel32.dll"", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibrary(string lpFileName);

        [DllImport(""kernel32.dll"", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FreeLibrary(IntPtr hModule);

        [STAThread]
        public static void Main()
        {{
            // Eine Ausnahme, die niemand fängt, erscheint in einem Fenster, und die App läuft weiter
            // (UI-Thread und Tasks; auf einem anderen Thread zeigt das Fenster sie, bevor die App endet).
            NeoErrorWindow.Install();

            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

            PreloadAssemblies();

            App app = new App();

            // Scheitert schon das Hauptfenster, steht der Fehler an seiner Stelle.
            app.Run(NeoErrorWindow.MainWindow(() => new MainWindow()));
        }}

        private static void PreloadAssemblies()
        {{
            string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
            var loadedNativeHandles = new List<IntPtr>();

            foreach (string dllFile in Directory.EnumerateFiles(baseDirectory, ""*.dll""))
            {{
                try
                {{
                    AssemblyName testName = AssemblyName.GetAssemblyName(dllFile);
                    bool alreadyLoaded = AppDomain.CurrentDomain.GetAssemblies()
                        .Any(a => AssemblyName.ReferenceMatchesDefinition(testName, a.GetName()));
                    if (!alreadyLoaded)
                    {{
                        Assembly.LoadFrom(dllFile);
                    }}
                }}
                catch (BadImageFormatException)
                {{
                    IntPtr handle = LoadLibrary(dllFile);
                    if (handle != IntPtr.Zero)
                    {{
                        loadedNativeHandles.Add(handle);
                    }}
                }}
                catch (Exception)
                {{
                }}
            }}
        }}
    }}

{ExportErrors.WpfSource}
}}";
        }

        /// <summary>
        /// Erstellt den kompletten C#-Code für eine Avalonia-Export-App (Cross-Platform).
        /// Der Einstiegspunkt ist Program.Main (ohne direkte Avalonia-Abhängigkeiten),
        /// der zuerst alle DLLs aus dem EXE-Verzeichnis lädt und dann Avalonia startet.
        /// </summary>
        public static string CreateBaseCodeForExportAvalonia(string windowTitle)
        {
            string template = @"
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace Neo.App
{
    // Einstiegspunkt OHNE direkte Avalonia-Dependencies im Typ
    public static class Program
    {
[STAThread]
        public static void Main(string[] args)
        {
            try
            {
                System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

                // ZUERST: alles aus dem EXE-Verzeichnis laden
                PreloadAssemblies();
                object builder = BuildAvaloniaApp();
                if (builder == null)
                    throw new InvalidOperationException(""BuildAvaloniaApp hat null zurückgegeben."");

                // Typ der Extension-Klasse aus der Avalonia.Controls-Assembly holen
                var extType = Type.GetType(
                    ""Avalonia.ClassicDesktopStyleApplicationLifetimeExtensions, Avalonia.Controls"",
                    throwOnError: false);

                if (extType == null)
                    throw new InvalidOperationException(
                        ""ClassicDesktopStyleApplicationLifetimeExtensions in Avalonia.Controls nicht gefunden."");

                // Passendes Overload suchen: (AppBuilder, string[], ...)
                MethodInfo target = null;
                foreach (var m in extType.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (m.Name != ""StartWithClassicDesktopLifetime"")
                        continue;

                    var ps = m.GetParameters();
                    if (ps.Length >= 2 &&
                        ps[0].ParameterType.FullName == ""Avalonia.AppBuilder"" &&
                        ps[1].ParameterType == typeof(string[]))
                    {
                        target = m;
                        break;
                    }
                }

                if (target == null)
                    throw new InvalidOperationException(
                        ""Kein passendes StartWithClassicDesktopLifetime(AppBuilder, string[], ...) Overload gefunden."");

                var parameters = target.GetParameters();
                object result;

                if (parameters.Length == 2)
                {
                    // Signatur: (AppBuilder, string[])
                    result = target.Invoke(null, new object[] { builder, args });
                }
                else if (parameters.Length == 3)
                {
                    // Signatur: (AppBuilder, string[], ShutdownMode) ODER (AppBuilder, string[], Action<IClassicDesktopStyleApplicationLifetime>?)
                    // -> 3. Parameter auf null setzen + Default greifen lassen
                    var invokeArgs = new object[] { builder, args, null };
                    result = target.Invoke(null, invokeArgs);
                }
                else
                {
                    throw new InvalidOperationException(
                        ""Unerwartete Parameteranzahl bei StartWithClassicDesktopLifetime: "" + parameters.Length);
                }

            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(""Fatal error in Program.Main:"");
                Console.Error.WriteLine(ex.ToString());
                throw;
            }
        }

        // KEIN Avalonia-Typ in der Signatur, nur object
        private static object BuildAvaloniaApp()
        {
            // Hier ist Avalonia jetzt ok – wird erst nach PreloadAssemblies aufgerufen.
            return AppBuilder.Configure<App>()
                             .UsePlatformDetect()
                             .LogToTrace();
        }

        /// <summary>
        /// Lädt alle verwalteten Assemblies im Basisverzeichnis
        /// und delegiert das Laden nativer Bibliotheken an den NativeLoader.
        /// Das passiert VOR dem ersten Zugriff auf Avalonia-Typen.
        /// </summary>
        private static void PreloadAssemblies()
        {
            string baseDirectory = AppContext.BaseDirectory;
            var nativeHandles = new List<IntPtr>();

            // Managed Assemblies (.NET)
            foreach (string dllFile in Directory.EnumerateFiles(baseDirectory, ""*.dll""))
            {
                try
                {
                    AssemblyName assemblyName = AssemblyName.GetAssemblyName(dllFile);

                    bool alreadyLoaded = AppDomain.CurrentDomain
                        .GetAssemblies()
                        .Any(a => AssemblyName.ReferenceMatchesDefinition(assemblyName, a.GetName()));

                    if (!alreadyLoaded)
                    {
                        Assembly.LoadFrom(dllFile);
                    }
                }
                catch (BadImageFormatException)
                {
                    // Keine gültige .NET-Assembly -> wahrscheinlich native DLL
                    NativeLoader.TryLoadNative(dllFile, nativeHandles);
                }
                catch
                {
                    // Andere Fehler ignorieren (z.B. beschädigte Dateien, Zugriffsprobleme etc.)
                }
            }

            // OS-spezifische native Bibliotheken (.so/.dylib) nachladen
            NativeLoader.PreloadPlatformSpecific(baseDirectory, nativeHandles);
        }
    }

    // Anwendungs-Klasse für Avalonia (Desktop-Stil), ohne eigenen Main()
    public class App : Application
    {
        public override void Initialize()
            {
                // Wichtig: Theme registrieren, sonst ist das Fenster „nackt“ und bleibt quasi transparent
                Styles.Add(new FluentTheme(null));

                // Optional: explizit eine Variante erzwingen
                RequestedThemeVariant = ThemeVariant.Default;
                // oder: ThemeVariant.Light / ThemeVariant.Default
            }

        public override void OnFrameworkInitializationCompleted()
        {
            // Eine Ausnahme, die niemand fängt, erscheint in einem Fenster, und die App läuft weiter
            // (UI-Thread und Tasks; auf einem anderen Thread zeigt das Fenster sie, bevor die App endet).
            // Erst hier: NeoErrorWindow nennt Avalonia-Typen, die erst nach PreloadAssemblies da sind.
            NeoErrorWindow.Install();

            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                // Scheitert schon das Hauptfenster, steht der Fehler an seiner Stelle.
                desktop.MainWindow = NeoErrorWindow.MainWindow(() => new MainWindow());
            }

            base.OnFrameworkInitializationCompleted();
        }
    }

    /// <summary>
    /// Lädt native Bibliotheken OS-abhängig:
    /// - Windows: .dll via LoadLibrary
    /// - Linux:  .so via dlopen(""libdl"")
    /// - macOS:  .dylib via dlopen(""libdl.dylib"")
    /// </summary>
    internal static class NativeLoader
    {
        private const int RTLD_NOW = 2;

        // Windows
        [DllImport(""kernel32.dll"", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibrary(string lpFileName);

        // Linux (.so)
        [DllImport(""libdl"")]
        private static extern IntPtr dlopen_linux(string fileName, int flags);

        // macOS (.dylib)
        [DllImport(""libdl.dylib"")]
        private static extern IntPtr dlopen_macos(string fileName, int flags);

        /// <summary>
        /// Versucht, eine native Bibliothek zu laden. Pfad ist absolut.
        /// Die eigentliche API hängt vom Betriebssystem ab.
        /// </summary>
        public static void TryLoadNative(string path, List<IntPtr> handles)
        {
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    var handle = LoadLibrary(path);
                    if (handle != IntPtr.Zero)
                    {
                        handles.Add(handle);
                    }
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    var handle = dlopen_linux(path, RTLD_NOW);
                    if (handle != IntPtr.Zero)
                    {
                        handles.Add(handle);
                    }
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    var handle = dlopen_macos(path, RTLD_NOW);
                    if (handle != IntPtr.Zero)
                    {
                        handles.Add(handle);
                    }
                }
            }
            catch
            {
                // Einzelne fehlerhafte Libs ignorieren, damit nicht die ganze App crasht.
            }
        }

        /// <summary>
        /// Lädt zusätzliche native Bibliotheken mit OS-typischen Extensionen.
        /// Unter Windows werden native .dlls bereits über TryLoadNative in PreloadAssemblies behandelt.
        /// </summary>
        public static void PreloadPlatformSpecific(string baseDirectory, List<IntPtr> handles)
        {
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    foreach (var soFile in Directory.EnumerateFiles(baseDirectory, ""*.so""))
                    {
                        TryLoadNative(soFile, handles);
                    }
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    foreach (var dylibFile in Directory.EnumerateFiles(baseDirectory, ""*.dylib""))
                    {
                        TryLoadNative(dylibFile, handles);
                    }
                }
                // Windows: keine zusätzliche Behandlung nötig, .dlls wurden oben schon gescannt.
            }
            catch
            {
                // Fehler ignorieren; gescheiterte Einzel-Loads sollen die App nicht beenden.
            }
        }
    }

    // Hauptfenster der exportierten Avalonia-Anwendung
    public class MainWindow : Window
    {
        public MainWindow()
        {
            Title = ""<<WINDOW_TITLE>>"";
            Width = 1024;
            Height = 768;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;

            this.Background = new SolidColorBrush(Colors.DarkSlateGray);

            // Füge das UserControl als Inhalt des Fensters hinzu
            DynamicUserControl control = new DynamicUserControl();

            control.HorizontalAlignment = HorizontalAlignment.Stretch;
            control.VerticalAlignment = VerticalAlignment.Stretch;

            this.Content = control;
        }
    }

<<ERROR_WINDOW>>
}
";

            // Fehlerfenster einfügen, Fenstertitel sicher einbetten (Anführungszeichen escapen)
            return template
                .Replace("<<ERROR_WINDOW>>", ExportErrors.AvaloniaSource)
                .Replace("<<WINDOW_TITLE>>", windowTitle.Replace("\"", "\"\""));
        }
    }
}
