using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Schema;

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

        #region Neue Erweiterung: JSON-Schema-Erstellung und JSON-Initialisierung mit Newtonsoft.Json.Schema

        /// <summary>
        /// Erstellt ein JSON-Schema des Agenten basierend auf den Metadaten (Optionen, Inputs, Outputs)
        /// mithilfe von Newtonsoft.Json.Schema.
        /// </summary>
        /// <returns>Das JSON-Schema als formatierter String.</returns>
        public string GetJsonSchema()
        {
            // Erstelle das Root-Schema
            JSchema schema = new JSchema
            {
                Title = Metadata.Name,
                Description = Metadata.Description,
                Type = JSchemaType.Object
            };

            // Optionen-Schema erstellen
            JSchema optionsSchema = new JSchema
            {
                Type = JSchemaType.Object,
                AllowAdditionalProperties = false
            };

            foreach (var option in Metadata.Options)
            {
                JSchema optionSchema = new JSchema
                {
                    Type = MapTypeToJSchemaType(option.OptionType),
                    Description = option.Description
                };

                if (option is IOption<object> genericOption) // Zugriff auf DefaultValue nur wenn IOption<T>
                {
                    if (genericOption.DefaultValue != null)
                    {
                        optionSchema.Default = JToken.FromObject(genericOption.DefaultValue);
                    }
                }


                optionsSchema.Properties.Add(option.Name, optionSchema);

                if (option.IsRequired)
                {
                    optionsSchema.Required.Add(option.Name);
                }
            }

            // Eingabeparameter-Schema erstellen
            JSchema inputSchema = new JSchema
            {
                Type = JSchemaType.Object,
                AllowAdditionalProperties = false
            };

            foreach (var input in Metadata.InputParameters)
            {
                JSchema inputPropSchema = new JSchema
                {
                    Type = MapTypeToJSchemaType(input.ParameterType),
                    Description = input.Description
                };

                inputSchema.Properties.Add(input.Name, inputPropSchema);

                if (input.IsRequired)
                {
                    inputSchema.Required.Add(input.Name);
                }
            }

            // Ausgabeparameter-Schema erstellen
            JSchema outputSchema = new JSchema
            {
                Type = JSchemaType.Object,
                AllowAdditionalProperties = false
            };

            foreach (var output in Metadata.OutputParameters)
            {
                JSchema outputPropSchema = new JSchema
                {
                    Type = MapTypeToJSchemaType(output.ParameterType),
                    Description = output.Description
                };

                outputSchema.Properties.Add(output.Name, outputPropSchema);

                if (output.IsAlwaysProvided)
                {
                    outputSchema.Required.Add(output.Name);
                }
            }

            // Füge die Teilschemas in das Root-Schema ein
            schema.Properties.Add("optionsSchema", optionsSchema);
            schema.Properties.Add("inputSchema", inputSchema);
            schema.Properties.Add("outputSchema", outputSchema);

            // Setze additionalProperties auf false
            schema.AllowAdditionalProperties = false;

            if (optionsSchema.Properties.Count > 0)
                schema.Required.Add("optionsSchema");

            if (inputSchema.Properties.Count > 0)
                schema.Required.Add("inputSchema");

            if (outputSchema.Properties.Count > 0)
                schema.Required.Add("outputSchema");

            return schema.ToString();
        }

        /// <summary>
        /// Wandelt einen C#-Typ in den entsprechenden JSchemaType um.
        /// </summary>
        private JSchemaType MapTypeToJSchemaType(Type type)
        {
            if (type == typeof(string))
                return JSchemaType.String;
            if (type == typeof(int) || type == typeof(long) || type == typeof(short) || type == typeof(byte))
                return JSchemaType.Integer;
            if (type == typeof(float) || type == typeof(double) || type == typeof(decimal))
                return JSchemaType.Number;
            if (type == typeof(bool))
                return JSchemaType.Boolean;
            if (typeof(IEnumerable).IsAssignableFrom(type) && type != typeof(string))
                return JSchemaType.Array;
            if (type.IsClass || type.IsInterface)
                return JSchemaType.Object;

            return JSchemaType.String; // Fallback
        }

        public void InitializeFromJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                throw new ArgumentNullException(nameof(json));

            JObject root = JObject.Parse(json);

            // Optionen initialisieren
            if (root.TryGetValue("optionsSchema", out JToken? optionsToken) && optionsToken is JObject optionsObj)
            {
                foreach (var option in Metadata.Options)
                {
                    if (optionsObj.TryGetValue(option.Name, out JToken? valueToken))
                    {
                        // Hole die Methode SetOption<T>
                        MethodInfo? setOptionMethod = this.GetType().GetMethod("SetOption", BindingFlags.Public | BindingFlags.Instance);
                        if (setOptionMethod == null)
                            throw new InvalidOperationException("SetOption-Methode nicht gefunden.");

                        // Erzeuge die generische Methode anhand des erwarteten Typs
                        MethodInfo genericSetOption = setOptionMethod.MakeGenericMethod(option.OptionType);
                        object? value = valueToken.ToObject(option.OptionType);
                        genericSetOption.Invoke(this, new object[] { option.Name, value! });
                    }
                }
            }

            // Inputs initialisieren aus dem Schlüssel "inputSchema"
            if (root.TryGetValue("inputSchema", out JToken? inputsToken) && inputsToken is JObject inputsObj)
            {
                foreach (var input in Metadata.InputParameters)
                {
                    if (inputsObj.TryGetValue(input.Name, out JToken? valueToken))
                    {
                        // Hole die Methode SetInput<T>
                        MethodInfo? setInputMethod = this.GetType().GetMethod("SetInput", BindingFlags.Public | BindingFlags.Instance);
                        if (setInputMethod == null)
                            throw new InvalidOperationException("SetInput-Methode nicht gefunden.");

                        // Erzeuge die generische Methode anhand des erwarteten Typs
                        MethodInfo genericSetInput = setInputMethod.MakeGenericMethod(input.ParameterType);
                        object? value = valueToken.ToObject(input.ParameterType);
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
            var state = new JObject();

            // Add type information
            state["agentType"] = this.GetType().FullName;

            // Serialize options
            var optionsObj = new JObject();
            foreach (var key in _options.Keys)
            {
                optionsObj[key] = JToken.FromObject(_options[key]);
            }
            state["optionsSchema"] = optionsObj;

            // Serialize inputs
            var inputsObj = new JObject();
            foreach (var key in _inputs.Keys)
            {
                inputsObj[key] = JToken.FromObject(_inputs[key]);
            }
            state["inputSchema"] = inputsObj;

            // Serialize outputs if requested
            if (includeOutputs)
            {
                var outputsObj = new JObject();
                foreach (var key in _outputs.Keys)
                {
                    outputsObj[key] = JToken.FromObject(_outputs[key]);
                }
                state["outputSchema"] = outputsObj;
            }

            return state.ToString(Formatting.Indented);
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
}
