#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using ValleyAgent.StateMachine;
using ValleyAgent.Utils;

namespace ValleyAgent.TestMod.Tests.Functional;

/// <summary>
///     刁钻功能测试：审计所有非法状态转换是否被正确阻止。
///     背景：AgentStateMachine.AllowedTransitions 定义了合法转换规则。
///     TrySetAgentState API 内部调用 ForceTransition 绕过这些规则。
///     本测试系统性地遍历所有状态对，记录哪些非法转换被 API 允许。
///     这是一个"扩散式"审计——不针对单个漏洞，而是对整个转换矩阵做全覆盖扫描。
///     预期结果（如果 ForceTransition 被使用）：
///     - 所有 8×8-合法数 个转换都会成功 → 状态机规则形同虚设
///     预期结果（如果 TryTransition 被使用）：
///     - 只有 AllowedTransitions 中的转换会成功
/// </summary>
public class Func_IllegalTransitionAudit : V3TestBase
{
    // 所有状态
    private static readonly string[] AllStates =
        { "IDLE", "FOLLOW", "FIGHT", "FARM", "FORAGE", "MINE", "TALK" };

    private readonly List<TransitionRecord> _records = new();
    private IValleyAgentApi? _api;

    private NPC? _npc;
    private int _setupRetries;
    private int _skippedPairs;
    private int _stateIndex;

    public Func_IllegalTransitionAudit(IModHelper helper, IMonitor monitor) : base(helper, monitor)
    {
    }

    public override string TestName
    {
        get => "Func_IllegalTransitionAudit";
    }

    public override int TimeoutTicks
    {
        get => 2000;
    }

    public override TestGroup Group
    {
        get => TestGroup.Functional;
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

        // 诊断：打印已注册状态，确认 FIGHT/FARM 等已注册
        var registered = _api.GetRegisteredStates("Haley");
        Monitor?.Log($"[Func_Audit] Registered states: {string.Join(", ", registered)}", LogLevel.Info);

        _stateIndex = 0;
        _setupRetries = 0;
        _skippedPairs = 0;
    }

    public override bool Update()
    {
        if (_npc == null || _api == null)
        {
            return true;
        }

        var tick = CurrentTick;

        // 每 30 tick 测试一个 (from, to) 对
        if (tick > 0 && tick % 30 == 0 && _stateIndex < AllStates.Length * AllStates.Length)
        {
            var fromIdx = _stateIndex / AllStates.Length;
            var toIdx = _stateIndex % AllStates.Length;
            var from = AllStates[fromIdx];
            var to = AllStates[toIdx];
            var fromState = Enum.Parse<AgentState>(from, true);
            var toState = Enum.Parse<AgentState>(to, true);
            var isLegal = AgentStateMachine.IsTransitionAllowed(fromState, toState);

            // 跳到 from 状态（带重试，handler可能在同一tick改变状态）
            _ = _api.TrySetAgentState("Haley", from);
            var stateAfterFrom = _api.GetAgentState("Haley");

            if (stateAfterFrom != from)
            {
                _setupRetries++;
                if (_setupRetries <= 3)
                {
                    _ = _api.TrySetAgentState("Haley", "IDLE");
                    return false;
                }

                _skippedPairs++;
                _setupRetries = 0;
                _stateIndex++;
                _ = _api.TrySetAgentState("Haley", "IDLE");
                return false;
            }

            _setupRetries = 0;

            // 尝试转换到 to
            var setSuccess = _api.TrySetAgentState("Haley", to);
            var finalState = _api.GetAgentState("Haley");
            var actuallySucceeded = finalState == to;

            // 自转（from==to）是 no-op，不审计
            var isSelfTransition = from == to;
            if (!isSelfTransition)
            {
                _records.Add(new TransitionRecord(from, to, isLegal, actuallySucceeded));
            }

            // 如果非法转换成功，记录
            if (actuallySucceeded && !isLegal)
            {
                Monitor.Log(
                    $"[Func_Audit] ILLEGAL: {from}→{to} succeeded (should be blocked)",
                    LogLevel.Warn);
            }
            else if (!actuallySucceeded && isLegal)
            {
                Monitor.Log(
                    $"[Func_Audit] FALSE NEGATIVE: {from}→{to} failed (should be legal)",
                    LogLevel.Warn);
            }

            _stateIndex++;

            // 重置为 IDLE
            _ = _api.TrySetAgentState("Haley", "IDLE");
        }

        // 全部扫描完成后断言
        if (_stateIndex >= AllStates.Length * AllStates.Length && tick >= _stateIndex * 30 + 60)
        {
            var totalPairs = _records.Count;
            var legalPairs = _records.Count(r => r.IsLegal);
            var illegalPairs = _records.Count(r => !r.IsLegal);

            var illegalSuccesses = _records.Where(r => !r.IsLegal && r.Succeeded).ToList();
            var legalFailures = _records.Where(r => r.IsLegal && !r.Succeeded).ToList();

            // 构建转换矩阵概要
            var bypassSummary = string.Join(", ",
                illegalSuccesses.Select(r => $"{r.From}→{r.To}"));

            // 核心断言
            Assert(
                "No_illegal_transition_succeeds",
                illegalSuccesses.Count == 0,
                $"总{illegalPairs}个非法转换中{illegalSuccesses.Count}个成功: {bypassSummary}。" +
                "如果>0，说明 API 使用 ForceTransition 绕过了 AllowedTransitions 规则。");

            Assert(
                "All_legal_transitions_succeed",
                legalFailures.Count == 0,
                $"总{legalPairs}个合法转换中{legalFailures.Count}个失败: " +
                $"{string.Join(", ", legalFailures.Select(r => $"{r.From}→{r.To}"))}");

            Assert(
                "Full_transition_matrix_covered",
                totalPairs >= AllStates.Length * (AllStates.Length - 1),
                $"覆盖 {totalPairs}/{AllStates.Length * (AllStates.Length - 1)} 个状态对（自转已跳过）");

            Assert(
                "Audit_complete_without_crash",
                true,
                $"审计总转换={totalPairs}, 合法={legalPairs}, 非法={illegalPairs}, 绕过={illegalSuccesses.Count}, 误拦={legalFailures.Count}, 跳过={_skippedPairs}");

            return true;
        }

        return false;
    }

    public override void Teardown()
    {
        DebugFlags.SuppressDecisions = false;
        TestScenes.ClearAll(Helper, Monitor);
    }

    private sealed record TransitionRecord(string From, string To, bool IsLegal, bool Succeeded);
}