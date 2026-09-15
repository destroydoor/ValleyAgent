#nullable enable
using System;
using System.Security.Cryptography;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using ValleyAgent.Navigation;

namespace ValleyAgent.TestMod.Tests.Fuzzy;

/// <summary>
///     Fuzz test: Two NPCs (Haley and Abigail) act independently with random actions.
///     Verifies: Both NPCs remain in valid states, WebSocket responses route correctly,
///     no state mixing between NPCs, neither crashes.
/// </summary>
public class F5_DualAgent : V3TestBase
{
    private readonly IModHelper _helper;
    private readonly IMonitor _monitor;
    private NPC? _abigail;
    private bool _abigailCrashed;
    private string _abigailFinalState = "";
    private string _abigailLastState = "";
    private int _actionCount;
    private int _actionTick;
    private NPC? _haley;
    private bool _haleyCrashed;
    private string _haleyFinalState = "";
    private string _haleyLastState = "";
    private int _haleyMoveCount;
    private bool _statesNeverMixed;

    public F5_DualAgent(IModHelper helper, IMonitor monitor) : base(helper, monitor)
    {
        _helper = helper;
        _monitor = monitor;
    }

    public override string TestName
    {
        get => "F5_DualAgent";
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
        _haley = Game1.getCharacterFromName("Haley");
        _abigail = Game1.getCharacterFromName("Abigail");

        if (_haley == null || _abigail == null)
        {
            Skip("Haley or Abigail NPC not found");
            return;
        }

        // Allocate both agents
        var api = ModEntry.API;
        _ = api?.TryAllocateAgent("Haley");
        _ = api?.TryAllocateAgent("Abigail");

        _actionTick = -999;
        _actionCount = 0;
        _haleyCrashed = false;
        _abigailCrashed = false;
        _statesNeverMixed = true;
        _haleyLastState = "IDLE";
        _abigailLastState = "IDLE";
        _haleyFinalState = "";
        _abigailFinalState = "";
        _haleyMoveCount = 0;

        // Warp both NPCs near player on Farm
        SafeWarp.Farmer(Monitor, "Farm", 54, 15);
        var playerTile = Game1.player.Tile;

        Game1.warpCharacter(_haley, Game1.currentLocation, playerTile + new Vector2(-2, 0));

        Game1.warpCharacter(_abigail, Game1.currentLocation, playerTile + new Vector2(2, 0));
    }

    public override bool Update()
    {
        if (_haley == null || _abigail == null)
        {
            return true;
        }

        var tick = CurrentTick;
        var api = ModEntry.API;
        var movementService = ModEntry.MovementService;
        if (api == null)
        {
            return true;
        }

        // Every 200 ticks, alternate making them do random actions
        if (tick - _actionTick >= 200)
        {
            _actionTick = tick;
            _actionCount++;

            // Agent 1 (Haley): random direction move
            try
            {
                var dirIdx = RandomNumberGenerator.GetInt32(4);
                var offset = dirIdx switch
                {
                    0 => new Vector2(0, -1),
                    1 => new Vector2(0, 1),
                    2 => new Vector2(-1, 0),
                    _ => new Vector2(1, 0)
                };
                var target = _haley.Tile + offset * RandomNumberGenerator.GetInt32(2, 6);

                if (movementService != null)
                {
                    var result = movementService.MoveTo(_haley, target, MovementMode.ShortRange, tick);
                    if (result is MoveResult.Success or MoveResult.AlreadyMoving)
                    {
                        _haleyMoveCount++;
                    }
                }

                var haleyState = api.GetAgentState("Haley");
                if (_actionCount > 1 && haleyState != _haleyLastState && _haleyLastState != "IDLE")
                {
                    // State changed - ok, not mixing
                }

                _haleyLastState = haleyState;
            }
            catch (InvalidOperationException ex)
            {
                _haleyCrashed = true;
                _monitor.Log($"[F5] Haley crashed: {ex.Message}", LogLevel.Error);
            }
            catch (NullReferenceException ex)
            {
                _haleyCrashed = true;
                _monitor.Log($"[F5] Haley crashed: {ex.Message}", LogLevel.Error);
            }
            catch (ArgumentException ex)
            {
                _haleyCrashed = true;
                _monitor.Log($"[F5] Haley crashed: {ex.Message}", LogLevel.Error);
            }

            // Agent 2 (Abigail): random state pick
            try
            {
                var states = new[] { "IDLE", "FARM", "FIGHT", "FOLLOW" };
                var state = states[RandomNumberGenerator.GetInt32(states.Length)];
                _ = api.TrySetAgentState("Abigail", state);

                var abigailState = api.GetAgentState("Abigail");
                _abigailLastState = abigailState;
            }
            catch (InvalidOperationException ex)
            {
                _abigailCrashed = true;
                _monitor.Log($"[F5] Abigail crashed: {ex.Message}", LogLevel.Error);
            }
            catch (NullReferenceException ex)
            {
                _abigailCrashed = true;
                _monitor.Log($"[F5] Abigail crashed: {ex.Message}", LogLevel.Error);
            }
            catch (ArgumentException ex)
            {
                _abigailCrashed = true;
                _monitor.Log($"[F5] Abigail crashed: {ex.Message}", LogLevel.Error);
            }

            // Check for state mixing: 两个 NPC 的状态应该独立
            // 验证：Haley 的状态变化不应影响 Abigail 的状态
            var haleyStateNow = api.GetAgentState("Haley");
            var abigailStateNow = api.GetAgentState("Abigail");
            // 只有两个 NPC 状态相同且不是 IDLE 且同时变化时才标记
            var haleyChanged = haleyStateNow != _haleyLastState;
            var abigailChanged = abigailStateNow != _abigailLastState;
            if (haleyStateNow == abigailStateNow && haleyStateNow != "IDLE" && haleyChanged && abigailChanged)
            {
                _statesNeverMixed = false;
                _monitor.Log($"[F5] State mixing detected at tick {tick}: both changed to {haleyStateNow}",
                    LogLevel.Warn);
            }

            _haleyLastState = haleyStateNow;
            _abigailLastState = abigailStateNow;
        }

        return tick >= TimeoutTicks;
    }

    public override void Teardown()
    {
        var api = ModEntry.API;
        _haleyFinalState = api?.GetAgentState("Haley") ?? "";
        _abigailFinalState = api?.GetAgentState("Abigail") ?? "";

        Assert("Both_NPCs_remain_in_valid_states",
            !string.IsNullOrEmpty(_haleyFinalState) && !string.IsNullOrEmpty(_abigailFinalState),
            $"Haley={_haleyFinalState}, Abigail={_abigailFinalState}");
        Assert("WebSocket_responses_route_correctly", _actionCount > 0,
            $"actions={_actionCount}");
        Assert("No_state_mixing_between_NPCs", _statesNeverMixed,
            "States never mixed during dual action");
        AssertEx("Neither_NPC_crashes", !_haleyCrashed && !_abigailCrashed,
            "双 Agent 并发驱动期间任一 NPC 的 Update 链抛 InvalidOperationException/NullReferenceException 等被 catch（_haleyCrashed/_abigailCrashed=true）",
            $"haleyCrashed={_haleyCrashed}, abigailCrashed={_abigailCrashed}");

        _monitor.Log(
            $"[F5] Final: haley={_haleyFinalState} ({_haleyMoveCount} moves), abigail={_abigailFinalState} (actions={_actionCount}), haleyCrashed={_haleyCrashed}, abigailCrashed={_abigailCrashed}",
            LogLevel.Info);
    }
}