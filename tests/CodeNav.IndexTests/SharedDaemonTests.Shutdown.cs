using System.IO.Pipelines;
using System.Text.Json.Nodes;
using CodeNav.Mcp;
using CodeNav.Mcp.Daemon;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace CodeNav.Tests;

public sealed partial class SharedDaemonTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task RecoveryShimRunAsyncKeepsExitStatusWhenReconnectFaultsDuringShutdown(
        bool cancelShutdown, bool wrappedCancellation)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var shutdown = new CancellationTokenSource();
        using var caller = new CancellationTokenSource();
        var input = new Pipe();
        var output = new Pipe();
        await using Stream serverInput = input.Reader.AsStream();
        await using Stream clientInput = input.Writer.AsStream();
        await using Stream serverOutput = output.Writer.AsStream();
        await using Stream clientOutput = output.Reader.AsStream();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int attempts = 0;
        McpClient? client = null;
        Task? call = null;
        Task<int>? run = null;
        PhoenixProcessMode previousMode = PhoenixRuntimeMode.Current;
        bool succeeded = false;
        try
        {
            var failure = new DaemonUnavailableFailure(DaemonFailureCause.HandshakeTimeout, "timeout", "retry", true);
            run = UnavailableMcpShim.RunAsync(failure, shutdown.Token, async token =>
            {
                Interlocked.Increment(ref attempts);
                entered.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, token); }
                catch (OperationCanceledException ex) when (token.IsCancellationRequested)
                {
                    cancelled.TrySetResult();
                    if (wrappedCancellation)
                        throw new DaemonEndpointUnavailableException("Controlled cancelled connect", ex);
                    throw;
                }
                throw new InvalidOperationException("Reconnect must wait for shutdown.");
            }, serverInput, serverOutput);
            client = await McpClient.CreateAsync(new StreamClientTransport(clientInput, clientOutput,
                NullLoggerFactory.Instance), cancellationToken: timeout.Token);
            Assert.Equal("phoenix-codenav", client.ServerInfo.Name);
            var requestId = new RequestId("shutdown-connect");
            call = client.SendRequestAsync(new JsonRpcRequest
            {
                Id = requestId,
                Method = "tools/call",
                Params = new JsonObject { ["name"] = "server_capabilities" },
            }, caller.Token);
            await entered.Task.WaitAsync(timeout.Token);
            Assert.Equal(1, attempts);
            Assert.False(cancelled.Task.IsCompleted);

            // Await the notification write before EOF; the SDK's fire-and-forget
            // cancellation notification can otherwise be lost while its waiter unwinds.
            await client.SendMessageAsync(new JsonRpcNotification
            {
                Method = "notifications/cancelled",
                Params = new JsonObject { ["requestId"] = "shutdown-connect" },
            }, timeout.Token);
            caller.Cancel();
            _ = await Record.ExceptionAsync(() => call.WaitAsync(timeout.Token));
            Assert.False(cancelled.Task.IsCompleted); // The shared attempt outlives its waiter.

            if (cancelShutdown) await shutdown.CancelAsync();
            else await clientInput.DisposeAsync(); // EOF at the actual host's stream transport.

            Assert.Equal(4, await run.WaitAsync(timeout.Token));
            Assert.True(cancelled.Task.IsCompletedSuccessfully);
            Assert.Equal(1, attempts); // No replay or replacement daemon.
            succeeded = true;
        }
        finally
        {
            try
            {
                await shutdown.CancelAsync();
                caller.Cancel();
                if (call is not null) _ = await Record.ExceptionAsync(() => call.WaitAsync(timeout.Token));
                if (client is not null) await DisposeClientForCleanupAsync(client, succeeded);
            }
            finally
            {
                try
                {
                    if (run is not null)
                        try { await run.WaitAsync(TimeSpan.FromSeconds(15)); }
                        catch when (!succeeded) { } // Preserve the original assertion, including the RED.
                }
                finally { PhoenixRuntimeMode.Set(previousMode); }
            }
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task ProxyUnavailableFallbackKeepsSuppliedStreams(bool recoverable, bool unclassified)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var shutdown = new CancellationTokenSource();
        string root = Directory.CreateTempSubdirectory("Phoenix shim streams ").FullName;
        var endpoint = DaemonEndpoint.Create(root, null);
        var input = new Pipe();
        var output = new Pipe();
        await using Stream serverInput = input.Reader.AsStream();
        await using Stream clientInput = input.Writer.AsStream();
        await using Stream serverOutput = output.Writer.AsStream();
        await using Stream clientOutput = output.Reader.AsStream();
        McpClient? client = null;
        Task<int>? run = null;
        var previous = PhoenixRuntimeMode.Current;
        bool succeeded = false;
        try
        {
            var failure = new DaemonUnavailableFailure(recoverable
                ? DaemonFailureCause.HandshakeTimeout : DaemonFailureCause.IndexRebuildRequired,
                "recorded refusal", "retry", recoverable);
            // A NUL in the lock filename makes FileStream throw ArgumentException,
            // before bootstrap launch and outside the typed IOException handling.
            // Keep the original endpoint for cleanup; no production test seam is needed.
            var proxyEndpoint = unclassified
                ? endpoint with { StartupLockPath = Path.Combine(root, "private\0lock") }
                : endpoint;
            var proxy = new DaemonProxy(proxyEndpoint, null, false, false, "stream-test");
            if (unclassified)
                await Assert.ThrowsAsync<ArgumentException>(() => proxy.ConnectOrStartAsync(timeout.Token));
            else
                DaemonStartupStatus.Publish(endpoint, false, failure);
            run = proxy.RunAsync(shutdown.Token, serverInput, serverOutput);
            client = await McpClient.CreateAsync(new StreamClientTransport(clientInput, clientOutput,
                NullLoggerFactory.Instance), cancellationToken: timeout.Token);
            Assert.Equal(recoverable ? "phoenix-codenav" : "phoenix-codenav-unavailable", client.ServerInfo.Name);
            for (int call = 0; call < 2; call++)
            {
                var payload = await CallAsync(client, "server_capabilities");
                var meta = payload.GetProperty("meta");
                Assert.Equal(unclassified ? "daemon_proxy_failed" : failure.Cause, meta.GetProperty("cause").GetString());
                Assert.Equal(unclassified || recoverable, meta.GetProperty("retryable").GetBoolean());
                Assert.Equal(recoverable, payload.GetProperty("features").EnumerateArray()
                    .Any(feature => feature.GetProperty("id").GetString() == "shared-daemon-session-recovery"));
                if (unclassified)
                {
                    Assert.Contains("ArgumentException", meta.GetProperty("detail").GetString());
                    Assert.DoesNotContain("private", meta.GetProperty("detail").GetString());
                    Assert.DoesNotContain(root, meta.GetProperty("detail").GetString());
                }
            }
            Assert.Null(DaemonDescriptor.TryRead(endpoint));
            Assert.False(File.Exists(endpoint.DatabasePath));
            await clientInput.DisposeAsync();
            Assert.Equal(4, await run.WaitAsync(timeout.Token));
            succeeded = true;
        }
        finally
        {
            try
            {
                await shutdown.CancelAsync();
                if (client is not null) await DisposeClientForCleanupAsync(client, succeeded);
            }
            finally
            {
                try
                {
                    if (run is not null)
                        try { await run.WaitAsync(TimeSpan.FromSeconds(15)); }
                        catch when (!succeeded) { }
                }
                finally
                {
                    PhoenixRuntimeMode.Set(previous);
                    try { await CleanupEndpointForTestAsync(endpoint); }
                    finally { StrictWorkspaceCleanup.DeleteAfterSuccess(succeeded, root); }
                }
            }
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ProxyRejectsAnIncompleteStreamPairBeforeChangingRuntimeMode(
        bool inputOnly, bool recoverable)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        string root = Directory.CreateTempSubdirectory("Phoenix partial streams ").FullName;
        var endpoint = DaemonEndpoint.Create(root, null);
        var previous = PhoenixRuntimeMode.Current;
        bool succeeded = false;
        try
        {
            // Without entry validation this record reaches the fallback's own guard.
            // Throws alone would pass; the sentinel proves validation precedes side effects.
            var failure = new DaemonUnavailableFailure(recoverable
                ? DaemonFailureCause.HandshakeTimeout : DaemonFailureCause.IndexRebuildRequired,
                "recorded refusal", "retry", recoverable);
            DaemonStartupStatus.Publish(endpoint, false, failure);
            PhoenixRuntimeMode.Set(PhoenixProcessMode.Standalone);
            var proxy = new DaemonProxy(endpoint, null, false, false, "partial-stream-test");

            await Assert.ThrowsAsync<ArgumentException>(() => proxy.RunAsync(timeout.Token,
                input: inputOnly ? Stream.Null : null, output: inputOnly ? null : Stream.Null));

            Assert.Equal(PhoenixProcessMode.Standalone, PhoenixRuntimeMode.Current);
            Assert.Null(DaemonDescriptor.TryRead(endpoint));
            succeeded = true;
        }
        finally
        {
            PhoenixRuntimeMode.Set(previous);
            try { await CleanupEndpointForTestAsync(endpoint); }
            finally { StrictWorkspaceCleanup.DeleteAfterSuccess(succeeded, root); }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoveryShimRejectsAnIncompleteStreamPairBeforeChangingRuntimeMode(bool inputOnly)
    {
        var previous = PhoenixRuntimeMode.Current;
        try
        {
            PhoenixRuntimeMode.Set(PhoenixProcessMode.Standalone);
            var failure = new DaemonUnavailableFailure(DaemonFailureCause.HandshakeTimeout, "timeout", "retry", true);
            await Assert.ThrowsAsync<ArgumentException>(() => UnavailableMcpShim.RunAsync(failure,
                input: inputOnly ? Stream.Null : null, output: inputOnly ? null : Stream.Null));
            Assert.Equal(PhoenixProcessMode.Standalone, PhoenixRuntimeMode.Current);
        }
        finally { PhoenixRuntimeMode.Set(previous); }
    }
}
