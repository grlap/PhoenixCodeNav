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
    private static readonly JsonSerializerOptions Options = Json.Options;

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

    internal static JsonElement CreatePayload(
        DaemonUnavailableFailure failure,
        string toolName)
    {
        bool capabilities = string.Equals(
            toolName, "server_capabilities", StringComparison.Ordinal);
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
                    cause = failure.Cause,
                    detail = failure.Detail,
                    recovery = failure.Recovery,
                    retryable = failure.Retryable,
                },
                features = new[]
                {
                    new
                    {
                        id = "shared-mcp-daemon",
                        summary = "Phoenix shared daemon negotiation is present but unavailable for this session.",
                    },
                    new
                    {
                        id = "shared-mcp-daemon-default",
                        summary = "Phoenix's default shared-daemon topology is present but unavailable for this session.",
                    },
                },
            }
            : new
            {
                error = "phoenix_daemon_unavailable",
                tool = toolName,
                cause = failure.Cause,
                detail = failure.Detail,
                recovery = failure.Recovery,
                retryable = failure.Retryable,
                meta = new { indexMode = "unavailable" },
            };
        return JsonSerializer.SerializeToElement(payload, Options);
    }
}

internal sealed class UnavailableMcpServerTool : DelegatingMcpServerTool
{
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
        JsonElement structured = UnavailableMcpShim.CreatePayload(
            _failure, ProtocolTool.Name);
        return ValueTask.FromResult(new CallToolResult
        {
            IsError = capabilities ? null : true,
            StructuredContent = structured,
            Content = [new TextContentBlock { Text = structured.GetRawText() }],
        });
    }
}
