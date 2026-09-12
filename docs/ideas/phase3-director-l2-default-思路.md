# 阶段 3 实现思路 — Director 工具集 + L2 状态层 + 默认创建

> **Created:** 2026-08-06
> **设计依据:** `docs/design/2026-08-05-three-tier-architecture-redesign.md` §4/§5/§6/§9
> **执行计划:** `docs/plan/2026-08-05-three-tier-architecture-execution-plan.md` 阶段 3
> **基线:** ValleyTalk 7981e2f；ValleyAI 8eb703d（阶段 2 已合并）

---

## 0. 现状核对结论（动手前验证）

| 计划条目 | C# 现状 | TS 现状 |
|---|---|---|
| 3.1.1 默认创建 | **MISSING**（OnSaveLoadedCore L591 只恢复手动 agent，L618 `IsManuallyOverridden` skip） | — |
| 3.1.2 删 Agent/Non-agent 区分 | 部分（WorldSnapshotBuilder 已有 NPC 侧字段） | — |
| 3.2.1 NpcConfigLoader | **MISSING**（参考 NpcEconomyProfileLoader 骨架） | — |
| 3.3.1 DirectorTools | **MISSING**（CommandExecutor switch L101 可加 9 工具） | director.ts 是旧 Task-8 设计（无工具） |
| 3.3.2 Director 工具协议 | — | **MISSING**（messages.json 无 director_command；beat_* 是 planned） |
| 3.4.1 AgentBrain L2 | **MISSING**（有 PendingGoal L43，缺 MoodTag/TodayEvents/WorkingOn/OwedMoney） | — |
| 3.4.2 worldSnapshot L2 | **MISSING**（record 有 PlayerHeldItem L63，缺 npcMood/npcRecentEvents/npcWorkingOn/npcOwedMoney） | types.ts 缺 4 L2 字段 |
| 3.4.3 prompt L2 段 | — | **MISSING**（DIALOGUE_SYSTEM_TEMPLATE 无「## 你的状态」开头段） |
| 3.5 BeatStore | **MISSING**（C# 无 Beats/） | beat-store.ts SQLite 已存在（旧设计） |
| 3.6 PlayerActionTracker | **MISSING**（ActivityTracker 是逐日累计，非 10-tick 采样） | — |
| 3.7 DirectorContextBuilder | **MISSING** | — |

---

## 1. 关键设计裁决

### 1.1 Director 工具协议：新增 `director_command` 消息类型（裁决）

**裁决**：新增 `director_command` 消息（TS→C#，active），不复用 action_result。
- action_result 是 C#→TS 反馈通道（per-NPC 路由，无 npcName 即丢弃 protocol-adapter.ts:180-183），语义不匹配
- 9 个 Director 工具需要统一 TS→C# 入口
- messages.json 已有 planned 的 beat_* 可借鉴，但那些是旧设计；director_command 是新的干净通道

**消息格式**：
```json
{ type: "director_command", tool: "set_npc_position|set_npc_inventory|...", args: {...}, requestId: "..." }
```
C# 侧 CommandExecutor 新增 `case "director_command"` 特殊路由（不走 NPC Agent switch，走 DirectorTools.Execute）。

### 1.2 旧 director.ts / BeatStore 处置：保留作为 Director 上下文来源

**裁决**：TS 端旧 `director.ts`（morningPlan 产 beat → allocate_agent）**保留不动**，它是 Director 的"叙事灵感来源"；阶段 3 新增的是**工具型 Director**（director_command 通道）。两个并存：
- 旧：morningPlan 每日 0-3 beat → allocate_agent（已有，live）
- 新：Director agent 的工具调用 → director_command → C# DirectorTools 执行

C# 端 BeatStore（3.5）是新组件，与 TS beat-store.ts 对应（TS 存导演产出，C# 存当前活跃 beat 场景描述供 L3 注入）。

### 1.3 L2 注入位置：prompt 开头「## 你的状态」段

**裁决**：DIALOGUE_SYSTEM_TEMPLATE 最前面（规则段之前）加「## 你的状态」段，强制注入：
```
## 你的状态
心情：{mood_tag}
近期事件：{recent_events}
正在做：{working_on}
欠款：{owed_money}
当前目标：{current_goal_desc}
```
**警告**：`tests/prompt-segment-order.test.ts` 断言段序单调递增——需同步更新断言（静态段「你的状态」在规则段前仍是静态→动态顺序，规则段也是静态，OK）。

### 1.4 默认创建：全部 NPC 建 AgentBrain，但按需激活

**裁决**：SaveLoaded 遍历所有 NPC 创建 AgentBrain+Inventory（文件存在），但 `AgentService` 注册表只放"可激活"的（spark/对话/Director beat 时激活）。避免绕过 MaxAgentNpcs 上限（ModConfig L53，TrySparkActivate L2396 强制）。
- 不和玩家交互的 NPC：AgentBrain 文件存在但不分配（沿用现状）
- 设计文档 §14.1 待决项 4 裁决：按需激活

### 1.5 ModConfig 命名对齐

**裁决**：新配置用子对象模式（如 `DirectorConfig`、`L2Config`），与 GoalConfig（L1124-1141，GlobalTimeoutMinutes=240）对齐。注意**不是**计划里的 "GoalTimeoutGameHours"——实际字段是 `GlobalTimeoutMinutes`（游戏分钟）。新增 todayEvents 保留天数（3）、条数上限（5）、DirectorContextBuilder token 预算（800-1500）。

---

## 2. C# 端改动（ValleyTalk，主体）

### 2.1 AgentBrain L2 字段（3.4.1）— Abstractions/Brain/AgentBrain.cs

```csharp
public string MoodTag { get; set; } = "";                    // L2 心情标签（Director set_npc_mood 写入）
public List<string> TodayEvents { get; } = new();             // L2 近期事件，保留 3 天
public string? WorkingOn { get; set; }                        // L2 工作标记
public int OwedMoney { get; set; }                            // L2 欠款
```
day_started 清理 3 天前 todayEvents（保留近 3 天）。注意 AgentBrain 在 Abstractions 项目（不能引用 ValleyAgent）。

### 2.2 WorldSnapshot L2 字段（3.4.2）

IAgentServerProvider.cs record 尾部追加（沿用 tail-default-null 兼容约定）：
```csharp
string? NpcMood = null,            // L2 心情标签
IReadOnlyList<string>? NpcRecentEvents = null,  // L2 近期事件
string? NpcWorkingOn = null,       // L2 工作标记
int? NpcOwedMoney = null           // L2 欠款
```
WorldSnapshotBuilder.Build 填充（从 agent.Brain）。

### 2.3 默认创建（3.1.1）— EventHandlerInitializer

OnSaveLoadedCore 新增路径：遍历 Game1.player.friendshipData 所有 key（或 Game1.characters 的 NPC），对每个 NPC 调 `agentService.TryGetAgent` 若无则 CreateAgent。**注意**：不强制分配（不入 AllocationManager 活跃集），仅保证 AgentBrain 文件存在。与现有手动恢复路径（L618 skip）并行，不冲突。

### 2.4 NpcConfigLoader（3.2.1）— Config/NpcConfigLoader.cs

复制 NpcEconomyProfileLoader 骨架：
- 读 `{modDir}/npc-configs/{npc_name}.json`
- 字段：name/personality/speechStyle/birthday/isRomanceable/lovedGifts/likedGifts/dislikedGifts/hatedGifts/initialMoney/initialInventory/isProtagonist/defaultMood/relationships
- sealed class + OrdinalIgnoreCase dict + Load/LoadFromFile/GetProfile + 静默 IOException/JsonException 清空
- 配置示例：npc-configs/Shane.json + 其他主要 NPC

### 2.5 DirectorTools（3.3.1）— Commands/DirectorTools.cs 或独立 Director/DirectorTools.cs

9 个工具，CommandExecutor switch L101 加 `case "director_command"` 特殊路由：
- set_npc_position(npc, location, tile) — Game1.getCharacterFromName → warp/setTileLocation（复用 EventHandlerInitializer L750-754 模式）
- set_npc_inventory(npc, add?, remove?) — agent.Inventory.TryAdd/TryRemove
- set_npc_money(npc, delta) — agent.Inventory.Money += delta
- set_npc_mood(npc, moodTag) — agent.Brain.MoodTag = moodTag
- set_npc_recent_events(npc, events) — agent.Brain.TodayEvents 替换
- set_npc_working_on(npc, workingOn?) — agent.Brain.WorkingOn
- spawn_beat(npc, sceneDesc, ...) — BeatStore 创建
- spawn_group_beat(npcs, location, sceneScript, ...) — BeatStore 创建
- inject_memory(npc, text, importance, tags) — Brain 记忆注入

**Director prompt 约束**（设计 §6.3）："只能改符合人设的状态""不能让 NPC 突然变性格"——在 TS Director prompt 加。

### 2.6 BeatStore（3.5）— Beats/BeatStore.cs

字段：npc, sceneDesc, expectedInteraction, expireTime, playerVisible。worldSnapshot 加 `currentBeat?`。
- 新建 Beats/ 目录（参考 Goals/ 结构）
- TS beat-store.ts 已有 SQLite——C# 侧存当前活跃 beat 场景描述（L3 临时剧本注入源）

### 2.7 PlayerActionTracker（3.6）— Tracking/PlayerActionTracker.cs

- 每 10 tick 采样玩家行为（位置变化 + 当前动作分类）
- 分类：mine/farm/fish/forage/social/other
- day_started 聚合为百分比 + 趋势
- **不与 ActivityTracker 混淆**（它是逐日累计 + 里程碑；这是采样分类器）

### 2.8 DirectorContextBuilder（3.7）— AI/DirectorContextBuilder.cs

- day_started 拼装 Director 上下文
- 压缩为结构化文本（800-1500 token）
- 注入 Director prompt

### 2.9 C# 单测

- AgentBrain L2 字段读写 + todayEvents 清理
- NpcConfigLoader 加载（valid/invalid/缺文件）
- DirectorTools 各工具（位置/钱包/背包/心情/事件/工作标记）
- PlayerActionTracker 行为分类

---

## 3. TS 端改动（ValleyAI）

### 3.1 types.ts / decoder L2 字段

WorldSnapshot + SceneState 加 `npcMood/npcRecentEvents/npcWorkingOn/npcOwedMoney`（optional/null）；decoder null-safe。

### 3.2 prompt-builder.ts「## 你的状态」段

DIALOGUE_SYSTEM_TEMPLATE 开头加 L2 段 + 4 个新 placeholder；buildDialogueSystemPrompt 注入；**更新 prompt-segment-order.test.ts 断言**。

### 3.3 director_command 协议

messages.json 加 `director_command`（active，TS→C#）；types.ts IncomingMessage 加 DirectorCommandMessage；protocol-adapter routeMessage 加 case；**Director agent 工具集**（TS 侧定义 9 工具 schema，LLM 输出 → 发送 director_command）。

### 3.4 TS 测试

- types/decoder L2 字段
- prompt L2 段（段序测试更新）
- director_command 协议往返

---

## 4. 验证清单

- [ ] TS: `bun test packages/stardew` 全绿（407 + 新增）
- [ ] TS: `tsc --noEmit` 0 错
- [ ] TS: `check:protocol` PASS（messages.json 改后）
- [ ] C#: 编译 0 警告
- [ ] C#: 单测覆盖 L2/Director/PlayerActionTracker/NpcConfigLoader
- [ ] 游戏内实测（待游戏环境）

## 5. 风险与注意

- **L2 段序测试**：prompt-segment-order.test.ts 必须同步更新
- **director_command 是新增消息类型**：messages.json 两端同步 + check:protocol
- **默认创建绕过 MaxAgentNpcs**：只建 AgentBrain 不分配，按需激活
- **ModConfig 命名**：GoalConfig.GlobalTimeoutMinutes（非计划里的 GoalTimeoutGameHours）
- 中文注释用 write 工具写（UTF-8）
