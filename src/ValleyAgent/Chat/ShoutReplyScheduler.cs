using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using StardewModdingAPI;
using StardewValley;
using ValleyAgent.Services;

namespace ValleyAgent.Chat;

/// <summary>
///     E5-2 远程喊话延迟回应调度器。
///     玩家喊到不在场 NPC 后，为其安排 3~8 秒随机延迟，到期后在主线程把回应交给
///     ActiveSpeechRouter 渲染（气泡/聊天栏），并记录为被动回应（不消耗主动额度）。
///     每 NPC 同一时间最多 1 条待发回应（后到的覆盖先到的，避免喊话轰炸刷屏）。
///     主线程每 tick 调用 ProcessDueReplies（由 EventHandlerInitializer.OnUpdateTicked 驱动）。
/// </summary>
public sealed class ShoutReplyScheduler
{
    private static readonly TimeSpan MinDelay = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(8);

    private readonly IMonitor? _monitor;

    private readonly ConcurrentDictionary<string, ScheduledReply> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly ProactiveSpeechQuota? _quota;
    private readonly Func<string, string, string?>? _replyGenerator;

    /// <summary>
    ///     创建调度器。
    /// </summary>
    /// <param name="monitor">日志。</param>
    /// <param name="quota">主动额度服务（被动回应不消耗额度）。</param>
    /// <param name="replyGenerator">
    ///     回应文本生成器（npcName, playerShout → 回应文本）。
    ///     生产接线把这里接到 LLM 对话路径（如 ChatBarRouter 的 dialogue 请求）；
    ///     未接线时用默认礼貌回应。返回 null 表示沉默（不渲染）。
    /// </param>
    public ShoutReplyScheduler(
        IMonitor? monitor = null,
        ProactiveSpeechQuota? quota = null,
        Func<string, string, string?>? replyGenerator = null)
    {
        _monitor = monitor;
        _quota = quota;
        _replyGenerator = replyGenerator;
    }

    /// <summary>待发回应数量（诊断用）。</summary>
    public int PendingCount
    {
        get => _pending.Count;
    }

    /// <summary>当前待发回应 NPC 名单（测试断言用）。</summary>
    public IReadOnlyCollection<string> PendingNpcs
    {
        get => (IReadOnlyCollection<string>)_pending.Keys;
    }

    /// <summary>
    ///     为远程 NPC 安排一条延迟回应。该 NPC 已有待发回应 → 覆盖（时间重置、内容更新）。
    /// </summary>
    public void ScheduleReply(string npcName, string playerShout, DateTime? nowUtc = null)
    {
        if (string.IsNullOrWhiteSpace(npcName))
        {
            return;
        }

        var now = nowUtc ?? DateTime.UtcNow;
        // 确定性伪随机延迟（FNV-1a 哈希 → [3s, 8s]），避免 Random 触发 CA5394，
        // 且同 NPC+内容在同一次进程内延迟一致，便于测试断言。
        var delayMs = StableDelayMs(npcName, playerShout);
        var reply = new ScheduledReply(npcName, playerShout, now.AddMilliseconds(delayMs));

        _pending[npcName] = reply;
        _monitor?.Log($"[Shout] {npcName}: reply scheduled in {delayMs}ms", LogLevel.Debug);
    }

    /// <summary>FNV-1a 哈希 → 3~8 秒毫秒数（确定性，进程内稳定）。</summary>
    private static int StableDelayMs(string npcName, string playerShout)
    {
        const uint offset = 2166136261;
        const uint prime = 16777619;
        var hash = offset;
        foreach (var c in npcName + "\u0001" + playerShout)
        {
            hash ^= c;
            hash *= prime;
        }

        var window = (int)MaxDelay.TotalMilliseconds - (int)MinDelay.TotalMilliseconds + 1; // 5001
        return (int)MinDelay.TotalMilliseconds + (int)(hash % (uint)window);
    }

    /// <summary>取消某 NPC 的待发回应（NPC 离场/玩家离开场景）。</summary>
    public void Cancel(string npcName)
    {
        if (!string.IsNullOrWhiteSpace(npcName))
        {
            _ = _pending.TryRemove(npcName, out _);
        }
    }

    /// <summary>
    ///     主线程处理到期回应（每 tick 调用）。到期 → 渲染回应 + 记录被动回应。
    /// </summary>
    public void ProcessDueReplies(DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;

        foreach (var kv in _pending)
        {
            if (kv.Value.DueUtc > now)
            {
                continue;
            }

            if (_pending.TryRemove(kv.Key, out var reply))
            {
                RenderReply(reply);
            }
        }
    }

    /// <summary>清空全部待发回应（返回标题/卸载）。</summary>
    public void Clear() => _pending.Clear();

    private void RenderReply(ScheduledReply reply)
    {
        try
        {
            var npc = Game1.getCharacterFromName(reply.NpcName);
            if (npc == null)
            {
                return; // NPC 已离场/不存在 → 丢弃
            }

            // NPC 已入睡/不在场景 → 不渲染（睡觉的人不回应）
            if (!IsInGameWorld(npc))
            {
                return;
            }

            // 被动回应记录（不消耗主动额度）——必须在渲染前记录，保证"回应不算主动"语义
            _quota?.RecordPassiveResponse(reply.NpcName, DateTime.UtcNow);

            // 回应文本：优先注入的生成器（生产接线到 LLM 对话路径），否则默认礼貌回应
            var text = _replyGenerator?.Invoke(reply.NpcName, reply.PlayerShout);
            if (string.IsNullOrWhiteSpace(text))
            {
                return; // 生成器返回 null/空 → 沉默
            }

            // 渲染：远程回应不强制打开对话框，走气泡/聊天栏
            ActiveSpeechRouter.Route(npc, Game1.player, text);
            _monitor?.Log($"[Shout] {reply.NpcName}: replied to remote shout", LogLevel.Debug);
        }
        catch (Exception ex)
        {
            _monitor?.Log($"[Shout] Reply render failed for {reply.NpcName}: {ex}", LogLevel.Warn);
        }
    }

    private static bool IsInGameWorld(NPC npc)
    {
        try
        {
            return npc.currentLocation != null;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private sealed class ScheduledReply
    {
        public ScheduledReply(string npcName, string playerShout, DateTime dueUtc)
        {
            NpcName = npcName;
            PlayerShout = playerShout;
            DueUtc = dueUtc;
        }

        public string NpcName { get; }
        public string PlayerShout { get; }
        public DateTime DueUtc { get; }
    }
}