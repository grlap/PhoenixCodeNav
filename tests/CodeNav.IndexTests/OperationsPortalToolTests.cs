using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using CodeNav.Core.Indexing;
using CodeNav.Core.Semantic;
using CodeNav.Mcp;
using CodeNav.Portal;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace CodeNav.Tests;

[Collection("Operations Portal MCP process isolation")]
public sealed class OperationsPortalToolTests
{
    [Fact]
    public void ToolContractRequiresExplicitUserRequestAndVerbatimLinkDisplay()
    {
        MethodInfo method = typeof(NavigationTools).GetMethod(
            nameof(NavigationTools.OpenOperationsPortal))!;
        string description = Assert.IsType<DescriptionAttribute>(
            method.GetCustomAttribute<DescriptionAttribute>()).Description;

        Assert.Contains("only when the user explicitly asks", description);
        Assert.Contains("does not open a browser", description);
        Assert.Contains("present the returned url field verbatim", description);
        Assert.Equal(TimeSpan.FromSeconds(30),
            OperationsPortalLauncher.DefaultStartupTimeout);
    }

    [Fact]
    public void CompanionProcessStartInfoIsFullyIsolatedFromMcpStdio()
    {
        string executable = Path.Combine(Path.GetTempPath(), "portal companion");
        string workspace = Path.Combine(Path.GetTempPath(), "portal workspace");

        ProcessStartInfo start = OperationsPortalLauncher.CreateStartInfo(
            executable,
            workspace);

        Assert.False(start.UseShellExecute);
        Assert.True(start.CreateNoWindow);
        Assert.True(start.RedirectStandardInput);
        Assert.True(start.RedirectStandardOutput);
        Assert.True(start.RedirectStandardError);
        Assert.Equal(Path.GetDirectoryName(executable), start.WorkingDirectory);
        Assert.Equal(
            ["--launcher", "--workspace-root", workspace],
            start.ArgumentList.ToArray());
    }

    public static TheoryData<string> InvalidHandshakes => new()
    {
        "{",
        ValidHandshake(protocolVersion: 2),
        ValidHandshake(pid: 99),
        ValidHandshake(url: "http://example.com/#token=private"),
        ValidHandshake(url: "https://127.0.0.1:43127/#token=private"),
        ValidHandshake(url: "http://127.0.0.1:43127/"),
        ValidHandshake(launchSessionId: "not-a-valid-session-id"),
        ValidHandshake(readOnly: false),
    };

    [Theory]
    [MemberData(nameof(InvalidHandshakes))]
    public void InvalidHandshakeFieldsFailWithStableBoundedProtocolError(string handshake)
    {
        OperationsPortalLaunchResult result = OperationsPortalLauncher.ParseHandshake(
            handshake,
            launchedPid: 42);

        Assert.False(result.Success);
        Assert.Equal("portal_protocol_error", result.Error);
        Assert.True(result.Retryable);
        Assert.NotNull(result.Detail);
        Assert.Equal(
            "The Phoenix Operations Portal returned an invalid startup handshake.",
            result.Detail);
        Assert.True(
            Encoding.UTF8.GetByteCount(result.Detail!) <= OperationsPortalLauncher.MaxErrorBytes);
    }

    [Fact]
    public void InvalidHandshakeDoesNotEchoPrivatePayloadContent()
    {
        const string sentinel = "/Users/private-account/.phoenixcodenav/runtime/portal/session.json";
        OperationsPortalLaunchResult result = OperationsPortalLauncher.ParseHandshake(
            $$"""{"privatePath":"{{sentinel}}"}""",
            launchedPid: 42);

        Assert.False(result.Success);
        Assert.Equal("portal_protocol_error", result.Error);
        Assert.Equal(
            "The Phoenix Operations Portal returned an invalid startup handshake.",
            result.Detail);
        Assert.DoesNotContain(sentinel, result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OversizedHandshakeIsRejectedAtTheProtocolByteBoundary()
    {
        byte[] payload = Encoding.UTF8.GetBytes(
            new string('x', OperationsPortalLauncher.MaxHandshakeBytes + 1) + "\n");
        using var stream = new MemoryStream(payload);

        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(
            () => OperationsPortalLauncher.ReadBoundedLineAsync(
                stream,
                OperationsPortalLauncher.MaxHandshakeBytes,
                CancellationToken.None));

        Assert.Equal(
            "The portal startup handshake exceeded its bounded protocol size.",
            error.Message);
    }

    [Fact]
    public async Task HandshakeAtTheProtocolByteBoundaryIsReadCompletely()
    {
        string expected = new('x', OperationsPortalLauncher.MaxHandshakeBytes);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(expected + "\n"));

        string? actual = await OperationsPortalLauncher.ReadBoundedLineAsync(
            stream,
            OperationsPortalLauncher.MaxHandshakeBytes,
            CancellationToken.None);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task NoisyStderrIsFullyDrainedWhileOnlyItsBoundedPrefixIsRetained()
    {
        byte[] payload = Encoding.UTF8.GetBytes(
            new string('e', OperationsPortalLauncher.MaxErrorBytes + 32 * 1024));
        using var stream = new MemoryStream(payload);

        string retained = await OperationsPortalLauncher.DrainBoundedAsync(
            stream,
            OperationsPortalLauncher.MaxErrorBytes,
            CancellationToken.None);

        Assert.Equal(payload.Length, stream.Position);
        Assert.Equal(
            OperationsPortalLauncher.MaxErrorBytes,
            Encoding.UTF8.GetByteCount(retained));
    }

    [Fact]
    public async Task ToolReturnsAuthenticatedLinkAndDisplayInstructionWithoutOpeningBrowser()
    {
        string root = Directory.CreateTempSubdirectory("Phoenix portal tool workspace ").FullName;
        try
        {
            using var manager = new IndexManager(root, Path.Combine(root, "index.db"));
            using var semantic = new SemanticService(manager);
            var launcher = new FakeLauncher(new OperationsPortalLaunchResult(
                true,
                Status: "started",
                Url: "http://127.0.0.1:43127/#token=private-session-token",
                Pid: 8123,
                WorkspaceCount: 1));
            var tools = new NavigationTools(manager, semantic, launcher);

            using JsonDocument document = JsonDocument.Parse(
                await tools.OpenOperationsPortal());
            JsonElement response = document.RootElement;

            Assert.True(response.GetProperty("ready").GetBoolean());
            Assert.Equal("started", response.GetProperty("status").GetString());
            Assert.Equal(
                "http://127.0.0.1:43127/#token=private-session-token",
                response.GetProperty("url").GetString());
            Assert.Equal(8123, response.GetProperty("pid").GetInt32());
            Assert.Equal(1, response.GetProperty("workspaceCount").GetInt32());
            Assert.True(response.GetProperty("readOnly").GetBoolean());
            Assert.False(response.GetProperty("browserOpened").GetBoolean());
            Assert.Contains("Show the url field verbatim", response.GetProperty("instruction").GetString());
            Assert.Equal(Path.GetFullPath(root), launcher.WorkspaceRoot);
            Assert.StartsWith("0.12.53+",
                response.GetProperty("meta").GetProperty("build").GetString());
            Assert.Equal(
                "indexed",
                response.GetProperty("meta").GetProperty("confidence").GetString());
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public async Task ToolPreservesStructuredLauncherFailures()
    {
        string root = Directory.CreateTempSubdirectory("Phoenix portal failure ").FullName;
        try
        {
            using var manager = new IndexManager(root, Path.Combine(root, "index.db"));
            using var semantic = new SemanticService(manager);
            var tools = new NavigationTools(
                manager,
                semantic,
                new FakeLauncher(OperationsPortalLaunchResult.Failed(
                    "portal_start_timeout",
                    "The portal did not become ready within 30 seconds.",
                    retryable: true)));

            using JsonDocument document = JsonDocument.Parse(
                await tools.OpenOperationsPortal());
            JsonElement response = document.RootElement;

            Assert.Equal("portal_start_timeout", response.GetProperty("error").GetString());
            Assert.True(response.GetProperty("retryable").GetBoolean());
            Assert.Contains("30 seconds", response.GetProperty("detail").GetString());
            Assert.False(response.TryGetProperty("url", out _));
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public async Task MissingCompanionFailsWithoutStartingAProcess()
    {
        string root = Directory.CreateTempSubdirectory("Phoenix missing portal ").FullName;
        try
        {
            var launcher = new OperationsPortalLauncher(
                Path.Combine(root, "not-installed", "PhoenixCodeNav.Portal"),
                TimeSpan.FromMilliseconds(100));

            OperationsPortalLaunchResult result = await launcher.LaunchAsync(
                root,
                CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal("portal_companion_missing", result.Error);
            Assert.False(result.Retryable);
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public async Task StartupTimeoutKillsOnlyTheNewHelperAttempt()
    {
        string root = Directory.CreateTempSubdirectory("Phoenix portal timeout owner ").FullName;
        try
        {
            using FileStream existingOwner = AcquirePortalOwnerLock(root);
            var launcher = new OperationsPortalLauncher(
                FindBuiltPortalExecutable(),
                TimeSpan.FromMilliseconds(250));

            OperationsPortalLaunchResult result = await launcher.LaunchAsync(
                root,
                CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal("portal_start_timeout", result.Error);
            Assert.True(result.Retryable);
            Assert.False(existingOwner.SafeFileHandle.IsClosed);
            existingOwner.WriteByte(0x2A);
            existingOwner.Flush();
        }
        finally
        {
            PortalTestRuntimeCleanup.DeleteCoordinationFiles(root);
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public async Task PackagedMcpToolStartsPortalWithoutCorruptingStdioAndThenReusesIt()
    {
        string root = Directory.CreateTempSubdirectory("Phoenix portal MCP runtime ").FullName;
        int? portalPid = null;
        try
        {
            string repository = FindRepositoryRoot();
            string configuration = new DirectoryInfo(AppContext.BaseDirectory)
                .Parent?.Name ?? "Debug";
            string executableName = OperatingSystem.IsWindows()
                ? "PhoenixCodeNav.Mcp.exe"
                : "PhoenixCodeNav.Mcp";
            string executable = Path.Combine(
                repository,
                "src",
                "CodeNav.Mcp",
                "bin",
                configuration,
                "net10.0",
                executableName);
            Assert.True(File.Exists(executable), $"MCP apphost missing: {executable}");
            Assert.True(File.Exists(Path.Combine(
                Path.GetDirectoryName(executable)!,
                "portal",
                OperatingSystem.IsWindows()
                    ? "PhoenixCodeNav.Portal.exe"
                    : "PhoenixCodeNav.Portal")));

            var transport = new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = "Operations Portal stdio isolation",
                Command = executable,
                WorkingDirectory = Path.GetDirectoryName(executable)!,
                Arguments = ["--workspace-root", root],
            });
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(75));
            await using McpClient client = await McpClient.CreateAsync(
                transport,
                cancellationToken: timeout.Token);

            JsonElement started = await CallJsonAsync(
                client,
                "open_operations_portal",
                timeout.Token);
            portalPid = started.GetProperty("pid").GetInt32();
            Assert.True(started.TryGetProperty("ready", out JsonElement ready)
                        && ready.GetBoolean(), started.ToString());
            Assert.Equal("started", started.GetProperty("status").GetString());
            string url = started.GetProperty("url").GetString()!;

            JsonElement reused = await CallJsonAsync(
                client,
                "open_operations_portal",
                timeout.Token);
            Assert.Equal("reused", reused.GetProperty("status").GetString());
            Assert.Equal(url, reused.GetProperty("url").GetString());
            Assert.Equal(portalPid, reused.GetProperty("pid").GetInt32());

            // If either portal child inherited the MCP stdout stream, this next framed response
            // would fail to parse or disconnect the client.
            JsonElement capabilities = await CallJsonAsync(
                client,
                "server_capabilities",
                timeout.Token);
            Assert.Equal("0.12.53", capabilities.GetProperty("version").GetString());
            Assert.Contains(
                capabilities.GetProperty("features").EnumerateArray(),
                feature => feature.GetProperty("id").GetString()
                    == "operations-portal-mcp-launcher");

            using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
            Uri session = new(url);
            using HttpResponseMessage health = await http.GetAsync(
                session.GetLeftPart(UriPartial.Authority) + "/healthz",
                timeout.Token);
            Assert.True(health.IsSuccessStatusCode);
            using HttpResponseMessage shell = await http.GetAsync(
                session.GetLeftPart(UriPartial.Authority) + "/",
                timeout.Token);
            Assert.True(shell.IsSuccessStatusCode);
        }
        finally
        {
            if (portalPid is int pid)
            {
                try
                {
                    using System.Diagnostics.Process process =
                        System.Diagnostics.Process.GetProcessById(pid);
                    if (!process.HasExited)
                        process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                }
                catch (ArgumentException)
                {
                }
                catch (TimeoutException)
                {
                }
            }
            PortalTestRuntimeCleanup.DeleteCoordinationFiles(root);
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    private static async Task<JsonElement> CallJsonAsync(
        McpClient client,
        string tool,
        CancellationToken cancellationToken)
    {
        CallToolResult result = await client.CallToolAsync(
            tool,
            new Dictionary<string, object?>(),
            cancellationToken: cancellationToken);
        TextContentBlock text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content));
        return JsonDocument.Parse(text.Text).RootElement.Clone();
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "PhoenixCodeNav.sln")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName
            ?? throw new InvalidOperationException("Could not locate PhoenixCodeNav.sln.");
    }

    private static string FindBuiltPortalExecutable()
    {
        string configuration = new DirectoryInfo(AppContext.BaseDirectory)
            .Parent?.Name ?? "Debug";
        return Path.Combine(
            FindRepositoryRoot(),
            "src",
            "CodeNav.Mcp",
            "bin",
            configuration,
            "net10.0",
            "portal",
            OperatingSystem.IsWindows()
                ? "PhoenixCodeNav.Portal.exe"
                : "PhoenixCodeNav.Portal");
    }

    private static FileStream AcquirePortalOwnerLock(string workspaceRoot)
    {
        string key = PortalLaunchCoordinator.WorkspaceCoordinationKey(workspaceRoot);
        string runtimeDirectory = Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.UserProfile,
                Environment.SpecialFolderOption.DoNotVerify),
            ".phoenixcodenav",
            "runtime",
            "portal");
        Directory.CreateDirectory(runtimeDirectory);
        if (!OperatingSystem.IsWindows())
        {
            string applicationDirectory = Directory.GetParent(
                Directory.GetParent(runtimeDirectory)!.FullName)!.FullName;
            foreach (string directory in new[]
                     {
                         applicationDirectory,
                         Path.Combine(applicationDirectory, "runtime"),
                         runtimeDirectory,
                     })
            {
                File.SetUnixFileMode(
                    directory,
                    UnixFileMode.UserRead
                    | UnixFileMode.UserWrite
                    | UnixFileMode.UserExecute);
            }
        }
        string lockPath = Path.Combine(runtimeDirectory, $"{key}.lock");
        return new FileStream(
            lockPath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None);
    }

    private static string ValidHandshake(
        int protocolVersion = 1,
        string status = "started",
        string url = "http://127.0.0.1:43127/#token=private",
        int pid = 42,
        int workspaceCount = 1,
        string launchSessionId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
        bool readOnly = true) =>
        JsonSerializer.Serialize(new
        {
            protocolVersion,
            status,
            url,
            pid,
            workspaceCount,
            launchSessionId,
            readOnly,
        });

    private sealed class FakeLauncher(OperationsPortalLaunchResult result)
        : IOperationsPortalLauncher
    {
        internal string? WorkspaceRoot { get; private set; }

        public Task<OperationsPortalLaunchResult> LaunchAsync(
            string workspaceRoot,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WorkspaceRoot = workspaceRoot;
            return Task.FromResult(result);
        }
    }
}

[CollectionDefinition("Operations Portal MCP process isolation", DisableParallelization = true)]
public sealed class OperationsPortalMcpProcessCollection;

internal static class PortalTestRuntimeCleanup
{
    internal static void DeleteCoordinationFiles(string workspaceRoot)
    {
        if (!Directory.Exists(workspaceRoot))
            return;

        string key = PortalLaunchCoordinator.WorkspaceCoordinationKey(workspaceRoot);
        string runtimeDirectory = Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.UserProfile,
                Environment.SpecialFolderOption.DoNotVerify),
            ".phoenixcodenav",
            "runtime",
            "portal");
        TryDelete(Path.Combine(runtimeDirectory, $"{key}.json"));
        TryDelete(Path.Combine(runtimeDirectory, $"{key}.lock"));
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
