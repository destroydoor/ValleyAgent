using System;
using System.Collections.Generic;
using System.Linq;
using StardewValley;
using StardewValley.Monsters;
using ValleyAgent.Handlers;
using ValleyAgent.Services;

namespace ValleyAgent.Goals;

/// <summary>
///     fight Goal：清除区域怪物 / 达成击杀数（spec §3.2 终止条件："fight 清除怪物 / 到时间"）。
///
///     <para>委托模式：每 tick 调 <see cref="FightHandler.Update"/>（自动扫描最近可攻击怪物、寻路、
///     攻击；FightHandler 的掉落走玩家背包而非 NPC 背包，故无法用物品收集量判定）。
///     终止信号 = 起始怪物数 - 当前怪物数（击杀数）≥ quantity，或区域怪物清零。</para>
///
///     <para>数量语义：quantity 视为"击杀数"要求（默认 1 = 清空区域或至少击杀 1 只）。</para>
/// </summary>
public class FightGoal : GoalBase
{
    private readonly FightHandler _fightHandler;
    private int _baselineMonsterCount;
    private int _currentMonsterCount;

    public FightGoal(
        string npcName,
        bool reportBack,
        string callId,
        int quantity,
        int timeoutGameMinutes,
        int stuckTickThreshold,
        FightHandler fightHandler)
        : base(npcName, GoalType.Fight, reportBack, callId, timeoutGameMinutes, stuckTickThreshold)
    {
        _fightHandler = fightHandler ?? throw new ArgumentNullException(nameof(fightHandler));
        RequiredQuantity = Math.Max(1, quantity);
    }

    /// <summary>
    ///     纯终止判定（单测入口）：击杀数 = baseline - current（钳 0）；
    ///     达成条件：击杀数 ≥ requiredKills 或 当前区域怪物清零。
    /// </summary>
    public static bool IsTerminationMet(int baselineMonsters, int currentMonsters, int requiredKills) =>
        GoalCompletionEvaluator.IsKillCountMet(baselineMonsters, currentMonsters, requiredKills);

    /// <inheritdoc />
    protected override string DescribeProgressCore()
    {
        var killed = GoalCompletionEvaluator.ComputeKillCount(_baselineMonsterCount, _currentMonsterCount);
        return $"已击杀 {killed}/{RequiredQuantity} 怪物";
    }

    /// <inheritdoc />
    protected override void OnStarted(AgentInstance agent, NPC npc)
    {
        // 基线怪物数：Start 时当前地点的可攻击怪物数（GoalBase 先取背包快照，再调本方法）
        _npc = npc;
        _baselineMonsterCount = CountTargetableMonsters();
        _currentMonsterCount = _baselineMonsterCount;
    }

    /// <inheritdoc />
    protected override void TickCore(AgentInstance agent, NPC npc, int currentTick)
    {
        // 记录 NPC 引用：EvaluateTermination / UpdateCollected 无 npc 参数，需从字段取当前地点
        _npc = npc;
        _fightHandler.Update(npc, agent, currentTick);
    }

    /// <inheritdoc />
    protected override bool EvaluateTermination(IReadOnlyDictionary<string, int> currentCounts)
    {
        // fight 无物品产出：终止信号用怪物数差量，忽略背包参数
        _currentMonsterCount = CountTargetableMonsters();
        CollectedCount = GoalCompletionEvaluator.ComputeKillCount(_baselineMonsterCount, _currentMonsterCount);
        return IsTerminationMet(_baselineMonsterCount, _currentMonsterCount, RequiredQuantity);
    }

    /// <inheritdoc />
    protected override void UpdateCollected(IReadOnlyDictionary<string, int> currentCounts)
    {
        // 与 EvaluateTermination 一致：CollectedCount = 击杀数
        _currentMonsterCount = CountTargetableMonsters();
        CollectedCount = GoalCompletionEvaluator.ComputeKillCount(_baselineMonsterCount, _currentMonsterCount);
    }

    /// <summary>当前地点的可攻击怪物数（与 FightHandler.IsMonsterTargetable 语义一致：存活）。</summary>
    private int CountTargetableMonsters()
    {
        if (_npc?.currentLocation == null)
        {
            return 0;
        }

        return _npc.currentLocation.characters.OfType<Monster>().Count(m => m.Health > 0);
    }

    private NPC? _npc;
}
