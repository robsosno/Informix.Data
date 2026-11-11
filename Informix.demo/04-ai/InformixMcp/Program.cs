// InformixMcp: a stdio MCP server that gives an AI assistant read-only access to an Informix database
// through Informix.Data. See ../README.md. Everything it writes to stdout is the MCP protocol; logs go to stderr.

using Demos.InformixMcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services.AddSingleton<Database>();
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<InformixTools>();

await builder.Build().RunAsync();
