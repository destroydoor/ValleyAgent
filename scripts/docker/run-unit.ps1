<#
.SYNOPSIS
    Build and run ValleyAgent unit tests inside a Docker container.
    Game folder is mounted read-only; no game files are baked into the image.

.PARAMETER GamePath
    Path to the Stardew Valley game folder. Auto-detects common Windows
    Steam/GOG locations when omitted.

.PARAMETER NoCache
    Pass --no-cache to docker build.

.PARAMETER SkipBuild
    Skip image build; run tests with the existing image.

.EXAMPLE
    .\scripts\docker\run-unit.ps1
    .\scripts\docker\run-unit.ps1 -GamePath "D:\Games\Stardew Valley"
    .\scripts\docker\run-unit.ps1 -NoCache -SkipBuild
#>
[CmdletBinding()]
param(
    [string]$GamePath,
    [switch]$NoCache,
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'

# ── Resolve game path ─────────────────────────────────────────
if (-not $GamePath) {
    $candidates = @(
        "C:\Program Files (x86)\Steam\steamapps\common\Stardew Valley"
        "C:\Program Files\Steam\steamapps\common\Stardew Valley"
        "C:\Program Files\GOG Games\Stardew Valley"
        "D:\SteamLibrary\steamapps\common\Stardew Valley"
        "$PSScriptRoot\..\..\Stardew Valley"
    )
    foreach ($c in $candidates) {
        if (Test-Path "$c\Stardew Valley.dll") { $GamePath = $c; break }
    }
    if (-not $GamePath) {
        Write-Error "Cannot auto-detect game folder. Pass -GamePath explicitly."
        exit 1
    }
}
$GamePath = (Resolve-Path $GamePath).Path

if (-not (Test-Path "$GamePath\Stardew Valley.dll")) {
    Write-Error "Game folder '$GamePath' does not contain Stardew Valley.dll"
    exit 1
}
Write-Host "Game folder: $GamePath" -ForegroundColor Cyan

# ── Repo root & image tag ─────────────────────────────────────
$RepoRoot  = (Resolve-Path "$PSScriptRoot\..\..").Path
$ImageTag  = "valleytalk-unittests:latest"

# ── Build image ───────────────────────────────────────────────
if (-not $SkipBuild) {
    Write-Host "Building test image..." -ForegroundColor Yellow

    $buildArgs = @("build", "-f", "$RepoRoot\docker\Dockerfile.unittests", "-t", $ImageTag, $RepoRoot)
    if ($NoCache) { $buildArgs += "--no-cache" }

    docker @buildArgs
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Docker build failed (exit $LASTEXITCODE)."
        exit $LASTEXITCODE
    }
}

# ── NuGet cache volume (persists across runs) ─────────────────
$NugetCacheDir = "$RepoRoot\.tmp\dotnet-home"
if (-not (Test-Path $NugetCacheDir)) {
    New-Item -ItemType Directory -Path $NugetCacheDir -Force | Out-Null
}

# ── Run tests ─────────────────────────────────────────────────
Write-Host "Running unit tests (game mount read-only)..." -ForegroundColor Yellow

docker run --rm `
    -v "${GamePath}:/game:ro" `
    -v "${NugetCacheDir}:/root/.nuget/packages" `
    $ImageTag

$exitCode = $LASTEXITCODE

if ($exitCode -eq 0) {
    Write-Host "All tests passed." -ForegroundColor Green
} else {
    Write-Host "Tests failed (exit $exitCode)." -ForegroundColor Red
}
exit $exitCode
