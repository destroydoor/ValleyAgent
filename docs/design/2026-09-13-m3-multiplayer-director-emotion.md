# M3 多玩家化：情绪 per-player / 房客聊天栏·交易菜单 / 玩家画像与导演 per-player

> **Created:** 2026-09-13
> **议题:** issue #4「联机多玩家化遗留限制（M3 范围）」
> **前置:** `docs/design/2026-08-16-multiplayer-boundary-analysis.md`（M1 经济正确性）、
> `docs/design/2026-08-17-ledger-fixes-and-m2-multiplayer-context.md`（M2 多玩家上下文）
> **状态:** 已落地（TS 全绿；C# 仅语法级校验，实机联机验证待做 —— 见 §5）

---

## 0. 起点：issue #4 的三条待办

| # | 限制 | 代码证据（落地前） |
|---|---|---|
| 1 | 情绪引擎 NPC 世界级，非 per-player | `server/packages/stardew/src/emotion-engine.ts` — `Map<npcName, EmotionState>` |
| 2 | 房客聊天栏 / 送礼·交易菜单依赖主机端组件 | `ThinClientCapabilityMatrixTests.ThinClient_MustWireChatBarRouter`（显式豁免的已知红）；`GiftTradeMenuLogic.ShouldOfferGiftTradeMenu(isThinClient, …) => !isThinClient && …` |
| 3 | 导演 / 玩家画像单玩家视角 | `player-profile-store.ts` — `CHECK (id = 1)` 单行单玩家 |

三项一起做的原因：它们都是"联机下 A 的体验泄漏到 B"这一族缺陷的不同面
（情绪串味 / 房客少功能 / 画像只有一份）。

---

## 1. 情绪 per-player

### 数据模型：世界桶 + 玩家桶（与 M2 记忆拆分同范式）

```
情绪状态键 = (npcName, playerId)
  playerId 缺省 → 世界桶：state_changed（travel_failed / evicted / task_completed）
                          Director 世界级 mood
                          → 对所有玩家可见
  playerId 有值 → 玩家桶：action_result（可归属到发起玩家的工具成败）
                          Director 定向 mood
                          → 只对发起玩家可见
```

### 关键决策（不是"玩家桶恒优先"）

解析规则是**最近写入胜出**（内部单调 `seq`），不是"有玩家桶就用玩家桶"。
理由：玩家桶恒优先会让某玩家的一次性情绪**永久遮蔽**后续的世界级情绪，直到换日——
NPC 完成了一件大事（世界事件 → Happy），刚把它惹毛的那个玩家却永远看不到。

| 时刻 | 事件 | A 视角 | B 视角 |
|---|---|---|---|
| t1 | 世界：task_completed | Happy | Happy |
| t2 | A：chop_tree 失败 | Sad | Happy |
| t3 | 世界：evicted | Sad（世界事件更晚） | Sad |

### 归属来源：callId → playerId（不动协议）

`action_result` 消息不带 playerId（协议 `messages.json` 未定义该字段）。
`handleDialogue` 在发出响应前把 `callId → playerId` 存进 `pendingActionPlayers`
（与既有的 `pendingGoals` 同机制），`handleActionResult` 消费后即时删除；
换日 `handleDayStarted` 与情绪一起清空（防 callId 永远等不到回执时无界增长）。
**协议零改动**，旧 C# 客户端照常工作（无 playerId → 世界桶）。

### 触点

- `src/emotion-engine.ts`：双键存储 + `seq` + `applyEvent/applyMood/current/resetDay/resetAll` 全加可选 playerId
- `src/protocol-adapter.ts`：`pendingActionPlayers` 暂存表 + 响应情绪按 `req.playerId` 取
- 单机行为完全等价现状（playerId 缺省 → 世界桶）

---

## 2. 房客聊天栏 / 送礼·交易菜单

### 2.1 聊天栏：房客版路由器

此前 `ChatBarRouter` 只在主机 `EventHandlerInitializer` 初始化，房客侧
`ChatBoxInputPatch` 捕获了聊天输入却**无人路由 → 静默丢弃**。

新增 `ChatBarRouter.InitializeFarmhand(monitor, IDialogueTransport, AgentRemoteRenderer, ModConfig)`，
房客形态与主机形态只有三处差异：

| 关注点 | 主机 | 房客 |
|---|---|---|
| 在场候选 | 全部村民（`ChatRouteResolver` 再消歧） | 只取主机广播的 Agent 名单（`RemoteRenderer.GetRemoteState`） |
| 请求通道 | `IAgentServerProvider`（本地 LLM） | `IDialogueTransport`（转发主机） |
| 动作执行 | `DispatchDialogueActions` | **跳过**（主机已执行并广播，房客重复执行会双份） |
| 远程喊话 | 支持（有 AgentService 全员候选） | 静默跳过（无全员名单） |

`ModEntry.InitializeThinClientMode` 接线两件事，缺一不可：
初始化路由器 **+** 在 tick 排水里调用 `ChatBarRouter.ProcessPendingReplies()`
（后台线程入队、主线程消费——缺排水就是"主机回了包但房客什么都不显示"）；
回标题时 `ChatBarRouter.Reset()` 清状态（实例保留，下次 SaveLoaded 无需重建）。

### 2.2 送礼·交易菜单：去掉 isThinClient 过滤

`ShouldOfferGiftTradeMenu(isThinClient, isGiftableHeldItem) => !isThinClient && …`
的理由是"菜单注入交易意图依赖本地对话状态"。复核后该理由不成立：

- 送礼分支：`npc.tryToReceiveActiveObject(farmer)` → `NPCGiftPatch` 走
  `FarmhandGiftTransport`（**已接通**）；
- 交易分支：`OpenAgentDialogue` + `QueueExternalInput(交易意图)` → `SubmitInput` →
  `FarmhandDialogueTransport`（**已接通**）。

故判定改为 `isGiftableHeldItem`（形参从 2 个减到 1 个——留一个被忽略的 `isThinClient`
是陷阱，后来的调用方会以为它还在过滤）。同时修掉交易分支里
`OpenAgentDialogue(npc, false)` 的硬编码（房客需要 `isThinClient=true` 才不会去摸本地
`AgentService`）。

---

## 3. 玩家画像 + 导演 per-player

### 3.1 画像分键 + 惰性认领

`player_profile` 表：`id INTEGER CHECK (id = 1)` → `player_id TEXT PRIMARY KEY`。
`init()` 内做旧库迁移（`PRAGMA table_info` 探列 → 旧单行数据写入 `_legacy` 键 → 重建表），
真实 playerId 首次访问且 `_legacy` 有数据时**认领**（M2 记忆拆分同范式，单机升级无感）。

`PlayerProfileManager` 全部方法加可选 playerId（缺省 → legacy 键，行为等价 M3 前）；
`summarizeForDirector(playerId)` 用 `loadOrClaim` 取摘要。

### 3.2 玩家名录（内存观察式）

`PlayerDirectory`：TS 侧此前**没有任何"当前有哪些玩家"的权威名单**——
`day_started` 不带玩家列表，`GameContext.playerState` 是主机状态。
唯一稳定携带玩家身份的是 `dialogue` 请求的 `playerId`，故做观察式名录
（重新观察刷新显示名并移到队尾，容量 8，超出淘汰最久未见者；`_legacy` 与空 ID 不登记）。

**已知限制（明确记录）**：不持久化 → TS 重启后名录为空，当天 morningPlan 退化为
单玩家编排，直到有玩家开口说话。画像本身持久化，不丢数据。

### 3.3 导演 per-player

`Director.morningPlan({ playerIds })`：

- 每个玩家**各一次 LLM 调用**，prompt 用该玩家的画像摘要（不串味）；
- 产出 beat 打 `context.playerId`（可选字段，旧数据形状不变）；
- **跨玩家不重复安排同一 NPC**（`reserved` 集合跨迭代传递；节奏预算按世界算不按玩家算）；
- **全局 beat 预算不随玩家数放大**：`maxBeatsPerDay` 是总数，用尽即停；
  玩家数上限由新增的 `maxPlayersPerPlan`（默认 4）钉住 LLM 调用次数；
- `director_runs` 增量加 `player_id` 列（best-effort `ALTER TABLE`，重复加列吞异常），
  每个玩家一条留痕，可回答"这条 beat 当时是为谁编排的"。

未传 playerIds（单机 / 名录为空 / 只有 `_legacy`）→ 走原单玩家路径，一次 LLM 调用，
不加 `context.playerId`，行为等价 M3 前。

---

## 4. 测试

| 文件 | 覆盖 |
|---|---|
| `tests/m3-multiplayer.test.ts`（新增 13 例） | 情绪双键隔离 / 世界桶共享 / 最近写入胜出 / resetAll 清玩家桶 / per-player mood / **adapter 层 callId 归属**（A 的失败情绪不出现在 B 的 dialogue_response）/ 画像分键 / legacy 认领 / 摘要不串味 / 名录去重与容量 / 导演每玩家一次 LLM + beat.playerId + 预算不放大 + 同 NPC 不重复 / director_runs.player_id 旧库重开 |
| `ThinClientCapabilityMatrixTests`（C#，改） | 从"源码里提过 ChatBarRouter 这名字"升级为"初始化 + 回复排水 + 补丁接 monitor"三处接线都在位；新增房客转发形态审计 |
| `GiftTradeMenuLogicTests`（C#，改） | 菜单判定与模式解耦；用反射钉住"只剩一个形参"（不再有 isThinClient 过滤） |

---

## 5. 验证与未验证（重要）

**已验证（本机，2026-09-13）**

- `bun test`：`server` 工作区 **518 通过 / 0 失败**（原 505 + 新增 13）
- `bun run typecheck`：通过（strict + `noUnusedLocals` + `exactOptionalPropertyTypes`）
- `bun run check:protocol`：通过（协议未改动）
- `node scripts/check-privacy.mjs`、C# 测试防作弊扫描：通过
- C# 改动：`tree-sitter` 语法级解析全部通过（**仅语法层**）

**未验证（必须靠实机，不能假装做过）**

- C# 侧**没有编译验证**：单测工程经 `ModBuildConfig` 引用游戏 dll（`$(GamePath)\smapi-internal`），
  游戏文件有版权不能进沙箱/CI，本地 `dotnet build` 无法执行。
  房客聊天栏与送礼·交易菜单的**运行时行为**需由 `scripts/verify-all.ps1`
  （含 C# build/test）+ `scripts/test/run_farmhand_e2e.ps1` 在有游戏的机器上验收。
- 情绪 per-player / 导演 per-player 的**联机实机效果**：建议按
  `docs/plan/2026-08-19-docker-multiplayer-e2e-plan.md` 起双实例复现
  （A 让 NPC 砍树失败 → B 对话不应承接 Sad；两个玩家各说一句话后换日，
  director_runs 应有两条带不同 player_id 的留痕）。
