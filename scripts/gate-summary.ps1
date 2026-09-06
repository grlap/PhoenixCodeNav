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
    $incompleteGates.Add("external MCP")
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
    $incompleteGates.Add("website")
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

if ($failedGates.Count -eq 0 -and $incompleteGates.Count -eq 0) {
    Write-Host "GATES GREEN"
    exit 0
}

if ($failedGates.Count -eq 0) {
    $incomplete = @($incompleteGates | Select-Object -Unique) -join ", "
    Write-Host "GATES INCOMPLETE: $incomplete skipped"
    exit 1
}

$failed = @($failedGates | Select-Object -Unique) -join ", "
Write-Host "GATES RED: $failed"
exit 1
