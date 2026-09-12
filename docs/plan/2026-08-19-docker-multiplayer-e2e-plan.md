# Docker 联机 E2E 容器化计划（Phase 3，2026-08-19）

> 依据：`docs/plan/2026-08-17-docker-test-containerization-plan.md` Phase 3 立项（Phase 2 spike 已通过：单容器 IT 24 测试 130 PASS/0 FAIL/1 SKIP）。
> 前置调研（2026-08-19 三路 Explore）：TS 服务器 Linux 化可行性 / 联机 E2E harness 现状 / Docker 基础设施现状。
> 目标：TS 服务器容器 + host/farmhand 双游戏容器，通过文件通道驱动 C1-C5 联机测试，全绿即 Phase 3 落地。

## 一、目标架构

```
┌─────────────────────────────  docker network: valley-e2e  ─────────────────────────────┐
│                                                                                          │
│  ┌──────────────────────┐      ws://valley-ts:8765        ┌──────────────────────────┐  │
│  │ valley-ts (TS server) │◄────────────────────────────────┤ valley-host (游戏容器)   │  │
│  │ bun run cli.ts        │                                │ VALLEY_TEST_INSTANCE=host │  │
│  │ --hostname 0.0.0.0    │                                │ UseAgentServer=true       │  │
│  │ --port 8765           │                                │ AutoStartServer=false     │  │
│  │ LLM_API_KEY from env  │                                │ runner=manual (TestMod)   │  │
│  └──────────────────────┘                                └────────────┬─────────────┘  │
│        ▲  │                                                        │ Lidgren 24642      │
│        │  └─ /data-host 共享（agents/ 记忆 + C5 断言文件）         │ (容器网络直连)      │
│        │                                                           ▼                     │
│        └──────────────────────────────────────  ┌──────────────────────────┐            │
│                                ws://valley-ts   │ valley-farmhand (游戏容器) │            │
│                                                │ VALLEY_TEST_INSTANCE=farmhand│           │
│                                                │ ThinClient 模式             │            │
│                                                │ runner=manual (TestMod)    │            │
│                                                │ va_mp_join valley-host:24642│           │
│                                                └──────────────────────────┘            │
└──────────────────────────────────────────────────────────────────────────────────────────┘
```

**关键设计决策**（来自调研结论）：

| # | 决策 | 依据 |
|---|------|------|
| D1 | TS 服务器用源码跑（`bun run cli.ts`），**不用** mods-cache 里的 `valley-ai-server.exe`（Windows 二进制，Linux 不可执行） | Docker 调研 CRITICAL 发现 |
| D2 | TS 监听 `0.0.0.0:8765`（默认 127.0.0.1 容器内外部不可达），`--agents-dir`/`--data-path` 指向共享的 host 容器 mods 目录 | cli.ts:43 默认值；agents 记忆需跨容器一致 |
| D3 | C# 侧新增 `ModConfig.AgentServerHost`（默认 `127.0.0.1`），`ServiceInitializer.cs:240` 用它拼 WS URL——容器场景配 `valley-ts`（compose 服务名 DNS 解析） | 调研 1 结论：WS URL 现硬编码 127.0.0.1 |
| D4 | 两游戏容器都 `AutoStartServer=false`——绕过 ServerProcessManager 全部 Windows API（cmd.exe/netstat/taskkill），外部 TS 容器先起，mod 只连不拉起 | ServerProcessManager.cs:135-139 AutoStart=false 直接短路 |
| D5 | farmhand 走 ThinClient（`VALLEY_TEST_INSTANCE=farmhand` 已实现），经 SMAPI ModMessage 与 host 通信，不直连 TS | ModEntry.cs:296-308 |
| D6 | TestMod 必须 `runner=manual`（新 `test_config.e2e.json`）——否则 V3TestRunner 自动跑完 `Game1.quit` 容器提前退出 | Windows harness 前置补丁同款逻辑（run_farmhand_e2e.ps1:176-199） |
| D7 | 命令驱动：宿主直接写 `docker/data-{role}/Mods/ValleyAgent.TestMod/test_commands_{role}.txt`——bind mount 使容器内 CommandFileWatcher 即时可见，**零改造复用现有文件通道** | CommandFileWatcher.cs:107 实例文件命名 |
| D8 | 联机建立：host 载档后 `va_mp_host`（自动建 Cabin），farmhand 载档后 `va_mp_join valley-host:24642`（**地址参数已支持**，默认 127.0.0.1 需显式传容器名） | MultiplayerSetupCommands.cs:41-44 |
| D9 | LLM key 注入：TS 容器 env `LLM_API_KEY` 取自宿主 `docker/mods-cache/ValleyAgent/config.json` 的 `LlmApiKey`（prep 时已有，.gitignore 已排除不入库） | config.json LlmApiKey 字段实证 |
| D10 | 存档：两容器各自 AutoLoadGame 载同一 `awa_445353290`；host 开服建 Cabin，farmhand 加入槽位 | Windows harness 同机双开已验证此路径 |
| D11 | `/game`、`/smapi-cache` 只读挂载双容器共享；`/data-host`、`/data-farmhand` 独立 bind mount；TS 容器只读挂载 `/data-host` | Docker 调研 Q9.1/Q9.2 |
| D12 | 容器退出：C1-C5 完成后 harness `docker kill`（runner=manual 无自动退出） | TestMod 无 exit 命令（命令清单实证） |

## 二、改动清单

### 2.1 ValleyTalk C#（唯一代码改动）

| 文件 | 改动 |
|------|------|
| `src/ValleyAgent/Config/ModConfig.cs` | 新增 `AgentServerHost` 字段，默认 `"127.0.0.1"`（紧邻 `UseAgentServer`，注释说明容器场景配 compose 服务名） |
| `src/ValleyAgent/Initialization/ServiceInitializer.cs:239-241` | `wsUrl` 从 `$"ws://127.0.0.1:{config.ServerPort}"` 改为 `$"ws://{config.AgentServerHost}:{config.ServerPort}"` |
| GMCM 同步（如存在） | 注册/同步 `AgentServerHost`（调研 1 指出 GMCMIntegration.cs:1276 有复制同步逻辑） |
| 单测 | 现有 config 测试若有字段清单断言需补 `AgentServerHost`（编译过即可，行为无变化——默认值等于现状） |

### 2.2 Docker 编排（新增）

| 文件 | 内容 |
|------|------|
| `docker/docker-compose.e2e.yml` | 三服务：`valley-ts`（oven/bun 镜像 + ValleyAI 源码挂载 + `bun run cli.ts --hostname 0.0.0.0`）、`valley-host`、`valley-farmhand`（复用 valleyagent-gameit 镜像）；共享 network；host/farmhand 各挂独立 data 目录；`/game:/game:ro` 共享；VNC 端口错开（5801/5901 + 5802/5902，默认注释） |
| `docker/test_config.e2e.json` | `runner=manual`，`auto.exitOnComplete=false`；其余同 container 版（保留 F_MineRealCombat disable、Fuzzy\|Integration group 无关紧要因为 manual 不自动跑） |

### 2.3 脚本改动

| 文件 | 改动 |
|------|------|
| `scripts/docker/prep-mods.ps1` | 加 `-Role host\|farmhand\|it` 参数：`it` 保持现状（UseAgentServer=false）；`host` 配 `UseAgentServer=true, AutoStartServer=false, AgentServerHost=valley-ts, MaxAgentNpcs=2`；`farmhand` 配 `UseAgentServer=false`（ThinClient 不走 AgentService）+ `MaxAgentNpcs=0`。输出目录 `docker/mods-cache-{role}`；TestMod 的 test_config 按角色放（host/farmhand → test_config.e2e.json，it → container 版） |
| `scripts/docker/run-e2e.ps1`（新增） | 主驱动脚本：① 起 `valley-ts` 容器（LLM_API_KEY 从宿主 config 读）→ ② 起 `valley-host` → 等 SMAPI `Save loaded`（180s）→ 写 `test_commands_host.txt=va_mp_host` → 等 `MP] Hosted multiplayer server`（120s）→ ③ 起 `valley-farmhand` → 等 Save loaded → 写 `test_commands_farmhand.txt=va_mp_join valley-host:24642` → 等 `MP] Joined game`（180s）→ ④ C1-C5 序列（复用 Windows harness 步骤与日志断言）→ ⑤ 结果汇总（两份日志 + C5 rel 文件数断言）→ ⑥ `docker kill` 三容器 |

### 2.4 不动的东西（明确不做）

- **不搬**录屏（ffmpeg gdigrab）——排障走 VNC（容器内 Xvnc 已内置）
- **不改** `MultiplayerTestCommands.cs` 的 C1-C5 实现——文件通道 + 日志断言原样复用
- **不改** `va_mp_join` 默认地址逻辑——显式传参即可
- **不修** P2 审计项（另立批次）

## 三、C1-C5 驱动序列（run-e2e.ps1 主体）

```
1. 预分配  host: va_test_alloc Haley → 等 "[Alloc] Haley allocated"
2. C1      farmhand: va_test_c1 Haley → 等 15s（断言房客端 DialogueBox，看日志 [C1] PASS/FAIL）
3. C2      farmhand: va_test_c2 Haley 74 → 等 25s（送礼路径，日志断言）
4. C3      farmhand: va_test_c3 Haley → 等 60s（房客消息 → host TS LLM → 响应回包）
5. C4      host: va_test_c4 Haley → 等 20s（execute_adjust 带房客 playerId → 断言钱落房客）
6. C5      host: va_test_c3 Haley 晚上好呀 → 等 30s（断言 agents/Haley_players/ 下两个 *_rel.json）
```

日志断言文件：`docker/data-host/game/StardewValley/ErrorLogs/SMAPI-latest.txt`（host）与 `docker/data-farmhand/game/StardewValley/ErrorLogs/SMAPI-latest.txt`（farmhand）——Linux 容器内 SMAPI 日志在游戏目录内 `StardewValley/ErrorLogs/`（spike 实证）。

## 三·五、落地结论（2026-08-19 实证）

**全链路跑通：三容器（valley-ts + valley-host + valley-farmhand）C1-C5 全绿。**

最终结果：Alloc PASS / C1 PASS（房客端 DialogueBox）/ C2 PASS（礼物路径接管）/
C3 PASS（房客消息 → host TS 真实 LLM 响应回包，source=LLM）/ C4 PASS（execute_adjust
带房客 playerId，房客 +50 / NPC -50 落账正确）/ C5 PASS（agents/Haley_players/ 双
rel 文件 = 房客 + 主机各一）。

**联调踩坑记录（维护必读）**：
1. bun slim 镜像无 curl——healthcheck 改用 `bun -e fetch(...)`。
2. bind-mount 上 bun install 无法创建 workspace symlink（@valley/* EEXIST）——
   解法：ValleyAI 新增 `Dockerfile.server`，**源码先 COPY 再 install**（bun 只在
   workspace 源码就位时建链接），构建期安装，运行时不再 install。
3. ValleyAI `.dockerignore` 曾排除 bun.lock——构建找不到 lockfile，已放行。
4. TS 容器数据文件时序：npc_prompts.json 在 host 容器 startapp 才拷入 /data——
   TS 容器 command 里先 `cp /mods-src/ValleyAgent/Data/npc_prompts.json` 到可写
   /data-host（director sqlite 的 dbDir 也在 dataPath 同目录，必须可写）。
5. startapp.sh 时序：SMAPI installer 生成 StardewValley launcher **文件**，存档
   SAVE_DIR 在其同名**目录**下——rm 文件必须在 mkdir 之前（原顺序 mkdir 先跑，
   首次运行必崩，已修）。
6. run-e2e.ps1 LLM env 注入：compose `${LLM_API_KEY:?}` 读进程 env——PowerShell
   变量不会自动成为 env，必须显式 `$env:LLM_API_KEY = ...`（含 MODEL/BASE_URL/PROVIDER）。
7. C1-C4 断言硬化：等 `[Cx] PASS|FAIL` 明确日志，删掉"命令名 fallback"（会把
   失败误判为 PASS）。
8. C5 基线污染：宿主 E2E 遗留的 *_rel.json 会随 prep-mods 拷入——host 角色
   prep 时清空 Haley_players（C5 断言 ≥2 才真实）。

**Phase 3 状态**：联机容器 E2E 落地（M1 经济正确性 + M2 玩家上下文在容器内可复现）。
3 次无 flakes 稳定性验证、PIPE001-006 进自动队列列为后续项。

## 四、验证门槛

1. TS 容器 `curl -v http://valley-ts:8765/` 返回 WebSocket only（200）——Bun.serve 正确绑定 0.0.0.0
2. host 容器 SMAPI 日志出现 `Agent Server WebSocket connected`——mod 连上外部 TS（关键新链路）
3. 三容器齐全后 C1-C5 全绿（与 Windows harness 同判据：日志断言 + C4 钱落房客 + C5 双 rel 文件）
4. 跑通 1 次即 Phase 3 落地；3 次无 flakes 作为后续稳定性验证（可选）

## 五、风险与缓解

| 风险 | 缓解 |
|------|------|
| 软渲染下 tick 慢，日志等待超时不足 | Windows 超时 ×2 起步（C3 60s→120s 等），脚本用参数化超时 |
| 双游戏容器并发内存压力（llvmpipe 软渲染） | 先单跑验证资源；VNC 默认全关；必要时降分辨率/关 VNC |
| Lidgren 联机跨容器（非 localhost）可能有网络细节差异 | Windows 同机双开已验证 direct join 路径；容器同 bridge 网络 24642 直连预期可行；失败时抓 SMAPI 日志 + VNC 排障 |
| `valley-ts` 与游戏 mods 目录权限（agents 记忆写入） | TS 容器挂 /data-host 只读，agentsDir 指向 host 容器写出的 /data-host/Mods/ValleyAgent/agents（host 容器先启动、prep 已拷入 agents/） |
| LLM key 泄露入库 | key 只在 run-e2e.ps1 运行时从宿主 config 读入 env，不写文件不入库（.gitignore 已有 docker/ 下缓存目录） |

## 六、实施批次（subagent 分工）

- **批次 A（C# 代码）**：ModConfig.AgentServerHost + ServiceInitializer URL + GMCM + 编译 0 警告 + 相关单测绿。验证：宿主 `dotnet build` 全绿。
- **批次 B（编排）**：compose + test_config.e2e.json + prep-mods.ps1 -Role + run-e2e.ps1。验证：脚本语法 + dry-run 容器起停。
- **批次 C（联调）**：实跑三容器，C1-C5 全绿。此批由主会话亲自驱动（涉及真实 LLM 调用与游戏内状态，需要按 subagent-driven 工作流验收）。

## 七、交付物

| 文件 | 状态 |
|------|------|
| `docs/plan/2026-08-19-docker-multiplayer-e2e-plan.md` | 本文档 |
| `src/ValleyAgent/Config/ModConfig.cs` + `ServiceInitializer.cs` 改动 | 批次 A |
| `docker/docker-compose.e2e.yml` + `docker/test_config.e2e.json` | 批次 B |
| `scripts/docker/prep-mods.ps1`（-Role）+ `scripts/docker/run-e2e.ps1` | 批次 B |
| 联调记录 + 主计划文档 Phase 3 结论更新 | 批次 C |
