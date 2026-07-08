using CodeNav.Core.Indexing;
using CodeNav.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

string? workspaceRoot = null;
string? indexDb = null;
bool rebuild = false;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--workspace-root" or "-w": workspaceRoot = args[++i]; break;
        case "--index-db": indexDb = args[++i]; break;
        case "--rebuild": rebuild = true; break;
        case "--help" or "-h":
            Console.Error.WriteLine("Usage: PhoenixCodeNav.Mcp --workspace-root <dir> [--index-db <path>] [--rebuild]");
            return 0;
        default:
            Console.Error.WriteLine($"Unknown argument: {args[i]}");
            return 2;
    }
}

workspaceRoot ??= Environment.GetEnvironmentVariable("CODENAV_WORKSPACE_ROOT") ?? Directory.GetCurrentDirectory();
if (!Directory.Exists(workspaceRoot))
{
    Console.Error.WriteLine($"Workspace root not found: {workspaceRoot}");
    return 2;
}

var builder = Host.CreateApplicationBuilder(args);

// stdio transport owns stdout — all logging must go to stderr.
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Logging.SetMinimumLevel(LogLevel.Information);

builder.Services.AddSingleton(sp =>
{
    var logger = sp.GetRequiredService<ILogger<IndexManager>>();
    return new IndexManager(workspaceRoot, indexDb, msg => logger.LogInformation("{Message}", msg));
});
builder.Services.AddSingleton(sp =>
{
    var logger = sp.GetRequiredService<ILogger<CodeNav.Core.Semantic.SemanticService>>();
    return new CodeNav.Core.Semantic.SemanticService(
        sp.GetRequiredService<IndexManager>(),
        msg => logger.LogInformation("{Message}", msg));
});

builder.Services
    .AddMcpServer(o =>
    {
        o.ServerInfo = new() { Name = "phoenix-codenav", Version = CodeNav.Mcp.BuildInfo.Version };
        o.ServerInstructions =
            "Code navigation for a large C# workspace. Prefer these tools over shell grep for source navigation: " +
            "start with repo_overview; use search_symbol/definition/references for code identifiers; " +
            "search_text for literals and config keys; ALWAYS outline a large file before reading it, " +
            "then fetch only needed spans with source_context. Results carry confidence 'indexed' " +
            "(syntax/index-backed, not compiler-verified) and index freshness metadata.";
    })
    .WithStdioServerTransport()
    .WithTools<NavigationTools>();

var host = builder.Build();

// Kick off index open/build in the background — never block the MCP handshake.
host.Services.GetRequiredService<IndexManager>().Start(rebuild);

await host.RunAsync();
return 0;
