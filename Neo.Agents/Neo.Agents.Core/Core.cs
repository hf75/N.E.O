using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace Neo.Agents.Core
{
    // Schnittstelle für Agenten
    public interface IAgent
    {
        string Name { get; }

        void SetOption<T>(string key, T value);
        T GetOption<T>(string key);

        void SetInput<T>(string name, T input);
        T GetInput<T>(string key);

        Task ExecuteAsync(CancellationToken? cancellationToken = null);

        T GetOutput<T>(string name);

        string GetJsonSchema();

        string GetAgentStateAsJson(bool includeOutputs = false);

        void ValidateOptionsAndInputs();

        void InitializeFromJson(string json);

        /// <summary>
        /// Liefert die Metadaten dieses Agenten.
        /// </summary>
        AgentMetadata Metadata { get; }
    }

    public abstract class AgentBase : IAgent
    {
        private readonly Dictionary<string, object> _options = new Dictionary<string, object>();
        private readonly Dictionary<string, object> _inputs = new Dictionary<string, object>();
        private readonly Dictionary<string, object> _outputs = new Dictionary<string, object>();

        private readonly AgentMetadata _metadata;
        private bool _isDirty = true;

        protected bool IsDirty => _isDirty;

        protected void ResetDirtyFlag()
        {
            _isDirty = false;
        }

        protected AgentBase()
        {
            // Erzeuge die Metadaten des Agenten
            _metadata = CreateMetadata();

            InitializeOptions();
            InitializeInputs();
            InitializeOutputs();
        }

        /// <summary>
        /// Stellt die Metadaten für externe Komponenten bereit.
        /// </summary>
        public AgentMetadata Metadata => _metadata;

        #region Initialisierung der Optionen, Inputs und Outputs

        private void InitializeOptions()
        {
            foreach (var option in Metadata.Options)
            {
                // Use reflection to get DefaultValue from IOption<T> since
                // IOption<T> is not covariant (no 'out T'), so 'is IOption<object>' fails
                // for generic types like Dictionary<string,string> or List<string>.
                var iOptionInterface = option.GetType().GetInterfaces()
                    .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IOption<>));

                if (iOptionInterface != null)
                {
                    var defaultProp = iOptionInterface.GetProperty("DefaultValue");
                    var defaultValue = defaultProp?.GetValue(option);
                    _options[option.Name] = defaultValue ?? GetDefault(option.OptionType);
                }
                else
                {
                    _options[option.Name] = GetDefault(option.OptionType);
                }
            }
        }

        private void InitializeInputs()
        {
            foreach (var input in Metadata.InputParameters)
            {
                _inputs[input.Name] = GetDefault(input.ParameterType);
            }
        }

        private void InitializeOutputs()
        {
            foreach (var output in Metadata.OutputParameters)
            {
                _outputs[output.Name] = GetDefault(output.ParameterType);
            }
        }

        private object GetDefault(Type type)
        {
            return (type.IsValueType ? Activator.CreateInstance(type) : null)!;
        }

        #endregion

        #region Optionen setzen und abfragen

        public void SetOption<T>(string key, T value)
        {
            if (value == null)
                throw new ArgumentNullException(nameof(value));
            if (string.IsNullOrEmpty(key))
                throw new ArgumentNullException(nameof(key));

            if (_options.ContainsKey(key))
            {
                _options[key] = value;
                _isDirty = true;
            }
            else
            {
                throw new KeyNotFoundException($"Option '{key}' not found.");
            }
        }

        public T GetOption<T>(string key)
        {
            if (string.IsNullOrEmpty(key))
                throw new ArgumentNullException(nameof(key));

            if (_options.TryGetValue(key, out var value))
            {
                if (value is T typedValue)
                    return typedValue;
                throw new InvalidOperationException($"Option '{key}' is not of type {typeof(T)}.");
            }
            throw new KeyNotFoundException($"Option '{key}' not found.");
        }

        #endregion

        #region Inputs setzen und abfragen

        public void SetInput<T>(string name, T input)
        {
            if (input == null)
                throw new ArgumentNullException(nameof(input));
            if (string.IsNullOrEmpty(name))
                throw new ArgumentNullException(nameof(name));

            if (_inputs.ContainsKey(name))
            {
                _inputs[name] = input;
            }
            else
            {
                throw new KeyNotFoundException($"Input '{name}' not found.");
            }
        }

        public T GetInput<T>(string name)
        {
            if (_inputs.TryGetValue(name, out var value))
            {
                return (T)value;
            }
            throw new KeyNotFoundException($"Input '{name}' not found.");
        }

        #endregion

        #region Outputs setzen und abfragen

        protected void SetOutput<T>(string name, T output)
        {
            if (output == null)
                throw new ArgumentNullException(nameof(output));
            if (string.IsNullOrEmpty(name))
                throw new ArgumentNullException(nameof(name));

            if (_outputs.ContainsKey(name))
            {
                _outputs[name] = output;
            }
            else
            {
                throw new KeyNotFoundException($"Output '{name}' not found.");
            }
        }

        public T GetOutput<T>(string name)
        {
            if (_outputs.TryGetValue(name, out var value))
            {
                if (value is T typedValue)
                    return typedValue;
                throw new InvalidOperationException($"Output '{name}' is not of type {typeof(T)}.");
            }
            throw new KeyNotFoundException($"Output '{name}' not found.");
        }

        #endregion

        #region Abstrakte Methoden und Name

        public abstract string Name { get; }

        public abstract Task ExecuteAsync(CancellationToken? cancellationToken = null);

        public abstract void ValidateOptionsAndInputs();

        // Abstrakte Methode zur Erstellung der Agenten-Metadaten
        protected abstract AgentMetadata CreateMetadata();

        #endregion

        #region JSON-Schema-Erstellung und JSON-Initialisierung mit System.Text.Json

        /// <summary>
        /// Erstellt ein JSON-Schema des Agenten basierend auf den Metadaten (Optionen, Inputs, Outputs).
        /// </summary>
        /// <returns>Das JSON-Schema als formatierter String.</returns>
        public string GetJsonSchema()
        {
            var schema = new JsonObject
            {
                ["title"] = Metadata.Name,
                ["description"] = Metadata.Description,
                ["type"] = "object",
                ["additionalProperties"] = false
            };

            var required = new JsonArray();

            // Optionen-Schema erstellen
            var optionsProperties = new JsonObject();
            var optionsRequired = new JsonArray();
            foreach (var option in Metadata.Options)
            {
                var optionSchema = new JsonObject
                {
                    ["type"] = MapTypeToSchemaString(option.OptionType),
                    ["description"] = option.Description
                };

                var iOptionInterface = option.GetType().GetInterfaces()
                    .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IOption<>));
                if (iOptionInterface != null)
                {
                    var defaultProp = iOptionInterface.GetProperty("DefaultValue");
                    var defaultValue = defaultProp?.GetValue(option);
                    if (defaultValue != null)
                        optionSchema["default"] = JsonSerializer.SerializeToNode(defaultValue);
                }

                optionsProperties[option.Name] = optionSchema;
                if (option.IsRequired)
                    optionsRequired.Add(option.Name);
            }

            var optionsSchema = new JsonObject
            {
                ["type"] = "object",
                ["additionalProperties"] = false,
                ["properties"] = optionsProperties,
                ["required"] = optionsRequired
            };

            // Eingabeparameter-Schema erstellen
            var inputProperties = new JsonObject();
            var inputRequired = new JsonArray();
            foreach (var input in Metadata.InputParameters)
            {
                inputProperties[input.Name] = new JsonObject
                {
                    ["type"] = MapTypeToSchemaString(input.ParameterType),
                    ["description"] = input.Description
                };
                if (input.IsRequired)
                    inputRequired.Add(input.Name);
            }

            var inputSchema = new JsonObject
            {
                ["type"] = "object",
                ["additionalProperties"] = false,
                ["properties"] = inputProperties,
                ["required"] = inputRequired
            };

            // Ausgabeparameter-Schema erstellen
            var outputProperties = new JsonObject();
            var outputRequired = new JsonArray();
            foreach (var output in Metadata.OutputParameters)
            {
                outputProperties[output.Name] = new JsonObject
                {
                    ["type"] = MapTypeToSchemaString(output.ParameterType),
                    ["description"] = output.Description
                };
                if (output.IsAlwaysProvided)
                    outputRequired.Add(output.Name);
            }

            var outputSchema = new JsonObject
            {
                ["type"] = "object",
                ["additionalProperties"] = false,
                ["properties"] = outputProperties,
                ["required"] = outputRequired
            };

            // Füge die Teilschemas in das Root-Schema ein
            var properties = new JsonObject();
            properties["optionsSchema"] = optionsSchema;
            properties["inputSchema"] = inputSchema;
            properties["outputSchema"] = outputSchema;
            schema["properties"] = properties;

            if (optionsProperties.Count > 0)
                required.Add("optionsSchema");
            if (inputProperties.Count > 0)
                required.Add("inputSchema");
            if (outputProperties.Count > 0)
                required.Add("outputSchema");
            schema["required"] = required;

            return schema.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        }

        /// <summary>
        /// Wandelt einen C#-Typ in den entsprechenden JSON-Schema-Typstring um.
        /// </summary>
        private string MapTypeToSchemaString(Type type)
        {
            if (type == typeof(string))
                return "string";
            if (type == typeof(int) || type == typeof(long) || type == typeof(short) || type == typeof(byte))
                return "integer";
            if (type == typeof(float) || type == typeof(double) || type == typeof(decimal))
                return "number";
            if (type == typeof(bool))
                return "boolean";
            if (typeof(IEnumerable).IsAssignableFrom(type) && type != typeof(string))
                return "array";
            if (type.IsClass || type.IsInterface)
                return "object";

            return "string"; // Fallback
        }

        public void InitializeFromJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                throw new ArgumentNullException(nameof(json));

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Optionen initialisieren
            if (root.TryGetProperty("optionsSchema", out JsonElement optionsElem) && optionsElem.ValueKind == JsonValueKind.Object)
            {
                foreach (var option in Metadata.Options)
                {
                    if (optionsElem.TryGetProperty(option.Name, out JsonElement valueElem))
                    {
                        MethodInfo? setOptionMethod = this.GetType().GetMethod("SetOption", BindingFlags.Public | BindingFlags.Instance);
                        if (setOptionMethod == null)
                            throw new InvalidOperationException("SetOption-Methode nicht gefunden.");

                        MethodInfo genericSetOption = setOptionMethod.MakeGenericMethod(option.OptionType);
                        object? value = JsonSerializer.Deserialize(valueElem.GetRawText(), option.OptionType);
                        genericSetOption.Invoke(this, new object[] { option.Name, value! });
                    }
                }
            }

            // Inputs initialisieren aus dem Schlüssel "inputSchema"
            if (root.TryGetProperty("inputSchema", out JsonElement inputsElem) && inputsElem.ValueKind == JsonValueKind.Object)
            {
                foreach (var input in Metadata.InputParameters)
                {
                    if (inputsElem.TryGetProperty(input.Name, out JsonElement valueElem))
                    {
                        MethodInfo? setInputMethod = this.GetType().GetMethod("SetInput", BindingFlags.Public | BindingFlags.Instance);
                        if (setInputMethod == null)
                            throw new InvalidOperationException("SetInput-Methode nicht gefunden.");

                        MethodInfo genericSetInput = setInputMethod.MakeGenericMethod(input.ParameterType);
                        object? value = JsonSerializer.Deserialize(valueElem.GetRawText(), input.ParameterType);
                        genericSetInput.Invoke(this, new object[] { input.Name, value! });
                    }
                }
            }
        }

        /// <summary>
        /// Serializes the current state of the agent (options, inputs, and outputs) into a JSON string.
        /// </summary>
        /// <param name="includeOutputs">Whether to include output values in the serialization.</param>
        /// <returns>JSON representation of the agent's current state.</returns>
        public string GetAgentStateAsJson(bool includeOutputs = false)
        {
            var state = new JsonObject();
            state["agentType"] = this.GetType().FullName;

            // Serialize options
            var optionsObj = new JsonObject();
            foreach (var key in _options.Keys)
            {
                optionsObj[key] = JsonSerializer.SerializeToNode(_options[key]);
            }
            state["optionsSchema"] = optionsObj;

            // Serialize inputs
            var inputsObj = new JsonObject();
            foreach (var key in _inputs.Keys)
            {
                inputsObj[key] = JsonSerializer.SerializeToNode(_inputs[key]);
            }
            state["inputSchema"] = inputsObj;

            // Serialize outputs if requested
            if (includeOutputs)
            {
                var outputsObj = new JsonObject();
                foreach (var key in _outputs.Keys)
                {
                    outputsObj[key] = JsonSerializer.SerializeToNode(_outputs[key]);
                }
                state["outputSchema"] = outputsObj;
            }

            return state.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        }

        #endregion
    }

    public class AgentMetadata
    {
        public string Name { get; set; } = ""; // Initialisierung hinzugefügt, um CS8618 zu beheben
        public string Description { get; set; } = ""; // Initialisierung hinzugefügt, um CS8618 zu beheben
        public IList<IOption> Options { get; set; } = new List<IOption>();
        public IList<IInputParameter> InputParameters { get; set; } = new List<IInputParameter>();
        public IList<IOutputParameter> OutputParameters { get; set; } = new List<IOutputParameter>();
    }

    // Schnittstellen für Optionen und Parameter
    public interface IOption
    {
        string Name { get; }
        bool IsRequired { get; }
        string? Description { get; }
        Type OptionType { get; }
    }

    public interface IOption<T> : IOption
    {
        T DefaultValue { get; } // DefaultValue wieder hinzugefügt, aber nur im generischen Interface
    }

    public interface IInputParameter
    {
        string Name { get; }
        bool IsRequired { get; }
        string? Description { get; }
        Type ParameterType { get; }
    }

    public interface IOutputParameter
    {
        string Name { get; }
        bool IsAlwaysProvided { get; }
        string? Description { get; }
        Type ParameterType { get; }
    }

    // Implementierung der Option-Klasse
    public class Option<T> : IOption<T>
    {
        public string Name { get; set; }
        public bool IsRequired { get; set; }
        public string? Description { get; set; }
        public T DefaultValue { get; }

        Type IOption.OptionType => typeof(T);

        public Option(string name, bool isRequired, T defaultValue = default!, string? description = null)
        {
            Name = name;
            IsRequired = isRequired;
            DefaultValue = defaultValue;
            Description = description;
        }
    }

    // Implementierung der Eingabeparameter-Klasse
    public class InputParameter<T> : IInputParameter
    {
        public string Name { get; set; }
        public bool IsRequired { get; set; }
        public string? Description { get; set; }
        public Type ParameterType => typeof(T);

        public InputParameter(string name, bool isRequired, string? description = null)
        {
            Name = name;
            IsRequired = isRequired;
            Description = description;
        }
    }

    // Implementierung der Ausgabeparameter-Klasse
    public class OutputParameter<T> : IOutputParameter
    {
        public string Name { get; set; }
        public bool IsAlwaysProvided { get; set; }
        public string? Description { get; set; }
        public Type ParameterType => typeof(T);

        public OutputParameter(string name, bool isAlwaysProvided, string? description = null)
        {
            Name = name;
            IsAlwaysProvided = isAlwaysProvided;
            Description = description;
        }
    }

    // ─────────────────────────────────────────────────────────────
    //  Streaming Infrastructure
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// A single chunk of streaming output from a streaming agent.
    /// </summary>
    public class StreamChunk
    {
        /// <summary>
        /// Identifies what is being streamed (e.g. "TranscribedText", "AudioData").
        /// Maps to an OutputParameter name.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// The partial value for this chunk.
        /// </summary>
        public object Value { get; }

        /// <summary>
        /// True if this is the final chunk for this output name.
        /// </summary>
        public bool IsFinal { get; }

        public StreamChunk(string name, object value, bool isFinal = false)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            Value = value ?? throw new ArgumentNullException(nameof(value));
            IsFinal = isFinal;
        }

        /// <summary>
        /// Typed access to the chunk value.
        /// </summary>
        public T GetValue<T>() => (T)Value;
    }

    /// <summary>
    /// Extension of IAgent that supports streaming partial results
    /// via IAsyncEnumerable during execution.
    /// </summary>
    public interface IStreamingAgent : IAgent
    {
        /// <summary>
        /// Executes the agent and yields partial results as they become available.
        /// After enumeration completes, final results are also available via GetOutput().
        /// </summary>
        IAsyncEnumerable<StreamChunk> ExecuteStreamingAsync(
            CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Base class for agents that support streaming partial results.
    /// Subclasses override ExecuteStreamingCoreAsync to yield StreamChunks.
    /// Both streaming and batch execution are supported:
    ///   - ExecuteStreamingAsync() yields chunks and sets final outputs
    ///   - ExecuteAsync() calls ExecuteStreamingAsync() internally, discarding chunks
    /// </summary>
    public abstract class StreamingAgentBase : AgentBase, IStreamingAgent
    {
        /// <summary>
        /// Override this to implement the streaming logic.
        /// Yield StreamChunks as partial results become available.
        /// Call SetOutput() for final results before returning.
        /// </summary>
        protected abstract IAsyncEnumerable<StreamChunk> ExecuteStreamingCoreAsync(
            CancellationToken cancellationToken);

        /// <summary>
        /// Executes the agent with streaming. Yields partial results,
        /// then final outputs are available via GetOutput().
        /// </summary>
        public async IAsyncEnumerable<StreamChunk> ExecuteStreamingAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ValidateOptionsAndInputs();

            await foreach (var chunk in ExecuteStreamingCoreAsync(cancellationToken)
                .WithCancellation(cancellationToken))
            {
                yield return chunk;
            }
        }

        /// <summary>
        /// Batch-compatible execution. Runs the streaming pipeline internally
        /// but discards intermediate chunks — only final outputs remain.
        /// </summary>
        public override async Task ExecuteAsync(CancellationToken? cancellationToken = null)
        {
            var ct = cancellationToken ?? CancellationToken.None;

            await foreach (var _ in ExecuteStreamingAsync(ct).WithCancellation(ct))
            {
                // Consume and discard — final outputs are set via SetOutput()
            }
        }

        /// <summary>
        /// Helper: create a StreamChunk for a partial result.
        /// </summary>
        protected StreamChunk Chunk(string name, object value) =>
            new StreamChunk(name, value, isFinal: false);

        /// <summary>
        /// Helper: create a StreamChunk marked as final.
        /// </summary>
        protected StreamChunk FinalChunk(string name, object value) =>
            new StreamChunk(name, value, isFinal: true);
    }

    // ── Plugin Integration ─────────────────────────────────────────────

    /// <summary>
    /// Interface for agents that carry their own Neo.App integration metadata.
    /// Agents implementing this are auto-discovered by Neo.App at startup via reflection.
    /// </summary>
    public interface IAppIntegratedAgent
    {
        /// <summary>
        /// Display name shown in Settings UI (e.g. "Image Generation", "Speech-to-Text").
        /// </summary>
        string DisplayName { get; }

        /// <summary>
        /// Unique key for settings storage (e.g. "ImageGen", "SpeechToText").
        /// Used as dictionary key in SettingsModel.PluginAgentModels.
        /// </summary>
        string SettingsKey { get; }

        /// <summary>
        /// Name of the environment variable required for this agent (e.g. "GEMINI_API_KEY").
        /// Null if no API key is needed (e.g. local models).
        /// </summary>
        string? RequiredEnvVar { get; }

        /// <summary>
        /// Default model name (e.g. "gemini-3.1-flash-image-preview").
        /// </summary>
        string DefaultModel { get; }

        /// <summary>
        /// The C# helper template source code that gets compiled into generated apps.
        /// Contains placeholders that are replaced at compile time.
        /// Null if this agent doesn't provide a helper template.
        /// </summary>
        string? HelperTemplateCode { get; }

        /// <summary>
        /// Dictionary of placeholder → settings-key mappings for template replacement.
        /// E.g. { "IMAGEGEN_MODEL_PLACEHOLDER" => "ImageGen" } means replace the placeholder
        /// with the model name from PluginAgentModels["ImageGen"].
        /// </summary>
        IReadOnlyDictionary<string, string> TemplatePlaceholders { get; }

        /// <summary>
        /// DLL file name that must be available for compilation (e.g. "Neo.Agents.GeminiImageGen.dll").
        /// </summary>
        string AgentDllName { get; }

        /// <summary>
        /// System message documentation snippet that describes the agent's API
        /// for the AI code generator. Appended to the system prompt when this agent is active.
        /// Null if no system message docs are needed.
        /// </summary>
        string? SystemMessageDocs { get; }

        /// <summary>
        /// Fetches the list of available models from the provider API.
        /// Returns an empty list if the API is unreachable.
        /// </summary>
        Task<List<string>> FetchAvailableModelsAsync(string? apiKeyOrEndpoint);
    }

    /// <summary>
    /// Utility for loading embedded resources from agent assemblies.
    /// Used by IAppIntegratedAgent implementations to load their helper templates.
    /// </summary>
    public static class AgentResourceLoader
    {
        /// <summary>
        /// Loads an embedded resource by file name from the assembly that contains the given type.
        /// </summary>
        public static string? LoadEmbeddedResource(Type agentType, string resourceFileName)
        {
            var asm = agentType.Assembly;
            var resName = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith(resourceFileName, StringComparison.OrdinalIgnoreCase));
            if (resName == null) return null;
            using var stream = asm.GetManifestResourceStream(resName);
            if (stream == null) return null;
            using var reader = new System.IO.StreamReader(stream);
            return reader.ReadToEnd();
        }
    }
}
