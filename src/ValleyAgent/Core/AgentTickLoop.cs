using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using ValleyAgent.Brain;
using ValleyAgent.Config;
using ValleyAgent.Goals;
using ValleyAgent.Navigation;
using ValleyAgent.Services;
using ValleyAgent.StateMachine;

namespace ValleyAgent.Core;

public class AgentTickLoop
{
    public enum ProcessResult
    {
        /// <summary>Agent skipped (NPC not found or vanilla-released). ModEntry should not apply controller.</summary>
        Skip,

        /// <summary>Agent is dead. ModEntry should handle dead NPC.</summary>
        Dead,

        /// <summary>Normal — ModEntry should call ApplyControllerToNpc.</summary>
        Normal
    }

    // 超时：15 秒 = 900 ticks (60fps)
    private const int ExitPathTimeoutTicks = 900;
    private readonly ModConfig _config;
    private readonly Dictionary<string, int> _consecutiveIdleDecisions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _exitPathStartTick = new(StringComparer.OrdinalIgnoreCase);

    // WalkingBack 阶段：NPC 正在走回日程位置
    private readonly Dictionary<string, Vector2> _exitPathTargets = new(StringComparer.OrdinalIgnoreCase);

    private readonly IMonitor _monitor;
    private readonly IMovementService _movementService;
    private readonly HashSet<string> _releasedToVanilla = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _vanillaFinalized = new(StringComparer.OrdinalIgnoreCase);

    public AgentTickLoop(IMonitor monitor, IMovementService movementService, ModConfig config)
    {
        _monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        _movementService = movementService ?? throw new ArgumentNullException(nameof(movementService));
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    /// <summary>
    ///     Number of agents currently released to vanilla schedule.
    /// </summary>
    public int ReleasedCount
    {
        get => _releasedToVanilla.Count;
    }

    /// <summary>
    ///     NPC 已完全交还原版日程控制时触发。
    ///     此时 followSchedule=true, ignoreScheduleToday=false, checkSchedule 已调用。
    /// </summary>
    public event EventHandler<VanillaReleaseEventArgs>? OnVanillaReleaseFinalized;

    /// <summary>
    ///     Whether the given NPC has been released to vanilla schedule.
    /// </summary>
    public bool IsReleasedToVanilla(string npcName)
        => _releasedToVanilla.Contains(npcName);

    /// <summary>
    ///     立即把 agent 交还原版日程（set_state IDLE / stop 等显式"结束交互"信号用）。
    ///     不直接动 NPC——下一 tick 由 released 分支接管：BeginWalkBack → FinalizeVanillaRelease，
    ///     避免 agent 漫步与原版 schedule 双写 controller。
    /// </summary>
    public void ReleaseToVanillaNow(string npcName)
    {
        _ = _releasedToVanilla.Add(npcName);
        _monitor.Log($"{npcName}: explicit release to vanilla schedule requested", LogLevel.Info);
    }

    /// <summary>
    ///     Whether the given NPC has been fully finalized to vanilla (schedule re-engaged).
    /// </summary>
    public bool IsVanillaFinalized(string npcName)
        => _vanillaFinalized.Contains(npcName);

    /// <summary>
    ///     Whether the given NPC is currently walking back to schedule position.
    /// </summary>
    public bool IsWalkingBack(string npcName)
        => _exitPathTargets.ContainsKey(npcName);

    /// <summary>
    ///     Remove a released agent from the vanilla list (e.g., on non-IDLE decision).
    /// </summary>
    public void RemoveReleasedToVanilla(string npcName)
    {
        _ = _releasedToVanilla.Remove(npcName);
        _ = _vanillaFinalized.Remove(npcName);
        _ = _exitPathTargets.Remove(npcName);
        _ = _exitPathStartTick.Remove(npcName);
        _ = _consecutiveIdleDecisions.Remove(npcName);
    }

    /// <summary>
    ///     Reset idle decision count for test scenarios that force state changes.
    /// </summary>
    public void ResetIdleCounter(string npcName)
        => _consecutiveIdleDecisions.Remove(npcName);

    /// <summary>
    ///     Get the number of consecutive IDLE decisions for the given NPC.
    ///     Used by RuleBasedDecisionEngine to gate TALK transitions.
    /// </summary>
    public int GetConsecutiveIdleCount(string npcName)
        => _consecutiveIdleDecisions.TryGetValue(npcName, out var count) ? count : 0;

    /// <summary>
    ///     Track a consecutive IDLE decision and potentially release the agent.
    ///     FIXED (CR-3): Only tracks IDLE decisions when the agent is ACTUALLY in IDLE state.
    /// </summary>
    public void TrackIdleDecision(AgentInstance agent, string npcName)
    {
        if (agent.StateMachine.CurrentStateFlag != AgentState.IDLE)
        {
            _monitor.Log(
                $"{npcName}: IDLE decision ignored — agent is in active state ({agent.StateMachine.CurrentStateFlag})");
            return;
        }

        if (!_consecutiveIdleDecisions.TryGetValue(npcName, out var count))
        {
            count = 0;
        }

        _consecutiveIdleDecisions[npcName] = count + 1;

        if (count + 1 >= _config.MaxConsecutiveIdleBeforeRelease)
        {
            agent.Brain.SyncEmotion(NpcEmotion.Neutral, 0.1f, "IdleTooLong");
        }

        if (count + 1 >= _config.MaxConsecutiveIdleBeforeRelease)
        {
            _ = _releasedToVanilla.Add(npcName);
            _monitor.Log($"{npcName}: {count + 1} consecutive IDLE decisions — releasing to vanilla schedule",
                LogLevel.Info);
        }
    }

    /// <summary>
    ///     Reset on any active (non-IDLE) decision — re-engage from vanilla release.
    /// </summary>
    public void ResetIdleTracking(string npcName, AgentState targetState)
    {
        _consecutiveIdleDecisions[npcName] = 0;
        _ = _releasedToVanilla.Remove(npcName);
        _ = _vanillaFinalized.Remove(npcName);
        _ = _exitPathTargets.Remove(npcName);
        _ = _exitPathStartTick.Remove(npcName);
        _monitor.Log($"{npcName}: active decision ({targetState}) — re-engaged from vanilla");
    }

    /// <summary>
    ///     Process a single agent for one tick.
    /// </summary>
    public ProcessResult ProcessAgent(AgentInstance agent, string? lastDialogueNpcName)
    {
        var npc = Game1.getCharacterFromName(agent.NpcName);
        if (npc == null)
        {
            return ProcessResult.Skip;
        }

        // ── Goal 接管（阶段 2）：活跃目标优先于 vanilla release / IDLE 漫步 ──
        // Handler 单次动作后自退出（ForceTransition(IDLE)），但 Goal 未完成时要把 NPC
        // 拉回 EXECUTING_GOAL 继续执行（spec §1.2 "GoalExecutor 包装 handler.Update"）。
        // TALK 中不打断（玩家对话优先，对话结束后自然恢复）；死亡不恢复（由收尾处理）。
        var activeGoal = agent.Brain.PendingGoal;
        if (activeGoal != null)
        {
            if (activeGoal.Status == GoalStatus.Cancelled)
            {
                // 取消目标：清理引用（不汇报，静默回 IDLE 由状态机处理）
                agent.Brain.PendingGoal = null;
            }
            else if (agent.Health.IsDead)
            {
                if (activeGoal.Status == GoalStatus.Executing)
                {
                    activeGoal.Fail("npc_dead");
                }
            }
            else if (activeGoal.Status == GoalStatus.Executing
                     && agent.StateMachine.CurrentStateFlag != AgentState.EXECUTING_GOAL
                     && agent.StateMachine.CurrentStateFlag != AgentState.TALK)
            {
                agent.StateMachine.ForceTransition(
                    AgentState.EXECUTING_GOAL, true, fromDecision: true, reason: "goal-active");
            }
            else if (activeGoal.Status == GoalStatus.Failed
                     && agent.StateMachine.CurrentStateFlag != AgentState.EXECUTING_GOAL
                     && agent.StateMachine.CurrentStateFlag != AgentState.TRAVELING_TO_REPORT)
            {
                // NPC 死亡中断导致的失败目标残留（TickExecuting 无法再运行）→ 清理引用
                agent.Brain.PendingGoal = null;
            }
        }

        // ── Vanilla-release check ──
        if (_releasedToVanilla.Contains(agent.NpcName))
        {
            if (agent.StateMachine.CurrentStateFlag != AgentState.IDLE)
            {
                // 非 IDLE 状态（LLM 返回了活跃决策）→ 重新接管
                ReEngageAgent(agent.NpcName, npc);
                // fall through 到 Normal 处理
            }
            else
            {
                // 玩家对话触发重新激活
                if (!string.IsNullOrEmpty(lastDialogueNpcName)
                    && agent.NpcName.Equals(lastDialogueNpcName, StringComparison.OrdinalIgnoreCase))
                {
                    ReEngageAgent(agent.NpcName, npc);
                    npc.facePlayer(Game1.player);
                    npc.Speed = 0;
                    // fall through 到 Normal 处理
                }
                else if (_vanillaFinalized.Contains(agent.NpcName))
                {
                    // 已交还 → 不干预原版移动
                    return ProcessResult.Skip;
                }
                else if (_exitPathTargets.ContainsKey(agent.NpcName))
                {
                    // WalkingBack 阶段：检测到达/超时
                    TickWalkingBack(agent.NpcName, npc);
                    return ProcessResult.Skip;
                }
                else
                {
                    // 首次进入释放状态 → 开始走回日程位置
                    BeginWalkBack(agent.NpcName, npc);
                    return ProcessResult.Skip;
                }
            }
        }

        _movementService.Unfreeze(npc);
        if (npc.Speed == 0)
        {
            npc.Speed = MovementConstants.EffectivePathfindSpeed;
        }

        // ── Dead NPC check ──
        if (agent.Health.IsDead)
        {
            return ProcessResult.Dead;
        }

        // ── Dialogue handling ──
        if (!string.IsNullOrEmpty(lastDialogueNpcName)
            && agent.NpcName.Equals(lastDialogueNpcName, StringComparison.OrdinalIgnoreCase))
        {
            npc.facePlayer(Game1.player);
            npc.Speed = 0;
        }

        return ProcessResult.Normal;
    }

    /// <summary>
    ///     阶段1：NPC 开始走回日程位置。
    ///     计算目标 tile 并通过 MovementService 发起寻路。
    /// </summary>
    private void BeginWalkBack(string npcName, NPC npc)
    {
        _movementService.Stop(npc, "vanilla-release-begin-walkback");
        npc.Speed = MovementConstants.DefaultPathfindSpeed;

        // 尝试从原版日程获取目标位置
        var targetTile = GetScheduleTargetTile(npc);

        // 无日程 → 用 DefaultPosition
        if (targetTile == null)
        {
            var defaultMap = npc.DefaultMap;
            var defaultPos = npc.DefaultPosition;
            var targetLocation = Game1.getLocationFromName(defaultMap);

            if (targetLocation == null)
            {
                _monitor.Log($"{npcName}: no schedule and default map '{defaultMap}' not found — warping to player",
                    LogLevel.Warn);
                Game1.warpCharacter(npc, Game1.player.currentLocation, Game1.player.Tile);
                FinalizeVanillaRelease(npcName, npc);
                return;
            }

            // 跨地图 → 直接传送并交还
            if (npc.currentLocation?.Name != defaultMap)
            {
                var destTile = new Vector2((int)(defaultPos.X / 64f), (int)(defaultPos.Y / 64f));
                Game1.warpCharacter(npc, targetLocation, destTile);
                _monitor.Log($"{npcName}: schedule target is cross-map — warped to {defaultMap}", LogLevel.Info);
                FinalizeVanillaRelease(npcName, npc);
                return;
            }

            targetTile = new Vector2((int)(defaultPos.X / 64f), (int)(defaultPos.Y / 64f));
        }

        // 已经在目标位置附近 → 直接交还
        if (Vector2.Distance(npc.Tile, targetTile.Value) < 2f)
        {
            _monitor.Log($"{npcName}: already near schedule position — finalizing immediately", LogLevel.Info);
            FinalizeVanillaRelease(npcName, npc);
            return;
        }

        // 发起寻路
        var result = _movementService.MoveTo(npc, targetTile.Value, MovementMode.LongRange, Game1.ticks);
        if (result == MoveResult.Success || result == MoveResult.AlreadyMoving ||
            result == MoveResult.BlockedByCooldown)
        {
            _exitPathTargets[npcName] = targetTile.Value;
            _exitPathStartTick[npcName] = Game1.ticks;
            _monitor.Log(
                $"{npcName}: walking back to schedule position ({targetTile.Value.X:F0},{targetTile.Value.Y:F0})",
                LogLevel.Info);
        }
        else
        {
            // 寻路失败 → 直接传送并交还
            _monitor.Log($"{npcName}: pathfinding to schedule position failed ({result}) — warping", LogLevel.Warn);
            var targetLocation = Game1.getLocationFromName(npc.DefaultMap);
            if (targetLocation != null && npc.currentLocation?.Name != npc.DefaultMap)
            {
                Game1.warpCharacter(npc, targetLocation, new Vector2(
                    (int)(npc.DefaultPosition.X / 64f), (int)(npc.DefaultPosition.Y / 64f)));
            }

            FinalizeVanillaRelease(npcName, npc);
        }
    }

    /// <summary>
    ///     WalkingBack 阶段每 tick 检测：到达目标 / 超时 / 寻路中断。
    /// </summary>
    private void TickWalkingBack(string npcName, NPC npc)
    {
        if (!_exitPathTargets.TryGetValue(npcName, out var targetTile)
            || !_exitPathStartTick.TryGetValue(npcName, out var startTick))
        {
            return;
        }

        var elapsed = Game1.ticks - startTick;

        // 超时 → 传送并交还
        if (elapsed > ExitPathTimeoutTicks)
        {
            _monitor.Log($"{npcName}: walkback timed out ({elapsed / 60}s) — warping to target", LogLevel.Warn);
            _movementService.Stop(npc, "walkback-timeout");
            var targetLocation = Game1.getLocationFromName(npc.DefaultMap);
            if (targetLocation != null && npc.currentLocation?.Name != npc.DefaultMap)
            {
                Game1.warpCharacter(npc, targetLocation, new Vector2(
                    (int)(npc.DefaultPosition.X / 64f), (int)(npc.DefaultPosition.Y / 64f)));
            }

            FinalizeVanillaRelease(npcName, npc);
            return;
        }

        // 到达目标附近 → 交还
        var dist = Vector2.Distance(npc.Tile, targetTile);
        if (dist < 2f)
        {
            _monitor.Log($"{npcName}: reached schedule position — finalizing vanilla release", LogLevel.Info);
            _movementService.Stop(npc, "walkback-arrived");
            FinalizeVanillaRelease(npcName, npc);
            return;
        }

        // 寻路中断（controller 被清除）→ 重新发起
        if (!_movementService.IsMoving(npcName) && npc.controller == null)
        {
            var result = _movementService.MoveTo(npc, targetTile, MovementMode.LongRange, Game1.ticks);
            if (result != MoveResult.Success && result != MoveResult.BlockedByCooldown)
            {
                _monitor.Log($"{npcName}: walkback pathfinding lost and retry failed ({result}) — warping",
                    LogLevel.Warn);
                var targetLocation = Game1.getLocationFromName(npc.DefaultMap);
                if (targetLocation != null && npc.currentLocation?.Name != npc.DefaultMap)
                {
                    Game1.warpCharacter(npc, targetLocation, new Vector2(
                        (int)(npc.DefaultPosition.X / 64f), (int)(npc.DefaultPosition.Y / 64f)));
                }

                FinalizeVanillaRelease(npcName, npc);
            }
        }
    }

    /// <summary>
    ///     从 NPC 的 Schedule 中获取当前时间对应的目标 tile。
    ///     返回 null 表示无可用日程。
    /// </summary>
    private Vector2? GetScheduleTargetTile(NPC npc)
    {
        // 尝试让 SDV 加载日程
        try
        {
            if (npc.Schedule == null)
            {
                // ignoreScheduleToday=true 会阻止 checkSchedule 加载日程
                // 临时允许以触发加载
                var wasIgnoring = npc.ignoreScheduleToday;
                npc.ignoreScheduleToday = false;
                npc.checkSchedule(Game1.timeOfDay);
                npc.ignoreScheduleToday = wasIgnoring;
            }
            else
            {
                npc.checkSchedule(Game1.timeOfDay);
            }
        }
        catch (InvalidOperationException ex)
        {
            _monitor.Log($"{npc.Name}: checkSchedule for target lookup failed: {ex.Message}");
        }

        // checkSchedule 成功后 controller 的终点就是日程位置
        if (npc.controller != null && npc.controller.pathToEndPoint != null && npc.controller.pathToEndPoint.Count > 0)
        {
            var endPoint = npc.controller.pathToEndPoint.Peek();
            npc.controller = null;
            npc.Halt();
            return new Vector2(endPoint.X, endPoint.Y);
        }

        // 从 Schedule 字典中查找当前时间最近的条目
        if (npc.Schedule != null && npc.Schedule.Count > 0)
        {
            var currentTime = Game1.timeOfDay;
            Point? bestTarget = null;
            var bestTime = 0;

            foreach (var kvp in npc.Schedule)
            {
                if (kvp.Key <= currentTime && kvp.Key >= bestTime)
                {
                    bestTime = kvp.Key;
                    var desc = kvp.Value;
                    if (desc?.route != null && desc.route.Count > 0)
                    {
                        // route 的最后一个点是目标位置
                        var routeCopy = new Stack<Point>(new Stack<Point>(desc.route));
                        while (routeCopy.Count > 1)
                        {
                            routeCopy.Pop();
                        }

                        bestTarget = routeCopy.Pop();
                    }
                }
            }

            if (bestTarget.HasValue)
            {
                return new Vector2(bestTarget.Value.X, bestTarget.Value.Y);
            }
        }

        return null;
    }

    /// <summary>
    ///     阶段2：NPC 已到达日程位置，正式交还原版日程控制。
    /// </summary>
    private void FinalizeVanillaRelease(string npcName, NPC npc)
    {
        _movementService.Stop(npc, "vanilla-release-finalize");

        // 清除 WalkingBack 状态
        _exitPathTargets.Remove(npcName);
        _exitPathStartTick.Remove(npcName);

        // 恢复原版日程控制
        npc.followSchedule = true;
        npc.ignoreScheduleToday = false;
        npc.Speed = MovementConstants.DefaultPathfindSpeed;

        // 让原版 schedule 接管后续移动
        try
        {
            npc.checkSchedule(Game1.timeOfDay);
        }
        catch (InvalidOperationException ex)
        {
            _monitor.Log($"{npcName}: checkSchedule after finalize failed: {ex.Message}", LogLevel.Warn);
        }

        _vanillaFinalized.Add(npcName);
        _monitor.Log($"{npcName}: vanilla release finalized — followSchedule=true, schedule re-engaged", LogLevel.Info);

        OnVanillaReleaseFinalized?.Invoke(this, new VanillaReleaseEventArgs(npcName));
    }

    /// <summary>
    ///     从原版释放状态重新接管 NPC（LLM 返回活跃决策 或 玩家对话触发）。
    /// </summary>
    private void ReEngageAgent(string npcName, NPC npc)
    {
        _ = _releasedToVanilla.Remove(npcName);
        _ = _vanillaFinalized.Remove(npcName);
        _ = _exitPathTargets.Remove(npcName);
        _ = _exitPathStartTick.Remove(npcName);
        _ = _consecutiveIdleDecisions.Remove(npcName);
        _movementService.Unfreeze(npc);

        // 禁用原版日程，防止和 Agent 控制冲突
        npc.followSchedule = false;
        npc.ignoreScheduleToday = true;

        // 停止原版 schedule 创建的 PathFindController
        npc.controller = null;

        if (npc.Speed == 0)
        {
            npc.Speed = MovementConstants.DefaultPathfindSpeed;
        }

        _monitor.Log($"{npcName}: re-engaged from vanilla release", LogLevel.Info);
    }

    /// <summary>
    ///     清除所有释放状态（DayStarted 时调用）。
    /// </summary>
    public void ClearAllReleaseState()
    {
        _releasedToVanilla.Clear();
        _vanillaFinalized.Clear();
        _exitPathTargets.Clear();
        _exitPathStartTick.Clear();
        _consecutiveIdleDecisions.Clear();
    }
}

/// <summary>
///     NPC 交还原版日程时的事件参数。
/// </summary>
public class VanillaReleaseEventArgs : EventArgs
{
    public VanillaReleaseEventArgs(string npcName)
    {
        NpcName = npcName;
    }

    public string NpcName { get; }
}