using System.Collections;
using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Neo.App;

/// <summary>
/// One of two running programs, as the communicator of an app link sees it. An app link connects programs
/// without changing them: it reads what a program shows and holds, and does what a person could do to it.
/// Controls have an address: their name, or their type and number among those of that type ("Slider:2").
/// Everything returns at once; what is asked for happens in the order it was asked for.
/// </summary>
public interface ILinkSide
{
    /// <summary>The name of the program.</summary>
    string Name { get; }

    /// <summary>Runs once right away with the current value of a property of a control, then at every change.</summary>
    void OnChange<T>(string control, string property, Action<T> then);

    /// <summary>The same for a field or property of the program itself, looked at 20 times a second.</summary>
    void OnField<T>(string field, Action<T> then);

    /// <summary>Runs when a button is pressed, by the user or by the program.</summary>
    void OnClick(string button, Action then);

    /// <summary>Sets a property of a control. The program reacts as it does when the user changes it.</summary>
    void Set(string control, string property, object? value);

    /// <summary>Sets a field or property of the program. Nothing reacts by itself: call what shows it afterwards.</summary>
    void SetField(string field, object? value);

    /// <summary>Presses a button.</summary>
    void Press(string button);

    /// <summary>Calls a method of the program, a private one too.</summary>
    void Call(string method, params object?[] args);
}

/// <summary>
/// Values on their way between two programs of an app link: as text between the processes, and in whatever
/// type each side asks for. Text and numbers turn into each other, because what one program shows as text
/// another one often holds as a number. Used by the window that runs an app; generated code has no use for it.
/// </summary>
public static class LinkValue
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        IncludeFields = true,
        MaxDepth = 16,
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    /// <summary>A value as text that can leave the process.</summary>
    public static string ToJson(object? value)
    {
        var plain = Plain(value);
        if (plain == null) return "null";

        try
        {
            return JsonSerializer.Serialize(plain, plain.GetType(), Options);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException or ArgumentException)
        {
            return JsonSerializer.Serialize(plain.ToString(), Options);
        }
    }

    // What belongs to the toolkit is of no use to another program as an object graph. Of a list its items count,
    // of a control what it shows, and of anything else what it calls itself, like the colour of a brush.
    private static object? Plain(object? value)
    {
        if (value == null) return null;

        var type = value.GetType();
        if (type.Namespace?.StartsWith("Avalonia", StringComparison.Ordinal) != true) return value;

        if (value is IEnumerable items) return items.Cast<object?>().Select(Plain).ToList();

        foreach (var name in new[] { "Text", "Content" })
        {
            if (type.GetProperty(name)?.GetValue(value) is { } shown && !ReferenceEquals(shown, value))
                return Plain(shown);
        }
        return value.ToString();
    }

    /// <summary>The value in the type the receiver holds.</summary>
    /// <exception cref="FormatException">It cannot be that type.</exception>
    public static T Convert<T>(string json) => (T)Convert(json, typeof(T))!;

    /// <summary>The value in the type the receiver holds.</summary>
    /// <exception cref="FormatException">It cannot be that type.</exception>
    public static object? Convert(string json, Type type)
    {
        using var document = JsonDocument.Parse(json);
        var value = document.RootElement;
        var target = Nullable.GetUnderlyingType(type) ?? type;

        if (value.ValueKind == JsonValueKind.Null)
        {
            if (!type.IsValueType || type != target) return null;
            throw new FormatException($"There is no value to put into a {type.Name}.");
        }

        try
        {
            if (target == typeof(object)) return Natural(value);
            if (target == typeof(string)) return AsText(value);
            if (target == typeof(double) || target == typeof(float))
                return System.Convert.ChangeType(AsNumber<double>(value), target, CultureInfo.InvariantCulture);
            if (IsNumber(target))
                return System.Convert.ChangeType(AsNumber<decimal>(value), target, CultureInfo.InvariantCulture);

            if (value.ValueKind == JsonValueKind.String)
            {
                var text = value.GetString()!;
                if (target == typeof(bool)) return bool.Parse(text);
                if (target.IsEnum) return Enum.Parse(target, text, ignoreCase: true);
            }
            return value.Deserialize(type, Options);
        }
        catch (Exception ex) when (ex is JsonException or InvalidCastException or OverflowException or ArgumentException or NotSupportedException or InvalidOperationException)
        {
            throw new FormatException($"'{Shorten(json)}' cannot be a {type.Name}.", ex);
        }
    }

    private static object? Natural(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => value.Clone(),
    };

    // A number as written in JSON, not as this PC would write it.
    private static string AsText(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString()!,
        JsonValueKind.True => "True",
        JsonValueKind.False => "False",
        _ => value.GetRawText(),
    };

    private static T AsNumber<T>(JsonElement value) where T : INumberBase<T>
    {
        if (value.ValueKind is not (JsonValueKind.Number or JsonValueKind.String))
            throw new InvalidCastException($"A {value.ValueKind} is no number.");

        var text = (value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText()).Trim();

        // "1.5" as written everywhere first; the way this PC writes numbers only where that is the one way to read them.
        foreach (var (styles, culture) in new[]
        {
            (NumberStyles.Float, CultureInfo.InvariantCulture),
            (NumberStyles.Float, CultureInfo.CurrentCulture),
            (NumberStyles.Number, CultureInfo.InvariantCulture),
            (NumberStyles.Number, CultureInfo.CurrentCulture),
        })
        {
            if (T.TryParse(text, styles, culture, out var number)) return number;
        }
        throw new InvalidCastException($"'{text}' is no number.");
    }

    private static bool IsNumber(Type type) => !type.IsEnum && Type.GetTypeCode(type) is
        TypeCode.Byte or TypeCode.SByte or TypeCode.Int16 or TypeCode.UInt16 or TypeCode.Int32 or TypeCode.UInt32 or
        TypeCode.Int64 or TypeCode.UInt64 or TypeCode.Decimal;

    private static string Shorten(string text) => text.Length <= 60 ? text : text[..60] + "...";
}

/// <summary>
/// Work for one side of an app link, done one after the other, so that a value set and a button pressed after
/// it arrive in that order. A slider can change faster than the other side takes it: what has a key and still
/// waits gets replaced by its newer value, unless something without a key, a press, came in between.
/// </summary>
public sealed class LinkOutbox(Action<Exception> failed)
{
    private readonly object _gate = new();
    private readonly List<(string? Key, Func<Task> Work)> _waiting = [];
    private bool _running;
    private bool _closed;

    /// <param name="key">What makes this replace the same kind of thing still waiting; null: it happens for sure.</param>
    public void Post(string? key, Func<Task> work)
    {
        lock (_gate)
        {
            if (_closed) return;

            if (key != null)
            {
                for (int i = _waiting.Count - 1; i >= 0 && _waiting[i].Key != null; i--)
                {
                    if (_waiting[i].Key != key) continue;
                    _waiting[i] = (key, work);
                    return;
                }
            }

            _waiting.Add((key, work));
            if (_running) return;
            _running = true;
        }
        _ = Task.Run(RunAsync);
    }

    /// <summary>Done when everything posted so far is.</summary>
    public Task FlushAsync()
    {
        var flushed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(null, () => { flushed.TrySetResult(); return Task.CompletedTask; });
        lock (_gate)
            if (_closed) flushed.TrySetResult();
        return flushed.Task;
    }

    /// <summary>What still waits is dropped, and nothing new is taken.</summary>
    public void Close()
    {
        lock (_gate)
        {
            _closed = true;
            _waiting.Clear();
        }
    }

    private async Task RunAsync()
    {
        while (true)
        {
            Func<Task> work;
            lock (_gate)
            {
                if (_waiting.Count == 0)
                {
                    _running = false;
                    return;
                }
                work = _waiting[0].Work;
                _waiting.RemoveAt(0);
            }

            try
            {
                await work().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                failed(ex);
            }
        }
    }
}
