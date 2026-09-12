# run-it.ps1 — Runs ValleyAgent integration tests in Docker.
#
# Starts the container, polls for _TEST_COMPLETE.txt in the /data bind mount,
# parses _summary.json, prints per-test PASS/FAIL/SKIP table, propagates exit code.
#
# Usage:
#   .\scripts\docker\run-it.ps1                    # default 600s timeout
#   .\scripts\docker\run-it.ps1 -Timeout 1800      # 30min for first run
#   .\scripts\docker\run-it.ps1 -Vnc               # expose VNC for debugging
#   .\scripts\docker\run-it.ps1 -KeepContainer     # don't remove on exit

param(
    [int]$Timeout = 600,
    [switch]$Vnc,
    [switch]$KeepContainer
)

$ErrorActionPreference = "Stop"
$repoRoot = (Get-Location).Path
if (-not (Test-Path (Join-Path $repoRoot "AGENTS.md"))) {
    $repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
}

# -- Paths --
# NOTE: keep this block ASCII-only. PowerShell 5.1 reads UTF-8-no-BOM as GBK;
# multi-byte chars here can swallow the newline and null out $gameDir below.
# Game source: docker/linux-game (real Linux build via SteamCMD, see download-linux-game.ps1).
# NOT the host Windows game dir - Windows deps are platform-bound (spike proven).
$gameDir       = Join-Path $repoRoot "docker\linux-game"
$modsCacheDir  = Join-Path $repoRoot "docker\mods-cache"
$savesDir      = Join-Path $repoRoot "docker\saves"
$dataDir       = Join-Path $repoRoot "docker\data"
$smapiCacheDir = Join-Path $repoRoot "docker\smapi-cache"
$containerName = "valleyagent-it-$(Get-Date -Format 'yyyyMMddHHmmss')"

# ── Validate ──
if (-not (Test-Path $gameDir)) {
    Write-Error "Game directory not found: $gameDir"
    exit 1
}
if (-not (Test-Path $modsCacheDir)) {
    Write-Error "Mods cache not found. Run prep-mods.ps1 first: $modsCacheDir"
    exit 1
}
if (-not (Test-Path $savesDir)) {
    Write-Error "Saves directory not found. Run prep-mods.ps1 first: $savesDir"
    exit 1
}
if (-not (Test-Path (Join-Path $smapiCacheDir "SMAPI-4.3.2-installer.zip"))) {
    Write-Warning "SMAPI installer not found at $smapiCacheDir — container will fall back to downloading from GitHub (may fail)."
}

# Ensure /data bind mount directory exists
if (-not (Test-Path $dataDir)) {
    New-Item -ItemType Directory -Path $dataDir -Force | Out-Null
}

# ── Clean previous results in /data (but preserve game + SMAPI cache) ──
$prevMarker = Join-Path $dataDir "Mods\ValleyAgent.TestMod\logs"
if (Test-Path $prevMarker) {
    Remove-Item -Recurse -Force $prevMarker -ErrorAction SilentlyContinue
}

# ── Print header ──
Write-Host "=== ValleyAgent IT Runner ==="
Write-Host "Container: $containerName"
Write-Host "Timeout:   ${Timeout}s"
Write-Host "Data dir:  $dataDir"
Write-Host "VNC:       $(if ($Vnc) { 'enabled (ports 5800/5900)' } else { 'disabled' })"
Write-Host ""
Write-Host "Starting container..."

# ── Build docker run command as array (splatting-safe) ──
# Using & docker @args avoids Start-Process argument-quoting bugs.
$dockerCmd = @("run", "-d", "--name", $containerName)
if (-not $KeepContainer) { $dockerCmd += "--rm" }
$dockerCmd += @(
    "-v", "${dataDir}:/data",
    "-v", "${gameDir}:/game:ro",
    "-v", "${modsCacheDir}:/mods-src:ro",
    "-v", "${savesDir}:/saves-src:ro",
    "-v", "${smapiCacheDir}:/smapi-cache:ro",
    "-e", "SMAPI_VERSION=4.3.2",
    "--shm-size=256m"
)
if ($Vnc) {
    $dockerCmd += "-p", "5800:5800"
    $dockerCmd += "-p", "5900:5900"
}
$dockerCmd += "valleyagent-gameit"

# ── Start container detached (docker run -d returns immediately) ──
# NOT via Start-Job: jobs run docker silently and can fail without output.
& docker @dockerCmd
if ($LASTEXITCODE -ne 0) {
    Write-Error "docker run failed (exit code $LASTEXITCODE)"
    exit 1
}

# ── Poll for _TEST_COMPLETE.txt ──
$resultsSearchRoot = Join-Path $dataDir "Mods\ValleyAgent.TestMod\logs\test_results"
$deadline = (Get-Date).AddSeconds($Timeout)
$completed = $false

Write-Host "Waiting for test completion (polling _TEST_COMPLETE.txt)..."

while ((Get-Date) -lt $deadline) {
    # Check container still running
    $running = docker inspect -f '{{.State.Running}}' $containerName 2>$null
    if ($running -ne "True") {
        Write-Host ""
        Write-Host "Container exited before completion marker appeared."
        $exitCode = docker inspect -f '{{.State.ExitCode}}' $containerName 2>$null
        if ($exitCode) {
            Write-Host "Container exit code: $exitCode"
        }
        break
    }

    # Look for marker file
    if (Test-Path $resultsSearchRoot) {
        $marker = Get-ChildItem -Path $resultsSearchRoot -Recurse -Filter "_TEST_COMPLETE.txt" -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($marker) {
            Write-Host ""
            Write-Host "Completion marker found: $($marker.FullName)"
            $completed = $true
            break
        }
    }

    # Periodic status
    $elapsed = [math]::Round(((Get-Date) - ($deadline.AddSeconds(-$Timeout))).TotalSeconds)
    if ($elapsed % 15 -lt 2) {
        Write-Host "  Waiting... (${elapsed}s / ${Timeout}s)"
    }

    Start-Sleep -Seconds 2
}

# ── Stop container if still running (timeout case) ──
if (-not $completed) {
    Write-Host ""
    Write-Error "TIMEOUT: Tests did not complete within ${Timeout}s"
    docker kill $containerName 2>$null | Out-Null
    $debugDir = Join-Path $dataDir "logs-debug"
    if (Test-Path $debugDir) {
        Write-Host "Debug logs available at: $debugDir"
    }
    exit 124
}

# ── Wait for container to finish (SMAPI exits after tests) ──
Write-Host "Waiting for container to exit..."
docker wait $containerName 2>$null | Out-Null
# --rm removes the container on exit, so inspect may fail on detached mode;
# that is fine -- the marker file is our source of truth.

# ── Parse _summary.json ──
# Field names from V3TestRunner.WriteSummaryFile():
#   Top-level (anonymous type, camelCase): run_timestamp, total_tests, total_passed, total_failed, total_skipped
#   Tests array (SummaryEntry class, PascalCase, no PropertyNamingPolicy): Name, Group, Passed, Failed, Status, TimedOut
$summary = Get-ChildItem -Path $resultsSearchRoot -Recurse -Filter "_summary.json" -ErrorAction SilentlyContinue | Select-Object -First 1

if (-not $summary) {
    Write-Host ""
    Write-Error "No _summary.json found in $resultsSearchRoot"
    exit 1
}

$data = Get-Content $summary.FullName -Raw | ConvertFrom-Json

Write-Host ""
Write-Host "========================================"
Write-Host "  TEST RESULTS"
Write-Host "========================================"
Write-Host "  Run:          $($data.run_timestamp)"
Write-Host "  Total tests:  $($data.total_tests)"
Write-Host "  Passed:       $($data.total_passed)"
Write-Host "  Failed:       $($data.total_failed)"
Write-Host "  Skipped:      $($data.total_skipped)"
Write-Host "========================================"
Write-Host ""

# Per-test table
foreach ($t in $data.tests) {
    $statusStr = $t.Status
    $icon = switch ($statusStr) {
        "PASS" { "[PASS]" }
        "FAIL" { "[FAIL]" }
        "SKIP" { "[SKIP]" }
        default { "[????]" }
    }
    $score = "$($t.Passed)/$($t.Passed + $t.Failed)"
    $timeout = if ($t.TimedOut) { " (TIMEOUT)" } else { "" }
    Write-Host ("  {0,-8} {1,-40} {2}{3}" -f $icon, $t.Name, $score, $timeout)
}

Write-Host ""

# ── Debug logs ──
$debugDir = Join-Path $dataDir "logs-debug"
if (Test-Path $debugDir) {
    Write-Host "Debug logs: $debugDir"
}

# ── Exit code ──
if ($data.total_failed -gt 0) {
    Write-Host ""
    Write-Host "EXIT CODE: 1 ($($data.total_failed) failure(s))"
    exit 1
} elseif ($data.total_tests -eq 0) {
    Write-Host ""
    Write-Host "EXIT CODE: 1 (no tests ran)"
    exit 1
} else {
    Write-Host ""
    Write-Host "EXIT CODE: 0 (all passed)"
    exit 0
}
