[CmdletBinding()]
param(
    [switch]$SkipMcp,
    [switch]$SkipWebsite
)

$ErrorActionPreference = "Stop"
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$solutionPath = Join-Path $repoRoot "PhoenixCodeNav.sln"
$resultsDirectory = Join-Path $repoRoot "artifacts\gate-results"
$maxFailureLines = 200
$failedGates = [Collections.Generic.List[string]]::new()
$incompleteGates = [Collections.Generic.List[string]]::new()

# No process spawned by the gate may outlive it while retaining files in the private TEMP root.
$env:UseSharedCompilation = "false"
$env:DOTNET_CLI_USE_MSBUILD_SERVER = "0"
$env:MSBUILDDISABLENODEREUSE = "1"

function Convert-ToLines([object[]]$InputObject) {
    return @($InputObject | ForEach-Object { [string]$_ })
}

function Assert-Prerequisite([string]$Name, [string]$Path, [bool]$Required = $true) {
    if (-not $Required) { return $true }
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        Write-Host "prerequisite: missing $Name at $Path"
        $failedGates.Add("missing prerequisite: $Name")
        return $false
    }
    return $true
}

function Assert-Command([string]$Name, [bool]$Required = $true) {
    if (-not $Required) { return $true }
    if ($null -eq (Get-Command $Name -ErrorAction SilentlyContinue)) {
        Write-Host "prerequisite: command '$Name' is unavailable"
        $failedGates.Add("missing prerequisite: $Name")
        return $false
    }
    return $true
}

function Write-BoundedFailure(
    [string]$Name,
    [string]$Message,
    [string]$StackTrace,
    [string]$TrxPath
) {
    Write-Host "FAILED TEST: $Name"
    $lines = [Collections.Generic.List[string]]::new()
    $lines.Add("message:")
    foreach ($line in @($Message -split "`r?`n")) { $lines.Add("  $line") }
    $lines.Add("stack:")
    foreach ($line in @($StackTrace -split "`r?`n")) { $lines.Add("  $line") }

    $shown = [Math]::Min($maxFailureLines, $lines.Count)
    for ($index = 0; $index -lt $shown; $index++) {
        Write-Host $lines[$index]
    }
    if ($shown -lt $lines.Count) {
        Write-Host "  (+$($lines.Count - $shown) lines omitted, see $TrxPath)"
    }
}

function Read-Trx([string]$Path) {
    $settings = [Xml.XmlReaderSettings]::new()
    $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
    $reader = [Xml.XmlReader]::Create($Path, $settings)
    try {
        $document = [Xml.XmlDocument]::new()
        $document.Load($reader)
    } finally {
        $reader.Dispose()
    }

    $namespace = [Xml.XmlNamespaceManager]::new($document.NameTable)
    $namespace.AddNamespace("t", $document.DocumentElement.NamespaceURI)
    return [pscustomobject]@{ Document = $document; Namespace = $namespace }
}

function Test-IsFileContentionException([Exception]$Exception) {
    $current = $Exception
    while ($null -ne $current) {
        if ($current -is [IO.IOException]) {
            $nativeCode = $current.HResult -band 0xffff
            if ([Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT) {
                if ($nativeCode -in @(32, 33)) { return $true }
            } elseif ($nativeCode -in @(11, 35)) {
                # EAGAIN/EWOULDBLOCK from the FileShare.None-backed lease.
                return $true
            }
        }
        $current = $current.InnerException
    }
    return $false
}

function Get-PhysicalTempRoot {
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd(
        [IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    $item = Get-Item -LiteralPath $tempRoot -Force
    if (-not $item.PSIsContainer) { throw "TEMP is not a directory: $tempRoot" }
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        $target = ([IO.DirectoryInfo]$item).ResolveLinkTarget($true)
        if ($null -eq $target -or -not $target.Exists) {
            throw "TEMP reparse target could not be resolved: $tempRoot"
        }
        $tempRoot = [IO.Path]::GetFullPath($target.FullName).TrimEnd(
            [IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    }
    return $tempRoot
}

function Get-TempFamily([string]$Name) {
    if ($Name -eq ".net" -or $Name -eq "NuGetScratch" -or $Name -eq "VBCSCompiler" -or
        $Name.StartsWith("MSBuildTemp", [StringComparison]::OrdinalIgnoreCase) -or
        $Name.StartsWith("Microsoft.NET.Workload_", [StringComparison]::OrdinalIgnoreCase)) {
        return "toolchain"
    }
    if ($Name.StartsWith("codenav-", [StringComparison]::OrdinalIgnoreCase)) {
        return "test-codenav"
    }
    if ($Name.StartsWith("Phoenix ", [StringComparison]::Ordinal)) {
        return "test-phoenix"
    }
    if ($Name.StartsWith("PhoenixCodeNav-git-safety-", [StringComparison]::Ordinal)) {
        return "git-safety"
    }
    if ($Name.StartsWith("PhoenixCodeNav.FSharp.Reference.", [StringComparison]::Ordinal)) {
        return "fsharp-reference"
    }
    if ($Name.StartsWith("PhoenixCodeNav-fresh-index-selftest-", [StringComparison]::Ordinal)) {
        return "harness-selftest"
    }
    if ($Name -eq "PhoenixCodeNav.FSharp") { return "fsharp-output" }
    return "unattributed"
}

function New-TempSnapshotEntry([IO.FileSystemInfo]$Item, [string]$Family) {
    return [pscustomobject]@{
        Path = [IO.Path]::GetFullPath($Item.FullName)
        Name = $Item.Name
        Family = $Family
        IsDirectory = $Item -is [IO.DirectoryInfo]
        IsReparsePoint = (($Item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)
        Disposition = "kept"
        Detail = $null
    }
}

function Get-GateTempSnapshot([string]$TempRoot) {
    $comparison = if ([Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT) {
        [StringComparer]::OrdinalIgnoreCase
    } else {
        [StringComparer]::Ordinal
    }
    $snapshot = [Collections.Generic.Dictionary[string, object]]::new($comparison)
    foreach ($item in @(Get-ChildItem -LiteralPath $TempRoot -Force)) {
        $family = Get-TempFamily $item.Name
        $entry = New-TempSnapshotEntry $item $family
        $snapshot[$entry.Path] = $entry
    }
    return ,$snapshot
}

function Remove-EntryNoFollow([string]$Path) {
    $item = Get-Item -LiteralPath $Path -Force
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        if ($item.PSIsContainer) { [IO.Directory]::Delete($Path, $false) }
        else { [IO.File]::Delete($Path) }
        return
    }
    if ($item.PSIsContainer) {
        foreach ($child in @(Get-ChildItem -LiteralPath $Path -Force)) {
            Remove-EntryNoFollow $child.FullName
        }
        if (($item.Attributes -band [IO.FileAttributes]::ReadOnly) -ne 0) {
            $item.Attributes = $item.Attributes -band (-bnot [IO.FileAttributes]::ReadOnly)
        }
        [IO.Directory]::Delete($Path, $false)
    } else {
        if (($item.Attributes -band [IO.FileAttributes]::ReadOnly) -ne 0) {
            $item.Attributes = $item.Attributes -band (-bnot [IO.FileAttributes]::ReadOnly)
        }
        [IO.File]::Delete($Path)
    }
}

function Remove-GateOwnedSnapshotEntry([string]$ParentRoot, [object]$Entry) {
    if (-not (Test-Path -LiteralPath $Entry.Path)) {
        $Entry.Disposition = "removed"
        $Entry.Detail = "already absent"
        return $true
    }

    $parent = [IO.Path]::GetFullPath((Split-Path -Parent $Entry.Path))
    $comparison = if ([Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT) {
        [StringComparison]::OrdinalIgnoreCase
    } else { [StringComparison]::Ordinal }
    if (-not $parent.Equals([IO.Path]::GetFullPath($ParentRoot), $comparison)) {
        throw "candidate parent changed"
    }
    if ($Entry.IsReparsePoint) {
        $Entry.Detail = "refused (reparse point)"
        return $false
    }

    $current = Get-Item -LiteralPath $Entry.Path -Force
    if (($current.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "changed under us: candidate root became a reparse point"
    }
    Remove-EntryNoFollow $Entry.Path
    $Entry.Disposition = "removed"
    return $true
}

function Invoke-StaleGateRootSweep([string]$TempRoot) {
    $staleRoots = @(Get-ChildItem -LiteralPath $TempRoot -Force | Where-Object {
        $_.Name.StartsWith("PhoenixCodeNav-gate-", [StringComparison]::Ordinal) -and
        $_.Name -ne "PhoenixCodeNav-gate-temp-sweep.lock"
    } | ForEach-Object {
        New-TempSnapshotEntry $_ "stale-gate-root"
    } | Sort-Object Path)
    $reaped = 0
    $failures = [Collections.Generic.List[string]]::new()
    foreach ($entry in $staleRoots) {
        try {
            if (Remove-GateOwnedSnapshotEntry $TempRoot $entry) { $reaped++ }
        } catch {
            $failures.Add("remove stale gate root $($entry.Path): $($_.Exception.Message)")
            $entry.Detail = "cleanup failed"
        }
    }

    $survivors = @($staleRoots | Where-Object { Test-Path -LiteralPath $_.Path })
    Write-Host "temp: stale gate roots reaped: $reaped"
    $shownFailures = [Math]::Min($maxFailureLines, $failures.Count)
    for ($index = 0; $index -lt $shownFailures; $index++) {
        Write-Host "TEMP LEAK cleanup failure: $($failures[$index])"
    }
    if ($shownFailures -lt $failures.Count) {
        Write-Host "TEMP LEAK cleanup failure: (+$($failures.Count - $shownFailures) entries omitted)"
    }
    $shownSurvivors = [Math]::Min($maxFailureLines, $survivors.Count)
    for ($index = 0; $index -lt $shownSurvivors; $index++) {
        $entry = $survivors[$index]
        $detail = if ([string]::IsNullOrEmpty($entry.Detail)) { "" } else { " ($($entry.Detail))" }
        Write-Host "TEMP LEAK stale gate root kept: $($entry.Path)$detail — a gate-owned root nobody can reclaim: check for an orphaned gate process holding it, then remove it manually"
    }
    if ($shownSurvivors -lt $survivors.Count) {
        Write-Host "TEMP LEAK stale gate root kept: (+$($survivors.Count - $shownSurvivors) entries omitted) — a gate-owned root nobody can reclaim: check for an orphaned gate process holding it, then remove it manually"
    }

    return [pscustomobject]@{
        Survivors = $survivors.Count
        Failures = $failures.Count
    }
}

function Invoke-GateTempSweep([string]$PrivateTempRoot) {
    # Every direct child belongs to this wrapper by construction: TEMP/TMP/TMPDIR pointed at this
    # private root before any gate child launched. This function never inventories or sweeps the shared user TEMP; only Invoke-StaleGateRootSweep touches it, and only for this wrapper's own PhoenixCodeNav-gate-* roots under the exclusive lease.
    $leaked = @((Get-GateTempSnapshot $PrivateTempRoot).Values | Sort-Object Family, Path)
    $removed = 0
    $failures = [Collections.Generic.List[string]]::new()
    foreach ($entry in $leaked) {
        try {
            if (Remove-GateOwnedSnapshotEntry $PrivateTempRoot $entry) { $removed++ }
        } catch {
            $failures.Add("remove $($entry.Path): $($_.Exception.Message)")
            $entry.Detail = "cleanup failed"
        }
    }

    $survivors = @($leaked | Where-Object { Test-Path -LiteralPath $_.Path })
    if ($survivors.Count -eq 0) {
        try { [IO.Directory]::Delete($PrivateTempRoot, $false) }
        catch { $failures.Add("remove private root ${PrivateTempRoot}: $($_.Exception.Message)") }
    }

    $toolchainRemoved = @($leaked | Where-Object {
        $_.Family -eq "toolchain" -and $_.Disposition -eq "removed"
    }).Count
    Write-Host "temp: $($leaked.Count) residual entries in private gate root, $removed removed, $($survivors.Count) survivors, $($failures.Count) cleanup failures"
    Write-Host "temp: toolchain scratch removed: $toolchainRemoved"
    $shownFailures = [Math]::Min($maxFailureLines, $failures.Count)
    for ($index = 0; $index -lt $shownFailures; $index++) {
        Write-Host "TEMP LEAK cleanup failure: $($failures[$index])"
    }
    if ($shownFailures -lt $failures.Count) {
        Write-Host "TEMP LEAK cleanup failure: (+$($failures.Count - $shownFailures) entries omitted)"
    }

    $phoenixLeaks = @($leaked | Where-Object { $_.Family -ne "toolchain" })
    $shownEntries = [Math]::Min($maxFailureLines, $phoenixLeaks.Count)
    for ($index = 0; $index -lt $shownEntries; $index++) {
        $entry = $phoenixLeaks[$index]
        $detail = if ([string]::IsNullOrEmpty($entry.Detail)) { "" } else { " ($($entry.Detail))" }
        Write-Host "TEMP LEAK $($entry.Disposition): [$($entry.Family)] $($entry.Path)$detail"
    }
    if ($shownEntries -lt $phoenixLeaks.Count) {
        Write-Host "TEMP LEAK: (+$($phoenixLeaks.Count - $shownEntries) entries omitted)"
    }

    return [pscustomobject]@{
        Survivors = $survivors.Count
        Failures = $failures.Count
    }
}

$tempRoot = $null
$tempLease = $null
$privateTempRoot = $null
$originalTemp = [Environment]::GetEnvironmentVariable("TEMP", "Process")
$originalTmp = [Environment]::GetEnvironmentVariable("TMP", "Process")
$originalTmpDir = [Environment]::GetEnvironmentVariable("TMPDIR", "Process")
try {
    try {
        $tempRoot = Get-PhysicalTempRoot
        $tempLeasePath = Join-Path $tempRoot "PhoenixCodeNav-gate-temp-sweep.lock"
        if (Test-Path -LiteralPath $tempLeasePath) {
            $leaseItem = Get-Item -LiteralPath $tempLeasePath -Force
            if ($leaseItem.PSIsContainer -or
                ($leaseItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Phoenix gate TEMP lease is not a plain file: $tempLeasePath"
            }
        }
        # Ownership is the exclusive open handle, never the file's existence. A crashed wrapper
        # releases the OS handle, so its inert lease leaf cannot block a later run.
        try {
            $tempLease = [IO.File]::Open($tempLeasePath, [IO.FileMode]::OpenOrCreate,
                [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
        } catch {
            if (Test-IsFileContentionException $_.Exception) {
                Write-Host "temp: concurrent Phoenix gate owns the cleanup lease"
                Write-Host "gates not run: concurrent Phoenix gate"
                $incompleteGates.Add("gates not run: concurrent Phoenix gate")
            } else { throw }
        }
        if ($null -ne $tempLease) {
            $staleGateSweep = Invoke-StaleGateRootSweep $tempRoot
            if ($staleGateSweep.Survivors -gt 0 -or $staleGateSweep.Failures -gt 0) {
                $incompleteGates.Add("TEMP cleanup incomplete")
            }
            do {
                $privateTempRoot = Join-Path $tempRoot `
                    ("PhoenixCodeNav-gate-" + [Guid]::NewGuid().ToString("N").Substring(0, 8))
            } while (Test-Path -LiteralPath $privateTempRoot)
            [IO.Directory]::CreateDirectory($privateTempRoot) | Out-Null
            $privateRootItem = Get-Item -LiteralPath $privateTempRoot -Force
            if (-not $privateRootItem.PSIsContainer -or
                ($privateRootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "private gate TEMP root is not a plain directory: $privateTempRoot"
            }
            [Environment]::SetEnvironmentVariable("TEMP", $privateTempRoot, "Process")
            [Environment]::SetEnvironmentVariable("TMP", $privateTempRoot, "Process")
            if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
                [Environment]::SetEnvironmentVariable("TMPDIR", $privateTempRoot, "Process")
            }
        }
    } catch {
        Write-Host "TEMP LEAK: measurement setup failed: $($_.Exception.Message)"
        Write-Host "gates not run: TEMP measurement setup failed"
        $incompleteGates.Add("gates not run: TEMP measurement setup failed")
        if ($null -ne $privateTempRoot -and (Test-Path -LiteralPath $privateTempRoot)) {
            try { Remove-EntryNoFollow $privateTempRoot }
            catch { Write-Host "TEMP LEAK: setup cleanup failed: $($_.Exception.Message)" }
        }
        $privateTempRoot = $null
        if ($null -ne $tempLease) {
            $tempLease.Dispose()
            $tempLease = $null
        }
    }

    if ($null -ne $tempLease) {
        try {
            Set-Location -LiteralPath $repoRoot

$hasDotnet = Assert-Command "dotnet"
$hasSolution = Assert-Prerequisite "solution" $solutionPath
$hasMcpScript = Assert-Prerequisite "external MCP gate" `
    (Join-Path $repoRoot "scripts\test-roslyn-mcp.ps1") (-not $SkipMcp)
$hasNode = Assert-Command "node" (-not $SkipWebsite)
$hasWebsiteVerifier = Assert-Prerequisite "website verifier" `
    (Join-Path $repoRoot "website\verify.mjs") (-not $SkipWebsite)

if ($hasDotnet -and $hasSolution) {
    $restoreOutput = Convert-ToLines @(& dotnet restore $solutionPath --nologo -v:q 2>&1)
    $restoreExit = $LASTEXITCODE
    if ($restoreExit -eq 0) {
        Write-Host "restore: ok"
    } else {
        Write-Host "restore failures:"
        foreach ($line in $restoreOutput) { Write-Host $line }
        $failedGates.Add("restore")
    }

    $formatOutput = Convert-ToLines @(& dotnet format $solutionPath --verify-no-changes `
        --no-restore --verbosity diagnostic 2>&1)
    $formatExit = $LASTEXITCODE
    $formatOffenses = @($formatOutput | Where-Object {
        $_ -match '\.(cs|vb)\(\d+,\d+\):\s+(error|warning)\s+'
    })
    if ($formatExit -eq 0) {
        Write-Host "format: clean"
    } else {
        Write-Host "format: offenses"
        if ($formatOffenses.Count -gt 0) {
            foreach ($line in $formatOffenses) { Write-Host $line }
        } else {
            foreach ($line in $formatOutput) { Write-Host $line }
        }
        $failedGates.Add("format")
    }

    $buildOutput = Convert-ToLines @(& dotnet build $solutionPath -c Release --nologo `
        --no-restore -v:m 2>&1)
    $buildExit = $LASTEXITCODE
    $warningSummary = $buildOutput | Where-Object { $_ -match '^\s*(\d+)\s+Warning\(s\)\s*$' } |
        Select-Object -Last 1
    $errorSummary = $buildOutput | Where-Object { $_ -match '^\s*(\d+)\s+Error\(s\)\s*$' } |
        Select-Object -Last 1
    $diagnosticLines = @($buildOutput | Where-Object {
        $_ -match '(?i)(:\s*(warning|error)\s+[A-Z]*\d*\s*:|\b(warning|error)\s+(MSB|NETSDK|NU|FS|CS)\d+\b)'
    })
    $warnings = if ($warningSummary -match '^\s*(\d+)') { [int]$Matches[1] } else {
        @($diagnosticLines | Where-Object {
            $_ -match '(?i)(:\s*warning\s+|\bwarning\s+(MSB|NETSDK|NU|FS|CS)\d+\b)'
        }).Count
    }
    $errors = if ($errorSummary -match '^\s*(\d+)') { [int]$Matches[1] } else {
        @($diagnosticLines | Where-Object { $_ -match '(?i)(:\s*error\s+|\berror\s+(MSB|NETSDK|NU|FS|CS)\d+\b)' }).Count
    }
    $buildSummaryMissing = [string]::IsNullOrWhiteSpace([string]$warningSummary) -or
        [string]::IsNullOrWhiteSpace([string]$errorSummary)
    Write-Host "build: $warnings warnings, $errors errors"
    if ($warnings -gt 0 -or $errors -gt 0) {
        foreach ($line in $diagnosticLines) { Write-Host $line }
    }
    if ($buildSummaryMissing) {
        Write-Host "build summary unavailable; refusing to infer a clean build"
    }
    if ($buildExit -ne 0 -or $warnings -gt 0 -or $errors -gt 0 -or
        $buildSummaryMissing) {
        if ($diagnosticLines.Count -eq 0 -or $buildSummaryMissing) {
            foreach ($line in $buildOutput) { Write-Host $line }
        }
        $failedGates.Add("build")
    }

    [IO.Directory]::CreateDirectory($resultsDirectory) | Out-Null
    Get-ChildItem -LiteralPath $resultsDirectory -File -Filter "*.trx" |
        ForEach-Object { Remove-Item -Force -LiteralPath $_.FullName }

    $testOutput = Convert-ToLines @(& dotnet test $solutionPath -c Release --no-build `
        --no-restore --nologo -v:q --logger "trx;LogFilePrefix=gate" `
        --results-directory $resultsDirectory 2>&1)
    $testExit = $LASTEXITCODE
    $trxFiles = @(Get-ChildItem -LiteralPath $resultsDirectory -File -Filter "*.trx" |
        Sort-Object Name)
    $testRows = [Collections.Generic.List[object]]::new()
    $trxReadFailed = $false

    foreach ($trx in $trxFiles) {
        try {
            $parsed = Read-Trx $trx.FullName
            $definitions = @{}
            foreach ($definition in @($parsed.Document.SelectNodes("//t:UnitTest", $parsed.Namespace))) {
                $method = $definition.SelectSingleNode("t:TestMethod", $parsed.Namespace)
                $assembly = if ($null -ne $method -and -not [string]::IsNullOrWhiteSpace($method.codeBase)) {
                    [IO.Path]::GetFileNameWithoutExtension([string]$method.codeBase)
                } elseif (-not [string]::IsNullOrWhiteSpace($definition.storage)) {
                    [IO.Path]::GetFileNameWithoutExtension([string]$definition.storage)
                } else {
                    [IO.Path]::GetFileNameWithoutExtension($trx.Name)
                }
                $definitions[[string]$definition.id] = $assembly
            }
            foreach ($result in @($parsed.Document.SelectNodes("//t:UnitTestResult", $parsed.Namespace))) {
                $assembly = $definitions[[string]$result.testId]
                if ([string]::IsNullOrWhiteSpace($assembly)) {
                    $assembly = [IO.Path]::GetFileNameWithoutExtension($trx.Name)
                }
                $errorInfo = $result.SelectSingleNode("t:Output/t:ErrorInfo", $parsed.Namespace)
                $standardOutput = $result.SelectSingleNode("t:Output/t:StdOut", $parsed.Namespace)
                $standardError = $result.SelectSingleNode("t:Output/t:StdErr", $parsed.Namespace)
                $message = if ($null -ne $errorInfo) { [string]$errorInfo.Message } else { "" }
                $stack = if ($null -ne $errorInfo) { [string]$errorInfo.StackTrace } else { "" }
                $skipReason = if (-not [string]::IsNullOrWhiteSpace($message)) {
                    $message
                } elseif ($null -ne $standardOutput -and -not [string]::IsNullOrWhiteSpace($standardOutput.InnerText)) {
                    $standardOutput.InnerText
                } else {
                    "reason not recorded in TRX"
                }
                $testRows.Add([pscustomobject]@{
                    Assembly = $assembly
                    Outcome = [string]$result.outcome
                    Name = [string]$result.testName
                    Message = $message
                    Stack = $stack
                    SkipReason = $skipReason
                    StandardOutput = if ($null -ne $standardOutput) { $standardOutput.InnerText } else { "" }
                    StandardError = if ($null -ne $standardError) { $standardError.InnerText } else { "" }
                    Trx = $trx.FullName
                })
            }
        } catch {
            Write-Host "trx parse failed: $($trx.FullName): $($_.Exception.Message)"
            $failedGates.Add("test TRX parse")
            $trxReadFailed = $true
        }
    }

    foreach ($group in @($testRows | Group-Object Assembly | Sort-Object Name)) {
        $passed = @($group.Group | Where-Object Outcome -eq "Passed").Count
        $failed = @($group.Group | Where-Object Outcome -eq "Failed").Count
        $skipped = @($group.Group | Where-Object Outcome -eq "NotExecuted").Count
        Write-Host "tests: $($group.Name): $passed passed, $failed failed, $skipped skipped, $($group.Count) total"
    }
    foreach ($failure in @($testRows | Where-Object Outcome -eq "Failed" |
        Sort-Object Assembly, Name)) {
        Write-BoundedFailure $failure.Name $failure.Message $failure.Stack $failure.Trx
    }
    foreach ($skip in @($testRows | Where-Object Outcome -eq "NotExecuted" |
        Sort-Object Assembly, Name)) {
        $reason = ($skip.SkipReason -split "`r?`n" | ForEach-Object { $_.Trim() } |
            Where-Object { $_ }) -join " | "
        Write-Host "SKIPPED TEST: $($skip.Name): $reason"
    }
    $reportedCleanupDiagnostics = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::Ordinal)
    $shownCleanupDiagnostics = 0
    foreach ($row in $testRows) {
        foreach ($line in @(($row.StandardOutput + "`n" + $row.StandardError) -split "`r?`n" |
            Where-Object { $_ -match 'Test cleanup could not remove' })) {
            $rawDiagnostic = $line.Trim()
            if ($reportedCleanupDiagnostics.Add($rawDiagnostic) -and
                $shownCleanupDiagnostics -lt $maxFailureLines) {
                Write-Host "TEMP LEAK: [$($row.Assembly)] $($row.Name): $rawDiagnostic"
                $shownCleanupDiagnostics++
            }
        }
    }
    foreach ($line in @($testOutput | Where-Object { $_ -match 'Test cleanup could not remove' })) {
        $rawDiagnostic = $line.Trim()
        if ($reportedCleanupDiagnostics.Add($rawDiagnostic) -and
            $shownCleanupDiagnostics -lt $maxFailureLines) {
            Write-Host "TEMP LEAK: [test process] $rawDiagnostic"
            $shownCleanupDiagnostics++
        }
    }
    if ($reportedCleanupDiagnostics.Count -gt $shownCleanupDiagnostics) {
        Write-Host "TEMP LEAK: (+$($reportedCleanupDiagnostics.Count - $shownCleanupDiagnostics) cleanup diagnostics omitted)"
    }
    foreach ($trx in $trxFiles) { Write-Host "TRX: $($trx.FullName)" }

    if ($testExit -ne 0 -or $trxFiles.Count -eq 0 -or
        @($testRows | Where-Object Outcome -eq "Failed").Count -gt 0 -or $trxReadFailed) {
        if ($testExit -ne 0 -or $trxFiles.Count -eq 0) {
            Write-Host "test infrastructure output:"
            foreach ($line in $testOutput) { Write-Host $line }
        }
        $failedGates.Add("tests")
    }
}

if ($SkipMcp) {
    Write-Host "mcp: SKIPPED"
    $incompleteGates.Add("external MCP skipped")
} elseif ($hasMcpScript) {
    $mcpOutput = Convert-ToLines @(& pwsh -NoProfile -File `
        (Join-Path $repoRoot "scripts\test-roslyn-mcp.ps1") 2>&1)
    $mcpExit = $LASTEXITCODE
    $tallyIndex = -1
    for ($index = 0; $index -lt $mcpOutput.Count; $index++) {
        if ($mcpOutput[$index] -match '^External MCP integration:') { $tallyIndex = $index }
    }
    if ($tallyIndex -ge 0) {
        Write-Host ($mcpOutput[$tallyIndex] -replace '^External MCP integration:', 'mcp:')
        if ($tallyIndex + 1 -lt $mcpOutput.Count -and
            $mcpOutput[$tallyIndex + 1] -match '^Evidence:') {
            Write-Host $mcpOutput[$tallyIndex + 1]
        }
    } else {
        Write-Host "mcp: tally unavailable"
    }
    if ($mcpExit -ne 0) {
        Write-Host "MCP FAILURES:"
        $failureStart = if ($tallyIndex -ge 0) { $tallyIndex + 1 } else { 0 }
        foreach ($line in $mcpOutput[$failureStart..($mcpOutput.Count - 1)]) {
            if ($line -notmatch '^Evidence:') { Write-Host $line }
        }
        $failedGates.Add("external MCP")
    }
}

if ($SkipWebsite) {
    Write-Host "website: SKIPPED"
    $incompleteGates.Add("website skipped")
} elseif ($hasNode -and $hasWebsiteVerifier) {
    $websiteOutput = Convert-ToLines @(& node (Join-Path $repoRoot "website\verify.mjs") 2>&1)
    $websiteExit = $LASTEXITCODE
    if ($websiteExit -eq 0) {
        foreach ($line in $websiteOutput | Where-Object { $_ -match '^Website .* verification passed' }) {
            Write-Host ($line -replace '^Website ', 'website: ')
        }
    } else {
        Write-Host "website failures:"
        foreach ($line in $websiteOutput) { Write-Host $line }
        $failedGates.Add("website")
    }
}
        } finally {
            if ($null -ne $privateTempRoot) {
                try {
                    $tempSweep = Invoke-GateTempSweep $privateTempRoot
                    if ($tempSweep.Survivors -gt 0 -or $tempSweep.Failures -gt 0) {
                        $incompleteGates.Add("TEMP cleanup incomplete")
                    }
                }
                catch {
                    Write-Host "TEMP LEAK: measurement/sweep failed: $($_.Exception.Message)"
                    $incompleteGates.Add("TEMP measurement/sweep failed")
                }
            }
        }
    }
} finally {
    [Environment]::SetEnvironmentVariable("TEMP", $originalTemp, "Process")
    [Environment]::SetEnvironmentVariable("TMP", $originalTmp, "Process")
    [Environment]::SetEnvironmentVariable("TMPDIR", $originalTmpDir, "Process")
    if ($null -ne $tempLease) { $tempLease.Dispose() }
}

if ($failedGates.Count -eq 0 -and $incompleteGates.Count -eq 0) {
    Write-Host "GATES GREEN"
    exit 0
}

if ($failedGates.Count -eq 0) {
    $incompleteReasons = @($incompleteGates | Select-Object -Unique)
    $postRunCleanupReasons = @("TEMP cleanup incomplete", "TEMP measurement/sweep failed")
    $onlyPostRunCleanupReasons = @($incompleteReasons | Where-Object {
        $_ -notin $postRunCleanupReasons
    }).Count -eq 0
    $incomplete = $incompleteReasons -join "; "
    if ($onlyPostRunCleanupReasons) {
        $incomplete = "gates passed; $incomplete"
    }
    Write-Host "GATES INCOMPLETE: $incomplete"
    exit 1
}

$failed = @($failedGates | Select-Object -Unique) -join ", "
Write-Host "GATES RED: $failed"
exit 1
