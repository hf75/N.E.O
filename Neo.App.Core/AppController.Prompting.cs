using Neo.Agents;
using Neo.Agents.Core;
using Neo.IPC;
using Neo.Collab;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Newtonsoft.Json;
using Neo.AssemblyForge;
using Neo.AssemblyForge.Completion;
using Neo.App.Services;
using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Numerics;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Neo.App
{
    public partial class AppController
    {
        private Dictionary<string, string> BuildAdditionalSourceFiles(bool usePython)
        {
            var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (!string.IsNullOrWhiteSpace(AgentHelperCode))
            {
                var (agentClass, envVar) = GetAIQueryProviderInfo(Settings.AIQueryProvider);
                var templated = AgentHelperCode!
                    .Replace("AnthropicTextChatAgent", agentClass)
                    .Replace("ANTHROPIC_API_KEY", envVar)
                    .Replace("claude-sonnet-4-5", Settings.AIQueryModel);
                files["./agenthelpercode.cs"] = templated;
            }

            if (!string.IsNullOrWhiteSpace(ForeignAppHelperCode))
                files["./foreignapphelper.cs"] = ForeignAppHelperCode!;

            // Plugin agent templates (ImageGen, STT, etc.)
            foreach (var plugin in PluginAgents)
            {
                if (string.IsNullOrWhiteSpace(plugin.HelperTemplateCode)) continue;

                var templated = plugin.HelperTemplateCode;
                foreach (var (placeholder, settingsKey) in plugin.TemplatePlaceholders)
                {
                    var modelValue = Settings.PluginAgentModels.TryGetValue(settingsKey, out var m)
                        ? m : plugin.DefaultModel;
                    templated = templated.Replace(placeholder, modelValue);
                }

                files[$"./{plugin.SettingsKey.ToLowerInvariant()}helper.cs"] = templated;
            }

            if (usePython)
            {
                foreach (var c in PythonHelperCode)
                    files[c.Key.ToLowerInvariant()] = c.Value;
            }

            return files;
        }

        /// <summary>
        /// The apps on the app network right now, described for the prompt — every N.E.O. app on
        /// this machine except the one in our own window (the AI is writing that one).
        /// </summary>
        /// <param name="includeOwn">Our own one too: for a program the AI writes beside it, a part torn out of it.</param>
        private List<string> OtherRunningApps(bool includeOwn = false)
        {
            try
            {
                var ownPid = includeOwn ? null : ChildProcessService?.ChildProcessId;
                return Neo.App.Apps.Running(includeSelf: true)
                    .Where(a => ownPid == null || a.ProcessId != ownPid)
                    .Select(a => a.Describe())
                    .ToList();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AppNetwork] Could not list running apps: {ex.Message}");
                return new List<string>();
            }
        }

        // The start of the files the current prompt names, added to what the model reads and kept out of the history.
        private string _namedFilesForTheModel = string.Empty;

        // The same for the running programs it names.
        private string _foreignProgramsForTheModel = string.Empty;

        // How often Claude may search in one try at the code.
        private const int WebSearchesPerTry = 3;

        // What Claude looked up for the current prompt. Someone who decides about its code should know.
        private readonly List<string> _webSearches = new();
        private readonly List<WebSource> _webSources = new();
        private string? _webSearchProblem;

        /// <summary>What to tell about the searches behind a result; null when there were none.</summary>
        internal static string? WebSearchReport(IReadOnlyList<string> searches, IReadOnlyList<WebSource> sources, string? problem)
        {
            var lines = new List<string>();
            if (!string.IsNullOrEmpty(problem))
                lines.Add(problem + " Built without it.");

            if (searches.Count > 0)
            {
                lines.Add("Searched the web: " + string.Join(", ", searches.Distinct().Select(s => $"\"{ToSingleLine(s)}\"")));

                var pages = sources.DistinctBy(s => s.Url).ToList();
                const int shown = 5;
                lines.AddRange(pages.Take(shown).Select(s => $"{ToSingleLine(s.Title)}: {s.Url}"));
                if (pages.Count > shown)
                    lines.Add($"... and {pages.Count - shown} more pages.");
            }

            return lines.Count > 0 ? string.Join("\n", lines) : null;
        }

        // Code written after reading the web is shown first, whatever the setting says: a web page may
        // have tried to get something into it, and it would run at once with the user's rights.
        private bool ReviewBeforeRunning => !Settings.AcceptAutomatic || _webSearches.Count > 0;

        // After the answer: the chat scrolls to its end, and a long patch would push this out of sight.
        private void LogWebSearches()
        {
            if (WebSearchReport(_webSearches, _webSources, _webSearchProblem) is { } report)
                Logger.LogMessage(report, BubbleType.Info);
        }

        /// <summary>
        /// Quick on the first try. Another try means the code before it did not build, so it gets one
        /// level more care, up to high. A level above that, or none (the model's default), stays.
        /// </summary>
        internal static string EffortForAttempt(string configured, int attempt)
        {
            string[] levels = ["low", "medium", "high"];
            var start = Array.IndexOf(levels, configured);
            return start < 0 ? configured : levels[Math.Min(start + Math.Max(attempt, 1) - 1, levels.Length - 1)];
        }

        private void RecreatePromptToDllSession(bool preserveState)
        {
            // Also without an AI agent: the project (code editor, shared sessions) must exist; a
            // prompt then fails with "AI agent is not configured" (ExecuteGeneralPrompt says so first).
            var referenceDirs = new List<string>();
            if (!string.IsNullOrWhiteSpace(_coreRefPath)) referenceDirs.Add(_coreRefPath);
            if (!string.IsNullOrWhiteSpace(_desktopRefPath)) referenceDirs.Add(_desktopRefPath);

            var workspace = new AssemblyForgeWorkspace
            {
                NuGetPackageDirectory = NuGetPackageDirectory,
                OutputDllPath = DllPath,
                ReferenceAssemblyDirectories = referenceDirs,
                TargetFramework = OperatingSystem.IsWindows()
                    ? $"net{Environment.Version.Major}.0-windows"
                    : $"net{Environment.Version.Major}.0",
                AdditionalReferenceDllPaths = AdditionalDlls,
                AssemblyName = "DynamicUserControl",
            };

            var pipeline = new AssemblyForgePipelineOptions
            {
                MaxAttempts = Settings?.AiCodeGenerationAttempts ?? 5,
                Temperature = 0.1f,
                TopP = 0.9f,
            };

            var completionProvider = new DelegateCompletionProvider(async (req, ct) =>
            {
                if (AiAgent == null)
                    throw new InvalidOperationException("AI agent is not configured.");

                AiAgent.SetInput("SystemMessage", req.SystemMessage ?? string.Empty);
                AiAgent.SetInput("Prompt", (req.Prompt ?? string.Empty) + _namedFilesForTheModel + _foreignProgramsForTheModel + _fusionForTheModel + _fissionForTheModel);
                AiAgent.SetInput("History", req.History ?? string.Empty);
                AiAgent.SetInput("JsonSchema", req.JsonSchema ?? string.Empty);

                AiAgent.SetOption("Temperature", req.Temperature);
                AiAgent.SetOption("TopP", req.TopP);
                var claude = ReferenceEquals(AiAgent, Claude) ? AiAgent as AnthropicTextChatAgent : null;
                if (claude != null)
                {
                    claude.SetOption("Effort", EffortForAttempt(Settings.ClaudeEffort, req.Attempt));
                    claude.SetOption("WebSearchMaxUses", Settings.ClaudeWebSearch ? WebSearchesPerTry : 0);
                    claude.SetInput("WebSearchGuidance", AISystemMessages.WebSearchWhileBuilding());
                    claude.WebSearchProgress = query => _view.InvokeOnUIThreadAsync(() =>
                        _view.SetWaitIndicatorStatus(query == null ? "Generating..." : "Searching the web: " + Truncate(ToSingleLine(query), 60)));
                }

                try
                {
                    await AiAgent.ExecuteAsync(ct);
                }
                catch (WebSearchUnavailableException ex) when (claude != null)
                {
                    // The tool can be built without it.
                    _webSearchProblem = ex.Message;
                    claude.SetOption("WebSearchMaxUses", 0);
                    await AiAgent.ExecuteAsync(ct);
                }

                if (claude != null)
                {
                    _webSearches.AddRange(claude.GetOutput<List<string>>("WebSearches"));
                    _webSources.AddRange(claude.GetOutput<List<WebSource>>("WebSources"));
                }

                var result = AiAgent.GetOutput<string>("Result");
                if (string.IsNullOrWhiteSpace(result))
                    throw new InvalidOperationException("AI agent returned empty completion.");

                return result;
            });

            _promptToDllClient = new AssemblyForgeClient(completionProvider, workspace, pipeline);

            var additionalSourceFiles = BuildAdditionalSourceFiles(Settings!.UsePython);
            if (preserveState && VirtualProjectFiles != null)
            {
                foreach (var f in VirtualProjectFiles.GetAllFiles())
                {
                    if (string.Equals(f.Path, "./currentcode.cs", StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (!additionalSourceFiles.ContainsKey(f.Path))
                        additionalSourceFiles[f.Path] = f.Content;
                }
            }

            var sessionOptions = new AssemblyForgeSessionOptions
            {
                UiFramework = Settings.UseAvalonia ? AssemblyForgeUiFramework.Avalonia : AssemblyForgeUiFramework.Wpf,
                UseReactUi = Settings.UseReactUi,
                UsePython = Settings.UsePython,
                UserControlClassName = "DynamicUserControl",
                MainFilePath = "./currentcode.cs",
                InitialCode = preserveState ? (AppState.LastCode ?? string.Empty) : GetUserControlBaseCode(),
                InitialHistoryPrefix = preserveState ? string.Empty : "Code:\n\n",
                AdditionalSourceFiles = additionalSourceFiles,
                SystemMessageOverride = AISystemMessages.GetSystemMessage(Settings.UseAvalonia, Settings.UseReactUi, Settings.UsePython, PluginAgents, OtherRunningApps(), otherPrograms: ForeignAppHelperCode != null && OperatingSystem.IsWindows()),
            };

            _promptToDllSession = _promptToDllClient.CreateSession(sessionOptions);
            VirtualProjectFiles = _promptToDllSession.Project;

            if (preserveState)
            {
                _promptToDllSession.State.History = AppState.History ?? string.Empty;
                _promptToDllSession.State.CurrentCode = AppState.LastCode ?? string.Empty;
                _promptToDllSession.State.NuGetDlls = AppState.NuGetDlls?.ToList() ?? new List<string>();
                _promptToDllSession.State.PackageVersions = new Dictionary<string, string>(AppState.PackageVersions ?? new(), StringComparer.OrdinalIgnoreCase);
                _promptToDllSession.State.LastErrorMessage = LastErrorMsg ?? string.Empty;

                VirtualProjectFiles.UpdateFileContent("./currentcode.cs", _promptToDllSession.State.CurrentCode);
            }
            else
            {
                AppState.History = _promptToDllSession.State.History;
                AppState.LastCode = _promptToDllSession.State.CurrentCode;
                AppState.NuGetDlls = _promptToDllSession.State.NuGetDlls.ToList();
                AppState.PackageVersions = new Dictionary<string, string>(_promptToDllSession.State.PackageVersions, StringComparer.OrdinalIgnoreCase);
                LastErrorMsg = string.Empty;
            }
        }

        private void HandlePythonFilesInVirtualProjectFiles(bool usePython)
        {
            if (usePython == true && VirtualProjectFiles != null)
            {
                foreach (KeyValuePair<string, string> c in PythonHelperCode)
                    VirtualProjectFiles.AddFile(c.Key.ToLower(), c.Value);
            }
            else if (usePython == false && VirtualProjectFiles != null)
            {
                foreach (KeyValuePair<string, string> c in PythonHelperCode)
                    if (VirtualProjectFiles.FileExists(c.Key) == true)
                        VirtualProjectFiles.DeleteFile(c.Key);
            }
        }

        public async Task ExecuteAIQuery(string prompt, string systemMessage, string history, string jsonSchema, CancellationToken cancellationToken = default)
        {
            if (AiAgent == null)
            {
                Logger.LogMessage("No AI service configured. Please set at least one API key (ANTHROPIC_API_KEY, OPENAI_API_KEY, or GEMINI_API_KEY) as a user environment variable and restart.", BubbleType.CompletionError);
                return;
            }

            AiAgent.SetInput("SystemMessage", systemMessage);
            AiAgent.SetInput("Prompt", prompt);
            AiAgent.SetInput("History", history);

            AiAgent.SetOption("Temperature", 0.1f);
            AiAgent.SetOption("TopP", 0.9f);

            AiAgent.SetInput("JsonSchema", jsonSchema);

            await AiAgent.ExecuteAsync( cancellationToken );
        }

        public async Task<bool> ExecuteSystemPromptAsync(string prompt)
        {
            if (prompt.Trim().StartsWith("/loadnuget"))
            {
                string nugetName = prompt.Replace("/loadnuget", "").Trim();
                await ExecuteLoadNugetAsync(nugetName); // Diese Methode müssen wir auch noch verschieben
                return true;
            }
            return false;
        }

        public async Task ExecuteLoadNugetAsync(string nugetName)
        {
            try
            {
                Dictionary<string, string> packs = new() { [nugetName] = "default" };

                // ***WICHTIG***: vor der Mutation snapshotten
                // History: commit after mutation (see below)

                // Mutiert appState.NuGetDlls/PackageVersions
                await LoadAndApplyNugetPackages(packs);

                string packagesText = string.Join(", ", packs.Select(p => $"{p.Key} ({p.Value})"));
                _undoRedo.CommitChange(
                    title: $"NuGet: {nugetName}",
                    description: $"Loaded package(s): {packagesText}",
                    skipIfUnchanged: true);

                // Kein weiterer Commit nötig; wir wollten nur die Pakete laden.
                // (Wenn du magst, könntest du hier noch Bubbles schreiben:)

                foreach (string dll in AppState.NuGetDlls)
                    Logger.LogMessage(dll, BubbleType.Answer);
            }
            catch
            {
                Logger.LogMessage("Error!", BubbleType.Answer);
            }
        }

        // In AppController.cs

        /// <summary>Runs a prompt. False when it could not start (busy, or someone else in the shared session is changing the app) — keep the text then.</summary>
        public Task<bool> ExecutePromptAsync(string prompt)
        {
            if (CurrentStatus != AppStatus.Idle) return Task.FromResult(false);
            return RunSharedChangeAsync(StateKinds.Prompt, "working on a prompt: " + Truncate(ToSingleLine(prompt), 80), false, async () =>
            {
                await ExecutePromptCoreAsync(prompt);
                return true;
            }, title: prompt);
        }

        private async Task ExecutePromptCoreAsync(string prompt)
        {
            if (CurrentStatus != AppStatus.Idle) return;

            try
            {
                // Die Logik aus der alten Execute-Methode kommt hierher.
                if ((!string.IsNullOrEmpty(prompt)) || (!string.IsNullOrEmpty(LastErrorMsg)))
                {
                    // A running app the change can be patched into stays in view and in use: the user
                    // goes on working while the AI writes, and the patch finds the app as they left it.
                    if (!(ChildProcessService.CanStayInUseWhileGenerating && ChildProcessService.HasLoadedControl && _hotReload.HasRunningApp))
                    {
                        // Capture screenshot while child is still visible, then hide.
                        // Do NOT unload the control yet — only unload when we have new code to load.
                        var snapshot = ChildProcessService.CaptureChildScreenshot();
                        ChildProcessService.HideChild();
                        if (snapshot != null)
                            _view.ShowFrostedSnapshot(snapshot);
                    }

                    await SetStatusAsync(AppStatus.Generating, true, "Generating...");
                    _view.PromptToNextLine(); // UI-Aktion über die View

                    bool executed = await ExecuteSystemPrompt(prompt);

                    if (!executed)
                    {
                        // Wir übergeben das Token aus unserer neuen Quelle
                        await ExecuteGeneralPrompt(prompt, _cancellationSource.Token);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                Logger.LogMessage("Code generation was cancelled.", BubbleType.Info);
            }
            catch (Exception ex)
            {
                Logger.LogMessage($"An error occurred during execution: {ex.Message}", BubbleType.CompletionError);
            }
            finally
            {
                // Always restore child visibility and clean up frosted overlay
                ChildProcessService.ShowChild();
                _view.HideFrostedSnapshot();

                // Discard pipe disconnects caused by normal operation (not real crashes). An error the
                // app survived while the user went on working is real: going idle repairs it.
                if (_pendingCrash is not { err.Recoverable: true })
                    _pendingCrash = null;

                await SetStatusAsync(AppStatus.Idle, false);
            }
        }

        // Die ausgeschnittenen Methoden als private Helfer einfügen und anpassen:

        private async Task<bool> ExecuteSystemPrompt(string prompt)
        {
            if (prompt.Trim().StartsWith("/loadnuget"))
            {
                string nugetName = prompt.Replace("/loadnuget", "").Trim();
                await ExecuteLoadNugetAsync(nugetName); // Diese Methode müssen wir auch noch verschieben
                return true;
            }
            return false;
        }

        public async Task ExecuteGeneralPrompt(string prompt, CancellationToken cancellationToken, bool showResultInChild = true)
        {
            if (AiAgent == null)
            {
                Logger.LogMessage("No AI service configured. Please set at least one API key (ANTHROPIC_API_KEY, OPENAI_API_KEY, or GEMINI_API_KEY) as a user environment variable and restart.", BubbleType.CompletionError);
                return;
            }

            // Every prompt runs through the AssemblyForge prompt→DLL engine. The session is rebuilt
            // per prompt so settings changes (model, framework, Python, ...) take effect immediately.
            try
            {
                RecreatePromptToDllSession(preserveState: true);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ExecuteGeneralPrompt] RecreatePromptToDllSession failed: {ex}");
                if (_promptToDllSession == null)
                {
                    Logger.LogMessage($"The code generation engine could not be initialized: {ex.Message}", BubbleType.CompletionError);
                    return;
                }
                // Otherwise keep going with the session from the previous prompt.
            }

            await ExecuteGeneralPromptWithPromptToDllAsync(prompt, cancellationToken, showResultInChild);
        }

        private async Task ExecuteGeneralPromptWithPromptToDllAsync(string prompt, CancellationToken cancellationToken, bool showResultInChild)
        {
            string originalPrompt = prompt ?? string.Empty;

            _webSearches.Clear();
            _webSources.Clear();
            _webSearchProblem = null;

            // Off the UI thread: a named path can lie on a network drive that takes its time to answer.
            var namedFiles = await Task.Run(() => NamedFiles.Find(originalPrompt), cancellationToken);
            _namedFilesForTheModel = NamedFiles.ForTheModel(namedFiles);
            if (namedFiles.Count > 0)
                Logger.LogMessage("The AI sees the start of " + string.Join(", ", namedFiles.Select(f => f.Path)) + ", so that the tool fits the file.", BubbleType.Info);

            var foreignPrograms = await Task.Run(() => ForeignPrograms.Find(originalPrompt), cancellationToken);
            _foreignProgramsForTheModel = ForeignPrograms.ForTheModel(foreignPrograms);
            if (foreignPrograms.Count > 0)
                Logger.LogMessage(ForeignPrograms.ChatLine(foreignPrograms), BubbleType.Info);

            AssemblyForgeReviewCallback? reviewCallback = null;
            if (showResultInChild)
            {
                reviewCallback = async (ctx, ct) =>
                {
                    if (!ReviewBeforeRunning)
                        return new AssemblyForgeReviewDecision(AssemblyForgeReviewAction.Accept);

                    PatchReviewDecision decision = await _view.ShowPatchReviewDialogAsync(ctx.Patch, ctx.NuGetPackages, ctx.Explanation,
                        webSearchReport: WebSearchReport(_webSearches, _webSources, _webSearchProblem));

                    return decision switch
                    {
                        PatchReviewDecision.Apply => new AssemblyForgeReviewDecision(AssemblyForgeReviewAction.Accept),
                        PatchReviewDecision.Regenerate => new AssemblyForgeReviewDecision(
                            AssemblyForgeReviewAction.Regenerate,
                            "Please regenerate a safer revision. Return a valid unified diff patch targeting './currentcode.cs'."),
                        _ => new AssemblyForgeReviewDecision(AssemblyForgeReviewAction.Reject),
                    };
                };
            }

            var result = await _promptToDllSession.RunAsync(
                prompt: originalPrompt,
                cancellationToken: cancellationToken,
                reviewCallback: reviewCallback);

            if (result.Status == AssemblyForgeStatus.ChatOnly)
            {
                string chat = result.StructuredResponse?.Chat ?? string.Empty;
                Logger.LogMessage(originalPrompt, BubbleType.Prompt);
                Logger.LogMessageWithMarkdownFormating(chat, BubbleType.CompletionSuccess);
                LogWebSearches();

                // Keep undo/redo graph stable for chat-only turns.
                AppState.History = _promptToDllSession.State.History;
                LastErrorMsg = string.Empty;

                // Control is still loaded (we only hid the window), just show it again
                ChildProcessService?.ShowChild();
                ChildProcessService?.UpdatePosition();
                return;
            }

            // PowerShell / Console App → Single-Shot execution
            if (result.Status == AssemblyForgeStatus.PowerShellReady ||
                result.Status == AssemblyForgeStatus.ConsoleAppReady)
            {
                Logger.LogMessage(originalPrompt, BubbleType.Prompt);
                LogWebSearches();
                await ExecuteSingleShotToolAsync(originalPrompt, result.StructuredResponse!, cancellationToken);
                return;
            }

            if (result.Status == AssemblyForgeStatus.Rejected)
            {
                Logger.LogMessage("Changes rejected.", BubbleType.Info);
                LastErrorMsg = string.Empty;
                return;
            }

            if (result.Status != AssemblyForgeStatus.Success || result.StructuredResponse == null || string.IsNullOrWhiteSpace(result.OutputDllPath))
            {
                LastErrorMsg = result.ErrorMessage ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(LastErrorMsg))
                    Logger.LogMessage(LastErrorMsg, BubbleType.CompletionError);

                if (showResultInChild)
                    await CompileAndShowAsync(VirtualProjectFiles.GetSourceCodeAsStrings());

                int maxAttempts = Settings?.AiCodeGenerationAttempts ?? 5;
                Logger.LogMessage(
                    $"Code generation failed after {result.AttemptsUsed} of {maxAttempts} attempts. " +
                    "The AI was unable to produce valid code. You can try rephrasing your prompt or simplifying your request.",
                    BubbleType.CompletionError);
                LastErrorMsg = string.Empty;
                return;
            }

            var structuredResponse = result.StructuredResponse;

            Logger.LogMessage(originalPrompt, BubbleType.Prompt);
            Logger.LogMessageWithMarkdownFormating(
                AssemblyForgeHistoryFormatter.StructuredResponseToText(structuredResponse),
                BubbleType.Answer);
            LogWebSearches();

            string historyTitle = $"Prompt: {Truncate(ToSingleLine(originalPrompt), 60)}";
            string historyDescription = "Update";

            if (!string.IsNullOrWhiteSpace(structuredResponse.Explanation))
                historyDescription = Truncate(ToSingleLine(structuredResponse.Explanation), 240);
            else if (!string.IsNullOrWhiteSpace(structuredResponse.Patch))
                historyDescription = $"Patch ({structuredResponse.Patch.Split('\n').Length} lines)";
            else if (!string.IsNullOrWhiteSpace(structuredResponse.Code))
                historyDescription = $"Code ({structuredResponse.Code.Length} chars)";

            if (structuredResponse.NuGetPackages != null && structuredResponse.NuGetPackages.Count > 0)
                historyDescription = $"{historyDescription}\nNuGet: {string.Join(", ", structuredResponse.NuGetPackages)}";

            ApplyState(s =>
            {
                s.History = _promptToDllSession.State.History;
                s.LastCode = _promptToDllSession.State.CurrentCode;
                s.NuGetDlls = _promptToDllSession.State.NuGetDlls.ToList();
                s.PackageVersions = new Dictionary<string, string>(_promptToDllSession.State.PackageVersions, StringComparer.OrdinalIgnoreCase);
            }, title: historyTitle, description: historyDescription, skipIfUnchanged: true);

            if (!await DisplayCompiledDllAsync(result.OutputDllPath))
                throw new InvalidOperationException("Failed to load the control in the child process.");

            LastErrorMsg = string.Empty;
        }
    }
}
