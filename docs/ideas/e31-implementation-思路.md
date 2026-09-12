# E3-1 实施思路：NPC 钱包 + 背包数据层（C# 侧）

> 依据：`docs/plan/2026-08-02-execution-plan.md` 的 E3-1 条目（需求文档 1.1「经济全流程 C# 权威版」）
> 设计输入：`docs/design/2026-08-02-npc-economy-hire-chat-requirements.md`（价格溢价 / 预算费率 / 初始资金表）
> 范围：**仅 C# 侧数据层**。LLM/TS 侧（ValleyAI）本轮不修改。

---

## 1. 背景与目标

原版 Stardew 的 NPC 没有属于自己的钱：钱包、物品、交易都只有玩家侧。E3-1 要为每个 NPC 建立
**可持久化、可留痕、线程安全**的钱包 + 背包数据层，为后续 E3-2（定价策略）/ E3-3（交易菜单 +
pending offer）/ E3-4（只读展示）提供地基。

执行计划原文验收标准：**存档/读档钱包金额一致；每次变动有日志**。

三个"必须"：

1. **钱包是 C# 侧的权威数据**——数值判定（余额是否够、找零多少）全部落 C# 规则侧，LLM 只给意图
   （设计哲学 #5：TS 做智能，C# 做执行）。
2. **每次变动可追溯**——所有收支经 `OnWalletChanged` 事件发布到 TranscriptSink 写 JSONL
   （设计哲学 #7：AI 的决定必须可追溯）。
3. **认知不得脱离现实**——余额变动有回执（bool 返回值 + 事件参数里的 newBalance），NPC 的"我买得起"
   永远基于存档级真实余额（设计哲学 #8，海莉事件是总教训）。

## 2. 数据模型

### 2.1 钱包字段（`AgentInventory`，ValleyAgent.Abstractions/Inventory）

在现有 `AgentInventory`（12 格背包，`_lock` 串行化）上扩展：

```
AgentInventory
├── Money : int                    # 余额，get/set 都走 _lock；直接赋值 = 静默路径（初始化/读档，不触发事件）
├── NpcName : string               # 钱包事件上下文用（由 AgentService 分配时写入）
├── OnWalletChanged                # event Action<OnWalletChangedEventArgs>? —— 镜像 OnItemChanged 契约
├── AddMoney(amount, reason=null)  # 原子加钱，amount>0，触发事件，返回新余额
├── TrySpend(amount, reason=null)  # 原子花费，余额不足 false（零副作用），触发事件
└── TrySpendWithChange(amount, out change, reason=null)  # 花费后把"找零"（剩余余额）写 out
```

关键设计决策：

- **原子性**：check-then-act 全部在 `lock (_lock)` 内完成，并发 TrySpend 不会把余额扣成负数；
  事件在锁内触发，保证「变更顺序 == 事件顺序」。
- **事件参数不可变**：`OnWalletChangedEventArgs` 用 `sealed record`（与 `ItemChangeRecord` 同风格），
  字段：`NpcName / GameDate / Amount(带符号增量) / NewBalance / Reason`。`Amount` 用带符号增量
  （收入 +n、支出 -n），消费方可直接取符号判断方向。
- **订阅者异常隔离**：事件触发包 try/catch（.editorconfig 已把 CA1031 关为 none），
  订阅者写盘失败绝不影响钱包原子操作（与 TranscriptSink 内部 catch+Warn 哲学一致）。
- **静默路径**：`Money` 直接赋值不触发事件——存档恢复、档案初始化时的"回填"不应当被当成
  一条收支记录写进 JSONL（避免读档刷屏）。

### 2.2 事件参数（`OnWalletChangedEventArgs`，同文件定义）

```
OnWalletChangedEventArgs(
    string NpcName,
    string GameDate,      // 形如 "Y1_spring_3"；Abstractions 层不知道游戏日期，由调用方/TranscriptSink 补
    int Amount,           // 带符号增量：AddMoney +n / TrySpend -n
    int NewBalance,       // 事件发生后的余额（回执）
    string? Reason)       // 可选原因："paid_wage" / "bought_item" / "gift" 等
```

> 说明：任务描述里写的是"OnItemChangedEventArgs 模式"，实际代码里该模式的参数类型叫
> `ItemChangeRecord`（`Action<ItemChangeRecord>`）。钱包侧按任务显式命名
> `OnWalletChangedEventArgs`，语义完全镜像 `ItemChangeRecord`（NpcName/GameDate/Delta/Reason 四要素
> + 钱包特有的 NewBalance）。TranscriptSinkTests 里已有 `GetEvent("OnItemChanged")` 反射断言先例，
> 钱包测试用同样的方式断言 `Action<OnWalletChangedEventArgs>`。

### 2.3 NPC 经济档案（ValleyAgent/Economy，新增）

```
NpcEconomyProfile (record，不可变，公开 API)
├── Name             # NPC 名（字典 key，StringComparer.OrdinalIgnoreCase）
├── InitialMoney     # 初始资金（首次分配时的钱包回填值）
├── InitialItems     # 初始物品（qualified item id 列表，E3-2+ 消费，本轮只装载）
├── Savvy            # 精明度 0~1（E3-2 定价时 LLM 的议价意愿输入）
├── BudgetTier       # enum: Cautious / Normal / Generous（预算费率 0.3/0.5/0.8，来自需求文档）
├── DailyWage        # 日薪（E3-3 雇佣定价的参考锚）
└── Talkativeness    # 话痨度 0~1（E5 喊话频率/长度参考）
```

装载器 `NpcEconomyProfileLoader`（镜像 `MemoryRuleLoader` 的 pattern）：

- `Load(IModHelper helper, string relativePath)`：从 mod 目录读 JSON（默认
  `Data/npc_economy.json`），IOException/JsonException → 空表不抛。
- `LoadFromFile(string jsonPath)`：供单元测试直接喂临时文件。
- `GetProfile(string npcName)`：大小写不敏感缓存查找，未知 NPC 返回 null。
- 装载时做防御性钳制：`Savvy/Talkativeness` 钳到 [0,1]，`InitialMoney/DailyWage` 负值归零，
  `BudgetTier` 枚举解析失败回退 `Normal`。

### 2.4 配置文件（ValleyAgent/Data/npc_economy.json）

```json
[
  {
    "name": "Abigail",
    "initialMoney": 300,
    "initialItems": ["(O)286", "(O)66"],
    "savvy": 0.35,
    "budgetTier": "normal",
    "dailyWage": 20,
    "talkativeness": 0.75
  }
]
```

首版收录 7 人（≥5 要求）：Abigail / Alex / Clint / Gus / Harvey / Emily / Willy，数值贴合人设
（铁匠 Clint 抠门高 savvy、酒吧老板 Gus 慷慨 generous、医生 Harvey 谨慎 cautious……）。

## 3. 持久化方案

双轨并存（与现有存档体系一致）：

| 轨 | 类 | Money 字段 | 说明 |
|---|---|---|---|
| Legacy | `AgentSaveData.AgentData`（Obsolete 但仍在写） | `Money = -1` | 默认 -1 作"未设置"哨兵；旧档反序列化无此字段 → 保留 -1 → 读档时不覆盖档案初始资金 |
| 结构化 | `Save.Models.AgentStateData` | `Money = -1` | 同上哨兵；SaveDataManager 序列化/反序列化/迁移全走通 |

- **写档**：`EventHandlerInitializer.OnSaving` 两份存档都写 `agent.Inventory.Money`（真实余额 ≥ 0）。
- **读档**：`OnSaveLoadedCore` 两处恢复点都按 `if (money >= 0)` 才回填到
  `agent.Inventory.Money`（直接赋值，静默不触发事件）。结构化存档晚于 legacy 恢复，天然"后写覆盖"，
  与现有 Inventory 恢复顺序一致。
- **迁移**：`SaveDataManager.MigrateV1ToV2` / `ApplyDefaults` 不动 Money（int 默认 -1 即安全），
  旧档升级后钱包余额按"档案初始资金"补齐——这是刻意的：老存档没有余额记录，不该凭空给钱。

哨兵值设计理由：`int` 反序列化缺字段时走属性初始化器（-1），而不是 0。若默认 0，
读旧档会把所有 NPC 钱包清零并覆盖档案初始资金，违背"初始资金由配置分配"的需求。

## 4. 与现有系统的连接

```
┌─ 初始化（ServiceInitializer，改动点 A）─────────────────────┐
│  NpcEconomyProfileLoader 装载 Data/npc_economy.json        │
│  → RegisterSingleton                                        │
│  → AgentService.EconomyProfiles = loader                    │
└──────────────────────────────────────────────────────────────┘
            │ 分配时（AgentService.CreateAgent，改动点 B）
            ▼
┌─ 首次分配 ──────────────────────────────────────────────────┐
│  profile = EconomyProfiles.GetProfile(npcName)              │
│  TranscriptSink.AttachWallet(inventory, npcName)  ← 先订阅   │
│  if profile != null: inventory.Money = profile.InitialMoney │
│    （静默回填；随后读档恢复若命中已存余额则覆盖之）            │
└──────────────────────────────────────────────────────────────┘
            │ 运行中任意收支
            ▼
┌─ 留痕（TranscriptSink.AttachWallet，改动点 C）───────────────┐
│  OnWalletChanged → Enqueue(kind:"wallet_changed",           │
│    npcName, gameDate:SafeGameDate(),                        │
│    extra:{ amount, newBalance, reason })                    │
│  → Drain（主线程 UpdateTicked）写 {root}/{date}/{npc}.jsonl  │
└──────────────────────────────────────────────────────────────┘
            │ 存档/读档（EventHandlerInitializer.OnSaving / OnSaveLoadedCore，改动点 D）
            ▼
┌─ 持久化 ────────────────────────────────────────────────────┐
│  OnSaving: AgentData.Money = inv.Money（legacy 轨）          │
│            AgentStateData.Money = inv.Money（结构化轨）      │
│  OnSaveLoadedCore: if (money >= 0) inv.Money = money         │
└──────────────────────────────────────────────────────────────┘
```

### 改动点清单（含「超出 MUST-NOT 白名单」的说明）

1. **AgentInventory.cs**（白名单内）：钱包字段 + 事件 + 三个原子操作。
2. **TranscriptSink.cs**（白名单内）：新增 `AttachWallet(AgentInventory, npcName)`，镜像
   `AttachStateMachine` 的订阅模式。
3. **ServiceInitializer.cs**（白名单内）：注册/装载 NpcEconomyProfileLoader，挂到 AgentService。
4. **ModConfig.cs**（白名单内）：新增 `EconomyConfig`（`Enabled` + `DataFile` 默认
   `Data/npc_economy.json`）。
5. **AgentSaveData.cs / SaveData.cs**（白名单内）：两个数据类各加 `Money = -1`。
6. **ValleyAgent.csproj**（白名单外，功能必需）：`<None Update="Data\*.json">` 复制到输出，
   否则运行时 mod 目录里没有 npc_economy.json。这是与 `assets\*.json` / `RAG\*.json` 同构的最小改动。
7. **AgentService.cs**（白名单外，功能必需）：`CreateAgent` 内 3 行——档案查表回填初始资金 +
   AttachWallet 订阅。创建点是唯一能覆盖所有分配路径（读档恢复 / DayStarted 自动分配 / TestMod）的汇聚点。
8. **EventHandlerInitializer.cs**（白名单外，功能必需）：`OnSaving` 写两份存档的 Money +
   `OnSaveLoadedCore` 两处恢复点回填。存档恢复逻辑只存在于这个文件，白名单内没有任何文件能完成
   MUST-DO #8 的"读档恢复"。

> 7、8 两点是 MUST-DO（存档/读档一致、每次变动有日志）与 MUST-NOT（文件白名单）的字面冲突。
> 按"功能需求优先、改动最小化"处理：每处只加几行，不重构、不改签名、不碰无关逻辑。
> 除这两个文件 + csproj 一行外，其余改动严格限于白名单。

## 5. 钱包初始金额的赋值顺序（防止"档案初始化"与"读档恢复"互踩）

```
CreateAgent（改动点 B）        → Money = profile.InitialMoney   （新档路径）
OnSaveLoadedCore legacy 恢复  → if money>=0: Money = saved      （旧档覆盖）
OnSaveLoadedCore 结构化恢复   → if money>=0: Money = saved      （后写覆盖 legacy）
```

- 新档：没有存档 → 只有 CreateAgent 回填 → 钱包 = 档案初始资金。✅
- 旧档（无 Money 字段）：哨兵 -1 → 不覆盖 → 钱包 = 档案初始资金。✅
- 新档第二次读档：存档里有真实余额 → 覆盖档案初始资金 → 钱包 = 上次离开时的余额。✅
  这正是执行计划"存档/读档钱包金额一致"的验收语义。

## 6. 测试计划（TDD，先写测试）

| 文件 | 覆盖 |
|---|---|
| `AgentInventoryWalletTests.cs` | 默认 0；AddMoney 增额+事件(delta/新余额/reason)；负数 AddMoney 抛异常；TrySpend 成功/失败/非法参数；TrySpendWithChange 找零语义；Money 直接赋值静默；并发扣款不超支不为负；事件类型反射断言 |
| `NpcEconomyProfileLoaderTests.cs` | 合法 JSON 解析；大小写不敏感查找；未知 NPC → null；缺文件/坏 JSON 不抛；BudgetTier 大小写枚举解析；savvy/talkativeness 钳制；负值归零 |
| `SaveDataPersistenceTests.cs` | AgentStateData.Money 经 SaveDataManager 往返一致；两个数据类默认 -1；V1→V2 迁移保留 Money；空 JSON → 默认 -1 |

## 7. 本轮不做（留给 E3-2+）

- 初始物品 `InitialItems` 只装载不落地（E3-2 定价时随交易流程注入）。
- `Savvy / BudgetTier / DailyWage / Talkativeness` 只进档案不进决策（E3-2 定价、E3-3 雇佣、E5 喊话消费）。
- `OnItemChanged`（物品变动事件）仍保持 E1-1 预留契约不触发（`#pragma warning disable CS0067` 保留），
  物品级留痕不在 E3-1 验收范围。
- 不碰 TS/ValleyAI 仓库；wire 协议不变。

## 8. 验证门槛

1. `dotnet test` 三个新测试文件全绿（+ 既有测试不回归）。
2. `dotnet build -c Debug -p:GamePath=...` 0 warning 0 error。
3. 手动：进游戏 → NPC 分配后 `va_wallet <npc>`（控制台查余额）= 档案初始资金；
   读档后余额 = 上次存档值；transcript 目录出现 `wallet_changed` 行。
