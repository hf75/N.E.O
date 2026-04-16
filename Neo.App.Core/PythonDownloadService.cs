using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Neo.App
{
    /// <summary>
    /// Manages downloading and extracting the Python 3.11 runtime from
    /// astral-sh/python-build-standalone GitHub releases.
    /// </summary>
    public static class PythonDownloadService
    {
        private const string ReleaseTag = "20251031";

        private const string WindowsArchive =
            "cpython-3.11.14+20251031-x86_64-pc-windows-msvc-install_only_stripped.tar.gz";

        private const string LinuxArchive =
            "cpython-3.11.14+20251031-x86_64-unknown-linux-gnu-install_only_stripped.tar.gz";

        private const string MacOsArm64Archive =
            "cpython-3.11.14+20251031-aarch64-apple-darwin-install_only_stripped.tar.gz";

        private static readonly string BaseUrl =
            $"https://github.com/astral-sh/python-build-standalone/releases/download/{ReleaseTag}/";

        /// <summary>
        /// Returns the platform subfolder name (e.g. "win-x64", "linux-x64", "osx-arm64").
        /// </summary>
        public static string GetPlatformSubfolder()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return "win-x64";
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                return "linux-x64";
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                return "osx-arm64";

            throw new PlatformNotSupportedException("Unsupported operating system for Python runtime.");
        }

        /// <summary>
        /// Returns the base directory where the Python runtime is stored per-user.
        /// e.g. %LOCALAPPDATA%\Neo\Python\win-x64 on Windows.
        /// </summary>
        public static string GetPythonInstallDirectory()
            => GetInstallDirectoryForPlatform(GetPlatformSubfolder());

        /// <summary>
        /// Checks whether our own Python 3.11 runtime is available
        /// (downloaded blob or app-local embedded). System Python is never used.
        /// </summary>
        public static bool IsPythonInstalled()
        {
            // 1. Downloaded blob
            string installDir = GetPythonInstallDirectory();
            string keyFile = GetKeyFilePath(installDir);
            if (File.Exists(keyFile))
                return true;

            // 2. App-local embedded (for published/exported apps)
            string appLocal = Path.Combine(AppContext.BaseDirectory, "python", GetPlatformSubfolder());
            string appLocalKeyFile = GetKeyFilePath(appLocal, nested: false);
            if (File.Exists(appLocalKeyFile))
                return true;

            return false;
        }

        /// <summary>
        /// Returns the full path of the Python install root for the current platform.
        /// </summary>
        public static string GetPythonRootPath()
            => GetPythonRootPathForPlatform(GetPlatformSubfolder());

        /// <summary>
        /// Returns the full path of the Python install root for a specific target platform.
        /// (the directory containing python.exe / bin/python3.11).
        /// </summary>
        public static string GetPythonRootPathForPlatform(string platformSubfolder)
        {
            string baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(baseDir, "Neo", "Python", platformSubfolder, "python");
        }

        /// <summary>
        /// Checks whether the Python runtime for a specific target platform is cached locally.
        /// </summary>
        public static bool IsPythonInstalledForPlatform(string platformSubfolder)
        {
            string rootPath = GetPythonRootPathForPlatform(platformSubfolder);
            // Check for platform-specific key file
            string keyFile = platformSubfolder.StartsWith("win")
                ? Path.Combine(rootPath, "python.exe")
                : Path.Combine(rootPath, "bin", "python3.11");
            return File.Exists(keyFile);
        }

        /// <summary>
        /// Returns the platform-specific key file used to verify the installation.
        /// When nested=true (default), expects the tar extraction layout: dir/python/{files}.
        /// When nested=false, expects files directly in dir (app-local embedded layout).
        /// </summary>
        private static string GetKeyFilePath(string dir, bool nested = true)
        {
            string root = nested ? Path.Combine(dir, "python") : dir;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return Path.Combine(root, "python.exe");
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                return Path.Combine(root, "bin", "python3.11");
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                return Path.Combine(root, "bin", "python3.11");

            throw new PlatformNotSupportedException();
        }

        /// <summary>
        /// Returns the GitHub release download URL for the current platform.
        /// </summary>
        public static string GetDownloadUrl()
            => GetDownloadUrlForPlatform(GetPlatformSubfolder());

        /// <summary>
        /// Returns the GitHub release download URL for a specific target platform.
        /// </summary>
        public static string GetDownloadUrlForPlatform(string platformSubfolder)
            => BaseUrl + GetArchiveFileNameForPlatform(platformSubfolder);

        /// <summary>
        /// Returns the archive filename for the current platform.
        /// </summary>
        public static string GetArchiveFileName()
            => GetArchiveFileNameForPlatform(GetPlatformSubfolder());

        /// <summary>
        /// Returns the archive filename for a specific target platform.
        /// </summary>
        public static string GetArchiveFileNameForPlatform(string platformSubfolder)
        {
            return platformSubfolder switch
            {
                "win-x64" => WindowsArchive,
                "linux-x64" => LinuxArchive,
                "osx-arm64" => MacOsArm64Archive,
                _ => throw new PlatformNotSupportedException($"No Python archive for platform: {platformSubfolder}"),
            };
        }

        /// <summary>
        /// Returns the install directory for a specific target platform.
        /// e.g. %LOCALAPPDATA%\Neo\Python\linux-x64
        /// </summary>
        public static string GetInstallDirectoryForPlatform(string platformSubfolder)
        {
            string baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(baseDir, "Neo", "Python", platformSubfolder);
        }

        /// <summary>
        /// Downloads and extracts the Python runtime for the current platform.
        /// Reports progress as (bytesDownloaded, totalBytes?).
        /// </summary>
        public static Task DownloadAndExtractAsync(
            IProgress<(long bytesDownloaded, long? totalBytes)>? progress = null,
            CancellationToken cancellationToken = default)
            => DownloadAndExtractForPlatformAsync(GetPlatformSubfolder(), progress, cancellationToken);

        /// <summary>
        /// Downloads and extracts the Python runtime for a specific target platform.
        /// Used for cross-platform export (e.g. downloading Linux Python on Windows).
        /// Reports progress as (bytesDownloaded, totalBytes?).
        /// </summary>
        public static async Task DownloadAndExtractForPlatformAsync(
            string platformSubfolder,
            IProgress<(long bytesDownloaded, long? totalBytes)>? progress = null,
            CancellationToken cancellationToken = default)
        {
            string url = GetDownloadUrlForPlatform(platformSubfolder);
            string installDir = GetInstallDirectoryForPlatform(platformSubfolder);

            Directory.CreateDirectory(installDir);

            // Download to temp file
            string tempFile = Path.Combine(installDir, GetArchiveFileNameForPlatform(platformSubfolder));

            try
            {
                using var http = new HttpClient();
                http.Timeout = TimeSpan.FromMinutes(10);

                using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();

                long? totalBytes = response.Content.Headers.ContentLength;
                long bytesDownloaded = 0;

                using (var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken))
                using (var fileStream = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
                {
                    byte[] buffer = new byte[81920];
                    int bytesRead;

                    while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
                    {
                        await fileStream.WriteAsync(buffer, 0, bytesRead, cancellationToken);
                        bytesDownloaded += bytesRead;
                        progress?.Report((bytesDownloaded, totalBytes));
                    }
                }

                cancellationToken.ThrowIfCancellationRequested();

                // Extract using existing helper
                progress?.Report((bytesDownloaded, totalBytes)); // Signal extraction starting
                PythonArchiveHelper.ExtractPythonTar(tempFile, installDir);

                Debug.WriteLine($"[PythonDownloadService] Python {platformSubfolder} extracted to: {installDir}");
            }
            finally
            {
                // Clean up archive
                if (File.Exists(tempFile))
                {
                    try { File.Delete(tempFile); }
                    catch { /* best-effort cleanup */ }
                }
            }
        }
    }
}
