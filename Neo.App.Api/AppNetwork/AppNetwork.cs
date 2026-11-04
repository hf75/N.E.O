using System.IO.Pipes;
using System.Reflection;
using System.Text;
using System.Runtime.CompilerServices;
using System.Text.Json;

[assembly: InternalsVisibleTo("Neo.App.Api.Tests")]

namespace Neo.App;

/// <summary>
/// The publishing side of the app network: the window that runs an app calls
/// <see cref="Publish"/> once the app is loaded (and again after a live update, the offer may have
/// changed). From then on other N.E.O. apps on this machine find the app by name and call its
/// <c>[McpCallable]</c> methods and read its <c>[McpObservable]</c> values through <see cref="Apps"/>.
/// Generated code never calls this; it uses <see cref="Apps"/>.
///
/// <para>Transport: a named pipe per published app, one JSON request line in, one JSON response
/// line out, then the connection closes. Calls run on the app's UI thread (the window supplies the
/// dispatch) unless the method says <c>OffUiThread</c>.</para>
/// </summary>
public static class AppNetwork
{
    /// <summary>Calls nested deeper than this (A calls B calls A calls …) are refused.</summary>
    public const int MaxCallDepth = 8;

    private static readonly AsyncLocal<int> s_hops = new();
    private static readonly object s_gate = new();
    private static AppServer? s_server;

    /// <summary>How many app-to-app calls the current work is nested in (0 outside of any call).</summary>
    public static int CurrentHops => s_hops.Value;

    /// <summary>
    /// Messages between the N.E.O.s behind the apps, not between the apps: "take this program over". Set by
    /// the window that runs the app; generated code has no use for it. In: what kind of message and its
    /// content. Out: the answer.
    /// </summary>
    public static Func<string, string?, Task<string?>>? SystemHandler { get; set; }

    /// <summary>Sends such a message to the N.E.O. behind another app and returns its answer.</summary>
    public static async Task<string?> SendSystemAsync(AppDescriptor target, string kind, string? payload, TimeSpan timeout)
    {
        var args = payload == null ? null : new[] { JsonSerializer.SerializeToElement(payload, WireJson) };
        var result = await Apps.SendToAsync(target, "system", kind, args, timeout).ConfigureAwait(false);
        return result is { ValueKind: JsonValueKind.String } text ? text.GetString() : null;
    }

    /// <summary>The descriptor of this process's published app, or null.</summary>
    public static AppDescriptor? Published => s_server?.Descriptor;

    /// <summary>
    /// Asks the person before a method that changes something runs for another app or for Claude
    /// (<see cref="McpCallableAttribute.Changes"/>): true lets it run. Set by the window that runs the app. Without it
    /// such a call is refused, because nobody could be asked.
    /// </summary>
    public static Func<ChangeRequest, Task<bool>>? ChangeGate { get; set; }

    /// <summary>How long a call that changes something may wait for the person's answer.</summary>
    public static TimeSpan ConsentWait { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Repairs the app when a call ran into another program that changed (a ForeignControlMissingException): true
    /// once the repaired app is in place, and the call is made again. The call waits meanwhile. Set by the window of a
    /// host that can repair; without it the caller gets the error.
    /// </summary>
    public static Func<RepairRequest, Task<bool>>? RepairHandler { get; set; }

    /// <summary>How long a call waits for a repair (<see cref="AppDescriptor.RepairSeconds"/> of an interface).</summary>
    public static TimeSpan RepairAllowance { get; set; } = TimeSpan.FromSeconds(150);

    /// <summary>
    /// Announces <paramref name="app"/> on the app network. The name comes from its
    /// <see cref="AppInfoAttribute"/>, otherwise <paramref name="hostName"/>. Calling it again
    /// (a new app instance, a live update) replaces the offer and keeps the same address, so callers
    /// that remember the app still reach it.
    /// </summary>
    /// <param name="onUiThread">Runs a function on the app's UI thread and returns its result.</param>
    public static AppDescriptor Publish(object app, string hostName, Func<Func<object?>, Task<object?>> onUiThread)
    {
        lock (s_gate)
        {
            s_server ??= new AppServer();
            return s_server.Offer(app, hostName, onUiThread);
        }
    }

    /// <summary>Takes the app off the network (the window unloads it, the process ends).</summary>
    public static void Unpublish()
    {
        lock (s_gate)
        {
            s_server?.Dispose();
            s_server = null;
        }
    }

    internal static IDisposable EnterCall(int incomingHops)
    {
        var previous = s_hops.Value;
        s_hops.Value = incomingHops + 1;
        return new Restore(() => s_hops.Value = previous);
    }

    private sealed class Restore(Action undo) : IDisposable { public void Dispose() => undo(); }

    // ── Wire format ──────────────────────────────────────────────────────────

    /// <param name="From">Who calls: the name of the calling app, or what the process calls itself (<see cref="Apps.CallerName"/>). Shown to the person, not proof of anything.</param>
    internal sealed record Request(string Op, string? Name, JsonElement[]? Args, int Hops, string? From = null);
    internal sealed record Response(bool Ok, JsonElement? Result, string? Error);

    internal static readonly JsonSerializerOptions WireJson = new(JsonSerializerDefaults.Web);

    // ── Server ───────────────────────────────────────────────────────────────

    internal sealed class AppServer : IDisposable
    {
        private const string CallableAttribute = "Neo.App.McpCallableAttribute";
        private const string ObservableAttribute = "Neo.App.McpObservableAttribute";
        private const string InfoAttribute = "Neo.App.AppInfoAttribute";

        private readonly string _id = Guid.NewGuid().ToString("N")[..12];
        private readonly CancellationTokenSource _stop = new();
        private object? _app;
        private Func<Func<object?>, Task<object?>>? _onUiThread;

        public AppDescriptor? Descriptor { get; private set; }
        private string Pipe => "neo-app-" + _id;

        public AppDescriptor Offer(object app, string hostName, Func<Func<object?>, Task<object?>> onUiThread)
        {
            bool first = _app == null;
            _app = app;
            _onUiThread = onUiThread;
            Descriptor = Describe(app, hostName);
            AppRegistry.Write(Descriptor);
            if (first) _ = Task.Run(AcceptLoopAsync);
            return Descriptor;
        }

        private AppDescriptor Describe(object app, string hostName)
        {
            var type = app.GetType();
            string name = hostName, description = "";
            string? program = null;
            if (Attribute(type, InfoAttribute) is { } info)
            {
                name = Str(info, "Name") is { Length: > 0 } n ? n : hostName;
                description = Str(info, "Description") ?? "";
                program = Str(info, "Program") is { Length: > 0 } p ? p.Trim() : null;
            }

            var methods = new List<AppMethod>();
            foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (Attribute(m, CallableAttribute) is not { } attr) continue;
                methods.Add(new AppMethod(
                    m.Name,
                    Str(attr, "Description") ?? "",
                    m.GetParameters().Select(p => new AppParameter(p.Name ?? "_", TypeName(p.ParameterType))).ToList(),
                    TypeName(UnwrapTask(m.ReturnType)),
                    attr.GetType().GetProperty("OffUiThread")?.GetValue(attr) as bool? ?? false,
                    attr.GetType().GetProperty("TimeoutSeconds")?.GetValue(attr) as int? ?? 30,
                    attr.GetType().GetProperty("Changes")?.GetValue(attr) as bool? ?? false));
            }

            var values = new List<AppValue>();
            foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (!p.CanRead || Attribute(p, ObservableAttribute) is not { } attr) continue;
                values.Add(new AppValue(p.Name, Str(attr, "Description") ?? "", TypeName(p.PropertyType)));
            }

            // A caller of an interface waits while it is repaired, where a repair can happen.
            var repairSeconds = program != null && RepairHandler != null ? (int)RepairAllowance.TotalSeconds : 0;
            return new AppDescriptor(_id, name, description, Environment.ProcessId, Pipe, methods, values, DateTimeOffset.Now, program, repairSeconds);
        }

        private async Task AcceptLoopAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                NamedPipeServerStream? pipe = null;
                try
                {
                    pipe = new NamedPipeServerStream(Pipe, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await pipe.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                    var connected = pipe;
                    pipe = null;
                    _ = Task.Run(() => ServeAsync(connected));
                }
                catch (OperationCanceledException) { break; }
                catch
                {
                    // The pipe could not be created or accepted; try again shortly rather than spin.
                    try { await Task.Delay(200, _stop.Token).ConfigureAwait(false); } catch { break; }
                }
                finally { pipe?.Dispose(); }
            }
        }

        private async Task ServeAsync(NamedPipeServerStream pipe)
        {
            using (pipe)
            {
                Response response;
                try
                {
                    using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
                    var line = await reader.ReadLineAsync().ConfigureAwait(false);
                    var request = line == null ? null : JsonSerializer.Deserialize<Request>(line, WireJson);
                    response = request == null
                        ? new Response(false, null, "Empty request.")
                        : await HandleAsync(request).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    response = new Response(false, null, $"{ex.GetType().Name}: {ex.Message}");
                }

                try
                {
                    var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(response, WireJson) + "\n");
                    await pipe.WriteAsync(bytes).ConfigureAwait(false);
                    await pipe.FlushAsync().ConfigureAwait(false);
                    if (OperatingSystem.IsWindows()) pipe.WaitForPipeDrain();
                }
                catch { /* the caller went away */ }
            }
        }

        private async Task<Response> HandleAsync(Request request)
        {
            var app = _app;
            var descriptor = Descriptor;
            var onUiThread = _onUiThread;
            if (app == null || descriptor == null || onUiThread == null)
                return new Response(false, null, "The app is not loaded right now.");
            if (request.Hops >= MaxCallDepth)
                return new Response(false, null, $"Call chain too deep ({request.Hops} nested app-to-app calls); refusing to go on.");

            switch (request.Op)
            {
                case "describe":
                    return new Response(true, JsonSerializer.SerializeToElement(descriptor, AppRegistry.Json), null);

                case "system":
                {
                    if (SystemHandler is not { } handler)
                        return new Response(false, null, "This N.E.O. does not take such messages.");
                    var answer = await handler(request.Name ?? "", request.Args is { Length: > 0 } given ? given[0].GetString() : null).ConfigureAwait(false);
                    return new Response(true, JsonSerializer.SerializeToElement(answer, WireJson), null);
                }

                case "read":
                {
                    if (descriptor.Values.All(v => v.Name != request.Name))
                        return new Response(false, null, $"'{descriptor.Name}' has no value called '{request.Name}'.");
                    var prop = app.GetType().GetProperty(request.Name!, BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)!;
                    using (EnterCall(request.Hops))
                    {
                        var value = await onUiThread(() => prop.GetValue(app)).ConfigureAwait(false);
                        return new Response(true, JsonSerializer.SerializeToElement(value, WireJson), null);
                    }
                }

                case "call":
                {
                    var entry = descriptor.Methods.FirstOrDefault(m => m.Name == request.Name);
                    if (entry == null)
                        return new Response(false, null, $"'{descriptor.Name}' has no method called '{request.Name}'.");

                    if (entry.Changes)
                    {
                        if (ChangeGate is not { } gate)
                            return new Response(false, null, $"'{descriptor.Name}.{entry.Name}' changes something, and the window of '{descriptor.Name}' cannot ask the person.");
                        if (!await gate(new ChangeRequest(descriptor.Name, entry.Name, ArgumentsText(request.Args), request.From)).ConfigureAwait(false))
                            return new Response(false, null, $"The person did not allow '{descriptor.Name}.{entry.Name}'.");
                    }

                    using (EnterCall(request.Hops))
                    {
                        var (response, changed) = await InvokeAsync(entry, request).ConfigureAwait(false);
                        if (changed == null || RepairHandler is not { } repair) return response;

                        // The other program changed under the interface: the call waits while the app is repaired, then goes again.
                        var repaired = false;
                        try
                        {
                            repaired = await repair(new RepairRequest(descriptor.Name, entry.Name, changed.GetType().FullName ?? changed.GetType().Name,
                                changed.Message, changed.ToString(), changed.GetType().GetProperty("Program")?.GetValue(changed) as string ?? descriptor.Program)).ConfigureAwait(false);
                        }
                        catch (Exception) { /* not repaired */ }
                        if (!repaired) return response with { Error = response.Error + " The interface could not be repaired." };
                        // A repair that could not be patched in replaced the app, and with it this server: the new one takes the call.
                        AppServer current;
                        lock (s_gate) current = s_server ?? this;
                        return (await current.InvokeAsync(entry, request).ConfigureAwait(false)).Response;
                    }
                }

                default:
                    return new Response(false, null, $"Unknown operation '{request.Op}'.");
            }
        }

        // After a repair the app and its code can be new: everything is looked up again.
        private async Task<(Response Response, Exception? Changed)> InvokeAsync(AppMethod entry, Request request)
        {
            var app = _app;
            var onUiThread = _onUiThread;
            var descriptor = Descriptor;
            if (app == null || onUiThread == null || descriptor == null)
                return (new Response(false, null, "The app is not loaded right now."), null);
            var method = app.GetType().GetMethod(entry.Name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (method == null)
                return (new Response(false, null, $"'{descriptor.Name}' has no method called '{entry.Name}' any more."), null);
            var args = BindArguments(method.GetParameters(), request.Args ?? []);

            var timeout = TimeSpan.FromSeconds(Math.Max(1, entry.TimeoutSeconds));
            var work = entry.OffUiThread
                ? Task.Run(() => Unwrap(method.Invoke(app, args)))
                : UnwrapOnUi(onUiThread, method, app, args);
            var done = await Task.WhenAny(work, Task.Delay(timeout)).ConfigureAwait(false);
            if (done != work)
                return (new Response(false, null, $"'{descriptor.Name}.{entry.Name}' did not finish within {entry.TimeoutSeconds} s."), null);
            try
            {
                var result = await work.ConfigureAwait(false);
                return (new Response(true, method.ReturnType == typeof(void) || method.ReturnType == typeof(Task)
                    ? null : JsonSerializer.SerializeToElement(result, WireJson), null), null);
            }
            catch (Exception ex)
            {
                var thrown = ex is TargetInvocationException { InnerException: { } inner } ? inner : ex;
                return (new Response(false, null, $"'{descriptor.Name}.{entry.Name}' threw {thrown.GetType().Name}: {thrown.Message}"),
                    IsProgramChanged(thrown) ? thrown : null);
            }
        }

        // The helper for other programs is compiled into the app as source: its exception is known by its name.
        internal static bool IsProgramChanged(Exception ex) => ex.GetType().Name == "ForeignControlMissingException";

        private static string ArgumentsText(JsonElement[]? args)
        {
            var text = string.Join(", ", (args ?? []).Select(a => a.GetRawText()));
            return text.Length <= 300 ? text : text[..297] + "...";
        }

        // The UI-thread dispatch returns whatever the method returned; a Task is awaited here, on
        // the pool, so the UI thread is free while the method's async work runs.
        private static async Task<object?> UnwrapOnUi(Func<Func<object?>, Task<object?>> onUiThread, MethodInfo method, object app, object?[] args)
        {
            var raw = await onUiThread(() => method.Invoke(app, args)).ConfigureAwait(false);
            return await Unwrap(raw).ConfigureAwait(false);
        }

        private static async Task<object?> Unwrap(object? raw)
        {
            if (raw is not Task task) return raw;
            await task.ConfigureAwait(false);
            var type = task.GetType();
            if (!type.IsGenericType) return null;
            return type.GetProperty("Result")?.GetValue(task);
        }

        private static object?[] BindArguments(ParameterInfo[] parameters, JsonElement[] args)
        {
            var result = new object?[parameters.Length];
            for (int i = 0; i < parameters.Length; i++)
            {
                if (i < args.Length && args[i].ValueKind != JsonValueKind.Undefined)
                    result[i] = args[i].Deserialize(parameters[i].ParameterType, WireJson);
                else if (parameters[i].HasDefaultValue)
                    result[i] = parameters[i].DefaultValue;
                else
                    result[i] = parameters[i].ParameterType.IsValueType && Nullable.GetUnderlyingType(parameters[i].ParameterType) == null
                        ? Activator.CreateInstance(parameters[i].ParameterType) : null;
            }
            return result;
        }

        private static Type UnwrapTask(Type t)
        {
            if (t == typeof(Task)) return typeof(void);
            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Task<>)) return t.GetGenericArguments()[0];
            return t;
        }

        private static string TypeName(Type t) => TypeName(t, withMembers: true);

        // A type of the app's own goes with its public properties, one level deep: an app that reads or passes one
        // has to write its own type with exactly these names ("CategoryTotal { string Category, decimal Sum }").
        private static string TypeName(Type t, bool withMembers)
        {
            if (t == typeof(void)) return "void";
            if (t == typeof(string)) return "string";
            if (t == typeof(int)) return "int";
            if (t == typeof(long)) return "long";
            if (t == typeof(double)) return "double";
            if (t == typeof(decimal)) return "decimal";
            if (t == typeof(bool)) return "bool";
            if (t == typeof(object)) return "object";
            if (Nullable.GetUnderlyingType(t) is { } inner) return TypeName(inner, withMembers) + "?";
            if (t.IsArray) return TypeName(t.GetElementType()!, withMembers) + "[]";
            if (t.IsGenericType)
            {
                var name = t.Name[..t.Name.IndexOf('`')];
                return name + "<" + string.Join(", ", t.GetGenericArguments().Select(a => TypeName(a, withMembers))) + ">";
            }
            if (withMembers && IsOwnType(t) && MembersOf(t) is { Count: > 0 } members)
                return t.Name + " { " + string.Join(", ", members.Select(m => TypeName(m.Type, withMembers: false) + " " + m.Name)) + " }";
            return t.Name;
        }

        // A type of the app (or of a package it uses), not one of .NET or of the toolkit, whose shape everybody knows.
        private static bool IsOwnType(Type t)
        {
            if (t.IsPrimitive || t.IsEnum || t.IsInterface || (t.Namespace ?? "").StartsWith("System", StringComparison.Ordinal)) return false;
            var assembly = t.Assembly.GetName().Name ?? "";
            return assembly is not ("mscorlib" or "netstandard") && !assembly.StartsWith("System", StringComparison.Ordinal)
                && !assembly.StartsWith("Microsoft", StringComparison.Ordinal) && !assembly.StartsWith("Avalonia", StringComparison.Ordinal);
        }

        private static List<(string Name, Type Type)> MembersOf(Type t) =>
            t.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanRead && p.GetIndexParameters().Length == 0).Select(p => (p.Name, p.PropertyType))
                .Concat(t.GetFields(BindingFlags.Public | BindingFlags.Instance).Select(f => (f.Name, f.FieldType)))
                .Take(12).ToList();

        private static object? Attribute(MemberInfo member, string fullName) =>
            member.GetCustomAttributes(inherit: false).FirstOrDefault(a => a.GetType().FullName == fullName);

        private static string? Str(object attr, string property) => attr.GetType().GetProperty(property)?.GetValue(attr) as string;

        public void Dispose()
        {
            _stop.Cancel();
            AppRegistry.Remove(_id);
            _app = null;
            Descriptor = null;
        }
    }
}
