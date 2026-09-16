> **恢复来源说明（2026-09-16）:** 本底稿恢复自未合并分支 `arena/01a09648-valleyagent`（分支提交 `405ed3e`，该提交也是本文件唯一一次入库）。文中全部 file:line 行号以**当时的 HEAD `7021d83`** 为基准，与当前 main 已有偏移，阅读时勿按行号直接对照现码。除本段外内容一字未改，以保历史证据可核验。恢复原因与经过见 issue #12（2026-09-15 偏移审查发现 main 上引用悬空，详见 `docs/plan/2026-09-15-doc-code-drift-audit.md` §1 A9（登记）/ §6bis（恢复记录））。

# 架构偏移审计报告 — 文档声明 vs 实际代码逐项对照

> **Created:** 2026-09-12
> **审计性质:** 怀疑式静态审计，不依赖验证记录自述；所有结论附 file:line 证据（行号以当前 HEAD `7021d83` 为准）
> **对照基准:** README.md、AGENTS.md（2026-09-10 版）、`docs/design/2026-08-05-three-tier-architecture-redesign.md`、`docs/design/2026-08-15-ts-ledger-reflex-architecture.md`、`docs/plan/2026-08-06-phase3-director-verification.md`
> **审计方法:** 双端全量 grep + 引用图扫描（C# 334 个生产类 / TS 全部导出符号的文件外引用计数）+ 数据流逐链路追踪（Director/beat/L2/情绪/经济/协议）
> **未覆盖:** 动态门禁复跑（沙箱无 bun/.NET，无法执行 `bun test` / `tsc` / `check:protocol` / `dotnet build`）；游戏内行为

---

## 0. 总览

| 维度 | 结论 |
|---|---|
| 经济账本迁移（2026-08-15 四步） | **✓ 基本属实**，闭环完整、质量高 |
| 三层架构中枢（Director 编排） | **✗ 名存实亡**：两条 Director 链路均为死路，生产上无任何 Director→NPC 影响 |
| L3 临时剧本（beat） | **✗ 三处断裂**，端到端不存在 |
| L2 状态摘要 | **△ 半活**：钱包/背包/位置/目标活；心情/近期事件/工作/欠款生产环境恒为默认值 |
| TS 情绪引擎（步骤 3 "完成"） | **△ 断链**：引擎在算，但结果进不了 NPC prompt；C# 侧残留 5+ 处机械情绪直写 |
| 职责隔离（NPC 不知导演） | **△ 潜伏违规**：runBeat 死代码中 prompt 直接以"导演"接管 NPC（旧设计残留） |
| 死代码规模 | C# 约 **5,600 行**完整死子系统 + TS 约 **450 行**死函数/组件 + 多条空转管道 |

一句话总结：**这个项目"账本与执行"的一半已经长成了文档描述的样子，"导演与叙事"的一半还停在设计文档里**——协议、执行器、存储、测试全都铺好了，唯独驱动它们的大脑（TS 端 Director agent 和 beat 执行循环）从未接线，而文档（README/AGENTS）把它们描述为已落地的核心特性。

---

## 1. 重大偏移（P0 — 核心架构声明与实际不符）

### P0-1 Director 整体空转：三层架构的中枢层不存在于生产

**文档声明**：README 核心特性"导演编排 — Director 智能体基于全局视野编排事件、创造相遇机会、改写 NPC 状态"；AGENTS §3.5"Director 工具集（阶段 3 落地）"。

**实际——两套 Director 均为死路：**

**(a) 工具型 Director（阶段 3）：有手无脑。**
- C# 端 9 个工具（`src/ValleyAgent/Commands/DirectorTools.cs`）+ `CommandExecutor.cs:166` 特殊路由 + `director_command` 协议通道全部就绪并有 83 个单测；
- 但 **TS 端没有任何生产代码构造或发送 `director_command`**。全仓唯一"发送方"是 `protocol-adapter.ts:227 handleDirectorCommand`——它只是把 C# 侧发来的同型消息转发回 C#（`routeMessage` 的入站 case），而 C# 从不发送它（messages.json 自标 `orphan_route`）。Phase 3 验证记录自己佐证：`docs/plan/2026-08-06-phase3-director-verification.md` §6 遗留待办"游戏内实测（… Director spawn_beat 实际编排）待游戏环境运行"、§3 裁决 2"新工具型 Director 并存（additive）"——**"工具型 Director 的 LLM 大脑"从未开工**。

**(b) 旧叙事 Director（director.ts）：每天空转产 beat，产出无人消费。**
- `protocol-adapter.ts:169-194`：`day_started` 以 10% 概率调 `director.morningPlan()`，产出的 beat 存 TS SQLite（`beat-store.ts`）；
- 消费侧：`listActive()` 全仓仅测试调用（`beat-store.ts:108`）；`runBeat()` 无任何生产调用方（仅 `stardew-agent-beat.test.ts` / `transcript-wiring.test.ts`）；`milestoneReact()` 同样仅测试调用；
- 对 C# 的唯一副作用是每 beat 一条 `allocate_agent`（保活 agent），**beat.directive 本身只进日志不进消息**（`protocol-adapter.ts:190-193`）。

**(c) DirectorContextBuilder 每日产出即弃。**
- C# `AI/DirectorContextBuilder.cs` 每天拼 800–1500 token 全局上下文（`ModConfig.cs:1209 DirectorConfig.ContextMin/MaxTokens`，`ServiceInitializer.cs:462-463` 接线）随 `day_started` 发送；
- TS 侧只打印长度后丢弃：`protocol-adapter.ts:163-166`"内容消费留给工具型 Director"——即承认无消费者。

**结论**：`set_npc_position/inventory/money/mood/recent_events/working_on`、`spawn_beat/spawn_group_beat`、`inject_memory` 九个工具在生产环境**一次都不会被调用**。"Director 编排"作为 README 宣传的核心特性，端到端不存在。

### P0-2 L3 临时剧本（beat）链路三处断裂

**文档声明**：AGENTS §3.2"L3 临时剧本：当前 beat 场景描述，beat 有效期内强制注入"；§3 架构图"BeatStore: 当前 beat 场景描述"。

**实际三处断点（任一单独成立即致命）：**

| # | 断点 | 证据 |
|---|---|---|
| 1 | C# 发 `currentBeat`，TS 解码器没有这个字段，**接收即丢弃** | C# `IAgentServerProvider.cs:72-73`（`CurrentBeat` 尾参数）、`WorldSnapshotBuilder.cs:100`（从 C# BeatStore 填充）→ TS `types.ts:347-380 SceneState` 无 currentBeat；`world-snapshot-decoder.ts` 无映射；`prompt-builder.ts` 对话模板无 beat 段 |
| 2 | TS Director 产的 beat 永不执行 | 见 P0-1(b)：`runBeat` 死代码 |
| 3 | 写 C# BeatStore 的唯一入口 `spawn_beat` 无调用方 | 见 P0-1(a) |

**后果**：NPC 对话 prompt 中 L3 层**从未出现过任何剧本**。`BeatStore.cs`（C#）在生产中永远为空列表；`beat-store.ts`（TS）只是导演的自我备忘录（冷却预算用 `listRecent`）。

### P0-3 L2 状态摘要四个字段生产环境恒为默认值

**文档声明**：AGENTS 哲学第 3 条（P0）"状态可见 — 心情/近期事件…以 L2 状态摘要形式强制注入 prompt"。

**实际**（`prompt-builder.ts:201-215` 的「## 你的状态」段注入值）：

| 字段 | prompt 恒显值 | 唯一写入者 | 写入者状态 |
|---|---|---|---|
| `npcMood` | "平静" | `DirectorTools.cs:253 set_npc_mood` | 死路（P0-1a） |
| `npcRecentEvents` | "无" | `DirectorTools.cs:268 set_npc_recent_events` | 死路；`AgentBrain.AddTodayEvent`（`AgentBrain.cs:85`）**全仓零调用方** |
| `npcWorkingOn` | "无" | `DirectorTools.cs:303 set_npc_working_on` | 死路——**连 GoalExecutor 执行 set_goal 期间都不写 WorkingOn** |
| `npcOwedMoney` | 0 | （无任何写入者，含 DirectorTools） | 从未实现 |

**佐证**：`EventHandlerInitializer.cs:1067-1078` 的 todayEvents 三天清理逻辑每天在永远为空的列表上空转。
**净效果**：L2"强制注入"实际只有 钱包（TS 账本 ✓）/背包（TS 账本 ✓）/位置/当前目标（GoalExecutor ✓）四项活；心情/事件/工作/欠款是四个写死的占位符。设计哲学第 3 条（P0）一半未兑现。

---

## 2. 重大偏移（P1 — 已落地部分存在实质缺陷或潜伏违规）

### P1-1 TS 情绪引擎（迁移步骤 3，声明"完成"）结果进不了 NPC prompt，C# 侧机械情绪推导未清干净

**文档声明**：AGENTS §3.3"步骤 3：TS 情绪引擎（确定性零 LLM，Director set_npc_mood 覆盖权），C# EmotionAnalyzer… 删除"、哲学第 6 条"情绪引擎（确定性代码）全在 TS Agent Server"、设计文档核心决策 4"情绪引擎 = TS 确定性代码"。

**实际两条裂缝：**

1. **引擎在算，NPC 看不见。** TS `EmotionEngine` 由 `action_result`/`state_changed`/`day_started` 事件驱动（`protocol-adapter.ts:120,137,162`），但其输出只用于 `dialogue_response.emotion`（`protocol-adapter.ts:673-675`）。**NPC 对话 prompt 的 `心情：{mood_tag}` 取自 `scene.npcMood`**（`prompt-builder.ts:12,201-204`），而该字段唯一来源是死掉的 `set_npc_mood`（见 P0-3）→ **prompt 恒"平静"，情绪引擎对角色扮演零影响**。`handleDialogue`（`protocol-adapter.ts:567-632`）在调 `runDialogue` 前没有任何把 `emotionEngine.current()` 写回 scene 的代码。
2. **C# 端仍在直写情绪**（设计明言要删的"机械情绪映射"）：
   - `EventHandlerInitializer.cs:2250` PlayerInDanger → Worried
   - `Combat/MonsterAggroManager.cs:110` HurtInBattle → Tired
   - `Api/AgentActionApi.cs:117`、`Commands/ItemCommands.cs:142` GiftGiven → Grateful
   - `Core/AgentTickLoop.cs:140` IdleTooLong → Neutral
   - 存档持久化的也是这份 C# 情绪（`EventHandlerInitializer.cs:973-978`）。两套情绪真相互相不可见。

（`DialogueManagementApi.cs:213` 的 `DialogueResponse → SyncEmotion` 是 TS→C# 镜像同步，这条是符合设计的。）

### P1-2 runBeat 模板直接违反职责隔离 P0 原则（潜伏地雷）

**文档声明**：哲学第 1 条（P0）"NPC Agent 只做角色扮演，永远不知道'导演'存在"；§3 架构图"当前 beat 场景（第三人称描述，**不提'导演'**）"；§2.1"不做 NPC ↔ Director 双向沟通"。

**实际**（`prompt-builder.ts:79 BEAT_SYSTEM_TEMPLATE`，`stardew-agent.ts:351 runBeat`）：
- system prompt 第一句："**你现在被叙事导演临时接管，按照导演的高层指令行动**"；
- user message（`stardew-agent.ts:416-419`）：`导演指令：{directive}\n导演意图：{reasonGenerated}`——把 Director 的**生成理由**直接喂进 NPC 上下文；
- 行动规则："如果觉得指令不合适…可以拒绝行动但要说出来"——即 NPC 审批 Director 提案（§2.1 明令禁止）；
- 附带注入 `profileMgr.summarizeForDirector()` / `gameCtxMgr.summarizeForDirector()`（`stardew-agent.ts:364-365`）——NPC 拿到的是**导演视野**摘要而非角色视野。

**缓解因素**：`runBeat` 当前无生产调用方（P0-2），所以违规尚未发生在线上。但它是唯一写完的 beat 执行器——一旦有人"把 beat 接上"，就会直接踩穿 P0 隔离原则。该模板是 2026-07-21 旧叙事导演设计的产物，与 2026-08-05 起的治理设计直接冲突，属于**未按新设计重写的旧代码**。

同文件注释自认的另一处：ReActGuard（token 预算/无进展/重复调用软刹车）"implemented and unit-tested but **not yet wired into any production call path**"（`stardew-agent.ts:340-343`）——beat 循环唯一刹车是 maxTurns=8。

---

## 3. 中等偏移（M — 功能缝隙 / 空转管道 / 文档腐化）

### M-1 C# 死代码约 5,600 行（12 个零引用组件 + RAG 子系统）

引用图扫描（334 个生产类，文件外引用计数为 0，含 TestMod/UnitTests 复核）：

| 死组件 | 行数小计 | 说明 |
|---|---|---|
| `Context/`（AgentContextBuilder + 5 个 Provider） | ~1,000 | 整个子系统无引用（GameWorldContextProvider 仅存在于一条注释里） |
| `RAG/`（RAGKnowledgeBase + Models） | 1,749 | **启动时加载**（`ServiceInitializer.cs:110-112`，部署包还带着数据），但 `GetNpcPreferences/GetGiftTaste/GetNpcsWhoLove/GetAllNpcNames` 全仓**零调用方**（含测试） |
| `Controllers/` FarmController / ForageController / IdleController | ~900 | 零引用（活的另有其人：ForageHandler 等） |
| `Exceptions/` GlobalExceptionHandler / BoundaryCaseChecker | ~600 | 零引用 |
| `Recovery/RecoveryActions.cs`、`Validation/ActionValidator.cs`、`UI/AgentInventoryMenu.cs`、`Dialogue/Typewriter.cs`、`Handlers/WoodScanHelper.cs`、`Agents/InteractionTracker.cs` | ~350 | 均零引用 |

### M-2 求购（E3-5）双定价体系脱节 + 陈旧注释

- 保留 C# 生成本身是**有文档依据的**（AGENTS §3.3"求购保留 C# 生成，命中时拒绝送礼交接并提示走对话议价"，`NPCGiftPatch.cs:117,356` 行为相符 ✓）；
- 但求购单（物品/价格 1.0–1.1× 公道价，`NpcPurchaseRequestService.cs` + `PricingEngine.cs`）**不进 worldSnapshot、不进任何 TS 链路**——玩家被提示"走对话议价"后，NPC LLM 对求购单存在与否、价格多少一无所知，只能靠 playerHeldItem.MarketPrice 重新锚定。两套定价（C# 求购价 vs TS trade 工具价）互不知情；
- `NpcPurchaseRequestService.cs:11` 注释仍宣称"命中后经 **TradeSettlement.SettleNpcBuys** 原子结算"——该方法已随 TradeSettlement 删除，注释指向不存在的代码；
- 2026-08-15 设计 §5 步骤 2 明列"迁 TS：PendingOffer/PendingOfferRegistry、PricingEngine、NpcPurchaseRequestService、NpcEconomyProfile/Loader"，实际全部留在 C# 且 TS 端无对应物——设计文档从未回写这次"部分保留"的裁决。

### M-3 `consolidate_day`：active 协议消息两端空转

- C#：`EventHandlerInitializer.cs:1242-1267`（DayEnding）与 `:1567-1582`（跳睡兜底）每天对**所有** agent 序列化+发送+记日志；
- TS：`protocol-adapter.ts:485-488` handler 是存根——"Phase 1（E1-1 TranscriptStore）**将在这里接入**实际的逐 Agent LLM 日结"。
- 净效果：每天 N 条消息发送、接收、打日志，"记忆日结"功能不存在。AGENTS §3.3 把它列为 active 属于只对了协议状态、错了功能状态。

### M-4 MorningShoutRouter 生产恒走确定性兜底，注释与实现不符

`protocol-adapter.ts:83` `new MorningShoutRouter()`——构造时**不注入 LLM**。类内逻辑：无 LLM → 恒走"醒着+关系最近"确定性选择。而 `handleRouteShout` 的文档注释（`protocol-adapter.ts:492-494`）仍写"最多 1 次 LLM 调用"。可能是刻意的 token 节约，但代码注释与实际行为相反，且没有任何地方记录这个决策。

### M-5 "L1 长期记忆 RAG 检索"名不副实

README"三层记忆（长期记忆 **RAG 检索**…）"、AGENTS §3.2"RAG 语义检索命中才注入"。实际 `agent-memory.ts:88-89,319-321`：检索打分 = `importance + 新近度衰减`，无嵌入、无语义匹配（core 包 `memory-backend.ts` 亦无）。注入策略 = 显著记忆全量 + 最近 N 条。"RAG" 是对重要性排序的营销式命名。架构上可以接受（甚至更可控），但文档描述与实现机制不符。

### M-6 玩家画像 LLM 推断死代码

`player-profile.ts:196 refreshPreferences / :229 refreshPersonality`（LLM 推断 routinePattern/archetype）无生产调用方（仅测试）。画像永远停留在初始状态，`summarizeForDirector()` 的消费方又只有死掉的 runBeat 与 morningPlan prompt。

### M-7 配置表面分裂：Director 触发概率两处定义一处死

C# `ModConfig.cs DirectorConfig.TriggerProbability`（默认 0.1，带 DefaultValue 特性和注释）**零消费方**；真正生效的是 TS 侧 `cli.ts:68 --director-probability` / `server.ts:208`。玩家在游戏 GMCM 里调 C# 这个旋钮不会改变任何行为。

### M-8 质量门禁在合并仓布局下开箱即坏

- `server/scripts/check-protocol-contract.ts:38-39`：默认路径硬编码旧双仓布局 `D:\Source\ValleyAI` / `D:\Source\ValleyTalk\src`。本仓（C# 与 TS 同仓）直接跑 `bun run check:protocol` 会扫错目录；必须手动设 `VALLEYAI_ROOT`/`VALLEYTALK_SRC_ROOT`。"协议单一事实源"的强制执行器处于半失修状态。
- `scripts/build/deploy.ps1:11` `$GamePath = "D:\Source\ValleyTalk\Stardew Valley"` 同样硬编码（README 有"按需修改"提示，程度较轻）。

### M-9 "ValleyTalk for SVE" 人设数据未接入现行架构

README："Stardew Valley Expanded 兼容 NPC 人设数据"。实际：NPC 人设的唯一生产来源是 TS `server/packages/stardew/data/npc_prompts.json`（33 个原版 NPC，无 SVE 角色）；SVE bio 的 C# 消费者 `ValleyTalkBioLoader` 只被 Debug 命令处理器引用（`ConsoleCommandHandlers.cs:34`）。SVE NPC 在当前架构下拿不到人设 prompt。

---

## 4. 轻微偏移（L — 文档与代码的小幅失真）

| # | 内容 | 证据 |
|---|---|---|
| L-1 | AGENTS §3.4 工具清单漏 `chop_tree`（D2 两阶段意图工具，实际存在且 LLM 可见） | `stardew-tools.ts:316` |
| L-2 | §3.3"dialogue（唯一 LLM 触发）"不准确：`day_started`→`morningPlan` 也是生产 LLM 触发点（10%/天） | `protocol-adapter.ts:169-194` |
| L-3 | 分发包命名仍为旧项目名 `ValleyTalk-dist-*.zip`（README 自己也这么写，属自觉的遗留命名） | AGENTS §5 |
| L-4 | messages.json 里 10 条 `planned` 死 schema（beat_plan/beat_start/beat_end/emotion_sync/memory_sync/decision/gift_eval/rag_query/state_sync/day_end）+ TS `narrative-types.ts` 对应类型仅测试引用——"schema 留档"与噪音的边界在持续劣化 | `messages.json`、TS 死代码扫描 |
| L-5 | AGENTS"四步全部完成（2026-08-15）"与设计 §5 步骤 2 的"迁 TS/删"清单不符（求购族保留 C#），治理文档与设计文档互相矛盾且都未回写 | 见 M-2 |
| L-6 | `check:imports`（depcruise）只护 `packages/core`，`packages/stardew` 无依赖规则 | `server/package.json`、`.dependency-cruiser.cjs` |
| L-7 | `ProtocolV2.cs` 保留 4 个零引用旧消息类（CommandMessage/PlayerInputMessage/EventMessage/ToolCallMessage）——"已删除死管道"声明只对 wire 层成立，类层仍在 | `ProtocolV2.cs:86-90` |

---

## 5. 验证符合项（声明属实，予以确认）

审计同时确认以下高价值声明**与代码一致**，避免误伤：

| 声明 | 验证结果 |
|---|---|
| 经济账本 TS 权威 + execute_adjust/adjust_result 闭环 + 幂等 + pending 状态机 | ✓ `agent-ledger.ts`（beginPending→execute→commit/rollback、终态即时删除）、`AdjustExecutor.cs`（791 行，物理校验+原子批+指令日志）、`protocol-adapter.ts:302-367` |
| 经济工具 TS 同步编排（trade/give_item/give_gift/receive_payment 不再走 C# 语义） | ✓ `stardew-tools.ts:124-231,261-285` 全部经 `economy.adjust` 原子批；2026-08-23 整数收口在 |
| 账本自填（scene.npcMoney/npcInventory 以 TS 账本覆盖 C# 镜像） | ✓ `protocol-adapter.ts:334-349 applyLedgerToScene` |
| C# 端零 LLM | ✓ 无 LLM HTTP 调用；全部 SendAsync 为本地 transport 抽象 |
| EmotionAnalyzer / DialogueMemoryAnalyzer / HaggleStateMachine / 雇佣合同族 / AgentInventoryTradeActor 已删除 | ✓ 文件已不存在 |
| RuleBasedDecisionEngine 瘦身为生存反射（战斗/低血量逃跑/默认空闲） | ✓ `AI/RuleBasedDecisionEngine.cs:60-150`，性格决策已删 |
| 协议单一事实源状态标注（active/orphan_route/planned）与两端一致 | ✓ messages.json 27 条 vs C# 常量 vs TS routeMessage case 逐一吻合；`director_command`/`execute_adjust`/`route_shout` 三条 orphan_route 语义（TS 路由、C# 不发）与实际一致 |
| Outbox + reconnect_sync 重连对账 | ✓ `WebSocketClient.cs:29-43,139,264`（断线入队/上限/补发/带计数回调）、`protocol-adapter.ts:243-268`（凭 instructionId 重发 pending） |
| 主线程纪律（联机 P0） | ✓ `HostRequestHandlers.MainThreadActions` + `EnqueueMainThread`；2026-09-11 遥测（QueueTelemetry/Watchdog/StuckOpTracker）均在 |
| spark 5% 激活、休眠 NPC 走原版 | ✓ `SparkAllocator.cs:107` |
| set_goal → C# GoalExecutor 零 LLM 执行（chop/mine/water/fight/forage） | ✓ `Goals/` 五种 Goal + `ServiceInitializer.cs:385-387` 接线 |
| 多玩家：playerId 贯穿、记忆世界桶/玩家桶、per-player 好感、快照自愈 | ✓ `protocol-adapter.ts:593-612`、`agent-memory.ts` PlayerMemory；Multiplayer 14 测试存在（6+5+3） |
| 好感 ±100 钳制 / dialogue 主路径并回 friendshipDelta | ✓ `stardew-agent.ts:56,626` |
| 求购命中拒绝送礼交接（不改资产） | ✓ `NPCGiftPatch.cs:117,331-356`（但见 M-2 的链路缝隙） |

---

## 6. 偏移根因分析

1. **两代设计叠加未清理**：2026-07-21 叙事导演（director.ts/beat-store/runBeat/ReactGuard/BEAT_SYSTEM_TEMPLATE）与 2026-08-05 三层架构（工具型 Director + C# BeatStore + L3）是**两套互不兼容的 Director 设计**。Phase 3 裁决"旧保留作灵感、新并存（additive）"实际结果是：旧的留了但断头，新的建了但没脑子，两套的尸体都在。
2. **"落地"的验收口径是组件级而非链路级**：Phase 3 验证记录验收的是"DirectorTools 9 工具 + 83 测试 + 协议 PASS"——每个组件都真，但没有任何验收要求"从 LLM 决策到 NPC 状态改变"的端到端链路通。于是出现"有执行器、有协议、有存储、有测试，唯独没有调用方"的完整死路。
3. **文档先行、修订不回写**：设计文档（2026-08-15 步骤 2"迁 TS/删"清单）与 AGENTS（"求购保留 C#"）与代码三方各说各话；"四步全部完成"是按最宽口径宣布的。
4. **验证门禁是静态的**：check:protocol 能抓"消息类型"漂移，抓不到"消息有了但没人发/没人消费"这种语义性死路。死代码没有门禁（depcruise 只护 core 的依赖方向）。

---

## 7. 建议处置（按优先级，供排期参考）

| 优先级 | 建议 | 对应发现 |
|---|---|---|
| 1 | **二选一裁决 Director 的命运**：要么给工具型 Director 造脑（TS 端一个读 directorContext、调 9 工具的 LLM agent loop，day_started 驱动），要么正式砍掉旧叙事 Director（director.ts/beat-store/runBeat/BEAT_SYSTEM_TEMPLATE/react-guard ~1,500 行）并从 README 撤下"导演编排"宣传。当前"半存半废"状态是最大的架构债 | P0-1, P1-2 |
| 2 | **修 L3/L2 断链的最小闭环**：TS SceneState 加 `currentBeat` 字段 + 解码 + 对话 prompt 注入段（第三人称化措辞，不提导演）→ C# 已有数据立即可用；同时给 WorkingOn 接 GoalExecutor 写入、给 TodayEvents 接运行时事件（AddTodayEvent 已有实现零调用） | P0-2, P0-3 |
| 3 | **情绪引擎接线**：`handleDialogue` 在 `runDialogue` 前用 `emotionEngine.current()` 回填 `scene.npcMood`（一行级改动，立即让 prompt 心情变真）；裁决 C# 5 处 SyncEmotion 直写的去留（至少 Worried/Tired 应改为发 state_changed 事件让 TS 引擎感知） | P1-1 |
| 4 | **求购单进 TS 上下文**：把活跃求购（物品/价格）塞进 worldSnapshot 或 dialogue 请求，让"走对话议价"后的 NPC 有价格锚；顺手修 NpcPurchaseRequestService 的 TradeSettlement 陈旧注释 | M-2 |
| 5 | **死代码清理批**：C# ~5,600 行（Context/RAG/Controllers 三件套/Exceptions/Recovery/Validation/UI…）+ TS react-guard/refresh*/runBeat；或至少先删"加载但不查询"的 RAG（省启动开销与部署体积） | M-1, M-6 |
| 6 | **门禁修缮**：check-protocol-contract 改为仓库相对路径探测；consolidate_day 存根要么实现要么把消息降级 planned；增加一条"orphan_route 消息必须有计划消费方"的 lint 规则 | M-3, M-8 |
| 7 | **文档对齐批**：AGENTS §3.3/§3.4/§3.5 按 M-2/L-1/L-2 修正；README 的"导演编排/RAG 检索/SVE 兼容"三处表述降级为规划中或如实描述 | M-5, M-9, L-* |

---

## 8. 审计局限

- 静态分析为主，无法在沙箱复跑动态门禁（`bun test`/`tsc --noEmit`/`check:protocol`/`dotnet build` 均因无 bun/.NET 运行时与无外网未执行）；"全绿"声明本次未复核。
- 引用计数判定死代码时已剔除嵌套类型误报并抽查复核，但仍建议处置前对每个组件跑一次删除编译验证。
- 未审计：C# 状态机/寻路等运行时行为正确性、联机 E2E 实跑结果、LLM prompt 质量本身。
