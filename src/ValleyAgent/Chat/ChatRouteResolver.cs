using System;
using System.Collections.Generic;
using System.Linq;
using StardewModdingAPI;
using ValleyAgent.Infrastructure;

namespace ValleyAgent.Chat;

/// <summary>
///     在场 NPC 的玩家视角摘要（路由输入，纯数据）。
///     只包含与玩家同地图的村民（不在场 NPC 听不到普通聊天，§3.1）。
/// </summary>
public sealed class ChatPresence
{
    public ChatPresence(string name, string? displayName, int distanceTiles, bool isFollowing,
        DateTime lastInteractionUtc)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        DisplayName = displayName;
        DistanceTiles = distanceTiles;
        IsFollowing = isFollowing;
        LastInteractionUtc = lastInteractionUtc;
    }

    /// <summary>NPC 内部名（英文，如 "Haley"）。</summary>
    public string Name { get; }

    /// <summary>NPC 本地化显示名（如中文环境 "海莉"）。可为空。</summary>
    public string? DisplayName { get; }

    /// <summary>到玩家的曼哈顿距离（格）。</summary>
    public int DistanceTiles { get; }

    /// <summary>是否处于 FOLLOW（跟随玩家）状态。</summary>
    public bool IsFollowing { get; }

    /// <summary>最近一次与玩家交互的时间（UTC）；从未交互为 <see cref="DateTime.MinValue" />。</summary>
    public DateTime LastInteractionUtc { get; }
}

/// <summary>
///     路由参数（默认值与需求 §3.1.1 对齐：30 秒窗口、~8 格附近、群体最多 2 个接话）。
///     Talkativeness 是注入点：E2-2 用好感度确定性代理，E5-3 换成逐人设话痨度配置。
/// </summary>
public sealed class ChatRouteOptions
{
    public TimeSpan RecentInteractionWindow { get; init; } = TimeSpan.FromSeconds(30);

    public int NearbyTiles { get; init; } = 8;

    public int GroupResponseMax { get; init; } = 2;

    /// <summary>话痨度（0~1）：群体称呼时按此降序选接话人。</summary>
    public Func<string, double> Talkativeness { get; init; } = _ => 0.5;
}

/// <summary>路由结果：单个目标（IsGroup=false）或群体目标（最多 GroupResponseMax 个）。</summary>
public sealed class ChatRoute
{
    public ChatRoute(string npcName)
    {
        TargetNpcs = new[] { npcName };
        IsGroup = false;
    }

    public ChatRoute(IReadOnlyList<string> npcNames, bool isGroup)
    {
        TargetNpcs = npcNames;
        IsGroup = isGroup;
    }

    public IReadOnlyList<string> TargetNpcs { get; }

    public bool IsGroup { get; }
}

/// <summary>
///     四层路由消歧 + 沉默权预过滤（E2-2，纯逻辑可单测）。
///     路由是确定性规则（便宜）；"要不要接话"的智能判定只花在被路由命中的 NPC 上（LLM）。
/// </summary>
public static class ChatRouteResolver
{
    // 群体称呼关键词（中文 + 英文）。
    private static readonly string[] GroupAddressKeywords =
    {
        "你们", "大家", "everyone", "everybody", "guys", "folks"
    };

    /// <summary>
    ///     四层路由：名字提及 → 群体称呼 → 当前会话 → 跟随者 → 最近交互+距离 / 最近在场。
    ///     无在场 NPC 或无法判定 → null（不路由；远程喊话是 E5）。
    /// </summary>
    public static ChatRoute? Resolve(
        string text,
        string? sessionNpc,
        IReadOnlyList<ChatPresence> present,
        ChatRouteOptions? options = null,
        DateTime? nowUtc = null,
        IMonitor? monitor = null)
    {
        if (string.IsNullOrWhiteSpace(text) || present == null || present.Count == 0)
        {
            return null;
        }

        var opts = options ?? new ChatRouteOptions();
        var now = nowUtc ?? DateTime.UtcNow;
        if (opts.GroupResponseMax < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "GroupResponseMax must be >= 1.");
        }

        // 第 1 层：名字提及 —— 具体优先于群体，永远有效。
        var named = TryMatchName(text, present);
        if (named != null)
        {
            return new ChatRoute(named);
        }

        // 群体称呼：附近 NPC 按话痨度取最多 GroupResponseMax 个，其余沉默（防刷屏）。
        if (IsGroupAddress(text))
        {
            var responders = present
                .Where(p => p.DistanceTiles <= opts.NearbyTiles)
                .OrderByDescending(p => SafeTalkativeness(opts.Talkativeness, p.Name, monitor))
                .ThenBy(p => p.DistanceTiles)
                .Take(opts.GroupResponseMax)
                .Select(p => p.Name)
                .ToList();
            return responders.Count > 0 ? new ChatRoute(responders, true) : null;
        }

        // 第 2 层：当前会话（需在场，任意距离，只要同地图）。
        if (!string.IsNullOrEmpty(sessionNpc)
            && present.Any(p => string.Equals(p.Name, sessionNpc, StringComparison.OrdinalIgnoreCase)))
        {
            return new ChatRoute(sessionNpc!);
        }

        // 第 3 层：跟随者（Phase 4 雇佣状态未建，只做 FOLLOW）。
        var follower = present
            .Where(p => p.IsFollowing)
            .OrderBy(p => p.DistanceTiles)
            .FirstOrDefault();
        if (follower != null)
        {
            return new ChatRoute(follower.Name);
        }

        // 第 4 层：30 秒内交互过且附近（~8 格）→ 取最近；否则 → 最近在场 NPC。
        var recentNearby = present
            .Where(p => p.LastInteractionUtc != DateTime.MinValue
                        && now - p.LastInteractionUtc <= opts.RecentInteractionWindow
                        && p.DistanceTiles <= opts.NearbyTiles)
            .OrderBy(p => p.DistanceTiles)
            .FirstOrDefault();
        if (recentNearby != null)
        {
            return new ChatRoute(recentNearby.Name);
        }

        var nearest = present.OrderBy(p => p.DistanceTiles).FirstOrDefault();
        return nearest != null ? new ChatRoute(nearest.Name) : null;
    }

    /// <summary>
    ///     名字提及匹配：返回在场 NPC 中名字出现在文本里的那个，否则 null。
    ///     同时匹配内部 Name 与本地化 DisplayName；候选按名长降序保证确定性。
    ///     拉丁名做词边界（前后不得紧邻拉丁字母），CJK 名直接子串。
    /// </summary>
    public static string? TryMatchName(string text, IReadOnlyList<ChatPresence> present)
    {
        if (string.IsNullOrEmpty(text) || present == null)
        {
            return null;
        }

        // 名长降序：更长/更具体的名字优先（避免短名截胡长名的子串场景）。
        var candidates = new List<(string Name, ChatPresence Presence)>();
        foreach (var p in present)
        {
            candidates.Add((p.Name, p));
            if (!string.IsNullOrWhiteSpace(p.DisplayName))
            {
                candidates.Add((p.DisplayName!, p));
            }
        }

        candidates.Sort((a, b) => b.Name.Length.CompareTo(a.Name.Length));

        foreach (var candidate in candidates)
        {
            if (ContainsName(text, candidate.Name))
            {
                return candidate.Presence.Name;
            }
        }

        return null;
    }

    /// <summary>内容明显不是对话（纯标点/符号/表情）→ 不应触发回应（沉默权预过滤）。</summary>
    public static bool IsTrivialNonDialogue(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        return text.All(c => !char.IsLetterOrDigit(c));
    }

    /// <summary>是否群体称呼（"你们""大家"等）。</summary>
    public static bool IsGroupAddress(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        foreach (var keyword in GroupAddressKeywords)
        {
            if (text.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsName(string text, string name)
    {
        var index = text.IndexOf(name, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return false;
        }

        // 拉丁名词边界：名字两端不得紧邻拉丁字母（"Haley" 不匹配 "HaleyX"），
        // 但 CJK 字符之间不做边界（"小海莉" 能命中 "海莉"，"Haley你" 也能命中 "Haley"）。
        if (IsLatinOnly(name))
        {
            if (index > 0 && IsLatinLetter(text[index - 1]))
            {
                return false;
            }

            var end = index + name.Length;
            if (end < text.Length && IsLatinLetter(text[end]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsLatinOnly(string s)
    {
        foreach (var c in s)
        {
            if (!IsLatinLetter(c))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsLatinLetter(char c)
        => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');

    private static double SafeTalkativeness(Func<string, double> talkativeness, string npcName, IMonitor? monitor)
    {
        try
        {
            return Math.Clamp(talkativeness(npcName), 0.0, 1.0);
        }
        catch (Exception ex)
        {
            // issue #26 批③：话痨度查询失败 → 回退中性 0.5，路由继续但排序质量降级；节流防刷屏
            if (QueueTelemetry.ShouldWarn("chat-route:talkativeness"))
            {
                monitor?.Log($"[ChatRouteResolver] talkativeness lookup failed for '{npcName}' — falling back to neutral 0.5: {ex}", LogLevel.Warn);
            }

            return 0.5;
        }
    }
}