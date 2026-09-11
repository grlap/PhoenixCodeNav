using System.Diagnostics;
using CodeNav.Mcp;
using CodeNav.Mcp.Daemon;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeNav.Tests;

public sealed partial class SharedDaemonTests
{
    [Theory]
    [InlineData(false, "repo_overview")]
    [InlineData(true, "repo_overview")]
    [InlineData(false, "server_capabilities")]
    [InlineData(true, "server_capabilities")]
    public async Task ObstructedRuntimeDirectoryReturnsTerminalFailureThroughProxyAndCli(
        bool link, string cliTool)
    {
        string root = Directory.CreateTempSubdirectory("Phoenix runtime refusal ").FullName;
        DaemonEndpoint? cliEndpoint = null;
        bool succeeded = false;
        try
        {
            string blocked = Path.Combine(root, "blocked");
            string target = Directory.CreateDirectory(Path.Combine(root, "target")).FullName;
            string sentinel = Path.Combine(target, "sentinel.txt");
            File.WriteAllText(sentinel, "untouched");
            if (link)
                Assert.True(TestWorkspaceCleanup.TryCreateDirectoryLink(blocked, target, out var reason), reason);
            else
                File.WriteAllText(blocked, "not a directory");

            // Never obstruct the shared per-user runtime directory. Only this endpoint's
            // coordination paths point at the fixture; its pipe identity stays unique.
            var endpoint = DaemonEndpoint.Create(root, null) with
            {
                RuntimeDirectory = blocked,
                StartupLockPath = Path.Combine(blocked, "startup.lock"),
                StartupStatusPath = Path.Combine(blocked, "startup.json"),
                DescriptorPath = Path.Combine(blocked, "daemon.json"),
                SocketPath = OperatingSystem.IsWindows() ? null : Path.Combine(blocked, "d.sock"),
            };
            var proxy = new DaemonProxy(endpoint, null, false, false, "runtime-refusal-test");
            using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var error = await Assert.ThrowsAsync<DaemonProxyFailureException>(() => proxy.ConnectOrStartAsync(lifetime.Token));
            // Unix rejects unsafe runtime authority in ConnectAsync, before startup.
            // Windows reaches the runtime-directory preparation at the startup boundary.
            string cause = OperatingSystem.IsWindows()
                ? "daemon_runtime_directory_unavailable" : "daemon_endpoint_authority_failed";
            Assert.Equal(cause, error.Failure.Cause);
            Assert.False(error.Failure.Retryable);
            Assert.False(error.Failure.CanRecoverInSession);
            foreach (string tool in new[] { "repo_overview", "server_capabilities" })
            {
                var payload = UnavailableMcpServerTool.FailureResult(error.Failure, tool).StructuredContent!.Value;
                var detail = tool == "server_capabilities" ? payload.GetProperty("meta") : payload;
                Assert.Equal(cause, detail.GetProperty("cause").GetString());
                Assert.False(detail.GetProperty("retryable").GetBoolean());
            }

            // Bridge the actual isolated preparation refusal into a live startup record,
            // then exercise the real CLI process and RunAsync's typed-failure catch.
            // The CLI consumes the cached refusal; it does not obstruct shared runtime.
            cliEndpoint = DaemonEndpoint.Create(root, null);
            DaemonStartupStatus.Publish(cliEndpoint, false, error.Failure);
            Assert.Equal(error.Failure, DaemonStartupStatus.TryReadLiveFailure(cliEndpoint, false));
            CliResult cli = await RunCliAsync(FindMcpExecutable(), root, [cliTool]);
            Assert.Equal(3, cli.ExitCode);
            var cliDetail = cliTool == "server_capabilities"
                ? cli.Payload.GetProperty("meta") : cli.Payload;
            Assert.Equal(cause, cliDetail.GetProperty("cause").GetString());
            Assert.False(cliDetail.GetProperty("retryable").GetBoolean());
            Assert.Equal("The live startup owner last reported: " + error.Failure.Detail,
                cliDetail.GetProperty("detail").GetString());
            Assert.StartsWith(error.Failure.Recovery, cliDetail.GetProperty("recovery").GetString());
            Assert.DoesNotContain("retry a tool call in this MCP session",
                cliDetail.GetProperty("recovery").GetString());
            Assert.Equal("unavailable", cli.Payload.GetProperty("meta").GetProperty("indexMode").GetString());
            Assert.False(File.Exists(cliEndpoint.StartupLockPath));
            Assert.False(File.Exists(cliEndpoint.DescriptorPath));
            Assert.False(File.Exists(cliEndpoint.DatabasePath));
            Assert.False(File.Exists(endpoint.StartupLockPath));
            Assert.False(File.Exists(endpoint.StartupStatusPath));
            Assert.False(File.Exists(endpoint.DescriptorPath));
            Assert.Equal("untouched", File.ReadAllText(sentinel));
            Assert.Equal(new[] { sentinel }, Directory.GetFiles(target));
            if (link) Assert.NotNull(new DirectoryInfo(blocked).LinkTarget);
            else Assert.Equal("not a directory", File.ReadAllText(blocked));
            succeeded = true;
        }
        finally
        {
            try
            {
                if (cliEndpoint is not null) await CleanupEndpointForTestAsync(cliEndpoint);
            }
            catch when (!succeeded) { }
            finally { StrictWorkspaceCleanup.DeleteAfterSuccess(succeeded, root); }
        }
    }

    [Theory]
    [InlineData("daemon_handshake_timeout", false, true)]
    [InlineData("daemon_proxy_failed", true, false)]
    [InlineData("daemon_index_rebuild_required", false, false)]
    [InlineData("daemon_index_destination_foreign", false, false)]
    [InlineData("daemon_index_destination_unsafe", false, false)]
    public async Task ProxyRecoverySelectionUsesCauseInsteadOfClientRetryAdvice(
        string cause, bool retryable, bool recoverable)
    {
        string root = Directory.CreateTempSubdirectory("Phoenix recovery policy ").FullName;
        DaemonEndpoint endpoint = DaemonEndpoint.Create(root, null);
        McpClient? client = null;
        bool succeeded = false;
        try
        {
            DaemonStartupStatus.Publish(endpoint, false, DaemonWireTestData.Failure(cause, "cached failure", "retry advice", retryable));
            client = await CreateClientAsync(FindMcpExecutable(), root);
            Assert.Equal(recoverable ? "phoenix-codenav" : "phoenix-codenav-unavailable", client.ServerInfo.Name);
            var payload = await CallAsync(client, "server_capabilities");
            Assert.Equal(cause, payload.GetProperty("meta").GetProperty("cause").GetString());
            Assert.Equal(retryable, payload.GetProperty("meta").GetProperty("retryable").GetBoolean());
            Assert.Equal(recoverable, payload.GetProperty("features").EnumerateArray()
                .Any(feature => feature.GetProperty("id").GetString() == "shared-daemon-session-recovery"));
            string advice = payload.GetProperty("meta").GetProperty("recovery").GetString()!;
            Assert.Equal(recoverable, advice.Contains("retry a tool call in this MCP session", StringComparison.Ordinal));
            Assert.Contains("close the Phoenix session", advice);
            Assert.Contains("then reconnect", advice);
            const string reconnectAdvice = "retry advice If no daemon is running after the condition clears, close the Phoenix session that first reported this startup failure, then reconnect.";
            Assert.Equal(recoverable
                ? reconnectAdvice + " If an existing daemon is ready, retry a tool call in this MCP session; recovery does not start another daemon."
                : reconnectAdvice, advice);
            Assert.Null(DaemonDescriptor.TryRead(endpoint));
            succeeded = true;
        }
        finally
        {
            if (client is not null) await DisposeClientForCleanupAsync(client, succeeded);
            DaemonStartupStatus.Delete(endpoint);
            await CleanupEndpointForTestAsync(endpoint);
            StrictWorkspaceCleanup.DeleteAfterSuccess(succeeded, root);
        }
    }

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
            await DaemonProtocol.WriteResponseAsync(stream, DaemonHandshakeResponse.SessionAccepted("test",
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
                DaemonStartupStatus.Publish(endpoint, false, DaemonWireTestData.Failure(cause, "reported detail", "retry", true));
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
            await DaemonProtocol.WriteResponseAsync(stream, DaemonHandshakeResponse.SessionAccepted("test",
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
            var response = olderDaemon
                ? DaemonHandshakeResponse.Refused(DaemonFailureCause.OlderThanClient, "test", "0.1.0", BuildInfo.IndexSchema,
                    endpoint.WorkspaceIdentity, endpoint.DatabaseKey, Environment.ProcessId, request.Nonce)
                : DaemonHandshakeResponse.SessionAccepted("test", BuildInfo.Version, BuildInfo.IndexSchema,
                    endpoint.WorkspaceIdentity, endpoint.DatabaseKey, Environment.ProcessId, request.Nonce);
            await DaemonProtocol.WriteResponseAsync(stream, response, lifetime.Token);
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

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ExistingMcpSessionRecoveryDeterminesExitStatus(bool recover, bool loseRecoveredConnection)
    {
        string root = Directory.CreateTempSubdirectory("Phoenix same session recovery ").FullName;
        DaemonEndpoint endpoint = DaemonEndpoint.Create(root, null);
        using var silentLifetime = new CancellationTokenSource();
        using var daemonLifetime = new CancellationTokenSource();
        IDaemonTransportListener listener = DaemonTransport.Listen(endpoint);
        McpClient? client = null;
        Process? proxyProcess = null;
        Task<string>? stderr = null;
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
            string executable = FindMcpExecutable();
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(executable)!,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            start.ArgumentList.Add("--workspace-root");
            start.ArgumentList.Add(root);
            proxyProcess = Process.Start(start) ?? throw new IOException("Recovery test proxy did not start.");
            stderr = proxyProcess.StandardError.ReadToEndAsync();
            using var initializeTimeout = new CancellationTokenSource(
                TimeSpan.FromMilliseconds(TestProcessExitTimeoutMilliseconds));
            client = await McpClient.CreateAsync(new StreamClientTransport(
                proxyProcess.StandardInput.BaseStream, proxyProcess.StandardOutput.BaseStream,
                NullLoggerFactory.Instance), cancellationToken: initializeTimeout.Token);
            Assert.Equal("phoenix-codenav", client.ServerInfo.Name);
            Assert.Contains("retains this MCP session", client.ServerInstructions);
            var unavailable = await CallAsync(client, "server_capabilities");
            Assert.Equal("daemon_handshake_timeout", unavailable.GetProperty("meta").GetProperty("cause").GetString());
            Assert.Contains(unavailable.GetProperty("features").EnumerateArray(),
                feature => feature.GetProperty("id").GetString() == "shared-daemon-session-recovery");

            if (!recover)
            {
                await AssertProxyExitAsync(4);
                succeeded = true;
                return;
            }

            silentLifetime.Cancel();
            await listener.DisposeAsync();
            await silentServer.WaitAsync(TimeSpan.FromSeconds(15));
            var daemon = new DaemonServer(endpoint, null, false, keepAlive: true);
            daemonTask = daemon.RunAsync(daemonLifetime.Token);
            await WaitUntilAsync(() => DaemonDescriptor.TryRead(endpoint)?.Pid == Environment.ProcessId,
                TimeSpan.FromSeconds(15));

            // A still-live startup owner can retain an old refusal after a daemon becomes ready.
            // Connection must win over that record; reading the record first would strand this session again.
            DaemonStartupStatus.Publish(endpoint, rebuild: false, DaemonWireTestData.Failure(
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
            if (loseRecoveredConnection)
            {
                daemonLifetime.Cancel();
                await daemonTask.WaitAsync(TimeSpan.FromSeconds(15));
                daemonTask = null;
                using var callTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                var lost = await client.CallToolAsync("repo_overview",
                    new Dictionary<string, object?>(), cancellationToken: callTimeout.Token);
                Assert.True(lost.IsError);
                Assert.Equal("daemon_request_outcome_unknown",
                    ParseContent(lost).GetProperty("cause").GetString());
                Assert.False(ParseContent(lost).GetProperty("retryable").GetBoolean());
            }
            await AssertProxyExitAsync(loseRecoveredConnection ? 4 : 0);
            succeeded = true;
        }
        finally
        {
            var cleanupFailures = new List<Exception>();
            try
            {
                await AttemptCleanupAsync(async () =>
                {
                    silentLifetime.Cancel();
                    await listener.DisposeAsync();
                    await silentServer.WaitAsync(TimeSpan.FromSeconds(15));
                });
                await AttemptCleanupAsync(async () =>
                {
                    if (client is not null) await DisposeClientForCleanupAsync(client, succeeded);
                });
                await AttemptCleanupAsync(async () =>
                {
                    if (proxyProcess is null) return;
                    try
                    {
                        // Use the owned handle rather than resolving a potentially reused PID.
                        if (!proxyProcess.HasExited) proxyProcess.Kill(entireProcessTree: true);
                        await proxyProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                        if (stderr is not null) await stderr.WaitAsync(TimeSpan.FromSeconds(10));
                    }
                    finally { proxyProcess.Dispose(); }
                });
            }
            finally
            {
                try
                {
                    await AttemptCleanupAsync(async () =>
                    {
                        daemonLifetime.Cancel();
                        if (daemonTask is not null)
                            try { await daemonTask; }
                            catch (OperationCanceledException) when (daemonLifetime.IsCancellationRequested) { }
                    });
                }
                finally
                {
                    PhoenixRuntimeMode.Set(PhoenixProcessMode.Standalone);
                    await AttemptCleanupAsync(() =>
                    {
                        DaemonStartupStatus.Delete(endpoint);
                        return Task.CompletedTask;
                    });
                    await AttemptCleanupAsync(() => CleanupEndpointForTestAsync(endpoint));
                    await AttemptCleanupAsync(() =>
                    {
                        StrictWorkspaceCleanup.DeleteAfterSuccess(succeeded && cleanupFailures.Count == 0, root);
                        return Task.CompletedTask;
                    });
                }
            }
            if (cleanupFailures.Count > 0)
            {
                var failure = new AggregateException("Recovery test cleanup failed.", cleanupFailures);
                if (succeeded) throw failure;
                Console.Error.WriteLine(failure); // Preserve the original test failure.
            }

            async Task AttemptCleanupAsync(Func<Task> cleanup)
            {
                try { await cleanup(); }
                catch (Exception ex) { cleanupFailures.Add(ex); }
            }
        }

        async Task AssertProxyExitAsync(int expected)
        {
            proxyProcess!.StandardInput.Close();
            await proxyProcess.WaitForExitAsync().WaitAsync(
                TimeSpan.FromMilliseconds(TestProcessExitTimeoutMilliseconds));
            Assert.Equal(expected, proxyProcess.ExitCode);
            Assert.NotNull(stderr);
            string diagnostics = await stderr;
            Assert.False(diagnostics.Contains("Phoenix warning: daemon recovery", StringComparison.Ordinal), diagnostics);
        }
    }
}
