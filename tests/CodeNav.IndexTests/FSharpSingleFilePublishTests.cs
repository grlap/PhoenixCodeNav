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
        Directory.CreateDirectory(publish);
        Directory.CreateDirectory(workspace);
        try
        {
            string repository = FindRepositoryRoot();
            string project = Path.Combine(repository, "src", "CodeNav.Mcp",
                "CodeNav.Mcp.csproj");
            ProcessResult result = await RunAsync("dotnet",
            [
                "publish", project, "-c", "Release", "-r", "win-x64",
                "--no-restore",
                "--self-contained", "-p:PublishSingleFile=true",
                "-p:EnableCompressionInSingleFile=true",
                "-p:IncludeNativeLibrariesForSelfExtract=true", "-o", publish,
            ], repository, TimeSpan.FromMinutes(3));
            Assert.True(result.ExitCode == 0,
                $"single-file publish failed ({result.ExitCode})\n{result.Output}\n{result.Error}");

            string executable = Path.Combine(publish, "PhoenixCodeNav.Mcp.exe");
            string sidecar = Path.Combine(publish, "FSharp.Core.dll");
            string emptyPackageCache = Path.Combine(root, "empty NuGet cache");
            Directory.CreateDirectory(emptyPackageCache);
            Assert.True(File.Exists(executable));
            Assert.True(File.Exists(sidecar));
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
                Arguments = new[] { "--workspace-root", workspace },
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
            Assert.Equal("0.12.10", capabilities.GetProperty("version").GetString());
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
            Assert.NotEqual("fsharp_core_reference_unavailable",
                semantic.TryGetProperty("error", out JsonElement error)
                    ? error.GetString()
                    : null);
        }
        finally
        {
            TestWorkspaceCleanup.ClearIndexPools(workspace);
            try { Directory.Delete(root, recursive: true); } catch { }
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

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "PhoenixCodeNav.sln")))
            directory = directory.Parent;
        return directory?.FullName ??
               throw new InvalidOperationException("Could not locate PhoenixCodeNav.sln.");
    }

    private static async Task<ProcessResult> RunAsync(string fileName,
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
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using Process process = Process.Start(start) ??
                                throw new InvalidOperationException($"Could not start {fileName}.");
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            // Process.WaitForExitAsync also waits for redirected streams to reach EOF. A
            // descendant can keep those pipe handles open after the direct process exits, so
            // observe process exit independently and spend the same deadline on pipe drainage.
            await WaitForExitSignalAsync(process, cts.Token);
            string[] captured = await Task.WhenAll(output, error).WaitAsync(cts.Token);
            return new(process.ExitCode, captured[0], captured[1]);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            Exception? cleanupError = null;
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                using var cleanupCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await WaitForExitSignalAsync(process, cleanupCts.Token);
            }
            catch (Exception ex)
            {
                cleanupError = ex;
            }

            // A descendant can retain inherited pipe handles after the direct process exits.
            // Closing our readers is the only bounded way to release those reads once the parent
            // can no longer be traversed by Process.Kill(entireProcessTree: true).
            // StreamReader.Close can wait behind its outstanding async read. Close the pipe
            // streams themselves so the reads fault/complete without making cleanup unbounded.
            try { process.StandardOutput.BaseStream.Dispose(); }
            catch (Exception ex) { cleanupError ??= ex; }
            try { process.StandardError.BaseStream.Dispose(); }
            catch (Exception ex) { cleanupError ??= ex; }
            string[] captured = await ObserveReadersAsync(output, error);
            throw new TimeoutException(
                $"{fileName} exceeded {timeout}.\n{captured[0]}\n{captured[1]}", cleanupError);
        }
    }

    private static async Task WaitForExitSignalAsync(Process process,
        CancellationToken cancellationToken)
    {
        if (process.HasExited) return;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnExited(object? _, EventArgs __) => completion.TrySetResult();
        process.Exited += OnExited;
        try
        {
            process.EnableRaisingEvents = true;
            if (process.HasExited) return;
            await completion.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            process.Exited -= OnExited;
        }
    }

    private static async Task<string[]> ObserveReadersAsync(params Task<string>[] readers)
    {
        Task<string[]> all = Task.WhenAll(readers);
        Task completed = await Task.WhenAny(all, Task.Delay(TimeSpan.FromMilliseconds(250)));
        if (completed == all)
        {
            try { return await all; }
            catch { }
        }

        _ = all.ContinueWith(static task => _ = task.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return readers.Select(static reader => reader.Status == TaskStatus.RanToCompletion
                ? reader.Result
                : "<redirected output unavailable after deadline>")
            .ToArray();
    }

    private sealed record ProcessResult(int ExitCode, string Output, string Error);
}
