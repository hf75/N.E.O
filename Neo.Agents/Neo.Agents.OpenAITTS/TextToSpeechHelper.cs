using System;
using System.Threading;
using System.Threading.Tasks;

using Neo.Agents;
using Neo.Agents.Core;

namespace Neo.App
{
    public static class AITextToSpeech
    {
        static OpenAITTSAgent _ttsAgent = new OpenAITTSAgent();

        /// <summary>
        /// Synthesizes text to audio bytes using the OpenAI TTS API.
        /// Returns the complete audio data.
        /// </summary>
        public static async Task<byte[]> SynthesizeAsync(
            string text,
            string voice = "alloy",
            string responseFormat = "mp3",
            double speed = 1.0,
            string? instructions = null,
            CancellationToken cancellationToken = default)
        {
            _ttsAgent.SetOption("ApiKey", Environment.GetEnvironmentVariable("OPENAI_API_KEY", EnvironmentVariableTarget.Process));
            _ttsAgent.SetOption("Model", "TTS_MODEL_PLACEHOLDER");

            _ttsAgent.SetInput("Text", text);
            _ttsAgent.SetInput("Voice", voice);
            _ttsAgent.SetInput("ResponseFormat", responseFormat);
            _ttsAgent.SetInput("Speed", speed);

            if (!string.IsNullOrEmpty(instructions))
                _ttsAgent.SetInput("Instructions", instructions);

            await _ttsAgent.ExecuteAsync(cancellationToken);

            return _ttsAgent.GetOutput<byte[]>("AudioBytes");
        }

        /// <summary>
        /// Synthesizes text to audio with streaming chunks for real-time playback.
        /// The onAudioChunk callback receives each audio chunk as it arrives.
        /// Returns the final complete audio bytes.
        /// </summary>
        public static async Task<byte[]> SynthesizeStreamingAsync(
            string text,
            Action<byte[]> onAudioChunk,
            string voice = "alloy",
            string responseFormat = "mp3",
            double speed = 1.0,
            string? instructions = null,
            CancellationToken cancellationToken = default)
        {
            _ttsAgent.SetOption("ApiKey", Environment.GetEnvironmentVariable("OPENAI_API_KEY", EnvironmentVariableTarget.Process));
            _ttsAgent.SetOption("Model", "TTS_MODEL_PLACEHOLDER");

            _ttsAgent.SetInput("Text", text);
            _ttsAgent.SetInput("Voice", voice);
            _ttsAgent.SetInput("ResponseFormat", responseFormat);
            _ttsAgent.SetInput("Speed", speed);

            if (!string.IsNullOrEmpty(instructions))
                _ttsAgent.SetInput("Instructions", instructions);

            await foreach (var chunk in _ttsAgent.ExecuteStreamingAsync(cancellationToken))
            {
                if (chunk.Name == "AudioBytes" && !chunk.IsFinal)
                    onAudioChunk(chunk.GetValue<byte[]>());
            }

            return _ttsAgent.GetOutput<byte[]>("AudioBytes");
        }
    }
}
