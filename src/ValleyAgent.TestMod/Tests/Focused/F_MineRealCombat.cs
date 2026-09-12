#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Monsters;
using ValleyAgent.Utils;

namespace ValleyAgent.TestMod.Tests.Focused;

/// <summary>
///     Focused test: 真实矿洞环境下的挖矿与战斗执行验证（2026-08-03 测试加固 C 任务）。
///     现有覆盖的缺口：E14 只测 MineHandler.ScanEnvironment 的位置盲区（不挖矿），
///     F2 只在农场测战斗，VIS006 只截图 —— 没有"真把 NPC 和玩家放进矿洞、
///     生成石头和怪物、验证 MINE/FIGHT 状态真实执行"的测试。本测试补齐：
///     Phase 时间线（TimeoutTicks=1800）：
///     Setup      玩家+NPC 进 Mine（SafeWarp 落点 BFS 验证 + EventCleanup 清 Marlon 剧情）
///     60         生成 3 块石头，强制 MINE 状态
///     700        断言：石头被真正挖掉（objects 移除）
///     720        生成 2 只史莱姆，强制 FIGHT 状态，记录怪物总血量
///     1500       断言：怪物受伤或死亡（血量下降/被移除）
///     1600       断言：NPC 状态正确退出（无目标后回 IDLE）
///     300        断言：Marlon 剧情事件已被清理（eventUp=false）
///     事件处理说明：玩家首进 Mine 触发 Marlon 剧情（eventUp），会阻断 warp Position
///     设置与 NPC 行为。Setup 用 SafeWarp（warp 前后各清一次）+ 180 tick 抑制窗口
///     （Update 内逐 tick EventCleanup.ClearActiveEvents），并 SuppressEventGuards
///     防止 GameEventGuard 拦截导航。清理只置空字段，不碰 onEventFinished 的 NRE 坑。
/// </summary>
public class F_MineRealCombat : V3TestBase
{
    private readonly List<Monster> _slimes = new();
    private readonly List<Vector2> _stoneTiles = new();
    private IValleyAgentApi? _api;
    private int _eventSuppressTicksLeft;
    private int _initialSlimeTotalHealth = -1;
    private NPC? _npc;

    public F_MineRealCombat(IModHelper helper, IMonitor monitor) : base(helper, monitor)
    {
    }

    public override string TestName
    {
        get => "F_MineRealCombat";
    }

    public override int TimeoutTicks
    {
        get => 1800;
    }

    public override TestGroup Group
    {
        get => TestGroup.Fuzzy;
    }

    public override void Setup()
    {
        Monitor.Log($"=== {TestName} Setup ===", LogLevel.Info);

        // 抑制 LLM 决策，避免随机决策覆盖 MINE/FIGHT 状态
        DebugFlags.SuppressDecisions = true;

        // 同 F_FollowCrossMap：必须用容器构建的真实 API，fallback API 会静默失败
        _api = ValleyAgent.ModEntry.Instance?.API ?? ModEntry.API;
        if (_api == null)
        {
            Skip("API null");
            return;
        }

        // 清前序测试的事件残留 + 强制事件守卫 Override（测试期间导航不被事件拦截）
        _ = EventCleanup.ClearActiveEvents(Monitor, $"{TestName} setup");
        EventCleanup.SuppressEventGuards();

        // 玩家进 Mine：落点经 WarpTargetGuard BFS 验证（从 Mountain→Mine 入口可达），
        // SafeWarp 内部在 warp 前后各清一次事件（Marlon 剧情）
        var landing = SafeWarp.Farmer(Monitor, "Mine", 12, 11, TestName);
        Monitor.Log($"[{TestName}] Player warped to Mine @({landing.X},{landing.Y})", LogLevel.Info);

        // Marlon 剧情可能在 warp 后数 tick 才拉起：武装 180 tick 抑制窗口
        _eventSuppressTicksLeft = 180;

        // 保护玩家不被史莱姆误伤打死（同 F2）
        Game1.player.maxHealth = 99999;
        Game1.player.health = 99999;

        _npc = Game1.getCharacterFromName("Haley");
        if (_npc == null)
        {
            Skip("Haley not found");
            return;
        }

        _ = _api.TryAllocateAgent("Haley");
        _ = _api.TryRevive("Haley");

        // NPC 同步进 Mine（warpCharacter 是同步的），落在玩家附近
        var mine = Game1.getLocationFromName("Mine");
        if (mine == null)
        {
            Skip("Mine location not found");
            return;
        }

        Game1.warpCharacter(_npc, mine, Game1.player.Tile);
        _npc.followSchedule = false;
        _npc.ignoreScheduleToday = true;
        _npc.Halt();
        _npc.controller = null;

        Monitor.Log(
            $"[{TestName}] NPC at {_npc.currentLocation?.NameOrUniqueName} {_npc.Tile}, player at {Game1.player.Tile}",
            LogLevel.Info);
    }

    public override bool Update()
    {
        if (_npc == null || _api == null)
        {
            return true;
        }

        var tick = CurrentTick;

        // Marlon 剧情抑制窗口：逐 tick 清掉 warp 后拉起的事件
        if (_eventSuppressTicksLeft > 0)
        {
            _eventSuppressTicksLeft--;
            _ = EventCleanup.ClearActiveEvents(Monitor, $"{TestName} post-warp suppression");
        }

        // ── tick 300：剧情事件已清理（B 任务验证点） ──
        if (tick == 300)
        {
            Assert("no_event_lingering_after_mine_warp", !EventCleanup.IsEventActive,
                $"eventUp={Game1.eventUp} currentEvent={Game1.currentLocation?.currentEvent != null}");
        }

        // ── Phase 1 (tick 60)：生成石头，强制 MINE ──
        if (tick == 60)
        {
            var spawned = TestScenes.SpawnMineSceneNearNpc(Helper, Monitor, _npc.TilePoint);
            _stoneTiles.AddRange(spawned);
            Monitor.Log($"[{TestName}] Spawned {_stoneTiles.Count} stones near NPC in Mine", LogLevel.Info);

            var ok = _api.TrySetAgentState("Haley", "MINE");
            Monitor.Log($"[{TestName}] TrySetAgentState(MINE) = {ok}", LogLevel.Info);
            Assert("mine_state_entered", ok, $"TrySetAgentState(MINE)={ok}");
        }

        // ── tick 700：断言石头被真正挖掉 ──
        if (tick == 700)
        {
            var mine = _npc.currentLocation;
            var remaining = mine == null
                ? _stoneTiles.Count
                : _stoneTiles.Count(t => mine.objects.ContainsKey(t));
            Assert("mine_stones_removed", _stoneTiles.Count > 0 && remaining == 0,
                $"spawned={_stoneTiles.Count} remaining={remaining}");
            Monitor.Log(
                $"[{TestName}] After MINE phase: {_stoneTiles.Count - remaining}/{_stoneTiles.Count} stones mined, " +
                $"state={_api.GetAgentState("Haley")}", LogLevel.Info);
        }

        // ── Phase 2 (tick 720)：生成史莱姆，强制 FIGHT ──
        if (tick == 720)
        {
            var mine = _npc.currentLocation;
            if (mine == null)
            {
                Assert("fight_phase_location_available", false, "NPC currentLocation null at fight phase");
                return true;
            }

            for (var i = 0; i < 2; i++)
            {
                var desiredTile = _npc.Tile + new Vector2(3 + i, 0);
                var spawnTile = TestScenes.FindWalkableTileNear(mine, desiredTile, _npc.Tile, 8);
                var slime = new GreenSlime(spawnTile * 64f)
                {
                    Name = $"MineTestSlime_{i}"
                };
                mine.characters.Add(slime);
                _slimes.Add(slime);
            }

            _initialSlimeTotalHealth = _slimes.Sum(s => s.Health);
            Monitor.Log($"[{TestName}] Spawned {_slimes.Count} slimes (totalHP={_initialSlimeTotalHealth})",
                LogLevel.Info);

            var ok = _api.TrySetAgentState("Haley", "FIGHT");
            Monitor.Log($"[{TestName}] TrySetAgentState(FIGHT) = {ok}", LogLevel.Info);
            Assert("fight_state_entered", ok, $"TrySetAgentState(FIGHT)={ok}");
        }

        // ── tick 1500：断言怪物受伤或死亡 ──
        if (tick == 1500)
        {
            var mine = _npc.currentLocation;
            var aliveHealth = _slimes
                .Where(s => mine != null && mine.characters.Contains(s))
                .Sum(s => Math.Max(s.Health, 0));
            var damagedOrKilled = _initialSlimeTotalHealth > 0 && aliveHealth < _initialSlimeTotalHealth;
            Assert("fight_monster_damaged_or_killed", damagedOrKilled,
                $"initialHP={_initialSlimeTotalHealth} currentAliveHP={aliveHealth}");
        }

        // ── tick 1600：断言 NPC 状态正确退出（石头/怪物清空后回 IDLE） ──
        if (tick == 1600)
        {
            var state = _api.GetAgentState("Haley");
            Assert("npc_state_exits_to_idle", string.Equals(state, "IDLE", StringComparison.OrdinalIgnoreCase),
                $"state={state} (no targets left, handler should ForceTransition(IDLE))");
        }

        return tick >= TimeoutTicks;
    }

    public override void Teardown()
    {
        // 清理残留怪物/石头，避免污染后续测试
        var mine = Game1.getLocationFromName("Mine");
        if (mine != null)
        {
            foreach (var slime in _slimes)
            {
                if (mine.characters.Contains(slime))
                {
                    _ = mine.characters.Remove(slime);
                }
            }

            foreach (var tile in _stoneTiles)
            {
                if (mine.objects.ContainsKey(tile))
                {
                    _ = mine.objects.Remove(tile);
                }
            }
        }

        // 史莱姆可能打死 Haley：复活保证测试隔离（同 F2 模式）
        var reviveApi = ValleyAgent.ModEntry.Instance?.API ?? ModEntry.API;
        if (Game1.getCharacterFromName("Haley") == null || (reviveApi?.GetNpcHealth("Haley") ?? 100) <= 0)
        {
            _ = reviveApi?.TryRevive("Haley");
        }

        TestScenes.ClearAll(Helper, Monitor);

        // 恢复决策与事件守卫
        DebugFlags.SuppressDecisions = false;
        EventCleanup.ResetGuardOverrides();

        Monitor.Log($"[{TestName}] Teardown complete.", LogLevel.Info);
        SaveResults();
    }
}