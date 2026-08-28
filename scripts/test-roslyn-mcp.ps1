[CmdletBinding()]
param(
    [string]$Workspace,
    [string]$IndexDb,
    [string]$BaselinePath,
    [string]$FSharpWorkspace,
    [string]$FSharpIndexDb,
    [string]$FSharpBaselinePath,
    [string]$EvidencePath,
    [string]$PinnedNet472ReferenceRoot,
    [switch]$SelfTestProcessLifecycle,
    [switch]$SelfTestProcessLifecycleReadinessFailure,
    [switch]$SelfTestProcessHost,
    [switch]$SelfTestProcessGrandchild,
    [switch]$SelfTestSemanticRetryContract,
    [switch]$SelfTestFreshIndexLifecycleContract,
    [switch]$SelfTestPinnedFrameworkReferenceContract,
    [switch]$SelfTestFreshIndexLeaseProbe,
    [string]$SelfTestLeasePath
)

Set-StrictMode -Version 1.0
$ErrorActionPreference = "Stop"

# Private modes used by the xUnit lifecycle regression below. The host deliberately wedges
# with a descendant inheriting stderr so teardown must kill the complete process tree.
if ($SelfTestProcessGrandchild) {
    [Console]::Error.WriteLine("GRANDCHILD_READY=$PID")
    Start-Sleep -Seconds 60
    exit 0
}
if ($SelfTestProcessHost) {
    $shell = (Get-Process -Id $PID).Path
    $start = New-Object System.Diagnostics.ProcessStartInfo
    $start.FileName = $shell
    $start.Arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`" -SelfTestProcessGrandchild"
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $grandchild = [Diagnostics.Process]::Start($start)
    # Publish the descendant identity on the quiet control stream before intentionally flooding
    # stderr. Under full-suite CPU contention, discovering this line through the asynchronous
    # rolling stderr tail made readiness depend on how quickly 200 KiB could be drained.
    [Console]::Out.WriteLine("GRANDCHILD_PID=$($grandchild.Id)")
    [Console]::Out.Flush()
    [Console]::Error.Write((('x' * 200000) -join ''))
    Start-Sleep -Seconds 60
    exit 0
}

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))

function Quote-ProcessArgument([string]$Value) {
    return '"' + $Value.Replace('"', '\"') + '"'
}

function Test-IsFileContentionException([Exception]$Exception) {
    $current = $Exception
    while ($null -ne $current) {
        if ($current -is [IO.IOException] -or
            $current -is [UnauthorizedAccessException]) {
            return $true
        }
        $current = $current.InnerException
    }
    return $false
}

if ($SelfTestFreshIndexLeaseProbe) {
    $probeLease = $null
    try {
        $probeLease = [IO.File]::Open($SelfTestLeasePath, [IO.FileMode]::OpenOrCreate,
            [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
        Write-Host "LEASE_PROBE_ACQUIRED"
        exit 0
    } catch {
        if (Test-IsFileContentionException $_.Exception) {
            [Console]::Error.WriteLine("LEASE_PROBE_REJECTED")
            exit 23
        }
        throw
    } finally {
        if ($null -ne $probeLease) { $probeLease.Dispose() }
    }
}

function Stop-ProcessTree([Diagnostics.Process]$Process) {
    if ($Process.HasExited) { return }
    $killTree = @($Process.GetType().GetMethods() | Where-Object {
        $_.Name -eq "Kill" -and $_.GetParameters().Count -eq 1 -and
        $_.GetParameters()[0].ParameterType -eq [bool]
    } | Select-Object -First 1)
    if ($killTree.Count -gt 0) {
        $killTree[0].Invoke($Process, @($true)) | Out-Null
        return
    }
    if ([Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT) {
        $taskkill = Join-Path $env:SystemRoot "System32\taskkill.exe"
        & $taskkill /PID $Process.Id /T /F 2>$null | Out-Null
        return
    }
    $Process.Kill()
}

function Get-McpClientStderrSnapshot($Client) {
    $tailProperty = $Client.PSObject.Properties["StderrTail"]
    if ($null -ne $tailProperty -and $null -ne $tailProperty.Value) {
        return [string]$tailProperty.Value.Snapshot()
    }

    $stderr = [string]$Client.StderrTask.Result
    if ($stderr.Length -le 65536) { return $stderr }
    return $stderr.Substring($stderr.Length - 65536)
}

function Stop-McpClient($Client) {
    if ($null -eq $Client) { return }
    $exitConfirmed = $false
    try {
        try { $Client.Process.StandardInput.Close() } catch { }
        if (-not $Client.Process.WaitForExit(5000)) {
            Stop-ProcessTree $Client.Process
        }
        if (-not $Client.Process.WaitForExit(5000)) {
            throw "$($Client.Label): process tree did not exit after bounded termination"
        }
        # StandardError is consumed with ReadToEndAsync, so that task—not the parameterless
        # WaitForExit overload—is the actual redirected-stream drain barrier.
        $exitConfirmed = $true
        if (-not $Client.StderrTask.Wait(3000)) {
            throw "$($Client.Label): stderr drain did not complete after process exit"
        }
        $stderr = Get-McpClientStderrSnapshot $Client
        if ($Client.Process.ExitCode -ne 0 -and -not [bool]$Client.AllowNonZeroExit) {
            $tail = @($stderr -split "`r?`n" | Select-Object -Last 12) -join [Environment]::NewLine
            throw "$($Client.Label): MCP exited $($Client.Process.ExitCode)`n$tail"
        }
    } finally {
        if (-not $exitConfirmed) {
            try {
                if (-not $Client.Process.HasExited) {
                    Stop-ProcessTree $Client.Process
                    $Client.Process.WaitForExit(2000) | Out-Null
                }
            } catch { }
        }
        $Client.Process.Dispose()
    }
}

function Start-ProcessLifecycleSelfTestClient {
    $shell = (Get-Process -Id $PID).Path
    $start = New-Object System.Diagnostics.ProcessStartInfo
    $start.FileName = $shell
    $start.Arguments = "-NoProfile -ExecutionPolicy Bypass -File $(Quote-ProcessArgument $PSCommandPath) -SelfTestProcessHost"
    $start.WorkingDirectory = $repoRoot
    $start.UseShellExecute = $false
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.CreateNoWindow = $true
    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $start
    if (-not $process.Start()) { throw "Failed to start lifecycle self-test host" }
    return [pscustomobject]@{
        Label = "lifecycle-self-test"
        Process = $process
        NextId = 0
        StderrTail = $null
        StderrTask = $process.StandardError.ReadToEndAsync()
        ReadyTask = $process.StandardOutput.ReadLineAsync()
        AllowNonZeroExit = $true
    }
}

# Run lifecycle probes before Add-Type, baseline loading, or external-workspace setup. Their
# process tree and stderr volume are deliberately bounded, so ReadToEndAsync avoids a cold C#
# compilation that can starve behind the process-heavy solution projects during the full gate.
if ($SelfTestProcessLifecycle -or $SelfTestProcessLifecycleReadinessFailure) {
    $client = $null
    try {
        $client = Start-ProcessLifecycleSelfTestClient
        if (-not $client.ReadyTask.Wait(15000)) {
            throw "Lifecycle host did not report readiness"
        }
        $pidMatch = [regex]::Match([string]$client.ReadyTask.Result, "GRANDCHILD_PID=(\d+)")
        if (-not $pidMatch.Success) {
            throw "Lifecycle host did not report its descendant pid"
        }
        $grandchildPid = [int]$pidMatch.Groups[1].Value
        if ($SelfTestProcessLifecycleReadinessFailure) {
            Write-Host "READINESS_FAILURE_GRANDCHILD_PID=$grandchildPid"
            throw "Injected lifecycle readiness assertion failure"
        }

        $stopwatch = [Diagnostics.Stopwatch]::StartNew()
        Stop-McpClient $client
        $rawStderrLength = ([string]$client.StderrTask.Result).Length
        $stderr = Get-McpClientStderrSnapshot $client
        $client = $null
        if ($stopwatch.Elapsed -ge [TimeSpan]::FromSeconds(15)) {
            throw "Lifecycle teardown exceeded its hard bound"
        }
        if ($stderr.Length -gt 65536) {
            throw "Captured stderr exceeded the rolling-tail bound"
        }
        if ($rawStderrLength -le 65536) {
            throw "Lifecycle probe did not produce enough stderr to exercise tail truncation"
        }
        $deadline = [DateTime]::UtcNow.AddSeconds(2)
        while ([DateTime]::UtcNow -lt $deadline -and
               $null -ne (Get-Process -Id $grandchildPid -ErrorAction SilentlyContinue)) {
            Start-Sleep -Milliseconds 50
        }
        if ($null -ne (Get-Process -Id $grandchildPid -ErrorAction SilentlyContinue)) {
            throw "Lifecycle teardown left a descendant running"
        }
        Write-Host "Process lifecycle self-test passed"
    } finally {
        if ($null -ne $client) {
            try { Stop-McpClient $client } catch {
                Write-Warning "Lifecycle cleanup after readiness failure also failed: $($_.Exception.Message)"
            }
        }
    }
    exit 0
}

if ($null -eq ("PhoenixCodeNav.Integration.BoundedTextTail" -as [type])) {
    Add-Type -TypeDefinition @"
using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace PhoenixCodeNav.Integration
{
    public sealed class BoundedTextTail
    {
        private readonly int _capacity;
        private readonly object _gate = new object();
        private readonly StringBuilder _text = new StringBuilder();

        public BoundedTextTail(int capacity)
        {
            if (capacity < 1) throw new ArgumentOutOfRangeException("capacity");
            _capacity = capacity;
        }

        public async Task DrainAsync(StreamReader reader)
        {
            var buffer = new char[4096];
            int read;
            while ((read = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
            {
                lock (_gate)
                {
                    _text.Append(buffer, 0, read);
                    if (_text.Length > _capacity)
                        _text.Remove(0, _text.Length - _capacity);
                }
            }
        }

        public string Snapshot()
        {
            lock (_gate) return _text.ToString();
        }
    }
}
"@
}

if ([string]::IsNullOrWhiteSpace($BaselinePath)) {
    $BaselinePath = Join-Path $repoRoot "tests\integration\roslyn-mcp-baseline.json"
}
$baseline = Get-Content -Raw -LiteralPath $BaselinePath | ConvertFrom-Json
$expectedCounts = $baseline.counts
if ([string]::IsNullOrWhiteSpace($Workspace)) {
    $Workspace = if ([string]::IsNullOrWhiteSpace($env:PHOENIX_ROSLYN_WORKSPACE)) {
        Join-Path $repoRoot ([string]$baseline.defaultWorkspace)
    } else {
        $env:PHOENIX_ROSLYN_WORKSPACE
    }
}
$Workspace = [IO.Path]::GetFullPath($Workspace)
$externalIntegrationRoot = [IO.Path]::GetFullPath(
    (Join-Path $repoRoot "artifacts\external-integration"))
$freshRun = $null
$freshIndexRoot = $null
$freshRunLease = $null
$isolatedPackagesRoot = $null
$freshRunLeasePath = Join-Path $externalIntegrationRoot ".fresh-index-gate.lock"
$usesDefaultRoslynIndex = [string]::IsNullOrWhiteSpace($IndexDb)
if (-not $usesDefaultRoslynIndex) {
    $IndexDb = [IO.Path]::GetFullPath($IndexDb)
}
if ([string]::IsNullOrWhiteSpace($FSharpBaselinePath)) {
    $FSharpBaselinePath = Join-Path $repoRoot "tests\integration\fsharp-mcp-baseline.json"
}
$fsharpBaseline = Get-Content -Raw -LiteralPath $FSharpBaselinePath | ConvertFrom-Json
$pinnedNet472Package = [string]$baseline.semanticInputs.net472ReferencePackage
$pinnedNet472Version = [string]$baseline.semanticInputs.net472ReferencePackageVersion
$pinnedNet472Framework = [string]$baseline.semanticInputs.net472Framework
$expectedResolvedPackageDllCount = [int]$baseline.semanticInputs.resolvedPackageDllCount
if ([string]::IsNullOrWhiteSpace($PinnedNet472ReferenceRoot)) {
    $PinnedNet472ReferenceRoot = Join-Path $repoRoot `
        "tests\CodeNav.Tests\bin\Release\net10.0\pinned-frameworks\net472"
}
$pinnedNet472ReferenceRoot = [IO.Path]::GetFullPath($PinnedNet472ReferenceRoot)
if ([string]::IsNullOrWhiteSpace($FSharpWorkspace)) {
    $FSharpWorkspace = if ([string]::IsNullOrWhiteSpace($env:PHOENIX_FSHARP_WORKSPACE)) {
        Join-Path $repoRoot ([string]$fsharpBaseline.defaultWorkspace)
    } else {
        $env:PHOENIX_FSHARP_WORKSPACE
    }
}
$FSharpWorkspace = [IO.Path]::GetFullPath($FSharpWorkspace)
$usesDefaultFSharpIndex = [string]::IsNullOrWhiteSpace($FSharpIndexDb)
if (-not $usesDefaultFSharpIndex) {
    $FSharpIndexDb = [IO.Path]::GetFullPath($FSharpIndexDb)
}
if ([string]::IsNullOrWhiteSpace($EvidencePath)) {
    $EvidencePath = Join-Path $repoRoot "artifacts\external-integration\last-results.json"
}
$EvidencePath = [IO.Path]::GetFullPath($EvidencePath)

function Invoke-Git([string]$WorkingDirectory, [string[]]$Arguments) {
    $output = @(& git -C $WorkingDirectory @Arguments)
    if ($LASTEXITCODE -ne 0) {
        throw "git -C '$WorkingDirectory' $($Arguments -join ' ') failed"
    }
    return $output
}

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Assert-Equal($Expected, $Actual, [string]$Message) {
    if ($Expected -ne $Actual) {
        throw "$Message (expected '$Expected', actual '$Actual')"
    }
}

function Assert-Contains([object[]]$Values, $Expected, [string]$Message) {
    if (-not (@($Values) -contains $Expected)) {
        throw "$Message (missing '$Expected'; actual '$(@($Values) -join ', ')')"
    }
}

function Get-SqliteArtifactPaths([string]$DatabasePath) {
    return @(
        $DatabasePath,
        "$DatabasePath-wal",
        "$DatabasePath-shm",
        "$DatabasePath-journal"
    )
}

function Assert-VerifiedDirectoryChain([string]$AnchorPath, [string]$TargetPath,
    [string]$Label, [bool]$CreateMissing = $false) {
    $anchorFull = [IO.Path]::GetFullPath($AnchorPath).TrimEnd(
        [IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    $targetFull = [IO.Path]::GetFullPath($TargetPath).TrimEnd(
        [IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    $comparison = if ([Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT) {
        [StringComparison]::OrdinalIgnoreCase
    } else {
        [StringComparison]::Ordinal
    }
    $separator = [string][IO.Path]::DirectorySeparatorChar
    Assert-True ($targetFull.Equals($anchorFull, $comparison) -or
        $targetFull.StartsWith($anchorFull + $separator, $comparison)) `
        "$Label escapes its trusted anchor: $targetFull"

    $anchorItem = Get-Item -LiteralPath $anchorFull -Force
    Assert-True ($anchorItem.PSIsContainer -and
        ($anchorItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) `
        "$Label anchor is not a plain directory: $anchorFull"

    $cursor = $anchorFull
    $relative = $targetFull.Substring($anchorFull.Length).TrimStart(
        [IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    foreach ($segment in @($relative -split '[\\/]+' | Where-Object {
        -not [string]::IsNullOrWhiteSpace($_) -and $_ -ne '.'
    })) {
        $cursor = Join-Path $cursor $segment
        if ($CreateMissing -and -not (Test-Path -LiteralPath $cursor)) {
            [IO.Directory]::CreateDirectory($cursor) | Out-Null
        }
        Assert-True (Test-Path -LiteralPath $cursor -PathType Container) `
            "$Label directory is missing: $cursor"
        $item = Get-Item -LiteralPath $cursor -Force
        Assert-True ($item.PSIsContainer -and
            ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) `
            "$Label directory is a reparse point: $cursor"
    }
}

function New-FreshIndexRun([string]$AnchorPath, [string]$IntegrationRoot,
    [string]$LeasePath) {
    Assert-VerifiedDirectoryChain $AnchorPath $IntegrationRoot `
        "External integration root" $true
    if (Test-Path -LiteralPath $LeasePath) {
        $leaseItem = Get-Item -LiteralPath $LeasePath -Force
        Assert-True (-not $leaseItem.PSIsContainer -and
            ($leaseItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) `
            "External integration lease is not a plain file: $LeasePath"
    }

    $lease = $null
    $runRoot = $null
    try {
        try {
            $lease = [IO.File]::Open($LeasePath, [IO.FileMode]::OpenOrCreate,
                [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
        } catch {
            if (Test-IsFileContentionException $_.Exception) {
                throw "Another external MCP integration gate owns the fresh-index lease: $LeasePath"
            }
            throw
        }
        $runId = [Guid]::NewGuid().ToString("N")
        $runRoot = Join-Path $IntegrationRoot "fresh-index-$runId"
        [IO.Directory]::CreateDirectory($runRoot) | Out-Null
        Assert-VerifiedDirectoryChain $AnchorPath $runRoot "Fresh integration run"
        return [pscustomobject]@{
            RunId = $runId
            Root = [IO.Path]::GetFullPath($runRoot)
            Lease = $lease
            LeasePath = [IO.Path]::GetFullPath($LeasePath)
        }
    } catch {
        if ($null -ne $runRoot -and (Test-Path -LiteralPath $runRoot -PathType Container)) {
            try {
                Assert-VerifiedDirectoryChain $AnchorPath $runRoot "Failed fresh integration run"
                [IO.Directory]::Delete($runRoot, $false)
            } catch { }
        }
        if ($null -ne $lease) { $lease.Dispose() }
        throw
    }
}

function New-IsolatedPackagesRoot([string]$AnchorPath, [string]$IntegrationRoot,
    [bool]$FailAfterCreateForTest = $false) {
    Assert-VerifiedDirectoryChain $AnchorPath $IntegrationRoot `
        "External integration root" $true
    $root = Join-Path $IntegrationRoot `
        ("fresh-packages-" + [Guid]::NewGuid().ToString("N"))
    try {
        [IO.Directory]::CreateDirectory($root) | Out-Null
        if ($FailAfterCreateForTest) {
            throw "test-only isolated package root verification failure"
        }
        Assert-VerifiedDirectoryChain $AnchorPath $root "Isolated integration package root"
        $entries = @(Get-ChildItem -LiteralPath $root -Force)
        Assert-Equal 0 $entries.Count "Isolated integration package root was not created empty"
        return [IO.Path]::GetFullPath($root)
    } catch {
        if (Test-Path -LiteralPath $root -PathType Container) {
            try {
                Assert-VerifiedDirectoryChain $AnchorPath $root `
                    "Failed isolated integration package root"
                [IO.Directory]::Delete($root, $false)
            } catch { }
        }
        throw
    }
}

function Remove-IsolatedPackagesRoot([string]$AnchorPath, [string]$PackagesRoot) {
    Assert-VerifiedDirectoryChain $AnchorPath $PackagesRoot `
        "Isolated integration package cleanup"
    $entries = @(Get-ChildItem -LiteralPath $PackagesRoot -Force)
    Assert-Equal 0 $entries.Count `
        "Isolated integration package root was populated during the no-restore gate"
    [IO.Directory]::Delete($PackagesRoot, $false)
}

function Assert-PinnedNet472ReferenceFixture([string]$AnchorPath, [string]$FixtureRoot,
    [string]$Package, [string]$Version, [string]$Framework) {
    Assert-VerifiedDirectoryChain $AnchorPath $FixtureRoot `
        "Pinned net472 reference fixture"
    $manifestPath = Join-Path $FixtureRoot "Phoenix.ReferenceAssemblies.manifest"
    Assert-True (Test-Path -LiteralPath $manifestPath -PathType Leaf) `
        "Pinned net472 reference fixture manifest is missing: $manifestPath"
    $manifestItem = Get-Item -LiteralPath $manifestPath -Force
    Assert-True (-not $manifestItem.PSIsContainer -and
        ($manifestItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) `
        "Pinned net472 reference fixture manifest is not a plain file: $manifestPath"
    $expectedManifest = "package=$Package|version=$Version|framework=$Framework"
    Assert-Equal $expectedManifest ((Get-Content -Raw -LiteralPath $manifestPath).Trim()) `
        "Pinned net472 reference fixture identity changed"
    foreach ($required in @("mscorlib.dll", "System.dll", "System.Core.dll")) {
        $requiredPath = Join-Path $FixtureRoot $required
        Assert-True (Test-Path -LiteralPath $requiredPath -PathType Leaf) `
            "Pinned net472 reference fixture is missing $required"
        $requiredItem = Get-Item -LiteralPath $requiredPath -Force
        Assert-True (-not $requiredItem.PSIsContainer -and
            ($requiredItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) `
            "Pinned net472 reference fixture contains a linked required assembly: $requiredPath"
    }
}

function Assert-SemanticInputAuthority($Payload, [string]$Label) {
    Assert-True ([bool]$Payload.coverage.frameworkRefsAvailable) `
        "$Label did not use the pinned net472 framework references"
    Assert-True ($null -ne $Payload.coverage.PSObject.Properties["frameworkRefsSource"]) `
        "$Label omitted frameworkRefsSource compiler-input evidence"
    Assert-Equal $script:pinnedNet472ReferenceRoot `
        ([IO.Path]::GetFullPath([string]$Payload.coverage.frameworkRefsSource)) `
        "$Label used a framework-reference source other than the pinned fixture"
    Assert-True ($null -ne $Payload.coverage.PSObject.Properties["resolvedPackageDllCount"]) `
        "$Label omitted resolvedPackageDllCount compiler-input evidence"
    Assert-Equal $script:expectedResolvedPackageDllCount `
        ([int]$Payload.coverage.resolvedPackageDllCount) `
        "$Label resolved package-DLL input count changed"
}

function Assert-CapabilitySemanticInputAuthority($Semantic, [string]$Label) {
    Assert-True ([bool]$Semantic.frameworkRefsAvailable) `
        "$Label capabilities did not use the pinned net472 framework references"
    Assert-True ($null -ne $Semantic.PSObject.Properties["frameworkRefsSource"]) `
        "$Label capabilities omitted frameworkRefsSource compiler-input evidence"
    Assert-Equal $script:pinnedNet472ReferenceRoot `
        ([IO.Path]::GetFullPath([string]$Semantic.frameworkRefsSource)) `
        "$Label capabilities used a framework-reference source other than the pinned fixture"
}

function Remove-FreshIndexRun([string]$AnchorPath, [string]$RunRoot,
    [string[]]$DatabasePaths) {
    Assert-VerifiedDirectoryChain $AnchorPath $RunRoot "Fresh integration cleanup"
    $rootFull = [IO.Path]::GetFullPath($RunRoot)
    foreach ($databasePath in $DatabasePaths) {
        $databaseFull = [IO.Path]::GetFullPath($databasePath)
        Assert-True ([IO.Path]::GetFullPath((Split-Path -Parent $databaseFull)) -eq $rootFull) `
            "Fresh integration database is outside its run directory: $databaseFull"
        foreach ($candidate in Get-SqliteArtifactPaths $databaseFull) {
            Assert-VerifiedDirectoryChain $AnchorPath $RunRoot "Fresh integration cleanup"
            if (-not (Test-Path -LiteralPath $candidate)) { continue }
            $item = Get-Item -LiteralPath $candidate -Force
            Assert-True (-not $item.PSIsContainer -and
                ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) `
                "Fresh integration cleanup refused a non-plain file: $candidate"
            Remove-Item -LiteralPath $candidate -Force
        }
    }

    Assert-VerifiedDirectoryChain $AnchorPath $RunRoot "Fresh integration cleanup"
    $remaining = @(Get-ChildItem -LiteralPath $RunRoot -Force)
    Assert-Equal 0 $remaining.Count `
        "Fresh integration cleanup found unexpected entries in $RunRoot"
    [IO.Directory]::Delete($RunRoot, $false)
}

function Assert-FriendRelationshipAuthority($Payload, [string]$Label,
    [bool]$RequireCoverage = $true, [object[]]$ExpectedUnprovenProjects = @()) {
    $expectedConfidence = if ($null -ne $baseline.target.PSObject.Properties["friendRelationshipConfidence"]) {
        [string]$baseline.target.friendRelationshipConfidence
    } else {
        "exact"
    }
    $expectedPartialReason = if ($null -ne $baseline.target.PSObject.Properties["friendRelationshipPartialReason"]) {
        [string]$baseline.target.friendRelationshipPartialReason
    } else {
        ""
    }

    Assert-Equal $expectedConfidence ([string]$Payload.meta.confidence) "$Label confidence changed"
    Assert-Equal "semantic" ([string]$Payload.meta.navigationLayer) "$Label lost compiler-semantic provenance"
    Assert-Equal ([string]$baseline.target.documentationCommentId) `
        ([string]$Payload.symbol.documentationCommentId) "$Label resolved a different compiler symbol"
    if ($RequireCoverage) {
        Assert-True ($ExpectedUnprovenProjects.Count -gt 0) `
            "$Label omitted its tool-specific expected friend-assembly coverage baseline"
        Assert-True ($null -ne $Payload.PSObject.Properties["coverage"] -and
            $null -ne $Payload.coverage) `
            "$Label omitted required friend-assembly coverage"
        Assert-True ($null -ne $Payload.coverage.PSObject.Properties["unprovenFriendAssemblyProjects"]) `
            "$Label omitted required unproven friend-assembly projects"
        $expectedUnprovenProjects = @($ExpectedUnprovenProjects |
            ForEach-Object { [string]$_ } | Sort-Object)
        $actualUnprovenProjects = @($Payload.coverage.unprovenFriendAssemblyProjects |
            ForEach-Object { [string]$_ } | Sort-Object)
        Assert-Equal ($expectedUnprovenProjects -join "|") ($actualUnprovenProjects -join "|") `
            "$Label unproven friend-assembly coverage changed"
    }
    $actualPartialReason = if ($null -ne $Payload.PSObject.Properties["partialReason"]) {
        [string]$Payload.partialReason
    } else {
        ""
    }
    if ([string]::IsNullOrWhiteSpace($expectedPartialReason)) {
        Assert-True ([string]::IsNullOrWhiteSpace($actualPartialReason)) "$Label unexpectedly became partial: $actualPartialReason"
    } else {
        $isPartial = $null -ne $Payload.PSObject.Properties["partial"] -and [bool]$Payload.partial
        Assert-True $isPartial "$Label omitted partial=true for unproven friend authority"
        Assert-Equal $expectedPartialReason $actualPartialReason "$Label partial reason changed"
    }
}

function Get-TypeResultName($Item) {
    if ($null -ne $Item.PSObject.Properties["name"] -and
        -not [string]::IsNullOrWhiteSpace([string]$Item.name)) {
        return [string]$Item.name
    }
    $display = if ($null -ne $Item.PSObject.Properties["symbol"] -and
                   $null -ne $Item.symbol -and
                   $null -ne $Item.symbol.PSObject.Properties["display"]) {
        [string]$Item.symbol.display
    } elseif ($null -ne $Item.PSObject.Properties["display"]) {
        [string]$Item.display
    } else {
        ""
    }
    $separator = $display.LastIndexOf('.')
    if ($separator -ge 0) { return $display.Substring($separator + 1) }
    return $display
}

function Get-ReferenceContractSignature($Payload, [switch]$IgnoreResidentSolutionProjects) {
    $kinds = @($Payload.kinds.PSObject.Properties | Sort-Object Name | ForEach-Object {
        "$([string]$_.Name)|$([int]$_.Value)"
    })
    $groups = @($Payload.groups | ForEach-Object {
        $samples = @($_.samples | ForEach-Object {
            "$([string]$_.path)|$([int]$_.line)|$([string]$_.kind)"
        })
        "$([string]$_.project)|$([bool]$_.isTest)|$([int]$_.count)|$($samples -join ';')"
    } | Sort-Object)
    $partialReason = if ($null -ne $Payload.PSObject.Properties["partialReason"]) {
        [string]$Payload.partialReason
    } else { "" }
    $coverage = [ordered]@{
        loaded = [int]$Payload.coverage.loadedProjects
        requested = [int]$Payload.coverage.requestedProjects
    }
    if (-not $IgnoreResidentSolutionProjects) {
        $coverage["solution"] = [int]$Payload.coverage.solutionProjects
    }
    return ([ordered]@{
        symbol = [string]$Payload.symbol.documentationCommentId
        total = [int]$Payload.totalReferences
        partial = [bool]$Payload.partial
        partialReason = $partialReason
        truncated = [bool]$Payload.truncated
        kinds = $kinds
        groups = $groups
        coverage = $coverage
    } | ConvertTo-Json -Compress -Depth 20)
}

function Start-McpClient([string]$Label, [string]$WorkspaceRoot, [string]$DatabasePath) {
    $mcpDll = Join-Path $repoRoot "src\CodeNav.Mcp\bin\Release\net10.0\PhoenixCodeNav.Mcp.dll"
    if (-not (Test-Path -LiteralPath $mcpDll -PathType Leaf)) {
        throw "Release MCP binary is missing. Run: dotnet build PhoenixCodeNav.sln -c Release --no-restore"
    }

    $start = New-Object System.Diagnostics.ProcessStartInfo
    $start.FileName = "dotnet"
    $start.Arguments = "$(Quote-ProcessArgument $mcpDll) --workspace-root $(Quote-ProcessArgument $WorkspaceRoot) --index-db $(Quote-ProcessArgument $DatabasePath) --daemon-idle-ms 300"
    $start.WorkingDirectory = $repoRoot
    $start.UseShellExecute = $false
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.CreateNoWindow = $true
    $start.EnvironmentVariables["PHOENIX_TELEMETRY_IPC"] = "0"
    Assert-True (-not [string]::IsNullOrWhiteSpace($script:isolatedPackagesRoot)) `
        "Isolated integration package root is unavailable"
    $start.EnvironmentVariables["NUGET_PACKAGES"] = $script:isolatedPackagesRoot
    $start.EnvironmentVariables["CODENAV_NET472_REFS"] = `
        $script:pinnedNet472ReferenceRoot

    $telemetryDirectory = Join-Path $WorkspaceRoot ".codenav\telemetry"
    $telemetryFilesBefore = @(
        Get-ChildItem -LiteralPath $telemetryDirectory -Filter "phoenix-*.jsonl" -File `
            -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName }
    )
    $telemetryLineCountsBefore = @{}
    foreach ($file in $telemetryFilesBefore) {
        $telemetryLineCountsBefore[$file] = @(Get-Content -LiteralPath $file `
                -ErrorAction SilentlyContinue).Count
    }
    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $start
    if (-not $process.Start()) { throw "Failed to start MCP $Label process" }
    $stderrTail = [PhoenixCodeNav.Integration.BoundedTextTail]::new(65536)
    return [pscustomobject]@{
        Label = $Label
        WorkspaceRoot = $WorkspaceRoot
        DatabasePath = $DatabasePath
        TelemetryFilesBefore = $telemetryFilesBefore
        TelemetryLineCountsBefore = $telemetryLineCountsBefore
        Process = $process
        NextId = 0
        StderrTail = $stderrTail
        StderrTask = $stderrTail.DrainAsync($process.StandardError)
        AllowNonZeroExit = $false
        RuntimeProcessId = $null
    }
}

function Get-ReferenceTelemetryRecords($Client) {
    $telemetryDirectory = Join-Path $Client.WorkspaceRoot ".codenav\telemetry"
    $producerId = if ($null -ne $Client.RuntimeProcessId) {
        [int]$Client.RuntimeProcessId
    } else {
        [int]$Client.Process.Id
    }
    $pattern = "phoenix-$producerId-*.jsonl"
    $records = @()
    $files = @(Get-ChildItem -LiteralPath $telemetryDirectory -Filter $pattern -File `
            -ErrorAction SilentlyContinue | Sort-Object Name)
    foreach ($file in $files) {
        $skip = if ($Client.TelemetryLineCountsBefore.ContainsKey($file.FullName)) {
            [int]$Client.TelemetryLineCountsBefore[$file.FullName]
        } else { 0 }
        $newLines = @(Get-Content -LiteralPath $file.FullName -ErrorAction SilentlyContinue |
            Select-Object -Skip $skip)
        foreach ($line in $newLines) {
            try {
                $record = $line | ConvertFrom-Json
                if ([string]$record.tool -eq "references" -and
                    $null -ne $record.queryStages -and
                    $null -ne $record.queryStages.documentScope) {
                    $records += $record
                }
            } catch { }
        }
    }
    return $records
}

function Wait-ReferenceTelemetry($Client, [int]$AfterCount,
    [int]$TimeoutMs = 10000, [string]$ExpectedResult = "exact") {
    $deadline = [DateTime]::UtcNow.AddMilliseconds($TimeoutMs)
    $settledAt = $null
    $lastPostCallCount = -1
    while ([DateTime]::UtcNow -lt $deadline) {
        $records = @(Get-ReferenceTelemetryRecords $Client)
        $postCallCount = [Math]::Max(0, $records.Count - $AfterCount)
        if ($postCallCount -gt 0) {
            if ($postCallCount -ne $lastPostCallCount) {
                $lastPostCallCount = $postCallCount
                $settledAt = [DateTime]::UtcNow.AddMilliseconds(750)
            } elseif ($null -ne $settledAt -and [DateTime]::UtcNow -ge $settledAt) {
                # Retryable cold-load/snapshot attempts need not emit a document-scope record,
                # and telemetry is flushed asynchronously after the accepted MCP call returns.
                # Wait for the post-call record set to settle, then attribute the final record to
                # the accepted invocation rather than racing an earlier retry record.
                $records = @(Get-ReferenceTelemetryRecords $Client)
                $accepted = @($records | Select-Object -Skip $AfterCount |
                    Select-Object -Last 1)[0]
                Assert-Equal $ExpectedResult ([string]$accepted.result) `
                    "$($Client.Label): references telemetry result changed"
                return $accepted
            }
        }
        Start-Sleep -Milliseconds 50
    }
    throw "$($Client.Label): timed out waiting for accepted references telemetry after record $AfterCount"
}

function Send-McpRequest($Client, [string]$Method, $Parameters, [int]$TimeoutMs = 30000) {
    $Client.NextId = [int]$Client.NextId + 1
    $id = [int]$Client.NextId
    $request = @{
        jsonrpc = "2.0"
        id = $id
        method = $Method
        params = $Parameters
    } | ConvertTo-Json -Compress -Depth 30
    $Client.Process.StandardInput.WriteLine($request)
    $Client.Process.StandardInput.Flush()

    $deadline = [DateTime]::UtcNow.AddMilliseconds($TimeoutMs)
    while ([DateTime]::UtcNow -lt $deadline) {
        $remaining = [Math]::Max(1, [int]($deadline - [DateTime]::UtcNow).TotalMilliseconds)
        $read = $Client.Process.StandardOutput.ReadLineAsync()
        if (-not $read.Wait($remaining)) {
            throw "$($Client.Label): timed out waiting for $Method after ${TimeoutMs}ms"
        }
        $line = $read.Result
        if ($null -eq $line) { throw "$($Client.Label): stdout closed while waiting for $Method" }
        $message = $line | ConvertFrom-Json
        if ($message.id -ne $id) { continue }
        if ($null -ne $message.error) {
            throw "$($Client.Label): JSON-RPC error for ${Method}: $($message.error | ConvertTo-Json -Compress -Depth 10)"
        }
        return $message.result
    }
    throw "$($Client.Label): timed out waiting for $Method"
}

function Send-McpNotification($Client, [string]$Method, $Parameters) {
    $request = @{
        jsonrpc = "2.0"
        method = $Method
        params = $Parameters
    } | ConvertTo-Json -Compress -Depth 10
    $Client.Process.StandardInput.WriteLine($request)
    $Client.Process.StandardInput.Flush()
}

function Invoke-McpTool($Client, [string]$Name, [hashtable]$Arguments, [int]$TimeoutMs = 30000) {
    $result = Send-McpRequest $Client "tools/call" @{ name = $Name; arguments = $Arguments } $TimeoutMs
    Assert-True (@($result.content).Count -gt 0) "$($Client.Label): $Name returned no MCP content"
    return $result.content[0].text | ConvertFrom-Json
}

function Initialize-McpClient($Client, [string]$ExpectedMode) {
    $initialize = Send-McpRequest $Client "initialize" @{
        protocolVersion = "2025-06-18"
        capabilities = @{}
        clientInfo = @{ name = "phoenix-roslyn-integration"; version = "1" }
    }
    Send-McpNotification $Client "notifications/initialized" @{}
    $tools = Send-McpRequest $Client "tools/list" @{}

    $capabilities = $null
    $stableReadyObservations = 0
    # Every writer startup deliberately runs a detect-all freshness sweep. The frozen Roslyn
    # checkout has 17k C# files, and building its fresh integration index can take several
    # minutes. Keep this integration gate bounded, but size the bound for the repository it
    # intentionally exercises rather than a tiny unit fixture.
    # A writer can briefly report ready before a queued refresh starts, so require readiness to
    # remain stable across two observations one second apart before issuing semantic probes.
    for ($attempt = 0; $attempt -lt 600; $attempt++) {
        $capabilities = Invoke-McpTool $Client "server_capabilities" ([hashtable]::new())
        if ($capabilities.index.state -eq "ready" -and $capabilities.index.mode -eq $ExpectedMode) {
            $stableReadyObservations++
            if ($stableReadyObservations -ge 2) { break }
        } else {
            $stableReadyObservations = 0
        }
        Start-Sleep -Seconds 1
    }
    Assert-Equal "ready" $capabilities.index.state "$($Client.Label): index did not become ready"
    Assert-Equal $ExpectedMode $capabilities.index.mode "$($Client.Label): unexpected index access mode"
    Assert-Equal 2 $stableReadyObservations "$($Client.Label): index readiness did not remain stable"
    Assert-Equal "daemon" $capabilities.runtime.indexMode "$($Client.Label): ordinary launch did not use the shared daemon"
    $Client.RuntimeProcessId = [int]$capabilities.runtime.processId
    return [pscustomobject]@{
        Initialize = $initialize
        Tools = $tools
        Capabilities = $capabilities
    }
}

function Request-McpDaemonRetirement($Client) {
    if ($null -eq $Client) { return }
    $start = New-Object System.Diagnostics.ProcessStartInfo
    $start.FileName = "dotnet"
    $start.Arguments = "$(Quote-ProcessArgument $script:mcpDllPath) --workspace-root $(Quote-ProcessArgument $Client.WorkspaceRoot) --index-db $(Quote-ProcessArgument $Client.DatabasePath) --daemon-retire-authorized"
    $start.WorkingDirectory = $repoRoot
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.CreateNoWindow = $true
    $start.EnvironmentVariables["NUGET_PACKAGES"] = $script:isolatedPackagesRoot
    $start.EnvironmentVariables["CODENAV_NET472_REFS"] = `
        $script:pinnedNet472ReferenceRoot
    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $start
    if (-not $process.Start()) {
        throw "$($Client.Label): failed to start authority-checked daemon retirement"
    }
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    try {
        if (-not $process.WaitForExit(130000)) {
            try { $process.Kill() } catch { }
            throw "$($Client.Label): authority-checked daemon retirement exceeded its bound"
        }
        if (-not $stdout.Wait(3000) -or -not $stderr.Wait(3000)) {
            throw "$($Client.Label): daemon-retirement output drain did not complete"
        }
        if ($process.ExitCode -ne 0) {
            $detail = ([string]$stderr.Result).Trim()
            throw "$($Client.Label): authority-checked daemon retirement exited $($process.ExitCode): $detail"
        }
    } finally {
        $process.Dispose()
    }
}

function Test-RetryableSemanticPayload($Payload) {
    $reason = [string]$Payload.reason
    $partialReason = [string]$Payload.partialReason
    return $reason -match "cluster_cold_load|index_snapshot_unavailable" -or
           $partialReason -match "cluster_cold_load|index_snapshot_unavailable"
}

function Invoke-SemanticWithRetryCore($Client, [string]$Name, [hashtable]$Arguments,
    [ref]$AttemptCount) {
    for ($attempt = 0; $attempt -lt 4; $attempt++) {
        $AttemptCount.Value = $attempt + 1
        $payload = Invoke-McpTool $Client $Name $Arguments 120000
        if (-not (Test-RetryableSemanticPayload $payload)) { return $payload }
        Start-Sleep -Milliseconds 500
    }
    return $payload
}

function Invoke-SemanticWithRetry($Client, [string]$Name, [hashtable]$Arguments) {
    $attemptCount = 0
    return Invoke-SemanticWithRetryCore $Client $Name $Arguments ([ref]$attemptCount)
}

if ($SelfTestSemanticRetryContract) {
    $indexedFallback = [pscustomobject]@{
        error = $null
        partialReason = "index_snapshot_unavailable"
        meta = [pscustomobject]@{ confidence = "indexed" }
    }
    $coldError = [pscustomobject]@{
        error = "semantic_unavailable"
        reason = "cluster_cold_load: retry"
    }
    $stableIndexed = [pscustomobject]@{
        error = $null
        partialReason = "project_model_unproven"
        meta = [pscustomobject]@{ confidence = "indexed" }
    }
    Assert-True (Test-RetryableSemanticPayload $indexedFallback) "Indexed auto fallback was not classified as transient"
    Assert-True (Test-RetryableSemanticPayload $coldError) "Semantic-unavailable cold load was not classified as transient"
    Assert-True (-not (Test-RetryableSemanticPayload $stableIndexed)) "Stable indexed partiality was misclassified as transient"
    $missingCoverage = [pscustomobject]@{
        meta = [pscustomobject]@{
            confidence = [string]$baseline.target.friendRelationshipConfidence
            navigationLayer = "semantic"
        }
        symbol = [pscustomobject]@{
            documentationCommentId = [string]$baseline.target.documentationCommentId
        }
        partial = $true
        partialReason = [string]$baseline.target.friendRelationshipPartialReason
    }
    $missingCoverageRejected = $false
    try {
        Assert-FriendRelationshipAuthority $missingCoverage "self-test missing coverage" `
            -ExpectedUnprovenProjects @($baseline.target.referencesUnprovenFriendAssemblyProjects)
    } catch {
        $missingCoverageRejected = $_.Exception.Message -match "omitted required friend-assembly coverage"
    }
    Assert-True $missingCoverageRejected "Friend-authority coverage omission was not rejected"
    Assert-FriendRelationshipAuthority $missingCoverage "self-test optional coverage" $false
    Write-Host "Semantic retry contract self-test passed"
    exit 0
}

if ($SelfTestFreshIndexLifecycleContract) {
    $selfTestRoot = Join-Path ([IO.Path]::GetTempPath()) `
        ("PhoenixCodeNav-fresh-index-selftest-" + [Guid]::NewGuid().ToString("N"))
    [IO.Directory]::CreateDirectory($selfTestRoot) | Out-Null
    $anchor = Join-Path $selfTestRoot "anchor"
    $outside = Join-Path $selfTestRoot "outside"
    [IO.Directory]::CreateDirectory($anchor) | Out-Null
    [IO.Directory]::CreateDirectory($outside) | Out-Null
    $outsideMarker = Join-Path $outside "outside-marker.txt"
    [IO.File]::WriteAllText($outsideMarker, "outside")
    $linkedRoot = Join-Path $anchor "linked-integration"
    if ([Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT) {
        New-Item -ItemType Junction -Path $linkedRoot -Target $outside | Out-Null
    } else {
        [IO.Directory]::CreateSymbolicLink($linkedRoot, $outside) | Out-Null
    }
    $linkRejected = $false
    try {
        Assert-VerifiedDirectoryChain $anchor $linkedRoot "Linked self-test root"
    } catch {
        $linkRejected = $_.Exception.Message -match "reparse point"
    }
    Assert-True $linkRejected "Fresh-index containment accepted a linked ancestor"
    Assert-True (Test-Path -LiteralPath $outsideMarker -PathType Leaf) `
        "Fresh-index containment modified the outside marker"
    [IO.Directory]::Delete($linkedRoot, $false)

    $integrationRoot = Join-Path $anchor "artifacts\external-integration"
    $leasePath = Join-Path $integrationRoot ".fresh-index-gate.lock"
    $run = New-FreshIndexRun $anchor $integrationRoot $leasePath
    $probeStart = New-Object Diagnostics.ProcessStartInfo
    # Reuse the exact host executable. `Get-Command pwsh` can legitimately return both the
    # Homebrew cellar binary and its /opt/homebrew/bin link; stringifying that array produces one
    # invalid, space-separated FileName.
    $probeStart.FileName = (Get-Process -Id $PID).Path
    $probeStart.Arguments = "-NoProfile -ExecutionPolicy Bypass -File $(Quote-ProcessArgument $PSCommandPath) -SelfTestFreshIndexLeaseProbe -SelfTestLeasePath $(Quote-ProcessArgument $leasePath)"
    $probeStart.UseShellExecute = $false
    $probeStart.CreateNoWindow = $true
    $probeStart.RedirectStandardOutput = $true
    $probeStart.RedirectStandardError = $true
    $probeProcess = [Diagnostics.Process]::Start($probeStart)
    $probeStdout = $probeProcess.StandardOutput.ReadToEndAsync()
    $probeStderr = $probeProcess.StandardError.ReadToEndAsync()
    $probeProcess.WaitForExit()
    $probeStdout.Wait()
    $probeStderr.Wait()
    Assert-Equal 23 $probeProcess.ExitCode `
        "Fresh-index lease allowed a second process to acquire ownership; stdout=$([string]$probeStdout.Result); stderr=$([string]$probeStderr.Result)"
    Assert-True ([string]$probeStderr.Result -match "LEASE_PROBE_REJECTED") `
        "Fresh-index lease probe did not report the ownership collision"
    $probeProcess.Dispose()

    $roslynDb = Join-Path $run.Root "roslyn-index.db"
    $fsharpDb = Join-Path $run.Root "fsharp-index.db"
    foreach ($artifact in @($roslynDb, "$roslynDb-wal", $fsharpDb, "$fsharpDb-shm")) {
        [IO.File]::WriteAllText($artifact, "self-test")
    }
    $unexpected = Join-Path $run.Root "unexpected.txt"
    [IO.File]::WriteAllText($unexpected, "must survive exact cleanup")
    $unexpectedRejected = $false
    try {
        Remove-FreshIndexRun $anchor $run.Root @($roslynDb, $fsharpDb)
    } catch {
        $unexpectedRejected = $_.Exception.Message -match "unexpected entries"
    }
    Assert-True $unexpectedRejected "Fresh-index cleanup silently removed an unknown entry"
    Assert-True (Test-Path -LiteralPath $unexpected -PathType Leaf) `
        "Fresh-index cleanup recursively deleted an unknown entry"
    Remove-Item -LiteralPath $unexpected -Force
    Remove-FreshIndexRun $anchor $run.Root @($roslynDb, $fsharpDb)
    $packageFailureObserved = $false
    try {
        New-IsolatedPackagesRoot $anchor $integrationRoot $true | Out-Null
    } catch {
        $packageFailureObserved = $_.Exception.Message -match `
            "test-only isolated package root verification failure"
    }
    Assert-True $packageFailureObserved `
        "Isolated package-root post-create failure was not observed"
    Assert-Equal 0 (@(Get-ChildItem -LiteralPath $integrationRoot -Force | Where-Object {
        $_.Name -like "fresh-packages-*"
    }).Count) "Failed isolated package-root verification leaked its directory"
    $run.Lease.Dispose()
    Remove-Item -LiteralPath $leasePath -Force
    [IO.Directory]::Delete($integrationRoot, $false)
    [IO.Directory]::Delete((Split-Path -Parent $integrationRoot), $false)
    Remove-Item -LiteralPath $outsideMarker -Force
    [IO.Directory]::Delete($outside, $false)
    [IO.Directory]::Delete($anchor, $false)
    [IO.Directory]::Delete($selfTestRoot, $false)
    Write-Host "Fresh-index lifecycle contract self-test passed"
    exit 0
}

if ($SelfTestPinnedFrameworkReferenceContract) {
    Assert-PinnedNet472ReferenceFixture $repoRoot $pinnedNet472ReferenceRoot `
        $pinnedNet472Package $pinnedNet472Version $pinnedNet472Framework
    $missingRejected = $false
    try {
        Assert-PinnedNet472ReferenceFixture $repoRoot `
            (Join-Path $pinnedNet472ReferenceRoot "missing") `
            $pinnedNet472Package $pinnedNet472Version $pinnedNet472Framework
    } catch {
        $missingRejected = $_.Exception.Message -match "directory is missing"
    }
    Assert-True $missingRejected "Missing pinned framework fixture was not rejected"
    Write-Host "Pinned framework-reference contract self-test passed"
    exit 0
}

$mcpDllPath = Join-Path $repoRoot "src\CodeNav.Mcp\bin\Release\net10.0\PhoenixCodeNav.Mcp.dll"
Assert-True (Test-Path -LiteralPath $mcpDllPath -PathType Leaf) "Release MCP binary is missing. Run: dotnet build PhoenixCodeNav.sln -c Release --no-restore"
Assert-Equal $pinnedNet472Package `
    ([string]$fsharpBaseline.semanticInputs.net472ReferencePackage) `
    "Roslyn and FSharp baselines disagree on the pinned net472 reference package"
Assert-Equal $pinnedNet472Version `
    ([string]$fsharpBaseline.semanticInputs.net472ReferencePackageVersion) `
    "Roslyn and FSharp baselines disagree on the pinned net472 reference package version"
Assert-Equal $pinnedNet472Framework `
    ([string]$fsharpBaseline.semanticInputs.net472Framework) `
    "Roslyn and FSharp baselines disagree on the pinned net472 framework"
Assert-Equal $expectedResolvedPackageDllCount `
    ([int]$fsharpBaseline.semanticInputs.resolvedPackageDllCount) `
    "Roslyn and FSharp baselines disagree on the resolved package-DLL count"
Assert-PinnedNet472ReferenceFixture $repoRoot $pinnedNet472ReferenceRoot `
    $pinnedNet472Package $pinnedNet472Version $pinnedNet472Framework
Assert-True (Test-Path -LiteralPath $Workspace -PathType Container) "Frozen Roslyn workspace is missing: $Workspace"
$roslynGitlink = [string](@(Invoke-Git $repoRoot @("rev-parse", "HEAD:external/roslyn"))[0])
$roslynHead = [string](@(Invoke-Git $Workspace @("rev-parse", "HEAD"))[0])
Assert-Equal ([string]$baseline.roslynCommit) $roslynGitlink "Roslyn gitlink differs from the locked integration baseline"
Assert-Equal $roslynGitlink $roslynHead "Roslyn checkout HEAD differs from the pinned submodule gitlink"
$unexpectedStatus = @(Invoke-Git $Workspace @("--no-optional-locks", "status", "--porcelain=v1", "--untracked-files=all") |
    Where-Object { $_ -and $_ -notmatch '^\?\? \.codenav/' })
Assert-Equal 0 $unexpectedStatus.Count "Frozen Roslyn workspace contains changes outside .codenav"
Assert-True (Test-Path -LiteralPath $FSharpWorkspace -PathType Container) "Frozen FSharp workspace is missing: $FSharpWorkspace"
$fsharpGitlink = [string](@(Invoke-Git $repoRoot @("rev-parse", "HEAD:external/fsharp"))[0])
$fsharpHead = [string](@(Invoke-Git $FSharpWorkspace @("rev-parse", "HEAD"))[0])
Assert-Equal ([string]$fsharpBaseline.fsharpCommit) $fsharpGitlink "FSharp gitlink differs from the locked integration baseline"
Assert-Equal $fsharpGitlink $fsharpHead "FSharp checkout HEAD differs from the pinned submodule gitlink"
$unexpectedFSharpStatus = @(Invoke-Git $FSharpWorkspace @("--no-optional-locks", "status", "--porcelain=v1", "--untracked-files=all") |
    Where-Object { $_ -and $_ -notmatch '^\?\? \.codenav/' })
Assert-Equal 0 $unexpectedFSharpStatus.Count "Frozen FSharp workspace contains changes outside .codenav"
Assert-True (Test-Path -LiteralPath (Join-Path $FSharpWorkspace ([string]$fsharpBaseline.target.sourcePath)) -PathType Leaf) "Frozen FSharp checkout is missing the source probe"
Assert-True (Test-Path -LiteralPath (Join-Path $FSharpWorkspace ([string]$fsharpBaseline.target.projectPath)) -PathType Leaf) "Frozen FSharp checkout is missing the project probe"

function Initialize-FreshIndexPath([string]$Label, [string]$DatabasePath) {
    foreach ($candidate in Get-SqliteArtifactPaths $DatabasePath) {
        Assert-True (-not (Test-Path -LiteralPath $candidate)) `
            "$Label integration index path is not fresh: $candidate"
    }
    [IO.Directory]::CreateDirectory((Split-Path -Parent $DatabasePath)) | Out-Null
}

$defaultDatabasePaths = New-Object System.Collections.Generic.List[string]
try {
    if ($usesDefaultRoslynIndex -or $usesDefaultFSharpIndex) {
        $freshRun = New-FreshIndexRun $repoRoot $externalIntegrationRoot $freshRunLeasePath
        $freshIndexRoot = [string]$freshRun.Root
        $freshRunLease = $freshRun.Lease
        if ($usesDefaultRoslynIndex) {
            $IndexDb = Join-Path $freshIndexRoot "roslyn-index.db"
            $defaultDatabasePaths.Add($IndexDb)
        }
        if ($usesDefaultFSharpIndex) {
            $FSharpIndexDb = Join-Path $freshIndexRoot "fsharp-index.db"
            $defaultDatabasePaths.Add($FSharpIndexDb)
        }
    }
    $IndexDb = [IO.Path]::GetFullPath($IndexDb)
    $FSharpIndexDb = [IO.Path]::GetFullPath($FSharpIndexDb)

    Initialize-FreshIndexPath "Roslyn" $IndexDb
    Initialize-FreshIndexPath "FSharp" $FSharpIndexDb
    $isolatedPackagesRoot = New-IsolatedPackagesRoot $repoRoot $externalIntegrationRoot
} catch {
    $setupFailure = $_.Exception
    if ($null -ne $isolatedPackagesRoot -and
        (Test-Path -LiteralPath $isolatedPackagesRoot -PathType Container)) {
        try { Remove-IsolatedPackagesRoot $repoRoot $isolatedPackagesRoot } catch { }
    }
    if ($null -ne $freshIndexRoot -and
        (Test-Path -LiteralPath $freshIndexRoot -PathType Container)) {
        try {
            Remove-FreshIndexRun $repoRoot $freshIndexRoot @($defaultDatabasePaths)
        } catch {
            Write-Warning "Fresh-index setup cleanup also failed: $($_.Exception.Message)"
        }
    }
    if ($null -ne $freshRunLease) { $freshRunLease.Dispose() }
    throw $setupFailure
}
Write-Host "[SETUP] Roslyn pinned checkout will build a fresh integration index at $IndexDb"
Write-Host "[SETUP] FSharp pinned checkout will build a fresh integration index at $FSharpIndexDb"

function Invoke-ReferencesWithTelemetry($Client, [hashtable]$Arguments,
    [string]$ExpectedResult) {
    $afterCount = @(Get-ReferenceTelemetryRecords $Client).Count
    $attemptCount = 0
    $payload = Invoke-SemanticWithRetryCore $Client "references" $Arguments `
        ([ref]$attemptCount)
    $telemetry = Wait-ReferenceTelemetry $Client $afterCount `
        -ExpectedResult $ExpectedResult
    return [pscustomobject]@{
        Payload = $payload
        Telemetry = $telemetry
        Attempts = $attemptCount
    }
}

function Get-RoslynOverviewCounts($Overview) {
    return [ordered]@{
        projects = [int]$Overview.projects.total
        solutions = [int]$Overview.solutions
        csharpFiles = [int]$Overview.csFiles
        fsharpFiles = [int]$Overview.fsFiles
        fsharpProjects = [int]$Overview.projects.fsharp
        symbols = [int]$Overview.symbols
        orphanedFiles = [int]$Overview.orphanedFiles
    }
}

function Get-FSharpOverviewCounts($Overview) {
    return [ordered]@{
        projects = [int]$Overview.projects.total
        fsharpProjects = [int]$Overview.projects.fsharp
        csharpFiles = [int]$Overview.csFiles
        fsharpFiles = [int]$Overview.fsFiles
        symbols = [int]$Overview.symbols
        orphanedFiles = [int]$Overview.orphanedFiles
    }
}

$evidence = [ordered]@{
    baseline = $baseline.name
    phoenixBuild = $null
    roslynGitlink = $roslynGitlink
    roslynHead = $roslynHead
    workspace = $Workspace
    indexDb = $IndexDb
    freshIndexRun = if ($null -ne $freshRun) {
        [ordered]@{
            runId = [string]$freshRun.RunId
            root = [string]$freshRun.Root
            leasePath = [string]$freshRun.LeasePath
        }
    } else { $null }
    isolatedPackagesRoot = $isolatedPackagesRoot
    packageResolutionMode = "verified_empty_isolated_global_packages_root"
    frameworkReferences = [ordered]@{
        package = $pinnedNet472Package
        version = $pinnedNet472Version
        framework = $pinnedNet472Framework
        source = $pinnedNet472ReferenceRoot
        expectedAvailable = $true
        resolvedPackageDllCount = $expectedResolvedPackageDllCount
    }
    roslynFreshIndex = $null
    roslynCountsProvenance = [ordered]@{
        id = [string]$baseline.countsProvenance
        detail = [string]$baseline.countsProvenanceDetail
    }
    fsharpBaseline = $fsharpBaseline.name
    fsharpGitlink = $fsharpGitlink
    fsharpHead = $fsharpHead
    fsharpWorkspace = $FSharpWorkspace
    fsharpIndexDb = $FSharpIndexDb
    fsharpFreshIndex = $null
    startedAtUtc = [DateTime]::UtcNow.ToString("O")
    results = [ordered]@{}
}
$failures = New-Object System.Collections.Generic.List[string]
$passed = 0

function Test-IntegrationCase([string]$Name, [scriptblock]$Body) {
    Write-Host "[RUN ] $Name"
    try {
        & $Body
        $script:passed++
        Write-Host "[PASS] $Name" -ForegroundColor Green
    } catch {
        $script:failures.Add("${Name}: $($_.Exception.Message)")
        Write-Host "[FAIL] $Name - $($_.Exception.Message)" -ForegroundColor Red
    }
}

$writer = $null
$secondClient = $null
$fsharpWriter = $null
try {
    $writer = Start-McpClient "writer" $Workspace $IndexDb
    $writerSession = Initialize-McpClient $writer "writer"
    $writerCapabilities = $writerSession.Capabilities
    $overview = Invoke-McpTool $writer "repo_overview" ([hashtable]::new())
    $evidence.roslynFreshIndex = [ordered]@{
        outcome = "fresh"
        startupBuildReason = [string]$writerCapabilities.index.startupBuildReason
        indexVersion = [string]$overview.meta.indexVersion
        counts = Get-RoslynOverviewCounts $overview
    }
    $evidence.phoenixBuild = [ordered]@{
        serverInfo = $writerSession.Initialize.serverInfo
        capabilities = $writerCapabilities.build
    }
    $evidence.results.writerCapabilities = $writerCapabilities

    Test-IntegrationCase "Roslyn pinned checkout builds a fresh index" {
        Assert-True (Test-Path -LiteralPath $IndexDb -PathType Leaf) `
            "Roslyn fresh index was not created: $IndexDb"
        Assert-Equal "fresh" ([string]$evidence.roslynFreshIndex.outcome) `
            "Roslyn integration index was not fresh"
        Assert-Equal "startup_missing" ([string]$evidence.roslynFreshIndex.startupBuildReason) `
            "Roslyn server did not report a database-absent startup build"
    }

    Test-IntegrationCase "current server uses the fresh pinned Roslyn index" {
        Assert-True (-not [string]::IsNullOrWhiteSpace([string]$writerSession.Initialize.serverInfo.version)) "MCP omitted its runtime version"
        Assert-True (@($writerSession.Tools.tools).Count -gt 0) "MCP advertised no tools"
        Assert-Equal ([string]$baseline.roslynCommit) ([string]$overview.git.indexedCommit) "Indexed commit changed"
        Assert-Equal ([string]$overview.meta.indexVersion) ([string]$writerCapabilities.index.indexVersion) "Roslyn capabilities evidence is stale for the judged index epoch"
        Assert-CapabilitySemanticInputAuthority $writerCapabilities.semantic "Roslyn"
    }

    $evidence.results.repoOverview = $overview
    Test-IntegrationCase "repository counts" {
        Assert-Equal ([int]$expectedCounts.projects) ([int]$overview.projects.total) "Project count changed"
        Assert-Equal ([int]$expectedCounts.solutions) ([int]$overview.solutions) "Solution count changed"
        Assert-Equal ([int]$expectedCounts.csFiles) ([int]$overview.csFiles) "C# file count changed"
        Assert-Equal ([int]$expectedCounts.symbols) ([int]$overview.symbols) "Symbol count changed"
        Assert-Equal ([int]$expectedCounts.orphanedFiles) ([int]$overview.orphanedFiles) "Orphaned-file count changed"
        Assert-Equal ([int]$expectedCounts.fsFiles) ([int]$overview.fsFiles) "F# file count changed"
        Assert-Equal ([int]$expectedCounts.fsProjects) ([int]$overview.projects.fsharp) "F# project count changed"
        Assert-True ([bool]$overview.git.headMatchesIndex) "Roslyn HEAD no longer matches the fresh integration index"
    }

    $fileResult = Invoke-McpTool $writer "find_file" @{ nameOrGlob = "ICompilationFactoryService.cs"; limit = 10 }
    $evidence.results.findFile = $fileResult
    Test-IntegrationCase "file discovery" {
        Assert-Contains @($fileResult.files | ForEach-Object { [string]$_.path }) ([string]$baseline.target.path) "Target file was not found"
        Assert-Equal "indexed" ([string]$fileResult.meta.confidence) "find_file confidence changed"
    }

    $markdownFiles = Invoke-McpTool $writer "find_file" @{ nameOrGlob = "README.md"; limit = 20 }
    $markdownSearch = Invoke-McpTool $writer "search_text" @{
        query = "open-source implementation"
        pathGlob = "README.md"
        lang = "md"
        limit = 10
    }
    $evidence.results.markdownTextIndexing = [ordered]@{
        files = $markdownFiles
        search = $markdownSearch
    }
    Test-IntegrationCase "markdown text indexing" {
        $rootReadme = @($markdownFiles.files | Where-Object {
            [string]$_.path -eq "README.md" -and [string]$_.language -eq "md"
        })
        Assert-Equal 1 $rootReadme.Count "Root README.md was not exposed as lang=md"
        Assert-Contains @($markdownSearch.hits | ForEach-Object { [string]$_.path }) "README.md" "Markdown FTS omitted the Roslyn README marker"
        Assert-Equal "indexed" ([string]$markdownSearch.meta.confidence) "Markdown search confidence changed"
    }

    $search = Invoke-McpTool $writer "search_symbol" @{ query = [string]$baseline.target.name; limit = 10 }
    $evidence.results.searchSymbol = $search
    $targetSymbols = @($search.symbols | Where-Object { $_.path -eq [string]$baseline.target.path -and $_.arity -eq [int]$baseline.target.arity })
    Test-IntegrationCase "symbol discovery and arity" {
        Assert-Equal "exact" ([string]$search.matchMode) "search_symbol match mode changed"
        Assert-Equal 1 $targetSymbols.Count "Target declaration is missing or ambiguous"
        Assert-Equal "interface" ([string]$targetSymbols[0].kind) "Target kind changed"
    }
    $targetHandle = [string]$targetSymbols[0].symbolId

    # Run the exact references canary as the first semantic operation. Besides preserving the
    # correctness gate, this makes the emitted semanticOp record a reproducible cold-path sample
    # for compilationPreparation/documentScope/findReferences attribution on the pinned corpus.
    $referencesCall = Invoke-ReferencesWithTelemetry $writer @{ symbolId = $targetHandle; mode = "auto"; maxProjects = 0; maxFiles = 1000; samplesPerGroup = 20; timeoutMs = 60000 } "partial"
    $references = $referencesCall.Payload
    $evidence.results.references = $references
    Test-IntegrationCase "semantic references" {
        Assert-True ($null -eq $references.error) "references returned $($references.error): $($references.reason)"
        Assert-SemanticInputAuthority $references "references"
        Assert-FriendRelationshipAuthority $references "references" `
            -ExpectedUnprovenProjects @($baseline.target.referencesUnprovenFriendAssemblyProjects)
        Assert-Equal ([int]$baseline.target.referenceCount) ([int]$references.totalReferences) "Reference count changed"
        Assert-Equal ([int]$baseline.target.referenceProjects) @($references.groups).Count "Reference-project count changed"
    }
    $referencesWarmCall = Invoke-ReferencesWithTelemetry $writer @{ symbolId = $targetHandle; mode = "auto"; maxProjects = 0; maxFiles = 1000; samplesPerGroup = 20; timeoutMs = 60000 } "partial"
    $referencesWarm = $referencesWarmCall.Payload
    $evidence.results.referencesWarm = $referencesWarm
    Test-IntegrationCase "warm semantic references parity" {
        Assert-True ($null -eq $referencesWarm.error) "warm references returned $($referencesWarm.error): $($referencesWarm.reason)"
        Assert-SemanticInputAuthority $referencesWarm "warm references"
        Assert-FriendRelationshipAuthority $referencesWarm "warm references" `
            -ExpectedUnprovenProjects @($baseline.target.referencesUnprovenFriendAssemblyProjects)
        Assert-Equal (Get-ReferenceContractSignature $references) (Get-ReferenceContractSignature $referencesWarm) "Cold/warm reference contract diverged"
    }
    $writerReferenceColdTelemetry = $referencesCall.Telemetry
    $writerReferenceWarmTelemetry = $referencesWarmCall.Telemetry
    $evidence.results.referencesTelemetry = [ordered]@{
        cold = $writerReferenceColdTelemetry
        warm = $writerReferenceWarmTelemetry
    }
    Test-IntegrationCase "semantic references document-scope telemetry" {
        $coldPreparation = $writerReferenceColdTelemetry.queryStages.compilationPreparation
        $coldScope = $writerReferenceColdTelemetry.queryStages.documentScope
        $warmScope = $writerReferenceWarmTelemetry.queryStages.documentScope
        Assert-True ([double]$writerReferenceColdTelemetry.clusterLoadProcessWideCpuMs -ge 0) "Cold cluster-load process CPU was not published"
        Assert-True ([double]$coldPreparation.processWideCpuMs -ge 0) "Compilation process CPU was not published"
        Assert-True ([int]$coldPreparation.processorCount -ge 1) "Compilation processor count was not published"
        Assert-True ([int]$coldPreparation.laneLimit -ge 1) "Compilation lane limit was not published"
        Assert-True ([double]$coldPreparation.busySumMs -ge [double]$coldPreparation.maxProjectBusyMs) "Compilation busy sum is below its project maximum"
        Assert-True ([double]$coldPreparation.maxProjectBusyMs -le ([double]$coldPreparation.criticalPathMs + 0.2)) "Compilation critical path is below its project maximum"
        Assert-True ([double]$coldPreparation.criticalPathMs -le ([double]$coldPreparation.waveMaxSumMs + 0.2)) "Compilation critical path exceeds the current wave floor"
        Assert-True ([double]$coldPreparation.waveMaxSumMs -le ([double]$coldPreparation.totalMs + 0.2)) "Compilation wave floor exceeds preparation wall"
        Assert-Equal "documentScoped" ([string]$coldScope.mode) "Cold references did not use document scoping"
        Assert-True ([int]$coldScope.candidateDocuments -lt [int]$coldScope.solutionDocuments) "Cold candidate documents did not reduce the solution"
        Assert-True ([int]$coldScope.scopedDocuments -lt [int]$coldScope.solutionDocuments) "Cold scoped documents did not reduce the solution"
        Assert-True ([int]$coldScope.scopedProjects -ge 1) "Cold scope omitted its Roslyn project breadth"
        Assert-True ([int]$coldScope.documentsInScopedProjects -ge [int]$coldScope.scopedDocuments) "Cold scope understated documents in its selected projects"
        Assert-Equal "documentScoped" ([string]$warmScope.mode) "Warm references did not retain document scoping"
        Assert-True ([bool]$warmScope.cacheHit) "Warm references did not reuse the leased-solution scope"
        Assert-Equal ([int]$coldScope.scopedDocuments) ([int]$warmScope.scopedDocuments) "Cold/warm scoped document counts diverged"
        Assert-Equal ([int]$coldScope.documentsInScopedProjects) ([int]$warmScope.documentsInScopedProjects) "Cold/warm project document breadth diverged"
    }

    $outline = Invoke-McpTool $writer "outline" @{ path = [string]$baseline.target.path; depth = 2 }
    $evidence.results.outline = $outline
    Test-IntegrationCase "outline" {
        Assert-True ($null -eq $outline.error) "Target outline returned an error"
        Assert-True (($outline | ConvertTo-Json -Compress -Depth 20) -match "ICompilationFactoryService") "Outline omitted the target declaration"
    }

    $source = Invoke-McpTool $writer "source_context" @{ symbolId = $targetHandle; contextLines = 1; maxBytes = 4096 }
    $evidence.results.sourceContext = $source
    Test-IntegrationCase "bounded source context" {
        Assert-Equal ([string]$baseline.target.path) ([string]$source.path) "source_context returned the wrong file"
        Assert-True (($source | ConvertTo-Json -Compress -Depth 10) -match "interface ICompilationFactoryService") "source_context omitted the declaration text"
        Assert-Equal "text" ([string]$source.meta.navigationLayer) "source_context layer changed"
    }

    $sourceRangeAlias = Invoke-McpTool $writer "source_context" @{
        path = [string]$baseline.target.path
        range = [string]$baseline.target.line
        contextLines = 0
        maxBytes = 4096
    }
    $evidence.results.sourceContextRangeAlias = $sourceRangeAlias
    Test-IntegrationCase "source_context range compatibility alias" {
        Assert-True ($null -eq $sourceRangeAlias.error) "source_context range alias returned $($sourceRangeAlias.error)"
        Assert-Equal ([string]$baseline.target.path) ([string]$sourceRangeAlias.path) "source_context range alias returned the wrong file"
        Assert-True (($sourceRangeAlias | ConvertTo-Json -Compress -Depth 10) -match "interface ICompilationFactoryService") "source_context range alias omitted the target line"
    }

    $symbolAt = Invoke-McpTool $writer "symbol_at" @{ path = [string]$baseline.target.path; line = [int]$baseline.target.line }
    $evidence.results.symbolAt = $symbolAt
    Test-IntegrationCase "reverse symbol lookup and ownership" {
        Assert-True ([bool]$symbolAt.found) "symbol_at did not find the target"
        Assert-Contains @($symbolAt.chain | ForEach-Object { [string]$_.name }) ([string]$baseline.target.name) "symbol_at chain omitted the target"
        Assert-True (@($symbolAt.owningProjects).Count -gt 0) "symbol_at did not report an owning project"
    }

    $projectsContaining = Invoke-McpTool $writer "projects_containing" @{ path = [string]$baseline.target.path }
    $evidence.results.projectsContaining = $projectsContaining
    $ownerNames = @($projectsContaining.projects | ForEach-Object { [string]$_.name })
    Test-IntegrationCase "compiled ownership" {
        Assert-True ($ownerNames.Count -gt 0) "projects_containing returned no owners"
        Assert-Contains $ownerNames "Microsoft.CodeAnalysis.Workspaces" "Expected Workspaces owner is absent"
    }

    $definition = Invoke-SemanticWithRetry $writer "definition" @{ symbolId = $targetHandle; mode = "auto"; includeBody = $true; timeoutMs = 30000 }
    $evidence.results.definition = $definition
    Test-IntegrationCase "semantic definition" {
        Assert-True ($null -eq $definition.error) "definition returned $($definition.error): $($definition.reason)"
        Assert-Equal "exact" ([string]$definition.meta.confidence) "definition lost compiler-exact confidence"
        Assert-True (($definition | ConvertTo-Json -Compress -Depth 20) -match [regex]::Escape([string]$baseline.target.path)) "definition returned the wrong declaration"
    }

    $understanding = $baseline.understandingTarget
    $calleeDefinition = Invoke-SemanticWithRetry $writer "definition" @{
        name = [string]$understanding.calleeName
        path = [string]$understanding.path
        line = [int]$understanding.calleeLine
        column = [int]$understanding.calleeColumn
        mode = "semantic"
        includeBody = $true
        timeoutMs = 30000
    }
    $understandingContext = Invoke-McpTool $writer "source_context" @{
        path = [string]$understanding.path
        spans = "$($understanding.propertyLine),$($understanding.calleeLine),$($understanding.followOnLine)"
        contextLines = 0
        maxBytes = 8192
    }
    $evidence.results.codeUnderstanding = [ordered]@{
        callee = $calleeDefinition
        context = $understandingContext
    }
    Test-IntegrationCase "compiler-resolved code understanding chain" {
        Assert-True ($null -eq $calleeDefinition.error) "GetCompilationAsync definition returned $($calleeDefinition.error): $($calleeDefinition.partialReason)"
        Assert-Equal "exact" ([string]$calleeDefinition.meta.confidence) "GetCompilationAsync definition lost compiler-exact confidence"
        Assert-Equal "semantic" ([string]$calleeDefinition.meta.navigationLayer) "GetCompilationAsync definition lost semantic provenance"
        Assert-Equal "RegularCompilationTracker" ([string]$calleeDefinition.symbol.containingType) "GetCompilationAsync definition lost its containing type"
        Assert-Contains @($calleeDefinition.declarations | ForEach-Object { [string]$_.path }) ([string]$understanding.calleeDefinitionPath) "GetCompilationAsync bound to the wrong declaration"
        Assert-True ([string]$calleeDefinition.body.source -match [regex]::Escape([string]$understanding.calleeSignature)) "GetCompilationAsync body omitted its Task<Compilation> return type"
        Assert-Equal "live" ([string]$calleeDefinition.body.freshness) "GetCompilationAsync body lost live-source freshness"

        Assert-True ($null -eq $understandingContext.error) "Code-understanding source context returned $($understandingContext.error)"
        Assert-Equal "text" ([string]$understandingContext.meta.navigationLayer) "Code-understanding source context lost text-layer provenance"
        Assert-Equal "live" ([string]$understandingContext.freshness) "Code-understanding source context lost live-source freshness"
        $understandingJson = $understandingContext | ConvertTo-Json -Compress -Depth 20
        Assert-True ($understandingJson -match [regex]::Escape([string]$understanding.propertySignature)) "Source context omitted the receiver's declared type"
        Assert-True ($understandingJson -match [regex]::Escape([string]$understanding.followOnText)) "Source context omitted the follow-on Compilation use"
    }

    $implementations = Invoke-SemanticWithRetry $writer "implementations" @{ symbolId = $targetHandle; maxProjects = 0; timeoutMs = 60000 }
    $evidence.results.implementations = $implementations
    $implementationNames = @($implementations.implementations | ForEach-Object { Get-TypeResultName $_ })
    Test-IntegrationCase "compiler implementations" {
        Assert-True ($null -eq $implementations.error) "implementations returned $($implementations.error): $($implementations.reason)"
        Assert-SemanticInputAuthority $implementations "implementations"
        Assert-FriendRelationshipAuthority $implementations "implementations" `
            -ExpectedUnprovenProjects @($baseline.target.implementationsUnprovenFriendAssemblyProjects)
        Assert-True ($null -eq $implementations.PSObject.Properties["symbolConfidence"]) `
            "implementations unexpectedly used mixed fallback identity"
        Assert-True ($null -eq $implementations.PSObject.Properties["implementationsConfidence"]) `
            "implementations unexpectedly used heuristic fallback results"
        foreach ($expected in @($baseline.target.expectedImplementations)) {
            Assert-Contains $implementationNames ([string]$expected.name) "Expected implementation is absent"
        }
    }

    $hierarchy = Invoke-SemanticWithRetry $writer "type_hierarchy" @{ symbolId = $targetHandle; maxProjects = 0; timeoutMs = 60000 }
    $evidence.results.typeHierarchy = $hierarchy
    $derivedNames = @($hierarchy.derivedOrImplementing | ForEach-Object { Get-TypeResultName $_ })
    Test-IntegrationCase "compiler type hierarchy" {
        Assert-True ($null -eq $hierarchy.error) "type_hierarchy returned $($hierarchy.error): $($hierarchy.reason)"
        Assert-SemanticInputAuthority $hierarchy "type_hierarchy"
        Assert-FriendRelationshipAuthority $hierarchy "type_hierarchy" `
            -ExpectedUnprovenProjects @($baseline.target.typeHierarchyUnprovenFriendAssemblyProjects)
        Assert-True ($null -eq $hierarchy.PSObject.Properties["derivedConfidence"]) `
            "type_hierarchy unexpectedly used heuristic derived results"
        foreach ($expected in @($baseline.target.expectedImplementations)) {
            Assert-Contains $derivedNames ([string]$expected.name) "Expected hierarchy descendant is absent"
        }
    }

    $implementationBindings = [ordered]@{}
    foreach ($expected in @($baseline.target.expectedImplementations)) {
        $owner = Invoke-McpTool $writer "projects_containing" @{ path = [string]$expected.path }
        $baseDefinition = Invoke-SemanticWithRetry $writer "definition" @{
            name = [string]$baseline.target.name
            path = [string]$expected.path
            line = [int]$expected.line
            column = [int]$expected.baseColumn
            mode = "auto"
            timeoutMs = 30000
        }
        $implementationBindings[[string]$expected.name] = [ordered]@{
            ownership = $owner
            baseDefinition = $baseDefinition
        }
        Test-IntegrationCase "implementation ownership: $($expected.name)" {
            Assert-Contains @($owner.projects | ForEach-Object { [string]$_.name }) ([string]$expected.project) "Implementation file is attributed to the wrong project set"
        }
        Test-IntegrationCase "implementation base binding: $($expected.name)" {
            Assert-True ($null -eq $baseDefinition.error) "Definition of the implementation's base returned $($baseDefinition.error): $($baseDefinition.reason)"
            Assert-FriendRelationshipAuthority $baseDefinition "Implementation base binding" $false
            Assert-True (($baseDefinition | ConvertTo-Json -Compress -Depth 20) -match [regex]::Escape([string]$baseline.target.path)) "Implementation base bound to the wrong declaration"
        }
    }
    $evidence.results.implementationBindings = $implementationBindings

    $text = Invoke-McpTool $writer "search_text" @{ query = "ICompilationFactoryService"; pathGlob = "src/Workspaces/**"; limit = 20 }
    $evidence.results.searchText = $text
    Test-IntegrationCase "ranked text search" {
        Assert-True (@($text.hits).Count -gt 0) "search_text returned no precise hits"
        Assert-Contains @($text.hits | ForEach-Object { [string]$_.path }) ([string]$baseline.target.path) "search_text omitted the declaration file"
    }

    $relatedTests = Invoke-McpTool $writer "related_tests" @{ name = [string]$baseline.target.name; owningProject = "Microsoft.CodeAnalysis.Workspaces"; limit = 10 }
    $evidence.results.relatedTests = $relatedTests
    Test-IntegrationCase "related-test discovery" {
        Assert-Equal "heuristic" ([string]$relatedTests.meta.confidence) "related_tests confidence changed"
        Assert-True (@($relatedTests.testGroups).Count -gt 0) "related_tests returned no leads"
    }

    $impact = Invoke-McpTool $writer "impact" @{ symbolId = $targetHandle }
    $evidence.results.impact = $impact
    Test-IntegrationCase "impact bundle" {
        Assert-True ($null -eq $impact.error) "impact returned an error"
        Assert-Equal ([int]$baseline.target.indexedReferenceCandidates) ([int]$impact.references.totalCandidates) "Indexed impact reference count changed"
        Assert-True ([int]$impact.transitiveDependentProjects -gt 0) "impact lost dependent-project evidence"
    }

    $contextPack = Invoke-McpTool $writer "context_pack" @{ name = [string]$baseline.target.name; container = "Microsoft.CodeAnalysis"; maxBytes = 20000; timeoutMs = 30000 } 60000
    $evidence.results.contextPack = $contextPack
    Test-IntegrationCase "context pack" {
        Assert-True ($null -eq $contextPack.error) "context_pack returned an error"
        Assert-True ($null -ne $contextPack.primarySource) "context_pack omitted primary source"
        Assert-True ($null -ne $contextPack.references) "context_pack omitted reference evidence"
    }

    $methodAt = Invoke-McpTool $writer "symbol_at" @{ path = [string]$baseline.methodTarget.path; line = [int]$baseline.methodTarget.line }
    $methodCandidates = @($methodAt.chain | Where-Object {
        $_.name -eq [string]$baseline.methodTarget.name -and
        ([string]$_.containingType).EndsWith([string]$baseline.methodTarget.container, [StringComparison]::Ordinal)
    })
    $evidence.results.methodSymbolAt = $methodAt
    Test-IntegrationCase "method identity at a real overload site" {
        Assert-True ([bool]$methodAt.found) "symbol_at did not find the method target"
        Assert-Equal 1 $methodCandidates.Count "Method declaration is missing or ambiguous at its exact line"
        Assert-True (-not [string]::IsNullOrWhiteSpace([string]$methodCandidates[0].symbolId)) "Method target has no stable symbol handle"
    }

    if ($methodCandidates.Count -eq 1) {
        $methodHandle = [string]$methodCandidates[0].symbolId
        $methodDefinition = Invoke-SemanticWithRetry $writer "definition" @{ symbolId = $methodHandle; mode = "auto"; timeoutMs = 30000 }
        $methodPosition = @{
            name = [string]$baseline.methodTarget.name
            path = [string]$baseline.methodTarget.path
            line = [int]$baseline.methodTarget.line
            maxProjects = 0
            timeoutMs = 60000
        }
        $methodCallers = Invoke-SemanticWithRetry $writer "callers" $methodPosition
        $methodCallees = Invoke-SemanticWithRetry $writer "callees" @{
            name = [string]$baseline.methodTarget.name
            path = [string]$baseline.methodTarget.path
            line = [int]$baseline.methodTarget.line
            timeoutMs = 60000
        }
        $evidence.results.methodDefinition = $methodDefinition
        $evidence.results.methodCallers = $methodCallers
        $evidence.results.methodCallees = $methodCallees
        Test-IntegrationCase "method definition" {
            Assert-True ($null -eq $methodDefinition.error) "Method definition returned $($methodDefinition.error): $($methodDefinition.reason)"
            Assert-Equal "exact" ([string]$methodDefinition.meta.confidence) "Method definition lost compiler-exact confidence"
            Assert-True (($methodDefinition | ConvertTo-Json -Compress -Depth 20) -match [regex]::Escape([string]$baseline.methodTarget.path)) "Method definition returned the wrong declaration"
        }
        Test-IntegrationCase "method callers" {
            Assert-True ($null -eq $methodCallers.error) "callers returned $($methodCallers.error): $($methodCallers.reason)"
            Assert-Equal "exact" ([string]$methodCallers.meta.confidence) "callers lost compiler-exact confidence"
            Assert-True (@($methodCallers.callers).Count -gt 0) "callers returned no call sites"
        }
        Test-IntegrationCase "method callees" {
            Assert-True ($null -eq $methodCallees.error) "callees returned $($methodCallees.error): $($methodCallees.reason)"
            Assert-Equal "exact" ([string]$methodCallees.meta.confidence) "callees lost compiler-exact confidence"
            Assert-True (@($methodCallees.callees).Count -gt 0) "callees returned no outgoing calls"
        }
    }

    # The internal overload target above is intentionally partial for references because the
    # pinned repository contains textual candidates outside its loadable dependency graph.
    # Keep a separate public, uniquely named method canary whose complete project closure proves
    # that references still returns compiler-exact, non-partial results end to end.
    $exactReferencesAt = Invoke-McpTool $writer "symbol_at" @{
        path = [string]$baseline.exactReferencesTarget.path
        line = [int]$baseline.exactReferencesTarget.line
    }
    $exactReferenceCandidates = @($exactReferencesAt.chain | Where-Object {
        $_.name -eq [string]$baseline.exactReferencesTarget.name -and
        ([string]$_.containingType).EndsWith([string]$baseline.exactReferencesTarget.container, [StringComparison]::Ordinal)
    })
    $exactReferences = $null
    if ($exactReferenceCandidates.Count -eq 1) {
        $exactReferences = Invoke-SemanticWithRetry $writer "references" @{
            symbolId = [string]$exactReferenceCandidates[0].symbolId
            mode = "auto"
            maxProjects = 0
            maxFiles = 1000
            samplesPerGroup = [int]$baseline.exactReferencesTarget.samplesPerGroup
            timeoutMs = 60000
        }
    }
    $evidence.results.exactReferencesSymbolAt = $exactReferencesAt
    $evidence.results.exactReferences = $exactReferences
    Test-IntegrationCase "compiler-exact method references" {
        Assert-Equal 1 $exactReferenceCandidates.Count "Exact references target is missing or ambiguous at its pinned declaration"
        Assert-True ($null -ne $exactReferences) "Exact references target could not be queried"
        Assert-True ($null -eq $exactReferences.error) "Method references returned $($exactReferences.error): $($exactReferences.reason)"
        Assert-Equal ([string]$baseline.exactReferencesTarget.documentationCommentId) ([string]$exactReferences.symbol.documentationCommentId) "Method references bound to the wrong symbol"
        Assert-Equal "exact" ([string]$exactReferences.meta.confidence) "Method references lost compiler-exact confidence"
        Assert-Equal "semantic" ([string]$exactReferences.meta.navigationLayer) "Method references lost semantic provenance"
        $exactReferencesPartial = $null -ne $exactReferences.PSObject.Properties["partial"] -and [bool]$exactReferences.partial
        Assert-True (-not $exactReferencesPartial) "Method references unexpectedly became partial: $($exactReferences.partialReason)"
        Assert-Equal ([int]$baseline.exactReferencesTarget.referenceCount) ([int]$exactReferences.totalReferences) "Compiler-exact method reference count changed"
        Assert-Equal ([int]$baseline.exactReferencesTarget.referenceProjects) @($exactReferences.groups).Count "Compiler-exact method reference project count changed"
        foreach ($expectedGroup in @($baseline.exactReferencesTarget.groups)) {
            $actualGroups = @($exactReferences.groups | Where-Object {
                [string]$_.project -eq [string]$expectedGroup.project
            })
            Assert-Equal 1 $actualGroups.Count "Compiler-exact method reference group $($expectedGroup.project) is missing or duplicated"
            Assert-Equal ([int]$expectedGroup.count) ([int]$actualGroups[0].count) "Compiler-exact method reference count changed for $($expectedGroup.project)"
            $actualSamples = @($actualGroups[0].samples | ForEach-Object {
                "$([string]$_.path)|$([int]$_.line)|$([string]$_.kind)"
            })
            Assert-Equal (@($expectedGroup.samples) -join ";") ($actualSamples -join ";") "Compiler-exact method sample order changed for $($expectedGroup.project)"
        }
    }

    $projectGraph = Invoke-McpTool $writer "project_graph" @{ project = "Microsoft.CodeAnalysis.Workspaces"; depth = 2; direction = "both" }
    $evidence.results.projectGraph = $projectGraph
    Test-IntegrationCase "project graph" {
        Assert-Equal "Microsoft.CodeAnalysis.Workspaces" ([string]$projectGraph.root.name) "project_graph resolved the wrong root"
        Assert-True ([int]$projectGraph.nodeCount -gt 1) "project_graph returned no neighbors"
        Assert-True (@($projectGraph.edges).Count -gt 0) "project_graph returned no edges"
    }

    $dependency = Invoke-McpTool $writer "dependency_path" @{ fromProject = "Microsoft.CodeAnalysis.CSharp.Workspaces"; toProject = "Microsoft.CodeAnalysis.Workspaces"; maxPaths = 3 }
    $evidence.results.dependencyPath = $dependency
    Test-IntegrationCase "dependency path" {
        Assert-True ([bool]$dependency.found) "Expected CSharp.Workspaces -> Workspaces path is absent"
        Assert-True (@($dependency.paths).Count -gt 0) "dependency_path returned no display path"
    }

    $batchOutline = Invoke-McpTool $writer "batch_outline" @{ paths = "$($baseline.target.path),$($baseline.target.expectedImplementations[0].path)"; depth = 1 }
    $evidence.results.batchOutline = $batchOutline
    Test-IntegrationCase "batch outline" {
        Assert-Equal 2 @($batchOutline.outlines).Count "batch_outline did not return both files"
    }

    $config = Invoke-McpTool $writer "config_lookup" @{ key = "LangVersion"; limit = 20 }
    $evidence.results.configLookup = $config
    Test-IntegrationCase "configuration lookup" {
        Assert-True (@($config.hits).Count -gt 0) "config_lookup returned no LangVersion hits"
    }

    $repeat = Invoke-SemanticWithRetry $writer "implementations" @{ symbolId = $targetHandle; maxProjects = 0; timeoutMs = 60000 }
    $evidence.results.implementationsWarmRepeat = $repeat
    $repeatNames = @($repeat.implementations | ForEach-Object { Get-TypeResultName $_ }) -join "|"
    Test-IntegrationCase "warm semantic repeat is stable" {
        Assert-Equal ([string]$implementations.meta.confidence) ([string]$repeat.meta.confidence) "Warm repeat confidence changed"
        Assert-Equal ($implementationNames -join "|") $repeatNames "Warm repeat implementation membership changed"
        Assert-Equal ([string]$implementations.meta.indexVersion) ([string]$repeat.meta.indexVersion) "Warm repeat crossed index epochs"
    }

    $fsharpWriter = Start-McpClient "fsharp-writer" $FSharpWorkspace $FSharpIndexDb
    $fsharpSession = Initialize-McpClient $fsharpWriter "writer"
    $fsharpCapabilities = $fsharpSession.Capabilities
    $fsharpOverview = Invoke-McpTool $fsharpWriter "repo_overview" ([hashtable]::new())
    $evidence.fsharpFreshIndex = [ordered]@{
        outcome = "fresh"
        startupBuildReason = [string]$fsharpCapabilities.index.startupBuildReason
        indexVersion = [string]$fsharpOverview.meta.indexVersion
        counts = Get-FSharpOverviewCounts $fsharpOverview
    }
    $evidence.results.fsharpCapabilities = $fsharpCapabilities
    $evidence.results.fsharpOverview = $fsharpOverview

    Test-IntegrationCase "FSharp pinned checkout builds a fresh index" {
        Assert-True (Test-Path -LiteralPath $FSharpIndexDb -PathType Leaf) `
            "FSharp fresh index was not created: $FSharpIndexDb"
        Assert-Equal "fresh" ([string]$evidence.fsharpFreshIndex.outcome) `
            "FSharp integration index was not fresh"
        Assert-Equal "startup_missing" ([string]$evidence.fsharpFreshIndex.startupBuildReason) `
            "FSharp server did not report a database-absent startup build"
    }

    Test-IntegrationCase "current server uses the fresh pinned FSharp index" {
        Assert-True (-not [string]::IsNullOrWhiteSpace([string]$fsharpSession.Initialize.serverInfo.version)) "FSharp MCP omitted its runtime version"
        Assert-True (@($fsharpSession.Tools.tools).Count -gt 0) "FSharp MCP advertised no tools"
        Assert-Equal ([string]$fsharpBaseline.fsharpCommit) ([string]$fsharpOverview.git.indexedCommit) "FSharp indexed commit changed"
        Assert-True ([bool]$fsharpOverview.git.headMatchesIndex) "FSharp HEAD no longer matches the fresh integration index"
        Assert-Equal ([string]$fsharpOverview.meta.indexVersion) ([string]$fsharpCapabilities.index.indexVersion) "FSharp capabilities evidence is stale for the judged index epoch"
        Assert-CapabilitySemanticInputAuthority $fsharpCapabilities.semantic "FSharp"
    }

    Test-IntegrationCase "official FSharp repository counts" {
        Assert-Equal ([int]$fsharpBaseline.counts.projects) ([int]$fsharpOverview.projects.total) "FSharp project count changed"
        Assert-Equal ([int]$fsharpBaseline.counts.fsharpProjects) ([int]$fsharpOverview.projects.fsharp) "FSharp-language project count changed"
        Assert-Equal ([int]$fsharpBaseline.counts.csharpFiles) ([int]$fsharpOverview.csFiles) "FSharp repository C# file count changed"
        Assert-Equal ([int]$fsharpBaseline.counts.fsharpFiles) ([int]$fsharpOverview.fsFiles) "FSharp source count changed"
        Assert-Equal ([int]$fsharpBaseline.counts.symbols) ([int]$fsharpOverview.symbols) "FSharp repository symbol count changed"
        Assert-Equal ([int]$fsharpBaseline.counts.orphanedFiles) ([int]$fsharpOverview.orphanedFiles) "FSharp repository orphaned-file count changed"
    }

    $fsharpFile = Invoke-McpTool $fsharpWriter "find_file" @{ nameOrGlob = "option.fs"; limit = 10 }
    $fsharpProject = Invoke-McpTool $fsharpWriter "find_file" @{ nameOrGlob = "FSharp.Core.fsproj"; limit = 10 }
    $fsharpOwners = Invoke-McpTool $fsharpWriter "projects_containing" @{ path = [string]$fsharpBaseline.target.sourcePath }
    $evidence.results.fsharpDiscovery = [ordered]@{ file = $fsharpFile; project = $fsharpProject; owners = $fsharpOwners }
    Test-IntegrationCase "official FSharp file discovery and ownership" {
        Assert-True (@($fsharpFile.files | Where-Object { $_.path -eq [string]$fsharpBaseline.target.sourcePath -and $_.language -eq "fs" }).Count -eq 1) "Official FSharp source is not indexed with lang=fs"
        Assert-True (@($fsharpProject.files | Where-Object { $_.path -eq [string]$fsharpBaseline.target.projectPath -and $_.language -eq "fsproj" }).Count -eq 1) "Official FSharp project is not indexed with lang=fsproj"
        Assert-True (@($fsharpOwners.projects | Where-Object { $_.name -eq [string]$fsharpBaseline.target.projectName -and $_.language -eq "fs" }).Count -eq 1) "Official FSharp compile ownership is absent"
    }

    $fsharpText = Invoke-McpTool $fsharpWriter "search_text" @{
        query = [string]$fsharpBaseline.target.probeText
        pathGlob = [string]$fsharpBaseline.target.sourcePath
        limit = 10
    }
    $fsharpOutline = Invoke-McpTool $fsharpWriter "outline" @{ path = [string]$fsharpBaseline.target.sourcePath; depth = 2 }
    $fsharpSymbolSearch = Invoke-McpTool $fsharpWriter "search_symbol" @{
        query = [string]$fsharpBaseline.target.outlineFunction
        kinds = "function"
        match = "exact"
        pathGlob = [string]$fsharpBaseline.target.sourcePath
        limit = 10
    }
    $evidence.results.fsharpNavigation = [ordered]@{
        searchText = $fsharpText
        searchSymbol = $fsharpSymbolSearch
        outline = $fsharpOutline
    }
    Test-IntegrationCase "official FSharp text and syntax navigation" {
        Assert-True ([int]$fsharpText.preciseCount -ge [int]$fsharpBaseline.target.minimumPreciseTextHits) "Official FSharp source text is not searchable"
        $indexedFunctions = @($fsharpSymbolSearch.symbols | Where-Object {
            $_.name -eq [string]$fsharpBaseline.target.outlineFunction -and
            $_.kind -eq "function" -and
            $_.path -eq [string]$fsharpBaseline.target.sourcePath -and
            $_.ns -eq [string]$fsharpBaseline.target.symbolNamespace -and
            $_.startLine -eq [int]$fsharpBaseline.target.symbolStartLine
        })
        Assert-Equal 1 $indexedFunctions.Count "Official FSharp function identity is absent or duplicated in indexed symbol-name search"
        Assert-Equal "indexed" ([string]$fsharpSymbolSearch.meta.confidence) "Official FSharp symbol search confidence changed"
        Assert-Equal "syntax" ([string]$fsharpSymbolSearch.meta.navigationLayer) "Official FSharp symbol search reported the wrong navigation layer"
        Assert-True ($null -eq $fsharpOutline.error) "Official FSharp outline returned $($fsharpOutline.error)"
        Assert-Equal "indexed" ([string]$fsharpOutline.meta.confidence) "Official FSharp outline confidence changed"
        Assert-Equal "syntax" ([string]$fsharpOutline.meta.navigationLayer) "Official FSharp outline reported the wrong navigation layer"
        Assert-Equal ([string]$fsharpBaseline.target.outlinePartialReason) ([string]$fsharpOutline.partialReason) "Official FSharp outline partial reason changed"
        Assert-Equal ([string]$fsharpBaseline.target.projectPath) ([string]$fsharpOutline.selectedParseContext.project) "Official FSharp outline selected the wrong project"
        Assert-Equal ([string]$fsharpBaseline.target.targetFramework) ([string]$fsharpOutline.selectedParseContext.targetFramework) "Official FSharp outline selected the wrong target framework"
        $outlineJson = $fsharpOutline | ConvertTo-Json -Compress -Depth 30
        Assert-True ($outlineJson -match [regex]::Escape([string]$fsharpBaseline.target.outlineModule)) "Official FSharp outline omitted the expected module"
        Assert-True ($outlineJson -match [regex]::Escape([string]$fsharpBaseline.target.outlineFunction)) "Official FSharp outline omitted the expected function"
    }

    $fsharpSymbolAt = Invoke-McpTool $fsharpWriter "symbol_at" @{
        path = [string]$fsharpBaseline.target.sourcePath
        line = [int]$fsharpBaseline.target.line
        column = [int]$fsharpBaseline.target.column
    }
    $fsharpDefinition = Invoke-McpTool $fsharpWriter "definition" @{
        path = [string]$fsharpBaseline.target.sourcePath
        line = [int]$fsharpBaseline.target.line
        column = [int]$fsharpBaseline.target.column
        mode = "auto"
        timeoutMs = 30000
    }
    $evidence.results.fsharpSemanticBoundary = [ordered]@{ symbolAt = $fsharpSymbolAt; definition = $fsharpDefinition }
    Test-IntegrationCase "official FSharp bounded semantic boundary is explicit" {
        foreach ($payload in @($fsharpSymbolAt, $fsharpDefinition)) {
            Assert-Equal ([string]$fsharpBaseline.target.semanticError) ([string]$payload.error) "Official FSharp semantic boundary changed"
            Assert-True ([bool]$payload.partial) "Official FSharp semantic boundary omitted partial=true"
            Assert-Equal ([string]$fsharpBaseline.target.semanticPartialReason) ([string]$payload.partialReason) "Official FSharp semantic partial reason changed"
            Assert-Equal ([string]$fsharpBaseline.target.projectPath) ([string]$payload.selectedFSharpTypeCheckContext.project) "Official FSharp semantic boundary selected the wrong project"
            Assert-Equal ([string]$fsharpBaseline.target.targetFramework) ([string]$payload.selectedFSharpTypeCheckContext.targetFramework) "Official FSharp semantic boundary selected the wrong target framework"
        }
    }

    $secondClient = Start-McpClient "second-client" $Workspace $IndexDb
    $secondSession = Initialize-McpClient $secondClient "writer"
    $evidence.results.secondClientCapabilities = $secondSession.Capabilities
    Test-IntegrationCase "second client shares the same daemon and index epoch" {
        Assert-Equal ([string]$writerCapabilities.index.indexVersion) ([string]$secondSession.Capabilities.index.indexVersion) "Second client attached to a different index epoch"
        Assert-Equal ([int]$writerCapabilities.runtime.processId) ([int]$secondSession.Capabilities.runtime.processId) "Second client did not join the same daemon process"
    }

    $secondSearch = Invoke-McpTool $secondClient "search_symbol" @{ query = [string]$baseline.target.name; limit = 10 }
    $secondTarget = @($secondSearch.symbols | Where-Object { $_.path -eq [string]$baseline.target.path -and $_.arity -eq [int]$baseline.target.arity })
    $evidence.results.secondClientSearch = $secondSearch
    Test-IntegrationCase "second client symbol identity" {
        Assert-Equal 1 $secondTarget.Count "Second client target declaration is missing or ambiguous"
        Assert-Equal $targetHandle ([string]$secondTarget[0].symbolId) "Second client saw a different index handle"
    }

    $secondReferencesCall = Invoke-ReferencesWithTelemetry $secondClient @{ symbolId = [string]$secondTarget[0].symbolId; mode = "auto"; maxProjects = 0; maxFiles = 1000; samplesPerGroup = 20; timeoutMs = 60000 } "partial"
    $secondReferences = $secondReferencesCall.Payload
    $evidence.results.secondClientReferences = $secondReferences
    $secondReferenceTelemetry = $secondReferencesCall.Telemetry
    $evidence.results.secondClientReferenceTelemetry = $secondReferenceTelemetry
    Test-IntegrationCase "second client semantic references parity" {
        Assert-True ($null -eq $secondReferences.error) "Second-client references returned $($secondReferences.error): $($secondReferences.reason)"
        Assert-Equal ([string]$references.meta.confidence) ([string]$secondReferences.meta.confidence) "Shared-client references confidence diverged"
        # Resident solution size can grow as the shared daemon warms projects for either client.
        # The semantic result and requested/loaded coverage must remain identical.
        Assert-Equal (Get-ReferenceContractSignature $references -IgnoreResidentSolutionProjects) (Get-ReferenceContractSignature $secondReferences -IgnoreResidentSolutionProjects) "Shared-client reference contract diverged"
        Assert-Equal "documentScoped" ([string]$secondReferenceTelemetry.queryStages.documentScope.mode) "Second-client references did not use live-text document scoping"
        Assert-True ([int]$secondReferenceTelemetry.queryStages.documentScope.scopedDocuments -lt [int]$secondReferenceTelemetry.queryStages.documentScope.solutionDocuments) "Second-client document scope did not reduce the solution"
    }

    $secondImplementations = Invoke-SemanticWithRetry $secondClient "implementations" @{ symbolId = [string]$secondTarget[0].symbolId; maxProjects = 0; timeoutMs = 60000 }
    $evidence.results.secondClientImplementations = $secondImplementations
    $secondImplementationNames = @($secondImplementations.implementations | ForEach-Object { Get-TypeResultName $_ }) -join "|"
    Test-IntegrationCase "second client compiler implementations parity" {
        Assert-True ($null -eq $secondImplementations.error) "Second-client implementations returned $($secondImplementations.error): $($secondImplementations.reason)"
        Assert-Equal ([string]$implementations.meta.confidence) ([string]$secondImplementations.meta.confidence) "Shared-client confidence diverged"
        Assert-Equal ($implementationNames -join "|") $secondImplementationNames "Shared-client implementation membership diverged"
    }
} finally {
    $stopErrors = New-Object System.Collections.Generic.List[string]
    try { Stop-McpClient $fsharpWriter } catch { $stopErrors.Add($_.Exception.Message) }
    try { Stop-McpClient $secondClient } catch { $stopErrors.Add($_.Exception.Message) }
    try { Stop-McpClient $writer } catch { $stopErrors.Add($_.Exception.Message) }
    foreach ($client in @($fsharpWriter, $secondClient, $writer)) {
        try { Request-McpDaemonRetirement $client } catch { $stopErrors.Add($_.Exception.Message) }
    }
    if ($null -ne $isolatedPackagesRoot -and
        (Test-Path -LiteralPath $isolatedPackagesRoot -PathType Container)) {
        try {
            Remove-IsolatedPackagesRoot $repoRoot $isolatedPackagesRoot
        } catch {
            $stopErrors.Add("isolated integration package cleanup failed: $($_.Exception.Message)")
        }
    }
    if ($null -ne $freshIndexRoot -and
        (Test-Path -LiteralPath $freshIndexRoot -PathType Container)) {
        try {
            Remove-FreshIndexRun $repoRoot $freshIndexRoot @($defaultDatabasePaths)
        } catch {
            $stopErrors.Add("fresh integration index cleanup failed: $($_.Exception.Message)")
        }
    }
    if ($null -ne $freshRunLease) {
        try { $freshRunLease.Dispose() } catch {
            $stopErrors.Add("fresh integration lease release failed: $($_.Exception.Message)")
        }
    }
    foreach ($stopError in $stopErrors) { $failures.Add("teardown: $stopError") }

    $evidence.completedAtUtc = [DateTime]::UtcNow.ToString("O")
    $evidence.passed = $passed
    $evidence.failed = $failures.Count
    $evidence.failures = @($failures)
    $evidenceDirectory = Split-Path -Parent $EvidencePath
    [IO.Directory]::CreateDirectory($evidenceDirectory) | Out-Null
    [IO.File]::WriteAllText($EvidencePath, ($evidence | ConvertTo-Json -Depth 40), [Text.UTF8Encoding]::new($false))
}

Write-Host ""
Write-Host "External MCP integration: $passed passed, $($failures.Count) failed"
Write-Host "Evidence: $EvidencePath"
if ($failures.Count -gt 0) {
    foreach ($failure in $failures) { Write-Host " - $failure" -ForegroundColor Red }
    exit 1
}
exit 0
