using System;
using System.Threading;
using System.Threading.Tasks;

using Neo.Agents;
using Neo.Agents.Core;

namespace Neo.App
{
    public static class AISpeechToText
    {
        static OpenAIWhisperAgent _sttAgent = new OpenAIWhisperAgent();

        /// <summary>
        /// Transcribes audio data to text using the OpenAI Whisper API.
        /// Returns the transcribed text.
        /// </summary>
        public static async Task<string> TranscribeAsync(
            byte[] audioData,
            string audioFileName = "audio.wav",
            string? language = null,
            string? prompt = null,
            CancellationToken cancellationToken = default)
        {
            _sttAgent.SetOption("ApiKey", Environment.GetEnvironmentVariable("OPENAI_API_KEY", EnvironmentVariableTarget.Process));
            _sttAgent.SetOption("Model", "STT_MODEL_PLACEHOLDER");

            _sttAgent.SetInput("AudioData", audioData);
            _sttAgent.SetInput("AudioFileName", audioFileName);

            if (!string.IsNullOrEmpty(language))
                _sttAgent.SetInput("Language", language);

            if (!string.IsNullOrEmpty(prompt))
                _sttAgent.SetInput("Prompt", prompt);

            await _sttAgent.ExecuteAsync(cancellationToken);

            return _sttAgent.GetOutput<string>("TranscribedText");
        }

        /// <summary>
        /// Transcribes audio data to text with streaming partial results.
        /// The onPartialResult callback is invoked with the growing transcription text.
        /// Returns the final transcribed text.
        /// </summary>
        public static async Task<string> TranscribeStreamingAsync(
            byte[] audioData,
            Action<string> onPartialResult,
            string audioFileName = "audio.wav",
            string? language = null,
            string? prompt = null,
            CancellationToken cancellationToken = default)
        {
            _sttAgent.SetOption("ApiKey", Environment.GetEnvironmentVariable("OPENAI_API_KEY", EnvironmentVariableTarget.Process));
            _sttAgent.SetOption("Model", "STT_MODEL_PLACEHOLDER");

            _sttAgent.SetInput("AudioData", audioData);
            _sttAgent.SetInput("AudioFileName", audioFileName);

            if (!string.IsNullOrEmpty(language))
                _sttAgent.SetInput("Language", language);

            if (!string.IsNullOrEmpty(prompt))
                _sttAgent.SetInput("Prompt", prompt);

            await foreach (var chunk in _sttAgent.ExecuteStreamingAsync(cancellationToken))
            {
                if (chunk.Name == "TranscribedText")
                    onPartialResult(chunk.GetValue<string>());
            }

            return _sttAgent.GetOutput<string>("TranscribedText");
        }
    }
}
