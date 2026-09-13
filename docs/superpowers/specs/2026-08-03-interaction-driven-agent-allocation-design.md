# Agent 交互式分配与三档容量设计

> **创建时间**: 2026-08-03
> **状态**: 已批准（方案 B），转入实施计划
> **代码位置**: `<REPO_ROOT>`（C# Mod）+ `<VALLEYAI_ROOT>`（TS Agent Server）
> **背景**: 当前自动分配在 Day 1 会把 Agent 任意分配给刘易斯/罗宾等"未遇见村民"（Phase 2 fallback 任意挑选），违反"NPC 自主意识"愿景。本设计改为交互驱动 + 导演驱动 + 低概率 spark 的懒加载分配，并把容量配置从两档（Min/Max）扩展为三档（Min/Normal/Max）。

---

## 1. 决策摘要

| 决策点 | 选择 | 理由 |
|--------|------|------|
| 第1天默认 Agent | 0 个（除非导演/spark/玩家交互激活） | 消除任意分配刘易斯/罗宾的设计错误 |
| 分配触发源 | 玩家交互（保留）+ 导演 beat（新增）+ 5% 邻近 spark（新增） | "导演 + 玩家互动 + 少量 spark" 三路并行 |
| 淘汰依据 | 互动空闲超时（非立即淘汰，给玩家切换 NPC 留缓冲） | 避免对话切换时反复创建/销毁 Agent |
| 容量档位 | 三档 Min/Normal/Max（新增 Normal） | 灵活区分"硬下限/日常目标/硬上限" |
| 导演通道 | 方案 B：现在就接入 `allocate_agent` 通道 | 导演已存在，beat 落到未分配 NPC 是自然增量 |
| 协议 | 新增 `allocate_agent`（TS→C#）wire 消息 | 契约单一源 `protocol/messages.json` + `check:protocol` |

---

## 2. 现状与问题

### 2.1 当前分配路径（3 处）

1. **DayStarted 自动分配** [EventHandlerInitializer.cs:929-990](file:///<REPO_ROOT>/src/ValleyAgent/Initialization/EventHandlerInitializer.cs#L929-L990)
   - Phase 1：从有 friendship 数据的 NPC 按 hearts 排序补到 MaxAgentNpcs
   - **Phase 2（问题根源）**：从"玩家从未遇见"的村民任意挑选补到 MinAgentNpcs。第1天无 friendship 数据 → 走 Phase 2 → 任意分配刘易斯/罗宾等
2. **对话 PromoteToAgent** [DialogueBoxInputPatch.cs:452](file:///<REPO_ROOT>/src/ValleyAgent/Patches/DialogueBoxInputPatch.cs#L452)：玩家通过聊天栏/物理动作激活 NPC 时 `ForceAllocate`。保留。
3. **存档恢复** [EventHandlerInitializer.cs:574](file:///<REPO_ROOT>/src/ValleyAgent/Initialization/EventHandlerInitializer.cs#L574)：`ForceAllocate` 恢复存档中的 Agent。保留。

### 2.2 现有淘汰

- `AgentAllocationManager.ForceAllocate` 满员淘汰最低优先级非 manual Agent
- `AgentTickLoop.TrackIdleDecision` 在 `MaxConsecutiveIdleBeforeRelease` 次连续 IDLE LLM 决策后释放到原版日程（基于 LLM 决策空闲，非玩家互动空闲）

### 2.3 设计缺陷

- Phase 2 任意回退违反"NPC 自主意识"愿景
- 缺导演→分配通道，导演 beat 落到未分配 NPC 时直接落空
- 容量只有 Min/Max 两档，无法表达"日常目标 vs 硬上限"

---

## 3. 目标

- **P0** Day 1 不再任意分配刘易斯/罗宾，默认 0 个 Agent
- **P0** 玩家与谁互动，Agent 就分配给谁（PromoteToAgent 已有，保留）
- **P0** 导演 `morningPlan` 选中某 NPC 时能触发该 NPC 的 Agent 分配
- **P0** 5% 概率 spark 激活附近 NPC（每日每 NPC 一次）
- **P0** Agent 长时间未与玩家互动时被清理（玩家转投别人时不立即销毁）
- **P1** 容量三档 Min/Normal/Max，接入 GMCM 设置界面
- **P0** 静态检测 0 警告 0 错误，契约测试 `check:protocol` 转绿

---

## 4. 设计

### 4.1 三档容量配置

| 档位 | 字段 | 默认 | 语义 |
|------|------|------|------|
| 最小 | `MinAgentNpcs`（已有） | 0 | 硬下限。低于此值时 spark 概率加倍（10%）、导演优先选未分配 NPC 直到达到 Min。设 0 = 纯懒加载 |
| 普通 | `NormalAgentNpcs`（新增） | 1 | 日常目标。spark 主动激活到此数量停止；导演 `allocate_agent` 仍可越过此值到 Max |
| 最大 | `MaxAgentNpcs`（已有） | 2 | 硬上限。任何分配不超过，`ForceAllocate` 满员淘汰最低优先级 |

约束：`0 ≤ Min ≤ Normal ≤ Max ≤ 10`。

**spark 概率切换**：
- `current < Min` → 10%
- `Min ≤ current < Normal` → 5%
- `current ≥ Normal` → 0%（停止 spark 主动激活，但导演/玩家交互仍可激活到 Max）

### 4.2 分配触发（按优先级）

#### 4.2.1 玩家交互（保留现有）

- 入口：`PromoteToAgent` [DialogueBoxInputPatch.cs:452](file:///<REPO_ROOT>/src/ValleyAgent/Patches/DialogueBoxInputPatch.cs#L452)
- 行为：玩家对话/聊天栏路由/物理动作触发 → `ForceAllocate` + 标记 manual override
- 刷新 `LastPlayerInteractionTick`（见 4.3）

#### 4.2.2 导演 beat（新增）

- 协议：新增 `allocate_agent` wire 消息（TS→C#，fire_and_forget）
  - 字段：`{ type: "allocate_agent", npcName: string, keepUntilIso?: string, requestId: string }`
  - `keepUntilIso`：ISO 8601 时间戳，期间豁免互动空闲淘汰（与 beat.windowEnd 一致）
- TS 端：导演 `morningPlan` / `milestoneReact` 产出 beat 时，若 beat.npcName 未分配 → 先发 `allocate_agent` 再发 beat
- C# 端：`CommandExecutor` 新增 `ExecuteAllocateAgent` handler
  - `ForceAllocate(npcName)` + 写入 `keepUntil` 到 `AgentAllocationInfo`
  - 失败（满员无可替槽）→ 回 `action_result { reason: "max_capacity_reached" }`
  - 成功 → 回 `action_result { reason: "allocated" }`

#### 4.2.3 5% 邻近 spark（新增）

- 触发：玩家进入某 NPC 半径 `ChatNearbyDistanceTiles`（默认 8）内
- 概率：根据 4.1 spark 概率切换（10% / 5% / 0%）
- 限制：**每 NPC 每天**只掷一次（`Game1.Date` 作 key 记录到 `_sparkRolledToday` 集合，换日清空，与 `ProactiveSpeechQuota.ResetDaily` 同模式）
- 实现：在 `AgentTickLoop` 的 tick 循环里检查玩家附近 NPC，命中时 `ForceAllocate`（非 manual，可被淘汰）
- 失败（满员）：仅 Trace 日志，不影响游戏

### 4.3 淘汰

#### 4.3.1 删除 Phase 2 任意回退

- 删除 [EventHandlerInitializer.cs:970-989](file:///<REPO_ROOT>/src/ValleyAgent/Initialization/EventHandlerInitializer.cs#L970-L989) 的 Phase 2 fallback 逻辑
- `MinAgentNpcs` 不再从"未遇见村民"里任意拉人
- 第1天 = 0 个 Agent（除非存档恢复/spark/导演/交互激活）

#### 4.3.2 互动空闲淘汰（新增）

- `AgentAllocationInfo` 新增字段：
  - `LastPlayerInteractionTick`：玩家与该 NPC 对话/送礼/聊天栏路由时刷新
  - `KeepUntil`：导演 `allocate_agent` 设置的豁免截止时间（可空）
- 淘汰判定（在 `AgentTickLoop` 每 tick 检查）：
  ```
  if (now - LastPlayerInteractionTick > IdleThresholdSeconds
      && (KeepUntil == null || now > KeepUntil))
      → RemoveAgent(npcName)  // 复用现有路径，TS 记忆保留
  ```
- 玩家转投别的 NPC 时不立即淘汰旧 Agent，让其进入空闲倒计时（"暂时清理"语义）
- `RemoveAgent` 现有路径保留（F5 淘汰通知、TS 记忆保留）

#### 4.3.3 容量淘汰（保留现有）

`AgentAllocationManager.ForceAllocate` 满员淘汰逻辑不变，硬上限 = `MaxAgentNpcs`。

### 4.4 协议变更

`<VALLEYAI_ROOT>\protocol\messages.json` 新增 `allocate_agent` 消息定义：

```json
{
  "type": "allocate_agent",
  "direction": "ts_to_csharp",
  "transport": "fire_and_forget",
  "status": "active",
  "description": "导演请求 C# 把某 NPC 分配为 Agent。C# ForceAllocate 并设置 keepUntil 豁免互动空闲淘汰。",
  "fields": [
    { "name": "type", "type": "string", "required": true, "value": "allocate_agent" },
    { "name": "requestId", "type": "string", "required": true, "description": "Guid N 格式" },
    { "name": "npcName", "type": "string", "required": true, "description": "待分配的 NPC 名" },
    { "name": "keepUntilIso", "type": "string", "required": false, "description": "ISO 8601 豁免截止时间，对应 beat.windowEnd" }
  ]
}
```

- `check:protocol` 自动校验两端接线（TS routeMessage 处理 + C# WebSocketClient 处理）
- C# 收到后回 `action_result { reason: "allocated" | "max_capacity_reached" }`

### 4.5 配置迁移

旧存档只有 Min/Max，无 Normal 字段。反序列化时 `NormalAgentNpcs` 默认 0 → 迁移逻辑设为 `Math.Clamp(Max, Min, Max)`（即旧存档 Normal = Max，行为与现状一致，不引入回归）。迁移代码加在 [ModConfig.cs:421-427](file:///<REPO_ROOT>/src/ValleyAgent/Config/ModConfig.cs#L421-L427) 现有迁移旁。

`Validate()` 增加：
```csharp
if (NormalAgentNpcs < MinAgentNpcs) { NormalAgentNpcs = MinAgentNpcs; changed = true; }
if (NormalAgentNpcs > MaxAgentNpcs) { NormalAgentNpcs = MaxAgentNpcs; changed = true; }
```

### 4.6 GMCM 设置界面

现有 [GMCMIntegration.cs:217-237](file:///<REPO_ROOT>/src/ValleyAgent/Config/GMCMIntegration.cs#L217-L237) 有 Min/Max 两个 `AddNumberOption` 滑块。新增 Normal 档：

- 插在 Min 与 Max 之间，`setValue: value => config.NormalAgentNpcs = Math.Clamp(value, config.MinAgentNpcs, config.MaxAgentNpcs)`
- Min 滑块 `setValue` 联动：Min 上调超过 Normal 时 Normal 跟进
- Max 滑块 `setValue` 联动：Max 下调低于 Normal 时 Normal 回退
- Clone 方法 [GMCMIntegration.cs:882-883](file:///<REPO_ROOT>/src/ValleyAgent/Config/GMCMIntegration.cs#L882-L883) 加 `target.NormalAgentNpcs = source.NormalAgentNpcs`

---

## 5. 错误处理

- **5% spark 失败**：`ForceAllocate` 失败（满员无可替槽）→ 仅 Trace 日志，不影响游戏
- **导演 `allocate_agent` 失败**：C# 分配失败 → 回 `action_result { reason: "max_capacity_reached" }`，TS 知道 beat 无法落地（beat 仍记录但状态为 dropped）
- **`keepUntilIso` 解析失败**：当作无豁免处理（fail-open，不阻塞淘汰）
- **`_sparkRolledToday` 集合**：换日清空，与 `ProactiveSpeechQuota.ResetDaily` 同模式
- **存档迁移**：旧存档 Normal 字段缺失 → 默认 0 → 迁移到 `Math.Clamp(Max, Min, Max)`，保证不回归
- **WebSocket 断连**：`allocate_agent` 是 fire_and_forget，断连时 TS 端 send 失败由 ProtocolAdapter 捕获并 Trace 日志（不阻塞导演流程）

---

## 6. 测试

### 6.1 契约测试（TS）

- `protocol/messages.json` 加 `allocate_agent` 消息定义
- `check:protocol` 验证：
  - TS `routeMessage` 路由 `allocate_agent`（发往 C#）
  - C# `WebSocketClient` 处理 `allocate_agent` 类型
  - exit 0（全绿）

### 6.2 单元测试（C#）

- `AgentAllocationManagerTests`：
  - `AgentAllocationInfo` 新增 `LastPlayerInteractionTick` / `KeepUntil` 字段
  - `ShouldEvict(now, idleThreshold)` 判定：互动空闲超时且无 keep 豁免 → true
  - `ShouldEvict` 有 keep 且未过期 → false
- `ModConfigTests`：
  - `Validate` 断言 `0 ≤ Min ≤ Normal ≤ Max ≤ 10`
  - 迁移测试：旧存档（无 Normal 字段）加载后 `Normal == Max`
  - 联动测试：Min 上调超过 Normal 时 Normal 跟进

### 6.3 组件测试（C#）

- 模拟 Day 1 进档 → 断言 0 个 Agent（无任意回退）
- 模拟玩家进入 NPC 半径 → 5% spark 路径只每日每 NPC 一次
- 模拟 `allocate_agent` 消息 → 断言分配 + keepUntil 生效
- 模拟互动空闲超时 → 断言 RemoveAgent 被调用
- 三档不同组合下 spark 概率正确切换（10%/5%/0%）

### 6.4 游戏内验证（手动或 TestMod）

- 第1天进档，确认不再自动分配刘易斯/罗宾
- GMCM 设置界面三档滑块联动正常
- 玩家与某 NPC 对话 → 该 NPC 成为 Agent
- 玩家切换到别的 NPC → 旧 Agent 进入空闲倒计时（不立即销毁）
- 导演 morningPlan 选中未分配 NPC → 该 NPC 被分配

---

## 7. 不变量

- **INV-1**：Day 1 进档（无存档数据）后 `AllocationManager.CurrentAgentCount == 0`，除非 spark/导演/交互激活
- **INV-2**：任何时刻 `0 ≤ MinAgentNpcs ≤ NormalAgentNpcs ≤ MaxAgentNpcs ≤ 10`
- **INV-3**：`allocate_agent` 消息一定有对应的 `action_result` 回执（reason 枚举）
- **INV-4**：`KeepUntil` 过期后 Agent 仍受互动空闲淘汰约束
- **INV-5**：每 NPC 每天最多掷一次 spark（`_sparkRolledToday` 集合换日清空）
- **INV-6**：删除 Phase 2 后，`MinAgentNpcs` 不再触发任意村民分配

---

## 8. 实施顺序（粗粒度）

1. **C# 配置层**：ModConfig 加 NormalAgentNpcs + 迁移 + Validate + GMCM 接入
2. **C# 协议层**：ProtocolV2 加 `allocate_agent` 处理 + ActionResultReason 枚举加 `allocated` / `max_capacity_reached`
3. **C# 分配层**：AgentAllocationInfo 加 `LastPlayerInteractionTick` / `KeepUntil` + 删除 Phase 2 fallback + spark 触发器 + 互动空闲淘汰
4. **TS 协议层**：messages.json 加 `allocate_agent` + routeMessage 路由
5. **TS 导演层**：morningPlan/milestoneReact 产出 beat 时先发 `allocate_agent`
6. **契约测试**：`check:protocol` 转绿
7. **单测 + 组件测试**：6.2 / 6.3 全绿
8. **游戏内验证**：6.4

详细任务分解由 writing-plans skill 产出。
