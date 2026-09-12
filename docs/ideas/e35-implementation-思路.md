# E3-5 NPC 求购（NpcPurchaseRequestService）实现思路

> 分支：`feat/exec-e35`（基于已含 E3-1/E3-2/E3-3 的集成分支）
> 日期：2026-08-03
> 依据：`docs/plan/2026-08-03-phase3-5-full-execution-plan.md` A5（E3-5）

## 1. 需求

NPC 主动发布求购信息：格斯收食材、克林特收矿石等。玩家把对应物品交给 NPC 即成交，
NPC 钱包按求购价扣减（L4 不变量：扣减后不为负）。

- **求购生成**：逐人设（npc_economy.json 新增 `purchaseItems` 偏好），每日频率受限
- **求购价**：1.0~1.1× 公道价（PricingEngine.CalculateFairPrice），确定性哈希取倍率
- **交付成交**：复用 E3-3 结算路径——玩家献出物品时命中待成交单 → 原子结算（钱包扣减 + 物品转移）
- **发布**：聊天栏公告（复用 E2-2 聊天栏路由）

## 2. 设计决策

### 2.1 独立 Registry（长 TTL）

E3-3 的 `PendingOfferRegistry` 默认 30s TTL（还价时效），求购单需要**当天有效**。
因此 NpcPurchaseRequestService **自持一个长 TTL 的 PendingOfferRegistry**（默认到当天结束，
取 `TimeSpan.FromHours(20)` 兜底），不污染还价 30s 语义。

### 2.2 交付路径（NPCGiftPatch 双 Registry）

`NPCGiftPatch.TrySettlePendingTrade` 先查 `PendingOffers`（还价单），未命中再查
`PurchaseOffers`（求购单）——命中求购单同样走 `TradeSettlement.SettleNpcBuys` 原子结算。

### 2.3 求购价

```
fair = PricingEngine.CalculateFairPrice(item.SalePrice)   // E3-2 公道价
multiplier = 1.0 + (FNV(npcName+itemId+gameDate) % 11) / 100.0   // 1.00~1.10
price = max(1, (int)(fair * multiplier))
```

### 2.4 频率限制

每 NPC 每天最多 `MaxRequestsPerNpcPerDay`（默认 1）条求购；同 NPC 已有未过期求购单时
不重复生成（Registry 单待成交单约束）。

### 2.5 数据

`npc_economy.json` 每 NPC 增加 `purchaseItems: ["(O)xxx", ...]`——格斯收食材、
克林特收矿石（铁/铜/金锭）、阿比盖尔收石英/紫水晶等。`NpcEconomyProfile` 增加
`PurchaseItems` 字段（默认空表，老数据不破坏）。

## 3. 改动文件

| 文件 | 改动 |
|---|---|
| `src/ValleyAgent/Economy/NpcPurchaseRequestService.cs` | **新增**：求购生成 + 长 TTL Registry + 频率限制 |
| `src/ValleyAgent/Economy/NpcEconomyProfile.cs` | 增加 `PurchaseItems` 字段 |
| `src/ValleyAgent/Economy/NpcEconomyProfileLoader.cs` | 解析 `purchaseItems` |
| `src/ValleyAgent/Data/npc_economy.json` | 每 NPC 增加 `purchaseItems` |
| `src/ValleyAgent/Patches/NPCGiftPatch.cs` | 新增 `PurchaseOffers` 静态 + TrySettlePendingTrade 双 Registry 查询 |
| `src/ValleyAgent/Initialization/EventHandlerInitializer.cs` | 接线：ServiceInitializer 注册 + OnDayStarted 生成求购 + OnPlayerWarped 作废 |
| `src/ValleyAgent/Initialization/ServiceInitializer.cs` | 注册 NpcPurchaseRequestService |
| `src/ValleyAgent.UnitTests/NpcPurchaseRequestServiceTests.cs` | **新增**：频率上限、倍率区间、交付后钱包扣减 |

## 4. 验证

- `dotnet build src/ValleyAgent` → 0 警告 0 错误
- `dotnet test src/ValleyAgent.UnitTests` → 全部通过（含 E3-5 新测）
- L4 不变量：扣减后钱包不为负（TradeSettlement 前置 buyer.Money < price 拒绝，测试覆盖）
