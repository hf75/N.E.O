using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Python.Runtime;

namespace Neo.App
{
    /// <summary>
    /// Zentraler Einstiegspunkt für die Python-Initialisierung.
    /// - Versucht zuerst eine eingebettete Runtime im Unterordner ./python/{plattform}/ (exportierte Apps)
    /// - Dann user-lokal unter %LOCALAPPDATA%/Neo/Python/{platform}/python/ (on-demand Download)
    /// - Initialisiert dann PythonEngine und BeginAllowThreads() genau einmal.
    /// System-Python wird bewusst nie verwendet.
    /// </summary>
    public static class PythonHost
    {
        private static bool _initialized = false;
        private static readonly object _initLock = new object();

        /// <summary>Root-Verzeichnis der eingebetteten Python-Distribution (z.B. ...\python\win-x64).</summary>
        public static string PythonRoot { get; private set; }

        /// <summary>Pfad zur python.exe (bzw. bin/python3.11 unter Linux/macOS), falls verfügbar.</summary>
        public static string PythonExecutablePath { get; private set; }

        /// <summary>Ordner für dynamisch per pip installierte Pakete.</summary>
        public static string PackagesDirectory { get; private set; }

        private static string _pythonRoot;

        /// <summary>
        /// Konfiguriert die Python-Umgebung (embedded oder system) und ruft PythonEngine.Initialize().
        /// Mehrfache Aufrufe sind erlaubt, Initialisierung passiert genau einmal.
        /// </summary>
        public static void SetPythonEnvAndInit()
        {
            if (_initialized)
                return;

            lock (_initLock)
            {
                if (_initialized)
                    return;

                string baseDir = AppContext.BaseDirectory;
                string embeddedRoot = TryGetEmbeddedPythonRoot(baseDir);

                if (!string.IsNullOrEmpty(embeddedRoot))
                {
                    ConfigureForEmbeddedPython(embeddedRoot);
                }
                else
                {
                    throw new InvalidOperationException(
                        "No Python 3.11 runtime found. Please enable Python in Settings — " +
                        "the runtime will be downloaded automatically.");
                }

                // eigenen Paketordner anlegen (falls wir embedded arbeiten)
                if (!string.IsNullOrEmpty(PythonRoot))
                {
                    // Du kannst das bei Bedarf auf LocalApplicationData umbiegen;
                    // aktuell bleibt es wie gehabt direkt unter PythonRoot.
                    // PackagesDirectory = Path.Combine(PythonRoot, "nw-packages");                    
                    PackagesDirectory = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "Neo",
                        "PythonModules"
                    );

                    Directory.CreateDirectory(PackagesDirectory);
                }

                PythonEngine.Initialize();
                PythonEngine.BeginAllowThreads();

                // PackagesDirectory in sys.path einhängen
                if (!string.IsNullOrEmpty(PackagesDirectory))
                {
                    using (Py.GIL())
                    {
                        string p = PackagesDirectory.Replace("\\", "\\\\");
                        string code =
                            "import sys\n" +
                            "p = r'" + p + "'\n" +
                            "if p not in sys.path:\n" +
                            "    sys.path.insert(0, p)\n" +
                            "print('[PythonHost] sys.path includes:', p)\n";

                        PythonEngine.Exec(code);
                    }
                }

                _initialized = true;
            }
        }

        /// <summary>
        /// Führt eine Aktion innerhalb des GIL aus und prüft danach Python stderr.
        /// Falls stderr nicht-leer ist, wird eine Exception geworfen damit der Auto-Repair greift.
        /// Ersetzt using (Py.GIL()) für fehleranfällige Python-Aufrufe.
        /// </summary>
        public static void RunWithErrorCheck(Action pythonAction)
        {
            using (Py.GIL())
            {
                PythonEngine.Exec(
                    "import sys as _sys, io as _io\n" +
                    "_nw_old_stderr = _sys.stderr\n" +
                    "_nw_stderr_buf = _io.StringIO()\n" +
                    "_sys.stderr = _nw_stderr_buf");
                try
                {
                    pythonAction();
                }
                finally
                {
                    PythonEngine.Exec("_sys.stderr = _nw_old_stderr");
                }

                dynamic captured = PythonEngine.Eval("_nw_stderr_buf.getvalue()");
                string stderr = (string)captured;

                if (!string.IsNullOrWhiteSpace(stderr))
                    throw new InvalidOperationException("Python runtime error:\n" + stderr);
            }
        }

        /// <summary>
        /// Wie RunWithErrorCheck, aber mit Rückgabewert.
        /// </summary>
        public static T RunWithErrorCheck<T>(Func<T> pythonFunc)
        {
            T result = default;
            RunWithErrorCheck(() => { result = pythonFunc(); });
            return result;
        }

        /// <summary>
        /// Sucht nach einer Python-Runtime:
        /// 1. App-lokal im Unterordner ./python/{plattform} (für exportierte Apps)
        /// 2. User-lokal im %LOCALAPPDATA%/Neo/Python/{platform}/python/ (on-demand Download)
        /// Gibt den Root-Pfad zurück oder null, wenn nichts gefunden wurde.
        /// </summary>
        private static string TryGetEmbeddedPythonRoot(string baseDir)
        {
            string platformFolder;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                platformFolder = "win-x64";
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                platformFolder = "linux-x64";
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                platformFolder = "osx-arm64";
            else
                return null;

            // 1. App-local (exported apps ship Python alongside the executable)
            string appLocal = Path.Combine(baseDir, "python", platformFolder);
            if (Directory.Exists(appLocal))
                return appLocal;

            // 2. User-local (downloaded on-demand by PythonDownloadService)
            // install_only archives extract to python/ (not python/install/)
            string userLocal = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Neo", "Python", platformFolder, "python");
            if (Directory.Exists(userLocal))
                return userLocal;

            return null;
        }

        /// <summary>
        /// Konfiguration für eine eingebettete python-build-standalone-Distribution.
        /// Erwartet das "install_only"/"install_only_stripped"-Layout.
        /// </summary>
        private static void ConfigureForEmbeddedPython(string pythonRoot)
        {
            _pythonRoot = pythonRoot;
            PythonRoot = pythonRoot;

            string pythonDllPath;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // Layout (install_only):
                // pythonRoot/
                //   python.exe
                //   python311.dll
                //   DLLs/
                //   Lib/
                //   Lib/site-packages/
                //   ...
                PythonExecutablePath = Path.Combine(pythonRoot, "python.exe");
                pythonDllPath = Path.Combine(pythonRoot, "python311.dll");

                if (!File.Exists(pythonDllPath))
                    throw new FileNotFoundException("Embedded Python library not found: " + pythonDllPath);

                if (!File.Exists(PythonExecutablePath))
                    throw new FileNotFoundException("Embedded python executable not found: " + PythonExecutablePath);

                Runtime.PythonDLL = pythonDllPath;

                // PythonHome = Root der Distribution
                PythonEngine.PythonHome = pythonRoot;
                Environment.SetEnvironmentVariable("PYTHONHOME", pythonRoot);

                // sys.path explizit so aufbauen, dass Lib, DLLs und site-packages drin sind
                string libDir = Path.Combine(pythonRoot, "Lib");
                string dllsDir = Path.Combine(pythonRoot, "DLLs");
                string sitePackagesDir = Path.Combine(libDir, "site-packages");

                var pathParts = new List<string>();
                if (Directory.Exists(libDir)) pathParts.Add(libDir);
                if (Directory.Exists(dllsDir)) pathParts.Add(dllsDir);
                if (Directory.Exists(sitePackagesDir)) pathParts.Add(sitePackagesDir);

                if (pathParts.Count > 0)
                {
                    string pythonPath = string.Join(Path.PathSeparator.ToString(), pathParts);
                    PythonEngine.PythonPath = pythonPath;
                    Environment.SetEnvironmentVariable("PYTHONPATH", pythonPath);
                }

                // Native DLLs auffindbar machen (vcruntime, etc.)
                PrependEnvPath("PATH", pythonRoot);
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                // Layout (install_only):
                // pythonRoot/
                //   bin/python3.11
                //   lib/libpython3.11.so.1.0
                //   lib/python3.11/...
                //   lib/python3.11/lib-dynload/
                string binDir = Path.Combine(pythonRoot, "bin");
                string libRoot = Path.Combine(pythonRoot, "lib");
                string stdLibDir = Path.Combine(libRoot, "python3.11");
                string dynLibDir = Path.Combine(stdLibDir, "lib-dynload");
                string zipLib = Path.Combine(libRoot, "python311.zip"); // optional
                string sitePackagesDir = Path.Combine(stdLibDir, "site-packages");

                PythonExecutablePath = Path.Combine(binDir, "python3.11");
                pythonDllPath = Path.Combine(libRoot, "libpython3.11.so.1.0");

                if (!File.Exists(pythonDllPath))
                    throw new FileNotFoundException("Embedded Python library not found: " + pythonDllPath);

                if (!File.Exists(PythonExecutablePath))
                    throw new FileNotFoundException("Embedded python executable not found: " + PythonExecutablePath);

                Runtime.PythonDLL = pythonDllPath;

                PythonEngine.PythonHome = pythonRoot;
                Environment.SetEnvironmentVariable("PYTHONHOME", pythonRoot);

                var pathParts = new List<string>();
                if (File.Exists(zipLib)) pathParts.Add(zipLib);
                if (Directory.Exists(stdLibDir)) pathParts.Add(stdLibDir);
                if (Directory.Exists(dynLibDir)) pathParts.Add(dynLibDir);
                if (Directory.Exists(sitePackagesDir)) pathParts.Add(sitePackagesDir);

                if (pathParts.Count > 0)
                {
                    string pythonPath = string.Join(Path.PathSeparator.ToString(), pathParts);
                    PythonEngine.PythonPath = pythonPath;
                    Environment.SetEnvironmentVariable("PYTHONPATH", pythonPath);
                }

                // Native libpython + Extensions auffindbar machen
                PrependEnvPath("LD_LIBRARY_PATH", libRoot);
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                // Analog zu Linux, aber mit .dylib
                string binDir = Path.Combine(pythonRoot, "bin");
                string libRoot = Path.Combine(pythonRoot, "lib");
                string stdLibDir = Path.Combine(libRoot, "python3.11");
                string dynLibDir = Path.Combine(stdLibDir, "lib-dynload");
                string zipLib = Path.Combine(libRoot, "python311.zip"); // optional
                string sitePackagesDir = Path.Combine(stdLibDir, "site-packages");

                PythonExecutablePath = Path.Combine(binDir, "python3.11");
                pythonDllPath = Path.Combine(libRoot, "libpython3.11.dylib");

                if (!File.Exists(pythonDllPath))
                    throw new FileNotFoundException("Embedded Python library not found: " + pythonDllPath);

                if (!File.Exists(PythonExecutablePath))
                    throw new FileNotFoundException("Embedded python executable not found: " + PythonExecutablePath);

                Runtime.PythonDLL = pythonDllPath;

                PythonEngine.PythonHome = pythonRoot;
                Environment.SetEnvironmentVariable("PYTHONHOME", pythonRoot);

                var pathParts = new List<string>();
                if (File.Exists(zipLib)) pathParts.Add(zipLib);
                if (Directory.Exists(stdLibDir)) pathParts.Add(stdLibDir);
                if (Directory.Exists(dynLibDir)) pathParts.Add(dynLibDir);
                if (Directory.Exists(sitePackagesDir)) pathParts.Add(sitePackagesDir);

                if (pathParts.Count > 0)
                {
                    string pythonPath = string.Join(Path.PathSeparator.ToString(), pathParts);
                    PythonEngine.PythonPath = pythonPath;
                    Environment.SetEnvironmentVariable("PYTHONPATH", pythonPath);
                }

                PrependEnvPath("DYLD_LIBRARY_PATH", libRoot);
            }
            else
            {
                throw new PlatformNotSupportedException("Unsupported OS for embedded Python.");
            }
        }

        /// <summary>
        /// Fügt einen Ordner vorne in eine Environment-Variable ein, falls noch nicht vorhanden.
        /// Wird z.B. für PATH / LD_LIBRARY_PATH / DYLD_LIBRARY_PATH verwendet.
        /// </summary>
        private static void PrependEnvPath(string variableName, string directory)
        {
            if (string.IsNullOrEmpty(directory))
                return;

            string current = Environment.GetEnvironmentVariable(variableName) ?? string.Empty;

            if (string.IsNullOrEmpty(current))
            {
                Environment.SetEnvironmentVariable(variableName, directory);
                return;
            }

            string[] parts = current.Split(Path.PathSeparator);
            foreach (var part in parts)
            {
                if (string.Equals(part, directory, StringComparison.OrdinalIgnoreCase))
                {
                    // schon enthalten
                    return;
                }
            }

            string updated = directory + Path.PathSeparator + current;
            Environment.SetEnvironmentVariable(variableName, updated);
        }

    }
}
