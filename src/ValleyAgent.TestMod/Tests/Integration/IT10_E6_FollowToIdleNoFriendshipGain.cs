#nullable enable
using StardewModdingAPI;

namespace ValleyAgent.TestMod.Tests.Integration;

/// <summary>
///     IT10：验证 E6 修复 — FOLLOW → IDLE 状态转换不再 +2 好感度。
///     历史版本中 FOLLOW 态结束转 IDLE 会误加 friendshipPoints，导致玩家无需交互就涨好感。
///     E6 修复后状态机转换不再修改 friendshipData。
///     验证点：
///     1. 记录 friendshipPoints 基线
///     2. TrySetAgentState(FOLLOW) → TrySetAgentState(IDLE)
///     3. friendshipPoints 不变（delta == 0）
///     这是最纯粹的 E6 验证：直接通过 IValleyAgentApi 操作状态机，不依赖 LLM/dialogue。
/// </summary>
public class IT10_E6_FollowToIdleNoFriendshipGain : IntegrationTestBase
{
    private bool _asserted;
    private int _friendshipBefore;
    private bool _setupComplete;
    private bool _transitionsDone;

    public IT10_E6_FollowToIdleNoFriendshipGain(IModHelper helper, IMonitor monitor) : base(helper, monitor)
    {
    }

    public override string TestName
    {
        get => "IT10_E6_FollowToIdleNoFriendshipGain";
    }

    public override int TimeoutTicks
    {
        get => 600;
    }

    public override TestGroup Group
    {
        get => TestGroup.Integration;
    }

    public override void Setup()
    {
        if (!TryCommonSetup())
        {
            return;
        }

        // 起点 IDLE
        _ = Api!.TrySetAgentState(NpcName, "IDLE");

        // 记录 friendshipPoints 基线
        _friendshipBefore = Api.GetNpcFriendshipPoints(NpcName);

        Monitor.Log($"[{TestName}] Setup complete. friendshipBefore={_friendshipBefore}", LogLevel.Info);
        _setupComplete = true;
    }

    public override bool Update()
    {
        if (!_setupComplete || Api == null)
        {
            return true;
        }

        // Phase 1: FOLLOW → IDLE 转换
        if (CurrentTick == 30 && !_transitionsDone)
        {
            _transitionsDone = true;

            var followOk = Api.TrySetAgentState(NpcName, "FOLLOW");
            Assert("set_follow_ok", followOk, $"TrySetAgentState(FOLLOW)={followOk}");

            var followState = Api.GetAgentState(NpcName);
            Assert("state_is_follow", followState == "FOLLOW", $"state={followState}");

            var idleOk = Api.TrySetAgentState(NpcName, "IDLE");
            Assert("set_idle_ok", idleOk, $"TrySetAgentState(IDLE)={idleOk}");

            var idleState = Api.GetAgentState(NpcName);
            Assert("state_is_idle", idleState == "IDLE", $"state={idleState}");
        }

        // Phase 2: 验证 friendshipPoints 不变
        if (CurrentTick == 120 && !_asserted)
        {
            _asserted = true;
            var friendshipAfter = Api.GetNpcFriendshipPoints(NpcName);
            var delta = friendshipAfter - _friendshipBefore;

            Assert("friendship_unchanged", delta == 0,
                $"before={_friendshipBefore} after={friendshipAfter} delta={delta} (expected 0)");

            // 二次确认：再走一次 FOLLOW→IDLE，friendship 仍不应变
            var followOk2 = Api.TrySetAgentState(NpcName, "FOLLOW");
            var idleOk2 = Api.TrySetAgentState(NpcName, "IDLE");
            var friendshipAfter2 = Api.GetNpcFriendshipPoints(NpcName);
            var delta2 = friendshipAfter2 - _friendshipBefore;
            Assert("friendship_unchanged_after_second_cycle", delta2 == 0,
                $"second cycle: follow={followOk2} idle={idleOk2} delta={delta2}");
        }

        return CurrentTick >= 200;
    }

    public override void Teardown()
    {
        Api?.TrySetAgentState(NpcName, "IDLE");
        CommonTeardown();
        Monitor.Log($"[{TestName}] Teardown.", LogLevel.Info);
    }
}