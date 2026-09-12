# 3 人联机主机无报错卡死——复刻与原因定位（结论文档）

> **日期**：2026-09-10
> **状态**：结案（原因未完全定罪，证据链与排除项见 §6；下一步唯一确证路径见 §8）。同日用户指令"开始修复"——已实证缺陷族修复批 6 项见文末附录（验证标准：虚拟环境由失败到成功，已达标）
> **任务依据**：`docs/plan/2026-09-10-host-freeze-repro-plan.md`（换 agent 交接文档）
> **用户约束**：先只查原因；修复批仅限已实证缺陷（看门狗/测试驱动/TS 兼容层）
> **实机证据**：修复前/后两轮真实测试（见交接文档 §1）——3 人联机，主机无报错未响应；第 3 玩家加入时也崩；发行包（无 TestMod/看门狗）零现场证据

---

## 0. 结论摘要（TL;DR）

1. **"每玩家一个导演"是误读，确凿**。TS 端 Director 是进程级单例（`server.ts:171` 启动时构造一次），全服务端唯一按 playerId 建 map 的是 NPC 记忆玩家桶（`agent-memory.ts:201`），与导演无关。误读的最直接来源：`handleDayStarted` **没有按日期去重**（`protocol-adapter.ts:147-186`），联机下同一游戏日多条 `day_started` 会各跑一轮 morningPlan，日志出现多组 `[director] morningPlan start`；其次 `director.ts:412` 把完整 prompt（含玩家姓名/位置）打进日志。详见 §2。
2. **第 3 人加入路径本身很轻**（探查 C）：加入 = 一行日志 + 一次 FullSync（活跃 Agent × 全存档 farmer 好感扫描）+ 每秒广播多一份拷贝，无按玩家数平方增长的工作。容器 6 轮 soak 共 18 次房客加入，零失败零卡顿。
3. **容器复刻 6 轮未复现进程级冻结**（主线程看门狗 5s 阈值零捕获，主线程 CPU ≤30%）。但第 5 轮（真实 LLM 对话 + 房客目标 FOLLOW + 跨图 + 过午）**现场抓到候选 1 缺陷族的活性表现**（正常游玩时段）：跨图坐标混算（FarmHouse 室内出现 61 格距离）、每 tick NoPathFound 无退出重试、NPC 走到错误地图的"幽灵点"站住。另"时间卡死 2600"被第 6 轮对照（同拓扑无 Agent 无对话，同样卡住）**裁定为测试装置工件**：TestMod 跳过房客角色定制 → `isCustomized=false` → 原版 2600 强制昏迷条件（反编译 `Game1.cs:6453`）永不满足 → 日终握手卡死，与模组无关，实机真实玩家不受影响。详见 §5。
4. **根因未完全定罪**。审计排序前三候选（§3）：①FOLLOW 跨图参照系错位缺陷族（活性已实证，容器中不冻结但行为级缺陷确认）②后台决策线程裸读 Game1 集合 → Dictionary 桶链损坏 → 主线程静默死循环（竞态窄窗口，无法主动复现，与"实机必崩/容器不崩"的时序差异相容）③FriendshipSystem 双线程写普通 Dictionary（冷路径）。**硬冻结的直接定罪证据只有实机 dump 一条路**（§8）。
5. **副发现（真实缺陷，与卡死无关但重要）**：
   - TS `llm-provider.ts:425` 的 provider switch **区分大小写**；`"MiniMax"` 落到 default 分支打 `api.openai.com`（CN 被墙，21s 连接超时 ×3）→ 全部对话 FALLBACK。实机走多供应商 router（config 字段小写 `minimax`）不受影响；**docker/E2E compose 直传 config 原值的路径踩坑**——本 soak 前 4 轮 C3 全灭的真实原因，且意味着 09-09 E2E 的 C3"C通过"可能一直是 FALLBACK 响应（PASS 判据不区分 source=LLM 还是 fallback）。
   - 房客对话主机侧 60s 超时（`HostRequestHandlers.cs:120`）与 TS 侧 3 次重试 ≈67s 的错位：慢 LLM/网络抖动时 fallback 回包必然迟到，以 `[WS] Unhandled unsolicited message type: dialogue_response` 落空，房客什么都收不到。
   - 看门狗 3s 默认阈值在读档加载期有假阳性（实测布防后 5s 内 3234ms 慢 tick）；容器内用 `VALLEY_WATCHDOG_MS=5000` + 监控起始时间窗过滤规避（真冻结是无限期停摆，5s 阈值不漏）。

---

## 1. 实机证据回顾

| 轮次 | 场景 | 现象 |
|---|---|---|
| 修复前 | 3 玩家不同地图游玩一段时间 | 主机无报错卡死；客机无模组无法与 NPC 交流；用户从日志推断"每玩家一个导演" |
| 修复后 | 玩家一家、玩家二矿洞、玩家三小镇 | 主机崩；第 3 玩家加入时也崩 |
| 修复后 | 加入不立即崩 | 过中午后崩，无预警无报错，程序直接未响应 |
| 对照 | 原版 3 人同玩法 | 无问题 |

前置提交（86399cc/0a74e12/f755aa1/7e41696）未声称修过主机卡死；看门狗（f755aa1）就是为抓本次现场做的仪器。

## 2. 探查 A：TS 侧"每玩家一个导演"真相

**结论：Director 单例，误读确认。**

- 单例构造：`ValleyAI/packages/stardew/src/server.ts:128/171`（`new Director(...)` 只在 `startServer()` 执行一次，先于任何 WS 连接）；依赖（BeatStore/ProfileMgr/GameContextMgr/ActivityStore）全部一次性单例。
- 玩家画像表是 `CHECK (id = 1)` 单行表（`player-profile-store.ts:22-51`）；`GameContext.playerState` 单人槽位（`narrative-types.ts:125-134`）——**Director 连"多玩家"的数据结构都不存在**。
- 全仓唯一 playerId-keyed map：`AgentMemory.players`（`agent-memory.ts:201`，NPC 记忆玩家桶，属 NPC Agent 侧）。
- `hello` 不创建任何状态（`protocol-adapter.ts:96-104`）；`allocate_agent` 是 TS→C# 方向（`:173-180`）。
- **误读来源**（按嫌疑排序）：
  1. `protocol-adapter.ts:147-186` `handleDayStarted` **无按 dateIso 去重**（对比 `consolidate_day` 有 `seenConsolidations` 去重 `:77/:469-472`）——联机下同一游戏日多次 `day_started` 各跑一轮 morningPlan，日志多组 `[director] morningPlan start`（对比：容器 soak 的 day_started 单发时只有一组）。
  2. `director.ts:412` 把**完整 prompt 原文**打进日志，内嵌玩家姓名/位置（来自单人画像槽位），不同时刻显示不同玩家。
  3. `protocol-adapter.ts:179` 每个 beat 一条 `[send] allocate_agent npc=...` 成串出现。
- 多连接：单槽 `activeWs`（`server.ts:134/208-210`），最后连接者独占 TS→C# 主动流量；第二连接出现几乎无日志可见（`open()` 不打日志）。本次实测仅主机一条连接，未出现双连接。
- 流量缩放：TS 无任何 setInterval；Director 通道每日 ≤3 条 allocate_agent 且 90% 天数不发；唯一按玩家数缩放的是对话通道和 `AgentMemory.save()` 每次对话后遍历写所有玩家桶（`agent-memory.ts:513-529`）。TS 无游戏时钟，"过中午"无对应机制。

## 3. 探查 B：C# 主机主线程阻塞审计（前三候选）

主线程入口集合与六大队列泵审计结论：**无 .Result/.Wait()/Thread.Sleep 落在主线程可达路径**（唯一每 tick 主线程同步 IO 是 TranscriptSink.Flush，`TranscriptSink.cs:131-152`，正常磁盘亚毫秒）；六队列全 ConcurrentQueue + while(TryDequeue) 全量排水，**死锁对 0 处**。

### 候选 1（活性已实证）：FOLLOW 跨图参照系错位缺陷族
- 跨图判定用**主机所在图**（`AgentNavigator.cs:198-213`：`npc.currentLocation != Game1.currentLocation`）；本地跟随目标用**对话发起玩家**坐标（`:418-429` `ResolveFollowTarget` = `LastDialoguePlayerId`，`:476` `FindWalkableTileNear(npc.currentLocation, playerTile, npc)`），**不校验目标与 NPC 同图**——另一张图的坐标被拿到本图解释。
- 无退出条件链：NoPath 熔断自复位（`MovementService.cs:336-342/762-774`，5 次即清零重来）；`UpdateLocalFollowing` 每 tick 重入；FOLLOW 受 `_dialogueFollowedAgents` 保护不被决策改出（`EventHandlerInitializer.cs:2033-2040`，任何来源进入 FOLLOW 都自动加保护 `:2785`）；旅行熔断只计异常（`AgentNavigator.cs:798/806-815`），成功 warp 清零。
- 单次 MoveTo 最多两遍同步 A*（PFC + CustomAStar，节点上限各 50000，`MovementService.cs:407-448`）。
- 3 人联机是触发前提：`LastDialoguePlayerId` 指向房客时错位才存在（单人/纯主机目标时两个参照系一致）。

### 候选 2：后台决策线程裸读 → Dictionary 损坏 → 主线程静默死循环
- `EventHandlerInitializer.cs:2518-2542` `MakeDecisionsAsync` 在 ThreadPool 上执行 `DecideByRules`（`:3192-3249`）——裸读 `Game1.getCharacterFromName`（NetList）、`location.Objects`、`friendshipData`；`AgentTickLoop.cs:115-116` 后台读普通 Dictionary `_consecutiveIdleDecisions` 而主线程同刻在写（`:122-162`）。
- 机理：Dictionary 扩容竞争 → 桶链成环 → `TryGetValue/Insert` 无限自旋——无异常、无日志、单线程死循环，完美吻合实机症状。
- 每 30s 决策批 + 对话结束/送礼/任务完成触发（`:2137/:2151/:2164`）——过午高频对话是放大器。竞态窗口极窄，无法主动复现。

### 候选 3：FriendshipSystem 共享普通 Dictionary 双线程写
- `FriendshipSystem.cs:19/22`（`_dailyTracker`/`_history` 普通 Dictionary）；主线程写 `ApplyDirectChange`（`:170-211`） vs 后台写 `ApplyWithExternalDeltaAsync`（`:91-166`，信号量挡不住主线程路径）。纯本 mod 游玩时后台入口是冷路径（DialogueManagementApi 是外部 API），TestMod 在场时升为热路径。

### 其他已核对
- 第 3 人加入路径（探查 C）：`MultiplayerEventRouter.cs:156-173` OnPeerConnected → `AgentSyncBroadcaster.SendFullSync`（`:93-119`，唯一 farmer 全遍历 `:284`，一次性）；无玩家加入触发的 TS 通知；`worldSnapshot` 只携带发起者单人（`WorldSnapshotBuilder.cs:40-102`）。
- **装模组客机的预启动风险**：`ModEntry.cs:150-233` GameLaunched（模式判定前）只要 `AutoStartServer && UseAgentServer`（默认 true）就预启动本地 TS 并 `KillExistingServerOnPort` 强杀 8765 端口任意进程（`ServerProcessManager.cs:651-713`）；唯一豁免是 `VALLEY_TEST_INSTANCE=farmhand`。**跨机无害（各杀各的 127.0.0.1），同机多实例才有端口互杀循环**。
- 无模组客机影响≈零：主机广播的未知 ModMessage 被原版客户端 switch 无 default 直接忽略（反编译 `Multiplayer.cs:1559-1734`）。

## 4. 容器复刻（soak）方法

- **拓扑**：valley-ts + valley-host + 3× valley-farmhand（compose 覆盖文件 `docker/docker-compose.soak.yml`；三房客共用 `VALLEY_TEST_INSTANCE=farmhand`，各自独立 data 卷使命令文件互不干扰）。
- **驱动**：`scripts/docker/run-soak.ps1`——`va_mp_host 3`（TestMod 扩展：循环补建 N 个木屋角色）→ 逐个错峰加入（顺带压"第 3 人加入崩"路径）→ `va_test_c3` 真实 LLM 对话（设置 `LastDialoguePlayerId`）→ `va_soak_follow` 强制 FOLLOW（与 E1/EXP 测试同款 `TrySetAgentState` API）→ `va_mp_goto` 三房客分图（SafeWarp 校验落点）→ 监控循环（看门狗文件/SMAPI Watchdog 行/容器状态/每 30s 线程 CPU 快照/每分钟 FOLLOW 重申+diag 游戏时间）。
- **仪器**：TestMod 主线程看门狗（5s 阈值，`VALLEY_WATCHDOG_MS=5000`；监控起始时间窗过滤加载期假阳性）；.NET 6 createdump 可用于容器内抓栈（本次未触发）。
- **TestMod 新增命令**（测试驱动，不改生产逻辑）：`va_mp_host [min_cabins]`、`va_mp_goto <map> [x y]`、`va_soak_follow <npc>`、`va_soak_diag`。

## 5. soak 结果（5+1 轮）

| 轮 | 场景 | 时间跨度 | 结果 |
|---|---|---|---|
| 1 | spread（Town/Mountain/Forest），FOLLOW=host 目标（C3 因 provider 大小写 bug 全灭退化） | 900→2050 | 无冻结，看门狗 0 捕获 |
| 2 | mine（房客1 真实 UndergroundMine1），同退化 | 900→2100 | 无冻结 |
| 3 | mine + 双 Agent（Haley+Alex），同退化 | 900→**2600** | 无冻结（触及日终时刻） |
| 4 | 探针门控 C3——provider bug 仍在，中途终止诊断 | — | — |
| 5 | **完整形态**：provider 修复后真实 LLM 对话落账（Haley 目标=跨图房客✓），mine + 双 Agent + 跨 2600 | 900→2600（卡住） | **无进程级冻结**，抓到活性缺陷族（下详） |
| 6 | 对照轮：同拓扑、3 房客加入、**无 Agent 无对话** | 900→2600（**同样卡住**） | **裁定"卡 2600"为测试装置工件**（§5.1-5） |

### 5.1 第 5 轮活性发现（候选 1 缺陷族实证）

以下全部来自主机 SMAPI 日志（artifacts：`.tmp/soak-mine-*/`）：

1. **跨图坐标混算**（正常游玩时段，游戏时间 ~1810-1950）：`travel(Travelling,Farm) loc=FarmHouse playerLoc=FarmHouse dist=61.1` —— FarmHouse 室内（~10×8 格）不可能有 61 格距离，距离在传送落地前的旧坐标空间计算。
2. **每 tick NoPathFound 无退出重试**（正常游玩时段）：`[AgentNavigator] Haley: follow MoveTo -> NoPathFound (target {22,20})` 连续每秒 60 条（12:48-12:53，FarmHouse 内不可达目标）——房客 Mountain 坐标被拿到 FarmHouse 解释。
3. **幽灵点行为缺陷**（正常游玩时段）：Mountain (22,20) 在 Farm 恰好可走 → NPC 走到错误地图上的"目标点"站住（认知与现实脱节的又一形态）。
4. warp 乒乓（Farm↔FarmHouse 200+ 次"arriving"）：发生在 2600 日终混乱期——见下条，随装置工件一并降级。
5. **时间卡死 2600 ≥20 分钟 = 测试装置工件，非模组缺陷**（对照轮裁定）：第 5/6 轮均卡 2600（有无 Agent 无区别）。机理：TestMod `ActivateFarmhand` 跳过角色定制流程 → 房客 `isCustomized=false` → 原版 2600 强制昏迷前置条件 `(IsMasterGame || player.isCustomized.Value)`（反编译 `Game1.cs:6453`）在房客身上永不满足 → 多人日终握手卡死。实机真实玩家（已定制）不受影响；第 5 轮的 warp 乒乓也是这个怪态（主人已昏迷回床、房客站着不动）的伴生现象，降级为"装置交互"，不计入模组缺陷。
6. 全程看门狗 0 捕获、主线程 CPU ≤30%（对照 llvmpipe 渲染线程各 ~20%）——**上述缺陷族在容器中是持续抖动而非 ≥5s 单 tick 停摆**。

### 5.2 复现失败的环境差异（诚实清单）

容器 ≠ 实机：Linux/.NET 6 vs Windows（**"未响应"判定 = Win32 消息泵 5s 饥饿，容器无此概念**）；llvmpipe 软渲染 vs 真实 GPU；docker bridge vs 真实网络（3 客户端跨网）；脚本化最小操作（**送礼 C2/交易菜单/聊天栏/持续玩家输入未覆盖**）vs 真实游玩；dev 构建+TestMod vs 发行包；25-35 分钟 vs 实机"玩了一段时间"。候选 2（竞态）本来就无法靠 soak 主动命中。

## 6. 根因判定（当前证据下的诚实结论）

**未定罪。** 证据状态：

| 假设 | 状态 |
|---|---|
| "每玩家一个导演" | **证伪**（误读，§2） |
| 第 3 人加入路径本身 | **大幅削弱**（审计轻量 + 15 次容器加入零异常） |
| TS 侧流量/定时机制 | **削弱**（无定时器、无游戏时钟、导演通道日 ≤3 条） |
| 候选 1：FOLLOW 跨图参照系错位 | **缺陷族活性实证**（坐标混算/NoPath 循环/幽灵点/warp 乒乓）；但容器中未升级为硬冻结。实机上叠加真实网络同步（每次 NPC warp = 全客户端广播）与消息泵判定，**仍是最可能的实机冻结构成成分，但缺最后一环证据** |
| 候选 2：后台线程 Dictionary 损坏 → 主线程静默自旋 | **无法排除**——症状吻合度最高（无异常/无日志/未响应），无法主动复现，容器时序差异可解释未命中 |
| 候选 3：FriendshipSystem 双线程写字典 | 冷路径（本 mod 纯游玩），排后 |
| 原版多人本身 | 用户已对照（原版不崩） |

**结论表述**：实机冻结的根因落在"模组 × 多人"交互面内；已实证存在一类多人才会发生的 FOLLOW 参照系缺陷（行为级，见 §5.1），它与实机场景（玩家分散跨图 + NPC 跟随）高度重合；但是否足以单独造成"无响应"未能在容器证明，后台线程字典损坏类静默死循环（候选 2）同样无法排除。**硬冻结的直接定罪证据只有实机主线程栈一条路。**

## 7. 副发现（真实缺陷，建议另开修复任务）

1. **`llm-provider.ts:425` provider switch 大小写敏感**：`"MiniMax"` → default 分支 → 无 baseURL → `api.openai.com`（CN 被墙）→ 全部对话 FALLBACK。实机多供应商 router 不受影响；**受影响面**：docker/E2E compose 直传 config 原值（`run-e2e.ps1` 从 `config.json` 读 `LlmProvider` 原样传给 compose）、TS CLI 直用。09-09 E2E C3 的"PASS"判据不区分 `source=LLM/fallback`，**历史 E2E 可能一直是 FALLBACK 假绿**（待查证）。修复方向：switch 前规范化小写。
2. **60s 主机超时 vs 67s TS 重试链错位**：慢 LLM/网络抖动 → fallback 迟到 → `[WS] Unhandled unsolicited dialogue_response` 丢弃 → 房客无响应（本 soak 前 4 轮 C3 FAIL 的机理）。修复方向：超时对齐或迟到回包补投。
3. **看门狗加载期假阳性**：3s 阈值在读档慢 tick 会误报（实测 3234ms）。建议默认 5000ms 或扩展首跳门控覆盖读档期。
4. **`handleDayStarted` 无按日期去重**（`protocol-adapter.ts:147-186`）：联机同日多条 day_started → 多轮 morningPlan（也是"多导演"误读的直接来源）。

## 8. 下一步取证方案（唯一确证路径）

**实机部署含 TestMod 的构建**（看门狗在 Windows 直接 P/Invoke dbghelp 落 .dmp + 伴随日志到 `Mods/ValleyAgent.TestMod/watchdog/`）：

1. 打包含 TestMod 的发行变体（或 dev 构建直拷），三台实机照常游玩（复现条件：3 人、跨图、过午、有 NPC 跟随）。
2. 冻结发生时看门狗自动落 dump（每停滞事件一次，保留 5 份）。
3. 分析：`dotnet-dump analyze <dmp>` → `clrstack -all` → 找主线程（game tick 线程）：
   - 栈在 `MovementService`/`PathFindController`/A* → 候选 1 定罪；
   - 栈在 `Dictionary.FindValue`/`Insert` 且对应后台线程同刻在写 → 候选 2 定罪；
   - 其他栈 → 新线索。
4. 同步核查实机 `ValleyAgent-error.log`/`ValleyAgent-server.log`（发行包已带落盘）。

## 9. 本任务产生的工件

| 工件 | 位置 |
|---|---|
| soak compose 覆盖（3 房客 + 看门狗 5s） | `docker/docker-compose.soak.yml` |
| soak 驱动脚本（探针门控 C3/对照轮/冻结取证自动化） | `scripts/docker/run-soak.ps1` |
| TestMod 测试驱动命令（va_mp_host N / va_mp_goto / va_soak_follow / va_soak_diag） | `src/ValleyAgent.TestMod/SoakCommands.cs`、`MultiplayerSetupCommands.cs` |
| soak artifacts（各轮 SMAPI 日志/线程快照） | `.tmp/soak-*/` |
| 第 5 轮卡死态线程快照 | `.tmp/round5-stuck-thread-cpu.txt` |
| 探查 A/B/C 完整报告 | 本会话（已浓缩入 §2/§3） |

---

# 附录：2026-09-10 修复批（用户指令"开始修复"，验证标准=虚拟环境由失败到成功）

## 修复清单（6 项）

| # | 缺陷 | 修复 | file:line |
|---|---|---|---|
| 1 | **FOLLOW 跨图参照系错位**（候选 1 主缺陷）：跟随目标在别的图时，其坐标被当本图坐标寻路 → 每 tick NoPathFound 风暴/幽灵点站桩 | `UpdateLocalFollowing` 入口加跨图守卫：目标图 ≠ NPC 图时停行、清除跟随意图、5s 节流 "standing by" 日志、原地等待（跨图旅行本就只跟主机位置） | `AgentNavigator.cs:422-452`（新增字典/常量 `:52-54`） |
| 2 | TS `createModel` 的 provider switch 区分大小写——`"MiniMax"` 落 default 分支无 baseURL 打 `api.openai.com`（CN 被墙）→ 全对话 FALLBACK | `resolveConfig` 入口统一 `toLowerCase()`（switch 与日志同时受益） | `ValleyAI/packages/core/src/llm-config.ts:31-35` |
| 3 | 主机对话 60s 超时 vs TS 3 次重试链 ≈67s：慢 LLM 时 fallback 回包迟到被当 unsolicited 丢弃，房客收不到 | 默认超时 60s → 120s | `PendingRequestTracker.cs:26-52` |
| 4 | 看门狗 3s 默认阈值在读档期慢 tick 误报（实测 3234ms 假阳性） | 默认阈值 3s → 5s（真冻结无限期，5s 不漏） | `MainThreadWatchdog.cs:62-66` |
| 5 | C3 判据不区分 fallback 与真实 LLM（"PASS" 假绿） | PASS 行携带 `source=`；自动化断言 `[C3] PASS (source=LLM)` | `MultiplayerTestCommands.cs:268-274`；`run-soak.ps1` Send-C3WithProbe |
| 6 | **TestMod 预存 bug**：`EnsureLoaded` 写死 `TestConfig.NpcName`，`va_test_c3 Alex` 实际对话的是 Haley（E2E 一直用 Haley 故从未暴露；soak 验证轮实抓：房客 2 的 "Alex" 命令被静默改发 Haley） | `EnsureLoaded` 增加 npcName 参数，C1/C2/C3/C4 四个调用点传 `args[0]` | `MultiplayerTestCommands.cs:282-296`（调用点 `:130/:190/:239/:413`） |

另：`run-soak.ps1` 加 UTF-8 BOM（PS 5.1 无 BOM 按 GBK 读，注释行尾 `。` 多字节吞换行符曾使 `if (-not $NoAgents) {` 被吞进注释导致 parse error）；驱动新增跨图守卫自动验证段（NoPathFound 计数 0 新增 + "standing by" 命中）与 `-NoAgents` 对照轮开关。

## 验证（虚拟环境由失败到成功）

| 判据 | 修复前（soak 第 1-4 轮） | 修复后（验证轮，mine+双 Agent） |
|---|---|---|
| C3 房客对话 | **全灭**（provider 大小写 → api.openai.com 21s×3 → 主机 60s 超时 → 迟到回包丢弃；RunC3 还会发错 NPC） | **Haley/Alex 双双第 1 次尝试 `source=LLM` 通过**（~6s 内响应） |
| FOLLOW 目标归属 | 错乱（Alex 命令实际对话 Haley，目标张冠李戴） | **Haley←矿洞玩家、Alex←Mountain 玩家，各归其位**（diag lastDialoguePlayerId 与玩家 id 精确对应） |
| 跨图 NoPathFound | **每秒 1 条风暴**（FarmHouse 内拿 Mountain 坐标寻路，无退出） | **0 新增**，两 NPC 均命中守卫日志：`standing by ('UndergroundMine1'/'Mountain' vs NPC 'FarmHouse')` |
| 看门狗 | 布防 5s 内 3234ms 假阳性（3s 阈值） | **零捕获零误报**（5s 阈值） |
| 进程冻结 | （未复现，但缺陷活性在） | 无冻结，游戏时间 900→2030 全程过午 |

TS 验证门：`bun test` 640 pass / 0 fail（含新增 provider 小写回归用例 26/26）；`tsc --noEmit` 干净；`check:protocol` PASS。C# 三项目编译 0 警告 0 错误。

## 未修项（保持"只修有实证的"）

- 候选 2/3（后台线程字典损坏）无活性实证，未动——留待实机带 TestMod 抓 dmp 定罪。
- `handleDayStarted` 无按日期去重（"多导演"误读来源）为 TS 行为缺陷，非本批卡死主题，未动。
- 卡 2600 日终 = 测试装置工件（`ActivateFarmhand` 跳过角色定制 → `isCustomized=false` → 原版强制昏迷条件永不满足），如需 soak 跨日需先给 TestMod 房客补定制流程——未动。

---

# 附录 B：猜想台账与终验流程（2026-09-10 用户要求：程序制作不猜，分清猜想与实际）

> 规则：一切未经验证的机理推断显式标注为**猜想**，与已验证事实分列。每条猜想必须有推理链、支撑证据、反证条件、终验方案；修复只基于已验证事实。台账是本任务剩余工作的唯一依据。

## 状态分级

- **已验证事实**：多轮 soak 日志 + 源码/对照实验双重证据。
- **未验证猜想**：有推理链与部分证据，未达到定罪标准。
- **已排除**：对照实验或源码裁定。

## 台账

| # | 条目 | 状态 | 证据要点 | 终验方案（判据 → 结论） |
|---|---|---|---|---|
| F1 | "每玩家一个导演" | **已排除** | TS Director 进程级单例（`server.ts:171`）；唯一 playerId-map 是 NPC 记忆桶；误读源=day_started 无去重 + 完整 prompt 入日志 | 源码裁定，无需再验 |
| F2 | FOLLOW 跨图参照系错位（行为级） | **已验证事实** | 修复前每秒 NoPathFound 风暴 + 幽灵点（正常游玩时段日志）；修复后 35s 窗 0 新增 + 守卫命中（对照实验） | 已闭环 |
| **U1** | **该缺陷族在实机可升级为整机"未响应"** | **未验证猜想** | 推理链：大图 × 每 60 tick 双 A*（各 5 万节点）× 多 NPC 叠加 + 实机真实网络同步（每次 warp 全客户端广播）+ Win32 消息泵 5s 饥饿判定；容器不冻结（Linux 无此判定、llvmpipe 时序不同、操作密度低） | 实机带看门狗复现（§终验流程）：dmp 主线程栈在 A*/MovementService → **定罪**；修复后实机不再崩（修前崩/修后不崩对照）→ **定罪**；多次复现 dmp 均不在 → **高置信排除** |
| **U2** | **后台决策线程裸读 Game1/普通 Dictionary → 桶链损坏 → 主线程静默无限自旋** | **未验证猜想** | 推理链：Dictionary 扩容竞争成环（`AgentTickLoop.cs:115-116` 后台读 vs 主线程写 `:122-162`）→ 主线程随后触碰即自旋，无异常无日志——与实机症状吻合度最高；竞态窄窗口，容器时序未命中 | 同上 dmp：栈在 `Dictionary.FindValue/Insert/Resize` 且对应后台线程在写 → **定罪**；多次 dmp 均不在 → 高置信排除 |
| **U3** | FriendshipSystem 双线程写普通 Dictionary 同机理 | **未验证猜想**（低优先级） | 后台写入口（`DialogueManagementApi` 外部 API 路径）在本 mod 纯游玩下是冷路径 | 可容器内主动加压（TestMod 并发送礼 + API 调用）观察；或实机 dmp 顺带裁决 |
| F3 | 卡 2600 日终卡死 | **已排除（装置工件）** | 对照轮：无 Agent 无对话同样卡；源码：房客 `isCustomized=false` → 原版 2600 强制昏迷前置条件永不满足 | 已闭环（需跨日 soak 先给 TestMod 房客补定制流程） |
| F4 | provider 大小写 → 全对话 FALLBACK | **已验证事实** | 容器内应用 provider 代码复现失败（21s×3）vs 同容器裸 fetch 秒通；修复后 C3 source=LLM 一次通过 | 已闭环 |
| F5 | 60s 超时 vs 67s 重试链错位丢回包 | **已验证事实** | 日志时序（60s TimeoutException → 67s 迟到 dialogue_response 被丢）；修复后一次通过 | 已闭环 |
| F6 | 第 3 人加入路径是突发重负载 | **已排除** | 审计：加入=1 行日志+一次 FullSync+每秒广播多一份，无平方级工作；18 次容器加入零异常 | 源码+实证裁定，无需再验 |

## 终验流程（唯一未决项 = U1/U2/U3 的实机裁决）

**分工**：我出构建与判据，用户实机跑（副屏，不锁鼠标）。步骤：

1. **构建**：打包含 TestMod 的发行变体（Windows 看门狗自动落 dmp 到 `Mods/ValleyAgent.TestMod/watchdog/`，5s 阈值，每停滞事件一份，留 5 份）。
2. **复刻条件**（对齐实机两次崩的画像）：3 人联机、玩家分散跨图（含矿洞）、游玩过中午（1200 后继续 20+ 分钟）、有 NPC 被对话触发跟随。
3. **采集**：冻结时看门狗自动落 dmp + 伴随日志（pid/uptime/在线人数）；同步收集 `ValleyAgent-error.log`、`ValleyAgent-server.log`。
4. **判据表**（每份 dmp 独立裁决，多份取并集）：
   - 主线程栈在 `PathFindController`/`CustomAStar`/`MovementService` → **U1 定罪** → 修大图寻路负载/重试上限（新任务）；
   - 主线程栈在 `Dictionary.FindValue/Insert/Resize` 且某后台线程同步在写同一字典 → **U2 定罪** → 修决策线程数据源纪律（新任务）；
   - 栈在 FriendshipSystem 路径 → **U3 定罪**；
   - 其他栈 → **新猜想入台账**，回到第 1 步循环，不凭猜动手；
   - 修复后实机不复现 → 对照验证完成，**U1 定罪**（行为级缺陷即实机根因）。
5. **输出格式**：现象（事实）与推断（猜想）分栏记录，逐条标状态，不混写。

### 终验流程修订（2026-09-11 日志仪器批落地后）

本节步骤 1 的"打包含 TestMod 的发行变体"**作废**：看门狗已生产化（`Infrastructure/MainThreadWatchdog.cs`），随标准发行包分发，实机终验直接用 `package-distribution.ps1` 的常规产物即可。采集物更新：

| 证据 | 位置 | 触发条件 |
|---|---|---|
| 主线程冻结 dmp + 伴随日志（timeOfDay/联机上下文/线程概览/运行中后台操作） | `Mods/ValleyAgent/watchdog/freeze-*.{dmp,log}` | 心跳停滞 >5s（`VALLEY_WATCHDOG_MS`） |
| 后台操作停滞 dmp（候选 2/3 猎捕：决策批 `decision-batch#N`、好感度 `friendship-external-delta#N`） | `Mods/ValleyAgent/watchdog/stuck-*.{dmp,log}` | 操作运行 >60s（`VALLEY_STUCKOP_MS`） |
| 看门狗/队列/慢寻路告警 | `ValleyAgent-error.log` + SMAPI 日志 | 队列深度 ≥100、WS 命令滞留 >2s、OnUpdateTicked 排水段 >100ms、MoveTo >500ms |
| TS 服务器完整输出 | `ValleyAgent-server.log` | 发行包 `ServerConsoleWindow=true` 下由 TS 端 `log-tee.ts` 自落盘（`VALLEY_SERVER_LOGFILE`），窗口与文件兼得 |
| WS 连接顶替/断连丢弃、day_started 重复（多导演误读观测面） | `ValleyAgent-server.log` | 第二连接出现 / 无活跃连接发消息 / 联机同日多条 day_started |

判据表（步骤 4）不变，新增一条：**若 freeze dmp 缺席但 stuck-*.dmp 出现** → 主线程未冻结、后台线程死循环坐实 → U2/U3 方向定罪（栈判读同步骤 4）。

验收（2026-09-11）：C# 编译 0 警告，单测 602/603（唯一失败 `ThinClient_MustWireChatBarRouter` 为改动前即红的 Phase 3 待办特征测试，git stash 对照确认）；TS `bun test packages/stardew` 505 过 0 失败、`tsc --noEmit` 干净、`check:protocol` PASS。
