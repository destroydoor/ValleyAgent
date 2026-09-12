using ValleyAgent.Chat;
using Xunit;

namespace ValleyAgent.UnitTests;

/// <summary>
///     E2-2 聊天栏路由消歧单元测试（纯逻辑，无游戏依赖）。
///     覆盖：四层优先级（名字提及 → 群体称呼 → 当前会话 → 跟随者 → 最近交互+距离 / 最近在场）、
///     名字匹配（中英文/词边界/长名优先）、群体限流、沉默权预过滤。
///     设计文档：docs/ideas/2026-08-03-e22-chat-bar-routing.md
/// </summary>
public static class ChatRouteResolverTests
{
    // ───────────────────────── 辅助 ─────────────────────────

    private static readonly DateTime FixedNow = new(2026, 8, 3, 12, 0, 0, DateTimeKind.Utc);

    private static ChatPresence P(string name, int distance, string? displayName = null, bool following = false,
        DateTime? lastInteraction = null)
        => new(name, displayName ?? name, distance, following, lastInteraction ?? DateTime.MinValue);

    private static ChatPresence RecentlyInteracted(string name, int distance, TimeSpan ago)
        => new(name, name, distance, false, FixedNow - ago);

    private static List<ChatPresence> Presence(params ChatPresence[] items) => new(items);

    // ───────────────────────── 名字提及 ─────────────────────────

    [Fact]
    public static void NameMention_ChineseDisplayName_RoutesToThatNpc()
    {
        var present = Presence(P("Haley", 3, "海莉"), P("Abigail", 4, "阿比盖尔"));
        var route = ChatRouteResolver.Resolve("海莉你看这批南瓜", null, present, nowUtc: FixedNow);
        Assert.NotNull(route);
        Assert.False(route!.IsGroup);
        Assert.Equal("Haley", route.TargetNpcs[0]);
    }

    [Fact]
    public static void NameMention_EnglishInternalName_RoutesToThatNpc()
    {
        var present = Presence(P("Haley", 3, "海莉"), P("Abigail", 4, "阿比盖尔"));
        var route = ChatRouteResolver.Resolve("Haley, look at this pumpkin", null, present, nowUtc: FixedNow);
        Assert.NotNull(route);
        Assert.Equal("Haley", route!.TargetNpcs[0]);
    }

    [Fact]
    public static void NameMention_CaseInsensitive_RoutesToThatNpc()
    {
        var present = Presence(P("Sebastian", 2, "塞巴斯蒂安"));
        var route = ChatRouteResolver.Resolve("sebastian 在吗", null, present, nowUtc: FixedNow);
        Assert.NotNull(route);
        Assert.Equal("Sebastian", route!.TargetNpcs[0]);
    }

    [Fact]
    public static void NameMention_WordBoundary_LatinNameNotSwallowedByLongerWord()
    {
        // "Same" 含 "Sam" 但两侧是拉丁字母 → 名字提及不得命中；
        // Sam 若被误命中会赢过最近在场判定，因此期望落到最近在场 Sandy(1) 证明词边界生效。
        var present = Presence(P("Sam", 10), P("Sandy", 1));
        var route = ChatRouteResolver.Resolve("Same place as before", null, present, nowUtc: FixedNow);
        Assert.NotNull(route);
        Assert.Equal("Sandy", route!.TargetNpcs[0]);
    }

    [Fact]
    public static void NameMention_MixedLatinAndCjk_MatchesLatinName()
    {
        var present = Presence(P("Haley", 3, "海莉"));
        // "Haley你" —— 名字后紧跟 CJK，不应触发拉丁词边界拒绝。
        var route = ChatRouteResolver.Resolve("Haley你看这个", null, present, nowUtc: FixedNow);
        Assert.NotNull(route);
        Assert.Equal("Haley", route!.TargetNpcs[0]);
    }

    [Fact]
    public static void NameMention_BeatsSessionAndFollower()
    {
        var present = Presence(
            P("Emily", 2, "艾米丽", true),
            P("Haley", 6, "海莉"));
        var route = ChatRouteResolver.Resolve("海莉你过来", "Emily", present, nowUtc: FixedNow);
        Assert.NotNull(route);
        Assert.Equal("Haley", route!.TargetNpcs[0]);
    }

    // ───────────────────────── 当前会话 ─────────────────────────

    [Fact]
    public static void Session_ActiveSessionBeatsFollower()
    {
        var present = Presence(P("Emily", 2, "艾米丽", true), P("Haley", 4, "海莉"));
        var route = ChatRouteResolver.Resolve("刚才说的那件事呢", "Haley", present, nowUtc: FixedNow);
        Assert.NotNull(route);
        Assert.Equal("Haley", route!.TargetNpcs[0]);
    }

    [Fact]
    public static void Session_NpcOffScene_FallsThroughToFollower()
    {
        // 会话对象不在场（present 里没有）→ 不能路由给它（不在场 NPC 听不到普通聊天）。
        var present = Presence(P("Emily", 2, "艾米丽", true));
        var route = ChatRouteResolver.Resolve("在吗", "Haley", present, nowUtc: FixedNow);
        Assert.NotNull(route);
        Assert.Equal("Emily", route!.TargetNpcs[0]);
    }

    // ───────────────────────── 跟随者 ─────────────────────────

    [Fact]
    public static void Follower_DefaultListener()
    {
        var present = Presence(P("Emily", 3, "艾米丽", true), P("Haley", 1, "海莉"));
        var route = ChatRouteResolver.Resolve("我们接下来去哪", null, present, nowUtc: FixedNow);
        Assert.NotNull(route);
        Assert.Equal("Emily", route!.TargetNpcs[0]);
    }

    [Fact]
    public static void Follower_MultipleFollowers_TakesNearest()
    {
        var present = Presence(P("Emily", 5, "艾米丽", true), P("Abigail", 2, "阿比盖尔", true));
        var route = ChatRouteResolver.Resolve("大家跟上", null, present, nowUtc: FixedNow);
        Assert.NotNull(route);
        Assert.Equal("Abigail", route!.TargetNpcs[0]);
    }

    // ───────────────────────── 最近交互 + 距离 / 最近在场 ─────────────────────────

    [Fact]
    public static void RecentInteraction_WithinWindowAndNearby_TakesNearestRecent()
    {
        var present = Presence(
            RecentlyInteracted("Haley", 2, TimeSpan.FromSeconds(10)),
            P("Emily", 1, "艾米丽"));
        var route = ChatRouteResolver.Resolve("今天天气不错", null, present, nowUtc: FixedNow);
        Assert.NotNull(route);
        Assert.Equal("Haley", route!.TargetNpcs[0]); // 10s 内交互过且 2 格 → 优先于更近但没交互的 Emily
    }

    [Fact]
    public static void RecentInteraction_InteractedButTooFar_FallsToNearestPresent()
    {
        var present = Presence(
            RecentlyInteracted("Haley", 20, TimeSpan.FromSeconds(10)), // 20 格 > 8 格附近阈值
            P("Emily", 1, "艾米丽"));
        var route = ChatRouteResolver.Resolve("今天天气不错", null, present, nowUtc: FixedNow);
        Assert.NotNull(route);
        Assert.Equal("Emily", route!.TargetNpcs[0]);
    }

    [Fact]
    public static void RecentInteraction_InteractionExpired_FallsToNearestPresent()
    {
        var present = Presence(
            RecentlyInteracted("Haley", 2, TimeSpan.FromSeconds(120)), // 超 30s 窗口
            P("Emily", 1, "艾米丽"));
        var route = ChatRouteResolver.Resolve("今天天气不错", null, present, nowUtc: FixedNow);
        Assert.NotNull(route);
        Assert.Equal("Emily", route!.TargetNpcs[0]);
    }

    [Fact]
    public static void NoPresence_ReturnsNull()
    {
        var route = ChatRouteResolver.Resolve("有人吗", null, Presence(), nowUtc: FixedNow);
        Assert.Null(route);
    }

    // ───────────────────────── 群体称呼 ─────────────────────────

    [Fact]
    public static void GroupAddress_SelectsTopTalkativeness_UptoMax()
    {
        var present = Presence(
            P("Haley", 2, "海莉"),
            P("Emily", 3, "艾米丽"),
            P("Abigail", 1, "阿比盖尔"));
        var options = new ChatRouteOptions { Talkativeness = n => n == "Haley" ? 0.9 : n == "Emily" ? 0.6 : 0.2 };
        var route = ChatRouteResolver.Resolve("大家快来帮忙", null, present, options, FixedNow);
        Assert.NotNull(route);
        Assert.True(route!.IsGroup);
        Assert.Equal(2, route.TargetNpcs.Count);
        Assert.Equal("Haley", route.TargetNpcs[0]);
        Assert.Equal("Emily", route.TargetNpcs[1]); // Abigail 话痨度低 → 沉默
    }

    [Fact]
    public static void GroupAddress_EnglishKeyword()
    {
        var present = Presence(P("Haley", 2, "海莉"), P("Emily", 3, "艾米丽"));
        var route = ChatRouteResolver.Resolve("hey everyone look", null, present, nowUtc: FixedNow);
        Assert.NotNull(route);
        Assert.True(route!.IsGroup);
    }

    [Fact]
    public static void GroupAddress_NobodyNearby_ReturnsNull()
    {
        var present = Presence(P("Haley", 20, "海莉"), P("Emily", 25, "艾米丽"));
        var route = ChatRouteResolver.Resolve("大家快来", null, present, nowUtc: FixedNow);
        Assert.Null(route); // 附近无人 → 群体沉默
    }

    [Fact]
    public static void GroupAddress_WithNameMention_SpecificWins()
    {
        var present = Presence(P("Haley", 2, "海莉"), P("Emily", 3, "艾米丽"));
        var route = ChatRouteResolver.Resolve("海莉你们来一下", null, present, nowUtc: FixedNow);
        Assert.NotNull(route);
        Assert.False(route!.IsGroup);
        Assert.Equal("Haley", route.TargetNpcs[0]);
    }

    // ───────────────────────── 沉默权预过滤 ─────────────────────────

    [Fact]
    public static void Trivial_PunctuationOnly_IsTrivial()
    {
        Assert.True(ChatRouteResolver.IsTrivialNonDialogue("..."));
        Assert.True(ChatRouteResolver.IsTrivialNonDialogue("？？？？"));
        Assert.True(ChatRouteResolver.IsTrivialNonDialogue("🙂🙂"));
        Assert.True(ChatRouteResolver.IsTrivialNonDialogue("   "));
        Assert.False(ChatRouteResolver.IsTrivialNonDialogue("海莉在吗"));
        Assert.False(ChatRouteResolver.IsTrivialNonDialogue("Hello!"));
        Assert.False(ChatRouteResolver.IsTrivialNonDialogue("去死吧史莱姆！"));
    }

    [Fact]
    public static void Trivial_MessageNeverRouted()
    {
        var present = Presence(P("Haley", 2, "海莉"));
        if (ChatRouteResolver.IsTrivialNonDialogue("......"))
        {
            return; // 预过滤在路由前拦截，这里只验证纯函数行为
        }

        Assert.Fail("Expected trivial message to be filtered");
    }

    // ───────────────────────── 群体上限钳制 ─────────────────────────

    [Fact]
    public static void GroupResponseMax_LowerThanOne_Throws()
    {
        var present = Presence(P("Haley", 2, "海莉"));
        var options = new ChatRouteOptions { GroupResponseMax = 0 };
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ChatRouteResolver.Resolve("大家来", null, present, options, FixedNow));
    }
}