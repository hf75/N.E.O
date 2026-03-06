using System;
using System.Diagnostics;
using Python.Runtime;

namespace Neo.App
{
    /// <summary>
    /// Lädt Python-Module und installiert sie bei Bedarf zur Laufzeit per pip.
    /// </summary>
    public static class PythonModuleLoader
    {
        /// <summary>
        /// Versucht, ein Modul zu importieren. Falls es fehlt, wird versucht,
        /// das entsprechende Paket per "pip install" nachzuinstallieren.
        /// Danach wird erneut importiert.
        /// 
        /// Muss NACH PythonHost.SetPythonEnvAndInit() verwendet werden.
        /// </summary>
        /// <param name="moduleName">Name beim import (z.B. ""torch"").</param>
        /// <param name="pipPackageName">
        /// Paketname für pip (Standard: identisch zu moduleName).
        /// </param>
        public static dynamic LoadModule(string moduleName, string pipPackageName = null)
        {
            if (string.IsNullOrEmpty(moduleName))
            {
                throw new ArgumentException("moduleName must not be null or empty.", nameof(moduleName));
            }

            if (pipPackageName == null)
            {
                pipPackageName = moduleName;
            }

            using (Py.GIL())
            {
                try
                {
                    // 1. Direkter Importversuch
                    return Py.Import(moduleName);
                }
                catch (PythonException ex)
                {
                    // 2. Prüfen, ob es wirklich ein "Modul fehlt"-Fall ist
                    if (!IsMissingModuleException(ex, moduleName))
                    {
                        // anderer Fehler → einfach durchreichen
                        throw;
                    }

                    Console.WriteLine("[PythonModuleLoader] Module '" + moduleName +
                                      "' nicht gefunden. Versuche 'pip install " +
                                      pipPackageName + "' ...");

                    // 3. pip versuchen
                    TryInstallWithPip(pipPackageName);

                    // 4. Zweiter Importversuch – diesmal sauber separiert und mit besserer Fehlermeldung
                    try
                    {
                        return Py.Import(moduleName);
                    }
                    catch (PythonException ex2)
                    {
                        // Hier sind wir, wenn pip das Paket nicht installieren konnte
                        throw new InvalidOperationException(
                            "Python module '" + moduleName +
                            "' could not be imported even after trying 'pip install " +
                            pipPackageName + "'. " +
                            "Check the console output for pip/installation errors " +
                            "and verify that this package is available for the current " +
                            "Python version and platform.",
                            ex2);
                    }
                }
            }
        }

        /// <summary>
        /// Ermittelt robust, ob eine PythonException einem fehlenden Modul entspricht.
        /// </summary>
        private static bool IsMissingModuleException(PythonException ex, string moduleName)
        {
            string typeName = ex.Type != null ? ex.Type.Name : string.Empty;
            string message = ex.Message ?? string.Empty;

            // Typcheck (ModuleNotFoundError / ImportError)
            if (string.Equals(typeName, "ModuleNotFoundError", StringComparison.Ordinal) ||
                string.Equals(typeName, "ImportError", StringComparison.Ordinal))
            {
                return true;
            }

            // Fallback: auf die typische Fehlermeldung prüfen
            if (message.IndexOf("No module named", StringComparison.OrdinalIgnoreCase) >= 0 &&
                message.IndexOf(moduleName, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            return false;
        }

        private static void TryInstallWithPip(string pipPackageName)
        {
            if (string.IsNullOrEmpty(PythonHost.PackagesDirectory))
            {
                throw new InvalidOperationException(
                    "PythonHost.PackagesDirectory is not set. " +
                    "Make sure PythonHost.SetPythonEnvAndInit() was called successfully."
                );
            }

            if (string.IsNullOrEmpty(PythonHost.PythonExecutablePath))
            {
                throw new InvalidOperationException(
                    "PythonHost.PythonExecutablePath is not set. " +
                    "Embedded Python executable not available; dynamic pip installation is not supported " +
                    "for this configuration."
                );
            }

            string pythonExe = PythonHost.PythonExecutablePath;
            string targetDir = PythonHost.PackagesDirectory;

            // Wir verwenden einen einfachen ProcessStartInfo-Aufruf:
            // <python.exe> -m pip install <paket> -t "<targetDir>"
            string arguments = "-m pip install " + pipPackageName + " -t \"" + targetDir + "\"";

            Console.WriteLine("[PythonModuleLoader] Starting pip:");
            Console.WriteLine("  exe : " + pythonExe);
            Console.WriteLine("  args: " + arguments);

            var psi = new ProcessStartInfo
            {
                FileName = pythonExe,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            // Optional: WorkingDirectory auf das PythonRoot setzen
            if (!string.IsNullOrEmpty(PythonHost.PythonRoot))
            {
                psi.WorkingDirectory = PythonHost.PythonRoot;
            }

            using (var process = Process.Start(psi))
            {
                if (process == null)
                {
                    throw new InvalidOperationException("Failed to start python process for pip.");
                }

                string stdout = process.StandardOutput.ReadToEnd();
                string stderr = process.StandardError.ReadToEnd();

                process.WaitForExit();

                Console.WriteLine("[PythonModuleLoader] pip stdout:");
                Console.WriteLine(stdout);
                Console.WriteLine("[PythonModuleLoader] pip stderr:");
                Console.WriteLine(stderr);

                if (process.ExitCode != 0)
                {
                    throw new InvalidOperationException(
                        "pip failed while trying to install package '" + pipPackageName +
                        "'. ExitCode=" + process.ExitCode + Environment.NewLine +
                        "stdout:" + Environment.NewLine + stdout + Environment.NewLine +
                        "stderr:" + Environment.NewLine + stderr
                    );
                }
            }
        }
    }
}
