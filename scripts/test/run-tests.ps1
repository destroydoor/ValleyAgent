#requires -Version 5.1
<#
.SYNOPSIS
    ValleyTalk 统一测试运行器 — 支持分组选择、全量测试、结果聚合。

.DESCRIPTION
    构建部署后启动 SMAPI，通过标记文件驱动 TestOrchestrator 自动运行指定测试。
    仅游戏测试一种入口（Python 服务器已废弃，TS Agent Server 由 C# Mod 自动拉起）。

.PARAMETER Group
    测试分组: Fuzzy, Edge, Functional, Real, All (默认 All)

.PARAMETER Tags
    按标签筛选（逗号分隔）

.PARAMETER Names
    按名称筛选（逗号分隔）

.PARAMETER NoGame
    跳过游戏测试

.PARAMETER Configuration
    构建配置: Debug 或 Release (默认 Debug)

.PARAMETER TimeoutSeconds
    游戏测试超时秒数 (默认 600)

.EXAMPLE
    .\run-tests.ps1 -Group Edge
    .\run-tests.ps1 -Group All
    .\run-tests.ps1 -Tags "combat,pathfinding"
    .\run-tests.ps1 -Names "F1_RandomWalk,E1_LongPathfind"
#>
[CmdletBinding()]
param(
    [ValidateSet("Fuzzy", "Edge", "Functional", "Real", "Narrative", "Visual", "All", "")]
    [string]$Group = "All",
    [string]$Tags = "",
    [string]$Names = "",
    [switch]$NoGame,
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",
    [int]$TimeoutSeconds = 600
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$timestamp = Get-Date -Format "yyyy-MM-dd_HHmmss"
$resultDir = Join-Path $RepoRoot "scripts\results"
if (-not (Test-Path $resultDir)) { New-Item -ItemType Directory -Path $resultDir -Force | Out-Null }

$gameExit = 0

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  ValleyTalk 统一测试运行器" -ForegroundColor Cyan
Write-Host "  时间: $timestamp" -ForegroundColor Gray
Write-Host "  分组: $Group" -ForegroundColor Gray
Write-Host "  智能层: TS Agent Server (valley-ai-server.exe，由 Mod 自动拉起)" -ForegroundColor Gray
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# ── 游戏集成测试（含 TS Agent Server 生命周期） ──
if (-not $NoGame) {
    Write-Host "[1/1] 运行游戏集成测试..." -ForegroundColor Cyan

    # 创建编排器标记文件
    . "$PSScriptRoot\..\lib\paths.ps1"
    $GamePath = Get-GamePath
    $markerDir = Join-Path $GamePath "Mods\ValleyAgent.TestMod"
    if (-not (Test-Path $markerDir)) { New-Item -ItemType Directory -Path $markerDir -Force | Out-Null }

    # 写入编排器标记文件（指定分组）
    $orchestratorMarker = Join-Path $markerDir "auto_orchestrator_run.flag"
    Set-Content -Path $orchestratorMarker -Value $Group -Force
    Write-Host "[MARKER] 编排器标记已创建: $orchestratorMarker (分组: $Group)" -ForegroundColor Cyan

    # 同时创建旧版标记文件（兼容）
    $autoTestMarker = Join-Path $markerDir "auto_test_run.flag"
    Set-Content -Path $autoTestMarker -Value "armed at $timestamp" -Force

    # 调用游戏测试脚本
    $gameScript = Join-Path $PSScriptRoot "test-game.ps1"
    if (Test-Path $gameScript) {
        & $gameScript -Configuration $Configuration -TimeoutSeconds $TimeoutSeconds
        $gameExit = $LASTEXITCODE
    } else {
        Write-Host "[1/1] 游戏测试脚本未找到，跳过。" -ForegroundColor Yellow
    }

    Write-Host ""
    Write-Host "[1/1] 游戏测试: $(if ($gameExit -eq 0) { '全部通过' } else { '有失败' })" -ForegroundColor $(if ($gameExit -eq 0) { 'Green' } else { 'Red' })
} else {
    Write-Host "[1/1] 游戏测试已跳过 (-NoGame)" -ForegroundColor Yellow
}

# ── 合并报告 ──
$resultFile = Join-Path $resultDir "unified-test-$timestamp.txt"

"" | Out-File -FilePath $resultFile -Encoding UTF8
"========================================" | Out-File -FilePath $resultFile -Append -Encoding UTF8
"  ValleyTalk 统一测试报告" | Out-File -FilePath $resultFile -Append -Encoding UTF8
"  时间: $timestamp" | Out-File -FilePath $resultFile -Append -Encoding UTF8
"  分组: $Group" | Out-File -FilePath $resultFile -Append -Encoding UTF8
"  智能层: TS Agent Server (valley-ai-server.exe)" | Out-File -FilePath $resultFile -Append -Encoding UTF8
"========================================" | Out-File -FilePath $resultFile -Append -Encoding UTF8
"" | Out-File -FilePath $resultFile -Append -Encoding UTF8

"# 游戏测试" | Out-File -FilePath $resultFile -Append -Encoding UTF8
"结果: $(if ($gameExit -eq 0) { '全部通过' } else { '失败' })" | Out-File -FilePath $resultFile -Append -Encoding UTF8
"" | Out-File -FilePath $resultFile -Append -Encoding UTF8

# 附加游戏测试结果
$gameResultFile = Get-ChildItem -Path $resultDir -Filter "game-test-*.txt" -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($gameResultFile) {
    "========================================" | Out-File -FilePath $resultFile -Append -Encoding UTF8
    "# 游戏测试详细输出" | Out-File -FilePath $resultFile -Append -Encoding UTF8
    "========================================" | Out-File -FilePath $resultFile -Append -Encoding UTF8
    Get-Content $gameResultFile.FullName | Out-File -FilePath $resultFile -Append -Encoding UTF8
}

# 附加编排器 JSON 摘要
$summaryFile = Get-ChildItem -Path "$RepoRoot\logs\test_results" -Filter "_summary.json" -Recurse -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($summaryFile) {
    "========================================" | Out-File -FilePath $resultFile -Append -Encoding UTF8
    "# 编排器 JSON 摘要" | Out-File -FilePath $resultFile -Append -Encoding UTF8
    "========================================" | Out-File -FilePath $resultFile -Append -Encoding UTF8
    Get-Content $summaryFile.FullName | Out-File -FilePath $resultFile -Append -Encoding UTF8
}

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  统一报告: $timestamp" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "游戏: $(if ($gameExit -eq 0) { '通过' } else { '失败' })" -ForegroundColor $(if ($gameExit -eq 0) { 'Green' } else { 'Red' })
Write-Host "报告保存到: $resultFile" -ForegroundColor Gray

exit $gameExit
