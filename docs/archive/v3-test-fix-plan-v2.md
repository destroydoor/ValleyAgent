# V3 测试修复计划 v2

> 基线: 1989 FAIL, 根因只有 6 类
> 1897 条 E1 spam = 每 tick 刷一条，不是 1897 个独立 bug

---

## 统计汇总

```
1989 FAIL = 1897x E1_spam + 91x E1_distance + 1x F5_Abigail
+ 其他非 spam FAIL (E4/E5/E6/E7/E8/Func/Real)
```

| 唯一 FAIL | 次数 | 文件 | 根因 |
|-----------|:---:|------|------|
| NPC_has_controller_at_some_point | 1897 | E1 | MovementService walkable 判定全 False |
| NPC_stays_in_range | 91 | E1+Real | 同上，NPC 站原地看着玩家走远 |
| Both_NPCs_remain_in_valid_states | 1 | F5 | MaxAgentNpcs=1，Abigail 未分配 |
| Player inventory stayed at 12 | 1 | E4 | 背包满溢出处理 |
| Monster loot appeared on ground | 1 | E4 | 怪死了没掉东西 |
| NPC is alive | 1 | E5 | 死亡测试没成功复活 |
| NPC is in IDLE state | 1 | E5 | 状态残留 FIGHT |
| Decision reason mentions weather | 2 | E6 | prompt 里没注入天气/节日上下文 |
| NPC stayed near original position | 1 | E7 | NPC 走远了 12 格 |
| NPC is alive after full day | 1 | E8 | NPC 死了没复活 |
| NPC is on map after day cycle | 1 | E8 | NPC 不在任何地图 |
| Phase1-5 各 1 个 | 5 | Func | Haley 角色性格拒绝干活（不是 bug） |
| Phase2-6 各 1-2 个 | 8 | Real | NPC 已死/位置损坏 |

---

## 按优先级排序

### P0: MovementService walkable=False（级联根因）

**现象**: 所有 call `IsTileWalkable` 的位置返回 False
**影响**: 所有寻路失败，NPC 无法移动 → E1 1897 条 spam
**位置**: `MovementService.cs` 的 `IsTileWalkable` 方法
**修法**: 
  1. 读 IsTileWalkable 实现
  2. 在 Farm 地图上验证 {54,17} {32,30} 是否真不可走（可能是地图 mod 改了碰撞层）
  3. 若不是地图问题 → 修判定逻辑
**不做的**: 不绕过 walkable 检查，不降低标准

### P1: 测试设计问题（不挡功能，但挡测试通过）

| # | 问题 | 文件 | 修法 |
|---|------|------|------|
| P1a | E1 每 tick 刷一条 FAIL spam | E1 | 断言冷却：同一断言 120 tick 内不重复 |
| P1b | NPC 死后不复活 | V3TestRunner | AdvanceTest() 已加 TryRevive，确认生效 |
| P1c | E5 FOLLOW 残留 | E5 | Setup 先 TrySetAgentState("IDLE") |
| P1d | E6 天气/节日未注入 | E6 | 测试先改天气再查决策 |
| P1e | Func Haley 不配合 | Func | 用高好感度 NPC 测（或接受角色性格） |
| P1f | Real NPC 已死 | Real | 利用 TryRevive + 加血量检查 |

### P2: 非关键（接受或改测试标准）

| # | 问题 | 建议 |
|---|------|------|
| P2a | E4 怪物不掉 loot | 改怪物 spawn 确保掉落 |
| P2b | F5 Abigail 不分配 | 改 MaxAgentNpcs=2 或 skip 断言 |
| P2c | E7 NPC 走远 12 格 | 降低距离阈值 |

---

## 执行顺序

```
修 1: MovementService IsTileWalkable（P0，核心）
修 2: V3TestRunner TryRevive 确认生效（P1b）
修 3: E1 断言冷却（P1a）
修 4: E5/E6 测试设计（P1c, P1d）
修 5: E4/E7/Func/Real 细节（P2）
```

---

## 禁止
- ❌ 不开游戏就认为修好了
- ❌ 批量正则替换
- ❌ 降低 walkable 判定标准
- ❌ 跳过失败断言来"通过"测试
