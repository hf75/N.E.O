using System.Diagnostics;
using System.Runtime.InteropServices;
using Neo.AssemblyForge;
using Neo.IPC;
using Newtonsoft.Json;

namespace Neo.App
{
    /// <summary>
    /// Fission, the other way round from Fusion: the user drags a part out of the running app with Alt held. The app gives the
    /// part away (it leaves its UI, and what the part showed is offered on the app network), and the part becomes a program of
    /// its own: written by the model in a session of its own, started as a living app where it was let go, and getting its
    /// data from the app it came from.
    /// </summary>
    public partial class AppController
    {
        // What the part is and how it looked, added to what the model reads and kept out of the history.
        private string _fissionForTheModel = string.Empty;

        private static readonly TimeSpan NewProgramPatience = TimeSpan.FromSeconds(60);

        private Task<string?> TornOutAsync(string? payload)
        {
            if (payload == null || Json.FromJson<FissionTearMessage>(payload) is not { } tear)
                return Task.FromResult<string?>(null);
            if (CurrentStatus != AppStatus.Idle)
            {
                _ = ChildProcessService.SetAppWindowAsync(new AppWindowMessage(Banner: "Not torn out: N.E.O. is busy with this app right now.", BannerColor: BannerRed, BannerSeconds: 6));
                return Task.FromResult<string?>("busy");
            }

            // The window waits for an answer, the work takes longer.
            _view.InvokeOnUIThreadAsync(() => _ = TearOutAsync(tear));
            return Task.FromResult<string?>("ok");
        }

        private async Task TearOutAsync(FissionTearMessage tear)
        {
            string? problem;
            try
            {
                problem = await TearOutCoreAsync(tear);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[Fission] " + ex);
                problem = ex.Message;
            }
            if (problem != null)
            {
                Logger.LogMessage("Not torn out: " + problem, BubbleType.Info);
                await ChildProcessService.SetAppWindowAsync(new AppWindowMessage(Banner: "Not torn out: " + problem, BannerColor: BannerRed, BannerSeconds: 8));
            }
        }

        /// <summary>Null when the part is a program of its own now; else why not.</summary>
        private async Task<string?> TearOutCoreAsync(FissionTearMessage tear)
        {
            var before = GetCurrentCode();
            if (string.IsNullOrWhiteSpace(before)) return "there is no app yet.";

            var sourceName = FusionName(tear.SourceName, before);
            var newName = FissionGuidance.NameFor(tear.Name, tear.FirstText is { } text && before.Contains(text, StringComparison.Ordinal) ? text : null, tear.Type);
            if (string.Equals(newName, sourceName, StringComparison.OrdinalIgnoreCase)) newName += " (torn out)";
            var part = $"the {tear.Type}" + (tear.Name != null ? $" named \"{tear.Name}\"" : "") + $" at the address \"{tear.Address}\", {Math.Round(tear.Width)} × {Math.Round(tear.Height)} px";
            var map = await ChildProcessService.DescribeForLinkAsync(before, tear.Address) ?? "";

            // 1. This app gives the part away, and offers what the part needs on the app network.
            Logger.LogMessage($"Tearing \"{newName}\" out of {sourceName}: the AI sees the part's controls (what is written in them only as its shape) and the source of this app.", BubbleType.Info);
            _fissionForTheModel = FissionGuidance.ForTheSource(sourceName, newName, part, map);
            bool ran;
            try
            {
                ran = await ExecutePromptAsync(FissionGuidance.ChatPrompt(newName));
            }
            finally
            {
                _fissionForTheModel = string.Empty;
            }
            if (!ran) return "N.E.O. is busy with this app right now.";
            if (GetCurrentCode() == before) return "the AI could not take the part out of this app.";

            // 2. The new program, in a session of its own: this app's history is not its history.
            await ChildProcessService.SetAppWindowAsync(new AppWindowMessage(Banner: $"Writing \"{newName}\" as a program of its own…", BannerColor: BannerDark));
            var (code, packages, problem) = await WriteNewProgramAsync(FissionGuidance.ForTheNewProgram(sourceName, newName, part, map, before));
            if (code == null) return problem ?? "the AI could not write the new program.";

            // 3. A program of its own: a living app, where the part was let go.
            var project = SaveNewProgram(newName, code, packages);
            var started = StartNewProgram(project, tear);
            Logger.LogMessage($"\"{newName}\" is a program of its own now, saved as {project}. It gets its data from {sourceName} while that runs; Ctrl+Shift+N in it changes it.", BubbleType.Info);
            await ChildProcessService.SetAppWindowAsync(new AppWindowMessage(Banner: $"\"{newName}\" is a program of its own now.", BannerColor: BannerGreen, BannerSeconds: 6));
            _ = PlaceNewProgramAsync(started, tear);
            return null;
        }

        private async Task<(string? Code, Dictionary<string, string> Packages, string? Problem)> WriteNewProgramAsync(string prompt)
        {
            // What the prompt before it put next to the prompt is not for this one.
            _namedFilesForTheModel = string.Empty;
            _foreignProgramsForTheModel = string.Empty;

            var options = new AssemblyForgeSessionOptions
            {
                UiFramework = AssemblyForgeUiFramework.Avalonia,
                UserControlClassName = "DynamicUserControl",
                MainFilePath = "./currentcode.cs",
                InitialCode = GetUserControlBaseCode(),
                InitialHistoryPrefix = "Code:\n\n",
                AdditionalSourceFiles = BuildAdditionalSourceFiles(usePython: false),
                // The app it came from is on the list: that is where its data is.
                SystemMessageOverride = AISystemMessages.GetSystemMessage(useAvalonia: true, useReact: false, usePython: false, PluginAgents, OtherRunningApps(includeOwn: true), otherPrograms: ForeignAppHelperCode != null && OperatingSystem.IsWindows()),
            };

            await SetStatusAsync(AppStatus.Generating, true, "Writing the program of its own...");
            try
            {
                var session = _promptToDllClient.CreateSession(options);
                // The packages of the app it came from: a chart torn out of it needs the chart's library.
                session.State.NuGetDlls = AppState.NuGetDlls?.ToList() ?? new List<string>();
                session.State.PackageVersions = new Dictionary<string, string>(AppState.PackageVersions ?? new(), StringComparer.OrdinalIgnoreCase);
                var result = await session.RunAsync(prompt: prompt, cancellationToken: _cancellationSource.Token, reviewCallback: null);
                if (result.Status != AssemblyForgeStatus.Success || string.IsNullOrWhiteSpace(session.State.CurrentCode))
                    return (null, new(), string.IsNullOrWhiteSpace(result.ErrorMessage) ? null : "the new program did not compile: " + Truncate(ToSingleLine(result.ErrorMessage), 200));
                return (session.State.CurrentCode, new Dictionary<string, string>(session.State.PackageVersions, StringComparer.OrdinalIgnoreCase), null);
            }
            finally
            {
                await SetStatusAsync(AppStatus.Idle, false);
            }
        }

        /// <summary>Where new programs are saved when the app has no project of its own; overrides N.E.O.'s folder (tests).</summary>
        public const string FissionFolderVariable = "NEO_FISSION_DIR";

        // Beside this app's own project when it has one (a living app), else in N.E.O.'s folder.
        private string SaveNewProgram(string name, string code, Dictionary<string, string> packages)
        {
            var folder = AutoSaveProjectPath != null
                ? Path.GetDirectoryName(AutoSaveProjectPath)!
                : Environment.GetEnvironmentVariable(FissionFolderVariable) is { Length: > 0 } overridden ? overridden
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Neo", "Fission");
            Directory.CreateDirectory(folder);

            var fileName = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '-' : c)).Trim();
            var path = Path.Combine(folder, fileName + ".resx");
            for (int n = 2; File.Exists(path); n++) path = Path.Combine(folder, $"{fileName} {n}.resx");

            File.WriteAllText(path, JsonConvert.SerializeObject(new ResxData { Version = 2, Code = code, Nuget = packages, History = "" }));
            return path;
        }

        // The same N.E.O., playing the new project: its window is the program, its N.E.O. hides behind it.
        private static Process StartNewProgram(string project, FissionTearMessage tear)
        {
            var host = Environment.ProcessPath ?? throw new InvalidOperationException("N.E.O. does not know where it was started from.");
            var start = new ProcessStartInfo(host, $"--play \"{project}\"") { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(host)! };
            return Process.Start(start) ?? throw new InvalidOperationException("The new program did not start.");
        }

        // Where the part was let go, as big as the part was. The new program's window is that of the app in it.
        private static async Task PlaceNewProgramAsync(Process host, FissionTearMessage tear)
        {
            var until = DateTime.UtcNow + NewProgramPatience;
            while (DateTime.UtcNow < until && !host.HasExited)
            {
                await Task.Delay(300);
                var window = AppWindowOf(host.Id);
                if (window == IntPtr.Zero) continue;
                var scale = GetDpiForWindow(window) is var dpi and > 0 ? dpi / 96.0 : 1.0;
                const double Frame = 60; // room for the title bar and a margin
                SetWindowPos(window, IntPtr.Zero, tear.X - 20, tear.Y - 20,
                    (int)Math.Round(Math.Max(320, tear.Width + 40) * scale), (int)Math.Round(Math.Max(200, tear.Height + Frame) * scale), SWP_NOZORDER);
                return;
            }
        }

        private static IntPtr AppWindowOf(int hostProcessId)
        {
            foreach (var child in Process.GetProcessesByName("Neo.PluginWindowAvalonia"))
            {
                using (child)
                {
                    if (ParentOf(child.Id) != hostProcessId) continue;
                    child.Refresh();
                    if (child.MainWindowHandle != IntPtr.Zero && IsWindowVisible(child.MainWindowHandle)) return child.MainWindowHandle;
                }
            }
            return IntPtr.Zero;
        }

        private static int ParentOf(int processId)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                var info = new ProcessBasicInformation();
                return NtQueryInformationProcess(process.Handle, 0, ref info, Marshal.SizeOf(info), out _) == 0 ? info.InheritedFromUniqueProcessId.ToInt32() : -1;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return -1; }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessBasicInformation
        {
            public IntPtr Reserved1, PebBaseAddress, Reserved2a, Reserved2b, UniqueProcessId, InheritedFromUniqueProcessId;
        }

        private const uint SWP_NOZORDER = 0x4;

        [DllImport("ntdll.dll")] private static extern int NtQueryInformationProcess(IntPtr process, int infoClass, ref ProcessBasicInformation info, int size, out int returned);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
    }
}
