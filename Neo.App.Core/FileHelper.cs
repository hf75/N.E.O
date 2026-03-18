using System.IO;
using System.Diagnostics;

namespace Neo.App
{
    public static class FileHelper
    {
        /// <summary>
        /// Kopiert alle DLLs, deren absolute Pfade in der Liste angegeben sind, in das Zielverzeichnis.
        /// </summary>
        /// <param name="dllPaths">Liste der absoluten Pfade zu den DLLs.</param>
        /// <param name="targetDirectory">Das Verzeichnis, in das die DLLs kopiert werden sollen.</param>
        public static void CopyDlls(List<string> dllPaths, string targetDirectory)
        {
            // Überprüfen, ob das Zielverzeichnis existiert, ansonsten anlegen.
            if (!Directory.Exists(targetDirectory))
            {
                Directory.CreateDirectory(targetDirectory);
            }

            // Alle Pfade durchgehen und die DLLs kopieren
            foreach (string dllPath in dllPaths)
            {
                if (File.Exists(dllPath))
                {
                    // Ermitteln des Dateinamens aus dem Pfad
                    string fileName = Path.GetFileName(dllPath);
                    string targetFilePath = Path.Combine(targetDirectory, fileName);

                    try
                    {
                        // Kopieren der Datei, true erlaubt das Überschreiben existierender Dateien.
                        File.Copy(dllPath, targetFilePath, true);
                    }
                    catch (IOException ex)
                    {
                        // Fehlerbehandlung beim Kopiervorgang
                        Debug.WriteLine($"Fehler beim Kopieren der Datei '{dllPath}': {ex.Message}");
                    }
                }
                else
                {
                    // Falls die Datei nicht gefunden wurde
                    Debug.WriteLine($"Datei nicht gefunden: {dllPath}");
                }
            }
        }

        public static void ClearDirectory(string path)
        {
            if (Directory.Exists(path))
            {
                try
                {
                    DirectoryInfo directory = new DirectoryInfo(path);

                    foreach (FileInfo file in directory.GetFiles())
                    {
                        file.Delete();
                    }

                    foreach (DirectoryInfo dir in directory.GetDirectories())
                    {
                        dir.Delete(true);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Fehler beim Löschen des Directory: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Durchsucht das angegebene Verzeichnis und dessen direkte Unterverzeichnisse 
        /// (eine Ebene) nach .cs-Dateien. Lädt den Inhalt jeder Datei als String 
        /// und gibt eine Liste dieser Strings zurück.
        /// </summary>
        /// <param name="rootDirectory">
        /// Das Wurzelverzeichnis, in dem die Suche ausgeführt werden soll.
        /// </param>
        /// <returns>
        /// Eine Liste mit den Inhalten aller gefundenen .cs-Dateien.
        /// </returns>
        public static List<string> LoadAllCsFilesOneLevel(string rootDirectory)
        {
            // Liste zum Sammeln aller Inhalte der gefundenen .cs-Dateien
            var fileContents = new List<string>();

            // 1) Prüfung, ob das Verzeichnis existiert
            if (!Directory.Exists(rootDirectory))
            {
                throw new DirectoryNotFoundException(
                    $"Das angegebene Verzeichnis '{rootDirectory}' wurde nicht gefunden."
                );
            }

            try
            {
                // 2) Zuerst alle .cs-Dateien im Wurzelverzeichnis selbst (TopDirectoryOnly)
                string[] csFilesInRoot = Directory.GetFiles(rootDirectory, "*.cs", SearchOption.TopDirectoryOnly);

                foreach (string filePath in csFilesInRoot)
                {
                    string content = File.ReadAllText(filePath);
                    fileContents.Add(content);
                }

                // 3) Anschließend alle direkten Unterverzeichnisse (nur eine Ebene tiefer)
                string[] subDirectories = Directory.GetDirectories(rootDirectory, "*", SearchOption.TopDirectoryOnly);

                foreach (string subDir in subDirectories)
                {
                    // In jedem Unterverzeichnis ebenfalls alle .cs-Dateien (TopDirectoryOnly)
                    string[] csFilesInSub = Directory.GetFiles(subDir, "*.cs", SearchOption.TopDirectoryOnly);

                    foreach (string filePath in csFilesInSub)
                    {
                        string content = File.ReadAllText(filePath);
                        fileContents.Add(content);
                    }
                }
            }
            catch (UnauthorizedAccessException ex)
            {
                // Wenn kein Zugriff auf Dateien/Verzeichnisse besteht
                throw new UnauthorizedAccessException("Zugriff verweigert.", ex);
            }
            catch (Exception ex)
            {
                // Allgemeine Fehlerbehandlung für unerwartete Fehler
                throw new Exception("Es ist ein unerwarteter Fehler aufgetreten.", ex);
            }

            // 4) Rückgabe aller gelesenen Inhalte
            return fileContents;
        }

        public static List<string> FindAllDllFilesOneLevel(string rootDirectory)
        {
            // Liste zum Sammeln aller Inhalte der gefundenen .cs-Dateien
            var dlls = new List<string>();

            // 1) Prüfung, ob das Verzeichnis existiert
            if (!Directory.Exists(rootDirectory))
            {
                throw new DirectoryNotFoundException(
                    $"Das angegebene Verzeichnis '{rootDirectory}' wurde nicht gefunden."
                );
            }

            try
            {
                string[] dllFiles = Directory.GetFiles(rootDirectory, "*.dll", SearchOption.AllDirectories);
                dlls.AddRange(dllFiles);
            }
            catch (UnauthorizedAccessException ex)
            {
                // Wenn kein Zugriff auf Dateien/Verzeichnisse besteht
                throw new UnauthorizedAccessException("Zugriff verweigert.", ex);
            }
            catch (Exception ex)
            {
                // Allgemeine Fehlerbehandlung für unerwartete Fehler
                throw new Exception("Es ist ein unerwarteter Fehler aufgetreten.", ex);
            }

            // 4) Rückgabe aller gelesenen Inhalte
            return dlls;
        }
    }
}
