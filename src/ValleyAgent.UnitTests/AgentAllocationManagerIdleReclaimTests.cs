using System;
using System.Collections.Generic;
using ValleyAgent.Agents;
using Xunit;

namespace ValleyAgent.UnitTests;

/// <summary>
///     PR2 B5 空闲回收的池表规则真单测（设计 docs/design/2026-09-13-agent-body-refactor.md §3.4 步骤 3）。
///     AgentAllocationManager 是纯 C#（lock + 字典，无 Game1 依赖），两条规则直接行为断言：
///     ① ReevaluateAllocations 显式跳过 KeepUntil 未到期者（不再依赖 manual 排序垫底的巧合）；
///     ② ReleaseIdleManualOverrides 只释放「无 KeepUntil/已过期 + 非活跃对话 + 空闲超时」的 manual override。
/// </summary>
public class AgentAllocationManagerIdleReclaimTests
{
    private static readonly DateTime Now = new(2026, 9, 14, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>ForceAllocate 造 manual override（promote/导演路径同款），经 GetAllocation 拿活跃引用改状态。</summary>
    private static AgentAllocationManager SeedManuals(AgentAllocationManager manager, params string[] names)
    {
        foreach (var name in names)
        {
            Assert.True(manager.ForceAllocate(name), $"ForceAllocate({name}) 应成功（池未满）");
        }

        return manager;
    }

    /// <summary>
    ///     直读池表（反射）：TryAllocate/ForceAllocate 都是"一出一进"，池经公开 API 不会自然超容，
    ///     而 ReevaluateAllocations 的裁剪分支正是为超容量账面态兜底——只能这样构造测试前置。
    /// </summary>
    private static Dictionary<string, AgentAllocationInfo> PeekTable(AgentAllocationManager manager) =>
        (Dictionary<string, AgentAllocationInfo>)typeof(AgentAllocationManager)
            .GetField("_allocations", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(manager)!;

    // ───────────────────────── 规则①：ReevaluateAllocations 显式 KeepUntil 跳过 ─────────────────────────

    [Fact]
    public void ReevaluateAllocations_KeepUntilActive_SkipsProtected_TrimsOthers()
    {
        // 超容量（3 > Max 2），KeepUntil 未到期者优先级最低——旧实现（仅 manual 垫底排序、
        // 无显式 KeepUntil 过滤）会先裁掉它；显式规则下被裁的必须是别人。
        var manager = new AgentAllocationManager(minAgents: 0, maxAgents: 2);
        var table = PeekTable(manager);
        // ReevaluateAllocations 内部用真实 DateTime.UtcNow 判豁免——KeepUntil 相对真实时钟构造，
        // 不能用测试 fabricated 时间（-now 仅用于 now 参数化的 ReleaseIdleManualOverrides 测试）。
        table["Kept"] = new AgentAllocationInfo("Kept", 0, 0, 0, 0) { KeepUntil = DateTime.UtcNow.AddMinutes(30) };
        table["Mid"] = new AgentAllocationInfo("Mid", 1, 0, 0, 1);
        table["High"] = new AgentAllocationInfo("High", 2, 0, 0, 2);

        var trimmed = new List<string>();
        manager.OnAgentDeallocated += (_, e) => trimmed.Add(e.NpcName);

        manager.ReevaluateAllocations();

        Assert.True(manager.IsAllocated("Kept"), "KeepUntil 未到期者不得被容量裁剪");
        Assert.True(manager.IsAllocated("High"));
        Assert.False(manager.IsAllocated("Mid"), "被裁的应是可候选中优先级最低的 Mid");
        Assert.Equal(new[] { "Mid" }, trimmed);
    }

    [Fact]
    public void ReevaluateAllocations_AllProtected_OverCapacityLeftIntact()
    {
        // 候选全部处于豁免期 → 允许暂时超容量运行（豁免到期后下一次周期调用补裁），不得裁任何 beat 身体。
        var manager = new AgentAllocationManager(minAgents: 0, maxAgents: 1);
        var table = PeekTable(manager);
        table["A"] = new AgentAllocationInfo("A", 0, 0, 0, 0) { KeepUntil = DateTime.UtcNow.AddMinutes(30) };
        table["B"] = new AgentAllocationInfo("B", 0, 0, 0, 0) { KeepUntil = DateTime.UtcNow.AddMinutes(30) };

        manager.ReevaluateAllocations();

        Assert.True(manager.IsAllocated("A"));
        Assert.True(manager.IsAllocated("B"));
    }

    [Fact]
    public void ReevaluateAllocations_ExpiredKeepUntil_LosesProtection()
    {
        // KeepUntil 已过期 → 豁免失效，按普通优先级参与裁剪（低优先级者被裁）。
        var manager = new AgentAllocationManager(minAgents: 0, maxAgents: 2);
        var table = PeekTable(manager);
        table["Expired"] = new AgentAllocationInfo("Expired", 0, 0, 0, 0) { KeepUntil = DateTime.UtcNow.AddMinutes(-1) };
        table["Mid"] = new AgentAllocationInfo("Mid", 1, 0, 0, 1);
        table["High"] = new AgentAllocationInfo("High", 2, 0, 0, 2);

        manager.ReevaluateAllocations();

        Assert.False(manager.IsAllocated("Expired"), "KeepUntil 过期后豁免失效");
        Assert.True(manager.IsAllocated("Mid"));
    }

    // ───────────────────────── 规则②：ReleaseIdleManualOverrides ─────────────────────────

    [Fact]
    public void ReleaseIdleManualOverrides_IdleNotActiveManual_Released()
    {
        var manager = SeedManuals(new AgentAllocationManager(0, 5), "Idle", "Fresh");
        manager.GetAllocation("Idle")!.LastUpdated = Now.AddMinutes(-20);
        manager.GetAllocation("Fresh")!.LastUpdated = Now.AddMinutes(-1);
        // 非 manual 的空闲分配不在此方法的射程内（释放只针对 manual override）
        Assert.True(manager.TryAllocate("PlainNpc", 0, 0, 0));
        manager.GetAllocation("PlainNpc")!.LastUpdated = Now.AddMinutes(-20);

        var released = manager.ReleaseIdleManualOverrides(Now, TimeSpan.FromMinutes(10), _ => false);

        Assert.Equal(1, released);
        Assert.False(manager.IsManuallyOverridden("Idle"));
        Assert.True(manager.IsManuallyOverridden("Fresh"), "LastUpdated 新鲜者不释放");
        Assert.False(manager.IsManuallyOverridden("PlainNpc"));
    }

    [Fact]
    public void ReleaseIdleManualOverrides_KeepUntilActive_NotReleased()
    {
        var manager = SeedManuals(new AgentAllocationManager(0, 5), "Beat");
        var info = manager.GetAllocation("Beat")!;
        info.LastUpdated = Now.AddMinutes(-60);
        info.KeepUntil = Now.AddMinutes(5); // 导演 beat 窗口内

        var released = manager.ReleaseIdleManualOverrides(Now, TimeSpan.FromMinutes(10), _ => false);

        Assert.Equal(0, released);
        Assert.True(manager.IsManuallyOverridden("Beat"), "KeepUntil 未到期者不释放");
    }

    [Fact]
    public void ReleaseIdleManualOverrides_InActiveConversation_NotReleased()
    {
        // 活跃对话由调用方委托判定（Manager 不依赖 Patches/Game1 层）——委托说活跃就不释放。
        var manager = SeedManuals(new AgentAllocationManager(0, 5), "Talking");
        manager.GetAllocation("Talking")!.LastUpdated = Now.AddMinutes(-60);

        var released = manager.ReleaseIdleManualOverrides(
            Now, TimeSpan.FromMinutes(10), name => string.Equals(name, "Talking", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(0, released);
        Assert.True(manager.IsManuallyOverridden("Talking"));
    }

    [Fact]
    public void ReleaseIdleManualOverrides_FreshLastUpdated_NotReleased()
    {
        var manager = SeedManuals(new AgentAllocationManager(0, 5), "Recent");
        manager.GetAllocation("Recent")!.LastUpdated = Now.AddMinutes(-9); // 阈值 10 分钟内

        var released = manager.ReleaseIdleManualOverrides(Now, TimeSpan.FromMinutes(10), _ => false);

        Assert.Equal(0, released);
        Assert.True(manager.IsManuallyOverridden("Recent"));
    }

    [Fact]
    public void ReleaseIdleManualOverrides_ReleaseIsNotEviction()
    {
        // 释放 ≠ 淘汰：只摘 manual 标记，池位仍在（裁剪由 ReevaluateAllocations 按容量执行）。
        var manager = SeedManuals(new AgentAllocationManager(0, 5), "Idle");
        manager.GetAllocation("Idle")!.LastUpdated = Now.AddMinutes(-20);

        _ = manager.ReleaseIdleManualOverrides(Now, TimeSpan.FromMinutes(10), _ => false);

        Assert.Equal(1, manager.CurrentAgentCount);
        Assert.True(manager.IsAllocated("Idle"));
    }

    // ───────────────────────── UpdatePriority 刷新 LastUpdated（B3 中继刷新与空闲规则配套） ─────────────────────────

    [Fact]
    public void UpdatePriority_RefreshesLastUpdated()
    {
        var manager = new AgentAllocationManager(0, 5);
        Assert.True(manager.TryAllocate("Haley", 0, 0, 0));
        var info = manager.GetAllocation("Haley")!;
        info.LastUpdated = DateTime.UtcNow.AddMinutes(-30);

        Assert.True(manager.UpdatePriority("Haley", 3, 0, 2));

        Assert.True(info.LastUpdated > DateTime.UtcNow.AddMinutes(-1),
            "UpdatePriority 必须刷新 LastUpdated：房客中继对话靠它维持'活跃'判定，否则空闲回收会误放正在聊的身体");
    }

    [Fact]
    public void ReleaseIdleManualOverrides_NullConversationDelegate_Throws()
    {
        var manager = new AgentAllocationManager(0, 5);
        Assert.Throws<ArgumentNullException>(
            () => manager.ReleaseIdleManualOverrides(Now, TimeSpan.FromMinutes(10), null!));
    }
}
