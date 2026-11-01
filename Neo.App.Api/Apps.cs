using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace Neo.App;

/// <summary>
/// Other N.E.O. apps running on this machine, and how to use them. Every running app that marks
/// methods with <c>[McpCallable]</c> and properties with <c>[McpObservable]</c> offers them here;
/// an app finds the others by name (<see cref="AppInfoAttribute"/>, or the name the host gave them).
///
/// <code>
///   var total = await Apps.ReadAsync&lt;decimal&gt;("Sales", "Total");
///   await Apps.CallAsync("Inventory", "Reserve", sku, 3);
///   var rows = await Apps.CallAsync&lt;List&lt;string&gt;&gt;("Inventory", "LowStock", 5);
///   using var watch = Apps.Watch&lt;int&gt;("Sales", "Count", n => countText.Text = n.ToString());
/// </code>
///
/// <para>Calls are asynchronous — await them, never block the UI thread on them. A missing app
/// throws <see cref="AppNotFoundException"/>; a call the other app could not carry out throws
/// <see cref="AppCallException"/> with its reason.</para>
/// </summary>
public static class Apps
{
    /// <summary>How long a call waits for the other app to pick up.</summary>
    public static TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>Every published app whose process is alive, this one included unless <paramref name="includeSelf"/> is false.</summary>
    public static IReadOnlyList<AppDescriptor> Running(bool includeSelf = false)
    {
        var all = AppRegistry.List();
        return includeSelf ? all : all.Where(a => a.Id != AppNetwork.Published?.Id).ToList();
    }

    /// <summary>The running app called <paramref name="name"/> (case-insensitive; the id works too), or null.</summary>
    public static AppDescriptor? Find(string name)
    {
        var all = AppRegistry.List();
        return all.LastOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? all.FirstOrDefault(a => string.Equals(a.Id, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>True when an app of that name is running right now.</summary>
    public static bool IsRunning(string name) => Find(name) != null;

    /// <summary>Calls a method of another app and returns its result.</summary>
    public static async Task<T?> CallAsync<T>(string app, string method, params object?[] args)
    {
        var result = await SendAsync(app, "call", method, args).ConfigureAwait(false);
        return result is { ValueKind: not JsonValueKind.Null and not JsonValueKind.Undefined } e ? e.Deserialize<T>(AppNetwork.WireJson) : default;
    }

    /// <summary>Calls a method of another app; the result, if any, is ignored.</summary>
    public static Task CallAsync(string app, string method, params object?[] args) => SendAsync(app, "call", method, args);

    /// <summary>Reads a value of another app.</summary>
    public static async Task<T?> ReadAsync<T>(string app, string value)
    {
        var result = await SendAsync(app, "read", value, null).ConfigureAwait(false);
        return result is { ValueKind: not JsonValueKind.Null and not JsonValueKind.Undefined } e ? e.Deserialize<T>(AppNetwork.WireJson) : default;
    }

    /// <summary>
    /// Watches a value of another app: <paramref name="onChanged"/> runs with the current value right
    /// away and again whenever it changes (the value is read every <paramref name="interval"/>, default
    /// half a second). The callback runs on the thread that called Watch when that thread has a
    /// synchronization context (the UI thread), otherwise on the thread pool. Dispose to stop.
    /// While the other app is not running, nothing is reported; when it comes back, watching resumes.
    /// </summary>
    public static IDisposable Watch<T>(string app, string value, Action<T?> onChanged, TimeSpan? interval = null)
    {
        var context = SynchronizationContext.Current;
        var cts = new CancellationTokenSource();
        var every = interval ?? TimeSpan.FromMilliseconds(500);
        _ = Task.Run(async () =>
        {
            string? last = null;
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    var raw = await SendAsync(app, "read", value, null).ConfigureAwait(false);
                    var text = raw?.GetRawText() ?? "null";
                    if (text != last)
                    {
                        last = text;
                        var typed = raw is { ValueKind: not JsonValueKind.Null and not JsonValueKind.Undefined } e ? e.Deserialize<T>(AppNetwork.WireJson) : default;
                        if (context != null) context.Post(_ => { if (!cts.IsCancellationRequested) onChanged(typed); }, null);
                        else onChanged(typed);
                    }
                }
                catch (AppNotFoundException) { /* not running right now */ }
                catch (AppCallException) { /* the value could not be read this time */ }
                catch (Exception) { /* transient */ }
                try { await Task.Delay(every, cts.Token).ConfigureAwait(false); } catch { break; }
            }
        });
        return new Stop(cts);
    }

    private sealed class Stop(CancellationTokenSource cts) : IDisposable
    {
        public void Dispose() { cts.Cancel(); cts.Dispose(); }
    }

    // ── Transport ────────────────────────────────────────────────────────────

    private static async Task<JsonElement?> SendAsync(string app, string op, string name, object?[]? args)
    {
        var target = Find(app) ?? throw new AppNotFoundException(app);
        var timeout = op == "call"
            ? TimeSpan.FromSeconds((target.Methods.FirstOrDefault(m => m.Name == name)?.TimeoutSeconds ?? 30) + 5)
            : TimeSpan.FromSeconds(15);
        return await SendToAsync(target, op, name, args?.Select(a => JsonSerializer.SerializeToElement(a, AppNetwork.WireJson)).ToArray(), timeout).ConfigureAwait(false);
    }

    internal static async Task<JsonElement?> SendToAsync(AppDescriptor target, string op, string name, JsonElement[]? args, TimeSpan timeout)
    {
        var request = new AppNetwork.Request(op, name, args, AppNetwork.CurrentHops);

        using var pipe = new NamedPipeClientStream(".", target.Pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync((int)ConnectTimeout.TotalMilliseconds).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new AppCallException($"'{target.Name}' is running but did not answer within {ConnectTimeout.TotalSeconds:0} s.");
        }

        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request, AppNetwork.WireJson) + "\n");
        await pipe.WriteAsync(bytes).ConfigureAwait(false);
        await pipe.FlushAsync().ConfigureAwait(false);

        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
        var readLine = reader.ReadLineAsync();
        var done = await Task.WhenAny(readLine, Task.Delay(timeout)).ConfigureAwait(false);
        if (done != readLine)
            throw new AppCallException($"'{target.Name}.{name}' did not answer within {timeout.TotalSeconds:0} s.");
        var line = await readLine.ConfigureAwait(false);
        var response = line == null ? null : JsonSerializer.Deserialize<AppNetwork.Response>(line, AppNetwork.WireJson);
        if (response == null) throw new AppCallException($"'{target.Name}' closed the connection without an answer.");
        if (!response.Ok) throw new AppCallException(response.Error ?? "The call failed.");
        return response.Result;
    }
}
