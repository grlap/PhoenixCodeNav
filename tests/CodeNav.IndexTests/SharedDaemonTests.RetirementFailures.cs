using CodeNav.Mcp;
using CodeNav.Mcp.Daemon;

namespace CodeNav.Tests;

public sealed partial class SharedDaemonTests
{
    [Theory]
    [InlineData("daemon_takeover_timeout")]
    [InlineData("daemon_writer_lease_unverifiable")]
    [InlineData("caller_cancelled")]
    public async Task ProxyRetirementPreservesTypedTakeoverFailures(string cause)
    {
        string root = Directory.CreateTempSubdirectory("Phoenix retirement catches ").FullName;
        string workspace = Directory.CreateDirectory(Path.Combine(root, "workspace")).FullName;
        DaemonEndpoint endpoint = DaemonEndpoint.Create(workspace, null);
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var retirementEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? peer = null;
        bool succeeded = false;
        try
        {
            var listener = DaemonTransport.Listen(endpoint);
            peer = ServeRetirementFailureAsync(listener, endpoint, cause, retirementEntered, lifetime.Token);
            var proxy = new DaemonProxy(endpoint, null, false, false, "retirement-catch-test");

            Task<Stream> connect = proxy.ConnectOrStartAsync(caller.Token);
            if (cause == "caller_cancelled")
            {
                await retirementEntered.Task.WaitAsync(lifetime.Token);
                caller.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connect);
            }
            else
            {
                var error = await Assert.ThrowsAsync<DaemonProxyFailureException>(() => connect);
                Assert.Equal(cause, error.Failure.Cause);
                Assert.True(error.Failure.Retryable);
                Assert.True(error.Failure.CanRecoverInSession);
                if (cause == "daemon_takeover_timeout")
                    Assert.IsAssignableFrom<OperationCanceledException>(error.InnerException);
                else
                    Assert.IsType<DaemonWriterLeaseUnverifiableException>(error.InnerException);

                var payload = UnavailableMcpServerTool.FailureResult(error.Failure,
                    "server_capabilities", error.Failure.CanRecoverInSession).StructuredContent!.Value;
                Assert.Equal(cause, payload.GetProperty("meta").GetProperty("cause").GetString());
                Assert.True(payload.GetProperty("meta").GetProperty("retryable").GetBoolean());
                Assert.Contains(payload.GetProperty("features").EnumerateArray(),
                    feature => feature.GetProperty("id").GetString() == "shared-daemon-session-recovery");
                Assert.False(caller.IsCancellationRequested);
            }
            Assert.False(lifetime.IsCancellationRequested);
            Assert.False(File.Exists(endpoint.StartupLockPath));
            Assert.False(File.Exists(endpoint.DatabasePath));
            succeeded = true;
        }
        finally
        {
            lifetime.Cancel();
            try
            {
                if (peer is not null)
                {
                    try { await peer; }
                    catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
                }
            }
            catch when (!succeeded) { }
            finally
            {
                try { await CleanupEndpointForTestAsync(endpoint); }
                catch when (!succeeded) { }
                finally { StrictWorkspaceCleanup.DeleteAfterSuccess(succeeded, root); }
            }
        }
    }

    private static async Task ServeRetirementFailureAsync(
        IDaemonTransportListener listener, DaemonEndpoint endpoint, string cause,
        TaskCompletionSource retirementEntered,
        CancellationToken cancellationToken)
    {
        await using (listener)
        {
            foreach (DaemonPreambleMode expectedMode in new[] { DaemonPreambleMode.Connect, DaemonPreambleMode.RetireAndReplace })
            {
                await using Stream stream = await listener.AcceptAsync(cancellationToken);
                var (version, mode, request) = await DaemonProtocol.ReadRequestAsync(stream, cancellationToken);
                Assert.Equal(expectedMode, mode);
                Assert.NotNull(request);
                var current = DaemonProtocol.Evaluate(endpoint, version, mode, request);
                if (mode == DaemonPreambleMode.RetireAndReplace)
                {
                    retirementEntered.TrySetResult();
                    if (cause is "daemon_takeover_timeout" or "caller_cancelled")
                    {
                        // Pin current proxy catch wiring with the real retirement handshake
                        // deadline, not the two-minute lease-drain deadline. Caller cancellation
                        // is a separate control; no production timing seam is needed.
                        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    }
                    else
                    {
                        // A vanished physical workspace makes the actual writer-lease probe
                        // unverifiable. Close discovery before acknowledging retirement so a
                        // Unix observer cannot enter an unserved listener backlog. The accepted
                        // response stream remains open independently of the listener.
                        Directory.Delete(endpoint.WorkspaceRoot);
                        await listener.DisposeAsync();
                    }
                }

                var response = mode == DaemonPreambleMode.Connect
                    ? DaemonHandshakeResponse.Refused(DaemonFailureCause.OlderThanClient, current.Detail,
                        "0.12.60", current.SchemaVersion, current.WorkspaceIdentity, current.DatabaseKey,
                        current.DaemonPid, request.Nonce)
                    : DaemonHandshakeResponse.RetirementAccepted(current.Detail,
                        "0.12.60", current.SchemaVersion, current.WorkspaceIdentity, current.DatabaseKey,
                        current.DaemonPid, request.Nonce);
                await DaemonProtocol.WriteResponseAsync(stream, response, cancellationToken);
            }
        }
    }
}
