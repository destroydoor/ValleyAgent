using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewValley;
using ValleyAgent.Inventory;
using ValleyAgent.Services;

namespace ValleyAgent.Goals;

/// <summary>
///     Goal 公共实现（阶段 2，spec §3.1 / design doc §7.3-7.4）。
///     管理状态机流转、开始时间/超时、背包基线快照、位置卡死检测；
///     子类实现 TickCore（委托 Handler 干活）与 EvaluateTermination（纯终止判定）。
/// </summary>
public abstract class GoalBase : IGoal
{
    private int _startGameMinutes;
    private int _timeoutGameMinutes;
    private Vector2 _lastPosition;
    private int _consecutiveNoMoveTicks;

    protected GoalBase(
        string npcName,
        GoalType type,
        bool reportBack,
        string callId,
        int timeoutGameMinutes,
        int stuckTickThreshold)
    {
        NpcName = npcName ?? throw new ArgumentNullException(nameof(npcName));
        Type = type;
        ReportBack = reportBack;
        CallId = callId;
        _timeoutGameMinutes = Math.Max(1, timeoutGameMinutes);
        _stuckTickThreshold = Math.Max(1, stuckTickThreshold);
    }

    /// <inheritdoc />
    public string NpcName { get; }

    /// <inheritdoc />
    public GoalType Type { get; }

    /// <inheritdoc />
    public GoalStatus Status { get; private set; } = GoalStatus.NotStarted;

    /// <inheritdoc />
    public bool ReportBack { get; }

    /// <inheritdoc />
    public string Reason { get; private set; } = string.Empty;

    /// <summary>创建目标时的 TS 工具调用 ID（汇报 action_result 时透传，供 TS 端关联）。</summary>
    public string CallId { get; }

    /// <inheritdoc />
    public int CollectedCount { get; protected set; }

    /// <summary>动作完成计数（water_crops 浇地次数 / fight 击杀数由子类映射到 CollectedCount）。</summary>
    public int ActionsCompleted { get; protected set; }

    /// <inheritdoc />
    public int ElapsedGameMinutes { get; private set; }

    /// <summary>终止条件要求的数量（quantity 参数：木头/矿石/采集物数量、浇地次数、击杀数）。</summary>
    public int RequiredQuantity { get; protected set; }

    /// <summary>终止条件要求的物品 ID（mine/forage 的 targetItemId，chop_tree 固定为 Wood）。</summary>
    public string TargetItemId { get; protected set; } = string.Empty;

    /// <summary>背包基线快照（Start 时捕获，按 itemId 聚合）。</summary>
    protected Dictionary<string, int> BaselineCounts { get; } = new(StringComparer.OrdinalIgnoreCase);

    private int _stuckTickThreshold;

    /// <inheritdoc />
    public virtual void Start(AgentInstance agent, NPC npc)
    {
        if (Status == GoalStatus.Executing)
        {
            return;
        }

        if (agent == null || npc == null)
        {
            return;
        }

        Status = GoalStatus.Executing;
        Reason = string.Empty;
        CollectedCount = 0;
        ActionsCompleted = 0;
        _startGameMinutes = GoalTime.TimeOfDayToMinutes(Game1.timeOfDay);
        BaselineCounts.Clear();
        foreach (var kvp in TakeBackpackSnapshot(agent.Inventory))
        {
            BaselineCounts[kvp.Key] = kvp.Value;
        }

        _lastPosition = npc.Tile;
        _consecutiveNoMoveTicks = 0;
        OnStarted(agent, npc);
    }

    /// <inheritdoc />
    public virtual void Tick(AgentInstance agent, NPC npc, int currentTick)
    {
        if (Status != GoalStatus.Executing)
        {
            return;
        }

        if (npc == null || agent == null)
        {
            return;
        }

        // 游戏分钟流逝（基于 timeOfDay，跨天回绕）
        ElapsedGameMinutes = GoalTime.ElapsedMinutes(
            _startGameMinutes, GoalTime.TimeOfDayToMinutes(Game1.timeOfDay));

        // 全局超时 → 失败
        if (GoalTime.IsTimedOut(ElapsedGameMinutes, _timeoutGameMinutes))
        {
            Fail("timeout");
            return;
        }

        // 位置卡死：连续 N tick 位置不变且不在执行动作 → 失败（复用 HandleStuck 的 120-tick 语义）
        TrackPosition(npc.Tile);
        if (IsStuck(_consecutiveNoMoveTicks, _stuckTickThreshold))
        {
            Fail("stuck");
            return;
        }

        TickCore(agent, npc, currentTick);

        if (Status != GoalStatus.Executing)
        {
            return;
        }

        // 终止判定：背包差量（物品收集量模型，spec §1.1）
        var currentCounts = TakeBackpackSnapshot(agent.Inventory);
        UpdateCollected(currentCounts);
        if (EvaluateTermination(currentCounts))
        {
            Complete();
        }
    }

    /// <inheritdoc />
    public virtual void Cancel(string reason)
    {
        if (Status is GoalStatus.NotStarted or GoalStatus.Executing)
        {
            Status = GoalStatus.Cancelled;
            Reason = reason ?? string.Empty;
        }
    }

    /// <summary>标记完成（仅执行中可转移）。</summary>
    public void Complete()
    {
        if (Status == GoalStatus.Executing)
        {
            Status = GoalStatus.Complete;
        }
    }

    /// <summary>标记失败（仅执行中可转移）。</summary>
    public void Fail(string reason)
    {
        if (Status == GoalStatus.Executing)
        {
            Status = GoalStatus.Failed;
            Reason = reason ?? string.Empty;
        }
    }

    /// <inheritdoc />
    public string DescribeProgress()
    {
        var statusLabel = Status switch
        {
            GoalStatus.NotStarted => "NotStarted",
            GoalStatus.Executing => "Executing",
            GoalStatus.Complete => "Complete",
            GoalStatus.Failed => $"Failed({Reason})",
            GoalStatus.Cancelled => "Cancelled",
            _ => Status.ToString(),
        };

        var detail = DescribeProgressCore();
        return $"{GoalTypeToWire(Type)}: {detail} [{statusLabel}]";
    }

    /// <summary>子类进度描述（如 "已获得 6/10 木头"）。</summary>
    protected abstract string DescribeProgressCore();

    /// <summary>Start 时子类初始化（记录基线环境计数等）。</summary>
    protected abstract void OnStarted(AgentInstance agent, NPC npc);

    /// <summary>每 tick 推进：委托 Handler 干活 / 自身动作循环。</summary>
    protected abstract void TickCore(AgentInstance agent, NPC npc, int currentTick);

    /// <summary>纯终止判定：基于背包差量快照（子类用静态纯函数实现，单测可注入）。</summary>
    protected abstract bool EvaluateTermination(IReadOnlyDictionary<string, int> currentCounts);

    /// <summary>刷新 CollectedCount（默认 = 所有物品获得量之和；子类可覆盖为指定物品）。</summary>
    protected virtual void UpdateCollected(IReadOnlyDictionary<string, int> currentCounts)
    {
        var delta = GoalCompletionEvaluator.ComputeCollectedCounts(BaselineCounts, currentCounts);
        CollectedCount = delta.Values.Sum();
    }

    // ─── 静态纯逻辑（单测可直接驱动） ───────────────────────────────

    /// <summary>位置卡死判定：连续无移动 tick ≥ 阈值。</summary>
    public static bool IsStuck(int consecutiveNoMoveTicks, int thresholdTicks) =>
        consecutiveNoMoveTicks >= thresholdTicks;

    /// <summary>背包 → itemId 数量快照（QualifiedItemId 优先，退化到 ItemId）。</summary>
    public static Dictionary<string, int> TakeBackpackSnapshot(AgentInventory? inventory)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (inventory == null)
        {
            return result;
        }

        foreach (var item in inventory.GetAllItems())
        {
            if (item == null)
            {
                continue;
            }

            var id = item.QualifiedItemId ?? item.ItemId ?? string.Empty;
            if (string.IsNullOrEmpty(id))
            {
                continue;
            }

            result[id] = result.GetValueOrDefault(id) + item.Stack;
        }

        return result;
    }

    /// <summary>GoalType → wire 字符串（"chop_tree" 等）。</summary>
    public static string GoalTypeToWire(GoalType type) => type switch
    {
        GoalType.ChopTree => "chop_tree",
        GoalType.Mine => "mine",
        GoalType.WaterCrops => "water_crops",
        GoalType.Fight => "fight",
        GoalType.Forage => "forage",
        _ => type.ToString().ToLowerInvariant(),
    };

    /// <summary>wire 字符串 → GoalType（未知类型返回 null）。</summary>
    public static GoalType? ParseGoalType(string type)
    {
        if (string.IsNullOrWhiteSpace(type))
        {
            return null;
        }

        return type.Trim().ToLowerInvariant() switch
        {
            "chop_tree" or "chop" or "chopping" => GoalType.ChopTree,
            "mine" or "mining" => GoalType.Mine,
            "water_crops" or "water" or "watering" => GoalType.WaterCrops,
            "fight" or "combat" or "attack" => GoalType.Fight,
            "forage" or "foraging" or "gather" => GoalType.Forage,
            _ => null,
        };
    }

    /// <summary>参数解析：quantity（默认 1，钳制 ≥ 1）与 targetItemId（可选）。纯逻辑，单测可注入字典。</summary>
    public static (int Quantity, string? TargetItemId) ParseParameters(IReadOnlyDictionary<string, object>? parameters)
    {
        if (parameters == null)
        {
            return (1, null);
        }

        var quantity = 1;
        if (parameters.TryGetValue("quantity", out var qtyObj) && qtyObj != null && int.TryParse(qtyObj.ToString(), out var parsed))
        {
            quantity = Math.Max(1, parsed);
        }

        var targetItemId = parameters.TryGetValue("targetItemId", out var targetObj)
                           && targetObj is string targetStr && !string.IsNullOrWhiteSpace(targetStr)
            ? targetStr.Trim()
            : null;
        return (quantity, targetItemId);
    }

    /// <summary>位置追踪：位置变化重置计数，不变累计。</summary>
    private void TrackPosition(Vector2 current)
    {
        if (current == _lastPosition)
        {
            _consecutiveNoMoveTicks++;
        }
        else
        {
            _lastPosition = current;
            _consecutiveNoMoveTicks = 0;
        }
    }
}
