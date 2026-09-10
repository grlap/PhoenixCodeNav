using System.Diagnostics;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace CodeNav.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class FSharpSingleFilePublishCollection
{
    public const string Name = "F# single-file publish isolation";
}

[Collection(FSharpSingleFilePublishCollection.Name)]
public sealed class FSharpSingleFilePublishTests
{
    [Fact]
    public async Task DefaultPublishDirectoryContainsPortalCompanion()
    {
        string repository = FindRepositoryRoot();
        string projectDirectory = Path.Combine(repository, "src", "CodeNav.Mcp");
        string project = Path.Combine(projectDirectory, "CodeNav.Mcp.csproj");
        string publish = Path.Combine(
            projectDirectory,
            "bin",
            "Release",
            "net10.0",
            "publish");
        try
        {
            if (Directory.Exists(publish))
                Directory.Delete(publish, recursive: true);

            RedirectedProcessResult result = await RunAsync("dotnet",
            [
                "publish", project, "-c", "Release", "--no-restore",
                "-p:UseSharedCompilation=false",
            ], repository, TimeSpan.FromMinutes(3));
            Assert.True(result.ExitCode == 0,
                $"default publish failed ({result.ExitCode})\n{result.Output}\n{result.Error}");

            Assert.True(File.Exists(Path.Combine(
                publish,
                OperatingSystem.IsWindows()
                    ? "PhoenixCodeNav.Mcp.exe"
                    : "PhoenixCodeNav.Mcp")));
            Assert.True(File.Exists(Path.Combine(
                publish,
                "portal",
                OperatingSystem.IsWindows()
                    ? "PhoenixCodeNav.Portal.exe"
                    : "PhoenixCodeNav.Portal")));
            Assert.True(File.Exists(Path.Combine(
                publish,
                "portal",
                "wwwroot",
                "index.html")));
        }
        finally
        {
            if (Directory.Exists(publish))
                Directory.Delete(publish, recursive: true);
        }
    }

    [Fact]
    public async Task ProcessRunnerBoundsPipeDrainAfterParentExit()
    {
        if (!OperatingSystem.IsWindows()) return;
        var stopwatch = Stopwatch.StartNew();
        await Assert.ThrowsAsync<TimeoutException>(() => RunAsync("powershell.exe",
        [
            "-NoProfile", "-NonInteractive", "-Command",
            "Start-Process -FilePath ping.exe -ArgumentList '127.0.0.1','-n','5' -NoNewWindow; exit 0",
        ], Environment.CurrentDirectory, TimeSpan.FromSeconds(1)));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(3),
            $"pipe drainage exceeded its deadline: {stopwatch.Elapsed}");
    }

    [Fact]
    public async Task ProductionSingleFilePairServesFSharpSemanticRequestsOverStdio()
    {
        if (!OperatingSystem.IsWindows()) return;
        string root = Directory.CreateTempSubdirectory("Phoenix FSharp publish ").FullName;
        string publish = Path.Combine(root, "published pair with spaces");
        string workspace = Path.Combine(root, "FSharp workspace with spaces");
        int? portalPid = null;
        bool testSucceeded = false;
        Directory.CreateDirectory(publish);
        Directory.CreateDirectory(workspace);
        try
        {
            string repository = FindRepositoryRoot();
            string project = Path.Combine(repository, "src", "CodeNav.Mcp",
                "CodeNav.Mcp.csproj");
            RedirectedProcessResult result = await RunAsync("dotnet",
            [
                "publish", project, "-c", "Release", "-r", "win-x64",
                "--no-restore",
                "--self-contained", "-p:PublishSingleFile=true",
                "-p:UseSharedCompilation=false",
                "-p:EnableCompressionInSingleFile=true",
                "-p:IncludeNativeLibrariesForSelfExtract=true", "-o", publish,
            ], repository, TimeSpan.FromMinutes(3));
            Assert.True(result.ExitCode == 0,
                $"single-file publish failed ({result.ExitCode})\n{result.Output}\n{result.Error}");

            string executable = Path.Combine(publish, "PhoenixCodeNav.Mcp.exe");
            string sidecar = Path.Combine(publish, "FSharp.Core.dll");
            string portalExecutable = Path.Combine(
                publish,
                "portal",
                "PhoenixCodeNav.Portal.exe");
            string emptyPackageCache = Path.Combine(root, "empty NuGet cache");
            Directory.CreateDirectory(emptyPackageCache);
            Assert.True(File.Exists(executable));
            Assert.True(File.Exists(sidecar));
            Assert.True(File.Exists(portalExecutable));
            Assert.True(File.Exists(Path.Combine(publish, "portal", "wwwroot", "index.html")));
            Assert.Single(Directory.EnumerateFiles(publish, "FSharp.Core.dll",
                SearchOption.TopDirectoryOnly));

            File.WriteAllText(Path.Combine(workspace, "Canary.fsproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net9.0</TargetFramework>
                    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
                  </PropertyGroup>
                  <ItemGroup><Compile Include="Canary.fs" /></ItemGroup>
                </Project>
                """);
            File.WriteAllText(Path.Combine(workspace, "Canary.fs"),
                "module Canary\nlet publishedSidecarMarker = 42\n");

            var transport = new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = "F# single-file sidecar canary",
                Command = Path.GetFileName(executable),
                WorkingDirectory = publish,
                Arguments = new[] { "--workspace-root", workspace, "--standalone" },
                EnvironmentVariables = new Dictionary<string, string?>
                {
                    ["NUGET_PACKAGES"] = emptyPackageCache,
                },
            });
            using var mcpTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            await using McpClient client = await McpClient.CreateAsync(transport,
                cancellationToken: mcpTimeout.Token);
            JsonElement capabilities = await WaitForReadyAsync(client, TimeSpan.FromSeconds(60),
                mcpTimeout.Token);
            Assert.Equal("0.12.104", capabilities.GetProperty("version").GetString());
            int mcpPid = capabilities.GetProperty("runtime").GetProperty("processId").GetInt32();
            JsonElement detailedCapabilities = await CallJsonAsync(client,
                "server_capabilities", new Dictionary<string, object?>
                {
                    ["detail"] = true,
                }, mcpTimeout.Token);
            string coldStartTiming = Assert.Single(
                    detailedCapabilities.GetProperty("features").EnumerateArray(),
                    feature => feature.GetProperty("id").GetString() ==
                               "semantic-cold-start-phase-timing")
                .GetProperty("summary").GetString()!;
            Assert.Contains("present only when the call enters the C# semantic pipeline",
                coldStartTiming);
            Assert.Contains("F# semantic navigation", coldStartTiming);
            JsonElement semantic = await CallJsonAsync(client, "symbol_at",
                new Dictionary<string, object?>
                {
                    ["path"] = "Canary.fs",
                    ["line"] = 2,
                    ["column"] = 5,
                    ["timeoutMs"] = 60_000,
                }, mcpTimeout.Token);
            Assert.True(semantic.TryGetProperty("found", out JsonElement found) &&
                        found.GetBoolean(), semantic.ToString());
            Assert.Equal("publishedSidecarMarker",
                semantic.GetProperty("symbol").GetProperty("name").GetString());
            Assert.Contains("fsharp_core_reference_host_fallback",
                semantic.GetProperty("partialReason").GetString());
            Assert.Contains("fsharp_core_reference_defaulted",
                semantic.GetProperty("partialReason").GetString());
            Assert.Equal("indexed",
                semantic.GetProperty("meta").GetProperty("confidence").GetString());
            Assert.NotEqual("fsharp_core_reference_unavailable",
                semantic.TryGetProperty("error", out JsonElement error)
                    ? error.GetString()
                    : null);

            int semanticOpsBeforeDefinition = SemanticOpLineCount(workspace);
            JsonElement definition = await CallJsonAsync(client, "definition",
                new Dictionary<string, object?>
                {
                    ["path"] = "Canary.fs",
                    ["line"] = 2,
                    ["column"] = 5,
                    ["mode"] = "semantic",
                    ["timeoutMs"] = 60_000,
                }, mcpTimeout.Token);
            Assert.False(definition.TryGetProperty("error", out _), definition.ToString());
            Assert.False(definition.TryGetProperty("timing", out JsonElement fsharpTiming) &&
                         fsharpTiming.TryGetProperty("semanticColdStart", out _),
                definition.ToString());
            Assert.Equal(semanticOpsBeforeDefinition, SemanticOpLineCount(workspace));

            JsonElement started = await CallJsonAsync(
                client,
                "open_operations_portal",
                cancellationToken: mcpTimeout.Token);
            portalPid = started.GetProperty("pid").GetInt32();
            Assert.True(started.GetProperty("ready").GetBoolean(), started.ToString());
            Assert.Equal("started", started.GetProperty("status").GetString());
            Assert.False(started.GetProperty("browserOpened").GetBoolean());
            string portalUrl = started.GetProperty("url").GetString()!;
            Assert.StartsWith("http://127.0.0.1:", portalUrl, StringComparison.Ordinal);
            Assert.Contains("/#token=", portalUrl, StringComparison.Ordinal);

            JsonElement reused = await CallJsonAsync(
                client,
                "open_operations_portal",
                cancellationToken: mcpTimeout.Token);
            Assert.Equal("reused", reused.GetProperty("status").GetString());
            Assert.Equal(portalUrl, reused.GetProperty("url").GetString());
            Assert.Equal(portalPid, reused.GetProperty("pid").GetInt32());

            using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
            Uri portalUri = new(portalUrl);
            using HttpResponseMessage health = await http.GetAsync(
                new Uri(portalUri.GetLeftPart(UriPartial.Authority) + "/healthz"),
                mcpTimeout.Token);
            Assert.True(health.IsSuccessStatusCode);
            using Process mcpProcess = Process.GetProcessById(mcpPid);
            await client.DisposeAsync();
            await client.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            await mcpProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            testSucceeded = true;
        }
        finally
        {
            bool cleanupSucceeded = false;
            try
            {
                bool portalExited = await TestProcessLifecycle.StopProcessAsync(portalPid);
                PortalTestRuntimeCleanup.DeleteCoordinationFiles(workspace);
                if (testSucceeded)
                {
                    Assert.True(portalExited, "the published portal must exit before cleanup");
                    StrictWorkspaceCleanup.AssertLeaseReleased(workspace);
                }
                cleanupSucceeded = testSucceeded;
            }
            finally
            {
                try
                {
                    ExternalProcessWorkspaceCleanup.DeleteAfterSuccessWithoutLease(
                        cleanupSucceeded,
                        workspace);
                }
                finally
                {
                    // The outer tree contains freshly published images. Process exit is proven
                    // above, but Windows can release those image sections just after exit, so
                    // this tree is deliberately tolerant cleanup, not a handle-release proof.
                    TestWorkspaceCleanup.DeleteWorkspace(root);
                }
            }
        }
    }

    private static async Task<JsonElement> WaitForReadyAsync(McpClient client, TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        JsonElement last = default;
        while (stopwatch.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            last = await CallJsonAsync(client, "server_capabilities",
                cancellationToken: cancellationToken);
            if (last.TryGetProperty("index", out JsonElement index) &&
                index.TryGetProperty("state", out JsonElement state) &&
                state.GetString() == "ready")
                return last;
            await Task.Delay(100, cancellationToken);
        }
        Assert.Fail($"published server did not become ready: {last}");
        return last;
    }

    private static async Task<JsonElement> CallJsonAsync(McpClient client, string tool,
        IReadOnlyDictionary<string, object?>? arguments = null,
        CancellationToken cancellationToken = default)
    {
        CallToolResult result = await client.CallToolAsync(tool,
            arguments ?? new Dictionary<string, object?>(),
            cancellationToken: cancellationToken);
        TextContentBlock text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content));
        return JsonDocument.Parse(text.Text).RootElement.Clone();
    }

    private static int SemanticOpLineCount(string workspace)
    {
        string telemetryDir = Path.Combine(workspace, ".codenav", "telemetry");
        if (!Directory.Exists(telemetryDir)) return 0;
        return Directory.EnumerateFiles(telemetryDir, "phoenix-*.jsonl")
            .SelectMany(path => ReadShared(path)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries))
            .Count(line => line.Contains("\"e\":\"semanticOp\"",
                StringComparison.Ordinal));
    }

    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
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

    private static async Task<RedirectedProcessResult> RunAsync(string fileName,
        IEnumerable<string> arguments, string workingDirectory, TimeSpan timeout)
    {
        var start = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        start.Environment["DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER"] = "1";
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using Process process = Process.Start(start) ??
                                throw new InvalidOperationException($"Could not start {fileName}.");
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        return await TestProcessLifecycle.WaitForExitAndDrainAsync(
            process, output, error, timeout, fileName);
    }
}
