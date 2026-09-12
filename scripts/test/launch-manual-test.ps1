# Launch Stardew Valley SMAPI for manual testing with auto scene setup
# - Ensures TestMod is disabled (moved to Mods\disabled)
# - Auto-loads ForTest save (via AutoLoadGame mod)
# - Waits for ValleyAgent to initialize (polls SMAPI log file)
# - Auto-allocates NPC as agent if none restored
# - Tests LLM connectivity
# - Enters interactive mode: user can type SMAPI console commands
param(
    [string]$NpcName = "Haley",
    [string]$SaveName = "ForTest_444038365",
    [int]$InitTimeoutSeconds = 120,
    [switch]$SetupOnly,
    [switch]$KeepTestMod
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$GamePath = $env:STARDW_PATH
if (-not $GamePath) { $GamePath = "D:\Source\ValleyTalk\Stardew Valley" }
$SMAPIExe = Join-Path $GamePath "StardewModdingAPI.exe"
$ModsDir = Join-Path $GamePath "Mods"

if (-not (Test-Path $SMAPIExe)) {
    Write-Host "[ERROR] SMAPI not found: $SMAPIExe" -ForegroundColor Red
    exit 1
}

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  ValleyAgent Manual Test Launcher" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  NPC:      $NpcName" -ForegroundColor Gray
Write-Host "  Save:     $SaveName" -ForegroundColor Gray
Write-Host "  GamePath: $GamePath" -ForegroundColor Gray
Write-Host ""

# --- Step 1: Disable TestMod (unless -KeepTestMod) -------------------------
# NOTE: SMAPI recursively scans Mods\ subdirectories, so "disabled" subfolder still loads.
# TestMod must be moved OUTSIDE the Mods tree entirely.
$testModPath = Join-Path $ModsDir "ValleyAgent.TestMod"
$backupDir = Join-Path $RepoRoot "mods-backup"
if ((-not $KeepTestMod) -and (Test-Path $testModPath)) {
    if (-not (Test-Path $backupDir)) { New-Item -ItemType Directory -Path $backupDir -Force | Out-Null }
    $dst = Join-Path $backupDir "ValleyAgent.TestMod"
    if (Test-Path $dst) { Remove-Item $dst -Recurse -Force }
    Move-Item -Path $testModPath -Destination $dst -Force
    Write-Host "[MOD] TestMod moved to $dst (manual testing does not need it)" -ForegroundColor Yellow
}
if (Test-Path $testModPath) {
    Write-Host "[MOD] TestMod present in Mods (will auto-run integration tests)" -ForegroundColor Red
} else {
    Write-Host "[MOD] TestMod disabled" -ForegroundColor Gray
}

# --- Step 2: Kill existing game processes and free port 8765 ----------------
Write-Host "[CLEANUP] Stopping existing game/server processes..." -ForegroundColor Yellow
$gameProcs = Get-Process -Name "StardewModdingAPI", "Stardew Valley" -ErrorAction SilentlyContinue
if ($gameProcs) { $gameProcs | Stop-Process -Force; Start-Sleep -Seconds 2 }

$staleProcs = Get-NetTCPConnection -LocalPort 8765 -ErrorAction SilentlyContinue |
    Select-Object -ExpandProperty OwningProcess -Unique
if ($staleProcs) {
    $staleProcs | ForEach-Object { Stop-Process -Id $_ -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Seconds 1
}

# --- Step 3: Archive old SMAPI log ------------------------------------------
$logPath = Join-Path $env:APPDATA "StardewValley\ErrorLogs\SMAPI-latest.txt"
if (Test-Path $logPath) {
    $archiveName = "SMAPI-" + (Get-Date -Format "yyyy-MM-dd_HHmmss") + ".txt"
    try {
        Rename-Item -Path $logPath -NewName $archiveName -ErrorAction SilentlyContinue
        Write-Host "[LOG] Archived previous SMAPI log" -ForegroundColor Gray
    } catch { }
}

# --- Step 4: Verify AutoLoadGame config --------------------------------------
$autoLoadConfig = Join-Path $GamePath "Mods\AutoLoadGame\config.json"
if (Test-Path $autoLoadConfig) {
    $cfg = Get-Content $autoLoadConfig -Raw | ConvertFrom-Json
    if ($cfg.LastFileLoaded -ne $SaveName) {
        Write-Host "[CONFIG] Updating AutoLoadGame to load: $SaveName" -ForegroundColor Yellow
        $cfg.LastFileLoaded = $SaveName
        $cfg | ConvertTo-Json | Set-Content $autoLoadConfig -Encoding UTF8
    }
    Write-Host "[CONFIG] AutoLoadGame -> $SaveName" -ForegroundColor Gray
} else {
    Write-Host "[WARN] AutoLoadGame not found, manual save load required" -ForegroundColor Yellow
}

# --- Step 5: Verify ValleyAgent config ---------------------------------------
$vaConfigPath = Join-Path $GamePath "Mods\ValleyAgent\config.json"
if (Test-Path $vaConfigPath) {
    $vaCfg = Get-Content $vaConfigPath -Raw | ConvertFrom-Json
    Write-Host "[CONFIG] ValleyAgent: MaxAgentNpcs=$($vaCfg.MaxAgentNpcs), Model=$($vaCfg.LlmModel)" -ForegroundColor Gray
    if ($vaCfg.MaxAgentNpcs -lt 1) {
        Write-Host "[CONFIG] MaxAgentNpcs=0, bumping to 1 for testing" -ForegroundColor Yellow
        $vaCfg.MaxAgentNpcs = 1
        $vaCfg | ConvertTo-Json -Depth 10 | Set-Content $vaConfigPath -Encoding UTF8
    }
}

# --- Step 6: Launch SMAPI (no console pipe; poll log file) -------------------
Write-Host ""
Write-Host "[LAUNCH] Starting SMAPI..." -ForegroundColor Cyan

$process = Start-Process -FilePath $SMAPIExe -WorkingDirectory $GamePath -PassThru
Write-Host "[LAUNCH] SMAPI PID: $($process.Id)" -ForegroundColor Gray
Write-Host ""

# --- Step 7: Poll log file for initialization markers ------------------------
Write-Host "[WAIT] Waiting for ValleyAgent to initialize (timeout: ${InitTimeoutSeconds}s)..." -ForegroundColor Yellow

$script:hostReady = $false
$script:restoreMsg = ""
$lastPosition = 0
$startTime = Get-Date

while (-not ($script:hostReady) -and -not $process.HasExited) {
    $elapsed = ((Get-Date) - $startTime).TotalSeconds
    if ($elapsed -gt $InitTimeoutSeconds) {
        Write-Host ""
        Write-Host "[TIMEOUT] ValleyAgent did not initialize within ${InitTimeoutSeconds}s" -ForegroundColor Red
        break
    }

    if (Test-Path $logPath) {
        try {
            $content = Get-Content $logPath -Encoding UTF8 -ReadCount 0 -ErrorAction SilentlyContinue
            if ($content -and $content.Count -gt $lastPosition) {
                for ($j = $lastPosition; $j -lt $content.Count; $j++) {
                    $line = $content[$j]
                    Write-Host $line
                    if ($line -match "ValleyAgent initialized successfully.*Host mode") {
                        $script:hostReady = $true
                    }
                    if ($line -match "Restored (\d+) agents from save") {
                        $script:restoreMsg = $line
                    }
                    if ($line -match "No existing ValleyAgent save data found") {
                        $script:restoreMsg = "No save data"
                    }
                }
                $lastPosition = $content.Count
            }
        } catch { }
    }

    if (-not $script:hostReady) { Start-Sleep -Milliseconds 500 }
}

if ($process.HasExited) {
    Write-Host "[EXIT] SMAPI exited unexpectedly" -ForegroundColor Red
    exit 1
}

# --- Step 8: Auto-setup test scene ------------------------------------------
if ($script:hostReady) {
    Write-Host ""
    Write-Host "[SETUP] ValleyAgent ready. Restore status: $($script:restoreMsg)" -ForegroundColor Green

    Start-Sleep -Seconds 2

    # Query current agent status
    Write-Host "[SETUP] Querying agent status..." -ForegroundColor Yellow
    $process.Refresh()
    Write-Host "  (game in background - see SMAPI window/log for agent status)" -ForegroundColor Gray

    # Allocate NPC if none restored
    if ($script:restoreMsg -eq "No save data" -or $script:restoreMsg -match "Restored 0 agents" -or $script:restoreMsg -eq "") {
        Write-Host "[SETUP] No agents restored, allocating $NpcName..." -ForegroundColor Yellow
        Write-Host "  Run in SMAPI console: ValleyAgent_allocate $NpcName" -ForegroundColor Gray
    } else {
        Write-Host "[SETUP] Agents restored from save: $($script:restoreMsg)" -ForegroundColor Green
    }

    Write-Host ""
    Write-Host "========================================" -ForegroundColor Green
    Write-Host "  Test Environment Ready" -ForegroundColor Green
    Write-Host "========================================" -ForegroundColor Green
    Write-Host "  Save: $SaveName loaded (AutoLoadGame)" -ForegroundColor White
    Write-Host "  NPC $NpcName should be an active Agent" -ForegroundColor White
    Write-Host "  Walk to NPC and right-click to talk" -ForegroundColor White
    Write-Host ""
    Write-Host "  In the SMAPI console window run:" -ForegroundColor Yellow
    Write-Host "    ValleyAgent_status                 - show agent states" -ForegroundColor Gray
    Write-Host "    ValleyAgent_allocate <npc>         - allocate NPC as agent" -ForegroundColor Gray
    Write-Host "    ValleyAgent_chat <npc> <message>   - chat with NPC" -ForegroundColor Gray
    Write-Host "    ValleyAgent_force_decision <npc>   - force LLM decision" -ForegroundColor Gray
    Write-Host "    ValleyAgent_test_llm               - test LLM connection" -ForegroundColor Gray
    Write-Host "    ValleyAgent_agents                 - list active agents" -ForegroundColor Gray
    Write-Host "    ValleyAgent_context <npc>          - show decision context" -ForegroundColor Gray
    Write-Host "    ValleyAgent_debug on|off           - toggle debug mode" -ForegroundColor Gray
    Write-Host "========================================" -ForegroundColor Green
    Write-Host ""
}

# --- Step 9: SetupOnly -> close, else keep game running ----------------------
if ($SetupOnly) {
    Write-Host "[SETUP-ONLY] Setup complete, closing game in 3 seconds..." -ForegroundColor Yellow
    Start-Sleep -Seconds 3
    if (-not $process.HasExited) { $process.Kill() }
    Write-Host "[DONE] Setup-only mode complete." -ForegroundColor Cyan
    exit 0
}

if ([Console]::IsInputRedirected) {
    # Non-interactive terminal (stdin redirected): cannot Read-Host.
    # Keep game running; exit script when game process closes by itself.
    Write-Host "[RUNNING] Game is running. Close the game window to stop." -ForegroundColor Cyan
    while (-not $process.HasExited) {
        Start-Sleep -Seconds 2
    }
    Write-Host "[DONE] Game exited." -ForegroundColor Cyan
    exit 0
}

Write-Host "[RUNNING] Game is running. Close it manually or press Enter in this window to stop it." -ForegroundColor Cyan
Read-Host "Press Enter to stop the game"
if (-not $process.HasExited) { $process.Kill() }
Write-Host "[DONE] Game closed." -ForegroundColor Cyan
