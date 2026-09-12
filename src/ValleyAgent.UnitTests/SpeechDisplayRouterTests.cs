using ValleyAgent.Utils;
using Xunit;

namespace ValleyAgent.UnitTests;

/// <summary>
///     E2-3 长文本规则：SentenceSplitter 按句切分器单元测试。
///     覆盖中文句末分隔符、中英混合、英文句点防误切（3.14 / Mr.Smith）、
///     连续句末标点合并、空白裁剪与空文本。
///     设计依据：docs/ideas/2026-08-03-e23-long-text-rules.md。
/// </summary>
public class SentenceSplitterTests
{
    [Fact]
    public void Chinese_SplitsOnPeriodExclamationQuestion()
    {
        var sentences = SentenceSplitter.Split("今天天气真好。我们一起去玩吧！明天见？");

        Assert.Equal(3, sentences.Count);
        Assert.Equal("今天天气真好。", sentences[0]);
        Assert.Equal("我们一起去玩吧！", sentences[1]);
        Assert.Equal("明天见？", sentences[2]);
    }

    [Fact]
    public void Mixed_EnglishPeriodBeforeCjk_Splits()
    {
        // '.' 后紧跟 CJK → 跨语言句末
        var sentences = SentenceSplitter.Split("Good morning.今天好吗？Let's go!");

        Assert.Equal(3, sentences.Count);
        Assert.Equal("Good morning.", sentences[0]);
        Assert.Equal("今天好吗？", sentences[1]);
        Assert.Equal("Let's go!", sentences[2]);
    }

    [Fact]
    public void EnglishPeriod_BetweenDigitsOrLetters_DoesNotSplit()
    {
        // 小数 3.14 与缩写 Mr.Smith 不得被 '.' 误切
        var sentences = SentenceSplitter.Split("3.14 是个数。Mr.Smith 你好。");

        Assert.Equal(2, sentences.Count);
        Assert.Equal("3.14 是个数。", sentences[0]);
        Assert.Equal("Mr.Smith 你好。", sentences[1]);
    }

    [Fact]
    public void ConsecutiveTerminators_MergeIntoSingleBoundary()
    {
        // ！！与？？ 各自合并为一次切分，不产生空句
        var sentences = SentenceSplitter.Split("太好了！！真的吗？？");

        Assert.Equal(2, sentences.Count);
        Assert.Equal("太好了！！", sentences[0]);
        Assert.Equal("真的吗？？", sentences[1]);
    }

    [Fact]
    public void TrimsWhitespace_AndKeepsInnerPunctuation()
    {
        var sentences = SentenceSplitter.Split("  你好。  再见。  ");

        Assert.Equal(2, sentences.Count);
        Assert.Equal("你好。", sentences[0]);
        Assert.Equal("再见。", sentences[1]);
    }

    [Fact]
    public void QuoteBracketedSentence_PreservedAsWhole()
    {
        // 引号/括号不拆句：标点随句保留
        var sentences = SentenceSplitter.Split("「今天真棒！」她笑了。");

        Assert.Equal(2, sentences.Count);
        Assert.Equal("「今天真棒！」", sentences[0]);
        Assert.Equal("她笑了。", sentences[1]);
    }

    [Fact]
    public void EmptyOrWhitespace_ReturnsEmpty()
    {
        Assert.Empty(SentenceSplitter.Split(""));
        Assert.Empty(SentenceSplitter.Split("   "));
    }

    [Fact]
    public void CountSentences_MatchesSplitCount()
    {
        Assert.Equal(0, SentenceSplitter.CountSentences(""));
        Assert.Equal(1, SentenceSplitter.CountSentences("一句。"));
        Assert.Equal(2, SentenceSplitter.CountSentences("一句。两句。"));
        Assert.Equal(3, SentenceSplitter.CountSentences("一句。两句！三句？"));
    }
}

/// <summary>
///     E2-3 长文本规则：SpeechDisplayRouter 显示规则单元测试。
///     验证 ≤1 句 → 气泡路径、>1 句 → 聊天栏按句间隔弹出、气泡主开关门控、
///     句子数优先于距离（长文无视距离恒走分段聊天栏）。
///     通过 SetClock / SetChatSink 测试接缝在无游戏环境验证调度时序。
/// </summary>
public class SpeechDisplayRouterTests
{
    /// <summary>
    ///     重置路由静态状态：新时钟、新 sink、默认配置，并排空上个测试遗留的待发消息。
    /// </summary>
    private static void Reset(MutableClock clock, List<string> sink)
    {
        SpeechDisplayRouter.SetClock(clock.Now);
        SpeechDisplayRouter.SetChatSink((message, _) => sink.Add(message));
        SpeechDisplayRouter.SetBubbleSink(null);
        clock.NowMs = long.MaxValue;
        SpeechDisplayRouter.Tick(); // 排空遗留待发消息
        sink.Clear();
        clock.NowMs = 0;
        SpeechDisplayRouter.ApplyConfig(true, SpeechDisplayRouter.DefaultLongTextIntervalMs);
    }

    // ─────────── 纯决策：句子数优先于距离 ───────────

    [Fact]
    public void DecideDisplay_ShortNear_Bubble() => Assert.Equal(SpeechDisplayMode.Bubble,
        SpeechDisplayRouter.DecideDisplay("你好。", true, 2f, 8));

    [Fact]
    public void DecideDisplay_ShortFar_ChatBar() => Assert.Equal(SpeechDisplayMode.ChatBar,
        SpeechDisplayRouter.DecideDisplay("你好。", true, 10f, 8));

    [Fact]
    public void DecideDisplay_ShortCrossMap_ChatBar() => Assert.Equal(SpeechDisplayMode.ChatBar,
        SpeechDisplayRouter.DecideDisplay("你好。", false, 2f, 8));

    [Fact]
    public void DecideDisplay_Long_IgnoresDistance_AlwaysStaged()
    {
        // 长文（>1 句）即使近在咫尺也不上气泡，恒走分段聊天栏
        Assert.Equal(SpeechDisplayMode.ChatBarStaged, SpeechDisplayRouter.DecideDisplay("今天天气真好。我们去吧！", true, 1f, 8));
        Assert.Equal(SpeechDisplayMode.ChatBarStaged,
            SpeechDisplayRouter.DecideDisplay("今天天气真好。我们去吧！", false, 100f, 8));
    }

    [Fact]
    public void CanUseBubble_ShortEnabled_True()
    {
        var clock = new MutableClock();
        Reset(clock, new List<string>());
        SpeechDisplayRouter.ApplyConfig(true, 2000);

        Assert.True(SpeechDisplayRouter.CanUseBubble("你好。"));
        Assert.False(SpeechDisplayRouter.IsLongText("你好。"));
    }

    [Fact]
    public void CanUseBubble_Long_False()
    {
        var clock = new MutableClock();
        Reset(clock, new List<string>());

        Assert.False(SpeechDisplayRouter.CanUseBubble("今天天气真好。我们去吧！"));
        Assert.True(SpeechDisplayRouter.IsLongText("今天天气真好。我们去吧！"));
    }

    [Fact]
    public void CanUseBubble_MasterSwitchOff_False()
    {
        var clock = new MutableClock();
        Reset(clock, new List<string>());
        SpeechDisplayRouter.ApplyConfig(false, 2000);

        // 气泡主开关关闭：短句也不进气泡（降级到聊天栏）
        Assert.False(SpeechDisplayRouter.CanUseBubble("你好。"));
    }

    // ─────────── 聊天栏调度：短句立即 / 长文按句间隔 ───────────

    [Fact]
    public void SpeakToChatBar_Short_ImmediateSingle()
    {
        var clock = new MutableClock();
        var sink = new List<string>();
        Reset(clock, sink);

        SpeechDisplayRouter.SpeakToChatBar("Abigail", "今天天气真好。", default);

        Assert.Single(sink);
        Assert.Equal("Abigail: 今天天气真好。", sink[0]);
    }

    [Fact]
    public void SpeakToChatBar_Long_ScheduledAtIntervals()
    {
        var clock = new MutableClock();
        var sink = new List<string>();
        Reset(clock, sink);
        SpeechDisplayRouter.ApplyConfig(true, 2000);
        clock.NowMs = 1000; // 调度基准时刻（Reset 会把时钟归零，须在 Reset 之后设置）

        SpeechDisplayRouter.SpeakToChatBar("Abigail", "今天天气真好。我们一起去玩吧！明天见？", default);

        // 调度后尚未 Tick → 不弹
        Assert.Empty(sink);

        // 首句立即到期（delay = 0 → due 1000）
        clock.NowMs = 1000;
        SpeechDisplayRouter.Tick();
        Assert.Single(sink);
        Assert.Equal("Abigail: 今天天气真好。", sink[0]);

        // 未到第二句间隔（1000 + 2000 = 3000）→ 不弹
        clock.NowMs = 2500;
        SpeechDisplayRouter.Tick();
        Assert.Single(sink);

        // 到 3000ms → 第二句
        clock.NowMs = 3000;
        SpeechDisplayRouter.Tick();
        Assert.Equal(2, sink.Count);
        Assert.Equal("Abigail: 我们一起去玩吧！", sink[1]);

        // 到 5000ms（1000 + 2×2000）→ 第三句，顺序保持句子 0..n
        clock.NowMs = 5000;
        SpeechDisplayRouter.Tick();
        Assert.Equal(3, sink.Count);
        Assert.Equal("Abigail: 明天见？", sink[2]);
    }

    [Fact]
    public void Tick_FiresOnlyDueMessages_InOrder()
    {
        var clock = new MutableClock { NowMs = 0 };
        var sink = new List<string>();
        Reset(clock, sink);
        SpeechDisplayRouter.ApplyConfig(true, 3000);

        SpeechDisplayRouter.SpeakToChatBar("Abigail", "一句。两句！三句？", default);

        // 0ms：第一句到期；第二句 3000ms、第三句 6000ms
        clock.NowMs = 0;
        SpeechDisplayRouter.Tick();
        Assert.Single(sink);
        Assert.Equal("Abigail: 一句。", sink[0]);

        // 2000ms：仍只有第一句
        clock.NowMs = 2000;
        SpeechDisplayRouter.Tick();
        Assert.Single(sink);

        // 3500ms：第二句到
        clock.NowMs = 3500;
        SpeechDisplayRouter.Tick();
        Assert.Equal(2, sink.Count);
        Assert.Equal("Abigail: 两句！", sink[1]);

        // 6000ms：第三句到，顺序不乱
        clock.NowMs = 6000;
        SpeechDisplayRouter.Tick();
        Assert.Equal(3, sink.Count);
        Assert.Equal("Abigail: 三句？", sink[2]);
    }

    [Fact]
    public void IntervalZero_FiresAllOnNextTick()
    {
        var clock = new MutableClock();
        var sink = new List<string>();
        Reset(clock, sink);
        SpeechDisplayRouter.ApplyConfig(true, 0);
        clock.NowMs = 500; // 调度基准（Reset 之后设置）

        SpeechDisplayRouter.SpeakToChatBar("Abigail", "一句。两句！三句？", default);
        Assert.Empty(sink); // 尚未 Tick

        clock.NowMs = 500;
        SpeechDisplayRouter.Tick();
        Assert.Equal(3, sink.Count);
        Assert.Equal("Abigail: 一句。", sink[0]);
        Assert.Equal("Abigail: 两句！", sink[1]);
        Assert.Equal("Abigail: 三句？", sink[2]);
    }

    /// <summary>可控时钟：手动推进 NowMs 观察调度结果。</summary>
    private sealed class MutableClock
    {
        public long NowMs;

        public long Now() => NowMs;
    }
}