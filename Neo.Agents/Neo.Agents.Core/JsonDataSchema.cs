using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Neo.Agents.Core
{
    /// <summary>
    /// The schema for data a model fills in for a generated app, the way structured output wants it: an object at
    /// the top, every object closed and every property required. The data itself sits in the property "result".
    /// </summary>
    public static class JsonDataSchema
    {
        /// <summary>How such data is written and read back: camelCase names, enums as strings.</summary>
        public static JsonSerializerOptions Options() => new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            RespectNullableAnnotations = true,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
            Converters = { new JsonStringEnumConverter() },
        };

        /// <summary>An object whose "result" holds a <paramref name="type"/>, and whatever else <paramref name="more"/> names.</summary>
        public static JsonObject ForResult(Type type, JsonSerializerOptions options, IReadOnlyDictionary<string, JsonNode>? more = null)
        {
            var result = options.GetJsonSchemaAsNode(type, new JsonSchemaExporterOptions
            {
                TreatNullObliviousAsNonNullable = true,
                TransformSchemaNode = NotNullWithoutAnnotation,
            });
            CloseObjects(result);

            var properties = new JsonObject { ["result"] = result };
            var required = new JsonArray("result");
            foreach (var (name, schema) in more ?? new Dictionary<string, JsonNode>())
            {
                properties[name] = schema;
                required.Add(name);
            }

            return new JsonObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["required"] = required,
                ["additionalProperties"] = false,
            };
        }

        /// <summary>The "result" of an answer in that schema, as a <typeparamref name="T"/>.</summary>
        public static T? ReadResult<T>(string json, JsonSerializerOptions options)
            => JsonNode.Parse(json)!["result"].Deserialize<T>(options);

        // Generated code has no nullable annotations. The exporter takes that as "may be null" for
        // properties, and then the code that wrote 'string Name' trips over a null in a row.
        private static JsonNode NotNullWithoutAnnotation(JsonSchemaExporterContext context, JsonNode schema)
        {
            if (context.PropertyInfo?.AttributeProvider is PropertyInfo property
                && new NullabilityInfoContext().Create(property).ReadState == NullabilityState.Unknown
                && schema is JsonObject node && node["type"] is JsonArray types)
            {
                node["type"] = types.Select(type => (string?)type).First(type => type != "null");
            }

            return schema;
        }

        private static void CloseObjects(JsonNode? schema)
        {
            if (schema is not JsonObject node)
                return;

            // A type used twice refers to its first place, which moved one level down.
            if ((string?)node["$ref"] is { } reference)
                node["$ref"] = "#/properties/result" + reference[1..];

            if (node["properties"] is JsonObject properties)
            {
                node["additionalProperties"] = false;
                node["required"] = new JsonArray(properties.Select(property => (JsonNode?)property.Key).ToArray());
                foreach (var property in properties)
                    CloseObjects(property.Value);
            }

            CloseObjects(node["items"]);

            if (node["anyOf"] is JsonArray variants)
            {
                foreach (var variant in variants)
                    CloseObjects(variant);
            }
        }
    }
}
