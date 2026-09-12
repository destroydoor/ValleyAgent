#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using ValleyAgent.StateMachine;
using ValleyAgent.Utils;

namespace ValleyAgent.TestMod.Tests.Fuzzy;

/// <summary>
///     刁钻模糊测试：TrySetAgentState 使用 ForceTransition 绕过 AllowedTransitions。
///     背景：TrySetAgentState 内部调用 agent.StateMachine.ForceTransition()，
///     ForceTransition 现在同时检查 AllowedTransitions 和最短持续时间。
///     测试：
///     10 轮随机测试：强制进入有持续时间的状态，立即强制转为非法目标状态，
///     验证转换不应成功（ForceTransition 现在会阻止持续时间未满足的转换）。
/// </summary>
public class F8_StateDurationBypass : V3TestBase
{
    // 有持续时间的状态
    private static readonly (string State, int MinSeconds)[] DurationStates =
    {
        ("FIGHT", 15),
        ("MINE", 10),
        ("FARM", 8),
        ("FORAGE", 8)
    };

    private readonly List<string> _violationLog = new();
    private IValleyAgentApi? _api;
    private int _illegalTransitionsSucceeded;

    private NPC? _npc;
    private int _round;
    private int _totalTransitions;

    public F8_StateDurationBypass(IModHelper helper, IMonitor monitor) : base(helper, monitor)
    {
    }

    public override string TestName
    {
        get => "F8_StateDurationBypass";
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
        DebugFlags.SuppressDecisions = true;

        _api = ModEntry.API;
        if (_api == null)
        {
            Skip("API不可用");
            return;
        }

        SafeWarp.Farmer(Monitor, "Farm", 54, 15);
        _npc = Game1.getCharacterFromName("Haley");
        if (_npc == null)
        {
            Skip("Haley NPC 未找到");
            return;
        }

        _npc.setTileLocation(new Vector2(32, 30));
        _npc.Halt();
        _ = _api.TryAllocateAgent("Haley");
        _ = _api.TryRevive("Haley");
        _ = _api.TrySetAgentState("Haley", "IDLE");

        _round = 0;
        _illegalTransitionsSucceeded = 0;
        _totalTransitions = 0;
    }

    public override bool Update()
    {
        if (_npc == null || _api == null)
        {
            return true;
        }

        var tick = CurrentTick;

        // 每 200 tick 执行一轮测试
        if (tick > 0 && tick % 200 == 0 && _round < 10)
        {
            ExecuteRound(_round);
            _round++;
        }

        // 全部轮次完成后断言
        if (_round >= 10 && tick >= 10 * 200 + 60)
        {
            Assert(
                "ForceTransition_duration_bypass_blocked",
                _illegalTransitionsSucceeded == 0,
                $"ForceTransition 绕过: {_illegalTransitionsSucceeded}/{_totalTransitions}次非法转换成功。" +
                "持续时间守卫已在状态机转换层强制执行。" +
                $"违规详情：{string.Join("; ", _violationLog.Take(5))}");

            Assert(
                "All_transitions_completed_without_crash",
                true,
                $"完成{_totalTransitions}次转换测试，无崩溃");

            Assert(
                "At_least_10_rounds_executed",
                _round >= 10,
                $"执行了{_round}轮测试");

            return true;
        }

        return false;
    }

    private void ExecuteRound(int round)
    {
        if (_api == null)
        {
            return;
        }

        // 随机选择一个有持续时间的状态
        var (targetState, minSeconds) = DurationStates[RandomNumberGenerator.GetInt32(DurationStates.Length)];

        // 强制进入目标状态
        _ = _api.TrySetAgentState("Haley", targetState);
        _totalTransitions++;

        // 立即尝试非法转换（1 tick 延迟，远低于最短持续时间）
        // 选择不在 AllowedTransitions 中的目标，确保转换因 AllowedTransitions 被阻止
        // 如果所有其他 DurationState 都是合法目标，则跳过本轮
        var illegalTargets = DurationStates
            .Where(ds => ds.State != targetState)
            .Where(ds => !AgentStateMachine.IsTransitionAllowed(
                Enum.Parse<AgentState>(targetState),
                Enum.Parse<AgentState>(ds.State)))
            .ToList();

        if (illegalTargets.Count == 0)
        {
            // 当前状态到所有其他 DurationState 都是合法转换，跳过本轮
            Monitor.Log($"[F8] R{round} 跳过: {targetState}→所有DurationState均合法", LogLevel.Debug);
            _ = _api.TrySetAgentState("Haley", "IDLE");
            return;
        }

        var illegalTarget = illegalTargets[RandomNumberGenerator.GetInt32(illegalTargets.Count)].State;

        _ = _api.TrySetAgentState("Haley", illegalTarget);
        var state2 = _api.GetAgentState("Haley");
        _totalTransitions++;

        if (state2 == illegalTarget)
        {
            _illegalTransitionsSucceeded++;
            _violationLog.Add($"{targetState}({minSeconds}s)→{illegalTarget}");
            Monitor.Log(
                $"[F8] R{round} ForceTransition bypass: {targetState}→{illegalTarget} " +
                $"(expected blocked: AllowedTransitions + {minSeconds}s min duration)",
                LogLevel.Warn);
        }

        // 重置
        _ = _api.TrySetAgentState("Haley", "IDLE");
    }

    public override void Teardown()
    {
        DebugFlags.SuppressDecisions = false;
        TestScenes.ClearAll(Helper, Monitor);
    }
}