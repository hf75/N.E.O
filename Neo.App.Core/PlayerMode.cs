using System.IO;
using System.Runtime.InteropServices;
using Newtonsoft.Json;

namespace Neo.App
{
    /// <summary>
    /// What a "living" export puts next to the host executable: which project the host plays.
    /// The host finds this file in its own folder at start-up and runs in player mode.
    /// </summary>
    public sealed class PlayerManifest
    {
        public const string FileName = "neo.player.json";

        /// <summary>The project the app is started from (and saved back to); relative to the manifest.</summary>
        public string Project { get; set; } = "";

        /// <summary>The project as it was exported; "Reset to original" copies it over <see cref="Project"/>.</summary>
        public string? Original { get; set; }

        public string Title { get; set; } = "";

        /// <summary>Window icon (.ico), relative to the manifest; null: the default.</summary>
        public string? Icon { get; set; }

        /// <summary>"avalonia" or "wpf": the UI framework the project's code is written for.</summary>
        public string? Framework { get; set; }
    }

    /// <summary>
    /// A living app: the exported app is N.E.O. itself, running with its own window hidden behind
    /// the app window. The user works with the app; Ctrl+Shift+N calls N.E.O. up beside it, and
    /// every change is saved back to the project the app was started from.
    /// </summary>
    public sealed record PlayerProject(string ProjectFile, string? OriginalFile, string Title, string? IconFile, string? Framework = null)
    {
        /// <summary>The name the app carries in titles and folder names.</summary>
        public string Name => Title;

        /// <summary>True when the project's code is Avalonia; false for WPF; null when the manifest does not say (the host's setting applies).</summary>
        public bool? UsesAvalonia => Framework?.ToLowerInvariant() switch { "avalonia" => true, "wpf" => false, _ => null };

        /// <summary>
        /// Player mode is entered when <see cref="PlayerManifest.FileName"/> lies next to the
        /// executable (an exported living app) or the command line says <c>--play &lt;project.resx&gt;</c>
        /// (trying a project as its users will see it).
        /// </summary>
        public static PlayerProject? Detect(string[] args, string baseDirectory)
        {
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (!string.Equals(args[i], "--play", StringComparison.OrdinalIgnoreCase)) continue;
                var project = Path.GetFullPath(args[i + 1]);
                var name = Path.GetFileNameWithoutExtension(project);
                var original = Path.Combine(Path.GetDirectoryName(project)!, name + ".original.resx");
                return new PlayerProject(project, File.Exists(original) ? original : null, name, null, FrameworkOf(project));
            }

            var manifestPath = Path.Combine(baseDirectory, PlayerManifest.FileName);
            if (!File.Exists(manifestPath)) return null;
            try
            {
                var manifest = JsonConvert.DeserializeObject<PlayerManifest>(File.ReadAllText(manifestPath));
                if (manifest == null || string.IsNullOrWhiteSpace(manifest.Project)) return null;

                string Resolve(string relative) => Path.GetFullPath(Path.Combine(baseDirectory, relative));
                var projectFile = Resolve(manifest.Project);
                return new PlayerProject(
                    projectFile,
                    manifest.Original != null && File.Exists(Resolve(manifest.Original)) ? Resolve(manifest.Original) : null,
                    string.IsNullOrWhiteSpace(manifest.Title) ? Path.GetFileNameWithoutExtension(projectFile) : manifest.Title,
                    manifest.Icon != null && File.Exists(Resolve(manifest.Icon)) ? Resolve(manifest.Icon) : null,
                    manifest.Framework ?? FrameworkOf(projectFile));
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// "avalonia" or "wpf" from the project's code (its usings), for projects without a manifest
        /// entry. The hosts share one settings file, and a host must not run WPF code in an Avalonia
        /// child because the other host was used last.
        /// </summary>
        public static string? FrameworkOf(string projectFile)
        {
            try
            {
                var code = JsonConvert.DeserializeObject<ResxData>(File.ReadAllText(projectFile))?.Code ?? "";
                if (code.Contains("using Avalonia", StringComparison.Ordinal)) return "avalonia";
                if (code.Contains("using System.Windows", StringComparison.Ordinal)) return "wpf";
            }
            catch { /* unreadable: the host's setting applies */ }
            return null;
        }

        /// <summary>Puts the original project back; the next load shows the app as it was exported.</summary>
        public bool ResetToOriginal()
        {
            if (OriginalFile == null || !File.Exists(OriginalFile)) return false;
            File.Copy(OriginalFile, ProjectFile, overwrite: true);
            return true;
        }
    }

    /// <summary>
    /// Writes a living app: a copy of the running host (this executable and everything next to it,
    /// trimmed to the current platform and without debug files), renamed after the app, plus the
    /// project, its pristine copy and the <see cref="PlayerManifest"/>.
    /// </summary>
    public static class LivingAppExporter
    {
        /// <param name="targetDirectory">Where the app goes; the project file (<c>&lt;name&gt;.resx</c>) must already be there.</param>
        /// <returns>The path of the app's executable.</returns>
        /// <param name="framework">"avalonia" or "wpf": what the project's code is written for (a host that can run both needs to know).</param>
        public static string Export(string targetDirectory, string appName, string? iconPath, IPlatformServices platform, string framework)
            => Export(targetDirectory, appName, iconPath, platform, framework,
                AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar),
                Environment.ProcessPath ?? throw new InvalidOperationException("The host executable is unknown."));

        /// <param name="hostDirectory">The running host's folder.</param>
        /// <param name="hostExe">The running host's executable, inside <paramref name="hostDirectory"/>.</param>
        public static string Export(string targetDirectory, string appName, string? iconPath, IPlatformServices platform, string framework,
            string hostDirectory, string hostExe)
        {
            Directory.CreateDirectory(targetDirectory);

            CopyHost(hostDirectory, targetDirectory);

            // The apphost knows its .dll by name, so it may carry the app's name.
            var exeName = OperatingSystem.IsWindows() ? appName + ".exe" : appName;
            var exePath = Path.Combine(targetDirectory, exeName);
            File.Copy(hostExe, exePath, overwrite: true);
            var hostExeCopy = Path.Combine(targetDirectory, Path.GetFileName(hostExe));
            if (File.Exists(hostExeCopy) && !string.Equals(hostExeCopy, exePath, StringComparison.OrdinalIgnoreCase))
                File.Delete(hostExeCopy);
            if (!OperatingSystem.IsWindows())
            {
                try { File.SetUnixFileMode(exePath, File.GetUnixFileMode(exePath) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute); }
                catch { /* best effort */ }
            }

            var projectFile = Path.Combine(targetDirectory, appName + ".resx");
            var originalFile = Path.Combine(targetDirectory, appName + ".original.resx");
            File.Copy(projectFile, originalFile, overwrite: true);

            string? iconName = null;
            if (!string.IsNullOrEmpty(iconPath) && File.Exists(iconPath))
            {
                iconName = appName + ".ico";
                File.Copy(iconPath, Path.Combine(targetDirectory, iconName), overwrite: true);
                try { platform.InjectIcon(exePath, iconPath); } catch { /* the window icon still comes from the manifest */ }
            }

            var manifest = new PlayerManifest
            {
                Project = Path.GetFileName(projectFile),
                Original = Path.GetFileName(originalFile),
                Title = appName,
                Icon = iconName,
                Framework = framework,
            };
            File.WriteAllText(Path.Combine(targetDirectory, PlayerManifest.FileName), JsonConvert.SerializeObject(manifest, Formatting.Indented));
            return exePath;
        }

        // Everything the host needs on this platform: no debug symbols, no documentation, no
        // native libraries of other operating systems (they are most of the host's size).
        private static void CopyHost(string source, string target)
        {
            foreach (var file in Directory.GetFiles(source))
            {
                var name = Path.GetFileName(file);
                if (IsDebugOrDocumentation(name) || name.Equals(PlayerManifest.FileName, StringComparison.OrdinalIgnoreCase)) continue;
                File.Copy(file, Path.Combine(target, name), overwrite: true);
            }

            foreach (var directory in Directory.GetDirectories(source))
            {
                var name = Path.GetFileName(directory);
                if (name.Equals("runtimes", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var rid in Directory.GetDirectories(directory))
                    {
                        if (!IsCurrentPlatform(Path.GetFileName(rid))) continue;
                        CopyTree(rid, Path.Combine(target, "runtimes", Path.GetFileName(rid)));
                    }
                    continue;
                }
                CopyTree(directory, Path.Combine(target, name));
            }
        }

        private static bool IsCurrentPlatform(string rid)
        {
            var current = RuntimeInformation.RuntimeIdentifier; // win-x64, linux-x64, osx-arm64, …
            var os = current.Split('-')[0];
            if (rid.Equals(current, StringComparison.OrdinalIgnoreCase) || rid.Equals(os, StringComparison.OrdinalIgnoreCase)) return true;
            // "unix" covers linux and osx; "osx" without an architecture covers osx-arm64
            return os is "linux" or "osx" && rid.Equals("unix", StringComparison.OrdinalIgnoreCase);
        }

        private static void CopyTree(string source, string target)
        {
            Directory.CreateDirectory(target);
            foreach (var file in Directory.GetFiles(source))
            {
                if (file.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)) continue;
                File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
            }
            foreach (var directory in Directory.GetDirectories(source))
                CopyTree(directory, Path.Combine(target, Path.GetFileName(directory)));
        }

        private static bool IsDebugOrDocumentation(string fileName)
            => fileName.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase);
    }
}
