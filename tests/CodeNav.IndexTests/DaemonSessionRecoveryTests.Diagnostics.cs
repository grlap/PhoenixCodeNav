using CodeNav.Mcp.Daemon;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Server;

namespace CodeNav.Tests;

public sealed partial class DaemonSessionRecoveryTests
{
    [Theory]
    [InlineData("initialize", false)]
    [InlineData("initialize", true)]
    [InlineData("dispatch", false)]
    [InlineData("dispatch", true)]
    [InlineData("cancel", false)]
    [InlineData("cancel", true)]
    [InlineData("shutdown", false)]
    [InlineData("shutdown", true)]
    [InlineData("connecting", false)]
    [InlineData("connecting", true)]
    [InlineData("completed-connect", false)]
    [InlineData("completed-connect", true)]
    public async Task CleanupDiagnosticsPreservePrimaryOutcomeEvenWhenWriterThrows(string phase, bool failWriter)
    {
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var caller = new CancellationTokenSource();
        using var services = new ServiceCollection().BuildServiceProvider();
        await using var transport = new StreamServerTransport(Stream.Null, Stream.Null, "cleanup", NullLoggerFactory.Instance);
        await using var server = McpServer.Create(transport, new McpServerOptions(), NullLoggerFactory.Instance, services);
        using var error = new CleanupDiagnosticWriter(failWriter);
        using Stream stream = phase == "initialize" ? new FailingDisposalStream() : new MemoryStream();
        await using var client = new CancellationClient
        {
            FailFirstDisposal = phase is "dispatch" or "cancel" or "shutdown",
            RequestFailure = phase == "dispatch" ? new IOException("Private transport details") : null,
            ForwardCancellation = _ => Task.FromException(new IOException("Private cancellation details")),
        };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int attempts = 0;
        await using var recovery = new DaemonSessionRecovery(Retryable, async token =>
        {
            attempts++;
            if (phase == "initialize") return stream;
            if (phase == "completed-connect") throw new InvalidOperationException("Private completed connect fault");
            Assert.Equal("connecting", phase);
            entered.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw new InvalidOperationException("Private connect fault during shutdown");
            }
            throw new InvalidOperationException("Unexpected fixture continuation");
        }, lifetime.Token, error);
        if (phase is "dispatch" or "cancel" or "shutdown") SetEstablishedClient(recovery, client, stream);

        if (phase == "cancel")
        {
            var call = recovery.InvokeAsync(Request(server, "cancel"), caller.Token).AsTask();
            await client.Entered.Task.WaitAsync(lifetime.Token);
            caller.Cancel();
            var cancelled = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
            Assert.Equal(caller.Token, cancelled.CancellationToken);
            Assert.Equal(1, client.CancellationSends);
            Assert.Equal(1, client.Requests);
        }
        else if (phase == "completed-connect")
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => recovery.InvokeAsync(Request(server), lifetime.Token).AsTask());
            Assert.Equal(0, error.Attempts);
            await recovery.DisposeAsync();
            await recovery.DisposeAsync(); // Observe a retained fault once, not once per disposal call.
        }
        else if (phase == "connecting")
        {
            var call = recovery.InvokeAsync(Request(server), lifetime.Token).AsTask();
            await entered.Task.WaitAsync(lifetime.Token);
            await recovery.DisposeAsync();
            // The original attempt still fails for its waiter; observing it during disposal
            // must neither rethrow nor replace it with an error from the diagnostic writer.
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => call);
            Assert.Equal("Private connect fault during shutdown", failure.Message);
        }
        else if (phase == "shutdown")
        {
            Assert.True(recovery.HasEstablishedConnection);
            await recovery.DisposeAsync();
        }
        else
        {
            var result = await recovery.InvokeAsync(Request(server, "mutate"), lifetime.Token);
            var payload = result.StructuredContent!.Value;
            Assert.Equal(phase == "initialize" ? "daemon_connection_unavailable" : "daemon_request_outcome_unknown",
                payload.GetProperty("cause").GetString());
            Assert.Equal(phase == "initialize", payload.GetProperty("retryable").GetBoolean());
            if (phase == "dispatch")
            {
                Assert.Contains("may have executed and was not replayed", payload.GetProperty("detail").GetString());
                Assert.Equal(1, client.Requests);
            }
        }
        Assert.False(recovery.HasEstablishedConnection);
        Assert.Equal(phase is "initialize" or "connecting" or "completed-connect" ? 1 : 0, attempts);
        string failureDescription = phase is "connecting" or "completed-connect"
            ? "connect attempt failed (InvalidOperationException); observed at shutdown."
            : $"{(phase == "initialize" ? "stream" : "client")} cleanup failed (InvalidOperationException).";
        Assert.Equal(1, error.Attempts);
        Assert.Equal($"Phoenix warning: daemon recovery {failureDescription}{Environment.NewLine}", error.ToString());
        Assert.DoesNotContain("Private", error.ToString());
        if (phase is not "connecting" and not "completed-connect") Assert.False(stream.CanRead);
        client.FailFirstDisposal = false;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExpectedTransportCleanupFailuresRemainSilent(bool cancelled)
    {
        using var error = new CleanupDiagnosticWriter(fail: false);
        await using var client = new CancellationClient();
        using var stream = new TransportCleanupStream(cancelled);
        await using var recovery = new DaemonSessionRecovery(Retryable,
            _ => throw new InvalidOperationException("No reconnect"), CancellationToken.None, error);
        SetEstablishedClient(recovery, client, stream);
        await recovery.DisposeAsync();
        Assert.False(recovery.HasEstablishedConnection);
        Assert.False(stream.CanRead);
        Assert.Equal(0, error.Attempts);
        Assert.Equal("", error.ToString());
    }

    private sealed class TransportCleanupStream(bool cancelled) : MemoryStream
    {
        public override ValueTask DisposeAsync()
        {
            base.Dispose();
            return ValueTask.FromException(cancelled ? new OperationCanceledException() : new IOException("Expected closed transport"));
        }
    }

    private sealed class CleanupDiagnosticWriter(bool fail) : StringWriter
    {
        internal int Attempts { get; private set; }
        public override void WriteLine(string? value)
        {
            Attempts++;
            base.WriteLine(value);
            if (fail) throw new IOException("Private diagnostic sink failure");
        }
    }
}
