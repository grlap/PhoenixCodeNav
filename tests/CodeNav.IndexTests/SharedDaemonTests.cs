using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using CodeNav.Mcp.Daemon;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace CodeNav.Tests;

[CollectionDefinition("Shared daemon MCP process isolation", DisableParallelization = true)]
public sealed class SharedDaemonProcessCollection;

[Collection("Shared daemon MCP process isolation")]
public sealed class SharedDaemonTests
{
    [Fact]
    public void HandshakeRequiresExactAuthorityAndAllowsOnlyNewerRetirement()
    {
        string root = Directory.CreateTempSubdirectory("Phoenix daemon protocol ").FullName;
        try
        {
            DaemonEndpoint endpoint = DaemonEndpoint.Create(root, indexDb: null);
            Assert.StartsWith("..", Path.GetRelativePath(root, endpoint.DescriptorPath));
            DaemonHandshakeRequest exact = DaemonProtocol.CreateRequest(endpoint, "test");
            DaemonHandshakeResponse accepted = DaemonProtocol.Evaluate(
                endpoint, DaemonProtocol.CurrentVersion, DaemonPreambleMode.Connect, exact);
            Assert.True(accepted.Accepted);
            Assert.Equal("ok", accepted.Cause);

            DaemonHandshakeResponse wrongUser = DaemonProtocol.Evaluate(
                endpoint,
                DaemonProtocol.CurrentVersion,
                DaemonPreambleMode.Connect,
                exact with { UserIdentity = exact.UserIdentity + "-other" });
            Assert.False(wrongUser.Accepted);
            Assert.Equal("daemon_user_mismatch", wrongUser.Cause);

            DaemonHandshakeResponse wrongDatabase = DaemonProtocol.Evaluate(
                endpoint,
                DaemonProtocol.CurrentVersion,
                DaemonPreambleMode.Connect,
                exact with { DatabaseKey = new string('A', 64) });
            Assert.False(wrongDatabase.Accepted);
            Assert.Equal("daemon_index_destination_mismatch", wrongDatabase.Cause);

            DaemonHandshakeResponse wrongWorkspace = DaemonProtocol.Evaluate(
                endpoint,
                DaemonProtocol.CurrentVersion,
                DaemonPreambleMode.Connect,
                exact with { WorkspaceIdentity = "not-the-workspace" });
            Assert.False(wrongWorkspace.Accepted);
            Assert.Equal("daemon_workspace_mismatch", wrongWorkspace.Cause);

            DaemonHandshakeResponse wrongPreamble = DaemonProtocol.Evaluate(
                endpoint,
                DaemonProtocol.CurrentVersion + 1,
                DaemonPreambleMode.Connect,
                exact);
            Assert.False(wrongPreamble.Accepted);
            Assert.Equal("daemon_preamble_incompatible", wrongPreamble.Cause);

            DaemonHandshakeResponse invalidNonce = DaemonProtocol.Evaluate(
                endpoint,
                DaemonProtocol.CurrentVersion,
                DaemonPreambleMode.Connect,
                exact with { Nonce = "short" });
            Assert.False(invalidNonce.Accepted);
            Assert.Equal("daemon_nonce_invalid", invalidNonce.Cause);

            DaemonHandshakeRequest newer = exact with { ToolVersion = "99.0.0" };
            DaemonHandshakeResponse replaceRequired = DaemonProtocol.Evaluate(
                endpoint, DaemonProtocol.CurrentVersion, DaemonPreambleMode.Connect, newer);
            Assert.False(replaceRequired.Accepted);
            Assert.Equal("daemon_older_than_client", replaceRequired.Cause);
            DaemonHandshakeResponse retire = DaemonProtocol.Evaluate(
                endpoint,
                DaemonProtocol.CurrentVersion,
                DaemonPreambleMode.RetireAndReplace,
                newer);
            Assert.True(retire.Accepted);
            Assert.True(retire.Retiring);
            Assert.Equal("daemon_retiring", retire.Cause);

            DaemonHandshakeRequest older = exact with { ToolVersion = "0.1.0" };
            DaemonHandshakeResponse restartAgent = DaemonProtocol.Evaluate(
                endpoint, DaemonProtocol.CurrentVersion, DaemonPreambleMode.Connect, older);
            Assert.False(restartAgent.Accepted);
            Assert.Equal("daemon_newer_than_client", restartAgent.Cause);
            DaemonHandshakeResponse forbiddenRetire = DaemonProtocol.Evaluate(
                endpoint,
                DaemonProtocol.CurrentVersion,
                DaemonPreambleMode.RetireAndReplace,
                older);
            Assert.False(forbiddenRetire.Accepted);
            Assert.Equal("daemon_retire_not_newer", forbiddenRetire.Cause);

            DaemonHandshakeRequest newerSchema = exact with { SchemaVersion = "999" };
            Assert.True(DaemonProtocol.Evaluate(
                endpoint,
                DaemonProtocol.CurrentVersion,
                DaemonPreambleMode.RetireAndReplace,
                newerSchema).Retiring);

            DaemonHandshakeResponse malformed = DaemonProtocol.Evaluate(
                endpoint,
                DaemonProtocol.CurrentVersion,
                DaemonPreambleMode.Connect,
                request: null);
            var proxy = new DaemonProxy(
                endpoint, null, false, false, false, "validation-test");
            proxy.ValidateResponse(exact, malformed);
            Assert.Equal("daemon_preamble_invalid", malformed.Cause);
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public void UnsafeUnixEndpointAndDescriptorLinksAreRefusedWithoutDeletingTargets()
    {
        if (OperatingSystem.IsWindows()) return;
        string root = Directory.CreateTempSubdirectory("Phoenix daemon unsafe endpoint ").FullName;
        DaemonEndpoint endpoint = DaemonEndpoint.Create(root, null);
        string descriptorTarget = Path.Combine(root, "descriptor-target.json");
        string escapedDirectory = Directory.CreateTempSubdirectory(
            "Phoenix daemon escaped descriptor ").FullName;
        string workspaceDiscovery = Path.Combine(root, ".codenav");
        try
        {
            DaemonTransport.EnsureRuntimeDirectory(endpoint);
            File.WriteAllText(endpoint.SocketPath!, "sentinel");
            Assert.Throws<IOException>(() => DaemonTransport.Listen(endpoint));
            Assert.Equal("sentinel", File.ReadAllText(endpoint.SocketPath!));

            Directory.CreateSymbolicLink(workspaceDiscovery, escapedDirectory);
            Assert.StartsWith("..", Path.GetRelativePath(root, endpoint.DescriptorPath));
            DaemonDescriptor.Publish(endpoint);
            Assert.Equal(Environment.ProcessId, DaemonDescriptor.TryRead(endpoint)?.Pid);
            Assert.Empty(Directory.EnumerateFileSystemEntries(escapedDirectory));
            DaemonDescriptor.DeleteOwn(endpoint);

            File.WriteAllText(descriptorTarget, "do-not-replace");
            File.CreateSymbolicLink(endpoint.DescriptorPath, descriptorTarget);
            Assert.Null(DaemonDescriptor.TryRead(endpoint));
            Assert.Throws<IOException>(() => DaemonDescriptor.Publish(endpoint));
            Assert.Equal("do-not-replace", File.ReadAllText(descriptorTarget));
        }
        finally
        {
            try { File.Delete(endpoint.SocketPath!); } catch { }
            try { File.Delete(endpoint.DescriptorPath); } catch { }
            try { File.Delete(descriptorTarget); } catch { }
            try { File.Delete(endpoint.StartupLockPath); } catch { }
            try { Directory.Delete(workspaceDiscovery); } catch { }
            TestWorkspaceCleanup.DeleteWorkspace(root);
            TestWorkspaceCleanup.DeleteWorkspace(escapedDirectory);
        }
    }

    [Fact]
    public async Task FrozenPreambleRoundTripsBeforeMcpBytes()
    {
        string root = Directory.CreateTempSubdirectory("Phoenix daemon frame ").FullName;
        try
        {
            DaemonEndpoint endpoint = DaemonEndpoint.Create(root, null);
            DaemonHandshakeRequest expected = DaemonProtocol.CreateRequest(endpoint, "frame-test");
            await using var stream = new MemoryStream();
            await DaemonProtocol.WriteRequestAsync(
                stream, DaemonPreambleMode.RetireAndReplace, expected, CancellationToken.None);
            Assert.InRange(stream.Length,
                DaemonProtocol.HeaderBytes + 2,
                DaemonProtocol.HeaderBytes + DaemonProtocol.MaxPayloadBytes);
            stream.Position = 0;
            (byte version, DaemonPreambleMode mode, DaemonHandshakeRequest? actual) =
                await DaemonProtocol.ReadRequestAsync(stream, CancellationToken.None);
            Assert.Equal(DaemonProtocol.CurrentVersion, version);
            Assert.Equal(DaemonPreambleMode.RetireAndReplace, mode);
            Assert.Equal(expected, actual);
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public async Task TransientAcceptFailureRetriesWithoutEndingTheDaemonLoop()
    {
        await using var listener = new FlakyDaemonListener();
        await using Stream accepted = await DaemonServer.AcceptWithRetryAsync(
            listener,
            NullLogger.Instance,
            CancellationToken.None,
            TimeSpan.Zero);
        Assert.Equal(2, listener.Attempts);
        Assert.IsType<MemoryStream>(accepted);
    }

    [Fact]
    public async Task RoundRobinAdmissionGivesWaitingPeerTheNextReleasedSlot()
    {
        var admission = new DaemonRequestAdmission(maxConcurrent: 1);
        await using var inputA = new MemoryStream();
        await using var outputA = new MemoryStream();
        await using var inputB = new MemoryStream();
        await using var outputB = new MemoryStream();
        await using var transportA = new StreamServerTransport(
            inputA, outputA, "admission-a", NullLoggerFactory.Instance);
        await using var transportB = new StreamServerTransport(
            inputB, outputB, "admission-b", NullLoggerFactory.Instance);
        using ServiceProvider services = new ServiceCollection().BuildServiceProvider();
        var options = new McpServerOptions
        {
            ServerInfo = new() { Name = "admission", Version = "1" },
        };
        await using McpServer serverA = McpServer.Create(
            transportA, options, NullLoggerFactory.Instance, services);
        await using McpServer serverB = McpServer.Create(
            transportB, options, NullLoggerFactory.Instance, services);
        admission.Register(serverA, "a");
        admission.Register(serverB, "b");
        try
        {
            IDisposable first = await admission.EnterAsync(serverA, CancellationToken.None);
            Task<IDisposable> secondA = admission.EnterAsync(
                serverA, CancellationToken.None).AsTask();
            Task<IDisposable> firstB = admission.EnterAsync(
                serverB, CancellationToken.None).AsTask();
            Assert.False(secondA.IsCompleted);
            Assert.False(firstB.IsCompleted);

            first.Dispose();
            Task winner = await Task.WhenAny(firstB, secondA).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Same(firstB, winner);
            IDisposable bLease = await firstB;
            Assert.False(secondA.IsCompleted);
            bLease.Dispose();
            (await secondA.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
            Assert.Equal(0, admission.ActiveCount);
        }
        finally
        {
            admission.Unregister(serverA);
            admission.Unregister(serverB);
        }
    }

    [Fact]
    public async Task DisconnectCancelsOnlyThatClientsQueuedAdmission()
    {
        var admission = new DaemonRequestAdmission(maxConcurrent: 1);
        await using var inputA = new MemoryStream();
        await using var outputA = new MemoryStream();
        await using var inputB = new MemoryStream();
        await using var outputB = new MemoryStream();
        await using var transportA = new StreamServerTransport(
            inputA, outputA, "disconnect-a", NullLoggerFactory.Instance);
        await using var transportB = new StreamServerTransport(
            inputB, outputB, "disconnect-b", NullLoggerFactory.Instance);
        using ServiceProvider services = new ServiceCollection().BuildServiceProvider();
        var options = new McpServerOptions
        {
            ServerInfo = new() { Name = "disconnect", Version = "1" },
        };
        await using McpServer serverA = McpServer.Create(
            transportA, options, NullLoggerFactory.Instance, services);
        await using McpServer serverB = McpServer.Create(
            transportB, options, NullLoggerFactory.Instance, services);
        admission.Register(serverA, "a");
        admission.Register(serverB, "b");

        IDisposable active = await admission.EnterAsync(serverA, CancellationToken.None);
        Task<IDisposable> cancelledA = admission.EnterAsync(
            serverA, CancellationToken.None).AsTask();
        Task<IDisposable> waitingB = admission.EnterAsync(
            serverB, CancellationToken.None).AsTask();
        admission.Unregister(serverA);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await cancelledA);
        Assert.False(waitingB.IsCompleted);
        active.Dispose();
        (await waitingB.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
        Assert.Equal(0, admission.ActiveCount);
        admission.Unregister(serverB);
    }

    [Fact]
    public async Task DrainCannotOvertakeAdmissionBeforeWaiterEnqueue()
    {
        using var enqueueReached = new ManualResetEventSlim();
        using var allowEnqueue = new ManualResetEventSlim();
        using var drainReached = new ManualResetEventSlim();
        var admission = new DaemonRequestAdmission(
            maxConcurrent: 1,
            beforeWaiterEnqueueUnderGate: () =>
            {
                enqueueReached.Set();
                Assert.True(allowEnqueue.Wait(TimeSpan.FromSeconds(5)));
            },
            beforeBeginDrainGate: drainReached.Set);
        await using var input = new MemoryStream();
        await using var output = new MemoryStream();
        await using var transport = new StreamServerTransport(
            input, output, "drain-race", NullLoggerFactory.Instance);
        using ServiceProvider services = new ServiceCollection().BuildServiceProvider();
        var options = new McpServerOptions
        {
            ServerInfo = new() { Name = "drain-race", Version = "1" },
        };
        await using McpServer server = McpServer.Create(
            transport, options, NullLoggerFactory.Instance, services);
        admission.Register(server, "drain-race");
        try
        {
            Task<IDisposable> entering = Task.Run(async () =>
                await admission.EnterAsync(server, CancellationToken.None));
            Assert.True(enqueueReached.Wait(TimeSpan.FromSeconds(5)));

            Task drain = Task.Run(admission.BeginDrain);
            Assert.True(drainReached.Wait(TimeSpan.FromSeconds(5)));
            Assert.False(drain.IsCompleted);

            allowEnqueue.Set();
            IDisposable lease = await entering.WaitAsync(TimeSpan.FromSeconds(5));
            await drain.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, admission.ActiveCount);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await admission.EnterAsync(server, CancellationToken.None));

            lease.Dispose();
            Assert.Equal(0, admission.ActiveCount);
        }
        finally
        {
            allowEnqueue.Set();
            admission.Unregister(server);
        }
    }

    [Fact]
    public async Task ConcurrentProxiesRecoverStaleDiscoveryShareOneDaemonAndSerializeRefreshes()
    {
        string root = Directory.CreateTempSubdirectory("Phoenix shared daemon ").FullName;
        McpClient? first = null;
        McpClient? second = null;
        McpClient? mismatch = null;
        try
        {
            string executable = FindMcpExecutable();
            DaemonEndpoint endpoint = DaemonEndpoint.Create(root, null);
            DaemonDescriptor.Publish(endpoint);
            Assert.Equal(Environment.ProcessId, DaemonDescriptor.TryRead(endpoint)?.Pid);

            Task<McpClient> firstStart = CreateClientAsync(executable, root);
            Task<McpClient> secondStart = CreateClientAsync(executable, root);
            McpClient[] clients = await Task.WhenAll(firstStart, secondStart);
            first = clients[0];
            second = clients[1];

            JsonElement firstCapabilities = await CallAsync(first, "server_capabilities");
            JsonElement secondCapabilities = await CallAsync(second, "server_capabilities");
            Assert.Equal("daemon",
                firstCapabilities.GetProperty("runtime").GetProperty("indexMode").GetString());
            Assert.Equal("daemon",
                secondCapabilities.GetProperty("runtime").GetProperty("indexMode").GetString());
            int daemonPid = firstCapabilities.GetProperty("runtime")
                .GetProperty("processId").GetInt32();
            Assert.Equal(daemonPid,
                secondCapabilities.GetProperty("runtime").GetProperty("processId").GetInt32());
            Assert.NotEqual(Environment.ProcessId, daemonPid);

            Task<JsonElement>[] refreshes = Enumerable.Range(0, 8)
                .Select(i => CallAsync(i % 2 == 0 ? first : second, "refresh_index"))
                .ToArray();
            foreach (JsonElement refresh in await Task.WhenAll(refreshes))
            {
                Assert.True(refresh.GetProperty("queued").GetBoolean());
                Assert.Equal("daemon",
                    refresh.GetProperty("meta").GetProperty("indexMode").GetString());
            }

            mismatch = await CreateClientAsync(
                executable,
                root,
                "--index-db",
                Path.Combine(root, ".codenav", "other.db"));
            JsonElement unavailable = await CallAsync(mismatch, "server_capabilities");
            Assert.Equal("unavailable",
                unavailable.GetProperty("meta").GetProperty("indexMode").GetString());
            Assert.Equal("daemon_index_destination_mismatch",
                unavailable.GetProperty("meta").GetProperty("cause").GetString());
            CallToolResult unavailableTool = await mismatch.CallToolAsync(
                "find_file",
                new Dictionary<string, object?> { ["nameOrGlob"] = "*.cs" });
            Assert.True(unavailableTool.IsError);
            JsonElement unavailableError = ParseContent(unavailableTool);
            Assert.Equal("phoenix_daemon_unavailable",
                unavailableError.GetProperty("error").GetString());
            Assert.Equal("daemon_index_destination_mismatch",
                unavailableError.GetProperty("cause").GetString());
        }
        finally
        {
            DaemonEndpoint? endpoint = null;
            try { endpoint = DaemonEndpoint.Create(root, null); } catch { }
            if (endpoint is not null)
            {
                try { await RetireDaemonForTestAsync(endpoint); } catch { }
            }
            await Task.WhenAll(
                mismatch is null ? Task.CompletedTask : TryDisposeClientAsync(mismatch),
                second is null ? Task.CompletedTask : TryDisposeClientAsync(second),
                first is null ? Task.CompletedTask : TryDisposeClientAsync(first));
            if (endpoint is not null)
            {
                await WaitUntilAsync(
                    () => !File.Exists(endpoint.DescriptorPath),
                    TimeSpan.FromSeconds(30));
                if (!OperatingSystem.IsWindows() && endpoint.SocketPath is not null)
                    DaemonUnixFileAuthority.TryRemoveOwnedSocket(endpoint.SocketPath);
                try { File.Delete(endpoint.StartupLockPath); } catch { }
            }
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public async Task KilledDaemonIsReelectedWithoutReusingTheDeadProcess()
    {
        string root = Directory.CreateTempSubdirectory("Phoenix daemon recovery ").FullName;
        McpClient? first = null;
        McpClient? successor = null;
        DaemonEndpoint? endpoint = null;
        try
        {
            string executable = FindMcpExecutable();
            endpoint = DaemonEndpoint.Create(root, null);
            first = await CreateClientAsync(executable, root);
            JsonElement before = await CallAsync(first, "server_capabilities");
            int firstPid = before.GetProperty("runtime").GetProperty("processId").GetInt32();
            Assert.Equal(firstPid, DaemonDescriptor.TryRead(endpoint)?.Pid);

            using (Process process = Process.GetProcessById(firstPid))
            {
                process.Kill(entireProcessTree: false);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
            await first.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            await TryDisposeClientAsync(first);
            first = null;

            successor = await CreateClientAsync(executable, root);
            JsonElement after = await CallAsync(successor, "server_capabilities");
            int successorPid = after.GetProperty("runtime").GetProperty("processId").GetInt32();
            Assert.NotEqual(firstPid, successorPid);
            Assert.Equal(successorPid, DaemonDescriptor.TryRead(endpoint)?.Pid);
            JsonElement refresh = await CallAsync(successor, "refresh_index");
            Assert.True(refresh.GetProperty("queued").GetBoolean());
        }
        finally
        {
            bool retirementAccepted = false;
            if (endpoint is not null)
            {
                try
                {
                    await RetireDaemonForTestAsync(endpoint);
                    retirementAccepted = true;
                }
                catch { }
            }
            if (retirementAccepted && successor is not null)
                try { await successor.Completion.WaitAsync(TimeSpan.FromSeconds(10)); } catch { }
            if (successor is not null) await TryDisposeClientAsync(successor);
            if (first is not null) await TryDisposeClientAsync(first);
            if (endpoint is not null)
                await CleanupEndpointForTestAsync(endpoint);
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public async Task WindowsLegacyStandaloneContenderRemainsAReadOnlyFollowerOfDaemonWriter()
    {
        if (!OperatingSystem.IsWindows()) return;
        string root = Directory.CreateTempSubdirectory("Phoenix mixed daemon follower ").FullName;
        McpClient? daemonClient = null;
        McpClient? legacyClient = null;
        DaemonEndpoint? endpoint = null;
        try
        {
            string executable = FindMcpExecutable();
            endpoint = DaemonEndpoint.Create(root, null);
            daemonClient = await CreateClientAsync(executable, root);
            JsonElement daemonCapabilities = await CallAsync(
                daemonClient, "server_capabilities");
            Assert.Equal("daemon",
                daemonCapabilities.GetProperty("runtime").GetProperty("indexMode").GetString());

            legacyClient = await CreateStandaloneClientAsync(executable, root);
            JsonElement legacyCapabilities = await CallAsync(
                legacyClient, "server_capabilities");
            Assert.Equal("legacy-follower",
                legacyCapabilities.GetProperty("runtime").GetProperty("indexMode").GetString());
            Assert.Equal("follower",
                legacyCapabilities.GetProperty("index").GetProperty("mode").GetString());
            CallToolResult refresh = await legacyClient.CallToolAsync(
                "refresh_index", new Dictionary<string, object?>());
            Assert.True(refresh.IsError);
            Assert.Equal("index_writer_required",
                ParseContent(refresh).GetProperty("error").GetString());
        }
        finally
        {
            if (endpoint is not null)
            {
                try { await RetireDaemonForTestAsync(endpoint); } catch { }
            }
            await Task.WhenAll(
                legacyClient is null ? Task.CompletedTask : TryDisposeClientAsync(legacyClient),
                daemonClient is null ? Task.CompletedTask : TryDisposeClientAsync(daemonClient));
            if (endpoint is not null)
                await CleanupEndpointForTestAsync(endpoint);
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public async Task ThreeClientsShareASublinearHeavyProcessFootprint()
    {
        string daemonRoot = Directory.CreateTempSubdirectory("Phoenix daemon footprint ").FullName;
        string standaloneRoot = Directory.CreateTempSubdirectory(
            "Phoenix standalone footprint ").FullName;
        var daemonClients = new List<McpClient>();
        McpClient? standalone = null;
        DaemonEndpoint? endpoint = null;
        try
        {
            string executable = FindMcpExecutable();
            endpoint = DaemonEndpoint.Create(daemonRoot, null);
            McpClient[] started = await Task.WhenAll(
                CreateClientAsync(executable, daemonRoot),
                CreateClientAsync(executable, daemonRoot),
                CreateClientAsync(executable, daemonRoot));
            daemonClients.AddRange(started);
            standalone = await CreateStandaloneClientAsync(executable, standaloneRoot);

            JsonElement[] daemonCapabilities = await Task.WhenAll(
                daemonClients.Select(client => CallAsync(client, "server_capabilities")));
            int daemonPid = daemonCapabilities[0].GetProperty("runtime")
                .GetProperty("processId").GetInt32();
            Assert.All(daemonCapabilities, capability => Assert.Equal(
                daemonPid,
                capability.GetProperty("runtime").GetProperty("processId").GetInt32()));
            JsonElement standaloneCapabilities = await CallAsync(
                standalone, "server_capabilities");
            int standalonePid = standaloneCapabilities.GetProperty("runtime")
                .GetProperty("processId").GetInt32();
            Assert.NotEqual(daemonPid, standalonePid);

            using Process daemonProcess = Process.GetProcessById(daemonPid);
            using Process standaloneProcess = Process.GetProcessById(standalonePid);
            long daemonBytes = await MinimumWorkingSetAsync(daemonProcess);
            long standaloneBytes = await MinimumWorkingSetAsync(standaloneProcess);
            long allowed = checked(standaloneBytes + standaloneBytes / 2 + 32L * 1024 * 1024);
            Assert.True(
                daemonBytes <= allowed,
                $"Three-client daemon RSS {daemonBytes:N0} exceeded 1.5x standalone plus 32 MiB ({allowed:N0}); standalone RSS was {standaloneBytes:N0}.");

            TimeSpan daemonLatency = await BestCapabilitiesLatencyAsync(daemonClients[0]);
            TimeSpan standaloneLatency = await BestCapabilitiesLatencyAsync(standalone);
            Assert.True(
                daemonLatency <= standaloneLatency * 2 + TimeSpan.FromMilliseconds(100),
                $"Warm daemon relay latency {daemonLatency} materially exceeded standalone {standaloneLatency}.");
        }
        finally
        {
            if (endpoint is not null)
            {
                try { await RetireDaemonForTestAsync(endpoint); } catch { }
            }
            IEnumerable<Task> disposals = daemonClients.Select(TryDisposeClientAsync);
            if (standalone is not null)
                disposals = disposals.Append(TryDisposeClientAsync(standalone));
            await Task.WhenAll(disposals);
            if (endpoint is not null)
                await CleanupEndpointForTestAsync(endpoint);
            TestWorkspaceCleanup.DeleteWorkspace(daemonRoot);
            TestWorkspaceCleanup.DeleteWorkspace(standaloneRoot);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IdleLingerStopsUnlessKeepAliveIsEnabled(bool keepAlive)
    {
        string root = Directory.CreateTempSubdirectory("Phoenix daemon idle ").FullName;
        DaemonEndpoint endpoint = DaemonEndpoint.Create(root, null);
        using Process daemon = LaunchDaemonForTest(
            FindMcpExecutable(), root, keepAlive, idleMilliseconds: 350);
        try
        {
            await WaitUntilAsync(
                () => DaemonDescriptor.TryRead(endpoint)?.Pid == daemon.Id,
                TimeSpan.FromSeconds(15));
            if (!OperatingSystem.IsWindows())
                Assert.Equal(daemon.Id, getsid(daemon.Id));
            if (keepAlive)
            {
                await Task.Delay(1_200);
                Assert.False(daemon.HasExited);
                await RetireDaemonForTestAsync(endpoint);
            }
            await daemon.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, daemon.ExitCode);
        }
        finally
        {
            if (!daemon.HasExited)
            {
                try { await RetireDaemonForTestAsync(endpoint); } catch { }
                try { await daemon.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
                catch { daemon.Kill(entireProcessTree: false); }
            }
            await CleanupEndpointForTestAsync(endpoint);
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public void CommandLineRequiresOneExplicitLaunchMode()
    {
        string? prior = Environment.GetEnvironmentVariable("CODENAV_SHARED_DAEMON");
        try
        {
            Environment.SetEnvironmentVariable("CODENAV_SHARED_DAEMON", "1");
            Assert.Equal(McpLaunchMode.SharedProxy, McpCommandLine.Parse([]).Mode);
            Assert.Equal(McpLaunchMode.SharedProxy,
                McpCommandLine.Parse(["--shared-daemon"]).Mode);
            Assert.Equal(McpLaunchMode.Standalone,
                McpCommandLine.Parse(["--standalone"]).Mode);
            Assert.True(McpCommandLine.Parse([
                "--shared-daemon", "--daemon-fallback-standalone"]).StandaloneFallback);
            Assert.True(DaemonProxy.ShouldUseStandaloneFallback(
                enabled: true, availabilityOnly: true));
            Assert.False(DaemonProxy.ShouldUseStandaloneFallback(
                enabled: true, availabilityOnly: false));
            Assert.False(DaemonProxy.ShouldUseStandaloneFallback(
                enabled: false, availabilityOnly: true));
            Assert.Throws<ArgumentException>(() =>
                McpCommandLine.Parse(["--shared-daemon", "--standalone"]));
            Assert.Throws<ArgumentException>(() =>
                McpCommandLine.Parse(["--workspace-root"]));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODENAV_SHARED_DAEMON", prior);
        }
    }

    private static async Task<McpClient> CreateClientAsync(
        string executable,
        string root,
        params string[] additionalArguments)
    {
        var arguments = new List<string>
        {
            "--workspace-root", root,
            "--shared-daemon",
            "--daemon-idle-ms", "600",
        };
        arguments.AddRange(additionalArguments);
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "Phoenix shared daemon test",
            Command = executable,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
            Arguments = arguments,
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        return await McpClient.CreateAsync(transport, cancellationToken: timeout.Token);
    }

    private static async Task<McpClient> CreateStandaloneClientAsync(
        string executable,
        string root)
    {
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "Phoenix standalone compatibility test",
            Command = executable,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
            Arguments = ["--workspace-root", root, "--standalone"],
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        return await McpClient.CreateAsync(transport, cancellationToken: timeout.Token);
    }

    private static async Task<JsonElement> CallAsync(McpClient client, string tool)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        CallToolResult result = await client.CallToolAsync(
            tool,
            new Dictionary<string, object?>(),
            cancellationToken: timeout.Token);
        Assert.False(result.IsError is true);
        return ParseContent(result);
    }

    private static JsonElement ParseContent(CallToolResult result)
    {
        TextContentBlock text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content));
        return JsonDocument.Parse(text.Text).RootElement.Clone();
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (predicate()) return;
            await Task.Delay(100);
        }
        Assert.True(predicate(), "Condition did not become true before timeout.");
    }

    private static async Task<long> MinimumWorkingSetAsync(Process process)
    {
        long minimum = long.MaxValue;
        for (int sample = 0; sample < 5; sample++)
        {
            process.Refresh();
            minimum = Math.Min(minimum, process.WorkingSet64);
            await Task.Delay(100);
        }
        return minimum;
    }

    private static async Task<TimeSpan> BestCapabilitiesLatencyAsync(McpClient client)
    {
        _ = await CallAsync(client, "server_capabilities");
        TimeSpan best = TimeSpan.MaxValue;
        for (int sample = 0; sample < 3; sample++)
        {
            var elapsed = Stopwatch.StartNew();
            for (int i = 0; i < 10; i++)
                _ = await CallAsync(client, "server_capabilities");
            elapsed.Stop();
            if (elapsed.Elapsed < best) best = elapsed.Elapsed;
        }
        return best;
    }

    private static async Task DisposeClientAsync(McpClient client)
    {
        await client.DisposeAsync();
        await client.Completion.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static async Task TryDisposeClientAsync(McpClient client)
    {
        try { await DisposeClientAsync(client); } catch { }
    }

    private static Process LaunchDaemonForTest(
        string executable,
        string root,
        bool keepAlive,
        int idleMilliseconds)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
        };
        start.ArgumentList.Add("--daemon");
        start.ArgumentList.Add("--workspace-root");
        start.ArgumentList.Add(root);
        start.ArgumentList.Add("--daemon-idle-ms");
        start.ArgumentList.Add(idleMilliseconds.ToString());
        if (keepAlive) start.ArgumentList.Add("--keepalive");
        return Process.Start(start) ?? throw new IOException("Test daemon did not start.");
    }

    private static async Task CleanupEndpointForTestAsync(DaemonEndpoint endpoint)
    {
        await WaitUntilAsync(
            () => !File.Exists(endpoint.DescriptorPath),
            TimeSpan.FromSeconds(30));
        if (!OperatingSystem.IsWindows() && endpoint.SocketPath is not null)
            DaemonUnixFileAuthority.TryRemoveOwnedSocket(endpoint.SocketPath);
        try { File.Delete(endpoint.StartupLockPath); } catch { }
    }

    private static async Task RetireDaemonForTestAsync(DaemonEndpoint endpoint)
    {
        await using Stream stream = await DaemonTransport.ConnectAsync(
            endpoint, TimeSpan.FromSeconds(2), CancellationToken.None);
        DaemonHandshakeRequest request = DaemonProtocol.CreateRequest(endpoint, "test-retire")
            with { ToolVersion = "99.0.0" };
        await DaemonProtocol.WriteRequestAsync(
            stream,
            DaemonPreambleMode.RetireAndReplace,
            request,
            CancellationToken.None);
        DaemonHandshakeResponse? response = await DaemonProtocol.ReadResponseAsync(
            stream, CancellationToken.None);
        Assert.NotNull(response);
        Assert.True(response.Accepted);
        Assert.True(response.Retiring);
    }

    private static string FindMcpExecutable()
    {
        string repository = FindRepositoryRoot();
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Debug";
        string executable = Path.Combine(
            repository,
            "src",
            "CodeNav.Mcp",
            "bin",
            configuration,
            "net10.0",
            OperatingSystem.IsWindows() ? "PhoenixCodeNav.Mcp.exe" : "PhoenixCodeNav.Mcp");
        Assert.True(File.Exists(executable), $"MCP apphost missing: {executable}");
        return executable;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "PhoenixCodeNav.sln")))
            directory = directory.Parent;
        return directory?.FullName ??
               throw new InvalidOperationException("Could not locate PhoenixCodeNav.sln.");
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int getsid(int processId);

    private sealed class FlakyDaemonListener : IDaemonTransportListener
    {
        internal int Attempts { get; private set; }

        public ValueTask<Stream> AcceptAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Attempts++;
            if (Attempts == 1)
                throw new IOException("synthetic transient accept failure");
            return ValueTask.FromResult<Stream>(new MemoryStream());
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
