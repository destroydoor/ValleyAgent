# P1 场景切换修复 + 联机止血 + npc_prompts 验证 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 消除 NPC 跨图瞬移到玩家脚下的出戏感（D1）、防止 NPC 闯入节日/事件地图（D2）、验证并补完 TS 服务器人格注入（B2 收尾）、联机模式下 farmhand 完全惰性化防止烧 token 与状态冲突（E2）。

**Architecture:** 全部改动在现有架构内进行，不依赖 P3 重构。C# 侧改 `AgentNavigator`（跨图旅行）与 `EventHandlerInitializer`（事件入口）；TS 侧（`D:\Source\ValleyAI`）仅补 PromptBuilder 对话历史注入；联机止血利用已有但未接线的 `MultiplayerHelper`。

**Tech Stack:** C# (.NET 6 + SMAPI 1.6)、TypeScript (Bun)、TestMod（游戏内 E2E）、bun test（TS 单元/集成）

**前置事实（2026-07-18 探查确认）：**
- FINDINGS B2 的核心（npc_prompts.json 是死数据）**已被 P0 TS 重写解决**：`NpcPromptLoader` 加载 33 NPC × 5 阶段并在 `PromptBuilder` 按好感度注入（`prompt-builder.ts:43-52`）。本计划 B2 部分只剩"验证 + 对话历史注入补完"。
- FINDINGS B1 的遗留缺口：`AgentMemory.getConversationContext()` 存在但 `PromptBuilder` 从未调用，prompt 只注入最近 5 条短期记忆，不含对话 transcript（`prompt-builder.ts:56`）。
- `MultiplayerHelper.ShouldRunAgentLogic` 已存在（`ValleyAgent.Abstractions\Multiplayer\MultiplayerHelper.cs:22-23`）但全生产代码零引用。
- `AgentSyncBroadcaster` / `AgentRemoteRenderer` / 8 种联机消息 DTO 已写好但从未实例化（E1 死代码，本计划不接线，属 P3）。
- `NPCDialoguePatch.cs:27-35` 已有可复用的事件/节日守卫表达式：`Game1.eventUp || Game1.CurrentEvent != null || Game1.isFestival()`。

---

## 背景：D1 缺陷的三处代码（探查确认）

瞬移不是一处，而是 `AgentNavigator.cs` 里三个直接传送分支（`D:\Source\ValleyTalk\src\ValleyAgent.Abstractions\Navigation\AgentNavigator.cs`）：

| 分支 | 行号 | 现状 | 缺陷 |
|------|------|------|------|
| Travelling 阶段 QuickTravel | 96-117 | 旅行时长 <300 tick 且 NPC 不在玩家地图 → `warpCharacter(npc, targetLoc, Game1.player.Tile)` + `setTileLocation(player.Tile + (1,0))` | 条件不与 `travel.TargetLocation` 比较（NPC 隐藏后 `npc.currentLocation != Game1.currentLocation` 恒为 true，首次 FOLLOW update 即触发，绕过整个模拟旅行时长）；落点不验证 |
| Departing 阶段 QuickTravel | 540-558 | 同上 | 同上 |
| 无旅行状态的跨图追赶 | 123-145 | FOLLOW update 发现地图不符且无旅行状态 → 直接 `warpCharacter` 到玩家附近安全瓦片 + `setTileLocation(safeTile + (1,0))` | 第三条瞬移旁路；`safeTile + (1,0)` 不验证 |

另有两处系统性问题：
- `OnPlayerWarped`（`EventHandlerInitializer.cs:1529-1603`）传 `e.NewLocation.Name`，而 navigator 内部比较用 `NameOrUniqueName`（动态地图不匹配）。
- 只守卫 `OnPlayerWarped` 不够：每 tick 的 `ApplyControllerToNpc`（`EventHandlerInitializer.cs:1805-1824`）会独立重启跨图旅行，绕过事件回调守卫。

---

## 文件结构映射

### C# Mod 侧（`D:\Source\ValleyTalk\src\`）

| 文件 | 操作 | 职责 |
|------|------|------|
| `ValleyAgent.Abstractions\Navigation\AgentNavigator.cs` | Modify | D1 三分支重写 + D2 中央守卫 + CancelTravel 位置恢复 |
| `ValleyAgent.Abstractions\Navigation\TravelState.cs`（若无则内嵌于 AgentNavigator.cs） | Modify | TravelState 增加 `DepartureLocation`/`DepartureNpcTile` 字段用于取消恢复 |
| `ValleyAgent\Utils\GameEventGuard.cs` | Create | D2 守卫表达式单一来源 + 可注入谓词（测试用） |
| `ValleyAgent\Initialization\EventHandlerInitializer.cs` | Modify | OnPlayerWarped 守卫 + NameOrUniqueName 归一 + ApplyControllerToNpc 每 tick 守卫 |
| `ValleyAgent\ModEntry.cs` | Modify | E2：Entry() farmhand 早期惰性返回 |
| `ValleyAgent\Initialization\ServiceInitializer.cs` | Modify | E2：farmhand 时不构造 ServerProcessManager/AgentService（由 ModEntry 守卫兜住，此处仅防御性跳过） |
| `ValleyAgent\Patches\NPCDialoguePatch.cs` | Modify | E2：Prefix 加 farmhand 放行（belt-and-braces） |
| `ValleyAgent\Patches\NPCGiftPatch.cs` | Modify | E2：Prefix 加 farmhand 放行 |
| `ValleyAgent\Patches\DialogueBoxInputPatch.cs` | Modify | E2：draw/receiveKeyPress/receiveLeftClick 加 farmhand 放行 |
| `ValleyAgent\Patches\SocialPagePatch.cs` | Modify | E2：draw/receiveLeftClick 加 farmhand 放行 |
| `ValleyAgent.TestMod\Tests\Experience\EXP004_CrossMapFollow.cs` | Modify | 回归断言更新（到达瓦片验证、无瞬移断言） |
| `ValleyAgent.TestMod\Tests\Experience\EXP013_QuickSceneSwitch.cs` | Create | D1 验收：5 秒内连切两图，NPC 不瞬移 |
| `ValleyAgent.TestMod\Tests\Functional\Func_FarmhandInert.cs` | Create | E2 验收：模拟 farmhand 上下文时全部入口惰性 |

### TS 服务器侧（`D:\Source\ValleyAI\packages\stardew\`）

| 文件 | 操作 | 职责 |
|------|------|------|
| `src\prompt-builder.ts` | Modify | 注入对话历史 transcript（调用已存在的 `memory.getConversationContext()`） |
| `tests\prompt-builder.test.ts` | Modify | 断言对话历史注入 + 占位符无残留 |

---

## Phase 1: D1 跨图瞬移修复（C#）

### Task 1: GameEventGuard 静态守卫类（先行，D1/D2 共用）

**Files:**
- Create: `D:\Source\ValleyTalk\src\ValleyAgent\Utils\GameEventGuard.cs`
- Test: 无独立单测基建（C# 单测项目当前不可用），通过 TestMod 功能测试覆盖

- [ ] **Step 1: 创建守卫类**

```csharp
namespace ValleyAgent.Utils
{
    /// <summary>
    /// D2 守卫表达式单一来源。判断当前是否处于事件/节日中，
    /// 期间禁止 NPC 跨图旅行的发起与重定向。
    /// 表达式复用自 NPCDialoguePatch.cs:27-35 的既有守卫。
    /// </summary>
    public static class GameEventGuard
    {
        /// <summary>可注入谓词，TestMod 可替换以模拟事件状态。null 时用真实游戏状态。</summary>
        public static Func<bool>? EventUpOverride;
        public static Func<bool>? CurrentEventOverride;
        public static Func<bool>? FestivalOverride;

        public static bool IsEventOrFestivalActive
        {
            get
            {
                bool eventUp = EventUpOverride?.Invoke() ?? Game1.eventUp;
                bool currentEvent = CurrentEventOverride?.Invoke() ?? (Game1.CurrentEvent != null);
                bool festival = FestivalOverride?.Invoke() ?? Game1.isFestival();
                return eventUp || currentEvent || festival;
            }
        }

        /// <summary>测试后必须调用，清除所有 override。</summary>
        public static void ResetOverrides()
        {
            EventUpOverride = null;
            CurrentEventOverride = null;
            FestivalOverride = null;
        }
    }
}
```

- [ ] **Step 2: Commit**

```bash
cd D:\Source\ValleyTalk
git add src/ValleyAgent/Utils/GameEventGuard.cs
git commit -m "feat(utils): add GameEventGuard single-source event/festival predicate

D2 fix foundation. Expression reused from NPCDialoguePatch:27-35. Injectable overrides for TestMod."
```

---

### Task 2: TravelState 记录出发位置 + CancelTravel 位置恢复

**Files:**
- Modify: `D:\Source\ValleyTalk\src\ValleyAgent.Abstractions\Navigation\AgentNavigator.cs`

**背景**：现状 `CancelTravel`（:164-168）只删旅行状态和隐藏标记；若 NPC 已被 `HideNpc` 挪到 `(-1000,-1000)`，取消后 NPC 永远卡在地图外。D2 守卫和玩家往返取消旅行都依赖安全的取消语义。

- [ ] **Step 1: TravelState 增加出发位置字段**

在 TravelState 类（内嵌于 AgentNavigator.cs）增加：

```csharp
/// <summary>旅行发起时 NPC 所在地图（NameOrUniqueName），用于 CancelTravel 位置恢复。</summary>
public string DepartureLocation { get; set; } = "";
/// <summary>旅行发起时 NPC 所在瓦片，用于 CancelTravel 位置恢复。</summary>
public Point DepartureNpcTile { get; set; }
```

在 `StartTravel`（:339-485）构造 TravelState 处填充：

```csharp
var travel = new TravelState
{
    TargetLocation = targetLocation,
    EntryTile = entryTile,
    TravelStartTick = currentTick,
    TravelDurationTicks = duration,
    PathHopCount = path.HopCount,
    IsTravelling = true,
    DepartureTile = departureTile,
    DepartStartTick = currentTick,
    DepartureLocation = fromLocation,          // 新增
    DepartureNpcTile = npc.TilePoint,          // 新增
};
```

- [ ] **Step 2: CancelTravel 恢复 NPC 位置**

替换 `CancelTravel`（:164-168）：

```csharp
public void CancelTravel(string npcName)
{
    if (_travelStates.TryGetValue(npcName, out var travel))
    {
        _ = _travelStates.Remove(npcName);
        // 若 NPC 已被隐藏到 (-1000,-1000)，恢复到出发位置
        if (_hiddenNpcs.Contains(npcName))
        {
            var npc = Game1.getCharacterFromName(npcName);
            var departureLoc = Game1.getLocationFromName(travel.DepartureLocation);
            if (npc != null && departureLoc != null)
            {
                try
                {
                    Game1.warpCharacter(npc, departureLoc, new Vector2(travel.DepartureNpcTile.X, travel.DepartureNpcTile.Y));
                }
                catch (InvalidOperationException)
                {
                    // 出发地图已不可用（极少见），交给 DayStarted 复活逻辑兜底
                }
            }
        }
    }
    ShowNpc(npcName);
}
```

- [ ] **Step 3: Commit**

```bash
git add src/ValleyAgent.Abstractions/Navigation/AgentNavigator.cs
git commit -m "fix(navigator): CancelTravel restores hidden NPC to departure position

Previously cancelling travel on a hidden NPC stranded it at (-1000,-1000) forever.
TravelState now records DepartureLocation/DepartureNpcTile at StartTravel time."
```

---

### Task 3: D1 修复 — QuickTravel 条件收敛 + 落点验证（Travelling 分支）

**Files:**
- Modify: `D:\Source\ValleyTalk\src\ValleyAgent.Abstractions\Navigation\AgentNavigator.cs`（:96-117）

- [ ] **Step 1: 重写 Travelling 阶段 QuickTravel**

设计规则：
- 玩家去了 `travel.TargetLocation`：NPC 本来就去那儿 → 不干预，让模拟旅行自然完成（或若旅行已接近尾声，正常到达）。
- 玩家去了**别的**地图：取消当前旅行（位置恢复已由 Task 2 保证）→ 对新目标 `StartTravel`（模拟旅行，不瞬移）。
- 删除"warp 到玩家脚下"行为本身。

替换 :96-117：

```csharp
// 快速旅行检查（仅 Travelling 阶段）：玩家改变了目的地
if (npc.currentLocation != Game1.currentLocation)
{
    var newTarget = Game1.currentLocation.NameOrUniqueName;
    if (!string.IsNullOrEmpty(newTarget)
        && !newTarget.Equals(travel.TargetLocation, StringComparison.OrdinalIgnoreCase))
    {
        // 玩家去了非目标地图 → 取消当前旅行（Task 2 已保证位置恢复），重新模拟旅行
        _monitor?.Log($"[AgentNavigator] {npcName}: player redirected to {newTarget} — restarting travel", LogLevel.Debug);
        CancelTravel(npcName);
        var restarted = false;
        NavigateToTaskLocation(npc, new[] { newTarget }, currentTick, out restarted);
        if (!restarted)
        {
            _monitor?.Log($"[AgentNavigator] {npcName}: cannot route to {newTarget}, staying put", LogLevel.Warn);
        }
        return;
    }
    // 玩家就在目标地图：什么都不做，UpdateTravel 到点自然到达
}
```

注意：`CancelTravel` 后 NPC 已恢复到出发地图，`NavigateToTaskLocation` 从出发地图重新寻路，符合"NPC 走过来需要时间"的活人感。

- [ ] **Step 2: Commit**

```bash
git add src/ValleyAgent.Abstractions/Navigation/AgentNavigator.cs
git commit -m "fix(navigator): D1 travelling quick-travel no longer teleports onto player

Player redirect now cancels + restarts simulated travel instead of warp-to-player-tile.
Condition now compares against travel.TargetLocation (was: any map mismatch)."
```

---

### Task 4: D1 修复 — Departing 分支同样收敛（:540-558）

**Files:**
- Modify: `D:\Source\ValleyTalk\src\ValleyAgent.Abstractions\Navigation\AgentNavigator.cs`

- [ ] **Step 1: 重写 Departing 阶段重定向**

替换 :540-558，逻辑与 Task 3 一致（此时 NPC 还在旧地图可见，直接重启旅行即可，无需 CancelTravel 恢复——NPC 尚未隐藏）：

```csharp
// 快速旅行：玩家再次切换地图，重定向
var newTarget = Game1.currentLocation.NameOrUniqueName;
if (!string.IsNullOrEmpty(newTarget) && !newTarget.Equals(travel.TargetLocation, StringComparison.OrdinalIgnoreCase))
{
    _monitor?.Log($"[AgentNavigator] {npcName}: player redirected during departure — restarting travel to {newTarget}", LogLevel.Debug);
    EndTravel(npcName);
    _movementService.Stop(npc, "travel-redirect");
    var restarted = false;
    NavigateToTaskLocation(npc, new[] { newTarget }, currentTick, out restarted);
    if (!restarted)
    {
        _monitor?.Log($"[AgentNavigator] {npcName}: cannot route to {newTarget}, staying put", LogLevel.Warn);
    }
    return;
}
```

- [ ] **Step 2: Commit**

```bash
git add src/ValleyAgent.Abstractions/Navigation/AgentNavigator.cs
git commit -m "fix(navigator): D1 departing quick-travel redirects via simulated travel"
```

---

### Task 5: D1 修复 — 消除无旅行状态的直接追赶传送（:123-145）

**Files:**
- Modify: `D:\Source\ValleyTalk\src\ValleyAgent.Abstractions\Navigation\AgentNavigator.cs`

- [ ] **Step 1: 改为启动模拟旅行**

替换 :123-145：

```csharp
if (npc.currentLocation != Game1.currentLocation)
{
    var targetLocation = Game1.currentLocation.NameOrUniqueName;
    if (!string.IsNullOrEmpty(targetLocation))
    {
        // D1 修复：不再直接 warp 追赶，统一走模拟旅行
        // （动态地图/无路径场景由 StartTravel 内部 fallback warp 处理，带 600 tick 冷却）
        var started = false;
        NavigateToTaskLocation(npc, new[] { targetLocation }, currentTick, out started);
        if (!started)
        {
            _monitor?.Log($"[AgentNavigator] {npc.Name}: cross-map catch-up travel failed to start", LogLevel.Warn);
        }
    }
    return;
}
```

- [ ] **Step 2: 验证无残留瞬移模式**

全文件搜索确认以下模式已不存在：
```
warpCharacter(npc, targetLoc, Game1.player.Tile)
setTileLocation(Game1.player.Tile + new Vector2(1, 0))
setTileLocation(safeTile + new Vector2(1, 0))
```
Expected: 0 matches（保留的合法 warp：UpdateTravel 的 EntryTile 到达 :509、StartTravel 的 fallback warp :375——两者都是验证过落点的单次 warpCharacter，无后置 setTileLocation 覆盖）。

- [ ] **Step 3: Commit**

```bash
git add src/ValleyAgent.Abstractions/Navigation/AgentNavigator.cs
git commit -m "fix(navigator): D1 remove no-travel direct catch-up warp

Map-mismatch with no travel state now starts simulated travel via NavigateToTaskLocation.
All warpCharacter-to-player-tile + unvalidated setTileLocation offset patterns eliminated."
```

---

### Task 6: OnPlayerWarped 归一化 + 每 tick 旁路收敛

**Files:**
- Modify: `D:\Source\ValleyTalk\src\ValleyAgent\Initialization\EventHandlerInitializer.cs`（:1529-1603, :1805-1824）

- [ ] **Step 1: OnPlayerWarped 传 NameOrUniqueName**

:1557-1561 改为：

```csharp
_agentNavigator?.NavigateToTaskLocation(
    npc,
    new[] { e.NewLocation?.NameOrUniqueName ?? "Farm" },
    _tickCounter,
    out startedTravel);
```

- [ ] **Step 2: 每 tick FOLLOW 自动旅行同样用 NameOrUniqueName**

:1811-1815 改为：

```csharp
_agentNavigator.NavigateToTaskLocation(
    npc,
    new[] { Game1.player.currentLocation?.NameOrUniqueName ?? "Farm" },
    _tickCounter,
    out started);
```

- [ ] **Step 3: Commit**

```bash
git add src/ValleyAgent/Initialization/EventHandlerInitializer.cs
git commit -m "fix(init): normalize NameOrUniqueName in cross-map follow entry points

Fixes dynamic-location (mine/volcano) name mismatch between warped event and navigator."
```

---

### Task 6A: 到达表现 — NPC 从地图入口"走过来"（用户决策 2026-07-18）

**Files:**
- Modify: `D:\Source\ValleyTalk\src\ValleyAgent.Abstractions\Navigation\AgentNavigator.cs`（UpdateTravel :487-522）

**用户硬要求**：模拟旅行到达后，NPC 出现的方向必须与来路一致，且观感是"像玩家一样从入口走过来"，**不是传送贴脸**。传送只允许作为卡死兜底。

**设计**：
- 方向正确性由构造保证：`EntryTile` 来自 `LocationGraph` 路径最后一跳的 `ToTile`（从 Farm 来就出现在 BusStop 的 Farm 侧入口），本任务验证并锁定这一行为，不新增逻辑。
- 到达后立即向玩家移动：`UpdateTravel` 到达 warp + `ShowNpc` + 到达语音之后，若玩家就在目标地图，调用 `_movementService.MoveTo(npc, Game1.player.TilePoint, MovementMode.LongRange, currentTick)`，NPC 从入口 visibly 走向玩家。下一 tick FOLLOW 的 `UpdateLocalFollowing` 自然接管后续跟随。
- 卡死兜底传送保留现状：`MovementService.HandleStuck` 的 last-resort teleport 是唯一的"传送"路径，符合用户"除非实在不行NPC卡死了"的许可。

- [ ] **Step 1: UpdateTravel 到达段追加入场移动**

:517-520 区域改为：

```csharp
// Fix #11: show NPC again after travel
ShowNpc(npc.Name);
ShowSpeech(npc, "我到了！");
EndTravel(npc.Name);

// 用户决策：到达观感 = 从入口走向玩家，不是传送贴脸。
// EntryTile 由 LocationGraph 最后一跳推导，方向与来路一致（构造保证）。
if (Game1.player.currentLocation?.NameOrUniqueName == travel.TargetLocation)
{
    _movementService.MoveTo(npc, Game1.player.TilePoint, MovementMode.LongRange, currentTick);
}
```

注意：`EndTravel` 前需先把 `travel.TargetLocation` 读到局部变量（EndTravel 后 travel 已移除）。实现时调整顺序：`var target = travel.TargetLocation; ... EndTravel(...); if (Game1.player... == target) MoveTo(...)`。

- [ ] **Step 2: EXP013 断言补全（随 Task 测试一并实现）**

`EXP013_QuickSceneSwitch` 增加到达表现断言：
1. NPC 到达瓦片 == LocationGraph 路径末跳 `ToTile`（地图边缘入口，非玩家 ±2 格内）；
2. 到达后 60 tick 内 NPC 与玩家距离**持续缩小**（在走路）或已存在向玩家的 PathFindController；
3. 全程无"NPC 与玩家距离 ≤2 且上一帧距离 >10"的贴脸帧（传送检测）。

- [ ] **Step 3: Commit**

```bash
git add src/ValleyAgent.Abstractions/Navigation/AgentNavigator.cs src/ValleyAgent.TestMod/Tests/Experience/EXP013_QuickSceneSwitch.cs
git commit -m "feat(navigator): arrival walks in from map entrance toward player

User decision 2026-07-18: arrival must look like walking over, direction consistent
with travel origin (graph entry tile), teleport only as stuck recovery."
```

---

## Phase 2: D2 节日/事件守卫（C#）

### Task 7: OnPlayerWarped 守卫

**Files:**
- Modify: `D:\Source\ValleyTalk\src\ValleyAgent\Initialization\EventHandlerInitializer.cs`（:1552-1573）

- [ ] **Step 1: FOLLOW 分支加守卫**

在 `if (currentState == AgentState.FOLLOW)` 块内、`CancelTravel` 之前插入：

```csharp
if (GameEventGuard.IsEventOrFestivalActive)
{
    _monitor.Log($"[Follow] {agent.NpcName}: event/festival active — skipping cross-map travel initiation", LogLevel.Debug);
    continue;
}
```

注意：非 FOLLOW 状态（FARM/MINE/等）→ IDLE 的转换**不加守卫**（玩家进节日时让任务态 NPC 回 IDLE 是正确行为）。

- [ ] **Step 2: Commit**

```bash
git add src/ValleyAgent/Initialization/EventHandlerInitializer.cs
git commit -m "fix(init): D2 guard OnPlayerWarped FOLLOW travel during event/festival"
```

---

### Task 8: 每 tick FOLLOW 自动旅行守卫（堵旁路）

**Files:**
- Modify: `D:\Source\ValleyTalk\src\ValleyAgent\Initialization\EventHandlerInitializer.cs`（:1805-1824）

- [ ] **Step 1: 加守卫**

:1806 条件改为：

```csharp
if (state == AgentState.FOLLOW && npc.currentLocation != Game1.player.currentLocation
    && !GameEventGuard.IsEventOrFestivalActive)
```

- [ ] **Step 2: Commit**

```bash
git add src/ValleyAgent/Initialization/EventHandlerInitializer.cs
git commit -m "fix(init): D2 guard per-tick FOLLOW auto-travel during event/festival

Without this, the per-tick path bypasses the OnPlayerWarped guard on the next tick."
```

---

### Task 9: AgentNavigator 中央守卫（保护 move_to / FARM / MINE / FORAGE 调用方）

**Files:**
- Modify: `D:\Source\ValleyTalk\src\ValleyAgent.Abstractions\Navigation\AgentNavigator.cs`（NavigateToTaskLocation :174-208）

- [ ] **Step 1: NavigateToTaskLocation 入口守卫**

方法体最前面插入：

```csharp
// D2 中央守卫：事件/节日期间禁止一切跨图旅行发起
// （AgentNavigator 在 Abstractions 项目，通过 GameEventGuard 引用主 mod 的 Utils；
//   若引用方向不允许，则将 GameEventGuard 下移至 Abstractions\Utils）
if (ValleyAgent.Utils.GameEventGuard.IsEventOrFestivalActive)
{
    startedTravel = false;
    return null;
}
```

**实现注意**：`GameEventGuard` 放在哪个项目取决于现有引用方向。`AgentNavigator` 在 `ValleyAgent.Abstractions`，主 mod 引用 Abstractions（Abstractions 不能反向引用主 mod）。**因此 GameEventGuard 应创建在 `ValleyAgent.Abstractions\Utils\GameEventGuard.cs`**（Task 1 路径相应调整），主 mod 与 Abstractions 都可用。

- [ ] **Step 2: 事件期间进行中的旅行处理**

在 `AgentNavigator.Update`（:83）方法开头插入：

```csharp
// D2：事件/节日开始时，进行中的旅行取消（Task 2 保证位置恢复）
if (ValleyAgent.Utils.GameEventGuard.IsEventOrFestivalActive
    && _travelStates.TryGetValue(npcName, out var activeTravel) && activeTravel.IsTravelling)
{
    _monitor?.Log($"[AgentNavigator] {npcName}: event/festival started — cancelling travel", LogLevel.Debug);
    CancelTravel(npcName);
}
```

设计说明：选"取消+恢复"而非"挂起"——节日结束后每 tick FOLLOW 会自动重新发起旅行（Phase 2 Task 8 的守卫此时已不成立），无需新增挂起状态机。

- [ ] **Step 3: Commit**

```bash
git add src/ValleyAgent.Abstractions/Navigation/AgentNavigator.cs src/ValleyAgent.Abstractions/Utils/GameEventGuard.cs
git commit -m "fix(navigator): D2 central event/festival guard in AgentNavigator

NavigateToTaskLocation refuses new travel during events; in-flight travel is cancelled
with position restore. Protects move_to/FARM/MINE/FORAGE callers."
```

---

### Task 10: D3 顺手修复 — 出发语音改 chat 可见（可选，低成本）

**Files:**
- Modify: `D:\Source\ValleyTalk\src\ValleyAgent.Abstractions\Navigation\AgentNavigator.cs`

**背景**：D3 核心是"走向出口玩家看不到"（PathFindController 只在激活地图推进，玩家已在新图）。彻底修复（预 warp 钩子）成本高、收益低，本计划只做低成本部分：出发/到达语音从"旧地图头顶气泡"（玩家看不见）改为 `NpcSpeechHelper.Speak`（气泡 + chat 消息，chat 在新图可见）。

- [ ] **Step 1: ShowSpeech 改走 NpcSpeechHelper**

`AgentNavigator.ShowSpeech`（:618-621）改为：

```csharp
private static void ShowSpeech(NPC npc, string text, int duration = 3000)
{
    // D3：玩家通常已在新地图，头顶气泡不可见；Speak 同时发 chat 消息保证玩家能看到
    ValleyAgent.Utils.NpcSpeechHelper.Speak(npc, text, duration);
}
```

**实现注意**：同 Task 9 的引用方向问题——`NpcSpeechHelper` 在主 mod `ValleyAgent\Utils`。若 Abstractions 不能引用，则在 `Abstractions\Utils` 新增一个轻量 `TravelSpeechHelper`（气泡+chat，复制 NpcSpeechHelper 的 20 行逻辑），AgentNavigator 用它。二选一，以实现时引用方向为准。

- [ ] **Step 2: Commit**

```bash
git commit -m "fix(navigator): D3 travel speech now also posts to chat box

Overhead bubble is invisible once player left the map; chat copy preserves the feedback."
```

---

## Phase 3: B2 验证 + 对话历史注入补完（TS，`D:\Source\ValleyAI`）

### Task 11: PromptBuilder 注入对话历史 transcript

**Files:**
- Modify: `D:\Source\ValleyAI\packages\stardew\src\prompt-builder.ts`
- Modify: `D:\Source\ValleyAI\packages\stardew\tests\prompt-builder.test.ts`

**背景**：P0 已解决 npc_prompts.json 死数据问题（33 NPC × 5 阶段注入，`prompt-builder.ts:43-52`）。遗留缺口：`AgentMemory.getConversationContext(10)` 存在但从未被调用，prompt 只有最近 5 条短期记忆，NPC 看不到对话 transcript → 玩家追问"我刚才说了什么"时 NPC 只能靠短期记忆猜测。

- [ ] **Step 1: Write the failing test**

在 `prompt-builder.test.ts` 追加：

```typescript
test("injects conversation history transcript into prompt", () => {
  const loader = new NpcPromptLoader(DATA_PATH);
  const builder = new PromptBuilder(loader);
  const memory = new AgentMemory("Abigail", "/tmp/x");
  memory.addConversation("player", "我叫张三");
  memory.addConversation("npc", "你好张三");
  memory.addConversation("player", "你喜欢什么");
  const scene = makeTestScene(); // 复用现有测试的 scene 构造
  const prompt = builder.buildDialogueSystemPrompt(memory, scene, "Abigail");
  expect(prompt).toContain("农场主: 我叫张三");
  expect(prompt).toContain("Abigail: 你好张三");
  expect(prompt).toContain("农场主: 你喜欢什么");
});

test("conversation history section degrades gracefully when empty", () => {
  const loader = new NpcPromptLoader(DATA_PATH);
  const builder = new PromptBuilder(loader);
  const memory = new AgentMemory("Abigail", "/tmp/x");
  const scene = makeTestScene();
  const prompt = builder.buildDialogueSystemPrompt(memory, scene, "Abigail");
  // 不应出现空段落或 undefined
  expect(prompt).not.toContain("undefined");
  expect(prompt).not.toMatch(/最近对话：\s*\n\s*\n/);
});
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd D:\Source\ValleyAI && bun test packages/stardew/tests/prompt-builder.test.ts`
Expected: FAIL with "expected prompt to contain 农场主: 我叫张三"

- [ ] **Step 3: Write minimal implementation**

在 `prompt-builder.ts` 模板中 `─── 当前场景 ───` 之前插入对话历史段，并在 build 函数中填充：

```typescript
// 模板新增段（位于 significant_memories 之后、当前场景之前）：
// ─── 最近对话 ───
// {conversation_history}

// build 函数中：
const conversationHistory = memory.getConversationContext(10);
// ...join 时：
const fullPrompt = template
  .replace("{phase_prompt}", fullPhasePrompt)
  .replace("{significant_memories}", significantText)
  .replace("{conversation_history}", conversationHistory || "（今天还没说过话）")
  // ...其余占位符不变
```

具体实现以保持现有模板结构为准；空历史时显示"（今天还没说过话）"而非空段。

- [ ] **Step 4: Run test to verify it passes**

Run: `cd D:\Source\ValleyAI && bun test packages/stardew/tests/prompt-builder.test.ts`
Expected: PASS（含既有 93 行测试无回归）

Run typecheck: `cd D:\Source\ValleyAI\packages\stardew && bun run typecheck`
Expected: 0 errors

- [ ] **Step 5: B2 全量验证（既有测试确认）**

Run: `cd D:\Source\ValleyAI && bun test packages/stardew/tests/npc-prompt-loader.test.ts`
Expected: PASS（33 NPC × 5 阶段加载、好感度→阶段映射——B2 核心已由 P0 解决，此处为确认门）

- [ ] **Step 6: Commit**

```bash
cd D:\Source\ValleyAI
git add packages/stardew/src/prompt-builder.ts packages/stardew/tests/prompt-builder.test.ts
git commit -m "feat(stardew): inject conversation history transcript into dialogue prompt

B1 leftover: getConversationContext(10) existed but was never called. NPC can now see
the actual dialogue transcript, not just last 5 short-term memories. B2 confirmed closed."
```

---

## Phase 4: E2 联机止血 — farmhand 完全惰性化（C#）

**设计决策**：farmhand（含分屏非主玩家屏）上 mod 完全不初始化任何 Agent 逻辑：不构造 ServerProcessManager（不自启服务器）、不注册事件、不打 Harmony 补丁、不注册控制台命令。主机（`Context.IsMainPlayer`）行为完全不变。利用已存在的 `MultiplayerHelper.IsFarmhand`（`IsMultiplayer && !IsMainPlayer`，同时覆盖联机 farmhand 与分屏副屏）。

**为什么 Entry 早退而不是逐事件守卫**：逐事件守卫要守 12 个事件 + 4 补丁类 + 13 命令 + ServerProcessManager，遗漏一个就是事故；Entry 早退一处兜住全部。belt-and-braces 补丁守卫防未来回归。

### Task 12: ModEntry.Entry farmhand 早退

**Files:**
- Modify: `D:\Source\ValleyTalk\src\ValleyAgent\ModEntry.cs`

- [ ] **Step 1: Entry 开头加守卫**

在 `Entry()` 方法体最前面（ServiceContainer 创建之前）插入：

```csharp
// E2 止血：farmhand（联机客机 / 分屏副屏）完全惰性。
// 不启动服务器、不注册事件、不打补丁、不烧 token。
// 联机完整支持属 P3（见 docs/superpowers/specs/2026-07-18-p3-multiplayer-architecture-options.md）。
if (ValleyAgent.Abstractions.Multiplayer.MultiplayerHelper.IsFarmhand)
{
    Monitor.Log(
        "ValleyAgent is inactive on farmhands/split-screen secondary screens (multiplayer not yet supported). " +
        "All agent logic runs on the host only.",
        LogLevel.Info);
    _farmhandInert = true;
    return;
}
```

新增私有字段 `private bool _farmhandInert;`。

- [ ] **Step 2: GetApi 与 ReturnedToTitle 防御**

```csharp
public override object? GetApi()
{
    // farmhand 惰性时返回 null（SMAPI 约定允许），调用方需 null-check
    return _farmhandInert ? null : _api;
}

private void OnReturnedToTitle(object? sender, ReturnedToTitleEventArgs e)
{
    if (_farmhandInert) return;   // 惰性时无可清理
    // ...原有逻辑
}
```

注意：ReturnedToTitle 订阅在 Entry 内，早退则未订阅，天然安全；上述防御仅防未来重构。

- [ ] **Step 3: 删除 OnGameLaunched 的旧警告日志**

`EventHandlerInitializer.cs:458-461` 的 "does not fully support multiplayer" 警告已无意义（farmhand 根本走不到这里，主机不需要警告）。删除该 if 块。

- [ ] **Step 4: Commit**

```bash
git add src/ValleyAgent/ModEntry.cs src/ValleyAgent/Initialization/EventHandlerInitializer.cs
git commit -m "fix(multiplayer): E2 farmhand full inert early-return in ModEntry.Entry

No server process, no events, no patches, no token burn on farmhands/split-screen
secondary screens. GetApi returns null. Replaces the toothless warning log."
```

---

### Task 13: Harmony 补丁 belt-and-braces 守卫

**Files:**
- Modify: `D:\Source\ValleyTalk\src\ValleyAgent\Patches\NPCDialoguePatch.cs`
- Modify: `D:\Source\ValleyTalk\src\ValleyAgent\Patches\NPCGiftPatch.cs`
- Modify: `D:\Source\ValleyTalk\src\ValleyAgent\Patches\DialogueBoxInputPatch.cs`
- Modify: `D:\Source\ValleyTalk\src\ValleyAgent\Patches\SocialPagePatch.cs`

**背景**：Entry 早退后 farmhand 上 `harmony.PatchAll()` 不会执行，补丁本不会存在。此任务防的是未来重构把守卫挪晚、或分屏场景静态字段串扰（`DialogueBoxInputPatch._activeAgentNpc` 等静态状态）。

- [ ] **Step 1: 每个 Prefix/Postfix 首行加守卫**

规则：Prefix 返回 bool 的 → `if (MultiplayerHelper.IsFarmhand) return true;`（放行原版）；Postfix/void → `if (MultiplayerHelper.IsFarmhand) return;`

| 文件 | 方法 | 守卫形式 |
|------|------|---------|
| NPCDialoguePatch.cs:25 | Prefix | `return true` |
| NPCGiftPatch.cs:45 | Prefix | `return true` |
| DialogueBoxInputPatch.cs:82 | ChatBox.activate Prefix | `return true` |
| DialogueBoxInputPatch.cs:89 | draw Postfix | `return` |
| DialogueBoxInputPatch.cs:110 | receiveKeyPress Prefix | `return true` |
| DialogueBoxInputPatch.cs:164 | receiveLeftClick Prefix | `return true` |
| SocialPagePatch.cs:36 | draw Postfix | `return` |
| SocialPagePatch.cs:67 | receiveLeftClick Postfix | `return` |

- [ ] **Step 2: Commit**

```bash
git add src/ValleyAgent/Patches/
git commit -m "fix(multiplayer): E2 belt-and-braces farmhand guards in all Harmony patches

Defense in depth against future guard regression and split-screen static-field bleed."
```

---

## 测试与验收

### TestMod 新增/更新

| 测试 | 文件 | 验证点 |
|------|------|--------|
| EXP013_QuickSceneSwitch | `Tests\Experience\EXP013_QuickSceneSwitch.cs`（新建） | D1 验收：FOLLOW 中玩家 5 秒内连切两图（Farm→BusStop→Town）→ 断言 NPC 不出现在玩家脚下；断言 NPC 在模拟旅行时长后出现在 Town 入口瓦片（LocationGraph 推导的 EntryTile），且落点 `TileWalkability.IsTileWalkable` 为 true |
| EXP004_CrossMapFollow（更新） | `Tests\Experience\EXP004_CrossMapFollow.cs` | 回归：正常跨图跟随仍工作；新增断言 NPC 到达瓦片可行走、到达时长 ≥ 模拟旅行最小时长（SingleHopTicks） |
| Func_FarmhandInert | `Tests\Functional\Func_FarmhandInert.cs`（新建） | E2 验收：通过 `GameEventGuard` 式 override 不可行（MultiplayerHelper 直接读 Context），改为反射设置或集成验证清单——实现时评估；最低交付：手动联机验证 checklist（见下） |
| F_FollowCrossMap（回归） | `Tests\Focused\F_FollowCrossMap.cs` | 900 tick 间隔跨图跟随无回归 |

### D2 验收（节日守卫）

节日期间手动验证清单（自动测试受限于游戏内节日日期）：
1. `GameEventGuard.FestivalOverride = () => true` 注入（TestMod debug 命令）→ 玩家跨图 → 断言无旅行发起、进行中旅行取消且 NPC 回到出发位置
2. 复原则 `GameEventGuard.ResetOverrides()`

### TS 测试

```bash
cd D:\Source\ValleyAI
bun test                                    # 全部（含覆盖率 80% 门槛）
bun run typecheck                           # 0 errors
```

### C# 构建

```bash
cd D:\Source\ValleyTalk\src\ValleyAgent
dotnet build -c Release                     # 0 warning 0 error
```

### 手动联机验证 checklist（E2）

- [ ] 主机开联机档：mod 正常初始化，服务器自启，Agent 工作
- [ ] farmhand 加入：SMAPI 日志出现 "inactive on farmhands" Info；无 valley-ai-server 进程在 farmhand 机器启动；farmhand 点击 NPC 走原版对话；`ValleyAgent_status` 命令不存在
- [ ] 无端口冲突（farmhand 不再触发 KillExistingServerOnPort 竞态）

### 性能红线（不变）

- `OnUpdateTicked` 增量 < 1ms（守卫为 O(1) 布尔读取）
- 旅行取消/重启不产生新 PathFindController 风暴（MovementService 单一所有权不变）

---

## 风险与缓解

| 风险 | 缓解 |
|------|------|
| 取消瞬移后 NPC 到达变慢，玩家觉得"跟丢了" | StartTravel fallback warp（动态地图）保留 600 tick 冷却兜底；到达语音 chat 可见（Task 10）缓解等待感 |
| GameEventGuard 放置项目引用方向错误 | Task 9 已注明：放 `ValleyAgent.Abstractions\Utils`，两侧可用 |
| EXP004 既有断言依赖旧的瞬移到达时长 | Task 同步更新断言为"到达时长 ≥ SingleHopTicks" |
| 节日当天 FOLLOW NPC 长期不跟随 | 设计如此（防破坏节日流程）；节日结束自动恢复 |
| farmhand 上 GetApi 返回 null 影响 TestMod | TestMod 仅在单机/主机测试环境运行，manifest 依赖检查不受影响 |

---

## 待用户决策的开放问题 — ✅ 已于 2026-07-18 全部拍板

1. **D3 彻底修复**（预 warp 钩子）：**不追加**。按本计划只做 chat 语音双发（Task 10）。
2. **E2 的 `GetApi()`**：**返回 null**（用户原话"空"）。跨 mod 调用方需 null-check，TestMod 仅主机环境运行不受影响。
3. **节日期间进行中旅行**：**取消+位置恢复+节后自动重启**（Task 2/9 已按此设计），不做挂起续行。
4. **到达表现（新增硬要求）**：NPC 到达必须是"从地图入口走过来"的观感，方向与来路一致；传送仅限卡死兜底 → 已落地为 **Task 6A**。
5. **联机排期**：用户要求联机（方案 B）**立即立项** → 见 `docs\superpowers\plans\2026-07-18-multiplayer-sync-host-authoritative.md`。本计划的 E2 止血（Phase 4）是其前置基线，不受影响。
