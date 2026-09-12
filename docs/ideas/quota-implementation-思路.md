# E5-3 主动发言额度服务（ProactiveSpeechQuota）实现思路

> 日期：2026-08-03
> 分支：feat/exec-quota（基于 feat/integration-phase3-5）
> 服务方：E5-2（晨间喊话）与 E5-3（主动搭话）共享的 C# 侧额度服务 B0

---

## 1. 问题背景

E5-2/E5-3 让 NPC **主动**对玩家发言（喊话 / 靠近搭话）。如果没有额度控制，
LLM 决策层可能在一天内反复触发主动发言，导致刷屏、Token 浪费、体验失真
（"NPC 不是复读机"）。

`ChatSessionRegistry`（E2-2 会话注册表）已经实现了一套**每日主动计数**钩子：

- `RecordNpcSpeech(npcName, isProactiveSpeech, gameDate, nowUtc)` —— 记录发言，
  会话内豁免、被动回应不计数；
- `GetDailyProactiveCount(npcName, gameDate)` —— 查询当日主动次数；
- `ClearDailyCounts()` —— 清空计数（日切）。

**但这些方法从未被调用**（dead hooks）。本任务要把它们接进一个真正被使用的
服务：`ProactiveSpeechQuota`。

## 2. 设计目标

1. **单一数据源**：每日主动计数仍然只存在 `ChatSessionRegistry` 里，
   `ProactiveSpeechQuota` 只做"包装 + 增强"，不复制状态（避免两份计数器漂移）。
2. **三层判定**（TryConsumeQuota 按序短路）：
   - 会话豁免：NPC 正在活跃会话中 → 不消耗额度，直接放行（对话回合 ≠ 主动发言）；
   - 冷却判定：距上次主动发言不足 `CooldownMinutes` → 拒绝；
   - 每日上限：当日已用次数 ≥ `DailyLimit` → 拒绝。
3. **被动回应永不消耗额度**：玩家搭话 / 喊话的回应走 `RecordPassiveResponse`，
   只留痕不计额（复用注册表的 `isProactiveSpeech: false` 早退路径）。
4. **可测试**：纯逻辑类，无 Game1 依赖，构造时注入 `ChatSessionRegistry`
   （默认用 `ChatSessionRegistry.Instance`），时间全部走 `nowUtc` 参数。

## 3. 为什么不把计数搬进新类

`ChatSessionRegistry` 已经是：

- 会话状态的唯一持有者（`IsInActiveSession` 做豁免判定要读它的会话表）；
- 计数 key 已经是 `"{gameDate}|{npcName}"`，天然按日隔离。

若新开一份计数器：

- 豁免判定与计数判定要跨两个类做，状态一致性难保证；
- 日切清空要清两处，容易漏；
- E2-2 测试已经覆盖了注册表的计数语义，重复实现 = 重复测试 = 重复维护。

**结论**：`ProactiveSpeechQuota` 包装 `ChatSessionRegistry`，把"计数"留在原地，
把"限制 + 冷却 + 豁免"的**决策逻辑**提到新类。这是组合优于重复。

## 4. 类设计（ProactiveSpeechQuota）

```csharp
public sealed class ProactiveSpeechQuota
{
    // 每日上限（来自 ModConfig.ProactiveSpeechDailyLimit，默认 2）
    public int DailyLimit { get; set; } = 2;
    // 冷却分钟（来自 ModConfig.ProactiveSpeechCooldownMinutes，默认 30）
    public int CooldownMinutes { get; set; } = 30;

    // 包装的注册表（唯一计数源）
    private readonly ChatSessionRegistry _registry;
    // 每 NPC 最近一次主动发言时间（UTC），冷却判定簿记源
    private readonly Dictionary<string, DateTime> _lastProactiveUtc;
    private readonly object _lock;

    public ProactiveSpeechQuota(ChatSessionRegistry? registry = null); // 默认 Instance

    public bool TryConsumeQuota(string npcName, string gameDate, DateTime? nowUtc = null);
    public int  GetRemainingQuota(string npcName, string gameDate);
    public void RecordPassiveResponse(string npcName, DateTime? nowUtc = null);
    public void ResetDaily(string gameDate);
    public bool IsSessionExempt(string npcName, DateTime? nowUtc = null);
}
```

### 4.1 TryConsumeQuota 判定顺序（短路）

```
1) 空 NPC 名 → false（防御）
2) IsSessionExempt(npc) → true（会话内豁免，不消耗、不刷新冷却）
3) 冷却未过（now - lastProactive < CooldownMinutes）→ false
4) GetRemainingQuota <= 0 → false（已达每日上限）
5) 通过：registry.RecordNpcSpeech(npc, true, gameDate, now)
          lastProactiveUtc[npc] = now   → true
```

注意第 2 步**先于**冷却判定：会话内来回是对话，不应把"会话豁免的发言"当作
主动发言去刷新冷却——否则玩家多聊几句，NPC 一整天都不能再主动搭话。

### 4.2 与注册表的协作

| 本类方法 | 委托到注册表 | 本类新增逻辑 |
|---|---|---|
| `TryConsumeQuota` | `RecordNpcSpeech(npc, true, gameDate)` | 上限 + 冷却 + 豁免预检 |
| `GetRemainingQuota` | `GetDailyProactiveCount(npc, gameDate)` | `max(0, DailyLimit - used)` |
| `RecordPassiveResponse` | `RecordNpcSpeech(npc, false, "")` | 无（被动早退路径天然不计数） |
| `ResetDaily` | `ClearDailyCounts()` | 同时清空冷却表 |
| `IsSessionExempt` | `IsInActiveSession(npc)` | 无 |

`ResetDaily` 传 `gameDate` 参数是为了语义明确（日切时调用方把当天日期传进来），
实现上委托 `ClearDailyCounts()` 全清——游戏同一时刻只有"当前这一天"活跃，
全清等价于只清当日，且能兜住跨天残留 key。

### 4.3 被动回应为何仍走注册表

`RecordPassiveResponse` 在注册表里走 `isProactiveSpeech: false` 的早退路径，
实际不写任何状态。保留这个调用点是为了维持"所有 NPC 发言都经注册表留痕"
的架构不变量：E5-2 喊话回应、E5-3 被动接话未来若要统计回应频率，只需扩展
注册表一处，无需改调用方。

## 5. 配置接线（ModConfig）

在 E2-2 聊天栏配置区附近新增：

```csharp
// ─── E5-3 主动发言额度（ProactiveSpeechQuota）──────────────
/// <summary>每日主动发言额度上限：NPC 主动喊话/搭话每天最多 N 次。</summary>
[DefaultValue(2)]
public int ProactiveSpeechDailyLimit { get; set; } = 2;

/// <summary>主动发言冷却（分钟）：同 NPC 两次主动发言至少间隔该时长。</summary>
[DefaultValue(30)]
public int ProactiveSpeechCooldownMinutes { get; set; } = 30;
```

`Validate()` 钳制：

- `ProactiveSpeechDailyLimit` ∈ [1, 20]（0 或负数无意义，上限防手滑）；
- `ProactiveSpeechCooldownMinutes` ∈ [0, 240]（0 = 关闭冷却，上限 4 小时）。

## 6. 服务接线

### 6.1 ServiceInitializer（注册单例）

```csharp
var proactiveSpeechQuota = new ProactiveSpeechQuota(ChatSessionRegistry.Instance)
{
    DailyLimit = config.ProactiveSpeechDailyLimit,
    CooldownMinutes = config.ProactiveSpeechCooldownMinutes,
};
_container.RegisterSingleton(proactiveSpeechQuota);
```

E5-2/E5-3 的调用方（后续任务）从容器解析或在构造时注入本服务。

### 6.2 EventHandlerInitializer.OnDayStarted（日切重置）

在 `OnDayStarted` 里、日志与缓存清理附近加入：

```csharp
// E5-3: 新的一天重置主动发言额度（计数 + 冷却）
_proactiveSpeechQuota?.ResetDaily(GetGameDateIso(Game1.year, Game1.currentSeason, Game1.dayOfMonth));
```

`_proactiveSpeechQuota` 字段在 `Initialize()` 中
`_services.GetService<ProactiveSpeechQuota>()` 解析（与 `_config` 等一致）。

## 7. 测试计划（ProactiveSpeechQuotaTests）

| 用例 | 断言 |
|---|---|
| 额度内消费 | TryConsumeQuota → true，剩余 = DailyLimit - 1 |
| 超上限拒绝 | 消费 DailyLimit 次后再次 → false，剩余 0 |
| 冷却期内拒绝 | 消费后 5 分钟（冷却 30）再试 → false |
| 冷却过后放行 | 消费后 31 分钟（冷却 30）再试 → true |
| 会话内豁免 | 活跃会话中 TryConsumeQuota → true，计数 0，剩余不变 |
| 豁免不刷新冷却 | 会话中发言后退出会话，立刻再试 → true（未被冷却卡住） |
| 被动回应不消费 | RecordPassiveResponse 后剩余 = DailyLimit，计数 0 |
| 日切重置 | ResetDaily 后剩余回满、冷却清空（可立刻再消费） |
| 空名字防御 | 空 NPC 名 TryConsumeQuota → false |

所有测试用注入的 `ChatSessionRegistry` 实例 + 显式 `nowUtc`，不碰静态单例，
互不污染。

## 8. 验收标准

1. 新文件 `src/ValleyAgent/Chat/ProactiveSpeechQuota.cs`，纯逻辑可单测；
2. `ChatSessionRegistry` 只做最小改动（无——本设计纯包装，零改动）；
3. `ModConfig` 两个新字段 + Validate 钳制；
4. `ServiceInitializer` 注册单例；`EventHandlerInitializer.OnDayStarted` 调 ResetDaily；
5. 单测全绿，构建 0 warning 0 error；
6. 提交信息：`feat(e5): shared ProactiveSpeechQuota service`。
