using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeNav.Mcp.Daemon;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace CodeNav.Tests;

public sealed partial class DaemonSessionRecoveryTests
{
    private static DaemonUnavailableFailure Retryable => DaemonWireTestData.Failure("daemon_handshake_timeout", "timeout", "retry", true);

    [Theory]
    [InlineData("repo_overview")]
    [InlineData("server_capabilities")]
    public void UnexpectedProxyFailurePayloadAdvisesReconnectAndRedactsExceptionDetails(string toolName)
    {
        var failure = DaemonProxy.MapProxyFailure(new InvalidOperationException("private exception detail"));
        var result = UnavailableMcpServerTool.FailureResult(failure, toolName);
        var payload = result.StructuredContent!.Value;
        var detail = toolName == "server_capabilities" ? payload.GetProperty("meta") : payload;
        Assert.Equal("daemon_proxy_failed", detail.GetProperty("cause").GetString());
        Assert.Contains("Retry the MCP connection", detail.GetProperty("recovery").GetString());
        Assert.DoesNotContain("private exception detail", detail.GetProperty("detail").GetString());
        Assert.True(detail.GetProperty("retryable").GetBoolean());
        // This checks the default failure payload, not the proxy's catch/delegate wiring.
        if (toolName == "server_capabilities")
            Assert.DoesNotContain(payload.GetProperty("features").EnumerateArray(),
                feature => feature.GetProperty("id").GetString() == "shared-daemon-session-recovery");
        else Assert.True(result.IsError);
    }

    public static TheoryData<string, bool, bool> RecoveryPolicies
    {
        get
        {
            var cases = new TheoryData<string, bool, bool>();
            string[] eligible =
            [
                "daemon_handshake_timeout", "daemon_startup_timeout", "daemon_launch_failed",
                "daemon_died_before_report", "daemon_startup_report_timeout", "daemon_startup_report_invalid",
                "daemon_writer_lease_unverifiable", "daemon_takeover_timeout", "daemon_older_than_client",
                "daemon_writer_unavailable", "daemon_writer_authority_unavailable", "daemon_index_validation_failed",
                "daemon_index_startup_failed", "daemon_startup_exception", "daemon_connection_unavailable",
            ];
            string[] terminal =
            [
                "daemon_proxy_failed", "daemon_response_authority_failed", "daemon_endpoint_authority_failed",
                "daemon_response_version_mismatch", "daemon_newer_than_client", "daemon_index_destination_mismatch",
                "daemon_index_rebuild_required", "daemon_index_destination_foreign", "daemon_index_destination_unsafe",
                "daemon_retiring", "daemon_request_outcome_unknown", "daemon_runtime_directory_unavailable", "unknown_future_failure",
            ];
            // Deliberately mismatch public advice and cause: these are policy controls,
            // not claims that every combination is emitted by current production code.
            foreach (bool retryable in new[] { false, true })
            {
                foreach (string cause in eligible) cases.Add(cause, retryable, true);
                foreach (string cause in terminal) cases.Add(cause, retryable, false);
            }
            return cases;
        }
    }

    [Theory]
    [MemberData(nameof(RecoveryPolicies))]
    public async Task SessionRecoveryEligibilityIsIndependentOfClientRetryAdvice(
        string cause, bool retryable, bool recoverable)
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        await using var transport = new StreamServerTransport(Stream.Null, Stream.Null, "recovery", NullLoggerFactory.Instance);
        await using var server = McpServer.Create(transport, new McpServerOptions(), NullLoggerFactory.Instance, services);
        var failure = DaemonWireTestData.Failure(cause, "failure", "recovery advice", retryable);
        int attempts = 0;
        await using var recovery = new DaemonSessionRecovery(failure, _ =>
        {
            Interlocked.Increment(ref attempts);
            throw new DaemonProxyFailureException(failure);
        }, CancellationToken.None);

        var tool = await recovery.InvokeAsync(Request(server), CancellationToken.None);
        var capabilities = await recovery.InvokeAsync(Request(server, "server_capabilities"), CancellationToken.None);

        Assert.Equal(cause, Cause(tool));
        Assert.Equal(retryable, tool.StructuredContent!.Value.GetProperty("retryable").GetBoolean());
        Assert.Equal(retryable, capabilities.StructuredContent!.Value.GetProperty("meta").GetProperty("retryable").GetBoolean());
        Assert.Equal(recoverable, AdvertisesRecovery(capabilities));
        Assert.Equal(recoverable ? 2 : 0, attempts);
    }

    [Fact]
    public async Task RecoveryPolicySurvivesStartupFrameAndRecordCopiesWithoutAProtocolField()
    {
        var failure = DaemonWireTestData.Failure("daemon_handshake_timeout", "timeout", "retry", false);
        using var frame = new MemoryStream();
        await DaemonStartupChannel.WriteAsync(frame, DaemonStartupReport.Refused(0, failure));
        using var payload = JsonDocument.Parse(frame.ToArray().AsMemory(sizeof(int)));
        Assert.Equal(new[] { "cause", "detail", "recovery", "retryable" },
            payload.RootElement.GetProperty("failure").EnumerateObject().Select(property => property.Name).ToArray());
        frame.Position = 0;
        var restored = Assert.IsType<DaemonUnavailableFailure>((await DaemonStartupChannel.ReadAsync(frame)).Failure);
        Assert.Equal(failure, restored);
        Assert.True(restored.CanRecoverInSession);
        Assert.True((restored with { Retryable = true }).CanRecoverInSession);
        Assert.False(DaemonWireTestData.Failure("daemon_proxy_failed", restored.Detail, restored.Recovery, Retryable: true).CanRecoverInSession);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnavailableCapabilitiesDiscloseOnlyEnabledSessionRecovery(bool enabled)
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        await using var transport = new StreamServerTransport(Stream.Null, Stream.Null, "recovery", NullLoggerFactory.Instance);
        await using var server = McpServer.Create(transport, new McpServerOptions(), NullLoggerFactory.Instance, services);
        await using var recovery = new DaemonSessionRecovery(Retryable,
            _ => throw new DaemonProxyFailureException(Retryable), CancellationToken.None);
        var result = enabled
            ? await recovery.InvokeAsync(Request(server, "server_capabilities"), CancellationToken.None)
            : UnavailableMcpServerTool.FailureResult(Retryable, "server_capabilities");
        Assert.Equal(enabled, result.StructuredContent!.Value.GetProperty("features").EnumerateArray()
            .Any(feature => feature.GetProperty("id").GetString() == "shared-daemon-session-recovery"));
    }

    [Theory]
    [InlineData("eof", false)]
    [InlineData("null-result", false)]
    [InlineData("malformed-result", false)]
    [InlineData("protocol-error", false)]
    [InlineData("initialize-protocol-error", false)]
    [InlineData("eof", true)]
    [InlineData("null-result", true)]
    [InlineData("malformed-result", true)]
    public async Task DispatchedRecoveryFailuresNeverReplayAndNextCallUsesAWorkingConnection(string failure, bool capabilities)
    {
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var firstPair = await StreamPairAsync(lifetime.Token);
        var secondPair = await StreamPairAsync(lifetime.Token);
        await using var firstClient = firstPair.Client;
        await using var firstServer = firstPair.Server;
        await using var secondClient = secondPair.Client;
        await using var secondServer = secondPair.Server;
        using var services = new ServiceCollection().BuildServiceProvider();
        await using var transport = new StreamServerTransport(Stream.Null, Stream.Null, "recovery", NullLoggerFactory.Instance);
        await using var server = McpServer.Create(transport, new McpServerOptions(), NullLoggerFactory.Instance, services);
        int connects = 0, mutations = 0;
        await using var recovery = new DaemonSessionRecovery(Retryable, _ =>
            Task.FromResult<Stream>(Interlocked.Increment(ref connects) == 1 ? firstClient : secondClient), lifetime.Token);
        Task first = ServeAsync(firstServer, failure);
        Task second = ServeAsync(secondServer, null);
        try
        {
            if (failure == "protocol-error")
            {
                var error = await Assert.ThrowsAsync<McpProtocolException>(() =>
                    recovery.InvokeAsync(Request(server, "mutate"), lifetime.Token).AsTask());
                Assert.Equal(McpErrorCode.InvalidParams, error.ErrorCode);
            }
            else
            {
                var lost = await recovery.InvokeAsync(Request(server, capabilities ? "server_capabilities" : "mutate"), lifetime.Token);
                bool initializeFailure = failure == "initialize-protocol-error";
                var detail = capabilities ? lost.StructuredContent!.Value.GetProperty("meta") : lost.StructuredContent!.Value;
                Assert.Equal(initializeFailure ? "daemon_connection_unavailable" : "daemon_request_outcome_unknown", detail.GetProperty("cause").GetString());
                Assert.Equal(initializeFailure, detail.GetProperty("retryable").GetBoolean());
                if (capabilities) Assert.True(AdvertisesRecovery(lost));
            }
            Assert.Equal(failure == "initialize-protocol-error" ? 0 : 1, mutations);
            Assert.Equal(1, connects); // The uncertain call itself was not replayed.
            var next = await recovery.InvokeAsync(Request(server, "echo"), lifetime.Token);
            Assert.Equal("working", Assert.IsType<TextContentBlock>(Assert.Single(next.Content)).Text);
            Assert.Equal(failure == "protocol-error" ? 1 : 2, connects);
            Assert.Equal(failure == "initialize-protocol-error" ? 0 : 1, mutations);
        }
        finally
        {
            lifetime.Cancel();
            try
            {
                // Observe cancellation before disposing streams still used by the peers.
                try { await Task.WhenAll(first, second); }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
                catch (IOException) when (lifetime.IsCancellationRequested) { }
            }
            finally
            {
                try { await firstServer.DisposeAsync(); }
                finally { await secondServer.DisposeAsync(); }
            }
        }

        async Task ServeAsync(NetworkStream stream, string? injectedFailure)
        {
            using var reader = new StreamReader(stream, leaveOpen: true);
            while (await reader.ReadLineAsync(lifetime.Token) is { } line)
            {
                using var message = JsonDocument.Parse(line);
                var root = message.RootElement;
                if (!root.TryGetProperty("id", out var id)) continue;
                string? method = root.GetProperty("method").GetString();
                object? result;
                if (method == "initialize")
                {
                    if (injectedFailure == "initialize-protocol-error")
                    {
                        await ReplyAsync(new { jsonrpc = "2.0", id, error = new { code = -32602, message = "initialize rejected" } });
                        continue;
                    }
                    result = new
                    {
                        protocolVersion = "2025-11-25",
                        capabilities = new { tools = new { } },
                        serverInfo = new { name = "wire-peer", version = "1" }
                    };
                }
                else
                {
                    Assert.Equal("tools/call", method);
                    if (root.GetProperty("params").GetProperty("name").GetString() is "mutate" or "server_capabilities")
                    {
                        Interlocked.Increment(ref mutations);
                        if (injectedFailure == "eof")
                        {
                            stream.Socket.Shutdown(SocketShutdown.Send); // FIN/EOF, not a reset.
                            return;
                        }
                        if (injectedFailure == "protocol-error")
                        {
                            await ReplyAsync(new { jsonrpc = "2.0", id, error = new { code = -32602, message = "rejected" } });
                            continue;
                        }
                        result = injectedFailure == "null-result" ? null : new { content = 42 };
                    }
                    else result = new { content = new[] { new { type = "text", text = "working" } } };
                }
                await ReplyAsync(new { jsonrpc = "2.0", id, result });
            }

            async Task ReplyAsync(object response)
            {
                await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(response) + "\n"), lifetime.Token);
                await stream.FlushAsync(lifetime.Token);
            }
        }
    }

    [Fact]
    public async Task ConcurrentRecoverySharesAttemptAndCancelledWaiterDoesNotCancelPeer()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        await using var transport = new StreamServerTransport(Stream.Null, Stream.Null, "recovery", NullLoggerFactory.Instance);
        await using var server = McpServer.Create(transport, new McpServerOptions(), NullLoggerFactory.Instance, services);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int attempts = 0;
        await using var recovery = new DaemonSessionRecovery(Retryable, async token =>
        {
            int attempt = Interlocked.Increment(ref attempts);
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            throw new DaemonProxyFailureException(attempt == 1 ? Retryable :
                DaemonWireTestData.Failure("daemon_response_authority_failed", "refused", "inspect", true));
        }, CancellationToken.None);
        using var cancelled = new CancellationTokenSource();
        var first = recovery.InvokeAsync(Request(server), cancelled.Token).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var peer = recovery.InvokeAsync(Request(server, "server_capabilities"), CancellationToken.None).AsTask();
        try
        {
            Assert.Equal(1, attempts);
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
            Assert.False(peer.IsCompleted);
        }
        finally { release.TrySetResult(); }
        var beforeRefusal = await peer;
        Assert.Equal("daemon_handshake_timeout", beforeRefusal.StructuredContent!.Value.GetProperty("meta").GetProperty("cause").GetString());
        Assert.True(AdvertisesRecovery(beforeRefusal));
        var refused = await recovery.InvokeAsync(Request(server, "server_capabilities"), CancellationToken.None);
        Assert.Equal("daemon_response_authority_failed", refused.StructuredContent!.Value.GetProperty("meta").GetProperty("cause").GetString());
        Assert.True(refused.StructuredContent!.Value.GetProperty("meta").GetProperty("retryable").GetBoolean());
        Assert.False(AdvertisesRecovery(refused));
        Assert.Equal(2, attempts);
        var stillRefused = await recovery.InvokeAsync(Request(server, "server_capabilities"), CancellationToken.None);
        Assert.Equal("daemon_response_authority_failed", stillRefused.StructuredContent!.Value.GetProperty("meta").GetProperty("cause").GetString());
        Assert.False(AdvertisesRecovery(stillRefused));
        Assert.Equal(2, attempts); // A permanent refusal is not retried or converted to success.
    }

    [Fact]
    public async Task RecoveryDisposalCancelsOwnedAttempt()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        await using var transport = new StreamServerTransport(Stream.Null, Stream.Null, "recovery", NullLoggerFactory.Instance);
        await using var server = McpServer.Create(transport, new McpServerOptions(), NullLoggerFactory.Instance, services);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool attemptCancelled = false;
        await using var recovery = new DaemonSessionRecovery(Retryable, async token =>
        {
            entered.SetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { attemptCancelled = token.IsCancellationRequested; }
            throw new InvalidOperationException("Unreachable");
        }, CancellationToken.None);
        Task<CallToolResult> call = recovery.InvokeAsync(Request(server), CancellationToken.None).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await recovery.DisposeAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        Assert.True(attemptCancelled);
    }

    [Fact]
    public async Task RecoveryForwardsMetadataResultsProgressAndCancellationWithoutReplayingLostCall()
    {
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var upstreamPair = await StreamPairAsync(lifetime.Token);
        var downstreamPair = await StreamPairAsync(lifetime.Token);
        await using var upstreamClientStream = upstreamPair.Client;
        await using var upstreamServerStream = upstreamPair.Server;
        await using var downstreamClientStream = downstreamPair.Client;
        await using var downstreamServerStream = downstreamPair.Server;
        using var services = new ServiceCollection().BuildServiceProvider();
        await using var upstreamTransport = new StreamServerTransport(upstreamServerStream, upstreamServerStream,
            "upstream", NullLoggerFactory.Instance);
        var cancelledAtServer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellableEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        RequestId cancellableRequestId = default;
        int mutations = 0;
        var upstreamOptions = new McpServerOptions
        {
            ServerInfo = new() { Name = "upstream", Version = "1" },
            Handlers = new()
            {
                CallToolHandler = async (context, token) =>
                {
                    var parameters = context.Params!;
                    if (parameters.Name == "cancel")
                    {
                        cancellableEntered.SetResult();
                        try { await Task.Delay(Timeout.Infinite, token); }
                        finally { if (token.IsCancellationRequested) cancelledAtServer.SetResult(); }
                    }
                    if (parameters.Name == "mutate")
                    {
                        Interlocked.Increment(ref mutations);
                        await upstreamServerStream.DisposeAsync();
                        await Task.Delay(Timeout.Infinite, token);
                    }
                    Assert.Equal("sentinel", parameters.Meta!["proof"]!.GetValue<string>());
                    Assert.Equal(42, parameters.Arguments!["value"].GetInt32());
                    await context.Server.SendNotificationAsync("notifications/progress", new JsonObject
                    {
                        ["progressToken"] = parameters.Meta["progressToken"]!.DeepClone(),
                        ["progress"] = 1,
                        ["total"] = 2,
                    }, cancellationToken: token);
                    return new CallToolResult
                    {
                        IsError = true,
                        Content = [new TextContentBlock { Text = "forwarded result" }],
                        StructuredContent = JsonSerializer.SerializeToElement(new { value = 42 }),
                        Meta = new JsonObject { ["reply"] = "kept" },
                    };
                },
            },
        };
        await using var upstream = McpServer.Create(upstreamTransport, upstreamOptions, NullLoggerFactory.Instance, services);
        Task upstreamTask = upstream.RunAsync(lifetime.Token);
        int connects = 0;
        await using var recovery = new DaemonSessionRecovery(Retryable, _ =>
        {
            Interlocked.Increment(ref connects);
            return Task.FromResult<Stream>(upstreamClientStream);
        }, lifetime.Token);
        await using var downstreamTransport = new StreamServerTransport(downstreamServerStream, downstreamServerStream,
            "downstream", NullLoggerFactory.Instance);
        await using var downstream = McpServer.Create(downstreamTransport, new McpServerOptions
        {
            ServerInfo = new() { Name = "recovery", Version = "1" },
            Handlers = new()
            {
                CallToolHandler = async (request, token) =>
            {
                if (request.Params?.Name == "cancel") cancellableRequestId = request.JsonRpcRequest.Id;
                return await recovery.InvokeAsync(request, token);
            }
            },
        }, NullLoggerFactory.Instance, services);
        Task downstreamTask = downstream.RunAsync(lifetime.Token);
        try
        {
            await using var client = await McpClient.CreateAsync(new StreamClientTransport(
                downstreamClientStream, downstreamClientStream, NullLoggerFactory.Instance), cancellationToken: lifetime.Token);
            var progress = new TaskCompletionSource<JsonRpcNotification>(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var registration = client.RegisterNotificationHandler("notifications/progress", (notification, _) =>
            {
                progress.TrySetResult(notification);
                return ValueTask.CompletedTask;
            });
            var result = await client.CallToolAsync(new CallToolRequestParams
            {
                Name = "echo",
                Arguments = new Dictionary<string, JsonElement> { ["value"] = JsonSerializer.SerializeToElement(42) },
                Meta = new JsonObject { ["proof"] = "sentinel", ["progressToken"] = "original-token" },
            }, lifetime.Token);
            Assert.True(result.IsError);
            Assert.Equal("forwarded result", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
            Assert.Equal(42, result.StructuredContent!.Value.GetProperty("value").GetInt32());
            Assert.Equal("kept", result.Meta!["reply"]!.GetValue<string>());
            Assert.Equal("original-token", (await progress.Task.WaitAsync(lifetime.Token)).Params!["progressToken"]!.GetValue<string>());
            Task<CallToolResult> cancelCall = client.CallToolAsync(new CallToolRequestParams { Name = "cancel" }, lifetime.Token).AsTask();
            await cancellableEntered.Task.WaitAsync(lifetime.Token);
            await client.SendMessageAsync(new JsonRpcNotification
            {
                Method = "notifications/cancelled",
                Params = JsonSerializer.SerializeToNode(new CancelledNotificationParams { RequestId = cancellableRequestId }),
            }, lifetime.Token);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelCall);
            await cancelledAtServer.Task.WaitAsync(lifetime.Token);
            var lost = await client.CallToolAsync(new CallToolRequestParams { Name = "mutate" }, lifetime.Token);
            Assert.Equal("daemon_request_outcome_unknown", Cause(lost));
            Assert.False(lost.StructuredContent!.Value.GetProperty("retryable").GetBoolean());
            Assert.Equal(1, mutations);
            Assert.Equal(1, connects);
        }
        finally
        {
            lifetime.Cancel();
            await downstreamServerStream.DisposeAsync();
            await upstreamServerStream.DisposeAsync();
            try { await downstreamTask; } catch (OperationCanceledException) { }
            try { await upstreamTask; }
            catch (OperationCanceledException) { }
            catch (IOException) when (mutations == 1) { } // This fixture deliberately closes the upstream socket.
        }
    }

    private static RequestContext<CallToolRequestParams> Request(McpServer server, string name = "repo_overview") =>
        new(server, new JsonRpcRequest { Id = new RequestId(1), Method = "tools/call" }, new() { Name = name });

    private static string? Cause(CallToolResult result) => result.StructuredContent!.Value.GetProperty("cause").GetString();

    private static bool AdvertisesRecovery(CallToolResult result) => result.StructuredContent!.Value
        .GetProperty("features").EnumerateArray()
        .Any(feature => feature.GetProperty("id").GetString() == "shared-daemon-session-recovery");

    private static async Task<(NetworkStream Client, NetworkStream Server)> StreamPairAsync(CancellationToken token)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = new TcpClient();
        Task connect = client.ConnectAsync((IPEndPoint)listener.LocalEndpoint, token).AsTask();
        using TcpClient accepted = await listener.AcceptTcpClientAsync(token);
        await connect;
        var clientStream = new NetworkStream(client.Client, ownsSocket: true);
        var serverStream = new NetworkStream(accepted.Client, ownsSocket: true);
        client.Client = null!;
        accepted.Client = null!;
        return (clientStream, serverStream);
    }
}
