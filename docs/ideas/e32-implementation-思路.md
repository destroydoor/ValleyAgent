# E3-2 实施思路：定价引擎 + 阶梯还价状态机（C# 规则侧）

> 依据：`docs/plan/2026-08-02-execution-plan.md` 的 E3-2 条目（需求文档 1.2/1.3/1.4）
> 设计输入：`docs/design/2026-08-02-npc-economy-hire-chat-requirements.md`（定价双层模型 / 购买力约束 / 阶梯式还价）
> 范围：**仅 C# 纯逻辑层**（PricingEngine + HaggleStateMachine + EconomyConstants）。
> LLM/TS 侧（ValleyAI）本轮不修改；交易菜单/pending offer/成交原子校验是 E3-3；求购是 E3-5。

---

## 1. 背景与拍板

E3-1 建立了 NPC 经济档案（`NpcEconomyProfile`：Savvy 精明度 0~1、BudgetTier 预算档次、
InitialMoney 初始资金、DailyWage 日薪）。但档案只是数据，价格怎么算、还价怎么走还没有规则。

需求文档待技术方案问题 #4 已拍板（执行计划 E3-2 条目）：

> **数值判定全部放 C# 规则侧，LLM 只做话术。** LLM 断线时 C# 规则侧生成默认话术（模板化固定句子）。

本轮的三个"必须"：

1. **公道价** = 游戏数据卖价（`Object.salePrice()`），在 E3-3 交易流程里由调用方取游戏数据传入；
   `PricingEngine` 本身是纯函数，不碰游戏状态（便于单测）。
2. **精明度驱动浮动率**：savvy 越高 → 心理价区间越窄（精明商人 ±5%，对钱没概念 ±50%），
   出价上限受购买力约束（钱包 × 预算比例）。
3. **阶梯还价状态机**：最多 3 轮、让步逐轮减半、第 4 轮必拒、恶意低价警觉、连崩两次关交易。
   纯逻辑、可单测、无游戏依赖，E3-3 交易流程直接消费。

---

## 2. 定价数学（PricingEngine）

### 2.1 公道价（公平价格基准）

```
公道价 = salePrice          # 调用方从 Object.salePrice() 取，负数钳 0
```

`CalculateFairPrice(int salePrice)`：钳制到 `[0, +∞)` 后原样返回。它存在的意义是
**单一语义锚点**——心理价区间、出价上限、还价底线全部从它推导，将来若引入品质/分类修正只改这一处。

### 2.2 精明度浮动率（spread）

savvy（0~1）线性映射到浮动率区间，**savvy 越高浮动越窄**。实现为
`PricingEngine.CalculateSavvySpread(savvy)`：

```
spread = MaxSavvySpread − (MaxSavvySpread − MinSavvySpread) × clamp01(savvy)
       = 0.5 − 0.45 × savvy          # 默认 Max=0.5 / Min=0.05
```

| savvy | spread | 心理价区间（公道价 100 为例） | 人设示例 |
|-------|--------|-------------------------------|----------|
| 0.0   | 50%    | [50, 150]                     | 莱纳斯（对钱没概念） |
| 0.3   | 36.5%  | [64, 137]                     | Alex（懵懂少年） |
| 0.5   | 27.5%  | [73, 128]                     | Abigail（中间态） |
| 0.8   | 14%    | [86, 114]                     | Gus（生意人） |
| 1.0   | 5%     | [95, 105]                     | Pierre（精明商人） |

```
心理价区间（实现为 CalculatePsychologicalLow / CalculatePsychologicalHigh）:
  low  = round(公道价 × (1 − spread))
  high = round(公道价 × (1 + spread))
```

舍入统一 `Math.Round(x, MidpointRounding.AwayFromZero)`（避免 banker's rounding 的半数歧义）。

### 2.3 购买力约束（出价上限）

```
预算比例: Cautious 0.3 / Normal 0.5 / Generous 0.8     （EconomyConstants.GetBudgetRatio）
出价上限 = min(心理价上限 high, 钱包余额 × 预算比例)     # 向下取整，绝不超支
```

`CalculateBudgetCap(profile, wallet)` = `(int)(wallet × 比例)`。
`CalculateNpcOfferPrice(salePrice, profile, wallet)` = `min(high, budgetCap)`。

**语义**：NPC 买入玩家的物品时，开出的**最高价** = min(它认为值多少, 它出得起多少)。
- 心理价高但钱包薄（莱纳斯 80g 全副身家）→ 上限压到 24g → "买不起"。
- 心理价低但钱包厚（Clint 1500g 铁匠）→ 上限被心理价卡住 → 不会当冤大头。
- 价格认知与支付能力是两件事，NPC 两个都要有（需求 §1.3）。

### 2.4 NPC 卖价（反向开价）

NPC 卖给玩家时从**区间上限**开价（需求 §1.4 "NPC 从区间上限开价"）：

```
NPC 卖开价 = high = round(公道价 × (1 + spread))
```

还价时向**区间下限 low** 让步。卖出不花 NPC 的钱，无购买力约束。

---

## 3. 阶梯还价状态机（HaggleStateMachine）

### 3.1 状态图

```
        ┌────────────┐
        │    Init    │
        └─────┬──────┘
              │ Start()：NPC 开出 opening 价
              ▼
   ┌──────────────────────┐
   │     PlayerOffer      │◄───────────────────────────────┐
   └───┬────────┬────────┬┘                                │
       │        │        │                                 │
 接受  │  恶意低价│ 报价在底线内 │  报价超出底线且轮次未满            │
       ▼        ▼        ▼                                 │
  ┌────────┐ ┌────────┐ ┌──────────────────────────────┐   │
  │Settled │ │Rejected│ │          NpcCounter           │───┘
  └────────┘ └────────┘ └──────────────────────────────┘
                 │         │ 报价超出底线且轮次已满（第 4 轮）
                 │         ▼
                 │     ┌────────┐
                 └────►│Rejected│
                       └────────┘
```

补充转换（任意等待玩家状态）：

- `PlayerAccepts()` → `Settled`（按当前 NPC 报价成交）。
- `PlayerDeclines()`（玩家拒绝 NPC 的当前报价/离开）：连崩计数 +1；
  连崩计数 ≥ 2 → `Rejected`（当天交易关闭）；否则留在原状态。
- 玩家每次重新出价（`PlayerCounters`）会清零连崩计数（"连续"的语义）。

### 3.2 状态与转换规则（NpcSells 方向为例，NpcBuys 对称）

构造参数：`fairPrice`（公道价，判恶意低价）、`openingPrice`（NPC 开价）、
`anchorPrice`（底线：NpcSells=区间下限 low / NpcBuys=区间上限 high）、方向、还价限额。

```
初始差距 gap = |openingPrice − anchorPrice|

PlayerCounters(offer)：
  1. 恶意低价检查（优先于一切）：
     NpcSells: offer < fairPrice × hostileThreshold(0.5)  → Rejected, IsHostile=true
     NpcBuys : offer > fairPrice / hostileThreshold       → Rejected, IsHostile=true
  2. 报价在底线内：
     NpcSells: offer ≥ anchorPrice  → Settled（按玩家价成交）
     NpcBuys : offer ≤ anchorPrice  → Settled
  3. 超出底线：
     轮次已满（round ≥ MaxRounds=3）→ Rejected（第 4 轮必拒）
     轮次未满 → NPC 让步：
        cumulativeRatio += markdownRatios[round]   # 0.5 → 0.75 → 0.875
        NpcSells: newPrice = opening − gap × cumulativeRatio（向下取整，对玩家最有利）
        NpcBuys : newPrice = opening + gap × cumulativeRatio（向上取整）
        round++ → NpcCounter
```

### 3.3 让步数列推导（为什么是 0.5 / 0.25 / 0.125）

需求文档 §1.4 例子：开价 1000，底线 800，gap = 200：

| 轮次 | cumulative | 让步量 | 报价 | 需求文档原文 |
|------|-----------|--------|------|--------------|
| 开价 | 0         | —      | 1000 | NPC 开价 1000g |
| 1    | 0.5       | 100    | 900  | 第 1 轮最多让到 900 |
| 2    | 0.75      | 150    | 850  | 第 2 轮最多让到 850 |
| 3    | 0.875     | 175    | 825  | 第 3 轮最多让到 825 |
| 4    | —         | —      | 拒   | 之后再低我宁愿不要 |

**每次让步量（100 → 50 → 25）逐轮减半**，与"让步逐轮减半"字面一致；
`markdownRatios = {0.5, 0.25, 0.125}` 是**每轮新增让步占初始 gap 的比例**，累加后
3 轮共让出 87.5% 的差距，留 12.5% 余量——这正是"第 4 轮必拒"的数学原因（到底线前的最后防线）。

### 3.4 状态机输出（record per-state）

每次转换返回 `HaggleResult`（不可变 record）：

```
HaggleResult(
    Outcome: Ongoing | Settled | Rejected,
    Price:   当前 NPC 报价（Settled 时为成交价），
    Round:   已让步轮次，
    IsHostile: 是否因恶意低价而拒，
    Reason:   "opened" / "accepted" / "settled_at_player_offer" /
              "conceded" / "max_rounds_reached" / "hostile_offer" / "closed_by_declines")
```

状态机另暴露只读属性：`State / CurrentPrice / Round / IsHostile / ConsecutiveDeclines / IsTerminal`。
不暴露任何可变集合，状态迁移唯一入口是三个 Player 方法 + Start。

---

## 4. 常量表（EconomyConstants）

| 常量 | 值 | 出处 |
|------|-----|------|
| CautiousBudgetRatio | 0.3 | 需求 §1.3 谨慎型 30% |
| NormalBudgetRatio | 0.5 | 需求 §1.3 普通 50% |
| GenerousBudgetRatio | 0.8 | 需求 §1.3 豪爽 80% |
| MaxSavvySpread | 0.5 | 需求 §1.2 莱纳斯 ±50%（默认浮动率上限） |
| MinSavvySpread | 0.05 | 需求 §1.2 皮埃尔 ±5%（默认浮动率下限） |
| MaxHaggleRounds | 3 | 需求 §1.4 还价最多 3 轮 |
| HostileOfferThreshold | 0.5 | 需求 §1.4 恶意低价阈值（低于公道价 50%） |
| MarkdownRatios | [0.5, 0.25, 0.125] | 需求 §1.4 让步逐轮减半 |

---

## 5. 配置项（ModConfig）

新增 `HaggleConfig`（E3-1 已有 `EconomyConfig`），全部可被 `ModConfig.Validate()` 钳制：

| 字段 | 默认 | 钳制范围 | 说明 |
|------|------|----------|------|
| Enabled | true | — | 还价状态机总开关 |
| MaxRounds | 3 | [1, 10] | 最大让步轮次 |
| HostileThreshold | 0.5 | (0, 1) | 恶意低价阈值（公道价比例） |
| MaxSavvySpread | 0.5 | (0, 1] | 精明度 0 时的浮动率 |
| MinSavvySpread | 0.05 | [0, 1) | 精明度 1 时的浮动率；≤ MaxSavvySpread |
| MarkdownRatios | [0.5, 0.25, 0.125] | 非空、每项 (0,1]、累积 ≤ 1 | 每轮让步比例 |

> 接线说明：`HaggleStateMachine` 构造函数的 `maxRounds` 参数直接消费
> `EconomyConstants.MaxHaggleRounds`（默认 3）；ServiceInitializer 在 E3-3（交易流程接线）时
> 把 `HaggleConfig.MaxRounds` 显式传入构造。`PricingEngine` 各方法消费常量表中的 spread /
> 预算比例常量。本轮只定义字段与钳制，不接线。

---

## 6. 测试计划（TDD，先写测试）

| 文件 | 覆盖 |
|------|------|
| `PricingEngineTests.cs` | 公道价 = 卖价（负数钳 0）；savvy→spread 单调递减（0.8 窄 / 0.3 宽）；心理价区间镜像 spread；同一物品向 3 个精明度 NPC 兜售 → 3 种出价；预算上限三档 0.3/0.5/0.8；钱包薄时出价被预算卡住（min 语义）；NPC 卖价 = 区间上限 |
| `HaggleStateMachineTests.cs` | Start→PlayerOffer；接受→Settled；让步数列 1000→900→850→825（round 1/2/3 的 markdown 减半）；第 4 轮必拒；恶意低价 <50% 拒 + hostile flag（50% 恰好不 hostile）；报价在底线内 → 按玩家价成交；连崩两次关交易；重新出价清零连崩计数；NpcBuys 对称让步 + 反向 hostile；gap=0 退化（报价不动、轮次耗尽拒）；自定义 MaxRounds=1 限额生效 |

三档预算性格测试（需求 §1.4 验收"缺钱/普通/豪爽 → 不同出价上限反应"）：
wallet=1000、公道价 500、savvy 0.1（high≈728）→ Cautious 出价 300 / Normal 500 / Generous 728。
同一物品同一钱包，只换预算档次，出价明显不同。

---

## 7. 与 E3-1 的衔接 & E3-3 预留

- **消费 E3-1**：`PricingEngine` 吃 `NpcEconomyProfile.Savvy / BudgetTier`；BudgetTier 枚举
  与费率映射（E3-1 XML 注释里已写 0.3/0.5/0.8）在 `EconomyConstants.GetBudgetRatio` 落地。
- **不消费**：InitialItems / InitialMoney / DailyWage / Talkativeness 仍留给 E3-3/E3-5。
- **E3-3 接线点**（本轮不做）：
  1. 调用方取 `Object.salePrice()` → `CalculateFairPrice`。
  2. 玩家卖：`opening = 区间下限 low`、`anchor = min(high, budgetCap)` 构 NpcBuys 状态机。
  3. 玩家买：`opening = high`、`anchor = low` 构 NpcSells 状态机。
  4. LLM 断线降级：状态机 `Reason` 常量 → 模板话术表。
  5. 成交时用 `Result.Price` 做原子校验（钱包/背包），见需求 §1.8。

## 8. 验证门槛

1. `dotnet test` 两个新测试文件全绿（+ 既有测试不回归）。
2. `dotnet build -c Debug -p:GamePath=...` 0 warning 0 error（单测项目 TreatWarningsAsErrors）。
3. 手工推理抽查：Gus(savvy 0.8, generous) 买 500g 货钱包 2500 → 出价 min(570, 2000)=570；
   莱纳斯类（savvy 0.05, cautious）买 5000g 钻石钱包 80 → spread=0.5−0.45×0.05=0.4775，
   high=round(5000×1.4775)=7388、cap=80×0.3=24 → 出价 min(7388, 24)=24 → "买不起"。
