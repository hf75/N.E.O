#define HIDE_INTERNAL_CODE
using Neo.Agents;
using Neo.Agents.Core;
using Neo.IPC;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Newtonsoft.Json;
using Neo.AssemblyForge;
using Neo.AssemblyForge.Completion;
using Neo.App.Services;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net.NetworkInformation;
using System.Numerics;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Media.Media3D;

namespace Neo.App
{
    public enum AppStatus
    {
        Idle,           // Die Anwendung ist bereit für eine Eingabe
        Initializing,   // Die Anwendung startet oder wird zurückgesetzt
        Generating,     // Die KI generiert Code
        Compiling,      // Der Code wird kompiliert und geladen
        Exporting,      // Das Projekt wird exportiert
        Importing,      // Ein Projekt wird importiert
        Repairing,      // Ein automatischer Reparaturvorgang läuft
        Busy            // Ein allgemeiner, blockierender Zustand (z.B. Sandbox-Wechsel)
    }

    public class AppController
    {
        private readonly MainWindow _view;
        private DesignerPropertiesWindow? _designerPropertiesWindow;

        public ApplicationState AppState { get; private set; } = new();
        public UndoRedoManager _undoRedo = null!;

        public AppStatus CurrentStatus { get; private set; } = AppStatus.Idle;
        private (CrashReason reason, ErrorMessage err)? _pendingCrash;

        private CancellationTokenSource _cancellationSource = new();

        public IAppLogger Logger { get; }

        public IAgent? AiAgent { get; private set;  }
        public IAgent? ChatGPT { get; private set; }
        public IAgent? Claude { get; private set; }
        public IAgent? Gemini { get; private set; }
        public IAgent? Ollama { get; private set; }
        public IAgent? LmStudio { get; private set; }
        public IAgent PowerShellAgent { get; private set; }
        public List<IAgent> AvailableAgents { get; } = new();

        VirtualProject VirtualProjectFiles { get; set; } = null!;

        private AssemblyForgeClient _promptToDllClient = null!;
        private AssemblyForgeSession _promptToDllSession = null!;
        private string? _coreRefPath;
        private string? _desktopRefPath;

        public IAppImportService AppImportService { get; }
        public IAppExportService AppExportService { get; }
        public IAppInstallerService AppInstallerService { get; }
        public ICompilationService CompilationService { get; }
        public INuGetPackageService NugetPackageService { get; }
        public IChildProcessService ChildProcessService { get; }
        public string NuGetPackageDirectory = null!;
        public List<string> GrantedFolders { get; set; } = new();
        private List<string> DefaultNugets { get; set; } = new();

        public string LastErrorMsg { get; set; } = "";
        public List<string> AdditionalDlls { get; set; } = new List<string>();
        public string DllPath { get; set; } = null!;

        // AgentHelper.cs musste extra in der csproj Datei hinzugefügt werden damit es eine EmbeddedResource ist!
        public string? AgentHelperCode { get; set; } = null;
        public string? ImageGenHelperCode { get; set; } = null;
        public Dictionary<string, string> PythonHelperCode { get; set; } = new();

        List<string> AdditionalFilesForExportCopy { get; set; } = new() { "appsettings.json" };
        public string? ExportIcoFullPath { get; set; } = null!;

        SettingsModel _settings = new();
        public SettingsModel Settings
        {
            get => _settings;
            set
            {
                if (value == null)
                    throw new ArgumentNullException(nameof(value));

                _settings = value;

                // Update AIQuery agent DLL and NuGet package based on provider setting.
                var requiredDll = GetAIQueryAgentDll(value.AIQueryProvider);
                if (!AdditionalDlls.Contains(requiredDll))
                    AdditionalDlls.Add(requiredDll);

                var requiredNuget = GetAIQueryNuGetPackage(value.AIQueryProvider);
                DefaultNugets.Clear();
                DefaultNugets.Add(requiredNuget);
                if (value.UsePython)
                    DefaultNugets.Add("pythonnet|default");

                // Recreate prompt-to-DLL pipeline with updated settings (python/avalonia/react/system prompt).
                if (_promptToDllSession != null)
                {
                    try { RecreatePromptToDllSession(preserveState: true); }
                    catch { /* Best-effort; UI can still function without rebuilding immediately. */ }
                }
            }
        }


        public AppController(MainWindow view)
        {
            _view = view;

            Logger = new AppLogger(AppState, view.historyView);

            Settings = SettingsService.Load();

            AgentHelperCode = EmbeddedResourceReader.GetEmbeddedResourceContent("AgentHelper.cs");

            var geminiKeyForImageGen = Environment.GetEnvironmentVariable("GEMINI_API_KEY", EnvironmentVariableTarget.User);
            if (!string.IsNullOrWhiteSpace(geminiKeyForImageGen))
                ImageGenHelperCode = EmbeddedResourceReader.GetEmbeddedResourceContent("ImageGenHelper.cs");

            PythonHelperCode["PythonHost.cs"] = EmbeddedResourceReader.GetEmbeddedResourceContent("PythonHost.cs");
            PythonHelperCode["PythonModuleLoader.cs"] = EmbeddedResourceReader.GetEmbeddedResourceContent("PythonModuleLoader.cs");

            AdditionalDlls.Add("./Neo.Agents.Core.dll");
            if (!string.IsNullOrWhiteSpace(geminiKeyForImageGen))
                AdditionalDlls.Add("./Neo.Agents.GeminiImageGen.dll");
            AdditionalDlls.Add(GetAIQueryAgentDll(Settings.AIQueryProvider));
            DefaultNugets.Add(GetAIQueryNuGetPackage(Settings.AIQueryProvider));

            if (Settings.UsePython)
                DefaultNugets.Add("pythonnet|default");

            var systemMessage = AISystemMessages.GetSystemMessage(Settings.UseAvalonia, Settings.UseReactUi, Settings.UsePython, ImageGenHelperCode != null);

            var openAiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY", EnvironmentVariableTarget.User);
            if (!string.IsNullOrWhiteSpace(openAiKey))
            {
                ChatGPT = new OpenAiTextChatAgent();
                ChatGPT.SetOption("ApiKey", openAiKey);
                ChatGPT.SetOption("Model", Settings.OpenAiModel);
                ChatGPT.SetOption("Temperature", 0.1f);
                ChatGPT.SetOption("TopP", 0.9f);
                ChatGPT.SetInput("SystemMessage", systemMessage);
                AvailableAgents.Add(ChatGPT);
            }

            var anthropicKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY", EnvironmentVariableTarget.User);
            if (!string.IsNullOrWhiteSpace(anthropicKey))
            {
                Claude = new AnthropicTextChatAgent();
                Claude.SetOption("ApiKey", anthropicKey);
                Claude.SetOption("Model", Settings.ClaudeModel);
                Claude.SetOption("Temperature", 0.1f);
                Claude.SetOption("TopP", 0.90f);
                Claude.SetInput("SystemMessage", systemMessage);
                AvailableAgents.Add(Claude);
            }

            var geminiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY", EnvironmentVariableTarget.User);
            if (!string.IsNullOrWhiteSpace(geminiKey))
            {
                Gemini = new GeminiTextChatAgent();
                Gemini.SetOption("ApiKey", geminiKey);
                Gemini.SetOption("Model", Settings.GeminiModel);
                Gemini.SetOption("Temperature", 0.1f);
                Gemini.SetOption("TopP", 0.90f);
                Gemini.SetInput("SystemMessage", systemMessage);
                AvailableAgents.Add(Gemini);
            }

            // Ollama (local server, no API key required)
            if (IsLocalEndpointReachable(Settings.OllamaEndpoint))
            {
                Ollama = new OllamaTextChatAgent();
                Ollama.SetOption("Endpoint", Settings.OllamaEndpoint);
                Ollama.SetOption("ApiKey", "ollama");
                Ollama.SetOption("Model", Settings.OllamaModel);
                Ollama.SetOption("Temperature", 0.1f);
                Ollama.SetOption("TopP", 0.9f);
                Ollama.SetInput("SystemMessage", systemMessage);
                AvailableAgents.Add(Ollama);
            }

            // LM Studio (local server, no API key required)
            if (IsLocalEndpointReachable(Settings.LmStudioEndpoint))
            {
                LmStudio = new LmStudioTextChatAgent();
                LmStudio.SetOption("Endpoint", Settings.LmStudioEndpoint);
                LmStudio.SetOption("ApiKey", "lm-studio");
                LmStudio.SetOption("Model", Settings.LmStudioModel);
                LmStudio.SetOption("Temperature", 0.1f);
                LmStudio.SetOption("TopP", 0.9f);
                LmStudio.SetInput("SystemMessage", systemMessage);
                AvailableAgents.Add(LmStudio);
            }

            // Prefer Claude, then Gemini, then ChatGPT, then local models as default
            AiAgent = Claude ?? Gemini ?? ChatGPT ?? Ollama ?? LmStudio;

            PowerShellAgent = new PowerShellCodeExecutionAgent();

            if (AvailableAgents.Count == 0)
            {
                Debug.WriteLine("[AppController] No AI agents available. Set ANTHROPIC_API_KEY, OPENAI_API_KEY, or GEMINI_API_KEY as user environment variables, or start a local server (Ollama/LM Studio).");
                Logger.LogMessage("No AI service configured. Please set at least one API key as a user environment variable:\n• ANTHROPIC_API_KEY\n• OPENAI_API_KEY\n• GEMINI_API_KEY\nOr start a local server:\n• Ollama (http://localhost:11434)\n• LM Studio (http://localhost:1234)", BubbleType.CompletionError);
            }
            else
            {
                var names = string.Join(", ", AvailableAgents.Select(a => a.Name));
                Debug.WriteLine($"[AppController] Available AI agents: {names}");
            }

            var dotnetMajor = Environment.Version.Major;
            _coreRefPath = DotNetRuntimeFinder.GetHighestRuntimePath(DotNetRuntimeType.NetCoreApp, dotnetMajor);
            _desktopRefPath = DotNetRuntimeFinder.GetHighestRuntimePath(DotNetRuntimeType.WindowsDesktopApp, dotnetMajor);

            NuGetPackageDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                MainWindow.AppName,
                "NuGetPackages"
            );

            //PythonModulesDirectory = Path.Combine(
            //    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            //    MainWindow.AppName,
            //    "PythonModules"
            //);

            DllPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                MainWindow.AppName,
                "DynamicUserControl.dll"
            );

            AppImportService = new AppImportService();
            AppExportService = new AppExportService(_coreRefPath, _desktopRefPath);
            AppInstallerService = new AppInstallerService(AppExportService);
            CompilationService = new CompilationService(_coreRefPath, _desktopRefPath);
            NugetPackageService = new NuGetPackageService(NuGetPackageDirectory, _coreRefPath, _desktopRefPath);
            ChildProcessService = new ChildProcessService(_view, _view.dynamicContentGrid);
            ChildProcessService.ChildProcessCrashed += HandleChildProcessCrashed;
            ChildProcessService.ChildLogReceived += (log) => Debug.WriteLine($"[Child Log] {log.Message}");
            ChildProcessService.DesignerSelectionReceived += HandleDesignerSelectionReceived;

            RecreatePromptToDllSession(preserveState: false);

            _undoRedo = new UndoRedoManager(AppState);
        }

        private static (string agentClass, string envVar) GetAIQueryProviderInfo(string provider) => provider switch
        {
            "OpenAI" => ("OpenAiTextChatAgent", "OPENAI_API_KEY"),
            "Gemini" => ("GeminiTextChatAgent", "GEMINI_API_KEY"),
            "Ollama" => ("OllamaTextChatAgent", ""),
            "LM Studio" => ("LmStudioTextChatAgent", ""),
            _ => ("AnthropicTextChatAgent", "ANTHROPIC_API_KEY"),
        };

        private static string GetAIQueryNuGetPackage(string provider) => provider switch
        {
            "OpenAI" => "OpenAI|2.3.0",
            "Gemini" => "Google_GenerativeAI|3.6.1",
            "Ollama" => "OpenAI|2.3.0",
            "LM Studio" => "OpenAI|2.3.0",
            _ => "Anthropic.SDK|5.8.0",
        };

        private static string GetAIQueryAgentDll(string provider) => provider switch
        {
            "OpenAI" => "./Neo.Agents.OpenAI.dll",
            "Gemini" => "./Neo.Agents.Gemini.dll",
            "Ollama" => "./Neo.Agents.Ollama.dll",
            "LM Studio" => "./Neo.Agents.LmStudio.dll",
            _ => "./Neo.Agents.Claude.dll",
        };

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

            if (!string.IsNullOrWhiteSpace(ImageGenHelperCode))
            {
                var templated = ImageGenHelperCode!
                    .Replace("IMAGEGEN_MODEL_PLACEHOLDER", Settings.ImageGenModel);
                files["./imagegenhelpercode.cs"] = templated;
            }

            if (usePython)
            {
                foreach (var c in PythonHelperCode)
                    files[c.Key.ToLowerInvariant()] = c.Value;
            }

            return files;
        }

        private void RecreatePromptToDllSession(bool preserveState)
        {
            if (AiAgent == null)
                return;

            var referenceDirs = new List<string>();
            if (!string.IsNullOrWhiteSpace(_coreRefPath)) referenceDirs.Add(_coreRefPath);
            if (!string.IsNullOrWhiteSpace(_desktopRefPath)) referenceDirs.Add(_desktopRefPath);

            var workspace = new AssemblyForgeWorkspace
            {
                NuGetPackageDirectory = NuGetPackageDirectory,
                OutputDllPath = DllPath,
                ReferenceAssemblyDirectories = referenceDirs,
                TargetFramework = $"net{Environment.Version.Major}.0-windows",
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
                AiAgent.SetInput("Prompt", req.Prompt ?? string.Empty);
                AiAgent.SetInput("History", req.History ?? string.Empty);
                AiAgent.SetInput("JsonSchema", req.JsonSchema ?? string.Empty);

                AiAgent.SetOption("Temperature", req.Temperature);
                AiAgent.SetOption("TopP", req.TopP);

                await AiAgent.ExecuteAsync(ct);

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
                SystemMessageOverride = AISystemMessages.GetSystemMessage(Settings.UseAvalonia, Settings.UseReactUi, Settings.UsePython, ImageGenHelperCode != null),
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

        public async Task HardResetWithLastStateInit()
        {
            await ChildProcessService.RestartAsync();
            await CompileAndShowAsync( VirtualProjectFiles.GetSourceCodeAsStrings() );
        }

        public void RequestCancellation()
        {
            _cancellationSource.Cancel();
        }

        public async Task<bool> Undo()
        {
            if (CurrentStatus != AppStatus.Idle) return false;
            try
            {
                await SetStatusAsync(AppStatus.Busy, false, "Undoing...");
                return await _undoRedo.Undo(RecompileStateAsync); // Direkter Aufruf der lokalen Methode
            }
            finally
            {
                await SetStatusAsync(AppStatus.Idle, false);
            }
        }

        public async Task<bool> Redo()
        {
            if (CurrentStatus != AppStatus.Idle) return false;
            try
            {
                await SetStatusAsync(AppStatus.Busy, false, "Redoing...");
                return await _undoRedo.Redo(RecompileStateAsync); // Direkter Aufruf
            }
            finally
            {
                await SetStatusAsync(AppStatus.Idle, false);
            }
        }

        public async Task<bool> CheckoutHistoryAsync(HistoryNode node)
        {
            if (CurrentStatus != AppStatus.Idle) return false;

            try
            {
                await SetStatusAsync(AppStatus.Busy, false, "Checking out...");
                return await _undoRedo.CheckoutAsync(node, RecompileStateAsync);
            }
            finally
            {
                await SetStatusAsync(AppStatus.Idle, false);
            }
        }

        private async Task SetStatusAsync(AppStatus status, bool showCancel = false, string message = "")
        {
            CurrentStatus = status;

            if (status != AppStatus.Idle)
            {
                // Status ist BUSY
                _cancellationSource = new CancellationTokenSource();

                // Hier entscheiden wir: Wollen wir bei diesem Status ein Overlay sehen?
                // Standardmäßig ja. Wenn du Status hast, die "still" sperren sollen, 
                // kannst du hier den letzten Parameter auf false setzen.
                await _view.SetUiBusyState(isBusy: true, message: message, showCancel: showCancel, showOverlay: true);
            }
            else
            {
                // Status ist IDLE
                _cancellationSource = null!; // Aufräumen

                // Einfach nur "false" übergeben. Der Rest passiert automatisch.
                await _view.SetUiBusyState(isBusy: false);

                // Crash verarbeiten, der eintraf, während die App beschäftigt war.
                if (_pendingCrash is { } pending)
                {
                    _pendingCrash = null;
                    await HandleChildProcessCrashed(pending.reason, pending.err);
                }
            }
        }

        //private async Task SetStatusAsync(AppStatus status, bool showCancel, string message = "")
        //{
        //    CurrentStatus = status;
        //    if (status != AppStatus.Idle)
        //    {
        //        _cancellationSource = new CancellationTokenSource();
        //        // Ruft die erweiterte Methode auf, zeigt IMMER den Indikator.
        //        await _view.LockUsageWithIndicator(true, showCancel, true, message);
        //    }
        //    else
        //    {
        //        // Hebt die Sperre auf UND versteckt den Indikator.
        //        await _view.LockUsageWithIndicator(false, false, false);
        //    }
        //}

        public void SwitchAI()
        {
            if (AvailableAgents.Count <= 1) return;

            int currentIndex = AvailableAgents.IndexOf(AiAgent!);
            int nextIndex = (currentIndex + 1) % AvailableAgents.Count;
            AiAgent = AvailableAgents[nextIndex];
        }

        public void ReloadAgents()
        {
            var systemMessage = AISystemMessages.GetSystemMessage(Settings!.UseAvalonia, Settings.UseReactUi, Settings.UsePython, ImageGenHelperCode != null);

            AvailableAgents.Clear();
            Claude = null;
            ChatGPT = null;
            Gemini = null;
            Ollama = null;
            LmStudio = null;

            var openAiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY", EnvironmentVariableTarget.User);
            if (!string.IsNullOrWhiteSpace(openAiKey))
            {
                ChatGPT = new OpenAiTextChatAgent();
                ChatGPT.SetOption("ApiKey", openAiKey);
                ChatGPT.SetOption("Model", Settings.OpenAiModel);
                ChatGPT.SetOption("Temperature", 0.1f);
                ChatGPT.SetOption("TopP", 0.9f);
                ChatGPT.SetInput("SystemMessage", systemMessage);
                AvailableAgents.Add(ChatGPT);
            }

            var anthropicKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY", EnvironmentVariableTarget.User);
            if (!string.IsNullOrWhiteSpace(anthropicKey))
            {
                Claude = new AnthropicTextChatAgent();
                Claude.SetOption("ApiKey", anthropicKey);
                Claude.SetOption("Model", Settings.ClaudeModel);
                Claude.SetOption("Temperature", 0.1f);
                Claude.SetOption("TopP", 0.90f);
                Claude.SetInput("SystemMessage", systemMessage);
                AvailableAgents.Add(Claude);
            }

            var geminiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY", EnvironmentVariableTarget.User);
            if (!string.IsNullOrWhiteSpace(geminiKey))
            {
                Gemini = new GeminiTextChatAgent();
                Gemini.SetOption("ApiKey", geminiKey);
                Gemini.SetOption("Model", Settings.GeminiModel);
                Gemini.SetOption("Temperature", 0.1f);
                Gemini.SetOption("TopP", 0.90f);
                Gemini.SetInput("SystemMessage", systemMessage);
                AvailableAgents.Add(Gemini);
            }

            if (IsLocalEndpointReachable(Settings.OllamaEndpoint))
            {
                Ollama = new OllamaTextChatAgent();
                Ollama.SetOption("Endpoint", Settings.OllamaEndpoint);
                Ollama.SetOption("ApiKey", "ollama");
                Ollama.SetOption("Model", Settings.OllamaModel);
                Ollama.SetOption("Temperature", 0.1f);
                Ollama.SetOption("TopP", 0.9f);
                Ollama.SetInput("SystemMessage", systemMessage);
                AvailableAgents.Add(Ollama);
            }

            if (IsLocalEndpointReachable(Settings.LmStudioEndpoint))
            {
                LmStudio = new LmStudioTextChatAgent();
                LmStudio.SetOption("Endpoint", Settings.LmStudioEndpoint);
                LmStudio.SetOption("ApiKey", "lm-studio");
                LmStudio.SetOption("Model", Settings.LmStudioModel);
                LmStudio.SetOption("Temperature", 0.1f);
                LmStudio.SetOption("TopP", 0.9f);
                LmStudio.SetInput("SystemMessage", systemMessage);
                AvailableAgents.Add(LmStudio);
            }

            AiAgent = Claude ?? Gemini ?? ChatGPT ?? Ollama ?? LmStudio;

            if (AvailableAgents.Count > 0)
            {
                var names = string.Join(", ", AvailableAgents.Select(a => a.Name));
                Debug.WriteLine($"[AppController] Reloaded AI agents: {names}");
            }
        }

        public void ApplyModelSettings()
        {
            Claude?.SetOption("Model", Settings.ClaudeModel);
            ChatGPT?.SetOption("Model", Settings.OpenAiModel);
            Gemini?.SetOption("Model", Settings.GeminiModel);
            Ollama?.SetOption("Model", Settings.OllamaModel);
            Ollama?.SetOption("Endpoint", Settings.OllamaEndpoint);
            LmStudio?.SetOption("Model", Settings.LmStudioModel);
            LmStudio?.SetOption("Endpoint", Settings.LmStudioEndpoint);
        }

        private static bool IsLocalEndpointReachable(string endpoint)
        {
            try
            {
                using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(2) };
                var response = Task.Run(() => http.GetAsync(endpoint.TrimEnd('/') + "/models")).GetAwaiter().GetResult();
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        // In AppController.cs
        public async Task ClearSessionAsync(string reason = "Clearing...")
        {
            // 1. Prüfen, ob die App bereit ist.
            if (CurrentStatus != AppStatus.Idle) return;

            try
            {
                // 2. Den Status setzen (ersetzt LockUsageWithIndicator).
                await SetStatusAsync(AppStatus.Initializing, false, reason);

                // 3. Die eigentliche Logik (fast 1:1 aus der alten Methode).
                Logger.Clear(); // Zugriff über die neue Eigenschaft

                FileHelper.ClearDirectory(NuGetPackageDirectory);

                string freshHistory = "Code:\n\n";

                HandlePythonFilesInVirtualProjectFiles(_settings.UsePython);

                freshHistory += GetUserControlBaseCode();

                // ApplyStateNonUndoableAsync müssen wir auch verschieben.
                // Vorerst rufen wir es über die View auf.
                ApplyStateNonUndoable(s =>
                {
                    s.History = freshHistory;
                    s.LastCode = GetUserControlBaseCode();
                    s.NuGetDlls = new();
                    s.PackageVersions = new();
                }, clearHistory: true);

                // PreloadMandatoryNugetPacks müssen wir auch verschieben.
                await PreloadMandatoryNugetPacks();

                VirtualProjectFiles.UpdateFileContent("./currentcode.cs", GetUserControlBaseCode());

                // Sync the extracted prompt→DLL session with the freshly reset state (code/history/NuGet).
                RecreatePromptToDllSession(preserveState: true);

                await ChildProcessService.RestartAsync();
                _view.dynamicContent.Content = new EmptyUserControl(); // UI-Manipulation über _view
                _view.HideFrostedSnapshot();
                ChildProcessService.HideChild();

                GrantedFolders.Clear();
                _view.ResetButtonMenu(); // UI-Manipulation über _view

                _cancellationSource = new CancellationTokenSource();
            }
            finally
            {
                // 4. UI-spezifische Aktionen am Ende.
                _view.Activate();
                _view.txtPrompt.Text = string.Empty;
                _view.txtPrompt.Focus();

                // 5. Status garantiert zurücksetzen.
                await SetStatusAsync(AppStatus.Idle, false);
            }
        }

        public void ApplyState(
            Action<ApplicationState> mutate,
            string title = "Change",
            string description = "",
            bool skipIfUnchanged = true)
        {
            mutate(AppState);
            _undoRedo.CommitChange(title, description, skipIfUnchanged);
        }

        // Diese auch.
        public void ApplyStateNonUndoable(
            Action<ApplicationState> mutate,
            bool clearHistory = true,
            string title = "Session reset",
            string description = "")
        {
            mutate(AppState);

            if (clearHistory)
                _undoRedo.ResetToCurrentState(title, description);
        }

        public async Task ApplyManualCodeEditAsync(string newCode)
        {
            if (CurrentStatus != AppStatus.Idle) return;

            try
            {
                await SetStatusAsync(AppStatus.Compiling, true, "Compiling manual edit...");

                ApplyState(s => s.LastCode = newCode, "Manual code edit");

                VirtualProjectFiles?.UpdateFileContent("./currentcode.cs", newCode);

                if (_promptToDllSession != null)
                    _promptToDllSession.State.CurrentCode = newCode;

                await CompileAndShowAsync(VirtualProjectFiles!.GetSourceCodeAsStrings());
            }
            catch (Exception ex)
            {
                Logger.LogMessage($"Compilation failed: {ex.Message}", BubbleType.CompletionError);
                Logger.LogMessage("Asking AI to fix the errors...", BubbleType.Info);

                // Hand off to the AI with the compile errors
                LastErrorMsg = ex.Message;
                await SetStatusAsync(AppStatus.Generating, true, "Generating fix...");
                await ExecuteGeneralPrompt("Please fix the compilation errors in the current code.", _cancellationSource.Token);
            }
            finally
            {
                await SetStatusAsync(AppStatus.Idle, false);
            }
        }

        private static string ToSingleLine(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            return Regex.Replace(text, @"\s+", " ").Trim();
        }

        private static string Truncate(string text, int maxLength)
        {
            if (string.IsNullOrEmpty(text) || maxLength <= 0)
                return string.Empty;

            if (text.Length <= maxLength)
                return text;

            return text.Substring(0, Math.Max(0, maxLength - 1)) + "…";
        }

        public async Task PreloadMandatoryNugetPacks()
        {
            var packs = ConvertPackageListToDictionary(DefaultNugets);
            await LoadAndApplyNugetPackages(packs);

            // Preload ist Teil der "Baseline" und soll die aktuelle Root-Snapshot konsistent halten.
            if (!_undoRedo.CanUndo && !_undoRedo.CanRedo)
                _undoRedo.ResetToCurrentState(title: "Session start", description: "Preloaded mandatory NuGet packages");
        }

        public Dictionary<string, string> ConvertPackageListToDictionary(List<string> packageList)
        {
            var result = new Dictionary<string, string>();

            foreach (var package in packageList)
            {
                // Überspringe leere oder nur aus Leerzeichen bestehende Einträge.
                if (string.IsNullOrWhiteSpace(package))
                    continue;

                // Teile den String anhand des Trennzeichens '|'
                var parts = package.Split('|');

                // Wenn sowohl Name als auch Version vorhanden sind:
                if (parts.Length >= 2)
                {
                    var name = parts[0].Trim();
                    var version = parts[1].Trim();
                    result[name] = version;
                }
                // Falls nur der Name vorhanden ist, setze Version auf "default"
                else if (parts.Length == 1)
                {
                    var name = parts[0].Trim();
                    result[name] = "default";
                }
            }

            return result;
        }

        public async Task LoadAndApplyNugetPackages(Dictionary<string, string> packages)
        {
            if (packages == null || packages.Count == 0) return;

            var result = await NugetPackageService.LoadPackagesAsync(packages, AppState.NuGetDlls);

            AppState.NuGetDlls = result.DllPaths;
            foreach (var package in result.PackageVersions)
            {
                AppState.PackageVersions.TryAdd(package.Key, package.Value);
            }
        }

        private async Task<string?> CompileToDllAsync(List<string> codes)
        {
            // Filter: nur Code-Files die nicht leer sind!
            List<string> filteredList = new();
            foreach (var code in codes)
            {
                if( !string.IsNullOrEmpty(code) && !string.IsNullOrWhiteSpace(code) )
                    filteredList.Add(code);
            }

            string? compiledUserControlDllPath = await CompilationService.CompileToDllAsync(
                filteredList,
                DllPath,
                AppState.NuGetDlls,
                AdditionalDlls
            );

            if (string.IsNullOrEmpty(compiledUserControlDllPath))
            {
                Logger.LogMessage("Compilation failed or returned no DLL path. Aborting.", BubbleType.CompletionError);
                LastErrorMsg = "Compilation failed.";
                return null;
            }

            return compiledUserControlDllPath;
        }

        private async Task<bool> DisplayCompiledDllAsync(string compiledUserControlDllPath)
        {
            // Unload old control right before loading the new one
            await ChildProcessService.UnloadControlAsync();

            bool success = await ChildProcessService.DisplayControlAsync(
                mainDllPath: compiledUserControlDllPath,
                nugetDlls: AppState.NuGetDlls ?? Enumerable.Empty<string>(),
                additionalDlls: AdditionalDlls
            );

            if (!success)
            {
                Logger.LogMessage("The compiled code could not be activated in the child process.", BubbleType.CompletionError);
                LastErrorMsg = "Failed to load the control in the child process.";
                return false;
            }

            await _view.HideFrostedSnapshotAsync();
            ChildProcessService.ShowChild();
            ChildProcessService.UpdatePosition();

            LastErrorMsg = "";
            return true;
        }

        private async Task<bool> CompileAndShowAsync(List<string> codes)
        {
            string? compiledUserControlDllPath = await CompileToDllAsync(codes);
            if (string.IsNullOrEmpty(compiledUserControlDllPath))
                return false;

            return await DisplayCompiledDllAsync(compiledUserControlDllPath);
        }

        public async Task ExportProjectAsync(string assemblyName, 
            string exportPath, 
            string selectedCreationMode,
            bool requiresFullExport, // false --> only save resx
            ExportSettings exportSettings,
            string? iconFullPath,
            bool installDesktop = false, bool installStartmenu = false)
        {
            if (CurrentStatus != AppStatus.Idle) return;

            try 
            {
                await SetStatusAsync(AppStatus.Exporting, false, "Exporting...");

                // 1. Daten für den Export sammeln
                var exportData = new ExportData(
                    AssemblyName: assemblyName,
                    ExportPath: exportPath,
                    RequiresFullExport: requiresFullExport,
                    ExportSettings: exportSettings,
                    SelectedCreationMode: selectedCreationMode,
                    VirtualProjectFiles: VirtualProjectFiles,
                    NuGetDlls: AppState.NuGetDlls,
                    PackageVersions: AppState.PackageVersions,
                    AdditionalDlls: AdditionalDlls,
                    AdditionalFilesToCopy: AdditionalFilesForExportCopy,
                    History: AppState.History,
                    IconFullPath: iconFullPath
                );

                // 2. Service aufrufen
                var (success, result, errorMessage) = await AppExportService.ExportAsync(exportData);

                string prefix = "Exported project to ";
                if (exportData.RequiresFullExport == false)
                    prefix = "Saved project to ";

                // 3. Ergebnis verarbeiten
                if (success && result != null)
                {
                    Logger.LogMessage(prefix + result.ExportedOrSavedPath, BubbleType.CompletionSuccess);

#if !HIDE_INTERNAL_CODE
                Logger.LogMessage(ExportWindowBaseCode.CreateBaseCodeForExport(result.AssemblyName), BubbleType.Answer);
#endif
                }
                else
                {
                    Logger.LogMessage($"Export failed: {errorMessage}", BubbleType.CompletionError);
#if !HIDE_INTERNAL_CODE
                Logger.LogMessage(AppState.LastCode, BubbleType.Answer);
                Logger.LogMessage(ExportWindowBaseCode.CreateBaseCodeForExport(assemblyName), BubbleType.Answer);
#endif
                }

                if (installDesktop == true || installStartmenu == true)
                {
                    AppInstaller.InstallApplicationPerUser(
                        basePath: exportPath,
                        appName: assemblyName,
                        installStartMenu: installStartmenu,
                        installDesktop: installDesktop);
                }
            }
            finally
            {
                await SetStatusAsync(AppStatus.Idle, false);
            }
        }

        // In AppController.cs
        public async Task ImportProjectAsync(string assemblyPath)
        {
            // 0. Clear everything
            await ClearSessionAsync();

            // 1. Prüfen, ob die App bereit ist.g
            if (CurrentStatus != AppStatus.Idle) return;

            try
            {
                // 2. Den Status setzen.
                await SetStatusAsync(AppStatus.Importing, false, "Importing...");

                // 3. Die eigentliche Logik
                var importResult = AppImportService.ImportFromAssembly(assemblyPath);

                if (!importResult.Success || importResult.Data == null)
                {
                    Logger.LogMessage(importResult.ErrorMessage ?? "An unknown import error occurred.", BubbleType.CompletionError);
                    return; // Wichtig: hier abbrechen, damit der finally-Block den Status zurücksetzt.
                }

                ResxData resxData = importResult.Data!;

                bool usePython = false;
                bool useAvalonia = false;

                if (resxData.Nuget!.ContainsKey("pythonnet"))
                    usePython = true;
                if (resxData.Nuget.ContainsKey("Avalonia.Desktop"))
                    useAvalonia = true;

                _settings.UsePython = usePython;
                _settings.UseAvalonia = useAvalonia;

                SettingsModel newSettings = SettingsService.Load();
                newSettings.UsePython = usePython;
                newSettings.UseAvalonia = useAvalonia;
                SettingsService.Save(newSettings);

                HandlePythonFilesInVirtualProjectFiles(usePython);

                CrossplatformSettings cpes = new CrossplatformSettings()
                {
                    UsePython = usePython,
                    UseAvalonia = useAvalonia,
                };

                ChildProcessService.ConfigureCrossplatformSettings(cpes);
                await ChildProcessService.RestartAsync();

                // NuGet-Pakete laden und anwenden
                await LoadAndApplyNugetPackages(resxData.Nuget);

                var changesToTest = new Dictionary<string, string>
                {
                    { "./currentcode.cs", resxData.Code! }
                };

                bool success = await CompileAndShowAsync( VirtualProjectFiles.GetSourceCodeAsStrings(changesToTest) );

                if (success)
                {
                    Logger.LogHistory(resxData.Code!);
                    Logger.LogMessage($"App '{Path.GetFileNameWithoutExtension(assemblyPath)}' successfully imported!", BubbleType.Answer);

                    if (!string.IsNullOrEmpty(resxData.History))
                    {
                        Logger.LogMessage("--- Imported History ---", BubbleType.Info);
                        Logger.LogMessageWithMarkdownFormating(resxData.History, BubbleType.Info);
                    }

                    // Harte Zustandsänderung mit unserer neuen, sauberen Helfermethode
                    ApplyStateNonUndoable(s =>
                    {
                        s.History = resxData.History ?? string.Empty;
                        s.LastCode = resxData.Code!;
                        // NuGetDlls und PackageVersions wurden bereits in LoadAndApplyNugetPackages aktualisiert
                        s.NuGetDlls = AppState.NuGetDlls;
                        s.PackageVersions = AppState.PackageVersions;
                    }, clearHistory: true);

                    VirtualProjectFiles.UpdateFileContent("./currentcode.cs", resxData.Code!);

                    // Sync the extracted prompt→DLL pipeline with the imported state/settings.
                    RecreatePromptToDllSession(preserveState: true);
                }
            }
            catch (Exception ex)
            {
                Logger.LogMessage($"A critical error occurred after importing: {ex.Message}", BubbleType.CompletionError);
            }
            finally
            {
                // 4. Status garantiert zurücksetzen.
                await SetStatusAsync(AppStatus.Idle, false);
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

        public async Task ExecutePromptAsync(string prompt)
        {
            if (CurrentStatus != AppStatus.Idle) return;

            try
            {
                // Die Logik aus der alten Execute-Methode kommt hierher.
                if ((!string.IsNullOrEmpty(prompt)) || (!string.IsNullOrEmpty(LastErrorMsg)))
                {
                    // Capture screenshot while child is still visible, then hide.
                    // Do NOT unload the control yet — only unload when we have new code to load.
                    var snapshot = ChildProcessService.CaptureChildScreenshot();
                    ChildProcessService.HideChild();
                    if (snapshot != null)
                        _view.ShowFrostedSnapshot(snapshot);

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

            // NEW: Use the extracted prompt→DLL engine when available.
            try { RecreatePromptToDllSession(preserveState: true); } catch { /* non-critical; falls back to legacy path */ }
            if (_promptToDllSession != null)
            {
                await ExecuteGeneralPromptWithPromptToDllAsync(prompt, cancellationToken, showResultInChild);
                return;
            }

            bool success = false;
            int numTries = Settings?.AiCodeGenerationAttempts ?? 5;
            string originalPrompt = prompt;
            string oldHistory = AppState.History;
            StructuredResponse? structuredResponse = null;
            string baseCodeForPatch = VirtualProjectFiles!.GetFileContent("./currentcode.cs") ?? AppState.LastCode ?? string.Empty;
            string reviewBaseCode = baseCodeForPatch;

            var allNuGetPackagesThisRun = new List<string>();
            var allNuGetPackagesThisRunSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            prompt = BuildDiffFirstPrompt(prompt, baseCodeForPatch);

            int currentAttempt = 0;
            IncreaseAttemptCounter(ref currentAttempt);

            string tmpHist = AppState.History;

            while (!success && numTries > 0 && !cancellationToken.IsCancellationRequested)
            {
                // Version Conflicts detected. Clear Cache with nuget packs!
                if (!string.IsNullOrEmpty(LastErrorMsg) && LastErrorMsg.Contains("CS0433"))
                    FileHelper.ClearDirectory(NuGetPackageDirectory);

                cancellationToken.ThrowIfCancellationRequested();
                numTries--;

                _view._waitIndicator!.StatusText = "Generating Pass: " + currentAttempt.ToString();

                try
                {
                    if (!string.IsNullOrEmpty(LastErrorMsg))
                        prompt += "\n\n" + LastErrorMsg;

                    string? completion = null;
                    string systemMessage = AISystemMessages.GetSystemMessage(Settings!.UseAvalonia, Settings.UseReactUi, Settings.UsePython, ImageGenHelperCode != null);

                    await ExecuteAIQuery(prompt,
                        systemMessage,
                        tmpHist,
                        JsonSchemata.JsonSchemaResponse,
                        cancellationToken);

                    completion = AiAgent.GetOutput<string>("Result");

                    if (completion == null)
                    {
                        Logger.LogMessage("The request could not be processed. Aborting.", BubbleType.CompletionError);
                        LastErrorMsg = "";
                        return;
                    }

                    structuredResponse = JsonConvert.DeserializeObject<StructuredResponse>(completion) ?? new StructuredResponse();

                    if (structuredResponse.Chat != null &&
                        structuredResponse.Chat.Length > 0 && !string.IsNullOrWhiteSpace(structuredResponse.Chat))
                    {
                        Logger.LogMessage(originalPrompt, BubbleType.Prompt);

                        Logger.LogMessageWithMarkdownFormating(structuredResponse.Chat, BubbleType.CompletionSuccess);
                        Logger.LogHistory(structuredResponse.Chat);

                        // Control is still loaded (we only hid the window), just show it again
                        ChildProcessService?.ShowChild();
                        ChildProcessService?.UpdatePosition();

                        return;
                    }

                    // PowerShell / Console App → Single-Shot execution
                    if (!string.IsNullOrWhiteSpace(structuredResponse.PowerShellScript) ||
                        !string.IsNullOrWhiteSpace(structuredResponse.ConsoleAppCode))
                    {
                        Logger.LogMessage(originalPrompt, BubbleType.Prompt);
                        await ExecuteSingleShotToolAsync(originalPrompt, structuredResponse, cancellationToken);
                        return;
                    }

                    if (structuredResponse.NuGetPackages != null)
                    {
                        foreach (var p in structuredResponse.NuGetPackages)
                        {
                            if (string.IsNullOrWhiteSpace(p)) continue;
                            var trimmed = p.Trim();
                            if (allNuGetPackagesThisRunSet.Add(trimmed))
                                allNuGetPackagesThisRun.Add(trimmed);
                        }
                    }

                    string codeToTest = structuredResponse.Code;
                    if (!string.IsNullOrWhiteSpace(structuredResponse.Patch))
                    {
                        var patchResult = UnifiedDiffPatcher.TryApplyToCurrentCode(baseCodeForPatch, structuredResponse.Patch);
                        if (!patchResult.Success || string.IsNullOrWhiteSpace(patchResult.PatchedText))
                            throw new Exception($"The generated patch could not be applied: {patchResult.ErrorMessage}");

                        codeToTest = patchResult.PatchedText;
                        structuredResponse.Code = codeToTest;
                    }

                    if (ContainsNamedUserControl(codeToTest, "DynamicUserControl") == false)
                        throw new Exception("The generated UserControl is invalid...");

                    bool shouldReview = !Settings!.AcceptAutomatic && showResultInChild;

                    List<string>? nugetDllsBefore = null;
                    Dictionary<string, string>? packageVersionsBefore = null;
                    if (shouldReview)
                    {
                        nugetDllsBefore = AppState.NuGetDlls.ToList();
                        packageVersionsBefore = new Dictionary<string, string>(AppState.PackageVersions, StringComparer.Ordinal);
                    }

                    var packs = ConvertPackageListToDictionary(structuredResponse!.NuGetPackages!);
                    await LoadAndApplyNugetPackages(packs);

                    if (showResultInChild)
                    {
                        var changesToTest = new Dictionary<string, string>
                        {
                            { "./currentcode.cs", codeToTest }
                        };

                        if (shouldReview)
                        {
                            string? compiledDllPath = await CompileToDllAsync(VirtualProjectFiles.GetSourceCodeAsStrings(changesToTest));
                            if (string.IsNullOrWhiteSpace(compiledDllPath))
                                throw new InvalidOperationException("Failed to compile the generated code.");

                             string previewPatch = UnifiedDiffGenerator.CreatePatchForCurrentCode(reviewBaseCode, codeToTest);
                             structuredResponse.Patch = previewPatch;
                             structuredResponse.NuGetPackages = allNuGetPackagesThisRun;

                             PatchReviewDecision decision = _view.Dispatcher.Invoke(() =>
                              {
                                 var review = new PatchReviewWindow(previewPatch, allNuGetPackagesThisRun, structuredResponse.Explanation)
                                 {
                                     Owner = _view
                                 };
                                 review.ShowDialog();
                                 return review.Decision;
                              });

                             if (decision != PatchReviewDecision.Apply)
                             {
                                 if (nugetDllsBefore != null) AppState.NuGetDlls = nugetDllsBefore;
                                 if (packageVersionsBefore != null) AppState.PackageVersions = packageVersionsBefore;

                                 if (decision == PatchReviewDecision.Regenerate)
                                 {
                                     string regenInstruction = "Please regenerate a safer revision. Return a valid unified diff patch targeting './currentcode.cs'.";

                                     prompt = BuildDiffFirstPrompt(
                                         userPrompt: originalPrompt + "\n\n" + regenInstruction,
                                         currentCode: reviewBaseCode);

                                     tmpHist = "";
                                     LastErrorMsg = "";

                                     IncreaseAttemptCounter(ref currentAttempt);
                                     numTries = Math.Max(numTries, 1);
                                     continue;
                                 }

                                 Logger.LogMessage("Changes rejected.", BubbleType.Info);
                                 return;
                             }

                             if (!await DisplayCompiledDllAsync(compiledDllPath))
                                 throw new InvalidOperationException("Failed to load the control in the child process.");
                         }
                        else
                        {
                        // Normaler Flow: Code kompilieren und sofort anzeigen.
                        if (await CompileAndShowAsync(VirtualProjectFiles.GetSourceCodeAsStrings(changesToTest)) == false)
                        {
                            // Wenn das Anzeigen fehlschlägt, zählt der Versuch als fehlgeschlagen.
                            // Wir werfen eine Exception, um in den catch-Block zu springen und einen neuen Versuch zu starten.
                            throw new InvalidOperationException("Failed to compile and show the generated code.");
                        }
                        }
                    }
                    // Im Reparatur-Flow (showResultInChild == false) tun wir hier nichts.
                    // Der Code wird erst später in HandleChildError kompiliert und angezeigt.
                    // --- ENDE DER ÄNDERUNG ---

                    success = true;
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    IncreaseAttemptCounter(ref currentAttempt);

                    if( structuredResponse == null )
                    {
                        // Das wird beim nächsten Durchgang dann wohl auch nichts mehr!
                        // Am besten abbrechen und die Token sparen!
                        Logger.LogMessage("The AI service timed out. Operation aborted.", BubbleType.CompletionError);
                        break;
                    }

                    string msg = $"Exception Message:\n{e.Message}\n\nInner Exception:\n{e.InnerException?.Message}\n\nStack Trace:\n{e.StackTrace}";
                    LastErrorMsg = msg;

                    tmpHist = "";

                    if (!string.IsNullOrWhiteSpace(structuredResponse.Code))
                        baseCodeForPatch = structuredResponse.Code;

                    bool patchRelatedError =
                        !string.IsNullOrWhiteSpace(structuredResponse.Patch) &&
                        e.Message.Contains("patch", StringComparison.OrdinalIgnoreCase);

                    string retryInstruction = patchRelatedError
                        ? $"Your previous PATCH could not be applied ({e.Message}). Return a valid unified diff patch targeting './currentcode.cs' (must include at least one '@@' hunk). If you cannot, return CODE RESPONSE."
                        : "Please fix the syntax errors in the current code. Keep changes minimal.";

                    prompt = BuildDiffFirstPrompt(
                        userPrompt: originalPrompt + "\n\n" + retryInstruction,
                        currentCode: baseCodeForPatch);
                    // LastErrorMsg wird oben in der Schleife drangehangen!

                    _view.txtPrompt.Text = originalPrompt;

                    Logger.LogMessageWithMarkdownFormating(AppLogger.StructuredResponseToText(structuredResponse!), BubbleType.CompletionError);
                    Logger.LogMessage(msg, BubbleType.CompletionError);
                }
                catch(OperationCanceledException)
                {
                    Logger.LogMessage("Operation cancelled.", BubbleType.CompletionError);
                    if (showResultInChild)
                    {
                        Logger.LogMessage("Restoring last good state.", BubbleType.CompletionError);
                        await CompileAndShowAsync(VirtualProjectFiles.GetSourceCodeAsStrings());

                        // Wir können auch wegen einem Timeout hier sein!
                        // Dann springen wir am besten raus!
                        if (!cancellationToken.IsCancellationRequested)
                            break;
                    }
                }
            }

            if (success)
            {
                var newHistory = oldHistory;
                newHistory += "\n\n" + originalPrompt;
                Logger.LogMessage(originalPrompt, BubbleType.Prompt);

                string? newLastCode = AppState.LastCode;
                if (structuredResponse != null)
                {
                    var answerText = AppLogger.StructuredResponseToText(structuredResponse);
                    newHistory += answerText;
                    Logger.LogMessageWithMarkdownFormating(answerText, BubbleType.Answer);
                    newLastCode = structuredResponse.Code;
                }

                string historyTitle = $"Prompt: {Truncate(ToSingleLine(originalPrompt), 60)}";
                string historyDescription = "Update";
                if (structuredResponse != null)
                {
                    if (!string.IsNullOrWhiteSpace(structuredResponse.Explanation))
                        historyDescription = Truncate(ToSingleLine(structuredResponse.Explanation), 240);
                    else if (!string.IsNullOrWhiteSpace(structuredResponse.Patch))
                        historyDescription = $"Patch ({structuredResponse.Patch.Split('\n').Length} lines)";
                    else if (!string.IsNullOrWhiteSpace(structuredResponse.Code))
                        historyDescription = $"Code ({structuredResponse.Code.Length} chars)";

                    if (structuredResponse.NuGetPackages != null && structuredResponse.NuGetPackages.Count > 0)
                        historyDescription = $"{historyDescription}\nNuGet: {string.Join(", ", structuredResponse.NuGetPackages)}";
                }
                ApplyState(s =>
                {
                    s.History = newHistory;
                    // WICHTIG: Wir müssen den LastCode hier aktualisieren,
                    // auch wenn wir ihn im Reparaturfall noch nicht angezeigt haben.
                    s.LastCode = newLastCode ?? s.LastCode;
                }, title: historyTitle, description: historyDescription, skipIfUnchanged: true);

                VirtualProjectFiles.UpdateFileContent("./currentcode.cs", AppState.LastCode!);
            }
            else if (!cancellationToken.IsCancellationRequested)
            {
                // --- ZWEITE ÄNDERUNG ---
                // Im Fehlerfall versuchen wir, den letzten guten Code wiederherzustellen,
                // aber nur im normalen Flow.
                if (showResultInChild)
                {
                    await CompileAndShowAsync(VirtualProjectFiles.GetSourceCodeAsStrings());
                }
                int maxAttempts = Settings?.AiCodeGenerationAttempts ?? 5;
                Logger.LogMessage(
                    $"Code generation failed after {currentAttempt} of {maxAttempts} attempts. " +
                    "The AI was unable to produce valid code. You can try rephrasing your prompt or simplifying your request.",
                    BubbleType.CompletionError);
                // --- ENDE DER ÄNDERUNG ---
            }

            LastErrorMsg = "";
        }

        private async Task ExecuteGeneralPromptWithPromptToDllAsync(string prompt, CancellationToken cancellationToken, bool showResultInChild)
        {
            string originalPrompt = prompt ?? string.Empty;

            AssemblyForgeReviewCallback? reviewCallback = null;
            if (showResultInChild && !Settings.AcceptAutomatic)
            {
                reviewCallback = async (ctx, ct) =>
                {
                    PatchReviewDecision decision = _view.Dispatcher.Invoke(() =>
                    {
                        var review = new PatchReviewWindow(ctx.Patch, ctx.NuGetPackages, ctx.Explanation)
                        {
                            Owner = _view
                        };
                        review.ShowDialog();
                        return review.Decision;
                    });

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
                var sr = result.StructuredResponse!;
                var localResponse = new StructuredResponse
                {
                    Code = sr.Code,
                    Patch = sr.Patch,
                    NuGetPackages = sr.NuGetPackages?.ToList() ?? new(),
                    Explanation = sr.Explanation,
                    Chat = sr.Chat,
                    PowerShellScript = sr.PowerShellScript,
                    ConsoleAppCode = sr.ConsoleAppCode,
                };
                Logger.LogMessage(originalPrompt, BubbleType.Prompt);
                await ExecuteSingleShotToolAsync(originalPrompt, localResponse, cancellationToken);
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

        private static string BuildDiffFirstPrompt(string userPrompt, string currentCode)
        {
            userPrompt ??= string.Empty;
            currentCode ??= string.Empty;

            return "You are editing the existing C# file './currentcode.cs'.\n\n" +
                   "PATCH REQUIREMENTS: The Patch field must include at least one hunk header line starting with '@@' (prefer numeric unified diff like '@@ -10,7 +10,8 @@').\n\n" +
                   "CURRENT FILE CONTENT:\n" +
                   "```csharp\n" +
                   currentCode +
                   "\n```\n\n" +
                   "TASK:\n" +
                   userPrompt +
                   "\n\n" +
                   "Prefer PATCH RESPONSE. If the patch would be extremely large or cannot be made to apply cleanly, use CODE RESPONSE instead.";
        }

        private static async Task<(string stdout, string stderr, int exitCode)> RunProcessAsync(string exePath, int timeoutSeconds, CancellationToken cancellationToken)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(exePath) ?? Environment.CurrentDirectory
            };

            using var process = new Process { StartInfo = startInfo };

            try
            {
                process.Start();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Could not start the compiled application: {ex.Message}", ex);
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            using var timeoutCts = timeoutSeconds > 0
                ? new CancellationTokenSource(timeoutSeconds * 1000)
                : new CancellationTokenSource();

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            try
            {
                await process.WaitForExitAsync(linkedCts.Token);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                throw new TimeoutException($"Console application exceeded the timeout of {timeoutSeconds} seconds.");
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            return (stdout.TrimEnd(), stderr.TrimEnd(), process.ExitCode);
        }

        // ── Single-Shot Tool Execution ────────────────────────────────────

        private async Task ExecuteSingleShotToolAsync(string originalPrompt, StructuredResponse response, CancellationToken cancellationToken)
        {
            AgentStepResult stepResult;

            if (!string.IsNullOrWhiteSpace(response.PowerShellScript))
                stepResult = await ExecutePowerShellStepAsync(response.PowerShellScript, response.Explanation, cancellationToken);
            else if (!string.IsNullOrWhiteSpace(response.ConsoleAppCode))
                stepResult = await ExecuteConsoleAppStepAsync(response.ConsoleAppCode, response.NuGetPackages?.ToList(), response.Explanation, cancellationToken);
            else
                return;

            if (stepResult.WasRejected)
                return;

            string preview = Truncate(ToSingleLine(stepResult.Stdout), 500);
            if (!string.IsNullOrWhiteSpace(stepResult.Stderr))
                preview += " | STDERR: " + Truncate(ToSingleLine(stepResult.Stderr), 200);
            Logger.LogMessage($"{stepResult.ToolName}: {preview}", BubbleType.Info);

            try
            {
                string summaryPrompt = $"The user asked: \"{originalPrompt}\"\n\n"
                    + $"A {stepResult.ToolName} was executed.\n\nSTDOUT:\n{Truncate(stepResult.Stdout, 8000)}\n\n"
                    + (string.IsNullOrEmpty(stepResult.Stderr) ? "" : $"STDERR:\n{Truncate(stepResult.Stderr, 4000)}\n\n")
                    + $"Exit Code: {stepResult.ExitCode}\n\n"
                    + "Please provide a brief, user-friendly explanation of the result in the language the user used.";

                AiAgent!.SetInput("Prompt", summaryPrompt);
                AiAgent.SetInput("SystemMessage", "You summarize tool execution results for the user.");
                AiAgent.SetInput("History", "");
                AiAgent.SetInput("JsonSchema", "");
                await AiAgent.ExecuteAsync(cancellationToken);

                var summary = AiAgent.GetOutput<string>("Result") ?? stepResult.Stdout;
                Logger.LogMessageWithMarkdownFormating(summary, BubbleType.Answer);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception)
            {
                Logger.LogMessage(Truncate(stepResult.Stdout, 2000), BubbleType.Answer);
            }

            LastErrorMsg = string.Empty;
        }

        // ── Agent Step infrastructure ────────────────────────────────────────

        private sealed class AgentStepResult
        {
            public string Stdout { get; init; } = string.Empty;
            public string Stderr { get; init; } = string.Empty;
            public int ExitCode { get; init; }
            public string ToolName { get; init; } = string.Empty;
            public bool WasRejected { get; init; }
        }

        private async Task<AgentStepResult> ExecutePowerShellStepAsync(string script, string? explanation, CancellationToken cancellationToken)
        {
            // Review if AcceptAutomatic is off
            if (!Settings.AcceptAutomatic)
            {
                PatchReviewDecision decision = _view.Dispatcher.Invoke(() =>
                {
                    var review = new PatchReviewWindow(script, null, explanation, isPowerShellMode: true)
                    {
                        Owner = _view
                    };
                    review.ShowDialog();
                    return review.Decision;
                });

                if (decision == PatchReviewDecision.Reject)
                {
                    Logger.LogMessage("PowerShell script rejected.", BubbleType.Info);
                    return new AgentStepResult { WasRejected = true, ToolName = "PowerShell" };
                }
            }

            Logger.LogMessage("Executing PowerShell script...", BubbleType.Info);

            try
            {
                PowerShellAgent.SetInput("Script", script);
                PowerShellAgent.SetInput("TimeoutSeconds", 60);
                await PowerShellAgent.ExecuteAsync(cancellationToken);

                return new AgentStepResult
                {
                    Stdout = PowerShellAgent.GetOutput<string>("StandardOutput") ?? string.Empty,
                    Stderr = PowerShellAgent.GetOutput<string>("ErrorOutput") ?? string.Empty,
                    ExitCode = PowerShellAgent.GetOutput<int>("ExitCode"),
                    ToolName = "PowerShell",
                };
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return new AgentStepResult
                {
                    Stdout = string.Empty,
                    Stderr = $"PowerShell error: {ex.Message}",
                    ExitCode = -1,
                    ToolName = "PowerShell",
                };
            }
        }

        private async Task<AgentStepResult> ExecuteConsoleAppStepAsync(string code, List<string>? nugetPackages, string? explanation, CancellationToken cancellationToken)
        {
            // Review if AcceptAutomatic is off
            if (!Settings.AcceptAutomatic)
            {
                PatchReviewDecision decision = _view.Dispatcher.Invoke(() =>
                {
                    var review = new PatchReviewWindow(code, nugetPackages, explanation, isConsoleAppMode: true)
                    {
                        Owner = _view
                    };
                    review.ShowDialog();
                    return review.Decision;
                });

                if (decision == PatchReviewDecision.Reject)
                {
                    Logger.LogMessage("Console app rejected.", BubbleType.Info);
                    return new AgentStepResult { WasRejected = true, ToolName = "ConsoleApp" };
                }
            }

            Logger.LogMessage("Compiling console application...", BubbleType.Info);

            string? exePath = null;
            string tempDir = Path.Combine(Path.GetTempPath(), "Neo.ConsoleApp", Guid.NewGuid().ToString("N"));

            try
            {
                // Load NuGet packages if needed
                List<string> nugetDlls = new();
                if (nugetPackages != null && nugetPackages.Count > 0)
                {
                    var packs = ConvertPackageListToDictionary(nugetPackages);
                    if (packs.Count > 0)
                    {
                        var nugetResult = await NugetPackageService.LoadPackagesAsync(packs, new List<string>());
                        nugetDlls = nugetResult.DllPaths;
                    }
                }

                // Compile
                exePath = await CompilationService.CompileToExeAsync(
                    new List<string> { code },
                    tempDir,
                    nugetDlls,
                    assemblyName: "ConsoleApp",
                    mainTypeName: "ConsoleApp.Program");

                if (string.IsNullOrWhiteSpace(exePath))
                    throw new InvalidOperationException("Compilation failed — no executable was produced.");

                Logger.LogMessage("Running console application...", BubbleType.Info);

                var (stdout, stderr, exitCode) = await RunProcessAsync(exePath, timeoutSeconds: 60, cancellationToken);

                return new AgentStepResult
                {
                    Stdout = stdout,
                    Stderr = stderr,
                    ExitCode = exitCode,
                    ToolName = "ConsoleApp",
                };
            }
            catch (Exception ex)
            {
                return new AgentStepResult
                {
                    Stdout = string.Empty,
                    Stderr = $"Console app error: {ex.Message}",
                    ExitCode = -1,
                    ToolName = "ConsoleApp",
                };
            }
            finally
            {
                try
                {
                    if (Directory.Exists(tempDir))
                        Directory.Delete(tempDir, recursive: true);
                }
                catch { /* best effort */ }
            }
        }

        public static bool ContainsNamedUserControl(string code, string className)
        {
            if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(className))
                return false;

            // Robust: SyntaxTree parsen und gezielt nach einer Klassendeklaration suchen,
            // deren Name passt und deren Basistyp "UserControl" ist (egal ob mit sealed/partial/etc.)
            var tree = CSharpSyntaxTree.ParseText(code);
            var root = tree.GetRoot();

            foreach (var cls in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
            {
                if (!string.Equals(cls.Identifier.ValueText, className, StringComparison.Ordinal))
                    continue;

                if (cls.BaseList == null)
                    continue;

                foreach (var baseType in cls.BaseList.Types)
                {
                    if (IsUserControlType(baseType.Type))
                        return true;
                }
            }

            return false;
        }

        private static bool IsUserControlType(TypeSyntax typeSyntax)
        {
            // Wir prüfen syntaktisch auf den rechten/letzten Namensteil == "UserControl".
            // Damit funktionieren u.a.:
            // - UserControl
            // - System.Windows.Controls.UserControl
            // - Avalonia.Controls.UserControl
            // - global::System.Windows.Controls.UserControl
            var rightMost = GetRightMostIdentifier(typeSyntax);
            return string.Equals(rightMost, "UserControl", StringComparison.Ordinal);
        }

        private static string? GetRightMostIdentifier(TypeSyntax typeSyntax)
        {
            switch (typeSyntax)
            {
                case IdentifierNameSyntax ins:
                    return ins.Identifier.ValueText;
                case GenericNameSyntax gns:
                    return gns.Identifier.ValueText;
                case QualifiedNameSyntax qns:
                    return GetRightMostIdentifier(qns.Right);
                case AliasQualifiedNameSyntax aqns:
                    return GetRightMostIdentifier(aqns.Name);
                default:
                    return null;
            }
        }

        public void IncreaseAttemptCounter(ref int currentAttempt)
        {
            currentAttempt += 1;
        }

        private void HandleDesignerSelectionReceived(DesignerSelectionMessage selection)
        {
            if (CurrentStatus != AppStatus.Idle)
                return;

            if (!_view.Dispatcher.CheckAccess())
            {
                _ = _view.Dispatcher.InvokeAsync(() => HandleDesignerSelectionReceived(selection));
                return;
            }

            if (_designerPropertiesWindow == null || !_designerPropertiesWindow.IsVisible)
            {
                _designerPropertiesWindow = new DesignerPropertiesWindow
                {
                    Owner = _view
                };

                _designerPropertiesWindow.ApplyRequested += DesignerPropertiesWindow_ApplyRequested;
                _designerPropertiesWindow.Closed += (_, _) => _designerPropertiesWindow = null;
                _designerPropertiesWindow.Show();
            }

            _designerPropertiesWindow.SetSelection(selection);
            _designerPropertiesWindow.RepositionNearCursor();
            _designerPropertiesWindow.Activate();
        }

        private async void DesignerPropertiesWindow_ApplyRequested(object? sender, DesignerApplyRequestedEventArgs e)
        {
            await ApplyDesignerEditsAsync(e.Selection, e.Updates);
        }

        private async Task ApplyDesignerEditsAsync(DesignerSelectionMessage selection, IReadOnlyDictionary<string, string> updates)
        {
            if (CurrentStatus != AppStatus.Idle)
                return;

            var currentCode = VirtualProjectFiles.GetFileContent("./currentcode.cs") ?? AppState.LastCode ?? string.Empty;

            if (!DesignerCodeEditor.TryApplyDesignerEdits(
                code: currentCode,
                useAvalonia: Settings.UseAvalonia,
                designId: selection.DesignId,
                updates: updates,
                updatedCode: out var newCode,
                error: out var err))
            {
                Logger.LogMessage(err ?? "Designer edit failed.", BubbleType.CompletionError);
                return;
            }

            if (string.Equals(newCode, currentCode, StringComparison.Ordinal))
                return;

            try
            {
                await SetStatusAsync(AppStatus.Compiling, false, "Applying designer change...");

                var changesToTest = new Dictionary<string, string> { { "./currentcode.cs", newCode } };
                bool compiledOk = await CompileAndShowAsync(VirtualProjectFiles.GetSourceCodeAsStrings(changesToTest));

                if (!compiledOk)
                {
                    Logger.LogMessage("Designer change did not compile; not applied.", BubbleType.CompletionError);
                    return;
                }

                string changedKeys = string.Join(", ", updates.Keys.Take(6));
                if (updates.Count > 6)
                    changedKeys += ", …";

                ApplyState(
                    s => { s.LastCode = newCode; },
                    title: $"Designer edit: {selection.DesignId}",
                    description: updates.Count <= 0 ? "Designer edit" : $"{updates.Count} change(s): {changedKeys}",
                    skipIfUnchanged: true);
                VirtualProjectFiles.UpdateFileContent("./currentcode.cs", newCode);
            }
            finally
            {
                await SetStatusAsync(AppStatus.Idle, false);
            }
        }

        private async Task HandleChildProcessCrashed(CrashReason reason, ErrorMessage err)
        {
            if (CurrentStatus != AppStatus.Idle)
            {
                // Wenn die App schon beschäftigt ist (z.B. beim Exportieren),
                // kann sie nicht gleichzeitig reparieren. Logge das Problem.
                Logger.LogMessage($"Child process crashed while app was busy ({CurrentStatus}). Recovery postponed.", BubbleType.CompletionError);
                _pendingCrash = (reason, err);
                return;
            }

            try
            {
                switch (reason)
                {
                    case CrashReason.UnhandledException:
                        await PerformAutomaticRepairAsync(err);
                        break;

                    case CrashReason.HeartbeatTimeout:
                    case CrashReason.PipeDisconnected:
                        await ShowRecoveryDialogAsync(err);
                        break;
                }
            }
            catch (Exception ex)
            {
                Logger.LogMessage($"CRITICAL: The recovery/repair process itself failed: {ex.Message}", BubbleType.CompletionError);
            }
        }

        private async Task PerformAutomaticRepairAsync(ErrorMessage err)
        {
            try
            {
                // 1. Status setzen und UI blockieren.
                _view.RepairOverlay.Visibility = Visibility.Visible;
                await SetStatusAsync(AppStatus.Repairing, true, "Attempting automatic repair...");

                // 2. Logik ausführen
                await ChildProcessService.RestartAsync();
                ChildProcessService.HideChild();

                Logger.LogMessage("The application crashed. Attempting an automatic repair...", BubbleType.Info);
                string exceptionMessage = $"{err.Message}\n\nType: {err.ExceptionType}\n\nStack Trace:\n{err.StackTrace}";
                LastErrorMsg = exceptionMessage;

                string repairPrompt = "Please fix the previous error.";

                if (err.ExceptionType != null &&
                    err.ExceptionType.Contains("Python", StringComparison.OrdinalIgnoreCase))
                {
                    repairPrompt =
                        "The application crashed with a Python runtime error. " +
                        "Please fix the Python code. The error details are included below. " +
                        "Remember: Do not catch PythonException — let errors propagate for automatic repair.";
                }

                await ExecuteGeneralPrompt(repairPrompt, _cancellationSource.Token, showResultInChild: true);

                _view.txtPrompt.Clear();

                Logger.LogMessage("Automatic repair successful. The application has been restored.", BubbleType.Info);                            
            }
            catch (OperationCanceledException)
            {
                Logger.LogMessage("Repair was aborted by user. Resetting the application.", BubbleType.Info);
                await ClearSessionAsync(); // Ruft unsere saubere Clear-Methode auf
            }
            finally
            {
                // 3. UI aufräumen und Status zurücksetzen
                ChildProcessService.ShowChild();
                _view.RepairOverlay.Visibility = Visibility.Collapsed; // UI-Manipulation über _view
                await SetStatusAsync(AppStatus.Idle, false);
            }
        }

        private async Task ShowRecoveryDialogAsync(ErrorMessage err)
        {
            await ChildProcessService.RestartAsync();

            // Der Dialog ist ein UI-Element, also muss die View ihn erstellen und anzeigen.
            // Wir fügen eine Helfermethode in der MainWindow hinzu.
            var result = _view.ShowCrashDialog();

            switch (result)
            {
                case CrashDialogResult.Button1: // Restore
                    Logger.LogMessage("Attempting to restore the last known good state...", BubbleType.Info);
                    await ChildProcessService.RestartAsync();
                    await CompileAndShowAsync(VirtualProjectFiles.GetSourceCodeAsStrings());
                    Logger.LogMessage("Restore successful.", BubbleType.Info);
                    break;

                case CrashDialogResult.Button2: // Reset
                    Logger.LogMessage("Resetting the application state...", BubbleType.Info);
                    await ClearSessionAsync();
                    break;

                case CrashDialogResult.Button3: // Do Nothing
                default:
                    Logger.LogMessage("Recovery cancelled by user.", BubbleType.Info);
                    break;
            }
        }

        public async Task UpdateSandboxConfigurationAsync(bool useSandbox, bool allowInternet)
        {
            if (CurrentStatus != AppStatus.Idle) return;

            try
            {
                string statusMessage = useSandbox ? "Enabling Sandbox mode..." : "Disabling Sandbox mode...";
                await SetStatusAsync(AppStatus.Initializing, false, statusMessage);

                // Die Logik aus der alten Methode hierher.
                var settings = new SandboxSettings
                {
                    AllowNetworkAccess = allowInternet,
                    GrantedFolders = new List<string>(GrantedFolders) // _grantedFolders ist ja schon im Controller
                };

                ChildProcessService.ConfigureSandbox(useSandbox, settings);
                await ChildProcessService.RestartAsync();

                // WICHTIG: Den letzten Code im neuen Prozess laden.
                // Der Aufruf geht jetzt an die noch in der View befindliche Methode.
                await CompileAndShowAsync(VirtualProjectFiles.GetSourceCodeAsStrings());
            }
            catch (Exception ex)
            {
                Logger.LogMessage($"Failed to update sandbox configuration: {ex.Message}", BubbleType.CompletionError);
            }
            finally
            {
                await SetStatusAsync(AppStatus.Idle, false);
            }
        }

        public async Task<bool> SetDesignerModeAsync(bool enabled)
        {
            if (CurrentStatus != AppStatus.Idle) return false;
            if (enabled && !ChildProcessService.HasLoadedControl) return false;

            if (enabled)
            {
                bool ok = await EnsureDesignIdsForClickToEditAsync();
                if (!ok)
                    return false;
            }

            if (!enabled)
            {
                await RemoveDesignIdsAsync();
            }

            await ChildProcessService.SetDesignerModeAsync(enabled);

            if (!enabled && _designerPropertiesWindow != null)
            {
                try { _designerPropertiesWindow.Close(); } catch { /* window may already be closed */ }
                _designerPropertiesWindow = null;
            }

            return true;
        }

        private async Task<bool> EnsureDesignIdsForClickToEditAsync()
        {
            if (CurrentStatus != AppStatus.Idle)
                return false;

            var currentCode = VirtualProjectFiles.GetFileContent("./currentcode.cs") ?? AppState.LastCode ?? string.Empty;

            if (!DesignerIdInjector.TryInjectDesignIds(
                code: currentCode,
                useAvalonia: Settings.UseAvalonia,
                updatedCode: out var newCode,
                injectedCount: out var injectedCount,
                error: out var injectErr))
            {
                Logger.LogMessage(injectErr ?? "Designer ID injection failed.", BubbleType.CompletionError);
                return false;
            }

            if (injectedCount <= 0 || string.Equals(newCode, currentCode, StringComparison.Ordinal))
                return true;

            try
            {
                await SetStatusAsync(AppStatus.Compiling, false, "Applying designer IDs...");

                var changesToTest = new Dictionary<string, string> { { "./currentcode.cs", newCode } };
                bool compiledOk = await CompileAndShowAsync(VirtualProjectFiles.GetSourceCodeAsStrings(changesToTest));

                if (!compiledOk)
                {
                    Logger.LogMessage("Failed to compile with injected designer IDs.", BubbleType.CompletionError);
                    return false;
                }

                ApplyState(
                    s => { s.LastCode = newCode; },
                    title: "Inject designer IDs",
                    description: $"Injected {injectedCount} design ID(s) for click-to-edit.",
                    skipIfUnchanged: true);
                VirtualProjectFiles.UpdateFileContent("./currentcode.cs", newCode);
                Logger.LogMessage($"Injected {injectedCount} design IDs for click-to-edit.", BubbleType.Info);
                return true;
            }
            finally
            {
                await SetStatusAsync(AppStatus.Idle, false);
            }
        }

        private async Task RemoveDesignIdsAsync()
        {
            var currentCode = VirtualProjectFiles.GetFileContent("./currentcode.cs") ?? AppState.LastCode ?? string.Empty;

            if (!DesignerIdInjector.TryRemoveDesignIds(
                code: currentCode,
                updatedCode: out var cleanCode,
                removedCount: out var removedCount,
                error: out var removeErr))
            {
                Logger.LogMessage(removeErr ?? "Designer ID removal failed.", BubbleType.CompletionError);
                return;
            }

            if (removedCount <= 0 || string.Equals(cleanCode, currentCode, StringComparison.Ordinal))
                return;

            try
            {
                await SetStatusAsync(AppStatus.Compiling, false, "Removing designer IDs...");

                var changesToTest = new Dictionary<string, string> { { "./currentcode.cs", cleanCode } };
                bool compiledOk = await CompileAndShowAsync(VirtualProjectFiles.GetSourceCodeAsStrings(changesToTest));

                if (!compiledOk)
                {
                    Logger.LogMessage("Failed to compile after removing designer IDs.", BubbleType.CompletionError);
                    return;
                }

                ApplyState(
                    s => { s.LastCode = cleanCode; },
                    title: "Remove designer IDs",
                    description: $"Removed {removedCount} design ID(s).",
                    skipIfUnchanged: true);
                VirtualProjectFiles.UpdateFileContent("./currentcode.cs", cleanCode);
            }
            finally
            {
                await SetStatusAsync(AppStatus.Idle, false);
            }
        }

        //public async Task UpdateCrossplatformConfigurationAsync(bool useAvalonia)
        //{
        //    if (CurrentStatus != AppStatus.Idle) return;

        //    try
        //    {
        //        string statusMessage = useAvalonia ? "Enabling Crossplatform mode..." : "Disabling Crossplatform mode...";
        //        await SetStatusAsync(AppStatus.Initializing, false, statusMessage);

        //        // Die Logik aus der alten Methode hierher.
        //        var settings = new CrossplatformSettings
        //        {
        //            UseAvalonia = useAvalonia,
        //        };

        //        ChildProcessService.ConfigureCrossplatformSettings(settings);
        //        await ChildProcessService.RestartAsync();

        //        // WICHTIG: Den letzten Code im neuen Prozess laden.
        //        // Der Aufruf geht jetzt an die noch in der View befindliche Methode.
        //        await CompileAndShowAsync(VirtualProjectFiles.GetSourceCodeAsStrings());
        //    }
        //    catch (Exception ex)
        //    {
        //        Logger.LogMessage($"Failed to update crossplatform configuration: {ex.Message}", BubbleType.CompletionError);
        //    }
        //    finally
        //    {
        //        await SetStatusAsync(AppStatus.Idle, false);
        //    }
        //}

        public void SetSharedFolderAccess(bool grantAccess)
        {
            GrantedFolders.Clear();
            if (grantAccess)
            {
                string? path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), MainWindow.AppName, "shared"); // AppName muss in den Controller
                GrantedFolders.Add(path);
            }
        }

        public async Task<bool> RecompileStateAsync(string code) // Jetzt ohne Status-Management
        {
            var changesToTest = new Dictionary<string, string>
            {
                { "./currentcode.cs", code }
            };

            bool ok = await CompileAndShowAsync(VirtualProjectFiles.GetSourceCodeAsStrings(changesToTest));
            if (ok)
                VirtualProjectFiles.UpdateFileContent("./currentcode.cs", code);

            return ok;
        }

        private string GetUserControlBaseCode()
        {
            if (!Settings.UseAvalonia)
                return UserControlBaseCode.BaseCode;
            else
                return UserControlBaseCodeAvalonia.BaseCode;
        }
    }
}
