# 三层架构重新设计 — 执行计划

> **Created:** 2026-08-05
> **设计依据:** `docs/design/2026-08-05-three-tier-architecture-redesign.md`
> **Status:** 待用户确认后开始实施
> **目标:** 分三阶段落地新架构，阶段 1 修复交易 bug，阶段 2 上 set_goal，阶段 3 大重构

---

## 阶段 1：交易 bug 修复（紧急）

**目标**：修复 Shane/Marnie/Haley 三个交易 bug，最小改动不涉及架构。

**预计改动文件**：

### 1.1 TS 端（ValleyAI）

#### 1.1.1 新增 trade 工具

**文件**：`packages/stardew/src/stardew-tools.ts`

- 新增 `trade` 工具定义
- 参数：`{item_id: string, quantity: number, price: number, direction: "npc_buys"}`
- 描述明确：NPC 是买家，按 price 单价购买玩家手里 quantity 个 item_id
- 返回值：`{success: boolean, reason?: string}`

#### 1.1.2 worldSnapshot 加 PlayerHeldItem

**文件**：`packages/stardew/src/types.ts` + `world-snapshot-decoder.ts`

- `SceneState` 加字段：`playerHeldItem?: {itemId, name, qty, marketPrice}`
- Decoder 解析新字段

#### 1.1.3 prompt 加交易规则

**文件**：`packages/stardew/src/prompt-builder.ts`

- 在 DIALOGUE_SYSTEM_TEMPLATE 加交易规则段：
  - 「玩家手持物 = 玩家想卖给你的东西，你是买家」
  - 「公道价 X（市场价），你可在 ±30% 内让步」
  - 「达成交易必须调用 trade 工具，不能只嘴上说说」

#### 1.1.4 give_item 工具描述明确 item_id 格式

**文件**：`packages/stardew/src/stardew-tools.ts`

- give_item 的 item_id 描述明确：「传 QualifiedItemId（如 `(O)388` for Wood），不要传 DisplayName」

### 1.2 C# 端（ValleyTalk）

#### 1.2.1 worldSnapshot 加 PlayerHeldItem

**文件**：`src/ValleyAgent/AI/WorldSnapshotBuilder.cs`

- 新增字段 `PlayerHeldItem`，包含 `{itemId, name, qty, marketPrice}`
- `marketPrice` 通过 `ResolveItemSalePrice`（已存在于 EventHandlerInitializer.cs:1107）获取

#### 1.2.2 CommandExecutor 实现 trade 工具

**文件**：`src/ValleyAgent/CommandExecutor.cs`

- `case "trade"` 分支已有（死代码），改为实际实现：
  - 校验：NPC 钱包 ≥ price × quantity
  - 校验：玩家手里 item_id ≥ quantity
  - 扣玩家物品、加玩家钱
  - 加 NPC 物品、扣 NPC 钱
  - 返回 success/failure

#### 1.2.3 give_item item_id 解析加名称回落

**文件**：`src/ValleyAgent/CommandExecutor.cs` 或 `Handlers/`

- `ExecuteGiveItem` 解析 item_id 时：
  - 优先 `ItemRegistry.Create(itemId, allowNull: true)`（QualifiedItemId 直接解析）
  - 失败回落：查 `ObjectInfo` 中文名/英文名匹配 → 转 QualifiedItemId → Create
  - 仍失败：返回 `unknown item_id '{itemId}'`，附带建议列表

#### 1.2.4 修复死代码 case "trade"

**文件**：`src/ValleyAgent/CommandExecutor.cs`

- 现有 `case "trade"` 是死代码，改为调用新的 ExecuteTrade
- 移除 NPCGiftPatch 的 PendingTrade 机制（或保留作为礼物流程的延伸，待确认）

### 1.3 验证

- [ ] TS: `bun test packages/stardew` 全绿
- [ ] TS: `tsc --noEmit` 0 错
- [ ] TS: `check:protocol` PASS
- [ ] C#: 编译 0 警告
- [ ] C#: 单测覆盖 trade 结算（成功/失败/余额不足/物品不足）
- [ ] 游戏内实测：
  - [ ] Shane 交易方向正确（NPC 买玩家物品）
  - [ ] Marnie 交易实际结算（金币转移 + 物品转移）
  - [ ] 价格锚定（4 纤维 ~8g 而非 100g）
  - [ ] Haley give_item 能解析 "Wood"（名称回落）

---

## 阶段 2：set_goal + GoalExecutor（核心）

**目标**：NPC 能接受委托、和玩家一起干活，不费 LLM 循环。

### 2.1 TS 端

#### 2.1.1 新增 set_goal 工具

**文件**：`packages/stardew/src/stardew-tools.ts`

- 参数：`{type: "chop_tree"|"mine"|"water_crops"|"fight"|"forage", params: object, reportBack: boolean}`
- 描述：设定目标后 NPC 进入执行态，C# GoalExecutor 后台跑，完成后寻路回玩家汇报

#### 2.1.2 状态流转

**文件**：`packages/stardew/src/types.ts` + `protocol-adapter.ts`

- NPC 状态加 `EXECUTING_GOAL` / `TRAVELING_TO_REPORT`
- worldSnapshot 加 `currentGoal?: {type, params, progress, status}`

### 2.2 C# 端

#### 2.2.1 GoalExecutor 调度器

**新文件**：`src/ValleyAgent/Goals/GoalExecutor.cs`

- 接收 set_goal 工具调用 → 创建 Goal 实例 → 存入 AgentBrain.PendingGoal
- 每 tick 检查 Goal 状态：执行中 / 达成 / 失败 / 超时
- 复用现有 Handler：chop_tree→FarmHandler, mine→MineHandler, fight→FightHandler, forage→ForageHandler, water_crops→FarmHandler

#### 2.2.2 五种 Goal 类型

**新文件**：`src/ValleyAgent/Goals/{ChopTreeGoal,MineGoal,WaterCropsGoal,FightGoal,ForageGoal}.cs`

- 每种 Goal 实现统一接口：`CheckProgress()`, `IsComplete()`, `IsFailed()`, `Tick()`
- 终止条件：
  - chop_tree: 获得 Wood ≥ qty
  - mine: 获得 targetItemId ≥ qty
  - water_crops: 浇完所有目标
  - fight: 清除怪物 / 到时间
  - forage: 采集到指定数量

#### 2.2.3 超时与卡死判定

**文件**：`src/ValleyAgent/Goals/GoalExecutor.cs`

- 全局超时：游戏内 4 小时（ModConfig 可调）
- 位置卡死：连续 N tick 位置不变且不在执行动作 → 超时
- 资源耗尽：指定位置找不到目标资源类型 → 超时

#### 2.2.4 寻路汇报

**文件**：`src/ValleyAgent/Goals/GoalExecutor.cs` + `Services/MovementService.cs`

- 目标完成 + reportBack=true：
  - 调 `MovementService.MoveTo(npc, player.currentLocation, player.tile)`
  - NPC 状态 = `TRAVELING_TO_REPORT`
  - 到达玩家附近（距离 < 5 格）→ 触发汇报 LLM
  - LLM 跑一次：speak + give_item + set_state(IDLE)
- 寻路失败 fallback：
  - 超时（2 游戏小时）→ inject_memory + update_status_summary
  - 玩家在矿洞深处 → NPC 寻路到矿洞入口等
  - NPC 倒地 → 复活后 inject_memory

### 2.3 验证

- [ ] TS: `bun test packages/stardew` 全绿
- [ ] TS: `tsc --noEmit` 0 错
- [ ] TS: `check:protocol` PASS
- [ ] C#: 编译 0 警告
- [ ] C#: 单测覆盖五种 Goal 的终止条件
- [ ] C#: 单测覆盖超时/卡死/玩家取消
- [ ] 游戏内实测：
  - [ ] 玩家委托 Shane 砍 10 木头，Shane set_goal 后真的去砍
  - [ ] 砍完 Shane 寻路回来汇报 + 给物品
  - [ ] 玩家在矿洞和 Shane 一起挖矿，Shane set_goal(mine) 后真挖
  - [ ] 挖够后 Shane 汇报 + 给物品
  - [ ] 超时场景：Shane 卡在树上 → 4 游戏小时后超时 → 汇报失败

---

## 阶段 3：大重构（Director 工具集 + L2 状态层 + 默认创建）

**目标**：架构落地到新设计，Director 真正能编排，所有 NPC 默认有 AgentBrain。

### 3.1 删除 Agent/Non-agent 区分

#### 3.1.1 所有 NPC 默认创建 AgentBrain+Inventory

**文件**：`src/ValleyAgent/Initialization/EventHandlerInitializer.cs`

- SaveLoaded 时遍历所有 NPC，按 per-NPC 配置创建 AgentBrain+Inventory
- 老存档检测缺失文件时自动补建
- 不和玩家交互的 NPC 只走原版路径，不加载 AgentBrain（但文件存在）

#### 3.1.2 删除工具层 Agent/Non-agent 区分

**文件**：`src/ValleyAgent/AI/WorldSnapshotBuilder.cs` + `src/ValleyAgent/CommandExecutor.cs`

- 所有 NPC 都有完整工具集
- worldSnapshot 的 npcInventory/npcMoney 不再是 null（默认创建后有值）

### 3.2 per-NPC 配置文件

#### 3.2.1 配置文件加载器

**新文件**：`src/ValleyAgent/Config/NpcConfigLoader.cs`

- 读取 `{modDir}/npc-configs/{npc_name}.json`
- 字段：name/personality/speechStyle/birthday/isRomanceable/lovedGifts/likedGifts/dislikedGifts/hatedGifts/initialMoney/initialInventory/isProtagonist/defaultMood/relationships

#### 3.2.2 配置文件示例

**新文件**：`src/ValleyAgent/npc-configs/Shane.json`（及所有其他 NPC）

### 3.3 Director 工具集

#### 3.3.1 Director 工具实现

**文件**：`src/ValleyAgent/Commands/DirectorTools.cs`（新）

- `set_npc_position(npc, location, tile)` — 移动 NPC
- `set_npc_inventory(npc, add?, remove?)` — 增删背包
- `set_npc_money(npc, delta)` — 改钱包
- `set_npc_mood(npc, moodTag)` — 设心情
- `set_npc_recent_events(npc, events)` — 设 L2 todayEvents
- `set_npc_working_on(npc, workingOn?)` — 设/清工作标记
- `spawn_beat(npc, sceneDesc, ...)` — 创建单 NPC beat
- `spawn_group_beat(npcs, location, sceneScript, ...)` — 创建多 NPC 互动 beat
- `inject_memory(npc, text, importance, tags)` — 注入 L1 长期记忆

#### 3.3.2 Director 工具协议

**文件**：`ValleyAI/protocol/messages.json`

- 新增 director_command 消息类型（或复用 action_result）
- Director LLM 输出的工具调用通过 TS 端转发到 C# 执行

### 3.4 L2 状态摘要层

#### 3.4.1 AgentBrain 加 L2 字段

**文件**：`src/ValleyAgent/Agents/AgentBrain.cs`

- 加字段：`MoodTag`, `TodayEvents: List<string>`, `WorkingOn: string?`, `OwedMoney: int`
- todayEvents 保留近 3 天，每天 day_started 清理 3 天前的

#### 3.4.2 worldSnapshot 加 L2 字段

**文件**：`src/ValleyAgent/AI/WorldSnapshotBuilder.cs`

- worldSnapshot 加：`npcMood`, `npcRecentEvents`, `npcWorkingOn`, `npcOwedMoney`

#### 3.4.3 prompt 拼 L2 摘要

**文件**：`ValleyAI/packages/stardew/src/prompt-builder.ts`

- System Prompt 开头加「## 你的状态」段
- 强制注入 L2 摘要，不依赖 RAG

### 3.5 BeatStore

**新文件**：`src/ValleyAgent/Beats/BeatStore.cs`

- 存储当前活跃的 beat
- 字段：npc, sceneDesc, expectedInteraction, expireTime, playerVisible
- worldSnapshot 加 `currentBeat?: {sceneDesc, ...}`

### 3.6 PlayerActionTracker

**新文件**：`src/ValleyAgent/Tracking/PlayerActionTracker.cs`

- 每 10 tick 采样玩家行为
- 行为分类：mine/farm/fish/forage/social/other
- day_started 聚合为百分比 + 趋势

### 3.7 DirectorContextBuilder

**新文件**：`src/ValleyAgent/AI/DirectorContextBuilder.cs`

- day_started 时拼装 Director 上下文
- 压缩为结构化文本（800-1500 token）
- 注入 Director prompt

### 3.8 验证

- [ ] TS: `bun test packages/stardew` 全绿
- [ ] TS: `tsc --noEmit` 0 错
- [ ] TS: `check:protocol` PASS
- [ ] C#: 编译 0 警告
- [ ] C#: 单测覆盖 Director 所有工具
- [ ] C#: 单测覆盖 L2 状态摘要注入
- [ ] C#: 单测覆盖 PlayerActionTracker 行为分类
- [ ] 游戏内实测：
  - [ ] Haley 默认有 AgentBrain，对话不再凭空说有 Wood/Stone
  - [ ] Director 能 spawn_beat 把 Abigail 放到矿洞入口
  - [ ] Director 能 spawn_group_beat 让 Haley 和 Alex 吵架（气泡播放）
  - [ ] Director 能改 NPC 钱包/背包/心情
  - [ ] NPC 对话时 L2 状态摘要强制注入（NPC 知道自己最近发生了什么）
  - [ ] PlayerActionTracker 正确分类玩家行为
  - [ ] day_started 时 Director 上下文正确拼装

---

## 实施顺序

1. **阶段 1 先做**（紧急）：修复交易 bug，最小改动
2. **阶段 2 次做**（核心）：set_goal + GoalExecutor
3. **阶段 3 最后做**（大重构）：Director 工具集 + L2 状态层 + 默认创建

每个阶段独立验证，阶段间保持兼容性。

## 风险与缓解

| 风险 | 缓解 |
|---|---|
| 阶段 1 的 trade 工具语义错（方向反） | 工具描述明确写「NPC 是买家」，prompt 也明确，单测覆盖 |
| 阶段 2 的 GoalExecutor 卡死 | 全局超时 4 游戏小时 + 位置卡死判定 + 资源耗尽判定 |
| 阶段 3 的 Director 工具被滥用 | Director prompt 加约束：「只能改符合人设的状态」「不能让 NPC 突然变性格」 |
| 阶段 3 的 L2 todayEvents 膨胀 | 保留近 3 天 + 每条字数限制（待定）+ 总条数限制 ≤5 |
| 阶段 3 的默认创建导致所有 NPC 都消耗资源 | 不和玩家交互的 NPC 只走原版路径，AgentBrain 按需激活 |

## 开放问题（实施时再细化）

1. Director spawn NPC 到玩家身边的寻路细节（玩家在矿洞深处怎么办）
2. spawn_group_beat 的玩家可见性判定（距离阈值还是视野判定）
3. Director LLM 的 token 预算上限
4. L2 todayEvents 的字数限制
5. NPC 间互动的 sceneScript 是否支持分支

这些问题不影响阶段 1/2 的实施，阶段 3 实施时再细化。
