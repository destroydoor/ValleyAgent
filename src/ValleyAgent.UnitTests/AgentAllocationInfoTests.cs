using ValleyAgent.Agents;
using Xunit;

namespace ValleyAgent.UnitTests;

/// <summary>
///     AgentAllocationInfo 互动空闲淘汰字段测试。
///     对应设计文档 §4.3.2 互动空闲淘汰。
/// </summary>
public static class AgentAllocationInfoTests
{
    private static readonly DateTime Now = new(2026, 8, 3, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public static void ShouldEvict_NoInteractionBeyondThreshold_NoKeep_ReturnsTrue()
    {
        var info = new AgentAllocationInfo("Haley", 0, 0, 0, 0)
        {
            LastPlayerInteractionTick = Now.AddSeconds(-100)
        };
        Assert.True(info.ShouldEvict(Now, 90));
    }

    [Fact]
    public static void ShouldEvict_WithinThreshold_ReturnsFalse()
    {
        var info = new AgentAllocationInfo("Haley", 0, 0, 0, 0)
        {
            LastPlayerInteractionTick = Now.AddSeconds(-30)
        };
        Assert.False(info.ShouldEvict(Now, 90));
    }

    [Fact]
    public static void ShouldEvict_BeyondThreshold_KeepNotExpired_ReturnsFalse()
    {
        var info = new AgentAllocationInfo("Haley", 0, 0, 0, 0)
        {
            LastPlayerInteractionTick = Now.AddSeconds(-100),
            KeepUntil = Now.AddSeconds(50)
        };
        Assert.False(info.ShouldEvict(Now, 90));
    }

    [Fact]
    public static void ShouldEvict_BeyondThreshold_KeepExpired_ReturnsTrue()
    {
        var info = new AgentAllocationInfo("Haley", 0, 0, 0, 0)
        {
            LastPlayerInteractionTick = Now.AddSeconds(-100),
            KeepUntil = Now.AddSeconds(-10)
        };
        Assert.True(info.ShouldEvict(Now, 90));
    }

    [Fact]
    public static void ShouldEvict_DefaultLastInteractionZero_BeyondThreshold_ReturnsTrue()
    {
        // 新分配的 Agent，LastPlayerInteractionTick 默认 default(DateTime)（很久以前）
        // 应当能被淘汰（除非有 keep 豁免）
        var info = new AgentAllocationInfo("Haley", 0, 0, 0, 0);
        Assert.True(info.ShouldEvict(Now, 90));
    }
}