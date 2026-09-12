# ValleyAgent 需求汇总与实施计划

## 需求总览

| # | 需求 | 优先级 | 状态 |
|---|---|---|---|
| 1 | 测试NPC改为 **Abigail** | P0 | ✅ 代码已完成 |
| 2 | **NPC血量系统** — 每个Agent NPC有独立HP/MaxHP，战斗中扣血/回血 | P0 | 待实现 |
| 3 | **12格NPC背包** — 收获/采集/挖掘的物品直接进入背包，不再掉地上 | P0 | 待实现 |
| 4 | **主动说话/表情工具** — 为LLM/API暴露 `Say()` / `Emote()` 方法 | P1 | 待实现 |
| 5 | **修复 Farm 收割失败** — NPC走到作物旁但 `harvest()` 返回false | P0 | 待修复 |
| 6 | **修复 Mine 挖矿失败** — `IsBreakableStone()` 对代码生成的石头返回false | P0 | 待修复 |
| 7 | **修复 Forage 采集失败** — 同Mine，采集逻辑可能未正确执行 | P0 | 待修复 |
| 8 | **修复 Gift 失败** — `SelectGiftForNpc` 对Abigail返回null（RAG池为空） | P0 | 待修复 |
| 9 | **修复 Thinking 状态** — NPC在THINKING时仍被WanderingSpouses等mod推动 | P0 | 待修复 |

---

## 详细实施方案

### 阶段一：基础数据层（血量 + 背包）

#### 1.1 AgentInventory — 12格NPC背包

新建 `ValleyAgent/Inventory/AgentInventory.cs`：
- `Item[] slots = new Item[12]`
- `bool TryAdd(Item item)` — 找空槽或堆叠放入
- `bool TryRemove(string itemId, int count, out Item item)`
- `Item[] GetAllItems()` — 返回当前所有物品快照
- `int GetFreeSlotCount()`
- 挂到 `AgentInstance` 上作为 `public AgentInventory Inventory { get; }`

修改采集/收割/挖矿逻辑：
- `FarmHandler.HarvestCrop` → 收获成功后，把产物 `TryAdd` 进背包，不再 `Game1.createItemDebris`
- `MineHandler.MineObject` → 同上
- `ForageHandler.PerformForage` → 同上
- `GiftSystem.TryTriggerGift` → 从NPC背包中取出物品给玩家

#### 1.2 AgentHealth — NPC血量系统

在 `AgentInstance` 上添加：
- `public int Health { get; set; } = 100`
- `public int MaxHealth { get; } = 100`
- `public bool IsDead => Health <= 0`
- `public void TakeDamage(int amount)`
- `public void Heal(int amount)`

修改 `FightHandler`：
- NPC攻击怪物时，怪物反击逻辑（简化版：若NPC在怪物旁边，每几秒受到少量伤害）
- 增加 `CheckNpcHealth` 紧急状态：若 `Health < 30` 自动切换到 `FOLLOW`（逃跑/求助）

修改 `AgentHUD` / `OnRenderedWorld`：
- 在NPC头顶绘制简易血条（绿条）

---

### 阶段二：行为修复

#### 2.1 修复 Farm 收割

问题诊断：
- `TestScenes.SpawnFarmScene` 使用 `new Crop("(O)472", ...)`，但 `Crop` 构造函数在 SDV 1.6 中可能不接受 QualifiedItemId（带 `(O)` 前缀）。
- `crop.harvest(x, y, dirt)` 可能因产物无处可放而返回 false。

修复方案：
1. `TestScenes` 中 Crop ID 改为 `"472"`（去掉 `(O)` 前缀）
2. `FarmHandler.HarvestCrop` 在调用 `harvest()` 前，先把产物逻辑改为：若 harvest 返回产物则放入背包；若返回 false 则手动移除作物并模拟收获

#### 2.2 修复 Mine 挖矿

问题诊断：
- `ItemRegistry.Create<SObject>("(O)343")` 创建的石头，`IsBreakableStone()` 可能返回 false（因为不是从地图数据加载的）

修复方案：
- `MineHandler.IsBreakableStone` 增加兜底：若 `obj.Name == "Stone"` 或 `obj.Category == -2`（矿物/资源类）也判定为可破坏
- `MineHandler.MineObject` 不再依赖 `IsBreakableStone()`，直接用 qualified item id 判断

#### 2.3 修复 Forage 采集

问题诊断：
- 与Mine类似，代码生成的可采集物可能某些标志位不正确

修复方案：
- `ForageHandler.IsForageableObject` 放宽判定：只要是 `SObject` 且 `CanBeGrabbed == true` 即可采集（不强制要求 `IsSpawnedObject`）
- 增加手动移除物体的兜底逻辑

#### 2.4 修复 Gift

问题诊断：
- `GiftSystem.BuildPoolFromRAG("Abigail")` 若RAG中无Abigail的礼物偏好数据，则返回空列表
- `TryTriggerGift` 无fallback机制

修复方案：
- 在 `GiftSystem.SelectGiftForNpc` 中增加硬编码通用礼物池（如：Amethyst, Chocolate Cake, Pufferfish 等常见物品），当RAG池为空时作为fallback
- 或者直接在 `ValleyAgentApi.TryTriggerGift` 中绕过 GiftSystem，用 `ItemRegistry.Create` 创建一个通用物品（如 `Parsnip`）并直接送入玩家背包

#### 2.5 修复 Thinking 状态

问题诊断：
- `ModEntry.ApplyControllerToNpc` 的 `THINKING` case 只设置了 `npc.Halt()` 一次，但 `WanderingSpouses` mod 在下一tick会重置NPC

修复方案：
- 在 `ApplyControllerToNpc` 的 THINKING case 中，每tick都执行：
  ```csharp
  npc.controller = null;
  npc.Halt();
  npc.followSchedule = false;
  npc.ignoreScheduleToday = true;
  npc.movementPause = 10; // 暂停移动
  ```
- 考虑在 `AgentInstance` 上增加 `IsMovementLocked` 标志，在 `OnUpdateTicked` 中全局强制halt

---

### 阶段三：主动说话/表情工具

新建 `ValleyAgent/Expression/AgentExpressionSystem.cs`：
- `Say(string text, int durationMs = 3000)` → 调用 `npc.showTextAboveHead(text, duration)`
- `Emote(int emoteIndex)` → 调用 `npc.doEmote(emoteIndex)`
- `SayWithEmote(string text, int emoteIndex)` → 两者一起

暴露到 `IValleyAgentApi`：
- `bool TrySpeak(string npcName, string text, int durationMs)`
- `bool TryEmote(string npcName, int emoteIndex)`

暴露到 `AgentInstance`：
- `public AgentExpressionSystem Expression { get; }`

LLM工具调用（未来扩展）：
- 在 `AIDecisionEngine` 的 prompt 中增加工具描述，让LLM可以通过结构化输出来触发说话/表情

---

### 阶段四：测试适配

修改 `TestRunner` / `TestAssertions`：
1. **Health Phase**：新增 `HEALTH` 测试阶段：强制NPC扣血，验证 `GetNpcHealth()` API
2. **Inventory Phase**：新增 `INVENTORY` 阶段：触发收获/挖矿后检查背包非空
3. **Expression Phase**：新增 `EXPRESSION` 阶段：调用 `TrySpeak`/`TryEmote` 并验证游戏内表现

---

## 实施顺序建议

按依赖关系排序：

```
1. AgentInventory（基础数据结构）
2. AgentHealth（基础数据结构）
3. 修复 Farm / Mine / Forage（把收获结果导向背包）
4. 修复 Gift（从背包/通用池取物）
5. 修复 Thinking（每tick强制halt）
6. AgentExpressionSystem（独立模块）
7. API暴露 + 测试适配
8. 编译验证 → 游戏测试
```

---

## 风险与注意事项

1. **SDV 1.6 API 兼容性**：`Crop` 构造函数、`harvest()` 返回值、`IsBreakableStone()` 行为在1.6中可能有变化，需要实际测试验证
2. **Pintail API 限制**：接口不能返回 `IReadOnlyList<T>`，不能有空引用类型。背包/血量API需使用 `string[]` 和基本类型
3. **WanderingSpouses mod 冲突**：该mod每tick重置NPC位置，Thinking状态的修复可能需要更激进的措施（如临时修改NPC的schedule）
4. **存档兼容性**：血量/背包数据需通过 `SaveDataManager` 持久化，避免读档后丢失
