# 一键生成 CI 所需 secret：STEAM_CONFIG_VDF_BASE64（ci.yml 头部注释的配套工具）。
#
# 原理：下载官方 steamcmd（Windows 版）到临时目录，交互式登录一次 Steam 账号
# （按提示输密码 + Steam Guard 验证码，只在本地发生，密码不进命令行参数），
# steamcmd 会把 ssfn 机器授权写进自己的 config/config.vdf；本脚本把它 base64
# 编码后复制到剪贴板并打印，粘贴到 GitHub 仓库
#   Settings → Secrets and variables → Actions → New repository secret
# 名字填 STEAM_CONFIG_VDF_BASE64。CI 用它免 Steam Guard 验证码登录下载游戏。
#
# 配套 secrets 共三个：STEAM_USERNAME / STEAM_PASSWORD / STEAM_CONFIG_VDF_BASE64。
# 若日后改密或 Steam 侧登出导致 ssfn 失效，重跑本脚本重新导出即可。
#
# 用法：
#   powershell -ExecutionPolicy Bypass -File scripts\ci\export-steam-config.ps1
[CmdletBinding()]
param(
    # Steam 用户名（拥有 Stardew Valley 的账号；建议用专用小号而非主号）
    [string]$UserName = $(Read-Host "Steam 用户名"),
    # steamcmd 安装目录（默认落在系统临时目录，与用户 TEMP 环境变量一致）
    [string]$SteamCmdDir = (Join-Path ([System.IO.Path]::GetTempPath()) "steamcmd-ci")
)

$ErrorActionPreference = "Stop"

# ---- 1. 准备 steamcmd ----
$exe = Join-Path $SteamCmdDir "steamcmd.exe"
if (-not (Test-Path $exe)) {
    Write-Host "==> 下载官方 steamcmd 到 $SteamCmdDir" -ForegroundColor Cyan
    New-Item -ItemType Directory -Force -Path $SteamCmdDir | Out-Null
    $zip = Join-Path $SteamCmdDir "steamcmd.zip"
    Invoke-WebRequest -Uri "https://steamcdn-a.akamaihd.net/client/installer/steamcmd.zip" -OutFile $zip
    Expand-Archive -Path $zip -DestinationPath $SteamCmdDir -Force
    Remove-Item $zip
}

# ---- 2. 交互式登录一次（写入 ssfn 机器授权到 config/config.vdf）----
Write-Host "==> 启动 steamcmd，请在窗口提示下输入密码与 Steam Guard 验证码" -ForegroundColor Cyan
Write-Host "    （登录成功出现 Steam> 提示符后输入 quit 退出）" -ForegroundColor Yellow
& $exe "+@ShutdownOnFailedCommand" 0 "+login" $UserName "+quit"
if ($LASTEXITCODE -ne 0) {
    Write-Host "FAIL: steamcmd 退出码 $LASTEXITCODE（登录未完成？重跑本脚本）" -ForegroundColor Red
    exit $LASTEXITCODE
}

# ---- 3. 定位并编码 config.vdf ----
# steamcmd 的 config 落盘位置因安装形态而异：本目录 / %USERPROFILE%\Steam，
# 两处都找一遍。
$configCandidates = @(
    (Join-Path $SteamCmdDir "config\config.vdf"),
    (Join-Path $env:USERPROFILE "Steam\config\config.vdf")
)
$config = $configCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $config) {
    Write-Host "FAIL: 未找到 config/config.vdf（查找过：$($configCandidates -join '; ')）" -ForegroundColor Red
    Write-Host "      登录可能未完成，请重跑本脚本并确保登录成功。" -ForegroundColor Red
    exit 1
}

$b64 = [Convert]::ToBase64String([System.IO.File]::ReadAllBytes($config))

# ---- 4. 输出 ----
Write-Host ""
Write-Host "PASS: 已从 $config 生成 base64（长度 $($b64.Length)）" -ForegroundColor Green
try {
    Set-Clipboard -Value $b64
    Write-Host "已复制到剪贴板。去 GitHub 仓库 Settings → Secrets and variables → Actions → New repository secret：" -ForegroundColor Green
}
catch {
    Write-Host "以下内容即为 base64（复制整行）：`n" -ForegroundColor Green
    Write-Host $b64
}
Write-Host "  Name:  STEAM_CONFIG_VDF_BASE64" -ForegroundColor Cyan
Write-Host "  Value: (粘贴剪贴板)" -ForegroundColor Cyan
Write-Host ""
Write-Host "再补两个 secret：STEAM_USERNAME、STEAM_PASSWORD（同一账号）。" -ForegroundColor Green
Write-Host "三个配齐后 push，ci.yml 的 csharp job 会自动启用。" -ForegroundColor Green
