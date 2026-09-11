using CodeNav.Mcp;
using CodeNav.Mcp.Daemon;

namespace CodeNav.Tests;

public sealed partial class SharedDaemonTests
{
    [Theory]
    [InlineData("proxy")]
    [InlineData("repo_overview")]
    [InlineData("server_capabilities")]
    public async Task RetirementResponseAuthorityFailureStaysTypedAndNonRetryable(string surface)
    {
        string root = Directory.CreateTempSubdirectory("Phoenix retire authority ").FullName;
        DaemonEndpoint endpoint = DaemonEndpoint.Create(root, null);
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        IDaemonTransportListener? listener = null;
        Task? peer = null;
        bool succeeded = false;
        try
        {
            listener = DaemonTransport.Listen(endpoint);
            DaemonDescriptor.Publish(endpoint);
            peer = ServeUnprovenRetirementAsync(listener, endpoint, lifetime.Token);

            if (surface == "proxy")
            {
                var proxy = new DaemonProxy(endpoint, null, false, false, "retire-authority-test");
                var error = await Assert.ThrowsAsync<DaemonProxyFailureException>(() =>
                    proxy.ConnectOrStartAsync(lifetime.Token));
                Assert.IsType<DaemonAuthorityException>(error.InnerException);
                Assert.Equal("daemon_response_authority_failed", error.Failure.Cause);
                Assert.False(error.Failure.Retryable);
                Assert.False(error.Failure.CanRecoverInSession);
                var payload = UnavailableMcpServerTool.FailureResult(error.Failure,
                    "server_capabilities", error.Failure.CanRecoverInSession).StructuredContent!.Value;
                Assert.Equal("daemon_response_authority_failed", payload.GetProperty("meta").GetProperty("cause").GetString());
                Assert.False(payload.GetProperty("meta").GetProperty("retryable").GetBoolean());
                Assert.DoesNotContain(payload.GetProperty("features").EnumerateArray(),
                    feature => feature.GetProperty("id").GetString() == "shared-daemon-session-recovery");
            }
            else
            {
                CliResult cli = await RunCliAsync(FindMcpExecutable(), root, [surface]);
                Assert.Equal(3, cli.ExitCode);
                var detail = surface == "server_capabilities" ? cli.Payload.GetProperty("meta") : cli.Payload;
                Assert.Equal("daemon_response_authority_failed", detail.GetProperty("cause").GetString());
                Assert.False(detail.GetProperty("retryable").GetBoolean());
                Assert.DoesNotContain("retry a tool call in this MCP session", detail.GetProperty("recovery").GetString());
            }

            await peer.WaitAsync(lifetime.Token);
            Assert.False(File.Exists(endpoint.StartupLockPath));
            Assert.False(File.Exists(endpoint.StartupStatusPath));
            Assert.False(File.Exists(endpoint.DatabasePath));
            succeeded = true;
        }
        finally
        {
            lifetime.Cancel();
            try
            {
                if (listener is not null) await listener.DisposeAsync();
                if (peer is not null) await peer;
            }
            catch when (!succeeded) { }
            finally
            {
                try
                {
                    DaemonDescriptor.DeleteOwn(endpoint);
                    await CleanupEndpointForTestAsync(endpoint);
                }
                catch when (!succeeded) { }
                finally { StrictWorkspaceCleanup.DeleteAfterSuccess(succeeded, root); }
            }
        }
    }

    private static async Task ServeUnprovenRetirementAsync(
        IDaemonTransportListener listener, DaemonEndpoint endpoint, CancellationToken cancellationToken)
    {
        foreach (DaemonPreambleMode expectedMode in new[] { DaemonPreambleMode.Connect, DaemonPreambleMode.RetireAndReplace })
        {
            await using Stream stream = await listener.AcceptAsync(cancellationToken);
            var (version, mode, request) = await DaemonProtocol.ReadRequestAsync(stream, cancellationToken);
            Assert.Equal(expectedMode, mode);
            Assert.NotNull(request);
            var current = DaemonProtocol.Evaluate(endpoint, version, mode, request);
            var response = mode == DaemonPreambleMode.Connect
                ? DaemonHandshakeResponse.Refused(DaemonFailureCause.OlderThanClient, current.Detail,
                    "0.12.60", current.SchemaVersion, current.WorkspaceIdentity, current.DatabaseKey,
                    current.DaemonPid, request.Nonce)
                : DaemonHandshakeResponse.RetirementAccepted(current.Detail,
                    "0.12.60", current.SchemaVersion, current.WorkspaceIdentity, current.DatabaseKey,
                    current.DaemonPid, "not-the-requested-nonce");
            await DaemonProtocol.WriteResponseAsync(stream, response, cancellationToken);
        }
    }
}
