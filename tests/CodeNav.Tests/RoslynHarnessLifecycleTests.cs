using System.Diagnostics;

namespace CodeNav.Tests;

public sealed class RoslynHarnessLifecycleTests
{
    [Fact]
    public void HarnessPinsExternalRepositoriesButNeverPhoenixBuild()
    {
        string root = FindRepositoryRoot();
        string script = File.ReadAllText(
            Path.Combine(root, "scripts", "test-roslyn-mcp.ps1"));
        string baselinePath = Path.Combine(
            root, "tests", "integration", "roslyn-mcp-baseline.json");
        string baseline = File.ReadAllText(baselinePath);
        string fsharpBaseline = File.ReadAllText(Path.Combine(
            root, "tests", "integration", "fsharp-mcp-baseline.json"));
        string submodules = File.ReadAllText(Path.Combine(root, ".gitmodules"));

        Assert.Contains(
            "Assert-Equal ([string]$baseline.roslynCommit) $roslynHead",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "Frozen Roslyn workspace contains changes outside .codenav",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "Assert-Equal ([string]$fsharpBaseline.fsharpCommit) $fsharpHead",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "Frozen FSharp workspace contains changes outside .codenav",
            script,
            StringComparison.Ordinal);
        Assert.Contains("phoenixBuild = $null", script, StringComparison.Ordinal);

        foreach (string forbidden in new[]
                 {
                     "AllowCandidatePhoenix",
                     "PrintCandidateIdentity",
                     "CandidateExpectationsPath",
                     "Get-GitTargetIdentity",
                     "phoenixBaselineCommit",
                     "phoenixTargetSha256",
                     "phoenixIdentityEntryCount",
                     "mcpSha256",
                     "MCP version changed",
                     "MCP tool count changed",
                     "Index schema changed",
                     "Reusable index version changed",
                     "Follower schema changed",
                 })
        {
            Assert.DoesNotContain(forbidden, script, StringComparison.Ordinal);
        }

        using var roslynDocument = System.Text.Json.JsonDocument.Parse(baseline);
        using var fsharpDocument = System.Text.Json.JsonDocument.Parse(fsharpBaseline);
        System.Text.Json.JsonElement fixture = roslynDocument.RootElement;
        System.Text.Json.JsonElement fsharpFixture = fsharpDocument.RootElement;
        Assert.True(fixture.TryGetProperty("roslynCommit", out _));
        Assert.True(fsharpFixture.TryGetProperty("fsharpCommit", out _));
        Assert.False(fixture.TryGetProperty("fsharp", out _));
        Assert.Equal("external/roslyn",
            fixture.GetProperty("defaultWorkspace").GetString());
        Assert.Equal("external/fsharp",
            fsharpFixture.GetProperty("defaultWorkspace").GetString());
        Assert.Equal("src/FSharp.Core/option.fs",
            fsharpFixture.GetProperty("target").GetProperty("sourcePath").GetString());
        Assert.Contains("path = external/roslyn", submodules, StringComparison.Ordinal);
        Assert.Contains(
            "url = https://github.com/dotnet/roslyn",
            submodules,
            StringComparison.Ordinal);
        Assert.Contains("path = external/fsharp", submodules, StringComparison.Ordinal);
        Assert.Contains(
            "url = https://github.com/dotnet/fsharp",
            submodules,
            StringComparison.Ordinal);
        foreach (string forbidden in new[]
                 {
                     "phoenixBaselineCommit",
                     "mcpSha256",
                     "mcpVersion",
                     "indexSchema",
                     "indexVersion",
                 })
        {
            Assert.False(fixture.TryGetProperty(forbidden, out _),
                $"External fixture must not lock Phoenix field '{forbidden}'.");
            Assert.False(fsharpFixture.TryGetProperty(forbidden, out _),
                $"FSharp fixture must not lock Phoenix field '{forbidden}'.");
        }

        Assert.False(File.Exists(Path.Combine(
            root, "tests", "integration", "roslyn-mcp-candidate.json")));
    }

    [Fact]
    public void HarnessRequiresStableReadyObservationsBeforeSemanticProbes()
    {
        string root = FindRepositoryRoot();
        string script = File.ReadAllText(
            Path.Combine(root, "scripts", "test-roslyn-mcp.ps1"));

        Assert.Contains("$stableReadyObservations = 0", script, StringComparison.Ordinal);
        Assert.Contains("for ($attempt = 0; $attempt -lt 600; $attempt++)", script,
            StringComparison.Ordinal);
        Assert.Contains("$stableReadyObservations++", script, StringComparison.Ordinal);
        Assert.Contains("if ($stableReadyObservations -ge 2) { break }", script,
            StringComparison.Ordinal);
        Assert.Contains("Start-Sleep -Seconds 1", script, StringComparison.Ordinal);
        Assert.Contains("Assert-Equal 2 $stableReadyObservations", script,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Start-Sleep -Milliseconds 250", script,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task SemanticRetryIncludesIndexedAutoFallbacks()
    {
        // The script body is immediate; this outer bound includes PowerShell startup while the
        // solution gate is concurrently running the process-heavy index and Git test projects.
        string output = await RunSelfTest("-SelfTestSemanticRetryContract", TimeSpan.FromSeconds(45));
        Assert.Contains("Semantic retry contract self-test passed", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TeardownBoundsStderrAndKillsDescendantProcessTree()
    {
        // The PowerShell self-test independently enforces the 15-second teardown bound.
        // Its outer watchdog also covers two process startups, a 15-second readiness bound, and
        // descendant verification while other solution test projects are running concurrently.
        string output = await RunSelfTest("-SelfTestProcessLifecycle", TimeSpan.FromSeconds(90));
        Assert.Contains("Process lifecycle self-test passed", output, StringComparison.Ordinal);
    }

    private static async Task<string> RunSelfTest(string switchName, TimeSpan timeout)
    {
        string root = FindRepositoryRoot();
        string script = Path.Combine(root, "scripts", "test-roslyn-mcp.ps1");
        string powerShell = OperatingSystem.IsWindows() ? "powershell.exe" : "pwsh";
        var start = new ProcessStartInfo(powerShell)
        {
            WorkingDirectory = root,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-ExecutionPolicy");
        start.ArgumentList.Add("Bypass");
        start.ArgumentList.Add("-File");
        start.ArgumentList.Add(script);
        start.ArgumentList.Add(switchName);

        using var process = Process.Start(start)!;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(timeout);
        }
        catch (TimeoutException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        string output = (await stdout) + Environment.NewLine + (await stderr);
        Assert.True(process.ExitCode == 0,
            $"Roslyn harness self-test {switchName} exited {process.ExitCode}:{Environment.NewLine}{output}");
        return output;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "PhoenixCodeNav.sln")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new DirectoryNotFoundException(
            "Could not locate PhoenixCodeNav.sln from the test output directory.");
    }
}
