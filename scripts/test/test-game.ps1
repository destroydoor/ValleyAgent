# Build + Deploy + Launch Stardew Valley SMAPI + Extract test results
param(
    [switch]$NoBuild,
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",
    [int]$TimeoutSeconds = 600
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$timestamp = Get-Date -Format "yyyy-MM-dd_HHmmss"
$resultFile = Join-Path $RepoRoot "scripts\results\game-test-$timestamp.txt"
# 优先用 STARDW_PATH 环境变量，回退到项目内默认路径（与 deploy.ps1 保持一致）
. "$PSScriptRoot\..\lib\paths.ps1"
$GamePath = Get-GamePath
$SMAPIExe = Join-Path $GamePath "StardewModdingAPI.exe"
$ModsDir = Join-Path $GamePath "Mods"

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  Game Integration Tests" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

# Step 1: Build + Deploy (unless -NoBuild)
if (-not $NoBuild) {
    Write-Host "[BUILD] Building ValleyAgent + TestMod..." -ForegroundColor Yellow
    & "$PSScriptRoot\..\build\build-all.ps1" -Configuration $Configuration -IncludeTests
    if ($LASTEXITCODE -ne 0) {
        "BUILD FAILED" | Out-File $resultFile -Encoding UTF8
        Write-Host "BUILD FAILED" -ForegroundColor Red
        exit 1
    }

    Write-Host "[DEPLOY] Deploying to Mods folder..." -ForegroundColor Yellow
    & "$PSScriptRoot\..\build\deploy.ps1" -SkipBuild -Configuration $Configuration
    if ($LASTEXITCODE -ne 0) {
        "DEPLOY FAILED" | Out-File $resultFile -Encoding UTF8
        Write-Host "DEPLOY FAILED" -ForegroundColor Red
        exit 1
    }

    # deploy.ps1 会删除 ValleyAgent.TestMod 目录（release 不应含测试 mod），这里重新部署 TestMod
    Write-Host "[DEPLOY] Re-deploying ValleyAgent.TestMod (removed by deploy.ps1)..." -ForegroundColor Yellow
    $testModSrc = Join-Path $RepoRoot "src\ValleyAgent.TestMod\bin\$Configuration\net6.0"
    $testModDst = Join-Path $ModsDir "ValleyAgent.TestMod"
    if (-not (Test-Path $testModDst)) { New-Item -ItemType Directory -Path $testModDst -Force | Out-Null }
    foreach ($f in @("ValleyAgent.TestMod.dll", "ValleyAgent.Abstractions.dll", "manifest.json", "test_config.json")) {
        $srcFile = Join-Path $testModSrc $f
        if (Test-Path $srcFile) {
            Copy-Item $srcFile $testModDst -Force
        }
    }
    $dataSrc = Join-Path $testModSrc "Data"
    if (Test-Path $dataSrc) {
        Copy-Item $dataSrc $testModDst -Recurse -Force
    }
    Write-Host "[DEPLOY] ValleyAgent.TestMod re-deployed." -ForegroundColor Green
}

# Step 2: Archive old SMAPI log for clean output
# SMAPI 默认把日志写到 %APPDATA%\StardewValley\ErrorLogs\，部分版本写到游戏目录下
$logCandidates = @(
    (Join-Path $env:APPDATA "StardewValley\ErrorLogs\SMAPI-latest.txt"),
    (Join-Path $GamePath "ErrorLogs\SMAPI-latest.txt"),
    (Join-Path $GamePath "SMAPI-latest.txt")
)
$logPath = $null
foreach ($candidate in $logCandidates) {
    if (Test-Path $candidate) { $logPath = $candidate; break }
}

if ($logPath) {
    $logFiles = Get-ChildItem -Path $GamePath -Filter "SMAPI-*.txt" -Recurse -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending
    if ($logFiles) {
        $latest = $logFiles | Select-Object -First 1
        if ($latest.Name -like "SMAPI-latest*") {
            $archiveName = "SMAPI-" + (Get-Date -Format "yyyy-MM-dd_HHmmss") + ".txt"
            try {
                Rename-Item -Path $latest.FullName -NewName $archiveName -ErrorAction SilentlyContinue
                Write-Host "[LOG] Archived previous SMAPI log" -ForegroundColor Gray
            } catch { }
        }
    }
}

# Step 3: Kill any existing game processes
$gameProcs = Get-Process -Name "StardewModdingAPI", "Stardew Valley" -ErrorAction SilentlyContinue
if ($gameProcs) {
    Write-Host "[KILL] Stopping existing game processes..." -ForegroundColor Yellow
    $gameProcs | Stop-Process -Force
    Start-Sleep -Seconds 2
}

# Kill any process holding port 8765 (TS Agent Server from previous session)
$staleProcs = Get-NetTCPConnection -LocalPort 8765 -ErrorAction SilentlyContinue |
    Select-Object -ExpandProperty OwningProcess -Unique
if ($staleProcs) {
    Write-Host "[CLEANUP] Killing processes on port 8765..." -ForegroundColor Yellow
    $staleProcs | ForEach-Object { Stop-Process -Id $_ -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Seconds 1
}

# Step 4: Launch SMAPI
Write-Host "[LAUNCH] Starting Stardew Valley via SMAPI..." -ForegroundColor Cyan
Write-Host "         Timeout: $TimeoutSeconds seconds" -ForegroundColor Gray

$startTime = Get-Date
$process = Start-Process -FilePath $SMAPIExe -WorkingDirectory $GamePath -PassThru
$smapiPid = $process.Id
Write-Host "[LAUNCH] SMAPI process started (PID: $smapiPid)" -ForegroundColor Gray

# Wait for log file to appear
$logPath = $null
for ($i = 0; $i -lt 30; $i++) {
    foreach ($candidate in $logCandidates) {
        if (Test-Path $candidate) { $logPath = $candidate; break }
    }
    if ($logPath) { break }
    Start-Sleep -Seconds 1
}

if ($logPath) {
    Write-Host "[LOG] Monitoring: $logPath" -ForegroundColor Gray
} else {
    # Fallback: find any SMAPI log
    $found = Get-ChildItem -Path $GamePath -Filter "SMAPI-latest.txt" -Recurse -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($found) { $logPath = $found.FullName }
    Write-Host "[LOG] Using: $logPath" -ForegroundColor Gray
}

# Step 5: Poll for test report or process exit
$reportFound = $false
$reportLines = @()
$lastReadPosition = 0
$maxWaitSeconds = $TimeoutSeconds

while ($true) {
    # Check if process exited
    if ($process.HasExited) {
        Write-Host "[EXIT] Game exited (code: $($process.ExitCode))" -ForegroundColor Cyan
        break
    }

    # Check timeout
    $elapsed = ((Get-Date) - $startTime).TotalSeconds
    if ($elapsed -gt $maxWaitSeconds) {
        Write-Host "[TIMEOUT] ${maxWaitSeconds}s elapsed — stopping game." -ForegroundColor Yellow
        $process.Kill()
        Start-Sleep -Seconds 1
        "TIMEOUT" | Out-File $resultFile -Append -Encoding UTF8
        break
    }

    # Progress every 30s
    if ([int]$elapsed % 30 -eq 0 -and [int]$elapsed -ne 0) {
        Write-Host "[WAIT] ${elapsed}s elapsed, waiting for test report..." -ForegroundColor Gray
    }

    # Read new log content looking for report
    if ($logPath -and (Test-Path $logPath)) {
        try {
            $content = Get-Content $logPath -Encoding UTF8 -ReadCount 0 -ErrorAction SilentlyContinue
            if ($content -and $content.Count -gt $lastReadPosition) {
                for ($j = $lastReadPosition; $j -lt $content.Count; $j++) {
                    $line = $content[$j]
                    if ($line -match "=== End of Report ===") {
                        $reportFound = $true
                        Write-Host "[REPORT] Test report detected! Extracting..." -ForegroundColor Green
                        for ($k = $j; $k -ge 0; $k--) {
                            if ($content[$k] -match "=== Test Report ===") {
                                $reportLines = $content[$k..$j]
                                break
                            }
                        }
                        break
                    }
                }
                $lastReadPosition = $content.Count
            }
        } catch { }
    }

    if ($reportFound) { break }
    Start-Sleep -Seconds 2
}

# Step 6: Kill game if still running
if (-not $process.HasExited) {
    Write-Host "[STOP] Closing game..." -ForegroundColor Yellow
    $process.Kill()
    Start-Sleep -Seconds 2
}

# Step 7: Extract report from log
$totalElapsed = [math]::Round(((Get-Date) - $startTime).TotalSeconds, 1)

if ($reportLines.Count -gt 0) {
    # Write report to result file
    $reportLines | Out-File -FilePath $resultFile -Encoding UTF8
    "" | Out-File -FilePath $resultFile -Append -Encoding UTF8
    "Elapsed: ${totalElapsed}s" | Out-File -FilePath $resultFile -Append -Encoding UTF8

    # Print to console
    Write-Host ""
    Write-Host "========================================" -ForegroundColor Cyan
    Write-Host "  Test Results" -ForegroundColor Cyan
    Write-Host "========================================" -ForegroundColor Cyan

    $passCount = 0; $failCount = 0
    foreach ($line in $reportLines) {
        if ($line -match "\[PASS\]") {
            Write-Host "  $line" -ForegroundColor Green
            $passCount++
        } elseif ($line -match "\[FAIL\]") {
            Write-Host "  $line" -ForegroundColor Red
            $failCount++
        } elseif ($line -match "Results:|Total time:|===") {
            Write-Host "  $line" -ForegroundColor Yellow
        } else {
            Write-Host "  $line" -ForegroundColor Gray
        }
    }

    Write-Host ""
    Write-Host "========================================" -ForegroundColor Cyan
    Write-Host "  Summary: $passCount PASS, $failCount FAIL" -ForegroundColor $(if ($failCount -eq 0) { "Green" } else { "Red" })
    Write-Host "  Total time: ${totalElapsed}s" -ForegroundColor Gray
    Write-Host "========================================" -ForegroundColor Cyan

    Write-Host "Results saved to: $resultFile" -ForegroundColor Gray
    exit $(if ($failCount -eq 0) { 0 } else { 1 })
} else {
    # Fallback: search log for report
    Write-Host ""
    Write-Host "[FALLBACK] No live report captured. Searching log..." -ForegroundColor Yellow

    $latestLog = Get-ChildItem -Path $GamePath -Filter "SMAPI-*.txt" -Recurse -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1

    if ($latestLog) {
        $inReport = $false
        $lines = @()
        Get-Content $latestLog.FullName -Encoding UTF8 | ForEach-Object {
            if ($_ -match "=== Test Report ===") { $inReport = $true }
            if ($inReport) { $lines += $_ }
            if ($_ -match "=== End of Report ===") { $inReport = $false }
        }
        if ($lines.Count -gt 0) {
            $lines | Out-File -FilePath $resultFile -Encoding UTF8
            "" | Out-File -FilePath $resultFile -Append -Encoding UTF8
            "NO_LIVE_REPORT_CAPTURED" | Out-File -FilePath $resultFile -Append -Encoding UTF8
            "Elapsed: ${totalElapsed}s" | Out-File -FilePath $resultFile -Append -Encoding UTF8
        }
    }

    Write-Host "Results saved to: $resultFile" -ForegroundColor Gray
    exit 1
}