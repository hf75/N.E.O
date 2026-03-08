using System;
using System.Threading;
using System.Threading.Tasks;

using Neo.Agents;

namespace Neo.App
{
    public static class AIImageGen
    {
        static GeminiImageGenAgent _imageAgent = new GeminiImageGenAgent();

        /// <summary>
        /// Generates an image from a text prompt using the Gemini API.
        /// Returns the image as a byte array (PNG).
        /// </summary>
        public static async Task<byte[]> GenerateImageAsync(
            string prompt,
            string? aspectRatio = null,
            string? systemInstruction = null,
            CancellationToken cancellationToken = default)
        {
            _imageAgent.SetOption("ApiKey", Environment.GetEnvironmentVariable("GEMINI_API_KEY", EnvironmentVariableTarget.Process));
            _imageAgent.SetOption("Model", "gemini-3.1-flash-image-preview");

            _imageAgent.SetInput("Prompt", prompt);

            if (!string.IsNullOrEmpty(aspectRatio))
                _imageAgent.SetInput("AspectRatio", aspectRatio);

            if (!string.IsNullOrEmpty(systemInstruction))
                _imageAgent.SetInput("SystemInstruction", systemInstruction);

            await _imageAgent.ExecuteAsync(cancellationToken);

            return _imageAgent.GetOutput<byte[]>("ImageBytes");
        }

        /// <summary>
        /// Edits a reference image based on a text prompt using the Gemini API.
        /// Returns the modified image as a byte array (PNG).
        /// </summary>
        public static async Task<byte[]> EditImageAsync(
            string prompt,
            byte[] referenceImage,
            string referenceImageMimeType = "image/png",
            string? aspectRatio = null,
            string? systemInstruction = null,
            CancellationToken cancellationToken = default)
        {
            _imageAgent.SetOption("ApiKey", Environment.GetEnvironmentVariable("GEMINI_API_KEY", EnvironmentVariableTarget.Process));
            _imageAgent.SetOption("Model", "gemini-3.1-flash-image-preview");

            _imageAgent.SetInput("Prompt", prompt);
            _imageAgent.SetInput("ReferenceImage", referenceImage);
            _imageAgent.SetInput("ReferenceImageMimeType", referenceImageMimeType);

            if (!string.IsNullOrEmpty(aspectRatio))
                _imageAgent.SetInput("AspectRatio", aspectRatio);

            if (!string.IsNullOrEmpty(systemInstruction))
                _imageAgent.SetInput("SystemInstruction", systemInstruction);

            await _imageAgent.ExecuteAsync(cancellationToken);

            return _imageAgent.GetOutput<byte[]>("ImageBytes");
        }
    }
}
