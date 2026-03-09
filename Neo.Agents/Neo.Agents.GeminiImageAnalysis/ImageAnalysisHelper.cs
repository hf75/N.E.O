using System;
using System.Threading;
using System.Threading.Tasks;

using Neo.Agents;
using Neo.Agents.Core;

namespace Neo.App
{
    public static class AIImageAnalysis
    {
        static GeminiImageAnalysisAgent _agent = new GeminiImageAnalysisAgent();

        /// <summary>
        /// Analyzes an image using the Gemini Vision API.
        /// Returns a text description or analysis based on the prompt.
        /// </summary>
        public static async Task<string> AnalyzeAsync(
            byte[] imageData,
            string prompt = "Describe this image in detail.",
            string imageMimeType = "image/png",
            string? systemInstruction = null,
            CancellationToken cancellationToken = default)
        {
            _agent.SetOption("ApiKey", Environment.GetEnvironmentVariable("GEMINI_API_KEY", EnvironmentVariableTarget.Process));
            _agent.SetOption("Model", "IMAGE_ANALYSIS_MODEL_PLACEHOLDER");

            _agent.SetInput("ImageData", imageData);
            _agent.SetInput("Prompt", prompt);
            _agent.SetInput("ImageMimeType", imageMimeType);

            if (!string.IsNullOrEmpty(systemInstruction))
                _agent.SetInput("SystemInstruction", systemInstruction);

            await _agent.ExecuteAsync(cancellationToken);

            return _agent.GetOutput<string>("AnalysisText");
        }

        /// <summary>
        /// Extracts all visible text from an image (OCR).
        /// </summary>
        public static async Task<string> ExtractTextAsync(
            byte[] imageData,
            string imageMimeType = "image/png",
            CancellationToken cancellationToken = default)
        {
            return await AnalyzeAsync(
                imageData,
                prompt: "Extract all visible text from this image. Return only the extracted text, preserving the original layout as much as possible.",
                imageMimeType: imageMimeType,
                systemInstruction: "You are an OCR system. Extract text accurately and completely. Do not add commentary or descriptions — only return the text found in the image.",
                cancellationToken: cancellationToken);
        }
    }
}
