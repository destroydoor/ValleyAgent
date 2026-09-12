#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;

namespace ValleyAgent.TestMod.Tests.Functional;

/// <summary>
///     Tests NPC action responses to various dialogue prompts across 5 phases.
///     Verifies that the NPC can interpret requests and suggest appropriate states.
/// </summary>
public class Func_DialogueActions : V3TestBase
{
    // M3 推理模型响应常规 15-40s，每 Phase 需 40s(2400t) 才能可靠收到回复
    private const int PhaseDuration = 2400;
    private const int Phase1Start = 50;
    private const int Phase2Start = Phase1Start + PhaseDuration;
    private const int Phase3Start = Phase2Start + PhaseDuration;
    private const int Phase4Start = Phase3Start + PhaseDuration;
    private const int Phase5Start = Phase4Start + PhaseDuration;
    private const int AssertionTick = Phase5Start + PhaseDuration - 50;
    private const int CompleteTick = Phase5Start + PhaseDuration + 450;

    // Dialogue responses — keyed by phase number (1-5)
    private readonly Dictionary<int, string> _phaseResponses = new();
    private IValleyAgentApi? _api;
    private int _friendshipAfter;

    private int _friendshipBefore;
    private NPC? _npc;

    // State snapshots
    private string _stateAtPhase1Start = string.Empty;
    private string _stateAtPhase2Start = string.Empty;
    private string _stateAtPhase3Start = string.Empty;
    private string _stateAtPhase4Start = string.Empty;
    private string _stateAtPhase5Start = string.Empty;

    public Func_DialogueActions(IModHelper helper, IMonitor monitor) : base(helper, monitor)
    {
    }

    private string NpcName
    {
        get => ConfigNpcName;
    }

    public override string TestName
    {
        get => "Func_DialogueActions";
    }

    public override int TimeoutTicks
    {
        get => CompleteTick + 300;
    }

    public override TestGroup Group
    {
        get => TestGroup.Functional;
    }

    public override void Setup()
    {
        Monitor.Log($"[{TestName}] Setting up — allocating NPC...", LogLevel.Info);

        _api = ModEntry.API;
        if (_api == null)
        {
            Monitor.Log($"[{TestName}] API null.", LogLevel.Error);
            return;
        }

        // Warp player to Farm first
        SafeWarp.Farmer(Monitor, "Farm", 54, 15);

        // Allocate NPC
        _api.TryAllocateAgent(NpcName);

        var npc = Game1.getCharacterFromName(NpcName);
        if (npc == null)
        {
            Monitor.Log($"[{TestName}] {NpcName} not found.", LogLevel.Error);
            return;
        }

        npc.setTileLocation(new Vector2(32, 30));
        _npc = npc;

        Monitor.Log($"[{TestName}] Setup complete. NPC: {_npc.Name} at {_npc.Tile}", LogLevel.Info);

        // Snapshot initial friendship
        _friendshipBefore = _api.GetNpcFriendshipPoints(NpcName);

        // 降低对话冷却时间，避免测试中请求被拒绝
        // 通过 IValleyAgentApi 实例属性访问（替代静态 ValleyAgentApi.DialogueCooldownMs）
        _api!.DialogueCooldownMs = 0;
    }

    public override bool Update()
    {
        if (_npc == null || _api == null)
        {
            return true; // cannot run
        }

        var tick = CurrentTick;

        // Phase progression (每 Phase 30s=1800t，适配 M3 推理模型):
        // Phase 0: Setup wait (ticks 0-50)
        // Phase 1: "帮我挖矿" (ticks 50-1850)
        // Phase 2: "帮我收菜" (ticks 1850-3650)
        // Phase 3: "送你一个礼物" (ticks 3650-5450)
        // Phase 4: "有怪物" (ticks 5450-7250)
        // Phase 5: "跟我来" (ticks 7250-9050) → assertions at 9000, return true at 9500

        if (tick < Phase1Start)
        {
            return false; // wait for setup
        }

        if (tick == Phase1Start)
        {
            _stateAtPhase1Start = _api.GetAgentState(NpcName);
            Monitor.Log($"[{TestName}] Phase 1 started: 帮我挖矿 | state={_stateAtPhase1Start}", LogLevel.Info);
            ClearCooldown();
            var ok = _api.TryGenerateDialogue(NpcName, "帮我挖矿，我需要一些石头");
            Monitor.Log($"[{TestName}] Phase 1 TryGenerateDialogue={ok}", LogLevel.Info);
        }

        if (tick >= Phase1Start && tick < Phase2Start)
        {
            TryCollectResponse(1);
            if (tick == Phase2Start - 1)
            {
                _stateAtPhase2Start = _api.GetAgentState(NpcName);
                Monitor.Log($"[{TestName}] Phase 2 started: 帮我收菜 | state={_stateAtPhase2Start}", LogLevel.Info);
                ClearCooldown();
                var ok = _api.TryGenerateDialogue(NpcName, "田里的作物熟了，帮我一起收菜吧");
                Monitor.Log($"[{TestName}] Phase 2 TryGenerateDialogue={ok}", LogLevel.Info);
            }
        }

        if (tick >= Phase2Start && tick < Phase3Start)
        {
            TryCollectResponse(2);
            if (tick == Phase3Start - 1)
            {
                _stateAtPhase3Start = _api.GetAgentState(NpcName);
                Monitor.Log($"[{TestName}] Phase 3 started: 送你一个礼物 | state={_stateAtPhase3Start}", LogLevel.Info);
                ClearCooldown();
                var ok = _api.TryGenerateDialogue(NpcName, "这个蓝莓送给你");
                Monitor.Log($"[{TestName}] Phase 3 TryGenerateDialogue={ok}", LogLevel.Info);
            }
        }

        if (tick >= Phase3Start && tick < Phase4Start)
        {
            TryCollectResponse(3);
            if (tick == Phase4Start - 1)
            {
                _stateAtPhase4Start = _api.GetAgentState(NpcName);
                Monitor.Log($"[{TestName}] Phase 4 started: 有怪物 | state={_stateAtPhase4Start}", LogLevel.Info);
                ClearCooldown();
                var ok = _api.TryGenerateDialogue(NpcName, "那边有怪物！");
                Monitor.Log($"[{TestName}] Phase 4 TryGenerateDialogue={ok}", LogLevel.Info);
            }
        }

        if (tick >= Phase4Start && tick < Phase5Start)
        {
            TryCollectResponse(4);
            if (tick == Phase5Start - 1)
            {
                _stateAtPhase5Start = _api.GetAgentState(NpcName);
                Monitor.Log($"[{TestName}] Phase 5 started: 跟我来 | state={_stateAtPhase5Start}", LogLevel.Info);
                ClearCooldown();
                var ok = _api.TryGenerateDialogue(NpcName, "跟我来，带你去个地方");
                Monitor.Log($"[{TestName}] Phase 5 TryGenerateDialogue={ok}", LogLevel.Info);
            }
        }

        if (tick >= Phase5Start)
        {
            TryCollectResponse(5);

            if (tick == AssertionTick)
            {
                _friendshipAfter = _api.GetNpcFriendshipPoints(NpcName);
                RunAssertions();
            }

            if (tick >= CompleteTick)
            {
                return true;
            }
        }

        return false;
    }

    private void TryCollectResponse(int phase)
    {
        if (_api == null || _npc == null)
        {
            return;
        }

        if (_phaseResponses.ContainsKey(phase))
        {
            return;
        }

        if (_api.TryGetLastDialogue(NpcName, out var resp) && !string.IsNullOrEmpty(resp))
        {
            // 跳过已收集过的回复（防止上一 Phase 的迟到的回复被错误归入当前 Phase）
            if (_phaseResponses.Values.Any(r => r == resp))
            {
                return;
            }

            _phaseResponses[phase] = resp;
        }
    }

    private string GetPhaseResponse(int phase) =>
        _phaseResponses.TryGetValue(phase, out var resp) ? resp : string.Empty;

    private void RunAssertions()
    {
        if (_api == null || _npc == null)
        {
            return;
        }

        // Phase 1: 帮我挖矿 — response contains mining keywords or MINE action/state
        var r1 = AssertPhase1();
        Record(r1, "Phase1_挖矿");

        // Phase 2: 帮我收菜 — response contains farming keywords or FARM action/state
        var r2 = AssertPhase2();
        Record(r2, "Phase2_收菜");

        // Phase 3: 送你一个礼物 — response mentions gift/thanks or friendship increased
        var r3 = AssertPhase3();
        Record(r3, "Phase3_礼物");

        // Phase 4: 有怪物 — response mentions fight/monster or FIGHT action/state
        var r4 = AssertPhase4();
        Record(r4, "Phase4_怪物");

        // Phase 5: 跟我来 — response mentions follow or FOLLOW action/state
        var r5 = AssertPhase5();
        Record(r5, "Phase5_跟随");

        // Overall: at least 2/5 phases produced the expected action
        bool[] phasesPassed = { r1.Passed, r2.Passed, r3.Passed, r4.Passed, r5.Passed };
        var passCount = phasesPassed.Count(p => p);

        Assert("AtLeast2_5_Phases_Pass", passCount >= 2,
            $"PassCount={passCount}/5 phases: P1={r1.Passed} P2={r2.Passed} P3={r3.Passed} P4={r4.Passed} P5={r5.Passed}");
    }

    private TestResult AssertPhase1()
    {
        if (_api == null)
        {
            return TestResult.Fail("API is null");
        }

        var currentState = _api.GetAgentState(NpcName);
        var resp = GetPhaseResponse(1);
        var hasState = currentState.Contains("MINE", StringComparison.OrdinalIgnoreCase);
        var hasText = !string.IsNullOrEmpty(resp) &&
                      (resp.Contains('矿') ||
                       resp.Contains("stone", StringComparison.OrdinalIgnoreCase) ||
                       resp.Contains("rock", StringComparison.OrdinalIgnoreCase) ||
                       resp.Contains("[ACTION:MINE]", StringComparison.OrdinalIgnoreCase));
        var passed = hasState || hasText;
        return passed
            ? TestResult.Pass($"Phase1 OK: state={currentState} text={Truncate(resp)}")
            : TestResult.Fail($"Phase1: state={currentState} text={Truncate(resp)} (no 矿/MINE keyword)");
    }

    private TestResult AssertPhase2()
    {
        if (_api == null)
        {
            return TestResult.Fail("API is null");
        }

        var currentState = _api.GetAgentState(NpcName);
        var resp = GetPhaseResponse(2);
        var hasState = currentState.Contains("FARM", StringComparison.OrdinalIgnoreCase);
        var hasText = !string.IsNullOrEmpty(resp) &&
                      (resp.Contains('菜') ||
                       resp.Contains("作物", StringComparison.OrdinalIgnoreCase) ||
                       resp.Contains("收获", StringComparison.OrdinalIgnoreCase) ||
                       resp.Contains("[ACTION:FARM]", StringComparison.OrdinalIgnoreCase));
        var passed = hasState || hasText;
        return passed
            ? TestResult.Pass($"Phase2 OK: state={currentState} text={Truncate(resp)}")
            : TestResult.Fail($"Phase2: state={currentState} text={Truncate(resp)} (no 菜/FARM keyword)");
    }

    private TestResult AssertPhase3()
    {
        if (_api == null)
        {
            return TestResult.Fail("API is null");
        }

        var currentState = _api.GetAgentState(NpcName);
        var resp = GetPhaseResponse(3);
        var hasState = currentState.Contains("IDLE", StringComparison.OrdinalIgnoreCase) ||
                       currentState.Contains("TALK", StringComparison.OrdinalIgnoreCase);
        var hasText = !string.IsNullOrEmpty(resp) &&
                      (resp.Contains("谢谢", StringComparison.OrdinalIgnoreCase) ||
                       resp.Contains("礼物", StringComparison.OrdinalIgnoreCase) ||
                       resp.Contains("berry", StringComparison.OrdinalIgnoreCase) ||
                       resp.Contains("蓝莓", StringComparison.OrdinalIgnoreCase) ||
                       resp.Contains("喜欢", StringComparison.OrdinalIgnoreCase) ||
                       resp.Contains("thank", StringComparison.OrdinalIgnoreCase) ||
                       resp.Contains("gift", StringComparison.OrdinalIgnoreCase));
        var friendshipChanged = _friendshipAfter != _friendshipBefore;
        var passed = hasState || hasText || friendshipChanged;
        return passed
            ? TestResult.Pass(
                $"Phase3 OK: state={currentState} text={Truncate(resp)} friendshipChanged={friendshipChanged}")
            : TestResult.Fail(
                $"Phase3: state={currentState} text={Truncate(resp)} (no 谢谢/礼物 keyword, no friendship change)");
    }

    private TestResult AssertPhase4()
    {
        if (_api == null)
        {
            return TestResult.Fail("API is null");
        }

        var currentState = _api.GetAgentState(NpcName);
        var resp = GetPhaseResponse(4);
        var hasState = currentState.Contains("FIGHT", StringComparison.OrdinalIgnoreCase);
        var hasText = !string.IsNullOrEmpty(resp) &&
                      (resp.Contains("怪物", StringComparison.OrdinalIgnoreCase) ||
                       resp.Contains("战斗", StringComparison.OrdinalIgnoreCase) ||
                       resp.Contains("slime", StringComparison.OrdinalIgnoreCase) ||
                       resp.Contains("monster", StringComparison.OrdinalIgnoreCase) ||
                       resp.Contains("[ACTION:FIGHT]", StringComparison.OrdinalIgnoreCase));
        var passed = hasState || hasText;
        return passed
            ? TestResult.Pass($"Phase4 OK: state={currentState} text={Truncate(resp)}")
            : TestResult.Fail($"Phase4: state={currentState} text={Truncate(resp)} (no 怪物/FIGHT keyword)");
    }

    private TestResult AssertPhase5()
    {
        if (_api == null)
        {
            return TestResult.Fail("API is null");
        }

        var currentState = _api.GetAgentState(NpcName);
        var resp = GetPhaseResponse(5);
        var hasState = currentState.Contains("FOLLOW", StringComparison.OrdinalIgnoreCase);
        var hasText = !string.IsNullOrEmpty(resp) &&
                      (resp.Contains('跟') ||
                       resp.Contains('来') ||
                       resp.Contains("follow", StringComparison.OrdinalIgnoreCase) ||
                       resp.Contains("come", StringComparison.OrdinalIgnoreCase) ||
                       resp.Contains("[ACTION:FOLLOW]", StringComparison.OrdinalIgnoreCase));
        var passed = hasState || hasText;
        return passed
            ? TestResult.Pass($"Phase5 OK: state={currentState} text={Truncate(resp)}")
            : TestResult.Fail($"Phase5: state={currentState} text={Truncate(resp)} (no 跟来/FOLLOW keyword)");
    }

    private void ClearCooldown() => ClearDialogueCooldown(_api, NpcName);

    public override void Teardown()
    {
        // 通过 IValleyAgentApi 实例属性恢复冷却时间（替代静态 ValleyAgentApi.DialogueCooldownMs）
        // Teardown 可能在 Setup 失败时调用，需 null 检查
        if (_api != null)
        {
            _api.DialogueCooldownMs = 30000;
        }

        Monitor.Log(
            $"[{TestName}] Teardown. Phases: 1={Truncate(GetPhaseResponse(1))} 2={Truncate(GetPhaseResponse(2))} 3={Truncate(GetPhaseResponse(3))} 4={Truncate(GetPhaseResponse(4))} 5={Truncate(GetPhaseResponse(5))}",
            LogLevel.Info);
        TestScenes.ClearAll(Helper, Monitor);
    }
}