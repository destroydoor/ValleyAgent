using ValleyAgent.Brain;
using Xunit;

namespace ValleyAgent.UnitTests.Brain;

/// <summary>
///     阶段 3 AgentBrain L2 状态摘要字段单元测试（spec §2.1 / §2.9）。
///     验证：MoodTag/TodayEvents/WorkingOn/OwedMoney 读写、todayEvents 条数上限、
///     RecentEventTexts 剥离日期前缀、PruneTodayEvents 三天保留清理。
///     AgentBrain 在 Abstractions 项目（无游戏依赖），可直接测试。
/// </summary>
public class AgentBrainL2Tests
{
    // ───────────────────────── 字段默认值 ─────────────────────────

    [Fact]
    public void L2Fields_DefaultValues()
    {
        var brain = new AgentBrain("Shane");

        Assert.Equal("", brain.MoodTag);
        Assert.Empty(brain.TodayEvents);
        Assert.Null(brain.WorkingOn);
        Assert.Equal(0, brain.OwedMoney);
    }

    [Fact]
    public void MoodTag_SetAndGet()
    {
        var brain = new AgentBrain("Shane");

        brain.MoodTag = "angry";

        Assert.Equal("angry", brain.MoodTag);
    }

    [Fact]
    public void WorkingOn_DefaultNull_SetAndClear()
    {
        var brain = new AgentBrain("Shane");

        brain.WorkingOn = "cleaning_coop";
        Assert.Equal("cleaning_coop", brain.WorkingOn);

        brain.WorkingOn = null;
        Assert.Null(brain.WorkingOn);
    }

    [Fact]
    public void OwedMoney_SetAndGet()
    {
        var brain = new AgentBrain("Shane");

        brain.OwedMoney = 500;

        Assert.Equal(500, brain.OwedMoney);
    }

    // ───────────────────────── TodayEvents 追加与上限 ─────────────────────────

    [Fact]
    public void AddTodayEvent_AppendsWithDatePrefix()
    {
        var brain = new AgentBrain("Abigail");

        brain.AddTodayEvent("visited the mines", "Y1_spring_1");

        Assert.Single(brain.TodayEvents);
        Assert.Equal("Y1_spring_1::visited the mines", brain.TodayEvents[0]);
    }

    [Fact]
    public void AddTodayEvent_CapsAtMaxTodayEvents()
    {
        var brain = new AgentBrain("Abigail");
        for (var i = 1; i <= AgentBrain.MaxTodayEvents + 3; i++)
        {
            brain.AddTodayEvent($"event {i}", $"Y1_spring_{i}");
        }

        Assert.Equal(AgentBrain.MaxTodayEvents, brain.TodayEvents.Count);
        // 最旧条目被丢弃：剩余应为 event 4..8（MaxTodayEvents=5）
        Assert.Equal("Y1_spring_4::event 4", brain.TodayEvents[0]);
        Assert.Equal("Y1_spring_8::event 8", brain.TodayEvents[^1]);
    }

    [Fact]
    public void AddTodayEvent_BlankText_Ignored()
    {
        var brain = new AgentBrain("Abigail");

        brain.AddTodayEvent("   ", "Y1_spring_1");
        brain.AddTodayEvent("", "Y1_spring_1");

        Assert.Empty(brain.TodayEvents);
    }

    [Fact]
    public void RecentEventTexts_StripsDatePrefix()
    {
        var brain = new AgentBrain("Abigail");
        brain.AddTodayEvent("found a diamond", "Y1_spring_1");

        var texts = brain.RecentEventTexts;

        Assert.Single(texts);
        Assert.Equal("found a diamond", texts[0]);
    }

    [Fact]
    public void RecentEventTexts_NoPrefixEntry_PassedThrough()
    {
        var brain = new AgentBrain("Abigail");
        // Director set_npc_recent_events 整体替换写入的条目无日期前缀 → 原样输出
        brain.TodayEvents.Add("rainy day at the saloon");

        Assert.Equal("rainy day at the saloon", brain.RecentEventTexts[0]);
    }

    // ───────────────────────── PruneTodayEvents 三天保留 ─────────────────────────

    /// <summary>
    ///     当前日期 Y2_spring_1（总天数 113），保留 3 天 → 截止 110。
    ///     Y1_winter_28（112）保留；Y1_winter_25（109）清除。
    /// </summary>
    [Fact]
    public void PruneTodayEvents_RemovesStaleEntries_KeepsRecent()
    {
        var brain = new AgentBrain("Abigail");
        brain.AddTodayEvent("recent", "Y1_winter_28");
        brain.AddTodayEvent("stale", "Y1_winter_25");
        brain.AddTodayEvent("today", "Y2_spring_1");

        var removed = brain.PruneTodayEvents("Y2_spring_1", retainDays: 3);

        Assert.Equal(1, removed);
        Assert.Equal(2, brain.TodayEvents.Count);
        Assert.Contains(brain.TodayEvents, e => e.StartsWith("Y1_winter_28::"));
        Assert.Contains(brain.TodayEvents, e => e.StartsWith("Y2_spring_1::"));
    }

    [Fact]
    public void PruneTodayEvents_NoPrefixEntries_Kept()
    {
        var brain = new AgentBrain("Abigail");
        // 无日期前缀（Director 整体替换写入）→ 视为当日，不清理
        brain.TodayEvents.Add("saloon gossip");

        var removed = brain.PruneTodayEvents("Y2_spring_1", retainDays: 3);

        Assert.Equal(0, removed);
        Assert.Single(brain.TodayEvents);
    }

    [Fact]
    public void PruneTodayEvents_InvalidDate_Noop()
    {
        var brain = new AgentBrain("Abigail");
        brain.AddTodayEvent("old", "Y1_spring_1");

        Assert.Equal(0, brain.PruneTodayEvents("not-a-date", retainDays: 3));
        Assert.Equal(0, brain.PruneTodayEvents("", retainDays: 3));
        Assert.Equal(0, brain.PruneTodayEvents("Y2_spring_1", retainDays: -1));
        Assert.Single(brain.TodayEvents);
    }

    [Fact]
    public void PruneTodayEvents_RetainDaysZero_RemovesAllDatedBeforeToday()
    {
        var brain = new AgentBrain("Abigail");
        brain.AddTodayEvent("yesterday", "Y1_winter_28");
        brain.AddTodayEvent("today", "Y2_spring_1");

        var removed = brain.PruneTodayEvents("Y2_spring_1", retainDays: 0);

        Assert.Equal(1, removed);
        Assert.Single(brain.TodayEvents);
        Assert.StartsWith("Y2_spring_1::", brain.TodayEvents[0]);
    }

    // ── 2026-08-20 Phase 2：上下文管理——对话记忆 entryType + 去重 ──

    [Fact]
    public void AddMemory_ConversationEntryType_StoredWithTypeAndDeduped()
    {
        var brain = new AgentBrain("Haley");

        brain.AddMemory("Player said: \"你好\"", entryType: MemoryEntryType.Conversation);
        brain.AddMemory("Player said: \"你好\"", entryType: MemoryEntryType.Conversation); // 同文本去重
        brain.AddMemory("Player said: \"你好\"", entryType: MemoryEntryType.Generic); // 按文本去重，与 entryType 无关

        Assert.Single(brain.ShortTermMemories);
        Assert.Equal(MemoryEntryType.Conversation, brain.ShortTermMemories[0].EntryType);
        Assert.Equal("Player said: \"你好\"", brain.ShortTermMemories[0].Text);
    }

    [Fact]
    public void AddMemory_BlankText_Ignored()
    {
        var brain = new AgentBrain("Haley");
        brain.AddMemory("   ", entryType: MemoryEntryType.Conversation);
        Assert.Empty(brain.ShortTermMemories);
    }
}
