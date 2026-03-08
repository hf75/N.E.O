using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Neo.App
{
    public class SettingsModel : INotifyPropertyChanged
    {
        private int _aiCodeGenerationAttempts = 5; // Standard
        private bool _useReactUi = false;
        private bool _usePython = false;
        private bool _useAvalonia = false;
        private bool _acceptAutomatic = false;
        private string _claudeModel = "claude-opus-4-6";
        private string _openAiModel = "gpt-5.2";
        private string _geminiModel = "gemini-3-pro-preview";
        private string _aiQueryProvider = "Claude";
        private string _aiQueryModel = "claude-sonnet-4-5";
        private string _ollamaModel = "llama3.1:latest";
        private string _ollamaEndpoint = "http://localhost:11434/v1/";
        private string _imageGenModel = "gemini-3.1-flash-image-preview";
        private string _lmStudioModel = "";
        private string _lmStudioEndpoint = "http://localhost:1234/v1/";

        public int AiCodeGenerationAttempts
        {
            get { return _aiCodeGenerationAttempts; }
            set
            {
                if (_aiCodeGenerationAttempts != value)
                {
                    _aiCodeGenerationAttempts = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool UseAvalonia
        {
            get { return _useAvalonia; }
            set
            {
                if (_useAvalonia != value)
                {
                    _useAvalonia = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool UseReactUi
        {
            get { return _useReactUi; }
            set
            {
                if (_useReactUi != value)
                {
                    _useReactUi = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool UsePython
        {
            get { return _usePython; }
            set
            {
                if (_usePython != value)
                {
                    _usePython = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool AcceptAutomatic
        {
            get { return _acceptAutomatic; }
            set
            {
                if (_acceptAutomatic != value)
                {
                    _acceptAutomatic = value;
                    OnPropertyChanged();
                }
            }
        }


        public string ClaudeModel
        {
            get { return _claudeModel; }
            set
            {
                if (_claudeModel != value)
                {
                    _claudeModel = value;
                    OnPropertyChanged();
                }
            }
        }

        public string OpenAiModel
        {
            get { return _openAiModel; }
            set
            {
                if (_openAiModel != value)
                {
                    _openAiModel = value;
                    OnPropertyChanged();
                }
            }
        }

        public string GeminiModel
        {
            get { return _geminiModel; }
            set
            {
                if (_geminiModel != value)
                {
                    _geminiModel = value;
                    OnPropertyChanged();
                }
            }
        }

        public string AIQueryProvider
        {
            get { return _aiQueryProvider; }
            set
            {
                if (_aiQueryProvider != value)
                {
                    _aiQueryProvider = value;
                    OnPropertyChanged();
                }
            }
        }

        public string AIQueryModel
        {
            get { return _aiQueryModel; }
            set
            {
                if (_aiQueryModel != value)
                {
                    _aiQueryModel = value;
                    OnPropertyChanged();
                }
            }
        }

        public string OllamaModel
        {
            get { return _ollamaModel; }
            set
            {
                if (_ollamaModel != value)
                {
                    _ollamaModel = value;
                    OnPropertyChanged();
                }
            }
        }

        public string OllamaEndpoint
        {
            get { return _ollamaEndpoint; }
            set
            {
                if (_ollamaEndpoint != value)
                {
                    _ollamaEndpoint = value;
                    OnPropertyChanged();
                }
            }
        }

        public string LmStudioModel
        {
            get { return _lmStudioModel; }
            set
            {
                if (_lmStudioModel != value)
                {
                    _lmStudioModel = value;
                    OnPropertyChanged();
                }
            }
        }

        public string LmStudioEndpoint
        {
            get { return _lmStudioEndpoint; }
            set
            {
                if (_lmStudioEndpoint != value)
                {
                    _lmStudioEndpoint = value;
                    OnPropertyChanged();
                }
            }
        }

        public string ImageGenModel
        {
            get { return _imageGenModel; }
            set
            {
                if (_imageGenModel != value)
                {
                    _imageGenModel = value;
                    OnPropertyChanged();
                }
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? propName = null)
        {
            var handler = PropertyChanged;
            if (handler != null) handler(this, new PropertyChangedEventArgs(propName));
        }
    }

    public static class SettingsService
    {
        // Passe den App-Namen nach Bedarf an
        private const string AppFolderName = "Neo";
        private const string SettingsFileName = "settings.json";

        public static string GetSettingsFolderPath()
        {
            var folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                AppFolderName);

            if (!Directory.Exists(folder))
                Directory.CreateDirectory(folder);

            return folder;
        }

        public static string GetSettingsFilePath()
        {
            return Path.Combine(GetSettingsFolderPath(), SettingsFileName);
        }

        public static SettingsModel Load()
        {
            try
            {
                var file = GetSettingsFilePath();
                if (!File.Exists(file))
                    return new SettingsModel(); // Defaults

                var json = File.ReadAllText(file);
                var model = JsonSerializer.Deserialize<SettingsModel>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                // Falls Datei korrupt oder leer
                if (model == null)
                    return new SettingsModel();

                // Minimal-Validierung: sinnvolle Untergrenze
                if (model.AiCodeGenerationAttempts < 1)
                    model.AiCodeGenerationAttempts = 5;
                return model;
            }
            catch
            {
                // Defensive: bei Fehlern sichere Defaults
                return new SettingsModel();
            }
        }

        public static void Save(SettingsModel model)
        {
            // Minimal-Validierung vor dem Speichern
            if (model.AiCodeGenerationAttempts < 1)
                model.AiCodeGenerationAttempts = 5;
            var json = JsonSerializer.Serialize(model, new JsonSerializerOptions
            {
                WriteIndented = true
            });
            File.WriteAllText(GetSettingsFilePath(), json);
        }
    }
}
