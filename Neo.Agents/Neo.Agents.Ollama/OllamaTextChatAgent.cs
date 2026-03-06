using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Neo.Agents.Core;
using OpenAI.Chat;
using OpenAI;
using System.ClientModel;
using System.ClientModel.Primitives;

namespace Neo.Agents
{
    /// <summary>
    /// Agent, der über ein Ollama-kompatibles OpenAI-Interface eine Nachricht abschickt und
    /// die Antwort anhand eines übergebenen JSON-Schemas erzwingt.
    /// Ollama läuft lokal und benötigt keinen API-Key.
    /// </summary>
    public class OllamaTextChatAgent : AgentBase
    {
        private ChatClient? _chatClient;

        public override string Name => "OllamaTextChatAgent";

        protected override AgentMetadata CreateMetadata()
        {
            var metadata = new AgentMetadata
            {
                Name = Name,
                Description = "Agent, der das Ollama OpenAI-kompatible Interface nutzt, um JSON im gewünschten Schema zu generieren."
            };

            metadata.Options.Add(new Option<string>(
                name: "Endpoint",
                isRequired: false,
                defaultValue: "http://localhost:11434/v1/",
                description: "Die Ollama-Server-URL (Standard: http://localhost:11434/v1/)."
            ));

            metadata.Options.Add(new Option<string>(
                name: "ApiKey",
                isRequired: false,
                defaultValue: "ollama",
                description: "API-Key (bei Ollama in der Regel nicht benötigt)."
            ));

            metadata.Options.Add(new Option<string>(
                name: "Model",
                isRequired: true,
                defaultValue: "llama3.1:latest",
                description: "Das zu verwendende Ollama-Modell (z.B. llama3.1:latest, codellama)."
            ));

            metadata.Options.Add(new Option<float>(
                name: "Temperature",
                isRequired: false,
                defaultValue: 0.0f,
                description: "Sampling-Temperature (z.B. 0.7)."
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

            metadata.InputParameters.Add(new InputParameter<string>(
                name: "SystemMessage",
                isRequired: true,
                description: "System-Instruction für das Chat-Model."
            ));

            metadata.InputParameters.Add(new InputParameter<string>(
                name: "History",
                isRequired: false,
                description: "Bisherige Unterhaltung bzw. letztes Assistant-Statement."
            ));

            metadata.InputParameters.Add(new InputParameter<string>(
                name: "Prompt",
                isRequired: true,
                description: "User-Eingabe oder Frage, die an das Modell gesendet wird."
            ));

            metadata.InputParameters.Add(new InputParameter<string>(
                name: "JsonSchema",
                isRequired: true,
                description: "Das JSON-Schema, das die Antwort des Modells strukturieren soll."
            ));

            metadata.OutputParameters.Add(new OutputParameter<string>(
                name: "Result",
                isAlwaysProvided: true,
                description: "Die rohe JSON-Antwort des Modells (passt zum übergebenen Schema)."
            ));

            return metadata;
        }

        public override void ValidateOptionsAndInputs()
        {
            var model = GetOption<string>("Model");
            if (string.IsNullOrWhiteSpace(model))
            {
                throw new ArgumentException("Die Option 'Model' darf nicht leer sein.");
            }

            var systemMsg = GetInput<string>("SystemMessage");
            if (string.IsNullOrWhiteSpace(systemMsg))
            {
                throw new ArgumentException("Der Input 'SystemMessage' darf nicht leer sein.");
            }

            var prompt = GetInput<string>("Prompt");
            if (string.IsNullOrWhiteSpace(prompt))
            {
                throw new ArgumentException("Der Input 'Prompt' darf nicht leer sein.");
            }

            var jsonSchema = GetInput<string>("JsonSchema");
            if (string.IsNullOrWhiteSpace(jsonSchema))
            {
                throw new ArgumentException("Der Input 'JsonSchema' darf nicht leer sein.");
            }
        }

        public override async Task ExecuteAsync(CancellationToken? cancellationToken = null)
        {
            ValidateOptionsAndInputs();

            var endpoint = GetOption<string>("Endpoint") ?? "http://localhost:11434/v1/";
            var apiKey = GetOption<string>("ApiKey");
            var model = GetOption<string>("Model");
            var temperature = GetOption<float>("Temperature");
            var topP = GetOption<float>("TopP");
            var timeoutSeconds = GetOption<int>("TimeoutSeconds");

            if (string.IsNullOrWhiteSpace(apiKey)) apiKey = "ollama";

            var systemMessage = GetInput<string>("SystemMessage");
            var history = GetInput<string>("History") ?? string.Empty;
            var prompt = GetInput<string>("Prompt");
            var jsonSchema = GetInput<string>("JsonSchema");

            if (timeoutSeconds < 0)
            {
                throw new ArgumentException("Die Option 'TimeoutSeconds' darf nicht negativ sein.");
            }

            var timeout = timeoutSeconds == 0
                ? System.Threading.Timeout.InfiniteTimeSpan
                : TimeSpan.FromSeconds(timeoutSeconds);

            var clientOptions = new OpenAIClientOptions
            {
                NetworkTimeout = timeout,
                Endpoint = new Uri(endpoint),
                Transport = new HttpClientPipelineTransport(new HttpClient { Timeout = timeout })
            };

            _chatClient = new ChatClient(model: model, credential: new ApiKeyCredential(apiKey), options: clientOptions);

            // JSON-Schema im Prompt anhängen (lokale Modelle unterstützen kein strict JSON schema)
            var finalPrompt = new StringBuilder();
            finalPrompt.AppendLine(prompt);
            finalPrompt.AppendLine();
            finalPrompt.AppendLine("Please provide ONLY a valid JSON data object that strictly conforms to the provided schema. " +
                "Do NOT output the JSON schema itself, any explanation, or a description of the structure—output ONLY a JSON object that can be directly deserialized.");
            finalPrompt.AppendLine(jsonSchema);

            List<ChatMessage> messages = new()
            {
                new SystemChatMessage(systemMessage),
                new AssistantChatMessage(history),
                new UserChatMessage(finalPrompt.ToString())
            };

            ChatCompletionOptions options = new()
            {
                MaxOutputTokenCount = 4096 * 8,
                ResponseFormat = ChatResponseFormat.CreateJsonObjectFormat()
            };

            ChatCompletion completion;
            if (cancellationToken == null)
                completion = await _chatClient.CompleteChatAsync(messages, options);
            else
                completion = await _chatClient.CompleteChatAsync(messages, options, cancellationToken.GetValueOrDefault());

            string rawJsonResponse = completion.Content[0].Text;

            // Markdown-Fences entfernen (lokale Modelle packen JSON manchmal in ```)
            string cleanedJson = StripMarkdownFences(rawJsonResponse);

            SetOutput("Result", cleanedJson);
        }

        private static string StripMarkdownFences(string text)
        {
            string result = text.Trim();
            if (result.StartsWith("```json"))
                result = result.Substring(7);
            else if (result.StartsWith("```"))
                result = result.Substring(3);
            if (result.EndsWith("```"))
                result = result.Substring(0, result.Length - 3);
            return result.Trim();
        }
    }
}
