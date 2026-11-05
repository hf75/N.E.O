using System.Diagnostics;
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

        // Pictures that go to Claude with the prompt (the part of another program, as it went along with the pointer);
        // the other providers get the text only.
        private List<byte[]> _picturesForTheModel = [];

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
            if (tear.Program != null) return await TearOutOfProgramAsync(tear, tear.Program);

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
            StartNewProgram(project, tear);
            Logger.LogMessage($"\"{newName}\" is a program of its own now, saved as {project}. It gets its data from {sourceName} while that runs; Ctrl+Shift+N in it changes it.", BubbleType.Info);
            await ChildProcessService.SetAppWindowAsync(new AppWindowMessage(Banner: $"\"{newName}\" is a program of its own now.", BannerColor: BannerGreen, BannerSeconds: 6));
            return null;
        }

        // Out of the other program this app is the interface of. That program keeps its part, nobody has its source; the new
        // program is a small window that does what the part does, through this interface.
        private async Task<string?> TearOutOfProgramAsync(FissionTearMessage tear, string program)
        {
            var before = GetCurrentCode();
            if (string.IsNullOrWhiteSpace(before)) return "there is no interface yet.";

            var interfaceName = FusionName(tear.SourceName, before);
            // The model goes by the process; the person reads the name the program's title bar shows ("Word", not "WINWORD").
            var shown = string.IsNullOrWhiteSpace(tear.ProgramName) ? program : tear.ProgramName;
            var newName = FissionGuidance.NameFor(tear.Name, null, $"Small {shown}");
            if (string.Equals(newName, interfaceName, StringComparison.OrdinalIgnoreCase) || string.Equals(newName, program, StringComparison.OrdinalIgnoreCase)
                || string.Equals(newName, shown, StringComparison.OrdinalIgnoreCase))
                newName += " (torn out)";
            var part = $"the {tear.Type}" + (tear.Name != null ? $" \"{tear.Name}\"" : "") + (tear.Address.Length > 0 ? $" at the address \"{tear.Address}\"" : "")
                + $", {Math.Round(tear.Width)} × {Math.Round(tear.Height)} px";
            var map = tear.Map ?? "";

            // The picture that went along with the pointer, as it is: how the part looks, which no list of controls says.
            // Claude reads pictures; the other providers get the list only, and are not told of a picture.
            byte[]? picture = null;
            try { if (tear.Picture is { Length: > 0 } base64) picture = Convert.FromBase64String(base64); }
            catch (FormatException) { /* no picture then */ }
            var withPicture = picture != null && ReferenceEquals(AiAgent, Claude);
            _picturesForTheModel = withPicture ? [picture!] : [];
            try
            {
                // 1. The interface offers what the part needs. It may have all of that already: no change is an answer too.
                Logger.LogMessage(withPicture
                    ? $"Tearing \"{newName}\" out of {shown}: the AI sees the part as it went along with the pointer, its controls, and the source of this interface."
                    : $"Tearing \"{newName}\" out of {shown}: the AI sees the part's controls (what they show only as its shape) and the source of this interface.", BubbleType.Info);
                _fissionForTheModel = FissionGuidance.ForTheInterface(interfaceName, program, newName, part, map, withPicture);
                bool ran;
                try
                {
                    ran = await ExecutePromptAsync(FissionGuidance.ChatPromptForProgram(newName, program));
                }
                finally
                {
                    _fissionForTheModel = string.Empty;
                }
                if (!ran) return "N.E.O. is busy with this interface right now.";

                // 2. The small program, in a session of its own, with this interface on its list of running apps.
                await ChildProcessService.SetAppWindowAsync(new AppWindowMessage(Banner: $"Writing \"{newName}\" as a program of its own…", BannerColor: BannerDark));
                var (code, packages, problem) = await WriteNewProgramAsync(FissionGuidance.ForTheProgramPart(interfaceName, program, newName, part, map, withPicture));
                if (code == null) return problem ?? "the AI could not write the new program.";

                // 3. As for a part of an app: a living app, where the part was let go.
                var project = SaveNewProgram(newName, code, packages);
                StartNewProgram(project, tear);
                Logger.LogMessage($"\"{newName}\" is a program of its own now, saved as {project}. It uses {shown} through {interfaceName} while both run; Ctrl+Shift+N in it changes it.", BubbleType.Info);
                await ChildProcessService.SetAppWindowAsync(new AppWindowMessage(Banner: $"\"{newName}\" is a program of its own now.", BannerColor: BannerGreen, BannerSeconds: 6));
                return null;
            }
            finally
            {
                _picturesForTheModel = [];
            }
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

        // The same N.E.O., playing the new project: its window is the program, its N.E.O. hides behind it. The window opens
        // where the part was let go, at least as big as the part was, and as big as what the program shows (placed from
        // outside once it showed, it went back to its first size as soon as the program in it laid itself out).
        private static void StartNewProgram(string project, FissionTearMessage tear)
        {
            var host = Environment.ProcessPath ?? throw new InvalidOperationException("N.E.O. does not know where it was started from.");
            var place = new PlayerPlace(tear.X - 20, tear.Y - 20, Math.Max(320, tear.Width + 40), Math.Max(200, tear.Height + 60));
            var start = new ProcessStartInfo(host, $"--play \"{project}\" --place {place.ToArgument()}") { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(host)! };
            using var started = Process.Start(start) ?? throw new InvalidOperationException("The new program did not start.");
        }
    }
}
