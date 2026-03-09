using System.Diagnostics;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Neo.Agents.Core;

namespace Neo.Agents
{
    /// <summary>
    /// Text-to-Speech agent using the OpenAI TTS API.
    /// Extends StreamingAgentBase to yield audio byte chunks as they arrive.
    /// Supports tts-1, tts-1-hd, and gpt-4o-mini-tts models.
    /// </summary>
    public class OpenAITTSAgent : StreamingAgentBase, IAppIntegratedAgent
    {
        private const string SpeechUrl = "https://api.openai.com/v1/audio/speech";
        private const int StreamBufferSize = 8192;

        private static readonly string[] AllowedVoices =
            { "alloy", "ash", "ballad", "coral", "echo", "fable", "nova", "onyx", "sage", "shimmer" };

        private static readonly string[] AllowedFormats =
            { "mp3", "opus", "aac", "flac", "wav", "pcm" };

        public override string Name => "OpenAITTSAgent";

        protected override AgentMetadata CreateMetadata()
        {
            var metadata = new AgentMetadata
            {
                Name = Name,
                Description = "Converts text to speech using the OpenAI TTS API. " +
                              "Supports streaming audio byte chunks for real-time playback."
            };

            // --- Options ---

            metadata.Options.Add(new Option<string>(
                name: "ApiKey",
                isRequired: true,
                defaultValue: null!,
                description: "OpenAI API key."
            ));

            metadata.Options.Add(new Option<string>(
                name: "Model",
                isRequired: false,
                defaultValue: "gpt-4o-mini-tts",
                description: "TTS model. Supported: 'tts-1', 'tts-1-hd', 'gpt-4o-mini-tts'."
            ));

            metadata.Options.Add(new Option<int>(
                name: "TimeoutSeconds",
                isRequired: false,
                defaultValue: 120,
                description: "HTTP request timeout in seconds."
            ));

            // --- Inputs ---

            metadata.InputParameters.Add(new InputParameter<string>(
                name: "Text",
                isRequired: true,
                description: "Text to synthesize into speech. Maximum 4096 characters."
            ));

            metadata.InputParameters.Add(new InputParameter<string>(
                name: "Voice",
                isRequired: false,
                description: "Voice to use. Supported: alloy, ash, ballad, coral, echo, fable, nova, onyx, sage, shimmer. Default: alloy."
            ));

            metadata.InputParameters.Add(new InputParameter<string>(
                name: "ResponseFormat",
                isRequired: false,
                description: "Audio output format. Supported: mp3, opus, aac, flac, wav, pcm. Default: mp3."
            ));

            metadata.InputParameters.Add(new InputParameter<double>(
                name: "Speed",
                isRequired: false,
                description: "Playback speed (0.25 to 4.0). Default: 1.0."
            ));

            metadata.InputParameters.Add(new InputParameter<string>(
                name: "Instructions",
                isRequired: false,
                description: "Style/tone instructions for the voice (gpt-4o-mini-tts only). " +
                             "E.g. 'Speak in a cheerful and friendly tone'."
            ));

            // --- Outputs ---

            metadata.OutputParameters.Add(new OutputParameter<byte[]>(
                name: "AudioBytes",
                isAlwaysProvided: true,
                description: "The complete synthesized audio as raw bytes."
            ));

            metadata.OutputParameters.Add(new OutputParameter<string>(
                name: "MimeType",
                isAlwaysProvided: true,
                description: "MIME type of the audio (e.g. 'audio/mpeg')."
            ));

            return metadata;
        }

        public override void ValidateOptionsAndInputs()
        {
            if (string.IsNullOrWhiteSpace(GetOption<string>("ApiKey")))
                throw new ArgumentException("Option 'ApiKey' must not be empty.");

            var text = GetInput<string>("Text");
            if (string.IsNullOrWhiteSpace(text))
                throw new ArgumentException("Input 'Text' must not be empty.");

            if (text.Length > 4096)
                throw new ArgumentException("Input 'Text' exceeds the 4096 character limit.");

            var voice = GetInput<string>("Voice");
            if (!string.IsNullOrEmpty(voice) && !AllowedVoices.Contains(voice, StringComparer.OrdinalIgnoreCase))
                throw new ArgumentException(
                    $"Voice '{voice}' is not supported. Allowed: {string.Join(", ", AllowedVoices)}");

            var format = GetInput<string>("ResponseFormat");
            if (!string.IsNullOrEmpty(format) && !AllowedFormats.Contains(format, StringComparer.OrdinalIgnoreCase))
                throw new ArgumentException(
                    $"Format '{format}' is not supported. Allowed: {string.Join(", ", AllowedFormats)}");

            var speed = GetInput<double>("Speed");
            if (speed != 0 && (speed < 0.25 || speed > 4.0))
                throw new ArgumentException("Input 'Speed' must be between 0.25 and 4.0.");

            var timeout = GetOption<int>("TimeoutSeconds");
            if (timeout < 0)
                throw new ArgumentException("Option 'TimeoutSeconds' must not be negative.");
        }

        protected override async IAsyncEnumerable<StreamChunk> ExecuteStreamingCoreAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var apiKey = GetOption<string>("ApiKey");
            var model = GetOption<string>("Model");
            var timeoutSeconds = GetOption<int>("TimeoutSeconds");
            var text = GetInput<string>("Text");
            var voice = GetInput<string>("Voice");
            var responseFormat = GetInput<string>("ResponseFormat");
            var speed = GetInput<double>("Speed");
            var instructions = GetInput<string>("Instructions");

            if (string.IsNullOrWhiteSpace(voice)) voice = "alloy";
            if (string.IsNullOrWhiteSpace(responseFormat)) responseFormat = "mp3";
            if (speed == 0) speed = 1.0;

            var timeout = timeoutSeconds == 0
                ? Timeout.InfiniteTimeSpan
                : TimeSpan.FromSeconds(timeoutSeconds);

            using var httpClient = new HttpClient { Timeout = timeout };
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            // Build JSON request body
            var requestBody = new TtsRequest
            {
                Model = model,
                Input = text,
                Voice = voice,
                ResponseFormat = responseFormat,
                Speed = speed
            };

            // Instructions only supported by gpt-4o-mini-tts
            if (!string.IsNullOrWhiteSpace(instructions) &&
                model.Contains("gpt-4o", StringComparison.OrdinalIgnoreCase))
            {
                requestBody.Instructions = instructions;
            }

            var jsonContent = new StringContent(
                JsonSerializer.Serialize(requestBody),
                Encoding.UTF8,
                "application/json");

            using var request = new HttpRequestMessage(HttpMethod.Post, SpeechUrl) { Content = jsonContent };

            // Stream the response for real-time audio delivery
            using var response = await httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new HttpRequestException(
                    $"OpenAI TTS API error ({response.StatusCode}): {Truncate(errorBody, 500)}");
            }

            var mimeType = GetMimeType(responseFormat);

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var memoryStream = new MemoryStream();
            var buffer = new byte[StreamBufferSize];
            int bytesRead;

            while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var chunk = new byte[bytesRead];
                Buffer.BlockCopy(buffer, 0, chunk, 0, bytesRead);
                memoryStream.Write(chunk, 0, bytesRead);

                yield return Chunk("AudioBytes", chunk);
            }

            var allBytes = memoryStream.ToArray();
            SetOutput("AudioBytes", allBytes);
            SetOutput("MimeType", mimeType);

            yield return FinalChunk("AudioBytes", allBytes);
        }

        private static string GetMimeType(string format) => format.ToLowerInvariant() switch
        {
            "mp3" => "audio/mpeg",
            "opus" => "audio/opus",
            "aac" => "audio/aac",
            "flac" => "audio/flac",
            "wav" => "audio/wav",
            "pcm" => "audio/L16",
            _ => "application/octet-stream"
        };

        private static string Truncate(string value, int maxLength) =>
            value.Length <= maxLength ? value : value[..maxLength] + "...";

        // ── IAppIntegratedAgent ────────────────────────────────────────

        public string DisplayName => "Text-to-Speech";
        public string SettingsKey => "TextToSpeech";
        public string? RequiredEnvVar => "OPENAI_API_KEY";
        public string DefaultModel => "gpt-4o-mini-tts";

        public string? HelperTemplateCode =>
            AgentResourceLoader.LoadEmbeddedResource(typeof(OpenAITTSAgent), "TextToSpeechHelper.cs");

        public IReadOnlyDictionary<string, string> TemplatePlaceholders { get; } =
            new Dictionary<string, string> { ["TTS_MODEL_PLACEHOLDER"] = "TextToSpeech" };

        public string AgentDllName => "Neo.Agents.OpenAITTS.dll";

        public string? SystemMessageDocs =>
            @"You also have access to AI text-to-speech synthesis in the 'Neo.App' namespace:

            // Synthesize text to audio bytes (batch mode). Returns the complete audio data.
            // voice: 'alloy', 'ash', 'ballad', 'coral', 'echo', 'fable', 'nova', 'onyx', 'sage', 'shimmer'
            // responseFormat: 'mp3' (default), 'opus', 'aac', 'flac', 'wav', 'pcm'
            // speed: 0.25 to 4.0 (default 1.0)
            // instructions: style/tone instructions (optional, e.g. 'Speak cheerfully')
            public static async Task<byte[]> AITextToSpeech.SynthesizeAsync(string text, string voice = ""alloy"", string responseFormat = ""mp3"", double speed = 1.0, string? instructions = null, CancellationToken cancellationToken = default)

            // Synthesize text with streaming audio chunks for real-time playback.
            // The onAudioChunk callback receives each audio chunk as it arrives.
            // Returns the final complete audio bytes.
            public static async Task<byte[]> AITextToSpeech.SynthesizeStreamingAsync(string text, Action<byte[]> onAudioChunk, string voice = ""alloy"", string responseFormat = ""mp3"", double speed = 1.0, string? instructions = null, CancellationToken cancellationToken = default)

            When the user asks to speak, read aloud, play audio of text, or use text-to-speech, ALWAYS use AITextToSpeech.
            For simple playback of the returned audio bytes, use System.Media.SoundPlayer with a MemoryStream (WAV format)
            or save to a temp file and use System.Diagnostics.Process.Start to play.
            For advanced audio playback (streaming, MP3), use NAudio (add NuGet package NAudio|default).
            Use SynthesizeStreamingAsync with NAudio's BufferedWaveProvider for real-time playback while audio is still being generated.
            Never use web-based TTS services or browser APIs — always use AITextToSpeech.
            ";

        public Task<List<string>> FetchAvailableModelsAsync(string? apiKeyOrEndpoint)
        {
            return Task.FromResult(new List<string>
            {
                "gpt-4o-mini-tts",
                "tts-1-hd",
                "tts-1",
            });
        }

        #region OpenAI TTS REST API DTOs

        private class TtsRequest
        {
            [JsonPropertyName("model")]
            public string Model { get; set; } = "gpt-4o-mini-tts";

            [JsonPropertyName("input")]
            public string Input { get; set; } = string.Empty;

            [JsonPropertyName("voice")]
            public string Voice { get; set; } = "alloy";

            [JsonPropertyName("response_format")]
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public string? ResponseFormat { get; set; }

            [JsonPropertyName("speed")]
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
            public double Speed { get; set; } = 1.0;

            [JsonPropertyName("instructions")]
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public string? Instructions { get; set; }
        }

        #endregion
    }
}
