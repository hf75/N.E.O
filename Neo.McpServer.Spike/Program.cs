// ============================================================================
// Live-MCP — Phase 0 / Task 1 Spike
//
// Goal: Determine whether Claude Code CLI re-fetches the tool list when the
// server sends `notifications/tools/list_changed` mid-session.
//
// Outcome decides whether Phase 2 (dynamic per-method tools) of the Live-MCP
// roadmap is feasible, or whether we pivot to a Meta-Tool-only architecture.
//
// Detection method: this server logs every `tools/list` request to stderr.
// If a fresh `tools/list` arrives shortly after we mutate the tool collection,
// the client supports it.
//
// This entire project can be deleted after Phase 0 completes.
// ============================================================================

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

AppDomain.CurrentDomain.UnhandledException += (_, e) =>
    Console.Error.WriteLine($"[SPIKE] FATAL: {e.ExceptionObject}");
TaskScheduler.UnobservedTaskException += (_, e) =>
{
    Console.Error.WriteLine($"[SPIKE] Unobserved task exception: {e.Exception}");
    e.SetObserved();
};

Console.Error.WriteLine($"[SPIKE] === Live-MCP Phase 0 spike starting at {Now()} ===");
Console.Error.WriteLine("[SPIKE] Initial tool list will contain: spike_phase_a, spike_advance");
Console.Error.WriteLine("[SPIKE] After 5s: spike_phase_b will be added (auto-fire)");
Console.Error.WriteLine("[SPIKE] On each spike_advance call: spike_phase_cN will be added");
Console.Error.WriteLine("[SPIKE] -> Watch for `tools/list request received` log lines after each mutation");

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.Logging.AddConsole(opts => opts.LogToStandardErrorThreshold = LogLevel.Trace);

// ── Tool collection (managed dynamically) ──
var toolCollection = new McpServerPrimitiveCollection<McpServerTool>();

McpServerTool MakeTool(string name, string description, string returnText) =>
    McpServerTool.Create(
        () => returnText,
        new McpServerToolCreateOptions { Name = name, Description = description });

// Always-present initial tool
toolCollection.Add(MakeTool(
    name: "spike_phase_a",
    description: "Phase 0 spike — initial tool, present from server startup. " +
                 "Returns a confirmation string so a successful invocation can be observed.",
    returnText: "OK: spike_phase_a (always-present tool)"));

// Manual trigger tool: each call appends a new spike_phase_cN
int counter = 0;
toolCollection.Add(McpServerTool.Create(
    () =>
    {
        var n = Interlocked.Increment(ref counter);
        var name = $"spike_phase_c{n}";
        var newTool = MakeTool(
            name,
            $"Phase 0 spike — added on demand by the {n}th spike_advance call. " +
            "If you can call this without restart, list_changed works.",
            $"OK: {name} (added by spike_advance call #{n})");
        toolCollection.Add(newTool);
        Console.Error.WriteLine($"[SPIKE] {Now()} spike_advance call #{n}: added '{name}'. " +
                                $"Collection now has {toolCollection.Count} tools.");
        return $"Added new tool '{name}'. Collection size: {toolCollection.Count}. " +
               "If your next prompt to Claude can call this tool, list_changed works.";
    },
    new McpServerToolCreateOptions
    {
        Name = "spike_advance",
        Description = "Phase 0 spike — when called, registers a new tool 'spike_phase_cN' (N increments " +
                      "on each call). Use this to manually trigger a tool-list mutation and see if " +
                      "Claude Code CLI re-fetches the tool list."
    }));

// Subscribe to Changed event so we can confirm the SDK fires it after Add()
toolCollection.Changed += (_, _) =>
    Console.Error.WriteLine($"[SPIKE] {Now()} ToolCollection.Changed fired. " +
                            $"Size={toolCollection.Count}. SDK should now send notifications/tools/list_changed.");

builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new() { Name = "neo-livemcp-spike", Version = "0.1.0" };

        options.Capabilities ??= new ServerCapabilities();
        options.Capabilities.Tools ??= new ToolsCapability();
        // CRITICAL: tells the client we will send notifications/tools/list_changed
        options.Capabilities.Tools.ListChanged = true;

        options.ToolCollection = toolCollection;

        // Custom ListToolsHandler — logs every tools/list request to stderr.
        // Returns empty result; SDK augments with toolCollection contents automatically.
        options.Handlers ??= new McpServerHandlers();
        options.Handlers.ListToolsHandler = (_, _) =>
        {
            Console.Error.WriteLine($"[SPIKE] {Now()} >>> tools/list request received " +
                                    $"(collection has {toolCollection.Count} tools: " +
                                    $"{string.Join(", ", toolCollection.PrimitiveNames)})");
            return ValueTask.FromResult(new ListToolsResult { Tools = [] });
        };

        options.ServerInstructions =
            "Live-MCP Phase 0 spike server. Tool list mutates over time:\n" +
            "  - spike_phase_a  (present from startup)\n" +
            "  - spike_advance  (call this to add a new spike_phase_cN tool)\n" +
            "  - spike_phase_b  (auto-added 5 seconds after server start)\n" +
            "If you can see and call all of these without restarting the server, " +
            "the client supports notifications/tools/list_changed.";
    })
    .WithStdioServerTransport();

var app = builder.Build();

// Auto-trigger after 5 seconds: add spike_phase_b
_ = Task.Run(async () =>
{
    await Task.Delay(TimeSpan.FromSeconds(5));
    Console.Error.WriteLine($"[SPIKE] {Now()} === AUTO-TRIGGER === adding spike_phase_b");
    toolCollection.Add(MakeTool(
        name: "spike_phase_b",
        description: "Phase 0 spike — auto-added 5 seconds after server start. " +
                     "If you can see/call this without restart, list_changed works.",
        returnText: "OK: spike_phase_b (added 5s post-startup)"));
});

Console.Error.WriteLine($"[SPIKE] {Now()} Ready, serving stdio MCP. Waiting for tools/list requests...");
await app.RunAsync();

static string Now() => DateTime.UtcNow.ToString("HH:mm:ss.fff") + "Z";
