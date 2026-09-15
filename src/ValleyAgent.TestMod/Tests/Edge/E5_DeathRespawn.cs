#nullable enable
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Monsters;

namespace ValleyAgent.TestMod.Tests.Edge;

/// <summary>
///     Tests NPC death and next-day respawn behavior.
///     Phase 1: NPC takes fatal damage (forced), verify removal from map.
///     Phase 2: Call TryRevive to simulate next-day revival.
///     Phase 3: Verify NPC is alive, healthy, and in IDLE state.
/// </summary>
public class E5_DeathRespawn : V3TestBase
{
    private IValleyAgentApi? _api;
    private bool _deathAsserted;
    private bool _died;

    private NPC? _npc;
    private int _phase = 1;
    private bool _revived;
    private GreenSlime? _slime;

    public E5_DeathRespawn(IModHelper helper, IMonitor monitor) : base(helper, monitor)
    {
    }

    public override string TestName
    {
        get => "E5_DeathRespawn";
    }

    public override int TimeoutTicks
    {
        get => 3000;
    }

    public override TestGroup Group
    {
        get => TestGroup.Edge;
    }

    public override void Setup()
    {
        _api = ModEntry.API;
        if (_api != null)
        {
            _ = _api.TryAllocateAgent("Haley");
        }

        SafeWarp.Farmer(Monitor, "Farm", 54, 15);
        _npc = Game1.getCharacterFromName("Haley");
        if (_npc == null)
        {
            Skip("Haley not found");
            return;
        }

        _npc.setTileLocation(new Vector2(32, 30));
        _npc.Halt();
        _npc.controller = null;

        // 生成史莱姆用于战斗触发
        var slime = new GreenSlime(new Vector2(34, 30) * 64f);
        Game1.currentLocation.characters.Add(slime);
        _slime = slime;

        // 强制 FIGHT 状态
        _ = _api?.TrySetAgentState("Haley", "FIGHT");
    }

    public override bool Update()
    {
        if (_npc == null || _api == null)
        {
            return true;
        }

        var tick = CurrentTick;

        // ── Phase 1: 等待死亡 (0-600) ──
        if (_phase == 1)
        {
            // 在 tick 120 强制设血量为 0 模拟死亡
            if (tick == 120 && !_died)
            {
                _ = _api.SetNpcHealth("Haley", 0);
                _died = true;
                Monitor.Log($"[E5] NPC forced death at tick {tick}.", LogLevel.Info);
            }

            var health = _api.GetNpcHealth(_npc.Name);
            if (health <= 0 && !_died)
            {
                _died = true;
                Monitor.Log($"[E5] NPC died at tick {tick}.", LogLevel.Info);
            }

            // NPC 死亡后应从地图移除（只断言一次，避免每 tick 重复）
            if (_died && !_deathAsserted)
            {
                var npcStillOnMap = Game1.currentLocation?.characters?.Contains(_npc) == true;
                if (!npcStillOnMap)
                {
                    Assert("NPC removed from map after death", !npcStillOnMap, $"tick={tick}");
                    Monitor.Log($"[E5] NPC confirmed removed from map at tick {tick}.", LogLevel.Info);
                    _deathAsserted = true;
                    _phase = 2;
                }
                else if (tick >= 300)
                {
                    // 等待足够时间后仍未移除则断言失败
                    Assert("NPC removed from map after death", false,
                        "NPC still on map after health=0 (waited 300 ticks)");
                    _deathAsserted = true;
                    _phase = 2;
                }
            }

            if (tick >= 600)
            {
                Monitor.Log($"[E5] Phase 1 timeout. Died={_died}.", LogLevel.Info);
                _phase = 2;
            }

            return false;
        }

        // ── Phase 2: 模拟复活 (600-1800) ──
        if (_phase == 2)
        {
            // 在 tick 700 调用 TryRevive 模拟次日复活
            if (tick == 700)
            {
                Monitor.Log($"[E5] Calling TryRevive at tick {tick}...", LogLevel.Info);
                _ = _api.TryRevive("Haley");
            }

            // 等待复活生效
            var health = _api.GetNpcHealth(_npc.Name);
            if (health > 0 && !_revived)
            {
                _revived = true;
                Monitor.Log($"[E5] NPC revived at tick {tick}. Health={health}.", LogLevel.Info);
                _phase = 3;
            }

            if (tick >= 1800)
            {
                Monitor.Log($"[E5] Phase 2 timeout. Revived={_revived}.", LogLevel.Info);
                _phase = 3;
            }

            return false;
        }

        // ── Phase 3: 验证复活 (1800-3000) ──
        if (_phase == 3)
        {
            var health = _api.GetNpcHealth(_npc.Name);
            var state = _api.GetAgentState(_npc.Name);
            var onMap = Game1.currentLocation?.characters?.Contains(_npc) == true;

            Assert("NPC is alive (health > 0)", health > 0, $"Health={health}");
            AssertEx("NPC is on map", onMap,
                "TryRevive 后 NPC 未被重新加入当前地图 characters（复活链漏掉 re-add，getCharacterFromName 仍返回实例但不在图上）",
                $"OnMap={onMap}");
            Assert("NPC is in IDLE state", state == "IDLE", $"State={state}");

            // 清理史莱姆
            if (_slime != null && _slime.currentLocation != null)
            {
                _ = _slime.currentLocation.characters.Remove(_slime);
            }

            return true; // done
        }

        return false;
    }

    public override void Teardown()
    {
        // 清理史莱姆
        if (_slime?.currentLocation != null)
        {
            _ = _slime.currentLocation.characters.Remove(_slime);
        }

        TestScenes.ClearAll(Helper, Monitor);
    }
}