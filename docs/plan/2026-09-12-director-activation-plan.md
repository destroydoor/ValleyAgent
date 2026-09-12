# 修改计划 — Director 有效化 + 死代码清除 + 断链修复

> **Created:** 2026-09-12
> **依据:** `docs/plan/2026-09-12-architecture-drift-audit.md`（架构偏移审计）
> **目标:** 消灭审计发现的所有 P0/P1/M 级偏移——Director 从"有手无脑"变为端到端有效；L2/L3 断链接通；情绪引擎接入 prompt；约 5,600 行 C# 死代码 + 约 1,500 行 TS 死代码清除；协议/门禁/文档对齐
> **执行方式:** 五个阶段，每阶段独立可验证

---

## 设计裁决

### D1：Director 走"工具大脑"路线（Option A），旧叙事 Director 退役

- **新建 `DirectorAgent`（TS）**：LLM 工具循环（复用 `@valley/core` 的 `agentLoop` + `ToolRegistry`），9 个工具全部经 `director_command` 下发 C# `DirectorTools.Execute`。这是 2026-08-05 治理设计钦定的"工具型 Director"，此前有手（C# 执行器+协议+83 测试）无脑（TS 无调用方）。
- **删除旧 `director.ts`**（morningPlan/milestoneReact beat-JSON 管线）：与工具大脑功能重复，且其配套 `runBeat` ReAct 接管路径违反 P0 职责隔离（prompt 直接说"你现在被叙事导演临时接管"）。两套并存是审计定性的最大架构债。
- **beat 的唯一通路 = L3 场景注入**：DirectorAgent 调 `spawn_beat` → C# `BeatStore` → `worldSnapshot.currentBeat` → TS 解码 → 对话 prompt「眼下正发生的事」段（第三人称，绝不出现"导演"字样）。NPC 在对话中自然演绎场景，无 ReAct 接管、无审批。
- **冷却/预算纪律保留**：spawn_beat 工具内做 TS 侧校验（NPC 在 GameContext 且 isAvailable、每日上限、近期同 NPC 冷却），beat 记录仍进 TS `BeatStore`（SQLite）供 `listRecent` 冷却查询。

### D2：Director 触发与投递时机

- `day_started`（10% 概率门控不变，TS `--director-probability` 可调）→ `DirectorAgent.runDayPlan(directorContext)`。
- C# `game_context_sync` 仅随 day_started 每日一推（无周期推送），故 beat 于 day start 即投递：`spawn_beat(durationMinutes=LLM 给定，30-600 钳制)` + `allocate_agent(keepUntilIso=now+duration 的 ISO8601)`（修复旧代码传 "16:00" 非 ISO 导致 KeepUntil 解析 fail-open 的旧缺陷）。
- `directorContext`（C# DirectorContextBuilder 每日拼装）作为 runDayPlan 的上下文输入——从"接收即丢弃"变为被消费。

### D3：L2/L3 数据源接线（C#）

| 字段 | 接线 |
|---|---|
| npcWorkingOn | `GoalExecutor.CreateGoal` 设目标描述；`SendGoalResult`/`FinalizeCancel` 清除；`BeginReportTravel` 设"去找农场主汇报" |
| npcRecentEvents | `GoalExecutor.SendGoalResult` 写目标完成/失败事件；`NPCGiftPatch` 送礼成功写事件（`AgentBrain.AddTodayEvent` 此前零调用方） |
| npcMood | 双来源：TS 情绪引擎（对话前预填 scene.npcMood）+ DirectorAgent `set_npc_mood`（两者本就是设计的覆盖权关系） |
| currentBeat | C# 已填（BeatStore）；**补 TS 侧解码 + prompt 注入段**（此前接收即丢弃） |
| npcOwedMoney | 无写入者，诚实化处理：prompt 欠款行仅非零时渲染 |
| 求购单 | `WorldSnapshotBuilder` 注入 `npcPurchaseOffers`（NpcPurchaseRequestService.Current 静态引用，BeatStore.Current 同模式）→ TS 解码 → prompt「你想收购」行，让"走对话议价"后 NPC 有价格锚 |

### D4：情绪引擎闭环（TS）

`handleDialogue` 在 `runDialogue` 前：`scene.npcMood` 为空时用 `emotionEngine.current(npcName).emotion` 预填——引擎状态首次进入 NPC prompt。C# 侧 5 处 `SyncEmotion` 直写保留为**镜像语义**（影响存档/调试查询，不影响 TS 认知权威），注释明确标注。

### D5：consolidate_day 从存根变为实做

首次收到某日期的 consolidate_day 时触发 `profileMgr.refreshPreferences()` + `refreshPersonality()`（LLM×2/天，失败静默保旧值——本就如此设计），玩家画像从"永远初始"变为每日进化（同时消灭 M-6 死代码）。逐 NPC 日结 LLM 留待后续（仍标注 TODO）。

### D6：死代码清单

**C# 删除（文件级，~5,600 行）**：Context/（AgentContextBuilder+5 Providers）、RAG/RAGKnowledgeBase.cs（启动加载但全仓零查询）、RAG/Models/GameMechanics.cs（仅被 RAGKnowledgeBase 用）、Controllers/{Farm,Forage,Idle}Controller.cs、Exceptions/{GlobalExceptionHandler,BoundaryCaseChecker}.cs、Recovery/RecoveryActions.cs、Validation/ActionValidator.cs、UI/AgentInventoryMenu.cs、Dialogue/Typewriter.cs、Handlers/WoodScanHelper.cs、Agents/InteractionTracker.cs、ProtocolV2 4 个死消息类（command/player_input/event/tool_call）。~~ModConfig.DirectorConfig.TriggerProbability~~（**执行时更正**：审计 M-7 误判——`ServerProcessManager.cs:301` 将其经 `--director-probability` 传给 TS server，是活配置，保留）。
保留：ValleyTalkBioLoader（Debug 控制台在用）、GameSummaryLoader（DecisionContextBuilder 在用）。
**TS 删除**：director.ts、react-guard.ts、runBeat+BEAT_SYSTEM_TEMPLATE、narrative-types 8 个死消息类型、messages.json 10 条 planned 死 schema。

### D7：门禁修缮

- `check-protocol-contract.ts` 默认路径改仓库相对（server/ 与 ../src），修复合并仓布局下开箱即坏。
- `deploy.ps1` GamePath 参数化。
- `morning-shout-router` 注释与实现对齐（生产确定性、LLM 钩子保留）。

---

## 执行阶段

| 阶段 | 内容 | 验证 |
|---|---|---|
| 1 | TS：DirectorAgent 新建 + adapter/server 接线 + prompt/decoder/types 扩展 + 旧 Director/runBeat/react-guard 删除 | `bun test packages/stardew` + `tsc --noEmit`（沙箱无法执行，交付后由用户复跑） |
| 2 | C#：GoalExecutor/NPCGiftPatch L2 写入 + WorldSnapshot 求购注入 + 静态引用接线 | `dotnet build` 0 警告 + `dotnet test`（同上） |
| 3 | C# 死代码删除 + 引用清理 + UnitTests 同步 | 同上 |
| 4 | 协议：messages.json 死 schema 清除 + check:protocol 路径修复 + deploy.ps1 | `bun run check:protocol` |
| 5 | 文档：AGENTS.md / README.md / 本验证记录 | 人工评审 |

## 测试处置

- **删除**：director.test.ts、director-pipeline.test.ts、director-run-recording.test.ts、transcript-director.test.ts、react-guard.test.ts、stardew-agent-beat.test.ts
- **重写**：protocol-adapter-day-started.test.ts（DirectorAgent stub）、server-director-e2e.test.ts（chatWithTools override 注入 spawn_beat 工具调用）、prompt-segment-order.test.ts（删 BEAT 段序测试）、prompt-builder.test.ts（欠款条件化 + L3 段断言）、transcript-wiring.test.ts（删 runBeat 用例）
- **新增**：director-agent.test.ts（工具循环/校验/冷却/降级）、decoder currentBeat/purchaseOffers 用例

## 已知风险与对策

- **沙箱无 bun/.NET**：所有改动为静态编写，交付清单附完整复验命令；C# 删除以"整文件删除 + 引用点逐一清理"为主，避免行内手术引入编译错误
- **director_command 回 action_result 泄漏风险**：C# 回执 NpcName 取消息 npcName 字段；TS 发送的 DirectorCommandMessage **不带 npcName** → 回执被 TS 安全丢弃。在 types.ts 注释中立此规矩
- **旧存档兼容**：AgentBrain 新增写入不影响存档结构（TodayEvents/WorkingOn 本就在存档模型里）
