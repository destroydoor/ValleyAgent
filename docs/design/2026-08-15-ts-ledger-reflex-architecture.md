# TS 账本权威 + C# 反射执行 — 架构修订设计

> **Created:** 2026-08-15
> **修订对象:** `docs/design/2026-08-05-three-tier-architecture-redesign.md`（本文件修订其 §2.1 "C# 是唯一的账本与状态持有者" 及哲学第 6 条的账本归属；其余三层结构、状态光谱、记忆分层、Director 单向控制原则**不变**）
> **产生方式:** C# 端职责审计（发现 8 处越界）→ 工程权衡头脑风暴 → 三方案比选（选定方案二）

---

## 1. 背景与动机

2026-08-15 对 C# 端做"纯接口"审计，发现越界：规则决策引擎（低血量→FOLLOW 等状态决策）、关键词记忆分析器（正则→L1 记忆污染）、情绪推导器（事件→情绪机械映射）等。严格"C# 零逻辑"清洗在工程上有代价，遂重新权衡。

**用户确认的优化目标**（多选）：迁移成本、Token 成本、断连韧性。**实时性不是目标**（反射留在 C# 的理由是断连韧性 + 零 token，而非毫秒延迟）。

**方案比选结果**：

| 方案 | 内容 | 结论 |
|---|---|---|
| 一：最小迁移 | 只迁经济账本，情绪/记忆分析/规则引擎原样留 C# | 否——正则假 AI 污染 L1 记忆、机械情绪伤害 P0 真实性格 |
| **二：账本+认知迁移（选定）** | 经济账本+情绪引擎+记忆分析迁 TS；C# 留状态机/GoalExecutor/生存反射/物理校验/outbox | **采纳** |
| 三：全面重构 | 状态机决策/编排也迁 TS | 否——断连即瘫痪，与"完成当前目标"矛盾 |

## 2. 核心决策记录

1. **经济/背包权威账本迁 TS**——推翻 2026-08-05 设计 §2.1 "C# 是唯一的账本与状态持有者"中经济部分。理由：经济/物品系统在 TS 实现对玩家无感；TS 需要账本做业务校验与决策。
2. **双层校验**——TS 管业务规则（NPC 余额/价格一致性/交易公平性，基于权威账本）；C# 管物理约束（玩家真实余额、背包空间、物品 ID 存在性——只有 C# 能运行时看到 `Game1.player`，worldSnapshot 的 playerMoney 可能过时）。执行失败回执失败码，TS 回滚账本。
3. **断连策略：完成当前目标**——GoalExecutor 跑完当前目标（本就零 LLM），完成汇报与期间事件进 Outbox，重连后补发对账；新请求（对话/交易）本地拒绝。
4. **情绪引擎 = TS 确定性代码，非 LLM**——零 token，但归属叙事侧（服务 P0 真实性格，可被人设档案参数化）。Director `set_npc_mood` 保留最终覆盖权。
5. **C# 记忆分析删除**——LLM 对话时已有 remember/forget 工具，C# 正则版是假 AI，直接删。

**一句话判据**（并入 AGENTS.md）：*能在断连时还必须工作的逻辑留 C#；账本和叙事永远在 TS；C# 的每一次余额/物品变更都必须由 TS 指令驱动并以回执确认。*

## 3. 职责与数据所有权矩阵（§A 已确认）

```
┌──────────────────────────────┐   ┌──────────────────────────────┐
│ TS 端 · 智能与账本            │   │ C# 端 · 反射与执行            │
│  NPC Agent（对话/角色扮演）    │   │  AgentBrain·L2黑板（情绪/事件 │
│  Director（编排/写状态）       │   │   镜像，非权威）              │
│  情绪引擎（确定性·零LLM）★迁入 │   │  执行引擎（状态机·GoalExecutor │
│  经济账本（权威·业务校验）★迁入 │◄──┼─ ①指令 adjust   ②回执·成败   │
│                              │──►│  生存反射）                   │
│                              │   │  断线 Outbox（缓存·补发）★新增│
│                              │   │  adjust 执行器（物理校验·游戏 │
│                              │   │   变更）★新增                │
└──────────────────────────────┘   └──────────────┬───────────────┘
                                                   ▼ 执行游戏变更
                                     Stardew Valley（玩家钱/背包/移动）
```

| 数据/能力 | 归属 | 变化 |
|---|---|---|
| NPC 钱包/背包账本 | **TS 经济账本（权威）** | 从 C# AgentInventory 迁出；C# 只留执行镜像 |
| 交易业务规则（价格/余额/公平性） | TS 账本 | PricingEngine/HaggleStateMachine/PendingOffer 全家迁 TS |
| 玩家侧物理约束 | C# adjust 执行器 | 运行时对 `Game1.player` 实时校验，不信 worldSnapshot |
| 情绪/心情推导 | **TS 情绪引擎（确定性代码）** | C# EmotionAnalyzer/DialogueMemoryAnalyzer 删除 |
| L1/L2 记忆 | TS（现状已是） | C# AgentBrain 记忆字段保持纯镜像 |
| 状态机 + GoalExecutor + 生存反射 | C#（保留） | RuleBasedDecisionEngine 瘦身为生存反射（逃跑/卡住/战斗） |
| 位置/移动 | C#（游戏世界状态） | 不变 |
| 断线缓冲 | C# Outbox（新增） | 磁盘持久化，带序号 |

## 4. 核心数据流（§B 已确认）

### 4.1 交易闭环（原子批 + 两阶段）

```
LLM 调 trade/give_item/give_gift/receive_payment
  → TS 账本：业务校验（NPC 库存/价格/余额）→ 记 pending 账
  → execute_adjust 指令（原子批：玩家扣钱+加物、NPC 扣物+加钱，带 instructionId）
  → C# 物理校验：玩家真钱够？背包有位？物品 ID 存在？
      失败 → adjust_result 失败码 → TS 回滚 pending → 工具结果返失败，LLM 自然改口
      成功 → 执行游戏变更（TradeSettlement 的原子回滚逻辑迁入执行器）
           → adjust_result 成功 + 双方新余额 → TS 账本 pending→committed
  → 回执同时写 L2 todayEvents + transcript 留痕
```

### 4.2 断线/重连对账

- **断线瞬间**：C# 进 disconnected 模式；GoalExecutor 继续跑完当前目标；新对话/交易请求本地拒绝（UI 提示）；期间事件（目标完成、状态变化）进 Outbox（磁盘持久化，带序号）。
- **重连**：C# 发 `reconnect_sync`（outbox 补发 + 全量状态）→ TS 逐条处理；目标完成汇报正常走流程（≤1 次 LLM）。
- **in-flight 一致性**：断线时 TS 已发但未收回执的 adjust（pending 条目），重连后 TS 凭 instructionId 向 C# 查询执行结果——C# 保留最近 256 条指令结果日志（环形缓冲，可配置）。断线期间不会有新经济指令（TS 不可达），账本天然无漂移，对账只处理 in-flight 批次。

### 4.3 协议变更（`ValleyAI/protocol/messages.json`，check:protocol 同步）

| 消息 | 方向 | 状态 | 说明 |
|---|---|---|---|
| `execute_adjust` | TS→C# | 新增 | 原子批：money/item 操作列表 + instructionId |
| `adjust_result` | C#→TS | 新增 | 每步成败 + 失败码 + 变更后余额/库存 |
| `reconnect_sync` | C#→TS | 新增 | outbox 补发 + 全量状态 |
| `worldSnapshot` | C#→TS | 修改 | npcMoney/npcInventory 改由 TS 账本自填（权威）；C# 镜像字段仅断线模式/UI 用 |

### 4.4 工具路由

- 经济类（trade/give_item/give_gift/receive_payment/accept_job 结算部分）→ **TS 账本拦截编排**，LLM 不直接触发 C# 经济变更
- 直通类（speak/emote/set_goal/set_state/move_to/show_dialogue）→ C# 不变
- `get_info` → NPC 侧读 TS 账本，玩家侧读 worldSnapshot

## 5. 迁移步骤（§C，绞杀式四步，每步可独立验证回滚）

| 步 | 内容 | 验证 |
|---|---|---|
| 1 | C# adjust 原语 + 物理校验 + 指令结果日志；TS 账本骨架（`agents/<npc>_ledger.json` 持久化，从 worldSnapshot 播种）。**旧路径不动** | 双端单测 + 既有测试全绿 |
| 2 | 交易流切新闭环；TradeSettlement 玩家侧并入执行器；**迁 TS**：PendingOffer/PendingOfferRegistry、HaggleStateMachine、PricingEngine、NpcPurchaseRequestService、EmploymentContract/ContractService/ContractViolation/LongWorkerProtection（雇佣合同族）、NpcEconomyProfile/Loader（账本种子数据）；**删**：AgentInventoryTradeActor、ITradeActor（PlayerTradeActor 并入 adjust 执行器）；worldSnapshot 经济字段改 TS 自填 | IT11 游戏内交易实测（防单价×数量回归）；断连注入 |
| 3 | TS 情绪引擎；删 C# EmotionAnalyzer、DialogueMemoryAnalyzer、RuleBasedDecisionEngine 性格决策（留生存反射） | 对话回归 + prompt 注入快照 |
| 4 | C# Outbox + reconnect_sync + 断线拒绝新请求；TS 对账 | 杀进程注入：跑完目标→重启→对账一致 |

## 6. 失败模式矩阵

| 失败 | 处理 |
|---|---|
| LLM 非法交易（没钱没货） | TS 业务校验拒绝 → 工具结果返失败，LLM 自然改口 |
| C# 物理校验失败 | 失败码回执（INSUFFICIENT_FUNDS/INVENTORY_FULL/ITEM_NOT_FOUND/...）→ TS 回滚 pending |
| 指令/回执丢失 | instructionId 幂等：C# 缓存已执行结果，TS 重发直接返回缓存 |
| TS 账本文件损坏 | 从 C# 镜像 + worldSnapshot 重建（降级路径） |
| 断连 | 见 §4.2；所有拒绝必须 log 被拒项和原因（**降级不静默**铁律） |

## 7. 测试策略（对齐五层体系 `2026-08-01-test-scoring-redesign.md`）

- **契约**：messages.json 新消息双端 schema 对齐（check:protocol）
- **组件**：TS 账本（并发/幂等/回滚/pending→committed）；C# 执行器原子性
- **故障注入**：断连、回执丢失、重复指令、账本损坏
- **不变量**：任何对账点 TS 账本 ≡ C# 镜像；交易前后玩家钱 + NPC 钱守恒（封闭交易内）
- **体验**：V3 TestMod 场景 + IT11 复跑

**门槛**：`bun test packages/stardew` + `tsc --noEmit` + `check:protocol` 全绿；C# 编译 0 警告；游戏内实测。

## 8. 对 2026-08-05 设计的修订清单

| 原条目 | 修订后 |
|---|---|
| §2.1 "C# 是唯一的账本与状态持有者：钱包/背包/状态机在 C#" | 拆分：钱包/背包权威账本在 **TS**；状态机/位置在 C#；C# 持执行镜像 + 指令结果日志 |
| 哲学 6 "TS 做智能，C# 做执行……数值判定优先落 C# 规则侧" | 改为"账本与叙事在 TS；断连必需的逻辑与物理校验在 C#"（见 AGENTS.md 同步修订） |
| §3.4 NPC 工具集 trade 直连 C# | 经济工具经 TS 账本编排，走 execute_adjust 闭环 |
| C# AgentInventory 事件（OnItemChanged/OnWalletChanged） | 镜像变更事件保留用于留痕，但不再是权威记账源 |
