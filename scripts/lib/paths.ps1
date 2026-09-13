# Shared path resolution for ValleyAgent scripts.
#
# 不要在脚本里硬编码本机绝对路径（会泄露开发者环境，且他人无法运行）。
# 统一通过环境变量 + 仓库相对路径 + 常见安装位置探测来解析。
#
# 用法（任意 scripts/<子目录>/xxx.ps1）：
#   . "$PSScriptRoot\..\lib\paths.ps1"
#   $RepoRoot  = Get-RepoRoot
#   $GamePath  = Get-GamePath          # 可传 -Hint $SomeParam 优先使用
#   $ValleyAI  = Get-ValleyAIRoot
#
# 可用环境变量：
#   STARDW_PATH    Stardew Valley 安装目录
#   VALLEYAI_ROOT  TS Agent Server (ValleyAI) 仓库根目录

function Get-RepoRoot {
    # scripts/lib/paths.ps1 -> scripts/lib -> scripts -> <repo root>
    return (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
}

function Get-GamePath {
    param([string]$Hint)

    $candidates = @()
    if ($Hint)              { $candidates += $Hint }
    if ($env:STARDW_PATH)   { $candidates += $env:STARDW_PATH }

    $repoRoot = Get-RepoRoot
    $candidates += (Join-Path $repoRoot "Stardew Valley")

    $candidates += @(
        "$env:ProgramFiles (x86)\Steam\steamapps\common\Stardew Valley",
        "$env:ProgramFiles\Steam\steamapps\common\Stardew Valley",
        "$env:ProgramFiles\GOG Games\Stardew Valley"
    )
    # 所有盘符下的常见 Steam/GOG 库位置
    foreach ($drive in (Get-PSDrive -PSProvider FileSystem -ErrorAction SilentlyContinue)) {
        $root = $drive.Root
        $candidates += @(
            (Join-Path $root "SteamLibrary\steamapps\common\Stardew Valley"),
            (Join-Path $root "Steam\steamapps\common\Stardew Valley"),
            (Join-Path $root "Games\Stardew Valley"),
            (Join-Path $root "GOG Games\Stardew Valley")
        )
    }

    foreach ($c in $candidates) {
        if ($c -and (Test-Path (Join-Path $c "Stardew Valley.dll"))) { return $c }
    }
    foreach ($c in $candidates) {
        if ($c -and (Test-Path $c)) { return $c }
    }

    throw "找不到 Stardew Valley 安装目录。请设置环境变量 STARDW_PATH，例如：`$env:STARDW_PATH = 'X:\SteamLibrary\steamapps\common\Stardew Valley'"
}

function Get-ValleyAIRoot {
    param([string]$Hint)

    if ($Hint -and (Test-Path $Hint)) { return $Hint }
    if ($env:VALLEYAI_ROOT)           { return $env:VALLEYAI_ROOT }

    $repoRoot = Get-RepoRoot
    # 优先：本仓库内置的 server/ 工作区；其次：与本仓库同级的 ValleyAI 检出
    foreach ($c in @((Join-Path $repoRoot "server"), (Join-Path (Split-Path -Parent $repoRoot) "ValleyAI"))) {
        if (Test-Path $c) { return $c }
    }
    return (Join-Path $repoRoot "server")
}
