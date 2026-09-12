#nullable enable
using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using ValleyAgent.Core;
using ValleyAgent.TestMod.Tests.Integration;
using ValleyAgent.Utils;
using ValleyAgent.WebSocket;

namespace ValleyAgent.TestMod.Tests.Functional;

/// <summary>
///     FOLLOW/IDLE vanilla-release 回归测试。
///     验证 set_state IDLE 走 CommandExecutor 真实路径后，NPC 回到 DefaultMap 并恢复原版调度。
///
///     场景 1（主）：FOLLOW → IDLE — 立即释放到原版日程，NPC 回家。
///     场景 2（辅）：TALK → IDLE — 不立即释放（延迟释放路径），followSchedule 保持 false。
///
///     覆盖 bug 修复：NPC 跟玩家回家后 LLM 调 set_state IDLE，原版调度 flag 残留 + controller 残留。
///     修复后 ExecuteSetState 中 FOLLOW→IDLE 调 ReleaseToVanillaNow → BeginWalkBack → FinalizeVanillaRelease。
/// </summary>
public class Func_VanillaReleaseReturnHome : IntegrationTestBase
{
    // 场景 1：FOLLOW → IDLE
    private bool _phase1SetupDone;
    private bool _phase1Triggered;
    private bool _phase1Asserted;
    private string _phase1NpcLocBefore = "";

    // 场景 2：TALK → IDLE（延迟释放）
    private bool _phase2SetupDone;
    private bool _phase2Triggered;
    private bool _phase2Asserted;

    private CommandExecutor? _executor;
    private AgentTickLoop? _tickLoop;
    private NPC? _npc;

    public Func_VanillaReleaseReturnHome(IModHelper helper, IMonitor monitor) : base(helper, monitor)
    {
    }

    public override string TestName
    {
        get => "Func_VanillaReleaseReturnHome";
    }

    /// <summary>两场景各 ~300 tick + 间隔，给足 1200 tick。</summary>
    public override int TimeoutTicks
    {
        get => 1200;
    }

    public override TestGroup Group
    {
        get => TestGroup.Functional;
    }

    public override void Setup()
    {
        if (!TryCommonSetup())
        {
            return;
        }

        _executor = Container!.GetService<CommandExecutor>();
        if (_executor == null)
        {
            Skip("CommandExecutor not registered in container");
            return;
        }

        _tickLoop = Container.GetService<AgentTickLoop>();

        _npc = Game1.getCharacterFromName(NpcName);
        if (_npc == null)
        {
            Skip($"{NpcName} NPC not found");
            return;
        }

        // 抑制 LLM 决策，避免覆盖测试中设置的状态
        DebugFlags.SuppressDecisions = true;

        // 把玩家和 NPC 都移到 FarmHouse（玩家家），模拟"NPC 跟玩家回家"场景
        SafeWarp.Farmer(Monitor, "FarmHouse", 5, 5);
        if (_npc.currentLocation?.Name != "FarmHouse")
        {
            _ = _npc.currentLocation?.characters.Remove(_npc);
            var farmHouse = Game1.getLocationFromName("FarmHouse");
            if (farmHouse != null)
            {
                farmHouse.characters.Add(_npc);
                _npc.currentLocation = farmHouse;
            }
        }

        _npc.setTileLocation(new Vector2(6, 5));
        _npc.Halt();
        _npc.controller = null;
        _npc.followSchedule = false;
        _npc.ignoreScheduleToday = true;

        Monitor.Log(
            $"[Func_VanillaRelease] Setup complete. NPC at {_npc.Tile} on {_npc.currentLocation?.Name}, " +
            $"DefaultMap={_npc.DefaultMap}",
            LogLevel.Info);
    }

    public override bool Update()
    {
        if (_npc == null || _executor == null || Api == null)
        {
            return true;
        }

        var npcLoc = _npc.currentLocation?.Name ?? "";

        // ═══════════════════════════════════════════════════════════════
        // 场景 1：FOLLOW → IDLE → NPC 回到 DefaultMap + 原版调度恢复
        // ═══════════════════════════════════════════════════════════════

        // Phase 1 setup: 进入 FOLLOW 态
        if (CurrentTick == 30 && !_phase1SetupDone)
        {
            _phase1SetupDone = true;
            _phase1NpcLocBefore = npcLoc;

            var followOk = Api.TrySetAgentState(NpcName, "FOLLOW");
            Assert("p1_set_follow_ok", followOk,
                $"TrySetAgentState(FOLLOW)={followOk}, npcLoc={npcLoc}");

            var state = Api.GetAgentState(NpcName);
            Assert("p1_state_is_follow", state == "FOLLOW",
                $"state={state}");

            Monitor.Log(
                $"[Func_VanillaRelease] Phase 1: FOLLOW entered. npcLoc={npcLoc}, DefaultMap={_npc.DefaultMap}",
                LogLevel.Info);
        }

        // Phase 1 trigger: 通过 CommandExecutor 真实路径执行 set_state IDLE
        if (CurrentTick == 60 && _phase1SetupDone && !_phase1Triggered)
        {
            _phase1Triggered = true;

            var args = new Dictionary<string, object> { ["state"] = "IDLE" };
            var action = new ToolAction("set_state", args, "func-vr-test-1");
            try
            {
                _executor.ExecuteAction(action, NpcName);
                Monitor.Log(
                    $"[Func_VanillaRelease] Phase 1: ExecuteAction(set_state, IDLE) called. " +
                    $"npcLoc={npcLoc}, DefaultMap={_npc.DefaultMap}",
                    LogLevel.Info);
            }
            catch (Exception ex)
            {
                Assert("p1_execute_action_no_throw", false, $"ExecuteAction threw: {ex.Message}");
                return true;
            }
        }

        // Phase 1 断言：轮询等待 NPC 回到 DefaultMap（跨图 warp 应在 ~2 tick 内完成）
        // 从 trigger tick+1 开始检查，到 tick 360 为止
        if (_phase1Triggered && !_phase1Asserted && CurrentTick > 60 && CurrentTick <= 360)
        {
            var currentLoc = _npc.currentLocation?.Name ?? "";
            var atDefaultMap = string.Equals(currentLoc, _npc.DefaultMap, StringComparison.OrdinalIgnoreCase);
            var followSchedule = _npc.followSchedule;
            var ignoreSchedule = _npc.ignoreScheduleToday;
            var state = Api.GetAgentState(NpcName);
            var finalized = _tickLoop?.IsVanillaFinalized(NpcName) ?? false;

            // 跨图 warp 后立即断言（不等超时）
            if (atDefaultMap && followSchedule && !ignoreSchedule)
            {
                _phase1Asserted = true;

                AssertEx("p1_npc_at_default_map", atDefaultMap,
                    "F1: FOLLOW→IDLE 后 NPC 未回到 DefaultMap（BeginWalkBack warp 失败或未触发）",
                    $"npcLoc={currentLoc} defaultMap={_npc.DefaultMap} beforeLoc={_phase1NpcLocBefore}");

                AssertEx("p1_followSchedule_true", followSchedule,
                    "F1: FinalizeVanillaRelease 未恢复 followSchedule",
                    $"followSchedule={followSchedule}");

                AssertEx("p1_ignoreSchedule_false", !ignoreSchedule,
                    "F1: FinalizeVanillaRelease 未清除 ignoreScheduleToday",
                    $"ignoreScheduleToday={ignoreSchedule}");

                AssertEx("p1_state_is_idle", state == "IDLE",
                    "F1: set_state IDLE 后状态机不是 IDLE",
                    $"state={state}");

                AssertEx("p1_controller_null_or_safe", _npc.controller == null,
                    "F1: controller 残留（MovementService.Stop 未清理）",
                    $"controller={(_npc.controller != null ? _npc.controller.GetType().Name : "null")}");

                AssertEx("p1_vanilla_finalized", finalized,
                    "F1: IsVanillaFinalized 为 false（FinalizeVanillaRelease 未跑完）",
                    $"IsVanillaFinalized={finalized}");

                Monitor.Log(
                    $"[Func_VanillaRelease] Phase 1 PASS: NPC at {currentLoc}, followSchedule={followSchedule}, " +
                    $"ignoreSchedule={ignoreSchedule}, state={state}, finalized={finalized}",
                    LogLevel.Info);
            }
            // 超时兜底：在 tick 360 做最后一次断言
            else if (CurrentTick == 360)
            {
                _phase1Asserted = true;

                AssertEx("p1_npc_at_default_map", atDefaultMap,
                    "F1: FOLLOW→IDLE 后 NPC 未回到 DefaultMap（超时）",
                    $"npcLoc={currentLoc} defaultMap={_npc.DefaultMap} beforeLoc={_phase1NpcLocBefore} elapsed={CurrentTick - 60}ticks");

                AssertEx("p1_followSchedule_true", followSchedule,
                    "F1: FinalizeVanillaRelease 未恢复 followSchedule（超时）",
                    $"followSchedule={followSchedule} elapsed={CurrentTick - 60}ticks");

                AssertEx("p1_ignoreSchedule_false", !ignoreSchedule,
                    "F1: FinalizeVanillaRelease 未清除 ignoreScheduleToday（超时）",
                    $"ignoreScheduleToday={ignoreSchedule} elapsed={CurrentTick - 60}ticks");

                AssertEx("p1_state_is_idle", state == "IDLE",
                    "F1: set_state IDLE 后状态机不是 IDLE（超时）",
                    $"state={state} elapsed={CurrentTick - 60}ticks");

                AssertEx("p1_controller_null_or_safe", _npc.controller == null,
                    "F1: controller 残留（超时）",
                    $"controller={(_npc.controller != null ? _npc.controller.GetType().Name : "null")} elapsed={CurrentTick - 60}ticks");

                AssertEx("p1_vanilla_finalized", finalized,
                    "F1: IsVanillaFinalized 为 false（超时）",
                    $"IsVanillaFinalized={finalized} elapsed={CurrentTick - 60}ticks");
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // 场景 2：TALK → IDLE — 不立即释放（延迟释放路径）
        // ═══════════════════════════════════════════════════════════════

        // Phase 2 setup: 把 NPC 拉回 FarmHouse，进入 TALK 态
        if (CurrentTick == 420 && _phase1Asserted && !_phase2SetupDone)
        {
            _phase2SetupDone = true;

            // 先重置 release 状态（场景 1 的残留）
            _tickLoop?.RemoveReleasedToVanilla(NpcName);

            // 把 NPC 拉回 FarmHouse（玩家所在位置，TALK 要求同图）
            SafeWarp.Farmer(Monitor, "FarmHouse", 5, 5);
            if (_npc.currentLocation?.Name != "FarmHouse")
            {
                _ = _npc.currentLocation?.characters.Remove(_npc);
                var farmHouse = Game1.getLocationFromName("FarmHouse");
                if (farmHouse != null)
                {
                    farmHouse.characters.Add(_npc);
                    _npc.currentLocation = farmHouse;
                }
            }

            _npc.setTileLocation(new Vector2(6, 5));
            _npc.Halt();
            _npc.controller = null;

            // 恢复 agent 控制（禁原版调度）
            _npc.followSchedule = false;
            _npc.ignoreScheduleToday = true;

            var talkOk = Api.TrySetAgentState(NpcName, "TALK");
            Assert("p2_set_talk_ok", talkOk,
                $"TrySetAgentState(TALK)={talkOk}");

            Monitor.Log(
                $"[Func_VanillaRelease] Phase 2: TALK entered. npcLoc={_npc.currentLocation?.Name}",
                LogLevel.Info);
        }

        // Phase 2 trigger: set_state IDLE
        if (CurrentTick == 480 && _phase2SetupDone && !_phase2Triggered)
        {
            _phase2Triggered = true;

            var args = new Dictionary<string, object> { ["state"] = "IDLE" };
            var action = new ToolAction("set_state", args, "func-vr-test-2");
            try
            {
                _executor.ExecuteAction(action, NpcName);
                Monitor.Log(
                    $"[Func_VanillaRelease] Phase 2: ExecuteAction(set_state, IDLE) from TALK called.",
                    LogLevel.Info);
            }
            catch (Exception ex)
            {
                Assert("p2_execute_action_no_throw", false, $"ExecuteAction threw: {ex.Message}");
                return true;
            }
        }

        // Phase 2 断言：TALK→IDLE 不应立即释放
        if (_phase2Triggered && !_phase2Asserted && CurrentTick >= 540)
        {
            _phase2Asserted = true;

            var currentLoc = _npc.currentLocation?.Name ?? "";
            var followSchedule = _npc.followSchedule;
            var ignoreSchedule = _npc.ignoreScheduleToday;
            var state = Api.GetAgentState(NpcName);
            var released = _tickLoop?.IsReleasedToVanilla(NpcName) ?? false;
            var finalized = _tickLoop?.IsVanillaFinalized(NpcName) ?? false;

            // TALK→IDLE 不调 ReleaseToVanillaNow，所以不应被释放
            AssertEx("p2_not_released_to_vanilla", !released,
                "F2: TALK→IDLE 不应触发 ReleaseToVanillaNow",
                $"IsReleasedToVanilla={released}");

            AssertEx("p2_not_finalized", !finalized,
                "F2: TALK→IDLE 不应触发 FinalizeVanillaRelease",
                $"IsVanillaFinalized={finalized}");

            AssertEx("p2_followSchedule_still_false", !followSchedule,
                "F2: TALK→IDLE 后 followSchedule 应保持 false（延迟释放）",
                $"followSchedule={followSchedule}");

            AssertEx("p2_state_is_idle", state == "IDLE",
                "F2: set_state IDLE 后状态机不是 IDLE",
                $"state={state}");

            // NPC 应仍在 FarmHouse（未被 warp 走）
            AssertEx("p2_npc_still_at_farmhouse",
                string.Equals(currentLoc, "FarmHouse", StringComparison.OrdinalIgnoreCase),
                "F2: TALK→IDLE 后 NPC 不应被 warp 走",
                $"npcLoc={currentLoc}");

            Monitor.Log(
                $"[Func_VanillaRelease] Phase 2: released={released}, finalized={finalized}, " +
                $"followSchedule={followSchedule}, state={state}, npcLoc={currentLoc}",
                LogLevel.Info);
        }

        return CurrentTick >= 660;
    }

    public override void Teardown()
    {
        DebugFlags.SuppressDecisions = false;

        // 恢复 NPC 到 IDLE + 原版调度，避免影响后续测试
        if (_npc != null)
        {
            _tickLoop?.RemoveReleasedToVanilla(NpcName);
            _npc.followSchedule = true;
            _npc.ignoreScheduleToday = false;
            _npc.controller = null;
        }

        Api?.TrySetAgentState(NpcName, "IDLE");
        CommonTeardown();
        Monitor.Log($"[Func_VanillaRelease] Teardown.", LogLevel.Info);
    }
}
