# Docker 测试容器化计划（2026-08-17）

> **状态**：已批准，实施中
> **依据**：2026-08-17 测试审计 + 社区调研（JunimoServer Testcontainers 参考架构）+ TestMod 基础设施探查
> **目标**：三层测试（TS / C# 单测 / 游戏内 IT）全部可容器化运行；游戏内层先做单实例 spike 验证
> **执行方式**：subagent 驱动开发（主会话编排审查，subagent 先交思路再审后放行）

## 一、现存系统问题清单（本计划收录的发现）

### P0 阻塞类

1. **IT14 从未运行过**：`IT14_MultiplayerAdjust` 写完未注册进 `V3TestRunner.InitializeTests()`（V3TestRunner.cs:694-710 只加 IT01-IT13），目前只能手动触发。playerId 三分支实际只靠 E2E harness 覆盖。
2. **C 盘容量**（已缓解）：清理后 22G 可用；WSL2 vhdx 增长仍需观察。测试临时文件已立规走 `<REPO_ROOT>\.tmp`。

### P1 测试体系缺口（2026-08-17 审计遗留）

3. 游戏内测试强依赖唯一真实存档 `TestSave_Main` + 真实 Haley NPC——无最小化测试存档、无快照/种子机制，测试副作用靠 Teardown 手工恢复。
4. IT11 无 Teardown 恢复；IT06/IT08 清整槽会销毁玩家原有同 ID 物品。
5. PIPE001-006（含自带 MockWebSocketServer 的 WS 链路测试）走 `vat_run pipeline` 手动触发，不进自动队列。
6. `AdjustExecutor.FindCachedResult` 无生产调用方（重连闭环只靠 TS 重发，设计 §6 的"查询"通道未实现）。

### P2 审计未修（沿用 known-unfixed 清单）

账本 pending 终态永不清理；账本写文件无 temp+rename；trade price/quantity 非整数校验缺失；批内多扣玩家钱预校验不累计。

### Linux 移植性阻塞点（容器化必须绕开或修复）

7. `ServerProcessManager`：端口清理用 `netstat+taskkill`、控制台用 `cmd.exe /c start`（ServerProcessManager.cs:299-333, 631-692）——容器内若让 mod 自起服务器则崩溃。
8. `valley-ai-server.exe` 是 `bun-windows-x64` 产物——Linux 容器需 `bun-linux-x64` 构建或容器内 `bun run cli.ts`。
9. E2E harness（run_farmhand_e2e.ps1）整体 Windows-bound：user32 P/Invoke 窗口编排 + gdigrab 录屏 + 固定窗口坐标。
10. `build-deploy.ps1` 曾硬编码本机绝对游戏路径；现已改为 `scripts/lib/paths.ps1` 的 `Get-GamePath`（`STARDW_PATH` 环境变量可覆盖）。

### 风险注记

Windows 版游戏 `Content/`（XNB 数据）跨平台通用预期成立，但 JunimoServer 用的是 Linux 发行版文件——spike 第一验证点，若启动报错需换 GOG Linux 版 Content。

## 二、阶段设计

### Phase 0 前置

C 盘清理（已完成）；Docker Desktop 启动 + WSL2 数据根确认在 D 盘（vhdx 迁移为后续项，暂观察）。

### Phase 1 纯逻辑层容器化（低成本，先行落地）

- **ValleyAI**：新增 `Dockerfile`（`oven/bun` 镜像）+ `docker-compose.test.yml`，跑 `bun install && bun test && bun run typecheck && bun run check:protocol`。
  - ⚠️ 待验证：`check:protocol`（scripts/check-protocol-contract.ts）是否跨仓库引用 ValleyTalk 的 C#/协议文件——若引用则需挂载或容器内跳过该项。
- **ValleyTalk C# 单测**：新增 `docker/Dockerfile.unittests`（`mcr.microsoft.com/dotnet/sdk:8.0`），**镜像不含游戏 DLL**——运行时挂载游戏目录（只读）+ `STARDREW_VALLEY_GAME_PATH` 环境变量，`dotnet test`（`stardewvalley.targets` 钩子现成）。构建上下文不得包含游戏目录/Mods/logs（.dockerignore 把关）。

### Phase 2 游戏内 IT 容器化 spike（核心验证）

1. **修问题 #1**：IT14 注册进 `V3TestRunner.InitializeTests()`（单机环境下它测 own-id/ghost-id/缺省三分支，全自包含，无需服务器）。
2. **spike 容器** `docker/Dockerfile.gameit`：
   - 基础镜像 `jlesage/baseimage-gui`（内建 Xvfb + VNC，JunimoServer 同款）；
   - 构建期装 Linux SMAPI 4.3.2（installer `--game-path /game`，脚本化同 JunimoServer：`printf "2\n\n" | SMAPI.Installer --install --game-path /game`）；
   - 运行时挂载：游戏目录（只读）→ `/game`；宿主 `Mods/`（含 ValleyAgent + TestMod + AutoLoadGame，config 改：`AutoStartServer=false`、`UseAgentServer=false`、`ConsoleWindow=false`）；存档 `TestSave_Main` → `~/.local/share/StardewValley/Saves/`；`logs/test_results` 挂出宿主；
   - 运行链：容器起 → SMAPI 启动游戏 → AutoLoadGame 载档 → V3TestRunner 自动跑（含 IT14）→ 写 JSON 结果 + `_TEST_COMPLETE.txt` → `Game1.quit` → 容器自然退出；
   - 驱动脚本 `scripts/docker/run-it.{ps1,sh}`：起容器、等 `_TEST_COMPLETE.txt`、读 `_summary.json` 判 PASS/FAIL、导出 VNC 端口排障。
3. **验证门槛**：IT01-IT13 + IT14 全 PASS（与宿主跑分一致）；跑 3 次无 flakes；软渲染下总时长可接受（预计 <5 分钟）。
4. spike 结论写回本文档（成功 → Phase 3 立项；失败 → 记录根因，游戏内层保持宿主实测）。

### Phase 3 全栈 + 联机容器（spike 通过后另行立项）

> **Phase 2 spike 结论（2026-08-19 实证）：结论为正——游戏内 IT 容器化全链路跑通。**
> 关键前提：**必须用 Linux 版游戏文件**。Windows 版游戏的 `*.deps.json` 平台绑定
> （runtime pack `win-x64`、native 条目全是 `api-ms-win-*.dll` stub），Linux host 无法解析，
> 已用 COREHOST_TRACE 实证（尝试过 dotnet 6 就位 / deps RID hack / runtime 平铺，均失败，
> 属平台本质差异——这也解释了 JunimoServer 必须下载 Linux 版）。Steam 版用户可用
> `scripts/docker/download-linux-game.ps1`（SteamCMD `+@sSteamCmdForcePlatformType linux`
> 强制 Linux 平台下载 appid 413150，3788 文件 / 676MB）获取真 Linux 版。
>
> **落地成果（2026-08-19）**：`Dockerfile.gameit`（jlesage/baseimage-gui + Xvnc）+ SMAPI 4.3.2
> 离线安装（`smapi-cache`）+ 游戏容器内自动载档跑 V3TestRunner，最终 **24 测试 130 PASS / 0 FAIL /
> 1 SKIP**（SKIP 为 DIR_DirectorBehaviorRecord，容器无 TS server 属预期）。测试时长约 10 分钟
> （软渲染 tick 慢，Fuzzy 组普遍 TIMEOUT 标记但断言全过）。
>
> **踩坑记录（后续维护必读）**：
> 1. SMAPI 数据目录在游戏目录内 `StardewValley/` 子目录（非 `~/.config`），且 installer 会创建
>    6.9KB 同名 launcher 文件与日志目录冲突——启动前必须 `rm -f "${GAME_DEST}/StardewValley"`。
> 2. `prep-mods.ps1` 曾把 `MaxAgentNpcs` 置 0 导致 IT 组全 SKIP（`TryAllocateAgent` 恒 false）——
>    需保留 `MaxAgentNpcs=2`；`ValleyAgent.Abstractions.dll` 需从 build output 补进 mods-cache
>    （宿主部署目录有时只有 manifest）。
> 3. F2_RandomMonsters 曾把 Haley 打到残血（3/100 < EmergencyHealthThreshold 30%），逃生逻辑
>    持续拉回 FOLLOW 污染后续 IT03——F2 Teardown 已改为无条件满血复活。
> 4. TestMod 部署 dll 比源码旧会导致新注册测试不跑——重跑前重新 `dotnet build` + 部署。
> 5. run-it.ps1 的 Start-Job 起容器会静默失败——已改 `docker run -d` + `$LASTEXITCODE` 检查。
> 6. 软渲染下 F_MineRealCombat 时序断言不稳定——容器配置 testFilter.disable 中排除。


- TS 服务器 Linux 化：compose 服务容器内 `bun run cli.ts --port 8765`，游戏 mod 连 `ws://server:8765`（绕开 ServerProcessManager 的 Windows 专有代码）；
- 联机 E2E：compose 起 host + farmhand 双游戏容器（`VALLEY_TEST_INSTANCE` 已可移植），用现成 `CommandFileWatcher` 通道（`test_commands_{instance}.txt`）驱动 C1-C5——文件通道等价于 JunimoServer 的 HTTP API 且零改造；
- PIPE001-006 进自动队列（问题 #5 顺手解决）。

### 明确不做

- 不把 E2E 录屏（gdigrab→x11grab）搬进容器——无头测试不需要录屏，排障用 VNC；
- 不在本计划修 P2 审计项（另立"修复批次"）；问题 #3（最小化测试存档）列为后续改进。

## 三、交付物

| 文件 | 内容 |
|---|---|
| `docs/plan/2026-08-17-docker-test-containerization-plan.md` | 本文档 |
| `<VALLEYAI_ROOT>\Dockerfile` + `docker-compose.test.yml` | TS 层 |
| `docker/Dockerfile.unittests` + `.dockerignore` + 运行脚本 | C# 单测层 |
| `docker/Dockerfile.gameit` + `scripts/docker/run-it.{ps1,sh}` | 游戏 IT spike |
| `src/ValleyAgent.TestMod/Runners/V3TestRunner.cs` 补 IT14 注册 | 修问题 #1 |

## 四、验证与提交

- Phase 1 门槛：两容器内测试数与宿主一致全绿（628 + 563）。
- Phase 2 门槛：见上。
- 提交：`feat(docker):` / `fix(testmod):` 前缀，两仓库分开；镜像与游戏文件不入库（.dockerignore + gitignore 确认）。
