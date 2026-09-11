using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using CodeNav.Core.Indexing;
using CodeNav.Mcp;
using CodeNav.Mcp.Daemon;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace CodeNav.Tests;

public sealed partial class DaemonSessionRecoveryTests
{
    [Fact]
    public async Task FailedInitializeCleanupPreservesTypedUnavailableResult()
    {
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var services = new ServiceCollection().BuildServiceProvider();
        await using var transport = new StreamServerTransport(Stream.Null, Stream.Null, "recovery", NullLoggerFactory.Instance);
        await using var server = McpServer.Create(transport, new McpServerOptions(), NullLoggerFactory.Instance, services);
        using var stream = new FailingDisposalStream();
        await using var recovery = new DaemonSessionRecovery(Retryable, _ => Task.FromResult<Stream>(stream), lifetime.Token, TextWriter.Null);
        var result = await recovery.InvokeAsync(Request(server, "echo"), lifetime.Token);
        Assert.Equal("daemon_connection_unavailable", Cause(result));
        Assert.True(result.StructuredContent!.Value.GetProperty("retryable").GetBoolean());
        Assert.False(recovery.HasEstablishedConnection);
        Assert.True(stream.Disposals > 0);
        Assert.False(stream.CanRead);
    }

    [Fact]
    public async Task FinalRecoveryCleanupDoesNotReplaceEstablishedExitDecision()
    {
        await using var client = new CancellationClient { FailFirstDisposal = true };
        using var stream = new MemoryStream();
        var recovery = new DaemonSessionRecovery(Retryable, _ => throw new InvalidOperationException("No reconnect"), CancellationToken.None, TextWriter.Null);
        SetEstablishedClient(recovery, client, stream);
        async Task<int> ShutdownAsync()
        {
            await using (recovery)
                return recovery.HasEstablishedConnection ? 0 : 4;
        }
        Assert.Equal(0, await ShutdownAsync());
        Assert.False(recovery.HasEstablishedConnection);
        Assert.Equal(1, client.Disposals);
        Assert.False(stream.CanRead);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task DispatchedFailurePreservesTypedOutcomeWhenDisposalFails(bool failDispose, bool capabilities)
    {
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var services = new ServiceCollection().BuildServiceProvider();
        await using var transport = new StreamServerTransport(Stream.Null, Stream.Null, "recovery", NullLoggerFactory.Instance);
        await using var server = McpServer.Create(transport, new McpServerOptions(), NullLoggerFactory.Instance, services);
        await using var client = new CancellationClient
        {
            FailFirstDisposal = failDispose,
            RequestFailure = new IOException("Controlled dispatched transport loss"),
        };
        await using var replacement = new CancellationClient();
        using var stream = new MemoryStream();
        using var replacementStream = new MemoryStream();
        int attempts = 0;
        await using var recovery = new DaemonSessionRecovery(Retryable, _ =>
        {
            attempts++;
            throw new InvalidOperationException("The dispatched request must not be replayed");
        }, lifetime.Token, TextWriter.Null);
        SetEstablishedClient(recovery, client, stream);
        var result = await recovery.InvokeAsync(Request(server, capabilities ? "server_capabilities" : "mutate"), lifetime.Token);
        var detail = capabilities ? result.StructuredContent!.Value.GetProperty("meta") : result.StructuredContent!.Value;
        Assert.Equal("daemon_request_outcome_unknown", detail.GetProperty("cause").GetString());
        Assert.False(detail.GetProperty("retryable").GetBoolean());
        Assert.Contains("may have executed and was not replayed", detail.GetProperty("detail").GetString());
        if (capabilities) Assert.True(AdvertisesRecovery(result));
        Assert.False(recovery.HasEstablishedConnection);
        Assert.False(stream.CanRead);
        Assert.Equal(1, client.Requests);
        Assert.Equal(1, client.Disposals);
        Assert.Equal(0, attempts);
        SetEstablishedClient(recovery, replacement, replacementStream);
        var next = await recovery.InvokeAsync(Request(server, "echo"), lifetime.Token);
        Assert.Equal("working", Assert.IsType<TextContentBlock>(Assert.Single(next.Content)).Text);
        Assert.True(recovery.HasEstablishedConnection);
        Assert.Equal(1, client.Requests);
        Assert.Equal(1, replacement.Requests);
        Assert.Equal(0, replacement.Disposals);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CancellationForwardingTransportFailureInvalidatesConnectionAndNextCallReconnects(bool failSend, bool failDispose)
    {
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var caller = new CancellationTokenSource();
        using var services = new ServiceCollection().BuildServiceProvider();
        await using var transport = new StreamServerTransport(Stream.Null, Stream.Null, "recovery", NullLoggerFactory.Instance);
        await using var server = McpServer.Create(transport, new McpServerOptions(), NullLoggerFactory.Instance, services);
        await using var client = new CancellationClient { FailFirstDisposal = failDispose };
        using var stream = new MemoryStream();
        int attempts = 0;
        await using var recovery = new DaemonSessionRecovery(Retryable, _ =>
        {
            Interlocked.Increment(ref attempts);
            throw new DaemonProxyFailureException(Retryable);
        }, lifetime.Token, TextWriter.Null);
        SetEstablishedClient(recovery, client, stream);
        Assert.True(recovery.HasEstablishedConnection);
        client.ForwardCancellation = _ => failSend
            ? Task.FromException(new IOException("Cancellation transport lost")) : Task.CompletedTask;

        Task<CallToolResult> call = recovery.InvokeAsync(Request(server, "cancel"), caller.Token).AsTask();
        await client.Entered.Task.WaitAsync(lifetime.Token);
        caller.Cancel();
        var cancelled = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call.WaitAsync(lifetime.Token));
        client.FailFirstDisposal = false; // The injected failure belongs to product cleanup, not fixture teardown.

        Assert.Equal(caller.Token, cancelled.CancellationToken);
        Assert.Equal(1, client.CancellationSends);
        Assert.Equal(!failSend, recovery.HasEstablishedConnection);
        Assert.Equal(failSend ? 1 : 0, client.Disposals);
        Assert.Equal(!failSend, stream.CanRead);
        Assert.Equal(0, attempts); // Never replay the cancelled request.
        var next = await recovery.InvokeAsync(Request(server, "echo"), lifetime.Token);
        Assert.Equal(failSend ? 1 : 0, attempts);
        if (failSend)
        {
            Assert.Equal("daemon_handshake_timeout", Cause(next));
            Assert.False(recovery.HasEstablishedConnection);
        }
        else
        {
            Assert.Equal("working", Assert.IsType<TextContentBlock>(Assert.Single(next.Content)).Text);
            Assert.True(recovery.HasEstablishedConnection);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationForwardingDistinguishesShutdownFromObservedTransportLoss(bool transportFailure)
    {
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var caller = new CancellationTokenSource();
        using var services = new ServiceCollection().BuildServiceProvider();
        await using var transport = new StreamServerTransport(Stream.Null, Stream.Null, "recovery", NullLoggerFactory.Instance);
        await using var server = McpServer.Create(transport, new McpServerOptions(), NullLoggerFactory.Instance, services);
        await using var client = new CancellationClient();
        using var stream = new MemoryStream();
        await using var recovery = new DaemonSessionRecovery(Retryable,
            _ => throw new InvalidOperationException("No reconnect during cancellation"), lifetime.Token);
        SetEstablishedClient(recovery, client, stream);
        Assert.True(recovery.HasEstablishedConnection);
        client.ForwardCancellation = token =>
        {
            lifetime.Cancel();
            return transportFailure ? Task.FromException(new IOException("Observed failure during shutdown"))
                : Task.FromCanceled(token);
        };
        Task<CallToolResult> call = recovery.InvokeAsync(Request(server, "cancel"), caller.Token).AsTask();
        await client.Entered.Task.WaitAsync(lifetime.Token);
        caller.Cancel();
        var cancelled = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        Assert.Equal(caller.Token, cancelled.CancellationToken);
        Assert.Equal(1, client.CancellationSends);
        Assert.Equal(!transportFailure, recovery.HasEstablishedConnection);
        Assert.Equal(transportFailure ? 1 : 0, client.Disposals);
    }

    [Fact]
    public async Task LateCancellationTransportFailureDoesNotInvalidateReplacementConnection()
    {
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var caller = new CancellationTokenSource();
        using var services = new ServiceCollection().BuildServiceProvider();
        await using var transport = new StreamServerTransport(Stream.Null, Stream.Null, "recovery", NullLoggerFactory.Instance);
        await using var server = McpServer.Create(transport, new McpServerOptions(), NullLoggerFactory.Instance, services);
        await using var first = new CancellationClient();
        await using var replacement = new CancellationClient();
        using var firstStream = new MemoryStream();
        using var replacementStream = new MemoryStream();
        var forwarding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        first.ForwardCancellation = async token =>
        {
            forwarding.TrySetResult();
            await release.Task.WaitAsync(token);
            throw new IOException("Old transport lost");
        };
        await using var recovery = new DaemonSessionRecovery(Retryable,
            _ => throw new InvalidOperationException("Replacement must be retained"), lifetime.Token);
        SetEstablishedClient(recovery, first, firstStream);
        Assert.True(recovery.HasEstablishedConnection);
        Task<CallToolResult> call = recovery.InvokeAsync(Request(server, "cancel"), caller.Token).AsTask();
        try
        {
            await first.Entered.Task.WaitAsync(lifetime.Token);
            caller.Cancel();
            await forwarding.Task.WaitAsync(lifetime.Token);
            SetEstablishedClient(recovery, replacement, replacementStream);
            Assert.True(recovery.HasEstablishedConnection);
        }
        finally { release.TrySetResult(); }
        var cancelled = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call.WaitAsync(lifetime.Token));
        Assert.Equal(caller.Token, cancelled.CancellationToken);
        Assert.True(recovery.HasEstablishedConnection);
        Assert.True(replacementStream.CanRead);
        Assert.Equal(0, replacement.Disposals);
        var next = await recovery.InvokeAsync(Request(server, "echo"), lifetime.Token);
        Assert.Equal("working", Assert.IsType<TextContentBlock>(Assert.Single(next.Content)).Text);
        Assert.Equal(1, first.Requests);
        Assert.Equal(1, replacement.Requests);
    }

    [Fact]
    public void RecoveryPolicyIsDiscoverableInHealthyAndUnavailableManifestsWithoutPromisingRecovery()
    {
        const string policy = "shared-daemon-recovery-cause-policy";
        var health = new IndexHealth("ready", "11", "indexed", "refreshed", 0, null, 123,
            "C:/" + new string('r', 257), "index.db");
        foreach (bool detail in new[] { false, true })
        {
            using var json = JsonDocument.Parse(NavigationTools.ServerCapabilitiesForTest(health, detail: detail));
            var entry = Assert.Single(json.RootElement.GetProperty("features").EnumerateArray(),
                feature => feature.GetProperty("id").GetString() == policy);
            Assert.Equal(detail, entry.TryGetProperty("summary", out var summary));
            if (detail)
            {
                Assert.Contains("v0.12.108", summary.GetString());
                Assert.Contains("cause", summary.GetString());
                Assert.Contains("retryable advice", summary.GetString());
            }
        }
        Assert.Contains(policy, NavigationTools.CapabilityFeatureIds(health));
        foreach (bool recoverable in new[] { false, true })
        {
            var failure = recoverable ? Retryable : DaemonWireTestData.Failure(
                "daemon_index_rebuild_required", "rebuild", "reconnect", false);
            var result = UnavailableMcpServerTool.FailureResult(failure, "server_capabilities", failure.CanRecoverInSession);
            Assert.Single(result.StructuredContent!.Value.GetProperty("features").EnumerateArray(),
                feature => feature.GetProperty("id").GetString() == policy);
            Assert.Equal(recoverable, AdvertisesRecovery(result));
            Assert.True(Json.Utf8Bytes(result.StructuredContent.Value.GetRawText()) <= Json.HardBudgetBytes);
        }
        int bytes = Json.Utf8Bytes(NavigationTools.ServerCapabilitiesUncompactedForTest(health));
        Assert.True(Json.HardBudgetBytes - bytes >= 2 * 1024, $"Manifest has only {Json.HardBudgetBytes - bytes} bytes reserve");
    }

    [Fact]
    public void EveryIndexFailureFactoryHasAnExplicitRecoveryExpectation()
    {
        var expected = new Dictionary<IndexStartupFailureCause, (string Cause, bool Recovery)>
        {
            [IndexStartupFailureCause.None] = ("daemon_index_startup_failed", true),
            [IndexStartupFailureCause.DestinationUnsafe] = ("daemon_index_destination_unsafe", false),
            [IndexStartupFailureCause.WriterLeaseContended] = ("daemon_writer_unavailable", true),
            [IndexStartupFailureCause.WriterAuthorityUnavailable] = ("daemon_writer_authority_unavailable", true),
            [IndexStartupFailureCause.DestinationChanged] = ("daemon_index_destination_unsafe", false),
            [IndexStartupFailureCause.DestinationForeign] = ("daemon_index_destination_foreign", false),
            [IndexStartupFailureCause.RebuildRequired] = ("daemon_index_rebuild_required", false),
            [IndexStartupFailureCause.DestinationValidationFailed] = ("daemon_index_validation_failed", true),
        };
        Assert.Equal(Enum.GetValues<IndexStartupFailureCause>().Order(), expected.Keys.Order());
        foreach (var (input, wanted) in expected)
        {
            DaemonUnavailableFailure failure = DaemonStartupFailures.FromIndexFailure(input);
            Assert.Equal(wanted.Cause, failure.Cause);
            Assert.Equal(wanted.Recovery, failure.CanRecoverInSession);
            Assert.Equal(wanted.Recovery, (failure with { Retryable = !failure.Retryable }).CanRecoverInSession);
        }
    }

    [Fact]
    public void StartupRetirementAndProxyFactoriesKeepExplicitRecoveryPolicy()
    {
        var failures = new[]
        {
            (DaemonStartupFailures.Unexpected(new InvalidOperationException("test")), "daemon_startup_exception", true),
            (DaemonProxy.MapRetirementFailure(new DaemonWriterLeaseUnverifiableException()), "daemon_writer_lease_unverifiable", true),
            (DaemonProxy.MapRetirementFailure(new OperationCanceledException()), "daemon_takeover_timeout", true),
            (DaemonProxy.MapProxyFailure(new IOException("test")), "daemon_proxy_failed", false),
        };
        foreach (var (failure, cause, recovery) in failures)
        {
            Assert.Equal(cause, failure.Cause);
            Assert.Equal(recovery, failure.CanRecoverInSession);
            Assert.Equal(recovery, (failure with { Retryable = !failure.Retryable }).CanRecoverInSession);
        }
        Assert.True(failures[^1].Item1.Retryable); // Advice can differ from session eligibility.
    }

    [Theory]
    [InlineData(false, "daemon_endpoint_authority_failed")]
    [InlineData(true, "daemon_response_authority_failed")]
    public void RetirementAuthorityFactoryPreservesTheRefusalBoundary(bool responseFailure, string cause)
    {
        var failure = DaemonProxy.MapRetirementFailure(new DaemonAuthorityException(
            "Private authority detail", responseFailure: responseFailure));
        Assert.Equal(cause, failure.Cause);
        Assert.False(failure.Retryable);
        Assert.False(failure.CanRecoverInSession);
        Assert.False((failure with { Retryable = true }).CanRecoverInSession);
        Assert.DoesNotContain("Private authority detail", failure.Detail);
        Assert.DoesNotContain("retry a tool call in this MCP session", failure.Recovery);
    }

    [Fact]
    public void SharedStartupFactoriesPreserveEveryFailureFieldAndRecoveryPolicy()
    {
        var failures = new[]
        {
            (DaemonStartupFailures.LaunchFailed(new IOException("Private launch details")), DaemonWireTestData.Failure(
                "daemon_launch_failed",
                "Phoenix daemon process could not be launched (IOException).",
                "Verify the deployed Phoenix executable and retry the MCP connection.", true)),
            (DaemonStartupFailures.ReportTimeout(), DaemonWireTestData.Failure(
                "daemon_startup_report_timeout",
                "Phoenix daemon did not report ready or refused before the startup deadline.",
                "Retry the MCP connection; if this repeats, inspect the Phoenix server log for a blocked startup.", true)),
            (DaemonStartupFailures.InvalidReport(), DaemonWireTestData.Failure(
                "daemon_startup_report_invalid",
                "Phoenix daemon returned an invalid private startup report.",
                "Restart active Phoenix sessions for this workspace, then reconnect.", true)),
        };
        foreach (var (actual, expected) in failures)
        {
            Assert.Equal(expected, actual);
            Assert.True(actual.CanRecoverInSession);
            Assert.True((actual with { Retryable = false }).CanRecoverInSession);
        }
    }

    [Theory]
    [InlineData(null, false, "Phoenix daemon closed its startup channel before reporting startup state.")]
    [InlineData(null, true, "Phoenix daemon bootstrap closed its startup channel before reporting startup state.")]
    [InlineData(7, false, "Phoenix daemon exited with code 7 before reporting startup state.")]
    [InlineData(7, true, "Phoenix daemon bootstrap exited with code 7 before reporting startup state.")]
    public void SharedStartupExitFactoryPreservesProcessAndExitCodeWording(int? exitCode, bool bootstrap, string detail)
    {
        var failure = DaemonStartupFailures.DiedBeforeReport(exitCode, bootstrap);
        Assert.Equal(DaemonWireTestData.Failure("daemon_died_before_report", detail,
            "Retry the MCP connection; if this repeats, inspect the Phoenix server log for the startup failure.", true), failure);
        Assert.True(failure.CanRecoverInSession);
        Assert.True((failure with { Retryable = false }).CanRecoverInSession);
    }

    [Theory]
    [InlineData("deadline", "daemon_startup_timeout")]
    [InlineData("eof", "daemon_died_before_report")]
    [InlineData("invalid", "daemon_startup_report_invalid")]
    [InlineData("timeout", "daemon_startup_report_timeout")]
    public async Task StartupReportFailuresProducedByProxyRemainRecoverable(string input, string cause)
    {
        using var process = new Process(); // Unstarted and owned: no external process is targeted.
        using Stream report = input == "timeout" ? new CancelledReportStream()
            : new MemoryStream(input == "invalid" ? [0, 0, 0, 1] : []);
        DateTime deadline = input == "deadline" ? DateTime.UtcNow.AddSeconds(-1) : DateTime.UtcNow.AddSeconds(15);
        var error = await Assert.ThrowsAsync<DaemonProxyFailureException>(() =>
            DaemonProxy.ReadStartupReportAsync(process, deadline, CancellationToken.None, report));
        Assert.Equal(cause, error.Failure.Cause);
        Assert.True(error.Failure.CanRecoverInSession);
        Assert.True((error.Failure with { Retryable = false }).CanRecoverInSession);
        var expected = input switch
        {
            "deadline" => DaemonWireTestData.Failure("daemon_startup_timeout",
                "Phoenix daemon startup deadline elapsed before a ready or refused report was received.",
                "Retry the MCP connection; if this repeats, inspect the Phoenix server log.", true),
            "eof" => DaemonStartupFailures.DiedBeforeReport(null, bootstrap: true),
            "invalid" => DaemonStartupFailures.InvalidReport(),
            "timeout" => DaemonStartupFailures.ReportTimeout(),
            _ => throw new ArgumentOutOfRangeException(nameof(input)),
        };
        Assert.Equal(expected, error.Failure);
        if (input == "deadline") Assert.Null(error.InnerException);
        else if (input == "eof") Assert.IsType<EndOfStreamException>(error.InnerException);
        else if (input == "invalid") Assert.IsType<IOException>(error.InnerException);
        else Assert.IsType<OperationCanceledException>(error.InnerException);
    }

    private sealed class CancelledReportStream : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(new OperationCanceledException("Controlled report timeout"));
    }

    private sealed class FailingDisposalStream : MemoryStream
    {
        internal int Disposals { get; private set; }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(new IOException("Controlled initialize transport loss"));
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new IOException("Controlled initialize transport loss"));
        public override ValueTask DisposeAsync()
        {
            Disposals++;
            base.Dispose();
            if (Disposals == 1) throw new InvalidOperationException("Controlled stream disposal failure");
            return ValueTask.CompletedTask;
        }
    }

    private static void SetEstablishedClient(DaemonSessionRecovery recovery, McpClient client, Stream stream)
    {
        // Inject only the already-established slot to force the otherwise timing-dependent
        // notification failure/interleaving. Real SDK initialize and process exit are covered
        // by the wire-peer and SharedDaemonTests controls; this is not an initialize simulation.
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var sync = typeof(DaemonSessionRecovery).GetField("_sync", flags)!.GetValue(recovery)!;
        lock (sync)
        {
            typeof(DaemonSessionRecovery).GetField("_client", flags)!.SetValue(recovery, client);
            typeof(DaemonSessionRecovery).GetField("_stream", flags)!.SetValue(recovery, stream);
        }
    }

    // The SDK marks client subclassing experimental; keep that opt-in local to this test double.
#pragma warning disable MCPEXP002
    private sealed class CancellationClient : McpClient
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Func<CancellationToken, Task> ForwardCancellation { get; set; } = _ => Task.CompletedTask;
        internal int CancellationSends { get; private set; }
        internal int Requests { get; private set; }
        internal int Disposals { get; private set; }
        internal bool FailFirstDisposal { get; set; }
        internal Exception? RequestFailure { get; set; }
        public override ServerCapabilities ServerCapabilities => new();
        public override Implementation ServerInfo => new() { Name = "controlled-established-client", Version = "1" };
        public override string? ServerInstructions => null;
        public override string? SessionId => null;
        public override string NegotiatedProtocolVersion => "2025-11-25";
        public override Task<ClientCompletionDetails> Completion => throw new NotSupportedException();

        public override async Task<JsonRpcResponse> SendRequestAsync(JsonRpcRequest request, CancellationToken cancellationToken = default)
        {
            Requests++;
            if (RequestFailure is not null) throw RequestFailure;
            if (request.Params!["name"]!.GetValue<string>() == "cancel")
            {
                Entered.TrySetResult();
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            return new JsonRpcResponse
            {
                Id = request.Id,
                Result = JsonSerializer.SerializeToNode(
                new CallToolResult { Content = [new TextContentBlock { Text = "working" }] })
            };
        }

        public override Task SendMessageAsync(JsonRpcMessage message, CancellationToken cancellationToken = default)
        {
            Assert.Equal("notifications/cancelled", Assert.IsType<JsonRpcNotification>(message).Method);
            CancellationSends++;
            return ForwardCancellation(cancellationToken);
        }

        public override IAsyncDisposable RegisterNotificationHandler(string method,
            Func<JsonRpcNotification, CancellationToken, ValueTask> handler) => throw new NotSupportedException();

        public override ValueTask DisposeAsync()
        {
            Disposals++;
            if (FailFirstDisposal && Disposals == 1)
                throw new InvalidOperationException("Unexpected disposal failure must not replace caller cancellation");
            return ValueTask.CompletedTask;
        }
    }
#pragma warning restore MCPEXP002
}
