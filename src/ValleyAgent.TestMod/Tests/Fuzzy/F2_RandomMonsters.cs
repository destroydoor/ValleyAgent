#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Monsters;

namespace ValleyAgent.TestMod.Tests.Fuzzy;

/// <summary>
///     Fuzz test: Spawns random numbers of monsters at random intervals.
///     Verifies: FightHandler responds, NPC health changes, no crashes, all monsters cleared.
/// </summary>
public class F2_RandomMonsters : V3TestBase
{
    private readonly List<Monster> _activeMonsters = new();
    private readonly IModHelper _helper;
    private readonly IMonitor _monitor;
    private bool _hadError;
    private bool _healthDecreasedOnce;
    private int _initialHealth;
    private int _maxMonstersSeen;
    private bool _monstersSpawnedOnce;
    private NPC? _npc;
    private bool _npcDied;
    private int _spawnCount;
    private int _spawnTick;

    public F2_RandomMonsters(IModHelper helper, IMonitor monitor) : base(helper, monitor)
    {
        _helper = helper;
        _monitor = monitor;
    }

    public override string TestName
    {
        get => "F2_RandomMonsters";
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
        // Warp player to Farm (outdoor, spawns monsters)
        SafeWarp.Farmer(Monitor, "Farm", 54, 15);

        _npc = Game1.getCharacterFromName("Haley");
        if (_npc == null)
        {
            Skip("Haley NPC not found");
            return;
        }

        // Protect player from stray monster hits
        Game1.player.maxHealth = 99999;
        Game1.player.health = 99999;

        // Teleport NPC to player location
        var playerTile = Game1.player.Tile;
        Game1.warpCharacter(_npc, Game1.currentLocation, playerTile);

        _initialHealth = ModEntry.API?.GetNpcHealth("Haley") ?? 100;
        _spawnTick = -999;
        _spawnCount = 0;
        _monstersSpawnedOnce = false;
        _healthDecreasedOnce = false;
        _npcDied = false;
        _hadError = false;
        _maxMonstersSeen = 0;
        _activeMonsters.Clear();

        // Allocate agent
        var api = ModEntry.API;
        _ = api?.TryAllocateAgent("Haley");

        // Force FIGHT state via API
        _ = api?.TrySetAgentState("Haley", "FIGHT");
    }

    public override bool Update()
    {
        if (_npc == null)
        {
            return true;
        }

        var tick = CurrentTick;
        var loc = Game1.currentLocation;
        if (loc == null)
        {
            return true;
        }

        try
        {
            return UpdateCore(tick, loc);
        }
        catch (InvalidOperationException ex)
        {
            return HandleUpdateError(ex, tick);
        }
        catch (NullReferenceException ex)
        {
            return HandleUpdateError(ex, tick);
        }
        catch (ArgumentException ex)
        {
            return HandleUpdateError(ex, tick);
        }
        catch (IndexOutOfRangeException ex)
        {
            return HandleUpdateError(ex, tick);
        }
        catch (KeyNotFoundException ex)
        {
            return HandleUpdateError(ex, tick);
        }
    }

    private bool HandleUpdateError(Exception ex, int tick)
    {
        _hadError = true;
        _monitor.Log($"[F2] Error at tick {tick}: {ex.Message}", LogLevel.Error);
        return tick >= TimeoutTicks;
    }

    private bool UpdateCore(int tick, GameLocation loc)
    {
        if (_npc == null)
        {
            return true;
        }

        // Every 180 ticks, spawn 1-3 green slimes within 5-10 tiles of NPC
        if (tick - _spawnTick >= 180)
        {
            _spawnTick = tick;

            // 玩家留在 NPC 附近确保 FightHandler 正确响应
            var count = RandomNumberGenerator.GetInt32(1, 4);

            for (var i = 0; i < count; i++)
            {
                var angle = RandomNumberGenerator.GetInt32(0, int.MaxValue) / (double)int.MaxValue * Math.PI * 2;
                var dist = 5.0 + RandomNumberGenerator.GetInt32(0, int.MaxValue) / (double)int.MaxValue * 5.0;
                var offset = new Vector2(
                    (float)(Math.Cos(angle) * dist),
                    (float)(Math.Sin(angle) * dist));

                var spawnTile = _npc.Tile + offset;
                spawnTile = TestScenes.FindWalkableTileNear(loc, spawnTile, _npc.Tile, 12);

                var worldPos = spawnTile * 64f;
                var slime = new GreenSlime(worldPos)
                {
                    Name = $"FuzzSlime_{_spawnCount}_{i}"
                };
                loc.characters.Add(slime);
                _activeMonsters.Add(slime);
            }

            _spawnCount++;
            _monitor.Log($"[F2] Spawned {count} monsters at tick {tick}, total spawns={_spawnCount}", LogLevel.Info);
        }

        // Track max monsters seen
        var currentMonsters = loc.characters.OfType<Monster>().ToList();
        if (currentMonsters.Count > 0)
        {
            _monstersSpawnedOnce = true;
        }

        if (currentMonsters.Count > _maxMonstersSeen)
        {
            _maxMonstersSeen = currentMonsters.Count;
        }

        // 检查 NPC 血量变化（包括死亡）
        var api = ModEntry.API;
        var currentHealth = api?.GetNpcHealth("Haley") ?? _initialHealth;
        if (currentHealth <= 0 && !_npcDied)
        {
            _npcDied = true;
            _healthDecreasedOnce = true; // 死亡也算血量变化
        }
        else if (currentHealth < _initialHealth && currentHealth > 0)
        {
            _healthDecreasedOnce = true;
        }

        // Clean up dead/despawned monsters from tracking list
        _ = _activeMonsters.RemoveAll(m =>
            m.currentLocation == null || !loc.characters.Contains(m));

        // All monsters cleared?
        var allCleared = _spawnCount > 0 && currentMonsters.Count == 0;

        // End after 1800 ticks
        return tick >= TimeoutTicks;
    }

    public override void Teardown()
    {
        if (_npc == null)
        {
            return;
        }

        var loc = Game1.currentLocation ?? _npc.currentLocation;

        // Clean up all remaining monsters so they don't carry over to next test
        if (loc != null)
        {
            var monsters = loc.characters.OfType<Monster>().ToList();
            foreach (var m in monsters)
            {
                _ = loc.characters.Remove(m);
            }
        }

        // 怪物可能把 Haley 打死或打残：SDV 中 NPC health 归零会被移出 characters，
        // 导致后续测试 Game1.getCharacterFromName("Haley") 返回 null → 连锁 SKIP
        // （全套回归时 F3/F4/F5 曾因此全部跳过）。
        // 残血（< EmergencyHealthThreshold=30%）同样危险：逃生逻辑会把状态机持续拉回
        // FOLLOW，污染后续测试（IT03 travel-failed→IDLE 曾因此失败）。无条件满血复活。
        var reviveApi = ValleyAgent.ModEntry.Instance?.API ?? ModEntry.API;
        if (Game1.getCharacterFromName("Haley") == null || (reviveApi?.GetNpcHealth("Haley") ?? 100) <= 0)
        {
            _ = reviveApi?.TryRevive("Haley");
            if (_npc.currentLocation == null)
            {
                var farm = Game1.getLocationFromName("Farm");
                if (farm != null)
                {
                    farm.characters.Add(_npc);
                    _npc.currentLocation = farm;
                }
            }

            _monitor.Log("[F2] Revived Haley after monster fight", LogLevel.Info);
        }
        else if ((reviveApi?.GetNpcHealth("Haley") ?? 100) < 100)
        {
            _ = reviveApi?.SetNpcHealth("Haley", 100);
            _monitor.Log("[F2] Healed Haley back to full after monster fight", LogLevel.Info);
        }

        var finalMonsters = loc?.characters.OfType<Monster>().Count() ?? 0;

        AssertEx("Monster_count_greater_than_zero_at_some_point", _monstersSpawnedOnce,
            "生成循环从未在 location.characters 里观察到 Monster（spawn 分支未执行或 GreenSlime 添加即被引擎清除），maxMonstersSeen=0",
            $"maxMonstersSeen={_maxMonstersSeen}");
        Assert("NPC_health_decreased_at_least_once", _healthDecreasedOnce || _npcDied,
            $"healthDecreased={_healthDecreasedOnce}, died={_npcDied}");
        AssertEx("Handler_no_crash", !_hadError,
            "F2 fuzz 期间 FightHandler/状态机抛 InvalidOperationException/NullReferenceException 等被 Update 的 catch 捕获（_hadError=true）",
            _hadError ? "Exception occurred during monster fuzz test" : "No exception during monster fuzz test");
        // 原 "All_spawned_monsters_cleared" 断言已删（2026-09-14 死断言清理）：
        // Teardown 先手动移除全部怪物、再统计 finalMonsters 断言 ==0，构造性恒真。

        _monitor.Log(
            $"[F2] Final: spawns={_spawnCount}, maxMonsters={_maxMonstersSeen}, healthDecreased={_healthDecreasedOnce}, finalMonsters={finalMonsters}",
            LogLevel.Info);
    }
}