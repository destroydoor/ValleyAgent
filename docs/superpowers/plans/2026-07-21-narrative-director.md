# 叙事导演引擎实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 实现双层 LLM 叙事导演引擎——导演 Agent 生成 beat，NPC Agent ReAct 执行 beat，将每日 Token 消耗从 2880 万降到 ~2.5 万并保证玩家体验密度。

**Architecture:** 三层架构。C# Mod 负责玩家行为采集与 NPC 接管/释放（纯执行器）；@valley/stardew (TS) 负责导演决策、PlayerProfile、GameContext、ReActGuard、Beat 持久化；@valley/core 复用现有 agent-loop/ToolRegistry/CircuitBreaker。Beat 间隙 NPC 走原版日程零 LLM 调用。

**Tech Stack:** TypeScript (Bun runtime)、C# (.NET 8.0 + SMAPI)、SQLite (better-sqlite3)、WebSocket JSON 协议、Vercel AI SDK + Minimax LLM。

**Spec:** [2026-07-21-narrative-director-design.md](file:///d:/Source/ValleyTalk/docs/superpowers/specs/2026-07-21-narrative-director-design.md)

---

## 文件结构

### TS 侧（`D:\Source\ValleyAI\packages\stardew\`）

| 文件 | 操作 | 职责 |
|------|------|------|
| `src\types.ts` | 修改 | 新增 Beat/PlayerProfile/GameContext 类型 + 8 种消息类型 |
| `src\beat-store.ts` | 新建 | Beat SQLite 持久化（CRUD + 状态查询） |
| `src\activity-log-store.ts` | 新建 | 活动日志 + FarmSnapshot + Milestone 持久化 |
| `src\player-profile-store.ts` | 新建 | PlayerProfile 五层 SQLite 持久化 |
| `src\react-guard.ts` | 新建 | ReAct 软刹车控制器（7 种刹车条件） |
| `src\player-profile.ts` | 新建 | PlayerProfileManager（更新逻辑 + prompt 注入） |
| `src\game-context.ts` | 新建 | GameContextManager（prompt 摘要） |
| `src\director.ts` | 新建 | 导演 Agent（morningPlan + milestoneReact） |
| `src\stardew-agent.ts` | 修改 | 增加 runBeat 入口 |
| `src\protocol-adapter.ts` | 修改 | 新增 8 种消息路由 |
| `src\server.ts` | 修改 | 初始化 Director + Stores |
| `src\prompt-builder.ts` | 修改 | 新增 beat prompt 模板 |

### C# 侧（`D:\Source\ValleyTalk\src\ValleyAgent\`）

| 文件 | 操作 | 职责 |
|------|------|------|
| `Tracking\ActivityTypes.cs` | 新建 | DailyActivity / Milestone / PlayStyle 数据结构 |
| `Tracking\ActivityTracker.cs` | 新建 | 玩家动作采集 + 里程碑检测 |
| `Tracking\FarmProfiler.cs` | 新建 | 农场实体扫描 + 流派推断 |
| `Narrative\BeatTypes.cs` | 新建 | Beat 数据结构 + 6 种 beat 消息类型 |
| `Narrative\BeatExecutor.cs` | 新建 | beat 执行期 NPC 接管/释放 + 兜底降级 |
| `Narrative\BeatScheduler.cs` | 新建 | 活跃 beat 调度 + 时间窗口激活 |
| `StateSyncSender.cs` | 修改 | 增加 activity_report / game_context_sync 推送 |

### 测试

每个新模块必须有同名 `.test.ts`（TS）或同 namespace 下的 `Tests.cs`（C#），遵循 TDD：先写测试 → 运行确认失败 → 实现 → 运行确认通过 → 静态检查 0 错误 0 警告 → commit。

---

## Phase 1: TS 数据层基础

### Task 1: types.ts 扩展

**Files:** Modify `D:\Source\ValleyAI\packages\stardew\src\types.ts`

在文件末尾追加以下类型定义（完整代码见 spec 第 2-5 章，所有字段名严格对齐）：

- `BeatStatus` = `"scheduled" | "active" | "completed" | "skipped" | "failed"`
- `Beat` interface（含 id/npcName/triggerTime/windowEnd/directive/context/status/reactSteps）
- `ReActStep` interface
- `PlayStyleTag` = `"brewer" | "farmer" | "rancher" | "miner" | "warrior" | "forager" | "socializer"`
- `PlayStyle`、`ActivityRank`、`LocationRank`、`DailyActivity`、`Interaction`、`GiftRecord`、`BeatHistoryEntry`
- `PlayerProfile` 五层（static/behavior/preferences/relationships/personality/story）
- `NpcStateSnapshot`、`InventorySlot`、`GameContext`
- 8 种消息类型：`ActivityReportMessage`、`ActivityMilestoneMessage`、`GameContextSyncMessage`、`BeatDirectiveMessage`、`BeatActivateMessage`、`BeatEventMessage`、`BeatStateMessage`、`PlayerStateUpdateMessage`

- [ ] **Step 1:** 新建 `tests/types-extended.test.ts`，覆盖 9 项类型编译验证（每个核心 interface 至少 1 个对象字面量构造）
- [ ] **Step 2:** 运行 `bun test tests/types-extended.test.ts` 确认失败（类型未导出）
- [ ] **Step 3:** 修改 `src/types.ts` 追加类型定义
- [ ] **Step 4:** 运行测试确认通过
- [ ] **Step 5:** `bunx tsc --noEmit` 0 errors
- [ ] **Step 6:** Commit `feat(stardew): add Beat/PlayerProfile/GameContext type definitions`

### Task 2: BeatStore SQLite 持久化

**Files:** Create `src/beat-store.ts` + Test `tests/beat-store.test.ts`

依赖：`bun add better-sqlite3 && bun add -d @types/better-sqlite3`

**接口**：
```typescript
class BeatStore {
  constructor(dbPath: string)
  init(): void
  save(beat: Beat): void                    // upsert
  getById(id: string): Beat | null
  updateStatus(id: string, status: BeatStatus): void
  listScheduled(): Beat[]
  listActive(): Beat[]
  listRecent(limit: number): Beat[]
  listByNpc(npcName: string): Beat[]
  countActiveByNpc(npcName: string): number
  countActiveTotal(): number
  delete(id: string): void
  close(): void
}
```

SQLite schema：
```sql
CREATE TABLE beats (
  id TEXT PRIMARY KEY,
  npc_name TEXT NOT NULL,
  trigger_time TEXT NOT NULL,
  window_end TEXT NOT NULL,
  directive TEXT NOT NULL,
  context_json TEXT NOT NULL,
  status TEXT NOT NULL DEFAULT 'scheduled',
  react_steps_json TEXT,
  created_at TEXT NOT NULL DEFAULT (datetime('now'))
);
CREATE INDEX idx_beats_status ON beats(status);
CREATE INDEX idx_beats_npc ON beats(npc_name);
```

- [ ] **Step 1:** 新建 `tests/beat-store.test.ts`，覆盖 11 项：save+load、updateStatus、listScheduled/Active、listRecent、listByNpc、countActiveByNpc/Total、delete、upsert 幂等、context JSON roundtrip
- [ ] **Step 2-6:** TDD 标准流程

### Task 3: ActivityLogStore SQLite 持久化

**Files:** Create `src/activity-log-store.ts` + Test `tests/activity-log-store.test.ts`

**接口**：
```typescript
class ActivityLogStore {
  saveDaily(activity: DailyActivity): void  // 同日期覆盖
  getDaily(date: string): DailyActivity | null
  listRecent(days: number): DailyActivity[]
  saveFarmSnapshot(date: string, playStyles: PlayStyle[]): void
  getLatestFarmSnapshot(): { date: string; playStyles: PlayStyle[] } | null
  getMedian(field: NumericField, days: number): number
  getSum(field: NumericField, days: number): number
  markMilestoneFired(type: string, date: string): void
  hasMilestoneFired(type: string, date: string): boolean
  hasMilestoneFiredWithin(type: string, days: number): boolean
}
```

`NumericField` = `"fishingMinutes" | "farmingMinutes" | "miningMinutes" | "foragingMinutes" | "socialMinutes" | "combatMinutes" | "fishCaught" | "cropsHarvested" | "itemsShipped" | "monstersKilled"`

三张表：`daily_activities`、`farm_snapshots`、`milestone_fired`，均使用 `ON CONFLICT(date) DO UPDATE` 实现幂等。

- [ ] **Step 1:** 新建测试覆盖 12 项
- [ ] **Step 2-6:** TDD 标准流程

### Task 4: PlayerProfileStore SQLite 持久化

**Files:** Create `src/player-profile-store.ts` + Test `tests/player-profile-store.test.ts`

**接口**：
```typescript
class PlayerProfileStore {
  save(profile: PlayerProfile): void           // 单行表 id=1
  load(): PlayerProfile | null
  updateStatic(staticLayer): void
  appendDailyActivity(activity: DailyActivity): void  // 同日期覆盖，保留最近 30 天
  updatePreferences(prefs): void
  updatePersonality(personality): void
  updateRelationship(npcName, rel): void
  appendBeatHistory(entry: BeatHistoryEntry): void    // 保留最近 50 条
  addRecurringTrope(trope: string): void              // 去重
}
```

单行表设计：`CREATE TABLE player_profile (id INTEGER PRIMARY KEY CHECK (id = 1), data_json TEXT NOT NULL, updated_at TEXT NOT NULL)`，整个 profile 序列化为 JSON 存储。

- [ ] **Step 1:** 新建测试覆盖 11 项
- [ ] **Step 2-6:** TDD 标准流程

---

## Phase 2: TS 导演决策层

### Task 5: ReActGuard 软刹车控制器

**Files:** Create `src/react-guard.ts` + Test `tests/react-guard.test.ts`

**接口**：
```typescript
interface GuardConfig {
  maxRepeatedToolCalls: number;   // 默认 3
  maxNoProgressSteps: number;     // 默认 5
  maxTokensPerBeat: number;       // 默认 8000
  maxLlmFailures: number;         // 默认 3
  maxToolFailures: number;        // 默认 3
  playerLeftThresholdMs: number;  // 默认 120_000
}

interface GuardContext {
  beat: Beat;
  accumulatedTokens: number;
  consecutiveLlmFailures: number;
  consecutiveToolFailures: number;
  npcTile: { x: number; y: number };
  playerTile: { x: number; y: number };
  playerLeftAt: number | null;          // null = 在场
  recentSteps: ReActStepRecord[];
}

interface GuardTrigger {
  reason: "repeated_tool_calls" | "no_progress" | "token_budget_exceeded"
        | "llm_failures" | "tool_failures" | "player_left" | "window_expired";
  detail: string;
}

class ReActGuard {
  constructor(config?: Partial<GuardConfig>)
  check(ctx: GuardContext): GuardTrigger | null
  reset(): void
}
```

刹车顺序（任一触发即返回 trigger）：
1. `player_left`（elapsed > playerLeftThresholdMs）
2. `token_budget_exceeded`（accumulatedTokens > maxTokensPerBeat）
3. `llm_failures`（consecutiveLlmFailures >= maxLlmFailures）
4. `tool_failures`（consecutiveToolFailures >= maxToolFailures）
5. `repeated_tool_calls`（最近 N 步 tool+argsHash 完全相同）
6. `no_progress`（最近 N 步 npcTile 完全相同）

- [ ] **Step 1:** 新建测试覆盖 11 项（每条刹车条件 + 不误触场景 + reset）
- [ ] **Step 2-6:** TDD 标准流程

### Task 6: PlayerProfileManager

**Files:** Create `src/player-profile.ts` + Test `tests/player-profile.test.ts`

**接口**：
```typescript
class PlayerProfileManager {
  constructor(
    public readonly profileStore: PlayerProfileStore,
    private readonly activityStore: ActivityLogStore,
    private readonly config: { callLlm: (prompt: string) => Promise<{text, usage}> }
  )
  initProfile(staticLayer): void                       // 创建空画像
  recordDailyActivity(activity: DailyActivity): void   // 写入 activityStore + profileStore
  updateRelationship(npcName, rel): void
  appendInteraction(npcName, interaction): void         // 保留最近 5 次
  appendGiftHistory(npcName, gift): void                // 保留最近 10 次
  appendBeatHistory(entry): void
  addRecurringTrope(trope): void
  inferPlayStylesFromActivityLog(): PlayStyle[]         // 从 activityStore 读最新 FarmSnapshot
  summarizeForDirector(): string                        // 紧凑文本注入导演 prompt
  async refreshPreferences(): Promise<void>             // LLM 推断 routinePattern
  async refreshPersonality(): Promise<void>             // LLM 推断 archetype
}
```

`summarizeForDirector` 输出格式（参考 spec 第 4.1 节示例）：
```
姓名: Alice, 农场: Riverland (Riverland)
流派: brewer(80%), farmer(60%)
性格: 内向, 原型: 独行者
叙事角色: 不情愿的农场主
最近活动: Year 2 Summer 14: 钓鱼90m/种地60m/挖矿0m
关系状态: Willy(friend,850pt)
```

- [ ] **Step 1:** 新建测试覆盖 8 项
- [ ] **Step 2-6:** TDD 标准流程

### Task 7: GameContextManager

**Files:** Create `src/game-context.ts` + Test `tests/game-context.test.ts`

**接口**：
```typescript
class GameContextManager {
  update(ctx: GameContext): void
  getCurrent(): GameContext | null
  isNpcAvailable(npcName: string): boolean
  summarizeForDirector(): string      // 含时间/进度/季节资源/玩家状态
  summarizeForNpc(npcName: string): string
}
```

输出格式参考 spec 第 5.3 节示例。中文映射常量：
```typescript
const DAY_OF_WEEK_MAP = { "Monday": "周一", "Tuesday": "周二", ... };
const WEATHER_MAP = { "sunny": "晴天", "rainy": "雨天", "snowy": "雪天", "stormy": "暴风雨" };
const SEASON_MAP = { "spring": "春", "summer": "夏", "fall": "秋", "winter": "冬" };
```

- [ ] **Step 1:** 新建测试覆盖 9 项
- [ ] **Step 2-6:** TDD 标准流程

### Task 8: Director 早晨计划 + 里程碑反应

**Files:** Create `src/director.ts` + Test `tests/director.test.ts`

**接口**：
```typescript
interface DirectorConfig {
  callLlm: (prompt: string) => Promise<{ text: string; usage: { promptTokens: number; completionTokens: number } }>;
  maxBeatsPerDay?: number;            // 默认 3
  maxRecentBeatsForBudget?: number;   // 默认 14
  npcCooldownDays?: number;            // 默认 5
}

class Director {
  constructor(beatStore, profileMgr, gameCtxMgr, activityStore, config)
  async morningPlan(): Promise<Beat[]>
  async milestoneReact(milestone: { type, description, detectedAt }): Promise<Beat | null>
}
```

**morningPlan 流程**（spec 6.1）：
1. 加载 GameContext，若 `isFestivalDay=true` 直接返回 `[]`
2. 加载 profileSummary + gameCtxSummary + 最近 14 天 beats
3. 构建 prompt（参考 spec 6.2 模板）
4. 调用 LLM，解析 JSON 数组（容错：尝试提取 `[...]` 子串）
5. 校验每个 beat（spec 6.3）：
   - 字段完整（npcName/triggerTime/windowEnd/directive/reasonGenerated）
   - 时间格式 `HH:MM` 合法
   - `windowEnd > triggerTime`
   - NPC 在 GameContext.npcStates 中且 isAvailable=true
   - 同 NPC 当天无重复
   - 该 NPC 在最近 14 天 beats 中无记录
6. 截断到 `maxBeatsPerDay`（默认 3）
7. 写入 BeatStore，返回 beat 列表

**milestoneReact 流程**（spec 6.4）：同上但最多 1 个 beat。

**降级**：LLM 异常 → 返回空数组/`null`，记日志，不重试。

- [ ] **Step 1:** 新建测试覆盖 12 项（含节日跳过、节奏预算耗尽、NPC 冷却、校验失败、LLM 失败、空数组、超 3 截断、多 NPC、里程碑触发、里程碑跳过）
- [ ] **Step 2-6:** TDD 标准流程

### Task 9: StardewAgent.runBeat 入口

**Files:** Modify `src/stardew-agent.ts` + Test `tests/stardew-agent-beat.test.ts`

在 class StardewAgent 末尾追加 `runBeat` 方法（不修改现有 runDialogue）：

```typescript
async runBeat(beat: Beat, gameCtx: GameContext, playerProfile: PlayerProfile): Promise<DialogueResult> {
  // 1. 构建 systemPrompt（委托 PromptBuilder.buildBeatSystemPrompt，缺省回退到 directive）
  // 2. 构造 SceneState（从 gameCtx 映射）
  // 3. 构建 ToolContext（复用 buildStardewTools）
  // 4. AgentContext.messages = [{ role: "user", content: `导演指令：${beat.directive}` }]
  // 5. AgentLoopConfig.maxTurns = 8（软上限，实际由 ReActGuard 控制）
  // 6. shouldStopAfterTurn: hasSpeak && hasOther（至少调一个 speak + 一个其他工具）
  // 7. runOnce + 返回 DialogueResult
}
```

imports 顶部追加：`import type { Beat, GameContext, PlayerProfile } from "./types";`

- [ ] **Step 1:** 新建测试覆盖 1 项（mock LLM 返回 speak 工具，验证返回 DialogueResult）
- [ ] **Step 2-6:** TDD 标准流程 + 全量回归测试

---

## Phase 3: C# 采集层

### Task 10: ActivityTypes.cs 数据结构

**Files:** Create `D:\Source\ValleyTalk\src\ValleyAgent\Tracking\ActivityTypes.cs`

定义 C# 镜像类型（与 TS types.ts 字段一一对应）：
- `class DailyActivity`（含 Date/FishingMinutes/.../GiftsGiven）
- `class GiftRecord`（To/ItemId）
- `enum PlayStyleTag { Brewer, Farmer, Rancher, Miner, Warrior, Forager, Socializer }`
- `class PlayStyle`（Tag/Confidence/Evidence）
- `enum MilestoneType { FishingStreak, MiningStreak, FarmingStreak, SameGiftRepeated, DialogueCount, MonsterKills, NewArea, FirstBundle }`
- `class Milestone`（Type/Description/DetectedAt）

- [ ] **Step 1:** 创建文件
- [ ] **Step 2:** `dotnet build` 0 errors 0 warnings
- [ ] **Step 3:** Commit

### Task 11: ActivityTracker 玩家行为采集

**Files:** Create `Tracking\ActivityTracker.cs` + Test `ValleyAgent.TestMod\Tests\Tracking\ActivityTrackerTests.cs`

**接口**：
```csharp
public class ActivityTracker
{
    void SetDate(string date);
    void RecordFishing(int caught, int minutes);
    void RecordFarming(int harvested, int minutes);
    void RecordMining(int minutes, int levelsDescended = 0);
    void RecordForaging(int items, int minutes);
    void RecordCombat(int killed, int minutes);
    void RecordGift(string toNpc, string itemId);
    void RecordDialogue(string npcName);              // 同 NPC 同天不重复加入 NpcsTalkedTo
    void RecordLocationVisit(string location);        // 同地点不重复
    DailyActivity GetCurrentDaily();
    DailyActivity FinalizeDay();                       // 返回当日数据并重置 _current
    List<Milestone> CheckMilestones();
}
```

里程碑阈值（spec 6.6）：
- FishingStreak：连续 ≥3 天且每天 FishingMinutes ≥ 30
- MiningStreak：连续 ≥3 天且每天 MiningMinutes ≥ 60
- FarmingStreak：连续 ≥3 天且每天 FarmingMinutes ≥ 30
- SameGiftRepeated：同 NPC 同 item 累计 ≥ 3 次
- DialogueCount：同 NPC 累计 ≥ 10 次

线程安全：所有公开方法用 `lock (_lock)` 保护 `_current` 和 `_history`。

- [ ] **Step 1:** 新建测试覆盖 10 项（各 Record 方法 + FinalizeDay + 3 个里程碑场景）
- [ ] **Step 2-6:** TDD 标准流程

### Task 12: FarmProfiler 农场流派扫描

**Files:** Create `Tracking\FarmProfiler.cs` + Test `ValleyAgent.TestMod\Tests\Tracking\FarmProfilerTests.cs`

**接口**：
```csharp
public class FarmProfiler
{
    public List<PlayStyle> InferPlayStyles(
        int kegCount, int cropTiles, int animalCount,
        int recentMiningMinutes, int recentMonstersKilled,
        int recentForagedItems, int recentDialoguesHad
    );
}
```

阈值表（spec 4.3）：

| Tag | 信号 | 阈值 |
|-----|------|------|
| Brewer | kegCount | ≥ 8 |
| Farmer | cropTiles | ≥ 100 |
| Rancher | animalCount | ≥ 4 |
| Miner | recentMiningMinutes | ≥ 60 |
| Warrior | recentMonstersKilled | ≥ 30 |
| Forager | recentForagedItems | ≥ 20 |
| Socializer | recentDialoguesHad | ≥ 10 |

confidence = 信号强度（按阈值比例） / 全部信号总和（归一化）。evidence 字段描述依据，如 `"检测到 12 个酒桶"`。

- [ ] **Step 1:** 新建测试覆盖 8 项（无信号 + 7 种流派各自触发）
- [ ] **Step 2-6:** TDD 标准流程

### Task 13: BeatTypes.cs + BeatExecutor + BeatScheduler

**Files:**
- Create `Narrative\BeatTypes.cs`（C# 镜像 Beat + 6 种 beat 消息类型）
- Create `Narrative\BeatExecutor.cs`（NPC 接管/释放/兜底）
- Create `Narrative\BeatScheduler.cs`（时间窗口激活）
- Test `ValleyAgent.TestMod\Tests\Narrative\BeatSchedulerTests.cs`

**BeatScheduler 接口**：
```csharp
public class BeatScheduler
{
    void RegisterBeat(Beat beat);              // 从 beat_directive 消息接收
    void Tick(GameTime gameTime);              // 每 tick 检查触发
    void CancelBeat(string beatId);
    IReadOnlyList<Beat> GetScheduledBeats();
    IReadOnlyList<Beat> GetActiveBeats();
}
```

**Tick 逻辑**：
1. 遍历 scheduled beats，检查 `CurrentTime >= triggerTime`
2. 前置条件：NPC 不在对话中、不在 FIGHT、玩家在线
3. 通过检查后：BeatExecutor.TakeOver(npc) + 调 TS `beat_activate`
4. 全服 ≤ 3 个 active，单 NPC ≤ 1 个 active

**BeatExecutor 接口**：
```csharp
public class BeatExecutor
{
    void TakeOver(NPC npc);                    // 保存原版 schedule，设 followSchedule=false
    void Release(NPC npc);                     // 恢复原版 schedule
    void ExecuteFallback(NPC npc, string reason);  // 兜底对白池 + emote + Release
}
```

**兜底对白池**（spec 7.4）：
```csharp
private static readonly string[] FallbackLines = {
    "（今天有点走神了……）",
    "（我好像忘了要做什么。）",
    "（嗯……算了，回头再说吧。）",
    "（突然想不起刚才在想什么了。）",
};
```

- [ ] **Step 1:** 新建测试覆盖 BeatScheduler 时间窗口激活 + 并发上限 + 前置条件 + Cancel
- [ ] **Step 2-6:** TDD 标准流程

---

## Phase 4: 协议层集成

### Task 14: TS protocol-adapter 新增消息路由

**Files:** Modify `src/protocol-adapter.ts`

在 `routeMessage` switch 新增 8 个 case：
- `activity_report` → `playerProfileMgr.recordDailyActivity(msg.dailyActivity)` + `activityStore.saveFarmSnapshot`
- `activity_milestone` → `director.milestoneReact(msg.milestone)` → 生成 beat 后通过 `ws.send(beat_directive)` 下发
- `game_context_sync` → `gameCtxMgr.update(msg.context)` → 触发 `director.morningPlan()` → 批量下发 beat_directive
- `beat_activate` → `agent.runBeat(beat, gameCtx, profile)` → 通过 `beat_state` 消息回传工具调用
- `beat_event` → 处理工具结果回注
- `beat_directive` → 内部路由（用于回环测试）
- `beat_state` → 处理 C# 状态更新
- `player_state_update` → `gameCtxMgr.update({ ...current, playerState: msg.playerState })`

每个 case 失败时返回 `{ type: "ack", requestId, error: "..." }` 不抛异常。

- [ ] **Step 1:** 新建测试 `tests/protocol-adapter-extended.test.ts` 覆盖 8 种消息路由
- [ ] **Step 2-6:** TDD 标准流程

### Task 15: TS server.ts 初始化 Director + Stores

**Files:** Modify `src/server.ts`

在 `startServer` 中：
1. 初始化 BeatStore/ActivityLogStore/PlayerProfileStore（共享 SQLite 路径）
2. 初始化 PlayerProfileManager（注入 LLM 调用函数）
3. 初始化 GameContextManager
4. 初始化 Director（注入 LLM 调用函数）
5. 将 Director/Stores/GameCtxMgr 通过 ProtocolAdapter 构造函数传入
6. 注册 LLM 调用函数桥接 VercelAIProvider：`callLlm = async (prompt) => provider.chat(prompt)`

- [ ] **Step 1:** 新建测试 `tests/server-integration.test.ts` 覆盖启动+消息路由
- [ ] **Step 2-6:** TDD 标准流程

### Task 16: C# WebSocketClient + StateSyncSender 协议扩展

**Files:**
- Modify `src\ValleyAgent.Abstractions\WebSocket\WebSocketClient.cs`
- Modify `src\ValleyAgent\StateSyncSender.cs`

**WebSocketClient 新增方法**：
```csharp
Task SendActivityReportAsync(DailyActivity activity, List<PlayStyle> farmSnapshot, CancellationToken ct);
Task SendMilestoneAsync(Milestone milestone, CancellationToken ct);
Task SendGameContextSyncAsync(GameContextSnapshot snapshot, CancellationToken ct);
Task SendBeatEventAsync(string beatId, string eventType, object payload, CancellationToken ct);
Task SendBeatActivateAsync(string beatId, CancellationToken ct);
```

所有方法用 `SendMessageAsync(jsonMessage, ct)` 统一发送，类型字段设为对应消息类型。

**StateSyncSender 新增**：
- 监听 `DayStarted` 事件（SMAPI API）→ 6am 触发 `FinalizeDay()` → 发送 `activity_report` + `game_context_sync`
- 监听 `Update` 事件，每 60 tick 发送 `player_state_update`（玩家位置/生命/能量/钱/背包）
- 监听 `WebSocketClient.OnMessageReceived` 事件，过滤 `type=beat_directive` 转发给 `BeatScheduler.RegisterBeat`

- [ ] **Step 1:** 新建测试覆盖各 Send 方法序列化正确 + StateSyncSender 钩子触发
- [ ] **Step 2-6:** TDD 标准流程

---

## Phase 5: 体验测试

### Task 17-21: EXP015-EXP019

**Files:** Create 5 个 `ValleyAgent.TestMod\Tests\Experience\EXP0XX_*.cs`

| EXP | 场景 | 验证点 |
|-----|------|--------|
| 015 | 玩家钓鱼 3 天后 Willy 主动来找 | Director + 里程碑触发 + beat 下发 + NPC 接管 |
| 016 | NPC beat 内 ReAct 行为可见且合理 | move_to + speak 工具调用链 + 玩家可见对白 |
| 017 | ReActGuard 触发后兜底对白显示 | 重复工具 → 降级对白 + emote + NPC 释放 |
| 018 | beat 结束后 NPC 回原版日程 | followSchedule=true 恢复 + AgentTickLoop 正常运转 |
| 019 | FarmProfiler 流派判定正确 | 酿酒/种田/矿工 三种场景的扫描结果准确 |

每个 EXP 包含：
- 测试前置：模拟玩家活动数据 / 农场实体
- 测试执行：触发事件
- 断言：beat 状态机正确流转 / 工具调用链完整 / NPC 行为符合预期
- 录像：通过 ffmpeg 录制 30-60 秒片段

- [ ] **Step 1-5:** 每个 EXP 一个 commit

---

## 自检

### Spec 覆盖

| Spec 章节 | 实现任务 |
|----------|---------|
| 1. 核心决策 | 全部任务体现 |
| 2. 三层架构 | Task 1-9（TS）、Task 10-13（C#）、Task 14-16（协议） |
| 3. Beat 生命周期 | Task 2（Store）+ Task 13（Scheduler/Executor）+ Task 9（runBeat）+ Task 14（beat_activate 路由） |
| 4. PlayerProfile 五层 | Task 4（Store）+ Task 6（Manager） |
| 5. GameContext | Task 7 |
| 6. Director 决策 | Task 8 |
| 7. ReActGuard | Task 5 |
| 8. 错误处理降级 | Task 5（Guard）+ Task 8（LLM 失败）+ Task 13（C# 兜底）+ Task 14（路由失败 ack） |
| 9. 测试策略 | 每任务单元测试 + Task 17-21 体验测试 |
| 10. Token 预算 | Task 5（maxTokensPerBeat）+ Task 8（maxBeatsPerDay） |
| 11. 实施范围 | 显式排除项（联机隔离/性格层 prompt 精细化/多导演协作）未触及 |

### 类型一致性

- `Beat["status"]` 在 Task 1 定义，Task 2/5/8 使用一致
- `PlayerProfile` 五层在 Task 1 定义，Task 4/6 使用一致
- `GameContext` 在 Task 1 定义，Task 7/8 使用一致
- `PlayStyleTag` 在 Task 1（TS）和 Task 10（C#）镜像一致

### 已知限制

- 完整 TDD 代码示例未在 plan 中展开，执行 agent 必须按 spec + 接口签名自行编写
- 联机模式 Director 隔离留作后续迭代
- 性格层 LLM prompt 精细化留作后续迭代

---

## Execution Handoff

**Plan complete and saved to `docs/superpowers/plans/2026-07-21-narrative-director.md`. Two execution options:**

**1. Subagent-Driven (recommended)** - 每个 Task 派发独立 subagent 执行，两阶段 review，快速迭代

**2. Inline Execution** - 在当前 session 按 executing-plans skill 批量执行，带检查点

**Which approach?**
