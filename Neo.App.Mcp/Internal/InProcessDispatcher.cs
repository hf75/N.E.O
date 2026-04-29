using System.Reflection;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Neo.App.Mcp.Internal;

/// <summary>
/// Frozen-Mode equivalent of <c>LiveMcpDispatcher</c>: invokes <c>[McpCallable]</c> methods and
/// reads <c>[McpObservable]</c> properties directly on the loaded UserControl — no IPC,
/// no host server. UI-thread affinity is enforced via <see cref="Dispatcher.UIThread"/> the
/// same way Dev-Mode does it.
///
/// <para>The dispatcher does NOT enforce loop protection in Frozen-Mode (single process,
/// no Ai.Trigger channel since the channel concept doesn't exist outside Dev-Mode). Adding
/// loop protection here would only matter if a Frozen app calls another MCP server and back
/// in a loop — out of scope for v1.</para>
/// </summary>
internal sealed class InProcessDispatcher
{
    private readonly Control _userControl;

    public InProcessDispatcher(Control userControl)
    {
        _userControl = userControl;
    }

    // ── Method invocation ─────────────────────────────────────────────────────

    public async Task<DispatchResult> InvokeMethodAsync(CallableEntry entry, string argsJson, CancellationToken ct)
    {
        var method = _userControl.GetType().GetMethod(
            entry.Name,
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        if (method == null)
            return DispatchResult.Fail($"Method '{entry.Name}' not found on '{_userControl.GetType().FullName}'.");

        object?[] args;
        try { args = DeserializeArgs(method.GetParameters(), argsJson); }
        catch (Exception ex) { return DispatchResult.Fail($"Argument deserialization failed: {ex.Message}"); }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, entry.TimeoutSeconds)));

            var resultTask = entry.OffUiThread
                ? Task.Run(() => InvokeAndUnwrap(method, _userControl, args), cts.Token)
                : Dispatcher.UIThread.InvokeAsync(() => InvokeAndUnwrap(method, _userControl, args)).GetTask();

            var completed = await Task.WhenAny(resultTask, Task.Delay(Timeout.Infinite, cts.Token));
            if (completed != resultTask)
                return DispatchResult.Fail($"Method '{entry.Name}' exceeded {entry.TimeoutSeconds}s timeout.");

            var raw = await resultTask;
            return DispatchResult.Ok(SerializeResult(raw, method.ReturnType));
        }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            return DispatchResult.Fail($"{tie.InnerException.GetType().Name}: {tie.InnerException.Message}");
        }
        catch (Exception ex)
        {
            return DispatchResult.Fail($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    // ── Observable read ──────────────────────────────────────────────────────

    public async Task<DispatchResult> ReadObservableAsync(string name, CancellationToken ct)
    {
        var prop = _userControl.GetType().GetProperty(
            name,
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        if (prop == null || !prop.CanRead)
            return DispatchResult.Fail($"Property '{name}' not readable.");

        try
        {
            var value = await Dispatcher.UIThread.InvokeAsync(() => prop.GetValue(_userControl));
            return DispatchResult.Ok(SerializeResult(value, prop.PropertyType));
        }
        catch (Exception ex)
        {
            return DispatchResult.Fail($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    // ── Internals ────────────────────────────────────────────────────────────

    private static object? InvokeAndUnwrap(MethodInfo method, object instance, object?[] args)
        => method.Invoke(instance, args);

    private static object?[] DeserializeArgs(ParameterInfo[] parameters, string argsJson)
    {
        if (string.IsNullOrWhiteSpace(argsJson) || argsJson == "null")
            return parameters.Select(p => p.HasDefaultValue ? p.DefaultValue : GetDefault(p.ParameterType)).ToArray();

        using var doc = JsonDocument.Parse(argsJson);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException($"Args JSON must be an array, got {doc.RootElement.ValueKind}.");

        var elements = doc.RootElement.EnumerateArray().ToArray();
        var result = new object?[parameters.Length];
        for (int i = 0; i < parameters.Length; i++)
        {
            if (i < elements.Length)
                result[i] = JsonSerializer.Deserialize(
                    elements[i].GetRawText(),
                    parameters[i].ParameterType,
                    JsonOptions.Default);
            else
                result[i] = parameters[i].HasDefaultValue
                    ? parameters[i].DefaultValue
                    : GetDefault(parameters[i].ParameterType);
        }
        return result;
    }

    private static object? GetDefault(Type t) =>
        t.IsValueType && Nullable.GetUnderlyingType(t) == null ? Activator.CreateInstance(t) : null;

    internal static string? SerializeResult(object? raw, Type returnType)
    {
        if (returnType == typeof(void)) return null;
        if (raw is Task task)
        {
            if (returnType == typeof(Task)) return null;
            var resultProp = task.GetType().GetProperty("Result");
            if (resultProp == null) return null;
            var taskResult = resultProp.GetValue(task);
            return taskResult == null ? "null" : JsonSerializer.Serialize(taskResult, JsonOptions.Default);
        }
        return raw == null ? "null" : JsonSerializer.Serialize(raw, raw.GetType(), JsonOptions.Default);
    }
}

internal readonly record struct DispatchResult(bool Success, string? ResultJson, string? Error)
{
    public static DispatchResult Ok(string? resultJson) => new(true, resultJson, null);
    public static DispatchResult Fail(string error) => new(false, null, error);
}

internal static class JsonOptions
{
    public static readonly JsonSerializerOptions Default = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };
}
