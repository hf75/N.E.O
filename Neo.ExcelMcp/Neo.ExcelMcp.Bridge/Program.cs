using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Neo.ExcelMcp.Bridge;

// ─── Critical: logging must go to stderr, never stdout ──────────────────────
// Claude talks to this process over stdio (stdin/stdout = JSON-RPC stream).
// Any Console.WriteLine on stdout breaks the MCP framing. Log to stderr only.
Console.Error.WriteLine("[Neo.ExcelMcp.Bridge] Starting...");

AppDomain.CurrentDomain.UnhandledException += (_, e) =>
    Console.Error.WriteLine($"[Neo.ExcelMcp.Bridge] FATAL unhandled exception: {e.ExceptionObject}");

TaskScheduler.UnobservedTaskException += (_, e) =>
{
    Console.Error.WriteLine($"[Neo.ExcelMcp.Bridge] Unobserved task exception: {e.Exception}");
    e.SetObserved();
};

var builder = Host.CreateApplicationBuilder(args);

// Route all logging to stderr, same pattern Neo.McpServer uses.
builder.Logging.ClearProviders();
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.Logging.AddConsole(opts => opts.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services.AddSingleton<PipeClient>();

builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new()
        {
            Name = "neo-excel",
            Version = "0.0.1"
        };
    })
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

var app = builder.Build();

Console.Error.WriteLine("[Neo.ExcelMcp.Bridge] Ready, waiting for MCP messages...");
await app.RunAsync();
