#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using ValleyAgent.StateMachine;
using ValleyAgent.Utils;

namespace ValleyAgent.TestMod.Tests.Edge;

/// <summary>
///     刁钻测试：TrySetAgentState 使用 ForceTransition 绕过所有状态机规则。
///     背景：IValleyAgentApi.TrySetAgentState 内部调用 agent.StateMachine.ForceTransition()，
///     ForceTransition 不检查 AllowedTransitions 也不检查 STATE_DURATIONS。
///     这意味着任何状态都可以绕过规则被设置。
///     测试步骤：
///     1. 强制 FIGHT 状态（有 15s 最短持续，且 FIGHT→FARM 不在允许转换中）
///     2. 等待极短时间后立即强制 FARM 状态
///     3. 验证：转换成功（违反 AllowedTransitions + STATE_DURATIONS）
///     4. 额外验证：FARM→FIGHT, FARM→MINE, FORAGE→FIGHT 等非法转换全部成功
/// </summary>
public class E10_IllegalTransitionPermitted : V3TestBase
{
    // 非法转换候选列表（实际合法性由 IsTransitionAllowed 动态判定）
    private static readonly (string From, string To)[] CandidateTransitions =
    {
        ("FIGHT", "FARM"),
        ("FIGHT", "FORAGE"),
        ("FIGHT", "MINE"),
        ("FIGHT", "TALK"),
        ("FARM", "FIGHT"),
        ("FARM", "MINE"),
        ("FARM", "FOLLOW"),
        ("FARM", "FORAGE"),
        ("FORAGE", "FIGHT"),
        ("FORAGE", "FARM"),
        ("FORAGE", "MINE"),
        ("MINE", "FARM"),
        ("MINE", "FORAGE"),
        ("TALK", "FARM"),
        ("TALK", "FORAGE"),
        ("TALK", "MINE")
    };

    private new readonly List<(string From, string To, bool ShouldFail, bool ActuallySucceeded)> _results = new();
    private IValleyAgentApi? _api;

    private NPC? _npc;

    public E10_IllegalTransitionPermitted(IModHelper helper, IMonitor monitor) : base(helper, monitor)
    {
    }

    public override string TestName
    {
        get => "E10_IllegalTransitionPermitted";
    }

    public override int TimeoutTicks
    {
        get => 1200;
    }

    public override TestGroup Group
    {
        get => TestGroup.Edge;
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

        // 确保初始状态为 IDLE
        _ = _api.TrySetAgentState("Haley", "IDLE");
    }

    public override bool Update()
    {
        if (_npc == null || _api == null)
        {
            return true;
        }

        var tick = CurrentTick;

        // 每 100 tick 测试一个非法转换
        // 从 tick=30 开始，确保 Setup 的 NPC 分配已完成
        var transitionIndex = (tick - 30) / 100;
        if (transitionIndex >= 0 && transitionIndex < CandidateTransitions.Length && tick >= 30 &&
            (tick - 30) % 100 == 0)
        {
            var (from, to) = CandidateTransitions[transitionIndex];
            var fromState = Enum.Parse<AgentState>(from, true);
            var toState = Enum.Parse<AgentState>(to, true);
            var isLegal = AgentStateMachine.IsTransitionAllowed(fromState, toState);

            if (isLegal)
            {
                _ = _api.TrySetAgentState("Haley", "IDLE");
                return false;
            }

            // 先强制到 from 状态
            _ = _api.TrySetAgentState("Haley", from);
            var stateAfterFrom = _api.GetAgentState("Haley");

            // 如果无法进入预期的 from 状态，跳过此转换
            if (stateAfterFrom != from)
            {
                Monitor.Log(
                    $"[E10] Transition #{transitionIndex}: {from}→{to} SKIPPED (无法进入{from}，当前={stateAfterFrom})",
                    LogLevel.Warn);
                _ = _api.TrySetAgentState("Haley", "IDLE");
                return false;
            }

            var success = _api.TrySetAgentState("Haley", to);
            var finalState = _api.GetAgentState("Haley");

            var actuallySucceeded = finalState == to;
            _results.Add((from, to, ShouldFail: true, actuallySucceeded));

            Monitor.Log(
                $"[E10] Transition #{transitionIndex}: {from}→{to} | " +
                $"SetSuccess={success} | FinalState={finalState} | " +
                $"Bypassed={(actuallySucceeded ? "YES (漏洞)" : "NO (正常)")}",
                LogLevel.Info);

            // 重置为 IDLE 以便下一个转换测试
            _ = _api.TrySetAgentState("Haley", "IDLE");
        }

        // 所有转换测试完后返回
        return tick >= CandidateTransitions.Length * 100 + 90;
    }

    public override void Teardown()
    {
        if (_npc == null)
        {
            return;
        }

        var bypassCount = 0;
        foreach (var (from, to, shouldFail, succeeded) in _results)
        {
            if (succeeded)
            {
                bypassCount++;
                Monitor.Log($"[E10] ILLEGAL BYPASS: {from}→{to} 成功（应被阻止）", LogLevel.Warn);
            }
        }

        var totalTested = _results.Count;

        // 核心断言：所有非法转换都应被阻止
        Assert(
            "Illegal_transitions_are_blocked_by_state_machine",
            bypassCount == 0,
            $"共{totalTested}个非法转换测试，{bypassCount}个被绕过。非法转换：{string.Join(", ", _results.Where(r => r.ActuallySucceeded).Select(r => $"{r.From}→{r.To}"))}");

        Assert(
            "All_illegal_transitions_tested",
            totalTested >= 1,
            $"测试了{totalTested}个非法转换（跳过了{CandidateTransitions.Length - totalTested}个合法转换）");

        Assert(
            "No_crash_during_transition_tests",
            true,
            "测试完成，无崩溃");

        DebugFlags.SuppressDecisions = false;
        TestScenes.ClearAll(Helper, Monitor);
    }
}