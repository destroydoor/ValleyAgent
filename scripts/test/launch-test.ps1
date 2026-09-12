# ValleyTalk TestMod Launch Script
# Builds both projects, copies to Mods folder, launches Stardew Valley via SMAPI.
# After game exit, tails the SMAPI log to show test results.

param(
    [string]$ModsFolder = "",
    [string]$StardewPath = "",
    [string]$RepoRoot = "",
    [switch]$NoBuild,
    [switch]$NoLaunch
)

$ErrorActionPreference = "Stop"
if (-not $RepoRoot) { $RepoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot) }

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  ValleyTalk TestMod Launcher" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

# ------------------------------------------------------------------
# 1. Locate SMAPI and Stardew Valley
# ------------------------------------------------------------------
if (-not $StardewPath) {
    # Try common paths
    $candidates = @(
        "$env:ProgramFiles\Steam\steamapps\common\Stardew Valley",
        "C:\Program Files (x86)\Steam\steamapps\common\Stardew Valley",
        "${env:ProgramFiles(x86)}\Steam\steamapps\common\Stardew Valley",
        "D:\SteamLibrary\steamapps\common\Stardew Valley",
        "E:\SteamLibrary\steamapps\common\Stardew Valley"
    )
    foreach ($c in $candidates) {
        if (Test-Path "$c\Stardew Valley.exe") {
            $StardewPath = $c
            break
        }
        if (Test-Path "$c\StardewModdingAPI.exe") {
            $StardewPath = $c
            break
    }
}
else {
    Write-Host "[BUILD] Skipped (-NoBuild)" -ForegroundColor Yellow
}

# ------------------------------------------------------------------
# 3. Copy mod DLLs to Mods folder
# ------------------------------------------------------------------
Write-Host ""
Write-Host "[COPY] Copying mod DLLs to Mods folder..." -ForegroundColor Yellow

$BuildDir = Join-Path $RepoRoot "src\ValleyAgent\bin\Debug\net6.0"

# Copy ValleyAgent mod
$TargetModDir = Join-Path $ModsFolder "ValleyAgent"
if (-not (Test-Path $TargetModDir)) {
    New-Item -ItemType Directory -Path $TargetModDir -Force | Out-Null
}

$filesToCopy = @(
    "ValleyAgent.dll", "ValleyAgent.pdb", "ValleyTalk.dll", "ValleyTalk.pdb",
    "manifest.json", "config.json"
)
foreach ($f in $filesToCopy) {
    $src = Join-Path $BuildDir $f
    $dst = Join-Path $TargetModDir $f
    if (Test-Path $src) {
        Copy-Item $src $dst -Force
    }
    else {
        Write-Host "  WARN: $f not found in build output" -ForegroundColor Yellow
    }
}

# Copy i18n folder
$I18nSrc = Join-Path $BuildDir "i18n"
$I18nDst = Join-Path $TargetModDir "i18n"
if (Test-Path $I18nSrc) {
    if (-not (Test-Path $I18nDst)) { New-Item -ItemType Directory -Path $I18nDst -Force | Out-Null }
    Copy-Item "$I18nSrc\*" $I18nDst -Force -Recurse
}

# Copy RAG folder
$RagSrc = Join-Path $BuildDir "RAG"
$RagDst = Join-Path $TargetModDir "RAG"
if (Test-Path $RagSrc) {
    if (-not (Test-Path $RagDst)) { New-Item -ItemType Directory -Path $RagDst -Force | Out-Null }
    Copy-Item "$RagSrc\*" $RagDst -Force -Recurse
}

# Copy TestMod DLL (goes inside ValleyAgent folder as a companion)
$TestModDll = Join-Path $RepoRoot "src\ValleyAgent.TestMod\bin\Debug\net6.0\ValleyAgent.TestMod.dll"
$TestModPdb = Join-Path $RepoRoot "src\ValleyAgent.TestMod\bin\Debug\net6.0\ValleyAgent.TestMod.pdb"
if (Test-Path $TestModDll) {
    Copy-Item $TestModDll $TargetModDir -Force
    if (Test-Path $TestModPdb) { Copy-Item $TestModPdb $TargetModDir -Force }
}

Write-Host "[COPY] Done — ValleyAgent + TestMod copied to $TargetModDir" -ForegroundColor Green

# ------------------------------------------------------------------
# 3.5. Clean old SMAPI log for clean test output
# ------------------------------------------------------------------
$logPattern = "SMAPI-latest.txt"
$logsDir = $StardewPath
$logFiles = Get-ChildItem -Path $logsDir -Filter "SMAPI-*.txt" -ErrorAction SilentlyContinue | 
    Sort-Object LastWriteTime -Descending

# Rename the latest log so the new one is clean
if ($logFiles) {
    $latest = $logFiles | Select-Object -First 1
    if ($latest.Name -like "SMAPI-latest*") {
        $archiveName = "SMAPI-" + (Get-Date -Format "yyyy-MM-dd_HHmmss") + ".txt"
        try {
            Rename-Item -Path $latest.FullName -NewName $archiveName -ErrorAction SilentlyContinue
            Write-Host "[LOG] Archived previous SMAPI log as $archiveName" -ForegroundColor Gray
        }
        catch {
            Write-Host "[LOG] Could not archive previous log: $_" -ForegroundColor Yellow
        }
    }
}

# ------------------------------------------------------------------
# 4. Launch Stardew Valley via SMAPI and auto-monitor for test report
# ------------------------------------------------------------------
if (-not $NoLaunch) {
    Write-Host ""
    Write-Host "[LAUNCH] Starting Stardew Valley with SMAPI..." -ForegroundColor Cyan
    Write-Host "         Monitoring SMAPI log for test report..." -ForegroundColor Cyan
    Write-Host ""

    $startTime = Get-Date
    $reportFound = $false
    $reportLines = @()
    $maxWaitSeconds = 300  # 5 minute timeout
    
    # Launch SMAPI in background
    $process = Start-Process -FilePath $SmapiExe.FullName -WorkingDirectory $StardewPath -PassThru
    $pid = $process.Id
    Write-Host "[LAUNCH] SMAPI process started (PID: $pid)" -ForegroundColor Gray
    
    # The SMAPI log file is written to StardewValley/ErrorLogs/SMAPI-latest.txt by default.
    # SMAPI also uses <gamefolder>/SMAPI-latest.txt on some versions. Check both.
    $logCandidates = @(
        (Join-Path $StardewPath "ErrorLogs\SMAPI-latest.txt"),
        (Join-Path $StardewPath "SMAPI-latest.txt")
    )
    
    # Wait for a log file to appear (SMAPI creates it on startup)
    $logPath = $null
    for ($i = 0; $i -lt 30; $i++) {
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
        Write-Host "[LOG] Waiting for SMAPI log file... (checking ErrorLogs/)" -ForegroundColor Yellow
        # Broad search
        $found = Get-ChildItem -Path $StardewPath -Filter "SMAPI-latest.txt" -Recurse -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending |
            Select-Object -First 1
        if ($found) { $logPath = $found.FullName }
    }
    
    if ($logPath) {
        Write-Host "[LOG] Monitoring: $logPath" -ForegroundColor Gray
    } else {
        Write-Host "[LOG] WARNING: Could not locate SMAPI log file. Will still wait for process exit." -ForegroundColor Yellow
    }
    
    # Poll the log every 2 seconds
    $lastReadPosition = 0
    while (-not $reportFound) {
        # Check if process exited
        if ($process.HasExited) {
            Write-Host "[EXIT] Stardew Valley exited (code: $($process.ExitCode))" -ForegroundColor Cyan
            break
        }
        
        # Check timeout
        $elapsed = ((Get-Date) - $startTime).TotalSeconds
        if ($elapsed -gt $maxWaitSeconds) {
            Write-Host "[TIMEOUT] $maxWaitSeconds seconds elapsed — stopping game." -ForegroundColor Yellow
            $process.Kill()
            Start-Sleep -Seconds 1
            break
        }
        
        # Progress indicator every 10s
        if ([int]$elapsed % 10 -eq 0 -and [int]$elapsed -ne 0) {
            Write-Host "[WAIT] ${elapsed}s elapsed, still waiting for test report..." -ForegroundColor Gray
        }
        
        # Read new log content
        if ($logPath -and (Test-Path $logPath)) {
            try {
                $content = Get-Content $logPath -Encoding UTF8 -ReadCount 0 -ErrorAction SilentlyContinue
                if ($content -and $content.Count -gt $lastReadPosition) {
                    # Check new lines for report markers
                    for ($j = $lastReadPosition; $j -lt $content.Count; $j++) {
                        $line = $content[$j]
                        if ($line -match "=== End of Report ===") {
                            $reportFound = $true
                            Write-Host "[REPORT] Test report complete! Extracting results..." -ForegroundColor Green
                            
                            # Extract report section (backward search to find start)
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
            }
            catch {
                # Log file temporarily locked — skip this poll
            }
        }
        
        if (-not $reportFound) {
            Start-Sleep -Seconds 2
        }
    }
    
    # Kill game if still running
    if (-not $process.HasExited) {
        Write-Host "[STOP] Closing Stardew Valley..." -ForegroundColor Yellow
        $process.Kill()
        Start-Sleep -Seconds 2
    }
    
    $totalElapsed = [math]::Round(((Get-Date) - $startTime).TotalSeconds, 1)
    Write-Host "[DONE] Total time: ${totalElapsed}s" -ForegroundColor Gray
    
    # ------------------------------------------------------------------
    # 5. Display test results
    # ------------------------------------------------------------------
    if ($reportLines.Count -gt 0) {
        Write-Host ""
        Write-Host "========================================" -ForegroundColor Cyan
        Write-Host "  Test Results" -ForegroundColor Cyan
        Write-Host "========================================" -ForegroundColor Cyan
        
        $passCount = 0
        $failCount = 0
        foreach ($line in $reportLines) {
            if ($line -match "\[PASS\]") {
                Write-Host "  $line" -ForegroundColor Green
                $passCount++
            }
            elseif ($line -match "\[FAIL\]") {
                Write-Host "  $line" -ForegroundColor Red
                $failCount++
            }
            elseif ($line -match "Results:|Total time:|===") {
                Write-Host "  $line" -ForegroundColor Yellow
            }
            else {
                Write-Host "  $line" -ForegroundColor Gray
            }
        }
        
        Write-Host ""
        Write-Host "========================================" -ForegroundColor Cyan
        Write-Host "  Summary: $passCount PASS, $failCount FAIL" -ForegroundColor $(if ($failCount -eq 0) { "Green" } else { "Red" })
        Write-Host "========================================" -ForegroundColor Cyan
        
        if ($failCount -eq 0) {
            exit 0
        } else {
            exit 1
        }
    }
    else {
        # Fallback: try reading the log file directly
        Write-Host ""
        Write-Host "[FALLBACK] No live report captured. Searching log file..." -ForegroundColor Yellow
        
        $latestLog = Get-ChildItem -Path $StardewPath -Filter "SMAPI-*.txt" -Recurse -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending |
            Select-Object -First 1
        
        if ($latestLog) {
            $inReport = $false
            Get-Content $latestLog.FullName -Encoding UTF8 | ForEach-Object {
                if ($_ -match "=== Test Report ===") { $inReport = $true }
                if ($inReport) { Write-Host "  $_" -ForegroundColor $(if ($_ -match "\[FAIL\]") {"Red"} elseif ($_ -match "\[PASS\]") {"Green"} else {"Gray"}) }
                if ($_ -match "=== End of Report ===") { $inReport = $false }
            }
        } else {
            Write-Host "No SMAPI log found." -ForegroundColor Red
        }
    }
}
else {
    Write-Host "[LAUNCH] Skipped (-NoLaunch)" -ForegroundColor Yellow
    Write-Host ""
    Write-Host "Mod files copied to: $TargetModDir" -ForegroundColor Green
    Write-Host "Launch manually: $($SmapiExe.FullName)" -ForegroundColor Green
}
    
    Write-Host "Reading: $($latestLog.FullName)" -ForegroundColor Gray
    
    # Extract test report section
    $inReport = $false
    $reportLines = @()
    Get-Content $latestLog.FullName -Encoding UTF8 | ForEach-Object {
        if ($_ -match "=== Test Report ===") {
            $inReport = $true
        }
        if ($inReport) {
            $reportLines += $_
        }
        if ($_ -match "=== End of Report ===") {
            $inReport = $false
        }
    }
    
    if ($reportLines.Count -gt 0) {
        Write-Host ""
        Write-Host "========================================" -ForegroundColor Cyan
        Write-Host "  Test Results" -ForegroundColor Cyan
        Write-Host "========================================" -ForegroundColor Cyan
        
        $passCount = 0
        $failCount = 0
        foreach ($line in $reportLines) {
            if ($line -match "\[PASS\]") {
                Write-Host "  $line" -ForegroundColor Green
                $passCount++
            }
            elseif ($line -match "\[FAIL\]") {
                Write-Host "  $line" -ForegroundColor Red
                $failCount++
            }
            elseif ($line -match "Results:|Total time:|===") {
                Write-Host "  $line" -ForegroundColor Yellow
            }
            else {
                Write-Host "  $line" -ForegroundColor Gray
            }
        }
        
        Write-Host ""
        Write-Host "========================================" -ForegroundColor Cyan
        Write-Host "  Summary: $passCount PASS, $failCount FAIL" -ForegroundColor $(if ($failCount -eq 0) { "Green" } else { "Red" })
        Write-Host "========================================" -ForegroundColor Cyan
    }
    else {
        Write-Host "No test report found in SMAPI log." -ForegroundColor Yellow
        Write-Host "Showing last 20 lines of log:" -ForegroundColor Gray
        Get-Content $latestLog.FullName -Encoding UTF8 -Tail 20 | ForEach-Object {
            Write-Host "  $_" -ForegroundColor Gray
        }
    }
}
else {
    Write-Host "[LAUNCH] Skipped (-NoLaunch)" -ForegroundColor Yellow
    Write-Host ""
    Write-Host "Mod files copied to: $TargetModDir" -ForegroundColor Green
    Write-Host "Launch manually: $($SmapiExe.FullName)" -ForegroundColor Green
}
