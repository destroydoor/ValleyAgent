# ValleyAgent — 项目指南（AI 助手版）

> **Last updated:** 2026-09-10（主机卡死排查+修复批落地版；新增设计哲学第 10 条"猜想与实际分离"）
> **卡死排查结论**：`docs/plan/2026-09-10-host-freeze-root-cause.md`（"每玩家一导演"=误读；6 轮 soak 无进程级冻结；FOLLOW 跨图缺陷族行为级实证并已修；U1/U2/U3 猜想台账与实机终验流程见附录 B）
> **当前执行依据**：`docs/plan/2026-08-05-three-tier-architecture-execution-plan.md`
> **架构修订设计**：`docs/design/2026-08-15-ts-ledger-reflex-architecture.md`（经济账本迁 TS + C# 反射执行，**四步全部完成（2026-08-15）**：adjust 执行器 + TS 账本 + 经济工具同步编排 + TS 情绪引擎 + 断线对账）
> **联机分析**：`docs/design/2026-08-16-multiplayer-boundary-analysis.md`（多人联机异常边界与约束；**M1 经济正确性已完成（2026-08-16）**：playerId 贯穿协议/执行器/好感度/FOLLOW，P0 主线程 dispatch 修复，BUSY 灰字；**M2 多玩家上下文已完成（2026-08-17）**：记忆拆世界桶/玩家桶 + per-player 好感/对话历史 + prompt 玩家化 + 工具记忆路由 + 迁移；M3 导演多玩家化未开始）
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
6. **账本与叙事在 TS，反射与执行在 C#** — LLM/RAG/决策/对话/**经济账本（权威）**/**情绪引擎（确定性代码）**全在 TS Agent Server；C# 执行游戏操作、持执行镜像、做**物理校验**（玩家真实余额/背包空间/物品存在性，运行时对 `Game1.player` 实时校验，不信 worldSnapshot）与**生存反射**（低血量逃跑/卡住检测/战斗目标）。判据：**能在断连时还必须工作的逻辑留 C#；账本和叙事永远在 TS；C# 的每一次余额/物品变更都必须由 TS 指令（execute_adjust）驱动并以回执（adjust_result）确认**（2026-08-15 修订，迁移未开始）
7. **性能优先** — 不卡，Token 可控；路由/判定用确定性规则，智能只花在歧义上
8. **AI 的决定必须可追溯** — 程序状态、NPC 思考、导演思考全量留痕，事后能回答"它当时为什么这么想"
9. **认知不得脱离现实** — 每次状态/资产变动必须有回执，NPC 对现实的认知与游戏状态保持一致（海莉事件是总教训）
10. **猜想与实际分离**（2026-09-10 用户定规矩）— 程序制作不猜：一切未经验证的机理推断必须显式标注为**猜想**，与已验证事实分列；每条猜想要有推理链、支撑证据、反证条件、**终验方案**（复现/验证步骤 + 判据）；修复只基于已验证事实，猜想不落地为结论、不直接动手修。反面教训（2026-09-10 卡死排查）："每玩家一个导演"（日志误读，证伪）、"卡 2600 是对话框工件"（初猜，对照轮才裁定）、"provider 大小写"（先猜后实证）——猜与实际混写，一步错步步错。台账范式见 `docs/plan/2026-09-10-host-freeze-root-cause.md` 附录 B

### 2.1 我们不做什么

- **不做 NPC 自主动机循环** — NPC 不自己决定"今天我要做什么"。没有导演 beat 时，NPC 要么休眠走原版，要么 spark 随机激活做点小事，要么被玩家对话触发响应
- **不做 LLM 状态轮询** — 不让 LLM 反复检查"我砍了几棵树了"。用 C# GoalExecutor 确定性判断终止条件
- **不做 NPC ↔ Director 双向沟通** — NPC 不审批 Director 提案，不向 Director 汇报，不接收 Director 的元层指令。Director 只改 NPC 的状态数据
- **不做玩家不在场的实时模拟** — 玩家委托 NPC 砍树然后离开，NPC 不会真的去砍。Director 直接改数据，玩家回来对话时 NPC 从 L2 摘要自然感知
- **不做 C# 端经济决策/记账（2026-08-15 规划）** — C# 不定价、不记权威账、不做交易语义判断；只做物理校验和 adjust 原语执行。正则关键词记忆分析（DialogueMemoryAnalyzer）、机械情绪推导（EmotionAnalyzer）属"假 AI"，已列入删除计划

## 3. 架构一页纸（三层架构）

```
┌─────────────────────────────────────────────────────────────┐
│ Director（导演智能体，TS 端）                               │
│ 触发：day_started → DirectorAgent.runDayPlan（工具脑，2026- │
│   09-12 有效化；旧 morningPlan JSON 管线与 runBeat ReAct    │
│   接管已退役）                                              │
│ 视野：全局信息 + 所有 NPC 人设档案 + 所有 NPC 的 L2 状态    │
│ 职责：编故事、编排事件、创造相遇机会、改 NPC 状态           │
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
│ AgentInventory: 执行镜像（权威账本在 TS，2026-08-15 规划）  │
│ BeatStore: 当前 beat 场景描述                               │
│ GoalExecutor: 执行态后台跑（chop_tree/mine/water/fight）    │
│ 生存反射: 低血量逃跑/卡住检测/战斗目标（精简自规则引擎）    │
│ adjust 执行器: 物理校验+原子批游戏变更+指令结果日志（规划） │
│ 断线 Outbox: 事件缓存+重连补发（规划）                      │
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

### 3.1 NPC 状态光谱

| 状态 | 行为 | LLM 消耗 | 进入条件 |
|---|---|---|---|
| **休眠** | 走原版路径，无 AgentBrain | 零 | 默认 |
| **有限自主** | 可对话/交易/送礼/移动；spark 随机激活 | 中（对话或 spark 命中时） | spark 5% / 玩家对话 / Director beat |
| **执行态** | C# GoalExecutor 后台跑 | 零（纯 C#）；完成时 1 次 LLM 汇报 | set_goal 工具调用 |

### 3.2 记忆与状态分层

| 层 | 存什么 | 注入方式 |
|---|---|---|
| **L1 长期记忆** | 玩家-NPC 重要互动、NPC 间重大事件、玩家说过的有长期影响的话 | RAG 语义检索命中才注入 |
| **L2 状态摘要** | 钱包/背包/位置/心情/近期事件/工作标记/欠款 | **每次对话强制注入** prompt 开头 |
| **L3 临时剧本** | 当前 beat 场景描述 | beat 有效期内强制注入 |

L2 todayEvents 保留近 3 天，每天 day_started 清理 3 天前的。判定标准：这件事是否需要跨场景、跨日长期检索？是 → L1；否 → 只 L2。

### 3.3 Wire 协议

单一事实源：`server/protocol/messages.json`（本仓合并布局），check:protocol 追踪（2026-09-12 起路径仓库相对、开箱即用；10 条 planned 死 schema——decision/gift_eval/rag_query/state_sync/emotion_sync/memory_sync/beat_plan/beat_start/beat_end/day_end——已删除，两端从未实现）。active 的有 `hello` / `ping` / `dialogue`（唯一 LLM 触发）/ `dialogue_response` / `action_result` / `state_changed` / `day_started` / `consolidate_day` / `allocate_agent` / `route_shout_response`（`route_shout` 为 orphan_route 预留）。**四步全部落地（2026-08-15）**：`execute_adjust`（TS→C# 原子批指令，带 instructionId，orphan_route）/ `adjust_result`（C#→TS 回执：每步成败+失败码+新余额，active）/ `reconnect_sync`（C#→TS 重连对账：outbox 补发 + active agent 名单，active）。步骤 2：trade/give_item/give_gift/receive_payment 由 TS 同步编排（账本校验→execute_adjust→回执），不再发 C# 执行；C# 还价单结算链已删除；求购（E3-5）保留 C# 生成，命中时拒绝送礼交接并提示走对话议价。步骤 3：TS 情绪引擎（确定性零 LLM，Director set_npc_mood 覆盖权），C# EmotionAnalyzer/DialogueMemoryAnalyzer 删除，RuleBasedDecisionEngine 瘦身为生存反射。步骤 4：C# 重连发 reconnect_sync（WebSocketClient.OnReconnected），TS 对账 in-flight adjust pending（凭 instructionId 重发，幂等缓存闭环）。旧 Python 时代的 `decision`/`friendship_eval` 等死管道已删除。

### 3.4 NPC Agent 工具集

speak / emote / give_item / give_gift / **trade**（新增，阶段 1）/ **set_goal**（新增，阶段 2）/ set_state / show_dialogue / remember / forget / get_info / accept_job / receive_payment（+evaluate_friendship，no-op 记录型）。worldSnapshot 同时携带玩家侧（location/inventory/playerMoney/PlayerHeldItem）与 NPC 侧（npcLocation/npcMoney/npcInventory/npcMood/npcRecentEvents/npcWorkingOn）数据，NPC 认知以 NPC 侧字段为准。

### 3.5 Director 工具集（阶段 3 落地；2026-09-12 端到端接线）

set_npc_position / set_npc_inventory / set_npc_money / set_npc_mood / set_npc_recent_events / set_npc_working_on / spawn_beat / spawn_group_beat / inject_memory。Director **没有** speak/emote/give_item/trade/set_goal —— 这些是 NPC Agent 的角色扮演工具。

**接线（2026-09-12 Director 有效化）**：C# `day_started`（含 directorContext）→ TS `DirectorAgent.runDayPlan`（chatWithTools 工具脑，9 工具全走 `director_command`）→ 逐 beat `allocate_agent`（keepUntilIso）+ C# BeatStore 落库 → 对话时经 L3 场景注入（worldSnapshot.currentBeat）进入 NPC prompt。spawn 预检：NPC 可用性 / 14 天同人冷却 / 每日上限（默认 3）。C# `Director.TriggerProbability` 配置经 `--director-probability` 传给 server（审计 M-7 误判为死旋钮，实为活配置）。

### 3.6 已知坑（详见旧版参考 §2.1.1）

- `ClientWebSocket` 必须 `Options.Proxy = null`，否则系统代理截胡本机 WS。
- `src/ValleyAgent/config.json` 不得作为 csproj 部署项（会覆盖用户配置）。
- 服务器生命周期绑定 Mod 而非存档，返回标题不杀进程。
- **导演日志静默 bug（2026-08-04）**：降级是行为上的（不崩溃），日志是可观测性的（必须可见）。任何 LLM 调用必须 log prompt 输入和 response 输出；任何决策分支必须 log 分支结果和原因；任何过滤/丢弃必须 log 被丢弃项和原因。

### 3.7 联机支持（2026-08-16 M1 已落地）

- **运行时三模式**（2026-07-18）：Host / ThinClient / Inert；主机权威 + SMAPI ModMessage 中继（`AgentSyncMessages.cs`，房客请求带 `PlayerId`）。
- **M1 经济正确性（2026-08-16）**：WS 协议 `dialogue`/`execute_adjust`/`adjust_result` 加 `playerId`（UniqueMultiplayerID 字符串，可选，缺省回落 `Game1.player`）；`AdjustExecutor` 按 `Game1.GetPlayer` 解析目标 Farmer（找不到 → 新失败码 `playerNotFound`）；房客对话好感度主机权威应用（此前被丢弃）；FOLLOW 目标 = 最近对话发起玩家（`AgentBrain.LastDialoguePlayerId`，本地路径缺省回落）；房客送礼命中求购单主机侧拦截；**P0 修复**：execute_adjust/director_command/allocate_agent 入主线程队列执行（联机下跨线程写 `NetIntDelta` 会污染同步）；BUSY 回复改灰色系统提示"他/她/它正在和别人交流"（`fallback=true` 走 chatBox 灰字）；同机双实例测试时 `VALLEY_TEST_INSTANCE=farmhand` 跳过服务器预启动（防端口互杀）。
- **联机审计修复（2026-08-23）**：五代理全仓审计后修复——**P0 线程纪律补齐**：房客中继 await 续体（好感度 NetInt 写入 / LastDialoguePlayerId / CommandExecutor 执行 / ModMessage 回包）与房客送礼 fd.Points 写入全部改走 `HostRequestHandlers.ProcessMainThreadActions` 主线程队列（原 P0 修复只覆盖 execute_adjust/director_command/allocate_agent 三类 WS 消息，中继链路漏网）；BUSY `fallback` 标记补进 ModMessage 契约与 `FarmhandDialogueTransport`（房客侧灰字此前不可达，会弹普通对话框）；房客负好感 delta 不再被 `is > 0` 静默丢弃（Clamp 0..2500，正负一致生效）；`IGiftTransport.SendAsync` 增加 `requesterPlayerId`，`HostGiftTransport` 好感基线改按送礼发起玩家解析（原来错用主机 friendshipData）；本地对话经 `DualPathAgentServerProvider.DialogueCompleted` 统一回写 LastDialoguePlayerId（TS echo 的 playerId 此前无人消费）；KeepUntil 豁免从空闲淘汰扩展到 ForceAllocate/TryAllocate 候选过滤与 PromoteToAgent 的 manual override 释放（导演 beat 不再被对话挤占）。TS 侧同步修复：NPC 台词入玩家桶 / forget 双桶 / legacyMigrated 迁移守卫 / pending 终态清理 / temp+rename 原子写盘 / trade 整数校验 / evaluate_friendship ±100 钳制。
- **房客可用性修复（2026-09-09）**：虚拟环境复现测试（`src/ValleyAgent.UnitTests/Multiplayer/` 14 测试，Windows/Docker 双平台 593 过/1 特征红）定位四缺口后修复——**①房客三队列排水**：`InitializeThinClientMode` UpdateTicked 补 `DialogueBoxInputPatch.ProcessPendingReplies` / `NPCGiftPatch.ProcessMainThreadActions` / `HostRequestHandlers.ProcessMainThreadActions`（此前房客只驱动 renderer，AI 回复不渲染、送礼好感不落账="网络上对了也没法实际使用"）；**②发送主线程化**：两个 Farmhand transport 的 Game1 读取 + SendMessage 整体经 `EnqueueMainThread` 投递（Task.Run 后台线程直发可污染底层消息队列）；**③回包 requestId**：`DialogueRequest/ResponseMessage` 与 `GiftRequest/ResponseMessage` 加可选 `RequestId`，主机 HandleDialogue/GiftRequest 全路径（含兜底/求购拒绝）原样回填，房客 `HandleResponse` 精确配对——带值但 pending 已超时清理 → 迟到回包丢弃（绝不回退 FIFO）；为空（旧主机）→ 退回 npcName 前缀 FIFO（版本内向后兼容）；**④快照主线程采集**：`SubmitInput` 的 `WorldSnapshotBuilder.Build`/`GetNpcState`/playerId 移到 Task.Run 之前。
- **已知限制**：travel 跨图跟随仍跟主机位置（仅 local following 已按玩家解析）；房客送礼命中求购时物品已在本地消耗（仅主机侧提示）；房客聊天栏/交易菜单仍禁用（ChatBarRouter 依赖主机端组件、GiftTradeMenu 中继记入待办——2026-09-09 计划 Phase 3）；房客快照反序列化失败时主机兜底重建会用主机钱包/背包拼房客 playerId（TS 决策对象可能错位）；断线期间 director_command/allocate_agent 无 outbox 直接丢弃；情绪引擎为 NPC 世界级而非 per-player（A 激怒 NPC，B 对话承接情绪——M3 需决策是否拆分）；导演/画像仍单玩家视角（M3 未开始）；**房主 3-4 人随机无日志整机卡死未定罪**（2026-09-09 排查：主机中继链路 3 流压测全绿已排除；2026-09-11 起看门狗已随生产 mod 分发，实机冻结自动落 dump，见 `docs/plan/2026-09-10-host-freeze-root-cause.md` 附录 B 终验流程）。
- **测试**：IT14（playerId 三分支：自身 ID / 不存在 ID → playerNotFound / 缺省回落）；E2E harness `scripts/test/run_farmhand_e2e.ps1`（C1-C5 全绿：C4 房客交易落账 + C5 双玩家对话上下文隔离——agents/Haley_players/ 两个独立 rel 文件）。

## 4. 关键文档索引

| 文档 | 内容 |
|---|---|
| `docs/design/2026-08-15-ts-ledger-reflex-architecture.md` | **账本权威修订设计（最新，步骤 1 已落地）** — 经济账本/情绪引擎迁 TS、双层校验、execute_adjust 闭环、断线 Outbox 对账、绞杀式四步迁移 |
| `docs/design/2026-08-05-three-tier-architecture-redesign.md` | **三层架构重新设计（基础架构依据）** — Director/C#状态层/NPC Agent 职责隔离、状态光谱、记忆分层、Director 工具集、set_goal 机制 |
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
- **验证门槛**：TS 侧 `bun test packages/stardew` + `tsc --noEmit` + `check:protocol` 全绿；C# 侧编译 0 警告 + 游戏内实测（手动或 TestMod）。
- 构建部署脚本见旧版参考附录 B（`scripts/build/build-deploy.ps1` 等）。
- **分发包**：`scripts/build/package-distribution.ps1` 产出 `release/ValleyTalk-dist-*.zip`（单 ValleyAgent 文件夹 + TS 服务器 exe + 预写 key 的 config；不含 TestMod/Autopilot）。API key 的单一事实源是 dev 部署 `Stardew Valley/Mods/ValleyAgent/config.json`（gitignored）。
- **第三方 LLM 兼容端点（sensenova/mimo）**：`@ai-sdk/openai` v2 对所有非 gpt-* 模型按新协议发 `developer` 角色 + `max_completion_tokens`，商汤会 400——provider 内用 `createCompatFetch` 改写回老式协议（`llm-provider.ts`）。商汤端点 `https://token.sensenova.cn/v1`（**仅 `sensenova-*` 自家模型免费；平台上的第三方模型如 glm-5.2/deepseek-v4-flash 消耗额度**；6.7-flash-lite 对 token plan key 报 route not found）；小米 `https://token-plan-cn.xiaomimimo.com/v1`（mimo-v2.5/mimo-v2.5-pro）。**当前编排：三角色主=minimax，备=统一 mimo-v2.5**。
- **运行时错误落盘**：`Infrastructure/ModErrorLog.cs` 写 `ValleyAgent-error.log`（未捕获异常/服务器崩溃/WS 错误）与 `ValleyAgent-server.log`（TS 服务器完整输出，需 `ServerConsoleWindow=false`）到 mod 目录，5MB 轮转。
- **卡死取证仪器（2026-09-11 生产化，纯观测零行为改动）**：`Infrastructure/MainThreadWatchdog.cs` 随生产 mod 布防（发行包自带，不再依赖 TestMod）——主线程心跳停滞 >5s（`VALLEY_WATCHDOG_MS`）自动落 dbghelp MiniDump + 伴随日志（含 timeOfDay/联机上下文/线程概览）到 `Mods/ValleyAgent/watchdog/`（留 5 份）；同线程顺带轮询 `StuckOperationTracker`（后台操作 >60s 落 stuck-*.dmp，`VALLEY_STUCKOP_MS`；接线：决策批/好感度外部 delta——候选 2/3 静默死循环的唯一观测面）。告警双通道（SMAPI Monitor + ModErrorLog 立即落盘）。TestMod 看门狗在生产版在场时自动让位（防并发 MiniDumpWriteDump）。配套遥测：`QueueTelemetry`（主线程队列深度 ≥100/WS 命令滞留 >2s/OnUpdateTicked 排水段 >100ms 告警）、MoveTo >500ms 慢寻路告警（U1 负载证据）。**TS 侧**：`ServerConsoleWindow=true`（发行包默认）时 C# 经 `VALLEY_SERVER_LOGFILE` 让 TS 自把控制台 tee 到 `ValleyAgent-server.log`（`log-tee.ts`）；WS 连接建立/顶替/断连丢弃、day_started 重复（"多导演"误读观测面）均有日志。
