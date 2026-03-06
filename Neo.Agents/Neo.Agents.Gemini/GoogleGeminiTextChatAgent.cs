using System;
using System.Collections.Generic;
using System.Text;
using System.Net.Http;
using System.Threading.Tasks;
using Neo.Agents.Core;
using GenerativeAI;
using GenerativeAI.Types;
using GenerativeAI.Core;

namespace Neo.Agents
{
    /// <summary>
    /// Agent, der das Google Gemini-API nutzt, um JSON im gewünschten Schema zu generieren.
    /// </summary>
    public class GeminiTextChatAgent : AgentBase
    {
        /// <summary>
        /// Name des Agenten
        /// </summary>
        public override string Name => "GeminiTextChatAgent";

        /// <summary>
        /// Optionen, Inputs und Outputs werden wie beim OpenAI-Agenten angelegt.
        /// </summary>
        protected override AgentMetadata CreateMetadata()
        {
            var metadata = new AgentMetadata
            {
                Name = Name,
                Description = "Agent, der das Google Gemini-API nutzt, um JSON im gewünschten Schema zu generieren."
            };

            // Optionen
            metadata.Options.Add(new Option<string>(
                name: "ApiKey",
                isRequired: true,
                defaultValue: null!,
                description: "Dein Google AI API Key (Pflicht)."
            ));

            metadata.Options.Add(new Option<string>(
                name: "Model",
                isRequired: true,
                defaultValue: "gemini-1.5-pro-latest",
                description: "Das zu verwendende Gemini-Modell (z.B. gemini-1.5-pro-latest)."
            ));

            metadata.Options.Add(new Option<float>(
                name: "Temperature",
                isRequired: false,
                defaultValue: 0.1f,
                description: "Sampling-Temperature (z.B. 0.1)."
            ));

            metadata.Options.Add(new Option<float>(
                name: "TopP",
                isRequired: false,
                defaultValue: 0.9f,
                description: "Top-P für nucleus sampling."
            ));

            metadata.Options.Add(new Option<int>(
                name: "TimeoutSeconds",
                isRequired: false,
                defaultValue: 600,
                description: "Timeout fuer den API-Request in Sekunden (0 = unendlich)."
            ));

            // Inputs
            metadata.InputParameters.Add(new InputParameter<string>(
                name: "SystemMessage",
                isRequired: true,
                description: "System-Instruction für das Modell."
            ));

            metadata.InputParameters.Add(new InputParameter<string>(
                name: "History",
                isRequired: false,
                description: "Bisherige Unterhaltung bzw. letztes Assistant-Statement."
            ));

            metadata.InputParameters.Add(new InputParameter<string>(
                name: "Prompt",
                isRequired: true,
                description: "User-Eingabe oder Frage, die an das Gemini-Model gesendet wird."
            ));

            metadata.InputParameters.Add(new InputParameter<string>(
                name: "JsonSchema",
                isRequired: true,
                description: "Das JSON-Schema, das die Antwort des Modells strukturieren soll."
            ));

            // Outputs
            metadata.OutputParameters.Add(new OutputParameter<string>(
                name: "Result",
                isAlwaysProvided: true,
                description: "Die rohe JSON-Antwort des Modells (passt zum übergebenen Schema)."
            ));

            return metadata;
        }

        /// <summary>
        /// Überprüfen, ob alle Pflichtfelder gesetzt sind.
        /// </summary>
        public override void ValidateOptionsAndInputs()
        {
            if (string.IsNullOrWhiteSpace(GetOption<string>("ApiKey")))
                throw new ArgumentException("Die Option 'ApiKey' darf nicht leer sein.");

            if (string.IsNullOrWhiteSpace(GetOption<string>("Model")))
                throw new ArgumentException("Die Option 'Model' darf nicht leer sein.");

            int timeoutSeconds = GetOption<int>("TimeoutSeconds");
            if (timeoutSeconds < 0)
                throw new ArgumentException("Die Option 'TimeoutSeconds' darf nicht negativ sein.");

            if (string.IsNullOrWhiteSpace(GetInput<string>("SystemMessage")))
                throw new ArgumentException("Der Input 'SystemMessage' darf nicht leer sein.");

            if (string.IsNullOrWhiteSpace(GetInput<string>("Prompt")))
                throw new ArgumentException("Der Input 'Prompt' darf nicht leer sein.");

            if (string.IsNullOrWhiteSpace(GetInput<string>("JsonSchema")))
                throw new ArgumentException("Der Input 'JsonSchema' darf nicht leer sein.");
        }

        /// <summary>
        /// Führt die Anfrage an das Gemini-API aus und gibt die Antwort als JSON-String aus.
        /// </summary>
        public override async Task ExecuteAsync(CancellationToken? cancellationToken = null)
        {
            // Optionen und Inputs prüfen
            ValidateOptionsAndInputs();

            string apiKey = GetOption<string>("ApiKey");
            string modelName = GetOption<string>("Model");
            float temperature = GetOption<float>("Temperature");
            float topP = GetOption<float>("TopP");
            int timeoutSeconds = GetOption<int>("TimeoutSeconds");
            string systemMessage = GetInput<string>("SystemMessage");
            string history = GetInput<string>("History") ?? string.Empty;
            string prompt = GetInput<string>("Prompt");
            string jsonSchema = GetInput<string>("JsonSchema");

            if (timeoutSeconds < 0)
                throw new ArgumentException("Die Option 'TimeoutSeconds' darf nicht negativ sein.");

            var timeout = timeoutSeconds == 0
                ? System.Threading.Timeout.InfiniteTimeSpan
                : TimeSpan.FromSeconds(timeoutSeconds);

            using var httpClient = new HttpClient { Timeout = timeout };
            var googleAI = new GoogleAi(apiKey, null, httpClient, null);

            // System-Instruktion (analog zu OpenAI system prompt)
            var systemInstruction = new Content
            {
                Role = "system",
                Parts = { new Part { Text = systemMessage } }
            };

            var generationConfig = new GenerationConfig
            {
                MaxOutputTokens = 65536*2,
                TopP = topP,
                Temperature = temperature,
                // Für JSON-Response
                ResponseMimeType = "application/json"
            };

            // Gemini-Modell initialisieren
            var model = googleAI.CreateGenerativeModel(modelName, generationConfig, systemInstruction: systemMessage);

            // User-Prompt bauen (mit JSON-Modus und Schema)
            var finalUserPrompt = new StringBuilder();

            // Historie als Assistant-Message anhängen, wenn vorhanden
            if (!string.IsNullOrWhiteSpace(history))
            {
                finalUserPrompt.AppendLine(history.Trim());
            }

            finalUserPrompt.AppendLine(prompt.Trim());
            finalUserPrompt.AppendLine();
            finalUserPrompt.AppendLine("Please provide ONLY a valid JSON data object that strictly conforms to the provided schema. " +
                                       "Do NOT output the JSON schema itself, any explanation, or a description of the structure—output ONLY a JSON object that can be directly deserialized.");
            finalUserPrompt.AppendLine(jsonSchema);

            // Gemini-API-Aufruf

            GenerateContentResponse response;
            if (cancellationToken == null)
                response = await model.GenerateContentAsync(finalUserPrompt.ToString());
            else
                response = await model.GenerateContentAsync(finalUserPrompt.ToString(), cancellationToken.GetValueOrDefault() );

            string result = response.Text()!.Trim();

            // Gemini kann JSON manchmal in Markdown packen, das entfernen wir
            string cleanedJson = result;
            if (cleanedJson.StartsWith("```json"))
                cleanedJson = cleanedJson.Substring(7);
            if (cleanedJson.EndsWith("```"))
                cleanedJson = cleanedJson.Substring(0, cleanedJson.Length - 3);

            cleanedJson = cleanedJson.Trim();

            // Ergebnis ablegen
            SetOutput("Result", cleanedJson);
        }
    }
}
