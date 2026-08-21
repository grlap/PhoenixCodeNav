using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using CodeNav.Portal;

namespace CodeNav.Tests;

[Collection("Portal launcher process isolation")]
public sealed class PortalLauncherRuntimeTests
{
    [Fact]
    public void PhysicalWorkspaceAliasesShareOneCoordinationKey()
    {
        string root = Directory.CreateTempSubdirectory(
            "Phoenix portal identity aliases ").FullName;
        string workspace = Directory.CreateDirectory(
            Path.Combine(root, "PortalIdentityCaseProbe")).FullName;
        try
        {
            string expected = PortalLaunchCoordinator.WorkspaceCoordinationKey(workspace);
            string caseVariant = Path.Combine(root, "portalidentitycaseprobe");
            if (Directory.Exists(caseVariant))
            {
                Assert.Equal(
                    expected,
                    PortalLaunchCoordinator.WorkspaceCoordinationKey(caseVariant));
            }

            if (!OperatingSystem.IsWindows())
            {
                string alias = Path.Combine(root, "workspace-alias");
                Directory.CreateSymbolicLink(alias, workspace);
                Assert.Equal(
                    expected,
                    PortalLaunchCoordinator.WorkspaceCoordinationKey(alias));
            }
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public async Task RuntimeDirectoryIsOwnerPrivateBeforeCoordinationFilesAreUsed()
    {
        if (OperatingSystem.IsWindows())
            return;

        string root = CreateRuntimeSecurityTestRoot();
        string workspace = Directory.CreateDirectory(Path.Combine(root, "workspace")).FullName;
        try
        {
            await using PortalLaunchCoordinator coordinator =
                await PortalLaunchCoordinator.AcquireAsync(
                    workspace,
                    CancellationToken.None,
                    root);

            Assert.True(coordinator.IsOwner);
            UnixFileMode expected = UnixFileMode.UserRead
                | UnixFileMode.UserWrite
                | UnixFileMode.UserExecute;
            string applicationDirectory = Path.Combine(root, ".phoenixcodenav");
            string runtimeDirectory = Path.Combine(applicationDirectory, "runtime");
            string portalDirectory = Path.Combine(runtimeDirectory, "portal");
            Assert.Equal(expected, File.GetUnixFileMode(applicationDirectory));
            Assert.Equal(expected, File.GetUnixFileMode(runtimeDirectory));
            Assert.Equal(expected, File.GetUnixFileMode(portalDirectory));
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public async Task WritableNonStickyRuntimeAncestorFailsClosed()
    {
        if (OperatingSystem.IsWindows())
            return;

        string root = CreateRuntimeSecurityTestRoot();
        string unsafeBase = Directory.CreateDirectory(Path.Combine(root, "unsafe-base")).FullName;
        string workspace = Directory.CreateDirectory(Path.Combine(root, "workspace")).FullName;
        UnixFileMode privateMode = UnixFileMode.UserRead
            | UnixFileMode.UserWrite
            | UnixFileMode.UserExecute;
        try
        {
            File.SetUnixFileMode(
                unsafeBase,
                privateMode
                | UnixFileMode.GroupWrite
                | UnixFileMode.OtherWrite);

            UnauthorizedAccessException error = await Assert.ThrowsAsync<UnauthorizedAccessException>(
                () => PortalLaunchCoordinator.AcquireAsync(
                    workspace,
                    CancellationToken.None,
                    unsafeBase));

            Assert.Contains("writable by other users", error.Message, StringComparison.Ordinal);
            Assert.False(Directory.Exists(Path.Combine(unsafeBase, ".phoenixcodenav")));
        }
        finally
        {
            File.SetUnixFileMode(unsafeBase, privateMode);
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public async Task ReparsePointRuntimeAncestorFailsClosed()
    {
        if (OperatingSystem.IsWindows())
            return;

        string root = CreateRuntimeSecurityTestRoot();
        string actualBase = Directory.CreateDirectory(Path.Combine(root, "actual-base")).FullName;
        string linkedBase = Path.Combine(root, "linked-base");
        string workspace = Directory.CreateDirectory(Path.Combine(root, "workspace")).FullName;
        try
        {
            Directory.CreateSymbolicLink(linkedBase, actualBase);

            IOException error = await Assert.ThrowsAsync<IOException>(
                () => PortalLaunchCoordinator.AcquireAsync(
                    workspace,
                    CancellationToken.None,
                    linkedBase));

            Assert.Contains("reparse point", error.Message, StringComparison.Ordinal);
            Assert.False(Directory.Exists(Path.Combine(actualBase, ".phoenixcodenav")));
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public async Task SuccessfulHealthFromDifferentSessionCannotReuseStaleDescriptor()
    {
        string root = CreateRuntimeSecurityTestRoot();
        string workspace = Directory.CreateDirectory(Path.Combine(root, "workspace")).FullName;
        PortalLaunchCoordinator? owner = null;
        var listener = new TcpListener(IPAddress.Loopback, 0);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            owner = await PortalLaunchCoordinator.AcquireAsync(
                workspace,
                timeout.Token,
                root);
            Assert.True(owner.IsOwner);

            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            const string descriptorSession =
                "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            const string unrelatedSession =
                "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
            var staleDescriptor = new PortalLaunchHandshake(
                PortalLaunchCoordinator.ProtocolVersion,
                "started",
                $"http://127.0.0.1:{port}/#token=stale-private-token",
                Environment.ProcessId,
                WorkspaceCount: 1,
                LaunchSessionId: descriptorSession,
                ReadOnly: true);
            string runtimeDirectory = Path.Combine(
                root,
                ".phoenixcodenav",
                "runtime",
                "portal");
            string key = PortalLaunchCoordinator.WorkspaceCoordinationKey(workspace);
            string descriptorPath = Path.Combine(runtimeDirectory, $"{key}.json");
            await File.WriteAllTextAsync(
                descriptorPath,
                PortalLaunchCoordinator.Serialize(staleDescriptor),
                timeout.Token);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    descriptorPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            string unrelatedHealth = JsonSerializer.Serialize(new
            {
                status = "ok",
                portalVersion = "unrelated-loopback-service",
                apiVersion = 1,
                protocolVersion = PortalLaunchCoordinator.ProtocolVersion,
                pid = Environment.ProcessId,
                launchSessionId = unrelatedSession,
                readOnly = true,
            });
            Task response = RespondOnceAsync(listener, unrelatedHealth, timeout.Token);
            Task<PortalLaunchCoordinator> contenderTask =
                PortalLaunchCoordinator.AcquireAsync(workspace, timeout.Token, root);

            await response.WaitAsync(timeout.Token);
            await owner.DisposeAsync();
            owner = null;

            await using PortalLaunchCoordinator contender =
                await contenderTask.WaitAsync(timeout.Token);
            Assert.True(contender.IsOwner);
            Assert.Null(contender.ReusedHandshake);
            Assert.False(File.Exists(descriptorPath));
        }
        finally
        {
            listener.Stop();
            if (owner is not null)
                await owner.DisposeAsync();
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Fact]
    public async Task LauncherStartsReusesAndRecoversAfterOwnerExit()
    {
        string root = Directory.CreateTempSubdirectory("Phoenix portal launcher workspace ").FullName;
        Process? owner = null;
        Process? restarted = null;
        try
        {
            string executable = Path.Combine(
                AppContext.BaseDirectory,
                OperatingSystem.IsWindows()
                    ? "PhoenixCodeNav.Portal.exe"
                    : "PhoenixCodeNav.Portal");
            Assert.True(File.Exists(executable), $"Portal apphost missing: {executable}");

            owner = Start(executable, root);
            JsonElement started = await ReadHandshakeAsync(owner, TimeSpan.FromSeconds(30));
            Assert.Equal(1, started.GetProperty("protocolVersion").GetInt32());
            Assert.Equal("started", started.GetProperty("status").GetString());
            Assert.Equal(owner.Id, started.GetProperty("pid").GetInt32());
            Assert.Equal(1, started.GetProperty("workspaceCount").GetInt32());
            Assert.True(started.GetProperty("readOnly").GetBoolean());
            string launchSessionId = started.GetProperty("launchSessionId").GetString()!;
            Assert.True(PortalLaunchCoordinator.IsLaunchSessionId(launchSessionId));
            string url = started.GetProperty("url").GetString()!;
            AssertLoopbackSessionUrl(url);

            using (Process helper = Start(executable, root))
            {
                JsonElement reused = await ReadHandshakeAsync(helper, TimeSpan.FromSeconds(10));
                await helper.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(0, helper.ExitCode);
                Assert.Equal("reused", reused.GetProperty("status").GetString());
                Assert.Equal(url, reused.GetProperty("url").GetString());
                Assert.Equal(owner.Id, reused.GetProperty("pid").GetInt32());
                Assert.Equal(
                    launchSessionId,
                    reused.GetProperty("launchSessionId").GetString());
                Assert.Equal("", await helper.StandardError.ReadToEndAsync());
            }

            using (var http = new HttpClient(new HttpClientHandler { UseProxy = false }))
            {
                Uri session = new(url);
                using HttpResponseMessage health = await http.GetAsync(
                    session.GetLeftPart(UriPartial.Authority) + "/healthz");
                Assert.True(health.IsSuccessStatusCode);
                PortalHealthStatus healthStatus = Assert.IsType<PortalHealthStatus>(
                    await health.Content.ReadFromJsonAsync<PortalHealthStatus>());
                Assert.Equal(owner.Id, healthStatus.Pid);
                Assert.Equal(launchSessionId, healthStatus.LaunchSessionId);
                Assert.Equal(PortalLaunchCoordinator.ProtocolVersion, healthStatus.ProtocolVersion);
                Assert.True(healthStatus.ReadOnly);
                using HttpResponseMessage shell = await http.GetAsync(
                    session.GetLeftPart(UriPartial.Authority) + "/");
                Assert.True(shell.IsSuccessStatusCode);
            }

            int priorPid = owner.Id;
            string priorUrl = url;
            await StopAsync(owner);
            owner.Dispose();
            owner = null;

            restarted = Start(executable, root);
            JsonElement fresh = await ReadHandshakeAsync(restarted, TimeSpan.FromSeconds(30));
            Assert.Equal("started", fresh.GetProperty("status").GetString());
            Assert.Equal(restarted.Id, fresh.GetProperty("pid").GetInt32());
            Assert.NotEqual(priorPid, restarted.Id);
            Assert.NotEqual(priorUrl, fresh.GetProperty("url").GetString());
        }
        finally
        {
            if (owner is not null)
            {
                await StopAsync(owner);
                owner.Dispose();
            }
            if (restarted is not null)
            {
                await StopAsync(restarted);
                restarted.Dispose();
            }
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    private static Process Start(string executable, string workspaceRoot)
    {
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("--launcher");
        start.ArgumentList.Add("--workspace-root");
        start.ArgumentList.Add(workspaceRoot);
        return Process.Start(start)
            ?? throw new InvalidOperationException("Could not start the portal apphost.");
    }

    private static async Task RespondOnceAsync(
        TcpListener listener,
        string json,
        CancellationToken cancellationToken)
    {
        using TcpClient client = await listener.AcceptTcpClientAsync(cancellationToken);
        await using NetworkStream stream = client.GetStream();
        var request = new byte[1024];
        _ = await stream.ReadAsync(request, cancellationToken);

        byte[] body = Encoding.UTF8.GetBytes(json);
        byte[] headers = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(headers, cancellationToken);
        await stream.WriteAsync(body, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static string CreateRuntimeSecurityTestRoot()
    {
        string userProfile = Environment.GetFolderPath(
            Environment.SpecialFolder.UserProfile,
            Environment.SpecialFolderOption.DoNotVerify);
        string root = Path.Combine(
            userProfile,
            $".phoenixcodenav-runtime-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                root,
                UnixFileMode.UserRead
                | UnixFileMode.UserWrite
                | UnixFileMode.UserExecute);
        }
        return root;
    }

    private static async Task<JsonElement> ReadHandshakeAsync(
        Process process,
        TimeSpan timeout)
    {
        string? line;
        try
        {
            line = await process.StandardOutput.ReadLineAsync()
                .WaitAsync(timeout);
        }
        catch (TimeoutException)
        {
            await StopAsync(process);
            string error = await process.StandardError.ReadToEndAsync();
            Assert.Fail($"Portal handshake timed out. {error}");
            throw;
        }

        if (line is null)
        {
            string error = await process.StandardError.ReadToEndAsync();
            Assert.Fail($"Portal exited without a handshake. {error}");
        }
        return JsonDocument.Parse(line!).RootElement.Clone();
    }

    private static void AssertLoopbackSessionUrl(string url)
    {
        Assert.True(Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed));
        Assert.Equal(Uri.UriSchemeHttp, parsed!.Scheme);
        Assert.True(parsed.IsLoopback);
        Assert.StartsWith("#token=", parsed.Fragment, StringComparison.Ordinal);
        Assert.True(parsed.Fragment.Length > "#token=".Length);
    }

    private static async Task StopAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (InvalidOperationException)
        {
        }
        catch (TimeoutException)
        {
        }
    }
}

[CollectionDefinition("Portal launcher process isolation", DisableParallelization = true)]
public sealed class PortalLauncherProcessCollection;
