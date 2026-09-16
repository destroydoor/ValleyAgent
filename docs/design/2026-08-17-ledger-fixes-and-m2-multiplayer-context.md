# 账本 P1 修复 + C3 补全 + M2 多玩家上下文 — 设计与实施记录

> **Created:** 2026-08-17
> **状态:** **已完成**（2026-09-15 偏移审查时复核：`player_profile` 分键 + `LEGACY_PLAYER_ID` 惰性认领 +
> `legacyMigrated` 迁移守卫 + 记忆双桶均已落地；原文"实施中"为撰写当时的快照）
> **前置:** `docs/design/2026-08-16-multiplayer-boundary-analysis.md`（§4 决策 #1/#2 的落地）、`docs/design/2026-08-15-ts-ledger-reflex-architecture.md`
> **涉及仓库:** ValleyTalk（C#）+ ValleyAI（TS）——文中旧称 ValleyAI 即本仓 `server/` 工作区（TS 已并入本仓库）

三个工作项独立可回滚、独立提交。

---

## 工作项 1：账本 P1 修复

### 1A. 物品账本键归一（give_gift/give_item 账本静默虚高）

**根因**：账本播种键 = QualifiedItemId（`(O)388`，WorldSnapshotBuilder.cs:82-89 已改发）；give_gift schema（stardew-tools.ts:146）指示 LLM 传显示名 → ops 的 itemId/itemName 都是名字 → C# 名称回落解析成功真实扣物 → TS commit 在错误键建负条目又被 `<=0` 删除 → 账本零变更、播种存量虚高。give_item 要求传 ID 但 LLM 不听话时同形状。

**三层修复**：
1. **回执权威键（根治）**：`AdjustStepResult` 加 optional `itemId`；C# `TryCommit` 已解析的 `item.QualifiedItemId` 提升为结构化字段传出（`out string? resolvedItemId`），成功 step 携带；TS `ledger.commit` 优先按回执 step.itemId 入账（npc-target step），回退 op.itemId。
2. **schema 收口**：give_gift 的 item_id 描述改为"传 QualifiedItemId（如 (O)388）"。
3. **TS 归一防线**：`agent-ledger` 新增 `resolveItemKey(state, rawId)`——rawId 非 ID 形态（不匹配 `^\([A-Z]\)` 前缀）时按条目 name 字段反查播种键；beginPending 校验与 commit 入账前归一（无回执键的旧客户端路径）。

**触点**：C# `ProtocolV2.cs`（AdjustStepResult + ItemId）、`AdjustExecutor.cs`（TryCommit out 参数 + step 构造）；TS `types.ts`（AdjustStepResult.itemId?）、`agent-ledger.ts`（commit 优先回执键 + resolveItemKey）、`stardew-tools.ts`（give_gift 描述）；`messages.json` steps 描述注明。

### 1B. sendAdjust 超时对账闭环（双倍扣钱风险）

**根因**：10s 超时 → `fail()`（ledger.rollback 终态 + 合成失败回执）→ LLM 收"失败"自然重试（新 instructionId）→ C# 二次真实执行 → 玩家双倍扣钱。C# 幂等缓存（256 条环形）与注释（EventHandlerInitializer.cs:1383）预期 TS 超时重发，TS 从不重发——契约脱节。

**修复**：超时**不进终态**——
1. 超时回调不调 `fail`（保留 pending），instructionId 挂 30s 后重发：`pendingAdjusts` 已删 → `sendAdjust(msg)` 重新挂 waiter + 下发 → C# 幂等命中缓存返回原回执 → handleAdjustResult 正常 commit/rollback 闭环。
2. 合成回执文案区分超时：工具侧（adjustFailureText）对超时回执输出"结果尚未确认，请不要重复付款/交付，稍后我会核实"（阻止 LLM 开新交易）。
3. reconnect_sync 重发覆盖 pending（超时不再回滚后天然覆盖）。
4. `rollback` 语义保留：beginPending 业务拒绝（未下发）与明确失败回执。
5. 已知限制（记录）：C# 进程重启丢内存幂等缓存 → 重发即重执行（极罕见，mod 死=WS 断，对账窗口内 mod 必然活着）。

**触点**：`protocol-adapter.ts`（sendAdjust 超时回调 + pendingReconciles + 重发）、`stardew-tools.ts`（adjustFailureText 超时文案）、测试 protocol-adapter-ledger.test.ts。

---

## 工作项 2：C3 补全

C3 失败根因：`TryGenerateDialogue` 返回 false——Haley 未被主机分配为 Agent。
- TestMod 加 `va_test_alloc <npc>`（调 `api.TryAllocateAgent`，成功日志 `[Alloc] <npc> allocated`）
- harness 在 C3 前向 hostCmdFile 写 `va_test_alloc Haley`，等 `[Alloc]` 日志后再发 C3
- 目标：E2E C1-C4 全绿

---

## 工作项 3：M2 多玩家上下文

### 数据模型（决策 #1 好感度 TS 权威 + 决策 #2 世界记忆一份/玩家关系分份）

```
agents/
  {npc}_world.json            # 世界记忆：任务/事件类 shortTermMemories + 非 relationship significantMemories
  {npc}_players/
    {playerId}_rel.json       # 玩家关系：playerName + friendship(TS 权威) + conversationHistory(50)
                              #   + 玩家相关 shortTermMemories + relationship 类 significantMemories
```

- conversationHistory **必须 per-player**（串味根源）；条目加 playerName 供标签渲染（替换硬编码"农场主" agent-memory.ts:140）。
- **friendship 权威在 TS**（rel 文件）：对话 delta TS 直接记账，同时 dialogue_response 回传 C# 加游戏内点（两套各自用途）。**快照自愈**：对话开始时快照好感（发起玩家视角）与 TS 值差 >50 → 以快照为准校准（覆盖 C2 送礼路径漂移）。
- 单机：playerId 恒定，行为等价现状。

### prompt 玩家化（发起玩家 P）

- getPhasePrompt/getAttitudeBrief 用 P 的 friendship；attitude 文案主语在 loader 层替换为 {playerName}（npc_prompts.json 不改）。
- conversation_history 标签 = P 的 farmerName；"农场主说："（stardew-agent.ts:230）→ "{playerName}说："。
- 注入：世界近事 5 + P 近事 5；significant 世界+P 全量。

### 工具记忆路由（ToolContext 拆 worldMemory/playerMemory）

- 交易/送礼/收款 → playerMemory（文案用玩家名）；任务/状态 → worldMemory
- remember：category=relationship → playerMemory（relatedNpcs=玩家名，替换 ["Farmer"] stardew-tools.ts:360），其余 → worldMemory
- get_info(player) → playerMemory.friendship（修死值）

### 迁移（惰性认领）

load 时旧 `{npc}_memory.json` 存在且 `{npc}_world.json` 不存在 → 拆分：relationship/significant + conversation → 玩家桶（挂 `_legacy`）；其余 → 世界桶。首个真实 playerId 对话时 rel 文件为空 → 认领 `_legacy`。单机无感。

### 分批

- **M2a**：数据模型拆分 + per-player 历史/好感 + prompt 玩家化 + 迁移 + 快照自愈（测试：agent-memory/prompt-builder/npc-prompt-loader/prompt-segment-order + 间接 8 个）
- **M2b**：工具记忆路由 + get_info + 文案清理 + E2E 双玩家实测

### 边界

- 不做 RAG（维持固定切片）；M3（画像/导演多玩家化）不在本计划；C# 几乎不动；协议不动（friendship 语义变化不触发 schema 变更）

---

## 实施顺序

1. P1 修复（1A→1B）→ 三件套 + 双仓库提交
2. C3 补全 → harness C1-C4 全绿 + 提交
3. M2a → 三件套 + 测试改造绿 + 提交
4. M2b → 同上 + E2E 双玩家实测 + 提交
5. 每步 AGENTS.md 更新

## 不能做什么

- C# 幂等缓存磁盘化（进程重启场景记为已知限制）
- npc_prompts.json 33 NPC × 5 档 phase prompt 重写（loader 层替换主语）
- vanilla friendshipData 与 TS 好感强制对账（自愈校准已覆盖实际漂移源）
