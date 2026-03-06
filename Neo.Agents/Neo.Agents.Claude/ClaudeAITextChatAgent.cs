using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Net.Http;
using System.Threading.Tasks;
using Neo.Agents.Core;
using Anthropic.SDK;
using Anthropic.SDK.Common;
using Anthropic.SDK.Messaging;

namespace Neo.Agents
{
    public class AnthropicTextChatAgent : AgentBase
    {
        public override string Name => "AnthropicTextChatAgent";

        protected override AgentMetadata CreateMetadata()
        {
            var metadata = new AgentMetadata
            {
                Name = Name,
                Description = "Agent, der das Anthropic Chat-Interface nutzt, um JSON im gewünschten Schema zu generieren."
            };

            metadata.Options.Add(new Option<string>("ApiKey", true, null!, "Dein Anthropic API Key (Pflicht)."));
            metadata.Options.Add(new Option<string>("Model", true, "claude-3-7-sonnet-latest", "Das zu verwendende Modell."));
            metadata.Options.Add(new Option<float>("Temperature", false, 0.0f, "Sampling-Temperature."));
            metadata.Options.Add(new Option<int>("TimeoutSeconds", false, 600, "Timeout fuer den API-Request in Sekunden (0 = unendlich)."));
            metadata.Options.Add(new Option<float>("TopP", false, 0.9f, "Top-P für nucleus sampling."));

            metadata.InputParameters.Add(new InputParameter<string>("SystemMessage", true, "System-Instruction für das Modell."));
            metadata.InputParameters.Add(new InputParameter<string>("History", false, "Bisherige Unterhaltung."));
            metadata.InputParameters.Add(new InputParameter<string>("Prompt", true, "User-Eingabe."));
            metadata.InputParameters.Add(new InputParameter<string>("JsonSchema", false, "Das JSON-Schema für die Antwort."));

            metadata.OutputParameters.Add(new OutputParameter<string>(
                name: "Result",
                isAlwaysProvided: true,
                description: "Die rohe Antwort des Modells."
            ));

            return metadata;
        }

        public override void ValidateOptionsAndInputs()
        {
            if (string.IsNullOrWhiteSpace(GetOption<string>("ApiKey")))
                throw new ArgumentException("Die Option 'ApiKey' darf nicht leer sein.");
            if (string.IsNullOrWhiteSpace(GetOption<string>("Model")))
                throw new ArgumentException("Die Option 'Model' darf nicht leer sein.");

            var timeoutSeconds = GetOption<int>("TimeoutSeconds");
            if (timeoutSeconds < 0)
                throw new ArgumentException("Die Option 'TimeoutSeconds' darf nicht negativ sein.");
            if (string.IsNullOrWhiteSpace(GetInput<string>("SystemMessage")))
                throw new ArgumentException("Der Input 'SystemMessage' darf nicht leer sein.");
            if (string.IsNullOrWhiteSpace(GetInput<string>("Prompt")))
                throw new ArgumentException("Der Input 'Prompt' darf nicht leer sein.");
        }

        public override async Task ExecuteAsync(CancellationToken? cancellationToken = null)
        {
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

            using var httpClient = new HttpClient { Timeout = timeout };
            using var anthropicClient = new AnthropicClient(new APIAuthentication(apiKey), httpClient, null);

            var messages = new List<Message>();
            if (!string.IsNullOrEmpty(systemMessage))
                messages.Add(new Message(RoleType.Assistant, systemMessage));
            if (!string.IsNullOrEmpty(history))
                messages.Add(new Message(RoleType.Assistant, history));
            if (!string.IsNullOrEmpty(prompt))
                messages.Add(new Message(RoleType.User, prompt));

            if ( !string.IsNullOrEmpty(jsonSchema) )
            {
                var tools = new List<Anthropic.SDK.Common.Tool>
                {
                    new Function("record_summary", "Generate JSON output based on the given schema.",
                        JsonNode.Parse(jsonSchema))
                };

                var toolChoice = new ToolChoice()
                {
                    Type = ToolChoiceType.Tool,
                    Name = "record_summary"
                };

                var parameters = new MessageParameters
                {
                    Messages = messages,
                    MaxTokens = 64000,
                    Model = model,
                    Temperature = (decimal)temperature,
                    Tools = tools,
                    ToolChoice = toolChoice,
                };

                MessageResponse result;

                if( cancellationToken == null )
                    result = await anthropicClient.Messages.GetClaudeMessageAsync(parameters);
                else
                    result = await anthropicClient.Messages.GetClaudeMessageAsync( parameters, cancellationToken.GetValueOrDefault() );

                var toolResult = result.Content.OfType<ToolUseContent>().First();
                var rawJsonResponse = toolResult.Input.ToJsonString();

                SetOutput("Result", rawJsonResponse);
            }
            else
            {
                var parameters = new MessageParameters
                {
                    Messages = messages,
                    MaxTokens = 64000,
                    Model = model,
                    Temperature = (decimal)temperature,
                };

                var result = await anthropicClient.Messages.GetClaudeMessageAsync(parameters);

                SetOutput("Result", result.Message.ToString());
            }
        }
    }
}
