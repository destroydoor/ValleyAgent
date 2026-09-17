#requires -Version 5.1
<#
.SYNOPSIS
    Build and deploy ValleyAgent + ValleyAgent.TestMod to Stardew Valley Mods folder.

.DESCRIPTION
    This script compiles both projects and handles the "file locked" issue when
    Stardew Valley / SMAPI is running. If the game is running, it will be closed
    automatically before deployment.

.PARAMETER StartGame
    Launch Stardew Valley via SMAPI after successful build.

.PARAMETER NoKill
    Do not kill the game process even if it's running. Build will fail if DLLs are locked.

.PARAMETER Configuration
    Build configuration: Debug (default) or Release.

.EXAMPLE
    .\build-deploy.ps1
    .\build-deploy.ps1 -StartGame
    .\build-deploy.ps1 -Configuration Release
#>
[CmdletBinding()]
param(
    [switch]$StartGame,
    [switch]$NoKill,
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug"
)

$ErrorActionPreference = "Stop"

# ── Paths ───────────────────────────────────────────────────────────
# 仓库根 = scripts\build 的上两级（$PSScriptRoot 是本脚本所在目录）
$ProjectRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
. "$PSScriptRoot\..\lib\paths.ps1"
$GamePath    = Get-GamePath
$SMAPIExe    = Join-Path $GamePath "StardewModdingAPI.exe"
$ModsDir     = Join-Path $GamePath "Mods"
$ValleyAgentDir      = Join-Path $ProjectRoot "src\ValleyAgent"
$ValleyAgentTestDir  = Join-Path $ProjectRoot "src\ValleyAgent.TestMod"

# ── Helper: Write colored log ───────────────────────────────────────
function Write-Log([string]$Message, [string]$Level = "Info") {
    $color = switch ($Level) {
        "Success" { "Green" }
        "Warn"    { "Yellow" }
        "Error"   { "Red" }
        default   { "White" }
    }
    Write-Host "[$Level] $Message" -ForegroundColor $color
}

# ── Step 1: Check if game is running ────────────────────────────────
$gameProcs = Get-Process -Name "StardewModdingAPI", "Stardew Valley" -ErrorAction SilentlyContinue
if ($gameProcs) {
    $procNames = ($gameProcs | Select-Object -ExpandProperty ProcessName) -join ", "
    if ($NoKill) {
        Write-Log "Game is running ($procNames) but -NoKill was specified. Build may fail if DLLs are locked." "Warn"
    } else {
        Write-Log "Game is running ($procNames). Stopping to unlock DLLs..." "Warn"
        $gameProcs | Stop-Process -Force
        Start-Sleep -Seconds 2
        Write-Log "Game processes terminated." "Success"
    }
}

# ── Step 2: Build ValleyAgent ───────────────────────────────────────
Write-Log "Building ValleyAgent ($Configuration)..."
try {
    dotnet build "$ValleyAgentDir\ValleyAgent.csproj" -c $Configuration --verbosity quiet
    if ($LASTEXITCODE -ne 0) { throw "ValleyAgent build failed with exit code $LASTEXITCODE" }
    Write-Log "ValleyAgent build succeeded." "Success"
} catch {
    Write-Log $_.Exception.Message "Error"
    exit 1
}

# ── Step 3: Build ValleyAgent.TestMod ───────────────────────────────
Write-Log "Building ValleyAgent.TestMod ($Configuration)..."
try {
    dotnet build "$ValleyAgentTestDir\ValleyAgent.TestMod.csproj" -c $Configuration --verbosity quiet
    if ($LASTEXITCODE -ne 0) { throw "ValleyAgent.TestMod build failed with exit code $LASTEXITCODE" }
    Write-Log "ValleyAgent.TestMod build succeeded." "Success"
} catch {
    Write-Log $_.Exception.Message "Error"
    exit 1
}

# ── Step 4: Verify deployment ───────────────────────────────────────
$deployedDll = Join-Path $ModsDir "ValleyAgent\ValleyAgent.dll"
$deployedTestDll = Join-Path $ModsDir "ValleyAgent.TestMod\ValleyAgent.TestMod.dll"

if (Test-Path $deployedDll) {
    $ts = (Get-Item $deployedDll).LastWriteTime
    Write-Log "ValleyAgent.dll deployed at $ts" "Success"
} else {
    Write-Log "ValleyAgent.dll not found in Mods folder — deployment may have failed." "Warn"
}

if (Test-Path $deployedTestDll) {
    $ts = (Get-Item $deployedTestDll).LastWriteTime
    Write-Log "ValleyAgent.TestMod.dll deployed at $ts" "Success"
} else {
    Write-Log "ValleyAgent.TestMod.dll not found in Mods folder — deployment may have failed." "Warn"
}

# ── Step 5: Start game (optional) ───────────────────────────────────
if ($StartGame) {
    if (Test-Path $SMAPIExe) {
        Write-Log "Starting Stardew Valley via SMAPI..."
        Start-Process $SMAPIExe
    } else {
        Write-Log "SMAPI executable not found at $SMAPIExe" "Error"
    }
}

Write-Log "Done."
