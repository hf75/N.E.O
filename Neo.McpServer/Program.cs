using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Neo.McpServer.Services;

var builder = Host.CreateApplicationBuilder(args);

// MCP STDIO uses stdout for JSON-RPC — disable all console logging
builder.Logging.ClearProviders();
builder.Logging.AddDebug(); // only goes to debugger, not stdout/stderr

builder.Services.AddSingleton<PreviewSessionManager>();
builder.Services.AddSingleton<CompilationPipeline>();

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly()
    .WithPromptsFromAssembly();

var app = builder.Build();
await app.RunAsync();
