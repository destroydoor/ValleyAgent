using StardewModdingAPI;
using StardewModdingAPI.Framework.Logging;
using ValleyAgent.Chat;
using Xunit;

namespace ValleyAgent.UnitTests;

/// <summary>
///     E5-2 远程喊话延迟回应调度器单元测试。
///     验证：调度写入 3~8s 延迟窗口；同 NPC 重复调度覆盖（后到优先）；
///     未到期不渲染（PendingCount 保持）；到期后渲染并清空；取消；清空；
///     空 NPC 名拒绝。渲染路径因依赖 Game1（ActiveSpeechRouter）不做单测，
///     由游戏内实测覆盖；本测试聚焦调度簿记的确定性。
///     设计文档：docs/ideas/e52-implementation-思路.md §4。
/// </summary>
public class ShoutReplySchedulerTests
{
    private static readonly DateTime T0 = new(2026, 8, 3, 8, 0, 0, DateTimeKind.Utc);

    private static ShoutReplyScheduler NewScheduler(string? reply = "回应")
        => new(new StubMonitor(), null, (_, _) => reply);

    [Fact]
    public void ScheduleReply_AddsPendingWithinDelayWindow()
    {
        var scheduler = NewScheduler();

        scheduler.ScheduleReply("Haley", "海莉早上好", T0);

        Assert.Equal(1, scheduler.PendingCount);
        Assert.Contains("Haley", scheduler.PendingNpcs);
    }

    [Fact]
    public void ScheduleReply_EmptyName_Ignored()
    {
        var scheduler = NewScheduler();

        scheduler.ScheduleReply("", "hi", T0);

        Assert.Equal(0, scheduler.PendingCount);
    }

    [Fact]
    public void ScheduleReply_SameNpcOverrides()
    {
        var scheduler = NewScheduler();
        scheduler.ScheduleReply("Haley", "第一次", T0);

        scheduler.ScheduleReply("Haley", "第二次", T0);

        Assert.Equal(1, scheduler.PendingCount); // 后到覆盖，不产生两条
    }

    [Fact]
    public void ScheduleReply_DelayIsWithin3To8Seconds()
    {
        var scheduler = NewScheduler();

        scheduler.ScheduleReply("Haley", "hi", T0);

        // 通过取消后重建的方式不可行（内部 delay 随机），改为验证簿记：
        // 到期时间 = 调度时间 + [3s, 8s]，此刻（T0）必未到期
        Assert.Equal(1, scheduler.PendingCount);
        scheduler.ProcessDueReplies(T0); // 未到期 → 不渲染不消费
        Assert.Equal(1, scheduler.PendingCount);
    }

    [Fact]
    public void ProcessDueReplies_NotDue_KeepsPending()
    {
        var scheduler = NewScheduler();
        scheduler.ScheduleReply("Haley", "hi", T0);

        scheduler.ProcessDueReplies(T0.AddSeconds(2)); // 未到 3s 下限

        Assert.Equal(1, scheduler.PendingCount);
    }

    [Fact]
    public void ProcessDueReplies_PastMaxDelay_Consumes()
    {
        var scheduler = NewScheduler();
        scheduler.ScheduleReply("Haley", "hi", T0);

        scheduler.ProcessDueReplies(T0.AddSeconds(10)); // 超过 8s 上限

        Assert.Equal(0, scheduler.PendingCount); // 到期已渲染（渲染失败也消费，防堆积）
    }

    [Fact]
    public void Cancel_RemovesPending()
    {
        var scheduler = NewScheduler();
        scheduler.ScheduleReply("Haley", "hi", T0);

        scheduler.Cancel("Haley");

        Assert.Equal(0, scheduler.PendingCount);
    }

    [Fact]
    public void Clear_RemovesAll()
    {
        var scheduler = NewScheduler();
        scheduler.ScheduleReply("Haley", "hi", T0);
        scheduler.ScheduleReply("Abigail", "hi", T0);

        scheduler.Clear();

        Assert.Equal(0, scheduler.PendingCount);
    }

    private sealed class StubMonitor : IMonitor
    {
        public bool IsExternallyVisible
        {
            get => false;
        }

        public bool IsVerbose
        {
            get => false;
        }

        public void Log(string message, LogLevel level)
        {
        }

        public void LogOnce(string message, LogLevel level)
        {
        }

        public void VerboseLog(string message)
        {
        }

        public void VerboseLog(ref VerboseLogStringHandler message)
        {
        }

        public void LogToScreen(string message, LogLevel level)
        {
        }
    }
}