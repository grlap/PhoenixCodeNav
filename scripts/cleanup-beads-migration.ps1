[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"

$repositoryRoot = (& git rev-parse --show-toplevel 2>$null).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($repositoryRoot)) {
    throw "Run this script from inside the PhoenixCodeNav Git repository."
}

$configuredHooksPath = (& git config --local --get core.hooksPath 2>$null)
if ($LASTEXITCODE -eq 1 -or [string]::IsNullOrWhiteSpace($configuredHooksPath)) {
    Write-Host "No repository-local core.hooksPath is configured."
    exit 0
}
if ($LASTEXITCODE -ne 0) {
    throw "Unable to read the repository-local core.hooksPath."
}

$configuredHooksPath = $configuredHooksPath.Trim()
$resolvedHooksPath = if ([System.IO.Path]::IsPathRooted($configuredHooksPath)) {
    [System.IO.Path]::GetFullPath($configuredHooksPath)
} else {
    [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $configuredHooksPath))
}
$legacyHooksPath = [System.IO.Path]::GetFullPath(
    (Join-Path $repositoryRoot ".beads/hooks"))
$comparison = if ($IsWindows) {
    [System.StringComparison]::OrdinalIgnoreCase
} else {
    [System.StringComparison]::Ordinal
}

if (-not [string]::Equals($resolvedHooksPath, $legacyHooksPath, $comparison)) {
    Write-Host "Leaving custom core.hooksPath unchanged: $configuredHooksPath"
    exit 0
}

& git config --local --unset core.hooksPath
if ($LASTEXITCODE -ne 0) {
    throw "Unable to remove the legacy repository-local core.hooksPath."
}

Write-Host "Removed legacy core.hooksPath: $configuredHooksPath"
