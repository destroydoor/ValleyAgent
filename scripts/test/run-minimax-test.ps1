#requires -Version 5.1
<#
.SYNOPSIS
    Launches Stardew Valley SMAPI with Minimax M3 LLM env vars and waits for V3TestRunner report.
    Uses streaming log reader to handle multi-GB SMAPI logs efficiently.
#>
[CmdletBinding()]
param(
    [int]$TimeoutSeconds = 1800
)

$ErrorActionPreference = "Stop"

$GamePath = "d:\Source\ValleyTalk\Stardew Valley"
$SMAPIExe = Join-Path $GamePath "StardewModdingAPI.exe"
$SmapiLogPath = Join-Path $env:AppData "StardewValley\ErrorLogs\SMAPI-latest.txt"
$RepoRoot = "d:\Source\ValleyTalk"
$timestamp = Get-Date -Format "yyyy-MM-dd_HHmmss"
$resultFile = Join-Path $RepoRoot "scripts\results\game-test-$timestamp.txt"

if (-not (Test-Path $SMAPIExe)) { throw "SMAPI not found: $SMAPIExe" }

# ── State variables for functions ──
$script:reportFound = $false
$script:sawStart = $false
$script:reportLines = [System.Collections.Generic.List[string]]::new()

# ── Helper: read only new lines since last file position (streaming) ──
function Read-NewLogLines {
    param([string]$Path, [ref]$Position)
    $result = [System.Collections.Generic.List[string]]::new()
    try {
        $fs = [System.IO.FileStream]::new($Path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
        $fs.Seek($Position.Value, [System.IO.SeekOrigin]::Begin) | Out-Null
        $sr = [System.IO.StreamReader]::new($fs, [System.Text.Encoding]::UTF8)
        while ($null -ne ($line = $sr.ReadLine())) {
            $result.Add($line)
        }
        $Position.Value = $fs.Position
        $sr.Close()
        $fs.Close()
    } catch {
        # File might be locked or rotating — skip this cycle
    }
    return ,$result
}

# ── Helper: scan lines for report markers ──
function Scan-ForReport {
    param($Lines)
    foreach ($line in $Lines) {
        if (-not $script:sawStart -and $line -match "=== Test Report ===") {
            $script:sawStart = $true
            Write-Host "[REPORT] Test report start detected." -ForegroundColor Green
        }
        if ($script:sawStart) {
            $script:reportLines.Add($line)
        }
        if ($script:sawStart -and $line -match "=== End of Report ===") {
            $script:reportFound = $true
            Write-Host "[REPORT] Test report end detected." -ForegroundColor Green
            break
        }
    }
}

# ── LLM env vars (inherited by SMAPI → mod → python) ──
# Key 不落仓库明文：优先读已有环境变量，否则加载 scripts/secrets.local.ps1（已 gitignore）
if ([string]::IsNullOrEmpty($env:VALLEY_LLM_API_KEY)) {
    $secretsScript = Join-Path $PSScriptRoot "../secrets.local.ps1"
    if (Test-Path $secretsScript) {
        . $secretsScript
    }
}
if ([string]::IsNullOrEmpty($env:VALLEY_LLM_API_KEY)) {
    Write-Error "VALLEY_LLM_API_KEY 未设置：请设置环境变量，或复制 scripts/secrets.local.ps1.template 为 secrets.local.ps1 并填入真实 key。"
    exit 1
}
$env:VALLEY_LLM_BASE_URL = "https://api.minimax.chat/v1"
$env:VALLEY_LLM_MODEL    = "MiniMax-M3"
$env:VALLEY_LLM_PROVIDER = "openai_compatible"
Remove-Item Env:OPENAI_API_KEY -ErrorAction SilentlyContinue

Write-Host "[ENV] VALLEY_LLM_BASE_URL = $env:VALLEY_LLM_BASE_URL" -ForegroundColor DarkGray
Write-Host "[ENV] VALLEY_LLM_MODEL     = $env:VALLEY_LLM_MODEL"    -ForegroundColor DarkGray
Write-Host "[ENV] OPENAI_API_KEY cleared = $([string]::IsNullOrEmpty($env:OPENAI_API_KEY))" -ForegroundColor DarkGray
Write-Host ""

# ── Kill any existing game / python processes ──
$gameProcs = Get-Process -Name "StardewModdingAPI", "Stardew Valley" -ErrorAction SilentlyContinue
if ($gameProcs) {
    Write-Host "[KILL] Stopping existing game processes..." -ForegroundColor Yellow
    $gameProcs | Stop-Process -Force
    Start-Sleep -Seconds 2
}
$pythonProcs = Get-Process -Name "python" -ErrorAction SilentlyContinue
if ($pythonProcs) {
    Write-Host "[KILL] Stopping existing python processes..." -ForegroundColor Yellow
    $pythonProcs | Stop-Process -Force
    Start-Sleep -Seconds 1
}

# ── Archive + truncate old SMAPI log for clean output ──
if (Test-Path $SmapiLogPath) {
    $archiveName = "SMAPI-" + $timestamp + ".txt"
    $archivePath = Join-Path (Split-Path $SmapiLogPath) $archiveName
    try {
        Copy-Item -Path $SmapiLogPath -Destination $archivePath -ErrorAction SilentlyContinue
        Write-Host "[LOG] Archived previous SMAPI log -> $archiveName" -ForegroundColor Gray
    } catch { }
    try { Set-Content -Path $SmapiLogPath -Value "" -Encoding UTF8 } catch { }
}

# ── Launch SMAPI ──
Write-Host "[LAUNCH] Starting SMAPI from: $SMAPIExe" -ForegroundColor Cyan
Write-Host "        Timeout: $TimeoutSeconds seconds" -ForegroundColor Gray

$startTime = Get-Date
$process = Start-Process -FilePath $SMAPIExe -WorkingDirectory $GamePath -PassThru
$launchedPid = $process.Id
Write-Host "[LAUNCH] SMAPI process started (PID: $launchedPid)" -ForegroundColor Gray

Start-Sleep -Seconds 5

# ── Poll for test report using streaming reader ──
$lastPos = 0L
$pollInterval = 3

while ($true) {
    if ($process.HasExited) {
        Write-Host "[EXIT] Game exited (code: $($process.ExitCode))" -ForegroundColor Cyan
        Start-Sleep -Seconds 2
        $newLines = Read-NewLogLines -Path $SmapiLogPath -Position ([ref]$lastPos)
        if ($newLines.Count -gt 0) { Scan-ForReport -Lines $newLines }
        break
    }

    $elapsed = ((Get-Date) - $startTime).TotalSeconds
    if ($elapsed -gt $TimeoutSeconds) {
        Write-Host "[TIMEOUT] ${TimeoutSeconds}s elapsed — stopping game." -ForegroundColor Yellow
        try { $process.Kill() } catch { }
        Start-Sleep -Seconds 1
        break
    }

    if ([int]$elapsed % 60 -eq 0 -and [int]$elapsed -ne 0) {
        Write-Host "[WAIT] $([int]$elapsed)s elapsed, waiting for test report..." -ForegroundColor Gray
    }

    if ($SmapiLogPath -and (Test-Path $SmapiLogPath)) {
        $newLines = Read-NewLogLines -Path $SmapiLogPath -Position ([ref]$lastPos)
        if ($newLines.Count -gt 0) { Scan-ForReport -Lines $newLines }
    }

    if ($script:reportFound) { break }
    Start-Sleep -Seconds $pollInterval
}

# ── Kill game if still running ──
if (-not $process.HasExited) {
    Write-Host "[STOP] Closing game..." -ForegroundColor Yellow
    try { $process.Kill() } catch { }
    Start-Sleep -Seconds 2
}

# ── Full scan for all PASS/FAIL lines ──
Write-Host ""
Write-Host "[SCAN] Scanning log for test results..." -ForegroundColor Cyan

$allFailLines = [System.Collections.Generic.List[string]]::new()
$allPassCount = 0
$allFailCount = 0
$lastTestMarkers = [System.Collections.Generic.List[string]]::new()
$funcDialogueResults = [System.Collections.Generic.List[string]]::new()

try {
    $fs2 = [System.IO.FileStream]::new($SmapiLogPath, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
    $sr2 = [System.IO.StreamReader]::new($fs2, [System.Text.Encoding]::UTF8)
    while ($null -ne ($logLine = $sr2.ReadLine())) {
        if ($logLine -match "\[FAIL\]") {
            $allFailLines.Add($logLine)
            $allFailCount++
        }
        if ($logLine -match "\[PASS\]") {
            $allPassCount++
        }
        if ($logLine -match "Starting test \d+/\d+:") {
            $lastTestMarkers.Add($logLine)
            if ($lastTestMarkers.Count -gt 5) { $lastTestMarkers.RemoveAt(0) }
        }
        if ($logLine -match "Func_DialogueActions:") {
            $funcDialogueResults.Add($logLine)
        }
    }
    $sr2.Close()
    $fs2.Close()
} catch {
    Write-Host "[SCAN] Error reading log: $($_.Exception.Message)" -ForegroundColor Red
}

Write-Host ""
Write-Host "=== ALL FAIL lines ===" -ForegroundColor Red
if ($allFailLines.Count -eq 0) {
    Write-Host "  (none)" -ForegroundColor Green
} else {
    foreach ($fl in $allFailLines) { Write-Host "  $fl" -ForegroundColor Red }
}

Write-Host ""
Write-Host "=== Last 5 'Starting test' markers ===" -ForegroundColor Gray
foreach ($m in $lastTestMarkers) { Write-Host "  $m" -ForegroundColor Gray }

Write-Host ""
Write-Host "=== PASS/FAIL counts ===" -ForegroundColor Yellow
Write-Host "  PASS: $allPassCount  FAIL: $allFailCount" -ForegroundColor $(if ($allFailCount -eq 0) { "Green" } else { "Red" })

if ($funcDialogueResults.Count -gt 0) {
    Write-Host ""
    Write-Host "=== Func_DialogueActions results ===" -ForegroundColor Cyan
    foreach ($fr in $funcDialogueResults) { Write-Host "  $fr" }
}

# Write results to file
$resultDir = Split-Path $resultFile -Parent
if (-not (Test-Path $resultDir)) { New-Item -ItemType Directory -Path $resultDir -Force | Out-Null }

$totalElapsed = [math]::Round(((Get-Date) - $startTime).TotalSeconds, 1)
$output = [System.Collections.Generic.List[string]]::new()
$output.Add("=== ALL FAIL lines (all) ===")
foreach ($fl in $allFailLines) { $output.Add($fl) }
$output.Add("")
$output.Add("=== Last 5 'Starting test' markers ===")
foreach ($m in $lastTestMarkers) { $output.Add($m) }
$output.Add("")
$output.Add("=== PASS/FAIL counts ===")
$output.Add("PASS: $allPassCount  FAIL: $allFailCount")
$output.Add("")
$output.Add("=== Func_DialogueActions results ===")
foreach ($fr in $funcDialogueResults) { $output.Add($fr) }
$output.Add("")
$output.Add("Elapsed: ${totalElapsed}s")

$output | Out-File -FilePath $resultFile -Encoding UTF8

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  Summary: $allPassCount PASS, $allFailCount FAIL" -ForegroundColor $(if ($allFailCount -eq 0) { "Green" } else { "Red" })
Write-Host "  Total time: ${totalElapsed}s" -ForegroundColor Gray
Write-Host "  Log: $SmapiLogPath" -ForegroundColor Gray
Write-Host "  Report: $resultFile" -ForegroundColor Gray
Write-Host "========================================" -ForegroundColor Cyan

exit $(if ($allFailCount -eq 0) { 0 } else { 1 })
