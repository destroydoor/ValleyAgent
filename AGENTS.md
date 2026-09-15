# ValleyAgent — 项目指南（AI 助手版）

> **Last updated:** 2026-09-14（架构漂移审计收割：C# ~5,600 行死子系统 + TS react-guard 删除；**旧叙事 Director 砍除裁决**——morningPlan/beat 线产出无人消费，director.ts/beat-store/runBeat/BEAT 模板/PlayerDirectory 已删，协议 10 条 planned 死 schema 清理 + consolidate_day 降级 planned；C# 工具层与 directorContext 推送保留待未来造脑；审计底稿原在 arena 分支 `docs/plan/2026-09-12-architecture-drift-audit.md`，**未随仓交付**——2026-09-15 偏移度审查确认三处引用悬空，该次审计的可追溯性目前已断档，见 `docs/plan/2026-09-15-doc-code-drift-audit.md` §1 A9）
> **卡死排查结论**：`docs/plan/2026-09-10-host-freeze-root-cause.md`（"每玩家一导演"=误读；6 轮 soak 无进程级冻结；FOLLOW 跨图缺陷族行为级实证并已修；U1/U2/U3 猜想台账与实机终验流程见附录 B）
> **当前执行依据**：`docs/plan/2026-08-05-three-tier-architecture-execution-plan.md`
> **架构修订设计**：`docs/design/2026-08-15-ts-ledger-reflex-architecture.md`（经济账本迁 TS + C# 反射执行，**四步全部完成（2026-08-15）**：adjust 执行器 + TS 账本 + 经济工具同步编排 + TS 情绪引擎 + 断线对账）
> **联机分析**：`docs/design/2026-08-16-multiplayer-boundary-analysis.md`（多人联机异常边界与约束；**M1 经济正确性已完成（2026-08-16）**：playerId 贯穿协议/执行器/好感度/FOLLOW，P0 主线程 dispatch 修复，BUSY 灰字；**M2 多玩家上下文已完成（2026-08-17）**：记忆拆世界桶/玩家桶 + per-player 好感/对话历史 + prompt 玩家化 + 工具记忆路由 + 迁移；**M3 已落地（2026-09-13）**：情绪 per-player 双键 + 房客聊天栏/送礼·交易菜单 + 玩家画像与导演 per-player，见 `docs/design/2026-09-13-m3-multiplayer-director-emotion.md`（**验收口径以该文档 §5 为准：TS 全绿；C# 仅语法级校验，实机联机验证待做**）
> **基础架构设计**：`docs/design/2026-08-05-three-tier-architecture-redesign.md`
> **旧版完整参考**（架构细节、模块矩阵、常量表）：`docs/archive/AGENTS-2026-08-02-v4.3-full.md`

---

## 1. 愿景与核心目标

原版 Stardew Valley 的 NPC 是程序化木偶。ValleyAgent 要让 NPC 变成**活的虚拟生命**：有自主行为、有钱有物、能交易、能被委托、能和玩家发生剧本外的事件。

| 优先级 | 目标 | 说明 |
|-------|------|------|
| P0 | **真实性格** | 所有言行符合角色设定，NPC 不是机器 |
| P0 | **AI 对话** | 每次对话独一无二，对话内容影响 NPC 行为 |
| P0 | **职责隔离** | NPC Agent 只做角色扮演，永远不知道"导演"存在；Director 只编排和改状态 |
| P1 | **共同冒险** | 一起战斗、收菜、挖矿（set_goal + C# GoalExecutor，零 LLM 循环） |
| P1 | **情感连接** | 对话影响好感度，NPC 记住玩家说过的话 |
| P1 | **社会存在** | NPC 是镇上的居民：钱包/背包/交易/雇佣、行动有节奏 |
| P1 | **导演编排** | Director 编故事、改 NPC 状态、创造相遇机会、编排 NPC 间互动 |
| P2 | **仪式感** | 做事有头有尾，送礼有完整流程 |
| P3 | **多 Agent** | 暂不实现 |

## 2. 设计哲学

1. **职责隔离** — NPC Agent 只做角色扮演，永远不知道"导演"存在；Director 只编排和改状态，不进入 NPC 的 LLM 上下文
2. **Token 经济** — 不做 LLM 循环；NPC 离屏任务由 Director 改数据，不实时执行；只在"设目标 + 汇报"或"对话"时消耗 token
3. **状态可见** — NPC 的钱包/背包/心情/位置/近期事件以 L2 状态摘要形式**强制注入** prompt，不依赖 RAG 检索
4. **记忆干净** — L1 长期记忆库只存"对玩家有意义、需要跨场景长期记住的事"；日常小事只更新 L2 摘要，不污染记忆库
5. **Director 单向控制** — Director → C# 状态层 → NPC Agent，单向流动；NPC 不审批、不协调、不感知 Director
6. **账本与叙事在 TS，反射与执行在 C#** — LLM/RAG/决策/对话/**经济账本（权威）**/**情绪引擎（确定性代码）**全在 TS Agent Server；C# 执行游戏操作、持执行镜像、做**物理校验**（玩家真实余额/背包空间/物品存在性，运行时对 `Game1.player` 实时校验，不信 worldSnapshot）与**生存反射**（低血量逃跑/卡住检测/战斗目标）。判据：**能在断连时还必须工作的逻辑留 C#；账本和叙事永远在 TS；C# 的每一次余额/物品变更都必须由 TS 指令（execute_adjust）驱动并以回执（adjust_result）确认**（2026-08-15 修订；**四步迁移已于 2026-08-15 全部落地**，见 §3.3）
7. **性能优先** — 不卡，Token 可控；路由/判定用确定性规则，智能只花在歧义上
8. **AI 的决定必须可追溯** — 程序状态、NPC 思考、导演思考全量留痕，事后能回答"它当时为什么这么想"
9. **认知不得脱离现实** — 每次状态/资产变动必须有回执，NPC 对现实的认知与游戏状态保持一致（海莉事件是总教训）
10. **猜想与实际分离**（2026-09-10 用户定规矩）— 程序制作不猜：一切未经验证的机理推断必须显式标注为**猜想**，与已验证事实分列；每条猜想要有推理链、支撑证据、反证条件、**终验方案**（复现/验证步骤 + 判据）；修复只基于已验证事实，猜想不落地为结论、不直接动手修。反面教训（2026-09-10 卡死排查）："每玩家一个导演"（日志误读，证伪）、"卡 2600 是对话框工件"（初猜，对照轮才裁定）、"provider 大小写"（先猜后实证）——猜与实际混写，一步错步步错。台账范式见 `docs/plan/2026-09-10-host-freeze-root-cause.md` 附录 B

### 2.1 我们不做什么

- **不做 NPC 自主动机循环** — NPC 不自己决定"今天我要做什么"。没有导演 beat 时，NPC 要么休眠走原版，要么 spark 随机激活做点小事，要么被玩家对话触发响应
- **不做 LLM 状态轮询** — 不让 LLM 反复检查"我砍了几棵树了"。用 C# GoalExecutor 确定性判断终止条件
- **不做 NPC ↔ Director 双向沟通** — NPC 不审批 Director 提案，不向 Director 汇报，不接收 Director 的元层指令。Director 只改 NPC 的状态数据
- **不做玩家不在场的实时模拟** — 玩家委托 NPC 砍树然后离开，NPC 不会真的去砍。Director 直接改数据，玩家回来对话时 NPC 从 L2 摘要自然感知
- **不做 C# 端经济决策/记账（2026-08-15 规划并已落地）** — C# 不定价、不记权威账、不做交易语义判断；只做物理校验和 adjust 原语执行。正则关键词记忆分析（DialogueMemoryAnalyzer）、机械情绪推导（EmotionAnalyzer）属"假 AI"，已于 2026-08-15 删除

## 3. 架构一页纸（三层架构）

```
┌─────────────────────────────────────────────────────────────┐
│ Director（导演智能体，TS 端）——规划中，尚未接线             │
│ 旧叙事线（morningPlan/beat/runBeat）2026-09-14 已砍除；     │
│ TS 大脑待造，C# 工具层与 directorContext 推送已就绪         │
│ 工具：set_npc_position/inventory/money/mood/recent_events/  │
│       working_on + spawn_beat + spawn_group_beat            │
│ 不做：不调 NPC LLM、不进 NPC 上下文、不审批                 │
└────────────────┬────────────────────────────────────────────┘
                 │ 单向：调 C# 工具改状态数据
                 ▼
┌─────────────────────────────────────────────────────────────┐
│ C# 状态层（零 LLM）                                         │
│ AgentBrain: L2 状态黑板（位置/心情/近期事件/工作；情绪记忆  │
│   为 TS 推送镜像。钱包/背包账本迁 TS 后不再存于 C#）        │
│ AgentInventory: 执行镜像（权威账本在 TS，2026-08-15 落地）  │
│ BeatStore: 当前 beat 场景描述（无生产者，随工具脑恢复）     │
│ GoalExecutor: 执行态后台跑（chop_tree/mine/water/fight）    │
│ 生存反射: 低血量逃跑/卡住检测/战斗目标（精简自规则引擎）    │
│ adjust 执行器: 物理校验+原子批+指令结果日志（已落地）       │
│ 断线 Outbox: 事件缓存+重连补发（2026-08-15 已落地）         │
│ PlayerActionTracker: 玩家行为采集器                         │
│ DirectorContextBuilder: 拼装 Director 上下文                │
│ WorldSnapshotBuilder: 拼装 worldSnapshot 给 TS              │
└────────────────┬────────────────────────────────────────────┘
                 │ 单向：worldSnapshot 拼装
                 ▼
┌─────────────────────────────────────────────────────────────┐
│ NPC Agent（角色扮演智能体，TS 端）                          │
│ 上下文：人设 + L2 状态摘要 + L1 长期记忆 + 玩家对话历史     │
│         + 当前 beat 场景（第三人称描述，不提"导演"）         │
│ 职责：纯角色扮演，响应玩家 + 可选 set_goal 自主做事         │
│ 工具：speak/emote/give_item/trade/give_gift/                │
│       set_goal/set_state/remember/forget/get_info/...       │
│ 不做：不知道导演存在、不审批、不协调、不感知元层            │
└─────────────────────────────────────────────────────────────┘
```

**关键原则**：NPC Agent 和 Director 之间**永远没有直接消息往来**。Director → C# 状态层 → NPC Agent，单向流动。

> 2026-09-14 起 `AgentAllocationManager` 语义是**并发身体池**：管理"谁当前持有身体（状态机/GoalExecutor/执行动作能力）"，受 [Min,Max] 并发上限约束、按需 ForceAllocate、空闲回收；不再是"这个 NPC 是不是 AI NPC"的身份判定。

### 3.1 NPC 状态光谱

> **分配语义变更（2026-09-14，issue #9 PR2）**：分配从"身份"改为"身体"——对话**不需要**分配（谁被聊到谁激活，房客与主机一致），只有**身体**占池位：按需 ForceAllocate（对话身体类动作 / 导演 `set_npc_position` / allocate_agent）+ 空闲回收（对话结束释放 override、TimeChanged 周期淘汰、OnAgentDeallocated 常驻拆除）。设计：`docs/design/2026-09-13-agent-body-refactor.md`。

| 状态 | 行为 | LLM 消耗 | 进入条件 |
|---|---|---|---|
| **休眠** | 走原版路径，无 AgentBrain | 零 | 默认 |
| **有限自主** | 可对话/交易/送礼/移动；spark 随机激活 | 中（对话或 spark 命中时） | spark 5% / 玩家对话 / 被 `allocate_agent` 建身体（须先有身体，当前无生产者） |
| **执行态** | C# GoalExecutor 后台跑 | 零（纯 C#）；完成时 1 次 LLM 汇报 | set_goal 工具调用 |

### 3.2 记忆与状态分层

| 层 | 存什么 | 注入方式 |
|---|---|---|
| **L1 长期记忆** | 玩家-NPC 重要互动、NPC 间重大事件、玩家说过的有长期影响的话 | 显著性排序检索命中才注入（无向量嵌入，审计 M-5） |
| **L2 状态摘要** | 钱包/背包/位置/心情/近期事件/工作标记/欠款 | **每次对话强制注入** prompt 开头 |
| **L3 场景剧本（休眠）** | 当前 beat 场景描述 | 叙事线已砍（2026-09-14），但 **C# 通路保留**：`BeatStore` → `WorldSnapshotBuilder` 仍会把活跃 beat 的 sceneDesc 注入 prompt；当前无生产者，等工具型 Director 造脑接入后自动复活（见 §3.5） |

L2 todayEvents 保留近 3 天，每天 day_started 清理 3 天前的。判定标准：这件事是否需要跨场景、跨日长期检索？是 → L1；否 → 只 L2。

### 3.3 Wire 协议

单一事实源：`server/protocol/messages.json`，check:protocol 追踪。active 的有 `hello` / `ping` / `dialogue`（唯一 LLM 触发）/ `dialogue_response` / `action_result` / `state_changed` / `day_started` / `allocate_agent` / `route_shout_response`（`route_shout` 为 orphan_route 预留）；`consolidate_day` 2026-09-14 降级 planned（记忆日结从未实现，两端代码已删）。**四步全部落地（2026-08-15）**：`execute_adjust`（TS→C# 原子批指令，带 instructionId，orphan_route）/ `adjust_result`（C#→TS 回执：每步成败+失败码+新余额，active）/ `reconnect_sync`（C#→TS 重连对账：outbox 补发 + active agent 名单，active）。步骤 2：trade/give_item/give_gift/receive_payment 由 TS 同步编排（账本校验→execute_adjust→回执），不再发 C# 执行；C# 还价单结算链已删除；求购（E3-5）保留 C# 生成，命中时拒绝送礼交接并提示走对话议价。步骤 3：TS 情绪引擎（确定性零 LLM，Director set_npc_mood 覆盖权），C# EmotionAnalyzer/DialogueMemoryAnalyzer 删除，RuleBasedDecisionEngine 瘦身为生存反射。步骤 4：C# 重连发 reconnect_sync（WebSocketClient.OnReconnected），TS 对账 in-flight adjust pending（凭 instructionId 重发，幂等缓存闭环）。旧 Python 时代的 `decision`/`friendship_eval` 死管道 schema 与 2026-09-14 清理的 10 条 planned 死 schema（beat_plan/beat_start/beat_end/emotion_sync/memory_sync/decision/gift_eval/rag_query/state_sync/day_end）均已从 messages.json 删除。

### 3.4 NPC Agent 工具集

speak / emote / give_item / give_gift / **trade**（新增，阶段 1）/ **set_goal**（新增，阶段 2）/ **chop_tree** / set_state / show_dialogue / remember / forget / get_info / accept_job / receive_payment（+evaluate_friendship，no-op 记录型）——共 **15 个 LLM 可见工具**（以 `server/packages/stardew/src/stardew-tools.ts` 注册项为准）。worldSnapshot 同时携带玩家侧（location/inventory/playerMoney/PlayerHeldItem）与 NPC 侧（npcLocation/npcMoney/npcInventory/npcMood/npcRecentEvents/npcWorkingOn）数据，NPC 认知以 NPC 侧字段为准。

### 3.5 Director 工具集（阶段 3 落地 = C# 工具层；TS 大脑待造）

set_npc_position / set_npc_inventory / set_npc_money / set_npc_mood / set_npc_recent_events / set_npc_working_on / spawn_beat / spawn_group_beat / inject_memory。Director **没有** speak/emote/give_item/trade/set_goal —— 这些是 NPC Agent 的角色扮演工具。

**2026-09-14 裁决**：旧叙事 Director（director.ts morningPlan→beat→allocate_agent）因产出无人消费（runBeat 从未接入生产、beat 唯一副作用是保活占池）已整体砍除；C# 的 9 个 DirectorTools + `director_command` 通道 + DirectorContextBuilder 每日推送**保留**，作为未来"工具型 Director 造脑"的就绪层（B4 的按需建身体逻辑同样保留）。

**就绪层的两个已登记缺口（2026-09-15 偏移审查 §3 实证，未修，等造脑时一并处理）**：①`ServerProcessManager.cs:291,301` 仍向 TS 透传 `--disable-director` / `--director-probability`，而 `cli.ts` 对未知参数**静默忽略**——即这两个上游配置项（`EnableDirector` / `Director.TriggerProbability`）目前对 TS 无实际效果；②`TestMod/Tests/Integration/DIR_DirectorBehaviorRecord.cs`（仍注册在 `V3TestRunner.cs:724`）每轮都在等已被砍除的 `morningPlan end` 日志，必跑到超时才收尾并产出空报告——造脑接线时应同时复活此测试或删除它。

### 3.6 已知坑（详见旧版参考 §2.1.1）

- `ClientWebSocket` 必须 `Options.Proxy = null`，否则系统代理截胡本机 WS。
- `src/ValleyAgent/config.json` 不得作为 csproj 部署项（会覆盖用户配置）。
- 服务器生命周期绑定 Mod 而非存档，返回标题不杀进程。
- **导演日志静默 bug（2026-08-04）**：降级是行为上的（不崩溃），日志是可观测性的（必须可见）。任何 LLM 调用必须 log prompt 输入和 response 输出；任何决策分支必须 log 分支结果和原因；任何过滤/丢弃必须 log 被丢弃项和原因。
- **配置项「可改但无效」陷阱（2026-09-15 登记）**：`ModConfig` 里有一批**消费者已删除的孤儿配置**——仍在 GMCM 面板可见、仍被 `Validate()` 钳制，但设置它们不产生任何效果（`ServerAddress` / `DevMode` / `DebugLogEnabled` / `CustomSystemPrompt` / `AIDailyTopicCount` / `StateRejectionCooldownTicks` / `MaxStateRejections` / `FallbackAI*Probability` / `TodayEventsMaxCount` / `Haggle.{Enabled,MaxRounds,HostileThreshold}`）。另有一批是**刻意的兼容垫片，勿删**（`AutoStartPythonServer` 等 5 个 `[Obsolete]` 项 + `ModelName` + `DialogueTemperature`，见 `ModConfig.cs` 顶部登记块与 `MigrateLegacyFields()`）。名单与摘除顺序见 `Config/ModConfig.cs` 顶注 + 偏移审查报告 §3 C4。

### 3.7 联机支持（2026-08-16 M1 已落地）

- **运行时三模式**（2026-07-18）：Host / ThinClient / Inert；主机权威 + SMAPI ModMessage 中继（`AgentSyncMessages.cs`，房客请求带 `PlayerId`）。
- **M1 经济正确性（2026-08-16）**：WS 协议 `dialogue`/`execute_adjust`/`adjust_result` 加 `playerId`（UniqueMultiplayerID 字符串，可选，缺省回落 `Game1.player`）；`AdjustExecutor` 按 `Game1.GetPlayer` 解析目标 Farmer（找不到 → 新失败码 `playerNotFound`）；房客对话好感度主机权威应用（此前被丢弃）；FOLLOW 目标 = 最近对话发起玩家（`AgentBrain.LastDialoguePlayerId`，本地路径缺省回落）；房客送礼命中求购单主机侧拦截；**P0 修复**：execute_adjust/director_command/allocate_agent 入主线程队列执行（联机下跨线程写 `NetIntDelta` 会污染同步）；BUSY 回复改灰色系统提示"他/她/它正在和别人交流"（`fallback=true` 走 chatBox 灰字）；同机双实例测试时 `VALLEY_TEST_INSTANCE=farmhand` 跳过服务器预启动（防端口互杀）。
- **联机审计修复（2026-08-23）**：五代理全仓审计后修复——**P0 线程纪律补齐**：房客中继 await 续体（好感度 NetInt 写入 / LastDialoguePlayerId / CommandExecutor 执行 / ModMessage 回包）与房客送礼 fd.Points 写入全部改走 `HostRequestHandlers.ProcessMainThreadActions` 主线程队列（原 P0 修复只覆盖 execute_adjust/director_command/allocate_agent 三类 WS 消息，中继链路漏网）；BUSY `fallback` 标记补进 ModMessage 契约与 `FarmhandDialogueTransport`（房客侧灰字此前不可达，会弹普通对话框）；房客负好感 delta 不再被 `is > 0` 静默丢弃（Clamp 0..2500，正负一致生效）；`IGiftTransport.SendAsync` 增加 `requesterPlayerId`，`HostGiftTransport` 好感基线改按送礼发起玩家解析（原来错用主机 friendshipData）；本地对话经 `DualPathAgentServerProvider.DialogueCompleted` 统一回写 LastDialoguePlayerId（TS echo 的 playerId 此前无人消费）；KeepUntil 豁免从空闲淘汰扩展到 ForceAllocate/TryAllocate 候选过滤与 PromoteToAgent 的 manual override 释放（导演 beat 不再被对话挤占）。TS 侧同步修复：NPC 台词入玩家桶 / forget 双桶 / legacyMigrated 迁移守卫 / pending 终态清理 / temp+rename 原子写盘 / trade 整数校验 / evaluate_friendship ±100 钳制。
- **房客可用性修复（2026-09-09）**：虚拟环境复现测试（`src/ValleyAgent.UnitTests/Multiplayer/` 14 测试，Windows/Docker 双平台 593 过/1 特征红）定位四缺口后修复——**①房客三队列排水**：`InitializeThinClientMode` UpdateTicked 补 `DialogueBoxInputPatch.ProcessPendingReplies` / `NPCGiftPatch.ProcessMainThreadActions` / `HostRequestHandlers.ProcessMainThreadActions`（此前房客只驱动 renderer，AI 回复不渲染、送礼好感不落账="网络上对了也没法实际使用"）；**②发送主线程化**：两个 Farmhand transport 的 Game1 读取 + SendMessage 整体经 `EnqueueMainThread` 投递（Task.Run 后台线程直发可污染底层消息队列）；**③回包 requestId**：`DialogueRequest/ResponseMessage` 与 `GiftRequest/ResponseMessage` 加可选 `RequestId`，主机 HandleDialogue/GiftRequest 全路径（含兜底/求购拒绝）原样回填，房客 `HandleResponse` 精确配对——带值但 pending 已超时清理 → 迟到回包丢弃（绝不回退 FIFO）；为空（旧主机）→ 退回 npcName 前缀 FIFO（版本内向后兼容）；**④快照主线程采集**：`SubmitInput` 的 `WorldSnapshotBuilder.Build`/`GetNpcState`/playerId 移到 Task.Run 之前。
- **M3 多玩家化（2026-09-13，issue #4 三项遗留限制全清）**：**①情绪 per-player 双键**——`EmotionEngine` 状态键 `(npcName, playerId)`：无归属事件（state_changed / Director 世界级 mood）进世界桶对所有人可见，可归属事件（action_result 经 `callId → playerId` 暂存表归属）进玩家桶；解析规则是**最近写入胜出**（内部单调 seq），避免一次性玩家情绪永久遮蔽后续世界情绪；换日 `resetAll` 清全部桶。**②房客聊天栏 + 送礼·交易菜单**——`ChatBarRouter.InitializeFarmhand(monitor, transport, remoteRenderer, config)`：在场候选取主机广播的 Agent 名单、请求经 `FarmhandDialogueTransport` 转发主机、回复本地渲染且**不重复执行 actions**（主机已执行并广播）；房客 tick 排水补 `ChatBarRouter.ProcessPendingReplies`；`GiftTradeMenuLogic.ShouldOfferGiftTradeMenu` 去掉 isThinClient 过滤（送礼走 `FarmhandGiftTransport`、交易走 dialogue transport，两条管道房客侧本就接通）。**③玩家画像 per-player**——`player_profile` 表改 `player_id` 主键（旧单行迁 `_legacy` + 首个真实玩家惰性认领，M2 记忆拆分同范式）；`PlayerProfileManager` 全部方法加可选 playerId；`director_runs` 增量加 `player_id` 列留痕。详见 `docs/design/2026-09-13-m3-multiplayer-director-emotion.md`。（③中原本并存的"新增 `PlayerDirectory` 观察式名录 + `Director.morningPlan({ playerIds })` 按玩家编排"**已于 2026-09-14 随旧叙事 Director 整体砍除**——`player-directory.ts` / `director.ts` 已不在 TS 源码树，勿按本节旧文理解。）
- **PR2 身体重构（2026-09-14，issue #9 B1-B5）**：房客可对**任意村民**进 AI 对话（B1 拆除 patch 三处身份门 + B2 聊天栏候选过滤 + B3 主机中继动作先 promote 再执行、LastDialoguePlayerId/优先级刷新不丢）；导演工具**行为类**（`set_npc_position`）按需建身体、纯数据类直接改休眠 Brain（B4）；空闲身体周期回收——对话结束释放 manual override、TimeChanged（每 10 游戏分钟）空闲回收编排、`ReevaluateAllocations` 显式跳过 KeepUntil、`OnAgentDeallocated` 常驻订阅做完整拆除（ForceTransition(evicted)→RemoveAgent 降级休眠→日程还原）（B5）。设计：`docs/design/2026-09-13-agent-body-refactor.md`。
- **已知限制**：房客送礼命中求购时物品已在本地消耗（仅主机侧提示）；房客快照反序列化失败时主机兜底重建会用主机钱包/背包拼房客 playerId（TS 决策对象可能错位）；断线期间 director_command/allocate_agent 无 outbox 直接丢弃；玩家画像行为层/活动日志仍世界级（`activity_report` 协议未接线，C# 未上报 per-player 活动）；导演 game_context 仍是主机玩家状态的世界级快照；房客不支持"喊不在场的 NPC"（远程喊话依赖主机侧 Agent 全员名单）；**房主 3-4 人随机无日志整机卡死未定罪**（2026-09-09 排查：主机中继链路 3 流压测全绿已排除；2026-09-11 起看门狗已随生产 mod 分发，实机冻结自动落 dump，见 `docs/plan/2026-09-10-host-freeze-root-cause.md` 附录 B 终验流程）。
- **测试**：IT14（playerId 三分支：自身 ID / 不存在 ID → playerNotFound / 缺省回落）；E2E harness `scripts/test/run_farmhand_e2e.ps1`（C1-C5 全绿：C4 房客交易落账 + C5 双玩家对话上下文隔离——agents/Haley_players/ 两个独立 rel 文件）。

## 4. 关键文档索引

| 文档 | 内容 |
|---|---|
| `docs/plan/2026-09-15-doc-code-drift-audit.md` | **文档↔代码偏移度审查（最新）** — 46 条 AGENTS 声明逐条取证、代码侧残留清单（TS 测试未参与类型检查 / 死导演测试 / 20 个零读者配置）、待办与未核实项分列 |
| `docs/design/2026-08-15-ts-ledger-reflex-architecture.md` | **账本权威修订设计（最新，四步全部落地 2026-08-15）** — 经济账本/情绪引擎迁 TS、双层校验、execute_adjust 闭环、断线 Outbox 对账、绞杀式四步迁移 |
| `docs/design/2026-08-05-three-tier-architecture-redesign.md` | **三层架构重新设计（基础架构依据；§2.1 账本归属已被 2026-08-15 文档推翻，见该文件顶栏警告）** — Director/C#状态层/NPC Agent 职责隔离、状态光谱、记忆分层、Director 工具集、set_goal 机制 |
| `docs/plan/2026-08-05-three-tier-architecture-execution-plan.md` | **当前执行计划（唯一实施依据）** — 三阶段落地：阶段1交易bug/阶段2set_goal/阶段3大重构 |
| `docs/plan/2026-08-02-execution-plan.md` | 旧执行计划（部分已完成，部分被 2026-08-05 计划取代） |
| `docs/design/2026-08-02-npc-economy-hire-chat-requirements.md` | NPC 经济/雇佣/聊天栏/喊话/消息分级需求（Director 协调部分被 2026-08-05 设计取代） |
| `docs/design/2026-08-01-npc-feedback-architecture.md` | 三场景设计 + ActionLedger 强制回执 + prompt 段序 |
| `docs/design/2026-08-01-test-scoring-redesign.md` | 五层测试体系（契约/组件/故障注入/不变量/体验打分） |
| `docs/design/2026-08-01-memory-narrative-extensibility.md` | TranscriptStore 留痕、记忆四层、导演编排、扩展性 |
| `docs/思路/2026-08-02-P0架构改进实施思路.md` | P0 架构改进思路（TS 端三项已落地，C# 端三项在执行计划 Phase 0） |
| `docs/archive/AGENTS-2026-08-02-v4.3-full.md` | 旧版完整 AGENTS（模块矩阵、状态机细节、常量表、文件速查） |

## 5. 开发规范（速查）

- 工作模式：**设计文档先行**——改代码前确认执行计划里有对应条目，需求细节看引用文档，不凭记忆发挥。
- **猜想台账**：排查/疑难任务先分档——已验证事实 / 未验证猜想 / 已排除；未验证猜想逐条记推理链、反证条件与终验方案（判据 → 结论），结案时逐条裁决；不凭猜动手（§2 第 10 条；范式见 `docs/plan/2026-09-10-host-freeze-root-cause.md` 附录 B）。
- 评论 `//` 说"为什么"，XML 文档说"是什么"；NPC 名做字典 key 用 `StringComparer.OrdinalIgnoreCase`；LLM 调用全 async 不阻塞主线程。
- Handler 退出前先收尾仪式再 `ForceTransition(IDLE)`；移动一律走 `IMovementService`，不直接碰 `npc.controller`。
- **验证门槛**：TS 侧 `bun test` + `bun run typecheck`（仅 `packages/*/src`）+ **`bun run typecheck:tests`（必须同时跑，否则测试目录的悬空 import 与已删类型不会被发现）** + `check:protocol` 全绿；C# 侧编译 0 警告 + 游戏内实测（手动或 TestMod）。
- 构建部署脚本见旧版参考附录 B（`scripts/build/build-deploy.ps1` 等）。
- **分发包**：`scripts/build/package-distribution.ps1` 产出 `release/ValleyTalk-dist-*.zip`（单 ValleyAgent 文件夹 + TS 服务器 exe + 预写 key 的 config；不含 TestMod/Autopilot）。API key 的单一事实源是 dev 部署 `Stardew Valley/Mods/ValleyAgent/config.json`（gitignored）。
- **第三方 LLM 兼容端点（sensenova/mimo）**：`@ai-sdk/openai` v2 对所有非 gpt-* 模型按新协议发 `developer` 角色 + `max_completion_tokens`，商汤会 400——provider 内用 `createCompatFetch` 改写回老式协议（`llm-provider.ts`）。商汤端点 `https://token.sensenova.cn/v1`（**仅 `sensenova-*` 自家模型免费；平台上的第三方模型如 glm-5.2/deepseek-v4-flash 消耗额度**；6.7-flash-lite 对 token plan key 报 route not found）；小米 `https://token-plan-cn.xiaomimimo.com/v1`（mimo-v2.5/mimo-v2.5-pro）。**当前编排：三角色主=minimax，备=统一 mimo-v2.5**。
- **运行时错误落盘**：`Infrastructure/ModErrorLog.cs` 写 `ValleyAgent-error.log`（未捕获异常/服务器崩溃/WS 错误）与 `ValleyAgent-server.log`（TS 服务器完整输出，需 `ServerConsoleWindow=false`）到 mod 目录，5MB 轮转。
- **卡死取证仪器（2026-09-11 生产化，纯观测零行为改动）**：`Infrastructure/MainThreadWatchdog.cs` 随生产 mod 布防（发行包自带，不再依赖 TestMod）——主线程心跳停滞 >5s（`VALLEY_WATCHDOG_MS`）自动落 dbghelp MiniDump + 伴随日志（含 timeOfDay/联机上下文/线程概览）到 `Mods/ValleyAgent/watchdog/`（留 5 份）；同线程顺带轮询 `StuckOperationTracker`（后台操作 >60s 落 stuck-*.dmp，`VALLEY_STUCKOP_MS`；接线：决策批/好感度外部 delta——候选 2/3 静默死循环的唯一观测面）。告警双通道（SMAPI Monitor + ModErrorLog 立即落盘）。TestMod 看门狗在生产版在场时自动让位（防并发 MiniDumpWriteDump）。配套遥测：`QueueTelemetry`（主线程队列深度 ≥100/WS 命令滞留 >2s/OnUpdateTicked 排水段 >100ms 告警）、MoveTo >500ms 慢寻路告警（U1 负载证据）。**TS 侧**：`ServerConsoleWindow=true`（发行包默认）时 C# 经 `VALLEY_SERVER_LOGFILE` 让 TS 自把控制台 tee 到 `ValleyAgent-server.log`（`log-tee.ts`）；WS 连接建立/顶替/断连丢弃、day_started 重复（"多导演"误读观测面）均有日志。
