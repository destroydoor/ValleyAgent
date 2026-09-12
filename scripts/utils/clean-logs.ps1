# Archive old SMAPI log files
param(
    [int]$KeepCount = 5,
    [string]$GamePath
)

. "$PSScriptRoot\..\lib\paths.ps1"
$GamePath = Get-GamePath -Hint $GamePath

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$timestamp = Get-Date -Format "yyyy-MM-dd_HHmmss"
$logFile = Join-Path $RepoRoot "scripts\results\clean-logs-$timestamp.txt"

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  SMAPI Log Cleanup" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

$logsPath = Join-Path $GamePath "ErrorLogs"
if (-not (Test-Path $logsPath)) {
    Write-Host "ErrorLogs directory not found at: $logsPath" -ForegroundColor Red
    exit 1
}

$logFiles = Get-ChildItem -Path $logsPath -Filter "SMAPI-*.txt" -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending

$totalCount = $logFiles.Count
Write-Host "Found $totalCount SMAPI log files" -ForegroundColor White
Write-Host "Keeping most recent $KeepCount files" -ForegroundColor White
Write-Host ""

$removed = 0
$logFiles | Select-Object -Skip $KeepCount | ForEach-Object {
    $archivedName = "archived_" + $_.Name
    try {
        Rename-Item -Path $_.FullName -NewName $archivedName -ErrorAction SilentlyContinue
        Write-Host "[ARCHIVED] $($_.Name)" -ForegroundColor Gray
        "[ARCHIVED] $($_.Name)" | Out-File -FilePath $logFile -Append -Encoding UTF8
        $removed++
    } catch {
        Write-Host "[SKIP] Could not archive: $($_.Name)" -ForegroundColor Yellow
    }
}

Write-Host ""
Write-Host "Archived $removed files" -ForegroundColor Green
Write-Host "Log saved to: $logFile" -ForegroundColor Gray