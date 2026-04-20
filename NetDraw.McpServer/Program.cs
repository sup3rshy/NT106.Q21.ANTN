using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Real Model Context Protocol server over stdio.
// Exposes drawing primitives as MCP tools. Launched as a child process by
// NetDraw.Server when a Claude API key is present.
//
// NOTE: stdout is reserved for MCP JSON-RPC frames. All logs MUST go to stderr.

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Logging.SetMinimumLevel(LogLevel.Information);

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
