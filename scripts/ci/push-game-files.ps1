# 一键准备 CI 的私有游戏镜像仓（ci.yml 头部注释的配套工具）。
#
# 做的事：
#   1. 检查/创建私有 GitHub 仓库（默认 destroydoor/stardew-game-files）
#   2. 把 docker/linux-game（Linux 版游戏文件，约 686MB）以**单提交**推上去
#      （每次运行都重建历史再 force push，仓库体积不随重传膨胀）
#   3. 打印 PAT 与 secrets 配置指引
#
# CI 侧（ci.yml 的 csharp job）用只读 PAT 拉取该仓并以只读卷挂进单测容器。
# 游戏版本更新后重跑本脚本即可换新（本地游戏目录先更新到新版本）。
#
# 用法：
#   powershell -ExecutionPolicy Bypass -File scripts\ci\push-game-files.ps1
#   可选：-RepoName 你的用户名/别的仓库名  -GameDir 其他游戏目录
[CmdletBinding()]
param(
    # 游戏目录（默认仓库内 docker\linux-game，即容器单测已验证过的那份）
    [string]$GameDir = "",
    # 私有镜像仓全名（须与 CI secret STARDW_GAME_REPO 一致）
    [string]$RepoName = "destroydoor/stardew-game-files"
)

$ErrorActionPreference = "Stop"

# ---- 0. 解析路径与 gh ----
$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
if (-not $GameDir) { $GameDir = Join-Path $RepoRoot "docker\linux-game" }

if (-not (Test-Path (Join-Path $GameDir "Stardew Valley.dll"))) {
    Write-Host "FAIL: $GameDir 里没有 Stardew Valley.dll，不是有效的游戏目录" -ForegroundColor Red
    exit 1
}

$gh = (Get-Command gh -ErrorAction SilentlyContinue).Source
if (-not $gh) { $gh = "C:\Program Files\GitHub CLI\gh.exe" }
if (-not (Test-Path $gh)) {
    Write-Host "FAIL: 找不到 gh CLI，请先安装 GitHub CLI 并 gh auth login" -ForegroundColor Red
    exit 1
}
& $gh auth status *> $null
if ($LASTEXITCODE -ne 0) {
    Write-Host "FAIL: gh 未登录，请先运行 gh auth login" -ForegroundColor Red
    exit 1
}

# ---- 1. 确保私有仓存在 ----
& $gh repo view $RepoName --json name *> $null
if ($LASTEXITCODE -ne 0) {
    Write-Host "==> 私有仓 $RepoName 不存在，正在创建" -ForegroundColor Cyan
    & $gh repo create $RepoName --private
    if ($LASTEXITCODE -ne 0) { Write-Host "FAIL: 创建仓库失败" -ForegroundColor Red; exit 1 }
}
else {
    $vis = & $gh repo view $RepoName --json visibility --jq '.visibility'
    if ($vis -ne "PRIVATE") {
        Write-Host "FAIL: $RepoName 已存在但可见性是 $vis——游戏文件必须放私有仓" -ForegroundColor Red
        exit 1
    }
}

# git 推送走 gh 的凭据助手（幂等）
& $gh auth setup-git

# ---- 2. 单提交历史重建 + 推送 ----
$gitDir = Join-Path $GameDir ".git"
if (Test-Path $gitDir) {
    $oldOrigin = git -C $GameDir remote get-url origin 2>$null
    if ($oldOrigin -notlike "*$RepoName*") {
        Write-Host "FAIL: $GameDir 下已有指向 $oldOrigin 的 .git（不是本脚本管理的镜像仓），请人工确认后删除或改用 -GameDir" -ForegroundColor Red
        exit 1
    }
    Remove-Item -Recurse -Force $gitDir
}

git -C $GameDir init -b main | Out-Null
git -C $GameDir config core.autocrlf false   # 游戏文件全二进制，禁止换行改写
git -C $GameDir config core.longpaths true   # Content 目录路径较深，防 Windows 260 限制
if (-not (git -C $GameDir config user.email)) {
    git -C $GameDir config user.name "Developer"
    git -C $GameDir config user.email "developer@example.com"
}
git -C $GameDir remote add origin "https://github.com/$RepoName.git"

Write-Host "==> 提交游戏文件（686MB，提交需一两分钟）..." -ForegroundColor Cyan
git -C $GameDir add -A
$stamp = Get-Date -Format "yyyy-MM-dd"
git -C $GameDir commit -m "Stardew Valley Linux game files (CI mirror) - $stamp" | Out-Null

Write-Host "==> 推送到 $RepoName（可能较久，中断后重跑本脚本即可）..." -ForegroundColor Cyan
git -C $GameDir push -f origin main
if ($LASTEXITCODE -ne 0) {
    Write-Host "FAIL: 推送失败（网络原因可重跑本脚本重试）" -ForegroundColor Red
    exit 1
}

# ---- 3. 指引 ----
Write-Host ""
Write-Host "PASS: 游戏文件已推送到私有仓 $RepoName" -ForegroundColor Green
Write-Host ""
Write-Host "接下来配两个 secret（Settings → Secrets and variables → Actions，或用 gh secret set）：" -ForegroundColor Green
Write-Host "  STARDW_GAME_REPO  = $RepoName" -ForegroundColor Cyan
Write-Host "  STARDW_GAME_PAT   = 细粒度 PAT：" -ForegroundColor Cyan
Write-Host "      GitHub → Settings → Developer settings → Fine-grained tokens → Generate new token"
Write-Host "      Repository access: Only select repositories → $RepoName"
Write-Host "      Permissions: Contents = Read-only（其余默认不给）"
Write-Host ""
Write-Host "gh 命令行方式："
Write-Host ('  gh secret set STARDW_GAME_REPO --body "{0}" -R destroydoor/ValleyAgent' -f $RepoName) -ForegroundColor Yellow
Write-Host "  gh secret set STARDW_GAME_PAT --body `<PAT值`> -R destroydoor/ValleyAgent" -ForegroundColor Yellow
Write-Host ""
Write-Host "两个 secret 配齐后，CI 的 csharp job 自动启用；游戏更新后重跑本脚本换新。" -ForegroundColor Green
