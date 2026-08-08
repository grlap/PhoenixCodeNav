using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace CodeNav.Mcp.Daemon;

internal sealed record DaemonUnavailableFailure(
    string Cause,
    string Detail,
    string Recovery,
    bool Retryable);

internal static class UnavailableMcpShim
{
    internal static async Task<int> RunAsync(
        DaemonUnavailableFailure failure,
        CancellationToken cancellationToken = default)
    {
        PhoenixRuntimeMode.Set(PhoenixProcessMode.UnavailableShim);
        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services
            .AddMcpServer(options =>
            {
                options.ServerInfo = new()
                {
                    Name = "phoenix-codenav-unavailable",
                    Version = BuildInfo.Version,
                };
                options.ServerInstructions =
                    "Phoenix shared daemon is unavailable. Tool calls return one typed cause and recovery action.";
            })
            .WithStdioServerTransport()
            .WithTools(ValidatedMcpToolRegistration.CreateNavigationTools()
                .Select(tool => (McpServerTool)new UnavailableMcpServerTool(tool, failure))
                .ToArray());
        using IHost host = builder.Build();
        await host.RunAsync(cancellationToken).ConfigureAwait(false);
        return 4;
    }
}

internal sealed class UnavailableMcpServerTool : DelegatingMcpServerTool
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    private readonly DaemonUnavailableFailure _failure;

    internal UnavailableMcpServerTool(
        McpServerTool inner,
        DaemonUnavailableFailure failure)
        : base(inner) => _failure = failure;

    public override ValueTask<CallToolResult> InvokeAsync(
        RequestContext<CallToolRequestParams> request,
        CancellationToken cancellationToken = default)
    {
        bool capabilities = string.Equals(
            ProtocolTool.Name, "server_capabilities", StringComparison.Ordinal);
        object payload = capabilities
            ? new
            {
                server = "phoenixCodeNav",
                version = BuildInfo.Version,
                build = new
                {
                    version = BuildInfo.Version,
                    commit = BuildInfo.Commit,
                    indexSchema = BuildInfo.IndexSchema,
                },
                meta = new
                {
                    indexMode = "unavailable",
                    cause = _failure.Cause,
                    detail = _failure.Detail,
                    recovery = _failure.Recovery,
                    retryable = _failure.Retryable,
                },
                features = new[]
                {
                    new
                    {
                        id = "shared-mcp-daemon",
                        summary = "Phoenix shared daemon negotiation is present but unavailable for this session.",
                    },
                },
            }
            : new
            {
                error = "phoenix_daemon_unavailable",
                tool = ProtocolTool.Name,
                cause = _failure.Cause,
                detail = _failure.Detail,
                recovery = _failure.Recovery,
                retryable = _failure.Retryable,
                meta = new { indexMode = "unavailable" },
            };
        JsonElement structured = JsonSerializer.SerializeToElement(payload, Options);
        return ValueTask.FromResult(new CallToolResult
        {
            IsError = capabilities ? null : true,
            StructuredContent = structured,
            Content = [new TextContentBlock { Text = structured.GetRawText() }],
        });
    }
}
