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
using System.IO;
using System.Net.NetworkInformation;
using System.Numerics;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

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

    public partial class AppController
    {
        private readonly IMainView _view;
        private readonly IPlatformServices _platformServices;

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
        public Dictionary<string, string> PythonHelperCode { get; set; } = new();

        /// <summary>
        /// Auto-discovered plugin agents that implement IAppIntegratedAgent.
        /// </summary>
        public List<IAppIntegratedAgent> PluginAgents { get; private set; } = new();

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
                var requiredDll = ResolveAppDll(GetAIQueryAgentDll(value.AIQueryProvider));
                if (!AdditionalDlls.Contains(requiredDll))
                    AdditionalDlls.Add(requiredDll);

                var requiredNuget = GetAIQueryNuGetPackage(value.AIQueryProvider);
                DefaultNugets.Clear();
                DefaultNugets.Add(requiredNuget);
                // Note: Avalonia DLLs are NOT loaded from NuGet — Neo.App.Avalonia
                // registers them from its own bin directory (RegisterHostAvaloniaDlls).
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


        public AppController(IMainView view, IPlatformServices platformServices)
        {
            _view = view;
            _platformServices = platformServices ?? throw new ArgumentNullException(nameof(platformServices));

            Logger = view.CreateLogger(AppState);

            Settings = SettingsService.Load();

            var hostAssembly = System.Reflection.Assembly.GetEntryAssembly() ?? GetType().Assembly;
            AgentHelperCode = EmbeddedResourceReader.GetEmbeddedResourceContent(hostAssembly, "AgentHelper.cs");

            PythonHelperCode["PythonHost.cs"] = EmbeddedResourceReader.GetEmbeddedResourceContent(hostAssembly, "PythonHost.cs");
            PythonHelperCode["PythonModuleLoader.cs"] = EmbeddedResourceReader.GetEmbeddedResourceContent(hostAssembly, "PythonModuleLoader.cs");

            // Auto-discover plugin agents (ImageGen, STT, etc.)
            PluginAgents = DiscoverPluginAgents();

            AdditionalDlls.Add(ResolveAppDll("Neo.Agents.Core.dll"));
            foreach (var plugin in PluginAgents)
            {
                AdditionalDlls.Add(ResolveAppDll(plugin.AgentDllName));

                // Ensure default model is in settings
                if (!Settings.PluginAgentModels.ContainsKey(plugin.SettingsKey))
                    Settings.PluginAgentModels[plugin.SettingsKey] = plugin.DefaultModel;
            }
            AdditionalDlls.Add(ResolveAppDll(GetAIQueryAgentDll(Settings.AIQueryProvider)));
            DefaultNugets.Add(GetAIQueryNuGetPackage(Settings.AIQueryProvider));

            // Note: Avalonia DLLs are NOT loaded from NuGet — Neo.App.Avalonia
            // registers them from its own bin directory (RegisterHostAvaloniaDlls).

            if (Settings.UsePython)
                DefaultNugets.Add("pythonnet|default");

            var systemMessage = AISystemMessages.GetSystemMessage(Settings.UseAvalonia, Settings.UseReactUi, Settings.UsePython, PluginAgents);

            var openAiKey = GetEnvVar("OPENAI_API_KEY");
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

            var anthropicKey = GetEnvVar("ANTHROPIC_API_KEY");
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

            var geminiKey = GetEnvVar("GEMINI_API_KEY");
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
                _view.AppName,
                "NuGetPackages"
            );

            //PythonModulesDirectory = Path.Combine(
            //    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            //    _view.AppName,
            //    "PythonModules"
            //);

            DllPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                _view.AppName,
                "DynamicUserControl.dll"
            );

            AppImportService = new AppImportService();
            AppExportService = new AppExportService(_coreRefPath, _desktopRefPath, _platformServices);
            AppInstallerService = new AppInstallerService(AppExportService, _platformServices);
            CompilationService = new CompilationService(_coreRefPath, _desktopRefPath);
            NugetPackageService = new NuGetPackageService(NuGetPackageDirectory, _coreRefPath, _desktopRefPath);
            ChildProcessService = _view.CreateChildProcessService();
            ChildProcessService.ChildProcessCrashed += HandleChildProcessCrashed;
            ChildProcessService.ChildLogReceived += (log) => Debug.WriteLine($"[Child Log] {log.Message}");
            ChildProcessService.DesignerSelectionReceived += HandleDesignerSelectionReceived;

            RecreatePromptToDllSession(preserveState: false);

            _undoRedo = new UndoRedoManager(AppState);
        }

        /// <summary>
        /// Resolves a DLL filename to its full path in the application's base directory.
        /// Handles relative paths (./name.dll) and bare filenames (name.dll).
        /// </summary>
        private static string ResolveAppDll(string dllName)
        {
            // Strip leading ./ if present
            if (dllName.StartsWith("./") || dllName.StartsWith(".\\"))
                dllName = dllName.Substring(2);

            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            return Path.Combine(baseDir, dllName);
        }

        public void RequestCancellation()
        {
            _cancellationSource.Cancel();
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
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), _view.AppName, "shared"); // AppName muss in den Controller
                GrantedFolders.Add(path);
            }
        }

        private string GetUserControlBaseCode()
        {
            if (!Settings.UseAvalonia)
                return UserControlBaseCode.BaseCode;
            else
                return UserControlBaseCodeAvalonia.BaseCode;
        }

        /// <summary>
        /// Returns the current source code from the virtual project files,
        /// falling back to AppState.LastCode or the base template.
        /// </summary>
        public string? GetCurrentCode()
        {
            return VirtualProjectFiles?.GetFileContent("./currentcode.cs")
                ?? AppState.LastCode;
        }

        /// <summary>
        /// Cross-platform environment variable lookup.
        /// On Windows: tries User-level (registry) first, falls back to Process.
        /// On Linux/macOS: reads from Process (shell environment).
        /// </summary>
        public static string? GetEnvVar(string name)
        {
            if (OperatingSystem.IsWindows())
            {
                var value = Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User);
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }
            return Environment.GetEnvironmentVariable(name);
        }
    }
}
