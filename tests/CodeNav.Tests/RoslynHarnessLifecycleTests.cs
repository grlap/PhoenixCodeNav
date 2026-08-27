using System.Diagnostics;

namespace CodeNav.Tests;

public sealed class RoslynHarnessLifecycleTests : IDisposable
{
    private readonly ProcessHeavyTestIsolation _processHeavyTestIsolation =
        ProcessHeavyTestIsolation.Acquire();

    public void Dispose() => _processHeavyTestIsolation.Dispose();

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
        Assert.Contains(
            "$roslynIndexWasMissing = -not (Test-Path -LiteralPath $IndexDb -PathType Leaf)",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "$fsharpIndexWasMissing = -not (Test-Path -LiteralPath $FSharpIndexDb -PathType Leaf)",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "Initialize-ReusableIndex \"writer\" $Workspace $IndexDb",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "Roslyn index bootstrap did not produce a reusable index",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "Initialize-ReusableIndex \"fsharp-writer\" $FSharpWorkspace $FSharpIndexDb",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "FSharp index bootstrap did not produce a reusable index",
            script,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Reusable Roslyn index is missing",
            script,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Reusable FSharp index is missing",
            script,
            StringComparison.Ordinal);

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
        Assert.Equal(
            "repeated_full_rebuild_friend_assembly_consumer_scan_authority_with_exact_public_method_canary",
            fixture.GetProperty("countsProvenance").GetString());
        Assert.Contains("Repeated ordinary startup and in-band full rebuild probes",
            fixture.GetProperty("countsProvenanceDetail").GetString());
        Assert.Contains("10 compiler-bound ICompilationFactoryService references",
            fixture.GetProperty("countsProvenanceDetail").GetString());
        Assert.Contains("49 indexed textual candidates are discovery inputs",
            fixture.GetProperty("countsProvenanceDetail").GetString());
        Assert.Contains("GetOpenDocumentIds",
            fixture.GetProperty("countsProvenanceDetail").GetString());
        Assert.Contains("external candidates cannot bind and emit no result group",
            fixture.GetProperty("countsProvenanceDetail").GetString());
        Assert.Contains("implementations retains exact symbol identity with heuristic indexed base-list hits",
            fixture.GetProperty("countsProvenanceDetail").GetString());
        Assert.Contains("six unproven friend-assembly projects",
            fixture.GetProperty("countsProvenanceDetail").GetString());
        Assert.Equal("external/fsharp",
            fsharpFixture.GetProperty("defaultWorkspace").GetString());
        Assert.Equal("src/FSharp.Core/option.fs",
            fsharpFixture.GetProperty("target").GetProperty("sourcePath").GetString());
        Assert.Equal(116168,
            fsharpFixture.GetProperty("counts").GetProperty("symbols").GetInt32());
        Assert.Equal(4174,
            fsharpFixture.GetProperty("counts").GetProperty("orphanedFiles").GetInt32());
        Assert.Equal("schema_29_repeated_full_and_scratch_cold_build_with_reused_index_parity",
            fsharpFixture.GetProperty("countsProvenance").GetString());
        Assert.Contains("in-band full refresh and an independent scratch cold build",
            fsharpFixture.GetProperty("countsProvenanceDetail").GetString());
        Assert.Contains("previously checked-in 116170/4140 claim was not reproducible",
            fsharpFixture.GetProperty("countsProvenanceDetail").GetString());
        Assert.Contains("cold-build/delta parity canary guards recurrence",
            fsharpFixture.GetProperty("countsProvenanceDetail").GetString());
        Assert.Contains("subsequent ordinary reuse",
            fsharpFixture.GetProperty("countsProvenanceDetail").GetString());
        Assert.Equal("indexed",
            fixture.GetProperty("target").GetProperty("friendRelationshipConfidence").GetString());
        Assert.Equal("project_model_unproven",
            fixture.GetProperty("target").GetProperty("friendRelationshipPartialReason").GetString());
        Assert.Equal("T:Microsoft.CodeAnalysis.Host.ICompilationFactoryService",
            fixture.GetProperty("target").GetProperty("documentationCommentId").GetString());
        Assert.Equal(6, fixture.GetProperty("target")
            .GetProperty("referencesUnprovenFriendAssemblyProjects").GetArrayLength());
        Assert.Equal("heuristic", fixture.GetProperty("target")
            .GetProperty("implementationsConfidence").GetString());
        Assert.Equal("no_semantic_implementers", fixture.GetProperty("target")
            .GetProperty("implementationsPartialReason").GetString());
        Assert.Equal("exact", fixture.GetProperty("target")
            .GetProperty("typeHierarchyConfidence").GetString());
        Assert.Equal("heuristic", fixture.GetProperty("target")
            .GetProperty("typeHierarchyDerivedConfidence").GetString());
        Assert.Equal("no_semantic_derived", fixture.GetProperty("target")
            .GetProperty("typeHierarchyPartialReason").GetString());
        Assert.Equal(6, fixture.GetProperty("target")
            .GetProperty("typeHierarchyUnprovenFriendAssemblyProjects").GetArrayLength());
        Assert.Equal(10,
            fixture.GetProperty("target").GetProperty("referenceCount").GetInt32());
        Assert.Equal(1,
            fixture.GetProperty("target").GetProperty("referenceProjects").GetInt32());
        Assert.Contains("function Invoke-ReferencesWithTelemetry", script,
            StringComparison.Ordinal);
        Assert.Contains("$afterCount = @(Get-ReferenceTelemetryRecords $Client).Count", script,
            StringComparison.Ordinal);
        Assert.Contains("function Invoke-SemanticWithRetryCore", script,
            StringComparison.Ordinal);
        Assert.Contains("([ref]$attemptCount)", script, StringComparison.Ordinal);
        Assert.Contains("Wait-ReferenceTelemetry $Client $afterCount `", script,
            StringComparison.Ordinal);
        Assert.Contains("Select-Object -Skip $AfterCount", script,
            StringComparison.Ordinal);
        Assert.Contains("Select-Object -Last 1", script,
            StringComparison.Ordinal);
        Assert.Contains("AddMilliseconds(750)", script, StringComparison.Ordinal);
        Assert.Contains("post-call record set to settle", script, StringComparison.Ordinal);
        Assert.Contains("timed out waiting for accepted references telemetry", script,
            StringComparison.Ordinal);
        Assert.DoesNotContain("$AfterCount + $ExpectedNewCount", script,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Select-Object -Last $ExpectedCount", script,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "[string]$record.result -eq $ExpectedResult",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "Assert-Equal $ExpectedResult ([string]$accepted.result)",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "-ExpectedUnprovenProjects @($baseline.target.referencesUnprovenFriendAssemblyProjects)",
            script,
            StringComparison.Ordinal);
        Assert.Contains("roslynCountsProvenance = [ordered]@{", script,
            StringComparison.Ordinal);
        Assert.Contains(
            "$baseline.target.typeHierarchyUnprovenFriendAssemblyProjects |",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "Test-IntegrationCase \"compiler-exact method references\"",
            script,
            StringComparison.Ordinal);
        Assert.Equal(9,
            fixture.GetProperty("exactReferencesTarget")
                .GetProperty("referenceCount").GetInt32());
        Assert.Equal(4,
            fixture.GetProperty("exactReferencesTarget")
                .GetProperty("referenceProjects").GetInt32());
        Assert.Equal(2,
            fixture.GetProperty("exactReferencesTarget")
                .GetProperty("samplesPerGroup").GetInt32());
        Assert.Equal(4,
            fixture.GetProperty("exactReferencesTarget")
                .GetProperty("groups").GetArrayLength());
        Assert.Contains(
            "Method references lost compiler-exact confidence",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "Repair-ReusedFSharpIndexIfCountsDrift $fsharpWriter $fsharpOverview",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "Repair-ReusedRoslynIndexIfCountsDrift $writer $overview",
            script,
            StringComparison.Ordinal);
        Assert.Contains("function Get-RoslynReferenceAuthorityEvidence", script,
            StringComparison.Ordinal);
        Assert.Contains("function Test-RoslynReferenceAuthorityEvidence", script,
            StringComparison.Ordinal);
        Assert.Contains("priorAuthorityEvidence = $priorAuthorityEvidence", script,
            StringComparison.Ordinal);
        Assert.Contains("rebuiltAuthorityEvidence = $rebuiltAuthorityEvidence", script,
            StringComparison.Ordinal);
        Assert.Contains("full rebuild still disagrees with the pinned authority-evidence baseline",
            script, StringComparison.Ordinal);
        Assert.Contains("Roslyn authority-probe restart crossed index epochs", script,
            StringComparison.Ordinal);
        Assert.Contains("roslynIndexRepair = $null", script,
            StringComparison.Ordinal);
        Assert.Contains("fsharpIndexRepair = $null", script, StringComparison.Ordinal);
        Assert.Contains("priorIndexVersion = $priorVersion", script,
            StringComparison.Ordinal);
        Assert.Contains("rebuiltIndexVersion = [string]$rebuilt.meta.indexVersion", script,
            StringComparison.Ordinal);
        Assert.Contains("$Label baseline will be judged against repaired index", script,
            StringComparison.Ordinal);
        Assert.Contains(
            "Test-IntegrationCase \"Roslyn reusable index startup and reuse are honest\"",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "Test-IntegrationCase \"FSharp reusable index startup and reuse are honest\"",
            script,
            StringComparison.Ordinal);
        Assert.Contains("this gate run remains failed", script,
            StringComparison.Ordinal);
        Assert.Contains("startupBuildReason = [string]$resetSession.Capabilities.index.startupBuildReason",
            script, StringComparison.Ordinal);
        Assert.Contains("startupPriorSchema = [string]$resetSession.Capabilities.index.startupPriorSchema",
            script, StringComparison.Ordinal);
        Assert.Contains("outcome = if ($startupRebuilt) { \"startup_rebuilt\" } else { \"reused\" }",
            script, StringComparison.Ordinal);
        Assert.Contains("only a later ordinary reuse of the repaired matching fixture may pass",
            script, StringComparison.Ordinal);
        Assert.Contains("outcome = \"bootstrapped\"", script,
            StringComparison.Ordinal);
        Assert.Contains("rebuiltCounts = Get-RoslynOverviewCounts $overview", script,
            StringComparison.Ordinal);
        Assert.Contains("$fsharpCapabilities = Invoke-McpTool $fsharpWriter \"server_capabilities\"",
            script,
            StringComparison.Ordinal);
        Assert.Contains("fsharpCapabilities = $fsharpCapabilities", script,
            StringComparison.Ordinal);
        Assert.Contains(
            "FSharp capabilities evidence is stale for the judged index epoch",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "omitted required friend-assembly coverage",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "Assert-FriendRelationshipAuthority $baseDefinition \"Implementation base binding\" $false",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "$Label full rebuild is still publishing",
            script,
            StringComparison.Ordinal);
        Assert.Contains("$implementations.implementationsConfidence", script,
            StringComparison.Ordinal);
        Assert.Contains("$hierarchy.derivedConfidence", script,
            StringComparison.Ordinal);
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
    public void HarnessRetiresSharedDaemonsThroughTheAuthorityCheckedControlPath()
    {
        string root = FindRepositoryRoot();
        string script = File.ReadAllText(
            Path.Combine(root, "scripts", "test-roslyn-mcp.ps1"));

        Assert.Contains("function Request-McpDaemonRetirement", script,
            StringComparison.Ordinal);
        Assert.Contains("--daemon-retire-authorized", script, StringComparison.Ordinal);
        Assert.Contains("Request-McpDaemonRetirement $client", script,
            StringComparison.Ordinal);
        Assert.Contains("$initialized = $false", script, StringComparison.Ordinal);
        Assert.Contains("if ($initialized -and $cleanupErrors.Count -gt 0)", script,
            StringComparison.Ordinal);
        Assert.Contains("cleanup after initialization failure also failed", script,
            StringComparison.Ordinal);
        Assert.DoesNotContain("function Stop-McpDaemonProcess", script,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Get-Process -Id $daemonPid", script,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ProcessLifecycleSelfTestsRunBeforeHeavyHarnessInitializationAndKeepCleanupFinally()
    {
        string root = FindRepositoryRoot();
        string script = File.ReadAllText(
            Path.Combine(root, "scripts", "test-roslyn-mcp.ps1"));
        int setupStart = script.IndexOf(
            "function Stop-ProcessTree", StringComparison.Ordinal);
        Assert.True(setupStart >= 0, "Lifecycle cleanup helpers were not found.");

        int blockStart = script.IndexOf(
            "if ($SelfTestProcessLifecycle -or $SelfTestProcessLifecycleReadinessFailure) {",
            setupStart, StringComparison.Ordinal);
        Assert.True(blockStart > setupStart, "Lifecycle self-test block was not found after its helpers.");

        int blockEnd = script.IndexOf(
            "if ($null -eq (\"PhoenixCodeNav.Integration.BoundedTextTail\" -as [type])) {",
            blockStart, StringComparison.Ordinal);
        Assert.True(blockEnd > blockStart,
            "Heavy harness initialization was not found after the lifecycle self-test.");
        string lifecycleBlock = script[setupStart..blockEnd];
        Assert.Contains("$client = $null", lifecycleBlock, StringComparison.Ordinal);
        Assert.Contains("try {", lifecycleBlock, StringComparison.Ordinal);
        Assert.Contains("} finally {", lifecycleBlock, StringComparison.Ordinal);
        Assert.Contains("if ($null -ne $client)", lifecycleBlock,
            StringComparison.Ordinal);
        Assert.Contains("Stop-McpClient $client", lifecycleBlock,
            StringComparison.Ordinal);
        Assert.Contains("StderrTask = $process.StandardError.ReadToEndAsync()",
            lifecycleBlock, StringComparison.Ordinal);
        Assert.Contains("$Client.Process.WaitForExit()", lifecycleBlock,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Add-Type -TypeDefinition", lifecycleBlock,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Get-Content -Raw -LiteralPath $BaselinePath",
            lifecycleBlock, StringComparison.Ordinal);
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
        // Its outer watchdog also covers two process startups, a 15-second control-stream
        // readiness bound, and
        // descendant verification while other solution test projects are running concurrently.
        string output = await RunSelfTest("-SelfTestProcessLifecycle", TimeSpan.FromSeconds(90));
        Assert.Contains("Process lifecycle self-test passed", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadinessFailureStillKillsDescendantProcessTree()
    {
        (int exitCode, string output) = await RunSelfTestProcess(
            "-SelfTestProcessLifecycleReadinessFailure", TimeSpan.FromSeconds(45));
        Assert.NotEqual(0, exitCode);
        System.Text.RegularExpressions.Match match =
            System.Text.RegularExpressions.Regex.Match(output,
                "READINESS_FAILURE_GRANDCHILD_PID=(\\d+)");
        Assert.True(match.Success, output);
        int grandchildPid = int.Parse(match.Groups[1].Value,
            System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(WaitForProcessExit(grandchildPid, TimeSpan.FromSeconds(15)),
            $"Readiness-failure cleanup left descendant {grandchildPid} alive:{Environment.NewLine}{output}");
    }

    private static async Task<string> RunSelfTest(string switchName, TimeSpan timeout)
    {
        (int exitCode, string output) = await RunSelfTestProcess(switchName, timeout);
        Assert.True(exitCode == 0,
            $"Roslyn harness self-test {switchName} exited {exitCode}:{Environment.NewLine}{output}");
        return output;
    }

    private static async Task<(int ExitCode, string Output)> RunSelfTestProcess(
        string switchName, TimeSpan timeout)
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
        return (process.ExitCode, output);
    }

    private static bool WaitForProcessExit(int processId, TimeSpan timeout)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            return process.WaitForExit((int)timeout.TotalMilliseconds);
        }
        catch (ArgumentException)
        {
            return true;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
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
