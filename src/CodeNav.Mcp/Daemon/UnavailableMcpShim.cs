using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace CodeNav.Mcp.Daemon;

internal sealed record DaemonUnavailableFailure
{
    public string Cause { get; }
    public string Detail { get; init; }
    public string Recovery { get; init; }
    public bool Retryable { get; init; }

    // Local emitters cannot introduce a cause without the catalog's explicit policy.
    internal DaemonUnavailableFailure(DaemonFailureCause cause, string Detail, string Recovery, bool Retryable)
        : this(cause.Id, Detail, Recovery, Retryable) { }

    [System.Text.Json.Serialization.JsonConstructor]
    private DaemonUnavailableFailure(string Cause, string Detail, string Recovery, bool Retryable)
    {
        this.Cause = Cause;
        this.Detail = Detail;
        this.Recovery = Recovery;
        this.Retryable = Retryable;
    }

    // Only received protocol causes use this path; unknown peers' causes fail closed.
    internal static DaemonUnavailableFailure FromRefusal(DaemonHandshakeResponse response, string Recovery, bool Retryable) =>
        new(response.Cause, response.Detail, Recovery, Retryable);

    // Computed from the same catalog after deserialization and with-copies; no wire field.
    internal bool CanRecoverInSession => DaemonFailureCause.CanRecover(Cause);
}

internal static class UnavailableMcpShim
{
    private static readonly JsonSerializerOptions Options = Json.Options;

    internal static async Task<int> RunAsync(
        DaemonUnavailableFailure failure,
        CancellationToken cancellationToken = default,
        Func<CancellationToken, Task<Stream>>? reconnect = null,
        Stream? input = null, Stream? output = null)
    {
        if ((input is null) != (output is null))
            throw new ArgumentException("Supply both input and output streams, or neither.");
        PhoenixRuntimeMode.Set(PhoenixProcessMode.UnavailableShim);
        await using var recovery = reconnect is null ? null : new DaemonSessionRecovery(failure, reconnect, cancellationToken);
        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        var mcp = builder.Services
            .AddMcpServer(options =>
            {
                options.ServerInfo = new()
                {
                    Name = recovery is null ? "phoenix-codenav-unavailable" : "phoenix-codenav",
                    Version = BuildInfo.Version,
                };
                options.ServerInstructions = recovery is null
                    ? "Phoenix shared daemon is unavailable. Tool calls return one typed cause and recovery action."
                    : "Phoenix tools reconnect to the existing shared daemon on demand after a transient startup failure. " +
                      "Inspect server_capabilities for current availability. Recovery retains this MCP session; " +
                      "calls already dispatched are never replayed.";
            });
        if (input is null)
            mcp.WithStdioServerTransport();
        else
            mcp.WithStreamServerTransport(input, output!);
        mcp.WithTools(ValidatedMcpToolRegistration.CreateNavigationTools()
                .Select(tool => (McpServerTool)new UnavailableMcpServerTool(tool, failure, recovery))
                .ToArray());
        using IHost host = builder.Build();
        await host.RunAsync(cancellationToken).ConfigureAwait(false);
        // Read before owned recovery disposal clears the connection. This describes the
        // established session, not proof that every tool succeeded or the daemon is still alive.
        return recovery?.HasEstablishedConnection == true ? 0 : 4;
    }

    internal static JsonElement CreatePayload(
        DaemonUnavailableFailure failure,
        string toolName,
        bool sessionRecoveryAvailable = false)
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
                    new
                    {
                        id = "shared-daemon-recovery-cause-policy",
                        summary = "v0.12.108 recovery by cause, not retryable advice",
                    },
                }.Concat(sessionRecoveryAvailable
                    ? [new { id = "shared-daemon-session-recovery", summary = "This MCP session can reconnect to an existing daemon on later tool calls; dispatched calls are not replayed." }]
                    : []).ToArray(),
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
    private readonly DaemonSessionRecovery? _recovery;

    internal UnavailableMcpServerTool(
        McpServerTool inner,
        DaemonUnavailableFailure failure,
        DaemonSessionRecovery? recovery = null)
        : base(inner) => (_failure, _recovery) = (failure, recovery);

    public override ValueTask<CallToolResult> InvokeAsync(
        RequestContext<CallToolRequestParams> request,
        CancellationToken cancellationToken = default)
    {
        if (_recovery is not null) return _recovery.InvokeAsync(request, cancellationToken);
        return ValueTask.FromResult(FailureResult(_failure, ProtocolTool.Name));
    }

    internal static CallToolResult FailureResult(DaemonUnavailableFailure failure, string toolName,
        bool sessionRecoveryAvailable = false)
    {
        bool capabilities = string.Equals(
            toolName, "server_capabilities", StringComparison.Ordinal);
        JsonElement structured = UnavailableMcpShim.CreatePayload(
            failure, toolName, sessionRecoveryAvailable);
        return new CallToolResult
        {
            IsError = capabilities ? null : true,
            StructuredContent = structured,
            Content = [new TextContentBlock { Text = structured.GetRawText() }],
        };
    }
}
