using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Neo.App
{
    public class ExportWindowBaseCode
    {
        public static string CreateBaseCodeForExport(string windowTitle)
        {
            // Definiert den C#-Code für ein dynamisch erstelltes Fehlerfenster.
            // Dieses Fenster zeigt Fehlerdetails an und macht sie kopierbar.
            // Es wird direkt in den generierten Code eingefügt, um keine zusätzlichen Dateien zu benötigen.
            string exceptionWindowClass = @"
    // Ein benutzerdefiniertes Fenster zur Anzeige von unbehandelten Ausnahmen.
    public class ExceptionWindow : Window
    {
        public ExceptionWindow(Exception ex, bool isTerminating)
        {
            this.Title = ""An unexpected error occurred"";
            this.Width = 650;
            this.Height = 450;
            this.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            this.MinWidth = 400;
            this.MinHeight = 300;

            var grid = new Grid();
            grid.Margin = new Thickness(15);
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var infoLabel = new Label
            {
                Content = isTerminating 
                    ? ""The application cannot recover from this error and will now exit."" 
                    : ""The application may be able to continue after this error."",
                FontWeight = FontWeights.Bold,
                Foreground = new System.Windows.Media.SolidColorBrush(isTerminating ? System.Windows.Media.Colors.DarkRed : System.Windows.Media.Colors.Black),
                Margin = new Thickness(0, 0, 0, 10)
            };
            Grid.SetRow(infoLabel, 0);

            var scrollViewer = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            Grid.SetRow(scrollViewer, 1);

            var textBox = new TextBox
            {
                IsReadOnly = true,
                Text = ex.ToString(), // .ToString() liefert die komplette Info inkl. StackTrace
                FontFamily = new System.Windows.Media.FontFamily(""Consolas""),
                TextWrapping = TextWrapping.NoWrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                BorderThickness = new Thickness(1)
            };
            scrollViewer.Content = textBox;

            var closeButton = new Button
            {
                Content = ""Schließen"",
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 10, 0, 0),
                Padding = new Thickness(12, 6, 12, 6),
                IsDefault = true
            };
            closeButton.Click += (s, e) => this.Close();
            Grid.SetRow(closeButton, 2);

            grid.Children.Add(infoLabel);
            grid.Children.Add(scrollViewer);
            grid.Children.Add(closeButton);

            this.Content = grid;
        }
    }";

            // Erstellt den vollständigen C#-Code für die exportierte Anwendung.
            return $@"
using System;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Runtime.InteropServices;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks; // Für TaskScheduler
using System.Windows.Threading; // Für DispatcherUnhandledExceptionEventArgs

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

    // Die Anwendungs-Klasse mit Einstiegspunkt und robuster Fehlerbehandlung
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
            // Handler Nr. 1: Für unbeobachtete Task-Exceptions (asynchrone Fehler)
            TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;

            // Handler Nr. 2: Für alle anderen unbehandelten Exceptions (letzte Rettung)
            AppDomain.CurrentDomain.UnhandledException += AppDomain_UnhandledException;

            PreloadAssemblies();

            App app = new App();
            
            // Handler Nr. 3: Speziell für den UI-Thread (wichtigster Handler für WPF)
            app.DispatcherUnhandledException += App_DispatcherUnhandledException;

            MainWindow mainWindow = new MainWindow();
            app.Run(mainWindow);
        }}

        // Fängt unbehandelte Ausnahmen auf dem UI-Thread ab.
        private static void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {{
            HandleException(e.Exception, isTerminating: false);
            // Verhindert, dass die Anwendung abstürzt.
            e.Handled = true;
        }}

        // Fängt unbehandelte Ausnahmen in Hintergrund-Tasks ab, die nicht 'awaited' wurden.
        private static void TaskScheduler_UnobservedTaskException(object sender, UnobservedTaskExceptionEventArgs e)
        {{
            HandleException(e.Exception, isTerminating: false);
            // Markiert die Ausnahme als ""beobachtet"", um einen Absturz zu verhindern (wichtig für .NET 4.0).
            e.SetObserved();
        }}

        // Fängt alle anderen unbehandelten Ausnahmen in der gesamten Anwendungsdomäne ab.
        // Dies ist die letzte Instanz, die Anwendung wird danach beendet.
        private static void AppDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {{
            HandleException((Exception)e.ExceptionObject, e.IsTerminating);
        }}

        // Zentrale Methode zur Anzeige des Fehlerfensters.
        private static void HandleException(Exception ex, bool isTerminating)
        {{
            // Stellt sicher, dass das UI-Fenster auf dem UI-Thread erstellt wird.
            if (Application.Current?.Dispatcher != null)
            {{Application.Current.Dispatcher.Invoke(() =>
            {{
                var exceptionWindow = new ExceptionWindow(ex, isTerminating);
                exceptionWindow.ShowDialog();
            }});
            }}
            else
            {{
                        // Fallback, falls der Dispatcher nicht verfügbar ist.
                        MessageBox.Show(ex.ToString(), ""Critical Error"", MessageBoxButton.OK, MessageBoxImage.Error);
            }}
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

    // Die ExceptionWindow-Klasse wird hier eingefügt.
    {exceptionWindowClass}
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
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
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
                // Globale Exception-Handler
                TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
                AppDomain.CurrentDomain.UnhandledException += AppDomain_UnhandledException;

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

        // Fängt unbehandelte Ausnahmen in Hintergrund-Tasks ab, die nicht 'awaited' wurden.
        private static void TaskScheduler_UnobservedTaskException(object sender, UnobservedTaskExceptionEventArgs e)
        {
            HandleException(e.Exception, isTerminating: false);
            // Markiert die Ausnahme als ""beobachtet"", um einen Absturz zu verhindern.
            e.SetObserved();
        }

        // Fängt alle anderen unbehandelten Ausnahmen in der gesamten Anwendungsdomäne ab.
        // Dies ist die letzte Instanz, die Anwendung wird danach ggf. beendet.
        private static void AppDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            HandleException((Exception)e.ExceptionObject, e.IsTerminating);
        }

        // Zentrale Methode zur Anzeige des Fehlerfensters.
        private static void HandleException(Exception ex, bool isTerminating)
        {
            void ShowWindow()
            {
                var lifetime = Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
                var owner = lifetime?.MainWindow;

                var exceptionWindow = new ExceptionWindow(ex, isTerminating)
                {
                    Icon = owner?.Icon
                };

                if (owner != null)
                {
                    exceptionWindow.ShowDialog(owner);
                }
                else
                {
                    exceptionWindow.Show();
                }
            }

            if (Dispatcher.UIThread.CheckAccess())
            {
                ShowWindow();
            }
            else
            {
                Dispatcher.UIThread.Post(ShowWindow);
            }

            if (isTerminating)
            {
                Console.Error.WriteLine(""Fatal error:\n"" + ex);
            }
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
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                var mainWindow = new MainWindow();
                desktop.MainWindow = mainWindow;
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

    // Ein benutzerdefiniertes Fenster zur Anzeige von unbehandelten Ausnahmen.
    public class ExceptionWindow : Window
    {
        public ExceptionWindow(Exception ex, bool isTerminating)
        {
            Title = ""An unexpected error occurred"";
            Width = 650;
            Height = 450;
            MinWidth = 400;
            MinHeight = 300;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;

            var grid = new Grid
            {
                Margin = new Thickness(15)
            };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var infoLabel = new TextBlock
            {
                Text = isTerminating
                    ? ""The application cannot recover from this error and will now exit.""
                    : ""The application may be able to continue after this error."",
                FontWeight = FontWeight.Bold,
                Margin = new Thickness(0, 0, 0, 10)
            };
            infoLabel.Foreground = new SolidColorBrush(isTerminating ? Colors.DarkRed : Colors.Black);
            Grid.SetRow(infoLabel, 0);

            var textBox = new TextBox
            {
                IsReadOnly = true,
                Text = ex.ToString(), // .ToString() liefert die komplette Info inkl. StackTrace
                FontFamily = new FontFamily(""Consolas""),
                TextWrapping = TextWrapping.NoWrap
            };

            var scrollViewer = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = textBox
            };
            Grid.SetRow(scrollViewer, 1);

            var closeButton = new Button
            {
                Content = ""Schließen"",
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 10, 0, 0),
                Padding = new Thickness(12, 6, 12, 6)
            };
            closeButton.Click += (_, __) => Close();
            Grid.SetRow(closeButton, 2);

            grid.Children.Add(infoLabel);
            grid.Children.Add(scrollViewer);
            grid.Children.Add(closeButton);

            Content = grid;
        }
    }
}
";

            // Fenstertitel sicher einbetten (Anführungszeichen escapen)
            return template.Replace("<<WINDOW_TITLE>>", windowTitle.Replace("\"", "\"\""));
        }
    }
}
