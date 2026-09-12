using StardewValley;
using ValleyAgent.Brain;
using ValleyAgent.Goals;
using ValleyAgent.Services;
using ValleyAgent.StateMachine;
using Xunit;

namespace ValleyAgent.UnitTests.Goals;

/// <summary>
///     <see cref="GoalExecutor" /> 调度决策 + 状态机边集单元测试（阶段 2，spec §3.5）。
///     DecideExecuting / DecideReporting 为纯决策函数（不依赖游戏状态）；
///     AgentStateMachine 边集验证 set_goal 的进入/退出路径（spec §1.3）。
/// </summary>
public class GoalExecutorTests
{
    // ───────────────────────── EXECUTING_GOAL 收尾决策 ─────────────────────────

    [Fact]
    public void DecideExecuting_StillExecuting_KeepsRunning()
    {
        Assert.Equal(
            GoalExecutor.ExecutingOutcome.KeepExecuting,
            GoalExecutor.DecideExecuting(GoalStatus.Executing, reportBack: true));
    }

    [Fact]
    public void DecideExecuting_CompleteWithReportBack_BeginsReport()
    {
        Assert.Equal(
            GoalExecutor.ExecutingOutcome.BeginReport,
            GoalExecutor.DecideExecuting(GoalStatus.Complete, reportBack: true));
    }

    [Fact]
    public void DecideExecuting_CompleteWithoutReportBack_FinalizesSilently()
    {
        Assert.Equal(
            GoalExecutor.ExecutingOutcome.FinalizeSuccess,
            GoalExecutor.DecideExecuting(GoalStatus.Complete, reportBack: false));
    }

    [Fact]
    public void DecideExecuting_Failed_FinalizesFailure()
    {
        Assert.Equal(
            GoalExecutor.ExecutingOutcome.FinalizeFailure,
            GoalExecutor.DecideExecuting(GoalStatus.Failed, reportBack: true));
    }

    [Fact]
    public void DecideExecuting_Cancelled_FinalizesCancel()
    {
        Assert.Equal(
            GoalExecutor.ExecutingOutcome.FinalizeCancel,
            GoalExecutor.DecideExecuting(GoalStatus.Cancelled, reportBack: true));
    }

    [Fact]
    public void DecideExecuting_NotStarted_KeepsRunning()
    {
        Assert.Equal(
            GoalExecutor.ExecutingOutcome.KeepExecuting,
            GoalExecutor.DecideExecuting(GoalStatus.NotStarted, reportBack: false));
    }

    // ───────────────────────── TRAVELING_TO_REPORT 决策 ─────────────────────────

    [Fact]
    public void DecideReporting_Arrived_Reports()
    {
        Assert.Equal(
            GoalExecutor.ReportingOutcome.Arrived,
            GoalExecutor.DecideReporting(isDead: false, arrived: true, timedOut: false));
    }

    [Fact]
    public void DecideReporting_NotArrived_KeepsReporting()
    {
        Assert.Equal(
            GoalExecutor.ReportingOutcome.KeepReporting,
            GoalExecutor.DecideReporting(isDead: false, arrived: false, timedOut: false));
    }

    [Fact]
    public void DecideReporting_Timeout_FallbackSend()
    {
        Assert.Equal(
            GoalExecutor.ReportingOutcome.FallbackSend,
            GoalExecutor.DecideReporting(isDead: false, arrived: false, timedOut: true));
    }

    [Fact]
    public void DecideReporting_DeadNpc_FallbackSend()
    {
        Assert.Equal(
            GoalExecutor.ReportingOutcome.FallbackSend,
            GoalExecutor.DecideReporting(isDead: true, arrived: false, timedOut: false));
    }

    // ───────────────────────── 状态机边集（spec §1.3） ─────────────────────────

    [Fact]
    public void AllowedTransitions_AnyStateCanEnterExecutingGoal()
    {
        foreach (var from in new[] { AgentState.IDLE, AgentState.FOLLOW, AgentState.FARM, AgentState.MINE, AgentState.FORAGE, AgentState.FIGHT, AgentState.TALK, AgentState.CHOP })
        {
            Assert.True(
                AgentStateMachine.IsTransitionAllowed(from, AgentState.EXECUTING_GOAL),
                $"{from} → EXECUTING_GOAL should be allowed");
        }
    }

    [Fact]
    public void AllowedTransitions_ExecutingGoalCanExit()
    {
        Assert.True(AgentStateMachine.IsTransitionAllowed(AgentState.EXECUTING_GOAL, AgentState.IDLE));
        Assert.True(AgentStateMachine.IsTransitionAllowed(AgentState.EXECUTING_GOAL, AgentState.TRAVELING_TO_REPORT));
        Assert.True(AgentStateMachine.IsTransitionAllowed(AgentState.EXECUTING_GOAL, AgentState.FOLLOW));
    }

    [Fact]
    public void AllowedTransitions_ReportingCanOnlyExitToIdle()
    {
        Assert.True(AgentStateMachine.IsTransitionAllowed(AgentState.TRAVELING_TO_REPORT, AgentState.IDLE));
        Assert.False(AgentStateMachine.IsTransitionAllowed(AgentState.TRAVELING_TO_REPORT, AgentState.EXECUTING_GOAL));
        Assert.False(AgentStateMachine.IsTransitionAllowed(AgentState.TRAVELING_TO_REPORT, AgentState.FOLLOW));
    }

    [Fact]
    public void AllowedTransitions_IdleCanEnterExecutingGoal()
    {
        Assert.True(AgentStateMachine.IsTransitionAllowed(AgentState.IDLE, AgentState.EXECUTING_GOAL));
    }

    // ───────────────────────── AgentBrain.PendingGoal ─────────────────────────

    [Fact]
    public void AgentBrain_PendingGoal_DefaultsToNull()
    {
        var brain = new AgentBrain("Shane");

        Assert.Null(brain.PendingGoal);
    }

    [Fact]
    public void AgentBrain_PendingGoal_SetAndGet()
    {
        var brain = new AgentBrain("Shane");
        var goal = new StubGoal();

        brain.PendingGoal = goal;

        Assert.Same(goal, brain.PendingGoal);
    }

    /// <summary>测试用最小 Goal 实现（仅验证 Brain 引用）。</summary>
    private sealed class StubGoal : IGoal
    {
        public string NpcName => "Shane";
        public GoalType Type => GoalType.ChopTree;
        public GoalStatus Status => GoalStatus.Executing;
        public bool ReportBack => false;
        public int CollectedCount => 0;
        public int ElapsedGameMinutes => 0;
        public string Reason => "";
        public void Start(AgentInstance agent, NPC npc) { }
        public void Tick(AgentInstance agent, NPC npc, int currentTick) { }
        public void Cancel(string reason) { }
        public void Complete() { }
        public void Fail(string reason) { }
        public string DescribeProgress() => "stub";
    }
}
