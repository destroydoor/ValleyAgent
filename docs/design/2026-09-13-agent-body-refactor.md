# Agent 概念语义重构：从"身份"到"身体"（Issue #9，PR2）

> **日期**：2026-09-13
> **状态**：待用户审阅（审阅通过后派 subagent 实施；**在 PR1 对话连续性之后执行**）
> **定案记录**（2026-09-13 与用户确认）：**语义重构**（不删类）＋ **空闲回收** ＋ **spark 保留** ＋ **分两个 PR**
> **关联**：Issue #9、PR1 设计（`2026-09-13-dialogue-continuity-fixes.md`）、三层架构设计 `2026-08-05-three-tier-architecture-redesign.md`

---

## 1. 提案与定性

用户提案（Issue #9）："agent 这个概念我们不是应该移除了吗？所有 NPC 都是能够互动的，主动行为由导演管。"

核实结论：**"是否分配为 Agent"的身份门槛在对话主链路上已经不存在**，剩余门槛只有三处遗留（§2）。因此本设计不是"删除 Agent 概念"的大手术，而是**语义重构**：把分配从"这个 NPC 是不是 AI NPC"的身份判定，改成"这个 NPC 当前是否持有**身体**（状态机/GoalExecutor/执行动作的能力）"的资源池判定。对话不需要分配（谁被聊到谁激活），只有"身体"占资源。

## 2. 事实台账（已验证）

### F1 主机对话已默认全员可 AI 互动

`EnableInfiniteDialogue` 默认 true（ModConfig.cs:506，GMCM「无限对话」）、`NonAgentAIChatEnabled` 默认 true（:111）、`EnableFirstClickVanilla` 默认 true（:228）。
非 Agent 村民对话分支：`NPCDialoguePatch.cs:135-165`——先播原版台词，关闭后经 `CloseDialoguePostfix`（:277-316）自动打开 AI 输入框；`EnableFirstClickVanilla=false` 时直接跳过原版进 AI。

### F2 TS 侧天然全员

`StardewAgentRegistry.getOrCreate`（stardew-agent-registry.ts:85-108）惰性创建 per-NPC 会话（记忆/账本/情绪），对话路径无分配概念。

### F3 动态升级已存在（按需分配的雏形）

`DialogueBoxInputPatch.PromoteToAgent`（DialogueBoxInputPatch.cs:477-540）：非 Agent 村民的回复出现需要"身体"的动作（set_state/follow/干活类，`s_promotionTriggerTools`）时自动 `ForceAllocate`（满员挤最低优先级非 manual Agent，记忆保留在 TS 文件零损失）。

### F4 房客身份门槛（硬门槛 ①，本 PR 拆除）

- 对话 patch：`NPCDialoguePatch.cs:122-124` 房客 isAgent 判定 = `RemoteRenderer.GetRemoteState(name) != null`（只认主机广播名单）；非 Agent 的两条 AI 分支带 `!isThinClient` 门（:142, :155）——房客对非 Agent 村民只能拿原版台词；
- 聊天栏：`ChatBarRouter.BuildPresence`（ChatBarRouter.cs:429-432）房客只把广播名单内 NPC 列为候选。
- 但**主机处理房客对话的中继根本不检查 Agent**（`HostRequestHandlers.HandleDialogueRequest` 直连 provider，HostRequestHandlers.cs:126）——门槛纯在房客侧 UI/patch。

### F5 导演编排已经是按需分配

TS `morningPlan` 逐 beat 发 `allocate_agent`（protocol-adapter.ts:169-218，KeepUntil=beat 窗口终点）；C# `AllocateAgentHandler`（Protocol/AllocateAgentHandler.cs:29-73）`ForceAllocate + KeepUntil 豁免`。
残余 gap：`DirectorTools.ResolveAgent`（DirectorTools.cs:377-384）对**beat 窗口外**的未分配 NPC 调 set_npc_*/inject_memory 会失败。

### F6 空闲淘汰机制现成

`AgentAllocationManager`（Agents/AgentAllocationManager.cs）具备 [Min,Max] 并发池、优先级（对话/送礼/好感度量）、ForceAllocate 挤出、KeepUntil 豁免（2026-08-23 审计扩展到 ForceAllocate/TryAllocate/PromoteToAgent 候选过滤）。当前缺的只是"对话结束后身体进入可淘汰状态"的触发接线。

### F7 spark 保留（定案）

`SparkAllocator` 5% 随机预激活仅主机侧（NPCDialoguePatch.cs:127-133 OnSparkCandidate）。与按需分配并存，语义 = 主机侧随机预热的"惊喜感"机制。

## 3. 目标架构

```
对话面：全员可聊（主机已达成 F1；本 PR 拆除房客门槛 F4）
   └─ 房客右键任意村民 / 聊天栏任意在场村民 → 中继主机 → TS getOrCreate → 回复
身体面：按需分配（promote 已有 F3；导演工具 gap 补齐 F5）
   └─ 需要身体（身体类动作 / set_npc_* / beat）→ 自动 ForceAllocate（受并发上限约束）
回收面：空闲回收（定案）
   └─ 对话结束 / beat 窗口到期 → 身体进入空闲淘汰候选 → ReevaluateAllocations 周期释放
      （KeepUntil 豁免语义不变：beat 有效期内的身体不被挤）
休眠面：保留（= 无身体的默认态）
   └─ 无身体 NPC 走原版路径，零 LLM / 零 tick 开销（性能优先哲学不变）
```

### 3.1 术语与配置

- 文档与注释统一用"身体（Body）"描述分配语义；`AgentAllocationManager` 类名**不改**（避免大手术，测试/事件/DirectorTools/广播全链路零波及）。
- `MaxAgentNpcs` 语义 = **并发身体上限**（数值上限 10 不放宽：>10 身体放大 C# tick 开销）；GMCM 三档（Min/Normal/Max）保留数值行为，文案改为"AI 身体"表述。
- `AgentSyncBroadcaster` 广播名单机制不变，语义变为"当前持有身体的 NPC"；房客**交互不再依赖它过滤**（渲染远程状态仍用）。

### 3.2 房客全员可对话的具体改法

1. `NPCDialoguePatch`：删除非 Agent AI 分支的 `!isThinClient` 门（:142, :155 两处）——房客与主机同节奏（EnableFirstClickVanilla 语义一致）；房客 isAgent 判定保留（有身体的 NPC 直接走 Agent 分支）。
2. `ChatBarRouter.BuildPresence`：删除房客名单过滤（:429-432）。
3. 房客请求落主机中继（已支持任意 NPC，F4）；**主机中继的动作分发需对齐 promote 逻辑**：`ApplyDialogueResponse` 当前只对有身体 NPC 记 LastDialoguePlayerId / 执行动作（HostRequestHandlers.cs:179-195），需复用 `DispatchDialogueActions` 的"安全动作直执行、身体动作先 promote"语义（DialogueBoxInputPatch.cs:413-433），否则房客对无身体村民的回复动作会被丢弃。

### 3.3 导演工具 gap 补齐

`DirectorTools.ResolveAgent` 失败（NPC 无身体）时自动 `ForceAllocate`（复用 KeepUntil 豁免规则：beat 有效期内的身体不被挤），失败原因照旧返回。覆盖"beat 窗口外 set_npc_*"场景。

### 3.4 空闲回收接线（定案：空闲回收）

- 对话结束（`EndTopicConversation`）与 beat 到期后，该身体的 manual override 释放、进入空闲淘汰候选；
- `ReevaluateAllocations` 周期性把无 KeepUntil 且优先级最低的身体释放回休眠态（机制已存在，接线即可）；
- **不做**"用完即释"（连续对话反复重建、丢 preDialogueState）与"永久保留到换日"（池被全天聊过的 NPC 占满）。

## 4. 成本测算（Issue #9 要求）

| 维度 | 增量 | 依据 |
|---|---|---|
| Token | **≈ 0** | 对话按需（聊到才有 LLM 调用，F2）；beat 每日预算钉死（maxBeatsPerDay=3 + 跨玩家去重 + NPC 冷却）；TS 会话本就 per-NPC 惰性 |
| C# 性能 | **≈ 0** | 身体数量上限不变（MaxAgentNpcs），变的只是"谁在池子里"从预分配变按需；无身体 NPC 零开销（F1 原版路径） |
| 同步/协议 | **无变更** | 广播机制照旧（内容语义变化）；无 messages.json 改动 |
| 改动面 | 中型 | 房客两处过滤 + 中继动作对齐 + DirectorTools 自动分配 + 回收接线 + GMCM 文案；无删类/无迁移 |

## 5. 明确不做

- 不删 `AgentAllocationManager`/`AgentService` 类（语义重构定案）；
- 不放宽 MaxAgentNpcs 上限（性能）；
- 不动 spark（保留定案）；
- 不做 TS 侧任何改动（F2 天然支持）。

## 6. 验证方案

### 自动化门槛

1. `bun test packages/stardew` 回归全绿（TS 零改动，防意外）；`tsc --noEmit`、`check:protocol`；
2. C# 编译 0 警告；xUnit 新增/回归：
   - 房客 patch：非 Agent 村民对话分支在 ThinClient 形态下可达；
   - ChatBarRouter 候选：房客对任意在场村民入候选；
   - 中继动作：无身体 NPC 的回复动作 → promote 成功后执行 / promote 失败安全跳过；
   - 回收：对话结束 + 空闲淘汰周期 → 身体释放回休眠；KeepUntil 内不释放。

### Docker 联机 IT（已有 harness）

扩展 C6：房客右键**非 Agent** 村民 → 原版台词 → AI 输入框 → 收到 AI 回复且好感度落账到房客玩家。

### 实机判据（用户验收）

1. 房客与主机行为一致：任意村民都能进 AI 对话；
2. 主机+房客同时聊**不同**的无身体村民，互不阻塞，两个身体按需建立（MaxAgentNpcs 足够时）；
3. MaxAgentNpcs=1 时：最后对话者持有身体；导演 beat 期间身体不被对话挤出（KeepUntil）；
4. beat 窗口外的 set_npc_*（TestMod/console 注入）自动建立身体后生效；
5. 长时间挂机后，空闲身体被回收（console `list-agents` 观察），村民回到原版行为。

## 7. 实施拆分（subagent 任务；PR1 合入后开工）

| # | 任务 | 前置 |
|---|---|---|
| B1 | 房客 patch 过滤移除（NPCDialoguePatch 两处） | 无 |
| B2 | ChatBarRouter 房客候选过滤移除 | 无 |
| B3 | 主机中继动作分发对齐 promote 语义（ApplyDialogueResponse） | 无 |
| B4 | DirectorTools 自动按需分配 | 无 |
| B5 | 空闲回收接线（对话/beat 结束 → 淘汰候选） | 无 |
| B6 | GMCM/配置文案 + AGENTS.md 更新 | B1-B5 |
| B7 | xUnit + Docker IT C6 | B1-B3 |

> B1/B2/B3 同属房客链路建议同一 subagent 顺序做；B4/B5 独立可并行。
