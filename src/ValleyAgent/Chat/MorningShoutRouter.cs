using System;
using System.Collections.Generic;
using System.Linq;

namespace ValleyAgent.Chat;

/// <summary>
///     E5-2 晨间喊话 4 层确定性路由（纯逻辑，无 Game1 依赖，可单测）。
///     4 层消歧：点名 → 当前会话 → 跟随/雇佣 → 已醒 + 好友度最高。
///     第 4 层在存在任何已醒候选时必然命中，因此 4 层路由完全确定性；
///     全员未醒 / 无候选 → 返回 null（沉默，不调 LLM）。
///     IRemoteShoutRouter（LLM 兜底）保留为合并期接线扩展点——确定性层无法
///     区分（如多个同好友度已醒 NPC）时由调用方决定是否调用，默认不调用。
///     设计文档：docs/ideas/e52-implementation-思路.md §3。
/// </summary>
public sealed class MorningShoutRouter
{
    /// <summary>
    ///     4 层确定性路由入口。返回 0（静默）或 1 个目标 NPC 名。同步、无副作用、可单测。
    /// </summary>
    public string? Resolve(
        string shoutText,
        IReadOnlyList<ShoutCandidate> candidates)
    {
        if (string.IsNullOrWhiteSpace(shoutText) || candidates == null || candidates.Count == 0)
        {
            return null;
        }

        var list = candidates.ToList();

        // Layer 1: 名字提及（OrdinalIgnoreCase 匹配 Name 或 DisplayName）
        foreach (var c in list)
        {
            if (ContainsName(shoutText, c.NpcName) || ContainsName(shoutText, c.DisplayName))
            {
                return c.NpcName;
            }
        }

        // Layer 2: 当前会话 NPC
        var session = list.FirstOrDefault(c => c.IsInActiveSession);
        if (session != null)
        {
            return session.NpcName;
        }

        // Layer 3: 跟随/雇佣中
        var following = list.FirstOrDefault(c => c.IsFollowingOrHired);
        if (following != null)
        {
            return following.NpcName;
        }

        // Layer 4: 已醒 + 好友度最高（存在任何已醒候选即命中）
        var awake = list.Where(c => c.IsAwake).OrderByDescending(c => c.FriendshipPoints).ToList();
        if (awake.Count > 0)
        {
            return awake[0].NpcName;
        }

        // 全员未醒 / 无候选 → 沉默
        return null;
    }

    private static bool ContainsName(string text, string? name)
    {
        return !string.IsNullOrEmpty(name)
               && text.Contains(name, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Agent NPC 候选信息（由调用方从 AgentService 装配）。</summary>
    public sealed record ShoutCandidate(
        string NpcName,
        string DisplayName,
        bool IsAwake,
        bool IsInActiveSession,
        bool IsFollowingOrHired,
        int FriendshipPoints);
}