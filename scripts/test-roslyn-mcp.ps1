[CmdletBinding()]
param(
    [string]$Workspace,
    [string]$IndexDb,
    [string]$BaselinePath,
    [string]$CandidateExpectationsPath,
    [switch]$AllowCandidatePhoenix,
    [switch]$PrintCandidateIdentity,
    [string]$EvidencePath,
    [switch]$SelfTestProcessLifecycle,
    [switch]$SelfTestProcessHost,
    [switch]$SelfTestProcessGrandchild,
    [switch]$SelfTestSemanticRetryContract
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
    [Console]::Error.Write((('x' * 200000) -join ''))
    [Console]::Error.WriteLine("`nGRANDCHILD_PID=$($grandchild.Id)")
    Start-Sleep -Seconds 60
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

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
if ([string]::IsNullOrWhiteSpace($BaselinePath)) {
    $BaselinePath = Join-Path $repoRoot "tests\integration\roslyn-mcp-baseline.json"
}
$baseline = Get-Content -Raw -LiteralPath $BaselinePath | ConvertFrom-Json
$expectations = $baseline
if ($AllowCandidatePhoenix) {
    if ([string]::IsNullOrWhiteSpace($CandidateExpectationsPath)) {
        $CandidateExpectationsPath = Join-Path $repoRoot "tests\integration\roslyn-mcp-candidate.json"
    }
    $candidate = Get-Content -Raw -LiteralPath $CandidateExpectationsPath | ConvertFrom-Json
    if ([string]$baseline.roslynCommit -ne [string]$candidate.roslynCommit) {
        throw "Candidate expectations target a different frozen Roslyn commit"
    }
    $expectations = $candidate
}
$supportsFSharpTierA = $expectations.PSObject.Properties.Name -contains "fsharpTierA" -and
    [bool]$expectations.fsharpTierA
$fsharpProbeText = if ($expectations.PSObject.Properties.Name -contains "fsharpProbeText") {
    [string]$expectations.fsharpProbeText
} else { [string]$baseline.fsharp.probeText }
$expectedIndexSchema = if ($expectations.PSObject.Properties.Name -contains "indexSchema") {
    [string]$expectations.indexSchema
} else { [string]$baseline.indexSchema }
$expectedIndexVersion = if ($expectations.PSObject.Properties.Name -contains "indexVersion") {
    [string]$expectations.indexVersion
} else { [string]$baseline.indexVersion }
$expectedCounts = if ($expectations.PSObject.Properties.Name -contains "counts") {
    $expectations.counts
} else { $baseline.counts }
if ([string]::IsNullOrWhiteSpace($Workspace)) {
    $Workspace = if ([string]::IsNullOrWhiteSpace($env:PHOENIX_ROSLYN_WORKSPACE)) {
        [string]$baseline.defaultWorkspace
    } else {
        $env:PHOENIX_ROSLYN_WORKSPACE
    }
}
$Workspace = [IO.Path]::GetFullPath($Workspace)
if ([string]::IsNullOrWhiteSpace($IndexDb)) {
    $IndexDb = Join-Path $Workspace ([string]$baseline.indexRelativePath)
}
$IndexDb = [IO.Path]::GetFullPath($IndexDb)
if ([string]::IsNullOrWhiteSpace($EvidencePath)) {
    $EvidencePath = Join-Path $repoRoot "artifacts\roslyn-integration\last-results.json"
}
$EvidencePath = [IO.Path]::GetFullPath($EvidencePath)

function Invoke-Git([string]$WorkingDirectory, [string[]]$Arguments) {
    $output = @(& git -C $WorkingDirectory @Arguments)
    if ($LASTEXITCODE -ne 0) {
        throw "git -C '$WorkingDirectory' $($Arguments -join ' ') failed"
    }
    return $output
}

function Get-TextSha256([string]$Value) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [Text.UTF8Encoding]::new($false).GetBytes($Value)
        return ([BitConverter]::ToString($sha.ComputeHash($bytes))).Replace("-", "").ToLowerInvariant()
    } finally {
        $sha.Dispose()
    }
}

function Get-GitTargetIdentity([string]$WorkingDirectory) {
    $head = [string](@(Invoke-Git $WorkingDirectory @("rev-parse", "HEAD"))[0])
    $unstaged = @(Invoke-Git $WorkingDirectory @("diff", "--name-only", "--no-ext-diff", "--no-textconv", "--no-color") |
        Where-Object { $_ -and $_.Trim() } | Sort-Object -Unique)
    $staged = @(Invoke-Git $WorkingDirectory @("diff", "--cached", "--name-only", "--no-ext-diff", "--no-textconv", "--no-color") |
        Where-Object { $_ -and $_.Trim() } | Sort-Object -Unique)
    $untracked = @(Invoke-Git $WorkingDirectory @("ls-files", "--others", "--exclude-standard") |
        Where-Object { $_ -and $_.Trim() } | Sort-Object -Unique)
    $paths = @($unstaged + $staged + $untracked | Sort-Object -Unique)
    $entries = foreach ($path in $paths) {
        $full = Join-Path $WorkingDirectory $path
        $workingHash = if (Test-Path -LiteralPath $full -PathType Leaf) {
            [string](@(Invoke-Git $WorkingDirectory @("hash-object", "--no-filters", "--", $path))[0])
        } else {
            "deleted"
        }
        $stagedHash = $null
        if ($staged -contains $path) {
            $stageLine = @(Invoke-Git $WorkingDirectory @("ls-files", "--stage", "--", $path) | Select-Object -First 1)
            $stagedHash = if ($stageLine.Count -gt 0) { [string](($stageLine[0] -split '\s+')[1]) } else { "deleted" }
        }
        [ordered]@{
            path = $path
            unstaged = $unstaged -contains $path
            staged = $staged -contains $path
            untracked = $untracked -contains $path
            workingHash = $workingHash
            stagedHash = $stagedHash
        }
    }
    # HEAD locks every clean tracked byte. The manifest below locks each dirty/untracked byte on
    # top of HEAD. Beads runtime state is deliberately not a compiler input, and the candidate
    # expectation file must be excluded to avoid a self-referential hash.
    $identityEntries = @($entries | Where-Object {
        -not ([string]$_.path).StartsWith(".beads/", [StringComparison]::OrdinalIgnoreCase) -and
        -not ([string]$_.path).Equals("tests/integration/roslyn-mcp-candidate.json", [StringComparison]::OrdinalIgnoreCase)
    })
    $canonical = New-Object System.Collections.Generic.List[string]
    $canonical.Add("HEAD=$head")
    foreach ($entry in $identityEntries) {
        $canonical.Add("$($entry.path)`t$($entry.unstaged)`t$($entry.staged)`t$($entry.untracked)`t$($entry.workingHash)`t$($entry.stagedHash)")
    }
    return [ordered]@{
        head = $head
        trackedDirty = @($unstaged + $staged | Sort-Object -Unique).Count
        entries = @($entries)
        identityEntryCount = $identityEntries.Count
        targetSha256 = Get-TextSha256 ($canonical -join "`n")
        identityExclusions = @(".beads/**", "tests/integration/roslyn-mcp-candidate.json")
    }
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

function Assert-FriendRelationshipAuthority($Payload, [string]$Label) {
    $expectedConfidence = if ($null -ne $expectations.target.PSObject.Properties["friendRelationshipConfidence"]) {
        [string]$expectations.target.friendRelationshipConfidence
    } else {
        "exact"
    }
    $expectedPartialReason = if ($null -ne $expectations.target.PSObject.Properties["friendRelationshipPartialReason"]) {
        [string]$expectations.target.friendRelationshipPartialReason
    } else {
        ""
    }

    Assert-Equal $expectedConfidence ([string]$Payload.meta.confidence) "$Label confidence changed"
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

function Quote-ProcessArgument([string]$Value) {
    return '"' + $Value.Replace('"', '\"') + '"'
}

function Start-McpClient([string]$Label) {
    $mcpDll = Join-Path $repoRoot "src\CodeNav.Mcp\bin\Release\net9.0\PhoenixCodeNav.Mcp.dll"
    if (-not (Test-Path -LiteralPath $mcpDll -PathType Leaf)) {
        throw "Release MCP binary is missing. Run: dotnet build PhoenixCodeNav.sln -c Release --no-restore"
    }

    $start = New-Object System.Diagnostics.ProcessStartInfo
    $start.FileName = "dotnet"
    $start.Arguments = "$(Quote-ProcessArgument $mcpDll) --workspace-root $(Quote-ProcessArgument $Workspace) --index-db $(Quote-ProcessArgument $IndexDb)"
    $start.WorkingDirectory = $repoRoot
    $start.UseShellExecute = $false
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.CreateNoWindow = $true
    $start.EnvironmentVariables["PHOENIX_TELEMETRY_IPC"] = "0"

    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $start
    if (-not $process.Start()) { throw "Failed to start MCP $Label process" }
    $stderrTail = [PhoenixCodeNav.Integration.BoundedTextTail]::new(65536)
    return [pscustomobject]@{
        Label = $Label
        Process = $process
        NextId = 0
        StderrTail = $stderrTail
        StderrTask = $stderrTail.DrainAsync($process.StandardError)
        AllowNonZeroExit = $false
    }
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
    # Every writer startup deliberately runs a detect-all freshness sweep. The frozen Roslyn
    # checkout has 17k C# files, and a cold post-reboot sweep can take several minutes even
    # though the reusable index is already valid. Keep this integration gate bounded, but size
    # the bound for the repository it intentionally exercises rather than a tiny unit fixture.
    for ($attempt = 0; $attempt -lt 2400; $attempt++) {
        $capabilities = Invoke-McpTool $Client "server_capabilities" ([hashtable]::new())
        if ($capabilities.index.state -eq "ready" -and $capabilities.index.mode -eq $ExpectedMode) { break }
        Start-Sleep -Milliseconds 250
    }
    Assert-Equal "ready" $capabilities.index.state "$($Client.Label): index did not become ready"
    Assert-Equal $ExpectedMode $capabilities.index.mode "$($Client.Label): unexpected index access mode"
    return [pscustomobject]@{
        Initialize = $initialize
        Tools = $tools
        Capabilities = $capabilities
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
        $exitConfirmed = $true
        if (-not $Client.StderrTask.Wait(3000)) {
            throw "$($Client.Label): stderr drain did not complete after process exit"
        }
        $stderr = [string]$Client.StderrTail.Snapshot()
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
    $stderrTail = [PhoenixCodeNav.Integration.BoundedTextTail]::new(65536)
    return [pscustomobject]@{
        Label = "lifecycle-self-test"
        Process = $process
        NextId = 0
        StderrTail = $stderrTail
        StderrTask = $stderrTail.DrainAsync($process.StandardError)
        AllowNonZeroExit = $true
    }
}

if ($SelfTestProcessLifecycle) {
    $client = Start-ProcessLifecycleSelfTestClient
    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    Stop-McpClient $client
    $stderr = [string]$client.StderrTail.Snapshot()
    Assert-True ($stopwatch.Elapsed -lt [TimeSpan]::FromSeconds(15)) "Lifecycle teardown exceeded its hard bound"
    Assert-True ($stderr.Length -le 65536) "Captured stderr exceeded the rolling-tail bound"
    $pidMatch = [regex]::Match($stderr, "GRANDCHILD_PID=(\d+)")
    Assert-True $pidMatch.Success "Lifecycle host did not report its descendant pid"
    $grandchildPid = [int]$pidMatch.Groups[1].Value
    $deadline = [DateTime]::UtcNow.AddSeconds(2)
    while ([DateTime]::UtcNow -lt $deadline -and
           $null -ne (Get-Process -Id $grandchildPid -ErrorAction SilentlyContinue)) {
        Start-Sleep -Milliseconds 50
    }
    Assert-True ($null -eq (Get-Process -Id $grandchildPid -ErrorAction SilentlyContinue)) "Lifecycle teardown left a descendant running"
    Write-Host "Process lifecycle self-test passed"
    exit 0
}

function Test-RetryableSemanticPayload($Payload) {
    $reason = [string]$Payload.reason
    $partialReason = [string]$Payload.partialReason
    return $reason -match "cluster_cold_load|index_snapshot_unavailable" -or
           $partialReason -match "cluster_cold_load|index_snapshot_unavailable"
}

function Invoke-SemanticWithRetry($Client, [string]$Name, [hashtable]$Arguments) {
    for ($attempt = 0; $attempt -lt 4; $attempt++) {
        $payload = Invoke-McpTool $Client $Name $Arguments 120000
        if (-not (Test-RetryableSemanticPayload $payload)) { return $payload }
        Start-Sleep -Milliseconds 500
    }
    return $payload
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
    Write-Host "Semantic retry contract self-test passed"
    exit 0
}

$phoenixHead = [string](@(Invoke-Git $repoRoot @("rev-parse", "HEAD"))[0])
$phoenixTarget = Get-GitTargetIdentity $repoRoot
$mcpDllPath = Join-Path $repoRoot "src\CodeNav.Mcp\bin\Release\net9.0\PhoenixCodeNav.Mcp.dll"
Assert-True (Test-Path -LiteralPath $mcpDllPath -PathType Leaf) "Release MCP binary is missing. Run: dotnet build PhoenixCodeNav.sln -c Release --no-restore"
$mcpSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $mcpDllPath).Hash.ToLowerInvariant()
if ($PrintCandidateIdentity) {
    [ordered]@{
        phoenixHead = $phoenixHead
        phoenixTargetSha256 = [string]$phoenixTarget.targetSha256
        phoenixIdentityEntryCount = [int]$phoenixTarget.identityEntryCount
        mcpSha256 = $mcpSha256
        identityExclusions = @($phoenixTarget.identityExclusions)
    } | ConvertTo-Json -Depth 5
    exit 0
}
if (-not $AllowCandidatePhoenix) {
    Assert-Equal ([string]$baseline.phoenixBaselineCommit) $phoenixHead "Phoenix HEAD differs from the locked released baseline; pass -AllowCandidatePhoenix deliberately"
    Assert-Equal 0 ([int]$phoenixTarget.trackedDirty) "Phoenix has tracked source changes; pass -AllowCandidatePhoenix deliberately"
    Assert-Equal ([string]$baseline.mcpSha256) $mcpSha256 "Release MCP binary differs from the locked released baseline; rebuild or pass -AllowCandidatePhoenix deliberately"
} else {
    Assert-True (-not [string]::IsNullOrWhiteSpace([string]$candidate.phoenixHead)) "Candidate expectations do not lock Phoenix HEAD"
    Assert-True (-not [string]::IsNullOrWhiteSpace([string]$candidate.phoenixTargetSha256)) "Candidate expectations do not lock the Phoenix source target"
    Assert-True (-not [string]::IsNullOrWhiteSpace([string]$candidate.mcpSha256)) "Candidate expectations do not lock the Release MCP binary"
    Assert-Equal ([string]$candidate.phoenixHead) $phoenixHead "Phoenix HEAD differs from the locked candidate target"
    Assert-Equal ([string]$candidate.phoenixTargetSha256) ([string]$phoenixTarget.targetSha256) "Phoenix staged/unstaged/untracked bytes differ from the locked candidate target"
    Assert-Equal ([int]$candidate.phoenixIdentityEntryCount) ([int]$phoenixTarget.identityEntryCount) "Phoenix candidate identity manifest size changed"
    Assert-Equal ([string]$candidate.mcpSha256) $mcpSha256 "Release MCP binary differs from the locked candidate build"
}
Assert-True (Test-Path -LiteralPath $Workspace -PathType Container) "Frozen Roslyn workspace is missing: $Workspace"
$roslynHead = [string](@(Invoke-Git $Workspace @("rev-parse", "HEAD"))[0])
Assert-Equal ([string]$baseline.roslynCommit) $roslynHead "Roslyn HEAD differs from the locked integration baseline"
$unexpectedStatus = @(Invoke-Git $Workspace @("--no-optional-locks", "status", "--porcelain=v1", "--untracked-files=all") |
    Where-Object { $_ -and $_ -notmatch '^\?\? \.codenav/' })
Assert-Equal 0 $unexpectedStatus.Count "Frozen Roslyn workspace contains changes outside .codenav"
Assert-True (Test-Path -LiteralPath $IndexDb -PathType Leaf) "Reusable Roslyn index is missing: $IndexDb"
Assert-True (Test-Path -LiteralPath (Join-Path $Workspace ([string]$baseline.fsharp.sourcePath)) -PathType Leaf) "Frozen Roslyn checkout is missing the F# source probe"
Assert-True (Test-Path -LiteralPath (Join-Path $Workspace ([string]$baseline.fsharp.projectPath)) -PathType Leaf) "Frozen Roslyn checkout is missing the F# project probe"

$evidence = [ordered]@{
    baseline = $expectations.name
    phoenixHead = $phoenixHead
    phoenixTarget = $phoenixTarget
    mcpSha256 = $mcpSha256
    roslynHead = $roslynHead
    workspace = $Workspace
    indexDb = $IndexDb
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
$follower = $null
try {
    $writer = Start-McpClient "writer"
    $writerSession = Initialize-McpClient $writer "writer"
    $evidence.results.writerCapabilities = $writerSession.Capabilities

    Test-IntegrationCase "server identity and frozen index" {
        Assert-Equal ([string]$expectations.mcpVersion) ([string]$writerSession.Initialize.serverInfo.version) "MCP version changed"
        Assert-Equal 26 @($writerSession.Tools.tools).Count "MCP tool count changed"
        Assert-Equal $expectedIndexSchema ([string]$writerSession.Capabilities.build.indexSchema) "Index schema changed"
        Assert-Equal $expectedIndexVersion ([string]$writerSession.Capabilities.index.indexVersion) "Reusable index version changed"
        Assert-Equal ([string]$baseline.roslynCommit) ([string](Invoke-McpTool $writer "repo_overview" ([hashtable]::new())).git.indexedCommit) "Indexed commit changed"
    }

    $overview = Invoke-McpTool $writer "repo_overview" ([hashtable]::new())
    $evidence.results.repoOverview = $overview
    Test-IntegrationCase "repository counts" {
        Assert-Equal ([int]$expectedCounts.projects) ([int]$overview.projects.total) "Project count changed"
        Assert-Equal ([int]$expectedCounts.solutions) ([int]$overview.solutions) "Solution count changed"
        Assert-Equal ([int]$expectedCounts.csFiles) ([int]$overview.csFiles) "C# file count changed"
        Assert-Equal ([int]$expectedCounts.symbols) ([int]$overview.symbols) "Symbol count changed"
        Assert-Equal ([int]$expectedCounts.orphanedFiles) ([int]$overview.orphanedFiles) "Orphaned-file count changed"
        if ($supportsFSharpTierA) {
            Assert-Equal ([int]$expectedCounts.fsFiles) ([int]$overview.fsFiles) "F# file count changed"
            Assert-Equal ([int]$expectedCounts.fsProjects) ([int]$overview.projects.fsharp) "F# project count changed"
        }
        Assert-True ([bool]$overview.git.headMatchesIndex) "Roslyn HEAD no longer matches the reusable index"
    }

    $fileResult = Invoke-McpTool $writer "find_file" @{ nameOrGlob = "ICompilationFactoryService.cs"; limit = 10 }
    $evidence.results.findFile = $fileResult
    Test-IntegrationCase "file discovery" {
        Assert-Contains @($fileResult.files | ForEach-Object { [string]$_.path }) ([string]$baseline.target.path) "Target file was not found"
        Assert-Equal "indexed" ([string]$fileResult.meta.confidence) "find_file confidence changed"
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

    $implementations = Invoke-SemanticWithRetry $writer "implementations" @{ symbolId = $targetHandle; maxProjects = 0; timeoutMs = 60000 }
    $evidence.results.implementations = $implementations
    $implementationNames = @($implementations.implementations | ForEach-Object { Get-TypeResultName $_ })
    Test-IntegrationCase "compiler implementations" {
        Assert-True ($null -eq $implementations.error) "implementations returned $($implementations.error): $($implementations.reason)"
        Assert-FriendRelationshipAuthority $implementations "implementations"
        foreach ($expected in @($baseline.target.expectedImplementations)) {
            Assert-Contains $implementationNames ([string]$expected.name) "Expected implementation is absent"
        }
    }

    $hierarchy = Invoke-SemanticWithRetry $writer "type_hierarchy" @{ symbolId = $targetHandle; maxProjects = 0; timeoutMs = 60000 }
    $evidence.results.typeHierarchy = $hierarchy
    $derivedNames = @($hierarchy.derivedOrImplementing | ForEach-Object { Get-TypeResultName $_ })
    Test-IntegrationCase "compiler type hierarchy" {
        Assert-True ($null -eq $hierarchy.error) "type_hierarchy returned $($hierarchy.error): $($hierarchy.reason)"
        Assert-FriendRelationshipAuthority $hierarchy "type_hierarchy"
        Assert-True ([string]::IsNullOrWhiteSpace([string]$hierarchy.derivedConfidence)) "type_hierarchy downgraded the derived/implementing section to $($hierarchy.derivedConfidence): $($hierarchy.partialReason)"
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
            Assert-FriendRelationshipAuthority $baseDefinition "Implementation base binding"
            Assert-True (($baseDefinition | ConvertTo-Json -Compress -Depth 20) -match [regex]::Escape([string]$baseline.target.path)) "Implementation base bound to the wrong declaration"
        }
    }
    $evidence.results.implementationBindings = $implementationBindings

    $references = Invoke-SemanticWithRetry $writer "references" @{ symbolId = $targetHandle; mode = "auto"; maxProjects = 0; maxFiles = 1000; samplesPerGroup = 1; timeoutMs = 60000 }
    $evidence.results.references = $references
    Test-IntegrationCase "semantic references" {
        Assert-True ($null -eq $references.error) "references returned $($references.error): $($references.reason)"
        Assert-FriendRelationshipAuthority $references "references"
        Assert-Equal ([int]$expectations.target.exactReferences) ([int]$references.totalReferences) "Reference count changed"
        Assert-Equal ([int]$expectations.target.exactReferenceProjects) @($references.groups).Count "Reference-project count changed"
    }

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

    $fsFile = Invoke-McpTool $writer "find_file" @{ nameOrGlob = "Library.fs"; limit = 10 }
    $fsProject = Invoke-McpTool $writer "find_file" @{ nameOrGlob = "*.fsproj"; limit = 10 }
    $fsText = Invoke-McpTool $writer "search_text" @{
        query = $fsharpProbeText
        pathGlob = [string]$baseline.fsharp.sourcePath
        limit = 10
    }
    $fsOutline = Invoke-McpTool $writer "outline" @{ path = [string]$baseline.fsharp.sourcePath; depth = 2 }
    $fsOwners = if ($supportsFSharpTierA) {
        Invoke-McpTool $writer "projects_containing" @{ path = [string]$baseline.fsharp.sourcePath }
    } else { $null }
    $fsGraph = if ($supportsFSharpTierA) {
        Invoke-McpTool $writer "project_graph" @{ project = "csharplib"; depth = 1; direction = "downstream" }
    } else { $null }
    $evidence.results.fsharpTierA = [ordered]@{
        supported = $supportsFSharpTierA
        findFile = $fsFile
        findProject = $fsProject
        searchText = $fsText
        outline = $fsOutline
        owners = $fsOwners
        graph = $fsGraph
    }
    Test-IntegrationCase "F# tier-a capability matches the locked build" {
        if ($supportsFSharpTierA) {
            Assert-True (@($fsFile.files | Where-Object { $_.path -eq [string]$baseline.fsharp.sourcePath -and $_.language -eq "fs" }).Count -eq 1) "F# source is not indexed with lang=fs"
            Assert-True (@($fsProject.files | Where-Object { $_.path -eq [string]$baseline.fsharp.projectPath -and $_.language -eq "fsproj" }).Count -eq 1) "F# project is not indexed with lang=fsproj"
            Assert-True (@($fsText.hits | Where-Object { $_.path -eq [string]$baseline.fsharp.sourcePath }).Count -gt 0) "F# source text is not searchable"
            Assert-Equal "unsupported_language" ([string]$fsOutline.error) "F# outline did not disclose its language boundary"
            Assert-Equal "fs" ([string]$fsOutline.language) "F# outline reported the wrong language"
            Assert-True (@($fsOwners.projects | Where-Object { $_.name -eq "fsharplib" -and $_.language -eq "fs" }).Count -eq 1) "F# compile ownership is absent"
            Assert-True (@($fsGraph.edges | Where-Object { $_.from -eq "csharplib" -and $_.fromLanguage -eq "cs" -and $_.to -eq "fsharplib" -and $_.toLanguage -eq "fs" }).Count -gt 0) "C# to F# project-reference edge is absent"
        } else {
            Assert-Equal 0 @($fsFile.files).Count "Released baseline unexpectedly indexes .fs files"
            Assert-Equal 0 @($fsProject.files).Count "Released baseline unexpectedly indexes .fsproj files"
            Assert-Equal 0 @($fsText.hits).Count "Released baseline unexpectedly searches F# source"
            Assert-Equal "file_not_indexed" ([string]$fsOutline.error) "Released F# outline baseline changed"
        }
    }

    $repeat = Invoke-SemanticWithRetry $writer "implementations" @{ symbolId = $targetHandle; maxProjects = 0; timeoutMs = 60000 }
    $evidence.results.implementationsWarmRepeat = $repeat
    $repeatNames = @($repeat.implementations | ForEach-Object { Get-TypeResultName $_ }) -join "|"
    Test-IntegrationCase "warm semantic repeat is stable" {
        Assert-Equal ([string]$implementations.meta.confidence) ([string]$repeat.meta.confidence) "Warm repeat confidence changed"
        Assert-Equal ($implementationNames -join "|") $repeatNames "Warm repeat implementation membership changed"
        Assert-Equal ([string]$implementations.meta.indexVersion) ([string]$repeat.meta.indexVersion) "Warm repeat crossed index epochs"
    }

    $follower = Start-McpClient "follower"
    $followerSession = Initialize-McpClient $follower "follower"
    $evidence.results.followerCapabilities = $followerSession.Capabilities
    Test-IntegrationCase "read-only follower attaches to the same epoch" {
        Assert-Equal ([string]$writerSession.Capabilities.index.indexVersion) ([string]$followerSession.Capabilities.index.indexVersion) "Follower attached to a different index epoch"
        Assert-Equal $expectedIndexSchema ([string]$followerSession.Capabilities.build.indexSchema) "Follower schema changed"
    }

    $followerSearch = Invoke-McpTool $follower "search_symbol" @{ query = [string]$baseline.target.name; limit = 10 }
    $followerTarget = @($followerSearch.symbols | Where-Object { $_.path -eq [string]$baseline.target.path -and $_.arity -eq [int]$baseline.target.arity })
    $evidence.results.followerSearch = $followerSearch
    Test-IntegrationCase "follower symbol identity" {
        Assert-Equal 1 $followerTarget.Count "Follower target declaration is missing or ambiguous"
        Assert-Equal $targetHandle ([string]$followerTarget[0].symbolId) "Follower saw a different index handle"
    }

    $followerImplementations = Invoke-SemanticWithRetry $follower "implementations" @{ symbolId = [string]$followerTarget[0].symbolId; maxProjects = 0; timeoutMs = 60000 }
    $evidence.results.followerImplementations = $followerImplementations
    $followerImplementationNames = @($followerImplementations.implementations | ForEach-Object { Get-TypeResultName $_ }) -join "|"
    Test-IntegrationCase "follower compiler implementations parity" {
        Assert-True ($null -eq $followerImplementations.error) "Follower implementations returned $($followerImplementations.error): $($followerImplementations.reason)"
        Assert-Equal ([string]$implementations.meta.confidence) ([string]$followerImplementations.meta.confidence) "Writer/follower confidence diverged"
        Assert-Equal ($implementationNames -join "|") $followerImplementationNames "Writer/follower implementation membership diverged"
    }
} finally {
    $stopErrors = New-Object System.Collections.Generic.List[string]
    try { Stop-McpClient $follower } catch { $stopErrors.Add($_.Exception.Message) }
    try { Stop-McpClient $writer } catch { $stopErrors.Add($_.Exception.Message) }
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
Write-Host "Roslyn MCP integration: $passed passed, $($failures.Count) failed"
Write-Host "Evidence: $EvidencePath"
if ($failures.Count -gt 0) {
    foreach ($failure in $failures) { Write-Host " - $failure" -ForegroundColor Red }
    exit 1
}
exit 0
