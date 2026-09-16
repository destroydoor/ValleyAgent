using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using ValleyAgent.Config;
using ValleyAgent.Handlers;
using ValleyAgent.Infrastructure;
using ValleyAgent.Navigation;
using ValleyAgent.Protocol;
using ValleyAgent.Services;
using ValleyAgent.StateMachine;
using ValleyAgent.WebSocket;
using ActionResultReason = ValleyAgent.Protocol.ProtocolV2.ActionResultReason;

namespace ValleyAgent.Goals;

/// <summary>
///     Goal 调度器（阶段 2，spec §3 / design doc §7.2 数据流）。
///
///     <list type="bullet">
///       <item><b>CreateGoal</b>：CommandExecutor case "set_goal" 入口 → 建 Goal → <c>AgentBrain.PendingGoal</c>
///            → <c>ForceTransition(EXECUTING_GOAL, skipDurationGuard, fromDecision)</c>。</item>
///       <item><b>TickExecuting</b>：注册为 EXECUTING_GOAL 状态 action，每 tick 驱动 goal.Tick()
///           （委托 Handler 干活）；完成后按 reportBack 进入寻路汇报或直接收尾。</item>
///       <item><b>TickReporting</b>：注册为 TRAVELING_TO_REPORT 状态 action，寻路回玩家（&lt;5 格到达）
///           触发汇报 LLM（action_result，不走新 wire 消息）；超时 2 游戏小时 / NPC 死亡 → fallback 直接汇报。</item>
///     </list>
///
///     <para>Handler 单次动作自退出（ForceTransition(IDLE)）由 AgentTickLoop.ProcessAgent 的恢复守卫
///     拉回 EXECUTING_GOAL 继续（spec §1.2 "GoalExecutor 包装 handler.Update"）。</para>
/// </summary>
public class GoalExecutor
{
    /// <summary>EXECUTING_GOAL 状态 action 的纯决策结果。</summary>
    public enum ExecutingOutcome
    {
        /// <summary>继续执行（Status == Executing，Handler 自退出由恢复守卫处理）。</summary>
        KeepExecuting,

        /// <summary>完成且 reportBack → 进入寻路汇报（TRAVELING_TO_REPORT）。</summary>
        BeginReport,

        /// <summary>完成且不汇报 → 直接回发成功结果并回 IDLE。</summary>
        FinalizeSuccess,

        /// <summary>失败（超时/卡死/资源耗尽/死亡）→ 回发失败结果并回 IDLE。</summary>
        FinalizeFailure,

        /// <summary>取消 → 静默回 IDLE（不汇报）。</summary>
        FinalizeCancel,
    }

    /// <summary>TRAVELING_TO_REPORT 状态 action 的纯决策结果。</summary>
    public enum ReportingOutcome
    {
        /// <summary>继续寻路/等待到达。</summary>
        KeepReporting,

        /// <summary>到达玩家附近（&lt; 到达距离）→ 触发汇报。</summary>
        Arrived,

        /// <summary>汇报超时（2 游戏小时）或 NPC 死亡 → fallback 直接汇报（不回发即丢弃）。</summary>
        FallbackSend,
    }

    private readonly IMonitor _monitor;
    private readonly IMovementService _movementService;
    private readonly AgentService _agentService;
    private readonly ModConfig _config;
    private readonly IAgentServerProvider? _agentServerProvider;
    private readonly AgentNavigator? _navigator;
    private readonly FarmHandler? _farmHandler;
    private readonly MineHandler? _mineHandler;
    private readonly FightHandler? _fightHandler;
    private readonly ForageHandler? _forageHandler;

    /// <summary>汇报阶段起始游戏分钟（每 NPC），用于 2 游戏小时汇报超时判定。</summary>
    private readonly Dictionary<string, int> _reportStartMinutes = new(StringComparer.OrdinalIgnoreCase);

    public GoalExecutor(
        IMonitor monitor,
        IMovementService movementService,
        AgentService agentService,
        ModConfig config,
        IAgentServerProvider? agentServerProvider,
        AgentNavigator? navigator,
        FarmHandler? farmHandler,
        MineHandler? mineHandler,
        FightHandler? fightHandler,
        ForageHandler? forageHandler)
    {
        _monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        _movementService = movementService ?? throw new ArgumentNullException(nameof(movementService));
        _agentService = agentService ?? throw new ArgumentNullException(nameof(agentService));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _agentServerProvider = agentServerProvider;
        _navigator = navigator;
        _farmHandler = farmHandler;
        _mineHandler = mineHandler;
        _fightHandler = fightHandler;
        _forageHandler = forageHandler;
    }

    // ═══════════════════════════════════════════════════════════════════
    // 纯决策逻辑（单测可直接驱动，不依赖游戏状态）
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>EXECUTING_GOAL 状态收尾决策：目标状态 → 下一步动作。</summary>
    public static ExecutingOutcome DecideExecuting(GoalStatus status, bool reportBack) => status switch
    {
        GoalStatus.Complete => reportBack ? ExecutingOutcome.BeginReport : ExecutingOutcome.FinalizeSuccess,
        GoalStatus.Failed => ExecutingOutcome.FinalizeFailure,
        GoalStatus.Cancelled => ExecutingOutcome.FinalizeCancel,
        _ => ExecutingOutcome.KeepExecuting,
    };

    /// <summary>TRAVELING_TO_REPORT 状态决策：死亡/超时 → fallback，到达 → 汇报，否则继续。</summary>
    public static ReportingOutcome DecideReporting(bool isDead, bool arrived, bool timedOut)
    {
        if (isDead || timedOut)
        {
            return ReportingOutcome.FallbackSend;
        }

        return arrived ? ReportingOutcome.Arrived : ReportingOutcome.KeepReporting;
    }

    // ═══════════════════════════════════════════════════════════════════
    // 创建目标（CommandExecutor case "set_goal" 调用）
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    ///     创建目标并进入 EXECUTING_GOAL。返回 false 时 error 携带失败原因（LLM 可感知）。
    ///     parameters 支持 {quantity: N, targetItemId: "..."}（water_crops/fight 只读 quantity）。
    /// </summary>
    public bool CreateGoal(
        AgentInstance agent,
        NPC npc,
        string typeStr,
        Dictionary<string, object>? parameters,
        bool reportBack,
        string? callId,
        out string error)
    {
        error = string.Empty;
        if (agent == null || npc == null)
        {
            error = "agent_missing";
            return false;
        }

        if (agent.Health.IsDead)
        {
            error = "npc_dead";
            return false;
        }

        var type = GoalBase.ParseGoalType(typeStr);
        if (type == null)
        {
            error = $"unknown_goal_type: {typeStr}";
            return false;
        }

        var (quantity, targetItemId) = GoalBase.ParseParameters(parameters);

        // 已有活跃目标 → 取消旧的（handler forced target 一并清理）
        if (agent.Brain.PendingGoal != null)
        {
            agent.Brain.PendingGoal.Cancel("superseded");
            ClearHandlerTargets(agent.NpcName);
            _ = _reportStartMinutes.Remove(agent.NpcName);
        }

        IGoal? goal = type switch
        {
            GoalType.ChopTree => new ChopTreeGoal(
                agent.NpcName, reportBack, callId ?? string.Empty, quantity,
                _config.Goals.GlobalTimeoutMinutes, _config.Goals.StuckTickThreshold, _monitor, _movementService),
            GoalType.Mine => new MineGoal(
                agent.NpcName, reportBack, callId ?? string.Empty, quantity, targetItemId,
                _config.Goals.GlobalTimeoutMinutes, _config.Goals.StuckTickThreshold, _mineHandler!),
            GoalType.WaterCrops => new WaterCropsGoal(
                agent.NpcName, reportBack, callId ?? string.Empty, quantity,
                _config.Goals.GlobalTimeoutMinutes, _config.Goals.StuckTickThreshold, _farmHandler!),
            GoalType.Fight => new FightGoal(
                agent.NpcName, reportBack, callId ?? string.Empty, quantity,
                _config.Goals.GlobalTimeoutMinutes, _config.Goals.StuckTickThreshold, _fightHandler!),
            GoalType.Forage => new ForageGoal(
                agent.NpcName, reportBack, callId ?? string.Empty, quantity, targetItemId,
                _config.Goals.GlobalTimeoutMinutes, _config.Goals.StuckTickThreshold, _forageHandler!),
            _ => null,
        };

        if (goal == null)
        {
            error = $"goal_dependency_missing: {typeStr}";
            return false;
        }

        agent.Brain.PendingGoal = goal;
        goal.Start(agent, npc);
        agent.StateMachine.ForceTransition(AgentState.EXECUTING_GOAL, true, fromDecision: true, reason: "set_goal");
        _monitor.Log(
            $"[Goal] {agent.NpcName}: set_goal({GoalBase.GoalTypeToWire(type.Value)}, qty={quantity}, reportBack={reportBack})",
            LogLevel.Info);
        return true;
    }

    /// <summary>取消指定 NPC 的活跃目标并清理 Handler 状态（玩家对话打断等场景预留）。</summary>
    public void CancelGoal(string npcName, string reason)
    {
        if (!_agentService.TryGetAgent(npcName, out var agent) || agent?.Brain.PendingGoal == null)
        {
            return;
        }

        agent.Brain.PendingGoal.Cancel(reason);
        ClearHandlerTargets(npcName);
        _ = _reportStartMinutes.Remove(npcName);
        _monitor.Log($"[Goal] {npcName}: goal cancelled ({reason})", LogLevel.Info);
    }

    /// <summary>
    ///     目标 tick 异常后的自愈收尾（issue #24）：Fail → 回报 TS → 清引用 → ForceIdle。
    ///     Fail 只在 Executing 态转移（幂等）；PendingGoal 已清空（目标刚正常收尾时异常）
    ///     则只做 ForceIdle。自身再抛异常只留痕，绝不外抛回状态机。
    /// </summary>
    private void TryFinalizeAsFailure(AgentInstance agent, string reason)
    {
        try
        {
            var goal = agent.Brain.PendingGoal;
            if (goal != null)
            {
                goal.Fail(reason);
                SendGoalResult(goal, success: false);
                agent.Brain.PendingGoal = null;
                ClearHandlerTargets(agent.NpcName);
            }

            ForceIdle(agent, reason);
        }
        catch (Exception ex)
        {
            _monitor.Log($"[Goal] {agent.NpcName}: goal cleanup after tick failure also failed: {ex}", LogLevel.Error);
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // 状态 action：EXECUTING_GOAL / TRAVELING_TO_REPORT
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>注册为 EXECUTING_GOAL 状态 action：每 tick 驱动目标并做收尾决策。</summary>
    public void TickExecuting(NPC npc, AgentInstance agent, int currentTick)
    {
        // issue #24：状态 action 泵隔离。目标 tick 抛异常 → 判定该目标失败并自愈
        // （Fail → 回报 TS → 清引用 → ForceIdle，复用 FinalizeFailure 语义），
        // 不让 NPC 永久卡在 EXECUTING_GOAL 每 tick 重演同一条异常；自愈再失败也只留痕。
        try
        {
            TickExecutingCore(npc, agent, currentTick);
        }
        catch (Exception ex)
        {
            if (QueueTelemetry.ShouldWarn($"safeseg:goal-executing:{agent.NpcName}"))
            {
                _monitor.Log($"[Goal] {agent.NpcName}: TickExecuting failed — failing goal: {ex}", LogLevel.Error);
                ModErrorLog.LogError("SafeRun", $"TickExecuting failed for {agent.NpcName}", ex);
            }

            TryFinalizeAsFailure(agent, "goal_tick_error");
        }
    }

    private void TickExecutingCore(NPC npc, AgentInstance agent, int currentTick)
    {
        var goal = agent.Brain.PendingGoal;
        if (goal == null)
        {
            // 无目标但停留在执行态 → 兜底回 IDLE
            ForceIdle(agent, "goal_missing");
            return;
        }

        if (agent.Health.IsDead)
        {
            goal.Fail("npc_dead");
        }

        goal.Tick(agent, npc, currentTick);

        switch (DecideExecuting(goal.Status, goal.ReportBack))
        {
            case ExecutingOutcome.KeepExecuting:
                // Handler 自退出（状态漂移到 IDLE）由 AgentTickLoop 恢复守卫拉回，此处不动
                break;

            case ExecutingOutcome.BeginReport:
                BeginReportTravel(agent, npc, goal);
                break;

            case ExecutingOutcome.FinalizeSuccess:
                SendGoalResult(goal, success: true);
                agent.Brain.PendingGoal = null;
                ForceIdle(agent, "goal_complete");
                break;

            case ExecutingOutcome.FinalizeFailure:
                SendGoalResult(goal, success: false);
                agent.Brain.PendingGoal = null;
                ForceIdle(agent, "goal_failed");
                break;

            case ExecutingOutcome.FinalizeCancel:
                agent.Brain.PendingGoal = null;
                ForceIdle(agent, "goal_cancelled");
                break;
        }
    }

    /// <summary>注册为 TRAVELING_TO_REPORT 状态 action：寻路回玩家 + 到达触发汇报。</summary>
    public void TickReporting(NPC npc, AgentInstance agent, int currentTick)
    {
        // issue #24：汇报链泵隔离。汇报阶段异常 → 按完成收尾（目标工作大概率已完成，
        // 正在回程汇报；照 FallbackSend 语义回报成功），避免 NPC 永久卡在 TRAVELING_TO_REPORT；
        // 收尾自身再抛也只留痕。
        try
        {
            TickReportingCore(npc, agent, currentTick);
        }
        catch (Exception ex)
        {
            if (QueueTelemetry.ShouldWarn($"safeseg:goal-reporting:{agent.NpcName}"))
            {
                _monitor.Log($"[Goal] {agent.NpcName}: TickReporting failed — finalizing report: {ex}", LogLevel.Error);
                ModErrorLog.LogError("SafeRun", $"TickReporting failed for {agent.NpcName}", ex);
            }

            TryFinishReportingAfterFailure(npc, agent);
        }
    }

    /// <summary>汇报链异常后的收尾（issue #24）：照 FallbackSend 语义回报成功并 FinishReport；再失败只留痕。</summary>
    private void TryFinishReportingAfterFailure(NPC npc, AgentInstance agent)
    {
        try
        {
            var goal = agent.Brain.PendingGoal;
            if (goal != null)
            {
                SendGoalResult(goal, success: true);
            }

            FinishReport(agent, npc, "report_error");
        }
        catch (Exception ex)
        {
            _monitor.Log($"[Goal] {agent.NpcName}: report cleanup after failure also failed: {ex}", LogLevel.Error);
        }
    }

    private void TickReportingCore(NPC npc, AgentInstance agent, int currentTick)
    {
        var goal = agent.Brain.PendingGoal;
        if (goal == null)
        {
            ForceIdle(agent, "report_no_goal");
            return;
        }

        var reportTimeoutMinutes = _config.Goals.ReportTimeoutMinutes;
        var elapsedMinutes = GoalTime.ElapsedMinutes(
            _reportStartMinutes.GetValueOrDefault(agent.NpcName, GoalTime.TimeOfDayToMinutes(Game1.timeOfDay)),
            GoalTime.TimeOfDayToMinutes(Game1.timeOfDay));
        var isDead = agent.Health.IsDead;
        var arrived = npc.currentLocation == Game1.player.currentLocation
            && Vector2.Distance(npc.Tile, Game1.player.Tile) < _config.Goals.ReportArrivalDistance;

        switch (DecideReporting(isDead, arrived, GoalTime.IsTimedOut(elapsedMinutes, reportTimeoutMinutes)))
        {
            case ReportingOutcome.Arrived:
                SendGoalResult(goal, success: true);
                FinishReport(agent, npc, "goal_reported");
                break;

            case ReportingOutcome.FallbackSend:
                _monitor.Log(
                    $"[Goal] {agent.NpcName}: report fallback (dead={isDead}, timedOut={GoalTime.IsTimedOut(elapsedMinutes, reportTimeoutMinutes)})",
                    LogLevel.Warn);
                SendGoalResult(goal, success: true);
                FinishReport(agent, npc, "report_fallback");
                break;

            case ReportingOutcome.KeepReporting:
                DriveReportTravel(npc, agent, currentTick);
                break;
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // 内部实现
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>目标完成 → 开始寻路回玩家（同图 MoveTo，跨图 AgentNavigator），状态 = TRAVELING_TO_REPORT。</summary>
    private void BeginReportTravel(AgentInstance agent, NPC npc, IGoal goal)
    {
        _reportStartMinutes[agent.NpcName] = GoalTime.TimeOfDayToMinutes(Game1.timeOfDay);

        if (npc.currentLocation == Game1.player.currentLocation)
        {
            _ = _movementService.MoveTo(npc, Game1.player.Tile, MovementMode.LongRange, Game1.ticks);
        }
        else if (_navigator != null)
        {
            var playerLocation = Game1.player.currentLocation?.NameOrUniqueName ?? "Farm";
            _navigator.NavigateToTaskLocation(npc, new[] { playerLocation }, Game1.ticks, out _);
        }
        else
        {
            Game1.warpCharacter(npc, Game1.player.currentLocation, Game1.player.Tile);
        }

        _monitor.Log(
            $"[Goal] {agent.NpcName}: goal complete, traveling to report ({goal.DescribeProgress()})",
            LogLevel.Info);
        agent.StateMachine.ForceTransition(
            AgentState.TRAVELING_TO_REPORT, true, fromDecision: true, reason: "goal_report");
    }

    /// <summary>汇报寻路推进：跨图走 AgentNavigator，同图走 MoveTo + 卡死检测。</summary>
    private void DriveReportTravel(NPC npc, AgentInstance agent, int currentTick)
    {
        if (npc.currentLocation != Game1.player.currentLocation)
        {
            if (_navigator != null)
            {
                if (!_navigator.IsTravelling(npc.Name))
                {
                    var playerLocation = Game1.player.currentLocation?.NameOrUniqueName ?? "Farm";
                    _navigator.NavigateToTaskLocation(npc, new[] { playerLocation }, currentTick, out _);
                }

                _ = _navigator.ProgressTravel(npc, currentTick);
            }

            return;
        }

        if (_movementService.HandleStuck(npc, Game1.player.Tile, currentTick))
        {
            return;
        }

        var result = _movementService.MoveTo(npc, Game1.player.Tile, MovementMode.LongRange, currentTick);
        if (result == MoveResult.NoPathFound)
        {
            _monitor.Log($"[Goal] {agent.NpcName}: report pathfinding failed ({result}) — fallback report", LogLevel.Warn);
            SendGoalResult(agent.Brain.PendingGoal!, success: true);
            FinishReport(agent, npc, "report_path_failed");
        }
    }

    /// <summary>汇报收尾：清空目标 + 回 IDLE。</summary>
    private void FinishReport(AgentInstance agent, NPC npc, string reason)
    {
        _movementService.Stop(npc, reason);
        agent.Brain.PendingGoal = null;
        _ = _reportStartMinutes.Remove(agent.NpcName);
        ForceIdle(agent, reason);
    }

    /// <summary>强制回 IDLE（跳过 min-duration 守卫，set_goal 属 LLM 决策语义）。</summary>
    private void ForceIdle(AgentInstance agent, string reason)
    {
        if (agent.StateMachine.CurrentStateFlag == AgentState.IDLE)
        {
            return;
        }

        agent.StateMachine.ForceTransition(AgentState.IDLE, true, fromDecision: true, reason: reason);
    }

    /// <summary>回发目标结果（action_result，TS 端 routeToolResult 感知成功/失败；不新增 wire 消息）。</summary>
    private void SendGoalResult(IGoal goal, bool success)
    {
        if (_agentServerProvider == null)
        {
            _monitor?.Log($"[Goal] {goal.NpcName}: no server provider — goal result not sent", LogLevel.Debug);
            return;
        }

        try
        {
            var result = new Dictionary<string, object>
            {
                ["status"] = success ? "complete" : "failed",
                ["goalType"] = GoalBase.GoalTypeToWire(goal.Type),
                ["progress"] = goal.DescribeProgress(),
                ["collected"] = goal.CollectedCount,
            };
            if (!success)
            {
                result["reason"] = goal.Reason;
            }

            var msg = new ProtocolV2.ActionResultMessage
            {
                NpcName = goal.NpcName,
                Tool = "set_goal",
                Action = "set_goal",
                CallId = (goal as GoalBase)?.CallId ?? string.Empty,
                Success = success,
                Reason = success ? ActionResultReason.None : ActionResultReason.InternalError,
                Result = result,
                RequestId = Guid.NewGuid().ToString("N"),
            };
            _ = _agentServerProvider.SendMessageAsync(MessageProtocol.Serialize(msg));
            _monitor.Log(
                $"[Goal] {goal.NpcName}: sent set_goal result (success={success}, {goal.DescribeProgress()})",
                LogLevel.Debug);
        }
        catch (InvalidOperationException ex)
        {
            _monitor?.Log($"[Goal] Failed to send set_goal result via WS: {ex}", LogLevel.Warn);
        }
    }

    /// <summary>清理各 Handler 的 forced-target 残留（取消/替换目标时）。</summary>
    private void ClearHandlerTargets(string npcName)
    {
        _farmHandler?.ClearForcedTarget(npcName);
        _mineHandler?.ClearForcedTarget(npcName);
        _fightHandler?.ClearForcedTarget(npcName);
        _forageHandler?.ClearForcedTarget(npcName);
    }
}
