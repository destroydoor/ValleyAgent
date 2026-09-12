# 阶段 2 实现思路 — set_goal + GoalExecutor

> **Created:** 2026-08-06
> **设计依据:** `docs/design/2026-08-05-three-tier-architecture-redesign.md` §7
> **执行计划:** `docs/plan/2026-08-05-three-tier-architecture-execution-plan.md` 阶段 2
> **基线:** ValleyTalk c6b4e2e；ValleyAI ce8bbf2（阶段 1 已合并）

---

## 0. 现状核对结论（动手前验证）

| 计划条目 | 现状 | 处置 |
|---|---|---|
| 2.1.1 TS set_goal 工具 | **MISSING**（stardew-tools.ts 无 set_goal，STATES union 无 EXECUTING_GOAL） | 新建 |
| 2.1.2 TS 状态流转 | **MISSING**（types.ts 无 currentGoal；protocol-adapter 无 set_goal 处理） | 新建 |
| 2.2.1 C# GoalExecutor | **MISSING**（无 Goals/ 目录） | 新建 |
| 2.2.2 五种 Goal | **MISSING** | 新建 |
| 2.2.3 超时/卡死 | **MISSING** | 新建 |
| 2.2.4 寻路汇报 | **MISSING**（但 AgentTickLoop.WalkingBack 阶段已有成熟"走回目的地+检测到达/超时"状态机可复用） | 新建 + 复用 |

**可复用基础设施（已确认）：**
- `IMovementService.MoveTo/Follow/Stop/Freeze/Unfreeze/HandleStuck` —— 唯一碰 npc.controller 的组件
- `WorkCommands` 委托模式：`handler.SetForcedTarget(name, tile[, action[, targetId]]) → ForceTransition(state) → MoveTo(approachTile)`，Handler 执行完自动 `ClearForcedTarget → Stop → ForceTransition(IDLE)`
- 五种 Handler：FarmHandler（chop_tree/water_crops）、MineHandler（mine）、FightHandler（fight）、ForageHandler（forage）——都有 forced-target override + 单次动作后自动退出
- `AgentTickLoop.BeginWalkBack/TickWalkingBack`（L230-358）：成熟"走回目的地 + 检测到达/超时/路径丢失"状态机
- `AgentStateMachine.AllowedTransitions` + `ForceTransition`（min-duration 守卫）
- `AgentBrain.CurrentGoal`（已存在，L36）

---

## 1. 关键设计决策

### 1.1 Goal 完成判定 ≠ GoalVerifier 环境差量

**重要差异**：`GoalVerifier`（Testing/GoalVerifier.cs）是**环境差量**模型（起始作物数 vs 当前作物数），用于 TestMod 审计。而阶段 2 的 Goal 终止条件按计划 2.2.2 是**物品获得量**：
- chop_tree: 获得 Wood ≥ qty
- mine: 获得 targetItemId ≥ qty
- water_crops: 浇完所有目标（动作完成）
- fight: 清除怪物 / 到时间（动作完成）
- forage: 采集到指定数量

**实现**：Goal 内部维护 `collectedCount`，在每 tick 对比 **NPC 背包物品数量差**（开始时的基线快照 vs 当前，按 itemId 聚合）判断获得量。不调用 GoalVerifier（它验证环境变化而非背包获得，语义不同）。环境差量模型保留给 TestMod 审计，互不干扰。

### 1.2 委托模式：Goal 持有 Handler 引用

每个 Goal 持有所属 Handler 引用，`Tick()` 里调 `handler.Update(npc, agent, currentTick)`（现有 Handler 的 per-tick 入口）。forced-target 场景：
- `set_goal(type:"chop_tree", params:{quantity:10})` → `FarmHandler.SetForcedTarget(npc, nearestTreeTile, "chop")` → `ForceTransition(EXECUTING_GOAL)`（或 CHOP/FARM）→ GoalExecutor.Tick 每 tick 调 handler.Update
- Handler 单次动作后自动 `ClearForcedTarget → ForceTransition(IDLE)` —— Goal 拦截这个退出，检查物品获得量，未达成则重新 SetForcedTarget 下一个目标

**关键**：Handler 的"单次动作自动退出"与 Goal 的"持续执行直到获得量达标"有张力。方案：GoalExecutor 包装 handler.Update，检测到 handler 退出（CurrentStateFlag 变 IDLE）后：
1. 若 Goal 未完成 → 重新寻目标 + SetForcedTarget + 恢复 EXECUTING_GOAL
2. 若 Goal 完成 → 进入寻路汇报阶段
3. 若超时/卡死/资源耗尽 → Goal 失败

### 1.3 状态机新状态

`AgentState` 加 `EXECUTING_GOAL` / `TRAVELING_TO_REPORT`。AllowedTransitions 加边：
```
IDLE → EXECUTING_GOAL（set_goal 进入）
EXECUTING_GOAL → IDLE（取消/失败）
EXECUTING_GOAL → TRAVELING_TO_REPORT（完成 + reportBack）
TRAVELING_TO_REPORT → IDLE（汇报完成）
FOLLOW/CHOP/FARM/MINE/FIGHT/FORAGE → EXECUTING_GOAL（任意状态可被 set_goal 接管）
```
**min-duration 守卫**：GoalExecutor 的 ForceTransition 需要绕过守卫（set_goal 是 LLM 决策，同 fromDecision 语义）。现有 `ForceTransition(newState, skipDurationGuard: true, fromDecision: true)` 已支持。

### 1.4 GoalExecutor 调度器

新文件 `src/ValleyAgent/Goals/GoalExecutor.cs`：
- 静态单例或服务注册（看 AgentService 模式）
- `CreateGoal(npcName, type, params, reportBack)` → 建 Goal 实例 → `AgentBrain.PendingGoal = goal` → ForceTransition(EXECUTING_GOAL)
- `Tick(int currentTick)`：每 tick 遍历有 PendingGoal 的 agent，调 goal.Tick()
- 钩子位置：`AgentTickLoop.ProcessAgent` 里（vanilla-release 检查后、dialogue 前）或 ModEntry 主循环。**推荐 AgentTickLoop**（已有 per-agent 上下文 npc/agent）

### 1.5 寻路汇报（reportBack=true）

复用 WalkingBack 状态机模式（AgentTickLoop.L230-358）：
1. Goal 完成 → `MovementService.MoveTo(npc, player.currentLocation, player.tile)`（跨图用 AgentNavigator）
2. 状态 = `TRAVELING_TO_REPORT`
3. 到达（距离 < 5 格）→ 触发汇报 LLM 跑一次：speak + give_item + set_state(IDLE)
4. 超时（2 游戏小时）→ inject_memory + update_status_summary
5. 玩家在矿洞深处 → 寻路到矿洞入口等
6. NPC 倒地 → 复活后 inject_memory

---

## 2. TS 端（ValleyAI）改动

### 2.1 set_goal 工具 — `stardew-tools.ts`

```ts
{
  name: "set_goal",
  description: "给自己设定一个要完成的任务。设定后你会进入执行态，C# 会后台执行" +
    "（不消耗对话），完成后会寻路回来向你汇报。",
  parameters: {
    type: "object",
    properties: {
      type: { type: "string", enum: ["chop_tree", "mine", "water_crops", "fight", "forage"] },
      params: { type: "object", description: "如 {quantity: 10} 或 {targetItemId: '(O)378', quantity: 5}" },
      reportBack: { type: "boolean", description: "完成后是否寻路回玩家汇报", default: true },
    },
    required: ["type"],
  },
  execute: async (args, context) => {
    // 意图式：log 意图，返回 {action:"set_goal", type, params, reportBack} 交由 C# 执行
  },
}
```

### 2.2 状态流转 — `types.ts` + `protocol-adapter.ts`

- `STATES` union 加 `"EXECUTING_GOAL" | "TRAVELING_TO_REPORT"`（stardew-tools.ts L10-12）
- WorldSnapshot/SceneState 加 `currentGoal?: {type, params, progress, status} | null`
- world-snapshot-decoder.ts null-safe 解码
- protocol-adapter.ts：`routeToolResult` 加 `set_goal` 分支（写"设定了目标"记忆）；`handleStateChanged` 已泛化无需改
- prompt-builder.ts：当前场景段可选加「你正在执行的目标：{current_goal_desc}」

### 2.3 协议 — messages.json

worldSnapshot 字段描述加 `currentGoal`。改后 `bun run check:protocol`。

### 2.4 测试

- stardew-tools.test.ts：set_goal 工具 schema + execute 返回
- world-snapshot-decoder.test.ts：currentGoal 解码（有/无）
- prompt-builder.test.ts：目标描述注入

---

## 3. C# 端（ValleyTalk）改动

### 3.1 新文件 `src/ValleyAgent/Goals/IGoal.cs` + `GoalBase.cs`

```csharp
public enum GoalStatus { NotStarted, Executing, Complete, Failed, Cancelled }

public interface IGoal
{
    string Type { get; }                    // "chop_tree" | "mine" | ...
    GoalStatus Status { get; }
    string DescribeProgress();              // 供汇报 LLM
    bool ReportBack { get; }
    void Start(AgentInstance agent, NPC npc);
    void Tick(AgentInstance agent, NPC npc, int currentTick);
    void Cancel(string reason);
    int ElapsedGameMinutes { get; }         // 超时判定
}
```

GoalBase 实现公共逻辑：开始时间、全局超时（4 游戏小时）、collectedCount 跟踪（背包物品差量）、位置卡死检测（连续 N tick 位置不变且不在执行动作）。

### 3.2 五种 Goal

| Goal | 依赖 Handler | 终止条件 | 超时 |
|---|---|---|---|
| ChopTreeGoal | FarmHandler（SetForcedTarget chop） | 背包 Wood ≥ qty | 4 游戏小时 |
| MineGoal | MineHandler | 背包 targetItemId ≥ qty | 4 游戏小时 |
| WaterCropsGoal | FarmHandler（water） | 目标作物全部浇完（动作完成计数） | 4 游戏小时 |
| FightGoal | FightHandler | 区域怪物清除 / 到时间 | 4 游戏小时 |
| ForageGoal | ForageHandler | 背包采集物 ≥ qty | 4 游戏小时 |

### 3.3 超时与卡死

- 全局超时：游戏内 4 小时（ModConfig 可调）
- 位置卡死：连续 N tick 位置不变且不在执行动作 → 超时（复用 MovementService.HandleStuck 的 120-tick 语义）
- 资源耗尽：指定位置找不到目标资源类型 → 失败

### 3.4 寻路汇报

在 GoalExecutor 或独立 ReportBackCoordinator 实现（复用 WalkingBack 模式）。触发汇报 LLM：通过现有 dialogue 通道发一条"目标完成"通知，让 TS 端跑一次 set_goal 汇报对话。

### 3.5 单测

- 五种 Goal 的终止条件（注入模拟背包数据，验证 collectedCount 判定）
- 超时/卡死/取消
- 背包差量计算（基线快照 vs 当前）

---

## 4. 验证清单

- [ ] TS: `bun test packages/stardew` 全绿（397 + 新增）
- [ ] TS: `tsc --noEmit` 0 错
- [ ] TS: `check:protocol` PASS
- [ ] C#: 编译 0 警告
- [ ] C#: 单测覆盖五种 Goal 终止条件 + 超时/卡死/取消
- [ ] 游戏内实测：Shane set_goal(chop_tree) 真砍 + 回来汇报；超时场景

## 5. 风险与注意

- **Handler 单次动作退出 vs Goal 持续执行**：核心张力，用 GoalExecutor 包装 handler.Update + 重寻目标解决
- **不动 Handler 现有逻辑**：只通过 SetForcedTarget 委托，不改 Handler 内部
- **汇报 LLM 触发通道**：需确认现有 dialogue 触发机制能否从 C# 侧主动发起（若无，用 action_result + TS 端监听 set_goal 完成状态）
- 中文注释用 write 工具写（UTF-8）
