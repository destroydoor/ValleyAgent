# Soak-monitor companion: watch SMAPI log for save load + 24h clock +
# watchdog captures. Background loop runs until cancelled or max-minutes
# hit. Forwards all the lines a human would scan for freezes.
[CmdletBinding()]
param(
    [int]$MaxMinutes = 240,
    [int]$GameTimeTarget = 2600,   # 2600 = end of day; 2700 = unreachable -> run full duration
    [string]$WatchdogDir = "Stardew Valley\Mods\ValleyAgent.TestMod\watchdog"
)
$ErrorActionPreference = "Stop"

$logPath = "Stardew Valley\ErrorLogs\SMAPI-latest.txt"
$freezeLogPath = Join-Path $WatchdogDir 'freeze-*.log'
$stdoutFile = "scripts/test/results/soak-monitor-stdout.txt"

New-Item -ItemType Directory -Path (Split-Path $stdoutFile) -Force | Out-Null

function Log-Line($line) {
    Write-Host $line
    $line | Out-File -FilePath $stdoutFile -Append -Encoding UTF8
}

function Get-LogText {
    param([string]$Path)
    if (-not (Test-Path $Path)) { return "" }
    $fs = [System.IO.FileStream]::new($Path, [System.IO.FileMode]::Open,
        [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
    $sr = [System.IO.StreamReader]::new($fs, [System.Text.Encoding]::UTF8)
    $text = $sr.ReadToEnd()
    $sr.Close(); $fs.Close()
    return $text
}

Log-Line "=== soak-monitor start $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') ==="
Log-Line "log: $logPath"
Log-Line "watchdog dir: $WatchdogDir"
Log-Line "max minutes: $MaxMinutes  target game time: $GameTimeTarget"

$deadline = (Get-Date).AddMinutes($MaxMinutes)
$iter = 0
$saveLoadedSeen = $false
$freezeSeen = $false
$lastWatchdogFiles = @()

while ((Get-Date) -lt $deadline) {
    $iter++
    Start-Sleep -Seconds 10

    $text = Get-LogText $logPath
    if (-not $text) {
        if ($iter % 6 -eq 0) { Log-Line "[$(Get-Date -Format 'HH:mm:ss')] iter=$iter no-log-yet" }
        continue
    }

    if (-not $saveLoadedSeen -and $text -match "Save loaded|SMAPI loaded") {
        $saveLoadedSeen = $true
        Log-Line "[$(Get-Date -Format 'HH:mm:ss')] SAVE LOADED detected"
    }

    $watchdogFiles = Get-ChildItem $WatchdogDir -Filter "freeze-*.log" -ErrorAction SilentlyContinue
    $newFiles = @($watchdogFiles | Where-Object { $_.Name -notin $lastWatchdogFiles.Name })
    foreach ($f in $newFiles) {
        $lastWatchdogFiles += $f
        $content = Get-Content $f.FullName -Raw -ErrorAction SilentlyContinue
        Log-Line "[$(Get-Date -Format 'HH:mm:ss')] WATCHDOG CAPTURE $($f.Name)"
        Log-Line "----"
        Log-Line $content
        Log-Line "----"
        $freezeSeen = $true
    }
    if ($freezeSeen) {
        Log-Line "FROZEN - exiting"
        exit 2
    }

    # game-time / status heartbeat every 30s
    if ($iter % 3 -eq 0) {
        $lines = $text -split "`n" | Select-Object -Last 60
        $timeLine = $lines | Where-Object { $_ -match "\[SMAPI\] context|\[game\] time|gameContextSync|State: " } | Select-Object -Last 1
        $stamp = Get-Date -Format 'HH:mm:ss'
        $cpu = $null
        try {
            $p = Get-Process -Name StardewModdingAPI -ErrorAction SilentlyContinue | Select-Object -First 1
            if ($p) {
                $cpu = [math]::Round($p.CPU, 2)
            }
        } catch { }
        $cpuInfo = if ($null -ne $cpu) { " cpu=$cpu" } else { "" }
        Log-Line "[$stamp] iter=$iter save=$saveLoadedSeen logsize=$((Get-Item $logPath).Length)$cpuInfo"
        if ($timeLine) { Log-Line "  $timeLine" }
    }
}

Log-Line "WINDOW EXPIRED - no freeze"
exit 0