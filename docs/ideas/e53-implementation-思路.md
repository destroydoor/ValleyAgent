# E5-3 主动发言（时间触发）实现思路

> 分支：`feat/exec-e53`（worktree: vt-e53，B0 配额同 worktree）
> 日期：2026-08-03
> 依据：`docs/plan/2026-08-03-phase3-5-full-execution-plan.md` B2（E5-3）

## 1. 需求与现状

玩家进入游戏后，NPC 应在**时间窗口触发点**（早晨/中午/傍晚）主动开口说话，
而不是被动等玩家搭话。核心约束：

- **额度约束**：主动发言必须经 `ProactiveSpeechQuota.TryConsumeQuota`，每日上限 + 冷却 + 会话豁免
- **唤醒约束**：未醒（睡觉）的 NPC 不触发
- **已存在但无生产者的队列**：`EventHandlerInitializer._pendingPreSpeakActions`
  （`ConcurrentQueue<(npcName, text)>`）已有**消费者**（`ProcessPendingPreSpeakActions`
  每 tick 出队 → `ActiveSpeechRouter.Route` 渲染气泡/聊天栏），但**没有任何生产者**
  往队列里入队——E5-3 就是补上这个"时间触发生产者"。

## 2. 改动文件

| 文件 | 改动 |
|---|---|
| `src/ValleyAgent/Chat/ProactiveSpeechTrigger.cs` | **新增**：时间窗口触发决策（纯逻辑，可单测） |
| `src/ValleyAgent/Initialization/EventHandlerInitializer.cs` | 接线：OnTimeChanged 调 trigger；EnqueuePreSpeak 公开入队 |
| `src/ValleyAgent.UnitTests/ProactiveSpeechTriggerTests.cs` | **新增**：决策单测 |
| `docs/ideas/e53-implementation-思路.md` | **本文档** |

## 3. ProactiveSpeechTrigger 设计

### 3.1 时间窗口（Stardew 时间，int 制 600=6:00）

| 窗口 | 触发点 | 文案池主题 |
|---|---|---|
| Morning | 600 | 早上好/新的一天 |
| Noon | 1200 | 中午好/吃了吗 |
| Evening | 1800 | 傍晚好/今天怎么样 |

窗口判定：`GetWindow(int time)` → 600≤t<1200 Morning、1200≤t<1800 Noon、t≥1800 Evening。

### 3.2 决策 API

```csharp
public sealed class ProactiveSpeechTrigger
{
    public IReadOnlyList<ProactiveSpeech> Evaluate(
        int time, string gameDate, IReadOnlyList<ProactiveCandidate> candidates,
        ProactiveSpeechQuota quota, DateTime? nowUtc = null)
}
```

- `ProactiveCandidate(NpcName, DisplayName, IsAwake, Talkativeness)` — Talkativeness 来自
  NpcEconomyProfile（0~1，E3-1 已提供）
- 逐候选：未醒 → 跳过；`quota.TryConsumeQuota(npc, gameDate)` 失败 → 跳过；
  通过 → 按窗口从文案池选一条（确定性 FNV 哈希选池，非 Random）
- 返回 `ProactiveSpeech(NpcName, Text)` 列表
- **纯逻辑、无 Game1 依赖** → 可单测

### 3.3 文案池（确定性选择，防重复）

每个窗口 3 条文案，用 FNV-1a(npcName + gameDate + 窗口序号) 哈希选下标，
同一天同一窗口同一 NPC 文案稳定，跨天变化。文案为中文问候短句。

### 3.4 接线（EventHandlerInitializer）

```csharp
// OnTimeChanged（每 10 游戏分钟触发）
if (_agentService != null && _proactiveSpeechQuota != null && IsTriggerWindow(e.NewTime))
{
    var candidates = BuildProactiveCandidates();   // 从 GetAllAgents + NpcScheduleService.IsAwake + EconomyProfiles
    var speeches = _proactiveSpeechTrigger.Evaluate(e.NewTime, GetGameDateIso(...), candidates, _proactiveSpeechQuota);
    foreach (var s in speeches) EnqueuePreSpeak(s.NpcName, s.Text);
}
```

- `EnqueuePreSpeak(npcName, text)`：公开静态方法入队 `_pendingPreSpeakActions`
- `IsTriggerWindow`：只在窗口**首次**进入时触发（记录 `_lastTriggerWindow`，同窗口不重复触发）

## 4. 验证

- `dotnet build src/ValleyAgent` → 0 警告 0 错误
- `dotnet test src/ValleyAgent.UnitTests` → 全部通过（含 E5-3 新测）
- 渲染路径复用已存在的 `ProcessPendingPreSpeakActions`（游戏内实测覆盖）
