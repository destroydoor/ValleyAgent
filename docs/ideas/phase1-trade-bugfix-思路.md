# 阶段 1 实现思路 — 交易 bug 修复

> **Created:** 2026-08-06
> **设计依据:** `docs/design/2026-08-05-three-tier-architecture-redesign.md` §8
> **执行计划:** `docs/plan/2026-08-05-three-tier-architecture-execution-plan.md` 阶段 1
> **基线:** ValleyTalk 78ed1b8 → 2eb0d91；ValleyAI d181c07

---

## 0. 现状核对结论（动手前验证）

阶段 1 目标是修复 Shane/Marnie/Haley 三个交易 bug。核对后发现 **C# 端大部分已由 E 系列工作实现**，真正缺口集中在 TS 端：

| 计划条目 | 现状 | 处置 |
|---|---|---|
| 1.1.1 TS trade 工具 | **MISSING** | 新建 |
| 1.1.2 TS playerHeldItem 类型+decoder | **MISSING**（C# 已发但 TS 不认识） | 新建 |
| 1.1.3 TS prompt 交易规则 | **MISSING** | 新建 |
| 1.1.4 TS give_item item_id 描述 | **MISSING** | 补描述 |
| 1.2.1 C# worldSnapshot PlayerHeldItem | **已实现**（E3-6，WorldSnapshotBuilder.cs:84 + record IAgentServerProvider.cs:63，marketPrice 用 sellToStorePrice 公道价） | 不动，验证即可 |
| 1.2.2 C# ExecuteTrade | **已实现**（CommandExecutor.cs:279-341，TradeDirection.NpcBuysPlayerItem 方向正确 + PendingOffers 30s TTL + NPCGiftPatch.TrySettlePendingTrade 交接结算） | 不动，验证即可 |
| 1.2.3 C# give_item 名称回落 | **MISSING**（CommandExecutor.cs:239 直接 ItemRegistry.Create 失败即 ItemNotFound） | 新建 |
| 1.2.4 C# case "trade" 死代码 | **已接线**（CommandExecutor.cs:109-112 调 ExecuteTrade） | 不动 |

**关键语义确认**：交易方向 = NPC 是买家（`TradeDirection.NpcBuysPlayerItem`），符合计划"NPC 是买家"要求。结算走"意图 + 玩家物理交接物品"路径（PendingOffer 30s TTL → NPCGiftPatch 收到玩家物品时 TrySettlePendingTrade → TradeSettlement.SettleNpcBuys 原子 4 步），保留作为礼物流程的延伸（设计文档 §14.1 待决项 3 裁决：保留）。

---

## 1. TS 端（ValleyAI）改动

### 1.1 新增 trade 工具 — `packages/stardew/src/stardew-tools.ts`

参照 receive_payment 的意图式模式（L106-121）新增：

```ts
{
  name: "trade",
  description: "与玩家交易：你是买家，按 price 单价购买玩家手里 quantity 个 item_id。" +
    "只有玩家手持该物品时才能成交。达成一致必须调用本工具，不能只嘴上说。",
  parameters: {
    type: "object",
    properties: {
      item_id: { type: "string", description: "物品的 QualifiedItemId，如 (O)388" },
      quantity: { type: "number", description: "购买数量" },
      price: { type: "number", description: "单价（g）" },
      direction: { type: "string", enum: ["npc_buys"], description: "固定为 npc_buys" },
    },
    required: ["item_id", "quantity", "price"],
  },
  execute: async (args, context) => {
    // 意图式：log 意图，返回 {action:"trade", ...} 交由 C# 执行
  },
  visibility: "llm_visible",
}
```

返回 `{action: "trade", itemId, quantity, price, direction: "npc_buys"}`。

### 1.2 worldSnapshot 加 playerHeldItem — `types.ts` + `world-snapshot-decoder.ts`

**types.ts** WorldSnapshot（L17-38）加可选字段；SceneState（L176-197）加 `playerHeldItem`（null 默认）：
```ts
playerHeldItem?: { itemId: string; name: string; qty: number; marketPrice: number } | null
```

**world-snapshot-decoder.ts**（L5-33）加 null-safe 解码（仿 L25/L27/L29 模式）：
```ts
playerHeldItem: obj.playerHeldItem
  ? { itemId: String(obj.playerHeldItem.itemId ?? ""), name: String(obj.playerHeldItem.name ?? ""),
      qty: Number(obj.playerHeldItem.qty ?? 1), marketPrice: Number(obj.playerHeldItem.marketPrice ?? 0) }
  : null,
```

### 1.3 prompt 加交易规则 — `prompt-builder.ts`

DIALOGUE_SYSTEM_TEMPLATE「当前场景」段（L42-50）附近/之内加：
- 「玩家手持物 = 玩家想卖给你的东西，你是买家」→ 注入 `{playerHeldItem_desc}`（有手持物时："玩家手持 {name}×{qty}（公道价约 {marketPrice}g）"；无则"玩家没有手持可交易物品"）
- 「公道价 X（市场价），你可在 ±30% 内让步」
- 「达成交易必须调用 trade 工具，不能只嘴上说说」
- 新增 `.replace("{playerHeldItem_desc}", ...)`（L178-194 区域）

### 1.4 give_item item_id 描述 — `stardew-tools.ts` L58 附近

give_item 的 item_id 描述补：「传 QualifiedItemId（如 `(O)388` for Wood），不要传 DisplayName」。

### 1.5 协议 — `ValleyAI/protocol/messages.json`

worldSnapshot 字段描述（L150-154）加 `playerHeldItem`（玩家手持物，可选）。改后跑 `bun run check:protocol`。

### 1.6 测试

- `tests/stardew-tools.test.ts`：trade 工具存在 + 参数 schema
- `tests/world-snapshot-decoder.test.ts`：playerHeldItem 解码（有/无/缺省）
- `tests/prompt-builder.test.ts`：手持物描述注入（有/无）+ 交易规则段存在

---

## 2. C# 端（ValleyTalk）改动

### 2.1 give_item 名称回落 — `CommandExecutor.cs` ExecuteGiveItem

L239 `ItemRegistry.Create(itemId, allowNull: true)` 失败时，加名称回落：
1. 优先 QualifiedItemId 直接解析（现有）
2. 失败回落：查中文名/英文名 → 转 QualifiedItemId → Create
   - 用 `ItemRegistry.GetData` 遍历或 `ItemRegistry.Create(itemId)` 对 DisplayName/Name 匹配
   - SDV 1.6 建议：`ItemRegistry.Create(itemId)` 失败后，用 `Game1.objectData` 或 `ItemRegistry` 的 `GetData(id)` 检查 item name；名称匹配用 `ObjectInfo` 层——具体实现：遍历 `ItemRegistry.ItemTypes` 或按已知常用名映射（如 "Wood" → "(O)388"）。最稳做法：`ItemRegistry.Create` 前先尝试 `ItemRegistry.GetData(itemId)`；若 itemId 不是合法 id 而是名称，用 `Data/Objects` 数据按 `DisplayName`/`Name` 查找匹配 id。
3. 仍失败：返回 `unknown item_id '{itemId}'`，附带建议列表（相近物品名 top 3）

同样给 `ExecuteTrade` 的 L310 校验加回落（保持一致性）。

### 2.2 C# 单测

`src/ValleyAgent.UnitTests/` 新增/扩展：
- give_item 名称回落：传 "Wood" 能解析为 (O)388；传 "木头"（中文名）能解析；传垃圾名返回 ItemNotFound + 建议列表
- trade 结算：成功/失败/余额不足/物品不足（已有 TradeSettlementTests 覆盖原子结算，补充 ExecuteTrade 校验层测试）

---

## 3. 验证清单

- [ ] TS: `bun test packages/stardew` 全绿（388 + 新增）
- [ ] TS: `tsc --noEmit` 0 错
- [ ] TS: `check:protocol` PASS
- [ ] C#: 编译 0 警告
- [ ] C#: 单测覆盖 give_item 名称回落 + trade 校验
- [ ] 游戏内实测：Shane 交易方向（NPC 买）、Marnie 实际结算、价格锚定（4 纤维 ~8g）、Haley give_item "Wood" 名称回落

---

## 4. 风险与注意

- **不动已实现的 E 系列代码**：ExecuteTrade/PlayerHeldItem 已验证正确，避免破坏现有 E3-3/E3-6 行为。
- **±30% 是临时值**：设计文档 §14.1 待决项 2 已记录，阶段 1 通过后再迁逐 NPC 精明度。
- **messages.json 是单一事实源**：TS/C# 两端改后必须 check:protocol 一致。
- 中文注释用 write 工具写（UTF-8），不用 PowerShell Set-Content（GBK 乱码坑）。
