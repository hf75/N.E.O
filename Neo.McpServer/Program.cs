using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using McpServerInstance = ModelContextProtocol.Server.McpServer;
using Neo.McpServer.Services;
using Neo.McpServer.Tools;

// Global unhandled exception handler — log to stderr so it appears in Cowork/Desktop logs
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
    Console.Error.WriteLine($"[Neo.McpServer] FATAL unhandled exception: {e.ExceptionObject}");
TaskScheduler.UnobservedTaskException += (_, e) =>
{
    Console.Error.WriteLine($"[Neo.McpServer] Unobserved task exception: {e.Exception}");
    e.SetObserved();
};

Console.Error.WriteLine("[Neo.McpServer] Starting...");

var builder = Host.CreateApplicationBuilder(args);

// MCP STDIO uses stdout for JSON-RPC — route logging to stderr so it appears in Cowork logs
builder.Logging.ClearProviders();
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.Logging.AddConsole(opts => opts.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services.AddSingleton<LoopProtection>();
builder.Services.AddSingleton<PreviewSessionManager>();
builder.Services.AddSingleton<CompilationPipeline>();
builder.Services.AddSingleton<SkillsRegistry>();

// Phase 2: dynamic per-method tools augment the always-on tool set. The registry's
// collection is what fires Changed → notifications/tools/list_changed. We instantiate
// it here so the same instance can be assigned to both DI and McpServerOptions.ToolCollection.
var liveMcpToolRegistry = new LiveMcpToolRegistry();
builder.Services.AddSingleton(liveMcpToolRegistry);

builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new()
        {
            Name = "neo-preview",
            Version = "2.0.0",
        };

        // ── Channel capability: allows pushing events into Claude Code sessions ──
        options.Capabilities ??= new ServerCapabilities();
        options.Capabilities.Experimental = new Dictionary<string, object>
        {
            ["claude/channel"] = new JsonObject()
        };

        // ── Live-MCP Phase 2: dynamic tool collection ──
        // Tools.ListChanged=true tells the client we will push notifications/tools/list_changed
        // when our tool set mutates (per-app tool registration on app load/unload/hot-reload).
        // The registry's collection augments the static tool set discovered by WithToolsFromAssembly.
        options.Capabilities.Tools ??= new ToolsCapability();
        options.Capabilities.Tools.ListChanged = true;
        options.ToolCollection = liveMcpToolRegistry.ToolCollection;

        options.ServerInstructions =
            "This server generates and previews live desktop Avalonia apps.\n\n" +

            "=== CHANNEL BACK-REPORTING ===\n" +
            "Events from running preview apps arrive as <channel source=\"neo-preview\" event_type=\"...\"> tags.\n" +
            "You automatically receive them and should react without waiting for user input.\n\n" +

            "Two event types exist:\n\n" +

            "1. event_type=\"runtime_error\" (system — always active)\n" +
            "   The app threw an unhandled exception. Content contains the error text and stack trace.\n" +
            "   Action: Inspect with get_runtime_errors, fix via patch_preview or update_preview, verify fix.\n\n" +

            "2. event_type=\"user_trigger\" (user-defined via Ai.Trigger in generated code)\n" +
            "   The content IS a complete instruction prompt — execute it as if the user had typed it.\n" +
            "   Use neo-preview tools (set_property, inject_data, read_data, patch_preview, etc.) to act on it,\n" +
            "   or call other MCP tools (web search, file ops, etc.) if the instruction requires it.\n\n" +

            "=== Neo.App.Api — COMPLETE API REFERENCE ===\n" +
            "The Neo.App.Api assembly is auto-referenced by every generated app. Do NOT search the codebase\n" +
            "or grep for this API — the signatures below are complete and authoritative.\n\n" +

            "Namespace: Neo.App\n" +
            "Static class: Ai   (NOT 'Neo' — that name would collide with the root Neo.* namespace and fail to compile)\n" +
            "Methods:\n" +
            "  static void Ai.Trigger(string prompt)\n" +
            "      Push a complete natural-language instruction to Claude. Claude starts a new turn and executes it.\n" +
            "  static void Ai.ScheduleTrigger(TimeSpan delay, string prompt)\n" +
            "      Same as Trigger but fires after the given delay. Non-blocking. Good for timer-based behaviour.\n\n" +

            "That's the whole API. There is no Trigger(object), no Trigger(string, params), no cancellation, no result.\n\n" +

            "=== USING Ai.Trigger IN GENERATED CODE ===\n" +
            "When the user describes app behaviour like:\n" +
            "  - \"When I click the button, do X\"\n" +
            "  - \"After 10 seconds, do Y\"\n" +
            "  - \"When the selection changes, do Z\"\n\n" +

            "…you must include `using Neo.App;` and use `Ai.Trigger(...)` in the generated event handlers.\n" +
            "Do NOT write local business logic — always delegate to Claude via Ai.Trigger. You are the runtime.\n\n" +

            "The prompt passed to Ai.Trigger is a COMPLETE instruction that Claude executes verbatim.\n" +
            "Interpolate current app state (ComboBox selection, TextBox values, etc.) directly into the prompt\n" +
            "so Claude has all context needed to act — Claude cannot read the app state otherwise.\n\n" +

            "Example — user says: \"Combobox of vacation countries + Research button. On click, research the\n" +
            "selected country and write the result to a TextBlock named resultText.\"\n\n" +

            "Your generated event handler:\n" +
            "```csharp\n" +
            "using Neo.App;\n" +
            "// ...\n" +
            "private void OnResearchClick(object sender, RoutedEventArgs e)\n" +
            "{\n" +
            "    var country = CountryCombo.SelectedItem?.ToString() ?? \"(unknown)\";\n" +
            "    Ai.Trigger(\n" +
            "        $\"Research vacation destination {country}: best time to visit, top 3 sights, typical prices. \" +\n" +
            "        $\"Write the result with set_property into the TextBlock named 'resultText'.\");\n" +
            "}\n" +
            "```\n\n" +

            "=== RULES ===\n" +
            "- Use the API exactly as documented above (class name is `Ai`, not `Neo`). Do not search the source to verify.\n" +
            "- Do not hand-roll business logic in the app — delegate via Ai.Trigger.\n" +
            "- Always name controls that Claude needs to update so set_property can find them by Name.\n\n" +

            "=== Live-MCP — exposing app capabilities to Claude (Phase 1) ===\n" +
            "When the user describes app behaviour where *Claude* drives the app — \"so I can ask you to add a TODO\",\n" +
            "\"so you can refresh the data\", \"so the assistant can read the count\" — annotate methods/properties so\n" +
            "Claude can call them via the meta-tools `invoke_method`, `read_observable`, and `inspect_app_api`.\n\n" +

            "Three opt-in attributes from `Neo.App` (auto-referenced):\n\n" +

            "  [McpCallable(\"description\")]                — on PUBLIC methods Claude should call.\n" +
            "    Optional: OffUiThread = true (heavy work), TimeoutSeconds = 60 (default 30).\n" +
            "  [McpObservable(\"description\")]              — on PUBLIC readable PROPERTIES Claude should read.\n" +
            "    Optional: Watchable = true (Phase 2 — subscribe to changes).\n" +
            "  [McpTriggerable(\"description\")]             — on PUBLIC properties returning a Control (Button etc.)\n" +
            "    Phase 3 will use these for simulate_input.\n\n" +

            "Without these attributes a method/property is invisible to Claude — opt-in by design.\n" +
            "Description quality matters: it is what Claude sees when picking which tool to call. State the\n" +
            "USER-VISIBLE EFFECT, not the implementation. Bad: \"Updates _items list.\" Good: \"Adds a TODO item to the list.\"\n\n" +

            "Example — user says: \"Build a TODO app where I can ask the assistant to add and complete items.\"\n\n" +
            "```csharp\n" +
            "using Neo.App;\n" +
            "using System.Collections.ObjectModel;\n" +
            "\n" +
            "public partial class DynamicUserControl : UserControl\n" +
            "{\n" +
            "    public ObservableCollection<TodoItem> Items { get; } = new();\n" +
            "\n" +
            "    [McpObservable(\"Total number of TODO items currently in the list.\")]\n" +
            "    public int ItemCount => Items.Count;\n" +
            "\n" +
            "    [McpObservable(\"Number of completed TODO items.\")]\n" +
            "    public int CompletedCount => Items.Count(i => i.IsDone);\n" +
            "\n" +
            "    [McpCallable(\"Adds a new TODO item to the list with the given title.\")]\n" +
            "    public void AddItem(string title) => Items.Add(new TodoItem(title));\n" +
            "\n" +
            "    [McpCallable(\"Marks the TODO item at the given index as completed.\")]\n" +
            "    public void CompleteItem(int index)\n" +
            "    {\n" +
            "        if (index >= 0 && index < Items.Count) Items[index].IsDone = true;\n" +
            "    }\n" +
            "}\n" +
            "```\n\n" +

            "Now Claude (or a future user) can drive the app:\n" +
            "  inspect_app_api()              → lists AddItem, CompleteItem, ItemCount, CompletedCount\n" +
            "  invoke_method(\"AddItem\",\"[\\\"Buy milk\\\"]\")\n" +
            "  read_observable(\"ItemCount\")    → \"1\"\n\n" +

            "When deciding whether to attribute something:\n" +
            "- Method that mutates user-visible state and the user might describe in natural language → [McpCallable]\n" +
            "- Property that summarises state Claude might want to verify → [McpObservable]\n" +
            "- Internal helper, plumbing, or anything the user wouldn't ask Claude to do → leave un-annotated\n\n" +

            "Live-MCP and Channels compose: a method invoked via `invoke_method` can itself call `Ai.Trigger`\n" +
            "to push a follow-up. Loop protection caps such chains at 5 hops by default.";
    })
    .WithStdioServerTransport()
    .WithToolsFromAssembly()
    .WithPromptsFromAssembly();

var app = builder.Build();

// Wire up static reference for MCP Prompt (prompts can't use constructor injection)
AvaloniaPrompt.Skills = app.Services.GetRequiredService<SkillsRegistry>();

// Wire up McpServer instance so PreviewSessionManager can push channel notifications
var previewManager = app.Services.GetRequiredService<PreviewSessionManager>();
var mcpServer = app.Services.GetRequiredService<McpServerInstance>();
previewManager.SetChannelServer(mcpServer);
Console.Error.WriteLine("[Neo.McpServer] Channel capability registered — app events will push to Claude.");

var skillsPath = Environment.GetEnvironmentVariable("NEO_SKILLS_PATH");
if (!string.IsNullOrWhiteSpace(skillsPath))
    Console.Error.WriteLine($"[Neo.McpServer] Skills registry: {skillsPath}");

Console.Error.WriteLine("[Neo.McpServer] Ready, waiting for MCP messages...");
await app.RunAsync();
