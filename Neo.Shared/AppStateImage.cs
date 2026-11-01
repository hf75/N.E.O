using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Neo.Shared
{
    /// <summary>
    /// What a running app holds, as data that can leave its process: the fields of the app's control, each
    /// as JSON, plus what the user did to the UI. Taken from one instance of the app and put into another,
    /// here or on another PC, or into a program that took the app over.
    ///
    /// <para>Only what is really data goes along: numbers, text, dates, lists and dictionaries of them, and
    /// the app's own classes and records made of those. Controls, timers, streams and connections belong
    /// to the process; what is left behind gets named, so that nobody has to guess.</para>
    /// </summary>
    /// <param name="Fields">Field name to its value as JSON.</param>
    /// <param name="Left">Fields that stay behind, with the reason.</param>
    /// <param name="Ui">What the user did to the UI, as the window of the toolkit writes it; null: nothing.</param>
    public sealed record AppStateImage(Dictionary<string, string> Fields, List<string> Left, string? Ui = null)
    {
        private const int MaxBytes = 32 * 1024 * 1024;

        private static readonly JsonSerializerOptions Options = new()
        {
            IncludeFields = true,
            ReferenceHandler = ReferenceHandler.IgnoreCycles,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        };

        // Types of the base class library that are plain values.
        private static readonly HashSet<Type> Values =
        [
            typeof(string), typeof(decimal), typeof(DateTime), typeof(DateTimeOffset), typeof(TimeSpan),
            typeof(DateOnly), typeof(TimeOnly), typeof(Guid), typeof(Uri), typeof(Version),
        ];

        /// <summary>How much data goes along.</summary>
        [JsonIgnore]
        public long Bytes => Fields.Sum(f => (long)f.Value.Length) + (Ui?.Length ?? 0);

        public string ToJson() => JsonSerializer.Serialize(this);

        public static AppStateImage? FromJson(string json) => JsonSerializer.Deserialize<AppStateImage>(json);

        /// <summary>Takes the state of a running app. On its UI thread.</summary>
        public static AppStateImage Capture(object app)
        {
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            var left = new List<string>();
            var plugin = app.GetType().Assembly;
            long total = 0;

            foreach (var field in FieldsOf(app.GetType()))
            {
                if (field.IsLiteral || (field.IsStatic && field.IsInitOnly)) continue;

                if (!IsData(field.FieldType, plugin, []))
                {
                    if (!IsPartOfTheUi(field.FieldType)) left.Add($"{field.Name} ({field.FieldType.Name})");
                    continue;
                }

                try
                {
                    var json = JsonSerializer.Serialize(field.GetValue(field.IsStatic ? null : app), field.FieldType, Options);
                    if (total + json.Length > MaxBytes)
                    {
                        left.Add($"{field.Name} (too much data: {json.Length / 1024} KB)");
                        continue;
                    }
                    total += json.Length;
                    fields[field.Name] = json;
                }
                catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException or TargetInvocationException)
                {
                    left.Add($"{field.Name} ({ex.GetBaseException().Message})");
                }
            }

            return new AppStateImage(fields, left);
        }

        /// <summary>What went into an app, and what could not.</summary>
        public sealed record Restored(int Values, List<string> Failed);

        /// <summary>
        /// Puts the values into a running app. On its UI thread. A list or dictionary the app already holds is
        /// filled, not replaced: its UI may be bound to that very object.
        /// </summary>
        /// <param name="prefix">Where the app got the fields from another program: a field that was taken may carry this prefix.</param>
        /// <param name="keep">Fields the app had before it took anything in: they stay its own, whatever comes in under their name.</param>
        public Restored RestoreTo(object app, string? prefix = null, IReadOnlyCollection<string>? keep = null)
        {
            var failed = new List<string>();
            var plugin = app.GetType().Assembly;
            var all = FieldsOf(app.GetType()).ToList();
            int restored = 0;

            foreach (var (name, json) in Fields)
            {
                var field = NamesFor(name, prefix).Select(n => all.FirstOrDefault(f => f.Name == n)).FirstOrDefault(f => f != null);
                if (field == null)
                {
                    failed.Add($"{name}: this app has no such field");
                    continue;
                }
                if (keep != null && keep.Contains(field.Name))
                {
                    failed.Add($"{name}: this app has its own");
                    continue;
                }
                if (!IsData(field.FieldType, plugin, []))
                {
                    failed.Add($"{name}: this app holds something else there");
                    continue;
                }

                try
                {
                    var target = field.IsStatic ? null : app;
                    var value = JsonSerializer.Deserialize(json, field.FieldType, Options);
                    var current = field.GetValue(target);
                    if (current is IList list && !current.GetType().IsArray && value is IEnumerable items)
                    {
                        list.Clear();
                        foreach (var item in items) list.Add(item);
                    }
                    else if (current is IDictionary dictionary && value is IDictionary incoming)
                    {
                        dictionary.Clear();
                        foreach (DictionaryEntry entry in incoming) dictionary[entry.Key] = entry.Value;
                    }
                    else
                    {
                        field.SetValue(target, value);
                    }
                    restored++;
                }
                catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException or InvalidOperationException or FieldAccessException or TargetInvocationException)
                {
                    failed.Add($"{name}: {ex.GetBaseException().Message}");
                }
            }
            return new Restored(restored, failed);
        }

        // The fields of the app's own classes: its base classes from outside (UserControl) are the toolkit's.
        private static IEnumerable<FieldInfo> FieldsOf(Type type)
        {
            var plugin = type.Assembly;
            for (var t = type; t != null && t.Assembly == plugin; t = t.BaseType)
            {
                foreach (var field in t.GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    yield return field;
            }
        }

        // Under its prefix first: then the field without it, if there is one, belongs to the app that took this one in.
        private static IEnumerable<string> NamesFor(string name, string? prefix)
        {
            if (!string.IsNullOrEmpty(prefix))
            {
                // An auto-property is stored under <Name>k__BackingField.
                const string Backing = "k__BackingField";
                if (name.StartsWith('<') && name.EndsWith(">" + Backing, StringComparison.Ordinal))
                {
                    var property = name[1..^(Backing.Length + 1)];
                    yield return $"<{prefix}_{property}>{Backing}";
                }
                else
                {
                    yield return $"{prefix}_{name}";                  // currency__rates
                    yield return $"{prefix}_{name.TrimStart('_')}";   // currency_rates
                    if (name.StartsWith('_')) yield return $"_{prefix}{name}";  // _currency_rates
                }
            }
            yield return name;
        }

        private static bool IsData(Type type, Assembly plugin, HashSet<Type> seen)
        {
            type = Nullable.GetUnderlyingType(type) ?? type;
            if (type.IsPrimitive || type.IsEnum || Values.Contains(type)) return type != typeof(IntPtr) && type != typeof(UIntPtr);
            if (type.IsArray) return type.GetArrayRank() == 1 && IsData(type.GetElementType()!, plugin, seen);

            if (type.IsGenericType)
            {
                var isContainer = type.Namespace is "System.Collections.Generic" or "System.Collections.ObjectModel"
                    || type.FullName!.StartsWith("System.ValueTuple`", StringComparison.Ordinal)
                    || type.FullName!.StartsWith("System.Tuple`", StringComparison.Ordinal);
                if (isContainer) return type.GetGenericArguments().All(a => IsData(a, plugin, seen));
                if (type.Assembly != plugin) return false;
            }

            if (type.Assembly != plugin || type.IsPointer || type.IsInterface || type.IsAbstract || typeof(Delegate).IsAssignableFrom(type)) return false;

            // The app's own class: data if it is made of data and is nothing of the toolkit.
            if (type.BaseType != null && type.BaseType != typeof(object) && type.BaseType != typeof(ValueType) && type.BaseType.Assembly != plugin) return false;
            if (!seen.Add(type)) return true;

            var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanRead && p.GetIndexParameters().Length == 0 && p.Name != "EqualityContract");
            var fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance);
            return properties.All(p => IsData(p.PropertyType, plugin, seen)) && fields.All(f => IsData(f.FieldType, plugin, seen));
        }

        // A control or a delegate in a field is how an app is built, nothing that anyone misses.
        private static bool IsPartOfTheUi(Type type)
        {
            if (typeof(Delegate).IsAssignableFrom(type)) return true;
            var assembly = type.Assembly.GetName().Name ?? "";
            return assembly.StartsWith("Avalonia", StringComparison.Ordinal)
                || assembly is "PresentationFramework" or "PresentationCore" or "WindowsBase";
        }
    }

    /// <summary>A value of the UI state in a form that survives leaving the process. The toolkits add their own types.</summary>
    public sealed record PortableValue(string Type, string Json)
    {
        public static PortableValue Of(object? value) => value switch
        {
            string or int or bool or double or decimal or DateTime or DateTimeOffset or TimeSpan
                => new PortableValue(value.GetType().Name, JsonSerializer.Serialize(value, value.GetType())),
            _ => new PortableValue("null", "null"), // the app's objects stay behind: a selection then goes by its position
        };

        public object? Value => Type switch
        {
            "String" => JsonSerializer.Deserialize<string>(Json),
            "Int32" => JsonSerializer.Deserialize<int>(Json),
            "Boolean" => JsonSerializer.Deserialize<bool>(Json),
            "Double" => JsonSerializer.Deserialize<double>(Json),
            "Decimal" => JsonSerializer.Deserialize<decimal>(Json),
            "DateTime" => JsonSerializer.Deserialize<DateTime>(Json),
            "DateTimeOffset" => JsonSerializer.Deserialize<DateTimeOffset>(Json),
            "TimeSpan" => JsonSerializer.Deserialize<TimeSpan>(Json),
            _ => null,
        };
    }

    /// <summary>The UI state of a window as data: what <see cref="AppStateImage.Ui"/> holds.</summary>
    public sealed record PortableUiState(List<PortableUiEntry> Entries, List<string> Focused)
    {
        public string ToJson() => JsonSerializer.Serialize(this);
        public static PortableUiState? FromJson(string json) => JsonSerializer.Deserialize<PortableUiState>(json);
    }

    public sealed record PortableUiEntry(string Key, string ControlType, Dictionary<string, PortableValue> Values);
}
