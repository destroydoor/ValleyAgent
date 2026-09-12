# 叙事导演引擎设计文档

> **创建时间**: 2026-07-21
> **状态**: 设计阶段（待用户审核）
> **代码位置**: `D:\Source\ValleyAI`（TS 服务器）+ `D:\Source\ValleyTalk`（C# Mod）
> **背景**: 当前架构让 NPC 每 30 秒调一次 LLM 在 7 状态 FSM 里做反应式决策，导致 Token 消耗高（2880 次/天/NPC）且无法保证玩家体验密度（NPC 不会主动找玩家、下矿钓鱼种地遇不到 NPC）。本设计引入双层 LLM 叙事导演架构解决这两个核心问题。

---

## 1. 核心决策摘要

| 决策点 | 选择 | 理由 |
|--------|------|------|
| 架构模型 | 双层 LLM：导演 Agent + NPC Agent | 导演保证体验密度，NPC Agent 保留自主感 |
| 导演触发 | 混合（早晨 6am 计划 + 里程碑反应） | 骨架稳定 + 关键时刻灵活插点 |
| Beat 分发 | 聚光灯模型（0-3 NPC/天，间隔几天一次） | 避免新鲜感消退，Token 可控 |
| Beat 执行 | 导演给方向，NPC Agent ReAct 即兴发挥 | NPC 灵活自主，不脚本死板 |
| ReAct 步数 | 无硬上限，ReActGuard 软刹车 | 灵活但可控 |
| Beat 间隙 | 原版日程（followSchedule=true） | 零 LLM 调用，游戏世界不空洞 |
| 玩家对话 | 保留 LLM 对话管线 | 对话是核心体验不可省 |
| 玩家画像 | 五层档案（静/行/偏/性/故） | 导演决策的核心输入 |
| 游戏上下文 | 每日同步 + 玩家状态增量推送 | 导演决策的世界锚点 |

---

## 2. 高层架构

### 2.1 三层架构

```
┌─────────────────────────────────────────────────────────────────┐
│ C# Mod (ValleyAgent) — 纯执行器 + 玩家行为采集器                   │
│  ├─ ActivityTracker（新增）— 实时采集玩家动作并打包                 │
│  │     ├─ 钓鱼/挖矿/种地/采集 的次数、时长、产出                    │
│  │     ├─ 送礼/对话 的对象、礼物 ID、对话情绪标签                   │
│  │     ├─ 跨区域移动（进入/离开时间戳）                            │
│  │     ├─ FarmProfiler（新增）— 扫描农场推断流派                   │
│  │     │     ├─ 酒桶数 → 酿酒流                                   │
│  │     │     ├─ 种植面积 → 种田流                                 │
│  │     │     ├─ 矿洞深度/怪物击杀 → 矿工/战斗流                    │
│  │     │     └─ 牧场规模 → 牧场流                                 │
│  │     └─ 里程碑检测（连续3天钓鱼、送同款礼物5次、累计对话10次...）│
│  ├─ BeatExecutor（新增）— 收到 beat 后接管 NPC，结束还回原版       │
│  ├─ BeatScheduler（新增）— 持活跃 beats，按时间窗口激活            │
│  ├─ AgentTickLoop（保留）— steering + FIGHT 紧急反应              │
│  └─ WebSocketClient（保留）— 协议层                                │
├─────────────────────────────────────────────────────────────────┤
│ @valley/stardew (TS)                                              │
│  ├─ Director（新增）— 全局导演 Agent                               │
│  │     ├─ 早晨 6am 计划：读 PlayerProfile + GameContext → 出 beats │
│  │     ├─ 里程碑触发：增量生成 beat                                │
│  │     └─ 维护节奏预算（最近 14 天 beat 历史）                     │
│  ├─ PlayerProfile（新增）— 玩家画像档案（五层）                    │
│  ├─ GameContext（新增）— 星露谷游戏状态快照                        │
│  ├─ StardewAgent（扩展）— 增加 runBeat(directive) ReAct 入口       │
│  ├─ ReActGuard（新增）— 软刹车控制器                               │
│  ├─ BeatStore（新增）— 持久化 beats 到 SQLite                     │
│  ├─ ActivityLogStore（新增）— 持久化玩家活动日志                   │
│  ├─ PlayerProfileStore（新增）— 持久化玩家画像                    │
│  └─ 现有 dialogue/memory/RAG 模块保留                              │
├─────────────────────────────────────────────────────────────────┤
│ @valley/core (TS) — 复用现有 agent-loop/ToolRegistry/CircuitBreaker│
└─────────────────────────────────────────────────────────────────┘
```

### 2.2 关键边界

- C# 不解析 beat 内容、不存 beat 状态，只按 `BeatScheduler` 的激活信号接管/释放 NPC
- TS 不直接调任何 StardewValley API，所有游戏操作通过 C# tool_call 执行
- 玩家活动日志由 C# 采集，TS 持久化（C# 重启可从 TS 拉取）
- Beat 间隙 NPC 走原版日程（`followSchedule=true`），零 LLM 调用

### 2.3 新增消息类型

| 消息 | 方向 | 时机 | 用途 |
|------|------|------|------|
| `activity_report` | C# → TS | 每日 6am | 昨日完整活动 + 农场扫描快照 |
| `activity_milestone` | C# → TS | 里程碑达成时 | 实时推送里程碑事件 |
| `game_context_sync` | C# → TS | 每日 6am | 完整游戏进度快照 |
| `player_state_update` | C# → TS | 每 60 tick | 玩家位置/生命/能量/钱/背包增量 |
| `beat_directive` | TS → C# | 导演生成 beat 时 | 下发 beat 给 BeatScheduler |
| `beat_activate` | C# → TS | triggerTime 到达时 | 请求激活 beat，启动 ReAct |
| `beat_event` | C# → TS | beat 执行中 | 工具执行结果回注 |
| `beat_state` | TS → C# | ReAct 每步 | 工具调用指令 |

---

## 3. Beat 生命周期

### 3.1 Beat 数据结构

```typescript
interface Beat {
  id: string;                          // UUID
  npcName: string;                     // 目标 NPC
  triggerTime: string;                 // "14:00" 当日触发
  windowEnd: string;                   // "16:00" 窗口结束
  directive: string;                   // 高层指令，自然语言
  context: {
    reasonGenerated: string;           // 导演为何生成这个 beat
    playerProfileSnapshot: PlayerProfile;
    gameContextSnapshot: GameContext;
    recentBeats: Beat[];               // 最近 3 个相关 beat
  };
  status: "scheduled" | "active" | "completed" | "skipped" | "failed";
  reactSteps?: ReActStep[];            // 执行轨迹（调试用）
}
```

### 3.2 Beat 状态机

```
scheduled ──(triggerTime 到达)──> active
                                    │
                                    ├──(ReAct 正常完成)──> completed
                                    ├──(windowEnd 超时)──> skipped
                                    ├──(ReActGuard 触发)──> failed (降级)
                                    └──(LLM 持续失败)──> failed (降级)
```

### 3.3 Beat 激活流程（C# 侧 BeatScheduler）

1. 当前时间 ≥ `triggerTime` 且 status=scheduled → 请求激活
2. 前置条件检查：
   - NPC 不在对话中
   - NPC 不在 FIGHT 紧急状态
   - 玩家在线（联机模式下任意玩家在线即可）
3. 通过检查 → 调用 TS 的 `beat_activate` 接口
4. C# 侧 BeatExecutor 接管 NPC（`followSchedule=false`）

### 3.4 Beat 执行流程（TS 侧 StardewAgent.runBeat）

1. 注入 prompt：NPC 人设 + directive + playerProfile + gameContext + recentBeats
2. 进入 ReAct 循环：
   - LLM 输出 thought + tool_call
   - tool_call 通过 `beat_state` 消息下发给 C# 执行
   - C# 执行后回 `beat_event` 给 TS
   - ReActGuard 检查刹车条件
   - 继续 / 终止
3. 正常完成 → status=completed，C# 释放 NPC 回原版日程

### 3.5 Beat 结束后处理

- TS 把 beat 写入 `BeatStore`（历史记录，供导演下次参考避免套路）
- C# BeatExecutor 释放 NPC，`followSchedule=true`
- AgentTickLoop 恢复正常运转

### 3.6 Beat 降级处理

- **skipped**：windowEnd 超时未触发 → 直接结束，记日志
- **failed**：ReActGuard 触发或 LLM 连续失败 → NPC 说一句兜底对白（"今天有点走神了"）+ emote + 释放回原版

### 3.7 Beat 并发约束

- 同一时刻一个 NPC 只能有 1 个 active beat
- 同一时刻全服最多 3 个 active beat（避免游戏世界空洞）
- 节日当天 / 婚礼当天 / 玩家住院 等特殊日不触发 beat

---

## 4. PlayerProfile 五层档案

### 4.1 数据结构

```typescript
interface PlayerProfile {
  // 静态层 — 游戏开始时填充
  static: {
    farmerName: string;
    gender: "male" | "female" | "unknown";
    farmName: string;
    farmType: string;
    startDate: string;
    lastUpdated: string;
  };

  // 行为层 — 每日由 ActivityTracker 上报，TS 累积存储
  behavior: {
    dailyActivities: DailyActivity[];  // 最近 30 天
    totalStats: {
      fishCaught: number;
      itemsShipped: number;
      monstersKilled: number;
      cropsHarvested: number;
      itemsForaged: number;
      giftsGiven: number;
      dialoguesHad: number;
      miningLevelsDescended: number;
    };
  };

  // 偏好层 — 由行为层推导，每周刷新
  preferences: {
    playStyle: PlayStyle[];
    topActivities: ActivityRank[];
    topLocations: LocationRank[];
    routinePattern: string;
    lastUpdated: string;
  };

  // 关系层 — 每 NPC 独立，每日刷新
  relationships: {
    [npcName: string]: {
      phase: string;
      friendshipPoints: number;
      last5Interactions: Interaction[];
      giftHistory: GiftRecord[];
      notableEvents: string[];
      lastUpdated: string;
    };
  };

  // 性格层 — 导演 LLM 推断，每季节（28 天）刷新
  personality: {
    traits: string[];
    archetype: string;
    narrativeRole: string;
    lastUpdated: string;
  };

  // 故事层 — 已发生的 beat 历史
  story: {
    completedBeats: BeatHistoryEntry[];
    recurringTropes: string[];
    lastUpdated: string;
  };
}
```

### 4.2 关键子类型

```typescript
interface DailyActivity {
  date: string;
  fishingMinutes: number;
  farmingMinutes: number;
  miningMinutes: number;
  foragingMinutes: number;
  socialMinutes: number;
  combatMinutes: number;
  locationsVisited: string[];
  fishCaught: number;
  cropsHarvested: number;
  itemsShipped: number;
  monstersKilled: number;
  npcsTalkedTo: string[];
  giftsGiven: { to: string; itemId: string }[];
}

interface PlayStyle {
  tag: string;                         // "brewer" | "farmer" | "miner" | ...
  confidence: number;                  // 0.0~1.0
  evidence: string;                    // 推断依据
}

interface Interaction {
  date: string;
  type: "dialogue" | "gift" | "quest" | "combat_together";
  summary: string;
  emotionTag: string;
}

interface BeatHistoryEntry {
  beatId: string;
  date: string;
  npcName: string;
  directive: string;
  outcome: "completed" | "skipped" | "failed";
  playerReaction?: string;
}
```

### 4.3 FarmProfiler 流派推断规则

| 流派 tag | 检测信号 | 阈值 |
|----------|---------|------|
| `brewer`（酿酒） | 农场内 Keg 数量 | ≥ 8 |
| `farmer`（种田） | 种植作物格数 | ≥ 100 |
| `rancher`（牧场） | FarmAnimal 数量 | ≥ 4 |
| `miner`（矿工） | 近 7 天 miningMinutes 中位 | ≥ 60 分钟/天 |
| `warrior`（战斗） | 近 7 天 monstersKilled | ≥ 30/天 |
| `forager`（采集） | 近 7 天 itemsForaged | ≥ 20/天 |
| `socializer`（社交） | 近 7 天 dialoguesHad | ≥ 10/天 |

- 多流派可共存
- confidence = 信号强度 / 全部信号总和
- C# 每 7 天扫描一次

### 4.4 PlayerProfile 更新时机

| 层 | 时机 | 谁负责 | LLM 调用 |
|----|------|--------|---------|
| 静态层 | 游戏开始 | C# 上报 | 无 |
| 行为层 | 每日 6am | C# 上报，TS 存储 | 无 |
| 偏好层 | 每 7 天 | TS LLM 推断 | 1 次/周 |
| 关系层 | 每日 6am | TS 综合 C# 数据 | 无 |
| 性格层 | 每 28 天 | TS LLM 推断 | 1 次/季节 |
| 故事层 | beat 完成后 | TS 追加 | 无 |

---

## 5. GameContext 游戏上下文

### 5.1 数据结构

```typescript
interface GameContext {
  time: {
    year: number;
    season: "spring" | "summer" | "fall" | "winter";
    day: number;
    dayOfWeek: string;
    weather: "sunny" | "rainy" | "snowy" | "stormy";
    isFestivalDay: boolean;
    festivalName?: string;
  };

  progress: {
    communityCenterComplete: boolean;
    communityCenterBundlesDone: string[];
    jojaMartRoute: boolean;
    islandsUnlocked: string[];
    desertUnlocked: boolean;
    railroadUnlocked: boolean;
    sewersUnlocked: boolean;
    greenhouseRestored: boolean;
  };

  seasonalResources: {
    plantableCrops: string[];
    catchableFish: string[];
    forageItems: string[];
    activeFestivals: string[];
  };

  npcStates: NpcStateSnapshot[];

  playerState: {
    location: string;
    tile: { x: number; y: number };
    health: number;
    maxHealth: number;
    energy: number;
    maxEnergy: number;
    money: number;
    inventory: InventorySlot[];
  };

  lastUpdated: string;
}
```

### 5.2 更新策略

- C# 每日 6am 通过 `game_context_sync` 推送完整快照
- C# 每 60 tick 只推送 `playerState` 增量
- NPC 状态只在 beat 激活时按需查询

### 5.3 导演 prompt 注入

GameContext 压缩成紧凑文本注入导演 prompt：

```
[游戏世界]
时间: Year 2 Summer 14 (周二), 晴天
进度: 社区中心已完成 4/6, 已解锁沙漠, 未解锁姜岛, 温室已修复
季节资源: 可种[玉米/向日葵/辣椒], 可钓[虹鳟/红鲷], 节日[夏威夷宴会]
玩家: 在 Farm (32,18), 生命 95/100, 钱 8500g, 背包有[玉米x12, 锄头, 钓竿]
```

---

## 6. Director 决策机制

### 6.1 早晨计划流程（每日 6am）

```
C# 发送 activity_report + game_context_sync
  │
  └── TS Director.morningPlan()
        │
        ├── 1. 加载 PlayerProfile（全五层）
        ├── 2. 加载 GameContext
        ├── 3. 加载 BeatStore 最近 14 天历史
        ├── 4. 节奏预算检查：
        │     ├── 最近 3 天有 beat → 今天不再发 beat（间隔约束）
        │     ├── 最近 7 天 beat 数 ≥ 4 → 今天不发
        │     └── 上次同 NPC 的 beat < 5 天前 → 该 NPC 今天不发
        ├── 5. 构建 prompt（见 6.2）
        ├── 6. LLM 调用 → 输出 0-3 个 beat 指令
        ├── 7. 校验 beat 输出（见 6.3）
        ├── 8. 持久化到 BeatStore
        └── 9. 通过 beat_directive 下发给 C# BeatScheduler
```

### 6.2 导演早晨计划 prompt 模板

```
你是星露谷的叙事导演。你的职责是设计 NPC 与玩家之间有意义的互动故事，
让玩家感觉到这个世界是活的、NPC 是关心他的。

[玩家画像]
姓名: {farmerName}, 农场: {farmName} ({farmType})
流派: {playStyles} (依据: {evidence})
性格: {traits}, 原型: {archetype}
叙事角色: {narrativeRole}
日常规律: {routinePattern}
最近活动: {recentActivitiesSummary}
关系状态: {relationshipsSummary}

[游戏世界]
{gameContextText}

[节奏约束]
最近 14 天已发的 beat: {recentBeatsSummary}
已用套路: {recurringTropes}
今天是否适合发 beat: {rhythmBudgetAssessment}

[设计原则]
1. 不要每天都安排特殊故事——新鲜感来自稀缺，隔几天一次最好
2. beat 要与玩家当前的行为和偏好相关——酿酒流玩家就让 NPC 对酒感兴趣
3. 避免重复套路——检查 recurringTropes，不要用同样的剧情结构
4. beat 要符合 NPC 人设——Abigail 不会突然去种地，Sebastian 不会去社交
5. 尊重游戏节奏——节日/婚礼/矿洞深层等特殊场景不发 beat
6. 高层指令只给方向，不写具体台词和动作——"下午去农场表达对玩家劳作的关心"而非"走到玩家面前说'你辛苦了'然后送一个玉米"

[输出格式]
输出 JSON 数组，0-3 个 beat。每个 beat 包含:
- npcName: NPC 名（必须从可用 NPC 列表中选）
- triggerTime: 当天触发时间 "HH:MM"
- windowEnd: 窗口结束 "HH:MM"（至少比 triggerTime 晚 1 小时）
- directive: 高层指令（1-2 句自然语言，给方向不给细节）
- reasonGenerated: 为什么选这个 NPC 和这个 beat（供 NPC Agent 理解动机）

如果没有合适的 beat，输出空数组 []。
```

### 6.3 Beat 输出校验

导演 LLM 输出后，TS 侧校验：
- `npcName` 在当前 GameContext 的可用 NPC 列表中
- `triggerTime` 和 `windowEnd` 格式合法且 windowEnd > triggerTime
- `directive` 非空且长度 ≤ 200 字符
- 该 NPC 今天没有被分配过 beat
- 该 NPC 最近 5 天没有被分配过 beat
- 节奏预算未被耗尽

校验失败的 beat 直接丢弃，记日志，不降级重试（导演输出 0 个 beat 是合法的）。

### 6.4 里程碑触发流程

```
C# 检测到里程碑（如"玩家连续钓鱼 3 天"）
  │
  └── 发送 activity_milestone
        │
        └── TS Director.milestoneReact(milestone)
              │
              ├── 1. 节奏预算快速检查（同 6.1 step 4）
              ├── 2. 构建 prompt（见 6.5）
              ├── 3. LLM 调用 → 输出 0-1 个 beat
              ├── 4. 校验（同 6.3）
              ├── 5. 持久化 + 下发
              └── 6. 如果生成 beat，触发时间设为当天最近的时间窗口
```

### 6.5 里程碑反应 prompt 模板

```
玩家刚刚达成了一个行为里程碑，你需要判断是否值得为此生成一个即兴 NPC 互动。

[玩家画像]
{playerProfileSummary}

[游戏世界]
{gameContextText}

[里程碑事件]
{milestoneDescription}
例: "玩家连续第 3 天去海边钓鱼，累计钓到 47 条鱼"

[节奏约束]
最近 14 天已发的 beat: {recentBeatsSummary}

[判断原则]
1. 里程碑要与 NPC 有关联——钓鱼里程碑适合 Willy/Abigail，不适合 Sebastian
2. 节奏预算未耗尽才发
3. 即兴 beat 要比计划 beat 更轻量——一个简短的问候/帮助/好奇就够
4. 如果里程碑不值得即兴反应，输出空数组 []

[输出格式]
同早晨计划，但最多 1 个 beat。
```

### 6.6 里程碑检测规则（C# 侧）

| 里程碑 | 检测条件 |
|--------|---------|
| 连续钓鱼 N 天 | 连续天数 ≥ 3 且当天钓鱼 ≥ 30 分钟 |
| 连续挖矿 N 天 | 连续天数 ≥ 3 且当天挖矿 ≥ 60 分钟 |
| 连续种地 N 天 | 连续天数 ≥ 3 且当天种地 ≥ 30 分钟 |
| 送同款礼物 N 次 | 同一 NPC 同一物品累计 ≥ 3 次 |
| 累计对话 N 次 | 某 NPC 累计对话 ≥ 10 次 |
| 累计击杀 N 怪 | 累计击杀 ≥ 100 |
| 首次进入新区域 | 沙漠/姜岛/铁路 首次进入 |
| 社区中心首个 bundle | 首个 bundle 完成 |

- 同类里程碑 7 天内只触发一次（避免连续钓鱼每天都触发）
- 里程碑触发后 C# 标记已触发，不重复推送

---

## 7. ReActGuard 软刹车

### 7.1 设计目标

ReAct 不设硬步数上限，但通过多维度的"软刹车"在 NPC 陷入死循环或资源浪费时优雅退出。

### 7.2 刹车条件（任一触发即终止 beat）

| 条件 | 阈值 | 检测方式 |
|------|------|---------|
| 重复工具调用 | 连续相同 tool_call 3 次 | 比较最近 3 步的 tool+args 哈希 |
| 无进度 | 连续 5 步 NPC 位置/状态无变化 | 比较位置 tile 和 AgentState |
| 时间窗口超时 | 当前时间 > beat.windowEnd | C# BeatScheduler 每 tick 检查 |
| Token 预算 | 单 beat 累积 token > 8000 | TS 累计 LLM 响应 token |
| 玩家离场 | 玩家离开 beat 关注区域 > 120 秒 | C# 跟踪玩家与 NPC 距离 |
| LLM 连续失败 | 连续 3 次 LLM 调用异常 | CircuitBreaker 协同 |
| 工具执行失败 | 同一工具连续失败 3 次 | C# 回报失败计数 |

### 7.3 刹车后处理

```
ReActGuard 触发
  │
  ├── 记录触发原因到 beat.reactSteps
  ├── beat.status = "failed"
  ├── TS 发送 beat_state { status: "failed", reason: "..." }
  ├── C# BeatExecutor 执行降级：
  │     ├── NPC 说兜底对白（从预设池随机选）
  │     ├── 播放 emote（困惑/叹气）
  │     └── 释放 NPC 回原版日程
  └── TS 更新 BeatStore
```

### 7.4 预设兜底对白池

```
[
  "（今天有点走神了……）",
  "（我好像忘了要做什么。）",
  "（嗯……算了，回头再说吧。）",
  "（突然想不起刚才在想什么了。）",
]
```

- 每次降级随机选一条，避免重复
- 兜底对白不调 LLM，纯本地

---

## 8. 错误处理与降级

### 8.1 降级链

```
LLM 正常 → ReAct 正常执行
  │
  ├── LLM 超时/限流 → CircuitBreaker OPEN
  │     ├── Beat 执行中 → ReActGuard 触发 → 降级兜底
  │     └── 导演计划 → 跳过今天 beat 生成（不发比发错好）
  │
  ├── WebSocket 断连
  │     ├── Beat 执行中 → C# 超时 30 秒后降级兜底
  │     └── 导演计划 → C# 缓存 activity_report，重连后补发
  │
  ├── C# BeatExecutor 异常
  │     ├── NPC 卡住 → AgentTickLoop 紧急回收
  │     └── NPC 死亡 → beat 标记 failed，不复活
  │
  └── TS 服务器崩溃
        ├── C# 检测 WS 断连 → 所有 active beat 降级兜底
        └── C# 保留 BeatScheduler 状态，TS 重启后重新同步
```

### 8.2 关键超时配置

| 操作 | 超时 | 重试 |
|------|------|------|
| 导演 LLM 调用 | 60 秒 | 1 次 |
| NPC Agent ReAct 单步 LLM | 30 秒 | 2 次 |
| 玩家对话 LLM | 30 秒 | 2 次（现有） |
| WebSocket beat_activate 请求 | 10 秒 | 0 次 |
| C# BeatExecutor 工具执行 | 15 秒/工具 | 0 次 |
| PlayerProfile 偏好层 LLM | 60 秒 | 1 次 |
| PlayerProfile 性格层 LLM | 90 秒 | 1 次 |

### 8.3 数据一致性

- BeatStore 每次状态变更立即写 SQLite（WAL 模式）
- PlayerProfileStore 每日 6am 更新后写一次，beat 完成后增量更新故事层
- ActivityLogStore 每日 6am 写入，幂等（同日期覆盖）
- C# 重启后从 TS 拉取 BeatStore 当前 scheduled beats，恢复 BeatScheduler

---

## 9. 测试策略

### 9.1 单元测试（TS 侧）

| 模块 | 测试重点 |
|------|---------|
| Director | 早晨计划输出校验、节奏预算计算、里程碑过滤 |
| PlayerProfile | 五层档案序列化/反序列化、每日更新逻辑 |
| GameContext | 快照构建、增量推送合并 |
| ReActGuard | 7 种刹车条件各自触发、不误触 |
| BeatStore | 持久化往返、历史查询、重复检测 |
| FarmProfiler 规则 | 各流派阈值边界 |

### 9.2 单元测试（C# 侧）

| 模块 | 测试重点 |
|------|---------|
| ActivityTracker | 动作计数准确性、时长统计、里程碑检测 |
| FarmProfiler | 农场实体扫描、流派判定 |
| BeatScheduler | 时间窗口激活、并发上限、前置条件检查 |
| BeatExecutor | NPC 接管/释放、降级兜底对白 |

### 9.3 集成测试

| 场景 | 验证点 |
|------|--------|
| 早晨计划 E2E | C# 上报 → TS 导演 → beat 下发 → C# 调度 |
| Beat 激活 E2E | triggerTime 到达 → ReAct 启动 → 工具执行 → 完成 |
| 里程碑触发 E2E | C# 检测 → TS 反应 → 即兴 beat |
| ReActGuard 刹车 | 重复工具 → 降级兜底 |
| WS 断连恢复 | 断连 → 降级 → 重连 → 状态同步 |
| 节奏预算 | 连续 3 天有 beat → 第 4 天不发 |

### 9.4 体验测试（TestMod 扩展）

| 测试 | 验证点 |
|------|--------|
| EXP015_DirectorBeat | 玩家钓鱼 3 天后 Willy 主动来找 |
| EXP016_BeatReAct | NPC beat 内 ReAct 行为可见且合理 |
| EXP017_BeatFallback | ReActGuard 触发后兜底对白显示 |
| EXP018_BeatScheduleRelease | beat 结束后 NPC 回原版日程 |
| EXP019_PlayerProfileAccuracy | FarmProfiler 流派判定正确 |

### 9.5 静态分析

- TS 侧：`tsc --noEmit` 0 错误，`bun test` 全通过
- C# 侧：`dotnet build` 0 警告 0 错误
- 现有 437 项单元测试不回归

---

## 10. Token 预算汇总

### 10.1 每日 Token 成本估算（10 个活跃 NPC）

| 调用源 | 频次/天 | Token/次 | 日 Token |
|--------|---------|---------|----------|
| 导演早晨计划 | 1 | ~3000 | 3000 |
| 导演里程碑反应 | 0-1 | ~2000 | 0-2000 |
| NPC Agent ReAct（per beat） | 0-3 beats | ~2000/beat | 0-6000 |
| 玩家对话 | 按玩家行为 | ~1500/次 | 可变 |
| PlayerProfile 偏好层 | 1/7 天 | ~2000 | 285（摊销） |
| PlayerProfile 性格层 | 1/28 天 | ~3000 | 107（摊销） |

**对比当前架构**：
- 当前：10 NPC × 2880 次/天 × ~1000 token = **28,800,000 token/天**
- 新架构（无玩家对话）：~9000-11000 token/天
- 新架构（含 10 次玩家对话）：~24000-26000 token/天
- **Token 成本降低 > 99%**

### 10.2 Token 预算硬限制

| 层级 | 限制 | 超限行为 |
|------|------|---------|
| 单次 LLM 调用 | maxTokens=800 | 截断 |
| 单 beat 累积 | 8000 token | ReActGuard 触发 |
| 单日导演调用 | 3 次（1 早晨 + 2 里程碑） | 超过则丢弃 |
| 单日 NPC Agent 调用 | 10 次（3 beat × ~3 步 + 1 次兜底） | 超过则降级 |
| 单日总 Token | 50000 | 拒绝新 LLM 调用，全部降级 |

---

## 11. 实施范围边界

### 11.1 本设计涵盖

- C# 新增：ActivityTracker、FarmProfiler、BeatExecutor、BeatScheduler
- TS 新增：Director、PlayerProfile、GameContext、ReActGuard、BeatStore、ActivityLogStore、PlayerProfileStore
- TS 扩展：StardewAgent 增加 runBeat 入口
- WebSocket 协议新增 8 种消息类型
- 测试：单元 + 集成 + 体验

### 11.2 本设计不涵盖（显式排除）

- 现有 7 状态 FSM 和 Handler 不改动（beat 间隙仍用原版日程，不经过 FSM；但 AgentTickLoop 的 `CheckEmergencyState` 仍生效——怪物靠近时临时接管 NPC 进入 FIGHT，战斗结束还回原版日程）
- 现有对话管线不改动（玩家点击对话仍走 runDialogue）
- 现有 RuleBasedDecisionEngine 不改动（仅在 FIGHT 紧急时兜底）
- 联机模式下的 Director 隔离（host 独占导演，farmhand 不运行导演）留作后续迭代
- PlayerProfile 的性格层 LLM 推断 prompt 精细化留作后续迭代
- 多导演协作（如不同 NPC 群组由不同导演负责）留作后续迭代

### 11.3 与现有代码的兼容性

- 现有 `AgentTickLoop` 保留，新增 `BeatScheduler` 作为前置检查层
- 现有 `StardewAgent.runDialogue` 保留，新增 `runBeat` 并行入口
- 现有 WebSocket 协议消息全部保留，新增消息类型不冲突
- 现有 SQLite 表保留，新增 `beats`、`activity_logs`、`player_profiles` 三张表
- 现有 `agents/<npc>_memory.json` 保留不变

---

## 12. 文件结构映射

### 12.1 C# 侧（`D:\Source\ValleyTalk\src\ValleyAgent\`）

| 文件 | 操作 | 职责 |
|------|------|------|
| `Tracking\ActivityTracker.cs` | 新建 | 玩家动作采集 + 里程碑检测 |
| `Tracking\FarmProfiler.cs` | 新建 | 农场实体扫描 + 流派推断 |
| `Narrative\BeatScheduler.cs` | 新建 | 活跃 beat 调度 + 时间窗口激活 |
| `Narrative\BeatExecutor.cs` | 新建 | beat 执行期 NPC 接管/释放 |
| `Narrative\BeatTypes.cs` | 新建 | Beat 数据结构 + 消息类型 |
| `Core\AgentTickLoop.cs` | 修改 | 增加 BeatScheduler 前置检查 |
| `WebSocket\WebSocketClient.cs` | 修改 | 新增 8 种消息类型处理 |
| `StateSyncSender.cs` | 修改 | 增加 activity_report / game_context_sync 推送 |

### 12.2 TS 侧（`D:\Source\ValleyAI\packages\stardew\`）

| 文件 | 操作 | 职责 |
|------|------|------|
| `src\director.ts` | 新建 | 导演 Agent（早晨计划 + 里程碑反应） |
| `src\player-profile.ts` | 新建 | 五层档案数据结构 |
| `src\game-context.ts` | 新建 | 游戏上下文快照 |
| `src\react-guard.ts` | 新建 | ReAct 软刹车控制器 |
| `src\beat-store.ts` | 新建 | Beat 持久化（SQLite） |
| `src\activity-log-store.ts` | 新建 | 活动日志持久化 |
| `src\player-profile-store.ts` | 新建 | 玩家画像持久化 |
| `src\stardew-agent.ts` | 修改 | 增加 runBeat 入口 |
| `src\protocol-adapter.ts` | 修改 | 新增消息路由 |
| `src\server.ts` | 修改 | 初始化 Director + Store |
| `src\types.ts` | 修改 | 新增 Beat/Profile/Context 类型 |
| `src\prompt-builder.ts` | 修改 | 新增导演 prompt 模板 + beat prompt 模板 |

### 12.3 测试文件

| 文件 | 操作 |
|------|------|
| `tests\director.test.ts` | 新建 |
| `tests\player-profile.test.ts` | 新建 |
| `tests\react-guard.test.ts` | 新建 |
| `tests\beat-store.test.ts` | 新建 |
| `tests\director-e2e.test.ts` | 新建（集成） |
| `ValleyTalk\src\ValleyAgent.TestMod\Tests\Experience\EXP015-019*.cs` | 新建 |
