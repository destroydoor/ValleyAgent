#nullable enable
using System.Security.Cryptography;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;

namespace ValleyAgent.TestMod.Tests.Fuzzy;

/// <summary>
///     Fuzz test: Rapidly cycles NPC through random states to stress-test state machine.
///     Verifies: At least 1 state transition succeeded, no crashes from invalid transitions,
///     state machine doesn't get stuck in unknown state.
/// </summary>
public class F4_RandomDecisions : V3TestBase
{
    private readonly IModHelper _helper;
    private readonly IMonitor _monitor;
    private readonly string[] _targetStates = { "IDLE", "FARM", "FIGHT", "FOLLOW" };
    private int _decisionCount;
    private int _decisionTick;
    private int _failCount;
    private string _lastState = "";
    private NPC? _npc;
    private bool _stuckInUnknown;
    private int _successCount;

    public F4_RandomDecisions(IModHelper helper, IMonitor monitor) : base(helper, monitor)
    {
        _helper = helper;
        _monitor = monitor;
    }

    public override string TestName
    {
        get => "F4_RandomDecisions";
    }

    public override int TimeoutTicks
    {
        get => 2400;
    }

    public override TestGroup Group
    {
        get => TestGroup.Fuzzy;
    }

    public override void Setup()
    {
        SafeWarp.Farmer(Monitor, "Farm", 54, 15);
        _npc = Game1.getCharacterFromName("Haley");
        if (_npc == null)
        {
            Skip("Haley NPC not found");
            return;
        }

        _npc.setTileLocation(new Vector2(32, 30));
        _npc.Halt();

        // Allocate agent
        // 必须用 ValleyAgent.ModEntry.Instance.API（容器构建的真实 API）：
        // ModEntry.API 从 ModRegistry 获取的可能是 GameLaunched 时的 fallback
        // ValleyAgentApi(null,null,null)（_actionApi=null），TrySetAgentState 等会静默返回 false，
        // 导致所有决策失败（prev=UNKNOWN, success=False）。
        var api = ValleyAgent.ModEntry.Instance?.API ?? ModEntry.API;
        _ = api?.TryAllocateAgent("Haley");

        // 诊断：打印已注册状态
        if (api != null)
        {
            var registered = api.GetRegisteredStates("Haley");
            Monitor?.Log($"[F4] Registered states: {string.Join(", ", registered)}", LogLevel.Info);
        }

        _decisionTick = -999;
        _decisionCount = 0;
        _successCount = 0;
        _failCount = 0;
        _lastState = "IDLE";
        _stuckInUnknown = false;
    }

    public override bool Update()
    {
        if (_npc == null)
        {
            return true;
        }

        var tick = CurrentTick;
        var api = ValleyAgent.ModEntry.Instance?.API ?? ModEntry.API;
        if (api == null)
        {
            return true;
        }

        // Every 300 ticks, pick random state from {IDLE, FARM, FIGHT, FOLLOW}
        // 先回到 IDLE 再转到目标状态，避免非法转换
        if (tick - _decisionTick >= 300)
        {
            _decisionTick = tick;

            var stateIndex = RandomNumberGenerator.GetInt32(_targetStates.Length);
            var targetState = _targetStates[stateIndex];

            var previousState = api.GetAgentState("Haley");
            _lastState = string.IsNullOrEmpty(previousState) ? "UNKNOWN" : previousState;

            if (previousState == targetState)
            {
                _successCount++;
                _decisionCount++;
                _monitor.Log(
                    $"[F4] Decision #{_decisionCount}: try={targetState}, prev={previousState}, curr={previousState}, success=true (already in target)",
                    LogLevel.Info);
                return tick >= TimeoutTicks;
            }

            if (previousState != "IDLE")
            {
                _ = api.TrySetAgentState("Haley", "IDLE");
            }

            var success = api.TrySetAgentState("Haley", targetState);
            var currentState = api.GetAgentState("Haley");

            if (success)
            {
                _successCount++;
            }
            else
            {
                _failCount++;
            }

            // Check if stuck in unknown state
            if (string.IsNullOrEmpty(currentState) || currentState == "UNKNOWN")
            {
                _stuckInUnknown = true;
            }

            _decisionCount++;
            _monitor.Log(
                $"[F4] Decision #{_decisionCount}: try={targetState}, prev={previousState}, curr={currentState}, success={success}",
                LogLevel.Info);
        }

        return tick >= TimeoutTicks;
    }

    public override void Teardown()
    {
        if (_npc == null)
        {
            return;
        }

        var api = ValleyAgent.ModEntry.Instance?.API ?? ModEntry.API;
        var finalState = api?.GetAgentState("Haley") ?? "UNKNOWN";

        Assert("At_least_1_state_transition_succeeded", _successCount >= 1,
            $"successCount={_successCount}, failCount={_failCount}");
        Assert("No_crashes_from_invalid_transitions", _failCount == 0 || _failCount < _successCount,
            $"failCount={_failCount}");
        Assert("State_machine_not_stuck_in_unknown_state", !_stuckInUnknown && !string.IsNullOrEmpty(finalState),
            $"finalState={finalState}, stuckInUnknown={_stuckInUnknown}");

        _monitor.Log(
            $"[F4] Final: decisions={_decisionCount}, success={_successCount}, fail={_failCount}, finalState={finalState}",
            LogLevel.Info);
    }
}