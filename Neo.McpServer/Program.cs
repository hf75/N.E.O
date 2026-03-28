using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Neo.McpServer.Services;

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

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly()
    .WithPromptsFromAssembly();

var app = builder.Build();
Console.Error.WriteLine("[Neo.McpServer] Ready, waiting for MCP messages...");
await app.RunAsync();
