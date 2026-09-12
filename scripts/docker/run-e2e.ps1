# run-e2e.ps1 -- Docker multiplayer E2E test driver (Phase 3).
# Orchestrates TS server + host + farmhand containers and runs C1-C5 tests.
#
# Prerequisites:
#   - Docker Desktop running (Linux containers)
#   - valleyagent-gameit image built (docker build -f docker/Dockerfile.gameit -t valleyagent-gameit .)
#   - docker/linux-game/ populated (run-it.ps1 or manual copy)
#   - docker/smapi-cache/ populated (SMAPI installer zip)
#   - docker/saves/ populated (save files, e.g. awa_445353290)
#
# Usage:
#   .\scripts\docker\run-e2e.ps1                      # full run
#   .\scripts\docker\run-e2e.ps1 -Vnc                  # expose VNC ports
#   .\scripts\docker\run-e2e.ps1 -SkipTs               # assume TS already running

param(
    [switch]$Vnc,
    [switch]$SkipTs
)

$ErrorActionPreference = "Stop"

# ── Helper functions ──

function Wait-LogLine {
    param(
        [string]$Path,
        [string]$Pattern,
        [int]$TimeoutSeconds,
        [string]$Label = ""
    )
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        if (Test-Path $Path) {
            try {
                $fs = [System.IO.FileStream]::new($Path, [System.IO.FileMode]::Open,
                    [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
                $sr = [System.IO.StreamReader]::new($fs, [System.Text.Encoding]::UTF8)
                $text = $sr.ReadToEnd()
                $sr.Close()
                $fs.Close()
                if ($text -match $Pattern) {
                    if ($Label) { Write-Host "  [$Label] matched: $Pattern" }
                    return $true
                }
            }
            catch {
                # File may be locked by SMAPI; retry next iteration
            }
        }
        Start-Sleep -Seconds 2
    }
    if ($Label) { Write-Host "  [$Label] TIMEOUT after ${TimeoutSeconds}s waiting for: $Pattern" }
    return $false
}

function Send-Command {
    param(
        [string]$Path,
        [string]$Command
    )
    # Wait a moment for CommandFileWatcher to be ready (previous command may have just been consumed)
    Start-Sleep -Milliseconds 500
    [System.IO.File]::WriteAllText($Path, $Command, (New-Object System.Text.UTF8Encoding $false))
    Write-Host "  -> $Command"
}

function Write-Step {
    param([string]$Name, [bool]$Passed, [string]$Detail = "")
    $status = if ($Passed) { "PASS" } else { "FAIL" }
    $color = if ($Passed) { "Green" } else { "Red" }
    $msg = "  {0,-8} {1}" -f $status, $Name
    if ($Detail) { $msg += " ($Detail)" }
    Write-Host $msg -ForegroundColor $color
}

# ── Resolve repo root ──
$repoRoot = (Get-Location).Path
if (-not (Test-Path (Join-Path $repoRoot "AGENTS.md"))) {
    $repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
}

# ── File paths ──
$composeFile = Join-Path $repoRoot "docker\docker-compose.e2e.yml"
$hostConfigJson = Join-Path $repoRoot "docker\mods-cache-host\ValleyAgent\config.json"
$hostSmaLog = Join-Path $repoRoot "docker\data-host\game\StardewValley\ErrorLogs\SMAPI-latest.txt"
$farmSmaLog = Join-Path $repoRoot "docker\data-farmhand\game\StardewValley\ErrorLogs\SMAPI-latest.txt"
$hostCmdFile = Join-Path $repoRoot "docker\data-host\Mods\ValleyAgent.TestMod\test_commands_host.txt"
$farmCmdFile = Join-Path $repoRoot "docker\data-farmhand\Mods\ValleyAgent.TestMod\test_commands_farmhand.txt"
$c5RelDir = Join-Path $repoRoot "docker\data-host\Mods\ValleyAgent\agents\Haley_players"

# ── Validate prerequisites ──
if (-not (Test-Path $composeFile)) {
    Write-Error "Compose file not found: $composeFile"
    exit 1
}

Write-Host ""
Write-Host "=========================================="
Write-Host "  Docker Multiplayer E2E Test (Phase 3)"
Write-Host "=========================================="
Write-Host ""

# ── 1. Read LLM API key ──
Write-Host "[1/7] Reading LLM API key..."
if (-not (Test-Path $hostConfigJson)) {
    Write-Host "  mods-cache-host not found, running prep-mods.ps1 -Role host..."
    & powershell -NoProfile -File (Join-Path $repoRoot "scripts\docker\prep-mods.ps1") -Role host
    if (-not (Test-Path $hostConfigJson)) {
        Write-Error "prep-mods.ps1 -Role host did not produce $hostConfigJson"
        exit 1
    }
}
$vaConfig = Get-Content $hostConfigJson -Raw | ConvertFrom-Json
$llmKey = $vaConfig.LlmApiKey
if ([string]::IsNullOrWhiteSpace($llmKey)) {
    Write-Error "LlmApiKey is empty in $hostConfigJson"
    exit 1
}
# Mask key for display
$maskedKey = $llmKey.Substring(0, [Math]::Min(8, $llmKey.Length)) + "..."
Write-Host "  LLM key: $maskedKey"

# Inject LLM env vars so compose ${LLM_*} interpolation works (compose reads
# process env, NOT PowerShell variables -- missing env would fail `up -d`).
$env:LLM_API_KEY = $llmKey
$env:LLM_MODEL = $vaConfig.LlmModel
$env:LLM_BASE_URL = $vaConfig.LlmBaseUrl
$env:LLM_PROVIDER = if (-not [string]::IsNullOrWhiteSpace($vaConfig.LlmProvider)) { $vaConfig.LlmProvider } else { "minimax" }

# ── 2. Prepare mods for farmhand if needed ──
$farmhandModsCache = Join-Path $repoRoot "docker\mods-cache-farmhand"
if (-not (Test-Path $farmhandModsCache)) {
    Write-Host "  mods-cache-farmhand not found, running prep-mods.ps1 -Role farmhand..."
    & powershell -NoProfile -File (Join-Path $repoRoot "scripts\docker\prep-mods.ps1") -Role farmhand
}

# ── 3. Ensure data directories exist ──
Write-Host "[2/7] Preparing data directories..."
$dataDirs = @(
    (Join-Path $repoRoot "docker\data-host"),
    (Join-Path $repoRoot "docker\data-farmhand")
)
foreach ($d in $dataDirs) {
    if (-not (Test-Path $d)) {
        New-Item -ItemType Directory -Path $d -Force | Out-Null
        Write-Host "  Created: $d"
    }
}

# Clean old SMAPI logs to avoid false positive matches from prior runs
Remove-Item $hostSmaLog, $farmSmaLog -ErrorAction SilentlyContinue

# ── 4. VNC override ──
$env:COMPOSE_FILE = $composeFile
$vncOverride = $null
if ($Vnc) {
    Write-Host "  VNC enabled: generating port override..."
    $vncOverride = Join-Path $repoRoot "docker\docker-compose.e2e.vnc.yml"
    @"
services:
  valley-host:
    ports:
      - "5801:5800"
      - "5901:5900"
  valley-farmhand:
    ports:
      - "5802:5800"
      - "5902:5900"
"@ | Set-Content $vncOverride -Encoding UTF8
    $env:COMPOSE_FILE = "${composeFile};${vncOverride}"
}

# ── 5. Start containers ──
Write-Host "[3/7] Starting containers..."
$composeArgs = @("-f", $composeFile)
if ($Vnc) {
    $composeArgs = @("-f", $composeFile, "-f", $vncOverride)
}

try {
    if ($SkipTs) {
        # Skip TS: only start game containers (assumes valley-ts already running)
        Write-Host "  -SkipTs: skipping valley-ts, starting game containers only..."
        & docker compose @composeArgs up -d --no-deps valley-host valley-farmhand
    } else {
        # Start all services. depends_on + healthcheck handles ordering:
        #   valley-ts starts first, host waits for TS healthy, farmhand waits for host started.
        & docker compose @composeArgs up -d
    }
    if ($LASTEXITCODE -ne 0) {
        throw "docker compose up failed (exit $LASTEXITCODE)"
    }
    Write-Host "  Containers starting..."

    # ── 6. Wait for host SMAPI ──
    Write-Host "[4/7] Waiting for host SMAPI save load..."
    if (-not (Wait-LogLine -Path $hostSmaLog -Pattern "ValleyAgent TestMod\] Save loaded" `
            -TimeoutSeconds 300 -Label "host-save")) {
        throw "Host SMAPI did not load save within 300s"
    }

    # Give CommandFileWatcher time to initialize
    Start-Sleep -Seconds 5

    # ── 7. Host multiplayer server ──
    Write-Host "[5/7] Starting host multiplayer server..."
    Send-Command -Path $hostCmdFile -Command "va_mp_host"
    if (-not (Wait-LogLine -Path $hostSmaLog -Pattern "MP\] Hosted multiplayer server" `
            -TimeoutSeconds 120 -Label "host-mp")) {
        throw "Host multiplayer server did not start within 120s"
    }

    # Give host a moment to finish server setup
    Start-Sleep -Seconds 5

    # ── 8. Wait for farmhand SMAPI ──
    Write-Host "[6/7] Waiting for farmhand SMAPI save load..."
    if (-not (Wait-LogLine -Path $farmSmaLog -Pattern "ValleyAgent TestMod\] (Save loaded|ValleyAgent TestMod loaded)" `
            -TimeoutSeconds 300 -Label "farm-save")) {
        throw "Farmhand SMAPI did not load within 300s"
    }

    Start-Sleep -Seconds 5

    # ── 9. Farmhand join ──
    Write-Host "  Joining farmhand to host..."
    Send-Command -Path $farmCmdFile -Command "va_mp_join valley-host:24642"
    if (-not (Wait-LogLine -Path $farmSmaLog -Pattern "MP\] Joined game and activated farmhand" `
            -TimeoutSeconds 180 -Label "farm-join")) {
        throw "Farmhand did not join within 180s"
    }
    Start-Sleep -Seconds 5

    # ── 10. C1-C5 test sequence ──
    Write-Host "[7/7] Running C1-C5 test sequence..."
    Write-Host ""
    $results = @()

    # Pre-allocate: host allocates Haley as agent
    Write-Host "  Allocating Haley..."
    Send-Command -Path $hostCmdFile -Command "va_test_alloc Haley"
    $allocOk = Wait-LogLine -Path $hostSmaLog -Pattern "\[Alloc\] Haley allocated" `
        -TimeoutSeconds 60 -Label "alloc"
    $results += @{ Name = "Alloc"; Passed = $allocOk }
    if (-not $allocOk) {
        Write-Host "  WARNING: Haley allocation failed; C1-C5 may fail." -ForegroundColor Yellow
    }
    Start-Sleep -Seconds 3

    # C1: farmhand -- checkAction triggers DialogueBox on farmhand
    # Log format (MultiplayerTestCommands.cs): "[C1] PASS: ..." / "[C1] FAIL: ..."
    Write-Host "  C1: checkAction (farmhand)..."
    Send-Command -Path $farmCmdFile -Command "va_test_c1 Haley"
    $c1Ok = Wait-LogLine -Path $farmSmaLog -Pattern "\[C1\] PASS" -TimeoutSeconds 60 -Label "C1"
    if (-not $c1Ok) {
        $c1Fail = Wait-LogLine -Path $farmSmaLog -Pattern "\[C1\] (FAIL|SKIP)" -TimeoutSeconds 10 -Label "C1-fail"
        $results += @{ Name = "C1"; Passed = $false; Detail = if ($c1Fail) { "farmhand checkAction FAIL/SKIP" } else { "C1 no result in 60s" } }
    } else {
        $results += @{ Name = "C1"; Passed = $true; Detail = "farmhand checkAction" }
    }

    # C2: farmhand -- gift path (item 74)
    Write-Host "  C2: gift path (farmhand)..."
    Send-Command -Path $farmCmdFile -Command "va_test_c2 Haley 74"
    $c2Ok = Wait-LogLine -Path $farmSmaLog -Pattern "\[C2\] PASS" -TimeoutSeconds 60 -Label "C2"
    if (-not $c2Ok) {
        $c2Fail = Wait-LogLine -Path $farmSmaLog -Pattern "\[C2\] (FAIL|SKIP)" -TimeoutSeconds 10 -Label "C2-fail"
        $results += @{ Name = "C2"; Passed = $false; Detail = if ($c2Fail) { "farmhand gift FAIL/SKIP" } else { "C2 no result in 60s" } }
    } else {
        $results += @{ Name = "C2"; Passed = $true; Detail = "farmhand gift" }
    }

    # C3: farmhand -- dialogue message -> host TS LLM -> response back
    Write-Host "  C3: dialogue via LLM (farmhand)..."
    Send-Command -Path $farmCmdFile -Command "va_test_c3 Haley"
    $c3Ok = Wait-LogLine -Path $farmSmaLog -Pattern "\[C3\] PASS" -TimeoutSeconds 150 -Label "C3"
    if (-not $c3Ok) {
        $c3Fail = Wait-LogLine -Path $farmSmaLog -Pattern "\[C3\] (FAIL|SKIP)" -TimeoutSeconds 10 -Label "C3-fail"
        $results += @{ Name = "C3"; Passed = $false; Detail = if ($c3Fail) { "farmhand dialogue FAIL/SKIP" } else { "C3 no result in 150s" } }
    } else {
        $results += @{ Name = "C3"; Passed = $true; Detail = "farmhand dialogue" }
    }

    # C4: host -- execute_adjust with farmhand playerId, money lands on farmhand
    Write-Host "  C4: multiplayer adjust (host)..."
    Send-Command -Path $hostCmdFile -Command "va_test_c4 Haley"
    $c4Ok = Wait-LogLine -Path $hostSmaLog -Pattern "\[C4\] PASS" -TimeoutSeconds 90 -Label "C4"
    if (-not $c4Ok) {
        $c4Fail = Wait-LogLine -Path $hostSmaLog -Pattern "\[C4\] (FAIL|SKIP)" -TimeoutSeconds 10 -Label "C4-fail"
        $results += @{ Name = "C4"; Passed = $false; Detail = if ($c4Fail) { "host adjust FAIL/SKIP" } else { "C4 no result in 90s" } }
    } else {
        $results += @{ Name = "C4"; Passed = $true; Detail = "host adjust" }
    }

    # C5: host -- host player dialogue, assert two rel.json files in Haley_players
    Write-Host "  C5: multi-player memory (host)..."
    Send-Command -Path $hostCmdFile -Command "va_test_c3 Haley $([char]0x665A)$([char]0x4E0A)$([char]0x597D)$([char]0x5440)"
    Start-Sleep -Seconds 60
    # C5 assertion: check Haley_players/ for >= 2 *_rel.json files
    # NOTE: count NEW files only -- mods-cache ships stale rel files from the
    # host E2E runs, which would make this assertion pass spuriously.
    # 2026-08-23 audit: _legacy_rel.json is the M2 migration bucket (pre-playerId
    # history), not a real per-player file -- exclude it from the count.
    $c5Ok = $false
    $c5Detail = ""
    if (Test-Path $c5RelDir) {
        # PS 5.1 quirk: combining -Filter with -Exclude breaks enumeration and
        # returns 0 files (C5 false-negative 2026-09-09). Filter only, then
        # exclude the legacy bucket in-memory.
        $relFiles = Get-ChildItem $c5RelDir -Filter "*_rel.json" -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -ne "_legacy_rel.json" }
        $relCount = if ($relFiles) { $relFiles.Count } else { 0 }
        $c5Ok = ($relCount -ge 2)
        $c5Detail = "$relCount rel files"
    } else {
        $c5Detail = "Haley_players/ not found"
    }
    $results += @{ Name = "C5"; Passed = $c5Ok; Detail = $c5Detail }

    # ── Results ──
    Write-Host ""
    Write-Host "=========================================="
    Write-Host "  Results"
    Write-Host "=========================================="
    $anyFail = $false
    foreach ($r in $results) {
        Write-Step -Name $r.Name -Passed $r.Passed -Detail $r.Detail
        if (-not $r.Passed) { $anyFail = $true }
    }
    Write-Host ""

    # ── Artifacts ──
    Write-Host "Artifacts:"
    Write-Host "  Host log:     $hostSmaLog"
    Write-Host "  Farmhand log: $farmSmaLog"
    Write-Host "  C5 rel dir:   $c5RelDir"
    Write-Host ""

    if ($anyFail) {
        Write-Host "RESULT: FAIL" -ForegroundColor Red
        exit 1
    } else {
        Write-Host "RESULT: ALL PASS" -ForegroundColor Green
        exit 0
    }
}
catch {
    Write-Host ""
    Write-Host "ERROR: $_" -ForegroundColor Red
    Write-Host $_.ScriptStackTrace -ForegroundColor DarkGray
    exit 1
}
finally {
    # ── Cleanup ──
    Write-Host ""
    Write-Host "Stopping containers..."
    if ($SkipTs) {
        # Only stop game containers, leave TS running
        & docker stop valley-host valley-farmhand 2>$null
        & docker rm valley-host valley-farmhand 2>$null
    } else {
        & docker compose @composeArgs down --timeout 10 2>$null
    }

    # Clean up VNC override if generated
    if ($Vnc -and $vncOverride -and (Test-Path $vncOverride)) {
        Remove-Item $vncOverride -Force -ErrorAction SilentlyContinue
    }
}
