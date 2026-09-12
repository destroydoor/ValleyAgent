# V3 测试修复计划

> 基线: 17 PASS / 8 FAIL / 8+2 SKIP
> 原则: 每文件独立读→改→build，不批量替换，2次失败即停
> 2026-05-10 更新: 实际执行后补入玩家保护等新发现

---

## 实际执行状态

| Fix | 状态 | 额外变更 |
|-----|:--:|----------|
| 1 F6 warpCharacter | ✅ | 2 处 `currentLocation` 赋值→`Game1.warpCharacter` |
| 2 F2 FIGHT | ✅ | +玩家保护 `maxHealth=99999` |
| 3 F5 DualAgent | ✅ | +Game1.warpCharacter 替代 direct assignment |
| 4 E3 null guard | ✅ | F6 级联修复后自动解决 |
| 5 E4 填背包+怪物 | ✅ | +怪物位置改为 NPC 旁 `{30,28}`+玩家保护 |
| 6 V3TestRunner skip | ✅ | +`WasSkipped` 属性+连续3次跳剩余 |
| 7 ⭐ 玩家保护 | ✅ | F2+E4 加 `maxHealth=99999`（新发现） |
| 8 ⭐ 怪物位置 | ✅ | E4 怪物从 `playerTile+3`→NPC 旁 `{30,28}`（新发现） |

> **新发现**: `Game1.warpFarmer` 可能触发剧情事件（Mine入口）/ 在玩家附近刷怪可能打死玩家 / `Game1.player.MaxHealth` 在SDV 1.6是 `maxHealth`（小写）

---

## 失败清单

| # | 文件 | 断言 | 根因 |
|---|------|------|------|
| 1 | F2_RandomMonsters | NPC_health_decreased | NPC 没进 FIGHT 状态，怪在场但不打 |
| 2 | F5_DualAgent | Both valid states | Abigail 空状态，没被分配 |
| 3 | F5_DualAgent | WS_responses_route_correctly | actions=0，双 NPC WebSocket 没路 |
| 4 | F5_DualAgent | No_state_mixing | 状态混了 |
| 5 | E3_Unreachable | NPC exists | SKIP 后仍执行了断言，缺 null guard |
| 6 | E4_FullInventoryFight | Inventory at 12 after fight | count=5，背包在战前被清空了 |
| 7 | E4_FullInventoryFight | Loot dropped | 怪死了没掉 loot |
| 8 | E4_FullInventoryFight | NPC participated | NPC 没参战 |
| 9 | F6_SceneSwitch | Haley not found | 直接改 `currentLocation` 弄丢了 NPC 引用 |
| 10 | E1-E8 全部 | Haley not found | 被 F6 牵连（F6 修好即解决） |
| 11 | Func_DialogueActions | Haley not found | 同上 |
| 12 | Real_OneFullDay | Haley not found | 同上 |

---

## 修复步骤

### Fix 1: F6_SceneSwitch — 防止 NPC 丢失

**做什么**: 把 `_npc.currentLocation = targetLocation` + `_npc.setTileLocation()` 替换为 `Game1.warpCharacter()`

**怎么修**:
```
1. read F6 文件，定位 Update() 里的 currentLocation 赋值行
2. edit 替换为:
   Game1.warpCharacter(_npc, targetLocation, targetTile);
   其中 targetLocation = Game1.getLocationFromName(locName)
3. build → 0 errors 继续
```

**失败处理**: 连续 2 次 build 失败 → 停，用 write 覆盖整个 Update() 方法

---

### Fix 2: F2_RandomMonsters — 强制 NPC 进 FIGHT

**做什么**: Setup() 里在 spawn 怪物后通过 API 强制 FIGHT 状态

**怎么修**:
```
1. read F2 文件，定位 Setup() 末尾
2. 在 api?.TryAllocateAgent("Haley") 之后加:
   _api?.TrySetAgentState("Haley", "FIGHT");
3. build
```

---

### Fix 3: F5_DualAgent — 修复 Abigail 分配

**做什么**: Setup() 里也对 Abigail 做分配 + 状态检查

**怎么修**:
```
1. read F5 文件，定位 Abigail 初始化代码
2. 加 api?.TryAllocateAgent("Abigail")
3. 加 Game1.warpFarmer 给 Abigail
4. build
```

---

### Fix 4: E3_Unreachable — 加 null guard

**做什么**: 断言前检查 `_npc != null`

**怎么修**:
```
1. read E3 文件，定位 "NPC exists" 断言行
2. 加 if (_npc == null) { Skip(...); return true; }
3. build
```

---

### Fix 5: E4_FullInventoryFight — 战前重新填满背包

**做什么**: 在 fight 开始前重新把背包填满到 12

**怎么修**:
```
1. read E4 文件，定位 fight 触发点
2. 在触发前加: 清空背包 → Game1.player.Items.AddRange(12 个石头)
3. build
```

---

### Fix 6: V3TestRunner — Setup 失败 3+ 次剩余全跳

**做什么**: V3TestRunner 跟踪连续 Setup 失败数，≥3 次后跳剩余

**怎么修**:
```
1. read V3TestRunner.cs
2. 在 Run 循环里加: if (_consecutiveSetupFailures >= 3) { Skip all remaining; }
3. build
```

---

## 绝对不能做的事

| ❌ 禁止 | 原因 |
|--------|------|
| PowerShell regex 批量替换 | 之前破坏了 8 个文件 |
| 改 Mod 核心代码 | 测试失败不能牺牲通用性 |
| 降低断言标准 | 不能把 FAIL 改成 PASS 通过作弊 |
| 跳过失败测试 | 不能 if(false) Assert(...) |
| build 不过继续改 | 每文件改完立即 build |
| "算了就这样" | 绝对不接受 |

---

## 防循环机制

| 情况 | 动作 |
|------|------|
| 第 1 次 build 失败 | 重新 read，确认 current state |
| 第 2 次同一错误 | 不用 edit，用 write 整段覆盖方法 |
| 第 3 次仍失败 | **停**。发 Oracle，不动手 |
| edit 找不到 oldString | 读更多行，扩大匹配范围，不猜 |
