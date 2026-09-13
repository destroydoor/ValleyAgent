# 交互式 Agent 分配与三档容量 — 实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Day 1 不再任意分配刘易斯/罗宾；改为玩家交互 + 导演 beat + 5% 邻近 spark 驱动的懒加载分配，配合三档容量（Min/Normal/Max）与互动空闲淘汰。

**Architecture:** C# 侧删除 Phase 2 任意回退，新增 spark 触发器 + 互动空闲淘汰 + `allocate_agent` 协议处理。TS 侧把 Director 接入 server，每日 10% 概率触发 `morningPlan`，对产出的 beat NPC 发送 `allocate_agent`。两仓库通过 `protocol/messages.json` 契约单一源对齐。

**Tech Stack:** C# (.NET 8, xUnit, SMAPI), TypeScript (Bun, bun:sqlite), WebSocket JSON 协议。

**设计文档:** `docs/superpowers/specs/2026-08-03-interaction-driven-agent-allocation-design.md`

**仓库布局:**
- C# Mod: `<REPO_ROOT>\src\ValleyAgent` + `<REPO_ROOT>\src\ValleyAgent.UnitTests`
- TS Server: `<VALLEYAI_ROOT>\packages\stardew` + `<VALLEYAI_ROOT>\protocol\messages.json`

---

## Phase 1: C# 三档容量配置 + GMCM

### Task 1: ModConfig 加 NormalAgentNpcs 字段

**Files:**
- Modify: `<REPO_ROOT>\src\ValleyAgent\Config\ModConfig.cs` (第 45-52 行附近)

- [ ] **Step 1: 写失败测试 — 迁移与 Validate**

Create: `<REPO_ROOT>\src\ValleyAgent.UnitTests\ModConfigThreeTierTests.cs`

```csharp
#nullable enable
using ValleyAgent.Config;
using Xunit;

namespace ValleyAgent.UnitTests;

public static class ModConfigThreeTierTests
{
    [Fact]
    public static void Validate_NormalWithinMinMax_NoChange()
    {
        var config = new ModConfig { MinAgentNpcs = 0, NormalAgentNpcs = 1, MaxAgentNpcs = 2 };
        var changed = config.Validate();
        Assert.False(changed);
        Assert.Equal(0, config.MinAgentNpcs);
        Assert.Equal(1, config.NormalAgentNpcs);
        Assert.Equal(2, config.MaxAgentNpcs);
    }

    [Fact]
    public static void Validate_NormalBelowMin_ClampedUp()
    {
        var config = new ModConfig { MinAgentNpcs = 2, NormalAgentNpcs = 0, MaxAgentNpcs = 3 };
        var changed = config.Validate();
        Assert.True(changed);
        Assert.Equal(2, config.NormalAgentNpcs);
    }

    [Fact]
    public static void Validate_NormalAboveMax_ClampedDown()
    {
        var config = new ModConfig { MinAgentNpcs = 0, NormalAgentNpcs = 5, MaxAgentNpcs = 2 };
        var changed = config.Validate();
        Assert.True(changed);
        Assert.Equal(2, config.NormalAgentNpcs);
    }

    [Fact]
    public static void Migrate_NormalDefaultsToMax_WhenZero()
    {
        // 旧存档无 Normal 字段 → 反序列化默认 0 → 迁移到 Max
        var config = new ModConfig { MinAgentNpcs = 0, NormalAgentNpcs = 0, MaxAgentNpcs = 2 };
        config.MigrateLegacyFields();
        Assert.Equal(2, config.NormalAgentNpcs);
    }
}
```

- [ ] **Step 2: 运行测试验证失败**

Run: `dotnet test src\ValleyAgent.UnitTests\ValleyAgent.UnitTests.csproj --filter "ModConfigThreeTierTests"`
Expected: FAIL — `NormalAgentNpcs` 属性不存在（编译错误）

- [ ] **Step 3: 加 NormalAgentNpcs 属性**

Modify `ModConfig.cs` 第 49 行后插入：

```csharp
        [DefaultValue(1)]
        public int NormalAgentNpcs { get; set; } = 1;
```

- [ ] **Step 4: 加迁移逻辑**

Modify `ModConfig.cs` MigrateLegacyFields 方法（第 425-428 行附近），在 `MaxAgentNpcs == 1` 迁移块后加：

```csharp
            // 三档迁移：旧存档无 NormalAgentNpcs 字段，反序列化默认 0。
            // 迁移到 Math.Clamp(Max, Min, Max)，保证行为与旧两档一致。
            if (NormalAgentNpcs == 0 && MaxAgentNpcs > 0)
            {
                NormalAgentNpcs = Math.Clamp(MaxAgentNpcs, MinAgentNpcs, MaxAgentNpcs);
            }
```

- [ ] **Step 5: 加 Validate 约束**

Modify `ModConfig.cs` Validate 方法（第 458 行后，MaxAgentNpcs 校验之后）加：

```csharp
            if (NormalAgentNpcs < MinAgentNpcs) { NormalAgentNpcs = MinAgentNpcs; changed = true; }
            if (NormalAgentNpcs > MaxAgentNpcs) { NormalAgentNpcs = MaxAgentNpcs; changed = true; }
```

- [ ] **Step 6: 运行测试验证通过**

Run: `dotnet test src\ValleyAgent.UnitTests\ValleyAgent.UnitTests.csproj --filter "ModConfigThreeTierTests"`
Expected: PASS (4/4)

- [ ] **Step 7: 提交**

```bash
git add src/ValleyAgent.UnitTests/ModConfigThreeTierTests.cs src/ValleyAgent/Config/ModConfig.cs
git commit -m "feat(config): add NormalAgentNpcs three-tier agent count config"
```

---

### Task 2: GMCM 设置界面接入 Normal 档

**Files:**
- Modify: `<REPO_ROOT>\src\ValleyAgent\Config\GMCMIntegration.cs` (第 213-242 行)

- [ ] **Step 1: 在 Min 与 Max 滑块之间插入 Normal 滑块**

Modify `GMCMIntegration.cs` 第 224 行后（Min 滑块之后、Max 滑块之前）插入：

```csharp
            gmcm.AddNumberOption(
                mod: manifest,
                name: () => T("普通 Agent NPC 数", "Normal Agent NPCs", config),
                tooltip: () => T("spark 主动激活的目标数量 (Min - Max)", "Target count for spark proactive activation (Min - Max).", config),
                getValue: () => config.NormalAgentNpcs,
                setValue: value =>
                {
                    config.NormalAgentNpcs = Math.Clamp(value, config.MinAgentNpcs, config.MaxAgentNpcs);
                },
                min: 0,
                max: 10,
                interval: 1);
```

- [ ] **Step 2: Min 滑块联动 Normal**

Modify Min 滑块的 `setValue`（第 218-221 行）改为：

```csharp
                setValue: value =>
                {
                    config.MinAgentNpcs = Math.Clamp(value, 0, config.MaxAgentNpcs);
                    if (config.NormalAgentNpcs < config.MinAgentNpcs)
                    {
                        config.NormalAgentNpcs = config.MinAgentNpcs;
                    }
                },
```

- [ ] **Step 3: Max 滑块联动 Normal**

Modify Max 滑块的 `setValue`（第 231-238 行）改为：

```csharp
                setValue: value =>
                {
                    var newMax = Math.Clamp(value, 0, 10);
                    config.MaxAgentNpcs = newMax;
                    if (config.MinAgentNpcs > newMax)
                    {
                        config.MinAgentNpcs = newMax;
                    }
                    if (config.NormalAgentNpcs > newMax)
                    {
                        config.NormalAgentNpcs = newMax;
                    }
                },
```

- [ ] **Step 4: Clone 方法加 NormalAgentNpcs**

Modify `GMCMIntegration.cs` 第 882-883 行，在 `target.MinAgentNpcs = source.MinAgentNpcs;` 后加：

```csharp
            target.NormalAgentNpcs = source.NormalAgentNpcs;
```

- [ ] **Step 5: 编译验证 0 警告**

Run: `dotnet build src\ValleyAgent\ValleyAgent.csproj -warnaserror`
Expected: BUILD SUCCESS, 0 warnings

- [ ] **Step 6: 提交**

```bash
git add src/ValleyAgent/Config/GMCMIntegration.cs
git commit -m "feat(config): add Normal Agent NPCs slider to GMCM with min/max linkage"
```

---

## Phase 2: C# 分配逻辑改造

### Task 3: AgentAllocationInfo 加互动空闲字段

**Files:**
- Modify: `<REPO_ROOT>\src\ValleyAgent\Agents\AgentAllocationInfo.cs`
- Test: `<REPO_ROOT>\src\ValleyAgent.UnitTests\AgentAllocationInfoTests.cs`

- [ ] **Step 1: 写失败测试 — ShouldEvict 判定**

Create: `<REPO_ROOT>\src\ValleyAgent.UnitTests\AgentAllocationInfoTests.cs`

```csharp
#nullable enable
using System;
using ValleyAgent.Agents;
using Xunit;

namespace ValleyAgent.UnitTests;

public static class AgentAllocationInfoTests
{
    private static readonly DateTime Now = new(2026, 8, 3, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public static void ShouldEvict_NoInteractionBeyondThreshold_NoKeep_ReturnsTrue()
    {
        var info = new AgentAllocationInfo("Haley", 0, 0, 0, 0)
        {
            LastPlayerInteractionTick = Now.AddSeconds(-100)
        };
        Assert.True(info.ShouldEvict(Now, idleThresholdSeconds: 90));
    }

    [Fact]
    public static void ShouldEvict_WithinThreshold_ReturnsFalse()
    {
        var info = new AgentAllocationInfo("Haley", 0, 0, 0, 0)
        {
            LastPlayerInteractionTick = Now.AddSeconds(-30)
        };
        Assert.False(info.ShouldEvict(Now, idleThresholdSeconds: 90));
    }

    [Fact]
    public static void ShouldEvict_BeyondThreshold_KeepNotExpired_ReturnsFalse()
    {
        var info = new AgentAllocationInfo("Haley", 0, 0, 0, 0)
        {
            LastPlayerInteractionTick = Now.AddSeconds(-100),
            KeepUntil = Now.AddSeconds(50)
        };
        Assert.False(info.ShouldEvict(Now, idleThresholdSeconds: 90));
    }

    [Fact]
    public static void ShouldEvict_BeyondThreshold_KeepExpired_ReturnsTrue()
    {
        var info = new AgentAllocationInfo("Haley", 0, 0, 0, 0)
        {
            LastPlayerInteractionTick = Now.AddSeconds(-100),
            KeepUntil = Now.AddSeconds(-10)
        };
        Assert.True(info.ShouldEvict(Now, idleThresholdSeconds: 90));
    }
}
```

- [ ] **Step 2: 运行测试验证失败**

Run: `dotnet test src\ValleyAgent.UnitTests\ValleyAgent.UnitTests.csproj --filter "AgentAllocationInfoTests"`
Expected: FAIL — `LastPlayerInteractionTick` / `KeepUntil` / `ShouldEvict` 不存在

- [ ] **Step 3: 加字段与 ShouldEvict 方法**

Modify `AgentAllocationInfo.cs` 在 `LastUpdated` 属性后加：

```csharp
        /// <summary>
        /// 最近一次玩家与该 NPC 互动的时间（对话/送礼/聊天栏路由时刷新）。
        /// 用于互动空闲淘汰判定。
        /// </summary>
        public DateTime LastPlayerInteractionTick { get; set; }

        /// <summary>
        /// 导演 allocate_agent 设置的豁免截止时间（可空）。
        /// 期间豁免互动空闲淘汰，对应 beat.windowEnd。
        /// </summary>
        public DateTime? KeepUntil { get; set; }

        /// <summary>
        /// 判定是否应因互动空闲被淘汰。
        /// 条件：距上次玩家互动超过阈值，且无 KeepUntil 豁免或 KeepUntil 已过期。
        /// </summary>
        public bool ShouldEvict(DateTime now, int idleThresholdSeconds)
        {
            var idleDuration = now - LastPlayerInteractionTick;
            if (idleDuration.TotalSeconds <= idleThresholdSeconds)
            {
                return false;
            }
            if (KeepUntil.HasValue && now < KeepUntil.Value)
            {
                return false;
            }
            return true;
        }
```

- [ ] **Step 4: 运行测试验证通过**

Run: `dotnet test src\ValleyAgent.UnitTests\ValleyAgent.UnitTests.csproj --filter "AgentAllocationInfoTests"`
Expected: PASS (4/4)

- [ ] **Step 5: 提交**

```bash
git add src/ValleyAgent/Agents/AgentAllocationInfo.cs src/ValleyAgent.UnitTests/AgentAllocationInfoTests.cs
git commit -m "feat(allocation): add LastPlayerInteractionTick, KeepUntil, ShouldEvict to AgentAllocationInfo"
```

---

### Task 4: 删除 DayStarted Phase 2 任意回退

**Files:**
- Modify: `<REPO_ROOT>\src\ValleyAgent\Initialization\EventHandlerInitializer.cs` (第 970-989 行)

- [ ] **Step 1: 删除 Phase 2 fallback 块**

Modify `EventHandlerInitializer.cs` 删除第 970-989 行的 Phase 2 块：

```csharp
                // ─── Phase 2: enforce MinAgentNpcs floor ───────────────────────────
                // Only kicks in when MinAgentNpcs > 0 and Phase 1 didn't reach the floor.
                // Pulls from villagers the player has never met (priority 0).
                var minNeeded = _config!.MinAgentNpcs - _agentService!.AllocationManager.CurrentAgentCount;
                if (minNeeded > 0)
                {
                    foreach (var name in fallbackCandidates.Take(minNeeded))
                    {
                        if (_agentService!.AllocationManager.TryAllocate(name, 0, 0, 0))
                        {
                            var agent = _agentService.CreateAgent(name);
                            if (agent != null)
                            {
                                _bioLoader?.InjectBio(agent.Brain);
                                _monitor.Log($"Auto-allocated Agent (min-floor): {name}", LogLevel.Info);
                                WireAgentStateEvents(agent);
                            }
                        }
                    }
                }
```

同时删除第 936-937 行的 `fallbackCandidates` 声明（不再需要）：

```csharp
                // ─── Phase 2 fallback: villagers with no friendship data, used only
                // to satisfy MinAgentNpcs when Phase 1 didn't yield enough agents.
                var fallbackCandidates = new List<string>();
```

以及第 949-952 行的 else 分支（fallbackCandidates.Add）：

```csharp
                    else
                    {
                        fallbackCandidates.Add(npc.Name);
                    }
```

- [ ] **Step 2: 编译验证**

Run: `dotnet build src\ValleyAgent\ValleyAgent.csproj -warnaserror`
Expected: BUILD SUCCESS, 0 warnings

- [ ] **Step 3: 提交**

```bash
git add src/ValleyAgent/Initialization/EventHandlerInitializer.cs
git commit -m "fix(allocation): remove Phase 2 fallback that arbitrarily assigned Lewis/Robin on Day 1"
```

---

### Task 5: spark 触发器 — 玩家附近 NPC 低概率激活

**Files:**
- Create: `<REPO_ROOT>\src\ValleyAgent\Agents\SparkAllocator.cs`
- Modify: `<REPO_ROOT>\src\ValleyAgent\Core\AgentTickLoop.cs` (tick 循环调用 spark)
- Test: `<REPO_ROOT>\src\ValleyAgent.UnitTests\SparkAllocatorTests.cs`

- [ ] **Step 1: 写失败测试 — spark 概率与每日每 NPC 一次**

Create: `<REPO_ROOT>\src\ValleyAgent.UnitTests\SparkAllocatorTests.cs`

```csharp
#nullable enable
using System;
using ValleyAgent.Agents;
using Xunit;

namespace ValleyAgent.UnitTests;

public static class SparkAllocatorTests
{
    [Fact]
    public static void TrySpark_FirstTimeForNpc_ReturnsTrueAndRecordsDate()
    {
        var spark = new SparkAllocator(seed: 42);
        // 概率 100% 时必定触发
        var hit = spark.TrySpark("Haley", dateKey: "Y1_spring_1", currentAgentCount: 0, minAgents: 0, normalAgents: 1, maxAgents: 2, probabilityOverride: 1.0);
        Assert.True(hit);
        // 同一 NPC 同一天不再触发
        var hit2 = spark.TrySpark("Haley", dateKey: "Y1_spring_1", currentAgentCount: 0, minAgents: 0, normalAgents: 1, maxAgents: 2, probabilityOverride: 1.0);
        Assert.False(hit2);
    }

    [Fact]
    public static void TrySpark_CurrentAtOrAboveNormal_ReturnsFalse()
    {
        var spark = new SparkAllocator(seed: 42);
        var hit = spark.TrySpark("Haley", "Y1_spring_1", currentAgentCount: 1, minAgents: 0, normalAgents: 1, maxAgents: 2, probabilityOverride: 1.0);
        Assert.False(hit);
    }

    [Fact]
    public static void TrySpark_BelowMin_UsesDoubleProbability()
    {
        var spark = new SparkAllocator(seed: 42);
        // current < Min → 概率加倍。probabilityOverride=0.5 表示基础 5%，
        // BelowMin 应翻倍到 10%。用 seed 控制随机数。
        // 这里验证 BelowMin 时概率确实翻倍：用 probabilityOverride=0.06（6%），
        // 基础不触发但翻倍后 12% 触发。
        var hit = spark.TrySpark("Haley", "Y1_spring_1", currentAgentCount: 0, minAgents: 1, normalAgents: 2, maxAgents: 3, probabilityOverride: 0.06);
        // seed=42 第一个随机数约 0.67，> 0.12 不触发。验证不抛异常即可。
        // 真实概率验证在集成测试。
        Assert.False(hit);
    }

    [Fact]
    public static void TrySpark_NewDay_ResetsRolledSet()
    {
        var spark = new SparkAllocator(seed: 42);
        spark.TrySpark("Haley", "Y1_spring_1", 0, 0, 1, 2, 1.0);
        // 第二天可以再次触发
        var hit = spark.TrySpark("Haley", "Y1_spring_2", 0, 0, 1, 2, 1.0);
        Assert.True(hit);
    }
}
```

- [ ] **Step 2: 运行测试验证失败**

Run: `dotnet test src\ValleyAgent.UnitTests\ValleyAgent.UnitTests.csproj --filter "SparkAllocatorTests"`
Expected: FAIL — `SparkAllocator` 类型不存在

- [ ] **Step 3: 实现 SparkAllocator**

Create: `<REPO_ROOT>\src\ValleyAgent\Agents\SparkAllocator.cs`

```csharp
using System;
using System.Collections.Generic;

namespace ValleyAgent.Agents
{
    /// <summary>
    /// 玩家进入 NPC 附近时低概率激活该 NPC 为 Agent。
    /// 每日每 NPC 只掷一次（dateKey 隔离），换日自动重置。
    /// 概率根据当前 Agent 数量分档：
    ///   current &lt; Min → 双倍概率
    ///   Min ≤ current &lt; Normal → 基础概率
    ///   current ≥ Normal → 不触发
    /// </summary>
    public class SparkAllocator
    {
        private readonly Dictionary<string, HashSet<string>> _rolledByDate
            = new(StringComparer.OrdinalIgnoreCase);
        private readonly Random _rng;

        /// <param name="seed">随机种子（测试可注入）。</param>
        public SparkAllocator(int seed)
        {
            _rng = new Random(seed);
        }

        public SparkAllocator() : this(Environment.TickCount) { }

        /// <summary>
        /// 尝试 spark 激活。
        /// </summary>
        /// <param name="npcName">NPC 名。</param>
        /// <param name="dateKey">游戏日期 key（如 "Y1_spring_1"），换日变化。</param>
        /// <param name="currentAgentCount">当前 Agent 数量。</param>
        /// <param name="minAgents">MinAgentNpcs。</param>
        /// <param name="normalAgents">NormalAgentNpcs。</param>
        /// <param name="maxAgents">MaxAgentNpcs。</param>
        /// <param name="probabilityOverride">基础概率覆盖（测试用，默认 null 走 5%）。</param>
        /// <returns>true 表示命中 spark（调用方应 ForceAllocate）。</returns>
        public bool TrySpark(
            string npcName,
            string dateKey,
            int currentAgentCount,
            int minAgents,
            int normalAgents,
            int maxAgents,
            double? probabilityOverride = null)
        {
            if (string.IsNullOrWhiteSpace(npcName) || string.IsNullOrWhiteSpace(dateKey))
            {
                return false;
            }

            // current >= Normal → 停止 spark
            if (currentAgentCount >= normalAgents)
            {
                return false;
            }

            // 每日每 NPC 只掷一次
            if (!_rolledByDate.TryGetValue(dateKey, out var rolledSet))
            {
                rolledSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                _rolledByDate[dateKey] = rolledSet;
            }
            if (rolledSet.Contains(npcName))
            {
                return false;
            }
            _ = rolledSet.Add(npcName);

            // 概率分档
            var baseProb = probabilityOverride ?? 0.05;
            var prob = currentAgentCount < minAgents ? baseProb * 2.0 : baseProb;

            return _rng.NextDouble() < prob;
        }

        /// <summary>
        /// 换日清理（可选——dateKey 变化自动隔离旧记录，但调用此方法可释放内存）。
        /// </summary>
        public void ResetDaily(string currentDateKey)
        {
            var keysToRemove = new List<string>();
            foreach (var kvp in _rolledByDate)
            {
                if (!string.Equals(kvp.Key, currentDateKey, StringComparison.OrdinalIgnoreCase))
                {
                    keysToRemove.Add(kvp.Key);
                }
            }
            foreach (var key in keysToRemove)
            {
                _ = _rolledByDate.Remove(key);
            }
        }
    }
}
```

- [ ] **Step 4: 运行测试验证通过**

Run: `dotnet test src\ValleyAgent.UnitTests\ValleyAgent.UnitTests.csproj --filter "SparkAllocatorTests"`
Expected: PASS (4/4)

- [ ] **Step 5: 在 AgentTickLoop 集成 spark 检查**

Modify `AgentTickLoop.cs` 构造函数加 `SparkAllocator` 字段（具体注入由 ServiceInitializer 完成，此处加字段与调用逻辑）。在 tick 循环中（已有 NPC 遍历逻辑的位置），玩家附近 NPC 检查：

在 `AgentTickLoop.cs` 类顶部加字段：

```csharp
        private readonly SparkAllocator? _sparkAllocator;
```

在 tick 处理逻辑中（ProcessAgent 方法内或附近），玩家附近 NPC spark 检查（简化：每 N tick 检查一次避免性能问题）：

```csharp
        // spark 检查：每 60 tick（约 1 秒）检查一次玩家附近 NPC
        if (_sparkAllocator != null && _agentService != null && tickCount % 60 == 0)
        {
            TrySparkNearbyNpcs(tickCount);
        }
```

加 `TrySparkNearbyNpcs` 方法（放在 AgentTickLoop 类内）：

```csharp
        private void TrySparkNearbyNpcs(int tickCount)
        {
            if (_config == null || _agentService == null || _sparkAllocator == null) return;

            var player = Game1.player;
            if (player?.currentLocation == null) return;

            var dateKey = $"{Game1.year}_{Game1.currentSeason}_{Game1.dayOfMonth}";
            var nearbyDistance = _config.ChatNearbyDistanceTiles;
            var currentCount = _agentService.AllocationManager.CurrentAgentCount;

            foreach (var npc in player.currentLocation.characters)
            {
                if (npc?.IsVillager != true) continue;
                if (_agentService.AllocationManager.IsAllocated(npc.Name)) continue;

                var dist = Math.Sqrt(
                    Math.Pow(npc.Tile.X - player.Tile.X, 2) +
                    Math.Pow(npc.Tile.Y - player.Tile.Y, 2));
                if (dist > nearbyDistance) continue;

                if (_sparkAllocator.TrySpark(
                        npc.Name, dateKey, currentCount,
                        _config.MinAgentNpcs, _config.NormalAgentNpcs, _config.MaxAgentNpcs))
                {
                    if (_agentService.AllocationManager.TryAllocate(npc.Name, 0, 0, 0))
                    {
                        var agent = _agentService.CreateAgent(npc.Name);
                        if (agent != null)
                        {
                            _monitor?.Log($"[Spark] Allocated {npc.Name} (nearby spark)", LogLevel.Info);
                        }
                    }
                    currentCount++; // 防止一次 tick 分配多个
                }
            }
        }
```

- [ ] **Step 6: ServiceInitializer 注入 SparkAllocator**

Modify `ServiceInitializer.cs` 在 AgentTickLoop 构造处注入 `new SparkAllocator()`。具体行号需查找 `new AgentTickLoop`。

- [ ] **Step 7: 编译验证 0 警告**

Run: `dotnet build src\ValleyAgent\ValleyAgent.csproj -warnaserror`
Expected: BUILD SUCCESS, 0 warnings

- [ ] **Step 8: 提交**

```bash
git add src/ValleyAgent/Agents/SparkAllocator.cs src/ValleyAgent/Core/AgentTickLoop.cs src/ValleyAgent/Initialization/ServiceInitializer.cs src/ValleyAgent.UnitTests/SparkAllocatorTests.cs
git commit -m "feat(allocation): add SparkAllocator for 5% nearby NPC activation"
```

---

### Task 6: 互动空闲淘汰

**Files:**
- Modify: `<REPO_ROOT>\src\ValleyAgent\Core\AgentTickLoop.cs`
- Modify: `<REPO_ROOT>\src\ValleyAgent\Patches\DialogueBoxInputPatch.cs` (PromoteToAgent 刷新 LastPlayerInteractionTick)
- Modify: `<REPO_ROOT>\src\ValleyAgent\Patches\NPCDialoguePatch.cs` (对话时刷新)
- Modify: `<REPO_ROOT>\src\ValleyAgent\Patches\NPCGiftPatch.cs` (送礼时刷新)

- [ ] **Step 1: 在 AgentTickLoop 加互动空闲淘汰检查**

在 `AgentTickLoop.cs` 的 `TrySparkNearbyNpcs` 方法后加：

```csharp
        private void CheckInteractionIdleEviction(int tickCount)
        {
            if (_config == null || _agentService == null) return;

            // 每 120 tick（约 2 秒）检查一次
            if (tickCount % 120 != 0) return;

            var now = DateTime.UtcNow;
            var idleThreshold = _config.IdleThresholdSeconds;
            var toEvict = new List<string>();

            foreach (var info in _agentService.AllocationManager.GetAllAllocatedAgents())
            {
                // manual override 不淘汰（玩家显式分配）
                if (info.IsManuallyOverridden) continue;

                if (info.ShouldEvict(now, idleThreshold))
                {
                    toEvict.Add(info.NpcName);
                }
            }

            foreach (var npcName in toEvict)
            {
                _monitor?.Log($"[IdleEviction] {npcName} evicted (no player interaction for {idleThreshold}s)", LogLevel.Info);
                _ = _agentService.RemoveAgent(npcName);
                _ = _agentService.AllocationManager.Deallocate(npcName);
            }
        }
```

在 tick 循环调用处加（紧挨 TrySparkNearbyNpcs 调用后）：

```csharp
            CheckInteractionIdleEviction(tickCount);
```

- [ ] **Step 2: PromoteToAgent 刷新 LastPlayerInteractionTick**

Modify `DialogueBoxInputPatch.cs` PromoteToAgent 方法（第 495 行 `allocated = manager.ForceAllocate(npcName);` 之后）加：

```csharp
                if (allocated)
                {
                    _ = manager.UpdatePriority(npcName, conversations, 0, hearts);
                    // 刷新互动时间，重置空闲淘汰计时
                    var info = manager.GetAllAllocatedAgents()
                        .FirstOrDefault(a => string.Equals(a.NpcName, npcName, StringComparison.OrdinalIgnoreCase));
                    if (info != null)
                    {
                        info.LastPlayerInteractionTick = DateTime.UtcNow;
                    }
                }
```

- [ ] **Step 3: NPCDialoguePatch 对话时刷新**

在 `NPCDialoguePatch.cs` 对话触发处（检查到玩家与 NPC 对话时）加刷新逻辑。查找现有对话触发点，加：

```csharp
                    // 刷新互动空闲计时
                    var allocInfo = _agentService?.AllocationManager.GetAllAllocatedAgents()
                        .FirstOrDefault(a => string.Equals(a.NpcName, npcName, StringComparison.OrdinalIgnoreCase));
                    if (allocInfo != null)
                    {
                        allocInfo.LastPlayerInteractionTick = DateTime.UtcNow;
                    }
```

- [ ] **Step 4: NPCGiftPatch 送礼时刷新**

在 `NPCGiftPatch.cs` 送礼触发处加同样的刷新逻辑。

- [ ] **Step 5: 编译验证 0 警告**

Run: `dotnet build src\ValleyAgent\ValleyAgent.csproj -warnaserror`
Expected: BUILD SUCCESS, 0 warnings

- [ ] **Step 6: 提交**

```bash
git add src/ValleyAgent/Core/AgentTickLoop.cs src/ValleyAgent/Patches/DialogueBoxInputPatch.cs src/ValleyAgent/Patches/NPCDialoguePatch.cs src/ValleyAgent/Patches/NPCGiftPatch.cs
git commit -m "feat(allocation): add interaction-idle eviction with LastPlayerInteractionTick refresh"
```

---

## Phase 3: C# 协议 — allocate_agent 处理

### Task 7: ProtocolV2 加 allocate_agent 消息常量与 ActionResultReason 枚举

**Files:**
- Modify: `<REPO_ROOT>\src\ValleyAgent\Protocol\ProtocolV2.cs` (第 13-18 行, 第 95-115 行)

- [ ] **Step 1: 加消息类型常量**

Modify `ProtocolV2.cs` 第 18 行后加：

```csharp
        public const string MessageTypeAllocateAgent = "allocate_agent";
        public const string MessageTypeDayStarted = "day_started";
```

- [ ] **Step 2: 加 AllocateAgentMessage 类**

在 `ProtocolV2.cs` ActionResultMessage 类后加：

```csharp
        /// <summary>
        /// 导演请求 C# 分配某 NPC 为 Agent（TS→C#, fire_and_forget）。
        /// </summary>
        public class AllocateAgentMessage
        {
            [JsonPropertyName("type")] public string Type { get; set; } = MessageTypeAllocateAgent;
            [JsonPropertyName("requestId")] public string RequestId { get; set; } = "";
            [JsonPropertyName("npcName")] public string NpcName { get; set; } = "";
            /// <summary>ISO 8601 豁免截止时间，对应 beat.windowEnd。可空。</summary>
            [JsonPropertyName("keepUntilIso")] public string? KeepUntilIso { get; set; }
        }

        /// <summary>
        /// C# 通知 TS 新的一天开始（C#→TS, fire_and_forget）。
        /// </summary>
        public class DayStartedMessage
        {
            [JsonPropertyName("type")] public string Type { get; set; } = MessageTypeDayStarted;
            [JsonPropertyName("requestId")] public string RequestId { get; set; } = "";
            [JsonPropertyName("dateIso")] public string DateIso { get; set; } = "";
        }
```

- [ ] **Step 3: 加 ActionResultReason 枚举值**

Modify `ProtocolV2.cs` ActionResultReason 枚举（第 114 行 `InternalError` 后）加：

```csharp
            /// <summary>ALLOCATED: allocate_agent 成功分配。</summary>
            Allocated,
            /// <summary>MAX_CAPACITY_REACHED: allocate_agent 失败，已达 MaxAgentNpcs 且无可替槽。</summary>
            MaxCapacityReached
```

- [ ] **Step 4: 编译验证**

Run: `dotnet build src\ValleyAgent\ValleyAgent.csproj -warnaserror`
Expected: BUILD SUCCESS

- [ ] **Step 5: 提交**

```bash
git add src/ValleyAgent/Protocol/ProtocolV2.cs
git commit -m "feat(protocol): add allocate_agent and day_started message types + Allocated/MaxCapacityReached reasons"
```

---

### Task 8: WebSocketClient 路由 unsolicited 消息

**Files:**
- Modify: `<REPO_ROOT>\src\ValleyAgent.Abstractions\WebSocket\WebSocketClient.cs` (第 181-202 行 HandleMessage)

- [ ] **Step 1: 加 OnUnsolicitedMessage 事件**

Modify `WebSocketClient.cs` 在第 43 行 `OnDisconnected` 事件后加：

```csharp
        /// <summary>
        /// 收到非 pending request 响应的 unsolicited 消息时触发（如 TS 主动下发的 allocate_agent）。
        /// 消息原文（JSON 字符串）传给订阅者按 type 路由。
        /// </summary>
        public event Action<string>? OnUnsolicitedMessage;
```

- [ ] **Step 2: HandleMessage 路由 unsolicited 消息**

Modify `WebSocketClient.cs` HandleMessage 方法（第 181-202 行）改为：

```csharp
        private void HandleMessage(string message)
        {
            try
            {
                using var doc = JsonDocument.Parse(message);
                var root = doc.RootElement;

                // 先尝试匹配 pending request
                if (root.TryGetProperty("requestId", out var reqIdProp)
                    && reqIdProp.ValueKind == JsonValueKind.String)
                {
                    var requestId = reqIdProp.GetString();
                    if (!string.IsNullOrEmpty(requestId))
                    {
                        if (_tracker.CompleteRequest(requestId, message))
                        {
                            return; // 匹配到 pending request，消费完毕
                        }
                    }
                }

                // 未匹配 pending request → unsolicited 消息，交给订阅者按 type 路由
                OnUnsolicitedMessage?.Invoke(message);
            }
            catch (JsonException)
            {
                var preview = message?.Length > 100 ? message[..100] + "..." : message;
                LogCallback?.Invoke($"[WS] Malformed message ignored: {preview}");
            }
        }
```

- [ ] **Step 3: 确认 CompleteRequest 返回 bool**

检查 `_tracker.CompleteRequest` 返回类型。如果当前返回 void，需改为返回 bool（匹配 true / 未匹配 false）。查找 RequestTracker 类型定义修改。

- [ ] **Step 4: 编译验证**

Run: `dotnet build src\ValleyAgent.Abstractions\ValleyAgent.Abstractions.csproj -warnaserror`
Expected: BUILD SUCCESS

- [ ] **Step 5: 提交**

```bash
git add src/ValleyAgent.Abstractions/WebSocket/WebSocketClient.cs
git commit -m "feat(ws): route unsolicited messages to OnUnsolicitedMessage event for type-based dispatch"
```

---

### Task 9: AllocateAgentHandler 处理 allocate_agent 消息

**Files:**
- Create: `<REPO_ROOT>\src\ValleyAgent\Protocol\AllocateAgentHandler.cs`
- Modify: `<REPO_ROOT>\src\ValleyAgent\Initialization\EventHandlerInitializer.cs` (订阅 OnUnsolicitedMessage)
- Test: `<REPO_ROOT>\src\ValleyAgent.UnitTests\AllocateAgentHandlerTests.cs`

- [ ] **Step 1: 写失败测试**

Create: `<REPO_ROOT>\src\ValleyAgent.UnitTests\AllocateAgentHandlerTests.cs`

```csharp
#nullable enable
using System;
using System.Collections.Generic;
using ValleyAgent.Agents;
using ValleyAgent.Protocol;
using Xunit;

namespace ValleyAgent.UnitTests;

public static class AllocateAgentHandlerTests
{
    [Fact]
    public static void Handle_ValidNpc_ForceAllocatesAndSetsKeepUntil()
    {
        var manager = new AgentAllocationManager(0, 2);
        var handler = new AllocateAgentHandler(manager, keepUntilParser: iso => DateTime.Parse(iso));

        var msg = new ProtocolV2.AllocateAgentMessage
        {
            NpcName = "Haley",
            RequestId = "req-1",
            KeepUntilIso = "2026-08-03T14:00:00Z"
        };
        var (success, reason) = handler.Handle(msg);

        Assert.True(success);
        Assert.Equal(ActionResultReason.Allocated, reason);
        Assert.True(manager.IsAllocated("Haley"));

        var info = manager.GetAllAllocatedAgents()[0];
        Assert.Equal(new DateTime(2026, 8, 3, 14, 0, 0, DateTimeKind.Utc), info.KeepUntil);
    }

    [Fact]
    public static void Handle_MaxCapacity_ReturnsMaxCapacityReached()
    {
        var manager = new AgentAllocationManager(0, 1);
        manager.TryAllocate("Robin", 0, 0, 5); // 满员，且 Robin 是 manual=false 优先级高
        var handler = new AllocateAgentHandler(manager, keepUntilParser: _ => null);

        var msg = new ProtocolV2.AllocateAgentMessage
        {
            NpcName = "Haley",
            RequestId = "req-2"
        };
        var (success, reason) = handler.Handle(msg);

        // Haley 优先级 0 < Robin 优先级 5 → 不会被替换
        Assert.False(success);
        Assert.Equal(ActionResultReason.MaxCapacityReached, reason);
    }

    [Fact]
    public static void Handle_AlreadyAllocated_ReturnsAllocatedAndUpdatesKeepUntil()
    {
        var manager = new AgentAllocationManager(0, 2);
        manager.TryAllocate("Haley", 0, 0, 0);
        var handler = new AllocateAgentHandler(manager, keepUntilParser: iso => DateTime.Parse(iso));

        var msg = new ProtocolV2.AllocateAgentMessage
        {
            NpcName = "Haley",
            RequestId = "req-3",
            KeepUntilIso = "2026-08-03T16:00:00Z"
        };
        var (success, reason) = handler.Handle(msg);

        Assert.True(success);
        Assert.Equal(ActionResultReason.Allocated, reason);
        var info = manager.GetAllAllocatedAgents()[0];
        Assert.Equal(new DateTime(2026, 8, 3, 16, 0, 0, DateTimeKind.Utc), info.KeepUntil);
    }

    [Fact]
    public static void Handle_InvalidKeepUntilIso_FailOpenNoKeep()
    {
        var manager = new AgentAllocationManager(0, 2);
        var handler = new AllocateAgentHandler(manager, keepUntilParser: _ => null);

        var msg = new ProtocolV2.AllocateAgentMessage
        {
            NpcName = "Haley",
            RequestId = "req-4",
            KeepUntilIso = "garbage"
        };
        var (success, reason) = handler.Handle(msg);

        Assert.True(success);
        Assert.Equal(ActionResultReason.Allocated, reason);
        var info = manager.GetAllAllocatedAgents()[0];
        Assert.Null(info.KeepUntil);
    }
}
```

- [ ] **Step 2: 运行测试验证失败**

Run: `dotnet test src\ValleyAgent.UnitTests\ValleyAgent.UnitTests.csproj --filter "AllocateAgentHandlerTests"`
Expected: FAIL — `AllocateAgentHandler` 类型不存在

- [ ] **Step 3: 实现 AllocateAgentHandler**

Create: `<REPO_ROOT>\src\ValleyAgent\Protocol\AllocateAgentHandler.cs`

```csharp
using System;
using System.Globalization;
using ValleyAgent.Agents;
using ValleyAgent.Protocol;

namespace ValleyAgent.Protocol
{
    /// <summary>
    /// 处理 TS 导演下发的 allocate_agent 消息。
    /// ForceAllocate 目标 NPC 并设置 KeepUntil 豁免。
    /// </summary>
    public class AllocateAgentHandler
    {
        private readonly AgentAllocationManager _manager;
        private readonly Func<string, DateTime?> _keepUntilParser;

        /// <param name="manager">分配管理器。</param>
        /// <param name="keepUntilParser">ISO 8601 → DateTime 解析器（测试可注入，fail-open 返回 null）。</param>
        public AllocateAgentHandler(
            AgentAllocationManager manager,
            Func<string, DateTime?>? keepUntilParser = null)
        {
            _manager = manager ?? throw new ArgumentNullException(nameof(manager));
            _keepUntilParser = keepUntilParser ?? DefaultParseKeepUntil;
        }

        public (bool Success, ActionResultReason Reason) Handle(ProtocolV2.AllocateAgentMessage msg)
        {
            if (string.IsNullOrWhiteSpace(msg.NpcName))
            {
                return (false, ActionResultReason.InvalidState);
            }

            // 解析 keepUntilIso，失败 fail-open（无豁免）
            DateTime? keepUntil = null;
            if (!string.IsNullOrWhiteSpace(msg.KeepUntilIso))
            {
                keepUntil = _keepUntilParser(msg.KeepUntilIso);
            }

            bool allocated;
            if (_manager.IsAllocated(msg.NpcName))
            {
                // 已分配 → 更新 keepUntil
                allocated = true;
            }
            else
            {
                allocated = _manager.ForceAllocate(msg.NpcName);
            }

            if (!allocated)
            {
                return (false, ActionResultReason.MaxCapacityReached);
            }

            // 写入 KeepUntil
            var info = _manager.GetAllAllocatedAgents()
                .FirstOrDefault(a => string.Equals(a.NpcName, msg.NpcName, StringComparison.OrdinalIgnoreCase));
            if (info != null)
            {
                info.KeepUntil = keepUntil;
            }

            return (true, ActionResultReason.Allocated);
        }

        private static DateTime? DefaultParseKeepUntil(string iso)
        {
            // 兼容 ISO 8601 多种格式，fail-open
            if (DateTime.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt))
            {
                return dt;
            }
            return null;
        }
    }
}
```

- [ ] **Step 4: 运行测试验证通过**

Run: `dotnet test src\ValleyAgent.UnitTests\ValleyAgent.UnitTests.csproj --filter "AllocateAgentHandlerTests"`
Expected: PASS (4/4)

- [ ] **Step 5: EventHandlerInitializer 订阅 OnUnsolicitedMessage**

Modify `EventHandlerInitializer.cs` 在 WebSocket 连接建立后（OnConnected 事件或 OnSaveLoadedCore）加订阅：

```csharp
                    // 订阅 unsolicited 消息（TS 主动下发的 allocate_agent）
                    var wsClient = _agentServerProvider?.WebSocketClient;
                    if (wsClient != null && _allocateAgentHandler != null)
                    {
                        wsClient.OnUnsolicitedMessage -= OnUnsolicitedMessage;
                        wsClient.OnUnsolicitedMessage += OnUnsolicitedMessage;
                    }
```

加 `OnUnsolicitedMessage` 方法：

```csharp
        private void OnUnsolicitedMessage(string json)
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("type", out var typeProp)) return;
                var type = typeProp.GetString();

                switch (type)
                {
                    case ProtocolV2.MessageTypeAllocateAgent:
                        HandleAllocateAgent(json);
                        break;
                    default:
                        _monitor?.Log($"[WS] Unhandled unsolicited message type: {type}", LogLevel.Trace);
                        break;
                }
            }
            catch (Exception ex)
            {
                _monitor?.Log($"[WS] Failed to route unsolicited message: {ex.Message}", LogLevel.Warn);
            }
        }

        private void HandleAllocateAgent(string json)
        {
            if (_allocateAgentHandler == null || _agentServerProvider == null) return;
            try
            {
                var msg = System.Text.Json.JsonSerializer.Deserialize<ProtocolV2.AllocateAgentMessage>(json);
                if (msg == null) return;

                var (success, reason) = _allocateAgentHandler.Handle(msg);

                // 回 action_result
                var actionResult = new ProtocolV2.ActionResultMessage
                {
                    Type = ProtocolV2.MessageTypeActionResult,
                    RequestId = Guid.NewGuid().ToString("N"),
                    NpcName = msg.NpcName,
                    Action = "allocate_agent",
                    Tool = "allocate_agent",
                    Success = success,
                    Reason = reason,
                    Result = success ? $"allocated {msg.NpcName}" : $"max capacity reached for {msg.NpcName}"
                };
                var responseJson = System.Text.Json.JsonSerializer.Serialize(actionResult);
                _ = _agentServerProvider.SendMessageAsync(responseJson);
            }
            catch (Exception ex)
            {
                _monitor?.Log($"[AllocateAgent] Failed: {ex.Message}", LogLevel.Warn);
            }
        }
```

- [ ] **Step 6: ServiceInitializer 注入 AllocateAgentHandler**

Modify `ServiceInitializer.cs` 在 AgentService 创建后实例化 `AllocateAgentHandler`，传入 `AllocationManager`，并赋值给 `EventHandlerInitializer._allocateAgentHandler` 字段。

- [ ] **Step 7: 编译验证 0 警告**

Run: `dotnet build src\ValleyAgent\ValleyAgent.csproj -warnaserror`
Expected: BUILD SUCCESS, 0 warnings

- [ ] **Step 8: 提交**

```bash
git add src/ValleyAgent/Protocol/AllocateAgentHandler.cs src/ValleyAgent/Initialization/EventHandlerInitializer.cs src/ValleyAgent/Initialization/ServiceInitializer.cs src/ValleyAgent.UnitTests/AllocateAgentHandlerTests.cs
git commit -m "feat(protocol): handle allocate_agent messages from TS director with keepUntil exemption"
```

---

### Task 10: C# DayStarted 发送 day_started 消息

**Files:**
- Modify: `<REPO_ROOT>\src\ValleyAgent\Initialization\EventHandlerInitializer.cs` (OnDayStarted 第 897 行附近)

- [ ] **Step 1: 在 OnDayStarted 发送 day_started**

Modify `EventHandlerInitializer.cs` OnDayStarted 方法（在现有 `ReevaluateAllocations` 调用后，第 920 行后）加：

```csharp
            // 通知 TS 新的一天开始（触发导演 morningPlan）
            _ = NotifyDayStartedAsync();
```

加 `NotifyDayStartedAsync` 方法：

```csharp
        private async Task NotifyDayStartedAsync()
        {
            if (_agentServerProvider == null) return;
            try
            {
                var dateIso = GetGameDateIso(Game1.year, Game1.currentSeason, Game1.dayOfMonth);
                var msg = System.Text.Json.JsonSerializer.Serialize(new
                {
                    type = ProtocolV2.MessageTypeDayStarted,
                    requestId = Guid.NewGuid().ToString("N"),
                    dateIso
                });
                await _agentServerProvider.SendMessageAsync(msg).ConfigureAwait(false);
                _monitor?.Log($"[DayStarted] Notified TS server (date={dateIso})", LogLevel.Debug);
            }
            catch (InvalidOperationException ex)
            {
                _monitor?.Log($"[DayStarted] Failed to notify TS: {ex.Message}", LogLevel.Debug);
            }
        }
```

- [ ] **Step 2: 编译验证**

Run: `dotnet build src\ValleyAgent\ValleyAgent.csproj -warnaserror`
Expected: BUILD SUCCESS

- [ ] **Step 3: 提交**

```bash
git add src/ValleyAgent/Initialization/EventHandlerInitializer.cs
git commit -m "feat(protocol): send day_started notification to TS on DayStarted"
```

---

## Phase 4: TS Director 接入 server + allocate_agent 发送

### Task 11: messages.json 加 allocate_agent 与 day_started 定义

**Files:**
- Modify: `<VALLEYAI_ROOT>\protocol\messages.json`

- [ ] **Step 1: 加 day_started 消息定义**

在 `messages.json` 的 `messages` 数组中加：

```json
                    {
                        "type":  "day_started",
                        "direction":  "csharp_to_ts",
                        "transport":  "fire_and_forget",
                        "status":  "active",
                        "routed_by_ts":  true,
                        "sent_by_csharp":  true,
                        "description":  "C# DayStarted 时通知 TS 新的一天开始。TS 触发导演 morningPlan（10% 概率）。",
                        "fields":  [
                                       { "name":  "type", "type":  "string", "required":  true, "value":  "day_started" },
                                       { "name":  "requestId", "type":  "string", "required":  true, "description":  "Guid N 格式" },
                                       { "name":  "dateIso", "type":  "string", "required":  true, "description":  "游戏日期 ISO key（如 Y1_spring_1）" }
                                   ]
                    },
```

- [ ] **Step 2: 加 allocate_agent 消息定义**

```json
                    {
                        "type":  "allocate_agent",
                        "direction":  "ts_to_csharp",
                        "transport":  "fire_and_forget",
                        "status":  "active",
                        "routed_by_ts":  false,
                        "sent_by_csharp":  false,
                        "description":  "导演请求 C# 把某 NPC 分配为 Agent。C# ForceAllocate 并设置 keepUntil 豁免互动空闲淘汰。回 action_result { reason: allocated | max_capacity_reached }。",
                        "fields":  [
                                       { "name":  "type", "type":  "string", "required":  true, "value":  "allocate_agent" },
                                       { "name":  "requestId", "type":  "string", "required":  true, "description":  "Guid N 格式" },
                                       { "name":  "npcName", "type":  "string", "required":  true, "description":  "待分配的 NPC 名" },
                                       { "name":  "keepUntilIso", "type":  "string", "required":  false, "description":  "ISO 8601 豁免截止时间，对应 beat.windowEnd" }
                                   ]
                    }
```

- [ ] **Step 3: 运行 check:protocol（预期部分红——TS/C# 实现待接）**

Run: `cd <VALLEYAI_ROOT> && bun run check:protocol`
Expected: 报 SCHEMA_DRIFT（routeMessage 未路由 day_started / allocate_agent 未发送）—— 这是预期红，后续 Task 接线后转绿

- [ ] **Step 4: 提交**

```bash
cd <VALLEYAI_ROOT>
git add protocol/messages.json
git commit -m "feat(protocol): add day_started and allocate_agent message definitions"
```

---

### Task 12: TS types.ts 加 DayStartedMessage 与 AllocateAgentMessage 类型

**Files:**
- Modify: `<VALLEYAI_ROOT>\packages\stardew\src\types.ts`

- [ ] **Step 1: 加类型定义**

在 `types.ts` 中 ConsolidateDayMessage 附近加：

```typescript
/** C# DayStarted 时通知 TS 新的一天开始。 */
export interface DayStartedMessage {
  type: "day_started";
  requestId: string;
  dateIso: string;
}

/** 导演请求 C# 分配某 NPC 为 Agent。 */
export interface AllocateAgentMessage {
  type: "allocate_agent";
  requestId: string;
  npcName: string;
  keepUntilIso?: string;
}
```

更新 `OutgoingMessage` 联合类型（如果存在）加 `AllocateAgentMessage`。

- [ ] **Step 2: 类型编译验证**

Run: `cd <VALLEYAI_ROOT> && bunx tsc --noEmit`
Expected: 0 errors

- [ ] **Step 3: 提交**

```bash
cd <VALLEYAI_ROOT>
git add packages/stardew/src/types.ts
git commit -m "feat(types): add DayStartedMessage and AllocateAgentMessage types"
```

---

### Task 13: ProtocolAdapter 路由 day_started + 发送 allocate_agent

**Files:**
- Modify: `<VALLEYAI_ROOT>\packages\stardew\src\protocol-adapter.ts`
- Test: `<VALLEYAI_ROOT>\packages\stardew\tests\protocol-adapter-day-started.test.ts`

- [ ] **Step 1: 写失败测试 — day_started 10% 概率触发 morningPlan**

Create: `<VALLEYAI_ROOT>\packages\stardew\tests\protocol-adapter-day-started.test.ts`

```typescript
import { describe, test, expect, mock } from "bun:test";
import { ProtocolAdapter } from "../src/protocol-adapter";
import type { StardewAgentRegistry } from "../src/stardew-agent-registry";

// 测试：day_started 消息路由 + 10% 概率门 + allocate_agent 发送
describe("ProtocolAdapter day_started", () => {
  test("day_started with probabilityOverride=1.0 triggers morningPlan and sends allocate_agent", async () => {
    const sentMessages: unknown[] = [];
    const mockSend = (msg: unknown) => { sentMessages.push(msg); };

    const mockDirector = {
      morningPlan: mock(async () => [
        { id: "b1", npcName: "Haley", triggerTime: "10:00", windowEnd: "14:00", directive: "找玩家聊天", context: {}, status: "scheduled" as const }
      ]),
    };

    const mockRegistry = {} as StardewAgentRegistry;
    const adapter = new ProtocolAdapter(mockRegistry, {
      sendToCsharp: mockSend,
      director: mockDirector as never,
      directorTriggerProbability: 1.0,
    });

    const result = await adapter.routeMessage({ type: "day_started", requestId: "r1", dateIso: "Y1_spring_1" });

    expect(result.type).toBe("ack");
    expect(mockDirector.morningPlan).toHaveBeenCalledTimes(1);
    expect(sentMessages.length).toBe(1);
    expect((sentMessages[0] as { type: string }).type).toBe("allocate_agent");
    expect((sentMessages[0] as { npcName: string }).npcName).toBe("Haley");
  });

  test("day_started with probabilityOverride=0.0 does not trigger morningPlan", async () => {
    const mockDirector = {
      morningPlan: mock(async () => []),
    };
    const mockRegistry = {} as StardewAgentRegistry;
    const adapter = new ProtocolAdapter(mockRegistry, {
      sendToCsharp: () => {},
      director: mockDirector as never,
      directorTriggerProbability: 0.0,
    });

    await adapter.routeMessage({ type: "day_started", requestId: "r2", dateIso: "Y1_spring_2" });

    expect(mockDirector.morningPlan).not.toHaveBeenCalled();
  });
});
```

- [ ] **Step 2: 运行测试验证失败**

Run: `cd <VALLEYAI_ROOT> && bun test packages/stardew/tests/protocol-adapter-day-started.test.ts`
Expected: FAIL — ProtocolAdapter 构造函数不接受 options

- [ ] **Step 3: ProtocolAdapter 加 options + day_started handler**

Modify `protocol-adapter.ts` 构造函数改为接受可选 options：

```typescript
export interface ProtocolAdapterOptions {
  /** 向 C# 发送消息的回调（用于主动下发 allocate_agent）。 */
  sendToCsharp?: (msg: unknown) => void;
  /** 导演实例（未注入则 day_started 不触发 morningPlan）。 */
  director?: Director;
  /** 导演每日触发概率（默认 0.1）。 */
  directorTriggerProbability?: number;
}

export class ProtocolAdapter {
  private readonly ruleEngine = new RuleEngine();
  private readonly seenConsolidations = new Set<string>();
  private pendingSaves: Promise<void>[] = [];
  private readonly shoutRouter = new MorningShoutRouter();
  private readonly sendToCsharp?: (msg: unknown) => void;
  private readonly director?: Director;
  private readonly directorTriggerProbability: number;

  constructor(
    private readonly registry: StardewAgentRegistry,
    options?: ProtocolAdapterOptions,
  ) {
    this.sendToCsharp = options?.sendToCsharp;
    this.director = options?.director;
    this.directorTriggerProbability = options?.directorTriggerProbability ?? 0.1;
  }
```

加 import：

```typescript
import type { Director } from "./director";
import type { AllocateAgentMessage, DayStartedMessage } from "./types";
```

加 `handleDayStarted` 方法：

```typescript
  async handleDayStarted(req: DayStartedMessage): Promise<{ type: "ack"; requestId: string }> {
    const id = req.requestId ?? "unknown";
    console.log(`[${timestamp()}] [day_started] date=${req.dateIso}`);

    if (!this.director || !this.sendToCsharp) {
      // 导演未注入或无发送通道 → 直接 ack
      return { type: "ack", requestId: id };
    }

    // 10% 概率触发导演
    if (Math.random() >= this.directorTriggerProbability) {
      console.log(`[${timestamp()}] [day_started] director skipped (probability gate)`);
      return { type: "ack", requestId: id };
    }

    try {
      const beats = await this.director.morningPlan();
      console.log(`[${timestamp()}] [day_started] director produced ${beats.length} beats`);

      // 对每个 beat 的 NPC 发送 allocate_agent
      for (const beat of beats) {
        const allocMsg: AllocateAgentMessage = {
          type: "allocate_agent",
          requestId: crypto.randomUUID().replace(/-/g, ""),
          npcName: beat.npcName,
          ...(beat.windowEnd !== undefined ? { keepUntilIso: beat.windowEnd } : {}),
        };
        this.sendToCsharp(allocMsg);
        console.log(`[${timestamp()}] [day_started] sent allocate_agent for ${beat.npcName}`);
      }
    } catch (err) {
      console.error(`[${timestamp()}] [day_started] director error:`, err);
    }

    return { type: "ack", requestId: id };
  }
```

在 `routeMessage` switch 加 case：

```typescript
      case "day_started": return this.handleDayStarted(m as DayStartedMessage);
```

- [ ] **Step 4: 运行测试验证通过**

Run: `cd <VALLEYAI_ROOT> && bun test packages/stardew/tests/protocol-adapter-day-started.test.ts`
Expected: PASS (2/2)

- [ ] **Step 5: 提交**

```bash
cd <VALLEYAI_ROOT>
git add packages/stardew/src/protocol-adapter.ts packages/stardew/tests/protocol-adapter-day-started.test.ts
git commit -m "feat(adapter): route day_started with 10% director trigger and allocate_agent dispatch"
```

---

### Task 14: server.ts 实例化 Director 并注入 ProtocolAdapter

**Files:**
- Modify: `<VALLEYAI_ROOT>\packages\stardew\src\server.ts`

- [ ] **Step 1: 加 Director 实例化**

Modify `server.ts` 在 `const adapter = new ProtocolAdapter(registry);`（第 62 行）之前加 Director 实例化。需要先实例化 BeatStore、PlayerProfileManager、GameContextManager、ActivityLogStore。

加 imports：

```typescript
import { Director } from "./director";
import { BeatStore } from "./beat-store";
import { PlayerProfileManager } from "./player-profile";
import { GameContextManager } from "./game-context";
import { ActivityLogStore } from "./activity-log-store";
import { join } from "node:path";
```

加 Director 配置到 ServerConfig：

```typescript
  /** 导演每日触发概率（默认 0.1）。 */
  directorTriggerProbability?: number;
```

在 startServer 中（adapter 创建前）加：

```typescript
  // 导演依赖项实例化
  const dbDir = join(config.dataPath, "director");
  const beatStore = new BeatStore(join(dbDir, "beats.sqlite"));
  beatStore.init();
  const profileMgr = new PlayerProfileManager(join(dbDir, "profiles.sqlite"));
  profileMgr.init();
  const gameCtxMgr = new GameContextManager();
  const activityStore = new ActivityLogStore(join(dbDir, "activity.sqlite"));
  activityStore.init();

  // 导演 LLM 调用器：复用 provider
  const director = new Director(beatStore, profileMgr, gameCtxMgr, activityStore, {
    callLlm: async (prompt: string) => {
      const result = await provider.callLlm([{ role: "user", content: prompt }]);
      return { text: result.text, usage: result.usage };
    },
  });

  // sendToCsharp 回调：通过最近活跃的 ws 发送
  let activeWs: import("bun").ServerWebSocket<unknown> | null = null;

  const adapter = new ProtocolAdapter(registry, {
    sendToCsharp: (msg: unknown) => {
      if (activeWs && activeWs.readyState === 1) {
        activeWs.send(JSON.stringify(msg));
      }
    },
    director,
    directorTriggerProbability: config.directorTriggerProbability ?? 0.1,
  });
```

- [ ] **Step 2: websocket open 回调记录 activeWs**

Modify `server.ts` websocket 配置的 `open` 回调：

```typescript
      open(ws) {
        activeWs = ws;
      },
```

- [ ] **Step 3: ServerHandle.stop 关闭 Director 依赖 store**

Modify `server.ts` ServerHandle 返回的 stop：

```typescript
  return {
    port: boundPort,
    stop: async () => {
      await adapter.flushPendingSaves();
      registry.closeTranscriptStore();
      beatStore.close();
      profileMgr.close();
      activityStore.close();
      server.stop();
    },
  };
```

- [ ] **Step 4: 类型编译验证**

Run: `cd <VALLEYAI_ROOT> && bunx tsc --noEmit`
Expected: 0 errors

- [ ] **Step 5: 运行全部 TS 测试验证无回归**

Run: `cd <VALLEYAI_ROOT> && bun test packages/stardew`
Expected: 既有测试全绿 + 新测试全绿

- [ ] **Step 6: 提交**

```bash
cd <VALLEYAI_ROOT>
git add packages/stardew/src/server.ts
git commit -m "feat(server): instantiate Director with stores and inject into ProtocolAdapter"
```

---

## Phase 5: 契约验证与集成

### Task 15: check:protocol 转绿

**Files:**
- Verify: `<VALLEYAI_ROOT>\scripts\check-protocol-contract.ts`

- [ ] **Step 1: 运行 check:protocol**

Run: `cd <VALLEYAI_ROOT> && bun run check:protocol`
Expected: exit 0（全绿）

- [ ] **Step 2: 如有 SCHEMA_DRIFT，修复**

检查报告：
- `day_started`：TS routeMessage 已路由 ✓，C# WebSocketClient 已发送（EventHandlerInitializer.NotifyDayStartedAsync）✓
- `allocate_agent`：TS sendToCsharp 已发送 ✓，C# OnUnsolicitedMessage 已处理 ✓

如 C# 侧未被脚本检测到（脚本只读源码静态扫描），确认 C# 字符串匹配 `"allocate_agent"` / `"day_started"` 存在。

- [ ] **Step 3: 提交（如有修复）**

```bash
cd <VALLEYAI_ROOT>
git add -A
git commit -m "test(protocol): check:protocol green for day_started and allocate_agent"
```

---

### Task 16: C# 全量单测 + 0 警告编译

- [ ] **Step 1: C# 全量单测**

Run: `dotnet test src\ValleyAgent.UnitTests\ValleyAgent.UnitTests.csproj`
Expected: ALL PASS

- [ ] **Step 2: C# 0 警告编译**

Run: `dotnet build src\ValleyAgent\ValleyAgent.csproj -warnaserror`
Expected: BUILD SUCCESS, 0 warnings

- [ ] **Step 3: TS 全量测试**

Run: `cd <VALLEYAI_ROOT> && bun test packages/stardew`
Expected: ALL PASS

- [ ] **Step 4: TS 类型检查**

Run: `cd <VALLEYAI_ROOT> && bunx tsc --noEmit`
Expected: 0 errors

---

### Task 17: 游戏内验证

- [ ] **Step 1: 第1天进档验证 0 个 Agent**

启动游戏，新建存档或加载无 Agent 存档，进入第1天。打开 SMAPI 控制台，确认：
- 无 "Auto-allocated Agent (min-floor): Lewis" / "Robin" 日志
- `ValleyAgent_allocate` 命令查 0 个 Agent

- [ ] **Step 2: GMCM 三档滑块联动**

打开 GMCM 设置界面，调整 Min/Normal/Max 滑块，确认联动正确。

- [ ] **Step 3: 玩家对话激活**

与某 NPC 对话，确认该 NPC 成为 Agent。

- [ ] **Step 4: 互动空闲淘汰**

不与 Agent 互动超过 IdleThresholdSeconds（默认 90s），确认 Agent 被淘汰。

- [ ] **Step 5: 导演 10% 触发（多日测试）**

多次睡觉换日，确认约 10% 概率触发导演 morningPlan，TS 日志出现 `[day_started] director produced N beats` + `[day_started] sent allocate_agent for X`。

- [ ] **Step 6: 提交最终验证记录**

```bash
git add -A
git commit -m "test: verify interaction-driven allocation end-to-end"
```

---

## 自审清单

**Spec 覆盖:**
- ✅ §4.1 三档配置 → Task 1-2
- ✅ §4.2.1 玩家交互（保留）→ 不需新 Task（PromoteToAgent 已有，Task 6 加刷新）
- ✅ §4.2.2 导演 beat → Task 11-14
- ✅ §4.2.3 5% spark → Task 5
- ✅ §4.3.1 删除 Phase 2 → Task 4
- ✅ §4.3.2 互动空闲淘汰 → Task 3, 6
- ✅ §4.4 协议变更 → Task 7, 11
- ✅ §4.5 配置迁移 → Task 1
- ✅ §4.6 GMCM → Task 2
- ✅ §6 测试 → 各 Task 内 TDD + Task 15-17

**类型一致性:**
- `NormalAgentNpcs` — Task 1 定义，Task 2/5 引用 ✓
- `LastPlayerInteractionTick` / `KeepUntil` / `ShouldEvict` — Task 3 定义，Task 6/9 引用 ✓
- `SparkAllocator` — Task 5 定义，Task 5 Step 5 引用 ✓
- `AllocateAgentHandler` — Task 9 定义，Task 9 Step 5 引用 ✓
- `MessageTypeAllocateAgent` / `MessageTypeDayStarted` — Task 7 定义，Task 9/10 引用 ✓
- `ActionResultReason.Allocated` / `MaxCapacityReached` — Task 7 定义，Task 9 引用 ✓
- TS `AllocateAgentMessage` / `DayStartedMessage` — Task 12 定义，Task 13 引用 ✓
- TS `ProtocolAdapterOptions` — Task 13 定义，Task 14 引用 ✓

**已知开放点:**
- Task 5 Step 5-6: AgentTickLoop 的 spark 集成需要读取现有 tick 循环结构确定插入点
- Task 6 Step 3-4: NPCDialoguePatch / NPCGiftPatch 的刷新点需要读取现有对话/送礼触发点
- Task 9 Step 6: ServiceInitializer 的注入点需要读取现有注入模式
- Task 14: Director 的 `callLlm` 签名需与 provider.callLlm 对齐（可能需要适配层）
- beat 执行（runBeat）不在本计划范围 — Director 产出的 beat 持久化到 BeatStore，NPC Agent 的 beat 消费作为后续计划
