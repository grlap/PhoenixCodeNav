using CodeNav.Mcp;
using CodeNav.Mcp.Daemon;
using ModelContextProtocol.Client;
using ModelContextProtocol.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeNav.Tests;

public sealed partial class SharedDaemonTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProxyRelayHandlesShutdownAndBrokenOutputWithoutStartingAnotherServer(bool brokenOutput)
    {
        string root = Directory.CreateTempSubdirectory("Phoenix relay shutdown ").FullName;
        DaemonEndpoint endpoint = DaemonEndpoint.Create(root, null);
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var shutdown = new CancellationTokenSource();
        await using IDaemonTransportListener listener = DaemonTransport.Listen(endpoint);
        Task server = Task.Run(async () =>
        {
            await using Stream stream = await listener.AcceptAsync(lifetime.Token);
            var (_, _, request) = await DaemonProtocol.ReadRequestAsync(stream, lifetime.Token);
            Assert.NotNull(request);
            await DaemonProtocol.WriteResponseAsync(stream, new DaemonHandshakeResponse(true, "ok", "test",
                BuildInfo.Version, BuildInfo.IndexSchema, endpoint.WorkspaceIdentity, endpoint.DatabaseKey,
                Environment.ProcessId, request.Nonce), lifetime.Token);
            // EOF makes the downstream copy finish first. Shutdown is triggered at its flush.
        });
        using var input = new PendingRelayInput();
        using var output = new FailingRelayOutput(shutdown, brokenOutput);
        bool succeeded = false;
        try
        {
            var proxy = new DaemonProxy(endpoint, null, false, false, "relay-shutdown-test");
            int exit = await proxy.RunAsync(shutdown.Token, input, output).WaitAsync(lifetime.Token);
            Assert.Equal(brokenOutput ? 4 : 0, exit);
            Assert.True(output.FlushAttempted);
            Assert.Equal(0, output.Length); // No second MCP initialize/server is written to consumed stdio.
            await input.Cancelled.Task.WaitAsync(lifetime.Token);
            Assert.Null(DaemonDescriptor.TryRead(endpoint));
            await server;
            succeeded = true;
        }
        finally
        {
            lifetime.Cancel();
            shutdown.Cancel();
            await listener.DisposeAsync();
            try { await server; } catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            PhoenixRuntimeMode.Set(PhoenixProcessMode.Standalone);
            await CleanupEndpointForTestAsync(endpoint);
            StrictWorkspaceCleanup.DeleteAfterSuccess(succeeded, root);
        }
    }

    private sealed class PendingRelayInput : MemoryStream
    {
        internal TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
        {
            try { await Task.Delay(Timeout.Infinite, cancellationToken); }
            finally { if (cancellationToken.IsCancellationRequested) Cancelled.TrySetResult(); }
        }
    }

    private sealed class FailingRelayOutput(CancellationTokenSource shutdown, bool brokenOutput) : MemoryStream
    {
        internal bool FlushAttempted { get; private set; }
        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            FlushAttempted = true;
            if (brokenOutput) return Task.FromException(new IOException("Closed stdout"));
            shutdown.Cancel();
            return Task.FromCanceled(cancellationToken);
        }
    }

    [Fact]
    public async Task RecoveryRereadsLiveStartupReportAndDoesNotRetainRemovedDiagnosis()
    {
        string root = Directory.CreateTempSubdirectory("Phoenix recovery report ").FullName;
        DaemonEndpoint endpoint = DaemonEndpoint.Create(root, null);
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        bool succeeded = false;
        try
        {
            var proxy = new DaemonProxy(endpoint, null, false, false, "report-test");
            foreach (string cause in new[] { "daemon_writer_unavailable", "daemon_index_validation_failed" })
            {
                DaemonStartupStatus.Publish(endpoint, false, new(cause, "reported detail", "retry", true));
                var error = await Assert.ThrowsAsync<DaemonProxyFailureException>(() => proxy.ConnectExistingAsync(lifetime.Token));
                Assert.Equal(cause, error.Failure.Cause);
                Assert.Equal("The live startup owner last reported: reported detail", error.Failure.Detail);
            }
            DaemonStartupStatus.Delete(endpoint);
            await Assert.ThrowsAsync<DaemonEndpointUnavailableException>(() => proxy.ConnectExistingAsync(lifetime.Token));
            Assert.Null(DaemonDescriptor.TryRead(endpoint));
            succeeded = true;
        }
        finally
        {
            DaemonStartupStatus.Delete(endpoint);
            await CleanupEndpointForTestAsync(endpoint);
            StrictWorkspaceCleanup.DeleteAfterSuccess(succeeded, root);
        }
    }

    [Fact]
    public async Task InitializedRawRelayEndsOnDaemonLossWithoutStartingAnotherMcpServer()
    {
        string root = Directory.CreateTempSubdirectory("Phoenix relay loss ").FullName;
        DaemonEndpoint endpoint = DaemonEndpoint.Create(root, null);
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using IDaemonTransportListener listener = DaemonTransport.Listen(endpoint);
        Task server = Task.Run(async () =>
        {
            await using Stream stream = await listener.AcceptAsync(lifetime.Token);
            var (_, _, request) = await DaemonProtocol.ReadRequestAsync(stream, lifetime.Token);
            Assert.NotNull(request);
            await DaemonProtocol.WriteResponseAsync(stream, new DaemonHandshakeResponse(true, "ok", "test",
                BuildInfo.Version, BuildInfo.IndexSchema, endpoint.WorkspaceIdentity, endpoint.DatabaseKey,
                Environment.ProcessId, request.Nonce), lifetime.Token);
            using var services = new ServiceCollection().BuildServiceProvider();
            await using var transport = new StreamServerTransport(stream, stream, "raw-relay", NullLoggerFactory.Instance);
            await using var mcp = McpServer.Create(transport, new McpServerOptions
            { ServerInfo = new() { Name = "raw-relay", Version = "1" } }, NullLoggerFactory.Instance, services);
            try { await mcp.RunAsync(lifetime.Token); }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        });
        McpClient? client = null;
        bool succeeded = false;
        try
        {
            client = await CreateClientAsync(FindMcpExecutable(), root);
            Assert.Equal("raw-relay", client.ServerInfo.Name);
            lifetime.Cancel();
            await server;
            await client.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("raw-relay", client.ServerInfo.Name);
            succeeded = true;
        }
        finally
        {
            lifetime.Cancel();
            await listener.DisposeAsync();
            try { await server; } catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            if (client is not null) await DisposeClientForCleanupAsync(client, succeeded);
            await CleanupEndpointForTestAsync(endpoint);
            StrictWorkspaceCleanup.DeleteAfterSuccess(succeeded, root);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoveryHandshakeNeverRebuildsOrReplacesDaemon(bool olderDaemon)
    {
        string root = Directory.CreateTempSubdirectory("Phoenix recovery handshake ").FullName;
        DaemonEndpoint endpoint = DaemonEndpoint.Create(root, null);
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using IDaemonTransportListener listener = DaemonTransport.Listen(endpoint);
        Task server = Task.Run(async () =>
        {
            await using Stream stream = await listener.AcceptAsync(lifetime.Token);
            var (_, mode, request) = await DaemonProtocol.ReadRequestAsync(stream, lifetime.Token);
            Assert.Equal(DaemonPreambleMode.Connect, mode);
            Assert.NotNull(request);
            Assert.False(request.Rebuild);
            await DaemonProtocol.WriteResponseAsync(stream, new DaemonHandshakeResponse(
                !olderDaemon, olderDaemon ? "daemon_older_than_client" : "ok", "test",
                olderDaemon ? "0.1.0" : BuildInfo.Version, BuildInfo.IndexSchema,
                endpoint.WorkspaceIdentity, endpoint.DatabaseKey, Environment.ProcessId, request.Nonce), lifetime.Token);
        });
        bool succeeded = false;
        try
        {
            var proxy = new DaemonProxy(endpoint, null, rebuild: true, keepAlive: false, "recovery-test");
            if (olderDaemon)
            {
                var error = await Assert.ThrowsAsync<DaemonProxyFailureException>(() => proxy.ConnectExistingAsync(lifetime.Token));
                Assert.Equal("daemon_older_than_client", error.Failure.Cause);
            }
            else
            {
                await using Stream stream = await proxy.ConnectExistingAsync(lifetime.Token);
            }
            await server;
            Assert.Null(DaemonDescriptor.TryRead(endpoint)); // Neither startup nor replacement published a daemon.
            succeeded = true;
        }
        finally
        {
            lifetime.Cancel();
            await listener.DisposeAsync();
            try { await server; } catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            await CleanupEndpointForTestAsync(endpoint);
            StrictWorkspaceCleanup.DeleteAfterSuccess(succeeded, root);
        }
    }

    [Fact]
    public async Task ExistingMcpSessionRecoversAfterHandshakeTimeoutWithoutRestart()
    {
        string root = Directory.CreateTempSubdirectory("Phoenix same session recovery ").FullName;
        DaemonEndpoint endpoint = DaemonEndpoint.Create(root, null);
        using var silentLifetime = new CancellationTokenSource();
        using var daemonLifetime = new CancellationTokenSource();
        IDaemonTransportListener listener = DaemonTransport.Listen(endpoint);
        McpClient? client = null;
        Task<int>? daemonTask = null;
        bool succeeded = false;
        var handlers = new List<Task>();
        Task silentServer = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    Stream stream = await listener.AcceptAsync(silentLifetime.Token);
                    handlers.Add(SilentHandshakeAsync(stream));
                }
            }
            catch (OperationCanceledException) when (silentLifetime.IsCancellationRequested) { }
            finally { await Task.WhenAll(handlers); }
        });
        async Task SilentHandshakeAsync(Stream stream)
        {
            await using (stream)
            {
                try
                {
                    await DaemonProtocol.ReadRequestAsync(stream, silentLifetime.Token);
                    await Task.Delay(Timeout.Infinite, silentLifetime.Token);
                }
                catch (OperationCanceledException) when (silentLifetime.IsCancellationRequested) { }
                catch (IOException) { }
            }
        }
        try
        {
            client = await CreateClientAsync(FindMcpExecutable(), root);
            Assert.Equal("phoenix-codenav", client.ServerInfo.Name);
            Assert.Contains("retains this MCP session", client.ServerInstructions);
            var unavailable = await CallAsync(client, "server_capabilities");
            Assert.Equal("daemon_handshake_timeout", unavailable.GetProperty("meta").GetProperty("cause").GetString());
            Assert.Contains(unavailable.GetProperty("features").EnumerateArray(),
                feature => feature.GetProperty("id").GetString() == "shared-daemon-session-recovery");

            silentLifetime.Cancel();
            await listener.DisposeAsync();
            await silentServer.WaitAsync(TimeSpan.FromSeconds(15));
            var daemon = new DaemonServer(endpoint, null, false, keepAlive: true);
            daemonTask = daemon.RunAsync(daemonLifetime.Token);
            await WaitUntilAsync(() => DaemonDescriptor.TryRead(endpoint)?.Pid == Environment.ProcessId,
                TimeSpan.FromSeconds(15));

            // A still-live startup owner can retain an old refusal after a daemon becomes ready.
            // Connection must win over that record; reading the record first would strand this session again.
            DaemonStartupStatus.Publish(endpoint, rebuild: false, new DaemonUnavailableFailure(
                "daemon_writer_unavailable", "Earlier writer refusal", "retry", Retryable: true));
            byte[] retainedStatus = File.ReadAllBytes(endpoint.StartupStatusPath);

            // The same client and its original stdio proxy process must now reach the daemon.
            var recovered = await CallAsync(client, "server_capabilities");
            Assert.False(recovered.TryGetProperty("meta", out var meta) &&
                meta.TryGetProperty("indexMode", out var mode) && mode.GetString() == "unavailable",
                recovered.GetRawText());
            Assert.Equal(Environment.ProcessId, recovered.GetProperty("runtime").GetProperty("processId").GetInt32());
            Assert.Equal(retainedStatus, File.ReadAllBytes(endpoint.StartupStatusPath));
            await WaitForIndexStateAsync(client, "ready");
            var again = await CallAsync(client, "server_capabilities");
            Assert.Equal(Environment.ProcessId, again.GetProperty("runtime").GetProperty("processId").GetInt32());
            succeeded = true;
        }
        finally
        {
            silentLifetime.Cancel();
            await listener.DisposeAsync();
            await silentServer.WaitAsync(TimeSpan.FromSeconds(15));
            if (client is not null) await DisposeClientForCleanupAsync(client, succeeded);
            daemonLifetime.Cancel();
            if (daemonTask is not null)
                try { await daemonTask; } catch (OperationCanceledException) { }
            PhoenixRuntimeMode.Set(PhoenixProcessMode.Standalone);
            DaemonStartupStatus.Delete(endpoint);
            await CleanupEndpointForTestAsync(endpoint);
            StrictWorkspaceCleanup.DeleteAfterSuccess(succeeded, root);
        }
    }
}
