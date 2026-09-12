# E3-3 交易流程（NPC↔玩家）实现思路

> 分支：`feat/exec-e33` ｜ 基线：integration `1226983`（含 E3-1 钱包、E3-2 定价）
> 实现：直接实现（子智能体派发多次超时零产出，改为主会话直接编写测试核心 + 游戏面补丁）
> 需求来源：`docs/design/2026-08-02-npc-economy-hire-chat-requirements.md` §1.7-1.9

## 1. 目标

实现 NPC↔玩家交易：玩家右键 Agent NPC 弹出「送礼/交易/取消」菜单 → 交易 → 还价状态机（E3-2）产出成交价 → 生成待成交单 → 玩家献上物品 → 原子结算。

## 2. 模块划分与依赖

| 模块 | 文件 | 依赖 | 可单测 |
|---|---|---|---|
| 待成交单 | `PendingOffer.cs` + `PendingOfferRegistry.cs` | 纯逻辑 | ✅ |
| 原子结算 | `ITradeActor.cs` + `TradeSettlement.cs` | 纯逻辑 + ITradeActor | ✅ |
| Actor 适配 | `AgentInventoryTradeActor.cs` | 包 AgentInventory | ✅ |
| 右键菜单 | `Patches/NPCRightClickPatch.cs` | Harmony + 游戏 API | ❌（编译门禁）|

```
玩家右键 NPC
   └─ NPCRightClickPatch → 菜单「送礼 / 交易 / 取消」
         └─ 交易 → E3-2 HaggleStateMachine 还价 → 成交价
               └─ PendingOfferRegistry.TryCreate(NPC, offer {item, agreedPrice, expiry+30s})
                     └─ 玩家献出物品时 → TradeSettlement.Settle
                           ├─ 校验：物品存在 / 数量充足 / 双方余额充足
                           ├─ 原子转移：扣玩家物品 + NPC 钱包 → PC 钱包 + NPC 背包
                           └─ 任一步失败 → 无部分状态 + NPC 通过 action_result.reason 获知
```

## 4. PendingOfferRegistry（每 NPC 单待成交单）

- `ConcurrentDictionary<string, PendingOffer>`，key 用 `StringComparer.OrdinalIgnoreCase`。
- **单待成交单**：NPC 已有待成交单时 `TryCreate` 返回 false（强制先结算/作废旧的）。
- **30s 超时**：`PruneExpired()` 扫描 `ExpiresUtc`，到期自动作废。
- **走失/换址作废**：`VoidAllFor(npc)`（OnWarped 时调用）。
- `TryTake(npc, out offer)` — 原子取走（结算用，保证一次只结算一次）。
- 线程安全：锁内检查+写入。

`PendingOffer`: `NpcName, ItemId, ItemName, AgreedPrice, Direction(NpcBuysPlayerItem), ExpiresUtc`。

## 5. TradeSettlement（原子结算）

面向 `ITradeActor` 接口（`HasItem / TryRemove / TryAdd / Money / TrySpend / AddMoney`），
使纯逻辑可单测（用两个 AgentInventoryTradeActor 扮演买卖双方）。

结算方向 `NpcBuys`（NPC 买玩家物品）：
1. **预校验（全做，零副作用）**：
   - R1: 玩家有该物品且数量充足
   - R2: NPC 钱包 ≥ agreedPrice（买家付钱）
   - R3: NPC 背包有空间装下物品
2. **按序提交（每步都可回滚）**：
   - 扣玩家物品 `player.TryRemove(item, qty)`
   - NPC 钱包 `npc.TrySpend(price)`
   - NPC 背包 `npc.TryAdd(物品副本)`
   - 玩家钱包 `player.AddMoney(price)`
3. **失败回滚**：第 3/4/5 步任一步失败 → 回补已扣项，`Settled=false`，reason 明确。
4. 成功回执：`SettleResult{itemId, qty, price, settled=true}`。

每个 reason 映射话术模板（NPC 告知玩家的文案），供 LLM 决策用。