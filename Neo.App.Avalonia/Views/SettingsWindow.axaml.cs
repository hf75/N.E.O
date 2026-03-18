using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;

using Neo.Agents.Core;

namespace Neo.App
{
    public partial class SettingsWindow : Window
    {
        private SettingsModel _workingCopy;

        private readonly string? _anthropicKey;
        private readonly string? _openAiKey;
        private readonly string? _geminiKey;

        private readonly List<string> _availableProviders = new();
        private readonly List<IAppIntegratedAgent> _pluginAgents;
        private readonly Dictionary<string, ComboBox> _pluginComboBoxes = new();

        public SettingsWindow()
        {
            InitializeComponent();

            var loaded = SettingsService.Load();
            _workingCopy = new SettingsModel
            {
                AiCodeGenerationAttempts = loaded.AiCodeGenerationAttempts,
                UseAvalonia = loaded.UseAvalonia,
                UseReactUi = loaded.UseReactUi,
                UsePython = loaded.UsePython,
                AcceptAutomatic = loaded.AcceptAutomatic,
                ClaudeModel = loaded.ClaudeModel,
                OpenAiModel = loaded.OpenAiModel,
                GeminiModel = loaded.GeminiModel,
                AIQueryProvider = loaded.AIQueryProvider,
                AIQueryModel = loaded.AIQueryModel,
                OllamaModel = loaded.OllamaModel,
                OllamaEndpoint = loaded.OllamaEndpoint,
                LmStudioModel = loaded.LmStudioModel,
                LmStudioEndpoint = loaded.LmStudioEndpoint,
                ImageGenModel = loaded.ImageGenModel,
                SpeechToTextModel = loaded.SpeechToTextModel,
                PluginAgentModels = new Dictionary<string, string>(loaded.PluginAgentModels),
            };

            this.DataContext = _workingCopy;
            AttemptsTextBox.Text = _workingCopy.AiCodeGenerationAttempts.ToString();

            _anthropicKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY", EnvironmentVariableTarget.User);
            _openAiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY", EnvironmentVariableTarget.User);
            _geminiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY", EnvironmentVariableTarget.User);

            if (!string.IsNullOrWhiteSpace(_anthropicKey))
            {
                RowClaudeModel.IsVisible = true;
                SetComboBoxSingleItem(ClaudeModelComboBox, _workingCopy.ClaudeModel);
                _availableProviders.Add("Claude");
            }

            if (!string.IsNullOrWhiteSpace(_openAiKey))
            {
                RowOpenAiModel.IsVisible = true;
                SetComboBoxSingleItem(OpenAiModelComboBox, _workingCopy.OpenAiModel);
                _availableProviders.Add("OpenAI");
            }

            if (!string.IsNullOrWhiteSpace(_geminiKey))
            {
                RowGeminiModel.IsVisible = true;
                SetComboBoxSingleItem(GeminiModelComboBox, _workingCopy.GeminiModel);
                _availableProviders.Add("Gemini");
            }

            _pluginAgents = DiscoverPluginAgents();
            BuildPluginAgentUI();

            RowOllamaModel.IsVisible = true;
            RowOllamaEndpoint.IsVisible = true;
            SetComboBoxSingleItem(OllamaModelComboBox, _workingCopy.OllamaModel);
            OllamaEndpointTextBox.Text = _workingCopy.OllamaEndpoint;
            _availableProviders.Add("Ollama");

            RowLmStudioModel.IsVisible = true;
            RowLmStudioEndpoint.IsVisible = true;
            SetComboBoxSingleItem(LmStudioModelComboBox, _workingCopy.LmStudioModel);
            LmStudioEndpointTextBox.Text = _workingCopy.LmStudioEndpoint;
            _availableProviders.Add("LM Studio");

            if (_availableProviders.Count > 0)
            {
                AIQueryProviderComboBox.ItemsSource = _availableProviders;
                var currentProvider = _availableProviders.Contains(_workingCopy.AIQueryProvider)
                    ? _workingCopy.AIQueryProvider
                    : _availableProviders[0];
                AIQueryProviderComboBox.SelectedItem = currentProvider;
                SetComboBoxSingleItem(AIQueryModelComboBox, _workingCopy.AIQueryModel);
            }
            else
            {
                RowAIQueryProvider.IsVisible = false;
                RowAIQueryModel.IsVisible = false;
            }

            this.Opened += SettingsWindow_Opened;
        }

        private static List<IAppIntegratedAgent> DiscoverPluginAgents()
        {
            var appDir = AppDomain.CurrentDomain.BaseDirectory;
            foreach (var dllPath in Directory.GetFiles(appDir, "Neo.Agents.*.dll"))
            {
                var asmName = Path.GetFileNameWithoutExtension(dllPath);
                if (AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == asmName))
                    continue;
                try { Assembly.LoadFrom(dllPath); } catch { }
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
                                var key = Environment.GetEnvironmentVariable(
                                    plugin.RequiredEnvVar, EnvironmentVariableTarget.User);
                                if (string.IsNullOrWhiteSpace(key))
                                    continue;
                            }
                            agents.Add(plugin);
                        }
                    }
                }
                catch { }
            }
            return agents;
        }

        private void BuildPluginAgentUI()
        {
            var panel = new StackPanel();

            foreach (var plugin in _pluginAgents)
            {
                var label = new TextBlock
                {
                    Text = $"{plugin.DisplayName} Model:",
                    Margin = new global::Avalonia.Thickness(0, 4, 0, 4)
                };

                var comboBox = new ComboBox
                {
                    Tag = plugin.SettingsKey
                };
                comboBox.Classes.Add("rounded");

                var currentModel = _workingCopy.PluginAgentModels.TryGetValue(plugin.SettingsKey, out var m)
                    ? m : plugin.DefaultModel;
                SetComboBoxSingleItem(comboBox, currentModel);

                _pluginComboBoxes[plugin.SettingsKey] = comboBox;

                panel.Children.Add(label);
                panel.Children.Add(comboBox);
            }

            PluginAgentModelsPanel.Items.Add(panel);
        }

        private static void SetComboBoxSingleItem(ComboBox comboBox, string item)
        {
            comboBox.ItemsSource = new List<string> { item };
            comboBox.SelectedItem = item;
        }

        private async void SettingsWindow_Opened(object? sender, EventArgs e)
        {
            var tasks = new List<Task>();

            if (!string.IsNullOrWhiteSpace(_anthropicKey))
                tasks.Add(LoadModelsAsync(ClaudeModelComboBox, _workingCopy.ClaudeModel,
                    () => ModelListService.FetchAnthropicModelsAsync(_anthropicKey)));

            if (!string.IsNullOrWhiteSpace(_openAiKey))
                tasks.Add(LoadModelsAsync(OpenAiModelComboBox, _workingCopy.OpenAiModel,
                    () => ModelListService.FetchOpenAiModelsAsync(_openAiKey)));

            if (!string.IsNullOrWhiteSpace(_geminiKey))
                tasks.Add(LoadModelsAsync(GeminiModelComboBox, _workingCopy.GeminiModel,
                    () => ModelListService.FetchGeminiModelsAsync(_geminiKey)));

            tasks.Add(LoadModelsAsync(OllamaModelComboBox, _workingCopy.OllamaModel,
                () => ModelListService.FetchOllamaModelsAsync(_workingCopy.OllamaEndpoint)));
            tasks.Add(LoadModelsAsync(LmStudioModelComboBox, _workingCopy.LmStudioModel,
                () => ModelListService.FetchLmStudioModelsAsync(_workingCopy.LmStudioEndpoint)));

            foreach (var plugin in _pluginAgents)
            {
                if (_pluginComboBoxes.TryGetValue(plugin.SettingsKey, out var comboBox))
                {
                    var currentModel = _workingCopy.PluginAgentModels.TryGetValue(plugin.SettingsKey, out var pm)
                        ? pm : plugin.DefaultModel;
                    var envVal = plugin.RequiredEnvVar != null
                        ? Environment.GetEnvironmentVariable(plugin.RequiredEnvVar, EnvironmentVariableTarget.User)
                        : null;
                    var capturedPlugin = plugin;
                    tasks.Add(LoadModelsAsync(comboBox, currentModel,
                        () => capturedPlugin.FetchAvailableModelsAsync(envVal)));
                }
            }

            tasks.Add(LoadAIQueryModelsAsync());
            await Task.WhenAll(tasks);
        }

        private async Task LoadAIQueryModelsAsync()
        {
            var provider = AIQueryProviderComboBox.SelectedItem as string;
            if (string.IsNullOrWhiteSpace(provider)) return;

            Func<Task<List<string>>>? fetchFunc = provider switch
            {
                "Claude" when !string.IsNullOrWhiteSpace(_anthropicKey)
                    => () => ModelListService.FetchAnthropicModelsAsync(_anthropicKey!),
                "OpenAI" when !string.IsNullOrWhiteSpace(_openAiKey)
                    => () => ModelListService.FetchOpenAiModelsAsync(_openAiKey!),
                "Gemini" when !string.IsNullOrWhiteSpace(_geminiKey)
                    => () => ModelListService.FetchGeminiModelsAsync(_geminiKey!),
                "Ollama" => () => ModelListService.FetchOllamaModelsAsync(_workingCopy.OllamaEndpoint),
                "LM Studio" => () => ModelListService.FetchLmStudioModelsAsync(_workingCopy.LmStudioEndpoint),
                _ => null,
            };

            if (fetchFunc != null)
                await LoadModelsAsync(AIQueryModelComboBox, _workingCopy.AIQueryModel, fetchFunc);
        }

        private static async Task LoadModelsAsync(ComboBox comboBox, string currentModel, Func<Task<List<string>>> fetchFunc)
        {
            try
            {
                var models = await fetchFunc();
                if (models.Count > 0)
                {
                    if (!string.IsNullOrWhiteSpace(currentModel) && !models.Contains(currentModel))
                        models.Insert(0, currentModel);
                    comboBox.ItemsSource = models;
                    comboBox.SelectedItem = currentModel;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SettingsWindow] Failed to load models: {ex.Message}");
            }
        }

        private async void AIQueryProviderComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (AIQueryProviderComboBox.SelectedItem is not string provider) return;

            var defaultModel = provider switch
            {
                "OpenAI" => "gpt-4o",
                "Gemini" => "gemini-2.0-flash",
                "Ollama" => "llama3.1:latest",
                "LM Studio" => "",
                _ => "claude-sonnet-4-5",
            };

            _workingCopy.AIQueryProvider = provider;
            _workingCopy.AIQueryModel = defaultModel;
            SetComboBoxSingleItem(AIQueryModelComboBox, defaultModel);

            await LoadAIQueryModelsAsync();
        }

        private void OkButton_Click(object? sender, RoutedEventArgs e)
        {
            if (int.TryParse(AttemptsTextBox.Text, out var attempts))
                _workingCopy.AiCodeGenerationAttempts = attempts;

            if (_workingCopy.AiCodeGenerationAttempts < 1)
                _workingCopy.AiCodeGenerationAttempts = 5;

            if (ClaudeModelComboBox.SelectedItem is string claudeModel && !string.IsNullOrWhiteSpace(claudeModel))
                _workingCopy.ClaudeModel = claudeModel;
            if (OpenAiModelComboBox.SelectedItem is string openAiModel && !string.IsNullOrWhiteSpace(openAiModel))
                _workingCopy.OpenAiModel = openAiModel;
            if (GeminiModelComboBox.SelectedItem is string geminiModel && !string.IsNullOrWhiteSpace(geminiModel))
                _workingCopy.GeminiModel = geminiModel;

            foreach (var plugin in _pluginAgents)
            {
                if (_pluginComboBoxes.TryGetValue(plugin.SettingsKey, out var comboBox))
                {
                    if (comboBox.SelectedItem is string model && !string.IsNullOrWhiteSpace(model))
                        _workingCopy.PluginAgentModels[plugin.SettingsKey] = model;
                }
            }

            var ollamaModel = OllamaModelComboBox.SelectedItem as string ?? "";
            if (!string.IsNullOrWhiteSpace(ollamaModel))
                _workingCopy.OllamaModel = ollamaModel;
            _workingCopy.OllamaEndpoint = OllamaEndpointTextBox.Text ?? "";

            var lmStudioModel = LmStudioModelComboBox.SelectedItem as string ?? "";
            if (!string.IsNullOrWhiteSpace(lmStudioModel))
                _workingCopy.LmStudioModel = lmStudioModel;
            _workingCopy.LmStudioEndpoint = LmStudioEndpointTextBox.Text ?? "";

            if (AIQueryProviderComboBox.SelectedItem is string aiQueryProvider && !string.IsNullOrWhiteSpace(aiQueryProvider))
                _workingCopy.AIQueryProvider = aiQueryProvider;
            if (AIQueryModelComboBox.SelectedItem is string aiQueryModel && !string.IsNullOrWhiteSpace(aiQueryModel))
                _workingCopy.AIQueryModel = aiQueryModel;

            SettingsService.Save(_workingCopy);
            Close(true);
        }

        private void CancelButton_Click(object? sender, RoutedEventArgs e)
        {
            Close(false);
        }

        private void ResetButton_Click(object? sender, RoutedEventArgs e)
        {
            _workingCopy.AiCodeGenerationAttempts = 5;
            _workingCopy.UseReactUi = false;
            _workingCopy.UsePython = false;
            _workingCopy.UseAvalonia = false;
            _workingCopy.AcceptAutomatic = false;
            _workingCopy.ClaudeModel = "claude-opus-4-6";
            _workingCopy.OpenAiModel = "gpt-5.2";
            _workingCopy.GeminiModel = "gemini-3-pro-preview";
            _workingCopy.OllamaModel = "llama3.1:latest";
            _workingCopy.OllamaEndpoint = "http://localhost:11434/v1/";
            _workingCopy.LmStudioModel = "";
            _workingCopy.LmStudioEndpoint = "http://localhost:1234/v1/";
            _workingCopy.AIQueryProvider = "Claude";
            _workingCopy.AIQueryModel = "claude-sonnet-4-5";

            AttemptsTextBox.Text = _workingCopy.AiCodeGenerationAttempts.ToString();

            SetComboBoxSingleItem(ClaudeModelComboBox, _workingCopy.ClaudeModel);
            SetComboBoxSingleItem(OpenAiModelComboBox, _workingCopy.OpenAiModel);
            SetComboBoxSingleItem(GeminiModelComboBox, _workingCopy.GeminiModel);
            SetComboBoxSingleItem(OllamaModelComboBox, _workingCopy.OllamaModel);
            OllamaEndpointTextBox.Text = _workingCopy.OllamaEndpoint;
            SetComboBoxSingleItem(LmStudioModelComboBox, _workingCopy.LmStudioModel);
            LmStudioEndpointTextBox.Text = _workingCopy.LmStudioEndpoint;

            foreach (var plugin in _pluginAgents)
            {
                _workingCopy.PluginAgentModels[plugin.SettingsKey] = plugin.DefaultModel;
                if (_pluginComboBoxes.TryGetValue(plugin.SettingsKey, out var comboBox))
                    SetComboBoxSingleItem(comboBox, plugin.DefaultModel);
            }

            AIQueryProviderComboBox.SelectedItem = _workingCopy.AIQueryProvider;
            SetComboBoxSingleItem(AIQueryModelComboBox, _workingCopy.AIQueryModel);
        }
    }
}
