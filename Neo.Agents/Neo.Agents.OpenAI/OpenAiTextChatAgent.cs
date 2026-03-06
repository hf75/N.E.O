using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Neo.Agents.Core;
using OpenAI.Chat;
using OpenAI;
using System.ClientModel;
using System.ClientModel.Primitives;

namespace Neo.Agents
{
    /// <summary>
    /// Agent, der über das OpenAI Chat-Interface eine Nachricht abschickt und
    /// die Antwort anhand eines übergebenen JSON-Schemas erzwingt.
    /// </summary>
    public class OpenAiTextChatAgent : AgentBase
    {
        private ChatClient? _chatClient;

        /// <summary>
        /// Name des Agenten
        /// </summary>
        public override string Name => "OpenAiTextChatAgent";

        /// <summary>
        /// Hier legen wir fest, welche Optionen, Inputs und Outputs der Agent hat.
        /// </summary>
        protected override AgentMetadata CreateMetadata()
        {
            var metadata = new AgentMetadata
            {
                Name = Name,
                Description = "Agent, der das OpenAI Chat-Interface nutzt, um JSON im gewünschten Schema zu generieren."
            };

            // -- Optionen -----------------------------------
            metadata.Options.Add(new Option<string>(
                name: "ApiKey",
                isRequired: true,
                defaultValue: null!,
                description: "Dein OpenAI API Key (Pflicht)."
            ));

            metadata.Options.Add(new Option<string>(
                name: "Model",
                isRequired: true,
                defaultValue: "gpt-4o",
                description: "Das zu verwendende OpenAI-Modell (z.B. gpt-3.5-turbo, gpt-4o)."
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

            // -- Eingaben ------------------------------------
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
                description: "User-Eingabe oder Frage, die an das OpenAI-Model gesendet wird."
            ));

            metadata.InputParameters.Add(new InputParameter<string>(
                name: "JsonSchema",
                isRequired: true,
                description: "Das JSON-Schema, das die Antwort des Modells strukturieren soll."
            ));

            // -- Ausgaben ------------------------------------
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
            var apiKey = GetOption<string>("ApiKey");
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                throw new ArgumentException("Die Option 'ApiKey' darf nicht leer sein.");
            }

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

        /// <summary>
        /// Führt die eigentliche Logik des Agenten aus: 
        /// Sendet die Anfrage an OpenAI und liefert das Ergebnis als JSON-String zurück.
        /// </summary>
        public override async Task ExecuteAsync(CancellationToken? cancellationToken = null)
        {
            // Erst Optionen/Inputs prüfen
            ValidateOptionsAndInputs();

            var apiKey = GetOption<string>("ApiKey");
            var model = GetOption<string>("Model");
            var temperature = GetOption<float>("Temperature");
            var topP = GetOption<float>("TopP");
            var timeoutSeconds = GetOption<int>("TimeoutSeconds");

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

            // ChatClient initialisieren (mit konfigurierbarem Timeout)
            var clientOptions = new OpenAIClientOptions
            {
                NetworkTimeout = timeout,
                Transport = new HttpClientPipelineTransport(new HttpClient { Timeout = timeout })
            };

            _chatClient = new ChatClient(model: model, credential: new ApiKeyCredential(apiKey), options: clientOptions);

            // Nachrichten aufbauen
            List<ChatMessage> messages = new()
            {
                new SystemChatMessage(systemMessage),
                new AssistantChatMessage(history),
                new UserChatMessage(prompt)
            };

            // Optionen für das Chat-Completion
            ChatCompletionOptions options = new()
            {   
                //ReasoningEffortLevel = ChatReasoningEffortLevel.High,
                MaxOutputTokenCount = 4096*8,
                //Temperature = temperature,
                //TopP = topP,
                ResponseFormat = ChatResponseFormat.CreateJsonSchemaFormat(
                    jsonSchemaFormatName: "user_defined_schema",
                    jsonSchema: BinaryData.FromBytes(System.Text.Encoding.UTF8.GetBytes(jsonSchema)),
                    jsonSchemaIsStrict: true
                )
            };

            ChatCompletion completion;
            if( cancellationToken == null )
                completion = await _chatClient.CompleteChatAsync(messages, options);
            else
                completion = await _chatClient.CompleteChatAsync(messages, options, cancellationToken.GetValueOrDefault());

            // Hier nehmen wir die erste Antwort als JSON
            string rawJsonResponse = completion.Content[0].Text;

            // Raw JSON in den Output legen
            SetOutput("Result", rawJsonResponse);
        }
    }
}
