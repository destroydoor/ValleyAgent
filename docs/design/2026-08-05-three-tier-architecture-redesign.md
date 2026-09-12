# 三层架构重新设计 — 架构设计

> **Created:** 2026-08-05
> **Updated:** 2026-08-06（补全设计文档，基线 78ed1b8 固化后）
> **执行依据:** `docs/plan/2026-08-05-three-tier-architecture-execution-plan.md`
> **旧版参考:** `docs/archive/AGENTS-2026-08-02-v4.3-full.md`（模块矩阵、状态机细节、常量表）

---

## 1. 背景与目标

原版 Stardew Valley 的 NPC 是程序化木偶。ValleyAgent 的目标是让 NPC 变成**活的虚拟生命**：有自主行为、有钱有物、能交易、能被委托、能和玩家发生剧本外的事件。

旧架构（v4.3 时代）的核心问题是**职责不分**：NPC Agent 既要角色扮演，又要感知"导演"存在；LLM 决策循环侵入日常行为；Agent/Non-agent 二分让大多数 NPC 没有状态层。2026-08-05 重设计为**三层架构**，用单向数据流把"智能"与"执行"彻底分离。

### 1.1 优先级

| 优先级 | 目标 | 说明 |
|-------|------|------|
| P0 | **真实性格** | 所有言行符合角色设定，NPC 不是机器 |
| P0 | **AI 对话** | 每次对话独一无二，对话内容影响 NPC 行为 |
| P0 | **职责隔离** | NPC Agent 只做角色扮演，永远不知道"导演"存在；Director 只编排和改状态 |
| P1 | **共同冒险** | 一起战斗、收菜、挖矿（set_goal + C# GoalExecutor，零 LLM 循环） |
| P1 | **情感连接** | 对话影响好感度，NPC 记住玩家说过的话 |
| P1 | **社会存在** | NPC 是镇上的居民：钱包/背包/交易/雇佣、行动有节奏 |
| P1 | **导演编排** | Director 编故事、改 NPC 状态、创造相遇机会、编排 NPC 间互动 |
| P2 | **仪式感** | 做事有头有尾，送礼有完整流程 |
| P3 | **多 Agent** | 暂不实现 |

### 1.2 与旧架构的关系

| 旧架构（v4.3） | 新架构（三层） | 处置 |
|---|---|---|
| Agent/Non-agent 二分 | 状态光谱（休眠/有限自主/执行态） | 阶段 3 删除区分，全员默认 AgentBrain |
| 常驻层事件驱动决策（30s 轮询） | dialogue 唯一 LLM 触发 + spark 随机激活 + Director beat + C# GoalExecutor | 删除 LLM 状态轮询 |
| 决策链（AIDecisionEngine/DecisionOrchestrator） | 确定性规则 + 对话时决策 + C# 执行 | 取代 |
| 四层记忆（工作/归档/凝练/重要） | 三层记忆（L1 长期 / L2 状态摘要 / L3 临时剧本） | 机制保留，层级重构（见 §5.2 映射） |
| 场景三 beat 消息流（beat_plan/start/end） | Director 工具 spawn_beat/spawn_group_beat + C# BeatStore | 消息改工具 |
| decision/friendship_eval 死管道 | 删除（schema 留档） | 已删 |

---

## 2. 三层架构总览

```
┌─────────────────────────────────────────────────────────────┐
│ Director（导演智能体，TS 端）                               │
│ 视野：全局信息 + 所有 NPC 人设档案 + 所有 NPC 的 L2 状态    │
│ 职责：编故事、编排事件、创造相遇机会、改 NPC 状态           │
│ 工具：set_npc_position/inventory/money/mood/recent_events/  │
│       working_on + spawn_beat + spawn_group_beat            │
│ 不做：不调 NPC LLM、不进 NPC 上下文、不审批                 │
└────────────────┬────────────────────────────────────────────┘
                 │ 单向：调 C# 工具改状态数据
                 ▼
┌─────────────────────────────────────────────────────────────┐
│ C# 状态层（零 LLM）                                         │
│ AgentBrain: L2 状态摘要（位置/钱包/背包/心情/近期事件/工作）│
│ AgentInventory: 实际物品                                    │
│ BeatStore: 当前 beat 场景描述                               │
│ GoalExecutor: 执行态后台跑（chop_tree/mine/water/fight）    │
│ PlayerActionTracker: 玩家行为采集器                         │
│ DirectorContextBuilder: 拼装 Director 上下文                │
│ WorldSnapshotBuilder: 拼装 worldSnapshot 给 TS              │
└────────────────┬────────────────────────────────────────────┘
                 │ 单向：worldSnapshot 拼装
                 ▼
┌─────────────────────────────────────────────────────────────┐
│ NPC Agent（角色扮演智能体，TS 端）                          │
│ 上下文：人设 + L2 状态摘要 + L1 长期记忆 + 玩家对话历史     │
│         + 当前 beat 场景（第三人称描述，不提"导演"）         │
│ 职责：纯角色扮演，响应玩家 + 可选 set_goal 自主做事         │
│ 工具：speak/emote/give_item/trade/give_gift/                │
│       set_goal/set_state/remember/forget/get_info/...       │
│ 不做：不知道导演存在、不审批、不协调、不感知元层            │
└─────────────────────────────────────────────────────────────┘
```

**关键原则**：NPC Agent 和 Director 之间**永远没有直接消息往来**。Director → C# 状态层 → NPC Agent，单向流动。

### 2.1 职责隔离细节

- **Director 不调 NPC LLM**：Director 只改 C# 状态数据（位置/钱包/背包/心情/事件/工作标记/beat），NPC 感知这些变化的方式与感知玩家行为完全一致（通过 L2 状态摘要）。
- **NPC 不知导演存在**：NPC 上下文中的 beat 场景是**第三人称描述**（"镇上有传闻说……"），绝不出现"导演/剧本/安排"等元层词汇。
- **C# 是唯一的账本与状态持有者**：钱包/背包/状态机在 C#，TS 端只有只读镜像（`actualState`）。每次状态/资产变动必须有回执（`action_result`/`state_changed`），NPC 对现实的认知与游戏状态保持一致——这是"海莉事件"的总教训。

### 2.2 我们不做什么

- **不做 NPC 自主动机循环** — NPC 不自己决定"今天我要做什么"。没有导演 beat 时，NPC 要么休眠走原版，要么 spark 随机激活做点小事，要么被玩家对话触发响应。
- **不做 LLM 状态轮询** — 不让 LLM 反复检查"我砍了几棵树了"。用 C# GoalExecutor 确定性判断终止条件。
- **不做 NPC ↔ Director 双向沟通** — NPC 不审批 Director 提案，不向 Director 汇报，不接收 Director 的元层指令。Director 只改 NPC 的状态数据。
- **不做玩家不在场的实时模拟** — 玩家委托 NPC 砍树然后离开，NPC 不会真的去砍。Director 直接改数据，玩家回来对话时 NPC 从 L2 摘要自然感知。

---

## 3. 设计哲学（9 条）

1. **职责隔离** — NPC Agent 只做角色扮演，永远不知道"导演"存在；Director 只编排和改状态，不进入 NPC 的 LLM 上下文
2. **Token 经济** — 不做 LLM 循环；NPC 离屏任务由 Director 改数据，不实时执行；只在"设目标 + 汇报"或"对话"时消耗 token
3. **状态可见** — NPC 的钱包/背包/心情/位置/近期事件以 L2 状态摘要形式**强制注入** prompt，不依赖 RAG 检索
4. **记忆干净** — L1 长期记忆库只存"对玩家有意义、需要跨场景长期记住的事"；日常小事只更新 L2 摘要，不污染记忆库
5. **Director 单向控制** — Director → C# 状态层 → NPC Agent，单向流动；NPC 不审批、不协调、不感知 Director
6. **TS 做智能，C# 做执行** — LLM/RAG/决策/对话全在 TS Agent Server；C# 只执行游戏操作、持有游戏数据（含钱包/背包），数值判定优先落 C# 规则侧
7. **性能优先** — 不卡，Token 可控；路由/判定用确定性规则，智能只花在歧义上
8. **AI 的决定必须可追溯** — 程序状态、NPC 思考、导演思考全量留痕，事后能回答"它当时为什么这么想"
9. **认知不得脱离现实** — 每次状态/资产变动必须有回执，NPC 对现实的认知与游戏状态保持一致（海莉事件是总教训）

---

## 4. NPC 状态光谱

| 状态 | 行为 | LLM 消耗 | 进入条件 |
|---|---|---|---|
| **休眠** | 走原版路径，无 AgentBrain | 零 | 默认 |
| **有限自主** | 可对话/交易/送礼/移动；spark 随机激活 | 中（对话或 spark 命中时） | spark 5% / 玩家对话 / Director beat |
| **执行态** | C# GoalExecutor 后台跑 | 零（纯 C#）；完成时 1 次 LLM 汇报 | set_goal 工具调用 |

- **休眠**对应旧架构的 vanilla-release：`MaxConsecutiveIdleBeforeRelease = 3`，只在 IDLE 状态触发，活跃状态受保护。状态变化上报 `state_changed(reason=vanilla_release)`。
- **有限自主**的触发源：spark 随机激活（5%）、玩家对话（PromoteToAgent）、Director beat。对应旧四层编排的 L0/L1/L2。
- **执行态**是全新的：`set_goal` 工具 → C# GoalExecutor 后台执行（零 LLM）→ 完成时 1 次 LLM 汇报。详见 §7。

---

## 5. 记忆与状态分层

### 5.1 三层记忆

| 层 | 存什么 | 注入方式 |
|---|---|---|
| **L1 长期记忆** | 玩家-NPC 重要互动、NPC 间重大事件、玩家说过的有长期影响的话 | RAG 语义检索命中才注入 |
| **L2 状态摘要** | 钱包/背包/位置/心情/近期事件/工作标记/欠款 | **每次对话强制注入** prompt 开头 |
| **L3 临时剧本** | 当前 beat 场景描述 | beat 有效期内强制注入 |

L2 todayEvents 保留近 3 天，每天 day_started 清理 3 天前的。判定标准：这件事是否需要跨场景、跨日长期检索？是 → L1；否 → 只 L2。

### 5.2 四层 → 三层映射（防混淆）

| 旧四层（08-01 记忆 doc） | 新三层 | 处置 |
|---|---|---|
| L1 工作记忆（conversation 50 + short-term 30） | — | 并入对话历史（每轮对话临时拼装），不再作为持久层 |
| L2 情节归档（archive 表） | L1 长期记忆的实现细节 | archive + daily consolidation + significant top-K 注入都是 L1 的实现机制 |
| L3 每日凝练（longTermMemories ≤20，consolidate_day） | L1 长期记忆的实现细节 | 凝练失败保留原始条目不丢，次日重试 |
| L4 重要事项（significantMemories top-K=10） | L1 长期记忆的实现细节 | 按 emotional_weight + 游戏日期 recency 排序 |
| —（不存在） | **L2 状态摘要** | **新发明**：钱包/背包/位置/心情/近期事件/工作标记/欠款，强制注入 |
| —（不存在） | **L3 临时剧本** | 对应 C# BeatStore，beat 有效期内注入 |

**注意**：新三层与旧四层**编号不同含义不同**。旧 L1（工作记忆）≠ 新 L1（长期记忆）。写作与阅读本文档时一律以新三层为准。

### 5.3 记忆机制细则（沿用旧版，机制不变）

- **剪枝不再等于遗忘**：L1 挤出的条目写 archive，不删除。
- **时间轴用游戏日期**：剪枝 recency 以游戏天数计，不用现实秒。
- **关系轨迹锚点**：consolidate_day 凝练 prompt 里固定要求一条"我和农场主的关系近况"摘要——即使细节被剪，NPC 始终知道"我们最近越走越近/他很久没来了"。
- **L2 摘要只更新不污染 L1**：日常小事（"今天浇了三块地"）只写 L2，不写 L1。

---

## 6. Wire 协议与工具集

### 6.1 Wire 协议

单一事实源：`ValleyAI/protocol/messages.json`，`check:protocol` 追踪（对应五层测试体系 L1 契约测试）。active 消息：

| 消息 | 方向 | 说明 |
|---|---|---|
| hello | C#→TS | 握手 |
| ping | 双向 | 心跳 |
| dialogue | C#→TS | **唯一 LLM 触发** |
| dialogue_response | TS→C# | 对话响应（含 actions） |
| action_result | C#→TS | 动作回执（强制，含机器可读 reason） |
| state_changed | C#→TS | 状态转换（reason 枚举：llm_decision/player_request/emergency/evicted/travel_failed/task_completed/handler_exit/day_started/vanilla_release） |
| day_started | C#→TS | 新一天开始 |
| consolidate_day | C#→TS | 每日凝练（旧名 day_end） |
| allocate_agent | TS→C# | 分配/释放 agent |
| route_shout_response | TS→C# | 喊话路由响应 |

`route_shout` 为 orphan_route 预留（C# 的 MorningShoutRouter 是主路由，TS 侧 fallback 是保留扩展点，C# 永不发送）。旧 Python 时代死管道 `decision`/`friendship_eval`/`state_sync`/`emotion_sync`/`memory_sync`/`gift_eval`/`rag_query` 已删除（schema 留档）。

**协议纪律**：任何带 `requestId`/`callId` 的请求，server 必须回带**相同** id 的响应（L1 响应可达性断言）。

### 6.2 NPC Agent 工具集

| 工具 | 说明 |
|---|---|
| speak / emote | 发言/表情 |
| give_item / give_gift | 给物品/送礼（give_item 的 item_id 须传 QualifiedItemId，阶段 1 加名称回落） |
| **trade**（阶段 1） | 交易：NPC 是买家，按 price 单价购买玩家手里 quantity 个 item_id |
| **set_goal**（阶段 2） | 设目标：NPC 进入执行态，C# GoalExecutor 后台跑，完成后寻路回玩家汇报 |
| set_state | 改状态 |
| show_dialogue | 显示对话气泡 |
| remember / forget | 记忆读写 |
| get_info | 查询（date/nearby/memory/health/inventory/player/location） |
| accept_job | 接受雇佣 |
| receive_payment | 收款（阶段 1 已有，调 C# TradeSettlement.SettlePlayerPays） |
| evaluate_friendship | no-op 记录型 |

speak/show_dialogue 为终止工具（agentLoop 检测到即结束，默认 maxTurns=5；beat 的 runBeat maxTurns=8）。

### 6.3 Director 工具集（阶段 3 落地）

| 工具 | 说明 |
|---|---|
| set_npc_position(npc, location, tile) | 移动 NPC |
| set_npc_inventory(npc, add?, remove?) | 增删背包 |
| set_npc_money(npc, delta) | 改钱包 |
| set_npc_mood(npc, moodTag) | 设心情 |
| set_npc_recent_events(npc, events) | 设 L2 todayEvents |
| set_npc_working_on(npc, workingOn?) | 设/清工作标记 |
| spawn_beat(npc, sceneDesc, ...) | 创建单 NPC beat |
| spawn_group_beat(npcs, location, sceneScript, ...) | 创建多 NPC 互动 beat |
| inject_memory(npc, text, importance, tags) | 注入 L1 长期记忆 |

**Director 没有** speak/emote/give_item/trade/set_goal——这些是 NPC Agent 的角色扮演工具。**Director 约束**（prompt 级）："只能改符合人设的状态""不能让 NPC 突然变性格"。

---

## 7. 执行态：GoalExecutor（阶段 2，核心）

### 7.1 设计原则

- **零 LLM 循环**：目标执行期间不调用 LLM。终止条件全部由 C# 确定性判定（目标获得量、位置卡死、全局超时、资源耗尽）。
- **复用现有 Handler**：chop_tree→FarmHandler, mine→MineHandler, fight→FightHandler, forage→ForageHandler, water_crops→FarmHandler。
- **委托模式沿用 WorkCommands**：`handler.SetForcedTarget(name, tile[, action[, targetId]]) → ForceTransition(state) → MoveTo(approachTile)`，Handler 执行到完成自动 `ClearForcedTarget → Stop → ForceTransition(IDLE)`。

### 7.2 数据流

```
NPC Agent（TS）: set_goal(type, params, reportBack)
      ↓ action_result 回执
C# CommandExecutor: case "set_goal" → GoalExecutor.CreateGoal(type, params, npc)
      ↓
GoalExecutor（每 tick）:
  ├─ 未开始 → 创建 Goal 实例 → AgentBrain.PendingGoal = goal → 寻路到任务地点
  ├─ 执行中 → goal.Tick()（调 Handler 干活）
  ├─ 达成   → goal.IsComplete() → 记录成果
  ├─ 失败   → goal.IsFailed()（超时/卡死/资源耗尽）
  └─ 完成   → reportBack=true → 状态 TRAVELING_TO_REPORT → 寻路回玩家 → 触发汇报 LLM
```

### 7.3 Goal 接口（五种实现共用）

```csharp
public interface IGoal
{
    GoalType Type { get; }
    bool IsComplete();   // 终止条件：chop_tree 获得 Wood ≥ qty；mine 获得 targetItemId ≥ qty；
                         // water_crops 浇完所有目标；fight 清除怪物/到时间；forage 采集到指定数量
    bool IsFailed();     // 超时/卡死/资源耗尽
    void Tick();         // 每 tick 推进（调用 Handler）
    string DescribeProgress(); // 供汇报 LLM 使用
}
```

完成判定沿用 GoalVerifier 的**基线差量**模型：捕获开始时的基线快照（起始木头数/怪物数/岩石数），`IsComplete` = 世界状态朝预期方向变化。三值判定 Success/Failure/Unclear，Unclear 用于退化基线。

### 7.4 超时与卡死判定

| 判定 | 条件 | 处理 |
|---|---|---|
| 全局超时 | 游戏内 4 小时（ModConfig 可调） | 失败 → 汇报失败 |
| 位置卡死 | 连续 N tick 位置不变且不在执行动作 | 失败 → 汇报失败 |
| 资源耗尽 | 指定位置找不到目标资源类型 | 失败 → 汇报失败 |
| 玩家取消 | 玩家对话打断 | 取消 → 不汇报 |

### 7.5 寻路汇报（reportBack=true）

- 目标完成 → `MovementService.MoveTo(npc, player.currentLocation, player.tile)`，状态 = `TRAVELING_TO_REPORT`。
- 到达玩家附近（距离 < 5 格）→ 触发汇报 LLM 跑一次：speak + give_item + set_state(IDLE)。
- 寻路失败 fallback：
  - 超时（2 游戏小时）→ inject_memory + update_status_summary
  - 玩家在矿洞深处 → NPC 寻路到矿洞入口等
  - NPC 倒地 → 复活后 inject_memory

### 7.6 移动与寻路（复用 MovementService，唯一碰 npc.controller 的组件）

- 移动一律走 `IMovementService`，不直接碰 `npc.controller`。
- `MoveTo` 三模式冷却：短距离 5 tick / 长距离 30 tick / 立即 0 tick。
- 跨图旅行（AgentNavigator.NavigateToTaskLocation）：单跳 ~3s/每跳 ~2s + 随机方差，单跳 30→180 tick 消除传送感；`DayStarted` 不再把 FOLLOW NPC 贴脸传送到玩家脚下。
- 卡住恢复：同一目标连续失败 3 次后尝试随机邻居；超过 120 tick 未移动直接步进；视野守卫（传送类恢复只在 NPC 不在玩家视野内时允许）。
- PFC 构造用 6 参重载并显式设 `endPoint`、A* 上限提到 50000（修复 5 参重载路径到 (0,0) 的 bug）。
- 距离 <2 tiles 停，>5 tiles 走（FOLLOW 阈值）。

### 7.7 状态机扩展

`AgentState` 增加 `EXECUTING_GOAL` / `TRAVELING_TO_REPORT`。AgentStateMachine 的 `AllowedTransitions` 增加对应边；`ForceTransition` 的 min-duration 守卫（FIGHT 15s / FARM 8s / MINE 10s / FORAGE 8s / FOLLOW 3s）对 GoalExecutor 的强制一次性任务需评估——Handler 现有 `ForceTransition(IDLE)` 在 min-duration 内会被阻塞，GoalExecutor 退出路径需绕过守卫或增加边。

---

## 8. 交易系统（阶段 1）

### 8.1 数据归属（已拍板，阶段 1 架构前提）

> 钱包/背包数据全在 C#；TS/LLM 只负责「谈」，C# 负责「成交」。LLM 在聊天里答应的价格只是**意向**，成交瞬间的游戏状态才算数。

### 8.2 成交原子校验

```
玩家背包里物品还在吗？数量够吗？
NPC 钱包余额够吗？（NPC 买入时）
玩家金钱够吗？（玩家买入时）
→ 全过：扣钱、转物品、写双方记忆、回 action_result（成功）
→ 任一不过：拒绝 + action_result.reason（复用已有反馈管道）
```

"谈妥但成交失败时 NPC **知道原因**，会说「咦，东西呢？」而不是以为自己买到了"——防"海莉式失忆"在经济系统里的重演。

### 8.3 结算机制现状与处置

- `TradeSettlement.SettleNpcBuys`（原子 4 步：移除玩家物品 → NPC 花钱 → NPC 加物品 → 玩家加钱，带回滚）已实现。
- `ExecuteTrade` 当前只创建 30s-TTL PendingOffer，实际结算延迟到玩家物理交接物品（NPCGiftPatch.TrySettlePendingTrade → TradeSettlement.SettleNpcBuys）。
- **阶段 1 决策点**：ExecuteTrade 的结算语义是"意图+延迟交接"还是"即时结算"。设计上保留延迟交接（玩家亲手给物品更符合 SDV 交互），但交易方向必须修正为 NPC 买玩家（修复 Shane/Marnie/Haley 三个 bug 的方向反转与价格锚定）。

### 8.4 定价模型（经济 doc §1.2，阶段 1 临时值待裁决）

- **公道价** = 物品的系统卖价（玩家卖给商店的价格）。
- **心理价区间** = 公道价 × (1 ± 精明度浮动率)。逐 NPC：皮埃尔 ±5%、格斯 ±10%、玛妮 ±15%、莱纳斯 ±50%。
- **阶段 1 prompt 临时值**：±30%（统一，最小改动）。**待裁决**：是否替换为逐 NPC 精明度浮动率（见 §11 待决项 2）。

### 8.5 购买力与还价

- 出价上限 = min(心理价, 钱包余额 × 预算比例)。预算档位：谨慎 30% / 普通 50% / 豪爽（莱纳斯）80%。
- 还价最多 3 轮，让步幅度逐轮减半；连续谈崩 2 次交易关闭；恶意低价（低于公道价下限再低 50%）触发警觉。
- pending offer：每 NPC 同时最多 1 个，默认 30 秒有效期。

### 8.6 交易入口

持物品右键 Agent NPC → 小菜单"送礼 / 交易 / 取消"（方案 A）。菜单防错："玩家明确选了「交易」，不存在「想谈价被当成送礼直接送掉」的事故"。NPC 背包展示界面必须**只读**（预留「展示模式」抽象，未来偷窃功能独立入口复用）。

---

## 9. 聊天与消息分级（阶段 3 相关）

### 9.1 路由优先级（确定性规则，非 LLM）

四层消歧：**名字提及 → 当前会话 → 跟随者/雇佣者 → 最近交互+距离** + 沉默权。

**明确不做**：每句话广播给所有在场 NPC 各自 LLM 判断是否接话——Token 翻 N 倍且必然抢话。路由是确定性规则（便宜），接不接才是智能。

### 9.2 主动发言额度

话痨度（0~1 逐人设）+ 主动发言上限：默认每人每日 ≤2 条。会话模式：60 秒未回应退出，会话内不计额度。长文本：>1 句不进气泡、按句分段进聊天栏。晨间喊话：时段/作息感知，"未醒/在忙可以不回"，连续清晨骚扰写入 NPC 记忆，额度豁免。

### 9.3 消息分级与静默规则

| 级别 | 形式 | 例子 |
|---|---|---|
| NPC 发言 | NPC 专属彩色 | 角色意愿表达 |
| 系统消息 | 灰色斜体 | 机械状态变化 |
| 静默 | 无 | 地图切换全程静默 |

- 区分标准：**机械状态变化 → 系统消息；角色意愿表达 → NPC 发言**。
- 失败也要灰色字，不要沉默：跨图跟随**失败**（跟丢）时发灰色系统消息。
- 地图切换全程静默：跟随中的跨图旅行禁止任何「我马上来」「我到了」类播报。

### 9.4 动态速度

>8 格 2×、3–8 格 1.5×、<3 格 1×。不做体力惩罚。

---

## 10. 验证方法（五层测试体系，沿用 08-01 测试 doc）

| 层 | 定义 | 落地 |
|---|---|---|
| L1 契约测试 | 秒级，CI 必跑，防协议漂移 | `check:protocol`（schema 单一源 messages.json）+ S_send ⊆ S_route + 响应可达性 |
| L2 组件测试 | 现有单测保留，防逻辑回归 | TS `bun test packages/stardew` + C# 单测 |
| L3 故障注入 | 分钟级，CI 必跑，防晴天偏见 | CH 用例（CH-01 海莉事件回归、CH-05 两阶段账本）；全红即构建失败 |
| L4 不变量 | 游戏内长跑 2-4 游戏日，防状态-记忆分裂 | InvariantMonitor 写 logs/invariants/{date}.jsonl；I1-I7 |
| L5 体验打分 | 自动指标 + 视觉辅助 | 幽灵承诺率（目标 0%）、跟随丢失率、反应延迟；事件流 JSONL 全量留痕 |

不变量 I1-I7：跟随连续性 / 状态镜像一致（2s 同步延迟）/ 物资守恒 / 反应保证 / 对话账本 / 位置合法 / 反馈不蒸发（drain 的 tool result 批次 ⟹ 注入 ∨ 记忆写入 ∨ 重入队，三者必居其一）。

**验证门槛**（AGENTS.md v6）：TS 侧 `bun test packages/stardew` + `tsc --noEmit` + `check:protocol` 全绿；C# 侧编译 0 警告 + 游戏内实测（手动或 TestMod）。

---

## 11. 常量表（沿用旧版 + 新增）

### 11.1 状态机时长（constants.json stateDurations）

| 状态 | 最短时长 |
|---|---|
| FIGHT | 15s |
| FARM | 8s |
| MINE | 10s |
| FORAGE | 8s |
| FOLLOW | 3s |
| 其他 | 0s |

### 11.2 好感度 → 阶段映射（npc_prompts.json）

| 好感度 | 阶段 |
|---|---|
| 0-250 | stranger |
| 251-500 | acquaintance |
| 501-1000 | friend |
| 1001-2000 | close |
| 2001-2500 | partner |

### 11.3 各状态行为常量

- FOLLOW：距离 <2 tiles 停，>5 tiles 走
- FIGHT：攻击冷却 48 ticks；FightHandler 扫描半径 12
- FARM：扫描 15 tiles（收获优先于浇水）
- MINE：扫描 12 tiles
- FORAGE：扫描 15 tiles（必须户外或矿洞）
- TALK：问候语冷却 60s（3600 ticks）、超时 30s（1800 ticks）
- MovementService：短距离 5 tick / 长距离 30 tick / 立即 0 tick 冷却；卡住 3 次随机邻居 / 120 tick 步进
- 跨图旅行：单跳 ~3s/每跳 ~2s + 随机方差；单跳 30→180 tick
- vanilla-release：MaxConsecutiveIdleBeforeRelease = 3，只在 IDLE 触发

### 11.4 新增常量（阶段 2/3）

| 常量 | 值 | 所属 |
|---|---|---|
| GoalExecutor 全局超时 | 4 游戏小时（ModConfig 可调） | 阶段 2 |
| 寻路汇报超时 | 2 游戏小时 | 阶段 2 |
| 汇报触发距离 | <5 格 | 阶段 2 |
| todayEvents 保留 | 3 天，条数 ≤5 | 阶段 3 |
| DirectorContextBuilder 预算 | 800-1500 token | 阶段 3 |
| spark 随机激活 | 5% | 光谱 |
| 交易让步 | ±30%（临时）/ 逐 NPC 精明度（待裁决） | 阶段 1 |

### 11.5 经济配置（经济 doc §1.6）

精明度浮动率 0.10（默认）/ 预算比例 0.5（谨慎 0.3 / 普通 0.5 / 豪爽 0.8）/ 还价最大轮数 3 / 让步衰减 0.5 / 恶意低价阈值 低于公道价下限 50% / 求购加价率 1.0~1.1。

---

## 12. 已知坑（沿用 AGENTS.md v6 §3.6）

- `ClientWebSocket` 必须 `Options.Proxy = null`，否则系统代理截胡本机 WS。
- `src/ValleyAgent/config.json` 不得作为 csproj 部署项（会覆盖用户配置）。
- 服务器生命周期绑定 Mod 而非存档，返回标题不杀进程。
- **导演日志静默 bug（2026-08-04）**：降级是行为上的（不崩溃），日志是可观测性的（必须可见）。任何 LLM 调用必须 log prompt 输入和 response 输出；任何决策分支必须 log 分支结果和原因；任何过滤/丢弃必须 log 被丢弃项和原因。
- `Stardew Valley/Mods` 目录被 git 跟踪：`dotnet build` 会部署覆盖跟踪文件，提交前必须 `git checkout -- "Stardew Valley/Mods"`，只 `git add` 预期源文件。

---

## 13. 阶段实施要点（与执行计划对应）

| 阶段 | 内容 | TS 端 | C# 端 | 关键决策 |
|---|---|---|---|---|
| 阶段 1 | 交易 bug 修复 | trade 工具 + playerHeldItem + prompt 交易规则 + give_item item_id 描述 | ExecuteTrade 实际实现 + give_item 名称回落 + 修复死代码 | 结算语义（延迟交接 vs 即时）；±30% vs 精明度 |
| 阶段 2 | set_goal + GoalExecutor | set_goal 工具 + EXECUTING_GOAL/TRAVELING_TO_REPORT 状态 + currentGoal 字段 | GoalExecutor + 五种 Goal + 超时/卡死 + 寻路汇报 | 复用 Handler 委托模式；状态机新边 |
| 阶段 3 | 大重构 | L2 状态摘要 prompt 段 + Director 工具协议 | Director 工具集 + AgentBrain L2 字段 + BeatStore + PlayerActionTracker + DirectorContextBuilder + 默认创建 + per-NPC 配置 | Director prompt 约束；todayEvents 字数限制；全员 AgentBrain 资源 |

每阶段独立验证，阶段间保持兼容性。

---

## 14. 待决冲突与开放问题

### 14.1 待决冲突

1. **旅行中说话 vs 全程静默**：08-01 允许 Travelling 中 speak 走 chatBox（"我马上就到"是沉浸感）vs 08-02 规则 1 禁止跨图旅行播报。**裁决建议**：以 08-02 为准（跨图旅行全程静默），但保留非跟随类旅行/高级 beat 中发言的权利——按"说话内容"而非"说话动作"裁决。
2. **trade 让步幅度**：阶段 1 prompt 硬编码 ±30% vs 经济 doc 逐 NPC 精明度（默认 10%）。±30% 是阶段 1 最小改动临时值；阶段 1 验证通过后再迁移到逐 NPC 精明度模型。
3. **PendingTrade 机制**：阶段 1.2.4 移除 vs 保留作为礼物流程延伸。**裁决建议**：保留 PendingOffer 作为物理交接路径（玩家亲手给物品符合 SDV 交互），只修正交易方向与价格锚定。
4. **全员 AgentBrain 资源**：不和玩家交互的 NPC 只走原版路径，AgentBrain 文件存在但按需激活（spark 5% 限流 + 对话触发）。

### 14.2 开放问题（实施时再细化）

1. Director spawn NPC 到玩家身边的寻路细节（玩家在矿洞深处怎么办）
2. spawn_group_beat 的玩家可见性判定（距离阈值还是视野判定）
3. Director LLM 的 token 预算上限
4. L2 todayEvents 的字数限制
5. NPC 间互动的 sceneScript 是否支持分支

这些问题不影响阶段 1/2 的实施，阶段 3 实施时再细化。

---

## 15. 文档索引

| 文档 | 内容 |
|---|---|
| `docs/plan/2026-08-05-three-tier-architecture-execution-plan.md` | **执行计划（唯一实施依据）** — 三阶段落地 |
| `docs/design/2026-08-01-npc-feedback-architecture.md` | 三场景设计 + ActionLedger 强制回执 + prompt 段序（本文 §2/§8 的源头） |
| `docs/design/2026-08-01-memory-narrative-extensibility.md` | TranscriptStore 留痕 + 记忆机制（本文 §5 的机制来源） |
| `docs/design/2026-08-02-npc-economy-hire-chat-requirements.md` | NPC 经济/雇佣/聊天栏需求（本文 §8/§9 的源头） |
| `docs/design/2026-08-01-test-scoring-redesign.md` | 五层测试体系（本文 §10 的源头） |
| `docs/archive/AGENTS-2026-08-02-v4.3-full.md` | 旧版完整 AGENTS（常量表、状态机细节、模块矩阵） |
