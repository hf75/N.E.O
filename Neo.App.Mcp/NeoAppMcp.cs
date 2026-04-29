using System.Reflection;
using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Neo.App.Mcp.Internal;
using McpServerInstance = ModelContextProtocol.Server.McpServer;

namespace Neo.App.Mcp;

/// <summary>
/// <para>Embeddable MCP-server runtime for <b>Frozen-Mode</b> N.E.O. apps. Drop a call to
/// <see cref="RunStdioAsync"/> into your app's startup path when launched with <c>--mcp</c>
/// and the running <see cref="UserControl"/> becomes a fully-featured stdio MCP server:</para>
///
/// <list type="bullet">
///   <item>Per-method MCP tools for every <c>[McpCallable]</c> on the UserControl
///         (snake_case names, JSON-schema-typed parameters).</item>
///   <item>Resource subscriptions for every <c>[McpObservable(Watchable = true)]</c>
///         (URI <c>app://&lt;name&gt;</c>, INPC-driven, 200 ms coalesce).</item>
///   <item>Static <c>raise_event</c> and <c>simulate_input</c> tools for self-testing flows.</item>
/// </list>
///
/// <para>No N.E.O. host process or IPC pipe — the app IS the MCP server. Same manifest as
/// Dev-Mode (M4.4 consistency guarantee), so user-side <c>[McpCallable]</c>-decorated methods
/// behave identically whether the app runs in <c>compile_and_preview</c> or as a frozen
/// single-file EXE.</para>
///
/// <para><b>Logging:</b> stdout is reserved for MCP JSON-RPC. All log output goes to stderr.
/// <b>Stdin:</b> stdin is consumed by the JSON-RPC framing, so the app must not read from it.</para>
/// </summary>
public static class NeoAppMcp
{
    /// <summary>
    /// Run the embedded MCP stdio server until <paramref name="cancellationToken"/> fires or
    /// stdin closes. Designed to be awaited from a background <see cref="Task.Run"/> so the
    /// Avalonia UI thread keeps pumping in <c>--mcp</c>-with-GUI mode.
    /// </summary>
    /// <param name="userControl">The loaded UserControl whose attributes drive the manifest.</param>
    /// <param name="options">Optional configuration; defaults work for the standard Frozen-Mode use case.</param>
    /// <param name="cancellationToken">Cancels the host on app shutdown.</param>
    public static async Task RunStdioAsync(
        Control userControl,
        NeoAppMcpOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(userControl);
        options ??= new NeoAppMcpOptions();

        var manifest = AppManifestBuilder.Build(userControl);
        var dispatcher = new InProcessDispatcher(userControl);
        var inputDispatcher = new InputDispatcher(userControl);

        // Resource subscriptions need a "push update" callback that fires
        // notifications/resources/updated on the wire — wired up after the McpServer is built.
        McpServerInstance? serverRef = null;
        var subs = new ObservableSubscriptions(
            userControl: userControl,
            onUpdated: async uri =>
            {
                if (serverRef == null) return;
                await serverRef.SendNotificationAsync(
                    NotificationMethods.ResourceUpdatedNotification,
                    new ResourceUpdatedNotificationParams { Uri = uri },
                    serializerOptions: null,
                    cancellationToken: default);
            },
            coalesceWindow: options.ResourceCoalesceWindow);

        var toolCollection = new McpServerPrimitiveCollection<McpServerTool>();
        foreach (var c in manifest.Callables)
            toolCollection.Add(new DynamicTool(c, dispatcher));
        toolCollection.Add(new RaiseEventTool(inputDispatcher));
        toolCollection.Add(new SimulateInputTool(inputDispatcher));

        var resourceCollection = new McpServerResourceCollection();
        foreach (var o in manifest.Observables.Where(o => o.Watchable))
            resourceCollection.Add(new DynamicResource(o, subs));

        var serverName = options.ServerName ?? DeriveServerNameFromEntryAssembly();
        var serverVersion = options.ServerVersion ?? DeriveServerVersionFromEntryAssembly();

        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Logging.AddConsole(opts => opts.LogToStandardErrorThreshold = LogLevel.Trace);

        builder.Services
            .AddMcpServer(opts =>
            {
                opts.ServerInfo = new() { Name = serverName, Version = serverVersion };
                opts.Capabilities ??= new ServerCapabilities();
                opts.Capabilities.Tools ??= new ToolsCapability();
                opts.Capabilities.Tools.ListChanged = false; // single app, manifest is frozen at startup
                opts.Capabilities.Resources ??= new ResourcesCapability();
                opts.Capabilities.Resources.Subscribe = true;
                opts.Capabilities.Resources.ListChanged = false;
                opts.ToolCollection = toolCollection;
                opts.ResourceCollection = resourceCollection;

                opts.Handlers ??= new McpServerHandlers();
                opts.Handlers.SubscribeToResourcesHandler = async (ctx, ct) =>
                {
                    var uri = ctx.Params?.Uri ?? "";
                    var name = ParseObservableNameFromUri(uri);
                    if (name != null) await subs.SubscribeAsync(name);
                    return new EmptyResult();
                };
                opts.Handlers.UnsubscribeFromResourcesHandler = async (ctx, ct) =>
                {
                    var uri = ctx.Params?.Uri ?? "";
                    var name = ParseObservableNameFromUri(uri);
                    if (name != null) await subs.UnsubscribeAsync(name);
                    return new EmptyResult();
                };

                opts.ServerInstructions = BuildServerInstructions(manifest, serverName);
            })
            .WithStdioServerTransport();

        var app = builder.Build();
        serverRef = app.Services.GetRequiredService<McpServerInstance>();

        Console.Error.WriteLine($"[{serverName}] MCP stdio server starting. " +
            $"{manifest.Callables.Count} callables, {manifest.Observables.Count(o => o.Watchable)} watchable observables.");

        try { await app.RunAsync(cancellationToken); }
        finally { subs.DisposeAll(); }
    }

    /// <summary>
    /// Diagnostic dump of the manifest to stderr. Wire this to a <c>--mcp-help</c> CLI flag
    /// so users (and CI smoke tests) can see what an EXE exposes without starting the JSON-RPC loop.
    /// </summary>
    public static void DumpManifest(Control userControl, TextWriter? writer = null)
    {
        ArgumentNullException.ThrowIfNull(userControl);
        writer ??= Console.Error;
        var manifest = AppManifestBuilder.Build(userControl);

        writer.WriteLine($"App: {manifest.ClassFullName}");
        writer.WriteLine($"Callables ({manifest.Callables.Count}):");
        foreach (var c in manifest.Callables)
        {
            var pars = string.Join(", ", c.Parameters.Select(p => $"{p.TypeName} {p.Name}"));
            writer.WriteLine($"  {c.ReturnTypeName} {c.Name}({pars})  →  tool '{Naming.ToSnakeCase(c.Name)}'");
            if (!string.IsNullOrEmpty(c.Description)) writer.WriteLine($"    {c.Description}");
        }
        writer.WriteLine($"Observables ({manifest.Observables.Count}):");
        foreach (var o in manifest.Observables)
        {
            var w = o.Watchable ? $"  →  resource '{Naming.BuildResourceUri(o.Name)}'" : "";
            writer.WriteLine($"  {o.TypeName} {o.Name}{w}");
            if (!string.IsNullOrEmpty(o.Description)) writer.WriteLine($"    {o.Description}");
        }
        writer.WriteLine($"Triggerables ({manifest.Triggerables.Count}):");
        foreach (var t in manifest.Triggerables)
            writer.WriteLine($"  {t.ControlType} {t.Name} (control: '{t.ControlName}')");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string? ParseObservableNameFromUri(string uri)
    {
        // Accept exact "app://<name>" form; reject anything else so we don't accidentally
        // resolve unintended URIs.
        const string prefix = "app://";
        return uri.StartsWith(prefix, StringComparison.Ordinal) && uri.Length > prefix.Length
            ? uri.Substring(prefix.Length)
            : null;
    }

    private static string DeriveServerNameFromEntryAssembly()
    {
        var name = Assembly.GetEntryAssembly()?.GetName().Name;
        return string.IsNullOrEmpty(name) ? "neo-frozen-app" : name;
    }

    private static string DeriveServerVersionFromEntryAssembly()
    {
        var asm = Assembly.GetEntryAssembly();
        if (asm == null) return "1.0.0";
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return string.IsNullOrEmpty(info) ? asm.GetName().Version?.ToString() ?? "1.0.0" : info;
    }

    private static string BuildServerInstructions(AppManifest manifest, string serverName) =>
        $"This is a frozen N.E.O. app exposed as a stdio MCP server: {serverName}.\n\n" +
        $"It exposes {manifest.Callables.Count} callable methods (each as a snake_cased tool) " +
        $"and {manifest.Observables.Count(o => o.Watchable)} watchable observables (URI app://<name>).\n" +
        "Always-on tools 'raise_event' and 'simulate_input' let Claude drive the GUI directly. " +
        "Use the per-method tools first; reach for raise_event / simulate_input when you need to " +
        "exercise the UI behaviour the user would.";
}
