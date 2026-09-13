# 打包 ValleyAgent 分发包（给朋友直接玩，零配置）
#
# 产物：release/ValleyTalk-dist-<日期>.zip
#   ValleyAgent/            —— 单个 mod 文件夹（含 TS 服务器 exe + 预配置的 API key）
#
# 与 deploy.ps1 的区别：
#   - 不进入游戏 Mods 目录（dev 环境不受影响），只产出独立 zip
#   - 不含 ValleyAgent.TestMod / ValleyAgent.Autopilot（测试基建，绝不分发）
#   - config.json 从 dev 部署配置读取 API key，并强制 ServerConsoleWindow=true
#     （玩家直接看得到 TS 服务终端实时日志；反馈问题时截图窗口即可）
#
# 用法：powershell -File scripts\build\package-distribution.ps1 [-Configuration Release] [-SkipBuild]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"
. "$PSScriptRoot\..\lib\paths.ps1"
$RepoRoot   = Get-RepoRoot
$ValleyAIDir = Get-ValleyAIRoot
$DevConfigPath = Join-Path $RepoRoot "Stardew Valley\Mods\ValleyAgent\config.json"
$OutRoot = Join-Path $RepoRoot "release"
$timestamp = Get-Date -Format "yyyy-MM-dd_HHmmss"
$logFile = Join-Path $RepoRoot "scripts\results\package-$timestamp.txt"

function Write-Log([string]$Message, [string]$Level = "Info") {
    $color = switch ($Level) {
        "Success" { "Green" }
        "Warn"    { "Yellow" }
        "Error"   { "Red" }
        default   { "White" }
    }
    $ts = Get-Date -Format "HH:mm:ss"
    $line = "[$ts] [$Level] $Message"
    Write-Host $line -ForegroundColor $color
    $line | Out-File -FilePath $logFile -Append -Encoding UTF8
}

New-Item -ItemType Directory -Path (Join-Path $RepoRoot "scripts\results") -Force | Out-Null

if (-not $SkipBuild) {
    & "$PSScriptRoot\build-all.ps1" -Configuration $Configuration
    if ($LASTEXITCODE -ne 0) { Write-Log "Build failed" "Error"; exit 1 }
}

Write-Log "========================================" "Info"
Write-Log "  Packaging distribution" "Info"
Write-Log "========================================" "Info"

$BuildDir = Join-Path $RepoRoot "src\ValleyAgent\bin\$Configuration\net6.0"
$StageDir = Join-Path $OutRoot "ValleyTalk-dist"
$ModStage = Join-Path $StageDir "ValleyAgent"

if (Test-Path $StageDir) { Remove-Item $StageDir -Recurse -Force }
New-Item -ItemType Directory -Path $ModStage -Force | Out-Null

# ─── Step 1: mod 程序集与元数据 ───────────────────────────────────────
foreach ($f in @(
    "ValleyAgent.dll", "ValleyAgent.xml",
    "ValleyAgent.Abstractions.dll", "ValleyAgent.Abstractions.xml",
    "manifest.json")) {
    $src = Join-Path $BuildDir $f
    if (Test-Path $src) {
        Copy-Item $src (Join-Path $ModStage $f) -Force
        Write-Log "Copied $f"
    } else {
        Write-Log "REQUIRED file missing in build output: $f" "Error"
        exit 1
    }
}

# ─── Step 2: 数据目录（i18n/RAG/Data/npc-configs 只拷 json，杜绝源码混入）──
foreach ($dir in @("i18n", "RAG", "Data", "npc-configs")) {
    $src = Join-Path $BuildDir $dir
    if (Test-Path $src) {
        $jsons = Get-ChildItem -Path $src -Filter "*.json" -File -ErrorAction SilentlyContinue
        if ($jsons) {
            $dst = Join-Path $ModStage $dir
            New-Item -ItemType Directory -Path $dst -Force | Out-Null
            $jsons | ForEach-Object { Copy-Item $_.FullName $dst -Force }
            Write-Log "Copied $dir/ ($($jsons.Count) json)"
        }
    } else {
        Write-Log "WARN: expected content dir missing in build output: $dir" "Warn"
    }
}

# ─── Step 3: TS Agent Server（exe + npc_prompts.json）─────────────────
$ServerExeSrc = Join-Path $ValleyAIDir "packages\stardew\bin\valley-ai-server.exe"
if (Test-Path $ServerExeSrc) {
    Copy-Item $ServerExeSrc (Join-Path $ModStage "valley-ai-server.exe") -Force
    $sizeMB = [math]::Round((Get-Item $ServerExeSrc).Length / 1MB, 1)
    Write-Log "Copied valley-ai-server.exe (${sizeMB} MB)" "Success"
} else {
    Write-Log "valley-ai-server.exe not found at $ServerExeSrc — run build-all first" "Error"
    exit 1
}

$PromptsSrc = Join-Path $ValleyAIDir "packages\stardew\data\npc_prompts.json"
if (Test-Path $PromptsSrc) {
    New-Item -ItemType Directory -Path (Join-Path $ModStage "data") -Force | Out-Null
    Copy-Item $PromptsSrc (Join-Path $ModStage "data\npc_prompts.json") -Force
    Write-Log "Copied data/npc_prompts.json" "Success"
} else {
    Write-Log "npc_prompts.json not found at $PromptsSrc" "Error"
    exit 1
}

# ─── Step 4: config.json —— 从 dev 配置取 key（单一事实源），分发版强制开服务终端窗口 ───
if (-not (Test-Path $DevConfigPath)) {
    Write-Log "Dev config with API keys not found: $DevConfigPath" "Error"
    exit 1
}
$distConfig = Get-Content $DevConfigPath -Raw -Encoding UTF8 | ConvertFrom-Json
# 2026-09-09 用户要求：默认打开 TS 服务终端（实时日志可见，反馈=截图窗口）。
# 注意：ConsoleWindow=true 时 stdout 不落 ValleyAgent-server.log（二选一，见 ServerProcessManager）；
# 要拿落盘日志需在 config.json 把该项改回 false 重启。
$distConfig.ServerConsoleWindow = $true
$distConfig | ConvertTo-Json -Depth 10 | Out-File (Join-Path $ModStage "config.json") -Encoding UTF8
Write-Log "Wrote config.json (keys from dev config, ServerConsoleWindow=true)" "Success"

# ─── Step 5: README ──────────────────────────────────────────────────
$readme = @"
ValleyTalk / ValleyAgent —— 星露谷 AI NPC 模组（朋友分发包）
=============================================================

一、安装步骤
  1. 先安装 SMAPI（https://smapi.io/），装过就跳过。
  2. 把本包里的 ValleyAgent 整个文件夹放进游戏的 Mods 目录：
     <星露谷游戏目录>/Mods/ValleyAgent
  3. 用 StardewModdingAPI.exe 启动游戏（不要用 steam 直接启动）。

二、不需要任何配置
  API Key 已内置在 config.json，进入存档即可和 NPC 正常聊天。
  （进存档时会自动启动 AI 服务，首次启动有几秒延迟，属正常现象。）
  进存档后还会弹出一个"AI 服务"黑色终端窗口——那是 AI 服务的实时日志，
  最小化即可，千万别关（关掉 AI 就停了）；关掉游戏时它会自己退出。

三、出问题了怎么反馈
  1. 把那个黑色终端窗口的内容截图发回来（这就是 AI 服务的完整日志）。
  2. 如果需要日志文件：把 Mods/ValleyAgent/config.json 里的
     "ServerConsoleWindow" 改成 false 再重启游戏，会生成
     Mods/ValleyAgent/ValleyAgent-server.log，把它发回来即可。
  3. ValleyAgent-error.log（错误记录）无论如何都会生成，一并发回。
  日志会自动滚动（单文件最大 5MB），不用担心越写越大。

四、联机（可选）
  主机和房客都装本包即可。NPC 的经济/好感/行为全部由主机权威同步，
  房客对话内容各自独立记忆，互不串台。

五、其他
  - 如果装了 Generic Mod Config Menu，游戏内可直接改设置（一般不用动）。
"@
[System.IO.File]::WriteAllText((Join-Path $StageDir "README.txt"), $readme, (New-Object System.Text.UTF8Encoding($true)))
Write-Log "Wrote README.txt" "Success"

# ─── Step 6: 分发卫生检查 —— 测试基建绝不允许混入 ─────────────────────
$forbidden = Get-ChildItem -Path $StageDir -Recurse -Include @(
    "ValleyAgent.TestMod.dll", "ValleyAgent.Autopilot.dll", "*.pdb") -File
if ($forbidden) {
    foreach ($f in $forbidden) { Write-Log "FORBIDDEN file in package: $($f.Name)" "Error" }
    exit 1
}

# ─── Step 7: zip ─────────────────────────────────────────────────────
$zipPath = Join-Path $OutRoot "ValleyTalk-dist-$(Get-Date -Format 'yyyyMMdd-HHmm').zip"
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path (Join-Path $StageDir "*") -DestinationPath $zipPath -Force
$zipMB = [math]::Round((Get-Item $zipPath).Length / 1MB, 1)
Write-Log "Packaged: $zipPath (${zipMB} MB)" "Success"
Write-Log "Done." "Success"
exit 0
