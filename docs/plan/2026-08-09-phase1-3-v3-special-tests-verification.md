# V3 三阶段专项测试 + 修复验证记录

> **Created:** 2026-08-09
> **依据:** `docs/plan/2026-08-06-execution-audit-report.md` §4（☐ 游戏内实测遗留）
> **范围:** 补三阶段游戏内专项覆盖（trade 结算 / set_goal 砍树 / Director 工具）+ 修复实测发现的 3 个真实 bug

---

## 1. 新增 V3 测试（游戏内实测）

### IT11_Trade_Settlement（阶段 1 交易结算）

覆盖链路：`ExecuteAction(trade)` → `CommandExecutor.ExecuteTrade` 写 PendingOffer →
`NPCGiftPatch.TrySettlePendingTrade`（反射触发玩家献出物品）→ `TradeSettlement.SettleNpcBuys` 原子结算。

| 断言 | 结果 |
|---|---|
| pending_offer_written（待成交单写入注册表） | PASS |
| settlement_succeeded（结算成功） | PASS |
| player_money_increased（玩家 +50） | PASS |
| npc_money_decreased（NPC -50） | PASS |
| player_item_decreased（玩家背包 -5 Wood） | PASS |
| npc_item_increased（NPC 背包 +5 Wood） | PASS |
| insufficient_settlement_rejected（钱包不足拒绝） | PASS |
| insufficient_npc_money_untouched（失败回滚，NPC 钱保留） | PASS |
| state_still_readable | PASS |

### IT12_SetGoal_ChopTree（阶段 2 set_goal 执行态）

覆盖链路：`set_goal(chop_tree, qty=5, reportBack=false)` → `GoalExecutor.CreateGoal` →
`EXECUTING_GOAL` 状态 → `AgentTickLoop` 驱动 `ChopTreeGoal.TickCore` 砍树 →
背包差量达成 → FinalizeSuccess 收尾。

| 断言 | 结果 |
|---|---|
| goal_created（PendingGoal=ChopTree） | PASS |
| state_executing_goal（EXECUTING_GOAL） | PASS |
| wood_harvested（背包 +5 Wood，日志：`chopped tree (+5 wood)`） | PASS |
| goal_cleared_after_complete（PendingGoal 清空） | PASS |
| state_left_executing（回 IDLE） | PASS |
| state_still_readable | PASS |

### IT13_DirectorTools（阶段 3 Director 工具集）

覆盖 9 工具中的 6 个 + 未知工具拒绝：set_npc_money / set_npc_mood / set_npc_position /
spawn_beat / set_npc_recent_events / inject_memory。13 断言全 PASS。

## 2. 实测发现的真实 bug（已修复）

### Bug-1：trade 结算金额未乘数量（阶段 1）

- **现象**：IT11 断言 `player money 500 → 510`（预期 +50）。5 个 Wood × 10g 只结算了 10g。
- **根因**：`trade` 工具参数 `price` 是**单价**，但 `PendingOffer.AgreedPrice` / `TradeSettlement.SettleNpcBuys`
  的 `agreedPrice` 是**成交总价**（`buyer.Money < agreedPrice` 按总价校验）。`CommandExecutor.ExecuteTrade`
  直接把单价传入 AgreedPrice，未乘 quantity。
- **修复**：`CommandExecutor.cs` ExecuteTrade 改 `totalPrice = price * quantity` 再写入 PendingOffer。
- **修复后**：+50/-50 正确；钱包不足反例（NPC 10g < 50g）正确拒绝且资产回滚。

### Bug-2：ChopTreeGoal 冷却溢出导致永远不砍树（阶段 2）

- **现象**：IT12 `wood_harvested` 失败，`[Goal] ... [Failed(stuck)]`，NPC 原地 120 tick 不动被误判卡死。
- **根因**：`ChopTreeGoal._lastChopTick` 初始 `int.MinValue`，`currentTick - int.MinValue` 在 C# 中
  溢出为负数 → 永远满足 `currentTick - _lastChopTick < ChopCooldownTicks`（冷却中）→ 砍树分支永不执行。
- **修复**：`ChopTreeGoal.cs` 冷却判定显式排除 `_lastChopTick == int.MinValue`（未砍过）。
- **修复后**：首个 tick 即砍树（`chopped tree (+5 wood)`），Goal Complete，状态回 IDLE。

### Bug-3：NpcEconomyProfileData 只读属性导致经济档案从未加载（遗留）

- **现象**：5 个 `NpcEconomyProfileLoaderTests` 失败（GetProfile 全返回 null）。
- **根因**：`Name`/`BudgetTier` 是无 setter 的只读属性，System.Text.Json 反序列化无法写入 →
  `entry.Name` 恒空串 → 整表 `continue` 跳过 → **生产环境 NPC 经济档案从未成功加载，钱包全为 0**。
- **修复**：`NpcEconomyProfileLoader.cs` 两个属性加 setter。
- **修复后**：579/579 单测全绿。

## 3. 环境问题修复：IdleEviction 长跑冲突

`CheckInteractionIdleEviction`（90s 互动空闲淘汰）在 TestMod 存在时跳过淘汰。
2026-08-06 V3 记录中 15 个 env-timing fail 的根因（测试中途 NPC 被逐出 → agent not found）由此消除。
IT04（AllocationManager 层 ForceAllocate 淘汰事件）不受影响。

## 4. 验证结果

| 门禁 | 结果 |
|---|---|
| TS `bun test packages/stardew` | **419 pass / 0 fail** |
| TS `tsc --noEmit` | **0 错误** |
| TS `check:protocol` | **PASS（exit 0）** |
| C# 编译 | **0 警告 0 错误** |
| C# 单测（ValleyAgent.UnitTests） | **579/579 pass**（原 574 + 修复 5） |
| 游戏内 V3（IT11/IT12/IT13） | **28/28 断言 PASS**（9+6+13） |

## 5. 遗留

- 全量 V3（Fuzzy\|Integration）回归未跑：三个新测试 + IdleEviction 豁免已验证，全量跑待下次部署验证。
- Director 开放问题（token 预算 / group beat 可见性）仍按执行计划 §开放问题 待细化。
