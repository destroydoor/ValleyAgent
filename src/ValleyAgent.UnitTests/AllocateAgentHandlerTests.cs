using System.Globalization;
using ValleyAgent.Agents;
using ValleyAgent.Protocol;
using Xunit;

namespace ValleyAgent.UnitTests;

/// <summary>
///     AllocateAgentHandler 单测：处理 TS 导演下发的 allocate_agent 消息。
///     对应设计文档 §4.2.2：ForceAllocate + 写入 KeepUntil 豁免。
/// </summary>
public static class AllocateAgentHandlerTests
{
    [Fact]
    public static void Handle_ValidNpc_ForceAllocatesAndSetsKeepUntil()
    {
        var manager = new AgentAllocationManager();
        var handler =
            new AllocateAgentHandler(manager, iso => DateTime.Parse(iso, null, DateTimeStyles.AdjustToUniversal));

        var msg = new ProtocolV2.AllocateAgentMessage
        {
            NpcName = "Haley",
            RequestId = "req-1",
            KeepUntilIso = "2026-08-03T14:00:00Z"
        };
        var (success, reason) = handler.Handle(msg);

        Assert.True(success);
        Assert.Equal(ProtocolV2.ActionResultReason.Allocated, reason);

        var info = manager.GetAllAllocatedAgents()[0];
        Assert.Equal(new DateTime(2026, 8, 3, 14, 0, 0, DateTimeKind.Utc), info.KeepUntil);
    }

    [Fact]
    public static void Handle_MaxCapacity_ReturnsMaxCapacityReached()
    {
        var manager = new AgentAllocationManager(0, 1);
        // 满员：Robin 已是 manual override → 无可替槽
        _ = manager.ForceAllocate("Robin");
        var handler = new AllocateAgentHandler(manager, _ => null);

        var msg = new ProtocolV2.AllocateAgentMessage
        {
            NpcName = "Haley",
            RequestId = "req-2"
        };
        var (success, reason) = handler.Handle(msg);

        // Robin 是 manual override，ForceAllocate 不会替换 → 返回 MaxCapacityReached
        Assert.False(success);
        Assert.Equal(ProtocolV2.ActionResultReason.MaxCapacityReached, reason);
    }

    [Fact]
    public static void Handle_AlreadyAllocated_ReturnsAllocatedAndUpdatesKeepUntil()
    {
        var manager = new AgentAllocationManager();
        _ = manager.TryAllocate("Haley", 0, 0, 0);
        var handler =
            new AllocateAgentHandler(manager, iso => DateTime.Parse(iso, null, DateTimeStyles.AdjustToUniversal));

        var msg = new ProtocolV2.AllocateAgentMessage
        {
            NpcName = "Haley",
            RequestId = "req-3",
            KeepUntilIso = "2026-08-03T16:00:00Z"
        };
        var (success, reason) = handler.Handle(msg);

        Assert.True(success);
        Assert.Equal(ProtocolV2.ActionResultReason.Allocated, reason);
        var info = manager.GetAllAllocatedAgents()[0];
        Assert.Equal(new DateTime(2026, 8, 3, 16, 0, 0, DateTimeKind.Utc), info.KeepUntil);
    }

    [Fact]
    public static void Handle_InvalidKeepUntilIso_FailOpenNoKeep()
    {
        var manager = new AgentAllocationManager();
        var handler = new AllocateAgentHandler(manager, _ => null);

        var msg = new ProtocolV2.AllocateAgentMessage
        {
            NpcName = "Haley",
            RequestId = "req-4",
            KeepUntilIso = "garbage"
        };
        var (success, reason) = handler.Handle(msg);

        Assert.True(success);
        Assert.Equal(ProtocolV2.ActionResultReason.Allocated, reason);
        var info = manager.GetAllAllocatedAgents()[0];
        Assert.Null(info.KeepUntil);
    }

    [Fact]
    public static void Handle_EmptyNpcName_ReturnsInvalidState()
    {
        var manager = new AgentAllocationManager();
        var handler = new AllocateAgentHandler(manager);

        var msg = new ProtocolV2.AllocateAgentMessage
        {
            NpcName = "",
            RequestId = "req-5"
        };
        var (success, reason) = handler.Handle(msg);

        Assert.False(success);
        Assert.Equal(ProtocolV2.ActionResultReason.InvalidState, reason);
    }

    [Fact]
    public static void Handle_ReplacesLowerPriorityAgent_WhenMaxCapacityButCandidateHigher()
    {
        var manager = new AgentAllocationManager(0, 1);
        // Robin 优先级 0，manual=false
        _ = manager.TryAllocate("Robin", 0, 0, 0);
        var handler = new AllocateAgentHandler(manager, _ => null);

        // ForceAllocate 会替换最低优先级 non-manual Agent
        var msg = new ProtocolV2.AllocateAgentMessage
        {
            NpcName = "Haley",
            RequestId = "req-6"
        };
        var (success, reason) = handler.Handle(msg);

        Assert.True(success);
        Assert.Equal(ProtocolV2.ActionResultReason.Allocated, reason);
        Assert.Equal(1, manager.CurrentAgentCount);
        Assert.True(manager.IsAllocated("Haley"));
        Assert.False(manager.IsAllocated("Robin"));
    }

    [Fact]
    public static void Handle_NullManager_Throws() =>
        Assert.Throws<ArgumentNullException>(() => new AllocateAgentHandler(null!));
}