using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Neo.App
{
    /// <summary>
    /// Definiert die Typen von .NET Runtimes, nach denen gesucht werden kann.
    /// </summary>
    public enum DotNetRuntimeType
    {
        /// <summary>
        /// Die Basis .NET Runtime (Microsoft.NETCore.App).
        /// </summary>
        NetCoreApp,
        /// <summary>
        /// Die .NET Runtime inklusive Windows Desktop Komponenten (Microsoft.WindowsDesktop.App).
        /// Erforderlich für WPF und Windows Forms Anwendungen.
        /// </summary>
        WindowsDesktopApp
    }

    public static class DotNetRuntimeFinder
    {
        /// <summary>
        /// Ermittelt den vollständigen Pfad zum Verzeichnis der höchsten installierten .NET Runtime
        /// des angegebenen Typs, optional gefiltert nach einer bestimmten Hauptversion.
        /// </summary>
        /// <param name="runtimeType">Der Typ der Runtime, nach dem gesucht werden soll
        /// (z.B. NetCoreApp für Microsoft.NETCore.App oder WindowsDesktopApp für Microsoft.WindowsDesktop.App).</param>
        /// <param name="majorVersionFilter">
        /// Optional. Wenn ein Wert angegeben wird (z.B. 8), wird nur nach der höchsten Runtime
        /// gesucht, deren Hauptversion diesem Wert entspricht (z.B. die höchste 8.x.x).
        /// Wenn null (Standard), wird die absolut höchste installierte Version des angegebenen Typs gesucht.
        /// </param>
        /// <returns>
        /// Den Pfad zum Verzeichnis der höchsten passenden Runtime-Version
        /// oder null, wenn keine passende Runtime gefunden wurde, der dotnet-Befehl nicht ausgeführt werden konnte
        /// oder ein Fehler auftrat.
        /// </returns>
        public static string? GetHighestRuntimePath(
            DotNetRuntimeType runtimeType, // Parameter für den Runtime-Typ hinzugefügt
            int? majorVersionFilter = null)
        {
            string? highestVersionPath = null;
            Version? highestVersion = null;

            // Den Namen der Runtime basierend auf dem Enum ermitteln
            string runtimeName;
            switch (runtimeType)
            {
                case DotNetRuntimeType.NetCoreApp:
                    runtimeName = "Microsoft.NETCore.App";
                    break;
                case DotNetRuntimeType.WindowsDesktopApp:
                    runtimeName = "Microsoft.WindowsDesktop.App";
                    break;
                default:
                    // Sollte mit Enum nicht passieren, aber zur Sicherheit
                    return null;
            }

            try
            {
                var processStartInfo = new ProcessStartInfo
                {
                    FileName = "dotnet",
                    Arguments = "--list-runtimes",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    // RedirectStandardError = true, // Bei Bedarf einkommentieren
                };

                using (var process = new Process { StartInfo = processStartInfo })
                {
                    process.Start();
                    string output = process.StandardOutput.ReadToEnd();
                    // string errorOutput = process.StandardError.ReadToEnd(); // Bei Bedarf einkommentieren
                    process.WaitForExit();

                    if (process.ExitCode != 0)
                    {
                        Debug.WriteLine($"Fehler beim Ausführen von 'dotnet --list-runtimes'. Exit Code: {process.ExitCode}");
                        //Console.Error.WriteLine($"Fehler beim Ausführen von 'dotnet --list-runtimes'. Exit Code: {process.ExitCode}");
                        // Console.Error.WriteLine($"Error Output: {errorOutput}"); // Bei Bedarf einkommentieren
                        return null;
                    }

                    // Regex dynamisch erstellen, um den korrekten Runtime-Namen zu verwenden
                    // Wichtig: Regex.Escape verwenden, falls der Name Sonderzeichen enthält (hier nur '.')
                    string escapedRuntimeName = Regex.Escape(runtimeName);
                    // Beispiel-Regex: ^Microsoft\.WindowsDesktop\.App\s+([\d\.]+)\s+\[(.*?)\]
                    var regex = new Regex($@"^{escapedRuntimeName}\s+([\d\.]+)\s+\[(.*?)\]", RegexOptions.Multiline);
                    var matches = regex.Matches(output);

                    foreach (Match match in matches)
                    {
                        // Gruppen: 0=Ganze Zeile, 1=Version, 2=Basispfad
                        if (match.Groups.Count == 3)
                        {
                            string versionString = match.Groups[1].Value;
                            string basePath = match.Groups[2].Value; // Der Pfad in den Klammern

                            if (Version.TryParse(versionString, out Version? currentVersion))
                            {
                                // Filterung nach Hauptversion (falls angegeben)
                                if (majorVersionFilter.HasValue && currentVersion.Major != majorVersionFilter.Value)
                                {
                                    continue; // Überspringen, wenn Hauptversion nicht passt
                                }

                                // Vergleiche mit der bisher höchsten *passenden* Version
                                if (highestVersion == null || currentVersion > highestVersion)
                                {
                                    highestVersion = currentVersion;
                                    // Der vollständige Pfad ist Basispfad + Version
                                    highestVersionPath = Path.Combine(basePath, versionString);
                                }
                            }
                            else
                            {
                                Debug.WriteLine($"Konnte Version nicht parsen: {versionString} für Runtime {runtimeName}");
                                //Console.Error.WriteLine($"Konnte Version nicht parsen: {versionString} für Runtime {runtimeName}");
                            }
                        }
                    }
                }
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                Debug.WriteLine($"Fehler beim Starten des dotnet-Prozesses. Ist das .NET SDK/Runtime korrekt installiert und im PATH? Fehler: {ex.Message}");
                //Console.Error.WriteLine($"Fehler beim Starten des dotnet-Prozesses. Ist das .NET SDK/Runtime korrekt installiert und im PATH? Fehler: {ex.Message}");
                return null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Ein unerwarteter Fehler ist aufgetreten: {ex.Message}");
                //Console.Error.WriteLine($"Ein unerwarteter Fehler ist aufgetreten: {ex.Message}");
                return null;
            }

            // Stelle sicher, dass das gefundene Verzeichnis auch existiert
            if (highestVersionPath != null && Directory.Exists(highestVersionPath))
            {
                return highestVersionPath;
            }
            else if (highestVersionPath != null)
            {
                Debug.WriteLine($"Der ermittelte Pfad '{highestVersionPath}' für Runtime '{runtimeName}' existiert nicht.");
                //Console.Error.WriteLine($"Der ermittelte Pfad '{highestVersionPath}' für Runtime '{runtimeName}' existiert nicht.");
                return null;
            }
            else
            {
                // Meldung anpassen
                string filterMessage = majorVersionFilter.HasValue ? $" für Hauptversion {majorVersionFilter.Value}" : "";
                Debug.WriteLine($"Keine passende .NET Runtime ({runtimeName}){filterMessage} gefunden.");
                //Console.Error.WriteLine($"Keine passende .NET Runtime ({runtimeName}){filterMessage} gefunden.");
                return null;
            }
        }
    }
}