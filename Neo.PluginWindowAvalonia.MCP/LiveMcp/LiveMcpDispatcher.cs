using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using Neo.IPC;

namespace Neo.PluginWindowAvalonia.MCP.LiveMcp;

/// <summary>
/// Runtime side of Live-MCP method invocation. Resolves the target method from
/// the loaded UserControl, deserializes args from the host's JSON payload,
/// dispatches on the UI thread (default) or the thread pool (when
/// <c>OffUiThread = true</c>), awaits a possible Task return, and serializes
/// the result back.
///
/// Failures are returned as <see cref="MethodResultMessage"/> with a
/// machine-readable <see cref="MethodResultMessage.ErrorCode"/> — never thrown
/// out of <see cref="InvokeAsync"/>.
/// </summary>
internal sealed class LiveMcpDispatcher
{
    private readonly Func<Control?> _getUserControl;
    private readonly Func<AppManifestMessage?> _getManifest;

    public LiveMcpDispatcher(Func<Control?> getUserControl, Func<AppManifestMessage?> getManifest)
    {
        _getUserControl = getUserControl;
        _getManifest = getManifest;
    }

    // ── Method invocation ─────────────────────────────────────────────────────

    public async Task<MethodResultMessage> InvokeAsync(InvokeMethodMessage frame)
    {
        var control = _getUserControl();
        var manifest = _getManifest();
        if (control == null || manifest == null)
            return Fail("invocation_failed", "No UserControl is currently loaded.");

        var entry = manifest.Callables.FirstOrDefault(c => c.Name == frame.Method);
        if (entry == null)
            return Fail("method_not_found", $"Method '{frame.Method}' is not [McpCallable].");

        var method = control.GetType().GetMethod(
            frame.Method,
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        if (method == null)
            return Fail("method_not_found", $"Method '{frame.Method}' not found on '{control.GetType().FullName}'.");

        object?[] args;
        try { args = DeserializeArgs(method.GetParameters(), frame.ArgsJson); }
        catch (Exception ex) { return Fail("invocation_failed", $"Argument deserialization failed: {ex.Message}"); }

        // Stamp loop-protection hops into AsyncLocal so any Ai.Trigger
        // emission from inside the method picks it up.
        LiveMcpCallContext.Set(frame.Hops);
        try
        {
            var timeout = TimeSpan.FromSeconds(Math.Max(1, entry.TimeoutSeconds));
            using var cts = new CancellationTokenSource(timeout);

            var resultTask = entry.OffUiThread
                ? Task.Run(() => InvokeAndUnwrap(method, control, args), cts.Token)
                : Dispatcher.UIThread.InvokeAsync(() => InvokeAndUnwrap(method, control, args)).GetTask();

            var completed = await Task.WhenAny(resultTask, Task.Delay(timeout, cts.Token));
            if (completed != resultTask)
                return Fail("timeout", $"Method '{frame.Method}' exceeded {entry.TimeoutSeconds}s timeout.");

            var raw = await resultTask;
            return new MethodResultMessage(
                Success: true,
                ResultJson: SerializeResult(raw, method.ReturnType));
        }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            return Fail("invocation_failed", $"{tie.InnerException.GetType().Name}: {tie.InnerException.Message}");
        }
        catch (Exception ex)
        {
            return Fail("invocation_failed", $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            LiveMcpCallContext.Clear();
        }
    }

    // ── Observable read ──────────────────────────────────────────────────────

    public async Task<ReadObservableResultMessage> ReadObservableAsync(string name)
    {
        var control = _getUserControl();
        var manifest = _getManifest();
        if (control == null || manifest == null)
            return new ReadObservableResultMessage(Success: false, Error: "No UserControl is currently loaded.");

        var entry = manifest.Observables.FirstOrDefault(o => o.Name == name);
        if (entry == null)
            return new ReadObservableResultMessage(Success: false, Error: $"Observable '{name}' is not [McpObservable].");

        var prop = control.GetType().GetProperty(
            name,
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        if (prop == null || !prop.CanRead)
            return new ReadObservableResultMessage(Success: false, Error: $"Property '{name}' not readable.");

        try
        {
            var value = await Dispatcher.UIThread.InvokeAsync(() => prop.GetValue(control));
            return new ReadObservableResultMessage(
                Success: true,
                ValueJson: SerializeResult(value, prop.PropertyType));
        }
        catch (Exception ex)
        {
            return new ReadObservableResultMessage(Success: false, Error: $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    // ── Internals ────────────────────────────────────────────────────────────

    private static object? InvokeAndUnwrap(MethodInfo method, object instance, object?[] args)
    {
        var result = method.Invoke(instance, args);
        return result;
    }

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
            {
                var elem = elements[i];
                result[i] = JsonSerializer.Deserialize(elem.GetRawText(), parameters[i].ParameterType, Json.Options);
            }
            else
            {
                result[i] = parameters[i].HasDefaultValue ? parameters[i].DefaultValue : GetDefault(parameters[i].ParameterType);
            }
        }
        return result;
    }

    private static object? GetDefault(Type t) =>
        t.IsValueType && Nullable.GetUnderlyingType(t) == null ? Activator.CreateInstance(t) : null;

    private static string? SerializeResult(object? raw, Type returnType)
    {
        // void methods don't carry a return value.
        if (returnType == typeof(void)) return null;

        // Unwrap Task / Task<T> — the actual completion is awaited at the call site
        // (resultTask), but the dispatcher sees the Task object as the return.
        // For non-generic Task we have no result; for Task<T> pull the .Result.
        if (raw is Task task)
        {
            if (returnType == typeof(Task)) return null;
            // task is already awaited (we awaited resultTask before getting here).
            var resultProp = task.GetType().GetProperty("Result");
            if (resultProp == null) return null;
            var taskResult = resultProp.GetValue(task);
            return taskResult == null ? "null" : JsonSerializer.Serialize(taskResult, Json.Options);
        }

        return raw == null ? "null" : JsonSerializer.Serialize(raw, raw.GetType(), Json.Options);
    }

    private static MethodResultMessage Fail(string code, string error) =>
        new(Success: false, ResultJson: null, Error: error, ErrorCode: code);
}
