using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ValleyAgent.Infrastructure;

namespace ValleyAgent.Friendship;

/// <summary>
///     好感度系统 — C# 端仅做数据记录和直接变更。
///     方案 B：好感度 delta 由 dialogue_response.friendshipDelta 携带，C# 不再独立调 TS server 评估。
/// </summary>
public class FriendshipSystem : IDisposable
{
    // 边际递减系数：第N次交互的倍率 = 1.0 - (N-1) * 0.25，最低0.1
    private const double DiminishingStep = 0.25;
    private const double DiminishingFloor = 0.1;

    // 每日交互计数：(npcName, dateKey, interactionType) -> count
    private readonly Dictionary<(string, string, InteractionType), int> _dailyTracker = new();

    // 历史记录
    private readonly Dictionary<string, List<FriendshipChangeRecord>> _history = new();

    // 单次交互最大变化量（可通过 ModConfig.MaxFriendshipChangePerInteraction 配置）
    private readonly int _maxChangePerInteraction;
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    // ApplyWithExternalDeltaAsync 调用序号：不同 NPC 的调用可并发，
    // StuckOperationTracker 的 opId 必须每次唯一（固定 id 会被并发覆盖，见方法内注释）
    private static long _externalDeltaSequence;

    private bool _disposed;

    public FriendshipSystem(int maxChangePerInteraction = 80)
    {
        _maxChangePerInteraction = maxChangePerInteraction;
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>评估开始事件</summary>
    public event EventHandler<FriendshipChangeContext>? OnEvaluationStarted;

    /// <summary>评估完成事件</summary>
    public event EventHandler<FriendshipChangeResult>? OnEvaluationCompleted;

    /// <summary>历史更新事件</summary>
    public event EventHandler<FriendshipChangeRecord>? OnHistoryUpdated;

    public event Action<string, int, string>? OnFriendshipChanged;

    /// <summary>
    ///     应用外部评估的好感度变化（方案 B：delta 来自 dialogue_response.friendshipDelta）。
    ///     复用边际递减 + 特殊事件倍率 + clamp + 历史记录逻辑，不调 TS server。
    ///     取代 EvaluateAndApplyChangeAsync（已删，消除 friendship_eval 死管道）。
    /// </summary>
    public async Task<FriendshipChangeResult> ApplyWithExternalDeltaAsync(
        FriendshipChangeContext context,
        int delta,
        string reason,
        CancellationToken cancellationToken = default)
    {
        if (context == null)
        {
            return new FriendshipChangeResult { Success = false, ErrorMessage = "Context cannot be null" };
        }

        if (string.IsNullOrWhiteSpace(context.NpcName))
        {
            return new FriendshipChangeResult { Success = false, ErrorMessage = "NPC name cannot be empty" };
        }

        if (context.CurrentFriendshipPoints < 0)
        {
            return new FriendshipChangeResult
                { Success = false, ErrorMessage = "Current friendship points cannot be negative" };
        }

        if (context.CurrentFriendshipPoints > context.MaxFriendshipPoints)
        {
            return new FriendshipChangeResult
                { Success = false, ErrorMessage = "Current friendship points exceed maximum" };
        }

        if (string.IsNullOrWhiteSpace(context.CurrentDateKey))
        {
            return new FriendshipChangeResult { Success = false, ErrorMessage = "Date key cannot be empty" };
        }

        // 候选 3（后台线程写好感度路径）的唯一主动观测面（2026-09-11 生产化仪器）：
        // Begin/End 让停滞看门狗在信号量等待/事件回调卡死 >60s 时点名本方法并落 stuck-*.dmp。
        // opId 带自增序号：不同 NPC 的调用可并发（信号量在 Begin 之后才等待），
        // 固定 opId 会被并发调用互相覆盖、先结束的把仍在跑的记录 End 掉——停滞检测就漏了
        var opId = $"friendship-external-delta#{Interlocked.Increment(ref _externalDeltaSequence)}";
        StuckOperationTracker.Begin(opId, $"{context.NpcName} delta={delta} reason={reason}");
        try
        {
            OnEvaluationStarted?.Invoke(this, context);

            await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var previousPoints = context.CurrentFriendshipPoints;
                var appliedChange = delta;
                var modifiers = new List<string>();

                // 应用特殊事件倍率
                var multiplier = GetSpecialEventMultiplier(context.SpecialEvents, modifiers);

                // 应用边际递减
                var dailyKey = (context.NpcName, context.CurrentDateKey, context.InteractionType);
                var interactionCount = _dailyTracker.GetValueOrDefault(dailyKey, 0);
                var diminishingMultiplier = 1.0;
                if (interactionCount > 0)
                {
                    diminishingMultiplier = Math.Max(DiminishingFloor, 1.0 - interactionCount * DiminishingStep);
                }

                // 计算最终变化
                var finalChange = (int)Math.Round(appliedChange * multiplier * diminishingMultiplier);

                // 防止非零 delta 被边际递减取整为 0
                if (finalChange == 0 && appliedChange != 0)
                {
                    finalChange = appliedChange > 0 ? 1 : -1;
                }

                // 限制单次交互最大变化量
                finalChange = Math.Clamp(finalChange, -_maxChangePerInteraction, _maxChangePerInteraction);

                // 限制在合法范围内
                var newPoints = Math.Clamp(previousPoints + finalChange, 0, context.MaxFriendshipPoints);
                var actualChange = newPoints - previousPoints;

                // 更新每日计数器
                _dailyTracker[dailyKey] = interactionCount + 1;

                var result = new FriendshipChangeResult
                {
                    Success = true,
                    PreviousPoints = previousPoints,
                    NewPoints = newPoints,
                    AppliedChange = actualChange,
                    Reason = reason,
                    AppliedModifiers = modifiers,
                    DiminishingReturnsApplied = interactionCount > 0
                };

                // 记录历史
                var record = new FriendshipChangeRecord
                {
                    NpcName = context.NpcName,
                    ChangeAmount = actualChange,
                    Reason = reason,
                    InteractionType = context.InteractionType,
                    DateKey = context.CurrentDateKey
                };

                if (!_history.ContainsKey(context.NpcName))
                {
                    _history[context.NpcName] = new List<FriendshipChangeRecord>();
                }

                _history[context.NpcName].Add(record);

                OnHistoryUpdated?.Invoke(this, record);
                OnEvaluationCompleted?.Invoke(this, result);
                OnFriendshipChanged?.Invoke(context.NpcName, newPoints, reason);

                return result;
            }
            finally
            {
                _semaphore.Release();
            }
        }
        finally
        {
            StuckOperationTracker.End(opId);
        }
    }

    /// <summary>直接应用好感度变化（不经过 TS 服务器，用于游戏事件等确定性变更）</summary>
    public FriendshipChangeResult ApplyDirectChange(FriendshipChangeContext context, int change, string reason)
    {
        var previousPoints = context.CurrentFriendshipPoints;
        var newPoints = Math.Clamp(previousPoints + change, 0, context.MaxFriendshipPoints);
        var actualChange = newPoints - previousPoints;

        var result = new FriendshipChangeResult
        {
            Success = true,
            PreviousPoints = previousPoints,
            NewPoints = newPoints,
            AppliedChange = actualChange,
            Reason = reason
        };

        // 记录历史
        var record = new FriendshipChangeRecord
        {
            NpcName = context.NpcName,
            ChangeAmount = actualChange,
            Reason = reason,
            InteractionType = context.InteractionType,
            DateKey = context.CurrentDateKey
        };

        if (!_history.ContainsKey(context.NpcName))
        {
            _history[context.NpcName] = new List<FriendshipChangeRecord>();
        }

        _history[context.NpcName].Add(record);

        // 更新每日计数
        var dailyKey = (context.NpcName, context.CurrentDateKey, context.InteractionType);
        _dailyTracker[dailyKey] = _dailyTracker.GetValueOrDefault(dailyKey, 0) + 1;

        OnHistoryUpdated?.Invoke(this, record);
        OnEvaluationCompleted?.Invoke(this, result);
        OnFriendshipChanged?.Invoke(context.NpcName, newPoints, reason);

        return result;
    }

    /// <summary>获取指定 NPC 的历史记录</summary>
    public IReadOnlyList<FriendshipChangeRecord> GetHistory(string npcName)
    {
        return _history.TryGetValue(npcName, out var records)
            ? records.AsReadOnly()
            : Array.Empty<FriendshipChangeRecord>();
    }

    /// <summary>获取全部历史记录</summary>
    public Dictionary<string, List<FriendshipChangeRecord>> GetAllHistory() => new(_history);

    /// <summary>清除所有历史记录</summary>
    public void ClearHistory() => _history.Clear();

    /// <summary>获取每日交互次数</summary>
    public int GetDailyInteractionCount(string npcName, string dateKey, InteractionType type)
    {
        var key = (npcName, dateKey, type);
        return _dailyTracker.GetValueOrDefault(key, 0);
    }

    /// <summary>清除每日计数器</summary>
    public void ClearDailyTracker() => _dailyTracker.Clear();

    private static double GetSpecialEventMultiplier(SpecialEventModifiers modifiers, List<string> modifierDescriptions)
    {
        var multiplier = 1.0;

        if (modifiers.HasFlag(SpecialEventModifiers.Birthday))
        {
            multiplier *= 3;
            modifierDescriptions.Add("Birthday (3x)");
        }

        if (modifiers.HasFlag(SpecialEventModifiers.Festival))
        {
            multiplier *= 2;
            modifierDescriptions.Add("Festival (2x)");
        }

        if (modifiers.HasFlag(SpecialEventModifiers.RainyDay))
        {
            multiplier *= 0.5;
            modifierDescriptions.Add("RainyDay (0.5x)");
        }

        if (modifiers.HasFlag(SpecialEventModifiers.FirstMeeting))
        {
            multiplier *= 2;
            modifierDescriptions.Add("FirstMeeting (2x)");
        }

        return multiplier;
    }

    public void RestoreHistory(Dictionary<string, List<FriendshipChangeRecord>> history)
    {
        _history.Clear();
        if (history == null)
        {
            return;
        }

        foreach (var kvp in history)
        {
            _history[kvp.Key] = new List<FriendshipChangeRecord>(kvp.Value);
        }
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        if (disposing)
        {
            _semaphore.Dispose();
        }

        _disposed = true;
    }
}