<#
.SYNOPSIS
    Downloads the Linux build of Stardew Valley via SteamCMD for Docker IT testing.

.DESCRIPTION
    Downloads SteamCMD (Windows build) to .tmp/steamcmd/, then uses it to pull
    the Linux Steam depot for Stardew Valley (appid 413150) into docker/linux-game/.

    This is a ONE-SHOT interactive tool. You will be prompted for your Steam
    username, password, and Steam Guard code (if required). The script is NOT
    part of CI and MUST NOT be called automatically.

    SECURITY NOTE: SteamCMD does not support stdin password piping; credentials
    are passed via command-line arguments (+login user pass). This means the
    password is briefly visible in the process list. Acceptable for a local
    one-shot developer tool -- do not run on shared machines.

.PARAMETER SteamUsername
    Steam account name. Falls back to $env:STEAM_USERNAME, then Read-Host.

.PARAMETER SteamPassword
    Steam account password. Falls back to $env:STEAM_PASSWORD, then Read-Host.
    Note: passed as plaintext arg to steamcmd.exe (see SECURITY NOTE above).

.EXAMPLE
    .\scripts\docker\download-linux-game.ps1
    .\scripts\docker\download-linux-game.ps1 -SteamUsername myuser -SteamPassword mypass
#>

param(
    [string]$SteamUsername,
    [string]$SteamPassword
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

# ── Resolve repo root ──
$repoRoot = (Get-Location).Path
if (-not (Test-Path (Join-Path $repoRoot "AGENTS.md"))) {
    # Fallback: script lives in scripts/docker/, go up two levels
    $repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
}

# ── Paths (all on D: drive, nothing on C:) ──
$steamcmdDir    = Join-Path $repoRoot ".tmp\steamcmd"
$steamcmdExe    = Join-Path $steamcmdDir "steamcmd.exe"
$steamcmdZip    = Join-Path $steamcmdDir "steamcmd.zip"
$gameTargetDir  = Join-Path $repoRoot "docker\linux-game"

# ═══════════════════════════════════════════════════════════════════════
# STEP 1: Ensure SteamCMD is available
# ═══════════════════════════════════════════════════════════════════════

if (-not (Test-Path $steamcmdExe)) {
    Write-Host "=== Downloading SteamCMD ===" -ForegroundColor Cyan
    New-Item -ItemType Directory -Path $steamcmdDir -Force | Out-Null

    $downloadUrl = "https://steamcdn-a.akamaihd.net/client/installer/steamcmd.zip"
    Write-Host "Downloading from $downloadUrl ..."

    # Use .NET WebClient for broad PS 5.1 compat; Invoke-WebRequest works too
    $wc = New-Object System.Net.WebClient
    try {
        $wc.DownloadFile($downloadUrl, $steamcmdZip)
    }
    finally {
        $wc.Dispose()
    }

    if (-not (Test-Path $steamcmdZip)) {
        Write-Error "Failed to download steamcmd.zip"
        exit 1
    }

    Write-Host "Extracting to $steamcmdDir ..."
    Expand-Archive -Path $steamcmdZip -DestinationPath $steamcmdDir -Force
    Remove-Item $steamcmdZip -Force

    if (-not (Test-Path $steamcmdExe)) {
        Write-Error "steamcmd.exe not found after extraction. Check $steamcmdDir"
        exit 1
    }

    Write-Host "SteamCMD installed." -ForegroundColor Green
}
else {
    Write-Host "SteamCMD already present at $steamcmdExe -- skipping download." -ForegroundColor DarkGray
}

# ═══════════════════════════════════════════════════════════════════════
# STEP 2: Resolve credentials
# ═══════════════════════════════════════════════════════════════════════

# Username
if (-not $SteamUsername) {
    if ($env:STEAM_USERNAME) {
        $SteamUsername = $env:STEAM_USERNAME
        Write-Host "Using STEAM_USERNAME from environment." -ForegroundColor DarkGray
    }
    else {
        $SteamUsername = Read-Host "Steam username"
    }
}

# Password
if (-not $SteamPassword) {
    if ($env:STEAM_PASSWORD) {
        $SteamPassword = $env:STEAM_PASSWORD
        Write-Host "Using STEAM_PASSWORD from environment." -ForegroundColor DarkGray
    }
    else {
        # Read-Host -AsSecureString -> convert to plain for steamcmd arg.
        # Tradeoff: the plaintext string lives in PS memory until GC collects it.
        # Acceptable for a local one-shot developer tool.
        $securePw = Read-Host "Steam password (input hidden)" -AsSecureString
        $bstr = [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($securePw)
        try {
            $SteamPassword = [System.Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)
        }
        finally {
            [System.Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
        }
    }
}

if (-not $SteamUsername -or -not $SteamPassword) {
    Write-Error "Username and password are required."
    exit 1
}

# ═══════════════════════════════════════════════════════════════════════
# STEP 3: Run SteamCMD to download Linux game depot
# ═══════════════════════════════════════════════════════════════════════

Write-Host ""
Write-Host "=== Downloading Stardew Valley (Linux build) via SteamCMD ===" -ForegroundColor Cyan
Write-Host "Target: $gameTargetDir"
Write-Host "App ID: 413150"
Write-Host ""
Write-Host "NOTE: If Steam Guard is enabled, SteamCMD will prompt you for a code." -ForegroundColor Yellow
Write-Host "      Enter the code from your Steam Mobile App or email when prompted." -ForegroundColor Yellow
Write-Host "      Both mobile app codes and email codes are entered the same way." -ForegroundColor Yellow
Write-Host "      Download is ~500MB-1GB -- this may take several minutes." -ForegroundColor Yellow
Write-Host ""

# Ensure target directory exists
New-Item -ItemType Directory -Path $gameTargetDir -Force | Out-Null

# Build SteamCMD arguments.
# IMPORTANT: @sSteamCmdForcePlatformType MUST appear before +login to ensure
# the platform override applies to the login+download sequence.
# SteamCMD processes + commands left-to-right.
$steamcmdArgs = @(
    "+@sSteamCmdForcePlatformType", "linux",
    "+login", $SteamUsername, $SteamPassword,
    "+force_install_dir", $gameTargetDir,
    "+app_update", "413150", "validate",
    "+quit"
)

# Run SteamCMD -- it is a console app; interactive stdin is preserved for
# Steam Guard prompts when invoked via & operator in PowerShell.
& $steamcmdExe @steamcmdArgs
$steamcmdExit = $LASTEXITCODE

# Wipe password from memory as best we can
$SteamPassword = $null
[System.GC]::Collect()

# ═══════════════════════════════════════════════════════════════════════
# STEP 4: Verify download
# ═══════════════════════════════════════════════════════════════════════

Write-Host ""
Write-Host "=== Verifying download ===" -ForegroundColor Cyan

$allFiles = Get-ChildItem $gameTargetDir -Recurse -File -ErrorAction SilentlyContinue
$totalFileCount = ($allFiles | Measure-Object).Count
# 空目录时 Measure-Object -Sum 返回 $null——SteamCMD 失败未下载任何文件时 .Sum 会崩
$totalSize = 0
if ($allFiles) { $totalSize = ($allFiles | Measure-Object -Property Length -Sum).Sum }
$totalMB = [math]::Round($totalSize / 1MB, 1)

Write-Host "  Total files: $totalFileCount"
Write-Host "  Total size:  $totalMB MB"
Write-Host ""

$allOk = $true

# --- Check 1: Stardew Valley.dll (the game assembly) ---
$dllPath = Join-Path $gameTargetDir "Stardew Valley.dll"
if (Test-Path $dllPath -PathType Leaf) {
    $dllMB = [math]::Round((Get-Item $dllPath).Length / 1MB, 2)
    Write-Host "  [OK] Stardew Valley.dll ($dllMB MB)" -ForegroundColor Green
}
else {
    Write-Host "  [MISSING] Stardew Valley.dll" -ForegroundColor Red
    $allOk = $false
}

# --- Check 2: Stardew Valley.deps.json targets linux-x64 ---
$depsPath = Join-Path $gameTargetDir "Stardew Valley.deps.json"
if (Test-Path $depsPath -PathType Leaf) {
    $depsContent = Get-Content $depsPath -Raw
    if ($depsContent -match "linux-x64") {
        Write-Host "  [OK] Stardew Valley.deps.json targets linux-x64" -ForegroundColor Green
    }
    else {
        Write-Host "  [WARN] Stardew Valley.deps.json does NOT contain 'linux-x64'" -ForegroundColor Red
        $allOk = $false
    }
}
else {
    Write-Host "  [MISSING] Stardew Valley.deps.json" -ForegroundColor Red
    $allOk = $false
}

# --- Check 3: libcoreclr.so 平铺在根目录（真 Linux 版运行时特征）---
# 真 Linux 版是 self-contained，运行时（libcoreclr.so/libhostpolicy.so 等）平铺在
# 游戏目录根，没有 dotnet/ 目录（2026-08-19 实测下载产物确认——早期假设错误）。
$coreclrPath = Join-Path $gameTargetDir "libcoreclr.so"
if (Test-Path $coreclrPath -PathType Leaf) {
    Write-Host "  [OK] libcoreclr.so (Linux self-contained runtime present)" -ForegroundColor Green
}
else {
    Write-Host "  [FAIL] libcoreclr.so missing -- this is NOT a Linux build" -ForegroundColor Red
    $allOk = $false
}

# --- Check 4: Content/ directory exists (game assets) ---
$contentPath = Join-Path $gameTargetDir "Content"
if (Test-Path $contentPath -PathType Container) {
    $contentFileCount = (Get-ChildItem $contentPath -Recurse -File -ErrorAction SilentlyContinue | Measure-Object).Count
    Write-Host "  [OK] Content/ directory ($contentFileCount files)" -ForegroundColor Green
}
else {
    Write-Host "  [WARN] Content/ directory not found -- game assets may be missing" -ForegroundColor Yellow
    # Not a hard failure -- some depot layouts place content elsewhere
}

Write-Host ""

# ═══════════════════════════════════════════════════════════════════════
# STEP 5: Report
# ═══════════════════════════════════════════════════════════════════════

# SteamCMD exit code handling
if ($steamcmdExit -ne 0) {
    Write-Host "WARNING: SteamCMD exited with code $steamcmdExit" -ForegroundColor Yellow

    # Check for common failure patterns in SteamCMD log
    $steamcmdLog = Join-Path $steamcmdDir "logs\content_log.txt"
    if (Test-Path $steamcmdLog) {
        $tail = Get-Content $steamcmdLog -Tail 20 -ErrorAction SilentlyContinue
        Write-Host ""
        Write-Host "--- Last 20 lines of SteamCMD content log ---" -ForegroundColor DarkGray
        $tail | ForEach-Object { Write-Host "  $_" }
        Write-Host "---" -ForegroundColor DarkGray
    }

    # Friendly message for login failures
    if ($steamcmdExit -eq 5) {
        Write-Host ""
        Write-Error @"
Login Failure (exit code 5). Common causes:
  - Wrong username or password
  - Steam Guard code was incorrect or expired
  - Account has Steam Family View enabled (disable it temporarily)
  - Account is locked due to too many failed attempts
Try running the script again with correct credentials.
"@
    }
}

if ($allOk) {
    Write-Host "=== SUCCESS ===" -ForegroundColor Green
    Write-Host "Linux game files ready at: $gameTargetDir"
    Write-Host ""
    Write-Host "Next steps:"
    Write-Host "  - Update run-it.ps1 to mount docker/linux-game as /game instead of"
    Write-Host "    the Windows 'Stardew Valley/' directory"
    Write-Host "  - Or test with: docker run --rm -v `"${gameTargetDir}`":/game:ro ..."
}
else {
    Write-Host "=== DOWNLOAD INCOMPLETE ===" -ForegroundColor Red
    Write-Host "Some expected files are missing or the build does not look like a Linux build."
    Write-Host ""
    Write-Host "Common causes:"
    Write-Host "  - Wrong credentials or Steam Guard failure"
    Write-Host "  - Steam rate limiting (try again in 30 minutes)"
    Write-Host "  - Disk space (need ~1GB free on D:)"
    Write-Host "  - Platform override did not take effect (dotnet was a flat file, not a directory)"
    Write-Host "    In this case, delete docker/linux-game/ and retry."
    exit 1
}
