# Game integration test v2 - launches SMAPI and monitors log
# Uses file-based output capture to avoid pipe-close-kills-process issue
$ErrorActionPreference = "Stop"
. "$PSScriptRoot\..\lib\paths.ps1"
$GamePath = Get-GamePath
$SMAPIExe = Join-Path $GamePath "StardewModdingAPI.exe"
$timestamp = Get-Date -Format "yyyy-MM-dd_HHmmss"
$stdoutFile = Join-Path $PSScriptRoot "results\game-stdout-$timestamp.txt"
$resultFile = Join-Path $PSScriptRoot "results\game-runtime-$timestamp.txt"
$logCandidates = @(
    (Join-Path $GamePath "ErrorLogs\SMAPI-latest.txt"),
    (Join-Path $GamePath "SMAPI-latest.txt")
)

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  Game Runtime Test v2 (SMAPI launch)" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

# Step 1: Kill any running game
$gameProcs = Get-Process -Name "StardewModdingAPI", "Stardew Valley" -ErrorAction SilentlyContinue
if ($gameProcs) {
    Write-Host "[CLEANUP] Stopping existing game processes..." -ForegroundColor Yellow
    $gameProcs | Stop-Process -Force
    Start-Sleep -Seconds 2
}

# Step 2: Archive previous SMAPI log
foreach ($candidate in $logCandidates) {
    if (Test-Path $candidate) {
        $archiveName = "SMAPI-archive-$timestamp.txt"
        try {
            Rename-Item -Path $candidate -NewName $archiveName -ErrorAction Stop
            Write-Host "[LOG] Archived previous log to $archiveName" -ForegroundColor Gray
        } catch { }
        break
    }
}

# Step 3: Launch SMAPI redirecting to file (does NOT pipe through Select-Object)
if (-not (Test-Path $SMAPIExe)) {
    Write-Host "[ERROR] SMAPI not found at $SMAPIExe" -ForegroundColor Red
    exit 1
}

Write-Host "[LAUNCH] Starting SMAPI at $SMAPIExe" -ForegroundColor Cyan
$startTime = Get-Date

# Use cmd.exe to start the game with output redirect to file
# This is critical: do NOT pipe through any filter that closes stdout
$psArgs = @(
    "-NoLogo", "-NoProfile", "-Command",
    "cd '$GamePath'; & '$SMAPIExe' *>&1 | Tee-Object '$stdoutFile'"
)
$process = Start-Process -FilePath "powershell.exe" -ArgumentList $psArgs -PassThru -WindowStyle Hidden
Write-Host "[LAUNCH] PID: $($process.Id)" -ForegroundColor Gray

# Step 4: Wait for log file
$logPath = $null
Write-Host "[WAIT] Looking for SMAPI log..." -ForegroundColor Gray
for ($i = 0; $i -lt 90; $i++) {
    foreach ($candidate in $logCandidates) {
        if (Test-Path $candidate) {
            $logPath = $candidate
            break
        }
    }
    if ($logPath) { break }
    Start-Sleep -Seconds 1
}

if (-not $logPath) {
    Write-Host "[WARN] No SMAPI log found after 90s" -ForegroundColor Yellow
    Write-Host "[INFO] Check stdout file: $stdoutFile" -ForegroundColor Gray
} else {
    Write-Host "[LOG] Monitoring: $logPath" -ForegroundColor Green
}

# Step 5: Wait for "Save loaded" or 180s timeout
$saveLoaded = $false
$maxWait = 180
Write-Host "[WAIT] Waiting for save load (max ${maxWait}s)..." -ForegroundColor Gray
$lastSize = 0
while ($true) {
    $elapsed = ((Get-Date) - $startTime).TotalSeconds
    if ($elapsed -gt $maxWait) {
        Write-Host "[TIMEOUT] No save load after ${maxWait}s" -ForegroundColor Yellow
        break
    }
    # Check wrapper process
    if ($process.HasExited) {
        Write-Host "[EXIT] Wrapper process exited (code: $($process.ExitCode))" -ForegroundColor Cyan
        break
    }
    # Check actual game process
    $gameProcs = Get-Process -Name "StardewModdingAPI" -ErrorAction SilentlyContinue
    $gameExited = $false
    if (-not $gameProcs) { $gameExited = $true }
    if ($logPath -and (Test-Path $logPath)) {
        try {
            $content = Get-Content $logPath -Encoding UTF8 -ReadCount 0 -ErrorAction SilentlyContinue
            if ($content -and $content.Count -gt $lastSize) {
                $newLines = $content[$lastSize..($content.Count - 1)]
                foreach ($line in $newLines) {
                    if ($line -match "Save loaded|Save Loaded|loaded a save|saving.*game") {
                        $saveLoaded = $true
                        Write-Host "[LOAD] Save loaded detected: $line" -ForegroundColor Green
                        break
                    }
                }
                $lastSize = $content.Count
            }
        } catch { }
    }
    if ($saveLoaded) { break }
    Start-Sleep -Seconds 2
}

# Step 6: After save loaded, wait 60s for mod initialization and runtime
if ($saveLoaded) {
    Write-Host "[WAIT] Save loaded. Letting mods run for 60s..." -ForegroundColor Gray
    Start-Sleep -Seconds 60
} else {
    Write-Host "[WAIT] No save loaded, waiting 30s anyway..." -ForegroundColor Yellow
    Start-Sleep -Seconds 30
}

# Step 7: Stop the game
Write-Host "[STOP] Stopping game..." -ForegroundColor Yellow
$gameProcs = Get-Process -Name "StardewModdingAPI", "Stardew Valley" -ErrorAction SilentlyContinue
if ($gameProcs) {
    $gameProcs | Stop-Process -Force
    Start-Sleep -Seconds 2
}
if (-not $process.HasExited) {
    $process.Kill()
    Start-Sleep -Seconds 1
}

# Step 8: Analyze log
$logContent = @()
if ($logPath -and (Test-Path $logPath)) {
    $logContent = Get-Content $logPath -Encoding UTF8 -ErrorAction SilentlyContinue
}

$errors = @()
$warnings = @()
$modLoads = @()
$reportLines = @()
$inReport = $false
foreach ($line in $logContent) {
    if ($line -match "=== Test Report ===") { $inReport = $true }
    if ($inReport) { $reportLines += $line }
    if ($line -match "=== End of Report ===") { $inReport = $false }

    if ($line -match "\[ERROR\]|Exception|traceback|FAILED" -and $line -notmatch "ERROR_HANDLED") {
        $errors += $line
    }
    if ($line -match "\[WARN\]" -and $line -notmatch "test_") {
        $warnings += $line
    }
    if ($line -match "ValleyAgent.*loaded|ValleyAgent TestMod loaded|TestMod loaded|ValleyAgent.*initialised") {
        $modLoads += $line
    }
}

# Step 9: Save report
$totalElapsed = [math]::Round(((Get-Date) - $startTime).TotalSeconds, 1)

@"
========================================
  Game Runtime Test Report
  Timestamp: $timestamp
  Elapsed: ${totalElapsed}s
  Save loaded: $saveLoaded
  Log file: $logPath
========================================

# Mod Load Events ($($modLoads.Count))
"@ | Out-File -FilePath $resultFile -Encoding UTF8
$modLoads | Out-File -FilePath $resultFile -Append -Encoding UTF8
"" | Out-File -FilePath $resultFile -Append -Encoding UTF8

"# Errors ($($errors.Count))" | Out-File -FilePath $resultFile -Append -Encoding UTF8
$errors | Select-Object -First 50 | Out-File -FilePath $resultFile -Append -Encoding UTF8
"" | Out-File -FilePath $resultFile -Append -Encoding UTF8

"# Warnings ($($warnings.Count))" | Out-File -FilePath $resultFile -Append -Encoding UTF8
$warnings | Select-Object -First 30 | Out-File -FilePath $resultFile -Append -Encoding UTF8
"" | Out-File -FilePath $resultFile -Append -Encoding UTF8

"# Test Report" | Out-File -FilePath $resultFile -Append -Encoding UTF8
$reportLines | Out-File -FilePath $resultFile -Append -Encoding UTF8

# Console output
Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  Mod Load Events" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
foreach ($line in $modLoads | Select-Object -First 20) {
    Write-Host "  $line" -ForegroundColor Gray
}
Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  Errors: $($errors.Count) | Warnings: $($warnings.Count)" -ForegroundColor $(if ($errors.Count -eq 0) { "Green" } else { "Red" })
Write-Host "  Save loaded: $saveLoaded" -ForegroundColor $(if ($saveLoaded) { "Green" } else { "Yellow" })
Write-Host "  Total time: ${totalElapsed}s" -ForegroundColor Gray
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Full report: $resultFile" -ForegroundColor Gray
if (Test-Path $stdoutFile) {
    Write-Host "Stdout log: $stdoutFile" -ForegroundColor Gray
}
exit $(if ($saveLoaded) { 0 } else { 1 })
