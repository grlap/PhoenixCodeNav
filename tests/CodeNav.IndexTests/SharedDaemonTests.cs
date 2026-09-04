using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
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

[CollectionDefinition("Shared daemon MCP process isolation", DisableParallelization = true)]
public sealed class SharedDaemonProcessCollection;

[Collection("Shared daemon MCP process isolation")]
public sealed class SharedDaemonTests
{
    [Theory]
    [InlineData(IndexStartupFailureCause.None, "daemon_index_startup_failed")]
    [InlineData(IndexStartupFailureCause.DestinationUnsafe,
        "daemon_index_destination_unsafe")]
    [InlineData(IndexStartupFailureCause.WriterLeaseContended,
        "daemon_writer_unavailable")]
    [InlineData(IndexStartupFailureCause.WriterAuthorityUnavailable,
        "daemon_writer_authority_unavailable")]
    [InlineData(IndexStartupFailureCause.DestinationChanged,
        "daemon_index_destination_unsafe")]
    [InlineData(IndexStartupFailureCause.DestinationForeign,
        "daemon_index_destination_foreign")]
    [InlineData(IndexStartupFailureCause.RebuildRequired,
        "daemon_index_rebuild_required")]
    [InlineData(IndexStartupFailureCause.DestinationValidationFailed,
        "daemon_index_validation_failed")]
    public void TypedIndexStartupFailuresMapWithoutDependingOnHumanProse(
        IndexStartupFailureCause startupCause,
        string expectedDaemonCause)
    {
        Assert.Equal(expectedDaemonCause,
            DaemonStartupFailures.FromIndexFailure(startupCause).Cause);
    }

    [Fact]
    public async Task PrivateStartupChannelRoundTripsAndClosedPipeFailsVisibly()
    {
        var expected = DaemonStartupReport.Refused(
            42,
            new DaemonUnavailableFailure(
                "daemon_test_refusal",
                "Test startup was refused.",
                "Resolve the test condition.",
                Retryable: false));
        await using var stream = new MemoryStream();
        await DaemonStartupChannel.WriteAsync(stream, expected);
        stream.Position = 0;
        Assert.Equal(expected, await DaemonStartupChannel.ReadAsync(stream));

        await using var closed = new MemoryStream();
        await Assert.ThrowsAsync<EndOfStreamException>(() =>
            DaemonStartupChannel.ReadAsync(closed).AsTask());
    }

    [Fact]
    public async Task FailedStartupReportsTerminateTheOwnedBootstrapProcess()
    {
        string root = Directory.CreateTempSubdirectory(
            "Phoenix failed startup cleanup ").FullName;
        Process? malformed = null;
        Process? timedOut = null;
        try
        {
            string executable = FindMcpExecutable();
            malformed = LaunchSilentStandaloneForStartupCleanupTest(executable, root);
            Assert.False(malformed.HasExited);
            await using var invalidReport = new MemoryStream([0, 0, 0, 1]);
            DaemonProxyFailureException invalid =
                await Assert.ThrowsAsync<DaemonProxyFailureException>(() =>
                    DaemonProxy.ReadStartupReportAsync(
                        malformed,
                        DateTime.UtcNow + TimeSpan.FromSeconds(5),
                        CancellationToken.None,
                        invalidReport));
            Assert.Equal("daemon_startup_report_invalid", invalid.Failure.Cause);
            Assert.True(malformed.HasExited);

            timedOut = LaunchSilentStandaloneForStartupCleanupTest(executable, root);
            Assert.False(timedOut.HasExited);
            DaemonProxyFailureException timeout =
                await Assert.ThrowsAsync<DaemonProxyFailureException>(() =>
                    DaemonProxy.ReadStartupReportAsync(
                        timedOut,
                        DateTime.UtcNow + TimeSpan.FromMilliseconds(250),
                        CancellationToken.None));
            Assert.Equal("daemon_startup_report_timeout", timeout.Failure.Cause);
            Assert.True(timedOut.HasExited);
        }
        finally
        {
            if (malformed is not null)
            {
                await DaemonProcessIsolation.TerminateFailedStartupAsync(malformed);
                malformed.Dispose();
            }
            if (timedOut is not null)
            {
                await DaemonProcessIsolation.TerminateFailedStartupAsync(timedOut);
                timedOut.Dispose();
            }
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public void StartupStatusRequiresALiveExactOwnerAndIgnoresCorruption()
    {
        string root = Directory.CreateTempSubdirectory(
            "Phoenix daemon startup status ").FullName;
        DaemonEndpoint endpoint = DaemonEndpoint.Create(root, null);
        var failure = new DaemonUnavailableFailure(
            "daemon_index_rebuild_required",
            "Index rebinding requires an explicit rebuild.",
            "Approve an explicit full rebuild.",
            Retryable: false);
        try
        {
            DaemonStartupStatus.Publish(endpoint, rebuild: false, failure);
            Assert.Equal(failure,
                DaemonStartupStatus.TryReadLiveFailure(endpoint, rebuild: false));
            Assert.Null(DaemonStartupStatus.TryReadLiveFailure(endpoint, rebuild: true));

            File.WriteAllText(endpoint.StartupStatusPath, "{not-json");
            Assert.Null(DaemonStartupStatus.TryReadLiveFailure(endpoint, rebuild: false));
        }
        finally
        {
            DaemonStartupStatus.Delete(endpoint);
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public async Task MovedIndexStartupRefusalReachesTheTransparentMcpShim()
    {
        string original = Directory.CreateTempSubdirectory(
            "Phoenix daemon moved index original ").FullName;
        string moved = original + "-moved";
        McpClient? client = null;
        DaemonEndpoint? endpoint = null;
        try
        {
            File.WriteAllText(Path.Combine(original, "Moved.cs"),
                "namespace Moved; public sealed class Example { }");
            IndexBuilder.Build(original, IndexBuilder.DefaultDbPath(original));
            Directory.Move(original, moved);
            endpoint = DaemonEndpoint.Create(moved, null);

            client = await CreateClientAsync(FindMcpExecutable(), moved);
            JsonElement capabilities = await CallAsync(client, "server_capabilities");
            JsonElement meta = capabilities.GetProperty("meta");
            Assert.Equal("unavailable", meta.GetProperty("indexMode").GetString());
            Assert.Equal("daemon_index_rebuild_required",
                meta.GetProperty("cause").GetString());
            Assert.Contains("--rebuild", meta.GetProperty("recovery").GetString());
            Assert.False(meta.GetProperty("retryable").GetBoolean());
            Assert.False(File.Exists(endpoint.DescriptorPath));
        }
        finally
        {
            if (client is not null) await TryDisposeClientAsync(client);
            if (endpoint is not null) await CleanupEndpointForTestAsync(endpoint);
            TestWorkspaceCleanup.DeleteWorkspace(moved);
            TestWorkspaceCleanup.DeleteWorkspace(original);
        }
    }

    [Fact]
    public async Task ConcurrentWaiterReusesExactWriterRefusalWithoutRespawning()
    {
        string root = Directory.CreateTempSubdirectory(
            "Phoenix daemon writer refusal ").FullName;
        string database = IndexBuilder.DefaultDbPath(root);
        using var writer = new IndexManager(root, database);
        McpClient? first = null;
        McpClient? second = null;
        McpClient? repaired = null;
        DaemonEndpoint endpoint = DaemonEndpoint.Create(root, null);
        try
        {
            writer.Start();
            Assert.True(writer.IsWriter, writer.Health().Error);

            first = await CreateClientAsync(FindMcpExecutable(), root);
            JsonElement firstCapabilities = await CallAsync(first, "server_capabilities");
            Assert.Equal("daemon_writer_unavailable",
                firstCapabilities.GetProperty("meta").GetProperty("cause").GetString());
            byte[] firstStatus = File.ReadAllBytes(endpoint.StartupStatusPath);

            second = await CreateClientAsync(FindMcpExecutable(), root);
            JsonElement secondCapabilities = await CallAsync(second, "server_capabilities");
            Assert.Equal("daemon_writer_unavailable",
                secondCapabilities.GetProperty("meta").GetProperty("cause").GetString());
            Assert.Contains("close the Phoenix session that first reported",
                secondCapabilities.GetProperty("meta").GetProperty("recovery").GetString());
            _ = await CallAsync(first, "server_capabilities");
            Assert.Equal(firstStatus, File.ReadAllBytes(endpoint.StartupStatusPath));
            Assert.False(File.Exists(endpoint.DescriptorPath));

            await TryDisposeClientAsync(second);
            second = null;
            await TryDisposeClientAsync(first);
            first = null;
            writer.Dispose();

            repaired = await CreateClientAsync(FindMcpExecutable(), root);
            JsonElement repairedCapabilities = await CallAsync(
                repaired, "server_capabilities");
            Assert.Equal("daemon", repairedCapabilities.GetProperty("runtime")
                .GetProperty("indexMode").GetString());
            Assert.False(File.Exists(endpoint.StartupStatusPath));
        }
        finally
        {
            if (repaired is not null) await TryDisposeClientAsync(repaired);
            try { await RetireDaemonForTestAsync(endpoint); } catch { }
            if (second is not null) await TryDisposeClientAsync(second);
            if (first is not null) await TryDisposeClientAsync(first);
            writer.Dispose();
            await CleanupEndpointForTestAsync(endpoint);
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public async Task CliDiscoversAndInvokesTheLiveSurfaceThroughTheSharedDaemon()
    {
        string root = Directory.CreateTempSubdirectory("Phoenix agent CLI ").FullName;
        string argumentsFile = Path.Combine(root, "cli-arguments.json");
        string executable = FindMcpExecutable();
        DaemonEndpoint endpoint = DaemonEndpoint.Create(root, null);
        McpClient? client = null;
        try
        {
            File.WriteAllText(Path.Combine(root, "CliTarget.cs"),
                "namespace CliFixture; public sealed class CliTarget { } " +
                "public sealed class CaféTarget { }");
            File.WriteAllText(argumentsFile,
                "{\"query\":\"CaféTarget\",\"lang\":\"csharp\",\"limit\":1}");

            CliResult tools = await RunCliAsync(executable, root, ["tools"]);
            Assert.Equal(0, tools.ExitCode);
            Assert.Equal(27, tools.Payload.GetProperty("tools").GetArrayLength());
            Assert.Equal(BuildInfo.Stamp,
                tools.Payload.GetProperty("meta").GetProperty("build").GetString());
            Assert.Equal(BuildInfo.IndexSchema,
                tools.Payload.GetProperty("meta").GetProperty("indexSchema").GetString());
            Assert.Contains(tools.Payload.GetProperty("tools").EnumerateArray(),
                tool => tool.GetProperty("name").GetString() == "search_symbol");

            CliResult help = await RunCliAsync(
                executable, root, ["help", "search_symbol"]);
            Assert.Equal(0, help.ExitCode);
            Assert.Equal("search_symbol", help.Payload.GetProperty("name").GetString());
            Assert.Equal("object", help.Payload.GetProperty("inputSchema")
                .GetProperty("type").GetString());
            Assert.Equal(BuildInfo.Stamp,
                help.Payload.GetProperty("meta").GetProperty("build").GetString());

            CliResult schema = await RunCliAsync(
                executable, root, ["schema", "search_symbol"]);
            Assert.Equal(0, schema.ExitCode);
            Assert.True(schema.Payload.GetProperty("properties")
                .TryGetProperty("query", out _));

            CliResult offlineSchema = await RunCliAsync(
                executable,
                Path.Combine(root, "missing-workspace"),
                ["schema", "search_symbol"]);
            Assert.Equal(0, offlineSchema.ExitCode);
            Assert.True(offlineSchema.Payload.GetProperty("properties")
                .TryGetProperty("query", out _));

            CliResult unknown = await RunCliAsync(
                executable, root, ["not_a_phoenix_tool"]);
            Assert.Equal(2, unknown.ExitCode);
            Assert.Equal("bad_request", unknown.Payload.GetProperty("error").GetString());
            Assert.Equal("unknown_tool", unknown.Payload.GetProperty("reason").GetString());

            CliResult multibyteUnknown = await RunCliAsync(
                executable, root, [new string('\u00e9', 20_000)]);
            Assert.Equal(2, multibyteUnknown.ExitCode);
            Assert.True(Encoding.UTF8.GetByteCount(multibyteUnknown.RawOutput) <=
                        Json.HardBudgetBytes + Environment.NewLine.Length);
            Assert.Contains(new string('\u00e9', 32), multibyteUnknown.RawOutput,
                StringComparison.Ordinal);

            string missingWorkspacePath = Path.Combine(root, "missing-cli-workspace");
            CliResult missingWorkspace = await RunCliAsync(
                executable, missingWorkspacePath, ["server_capabilities"]);
            Assert.Equal(2, missingWorkspace.ExitCode);
            Assert.Equal("bad_request",
                missingWorkspace.Payload.GetProperty("error").GetString());
            Assert.Equal("workspaceRoot",
                missingWorkspace.Payload.GetProperty("field").GetString());
            Assert.Equal("path_not_found",
                missingWorkspace.Payload.GetProperty("reason").GetString());

            CliResult wrongCase = await RunCliAsync(executable, root,
                ["search_symbol", "--Query", "CliTarget"]);
            Assert.Equal(2, wrongCase.ExitCode);
            Assert.Equal("unknown_field", wrongCase.Payload.GetProperty("reason").GetString());
            Assert.Equal("Query", wrongCase.Payload.GetProperty("field").GetString());

            CliResult missingArgumentsFile = await RunCliAsync(executable, root,
                ["search_symbol", "--args-file", Path.Combine(root, "missing.json")]);
            Assert.Equal(2, missingArgumentsFile.ExitCode);
            Assert.Equal("argument_source_unavailable",
                missingArgumentsFile.Payload.GetProperty("reason").GetString());

            CliResult nonRegularArgumentsFile = await RunCliAsync(executable, root,
                ["search_symbol", "--args-file", root]);
            Assert.Equal(2, nonRegularArgumentsFile.ExitCode);
            Assert.Equal("argument_source_not_regular",
                nonRegularArgumentsFile.Payload.GetProperty("reason").GetString());

            if (!OperatingSystem.IsWindows())
            {
                string symlinkArgumentsFile = Path.Combine(root, "cli-arguments-link.json");
                File.CreateSymbolicLink(symlinkArgumentsFile, argumentsFile);
                CliResult symlinkArguments = await RunCliAsync(executable, root,
                    ["search_symbol", "--args-file", symlinkArgumentsFile]);
                Assert.Equal(2, symlinkArguments.ExitCode);
                Assert.Equal("argument_source_not_regular",
                    symlinkArguments.Payload.GetProperty("reason").GetString());

                string fifoArgumentsFile = Path.Combine(root, "cli-arguments.fifo");
                Assert.Equal(0, mkfifo(fifoArgumentsFile, 0x180)); // 0600
                CliResult fifoArguments = await RunCliAsync(executable, root,
                    ["search_symbol", "--args-file", fifoArgumentsFile]);
                Assert.Equal(2, fifoArguments.ExitCode);
                Assert.Equal("argument_source_not_regular",
                    fifoArguments.Payload.GetProperty("reason").GetString());
            }

            CliResult rebuild = await RunCliAsync(executable, root,
                ["search_symbol", "--rebuild", "--query", "CliTarget"]);
            Assert.Equal(2, rebuild.ExitCode);
            Assert.Equal("unexpected_field", rebuild.Payload.GetProperty("reason").GetString());
            Assert.Equal("rebuild", rebuild.Payload.GetProperty("field").GetString());
            Assert.Contains("refresh_index", rebuild.Payload.GetProperty("detail").GetString());

            CliResult keepAlive = await RunCliAsync(executable, root,
                ["search_symbol", "--keepalive", "--query", "CliTarget"]);
            Assert.Equal(2, keepAlive.ExitCode);
            Assert.Equal("unexpected_field",
                keepAlive.Payload.GetProperty("reason").GetString());
            Assert.Equal("keepalive", keepAlive.Payload.GetProperty("field").GetString());

            CliResult daemonIdle = await RunCliAsync(executable, root,
                ["search_symbol", "--daemon-idle-ms", "100", "--query", "CliTarget"]);
            Assert.Equal(2, daemonIdle.ExitCode);
            Assert.Equal("unexpected_field",
                daemonIdle.Payload.GetProperty("reason").GetString());
            Assert.Equal("daemonIdleMs", daemonIdle.Payload.GetProperty("field").GetString());

            CliResult prettyError = await RunCliAsync(executable, root,
                ["search_symbol", "--pretty", "--rebuild"]);
            Assert.Equal(2, prettyError.ExitCode);
            Assert.Contains('\n', prettyError.RawOutput.TrimEnd('\r', '\n'));

            Assert.False(File.Exists(endpoint.DescriptorPath));
            Assert.False(File.Exists(endpoint.StartupLockPath));
            Assert.False(File.Exists(endpoint.StartupStatusPath));
            if (endpoint.SocketPath is not null)
                Assert.False(File.Exists(endpoint.SocketPath));

            client = await CreateClientAsync(executable, root);
            await WaitForIndexStateAsync(client, "ready");
            JsonElement mcpCapabilities = await CallAsync(client, "server_capabilities");
            int daemonPid = mcpCapabilities.GetProperty("runtime")
                .GetProperty("processId").GetInt32();

            CliResult capabilities = await RunCliAsync(
                executable, root, ["server_capabilities"]);
            Assert.Equal(0, capabilities.ExitCode);
            Assert.Equal(daemonPid, capabilities.Payload.GetProperty("runtime")
                .GetProperty("processId").GetInt32());
            Assert.Contains(capabilities.Payload.GetProperty("features").EnumerateArray(),
                feature => feature.GetProperty("id").GetString() ==
                           "agent-cli-tool-surface");

            CliResult flags = await RunCliAsync(executable, root,
                ["search_symbol", "--query", "CliTarget", "--lang", "csharp", "--limit", "1"]);
            Assert.Equal(0, flags.ExitCode);
            Assert.Equal("CliTarget", Assert.Single(flags.Payload.GetProperty("symbols")
                .EnumerateArray()).GetProperty("name").GetString());

            const string jsonArguments =
                "{\"query\":\"CaféTarget\",\"lang\":\"csharp\",\"limit\":1}";
            CliResult json = await RunCliAsync(
                executable, root, ["search_symbol", "--json", jsonArguments]);
            Assert.Equal(0, json.ExitCode);
            Assert.Equal("CaféTarget", Assert.Single(json.Payload.GetProperty("symbols")
                .EnumerateArray()).GetProperty("name").GetString());

            CliResult file = await RunCliAsync(
                executable, root, ["search_symbol", "--args-file", argumentsFile]);
            Assert.Equal(0, file.ExitCode);
            Assert.Equal("CaféTarget", Assert.Single(file.Payload.GetProperty("symbols")
                .EnumerateArray()).GetProperty("name").GetString());

            CliResult stdin = await RunCliAsync(
                executable, root, ["search_symbol", "--args-file", "-"], jsonArguments);
            Assert.Equal(0, stdin.ExitCode);
            Assert.Equal("CaféTarget", Assert.Single(stdin.Payload.GetProperty("symbols")
                .EnumerateArray()).GetProperty("name").GetString());

            CliResult badRequest = await RunCliAsync(executable, root, ["definition"]);
            Assert.Equal(2, badRequest.ExitCode);
            Assert.Equal("bad_request",
                badRequest.Payload.GetProperty("error").GetString());

            CliResult domainFailure = await RunCliAsync(executable, root,
                ["definition", "--documentationCommentId", "T:CliFixture.DoesNotExist"]);
            Assert.Equal(1, domainFailure.ExitCode);
            Assert.Equal("symbol_not_found",
                domainFailure.Payload.GetProperty("error").GetString());
        }
        finally
        {
            if (client is not null) await TryDisposeClientAsync(client);
            try { await RetireDaemonForTestAsync(endpoint); } catch { }
            await CleanupEndpointForTestAsync(endpoint);
            try { File.Delete(argumentsFile); } catch { }
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public async Task ColdConcurrentCliCallsElectAndReuseOneSharedDaemon()
    {
        string root = Directory.CreateTempSubdirectory(
            "Phoenix cold concurrent agent CLI ").FullName;
        string executable = FindMcpExecutable();
        DaemonEndpoint endpoint = DaemonEndpoint.Create(root, null);
        try
        {
            Assert.False(File.Exists(endpoint.DescriptorPath));

            Task<CliResult> firstCall = RunCliAsync(
                executable, root, ["server_capabilities"]);
            Task<CliResult> secondCall = RunCliAsync(
                executable, root, ["server_capabilities"]);
            CliResult[] calls = await Task.WhenAll(firstCall, secondCall);

            Assert.All(calls, call => Assert.Equal(0, call.ExitCode));
            int firstPid = calls[0].Payload.GetProperty("runtime")
                .GetProperty("processId").GetInt32();
            int secondPid = calls[1].Payload.GetProperty("runtime")
                .GetProperty("processId").GetInt32();
            Assert.Equal(firstPid, secondPid);
            Assert.Equal(BuildInfo.Version,
                calls[0].Payload.GetProperty("build").GetProperty("version").GetString());
            Assert.Equal(BuildInfo.IndexSchema,
                calls[0].Payload.GetProperty("build").GetProperty("indexSchema").GetString());
            Assert.True(File.Exists(endpoint.DescriptorPath));
            DaemonDescriptorRecord descriptor = Assert.IsType<DaemonDescriptorRecord>(
                DaemonDescriptor.TryRead(endpoint));
            Assert.Equal(firstPid, descriptor.Pid);
        }
        finally
        {
            try { await RetireDaemonForTestAsync(endpoint); } catch { }
            await CleanupEndpointForTestAsync(endpoint);
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public async Task CliReportsDaemonDeathDuringMcpExchangeAsUnavailable()
    {
        string root = Directory.CreateTempSubdirectory(
            "Phoenix CLI dying daemon ").FullName;
        string executable = FindMcpExecutable();
        DaemonEndpoint endpoint = DaemonEndpoint.Create(root, null);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var listening = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Task dyingDaemon = ServeHandshakeThenCloseAsync(
            endpoint, listening, timeout.Token);
        try
        {
            await listening.Task.WaitAsync(timeout.Token);
            CliResult result = await RunCliAsync(
                executable, root, ["server_capabilities"]);

            Assert.Equal(3, result.ExitCode);
            Assert.Equal("unavailable",
                result.Payload.GetProperty("meta").GetProperty("indexMode").GetString());
            Assert.Equal("daemon_cli_transport_failed",
                result.Payload.GetProperty("meta").GetProperty("cause").GetString());
            Assert.True(result.Payload.GetProperty("meta").GetProperty("retryable").GetBoolean());
            Assert.False(result.Payload.TryGetProperty("error", out _));
            await dyingDaemon.WaitAsync(timeout.Token);
        }
        finally
        {
            timeout.Cancel();
            try { await dyingDaemon; } catch { }
            DaemonDescriptor.DeleteOwn(endpoint);
            await CleanupEndpointForTestAsync(endpoint);
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public async Task CliReportsWriterRefusalAsJsonAndExitThree()
    {
        string root = Directory.CreateTempSubdirectory(
            "Phoenix agent CLI writer refusal ").FullName;
        string database = IndexBuilder.DefaultDbPath(root);
        string executable = FindMcpExecutable();
        DaemonEndpoint endpoint = DaemonEndpoint.Create(root, null);
        using var writer = new IndexManager(root, database);
        try
        {
            writer.Start();
            Assert.True(writer.IsWriter, writer.Health().Error);

            CliResult refusal = await RunCliAsync(
                executable, root, ["server_capabilities"]);
            Assert.Equal(3, refusal.ExitCode);
            Assert.Equal("unavailable", refusal.Payload.GetProperty("meta")
                .GetProperty("indexMode").GetString());
            Assert.Equal("daemon_writer_unavailable", refusal.Payload.GetProperty("meta")
                .GetProperty("cause").GetString());
        }
        finally
        {
            writer.Dispose();
            await CleanupEndpointForTestAsync(endpoint);
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

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
                endpoint, null, false, false, "validation-test");
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
    public void UnixRuntimeDirectorySkipsInvalidAndOverlongPreferredCandidates()
    {
        if (OperatingSystem.IsWindows()) return;
        string root = Directory.CreateTempSubdirectory("Phoenix daemon runtime ladder ").FullName;
        string overlongParent = Path.Combine(root, new string('x', 120));
        Directory.CreateDirectory(overlongParent);
        uint fallbackId = unchecked(3_000_000_000u + (uint)Environment.ProcessId);
        string expected = Path.Combine(
            DaemonUnixFileAuthority.ResolveExistingDirectory("/tmp"),
            $"phoenix-codenav-{fallbackId}");
        try
        {
            string missing = Path.Combine(root, "missing");
            string selected = DaemonEndpoint.SelectUnixRuntimeDirectory(
                missing,
                overlongParent,
                "/tmp",
                fallbackId);
            Assert.Equal(expected, selected);
            DaemonUnixFileAuthority.VerifyOwnerOnlyDirectory(selected);

            Assert.Throws<DaemonRuntimeDirectoryUnavailableException>(() =>
                DaemonEndpoint.SelectUnixRuntimeDirectory(
                    missing,
                    Path.Combine(root, "also-missing"),
                    Path.Combine(root, "still-missing"),
                    userId: 4242));
        }
        finally
        {
            try { Directory.Delete(expected); } catch { }
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public void UnixRuntimeDirectorySkipsUnsafeAndNonWritableExistingCandidates()
    {
        if (OperatingSystem.IsWindows()) return;
        string root = Path.Combine("/tmp", $"pr{Guid.NewGuid():N}"[..10]);
        string unsafeParent = Path.Combine(root, "unsafe");
        string unsafeTarget = Path.Combine(root, "target");
        string nonWritableParent = Path.Combine(root, "readonly");
        string fallbackParent = Path.Combine(root, "fallback");
        Directory.CreateDirectory(unsafeParent);
        Directory.CreateDirectory(unsafeTarget);
        Directory.CreateDirectory(nonWritableParent);
        Directory.CreateDirectory(fallbackParent);
        string unsafeCandidate = Path.Combine(unsafeParent, "phoenix-codenav-4242");
        Directory.CreateSymbolicLink(unsafeCandidate, unsafeTarget);
        File.SetUnixFileMode(nonWritableParent,
            UnixFileMode.UserRead | UnixFileMode.UserExecute);

        try
        {
            string selected = DaemonEndpoint.SelectUnixRuntimeDirectory(
                unsafeParent,
                nonWritableParent,
                fallbackParent,
                userId: 4242);
            string expected = Path.Combine(
                DaemonUnixFileAuthority.ResolveExistingDirectory(fallbackParent),
                "phoenix-codenav-4242");

            Assert.Equal(expected, selected);
            Assert.NotNull(new DirectoryInfo(unsafeCandidate).LinkTarget);
            DaemonUnixFileAuthority.VerifyOwnerOnlyDirectory(selected);
        }
        finally
        {
            File.SetUnixFileMode(nonWritableParent,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public void UnixRuntimeDirectoryIsStableAcrossSessionEnvironmentDifferences()
    {
        if (OperatingSystem.IsWindows()) return;
        string root = Path.Combine("/tmp", $"ps{Guid.NewGuid():N}"[..10]);
        string stableParent = Path.Combine(root, "stable");
        string firstXdg = Path.Combine(root, "xdg-a");
        string secondXdg = Path.Combine(root, "xdg-b");
        string firstTemporary = Path.Combine(root, "tmp-a");
        string secondTemporary = Path.Combine(root, "tmp-b");
        Directory.CreateDirectory(stableParent);
        Directory.CreateDirectory(firstXdg);
        Directory.CreateDirectory(secondXdg);
        Directory.CreateDirectory(firstTemporary);
        Directory.CreateDirectory(secondTemporary);

        try
        {
            string first = DaemonEndpoint.SelectUnixRuntimeDirectory(
                stableParent, firstXdg, firstTemporary, userId: 4242);
            string second = DaemonEndpoint.SelectUnixRuntimeDirectory(
                stableParent, secondXdg, secondTemporary, userId: 4242);
            string expected = Path.Combine(
                DaemonUnixFileAuthority.ResolveExistingDirectory(stableParent),
                "phoenix-codenav-4242");

            Assert.Equal(expected, first);
            Assert.Equal(first, second);
            DaemonUnixFileAuthority.VerifyOwnerOnlyDirectory(first);
            Assert.False(Directory.Exists(Path.Combine(firstXdg, "phoenix-codenav")));
            Assert.False(Directory.Exists(Path.Combine(secondXdg, "phoenix-codenav")));
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public void UnixRuntimeFallbackClassificationDrivesExactWarning()
    {
        if (OperatingSystem.IsWindows()) return;
        string root = Path.Combine("/tmp", $"pw{Guid.NewGuid():N}"[..10]);
        string workspace = Path.Combine(root, "workspace");
        string missingStableParent = Path.Combine(root, "missing-stable");
        string stableParent = Path.Combine(root, "stable");
        string xdgParent = Path.Combine(root, "xdg");
        string temporaryParent = Path.Combine(root, "tmp");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(stableParent);
        Directory.CreateDirectory(xdgParent);
        Directory.CreateDirectory(temporaryParent);

        try
        {
            DaemonEndpoint fallback = DaemonEndpoint.CreateForUnixRuntimeTest(
                workspace,
                indexDb: null,
                missingStableParent,
                xdgParent,
                temporaryParent,
                userId: 4242);
            Assert.False(fallback.IsStableRuntime);
            Assert.Equal(
                Path.Combine(
                    DaemonUnixFileAuthority.ResolveExistingDirectory(xdgParent),
                    "phoenix-codenav"),
                fallback.RuntimeDirectory);
            using var fallbackError = new StringWriter();
            DaemonRuntimeDiagnostics.WriteDiscoveryWarning(fallback, fallbackError);
            Assert.Equal(
                DaemonRuntimeDiagnostics.DiscoveryFallbackWarning + Environment.NewLine,
                fallbackError.ToString());

            DaemonEndpoint stable = DaemonEndpoint.CreateForUnixRuntimeTest(
                workspace,
                indexDb: null,
                stableParent,
                xdgParent,
                temporaryParent,
                userId: 4242);
            Assert.True(stable.IsStableRuntime);
            using var stableError = new StringWriter();
            DaemonRuntimeDiagnostics.WriteDiscoveryWarning(stable, stableError);
            Assert.Equal("", stableError.ToString());
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public void LegacyUnixCandidatesAreExistingAuthorizedAddressesOnly()
    {
        if (OperatingSystem.IsWindows()) return;
        string root = Path.Combine("/tmp", $"pl{Guid.NewGuid():N}"[..10]);
        string workspace = Path.Combine(root, "workspace");
        string xdgParent = Path.Combine(root, "xdg");
        string temporaryParent = Path.Combine(root, "tmp");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(xdgParent);
        Directory.CreateDirectory(temporaryParent);
        DaemonEndpoint primary = DaemonEndpoint.Create(workspace, null);
        string xdgRuntime = Path.Combine(
            DaemonUnixFileAuthority.ResolveExistingDirectory(xdgParent),
            "phoenix-codenav");
        string temporaryRuntime = Path.Combine(
            DaemonUnixFileAuthority.ResolveExistingDirectory(temporaryParent),
            "phoenix-codenav-4242");
        DaemonUnixFileAuthority.EnsureOwnerOnlyDirectory(xdgRuntime);
        DaemonUnixFileAuthority.EnsureOwnerOnlyDirectory(temporaryRuntime);

        try
        {
            IReadOnlyList<DaemonEndpoint> candidates =
                DaemonEndpoint.LegacyUnixCandidates(
                    primary, xdgParent, temporaryParent, userId: 4242);

            Assert.Equal(2, candidates.Count);
            Assert.Equal([xdgRuntime, temporaryRuntime],
                candidates.Select(candidate => candidate.RuntimeDirectory));
            Assert.All(candidates, candidate =>
            {
                Assert.False(candidate.IsStableRuntime);
                Assert.Equal(primary.EndpointKey, candidate.EndpointKey);
                Assert.Equal(primary.WorkspaceIdentity, candidate.WorkspaceIdentity);
                Assert.StartsWith(candidate.RuntimeDirectory, candidate.SocketPath!);
            });

            TestWorkspaceCleanup.DeleteWorkspace(temporaryRuntime);
            Assert.Single(DaemonEndpoint.LegacyUnixCandidates(
                primary, xdgParent, temporaryParent, userId: 4242));
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
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
    public async Task BlockedFirstSessionSetupDoesNotStarveSecondHandshake()
    {
        string root = Directory.CreateTempSubdirectory(
            "Phoenix daemon accept fairness ").FullName;
        DaemonEndpoint endpoint = DaemonEndpoint.Create(root, null);
        Stream? first = null;
        Stream? second = null;
        using var daemonLifetime = new CancellationTokenSource();
        using var firstHandlerEntered = new ManualResetEventSlim();
        using var releaseFirstHandler = new ManualResetEventSlim();
        int handlerOrdinal = 0;
        var daemon = new DaemonServer(
            endpoint,
            indexDb: null,
            rebuild: false,
            keepAlive: true,
            beforeConnectionHandshakeForTest: () =>
            {
                if (Interlocked.Increment(ref handlerOrdinal) != 1) return;
                firstHandlerEntered.Set();
                releaseFirstHandler.Wait();
            });
        Task<int> daemonTask = daemon.RunAsync(daemonLifetime.Token);
        try
        {
            await WaitUntilAsync(
                () => DaemonDescriptor.TryRead(endpoint)?.Pid == Environment.ProcessId,
                TimeSpan.FromSeconds(15));

            first = await DaemonTransport.ConnectAsync(
                endpoint, TimeSpan.FromSeconds(5), CancellationToken.None);
            DaemonHandshakeRequest firstRequest = DaemonProtocol.CreateRequest(
                endpoint, "blocked-first");
            await DaemonProtocol.WriteRequestAsync(
                first, DaemonPreambleMode.Connect, firstRequest, CancellationToken.None);
            Assert.True(firstHandlerEntered.Wait(TimeSpan.FromSeconds(5)));

            second = await DaemonTransport.ConnectAsync(
                endpoint, TimeSpan.FromSeconds(5), CancellationToken.None);
            DaemonHandshakeRequest secondRequest = DaemonProtocol.CreateRequest(
                endpoint, "accepted-second");
            using (var secondTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            {
                await DaemonProtocol.WriteRequestAsync(
                    second, DaemonPreambleMode.Connect, secondRequest, secondTimeout.Token);
                DaemonHandshakeResponse secondResponse = Assert.IsType<DaemonHandshakeResponse>(
                    await DaemonProtocol.ReadResponseAsync(second, secondTimeout.Token));
                Assert.True(secondResponse.Accepted);
                Assert.Equal(secondRequest.Nonce, secondResponse.Nonce);
            }

            releaseFirstHandler.Set();
            using var firstTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            DaemonHandshakeResponse firstResponse = Assert.IsType<DaemonHandshakeResponse>(
                await DaemonProtocol.ReadResponseAsync(first, firstTimeout.Token));
            Assert.True(firstResponse.Accepted);
            Assert.Equal(firstRequest.Nonce, firstResponse.Nonce);
        }
        finally
        {
            releaseFirstHandler.Set();
            if (second is not null) await second.DisposeAsync();
            if (first is not null) await first.DisposeAsync();
            daemonLifetime.Cancel();
            try { await daemonTask; } catch (OperationCanceledException) { }
            PhoenixRuntimeMode.Set(PhoenixProcessMode.Standalone);
            await CleanupEndpointForTestAsync(endpoint);
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public async Task HandlerSetupFailureStillDisposesTheAcceptedStream()
    {
        string root = Directory.CreateTempSubdirectory(
            "Phoenix daemon handler ownership ").FullName;
        DaemonEndpoint endpoint = DaemonEndpoint.Create(root, null);
        Stream? client = null;
        using var daemonLifetime = new CancellationTokenSource();
        using var handlerEntered = new ManualResetEventSlim();
        using var releaseHandler = new ManualResetEventSlim();
        var daemon = new DaemonServer(
            endpoint,
            indexDb: null,
            rebuild: false,
            keepAlive: true,
            beforeConnectionHandshakeForTest: () =>
            {
                handlerEntered.Set();
                releaseHandler.Wait();
                throw new InvalidOperationException("test handler setup failure");
            });
        Task<int> daemonTask = daemon.RunAsync(daemonLifetime.Token);
        try
        {
            await WaitUntilAsync(
                () => DaemonDescriptor.TryRead(endpoint)?.Pid == Environment.ProcessId,
                TimeSpan.FromSeconds(15));

            client = await DaemonTransport.ConnectAsync(
                endpoint, TimeSpan.FromSeconds(5), CancellationToken.None);
            DaemonHandshakeRequest request = DaemonProtocol.CreateRequest(
                endpoint, "handler-ownership");
            await DaemonProtocol.WriteRequestAsync(
                client, DaemonPreambleMode.Connect, request, CancellationToken.None);
            Assert.True(handlerEntered.Wait(TimeSpan.FromSeconds(5)));
            releaseHandler.Set();

            using var readTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAnyAsync<IOException>(() =>
                DaemonProtocol.ReadResponseAsync(client, readTimeout.Token).AsTask());
        }
        finally
        {
            releaseHandler.Set();
            if (client is not null) await client.DisposeAsync();
            daemonLifetime.Cancel();
            try { await daemonTask; } catch (OperationCanceledException) { }
            PhoenixRuntimeMode.Set(PhoenixProcessMode.Standalone);
            await CleanupEndpointForTestAsync(endpoint);
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public async Task HandshakeDeadlineUsesTypedUnavailableCause()
    {
        string root = Directory.CreateTempSubdirectory(
            "Phoenix daemon handshake timeout ").FullName;
        DaemonEndpoint endpoint = DaemonEndpoint.Create(root, null);
        IDaemonTransportListener listener = DaemonTransport.Listen(endpoint);
        var accepted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Task silentServer = Task.Run(async () =>
        {
            await using Stream stream = await listener.AcceptAsync(CancellationToken.None);
            accepted.SetResult();
            await release.Task;
        });
        var proxy = new DaemonProxy(
            endpoint, null, false, false, "handshake-timeout-test");
        try
        {
            Task<Stream> connecting = proxy.ConnectAcceptedAsync(
                endpoint, CancellationToken.None);
            await accepted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            DaemonProxyFailureException failure =
                await Assert.ThrowsAsync<DaemonProxyFailureException>(
                    async () => await connecting.WaitAsync(TimeSpan.FromSeconds(10)));

            Assert.Equal("daemon_handshake_timeout", failure.Failure.Cause);
            Assert.True(failure.Failure.Retryable);
            Assert.Contains("authority handshake", failure.Failure.Detail);
            Assert.Contains("close and restart active Phoenix sessions",
                failure.Failure.Recovery);
        }
        finally
        {
            release.TrySetResult();
            await silentServer;
            await listener.DisposeAsync();
            await CleanupEndpointForTestAsync(endpoint);
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public async Task HandshakeEofTransparentlyReelectsAfterPublishedDaemonDies()
    {
        string root = Directory.CreateTempSubdirectory(
            "Phoenix handshake EOF reelect ").FullName;
        DaemonEndpoint endpoint = DaemonEndpoint.Create(root, null);
        IDaemonTransportListener? listener = null;
        Task? closeFirstConnection = null;
        McpClient? client = null;
        try
        {
            listener = DaemonTransport.Listen(endpoint);
            IDaemonTransportListener firstListener = listener;
            closeFirstConnection = Task.Run(async () =>
            {
                await using Stream accepted = await firstListener.AcceptAsync(
                    CancellationToken.None);
                await firstListener.DisposeAsync();
            });

            client = await CreateClientAsync(FindMcpExecutable(), root);
            await closeFirstConnection;

            JsonElement capabilities = await CallAsync(client, "server_capabilities");
            Assert.Equal("daemon", capabilities.GetProperty("runtime")
                .GetProperty("indexMode").GetString());
            DaemonDescriptorRecord descriptor = Assert.IsType<DaemonDescriptorRecord>(
                DaemonDescriptor.TryRead(endpoint));
            Assert.Equal("daemon", descriptor.ProcessMode);
            Assert.NotEqual(Environment.ProcessId, descriptor.Pid);

            await TryDisposeClientAsync(client);
            client = null;
            await RetireDaemonForTestAsync(endpoint);
        }
        finally
        {
            if (client is not null) await TryDisposeClientAsync(client);
            if (listener is not null) await listener.DisposeAsync();
            if (closeFirstConnection is not null)
            {
                try { await closeFirstConnection; } catch { }
            }
            try { await RetireDaemonForTestAsync(endpoint); } catch { }
            await CleanupEndpointForTestAsync(endpoint);
            TestWorkspaceCleanup.DeleteWorkspace(root);
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
            DaemonDescriptorRecord? stale = DaemonDescriptor.TryRead(endpoint);
            Assert.Equal(Environment.ProcessId, stale?.Pid);
            Assert.Equal("non-daemon", stale?.ProcessMode);

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
                DaemonStartupStatus.Delete(endpoint);
            }
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public async Task DivergentSessionEnvironmentsShareTheDaemonDuringAndAfterRebuild()
    {
        if (OperatingSystem.IsWindows()) return;
        string root = Directory.CreateTempSubdirectory(
            "Phoenix shared daemon divergent environment ").FullName;
        string alternateRuntime = Directory.CreateTempSubdirectory(
            "Phoenix alternate runtime ").FullName;
        McpClient? first = null;
        McpClient? second = null;
        DaemonEndpoint endpoint = DaemonEndpoint.Create(root, null);
        using var buildEntered = new ManualResetEventSlim();
        using var releaseBuild = new ManualResetEventSlim();
        bool rebuildUsedDedicatedThread = false;
        using var daemonLifetime = new CancellationTokenSource();
        var daemon = new DaemonServer(
            endpoint,
            indexDb: null,
            rebuild: true,
            keepAlive: true,
            configureIndexForTest: manager =>
                manager.FullRebuildAfterTelemetryStartedForTest = () =>
                {
                    rebuildUsedDedicatedThread = !Thread.CurrentThread.IsThreadPoolThread;
                    buildEntered.Set();
                    releaseBuild.Wait();
                });
        Task<int> daemonTask = daemon.RunAsync(daemonLifetime.Token);
        try
        {
            await WaitUntilAsync(
                () => DaemonDescriptor.TryRead(endpoint)?.Pid == Environment.ProcessId,
                TimeSpan.FromSeconds(15));
            Assert.True(buildEntered.Wait(TimeSpan.FromSeconds(15)));
            Assert.True(rebuildUsedDedicatedThread,
                "The synchronous full rebuild consumed a shared ThreadPool worker.");

            string executable = FindMcpExecutable();
            first = await CreateClientAsync(executable, root);
            second = await CreateClientWithEnvironmentAsync(
                executable,
                root,
                new Dictionary<string, string?>
                {
                    ["XDG_RUNTIME_DIR"] = alternateRuntime,
                    ["TMPDIR"] = alternateRuntime,
                });

            JsonElement firstBuilding = await CallAsync(first, "server_capabilities");
            JsonElement secondBuilding = await CallAsync(second, "server_capabilities");
            Assert.Equal("building",
                firstBuilding.GetProperty("index").GetProperty("state").GetString());
            Assert.Equal("building",
                secondBuilding.GetProperty("index").GetProperty("state").GetString());
            int daemonPid = firstBuilding.GetProperty("runtime")
                .GetProperty("processId").GetInt32();
            Assert.Equal(Environment.ProcessId, daemonPid);
            Assert.Equal(daemonPid, secondBuilding.GetProperty("runtime")
                .GetProperty("processId").GetInt32());

            releaseBuild.Set();
            await WaitForIndexStateAsync(first, "ready");
            JsonElement secondReady = await CallAsync(second, "server_capabilities");
            Assert.Equal("ready",
                secondReady.GetProperty("index").GetProperty("state").GetString());
            Assert.Equal(daemonPid, secondReady.GetProperty("runtime")
                .GetProperty("processId").GetInt32());
        }
        finally
        {
            releaseBuild.Set();
            try { await RetireDaemonForTestAsync(endpoint); } catch { }
            await Task.WhenAll(
                second is null ? Task.CompletedTask : TryDisposeClientAsync(second),
                first is null ? Task.CompletedTask : TryDisposeClientAsync(first));
            daemonLifetime.Cancel();
            try
            {
                try { Assert.Equal(0, await daemonTask); }
                catch (OperationCanceledException) { }
            }
            finally
            {
                PhoenixRuntimeMode.Set(PhoenixProcessMode.Standalone);
            }
            await CleanupEndpointForTestAsync(endpoint);
            TestWorkspaceCleanup.DeleteWorkspace(root);
            TestWorkspaceCleanup.DeleteWorkspace(alternateRuntime);
        }
    }

    [Fact]
    public async Task ExplicitFullRebuildUsesDedicatedLaneAndKeepsCapabilitiesResponsive()
    {
        if (OperatingSystem.IsWindows()) return;
        string root = Directory.CreateTempSubdirectory(
            "Phoenix explicit rebuild dispatch ").FullName;
        File.WriteAllText(Path.Combine(root, "Marker.cs"),
            "public sealed class ExplicitRebuildDispatchMarker { }");
        IndexBuilder.Build(root);

        McpClient? client = null;
        DaemonEndpoint endpoint = DaemonEndpoint.Create(root, null);
        using var buildEntered = new ManualResetEventSlim();
        using var releaseBuild = new ManualResetEventSlim();
        bool rebuildUsedDedicatedThread = false;
        using var daemonLifetime = new CancellationTokenSource();
        var daemon = new DaemonServer(
            endpoint,
            indexDb: null,
            rebuild: false,
            keepAlive: true,
            configureIndexForTest: manager =>
                manager.FullRebuildAfterTelemetryStartedForTest = () =>
                {
                    rebuildUsedDedicatedThread = !Thread.CurrentThread.IsThreadPoolThread;
                    buildEntered.Set();
                    releaseBuild.Wait();
                });
        Task<int> daemonTask = daemon.RunAsync(daemonLifetime.Token);
        try
        {
            await WaitUntilAsync(
                () => DaemonDescriptor.TryRead(endpoint)?.Pid == Environment.ProcessId,
                TimeSpan.FromSeconds(15));
            client = await CreateClientAsync(FindMcpExecutable(), root);
            await WaitForIndexStateAsync(client, "ready");

            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
            {
                CallToolResult refresh = await client.CallToolAsync(
                    "refresh_index",
                    new Dictionary<string, object?> { ["force"] = "full" },
                    cancellationToken: timeout.Token);
                Assert.False(refresh.IsError is true);
                Assert.True(ParseContent(refresh).GetProperty("queued").GetBoolean());
            }
            Assert.True(buildEntered.Wait(TimeSpan.FromSeconds(15)),
                "The explicit full rebuild never entered its build phase.");
            Assert.True(rebuildUsedDedicatedThread,
                "The explicit full rebuild consumed a shared ThreadPool worker.");

            JsonElement building = await CallAsync(client, "server_capabilities");
            Assert.Equal("building",
                building.GetProperty("index").GetProperty("state").GetString());

            releaseBuild.Set();
            await WaitForIndexStateAsync(client, "ready");
        }
        finally
        {
            releaseBuild.Set();
            try { await RetireDaemonForTestAsync(endpoint); } catch { }
            if (client is not null) await TryDisposeClientAsync(client);
            daemonLifetime.Cancel();
            try
            {
                try { Assert.Equal(0, await daemonTask); }
                catch (OperationCanceledException) { }
            }
            finally
            {
                PhoenixRuntimeMode.Set(PhoenixProcessMode.Standalone);
            }
            await CleanupEndpointForTestAsync(endpoint);
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public async Task StableProxyRetiresOlderDaemonAtLegacyEnvironmentAddress()
    {
        if (OperatingSystem.IsWindows()) return;
        string root = Directory.CreateTempSubdirectory(
            "Phoenix legacy daemon upgrade ").FullName;
        string legacyParent = Path.Combine("/tmp", $"pu{Guid.NewGuid():N}"[..10]);
        Directory.CreateDirectory(legacyParent);
        McpClient? client = null;
        DaemonEndpoint stable = DaemonEndpoint.Create(root, null);
        string legacyRuntime = Path.Combine(legacyParent, "phoenix-codenav");
        DaemonUnixFileAuthority.EnsureOwnerOnlyDirectory(legacyRuntime);
        DaemonEndpoint legacy = Assert.Single(DaemonEndpoint.LegacyUnixCandidates(
            stable, legacyParent, temporaryParent: null, userId: 4242));
        using var fakeLifetime = new CancellationTokenSource();
        var retired = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Task fakeDaemon = ServeOlderLegacyDaemonAsync(
            legacy, retired, fakeLifetime.Token);
        try
        {
            await WaitUntilAsync(
                () => DaemonDescriptor.TryRead(legacy)?.Pid == Environment.ProcessId,
                TimeSpan.FromSeconds(15));
            client = await CreateClientWithEnvironmentAsync(
                FindMcpExecutable(),
                root,
                new Dictionary<string, string?>
                {
                    ["XDG_RUNTIME_DIR"] = legacyParent,
                    ["TMPDIR"] = legacyParent,
                });

            Assert.True(retired.Task.IsCompletedSuccessfully);
            JsonElement capabilities = await CallAsync(client, "server_capabilities");
            Assert.Equal("daemon",
                capabilities.GetProperty("runtime").GetProperty("indexMode").GetString());
            Assert.NotEqual(Environment.ProcessId, capabilities.GetProperty("runtime")
                .GetProperty("processId").GetInt32());
        }
        finally
        {
            fakeLifetime.Cancel();
            try { await fakeDaemon; } catch (OperationCanceledException) { }
            try { await RetireDaemonForTestAsync(stable); } catch { }
            if (client is not null) await TryDisposeClientAsync(client);
            await CleanupEndpointForTestAsync(stable);
            try { File.Delete(legacy.StartupLockPath); } catch { }
            TestWorkspaceCleanup.DeleteWorkspace(root);
            TestWorkspaceCleanup.DeleteWorkspace(legacyParent);
        }
    }

    [Fact]
    public async Task FrameworkDependentLaunchDefaultsToSharedDaemon()
    {
        string root = Directory.CreateTempSubdirectory(
            "Phoenix framework default daemon ").FullName;
        McpClient? client = null;
        DaemonEndpoint? endpoint = null;
        try
        {
            string indexDb = Path.Combine(root, ".codenav", "framework-index.db");
            endpoint = DaemonEndpoint.Create(root, indexDb);
            if (!OperatingSystem.IsWindows())
            {
                Assert.NotNull(endpoint.SocketPath);
                _ = new UnixDomainSocketEndPoint(endpoint.SocketPath);
            }
            client = await CreateFrameworkDependentClientAsync(root, indexDb,
                idleMilliseconds: 30_000);
            JsonElement capabilities = await CallAsync(client, "server_capabilities");
            Assert.Equal("daemon",
                capabilities.GetProperty("runtime").GetProperty("indexMode").GetString());
            Assert.Equal("writer",
                capabilities.GetProperty("index").GetProperty("mode").GetString());
            Assert.True(IndexOwnershipLease.IsHeld(root, indexDb));

            // The framework-dependent child is only a proxy. Closing it must leave the shared
            // daemon alive, and that daemon must keep the writer lease until explicit retirement.
            await DisposeClientAsync(client);
            client = null;
            Assert.True(IndexOwnershipLease.IsHeld(root, indexDb),
                "the shared daemon released its ownership lease when only its proxy exited");
        }
        finally
        {
            if (endpoint is not null)
            {
                try { await RetireDaemonForTestAsync(endpoint); } catch { }
            }
            if (client is not null) await TryDisposeClientAsync(client);
            if (endpoint is not null) await CleanupEndpointForTestAsync(endpoint);
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
    public async Task ExplicitStandaloneContenderIsUnavailableInsteadOfServingAsSecondMcpServer()
    {
        string root = Directory.CreateTempSubdirectory("Phoenix daemon standalone refusal ").FullName;
        McpClient? daemonClient = null;
        McpClient? standaloneClient = null;
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

            standaloneClient = await CreateStandaloneClientAsync(executable, root);
            JsonElement standaloneCapabilities = await CallAsync(
                standaloneClient, "server_capabilities");
            Assert.Equal("unavailable",
                standaloneCapabilities.GetProperty("meta").GetProperty("indexMode").GetString());
            Assert.Equal("standalone_writer_unavailable",
                standaloneCapabilities.GetProperty("meta").GetProperty("cause").GetString());
            Assert.False(standaloneCapabilities.TryGetProperty("index", out _));
            Assert.DoesNotContain("follower", standaloneCapabilities.GetRawText(),
                StringComparison.OrdinalIgnoreCase);
            CallToolResult refresh = await standaloneClient.CallToolAsync(
                "refresh_index", new Dictionary<string, object?>());
            Assert.True(refresh.IsError);
            JsonElement refreshError = ParseContent(refresh);
            Assert.Equal("phoenix_daemon_unavailable",
                refreshError.GetProperty("error").GetString());
            Assert.Equal("standalone_writer_unavailable",
                refreshError.GetProperty("cause").GetString());
        }
        finally
        {
            if (endpoint is not null)
            {
                try { await RetireDaemonForTestAsync(endpoint); } catch { }
            }
            await Task.WhenAll(
                standaloneClient is null ? Task.CompletedTask : TryDisposeClientAsync(standaloneClient),
                daemonClient is null ? Task.CompletedTask : TryDisposeClientAsync(daemonClient));
            if (endpoint is not null)
                await CleanupEndpointForTestAsync(endpoint);
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public void StandaloneWriterUnavailableFailureNeutralizesCompatibilityReaderDetails()
    {
        DaemonUnavailableFailure failure = McpApplication.CreateStandaloneWriterUnavailableFailure(
            IndexManager.FollowerAccessMode,
            "read-only follower requires a compatible index from the writer");

        Assert.Equal("standalone_writer_unavailable", failure.Cause);
        Assert.Contains("another Phoenix process", failure.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("follower", failure.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("without --standalone", failure.Recovery, StringComparison.Ordinal);
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

            (TimeSpan daemonLatency, TimeSpan standaloneLatency) =
                await MedianCapabilitiesLatenciesAsync(daemonClients[0], standalone);
            TimeSpan relativeCeiling = standaloneLatency * 2 + TimeSpan.FromMilliseconds(100);
            Assert.True(
                daemonLatency <= relativeCeiling,
                $"Median warm daemon relay latency {daemonLatency} materially exceeded the 2x standalone plus 100 ms ceiling {relativeCeiling}; median standalone latency was {standaloneLatency}.");
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
    public async Task AuthorityCheckedRetirementRefusesWrongDatabaseAndStopsMatchingDaemon()
    {
        string root = Directory.CreateTempSubdirectory(
            "Phoenix daemon authority retirement ").FullName;
        DaemonEndpoint endpoint = DaemonEndpoint.Create(root, null);
        DaemonEndpoint wrongDatabase = DaemonEndpoint.Create(
            root, Path.Combine(root, "other-index.db"));
        using Process daemon = LaunchDaemonForTest(
            FindMcpExecutable(), root, keepAlive: true, idleMilliseconds: 350);
        try
        {
            await WaitUntilAsync(
                () => DaemonDescriptor.TryRead(endpoint)?.Pid == daemon.Id,
                TimeSpan.FromSeconds(15));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            DaemonRetirementRefusedException refusal = await Assert.ThrowsAsync<
                DaemonRetirementRefusedException>(() =>
                DaemonRetirement.RetireForHarnessAsync(wrongDatabase, timeout.Token));
            Assert.Equal("daemon_index_destination_mismatch", refusal.Response.Cause);
            Assert.False(daemon.HasExited);

            await DaemonRetirement.RetireForHarnessAsync(endpoint, timeout.Token);
            await daemon.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, daemon.ExitCode);

            // Cleanup is idempotent when the authority-bound endpoint is already gone.
            await DaemonRetirement.RetireForHarnessAsync(endpoint, timeout.Token);
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
    public async Task RetirementWaitsForEndpointWhenDescriptorEvidenceDisappears()
    {
        string root = Directory.CreateTempSubdirectory(
            "Phoenix daemon retirement descriptor gap ").FullName;
        DaemonEndpoint endpoint = DaemonEndpoint.Create(root, null);
        const int retiringPid = 4242;
        var observed = new DaemonDescriptorRecord(
            retiringPid,
            BuildInfo.Version,
            BuildInfo.IndexSchema,
            endpoint.WorkspaceIdentity,
            endpoint.DatabaseKey,
            endpoint.EndpointKey,
            OperatingSystem.IsWindows() ? "named-pipe" : "unix-domain-socket",
            OperatingSystem.IsWindows() ? endpoint.PipeName : endpoint.SocketPath!,
            DateTimeOffset.UtcNow.ToString("O"));
        var firstProbe = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var finishProbe = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int probeCount = 0;

        ValueTask<int> ProbeAsync(DaemonEndpoint _, CancellationToken __)
        {
            if (Interlocked.Increment(ref probeCount) == 1)
            {
                firstProbe.TrySetResult();
                return ValueTask.FromResult(retiringPid);
            }
            return new ValueTask<int>(finishProbe.Task);
        }

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            Task wait = DaemonRetirement.WaitForRelinquishmentAsync(
                endpoint,
                retiringPid,
                observed,
                timeout.Token,
                readDescriptor: _ => null,
                probeDaemonPid: ProbeAsync,
                probeWriterLease: _ => throw new InvalidOperationException(
                    "a successor endpoint must not be mistaken for a free lease"),
                pollDelay: TimeSpan.Zero);

            await firstProbe.Task.WaitAsync(timeout.Token);
            await WaitUntilAsync(
                () => Volatile.Read(ref probeCount) == 2,
                TimeSpan.FromSeconds(5));
            Assert.False(wait.IsCompleted);

            finishProbe.SetResult(retiringPid + 1);
            await wait.WaitAsync(timeout.Token);
            Assert.Equal(2, Volatile.Read(ref probeCount));
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public async Task RetirementWaitsUntilTheWriterLeaseIsProvenFree()
    {
        string root = Directory.CreateTempSubdirectory(
            "Phoenix daemon retirement lease probe ").FullName;
        DaemonEndpoint endpoint = DaemonEndpoint.Create(root, null);
        var firstLeaseProbe = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int leaseReleased = 0;
        int probeCount = 0;

        ValueTask<int> MissingEndpoint(DaemonEndpoint _, CancellationToken __) =>
            ValueTask.FromException<int>(new DaemonEndpointUnavailableException(
                "test endpoint is gone"));

        IndexLeaseAcquireResult ProbeLease(DaemonEndpoint _)
        {
            Interlocked.Increment(ref probeCount);
            firstLeaseProbe.TrySetResult();
            return Volatile.Read(ref leaseReleased) == 0
                ? IndexLeaseAcquireResult.Contended
                : IndexLeaseAcquireResult.Acquired;
        }

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            Task wait = DaemonRetirement.WaitForRelinquishmentAsync(
                endpoint,
                daemonPid: 4242,
                observed: null,
                timeout.Token,
                readDescriptor: _ => null,
                probeDaemonPid: MissingEndpoint,
                probeWriterLease: ProbeLease,
                pollDelay: TimeSpan.FromMilliseconds(10));

            await firstLeaseProbe.Task.WaitAsync(timeout.Token);
            Assert.False(wait.IsCompleted);
            Volatile.Write(ref leaseReleased, 1);
            await wait.WaitAsync(timeout.Token);
            Assert.True(Volatile.Read(ref probeCount) >= 2);
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public async Task RetirementLeaseWaitCancellationMapsToTypedTakeoverFailure()
    {
        string root = Directory.CreateTempSubdirectory(
            "Phoenix daemon retirement lease timeout ").FullName;
        DaemonEndpoint endpoint = DaemonEndpoint.Create(root, null);

        ValueTask<int> MissingEndpoint(DaemonEndpoint _, CancellationToken __) =>
            ValueTask.FromException<int>(new DaemonEndpointUnavailableException(
                "test endpoint is gone"));

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
            OperationCanceledException exception = await Assert.ThrowsAnyAsync<
                OperationCanceledException>(() =>
                DaemonRetirement.WaitForRelinquishmentAsync(
                    endpoint,
                    daemonPid: 4242,
                    observed: null,
                    timeout.Token,
                    readDescriptor: _ => null,
                    probeDaemonPid: MissingEndpoint,
                    probeWriterLease: _ => IndexLeaseAcquireResult.Contended,
                    pollDelay: TimeSpan.FromMilliseconds(10)));

            DaemonUnavailableFailure failure = DaemonProxy.MapRetirementFailure(exception);
            Assert.Equal("daemon_takeover_timeout", failure.Cause);
            Assert.True(failure.Retryable);
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public async Task UnverifiableWriterLeaseMapsToItsOwnRetryableCause()
    {
        string root = Directory.CreateTempSubdirectory(
            "Phoenix daemon retirement lease unverifiable ").FullName;
        DaemonEndpoint endpoint = DaemonEndpoint.Create(root, null);

        ValueTask<int> MissingEndpoint(DaemonEndpoint _, CancellationToken __) =>
            ValueTask.FromException<int>(new DaemonEndpointUnavailableException(
                "test endpoint is gone"));

        try
        {
            DaemonWriterLeaseUnverifiableException exception = await Assert.ThrowsAsync<
                DaemonWriterLeaseUnverifiableException>(() =>
                DaemonRetirement.WaitForRelinquishmentAsync(
                    endpoint,
                    daemonPid: 4242,
                    observed: null,
                    CancellationToken.None,
                    readDescriptor: _ => null,
                    probeDaemonPid: MissingEndpoint,
                    probeWriterLease: _ => IndexLeaseAcquireResult.Failed,
                    pollDelay: TimeSpan.Zero));

            DaemonUnavailableFailure failure = DaemonProxy.MapRetirementFailure(exception);
            Assert.Equal("daemon_writer_lease_unverifiable", failure.Cause);
            Assert.True(failure.Retryable);
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public async Task RetirementWaitsForDeferredDisposeBeforeStartingSuccessor()
    {
        string root = Directory.CreateTempSubdirectory(
            "Phoenix daemon deferred retirement lease ").FullName;
        File.WriteAllText(Path.Combine(root, "Held.cs"),
            "namespace RetirementLease; public sealed class Held { }");
        IndexBuilder.Build(root);
        DaemonEndpoint endpoint = DaemonEndpoint.Create(root, null);
        using var startupEntered = new ManualResetEventSlim();
        using var releaseStartup = new ManualResetEventSlim();
        using var daemonLifetime = new CancellationTokenSource();
        McpClient? successor = null;
        var daemon = new DaemonServer(
            endpoint,
            indexDb: null,
            rebuild: false,
            keepAlive: true,
            configureIndexForTest: manager =>
            {
                manager.DisposeWaitTimeoutForTest = TimeSpan.FromMilliseconds(50);
                manager.StartupAfterLeaseAcquiredForTest = () =>
                {
                    startupEntered.Set();
                    releaseStartup.Wait();
                };
            });
        Task<int> daemonTask = daemon.RunAsync(daemonLifetime.Token);
        try
        {
            await WaitUntilAsync(
                () => DaemonDescriptor.TryRead(endpoint)?.Pid == Environment.ProcessId,
                TimeSpan.FromSeconds(15));
            Assert.True(startupEntered.Wait(TimeSpan.FromSeconds(10)));
            Assert.True(IndexOwnershipLease.IsHeld(
                root, IndexBuilder.DefaultDbPath(root)));

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            Task retirement = DaemonRetirement.RetireForHarnessAsync(
                endpoint, timeout.Token);
            await WaitUntilAsync(
                () => !File.Exists(endpoint.DescriptorPath),
                TimeSpan.FromSeconds(10));
            Assert.True(IndexOwnershipLease.IsHeld(
                root, IndexBuilder.DefaultDbPath(root)));
            Assert.False(retirement.IsCompleted);

            releaseStartup.Set();
            await retirement.WaitAsync(timeout.Token);
            successor = await CreateClientAsync(FindMcpExecutable(), root);
            JsonElement capabilities = await CallAsync(successor, "server_capabilities");
            Assert.Equal("daemon", capabilities.GetProperty("runtime")
                .GetProperty("indexMode").GetString());
            Assert.Equal(0, await daemonTask.WaitAsync(timeout.Token));
        }
        finally
        {
            releaseStartup.Set();
            if (successor is not null)
            {
                try { await RetireDaemonForTestAsync(endpoint); } catch { }
                await TryDisposeClientAsync(successor);
            }
            daemonLifetime.Cancel();
            try { await daemonTask; } catch (OperationCanceledException) { }
            PhoenixRuntimeMode.Set(PhoenixProcessMode.Standalone);
            await CleanupEndpointForTestAsync(endpoint);
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public void CommandLineDefaultsToSharedProxyAndKeepsStandaloneExplicit()
    {
        string? priorShared = Environment.GetEnvironmentVariable("CODENAV_SHARED_DAEMON");
        string? priorFallback = Environment.GetEnvironmentVariable(
            "CODENAV_DAEMON_STANDALONE_FALLBACK");
        try
        {
            Environment.SetEnvironmentVariable("CODENAV_SHARED_DAEMON", "0");
            Environment.SetEnvironmentVariable("CODENAV_DAEMON_STANDALONE_FALLBACK", "1");
            Assert.Equal(McpLaunchMode.SharedProxy, McpCommandLine.Parse([]).Mode);
            Assert.Equal(McpLaunchMode.SharedProxy,
                McpCommandLine.Parse(["--shared-daemon"]).Mode);
            Assert.Equal(McpLaunchMode.SharedProxy,
                McpCommandLine.Parse(["--daemon-fallback-standalone"]).Mode);
            Assert.Equal(McpLaunchMode.Standalone,
                McpCommandLine.Parse(["--standalone"]).Mode);
            Assert.Equal(McpLaunchMode.DaemonRetireAuthorized,
                McpCommandLine.Parse(["--daemon-retire-authorized"]).Mode);
            Assert.Throws<ArgumentException>(() =>
                McpCommandLine.Parse(["--shared-daemon", "--standalone"]));
            Assert.Throws<ArgumentException>(() =>
                McpCommandLine.Parse(["--daemon-retire-authorized", "--standalone"]));
            Assert.Throws<ArgumentException>(() =>
                McpCommandLine.Parse(["--workspace-root"]));

            Environment.SetEnvironmentVariable("CODENAV_SHARED_DAEMON", "1");
            Environment.SetEnvironmentVariable("CODENAV_DAEMON_STANDALONE_FALLBACK", "0");
            Assert.Equal(McpLaunchMode.SharedProxy, McpCommandLine.Parse([]).Mode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODENAV_SHARED_DAEMON", priorShared);
            Environment.SetEnvironmentVariable(
                "CODENAV_DAEMON_STANDALONE_FALLBACK", priorFallback);
        }
    }

    private static async Task<McpClient> CreateClientAsync(
        string executable,
        string root,
        params string[] additionalArguments)
        => await CreateClientWithEnvironmentAsync(
            executable, root, environmentVariables: null, additionalArguments);

    private static async Task<McpClient> CreateClientWithEnvironmentAsync(
        string executable,
        string root,
        IDictionary<string, string?>? environmentVariables,
        params string[] additionalArguments)
    {
        var arguments = new List<string>
        {
            "--workspace-root", root,
            "--daemon-idle-ms", "600",
        };
        arguments.AddRange(additionalArguments);
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "Phoenix shared daemon test",
            Command = executable,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
            Arguments = arguments,
            EnvironmentVariables = environmentVariables,
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

    private static async Task<McpClient> CreateFrameworkDependentClientAsync(
        string root,
        string indexDb,
        int idleMilliseconds = 600)
    {
        string executable = FindMcpExecutable();
        string managedEntry = Path.Combine(
            Path.GetDirectoryName(executable)!, "PhoenixCodeNav.Mcp.dll");
        Assert.True(File.Exists(managedEntry), $"MCP managed entry missing: {managedEntry}");
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "Phoenix framework-dependent daemon test",
            Command = "dotnet",
            WorkingDirectory = Path.GetDirectoryName(managedEntry)!,
            Arguments = [
                managedEntry,
                "--workspace-root", root,
                "--index-db", indexDb,
                "--daemon-idle-ms", idleMilliseconds.ToString(),
            ],
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

    private static async Task WaitForIndexStateAsync(McpClient client, string expected)
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        JsonElement capabilities = default;
        while (DateTime.UtcNow < deadline)
        {
            capabilities = await CallAsync(client, "server_capabilities");
            if (string.Equals(
                    capabilities.GetProperty("index").GetProperty("state").GetString(),
                    expected,
                    StringComparison.Ordinal))
                return;
            await Task.Delay(100);
        }
        Assert.Equal(expected,
            capabilities.GetProperty("index").GetProperty("state").GetString());
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

    private static async Task<(TimeSpan Daemon, TimeSpan Standalone)>
        MedianCapabilitiesLatenciesAsync(McpClient daemon, McpClient standalone)
    {
        _ = await CallAsync(daemon, "server_capabilities");
        _ = await CallAsync(standalone, "server_capabilities");
        var daemonSamples = new TimeSpan[5];
        var standaloneSamples = new TimeSpan[5];
        for (int sample = 0; sample < daemonSamples.Length; sample++)
        {
            // Alternate order so a short scheduling burst cannot systematically favor either
            // topology. The median rejects one slow batch without accepting a lucky best sample.
            if ((sample & 1) == 0)
            {
                daemonSamples[sample] = await MeasureCapabilitiesBatchAsync(daemon);
                standaloneSamples[sample] = await MeasureCapabilitiesBatchAsync(standalone);
            }
            else
            {
                standaloneSamples[sample] = await MeasureCapabilitiesBatchAsync(standalone);
                daemonSamples[sample] = await MeasureCapabilitiesBatchAsync(daemon);
            }
        }
        Array.Sort(daemonSamples);
        Array.Sort(standaloneSamples);
        return (daemonSamples[daemonSamples.Length / 2],
            standaloneSamples[standaloneSamples.Length / 2]);
    }

    private static async Task<TimeSpan> MeasureCapabilitiesBatchAsync(McpClient client)
    {
        var elapsed = Stopwatch.StartNew();
        for (int i = 0; i < 10; i++)
            _ = await CallAsync(client, "server_capabilities");
        elapsed.Stop();
        return elapsed.Elapsed;
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

    private static async Task<CliResult> RunCliAsync(
        string executable,
        string root,
        IReadOnlyList<string> arguments,
        string? standardInput = null)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
            RedirectStandardInput = standardInput is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(
                encoderShouldEmitUTF8Identifier: false,
                throwOnInvalidBytes: true),
            StandardErrorEncoding = new UTF8Encoding(
                encoderShouldEmitUTF8Identifier: false,
                throwOnInvalidBytes: true),
        };
        foreach (string argument in arguments)
            start.ArgumentList.Add(argument);
        bool discovery = arguments.Count > 0 &&
                         arguments[0] is "tools" or "help" or "schema";
        if (discovery)
        {
            start.Environment["CODENAV_WORKSPACE_ROOT"] = root;
        }
        else
        {
            start.ArgumentList.Add("--workspace-root");
            start.ArgumentList.Add(root);
        }

        using Process process = Process.Start(start) ??
            throw new IOException("Phoenix CLI test process did not start.");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        if (standardInput is not null)
        {
            byte[] inputBytes = Encoding.UTF8.GetBytes(standardInput);
            await process.StandardInput.BaseStream.WriteAsync(inputBytes);
            await process.StandardInput.BaseStream.FlushAsync();
            process.StandardInput.BaseStream.Close();
        }

        try
        {
            await process.WaitForExitAsync()
                .WaitAsync(TimeSpan.FromSeconds(30));
        }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw;
        }

        string output = await stdout;
        string diagnostics = await stderr;
        string[] diagnosticLines = diagnostics.Split(
            ['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.All(diagnosticLines, line => Assert.Equal(
            DaemonRuntimeDiagnostics.DiscoveryFallbackWarning, line));
        string compact = output.TrimEnd('\r', '\n');
        bool pretty = arguments.Contains("--pretty", StringComparer.Ordinal);
        if (!pretty)
        {
            Assert.DoesNotContain('\r', compact);
            Assert.DoesNotContain('\n', compact);
        }
        using JsonDocument document = JsonDocument.Parse(output);
        return new CliResult(
            process.ExitCode,
            document.RootElement.Clone(),
            output);
    }

    private sealed record CliResult(
        int ExitCode,
        JsonElement Payload,
        string RawOutput);

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
            RedirectStandardOutput = true,
        };
        start.ArgumentList.Add("--daemon");
        start.ArgumentList.Add("--workspace-root");
        start.ArgumentList.Add(root);
        start.ArgumentList.Add("--daemon-idle-ms");
        start.ArgumentList.Add(idleMilliseconds.ToString());
        if (keepAlive) start.ArgumentList.Add("--keepalive");
        Process process = Process.Start(start) ??
            throw new IOException("Test daemon did not start.");
        using var timeout = new CancellationTokenSource(DaemonProxy.StartupTimeout);
        DaemonStartupReport report = DaemonStartupChannel.ReadAsync(
                process.StandardOutput.BaseStream, timeout.Token)
            .AsTask().GetAwaiter().GetResult();
        if (!report.Ready)
        {
            process.Dispose();
            throw new IOException(
                $"Test daemon startup was refused: {report.Failure?.Cause}.");
        }
        return process;
    }

    private static Process LaunchSilentStandaloneForStartupCleanupTest(
        string executable,
        string root)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
        };
        start.ArgumentList.Add("--standalone");
        start.ArgumentList.Add("--workspace-root");
        start.ArgumentList.Add(root);
        return Process.Start(start) ??
            throw new IOException("Silent startup-cleanup test process did not start.");
    }

    private static async Task CleanupEndpointForTestAsync(DaemonEndpoint endpoint)
    {
        await WaitUntilAsync(
            () => !File.Exists(endpoint.DescriptorPath),
            TimeSpan.FromSeconds(30));
        if (!OperatingSystem.IsWindows() && endpoint.SocketPath is not null)
            DaemonUnixFileAuthority.TryRemoveOwnedSocket(endpoint.SocketPath);
        try { File.Delete(endpoint.StartupLockPath); } catch { }
        DaemonStartupStatus.Delete(endpoint);
    }

    private static async Task RetireDaemonForTestAsync(DaemonEndpoint endpoint)
    {
        await using Stream stream = await DaemonTransport.ConnectAsync(
            endpoint, TimeSpan.FromSeconds(2), CancellationToken.None);
        DaemonHandshakeRequest request =
            DaemonProtocol.CreateRequest(endpoint, "test-retire") with
            {
                ToolVersion = "99.0.0",
            };
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

    private static async Task ServeOlderLegacyDaemonAsync(
        DaemonEndpoint endpoint,
        TaskCompletionSource retired,
        CancellationToken cancellationToken)
    {
        IDaemonTransportListener? listener = null;
        bool retirementAccepted = false;
        try
        {
            listener = DaemonTransport.Listen(endpoint);
            DaemonDescriptor.Publish(endpoint);
            while (!retirementAccepted)
            {
                await using Stream stream = await listener.AcceptAsync(cancellationToken);
                (byte version, DaemonPreambleMode mode, DaemonHandshakeRequest? request) =
                    await DaemonProtocol.ReadRequestAsync(stream, cancellationToken);
                DaemonHandshakeResponse current = DaemonProtocol.Evaluate(
                    endpoint, version, mode, request);
                DaemonHandshakeResponse response = mode == DaemonPreambleMode.RetireAndReplace
                    ? current with
                    {
                        Accepted = true,
                        Cause = "daemon_retiring",
                        Detail = "Older Phoenix daemon accepted graceful retirement.",
                        ToolVersion = "0.12.60",
                        Retiring = true,
                    }
                    : current with
                    {
                        Accepted = false,
                        Cause = "daemon_older_than_client",
                        Detail = "Phoenix daemon is older; graceful replacement is required.",
                        ToolVersion = "0.12.60",
                    };
                await DaemonProtocol.WriteResponseAsync(
                    stream, response, cancellationToken);
                retirementAccepted = response.Retiring;
            }
        }
        finally
        {
            if (listener is not null)
                await listener.DisposeAsync();
            DaemonDescriptor.DeleteOwn(endpoint);
            if (retirementAccepted) retired.TrySetResult();
        }
    }

    private static async Task ServeHandshakeThenCloseAsync(
        DaemonEndpoint endpoint,
        TaskCompletionSource listening,
        CancellationToken cancellationToken)
    {
        await using IDaemonTransportListener listener = DaemonTransport.Listen(endpoint);
        DaemonDescriptor.Publish(endpoint);
        listening.TrySetResult();
        try
        {
            await using Stream stream = await listener.AcceptAsync(cancellationToken);
            (byte version, DaemonPreambleMode mode, DaemonHandshakeRequest? request) =
                await DaemonProtocol.ReadRequestAsync(stream, cancellationToken);
            DaemonHandshakeResponse response = DaemonProtocol.Evaluate(
                endpoint, version, mode, request);
            Assert.True(response.Accepted);
            await DaemonProtocol.WriteResponseAsync(stream, response, cancellationToken);
        }
        finally
        {
            DaemonDescriptor.DeleteOwn(endpoint);
        }
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

    [DllImport("libc", SetLastError = true)]
    private static extern int mkfifo(string path, uint mode);

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
