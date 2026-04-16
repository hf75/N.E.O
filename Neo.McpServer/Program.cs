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

builder.Services.AddSingleton<PreviewSessionManager>();
builder.Services.AddSingleton<CompilationPipeline>();
builder.Services.AddSingleton<SkillsRegistry>();

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

        options.ServerInstructions =
            "This server generates and previews live desktop Avalonia apps.\n\n" +

            "=== CHANNEL BACK-REPORTING ===\n" +
            "Events from running preview apps arrive as <channel source=\"neo-preview\" event_type=\"...\"> tags.\n" +
            "You automatically receive them and should react without waiting for user input.\n\n" +

            "Two event types exist:\n\n" +

            "1. event_type=\"runtime_error\" (system — always active)\n" +
            "   The app threw an unhandled exception. Content contains the error text and stack trace.\n" +
            "   Action: Inspect with get_runtime_errors, fix via patch_preview or update_preview, verify fix.\n\n" +

            "2. event_type=\"user_trigger\" (user-defined via Neo.Trigger in generated code)\n" +
            "   The content IS a complete instruction prompt — execute it as if the user had typed it.\n" +
            "   Use neo-preview tools (set_property, inject_data, read_data, patch_preview, etc.) to act on it,\n" +
            "   or call other MCP tools (web search, file ops, etc.) if the instruction requires it.\n\n" +

            "=== USING Neo.Trigger IN GENERATED CODE ===\n" +
            "When the user describes app behavior like:\n" +
            "  - \"When I click the button, do X\"\n" +
            "  - \"After 10 seconds, do Y\"\n" +
            "  - \"When the selection changes, do Z\"\n\n" +

            "…you must include `using Neo.App;` and use `Neo.Trigger(...)` in the generated event handlers.\n" +
            "The Neo.App.Api assembly is already referenced — no NuGet package needed.\n\n" +

            "Neo.Trigger(prompt) takes a string that is a COMPLETE instruction for you.\n" +
            "Interpolate current app state (ComboBox selection, TextBox values, etc.) directly into the prompt\n" +
            "so you have all context needed to act.\n\n" +

            "Example — user says: \"Mach eine Combobox mit Urlaubsländern und einen 'Recherchieren' Button. " +
            "Wenn ich den Button drücke, recherchiere zum gewählten Land.\"\n\n" +

            "Your generated event handler:\n" +
            "```csharp\n" +
            "using Neo.App;\n" +
            "// ...\n" +
            "private void OnResearchClick(object sender, RoutedEventArgs e)\n" +
            "{\n" +
            "    var country = CountryCombo.SelectedItem?.ToString() ?? \"(unbekannt)\";\n" +
            "    Neo.Trigger(\n" +
            "        $\"Recherchiere das Urlaubsland {country}: beste Reisezeit, Top-3 Sehenswürdigkeiten, \" +\n" +
            "        $\"typische Preise. Schreibe das Ergebnis mit set_property in den TextBlock 'resultText'.\");\n" +
            "}\n" +
            "```\n\n" +

            "For timer-based triggers use Neo.ScheduleTrigger(TimeSpan delay, string prompt).\n\n" +

            "=== IMPORTANT ===\n" +
            "Do NOT try to handle events locally in generated code via business logic — always delegate to Claude\n" +
            "via Neo.Trigger. The whole point is that YOU (Claude) are the brain of the running app.";
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
