using ValleyAgent.Chat;
using Xunit;

namespace ValleyAgent.UnitTests;

/// <summary>
///     E5-2 晨间喊话 4 层确定性路由单元测试。
///     验证：点名命中（大小写不敏感，支持显示名）；会话命中；跟随命中；已醒+好友最高命中；
///     4 层优先级顺序（点名 &gt; 会话 &gt; 跟随 &gt; 已醒+好友）；全员未醒 → 沉默；空输入/空候选 → 沉默。
///     设计文档：docs/ideas/e52-implementation-思路.md §3。
/// </summary>
public class MorningShoutRouterTests
{
    private static MorningShoutRouter NewRouter() => new();

    private static MorningShoutRouter.ShoutCandidate C(
        string name,
        string displayName,
        bool awake = true,
        bool inSession = false,
        bool following = false,
        int friendship = 0)
        => new(name, displayName, awake, inSession, following, friendship);

    [Fact]
    public void Resolve_NameMentioned_PicksMentionedNpc()
    {
        var router = NewRouter();
        var candidates = new List<MorningShoutRouter.ShoutCandidate>
        {
            C("Abigail", "阿比盖尔", true, friendship: 100),
            C("Haley", "海莉", true, friendship: 500)
        };

        var target = router.Resolve("海莉，早上好！", candidates);

        Assert.Equal("Haley", target);
    }

    [Fact]
    public void Resolve_NameMention_CaseInsensitive()
    {
        var router = NewRouter();
        var candidates = new List<MorningShoutRouter.ShoutCandidate>
        {
            C("Abigail", "阿比盖尔"),
            C("Haley", "海莉")
        };

        var target = router.Resolve("haley，你在吗", candidates);

        Assert.Equal("Haley", target);
    }

    [Fact]
    public void Resolve_NoNameButActiveSession_PicksSessionNpc()
    {
        var router = NewRouter();
        var candidates = new List<MorningShoutRouter.ShoutCandidate>
        {
            C("Abigail", "阿比盖尔", inSession: true, friendship: 50),
            C("Haley", "海莉", true, friendship: 800) // 好友更高但不在会话
        };

        var target = router.Resolve("早上好呀", candidates);

        Assert.Equal("Abigail", target); // 会话层优先于好友度
    }

    [Fact]
    public void Resolve_NoNameNoSession_FollowingNpcWins()
    {
        var router = NewRouter();
        var candidates = new List<MorningShoutRouter.ShoutCandidate>
        {
            C("Abigail", "阿比盖尔", true, friendship: 900),
            C("Sebastian", "塞巴斯蒂安", following: true, friendship: 10)
        };

        var target = router.Resolve("谁在附近呀", candidates);

        Assert.Equal("Sebastian", target); // 跟随层优先于已醒+好友
    }

    [Fact]
    public void Resolve_NoNameNoSessionNoFollow_AwakeHighestFriendshipWins()
    {
        var router = NewRouter();
        var candidates = new List<MorningShoutRouter.ShoutCandidate>
        {
            C("Abigail", "阿比盖尔", true, friendship: 300),
            C("Haley", "海莉", true, friendship: 800),
            C("Sebastian", "塞巴斯蒂安", false, friendship: 999) // 未醒不参与
        };

        var target = router.Resolve("有人在吗", candidates);

        Assert.Equal("Haley", target);
    }

    [Fact]
    public void Resolve_AllAsleep_ReturnsNull()
    {
        var router = NewRouter();
        var candidates = new List<MorningShoutRouter.ShoutCandidate>
        {
            C("Abigail", "阿比盖尔", false, friendship: 300),
            C("Haley", "海莉", false, friendship: 800)
        };

        var target = router.Resolve("有人在吗", candidates);

        Assert.Null(target); // 全员未醒 → 沉默
    }

    [Fact]
    public void Resolve_EmptyCandidates_ReturnsNull()
    {
        var router = NewRouter();

        var target = router.Resolve("有人在吗", new List<MorningShoutRouter.ShoutCandidate>());

        Assert.Null(target);
    }

    [Fact]
    public void Resolve_EmptyText_ReturnsNull()
    {
        var router = NewRouter();
        var candidates = new List<MorningShoutRouter.ShoutCandidate>
        {
            C("Haley", "海莉", true)
        };

        Assert.Null(router.Resolve("", candidates));
        Assert.Null(router.Resolve("   ", candidates));
    }

    [Fact]
    public void Resolve_AllFourLayersMiss_ReturnsNull()
    {
        var router = NewRouter();
        var candidates = new List<MorningShoutRouter.ShoutCandidate>
        {
            C("Haley", "海莉", false) // 未醒 + 无会话 + 无跟随
        };

        var target = router.Resolve("你好", candidates);

        Assert.Null(target); // 4 层全空 → 沉默（不调 LLM）
    }
}