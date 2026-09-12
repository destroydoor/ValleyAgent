# run-soak.ps1 -- 3-farmhand host-freeze soak driver (2026-09-10).
# Reproduces the real-machine scenario: 1 host + 3 farmhands, farmhands spread
# across maps after an LLM dialogue creates a FOLLOW bond, then idle past noon
# while watching the main-thread watchdog (TestMod f755aa1).
#
# Scenario staging (all via TestMod test-driver commands, no production changes):
#   1. va_mp_host 3        -- host server + 3 farmhand cabins
#   2. va_test_alloc Haley -- allocate one agent NPC
#   3. 3 farmhands join staggered (also probes the "3rd player join" crash)
#   4. farmhand1 va_test_c3 -- ONE real LLM dialogue (sets LastDialoguePlayerId)
#   5. va_soak_follow Haley -- force FOLLOW (same API the E1/EXP tests use)
#   6. va_mp_goto per farmhand (scenario-dependent maps)
#   7. idle loop: watch watchdog captures / container state / thread CPU;
#      re-assert FOLLOW + diag every minute; end at game time 2000+ or timeout
#
# Usage:
#   .\scripts\docker\run-soak.ps1                      # spread scenario (Town/Mountain/Forest)
#   .\scripts\docker\run-soak.ps1 -Scenario mine       # farmhand1 into a real mine level
#   .\scripts\docker\run-soak.ps1 -SkipUp              # containers already up (resume monitoring)
#
# Exit codes: 0 = completed without freeze, 2 = FREEZE captured (artifacts kept),
#             3 = host container exited early, 1 = setup failure.

param(
    [ValidateSet("spread", "mine")]
    [string]$Scenario = "spread",
    [string]$Npc = "Haley",
    [string]$Npc2 = "",
    [int]$DurationMinutes = 25,
    # Game-time early-exit threshold. 2000 = end once past 8pm (default).
    # 2700 is unreachable (day wraps at 2600) -> runs the full duration and
    # crosses day-end: saving, new-day 600, day_started + director morningPlan.
    [int]$EndAtGameTime = 2000,
    [switch]$NoAgents,   # control round: no alloc/C3/follow -- isolates mod-induced effects
    [switch]$SkipUp
)

$ErrorActionPreference = "Stop"
# NOTE: keep this file ASCII-only -- PowerShell 5.1 reads no-BOM UTF-8 as GBK and
# multi-byte comments can swallow newlines (prep-mods.ps1 lesson).

# ── Helpers (same patterns as run-e2e.ps1) ──

function Wait-LogLine {
    param([string]$Path, [string]$Pattern, [int]$TimeoutSeconds, [string]$Label = "")
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        if (Test-Path $Path) {
            try {
                $fs = [System.IO.FileStream]::new($Path, [System.IO.FileMode]::Open,
                    [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
                $sr = [System.IO.StreamReader]::new($fs, [System.Text.Encoding]::UTF8)
                $text = $sr.ReadToEnd()
                $sr.Close(); $fs.Close()
                if ($text -match $Pattern) {
                    if ($Label) { Write-Host "  [$Label] matched" }
                    return $true
                }
            } catch { }
        }
        Start-Sleep -Seconds 2
    }
    if ($Label) { Write-Host "  [$Label] TIMEOUT after ${TimeoutSeconds}s: $Pattern" -ForegroundColor Yellow }
    return $false
}

function Send-Command {
    param([string]$Path, [string]$Command)
    Start-Sleep -Milliseconds 500
    [System.IO.File]::WriteAllText($Path, $Command, (New-Object System.Text.UTF8Encoding $false))
    Write-Host "  -> $Command"
}

function Read-LogTail {
    param([string]$Path, [int]$Lines = 40)
    if (-not (Test-Path $Path)) { return @() }
    try {
        return @(Get-Content $Path -Tail $Lines -ErrorAction SilentlyContinue)
    } catch { return @() }
}

function Get-LogText {
    param([string]$Path)
    if (-not (Test-Path $Path)) { return "" }
    try {
        $fs = [System.IO.FileStream]::new($Path, [System.IO.FileMode]::Open,
            [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
        $sr = [System.IO.StreamReader]::new($fs, [System.Text.Encoding]::UTF8)
        $text = $sr.ReadToEnd()
        $sr.Close(); $fs.Close()
        return $text
    } catch { return "" }
}

function Test-LlmProbe {
    # 10-token POST probe against the real endpoint from inside valley-ts.
    # CN-network flakiness comes in windows; only fire C3 when the probe passes.
    $out = & docker exec valley-ts bun -e "const key=process.env.LLM_API_KEY;fetch(process.env.LLM_BASE_URL+'/chat/completions',{method:'POST',headers:{'Content-Type':'application/json','Authorization':'Bearer '+key},body:JSON.stringify({model:process.env.LLM_MODEL,messages:[{role:'user',content:'hi'}],max_tokens:10})}).then(r=>console.log('PROBE',r.status)).catch(e=>console.log('PROBEERR',e.message))" 2>$null
    return ($out -match "PROBE 200")
}

function Send-C3WithProbe {
    param([string]$Farmhand, [string]$NpcName, [int]$Attempts = 3)
    $ok = $false
    for ($attempt = 1; $attempt -le $Attempts -and -not $ok; $attempt++) {
        $probed = $false
        for ($p = 1; $p -le 6 -and -not $probed; $p++) {
            if (Test-LlmProbe) { $probed = $true } else {
                Write-Host "  [probe] LLM unreachable, waiting 20s ($p/6)..."
                Start-Sleep -Seconds 20
            }
        }
        if (-not $probed) { Write-Host "  [probe] giving up on probe for this attempt" -ForegroundColor Yellow }
        Write-Host "[c3] $Farmhand dialogue with $NpcName attempt $attempt (real LLM)..."
        Send-Command -Path $cmds[$Farmhand] -Command "va_test_c3 $NpcName Please walk around with me today"
        # source=LLM required: a fallback reply (LLM fully down) must not count
        $ok = Wait-LogLine -Path $logs[$Farmhand] -Pattern "\[C3\] PASS \(source=LLM\)" -TimeoutSeconds 150 -Label "c3-$NpcName-$attempt"
    }
    return $ok
}

# ── Resolve repo root ──
$repoRoot = (Get-Location).Path
if (-not (Test-Path (Join-Path $repoRoot "AGENTS.md"))) {
    $repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
}

# ── Paths ──
$composeE2e = Join-Path $repoRoot "docker\docker-compose.e2e.yml"
$composeSoak = Join-Path $repoRoot "docker\docker-compose.soak.yml"
$hostConfigJson = Join-Path $repoRoot "docker\mods-cache-host\ValleyAgent\config.json"
$hostWatchdogDir = Join-Path $repoRoot "docker\data-host\Mods\ValleyAgent.TestMod\watchdog"
$artifacts = Join-Path $repoRoot ("\.tmp\soak-{0}-{1}" -f $Scenario, (Get-Date -Format "yyyyMMdd-HHmmss"))
New-Item -ItemType Directory -Path $artifacts -Force | Out-Null

$instanceData = @{
    host      = "data-host"
    farmhand1 = "data-farmhand"
    farmhand2 = "data-farmhand2"
    farmhand3 = "data-farmhand3"
}
$logs = @{}
$cmds = @{}
foreach ($k in $instanceData.Keys) {
    $logs[$k] = Join-Path $repoRoot ("docker\{0}\game\StardewValley\ErrorLogs\SMAPI-latest.txt" -f $instanceData[$k])
    $cmds[$k] = Join-Path $repoRoot ("docker\{0}\Mods\ValleyAgent.TestMod\test_commands_farmhand.txt" -f $instanceData[$k])
}
$hostCmd = Join-Path $repoRoot "docker\data-host\Mods\ValleyAgent.TestMod\test_commands_host.txt"
$cmds["host"] = $hostCmd
$threadSnap = Join-Path $artifacts "thread-cpu.log"

# ── Scenario map assignment ──
$gotoMap = @{
    farmhand1 = "Town"
    farmhand2 = "Mountain"
    farmhand3 = "Forest"
}
if ($Scenario -eq "mine") {
    $gotoMap["farmhand1"] = "UndergroundMine1"
}

Write-Host ""
Write-Host "======================================================"
Write-Host "  Host-freeze soak: scenario=$Scenario npc=$Npc"
Write-Host "  farmhand maps: fh1=$($gotoMap['farmhand1']) fh2=$($gotoMap['farmhand2']) fh3=$($gotoMap['farmhand3'])"
Write-Host "  artifacts: $artifacts"
Write-Host "======================================================"
Write-Host ""

if (-not $SkipUp) {
    # ── 1. Validate prerequisites ──
    foreach ($f in @($composeE2e, $composeSoak, $hostConfigJson)) {
        if (-not (Test-Path $f)) { Write-Error "Missing: $f"; exit 1 }
    }
    foreach ($role in @("farmhand", "farmhand2", "farmhand3")) {
        $cache = Join-Path $repoRoot "docker\mods-cache-$role"
        if (-not (Test-Path $cache)) {
            if ($role -eq "farmhand") {
                Write-Host "  Running prep-mods.ps1 -Role farmhand..."
                & powershell -NoProfile -File (Join-Path $repoRoot "scripts\docker\prep-mods.ps1") -Role farmhand
            } else {
                $src = Join-Path $repoRoot "docker\mods-cache-farmhand"
                if (-not (Test-Path $src)) { Write-Error "mods-cache-farmhand missing"; exit 1 }
                Write-Host "  Copying mods-cache-farmhand -> mods-cache-$role"
                Copy-Item -Recurse -Force $src $cache
            }
        }
    }
    foreach ($k in $instanceData.Keys) {
        $d = Join-Path $repoRoot ("docker\" + $instanceData[$k])
        if (-not (Test-Path $d)) { New-Item -ItemType Directory -Path $d -Force | Out-Null }
    }

    # ── 2. LLM env from host config (compose interpolation) ──
    $vaConfig = Get-Content $hostConfigJson -Raw | ConvertFrom-Json
    if ([string]::IsNullOrWhiteSpace($vaConfig.LlmApiKey)) { Write-Error "LlmApiKey empty"; exit 1 }
    $env:LLM_API_KEY = $vaConfig.LlmApiKey
    $env:LLM_MODEL = $vaConfig.LlmModel
    $env:LLM_BASE_URL = $vaConfig.LlmBaseUrl
    # llm-provider.ts switch is case-sensitive: "MiniMax" would fall to the
    # default branch (api.openai.com, blocked in CN -> every LLM call fails).
    # Normalize to the lowercase enum the switch expects.
    $env:LLM_PROVIDER = if (-not [string]::IsNullOrWhiteSpace($vaConfig.LlmProvider)) { $vaConfig.LlmProvider.ToLowerInvariant() } else { "minimax" }

    # ── 3. Clean old logs + watchdog captures ──
    foreach ($k in $logs.Keys) { Remove-Item $logs[$k] -ErrorAction SilentlyContinue }
    if (Test-Path $hostWatchdogDir) {
        Get-ChildItem $hostWatchdogDir -Filter "freeze-*" -ErrorAction SilentlyContinue |
            Remove-Item -Force -ErrorAction SilentlyContinue
    }

    # ── 4. Start stack ──
    Write-Host "[up] Starting containers (ts + host + 3 farmhands)..."
    & docker compose -f $composeE2e -f $composeSoak up -d
    if ($LASTEXITCODE -ne 0) { Write-Error "docker compose up failed"; exit 1 }

    # ── 5. Host: save load -> server -> alloc ──
    Write-Host "[host] Waiting for save load..."
    if (-not (Wait-LogLine -Path $logs["host"] -Pattern "ValleyAgent TestMod\] Save loaded" -TimeoutSeconds 300 -Label "host-save")) { exit 1 }
    Start-Sleep -Seconds 5
    Send-Command -Path $cmds["host"] -Command "va_mp_host 3"
    if (-not (Wait-LogLine -Path $logs["host"] -Pattern "MP\] Hosted multiplayer server" -TimeoutSeconds 120 -Label "host-mp")) { exit 1 }
    Start-Sleep -Seconds 5
    if (-not $NoAgents) {
        Send-Command -Path $cmds["host"] -Command "va_test_alloc $Npc"
    if (-not (Wait-LogLine -Path $logs["host"] -Pattern "\[Alloc\] $Npc allocated" -TimeoutSeconds 90 -Label "alloc")) {
        Write-Host "  WARN: allocation may have failed; continuing" -ForegroundColor Yellow
    }
    if ($Npc2) {
        Send-Command -Path $cmds["host"] -Command "va_test_alloc $Npc2"
        $null = Wait-LogLine -Path $logs["host"] -Pattern "\[Alloc\] $Npc2 allocated" -TimeoutSeconds 90 -Label "alloc2"
    }
    } # end alloc guard
    Start-Sleep -Seconds 3

    # ── 6. Farmhands join staggered (probes the join-time crash path) ──
    foreach ($fh in @("farmhand1", "farmhand2", "farmhand3")) {
        Write-Host "[$fh] Waiting for save load..."
        if (-not (Wait-LogLine -Path $logs[$fh] -Pattern "ValleyAgent TestMod\] (Save loaded|ValleyAgent TestMod loaded)" -TimeoutSeconds 300 -Label "$fh-save")) { exit 1 }
        Start-Sleep -Seconds 5
        Send-Command -Path $cmds[$fh] -Command "va_mp_join valley-host:24642"
        if (-not (Wait-LogLine -Path $logs[$fh] -Pattern "MP\] Joined game and activated farmhand" -TimeoutSeconds 180 -Label "$fh-join")) {
            Write-Host "  $fh failed to join -- capturing logs and aborting" -ForegroundColor Red
            Copy-Item $logs[$fh] (Join-Path $artifacts "$fh-join-fail-SMAPI.log") -ErrorAction SilentlyContinue
            Copy-Item $logs["host"] (Join-Path $artifacts "host-at-join-fail-SMAPI.log") -ErrorAction SilentlyContinue
            if (Test-Path $hostWatchdogDir) {
                Get-ChildItem $hostWatchdogDir -Filter "freeze-*" -ErrorAction SilentlyContinue |
                    Copy-Item -Destination $artifacts -ErrorAction SilentlyContinue
            }
            exit 1
        }
        Start-Sleep -Seconds 8
    }

    # ── 7. Diag baseline ──
    Send-Command -Path $cmds["host"] -Command "va_soak_diag"
    Start-Sleep -Seconds 3

    if (-not $NoAgents) {
    # ── 8. One real LLM dialogue from farmhand1 (sets LastDialoguePlayerId) ──
    # MiniMax-M3 latency varies; a turn over 60s times out host-side and the
    # late dialogue_response is dropped as unsolicited. Probe-gate + retry
    # until one lands (needed so FOLLOW targets the farmhand, not the host).
    $c3ok = Send-C3WithProbe -Farmhand "farmhand1" -NpcName $Npc
    if (-not $c3ok) {
        Write-Host "  WARN: C3 never landed -- LastDialoguePlayerId stays null, FOLLOW falls back to host target" -ForegroundColor Yellow
    }
    if ($Npc2) {
        $c3ok2 = Send-C3WithProbe -Farmhand "farmhand2" -NpcName $Npc2
        if (-not $c3ok2) {
            Write-Host "  WARN: C3 for $Npc2 never landed -- falls back to host target" -ForegroundColor Yellow
        }
    }

    # ── 9. Stage FOLLOW on host ──
    Send-Command -Path $cmds["host"] -Command "va_soak_diag"
    Start-Sleep -Seconds 2
    Send-Command -Path $cmds["host"] -Command "va_soak_follow $Npc"
    $null = Wait-LogLine -Path $logs["host"] -Pattern "\[Soak\] Follow PASS" -TimeoutSeconds 60 -Label "follow"
    if ($Npc2) {
        Start-Sleep -Seconds 2
        Send-Command -Path $cmds["host"] -Command "va_soak_follow $Npc2"
        $null = Wait-LogLine -Path $logs["host"] -Pattern "\[Soak\] Follow PASS.*$Npc2" -TimeoutSeconds 60 -Label "follow2"
    }
    Start-Sleep -Seconds 3
    } # end if (-not $NoAgents)

    # ── 10. Spread farmhands across maps ──
    foreach ($fh in @("farmhand1", "farmhand2", "farmhand3")) {
        Send-Command -Path $cmds[$fh] -Command "va_mp_goto $($gotoMap[$fh]) 20 20"
        $null = Wait-LogLine -Path $logs[$fh] -Pattern "\[Soak\] Goto PASS" -TimeoutSeconds 45 -Label "$fh-goto"
        Start-Sleep -Seconds 3
    }
    Send-Command -Path $cmds["host"] -Command "va_soak_diag"
    Start-Sleep -Seconds 3

    # ── 10.5 Verify cross-map follow guard (2026-09-10 fix) ──
    # Before fix: cross-map follow target -> per-tick NoPathFound storm (1/sec).
    # After fix: guard logs "standing by" and the storm is gone.
    if (-not $NoAgents) {
        Write-Host "[verify] cross-map follow guard (35s window)..."
        $noPathBefore = ([regex]::Matches((Get-LogText $logs["host"]), "NoPathFound")).Count
        Start-Sleep -Seconds 35
        $hostText = Get-LogText $logs["host"]
        $noPathAfter = ([regex]::Matches($hostText, "NoPathFound")).Count
        $guardHit = $hostText -match "standing by"
        Write-Host "  NoPathFound: $noPathBefore -> $noPathAfter ; guard log hit: $guardHit"
        if ($noPathAfter -gt $noPathBefore) {
            Write-Host "  VERIFY FAIL: NoPathFound storm still present" -ForegroundColor Red
        } elseif (-not $guardHit) {
            Write-Host "  VERIFY SOFT-FAIL: no storm, but guard log not seen (may still be travelling)" -ForegroundColor Yellow
        } else {
            Write-Host "  VERIFY PASS: guard active, no cross-map pathfind storm" -ForegroundColor Green
        }
    }
}

# ── 11. Monitor loop ──
Write-Host ""
Write-Host "[watch] Monitoring (duration $DurationMinutes min, end at game time 2000+)..."
$deadline = (Get-Date).AddMinutes($DurationMinutes)
$monitorStart = Get-Date
$iter = 0
$freeze = $false
$exited = $false
$endTimeReached = $false

function Capture-Freeze {
    param([string]$Artifacts, [string]$SnapPath)
    Write-Host "  !!! FREEZE CONFIRMED -- capturing evidence ..." -ForegroundColor Red
    $watchdogLogs = Get-ChildItem $hostWatchdogDir -Filter "freeze-*.log" -ErrorAction SilentlyContinue
    foreach ($w in $watchdogLogs) {
        Copy-Item $w.FullName (Join-Path $Artifacts $w.Name) -ErrorAction SilentlyContinue
        $content = Get-Content $w.FullName -Raw -ErrorAction SilentlyContinue
        Write-Host "  companion log: $($w.Name)"
        Write-Host ($content -split "`n" | ForEach-Object { "    " + $_ })
        if ($content -match "pid=(\d+)") {
            $pid_ = $Matches[1]
            Write-Host "  dumping threads via createdump (pid=$pid_)..."
            & docker exec valley-host sh -c "/usr/share/dotnet/shared/Microsoft.NETCore.App/*/createdump -f /data/hang.dmp $pid_"
            & docker cp "valley-host:/data/hang.dmp" (Join-Path $Artifacts "hang.dmp")
        }
    }
    & docker exec valley-host sh -c "top -b -n1 -H 2>/dev/null | head -40" | Out-File (Join-Path $Artifacts "freeze-thread-cpu.txt") -Encoding utf8
    & docker logs valley-host --tail 300 2>&1 | Out-File (Join-Path $Artifacts "freeze-docker-log.txt") -Encoding utf8
    Read-LogTail -Path $logs["host"] -Lines 120 | Out-File (Join-Path $Artifacts "freeze-host-smapi-tail.txt") -Encoding utf8
}

while ((Get-Date) -lt $deadline) {
    $iter++
    Start-Sleep -Seconds 10

    # freeze detection 1: watchdog companion log written AFTER the monitor loop
    # started (load-phase slow-tick blips pre-date it and are ignored)
    $freezeFiles = @(
        Get-ChildItem $hostWatchdogDir -Filter "freeze-*.log" -ErrorAction SilentlyContinue |
            Where-Object { $_.LastWriteTime -ge $monitorStart.AddSeconds(-5) }
    )
    if ($freezeFiles.Count -gt 0) { $freeze = $true; Capture-Freeze -Artifacts $artifacts -SnapPath $threadSnap; break }

    # freeze detection 2: watchdog error line in SMAPI log
    $hostTail = Read-LogTail -Path $logs["host"] -Lines 60
    if ($hostTail -match "\[Watchdog\] Main thread stalled") { $freeze = $true; Capture-Freeze -Artifacts $artifacts -SnapPath $threadSnap; break }

    # host container state
    $state = & docker inspect --format "{{.State.Status}} {{.State.ExitCode}}" valley-host 2>$null
    if ($lastState -ne $state -and $state) { Write-Host "  [state] valley-host: $state" }
    $lastState = $state
    if ($state -match "^exited") { $exited = $true; break }

    # periodic: thread CPU snapshot
    if ($iter % 3 -eq 0) {
        $stamp = Get-Date -Format "HH:mm:ss"
        ("=== $stamp ===") | Out-File $threadSnap -Append -Encoding utf8
        & docker exec valley-host sh -c "top -b -n1 -H 2>/dev/null | head -22" | Out-File $threadSnap -Append -Encoding utf8
    }

    # periodic: re-assert FOLLOW + diag (also records game time)
    if ($iter % 6 -eq 0) {
        if (-not $NoAgents) {
            Send-Command -Path $cmds["host"] -Command "va_soak_follow $Npc"
            if ($Npc2) {
                Start-Sleep -Seconds 1
                Send-Command -Path $cmds["host"] -Command "va_soak_follow $Npc2"
            }
        }
        Start-Sleep -Seconds 2
        Send-Command -Path $cmds["host"] -Command "va_soak_diag"
        Start-Sleep -Seconds 3
        $tail = Read-LogTail -Path $logs["host"] -Lines 30
        $timeLine = $tail | Where-Object { $_ -match "\[Soak\] Diag: time=" } | Select-Object -Last 1
        if ($timeLine) {
            Write-Host "  $timeLine"
            if ($timeLine -match "time=(\d{3,4})") {
                if ([int]$Matches[1] -ge $EndAtGameTime) { $endTimeReached = $true; break }
            }
        } else {
            Write-Host "  [warn] no fresh Diag line -- log stalled?" -ForegroundColor Yellow
            $tail | Select-Object -Last 5 | ForEach-Object { Write-Host "    $_" }
        }
    }
}

# ── 12. Collect artifacts + verdict ──
Write-Host ""
foreach ($k in $logs.Keys) {
    Copy-Item $logs[$k] (Join-Path $artifacts "$k-SMAPI.log") -ErrorAction SilentlyContinue
}
if (Test-Path $hostWatchdogDir) {
    $caps = @(Get-ChildItem $hostWatchdogDir -Filter "freeze-*" -ErrorAction SilentlyContinue)
    if ($caps.Count -gt 0) { Copy-Item $caps.FullName $artifacts -ErrorAction SilentlyContinue }
}

if ($freeze) {
    Write-Host "RESULT: FROZEN -- evidence in $artifacts (containers left running for inspection)" -ForegroundColor Red
    exit 2
}

if ($exited) {
    Write-Host "RESULT: HOST EXITED -- state=$state" -ForegroundColor Red
    & docker logs valley-host --tail 200 2>&1 | Out-File (Join-Path $artifacts "exit-docker-log.txt") -Encoding utf8
    Write-Host "RESULT: containers left running for inspection" -ForegroundColor Red
    exit 3
}

Write-Host "RESULT: NO FREEZE within window" -ForegroundColor Green
if ($endTimeReached) { Write-Host "  reached game time $EndAtGameTime+ as planned" }
Write-Host "  Stopping containers..."
$ErrorActionPreference = "Continue"
& docker compose -f $composeE2e -f $composeSoak down --timeout 10 2>$null | Out-Null
exit 0
