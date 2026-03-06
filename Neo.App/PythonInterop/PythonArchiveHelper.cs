using System;
using System.Diagnostics;
using System.IO;
using System.Formats.Tar;
using System.Runtime.InteropServices;

namespace Neo.App
{
    /// <summary>
    /// Hilfsfunktionen zum Entpacken von Python-Archiven (TAR / TAR.GZ / TGZ).
    /// </summary>
    public static class PythonArchiveHelper
    {
        /// <summary>
        /// Entpackt ein Python-TAR-Archiv in ein Zielverzeichnis.
        /// Unterstützt:
        /// - .tar
        /// - .tar.gz / .tgz
        /// 
        /// Für .tar.zst bitte vorher extern dekomprimieren (z.B. mit "unzstd"),
        /// so dass hier ein .tar übergeben wird.
        /// </summary>
        /// <param name="tarFilePath">
        /// Absoluter Pfad zum TAR-Archiv (z.B. .../cpython-3.11.x-...-linux-x86_64.tar
        /// oder ...tar.gz / .tgz).
        /// </param>
        /// <param name="destinationDirectory">
        /// Zielverzeichnis, in das entpackt wird. Wird ggf. automatisch angelegt.
        /// </param>
        public static void ExtractPythonTar(string tarFilePath, string destinationDirectory)
        {
            if (string.IsNullOrEmpty(tarFilePath))
            {
                throw new ArgumentException("tarFilePath must not be null or empty.", nameof(tarFilePath));
            }

            if (!Path.IsPathRooted(tarFilePath))
            {
                throw new ArgumentException("tarFilePath must be an absolute path.", nameof(tarFilePath));
            }

            if (!File.Exists(tarFilePath))
            {
                throw new FileNotFoundException("TAR file not found: " + tarFilePath, tarFilePath);
            }

            if (string.IsNullOrEmpty(destinationDirectory))
            {
                throw new ArgumentException("destinationDirectory must not be null or empty.", nameof(destinationDirectory));
            }

            // Zielverzeichnis sicherstellen
            Directory.CreateDirectory(destinationDirectory);

            string extension = Path.GetExtension(tarFilePath).ToLowerInvariant();

            // .tar.gz oder .tgz → system tar (avoids .NET TarFile filename corruption)
            if (extension == ".gz" || extension == ".tgz")
            {
                ExtractGZipTar(tarFilePath, destinationDirectory);
            }
            // .tar → direkt per TarFile-API
            else if (extension == ".tar")
            {
                TarFile.ExtractToDirectory(tarFilePath, destinationDirectory, overwriteFiles: true);
            }
            // .zst oder andere exotische Endungen → bewusst nicht automatisch supporten
            else if (extension == ".zst")
            {
                throw new NotSupportedException(
                    "TAR-Dateien mit .zst-Kompression werden hier nicht direkt unterstützt. " +
                    "Bitte zuerst extern mit 'zstd'/'unzstd' in eine .tar-Datei dekomprimieren " +
                    "und dann diese Funktion mit dem .tar aufrufen."
                );
            }
            else
            {
                throw new NotSupportedException(
                    "Unbekanntes Archivformat: '" + extension +
                    "'. Unterstützt werden .tar, .tar.gz und .tgz."
                );
            }
        }

        /// <summary>
        /// Entpackt eine .tar.gz / .tgz Datei.
        /// Nutzt das System-tar (Windows 10+, Linux, macOS), da die .NET TarFile API
        /// bei bestimmten Archiv-Formaten (z.B. astral-sh install_only_stripped)
        /// die Dateinamen verstümmelt.
        /// </summary>
        private static void ExtractGZipTar(string gzipTarFilePath, string destinationDirectory)
        {
            // System tar is available on Windows 10+ (bsdtar), Linux, and macOS
            var psi = new ProcessStartInfo
            {
                FileName = "tar",
                ArgumentList = { "-xzf", gzipTarFilePath, "-C", destinationDirectory },
                UseShellExecute = false,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start tar process.");

            string stderr = process.StandardError.ReadToEnd();
            process.WaitForExit(5 * 60 * 1000); // 5 minutes max

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"tar extraction failed (exit code {process.ExitCode}): {stderr}");
            }
        }

        /// <summary>
        /// Kopiert den Inhalt eines Quellordners (inkl. aller Unterordner) in einen Zielordner.
        /// Bereits existierende Dateien im Ziel werden überschrieben.
        /// </summary>
        /// <param name="sourceDirectory">Absoluter Pfad zum Quellordner.</param>
        /// <param name="destinationDirectory">Absoluter Pfad zum Zielordner.</param>
        public static void CopyUnpacked(string sourceDirectory, string destinationDirectory)
        {
            if (string.IsNullOrEmpty(sourceDirectory))
            {
                throw new ArgumentException("sourceDirectory must not be null or empty.", nameof(sourceDirectory));
            }

            if (string.IsNullOrEmpty(destinationDirectory))
            {
                throw new ArgumentException("destinationDirectory must not be null or empty.", nameof(destinationDirectory));
            }

            if (!Directory.Exists(sourceDirectory))
            {
                throw new DirectoryNotFoundException("Source directory not found: " + sourceDirectory);
            }

            Directory.CreateDirectory(destinationDirectory);

            CopyDirectoryContentsRecursive(sourceDirectory, destinationDirectory);
        }

        /// <summary>
        /// Rekursive Hilfsmethode zum Kopieren des kompletten Verzeichnisinhalts.
        /// </summary>
        private static void CopyDirectoryContentsRecursive(string sourceDirectory, string destinationDirectory)
        {
            // Dateien kopieren
            string[] files = Directory.GetFiles(sourceDirectory);
            for (int i = 0; i < files.Length; i++)
            {
                string sourceFilePath = files[i];
                string fileName = Path.GetFileName(sourceFilePath);
                string destFilePath = Path.Combine(destinationDirectory, fileName);

                File.Copy(sourceFilePath, destFilePath, overwrite: true);
            }

            // Unterordner rekursiv kopieren
            string[] directories = Directory.GetDirectories(sourceDirectory);
            for (int i = 0; i < directories.Length; i++)
            {
                string sourceSubDir = directories[i];
                string subDirName = Path.GetFileName(sourceSubDir);
                string destSubDir = Path.Combine(destinationDirectory, subDirName);

                Directory.CreateDirectory(destSubDir);
                CopyDirectoryContentsRecursive(sourceSubDir, destSubDir);
            }
        }
    }
}
