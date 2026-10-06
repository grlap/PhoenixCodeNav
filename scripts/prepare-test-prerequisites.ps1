[CmdletBinding()]
param(
    [switch]$SkipMcp,
    [switch]$SkipWebsite
)

$ErrorActionPreference = "Stop"
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$failures = [Collections.Generic.List[string]]::new()
$packagesRoot = if ([string]::IsNullOrWhiteSpace($env:NUGET_PACKAGES)) {
    Join-Path ([Environment]::GetFolderPath("UserProfile")) ".nuget/packages"
} else { [IO.Path]::GetFullPath($env:NUGET_PACKAGES) }

function Require-File([string]$Name, [string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        $failures.Add("$Name missing: $Path")
    }
}

function Invoke-PreparationCommand([string]$Name, [string]$Command, [string[]]$Arguments) {
    $output = @(& $Command @Arguments 2>&1 | ForEach-Object { [string]$_ })
    if ($LASTEXITCODE -ne 0) {
        $failures.Add("$Name exited $LASTEXITCODE")
        foreach ($line in $output) { Write-Host $line }
    }
    return ,$output
}

function Write-PreparationFailures {
    foreach ($failure in $failures) { Write-Host "prerequisite: $failure" }
}

try {
    Push-Location -LiteralPath $repoRoot
    foreach ($command in @("dotnet", "git", "pwsh") + $(if (-not $SkipWebsite) { "node" })) {
        if ($null -eq (Get-Command $command -ErrorAction SilentlyContinue)) {
            $failures.Add("command '$command' is unavailable; install it and put it on PATH")
        }
    }
    Require-File "solution" (Join-Path $repoRoot "PhoenixCodeNav.sln")
    Require-File "fixture restore project" (Join-Path $PSScriptRoot "TestPrerequisites.csproj")
    if (-not $SkipWebsite) { Require-File "website verifier" (Join-Path $repoRoot "website/verify.mjs") }
    if (-not $SkipMcp) {
        Require-File "external MCP gate" (Join-Path $PSScriptRoot "test-roslyn-mcp.ps1")
        foreach ($name in @("roslyn", "fsharp")) {
            Require-File "$name baseline" (Join-Path $repoRoot "tests/integration/$name-mcp-baseline.json")
        }
    }
    if ($failures.Count -gt 0) { Write-PreparationFailures; exit 1 }

    $sdkVersion = (Invoke-PreparationCommand "SDK discovery" "dotnet" @("--version"))[0]
    if ($sdkVersion -notmatch '^(\d+)\.' -or [int]$Matches[1] -lt 10) {
        $failures.Add(".NET SDK 10 or later is required; selected SDK: $sdkVersion")
    }
    $runtimes = Invoke-PreparationCommand "runtime discovery" "dotnet" @("--list-runtimes")
    if (-not @($runtimes | Where-Object { $_ -match '^Microsoft.NETCore.App 10\.' }).Count) {
        $failures.Add(".NET 10 runtime is required to run the test assemblies")
    }

    # Use exact target-framework references, never newer references as a substitute.
    $dotnetRoots = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    if (-not [string]::IsNullOrWhiteSpace($env:DOTNET_ROOT)) { $dotnetRoots.Add($env:DOTNET_ROOT) | Out-Null }
    $programFiles = [Environment]::GetFolderPath("ProgramFiles")
    if (-not [string]::IsNullOrWhiteSpace($programFiles)) {
        $dotnetRoots.Add((Join-Path $programFiles "dotnet")) | Out-Null
    }
    foreach ($sdk in (Invoke-PreparationCommand "SDK paths" "dotnet" @("--list-sdks"))) {
        if ($sdk -match '\[(.+)\]$') { $dotnetRoots.Add((Split-Path -Parent $Matches[1])) | Out-Null }
    }
    foreach ($framework in @("net8.0", "net9.0", "net10.0", "netstandard2.1")) {
        $pack = if ($framework -eq "netstandard2.1") { "NETStandard.Library.Ref" } else { "Microsoft.NETCore.App.Ref" }
        $found = $false
        foreach ($root in $dotnetRoots) {
            $packRoot = Join-Path $root "packs/$pack"
            if (-not (Test-Path -LiteralPath $packRoot -PathType Container)) { continue }
            foreach ($version in (Get-ChildItem -LiteralPath $packRoot -Directory)) {
                if (Test-Path -LiteralPath (Join-Path $version.FullName "ref/$framework/netstandard.dll") -PathType Leaf) {
                    $found = $true
                }
            }
        }
        if (-not $found) { $failures.Add("exact $framework SDK reference pack missing; install the corresponding .NET SDK/reference pack") }
    }

    if (-not $SkipMcp) {
        foreach ($name in @("roslyn", "fsharp")) {
            $baseline = Get-Content -Raw -LiteralPath (Join-Path $repoRoot "tests/integration/$name-mcp-baseline.json") | ConvertFrom-Json
            $checkout = Join-Path $repoRoot "external/$name"
            $gitlink = (Invoke-PreparationCommand "$name gitlink" "git" @("rev-parse", "HEAD:external/$name"))[0]
            $head = (Invoke-PreparationCommand "$name checkout" "git" @("-C", $checkout, "rev-parse", "HEAD"))[0]
            if ($gitlink -ne $baseline."${name}Commit" -or $head -ne $gitlink) {
                $failures.Add("$name checkout/gitlink differs from the locked baseline; initialize the pinned submodule before running gates")
            }
            $status = Invoke-PreparationCommand "$name checkout status" "git" @("-C", $checkout, "--no-optional-locks", "status", "--porcelain=v1", "--untracked-files=all")
            if (@($status | Where-Object { $_ -and $_ -notmatch '^\?\? \.codenav/' }).Count -gt 0) {
                $failures.Add("$name checkout has changes outside .codenav; restore its pinned clean checkout before running gates")
            }
        }
    }

    # Test on the filesystem/TEMP actually used by the tests, without elevation on Windows.
    $probeRoot = Join-Path ([IO.Path]::GetTempPath()) ("Phoenix-prerequisite-" + [Guid]::NewGuid().ToString("N"))
    $probeTarget = Join-Path $probeRoot "target"
    $probeLink = Join-Path $probeRoot "link"
    try {
        [IO.Directory]::CreateDirectory($probeTarget) | Out-Null
        $linkType = if ($IsWindows) { "Junction" } else { "SymbolicLink" }
        New-Item -ItemType $linkType -Path $probeLink -Target $probeTarget -Force | Out-Null
        [IO.File]::WriteAllText((Join-Path $probeTarget "probe"), "ready")
        if ([IO.File]::ReadAllText((Join-Path $probeLink "probe")) -ne "ready") { throw "link target is unreadable" }
    } catch { $failures.Add("directory-link prerequisite failed in ${probeRoot}: $($_.Exception.Message)") }
    finally {
        # Delete the link itself, then the known leaf and empty owned directories. Never recurse.
        if (Test-Path -LiteralPath $probeLink) { [IO.Directory]::Delete($probeLink, $false) }
        if (Test-Path -LiteralPath (Join-Path $probeTarget "probe")) { [IO.File]::Delete((Join-Path $probeTarget "probe")) }
        if (Test-Path -LiteralPath $probeTarget) { [IO.Directory]::Delete($probeTarget, $false) }
        if (Test-Path -LiteralPath $probeRoot) { [IO.Directory]::Delete($probeRoot, $false) }
    }
    if ($failures.Count -gt 0) { Write-PreparationFailures; exit 1 }

    # Match Phoenix's cache rule even if NuGet.Config selects a different globalPackagesFolder.
    Write-Host "prerequisites: SDK $sdkVersion; package cache $packagesRoot"
    foreach ($project in @((Join-Path $repoRoot "PhoenixCodeNav.sln"), (Join-Path $PSScriptRoot "TestPrerequisites.csproj"))) {
        Invoke-PreparationCommand "restore $project" "dotnet" @("restore", $project, "--packages", $packagesRoot, "--nologo", "-v:q") | Out-Null
    }
    Require-File "netstandard2.0 fixture reference" (Join-Path $packagesRoot "netstandard.library/2.0.3/build/netstandard2.0/ref/netstandard.dll")
    Require-File "netstandard2.0 fixture core reference" (Join-Path $packagesRoot "netstandard.library/2.0.3/build/netstandard2.0/ref/mscorlib.dll")
    if (-not $SkipMcp) {
        Require-File "pinned net472 package reference" (Join-Path $packagesRoot "microsoft.netframework.referenceassemblies.net472/1.0.3/build/.NETFramework/v4.7.2/mscorlib.dll")
    }
    if ($failures.Count -gt 0) { Write-PreparationFailures; exit 1 }
    Write-Host "prerequisites: ready (solution and fixture packages restored; exact framework references, directory links, and gate inputs checked)"
    exit 0
} catch {
    Write-Host "prerequisite: $($_.Exception.Message)"
    exit 1
} finally { Pop-Location }
