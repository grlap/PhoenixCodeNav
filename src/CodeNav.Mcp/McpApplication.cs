using CodeNav.Core.Diagnostics;
using CodeNav.Core.Indexing;
using CodeNav.Core.Semantic;
using CodeNav.Mcp.Daemon;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CodeNav.Mcp;

internal static class McpApplication
{
    private const string Instructions =
        "Code navigation for a large C# workspace. Prefer these tools over shell grep for source navigation: " +
        "start with repo_overview; use search_symbol/definition/references for code identifiers; " +
        "search_text for literals and config keys; ALWAYS outline a large file before reading it, " +
        "then fetch only needed spans with source_context. Results carry confidence 'indexed' " +
        "(syntax/index-backed, not compiler-verified) and index freshness metadata. When the " +
        "user explicitly asks to open the Operations Portal, call open_operations_portal and " +
        "show its returned url field verbatim as a clickable link; never call it proactively.";

    internal static IHost BuildHost(
        string workspaceRoot,
        string? indexDb,
        bool stdio,
        DaemonRequestAdmission? daemonAdmission = null)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Logging.SetMinimumLevel(LogLevel.Information);

        builder.Services.AddSingleton(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<IndexManager>>();
            return new IndexManager(workspaceRoot, indexDb,
                msg => logger.LogInformation("{Message}", msg));
        });
        builder.Services.AddSingleton(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<SemanticService>>();
            return new SemanticService(
                sp.GetRequiredService<IndexManager>(),
                msg => logger.LogInformation("{Message}", msg));
        });
        if (daemonAdmission is not null)
            builder.Services.AddSingleton(daemonAdmission);

        var mcp = builder.Services
            .AddMcpServer(o =>
            {
                o.ServerInfo = new() { Name = "phoenix-codenav", Version = BuildInfo.Version };
                o.ServerInstructions = Instructions;
            })
            .WithTools(ValidatedMcpToolRegistration.CreateNavigationTools(daemonAdmission));
        if (stdio) mcp.WithStdioServerTransport();
        return builder.Build();
    }

    internal static IndexManager StartIndex(IHost host, bool rebuild)
    {
        IndexManager manager = host.Services.GetRequiredService<IndexManager>();
        manager.Start(rebuild);
        manager.EmitServerInfo(new TelemetryServerInfo(
            BuildInfo.Version,
            BuildInfo.Stamp,
            BuildInfo.IndexSchema,
            NavigationTools.CapabilityFeatureIds(manager.Health())));
        return manager;
    }

    internal static async Task<int> RunStandaloneAsync(
        string workspaceRoot,
        string? indexDb,
        bool rebuild,
        CancellationToken cancellationToken = default)
    {
        PhoenixRuntimeMode.Set(PhoenixProcessMode.Standalone);
        using IHost host = BuildHost(workspaceRoot, indexDb, stdio: true);
        StartIndex(host, rebuild);
        await host.RunAsync(cancellationToken).ConfigureAwait(false);
        return 0;
    }
}
