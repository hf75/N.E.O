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
    public partial class AppController
    {
        private static List<IAppIntegratedAgent> DiscoverPluginAgents()
        {
            // .NET loads assemblies lazily — agent assemblies may not be in the AppDomain yet.
            // Force-load all Neo.Agents.*.dll from the application directory before scanning.
            var appDir = AppDomain.CurrentDomain.BaseDirectory;
            foreach (var dllPath in Directory.GetFiles(appDir, "Neo.Agents.*.dll"))
            {
                var asmName = Path.GetFileNameWithoutExtension(dllPath);
                if (AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == asmName))
                    continue;

                try { Assembly.LoadFrom(dllPath); }
                catch { /* Ignore unloadable assemblies */ }
            }

            var agents = new List<IAppIntegratedAgent>();

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    foreach (var type in asm.GetTypes())
                    {
                        if (!typeof(IAppIntegratedAgent).IsAssignableFrom(type) ||
                            type.IsAbstract || type.IsInterface)
                            continue;

                        if (Activator.CreateInstance(type) is IAppIntegratedAgent plugin)
                        {
                            if (plugin.RequiredEnvVar != null)
                            {
                                var key = GetEnvVar(plugin.RequiredEnvVar);
                                if (string.IsNullOrWhiteSpace(key))
                                    continue;
                            }

                            agents.Add(plugin);
                        }
                    }
                }
                catch { /* Assembly cannot be scanned — skip */ }
            }

            Debug.WriteLine($"[AppController] Discovered {agents.Count} plugin agent(s): {string.Join(", ", agents.Select(a => a.DisplayName))}");
            return agents;
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
            "Gemini" => "Google_GenerativeAI|3.6.1",
            _ => "",  // Claude, OpenAI, Ollama, LM Studio use pure REST — no NuGet SDK needed
        };

        private static string GetAIQueryAgentDll(string provider) => provider switch
        {
            "OpenAI" => "Neo.Agents.OpenAI.dll",
            "Gemini" => "Neo.Agents.Gemini.dll",
            "Ollama" => "Neo.Agents.Ollama.dll",
            "LM Studio" => "Neo.Agents.LmStudio.dll",
            _ => "Neo.Agents.Claude.dll",
        };

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
            var systemMessage = AISystemMessages.GetSystemMessage(Settings!.UseAvalonia, Settings.UseReactUi, Settings.UsePython, PluginAgents);

            AvailableAgents.Clear();
            Claude = null;
            ChatGPT = null;
            Gemini = null;
            Ollama = null;
            LmStudio = null;

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
    }
}
