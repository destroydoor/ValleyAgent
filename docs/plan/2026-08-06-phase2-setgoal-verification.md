# 阶段 2 验证记录 — set_goal + GoalExecutor

> **Created:** 2026-08-06
> **依据:** `docs/plan/2026-08-05-three-tier-architecture-execution-plan.md` 阶段 2
> **状态:** 已合并至 master，验证门禁全绿

---

## 1. 实施内容

### TS 端（ValleyAI，commit `8eb703d`，14 文件 +204/-8）

| 条目 | 内容 |
|---|---|
| 2.1.1 set_goal 工具 | `stardew-tools.ts` 新增意图式 set_goal 工具：params {type enum 五种, params 自由对象, reportBack 默认 true}，required ["type"]；execute 只写意图日志返回 `{action:"set_goal", type, params, reportBack}`，结算由 C# GoalExecutor 执行 |
| 2.1.2 状态流转 | STATES union 加 `EXECUTING_GOAL` / `TRAVELING_TO_REPORT`（set_state 共享同一 union 单源）；types.ts WorldSnapshot+SceneState 加 `currentGoal: {type, params, progress, status}`；decoder null-safe 解码 |
| 2.1.2 协议适配 | protocol-adapter.ts routeToolResult 加 set_goal 分支（callId 存意图类型 → action_result 写「我给自己设定了目标：{type}」/失败「我设定的目标没成立」） |
| 2.1.3 prompt | 当前场景段加「你正在执行的目标：{current_goal_desc}」（有目标显示 type+progress，无目标不输出） |
| 协议 | messages.json worldSnapshot 描述加 currentGoal |
| 测试 | +10 新增（set_goal schema/execute/reportBack/set_state 新状态；currentGoal 解码 ×4；目标描述注入 ×2）；工具计数 14→15 |

### C# 端（ValleyTalk，commit `7981e2f`，20 文件 +2353/-4）

| 条目 | 内容 |
|---|---|
| 2.2.1 GoalExecutor | `Goals/GoalExecutor.cs`（450 行）：CreateGoal + per-tick Tick + 状态流转 + 汇报触发；钩子接入 AgentTickLoop.ProcessAgent + EventHandlerInitializer + ServiceInitializer |
| 2.2.1 AgentBrain.PendingGoal | Abstractions/Brain/AgentBrain.cs 加 PendingGoal |
| 2.2.2 五种 Goal | `Goals/{ChopTreeGoal,MineGoal,WaterCropsGoal,FightGoal,ForageGoal}.cs` + GoalBase（公共：基线快照/背包差量/超时/位置卡死）+ GoalCompletionEvaluator（纯静态，可单测） |
| 2.2.2 完成判定 | **物品收集量**模型（背包差量 ≥ qty），非 GoalVerifier 环境差量——设计决策 §1.1 |
| 2.2.3 超时判定 | 全局 4 游戏小时（ModConfig 可调）+ 位置卡死 + 资源耗尽 |
| 2.2.4 寻路汇报 | 完成 + reportBack=true → MoveTo(玩家) → TRAVELING_TO_REPORT → 距离 <5 格触发汇报；超时 2 小时 fallback |
| 状态机 | AgentState 加 EXECUTING_GOAL/TRAVELING_TO_REPORT；AgentStateMachine.AllowedTransitions 加对应边（含 IDLE/FOLLOW/FARM/MINE/FIGHT/FORAGE/TALK → EXECUTING_GOAL） |
| 配置 | ModConfig 加 GoalTimeoutGameHours / ReportBackTimeoutGameHours / ArrivalDistanceTiles |
| CommandExecutor | case "set_goal" 接线 → GoalExecutor.CreateGoal |
| 测试 | 3 新文件 66 测试：GoalCompletionEvaluatorTests / GoalExecutorTests / GoalTerminationTests（背包差量 + 五种终止条件 + 超时/卡死/取消） |

## 2. 验证结果

| 门禁 | 结果 |
|---|---|
| TS `bun test packages/stardew` | **407 pass / 0 fail**（397 基线 + 10 新增） |
| TS `tsc --noEmit` | **0 错误** |
| TS `check:protocol` | **PASS（exit 0）** |
| C# 编译 | **0 警告 0 错误** |
| C# 单测（Goals） | **66/66 通过** |

## 3. 关键设计决策

1. **Goal 完成判定 = 物品收集量（背包差量）**，非 GoalVerifier 环境差量。GoalVerifier 保留给 TestMod 审计，语义不同互不干扰。
2. **Handler 单次动作自动退出 vs Goal 持续执行**：GoalExecutor 包装 handler.Update，检测 handler 退出（状态回 IDLE）但 Goal 未完成 → 重寻目标 + SetForcedTarget + 恢复 EXECUTING_GOAL。
3. **chop_tree 委托差异**：FarmHandler 的 forced-target 只支持 water/harvest（HoeDirt），无 chop 支持——ChopTreeGoal 自带砍树逻辑（找树 → 砍 → 木头入 NPC 背包），其余四种 Goal 走 handler 委托。

## 4. 合并记录

- ValleyAI master ← `feat/phase2-setgoal`（fast-forward，HEAD `8eb703d`）
- ValleyTalk master ← `feat/phase2-goalexecutor`（fast-forward，HEAD `7981e2f`）
- 无冲突；两仓库各含阶段 1 历史

## 5. 遗留待办（阶段 2 范围外）

- 游戏内实测（Shane set_goal(chop_tree) 真砍 + 回来汇报 + 超时场景）待游戏环境运行
- C# 端汇报 LLM 触发通道的实际接通（TS protocol-adapter 已就绪，需游戏内验证 action_result 流）
