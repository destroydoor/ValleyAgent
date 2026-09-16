# M3 多玩家化真机 E2E（PR #5 验收）：双/三实例（host + farmhand xN）驱动 M3 能力。
#
# 覆盖：
#   C7  房客送礼/交易菜单（GiftTradeMenuLogic 去除 isThinClient 过滤）
#   C6  房客聊天栏路由（ChatBarRouter.InitializeFarmhand → 主机转发 → 本地渲染）
#   C3  多玩家对话（每个在线玩家各一次，触发 action_result → 情绪玩家桶归属）
#   C2  房客送礼 transport（回归）
#   C6h 主机聊天栏路由（M3 不破坏主机形态，回归）
#   换日 debug sleep → 导演 morningPlan per-player（PlayerDirectory 全员 → 各一次 LLM）
#
# 用法：
#   $env:VALLEY_TEST_SAVE = "<测试存档>"
#   powershell -File scripts\test\run_m3_e2e.ps1 [-Farmhands 1|2]
#   -Farmhands 2 = 三人场景（复现"三人一天内死锁"历史缺陷的观测面）
#
# 证据：SMAPI-latest*.txt（host=farmhand 按启动顺序 claim）、Mods/ValleyAgent/ValleyAgent-server.log。
# 注意：本脚本启动前会强杀残留游戏进程并清空 SMAPI-latest*.txt（日志名按 claim 顺序分配，
#       不清场则 farmhand 日志映射错位 —— 2026-09-13 实测踩坑）。
param(
    [ValidateSet(1, 2)]
    [int]$Farmhands = 1
)
$ErrorActionPreference = "Stop"

. "$PSScriptRoot\..\lib\paths.ps1"
$gameDir = Get-GamePath
$SaveName = if ($env:VALLEY_TEST_SAVE) { $env:VALLEY_TEST_SAVE } else { "TestSave_Main" }
$smapi = "$gameDir\StardewModdingAPI.exe"
$logDir = Join-Path $env:APPDATA "StardewValley\ErrorLogs"
$testModDir = "$gameDir\Mods\ValleyAgent.TestMod"
$hostCmdFile = "$testModDir\test_commands_host.txt"
$farmhandCmdFile = "$testModDir\test_commands_farmhand.txt"
$serverLog = "$gameDir\Mods\ValleyAgent\ValleyAgent-server.log"

function Wait-LogLine {
    param([string]$Path, [string]$Pattern, [int]$TimeoutSeconds, [string]$Label)
    Write-Host "  waiting: $Label ($Pattern)"
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        if (Test-Path $Path) {
            try {
                $fs = [System.IO.FileStream]::new($Path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
                $sr = [System.IO.StreamReader]::new($fs, [System.Text.Encoding]::UTF8)
                $text = $sr.ReadToEnd()
                $sr.Close()
                $fs.Close()
                if ($text -match $Pattern) { Write-Host "  OK: $Label" -ForegroundColor Green; return $true }
            }
            catch { }
        }
        Start-Sleep -Seconds 1
    }
    Write-Host "  TIMEOUT: $Label" -ForegroundColor Red
    return $false
}

function Start-GameInstance {
    param([string]$InstanceName)
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $smapi
    $psi.WorkingDirectory = $gameDir
    $psi.UseShellExecute = $false
    $psi.RedirectStandardInput = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.EnvironmentVariables["VALLEY_TEST_INSTANCE"] = $InstanceName
    $proc = [System.Diagnostics.Process]::Start($psi)
    # 异步排空 stdout/stderr，防管道缓冲塞满卡死游戏；输出内容不关心（证据走日志文件）。
    # 注意：不能先摸 StandardOutput 属性（同步模式）再 BeginOutputReadLine（异步模式），两者互斥。
    $proc.BeginOutputReadLine()
    $proc.BeginErrorReadLine()
    return $proc
}

function Send-Cmd {
    param([string]$File, [string]$Cmd)
    Set-Content -Path $File -Value $Cmd -Encoding UTF8 -NoNewline
}

Write-Host "=== M3 real-machine E2E (farmhands=$Farmhands) ==="
Write-Host "Save: $SaveName"
Write-Host "Game: $gameDir"

# ---- 清场：残留游戏进程 + 陈旧日志（否则日志名映射错位/端口互杀） ----
Get-Process -Name "StardewModdingAPI", "Stardew Valley" -ErrorAction SilentlyContinue |
    ForEach-Object { Write-Host "Killing leftover game process PID=$($_.Id)"; Stop-Process -Id $_.Id -Force }
Start-Sleep -Seconds 2
Get-ChildItem $logDir -Filter "SMAPI-latest*.txt" -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
Write-Host "Leftover processes killed, old SMAPI logs removed."

# V3 自动测试会占住主机实例并退出游戏，E2E 必须 manual（TestMod 构建部署会拷回 v3 默认值）。
$testCfg = "$testModDir\test_config.json"
if (Test-Path $testCfg) {
    $cfgText = Get-Content $testCfg -Raw -Encoding UTF8
    if ($cfgText -notmatch '"runner":\s*"manual"') {
        $cfgText = $cfgText -replace '"runner":\s*"[^"]*"', '"runner": "manual"'
        [System.IO.File]::WriteAllText($testCfg, $cfgText, (New-Object System.Text.UTF8Encoding $false))
        Write-Host "test_config.json runner set to manual"
    }
}

# 双/三实例共用存档，每次跑前恢复 AutoLoadGame 目标（强退可能清空）。
$autoLoadCfg = "$gameDir\Mods\AutoLoadGame\config.json"
if (Test-Path $autoLoadCfg) {
    $cfgText = Get-Content $autoLoadCfg -Raw -Encoding UTF8
    $cfgText = $cfgText -replace '"LastFileLoaded"\s*:\s*("[^"]*"|null)', "`"LastFileLoaded`": `"$SaveName`""
    [System.IO.File]::WriteAllText($autoLoadCfg, $cfgText, (New-Object System.Text.UTF8Encoding $false))
    Write-Host "AutoLoadGame LastFileLoaded = $SaveName"
}

# 导演 morningPlan 概率门控已随 issue #17 摘除（2026-09-16）：
# Director.TriggerProbability 配置项与 --director-probability 透传均不存在，
# 原先"临时改 1.0 / finally 恢复"的 config.json 改写块已删除。

$hostLog = "$logDir\SMAPI-latest.txt"            # 第 1 个 claim 的实例（host）
$farmhandLog = "$logDir\SMAPI-latest.player-2.txt"   # 第 2 个（farmhand A）
$farmhand2Log = "$logDir\SMAPI-latest.player-3.txt"  # 第 3 个（farmhand B，仅 -Farmhands 2）

$hostProc = $null
$farmhandProc = $null
$farmhand2Proc = $null
$failures = @()

try {
    Write-Host ""
    Write-Host "[1/10] Starting host instance..."
    $hostProc = Start-GameInstance -InstanceName "host"
    Write-Host "  host PID: $($hostProc.Id)"
    if (-not (Wait-LogLine -Path $hostLog -Pattern "ValleyAgent TestMod\] Save loaded" -TimeoutSeconds 240 -Label "host save loaded")) {
        throw "Host did not load save in time."
    }
    if (-not (Wait-LogLine -Path $hostLog -Pattern "VALLEY_TEST_INSTANCE=host" -TimeoutSeconds 10 -Label "host identity")) {
        throw "Host log identity mismatch (expected VALLEY_TEST_INSTANCE=host)."
    }
    Start-Sleep -Seconds 3

    Write-Host "[2/10] Hosting multiplayer server (cabins=$Farmhands)..."
    Send-Cmd -File $hostCmdFile -Cmd "va_mp_host $Farmhands"
    if (-not (Wait-LogLine -Path $hostLog -Pattern "MP\] Hosted multiplayer server" -TimeoutSeconds 120 -Label "host server started")) {
        throw "Host server did not start in time."
    }
    Start-Sleep -Seconds 3

    Write-Host "[3/10] Starting farmhand A..."
    $farmhandProc = Start-GameInstance -InstanceName "farmhand"
    Write-Host "  farmhand A PID: $($farmhandProc.Id)"
    if (-not (Wait-LogLine -Path $farmhandLog -Pattern "VALLEY_TEST_INSTANCE=farmhand" -TimeoutSeconds 240 -Label "farmhand A identity")) {
        throw "Farmhand A log identity mismatch (wrong log mapping?)."
    }
    Start-Sleep -Seconds 3

    if ($Farmhands -ge 2) {
        Write-Host "[3b/10] Starting farmhand B..."
        $farmhand2Proc = Start-GameInstance -InstanceName "farmhand"
        Write-Host "  farmhand B PID: $($farmhand2Proc.Id)"
        if (-not (Wait-LogLine -Path $farmhand2Log -Pattern "VALLEY_TEST_INSTANCE=farmhand" -TimeoutSeconds 240 -Label "farmhand B identity")) {
            throw "Farmhand B log identity mismatch."
        }
        Start-Sleep -Seconds 3
    }

    Write-Host "[4/10] Farmhand(s) joining host..."
    # A/B 共用 test_commands_farmhand.txt（env 同为 farmhand）：两边 watcher 就绪后写一次，同时消费、各自 join。
    Send-Cmd -File $farmhandCmdFile -Cmd "va_mp_join"
    if (-not (Wait-LogLine -Path $farmhandLog -Pattern "MP\] Joined game and activated farmhand" -TimeoutSeconds 240 -Label "farmhand A joined")) {
        throw "Farmhand A did not join in time."
    }
    if ($Farmhands -ge 2) {
        if (-not (Wait-LogLine -Path $farmhand2Log -Pattern "MP\] Joined game and activated farmhand" -TimeoutSeconds 240 -Label "farmhand B joined")) {
            throw "Farmhand B did not join in time."
        }
    }
    if (-not (Wait-LogLine -Path $farmhandLog -Pattern "\[ChatBar\] farmhand router ready" -TimeoutSeconds 60 -Label "M3 farmhand chat router init")) {
        $failures += "C0: farmhand ChatBarRouter 未初始化（[ChatBar] farmhand router ready 缺失）"
    }
    Start-Sleep -Seconds 3

    Write-Host "[5/10] Allocating Haley as Agent (host)..."
    Send-Cmd -File $hostCmdFile -Cmd "va_test_alloc Haley"
    if (-not (Wait-LogLine -Path $hostLog -Pattern "\[Alloc\] Haley allocated" -TimeoutSeconds 60 -Label "Haley allocated")) {
        throw "Haley allocation failed."
    }
    Start-Sleep -Seconds 3

    Write-Host "[6/10] C1 + C7: farmhand dialogue box & gift/trade menu (M3)..."
    Send-Cmd -File $farmhandCmdFile -Cmd "va_test_c1 Haley"
    if (-not (Wait-LogLine -Path $farmhandLog -Pattern "\[C1\] PASS" -TimeoutSeconds 60 -Label "C1 farmhand dialogue box PASS")) {
        $failures += "C1: 房客 AI 对话框未打开"
    }
    Send-Cmd -File $farmhandCmdFile -Cmd "va_test_menu Haley"
    if (-not (Wait-LogLine -Path $farmhandLog -Pattern "\[C7\] PASS" -TimeoutSeconds 60 -Label "C7 menu PASS")) {
        $failures += "C7: 房客送礼/交易菜单未弹出"
    }

    Write-Host "[7/10] C6: farmhand chat bar routing (M3)..."
    Send-Cmd -File $farmhandCmdFile -Cmd "va_test_chat Haley nice weather today"
    if (-not (Wait-LogLine -Path $farmhandLog -Pattern "\[ChatBar\] '.+' → " -TimeoutSeconds 60 -Label "C6 farmhand route hit")) {
        $failures += "C6: 房客聊天栏路由未命中（无 [ChatBar] 路由行）"
    }
    # 回复经主机 LLM 转发回来，本地渲染（等待足够长的 LLM 窗口）
    if (-not (Wait-LogLine -Path $farmhandLog -Pattern "\[ChatBar\] (Haley|海莉) → " -TimeoutSeconds 120 -Label "C6 farmhand reply rendered")) {
        $failures += "C6: 房客聊天栏 120s 内未收到/渲染回复"
    }

    Write-Host "[8/10] C2 gift + C6 host chat + C3 dialogues (each online player)..."
    # C3 可能触发 set_goal（默认消息"帮我挖矿"）让 Haley 进入执行态离开原地，
    # 依赖在场/近距离的 C2、C6h 放在 C3 之前。
    Send-Cmd -File $farmhandCmdFile -Cmd "va_test_c2 Haley 74"
    if (-not (Wait-LogLine -Path $farmhandLog -Pattern "\[C2\] PASS" -TimeoutSeconds 90 -Label "C2 farmhand gift PASS")) {
        $failures += "C2: 房客送礼 transport 未消耗礼物"
    }

    Send-Cmd -File $hostCmdFile -Cmd "va_test_chat Haley tell me a story"
    if (-not (Wait-LogLine -Path $hostLog -Pattern "\[ChatBar\] '.+' → " -TimeoutSeconds 90 -Label "C6 host route hit")) {
        $failures += "C6h: 主机聊天栏路由未命中（M3 不应破坏主机形态）"
    }
    # 等 C6h 的 LLM 回复渲染完，避免随后的 C3 撞上 NPC 在途对话 BUSY
    Wait-LogLine -Path $hostLog -Pattern "\[ChatBar\] (Haley|海莉) → " -TimeoutSeconds 120 -Label "C6 host reply rendered" | Out-Null

    Send-Cmd -File $farmhandCmdFile -Cmd "va_test_c3 Haley"
    # 并发对话可能有一端被 BUSY（主机灰字拒答）：PASS 落在任一房客日志都算链路通。
    $c3FarmhandOk = Wait-LogLine -Path $farmhandLog -Pattern "\[C3\] PASS" -TimeoutSeconds 120 -Label "C3 farmhand PASS"
    if (-not $c3FarmhandOk -and $Farmhands -ge 2) {
        $c3FarmhandOk = Wait-LogLine -Path $farmhand2Log -Pattern "\[C3\] PASS" -TimeoutSeconds 60 -Label "C3 farmhand B PASS (fallback)"
    }
    if (-not $c3FarmhandOk) {
        $failures += "C3: 房客对话 120s 内无 LLM 响应"
    }

    Send-Cmd -File $hostCmdFile -Cmd "va_test_c3 Haley"
    if (-not (Wait-LogLine -Path $hostLog -Pattern "\[C3\] PASS" -TimeoutSeconds 120 -Label "C3 host PASS")) {
        $failures += "C3: 主机对话 120s 内无 LLM 响应"
    }

    Write-Host "[9/10] Day change (va_test_sleep all instances) -> director morningPlan per-player..."
    # 联机换日走 ReadyCheckDialog：需要所有在线玩家都置 ready，故 sleep 命令必须发到每个实例
    # （A/B 共用 farmhand 命令文件，写一次两房客同时消费）。console 线程置位、主线程执行。
    Send-Cmd -File $farmhandCmdFile -Cmd "va_test_sleep"
    Send-Cmd -File $hostCmdFile -Cmd "va_test_sleep"
    if (-not (Wait-LogLine -Path $hostLog -Pattern "\[MP\] va_test_sleep: triggering day change" -TimeoutSeconds 60 -Label "sleep command fired (host)")) {
        $failures += "换日: va_test_sleep 命令未执行"
    }
    # 换日 + day_started + morningPlan（每个已知玩家一次 LLM 编排；实际 players=N 由日志记录，事后分析）
    if (-not (Wait-LogLine -Path $serverLog -Pattern "\[director\] morningPlan end \(multiplayer\)" -TimeoutSeconds 300 -Label "director morningPlan (multiplayer)")) {
        $failures += "导演: 换日后 300s 内 morningPlan(multiplayer) 未完成"
    }

    Write-Host "[10/10] Post-day stability observation (90s, freeze watch)..."
    # 换日后三个进程继续跑 90s：三人场景的历史缺陷是"日内死锁未响应"，观察窗口兜住换日后的同步抖动。
    $deadline = (Get-Date).AddSeconds(90)
    while ((Get-Date) -lt $deadline) {
        foreach ($p in @($hostProc, $farmhandProc, $farmhand2Proc)) {
            if ($p -and -not $p.HasExited -and -not $p.Responding) {
                $failures += "未响应: PID=$($p.Id) ($($p.ProcessName)) 在稳定性观察窗口无响应"
                $deadline = (Get-Date)  # 终止观察
            }
        }
        Start-Sleep -Seconds 5
    }
    Write-Host "  stability window done"

    Write-Host ""
    Write-Host "=== DONE. Failures: $($failures.Count) ==="
    foreach ($f in $failures) { Write-Host "  FAIL: $f" -ForegroundColor Red }
    if ($failures.Count -eq 0) { Write-Host "ALL M3 CHECKS GREEN" -ForegroundColor Green }
}
finally {
    Write-Host ""
    Write-Host "Stopping game instances..."
    foreach ($p in @($hostProc, $farmhandProc, $farmhand2Proc)) {
        if ($p -and -not $p.HasExited) {
            try { $p.Kill(); $p.WaitForExit(10000) } catch { }
        }
    }
    # 兜底：引用丢失的实例（Start-GameInstance 在赋值前抛异常时）不留僵尸
    Get-Process -Name "StardewModdingAPI", "Stardew Valley" -ErrorAction SilentlyContinue |
        ForEach-Object { Write-Host "Killing orphan game process PID=$($_.Id)"; Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue }
    Write-Host "Artifacts:"
    Write-Host "  Host log:     $hostLog"
    Write-Host "  Farmhand log: $farmhandLog"
    if ($Farmhands -ge 2) { Write-Host "  Farmhand B:   $farmhand2Log" }
    Write-Host "  Server log:   $serverLog"
}
