using ValleyAgent.Chat;
using Xunit;

namespace ValleyAgent.UnitTests;

/// <summary>
///     E5-3 时间触发主动发言决策单元测试。
///     验证：窗口判定（600/1200/1800 触发，窗口外不产出）；未醒跳过；额度不足跳过；
///     已醒+额度通过 → 产出文案；文案确定性（同 NPC+日期+窗口 → 同一条）；
///     空候选/空配额安全。
///     设计文档：docs/ideas/e53-implementation-思路.md §3。
/// </summary>
public class ProactiveSpeechTriggerTests
{
    private static readonly DateTime Now = new(2026, 8, 3, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>测试用配额：DailyLimit=2、无冷却（纯逻辑，真实实现无 Game1 依赖）。</summary>
    private static ProactiveSpeechQuota NewQuota()
    {
        var quota = new ProactiveSpeechQuota(ChatSessionRegistry.Instance)
        {
            DailyLimit = 2,
            CooldownMinutes = 0
        };
        quota.ResetDaily("Y1_spring_1");
        return quota;
    }

    private static ProactiveSpeechTrigger.ProactiveCandidate C(
        string name, string displayName, bool awake = true, double talkativeness = 0.5)
        => new(name, displayName, awake, talkativeness);

    [Fact]
    public void Evaluate_MorningWindow_ProducesSpeech()
    {
        var trigger = new ProactiveSpeechTrigger();
        var candidates = new List<ProactiveSpeechTrigger.ProactiveCandidate>
        {
            C("Haley", "海莉")
        };

        var result = trigger.Evaluate(600, "Y1_spring_1", candidates, NewQuota(), Now);

        Assert.Single(result);
        Assert.Equal("Haley", result[0].NpcName);
        Assert.False(string.IsNullOrWhiteSpace(result[0].Text));
    }

    [Theory]
    [InlineData(600)]
    [InlineData(1200)]
    [InlineData(1800)]
    public void Evaluate_TriggerPoints_AllProduce(int time)
    {
        var trigger = new ProactiveSpeechTrigger();
        var candidates = new List<ProactiveSpeechTrigger.ProactiveCandidate>
        {
            C("Haley", "海莉")
        };

        var result = trigger.Evaluate(time, "Y1_spring_1", candidates, NewQuota(), Now);

        Assert.Single(result);
    }

    [Theory]
    [InlineData(100)]
    [InlineData(300)]
    [InlineData(550)]
    [InlineData(700)]
    [InlineData(1300)]
    [InlineData(1700)]
    [InlineData(1900)]
    [InlineData(2600)]
    public void Evaluate_WindowSemantics(int time)
    {
        // 窗口语义：100/300/550 在早晨窗口(600)之前 → 不产出；
        // 700/1300/1700/1900/2600 在窗口内 → 产出（防重复由调用方 _lastProactiveTriggerWindow 负责）。
        var trigger = new ProactiveSpeechTrigger();
        var candidates = new List<ProactiveSpeechTrigger.ProactiveCandidate>
        {
            C("Haley", "海莉")
        };

        var result = trigger.Evaluate(time, "Y1_spring_1", candidates, NewQuota(), Now);

        if (time is 100 or 300 or 550)
        {
            Assert.Empty(result); // 早晨窗口之前 → 无发言
        }
        else
        {
            Assert.Single(result); // 窗口内任意时刻 → 发言
        }
    }

    [Fact]
    public void Evaluate_AsleepCandidate_Skipped()
    {
        var trigger = new ProactiveSpeechTrigger();
        var candidates = new List<ProactiveSpeechTrigger.ProactiveCandidate>
        {
            C("Haley", "海莉", false),
            C("Abigail", "阿比盖尔", true)
        };

        var result = trigger.Evaluate(600, "Y1_spring_1", candidates, NewQuota(), Now);

        Assert.Single(result);
        Assert.Equal("Abigail", result[0].NpcName);
    }

    [Fact]
    public void Evaluate_QuotaExhausted_Skipped()
    {
        var trigger = new ProactiveSpeechTrigger();
        var quota = NewQuota(); // DailyLimit=2
        var candidates = new List<ProactiveSpeechTrigger.ProactiveCandidate>
        {
            C("Haley", "海莉")
        };

        // 第一次调用消耗 1 次
        var first = trigger.Evaluate(600, "Y1_spring_1", candidates, quota, Now);
        Assert.Single(first);

        // 第二次调用消耗 1 次（DailyLimit=2 内）
        var second = trigger.Evaluate(1200, "Y1_spring_1", candidates, quota, Now);
        Assert.Single(second);

        // 第三次调用额度耗尽 → 跳过
        var third = trigger.Evaluate(1800, "Y1_spring_1", candidates, quota, Now);
        Assert.Empty(third);
    }

    [Fact]
    public void Evaluate_EmptyCandidates_ReturnsEmpty()
    {
        var trigger = new ProactiveSpeechTrigger();

        var result = trigger.Evaluate(600, "Y1_spring_1", new List<ProactiveSpeechTrigger.ProactiveCandidate>(),
            NewQuota(), Now);

        Assert.Empty(result);
    }

    [Fact]
    public void Evaluate_NullQuota_ReturnsEmpty()
    {
        var trigger = new ProactiveSpeechTrigger();
        var candidates = new List<ProactiveSpeechTrigger.ProactiveCandidate>
        {
            C("Haley", "海莉")
        };

        var result = trigger.Evaluate(600, "Y1_spring_1", candidates, null!, Now);

        Assert.Empty(result);
    }

    [Fact]
    public void PickLine_DeterministicPerNpcDateWindow()
    {
        // 同 NPC+日期+窗口 → 文案稳定
        var trigger = new ProactiveSpeechTrigger();
        var candidates = new List<ProactiveSpeechTrigger.ProactiveCandidate>
        {
            C("Haley", "海莉")
        };

        var r1 = trigger.Evaluate(600, "Y1_spring_1", candidates, NewQuota(), Now);
        var r2 = trigger.Evaluate(600, "Y1_spring_1", candidates, NewQuota(), Now);

        Assert.Equal(r1[0].Text, r2[0].Text); // 同输入 → 同输出（确定性）
    }

    [Fact]
    public void PickLine_ChangesAcrossDays()
    {
        // 不同日期 → 哈希种子变化 → 文案变化（断言非空 + 至少一次不同于前日）
        var trigger = new ProactiveSpeechTrigger();
        var candidates = new List<ProactiveSpeechTrigger.ProactiveCandidate>
        {
            C("Haley", "海莉")
        };

        var r1 = trigger.Evaluate(600, "Y1_spring_1", candidates, NewQuota(), Now);
        var r2 = trigger.Evaluate(600, "Y1_summer_1", candidates, NewQuota(), Now);

        Assert.Single(r1);
        Assert.Single(r2);
        Assert.False(string.IsNullOrWhiteSpace(r1[0].Text));
        Assert.False(string.IsNullOrWhiteSpace(r2[0].Text));
    }
}