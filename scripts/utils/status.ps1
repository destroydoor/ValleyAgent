# Show ValleyTalk project status
param(
    [string]$GamePath = "F:\SteamLibrary\steamapps\common\Stardew Valley"
)

$ErrorActionPreference = "Continue"
$RepoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$resultsDir = Join-Path $RepoRoot "scripts\results"

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  ValleyTalk Project Status" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# Build status
Write-Host "[BUILD]" -ForegroundColor Yellow
$agentDll = Join-Path $RepoRoot "src\ValleyAgent\bin\Debug\net6.0\ValleyAgent.dll"
$testmodDll = Join-Path $RepoRoot "src\ValleyAgent.TestMod\bin\Debug\net6.0\ValleyAgent.TestMod.dll"
if (Test-Path $agentDll) {
    $ts = (Get-Item $agentDll).LastWriteTime
    Write-Host "  ValleyAgent.dll    : OK ($ts)" -ForegroundColor Green
} else {
    Write-Host "  ValleyAgent.dll    : NOT BUILT" -ForegroundColor Red
}
if (Test-Path $testmodDll) {
    $ts = (Get-Item $testmodDll).LastWriteTime
    Write-Host "  ValleyAgent.TestMod.dll: OK ($ts)" -ForegroundColor Green
} else {
    Write-Host "  ValleyAgent.TestMod.dll: NOT BUILT" -ForegroundColor Red
}

# Game status
Write-Host ""
Write-Host "[GAME]" -ForegroundColor Yellow
$gameProcs = Get-Process -Name "StardewModdingAPI", "Stardew Valley" -ErrorAction SilentlyContinue
if ($gameProcs) {
    $pids = ($gameProcs | Select-Object -ExpandProperty Id) -join ", "
    Write-Host "  Game running: YES (PIDs: $pids)" -ForegroundColor Red
} else {
    Write-Host "  Game running: NO" -ForegroundColor Green
}

# Server status (TS Agent Server — valley-ai-server.exe)
Write-Host ""
Write-Host "[SERVER]" -ForegroundColor Yellow
$serverPort = Get-NetTCPConnection -LocalPort 8765 -ErrorAction SilentlyContinue |
    Select-Object -ExpandProperty OwningProcess -Unique
if ($serverPort) {
    Write-Host "  TS Agent Server on port 8765: YES (PIDs: $serverPort)" -ForegroundColor Green
} else {
    Write-Host "  TS Agent Server on port 8765: NO" -ForegroundColor Gray
}

# Latest test results
Write-Host ""
Write-Host "[LATEST RESULTS]" -ForegroundColor Yellow

$gameResults = Get-ChildItem -Path $resultsDir -Filter "game-test-*.txt" -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
$allResults = Get-ChildItem -Path $resultsDir -Filter "all-tests-*.txt" -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1

if ($gameResults) {
    $ts = $gameResults.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss")
    Write-Host "  Game tests:   $($gameResults.Name) ($ts)" -ForegroundColor Green
} else {
    Write-Host "  Game tests:   No results found" -ForegroundColor Gray
}

if ($allResults) {
    $ts = $allResults.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss")
    Write-Host "  Combined:     $($allResults.Name) ($ts)" -ForegroundColor Green
}

Write-Host ""
Write-Host "Results directory: $resultsDir" -ForegroundColor Gray