# Settings and Configuration

Open Settings via the **gear icon** in the toolbar.

## AI Provider Setup

### Cloud Providers (API Keys)

Set API keys as Windows user environment variables:

| Variable | Provider | Get a Key |
|----------|----------|-----------|
| `ANTHROPIC_API_KEY` | Anthropic Claude | [console.anthropic.com](https://console.anthropic.com/settings/keys) |
| `OPENAI_API_KEY` | OpenAI | [platform.openai.com](https://platform.openai.com/api-keys) |
| `GEMINI_API_KEY` | Google Gemini | [aistudio.google.com](https://aistudio.google.com/apikey) |

Keys are stored securely in Windows user environment variables — never in the project or settings file.

### Local Model Servers (No Key Required)

| Server | Default Endpoint | Download |
|--------|-----------------|----------|
| Ollama | `http://localhost:11434/v1/` | [ollama.com](https://ollama.com) |
| LM Studio | `http://localhost:1234/v1/` | [lmstudio.ai](https://lmstudio.ai) |

Endpoints can be customized in Settings.

## Settings Options

### General

| Setting | Default | Description |
|---------|---------|-------------|
| AI code generation attempts | 5 | Max retry attempts if compilation fails |
| Accept changes automatically | Off | Skip patch review and auto-apply AI changes |

### UI Framework

| Setting | Default | Description |
|---------|---------|-------------|
| Enable Avalonia | Off | Use Avalonia instead of WPF (enables cross-platform export) |
| Enable REACT-UI | Off | Use React with WebView2 (mutually exclusive with Avalonia) |
| Enable Python | Off | Enable Python 3.11 integration via pythonnet |

### AI Model Selection

Each provider shows a model dropdown (only visible if the API key is set):

- **Claude Model**: e.g., `claude-sonnet-4-20250514`
- **OpenAI Model**: e.g., `gpt-4o`
- **Gemini Model**: e.g., `gemini-2.5-pro-preview-06-05`
- **Ollama Model**: e.g., `llama3.1:latest`
- **LM Studio Model**: (depends on loaded model)

Models are fetched dynamically from each provider's API.

### AI Query Provider (Embedded)

Choose which AI provider is used for `AIQuery` calls inside your generated apps. This lets your created applications make their own AI requests.

## Settings Storage

Settings are saved to:

```
%LOCALAPPDATA%\Neo\settings.json
```

Use the **Reset** button to restore all defaults.
