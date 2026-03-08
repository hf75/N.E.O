using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Neo.Agents.Core;

namespace Neo.Agents
{
    /// <summary>
    /// Speech-to-Text agent using the OpenAI Whisper / GPT-4o-transcribe API.
    /// Extends StreamingAgentBase to yield partial transcription chunks via SSE
    /// when supported by the model (gpt-4o-transcribe, gpt-4o-mini-transcribe).
    /// Falls back to batch transcription for models without streaming (whisper-1).
    /// </summary>
    public class OpenAIWhisperAgent : StreamingAgentBase
    {
        private const string TranscriptionsUrl = "https://api.openai.com/v1/audio/transcriptions";

        public override string Name => "OpenAIWhisperAgent";

        protected override AgentMetadata CreateMetadata()
        {
            var metadata = new AgentMetadata
            {
                Name = Name,
                Description = "Transcribes audio to text using the OpenAI Whisper API. " +
                              "Supports streaming partial results with gpt-4o-transcribe models."
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
                defaultValue: "gpt-4o-mini-transcribe",
                description: "Transcription model. Supported: 'whisper-1', 'gpt-4o-transcribe', 'gpt-4o-mini-transcribe'."
            ));

            metadata.Options.Add(new Option<int>(
                name: "TimeoutSeconds",
                isRequired: false,
                defaultValue: 120,
                description: "HTTP request timeout in seconds."
            ));

            // --- Inputs ---

            metadata.InputParameters.Add(new InputParameter<byte[]>(
                name: "AudioData",
                isRequired: true,
                description: "Audio file content as byte array. Supported formats: mp3, mp4, mpeg, mpga, m4a, wav, webm. Max 25 MB."
            ));

            metadata.InputParameters.Add(new InputParameter<string>(
                name: "AudioFileName",
                isRequired: false,
                description: "File name with extension for MIME type detection (e.g. 'recording.wav'). Default: 'audio.wav'."
            ));

            metadata.InputParameters.Add(new InputParameter<string>(
                name: "Language",
                isRequired: false,
                description: "ISO-639-1 language code (e.g. 'en', 'de', 'fr'). If omitted, the language is auto-detected."
            ));

            metadata.InputParameters.Add(new InputParameter<string>(
                name: "Prompt",
                isRequired: false,
                description: "Optional context hint to guide the transcription style or vocabulary."
            ));

            metadata.InputParameters.Add(new InputParameter<double>(
                name: "Temperature",
                isRequired: false,
                description: "Sampling temperature (0.0 - 1.0). Lower values are more deterministic. Default: 0.0."
            ));

            // --- Outputs ---

            metadata.OutputParameters.Add(new OutputParameter<string>(
                name: "TranscribedText",
                isAlwaysProvided: true,
                description: "The full transcribed text."
            ));

            metadata.OutputParameters.Add(new OutputParameter<string>(
                name: "DetectedLanguage",
                isAlwaysProvided: false,
                description: "ISO-639-1 code of the detected language (when available)."
            ));

            return metadata;
        }

        public override void ValidateOptionsAndInputs()
        {
            if (string.IsNullOrWhiteSpace(GetOption<string>("ApiKey")))
                throw new ArgumentException("Option 'ApiKey' must not be empty.");

            var model = GetOption<string>("Model");
            if (string.IsNullOrWhiteSpace(model))
                throw new ArgumentException("Option 'Model' must not be empty.");

            var audioData = GetInput<byte[]>("AudioData");
            if (audioData == null || audioData.Length == 0)
                throw new ArgumentException("Input 'AudioData' must not be empty.");

            if (audioData.Length > 25 * 1024 * 1024)
                throw new ArgumentException("Input 'AudioData' exceeds the 25 MB limit.");

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
            var audioData = GetInput<byte[]>("AudioData");
            var audioFileName = GetInput<string>("AudioFileName");
            var language = GetInput<string>("Language");
            var prompt = GetInput<string>("Prompt");
            var temperature = GetInput<double>("Temperature");

            if (string.IsNullOrWhiteSpace(audioFileName))
                audioFileName = "audio.wav";

            var timeout = timeoutSeconds == 0
                ? Timeout.InfiniteTimeSpan
                : TimeSpan.FromSeconds(timeoutSeconds);

            using var httpClient = new HttpClient { Timeout = timeout };
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            // Determine if the model supports streaming
            bool supportsStreaming = model.Contains("gpt-4o", StringComparison.OrdinalIgnoreCase);

            if (supportsStreaming)
            {
                await foreach (var chunk in ExecuteStreamingTranscriptionAsync(
                    httpClient, model, audioData, audioFileName, language, prompt, temperature, cancellationToken))
                {
                    yield return chunk;
                }
            }
            else
            {
                await foreach (var chunk in ExecuteBatchTranscriptionAsync(
                    httpClient, model, audioData, audioFileName, language, prompt, temperature, cancellationToken))
                {
                    yield return chunk;
                }
            }
        }

        /// <summary>
        /// Streaming transcription via SSE (Server-Sent Events).
        /// Supported by gpt-4o-transcribe and gpt-4o-mini-transcribe.
        /// </summary>
        private async IAsyncEnumerable<StreamChunk> ExecuteStreamingTranscriptionAsync(
            HttpClient httpClient,
            string model,
            byte[] audioData,
            string audioFileName,
            string? language,
            string? prompt,
            double temperature,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            using var content = BuildMultipartContent(model, audioData, audioFileName, language, prompt, temperature);

            // Enable streaming
            content.Add(new StringContent("true"), "stream");

            using var request = new HttpRequestMessage(HttpMethod.Post, TranscriptionsUrl) { Content = content };

            using var response = await httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new HttpRequestException(
                    $"OpenAI Whisper API error ({response.StatusCode}): {Truncate(errorBody, 500)}");
            }

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream);

            var fullText = "";
            string? detectedLanguage = null;

            while (!reader.EndOfStream)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var line = await reader.ReadLineAsync(cancellationToken);

                if (string.IsNullOrWhiteSpace(line))
                    continue;

                if (!line.StartsWith("data: "))
                    continue;

                var data = line.Substring("data: ".Length);

                if (data == "[DONE]")
                    break;

                SseEvent? sseEvent;
                try
                {
                    sseEvent = JsonSerializer.Deserialize<SseEvent>(data);
                }
                catch
                {
                    continue;
                }

                if (sseEvent == null)
                    continue;

                switch (sseEvent.Type)
                {
                    case "transcript.text.delta":
                        if (!string.IsNullOrEmpty(sseEvent.Delta))
                        {
                            fullText += sseEvent.Delta;
                            yield return Chunk("TranscribedText", fullText);
                        }
                        break;

                    case "transcript.text.done":
                        if (!string.IsNullOrEmpty(sseEvent.Text))
                            fullText = sseEvent.Text;
                        break;

                    case "transcript.language":
                        detectedLanguage = sseEvent.Language;
                        break;
                }
            }

            SetOutput("TranscribedText", fullText);
            if (!string.IsNullOrEmpty(detectedLanguage))
                SetOutput("DetectedLanguage", detectedLanguage);

            yield return FinalChunk("TranscribedText", fullText);
        }

        /// <summary>
        /// Batch transcription (single request/response).
        /// Used for whisper-1 and other models without streaming support.
        /// </summary>
        private async IAsyncEnumerable<StreamChunk> ExecuteBatchTranscriptionAsync(
            HttpClient httpClient,
            string model,
            byte[] audioData,
            string audioFileName,
            string? language,
            string? prompt,
            double temperature,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            using var content = BuildMultipartContent(model, audioData, audioFileName, language, prompt, temperature);

            // Request verbose JSON for language detection
            content.Add(new StringContent("verbose_json"), "response_format");

            var response = await httpClient.PostAsync(TranscriptionsUrl, content, cancellationToken);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"OpenAI Whisper API error ({response.StatusCode}): {Truncate(responseBody, 500)}");
            }

            var result = JsonSerializer.Deserialize<WhisperResponse>(responseBody);
            var text = result?.Text ?? "";
            var detectedLanguage = result?.Language;

            SetOutput("TranscribedText", text);
            if (!string.IsNullOrEmpty(detectedLanguage))
                SetOutput("DetectedLanguage", detectedLanguage);

            yield return FinalChunk("TranscribedText", text);
        }

        private static MultipartFormDataContent BuildMultipartContent(
            string model,
            byte[] audioData,
            string audioFileName,
            string? language,
            string? prompt,
            double temperature)
        {
            var content = new MultipartFormDataContent();

            var audioContent = new ByteArrayContent(audioData);
            audioContent.Headers.ContentType = new MediaTypeHeaderValue(GetMimeType(audioFileName));
            content.Add(audioContent, "file", audioFileName);

            content.Add(new StringContent(model), "model");

            if (!string.IsNullOrEmpty(language))
                content.Add(new StringContent(language), "language");

            if (!string.IsNullOrEmpty(prompt))
                content.Add(new StringContent(prompt), "prompt");

            if (temperature > 0)
                content.Add(new StringContent(temperature.ToString("F1")), "temperature");

            return content;
        }

        private static string GetMimeType(string fileName)
        {
            var ext = Path.GetExtension(fileName).ToLowerInvariant();
            return ext switch
            {
                ".mp3" => "audio/mpeg",
                ".mp4" => "audio/mp4",
                ".mpeg" => "audio/mpeg",
                ".mpga" => "audio/mpeg",
                ".m4a" => "audio/mp4",
                ".wav" => "audio/wav",
                ".webm" => "audio/webm",
                ".ogg" => "audio/ogg",
                ".flac" => "audio/flac",
                _ => "application/octet-stream"
            };
        }

        private static string Truncate(string value, int maxLength) =>
            value.Length <= maxLength ? value : value[..maxLength] + "...";

        #region OpenAI Whisper REST API DTOs

        private class WhisperResponse
        {
            [JsonPropertyName("text")]
            public string? Text { get; set; }

            [JsonPropertyName("language")]
            public string? Language { get; set; }

            [JsonPropertyName("duration")]
            public double? Duration { get; set; }
        }

        private class SseEvent
        {
            [JsonPropertyName("type")]
            public string? Type { get; set; }

            [JsonPropertyName("delta")]
            public string? Delta { get; set; }

            [JsonPropertyName("text")]
            public string? Text { get; set; }

            [JsonPropertyName("language")]
            public string? Language { get; set; }
        }

        #endregion
    }
}
