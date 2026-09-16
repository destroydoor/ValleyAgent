using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Pathfinding;
using ValleyAgent.Utils;

namespace ValleyAgent.Navigation
{
    /// <summary>
    /// Handles NPC navigation: same-map PathFindController following
    /// and cross-map travel simulation with smart warp entry points.
    /// All PathFindController lifecycle is delegated to IMovementService.
    ///
    /// Fix #9: stuck offset targets are now validated for walkability.
    /// Fix #10: follow hysteresis logic lives directly in UpdateLocalFollowing (旧 FollowController 已删除).
    /// Fix #11: NPC is hidden during cross-map travel to prevent visible "frozen" state.
    /// </summary>
    public class AgentNavigator
    {
        private readonly LocationGraph _locationGraph;
        private readonly IMonitor? _monitor;
        private readonly IMovementService _movementService;

        // ─── Follow hysteresis (原 FollowController 逻辑内联于此) ─────────────
        // Fix #10: direct distance-based hysteresis (无控制器间滞后).
        // NPC stops when close (< closeThreshold), starts when far (> farThreshold).
        // In the dead zone, maintains previous intent to avoid flicker.
        private const float CloseDistanceThreshold = 2.0f;
        private readonly Dictionary<string, bool> _wantsToFollow = new(StringComparer.OrdinalIgnoreCase);

        // Per-agent pathfinding stuck detection
        private const int PathfindStuckThresholdTicks = 180;
        private const float PathfindStuckDistanceThreshold = 0.5f;
        private readonly Dictionary<string, Vector2> _pathFindStartPosition = new(StringComparer.OrdinalIgnoreCase);

        // Grace period before destroying controller when IsMoving becomes false
        private const int IsMovingFalseGraceTicks = 60;
        private readonly Dictionary<string, int> _isMovingFalseSinceTick = new(StringComparer.OrdinalIgnoreCase);

        // 跟随改道节流：防止玩家连续移动时每 tick 重建路径
        private readonly Dictionary<string, int> _lastFollowRepathTick = new(StringComparer.OrdinalIgnoreCase);

        // 跟随 NoPathFound 节流：A* 同步无路（NPC 被困死胡同等）时避免每 tick 重试刷屏。
        // 上层（UpdateTravel 到达步进）不受此限制——步进后位置改变，重试即时。
        private readonly Dictionary<string, int> _lastNoPathFoundTick = new(StringComparer.OrdinalIgnoreCase);
        private const int NoPathFoundRetryCooldownTicks = 60;

        // 跨图目标等待日志节流（2026-09-10 修复：目标在别的图时原地等待）
        private readonly Dictionary<string, int> _lastCrossMapFollowLogTick = new(StringComparer.OrdinalIgnoreCase);
        private const int CrossMapFollowLogCooldownTicks = 300;

        // 上次跟随 MoveTo 的目标瓦片。
        // 必须自己跟踪：PathFindController 的 4 参数 + bool 构造函数有 bug，
        // 不会设置 this.endPoint 字段（始终为 Point.Zero=(0,0)），
        // 因此不能依赖 pfc.endPoint 检测目标变化。
        private readonly Dictionary<string, Point> _lastFollowTarget = new(StringComparer.OrdinalIgnoreCase);

        // Travel state for cross-map following
        private readonly Dictionary<string, TravelState> _travelStates = new(StringComparer.OrdinalIgnoreCase);
        // ─── Cross-map travel timing ─────────────────────────────────────────
        // BaseTicksPerHop: ~2s per hop (reduced from 6s — NPC is hidden, no visual impact)
        // SingleHopTicks: ~3s for direct-adjacent maps。过短（旧值 30 tick ≈ 0.5s）会导致
        // 玩家刚进图还站在入口时 NPC 就出现在入口，视觉上等同传送贴脸。
        // 同 tick 防双重旅行：逐 tick FOLLOW 检查与 OnPlayerWarped 会在同一 tick
        // 各发起一次 StartTravel，造成重复日志与计时重置。
        private readonly Dictionary<string, int> _lastTravelStartTick = new(StringComparer.OrdinalIgnoreCase);
        private const int BaseTicksPerHop = 120;
        private const int SingleHopTicks = 180;
        // 无路径信息时（动态地图/图外地图）模拟旅行的保守跳数估计
        private const int EstimatedFallbackHops = 2;
        private const int MaxRandomVarianceTicks = 60;

        // D1 修复后 QuickTravelWindowTicks 已移除：玩家快速切图不再触发瞬移，
        // 统一走 CancelTravel + NavigateToTaskLocation 重启模拟旅行。

        // Cooldown to prevent rapid repeated fallback warps
        private const int FallbackWarpCooldownTicks = 600;
        private readonly Dictionary<string, int> _lastFallbackWarpTick = new(StringComparer.OrdinalIgnoreCase);

        // Fix #11: track NPCs currently hidden during travel
        private readonly HashSet<string> _hiddenNpcs = new(StringComparer.OrdinalIgnoreCase);

        // B4: 旅行连续失败熔断 — 同一 NPC 连续失败 2 次后，本游戏日不再自动发起旅行。
        // _consecutiveTravelFailures: npcName -> 连续失败次数（旅行成功时清零）
        // _travelBlockedUntilDay: npcName -> 触发熔断时的 Game1.dayOfMonth（同日不再自动发起）
        private readonly Dictionary<string, int> _consecutiveTravelFailures = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _travelBlockedUntilDay = new(StringComparer.OrdinalIgnoreCase);
        private const int TravelFailureThreshold = 2;

        /// <summary>
        /// The distance threshold above which the NPC begins moving toward the player.
        /// Configurable from ModConfig.FollowDistance.
        /// </summary>
        public float FarDistanceThreshold { get; set; } = 5.0f;

        /// <summary>
        /// 旅行失败回调（npcName → 调用方处理 ForceTransition 等副作用）。
        /// 由 ServiceInitializer 注入：触发 ForceTransition(IDLE, reason="travel_failed")。
        /// AgentNavigator 不引用 AgentService（Abstractions 不依赖 ValleyAgent），
        /// 通过此回调将状态转换委托给上层。
        /// </summary>
        public Action<string>? OnTravelFailed { get; set; }

        /// <summary>
        /// FOLLOW 目标玩家解析回调（2026-08-16 联机适配）。
        /// 由 ServiceInitializer 注入：读 AgentBrain.LastDialoguePlayerId（房客发起对话时已记录）
        /// → Game1.GetPlayer 解析目标 Farmer；缺省/无记录 → 调用方回落 Game1.player（本机）。
        /// 返回 null 表示无可用目标（NPC 原地不动，等待下次有效对话）。
        /// </summary>
        public Func<NPC, Farmer?>? FollowTargetResolver { get; set; }

        /// <summary>解析 FOLLOW 目标玩家（缺省回落本机玩家，与联机适配前的行为一致）。</summary>
        private Farmer? ResolveFollowTarget(NPC npc)
            => FollowTargetResolver?.Invoke(npc) ?? Game1.player;

        public AgentNavigator(LocationGraph locationGraph, IMovementService movementService, IMonitor? monitor)
        {
            _locationGraph = locationGraph;
            _movementService = movementService;
            _monitor = monitor;
        }

        // ─── Public API ──────────────────────────────────────────────────────

        /// <summary>
        /// Main update called every tick when the NPC is in FOLLOW state.
        /// Fix #10: uses direct distance checks.
        /// </summary>
        public void Update(NPC npc, int currentTick)
        {
            var npcName = npc.Name;

            // 节流诊断日志（每 5s）：跟随链路排障——旅行态/本地跟随、同图性、距离
            if (currentTick % 300 == 0)
            {
                var travelInfo = _travelStates.TryGetValue(npcName, out var t) && t.IsTravelling
                    ? $"travel({t.Phase},{t.TargetLocation},elapsed={currentTick - t.TravelStartTick}/{t.TravelDurationTicks})"
                    : "local";
                _monitor?.Log(
                    $"[AgentNavigator] {npcName}: {travelInfo} loc={npc.currentLocation?.Name} playerLoc={Game1.currentLocation?.Name} dist={Vector2.Distance(npc.Tile, Game1.player.Tile):F1} controller={(npc.controller == null ? "null" : npc.controller.GetType().Name)}",
                    LogLevel.Trace);
            }

            // D2：事件/节日开始时，进行中的旅行取消（CancelTravel 已保证位置恢复）。
            // 节日结束后每 tick FOLLOW 会自动重新发起旅行，无需新增挂起状态机。
            if (GameEventGuard.IsEventOrFestivalActive
                && _travelStates.TryGetValue(npcName, out var activeTravel) && activeTravel.IsTravelling)
            {
                _monitor?.Log($"[AgentNavigator] {npcName}: event/festival started — cancelling travel", LogLevel.Debug);
                CancelTravel(npcName);
                return;
            }

            if (_travelStates.TryGetValue(npcName, out var travel) && travel.IsTravelling)
            {
                // 出发阶段：NPC 走向出口
                if (travel.Phase == TravelPhase.Departing)
                {
                    UpdateDeparting(npc, travel, currentTick);
                    return;
                }

                // 快速旅行检查（仅 Travelling 阶段）：玩家改变了目的地。
                // 仅对跟随玩家旅行生效——任务旅行目标固定，不重定向。
                if (travel.FollowsPlayer
                    && npc.currentLocation != Game1.currentLocation
                    && Game1.currentLocation != null)
                {
                    var newTarget = Game1.currentLocation.NameOrUniqueName;
                    if (!string.IsNullOrEmpty(newTarget)
                        && !newTarget.Equals(travel.TargetLocation, StringComparison.OrdinalIgnoreCase))
                    {
                        // D1 修复：玩家去了非目标地图 → 取消当前旅行（CancelTravel 已保证位置恢复），重新模拟旅行。
                        // 不再 warp 到玩家脚下（瞬移出戏）。
                        _monitor?.Log($"[AgentNavigator] {npcName}: player redirected to {newTarget} — restarting travel", LogLevel.Debug);
                        CancelTravel(npcName);
                        var restarted = false;
                        NavigateToTaskLocation(npc, new[] { newTarget }, currentTick, out restarted);
                        if (!restarted)
                        {
                            _monitor?.Log($"[AgentNavigator] {npcName}: cannot route to {newTarget}, staying put", LogLevel.Warn);
                            // F2 修复：连续切图旅行重启失败同样处理 — 转 IDLE + 发 state_changed
                            // CancelTravel 已恢复位置，这里只触发熔断 + 回调 + 通知
                            RecordTravelFailure(npcName);
                            OnTravelFailed?.Invoke(npcName);
                            // E0-4 跨图跟丢 — 灰色系统消息（区别于 AI 主动彩色发言）
                            TravelSpeechHelper.SystemSpeak(npc.Name, "没跟上");
                        }
                        return;
                    }
                    // 玩家就在目标地图：什么都不做，UpdateTravel 到点自然到达
                }

                UpdateTravel(npc, currentTick);
                return;
            }

            if (npc.currentLocation != Game1.currentLocation && Game1.currentLocation != null)
            {
                var targetLocation = Game1.currentLocation.NameOrUniqueName;
                if (!string.IsNullOrEmpty(targetLocation))
                {
                    // D1 修复：不再直接 warp 追赶，统一走模拟旅行。
                    // 动态地图/无路径场景由 StartTravel 内部 fallback warp 处理（带 600 tick 冷却）。
                    var started = false;
                    NavigateToTaskLocation(npc, new[] { targetLocation }, currentTick, out started);
                    if (!started)
                    {
                        _monitor?.Log($"[AgentNavigator] {npc.Name}: cross-map catch-up travel failed to start", LogLevel.Warn);
                    }
                }
                return;
            }

            UpdateLocalFollowing(npc, currentTick);
        }

        /// <summary>
        /// Returns true if the NPC is currently in cross-map travel.
        /// </summary>
        public bool IsTravelling(string npcName) => _travelStates.TryGetValue(npcName, out var travel) && travel.IsTravelling;

        /// <summary>
        /// Cancels any active cross-map travel for the NPC.
        /// </summary>
        public void CancelTravel(string npcName)
        {
            if (_travelStates.TryGetValue(npcName, out var travel))
            {
                _ = _travelStates.Remove(npcName);
                // 若 NPC 正在旅行中（IsInvisible 隐藏），确保恢复可见。
                // 位置无需恢复——HideNpc 不再挪动 Position，NPC 一直留在出发位置。
                if (_hiddenNpcs.Contains(npcName))
                {
                    var npc = Game1.getCharacterFromName(npcName);
                    var departureLoc = Game1.getLocationFromName(travel.DepartureLocation);
                    if (npc != null && departureLoc != null)
                    {
                        try
                        {
                            // 保险兜底：若 NPC 因外部原因被移走，恢复到出发位置
                            Game1.warpCharacter(npc, departureLoc, new Vector2(travel.DepartureNpcTile.X, travel.DepartureNpcTile.Y));
                        }
                        catch (InvalidOperationException ex)
                        {
                            // 出发地图已不可用（极少见），交给 DayStarted 复活逻辑兜底
                            _monitor?.Log($"[AgentNavigator] {npcName}: CancelTravel failed to restore to {travel.DepartureLocation} — location unavailable ({ex})", LogLevel.Warn);
                        }
                    }
                }
            }
            ShowNpc(npcName);
        }

        /// <summary>
        /// B4: 记录旅行失败。连续失败达 <see cref="TravelFailureThreshold"/> 次后，
        /// 设置 _travelBlockedUntilDay 为当前 Game1.dayOfMonth，本游戏日不再自动发起旅行。
        /// 跨季节边界（dayOfMonth 重置）的极端情况下可能多阻塞一天，可接受。
        /// </summary>
        private void RecordTravelFailure(string npcName)
        {
            _consecutiveTravelFailures.TryGetValue(npcName, out var count);
            count++;
            _consecutiveTravelFailures[npcName] = count;
            if (count >= TravelFailureThreshold)
            {
                _travelBlockedUntilDay[npcName] = Game1.dayOfMonth;
                _monitor?.Log($"[AgentNavigator] {npcName}: travel circuit breaker OPENED (consecutive failures={count}, blockedDay={Game1.dayOfMonth})", LogLevel.Warn);
            }
        }

        /// <summary>
        /// B4: 旅行成功时重置连续失败计数。在 UpdateTravel warp 成功后调用。
        /// </summary>
        private void ResetTravelFailures(string npcName)
        {
            if (_consecutiveTravelFailures.ContainsKey(npcName))
            {
                _consecutiveTravelFailures[npcName] = 0;
            }
        }

        /// <summary>
        /// Attempts to navigate the NPC to one of the specified task locations.
        /// Returns the target location name if travel was started.
        /// </summary>
        /// <param name="npc">The NPC to navigate.</param>
        /// <param name="candidateLocations">Candidate target location names; the first reachable one is used.</param>
        /// <param name="currentTick">The current game tick, used for travel timing and cooldowns.</param>
        /// <param name="startedTravel">Receives true if a new travel was started; false otherwise.</param>
        /// <param name="followsPlayer">
        /// true（默认）= 跟随玩家旅行：玩家回到 NPC 当前图时取消旅行，玩家再次切图时重定向。
        /// false = 任务旅行（FARM/MINE/FORAGE）：目标固定为任务地点，不因玩家移动而取消或重定向。
        /// </param>
        public string? NavigateToTaskLocation(NPC npc, string[] candidateLocations, int currentTick, out bool startedTravel, bool followsPlayer = true)
        {
            startedTravel = false;

            // D2 中央守卫：事件/节日期间禁止一切跨图旅行发起。
            // 保护 move_to / FARM / MINE / FORAGE 等所有调用方。
            if (GameEventGuard.IsEventOrFestivalActive)
            {
                _monitor?.Log($"[AgentNavigator] {npc.Name}: NavigateToTaskLocation blocked — event/festival active", LogLevel.Debug);
                return null;
            }

            // B4: 熔断检查 — 本游戏日内连续失败达阈值后不再自动发起旅行。
            // 使用 dayOfMonth 比较：blockedDay >= currentDay 表示同一天内（或跨季节边界，
            // 极端情况下多阻塞一天，可接受）。dayOfMonth 1-28 每季节重置。
            if (_travelBlockedUntilDay.TryGetValue(npc.Name, out var blockedDay) && blockedDay >= Game1.dayOfMonth)
            {
                _monitor?.Log($"[AgentNavigator] {npc.Name}: NavigateToTaskLocation blocked — travel circuit breaker open (failed {TravelFailureThreshold}x, blockedDay={blockedDay})", LogLevel.Debug);
                startedTravel = false;
                return null;
            }

            var currentLoc = npc.currentLocation?.NameOrUniqueName ?? "";

            // 已在旅行中：检查是否需要重定向到新目标
            if (_travelStates.TryGetValue(npc.Name, out var existing) && existing.IsTravelling)
            {
                // 目标在候选列表中 → 旅行已在前往该目标，无需重启
                foreach (var target in candidateLocations)
                {
                    if (string.Equals(existing.TargetLocation, target, StringComparison.OrdinalIgnoreCase))
                    {
                        return existing.TargetLocation;
                    }
                }

                // 目标不在候选列表中 → 取消旧旅行，重定向到新目标
                CancelTravel(npc.Name);
            }

            foreach (var target in candidateLocations)
            {
                if (string.Equals(currentLoc, target, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var path = _locationGraph.FindPath(currentLoc, target);
                if (path != null && path.Hops.Count > 0)
                {
                    StartTravel(npc, target, currentTick, followsPlayer);
                    startedTravel = true;
                    return target;
                }
            }

            // Fallback: 路径查找失败时（如 NPC 在 Mine 等动态地图），直接调用 StartTravel
            // StartTravel 内部有 Game1.warpCharacter fallback 传送机制
            foreach (var target in candidateLocations)
            {
                if (!string.Equals(currentLoc, target, StringComparison.OrdinalIgnoreCase))
                {
                    StartTravel(npc, target, currentTick, followsPlayer);
                    startedTravel = true;
                    return target;
                }
            }

            return null;
        }

        /// <summary>
        /// 推进任务旅行的状态机（供 FARM/MINE/FORAGE 等非 FOLLOW 状态调用）。
        /// 仅推进已存在的旅行，不会发起新旅行，也不会做 FOLLOW 特有的玩家追踪/本地跟随。
        /// 返回 true 表示旅行仍在进行中（调用方应停止 NPC 移动并等待）；
        /// 返回 false 表示无旅行或旅行已结束（调用方应继续任务逻辑）。
        /// </summary>
        public bool ProgressTravel(NPC npc, int currentTick)
        {
            if (npc == null)
            {
                return false;
            }

            var npcName = npc.Name;
            if (!_travelStates.TryGetValue(npcName, out var travel) || !travel.IsTravelling)
            {
                return false;
            }

            // D2：事件/节日开始时取消旅行（与 Update 入口守卫一致）。
            if (GameEventGuard.IsEventOrFestivalActive)
            {
                _monitor?.Log($"[AgentNavigator] {npcName}: event/festival started — cancelling task travel", LogLevel.Debug);
                CancelTravel(npcName);
                return false;
            }

            if (travel.Phase == TravelPhase.Departing)
            {
                UpdateDeparting(npc, travel, currentTick);
            }
            else
            {
                UpdateTravel(npc, currentTick);
            }

            // 旅行是否仍在进行（UpdateDeparting/UpdateTravel 可能已 EndTravel）
            return _travelStates.TryGetValue(npcName, out var updated) && updated.IsTravelling;
        }

        // ─── Local (same-map) following ──────────────────────────────────────

        /// <summary>
        /// Fix #10: follow hysteresis merged directly into this method.
        /// Uses direct distance checks with hysteresis to prevent flicker.
        /// </summary>
        private void UpdateLocalFollowing(NPC npc, int currentTick)
        {
            var npcName = npc.Name;

            var followTarget = ResolveFollowTarget(npc);
            if (followTarget == null)
            {
                return; // 无可用跟随目标（联机：发起玩家已离线且无回落），原地等待
            }

            // 跨图目标守卫（2026-09-10 3 人联机 soak 实证修复）：跟随目标在另一张图时
            // 不参与本地跟随。此前把目标地图坐标当本图坐标寻路，会产生每 tick
            // NoPathFound 重试风暴（大图 × 双 A* 全图搜索）或"幽灵点"站桩（错图坐标恰好可走）。
            // 跨图旅行只按主机位置走（联机已知限制），目标在别的图时原地等待。
            if (followTarget.currentLocation is { } targetLoc
                && npc.currentLocation is { } npcLoc
                && !string.Equals(targetLoc.NameOrUniqueName, npcLoc.NameOrUniqueName, StringComparison.OrdinalIgnoreCase))
            {
                if (_movementService.IsMoving(npcName))
                {
                    _movementService.Stop(npc, "follow-target-cross-map");
                }

                _wantsToFollow[npcName] = false;
                _ = _isMovingFalseSinceTick.Remove(npcName);

                // 与上方 5s 节流诊断同频，避免每 tick 刷屏
                if (!_lastCrossMapFollowLogTick.TryGetValue(npcName, out var lastLog)
                    || currentTick - lastLog >= CrossMapFollowLogCooldownTicks)
                {
                    _lastCrossMapFollowLogTick[npcName] = currentTick;
                    _monitor?.Log(
                        $"[AgentNavigator] {npcName}: follow target '{followTarget.Name}' is in another map " +
                        $"('{targetLoc.NameOrUniqueName}' vs NPC '{npcLoc.NameOrUniqueName}') — standing by " +
                        "(cross-map travel follows host map only)",
                        LogLevel.Debug);
                }

                return;
            }

            var playerTile = followTarget.Tile;
            var distToTarget = Vector2.Distance(npc.Tile, playerTile);

            // ── Zero-distance check ──
            if (distToTarget < 0.5f)
            {
                _ = _isMovingFalseSinceTick.Remove(npcName);
                _wantsToFollow[npcName] = false;
                return;
            }

            // ── Hysteresis: direct distance-based ──
            if (distToTarget < CloseDistanceThreshold)
            {
                _wantsToFollow[npcName] = false;
            }
            else if (distToTarget > FarDistanceThreshold)
            {
                _wantsToFollow[npcName] = true;
            }
            // Dead zone (Close..Far): maintain previous intent — no flicker

            var wantsFollow = _wantsToFollow.TryGetValue(npcName, out var wf) && wf;

            // ── If NPC doesn't want to follow (close enough), stop and face player ──
            if (!wantsFollow)
            {
                if (_movementService.IsMoving(npcName))
                {
                    _movementService.Stop(npc, "follow-close-enough");
                }
                // Issue 4: When close, turn to face the player
                var direction = followTarget.Tile - npc.Tile;
                if (Math.Abs(direction.X) > Math.Abs(direction.Y))
                {
                    npc.FacingDirection = direction.X > 0 ? 1 : 3; // Right : Left
                }
                else
                {
                    npc.FacingDirection = direction.Y > 0 ? 2 : 0; // Down : Up
                }
                _ = _isMovingFalseSinceTick.Remove(npcName);
                return;
            }

            // ── Ensure target tile is walkable ──
            // ignore=npc：目标瓦片必须对 NPC 自身可走（玩家脚下瓦片被玩家碰撞占据，
            // 不带 ignore 会误判可走 → PFC 寻路失败 → 传送兜底）。
            var walkableTarget = FindWalkableTileNear(npc.currentLocation, playerTile, npc);
            var walkablePoint = new Point((int)walkableTarget.X, (int)walkableTarget.Y);

            // ── If no controller exists, create immediately ──
            if (npc.controller is null)
            {
                // NoPathFound 节流：A* 同步无路时 60 tick 内不重试（避免每 tick 刷屏）
                if (_lastNoPathFoundTick.TryGetValue(npcName, out var lastNoPath)
                    && currentTick - lastNoPath < NoPathFoundRetryCooldownTicks)
                {
                    return;
                }

                var moveResult = _movementService.MoveTo(npc, walkablePoint, MovementMode.LongRange, currentTick);
                if (moveResult != MoveResult.Success && moveResult != MoveResult.BlockedByCooldown)
                {
                    if (moveResult == MoveResult.NoPathFound)
                    {
                        _lastNoPathFoundTick[npcName] = currentTick;
                    }
                    _monitor?.Log($"[AgentNavigator] {npcName}: follow MoveTo -> {moveResult} (target {walkablePoint})", LogLevel.Trace);
                }
                _lastFollowTarget[npcName] = walkablePoint;
                _pathFindStartPosition[npcName] = npc.Tile;
                _ = _isMovingFalseSinceTick.Remove(npcName);
                return;
            }

            // ── Target changed: player has moved ──
            // 注意：不能用 pfc.endPoint 检测目标变化。
            // PathFindController 的 4 参数 + bool 构造函数有 bug，不会设置 this.endPoint 字段（始终为 (0,0)）。
            // 改用本地跟踪的 _lastFollowTarget。
            var lastTarget = _lastFollowTarget.TryGetValue(npcName, out var lt) ? lt : Point.Zero;
            if (lastTarget != walkablePoint)
            {
                // 只有玩家明显移动（>=3 格）且距上次改道超过 LongRange 冷却才重建路径：
                // 玩家连续移动时每 tick Stop+重建会让 NPC 原地踏步（“跟随断断续续”），
                // 适度让旧路径走完，到点后再由 controller==null 分支重新寻路追上。
                var endpointMoved = Vector2.Distance(new Vector2(lastTarget.X, lastTarget.Y), walkableTarget) >= 3f;
                var lastRepath = _lastFollowRepathTick.TryGetValue(npcName, out var lr) ? lr : -MovementConstants.EffectiveLongRangeCooldownTicks;
                if (endpointMoved && currentTick - lastRepath >= MovementConstants.EffectiveLongRangeCooldownTicks)
                {
                    _movementService.Stop(npc, "follow-target-changed");
                    _ = _movementService.MoveTo(npc, walkablePoint, MovementMode.LongRange, currentTick);
                    _lastFollowTarget[npcName] = walkablePoint;
                    _lastFollowRepathTick[npcName] = currentTick;
                    _pathFindStartPosition[npcName] = npc.Tile;
                    _ = _isMovingFalseSinceTick.Remove(npcName);
                }
                return;
            }

            // ── Not-moving grace period ──
            if (!npc.isMoving())
            {
                if (!_isMovingFalseSinceTick.TryGetValue(npcName, out var falseSinceTick))
                {
                    _isMovingFalseSinceTick[npcName] = currentTick;
                    falseSinceTick = currentTick;
                }

                var falseDuration = currentTick - falseSinceTick;
                if (falseDuration >= IsMovingFalseGraceTicks)
                {
                    _movementService.Stop(npc, "follow-grace-elapsed");
                    _ = _pathFindStartPosition.Remove(npcName);
                    _ = _isMovingFalseSinceTick.Remove(npcName);
                }
                return;
            }
            else
            {
                _ = _isMovingFalseSinceTick.Remove(npcName);
            }

            // ── Stuck detection with walkable offset ──
            if (_pathFindStartPosition.TryGetValue(npcName, out var startPos))
            {
                var moveState = _movementService.GetMovementState(npcName);
                if (moveState.IsMoving)
                {
                    var elapsed = currentTick - moveState.TickStarted;
                    if (elapsed >= PathfindStuckThresholdTicks)
                    {
                        var distMoved = Vector2.Distance(npc.Tile, startPos);
                        if (distMoved < PathfindStuckDistanceThreshold)
                        {
                            _monitor?.Log($"[AgentNavigator] {npcName}: PathFindController stuck ({elapsed} ticks, moved {distMoved:F1}) — recreating.", LogLevel.Debug);

                            // Fix #9: validate that the offset target is walkable
                            var stuckTarget = FindWalkableOffsetTarget(npc, walkablePoint, currentTick);

                            _movementService.Stop(npc, "follow-stuck");
                            _ = _movementService.MoveTo(npc, stuckTarget, MovementMode.LongRange, currentTick);
                            _pathFindStartPosition[npcName] = npc.Tile;
                        }
                    }
                }
            }
        }

        // ─── Cross-map travel ────────────────────────────────────────────────

        private void StartTravel(NPC npc, string targetLocation, int currentTick, bool followsPlayer = true)
        {
            var fromLocation = npc.currentLocation?.NameOrUniqueName ?? "";
            if (string.IsNullOrEmpty(fromLocation))
            {
                return;
            }

            // 动态地图（如 MineShaft）没有出口 warp，Departing 阶段会永远卡住，直接 fallback warp
            var isDynamicLocation = fromLocation.StartsWith("UndergroundMine", StringComparison.OrdinalIgnoreCase)
                                 || fromLocation.StartsWith("MineShaft", StringComparison.OrdinalIgnoreCase)
                                 || fromLocation.StartsWith("VolcanoDungeon", StringComparison.OrdinalIgnoreCase)
                                 || string.Equals(fromLocation, "Mine", StringComparison.OrdinalIgnoreCase);

            // 同 tick 已发起过旅行 → 跳过重复发起
            if (_lastTravelStartTick.TryGetValue(npc.Name, out var lastStart) && lastStart == currentTick)
            {
                return;
            }

            var path = _locationGraph.FindPath(fromLocation, targetLocation);
            if (path == null || path.Hops.Count == 0 || isDynamicLocation)
            {
                if (_lastFallbackWarpTick.TryGetValue(npc.Name, out var lastWarp) &&
                    currentTick - lastWarp < FallbackWarpCooldownTicks)
                {
                    _monitor?.Log(
                        $"No path from {fromLocation} to {targetLocation} for {npc.Name}. Fallback warp on cooldown.",
                        LogLevel.Warn);
                    return;
                }

                // 无路径/动态地图：不做即时传送（玩家在门口会看到 NPC 凭空出现）。
                // 统一走模拟旅行——NPC 原地消失（隐藏），经过估计的旅行时间后
                // 出现在目标地图入口，再走向玩家（UpdateTravel 到达逻辑）。
                _monitor?.Log(
                    $"No path from {fromLocation} to {targetLocation} for {npc.Name}. Simulating off-map travel.",
                    LogLevel.Debug);

                var targetLoc = Game1.getLocationFromName(targetLocation);
                var fallbackEntry = ResolveEntryTile(targetLoc);
                var fallbackVariance = RandomNumberGenerator.GetInt32(0, MaxRandomVarianceTicks + 1);
                var fallbackDuration = (BaseTicksPerHop * EstimatedFallbackHops) + fallbackVariance;

                var fallbackTravel = new TravelState
                {
                    TargetLocation = targetLocation,
                    EntryTile = new Point((int)fallbackEntry.X, (int)fallbackEntry.Y),
                    TravelStartTick = currentTick,
                    TravelDurationTicks = fallbackDuration,
                    PathHopCount = EstimatedFallbackHops,
                    IsTravelling = true,
                    Phase = TravelPhase.Travelling,
                    DepartureTile = npc.TilePoint,
                    DepartStartTick = currentTick,
                    DepartureLocation = fromLocation,
                    DepartureNpcTile = npc.TilePoint,
                    FollowsPlayer = followsPlayer,
                };

                _lastTravelStartTick[npc.Name] = currentTick;
                _lastFallbackWarpTick[npc.Name] = currentTick;
                _travelStates[npc.Name] = fallbackTravel;
                _movementService.Stop(npc, "travel-fallback");
                // E0-4 删除：跨图旅行广播（fallback 路径），不再污染 chatBox
                HideNpc(npc);
                _monitor?.Log(
                    $"Agent {npc.Name} travelling (simulated): {fromLocation} -> {targetLocation} (~{fallbackDuration / 60f:F1}s)",
                    LogLevel.Debug);
                return;
            }

            var lastHop = path.Hops[^1];
            var entryTile = lastHop.ToTile;

            // Bug C fix: validate entry tile is walkable before using it
            var targetLocationObj = Game1.getLocationFromName(targetLocation);
            if (targetLocationObj != null && !TileWalkability.IsTileWalkable(targetLocationObj, entryTile.X, entryTile.Y))
            {
                var safeTile = FindWalkableTileNear(targetLocationObj, new Vector2(entryTile.X, entryTile.Y));
                entryTile = new Point((int)safeTile.X, (int)safeTile.Y);
                _monitor?.Log($"[AgentNavigator] {npc.Name}: Corrected entry tile from {lastHop.ToTile} to walkable {entryTile}", LogLevel.Debug);
            }

            // entryTile 为零时，优先使用目标地图入口而非玩家位置
            if (entryTile == Point.Zero)
            {
                var resolved = ResolveEntryTile(Game1.getLocationFromName(targetLocation));
                entryTile = new Point((int)resolved.X, (int)resolved.Y);
            }

            var variance = RandomNumberGenerator.GetInt32(0, MaxRandomVarianceTicks + 1);
            var duration = path.HopCount <= 1
                ? SingleHopTicks + (variance / 4)
                : (BaseTicksPerHop * path.HopCount) + variance;

            // 获取出发瓦片（当前地图的出口位置）
            var firstHop = path.Hops[0];
            var departureTile = firstHop.FromTile;

            // Fix (2026-08-02): FromTile 可能直接取 warp 坐标（地图边界外，如 x=-1），
            // PathFindController 无法寻路到界外瓦片，导致 Departing 阶段 3s 无效寻路后靠超时强行走。
            // 修正为出口附近的可走瓦片，让 NPC 真正走向出口再出发。
            if (npc.currentLocation != null
                && !TileWalkability.IsTileWalkable(npc.currentLocation, departureTile.X, departureTile.Y))
            {
                var safeDeparture = FindWalkableTileNear(npc.currentLocation, new Vector2(departureTile.X, departureTile.Y));
                departureTile = new Point((int)safeDeparture.X, (int)safeDeparture.Y);
                _monitor?.Log($"[AgentNavigator] {npc.Name}: Corrected departure tile from {firstHop.FromTile} to walkable {departureTile}", LogLevel.Debug);
            }

            var distToDeparture = Vector2.Distance(npc.Tile, new Vector2(departureTile.X, departureTile.Y));

            var travel = new TravelState
            {
                TargetLocation = targetLocation,
                EntryTile = entryTile,
                TravelStartTick = currentTick,
                TravelDurationTicks = duration,
                PathHopCount = path.HopCount,
                IsTravelling = true,
                DepartureTile = departureTile,
                DepartStartTick = currentTick,
                DepartureLocation = fromLocation,
                DepartureNpcTile = npc.TilePoint,
                DepartureDistanceTiles = distToDeparture,
                FollowsPlayer = followsPlayer,
            };

            _lastTravelStartTick[npc.Name] = currentTick;

            // 已在出口附近 → 跳过出发阶段，直接隐藏旅行
            if (distToDeparture < DepartureReachedDistance)
            {
                travel.Phase = TravelPhase.Travelling;
                _travelStates[npc.Name] = travel;
                _movementService.Stop(npc, "travel-start");
                HideNpc(npc);
                _monitor?.Log(
                    $"Agent {npc.Name} travelling: {fromLocation} -> {targetLocation} " +
                    $"({path.HopCount} hop(s), ~{duration / 60f:F1}s, immediate departure)",
                    LogLevel.Debug);
            }
            // 距出口太远，跳过出发阶段避免大地图长距寻路失败。
            // 注：出发侧 NPC 所在地图玩家通常已离开（玩家看不见），隐藏后出现在
            // 目标地图入口再走向玩家即可，无需让 NPC 在无人地图长距离步行
            // （且 SDV 不驱动非活动地图的 NPC，walk-to-exit 会寻路失败）。
            else if (distToDeparture > DepartureSkipDistance)
            {
                travel.Phase = TravelPhase.Travelling;
                _travelStates[npc.Name] = travel;
                _movementService.Stop(npc, "travel-start");
                // E0-4 删除：跨图旅行出发广播
                HideNpc(npc);
                _monitor?.Log(
                    $"Agent {npc.Name} travelling: {fromLocation} -> {targetLocation} " +
                    $"({path.HopCount} hop(s), ~{duration / 60f:F1}s, skip-departure dist={distToDeparture:F0})",
                    LogLevel.Debug);
            }
            else
            {
                // 走向出口再消失
                travel.Phase = TravelPhase.Departing;
                _travelStates[npc.Name] = travel;
                _movementService.MoveTo(npc, departureTile, MovementMode.LongRange, currentTick);
                // E0-4 删除：跨图旅行出发广播
                _monitor?.Log(
                    $"Agent {npc.Name} departing: {fromLocation} -> {targetLocation} " +
                    $"(walking to exit, then ~{duration / 60f:F1}s travel)",
                    LogLevel.Debug);
            }
        }

        private void UpdateTravel(NPC npc, int currentTick)
        {
            if (!_travelStates.TryGetValue(npc.Name, out var travel) || !travel.IsTravelling)
            {
                return;
            }

            var elapsed = currentTick - travel.TravelStartTick;
            if (elapsed >= travel.TravelDurationTicks)
            {
                // 到达入口（try 块外声明，供后续步进重试使用；try 内更新为修正后值）
                var arrivalEntry = travel.EntryTile;
                try
                {
                    // Bug C fix: validate entry tile walkability before warp
                    var arrivalLoc = Game1.getLocationFromName(travel.TargetLocation);
                    var safeEntry = travel.EntryTile;
                    var entryWalkable = arrivalLoc != null &&
                                        TileWalkability.IsTileWalkable(arrivalLoc, travel.EntryTile.X, travel.EntryTile.Y);
                    if (!entryWalkable && arrivalLoc != null)
                    {
                        var safeTile = FindWalkableTileNear(arrivalLoc, new Vector2(travel.EntryTile.X, travel.EntryTile.Y));
                        safeEntry = new Point((int)safeTile.X, (int)safeTile.Y);
                        _monitor?.Log($"[AgentNavigator] {npc.Name}: Corrected arrival tile from {travel.EntryTile} to walkable {safeEntry}", LogLevel.Debug);
                    }
                    // Fix (2026-08-02): 入口可走但可能被围栏/建筑封闭——如 Farm 东门
                    // 门关着时，从 BusStop 方向到达 (79,15) 后无法寻路进农田（PathFindController
                    // 对关闭的 fence gate 不可通行）→ 到达后寻路失败 → MovementService 传送兜底
                    // （"NPC 传送贴脸"）。沿指向玩家的方向跳过封闭瓦片，落到围栏内侧第一个可走瓦片；
                    // 入口开放（邻居可走）则保持原入口，保证"从入口进入"的观感。
                    if (arrivalLoc != null && Game1.player.currentLocation?.NameOrUniqueName == travel.TargetLocation)
                    {
                        var reachableEntry = ResolveReachableEntry(npc.Name, arrivalLoc, safeEntry, Game1.player.TilePoint, npc);
                        if (reachableEntry != safeEntry)
                        {
                            _monitor?.Log($"[AgentNavigator] {npc.Name}: Entry {safeEntry} enclosed — shifted to reachable {reachableEntry}", LogLevel.Debug);
                            safeEntry = reachableEntry;
                        }
                    }

                    _monitor?.Log(
                        $"[AgentNavigator] {npc.Name}: arriving at {travel.TargetLocation} entry={travel.EntryTile} walkable={entryWalkable} -> {safeEntry} distToPlayer={Vector2.Distance(new Vector2(safeEntry.X, safeEntry.Y), Game1.player.Tile):F1}",
                        LogLevel.Debug);
                    Game1.warpCharacter(npc, travel.TargetLocation, new Vector2(safeEntry.X, safeEntry.Y));
                    _monitor?.Log($"Agent {npc.Name} arrived at {travel.TargetLocation}.", LogLevel.Debug);
                    // B4: 旅行成功，重置连续失败计数（即使之前失败过，到达即清零）
                    ResetTravelFailures(npc.Name);
                    // Fix (2026-08-02): SDV 的 PathFindController 无法通过关闭的 fence gate
                    // （NPC 不能开门），从入口寻路到玩家会 NoPathFound → MovementService 传送兜底
                    // （"传送贴脸"）。Agent NPC 仿玩家行为：到达时打开入口→玩家路径上的围栏门。
                    OpenFenceGatesOnPath(npc.Name, Game1.getLocationFromName(travel.TargetLocation),
                        safeEntry, Game1.player.TilePoint);
                    arrivalEntry = safeEntry;
                }
                catch (InvalidOperationException ex)
                {
                    _monitor?.Log($"Travel warp failed for {npc.Name}: {ex}", LogLevel.Error);
                    // F3 修复：旅行失败回滚到出发位置 + 转 IDLE + 发 state_changed
                    // CancelTravel 内部已 warpCharacter 回 DepartureNpcTile 并 ShowNpc
                    RecordTravelFailure(npc.Name);
                    CancelTravel(npc.Name);
                    OnTravelFailed?.Invoke(npc.Name);
                    // E0-4 删除：跨图旅行熔断广播
                    return; // 不继续执行后续的 ShowNpc/EndTravel/MoveTo（CancelTravel 已恢复位置）
                }

                // Fix #11: show NPC again after travel
                ShowNpc(npc.Name);
                // E0-4 删除：跨图旅行到达广播
                // 用户决策 2026-07-18：到达观感 = 从入口走向玩家，不是传送贴脸。
                // EntryTile 由 LocationGraph 最后一跳推导，方向与来路一致（构造保证）。
                // EndTravel 前先把目标存到局部变量（EndTravel 后 travel 已移除）。
                var targetLoc = travel.TargetLocation;
                EndTravel(npc.Name);
                if (Game1.player.currentLocation?.NameOrUniqueName == targetLoc)
                {
                    // Fix (2026-08-02): 初始化 _lastFollowTarget，否则 UpdateLocalFollowing
                    // 会把 lastTarget 视为 Point.Zero 触发 "target-changed" 分支，立即
                    // Stop + 重建控制器（又因冷却被 BlockedByCooldown），NPC 到达后原地
                    // 卡顿约 30 tick，且可能与寻路失败计数叠加导致误触发传送兜底。
                    var playerTilePoint = Game1.player.TilePoint;
                    // 到达目标必须对 NPC 自身可走：直接 MoveTo 玩家瓦片时，
                    // TileWalkability（character=player，忽略玩家碰撞）与 PFC
                    // （character=npc，玩家碰撞计入）判定不一致 → NoPathFound → 传送兜底。
                    // 取玩家附近对 NPC 可走的瓦片作为跟随目标（NPC 走到玩家旁即可）。
                    var arrivalLocation = Game1.getLocationFromName(targetLoc);
                    var approachTarget = arrivalLocation == null
                        ? new Vector2(playerTilePoint.X, playerTilePoint.Y)
                        : FindWalkableTileNear(arrivalLocation, new Vector2(playerTilePoint.X, playerTilePoint.Y), npc);
                    var approachPoint = new Point((int)approachTarget.X, (int)approachTarget.Y);
                    _lastFollowTarget[npc.Name] = approachPoint;
                    var arrivalResult = _movementService.MoveTo(npc, approachPoint, MovementMode.LongRange, currentTick);

                    // Fix (2026-08-02): 入口瓦片在寻路器 passability 中不可通行（warp 瓦片）
                    // 或被围栏/障碍封闭时 MoveTo 立即 NoPathFound——沿玩家方向步进到可走瓦片
                    // 重试（最多 3 次），保证"从入口走向玩家"而非触发 MovementService 传送兜底。
                    if (arrivalResult == MoveResult.NoPathFound
                        && Game1.getLocationFromName(targetLoc) is { } arrivalLocation2)
                    {
                        var steppedTile = arrivalEntry;
                        for (var attempt = 0; attempt < 3; attempt++)
                        {
                            var next = StepTowardPlayer(arrivalLocation2, steppedTile, playerTilePoint, npc);
                            if (next == steppedTile)
                            {
                                break;
                            }
                            steppedTile = next;
                            npc.setTileLocation(new Vector2(steppedTile.X, steppedTile.Y));
                            _monitor?.Log($"[AgentNavigator] {npc.Name}: entry unreachable from {arrivalEntry} — stepped to {steppedTile}", LogLevel.Debug);
                            var retry = _movementService.MoveTo(npc, approachPoint, MovementMode.LongRange, currentTick);
                            if (retry != MoveResult.NoPathFound)
                            {
                                break;
                            }
                        }
                    }
                }
            }
        }

        private void EndTravel(string npcName) => _ = _travelStates.Remove(npcName);

        // ─── Departing Phase ──────────────────────────────────────────────

        private void UpdateDeparting(NPC npc, TravelState travel, int currentTick)
        {
            var npcName = npc.Name;

            // FOLLOW 特有守卫：玩家回到 NPC 当前图 → 取消旅行恢复跟随。
            // 任务旅行（FARM/MINE/FORAGE）目标固定，不因玩家位置变化而取消。
            if (travel.FollowsPlayer && npc.currentLocation == Game1.currentLocation)
            {
                _monitor?.Log($"[AgentNavigator] {npcName}: Player returned during departure — cancelling travel", LogLevel.Debug);
                EndTravel(npcName);
                return;
            }

            // FOLLOW 特有守卫：玩家再次切换地图 → 重定向到玩家新位置。
            // 任务旅行目标固定为任务地点，不重定向。
            if (travel.FollowsPlayer)
            {
                var newTarget = Game1.currentLocation.NameOrUniqueName;
                if (!string.IsNullOrEmpty(newTarget) && !newTarget.Equals(travel.TargetLocation, StringComparison.OrdinalIgnoreCase))
                {
                    // D1 修复：Departing 阶段 NPC 尚未隐藏，直接 EndTravel + 重启模拟旅行，不瞬移。
                    _monitor?.Log($"[AgentNavigator] {npcName}: player redirected during departure — restarting travel to {newTarget}", LogLevel.Debug);
                    EndTravel(npcName);
                    _movementService.Stop(npc, "travel-redirect");
                    var restarted = false;
                    NavigateToTaskLocation(npc, new[] { newTarget }, currentTick, out restarted);
                    if (!restarted)
                    {
                        _monitor?.Log($"[AgentNavigator] {npcName}: cannot route to {newTarget}, staying put", LogLevel.Warn);
                        // F2 修复：Departing 阶段重启失败同样处理 — 转 IDLE + 发 state_changed
                        // Departing 阶段 NPC 未隐藏（EndTravel 已清状态），无需 CancelTravel 恢复位置
                        RecordTravelFailure(npcName);
                        OnTravelFailed?.Invoke(npcName);
                        // E0-4 跨图跟丢 — 灰色系统消息
                        TravelSpeechHelper.SystemSpeak(npc.Name, "没跟上");
                    }
                    return;
                }
            }

            // 到达出发瓦片
            var dist = Vector2.Distance(npc.Tile, new Vector2(travel.DepartureTile.X, travel.DepartureTile.Y));
            if (dist < DepartureReachedDistance)
            {
                StartActualTravel(npc, travel, currentTick);
                return;
            }

            // 距离感知超时：给足 NPC 走到出口的时间（NPC 速度 ~2 tiles/s，
            // DepartureTicksPerTile=45 → 1.33 tiles/s 保守估计 + 余量），上限 60s。
            // 旧的固定 180 tick（3s）超时在长距离步行时会在半路强制 StartActualTravel，
            // 导致 NPC 中途消失（等同瞬移），违反“像玩家一样走到出口再出发”。
            var departureTimeoutTicks = (int)Math.Min(DepartureTimeoutCapTicks,
                DepartureBaseTimeoutTicks + travel.DepartureDistanceTiles * DepartureTicksPerTile);
            var departElapsed = currentTick - travel.DepartStartTick;
            if (departElapsed > departureTimeoutTicks)
            {
                _monitor?.Log($"[AgentNavigator] {npcName}: Departure timeout ({departElapsed / 60f:F1}s for {travel.DepartureDistanceTiles:F0} tiles) — starting travel", LogLevel.Debug);
                StartActualTravel(npc, travel, currentTick);
                return;
            }

            // 继续走向出口
            if (!_movementService.IsMoving(npcName))
            {
                _movementService.MoveTo(npc, travel.DepartureTile, MovementMode.LongRange, currentTick);
            }
        }

        private void StartActualTravel(NPC npc, TravelState travel, int currentTick)
        {
            _movementService.Stop(npc, "travel-departure-done");
            // E0-4 删除：跨图旅行过渡广播
            HideNpc(npc);
            travel.Phase = TravelPhase.Travelling;
            travel.TravelStartTick = currentTick;
        }

        // ─── NPC Visibility (Fix #11) ──────────────────────────────────────

        /// <summary>
        /// Hides the NPC during cross-map travel by making it invisible.
        /// Prevents the "frozen NPC standing in place" visual.
        ///
        /// Fix (2026-08-02): 用 <see cref="NPC.IsInvisible"/> 隐藏而非把 Position 挪到 (-1000,-1000)。
        /// 负坐标会把 NPC 从 Game1 角色查找中弄丢（getCharacterFromName 返回 null），
        /// 导致 FOLLOW 逐 tick 链路（ProcessAgent → ApplyControllerToNpc → navigator.Update）
        /// 静默中断，旅行永远无法推进——跨地图跟随完全不可用。
        /// IsInvisible 同时隐藏精灵并禁用碰撞（GameLocation.isCollidingPosition 检查 !npc.IsInvisible），
        /// 且 NPC 仍保留在 location.characters 中，getCharacterFromName 可正常找到。
        /// </summary>
        private void HideNpc(NPC npc)
        {
            var npcName = npc.Name;
            _ = _hiddenNpcs.Add(npcName);
            // 通过 MovementService 统一停止，遵守单一所有权契约
            npc.IsInvisible = true;
            _movementService.Stop(npc, "travel-hide");
        }

        /// <summary>
        /// Restores NPC visibility after cross-map travel completes.
        /// The NPC's position is set by Game1.warpCharacter, so no position restore needed.
        /// </summary>
        private void ShowNpc(string npcName)
        {
            _ = _hiddenNpcs.Remove(npcName);
            var npc = Game1.getCharacterFromName(npcName);
            if (npc != null)
            {
                npc.IsInvisible = false;
            }
        }

        /// <summary>
        /// Returns true if the NPC is currently hidden during travel.
        /// </summary>
        public bool IsHidden(string npcName) => _hiddenNpcs.Contains(npcName);

        // ─── Helpers ─────────────────────────────────────────────────────────

        /// <summary>
        /// Fix #9: finds a walkable offset target for stuck recovery.
        /// Generates random offsets and validates walkability before using them.
        /// Falls back to the original walkable target if no valid offset is found.
        /// </summary>
        private static Point FindWalkableOffsetTarget(NPC npc, Point originalTarget, int currentTick)
        {
            // Try up to 5 random offsets within [-3, +3] range
            for (var attempt = 0; attempt < 5; attempt++)
            {
                var offsetX = RandomNumberGenerator.GetInt32(0, 7) - 3;
                var offsetY = RandomNumberGenerator.GetInt32(0, 7) - 3;

                // Skip zero offset
                if (offsetX == 0 && offsetY == 0)
                {
                    continue;
                }

                Point candidate = new(originalTarget.X + offsetX, originalTarget.Y + offsetY);

                // Validate walkability before using this target
                if (IsTileWalkable(npc.currentLocation, new Vector2(candidate.X, candidate.Y)))
                {
                    return candidate;
                }
            }

            // All offsets failed — use original target
            return originalTarget;
        }

        private static bool IsTileWalkable(GameLocation location, Vector2 tile)
            => TileWalkability.IsTileWalkable(location, (int)tile.X, (int)tile.Y);

        /// <summary>
        /// 解析目标地图的入口瓦片：优先地图第一个 warp 出口（玩家进图的位置），
        /// 不可走时找附近可走瓦片；地图无 warp 或地图缺失时退化为玩家附近可走瓦片。
        /// 用于 fallback warp 与 EntryTile 缺失场景，保证 NPC 从入口走进玩家视野。
        /// </summary>
        private static Vector2 ResolveEntryTile(GameLocation? targetLoc)
        {
            if (targetLoc == null)
            {
                return Game1.player.Tile;
            }

            if (targetLoc.warps?.Count > 0)
            {
                var firstWarp = targetLoc.warps[0];
                var tile = new Vector2(firstWarp.X, firstWarp.Y);
                if (!IsTileWalkable(targetLoc, tile))
                {
                    tile = FindWalkableTileNear(targetLoc, tile);
                }
                return tile;
            }

            var nearPlayer = Game1.player.Tile;
            if (!IsTileWalkable(targetLoc, nearPlayer))
            {
                nearPlayer = FindWalkableTileNear(targetLoc, nearPlayer);
            }
            return nearPlayer;
        }

        private static Vector2 FindWalkableTileNear(GameLocation location, Vector2 anchor, Character? ignore = null)
        {
            if (location == null)
            {
                // location 为 null 时返回 (0,0) 而非可能越界的 anchor
                return Vector2.Zero;
            }

            var mapWidth = location.Map.Layers[0].LayerWidth;
            var mapHeight = location.Map.Layers[0].LayerHeight;

            // 必须传 ignore=npc：目标瓦片（玩家脚下）被玩家碰撞占据时，
            // 不带 ignore 会把玩家自身碰撞忽略（character=Game1.player），
            // 与 PathFindController（character=npc，玩家碰撞计入）判定不一致，
            // 导致 PFC 判目标不可走 → NoPathFound → MovementService 传送兜底。
            var result = PathfindingUtility.FindWalkableTileNear(
                new Point((int)anchor.X, (int)anchor.Y),
                (x, y) => TileWalkability.IsTileWalkable(location, x, y, ignore),
                mapWidth,
                mapHeight,
                maxRadius: 10);

            return new Vector2(result.X, result.Y);
        }

        /// <summary>
        ///     修复入口封闭：入口瓦片可走但被围栏/建筑（如关闭的 fence gate）封住时，
        ///     PathFindController 无法从入口寻路到地图内部。沿指向玩家的主轴方向扫描：
        ///     若第一步（邻居）可走 → 入口开放，保持原入口（"从入口进入"观感）；
        ///     否则跳过连续封闭瓦片，落到围栏内侧的第一个可走瓦片。
        ///     ignore 必须传 npc：Farm 等地图的入口区域有 NPCBarrier 瓦片（玩家可走、
        ///     NPC 不可走，SDV 防 NPC 进玩家农场）。玩家视角会把入口误判为开放，
        ///     NPC 落在死胡同（如东门 (79,15) 被 x78 列 NPCBarrier 围死）→ 无路。
        /// </summary>
        private Point ResolveReachableEntry(string npcName, GameLocation location, Point entryTile, Point playerTile, Character? ignore = null)
        {
            if (location == null)
            {
                return entryTile;
            }

            var dx = playerTile.X - entryTile.X;
            var dy = playerTile.Y - entryTile.Y;

            // 玩家就在入口旁（或同侧很近）：无需调整
            if (Math.Abs(dx) < 2 && Math.Abs(dy) < 2)
            {
                return entryTile;
            }

            // 主轴方向：水平偏差大则沿水平方向扫描，否则垂直
            if (Math.Abs(dx) >= Math.Abs(dy))
            {
                var stepX = dx >= 0 ? 1 : -1;
                var neighbor = new Point(entryTile.X + stepX, entryTile.Y);
                if (TileWalkability.IsTileWalkable(location, neighbor.X, neighbor.Y, ignore))
                {
                    return entryTile; // 入口开放
                }
                for (var d = 2; d <= 8; d++)
                {
                    var candidate = new Point(entryTile.X + stepX * d, entryTile.Y);
                    var walkable = TileWalkability.IsTileWalkable(location, candidate.X, candidate.Y, ignore);
                    if (walkable)
                    {
                        return candidate; // 围栏/障碍内侧
                    }
                }
            }
            else
            {
                var stepY = dy >= 0 ? 1 : -1;
                var neighbor = new Point(entryTile.X, entryTile.Y + stepY);
                if (TileWalkability.IsTileWalkable(location, neighbor.X, neighbor.Y, ignore))
                {
                    return entryTile;
                }
                for (var d = 2; d <= 8; d++)
                {
                    var candidate = new Point(entryTile.X, entryTile.Y + stepY * d);
                    var walkable = TileWalkability.IsTileWalkable(location, candidate.X, candidate.Y, ignore);
                    if (walkable)
                    {
                        return candidate;
                    }
                }
            }

            return entryTile;
        }

        /// <summary>
        ///     打开入口→玩家路径上的围栏门（fence gate）。
        ///     SDV 的 PathFindController 把关闭的 fence gate 视为碰撞体（NPC 不能像玩家一样开门），
        ///     导致从入口寻路到玩家 NoPathFound → MovementService 传送兜底（"传送贴脸"根因之一）。
        ///     Agent NPC 仿玩家行为：到达时打开路径上的门（与玩家开门等效，门保持开启）。
        ///     用通用方式匹配门对象（Name == "Gate"）并通过反射设置 gateOpen，
        ///     避免依赖 FenceGate 具体类型（Abstractions 编译时不可见）。
        /// </summary>
        private void OpenFenceGatesOnPath(string npcName, GameLocation? location, Point from, Point to)
        {
            if (location?.objects == null || location.Map?.Layers == null || location.Map.Layers.Count == 0)
            {
                return;
            }

            var minX = Math.Max(0, Math.Min(from.X, to.X) - 3);
            var maxX = Math.Min(location.Map.Layers[0].LayerWidth - 1, Math.Max(from.X, to.X) + 3);
            var minY = Math.Max(0, Math.Min(from.Y, to.Y) - 3);
            var maxY = Math.Min(location.Map.Layers[0].LayerHeight - 1, Math.Max(from.Y, to.Y) + 3);

            foreach (var kvp in location.objects.Pairs)
            {
                var tile = kvp.Key;
                if (tile.X < minX || tile.X > maxX || tile.Y < minY || tile.Y > maxY)
                {
                    continue;
                }

                var obj = kvp.Value;
                if (obj == null || !string.Equals(obj.Name, "Gate", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // gateOpen 是 NetBool 字段：通过反射读 Value 属性并置 true，
                // 避免依赖 NetBool 编译类型（Abstractions 引用的 StardewValley API 不可见）。
                var gateOpenField = obj.GetType().GetField("gateOpen",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                var gateOpenValue = gateOpenField?.GetValue(obj);
                var valueProp = gateOpenValue?.GetType().GetProperty("Value");
                if (valueProp?.GetValue(gateOpenValue) is bool isOpen && !isOpen)
                {
                    valueProp.SetValue(gateOpenValue, true);
                    _monitor?.Log($"[AgentNavigator] {npcName}: opened fence gate at ({tile.X},{tile.Y}) to reach player", LogLevel.Debug);
                }
            }
        }

        /// <summary>
        ///     从 from 沿指向 to 的方向取一步可走瓦片（优先水平，其次垂直，再次对角）。
        ///     用于到达入口不可通行时的步进重试。
        ///     ignore 必须传 npc：Farm 等地图有 NPCBarrier 瓦片（玩家可走、NPC 不可走），
        ///     用玩家视角判定会把 NPC 步进到 NPC 无法通行的瓦片上，导致后续仍无法寻路。
        /// </summary>
        private static Point StepTowardPlayer(GameLocation location, Point from, Point to, Character? ignore = null)
        {
            var dx = Math.Sign(to.X - from.X);
            var dy = Math.Sign(to.Y - from.Y);

            var horizontal = new Point(from.X + dx, from.Y);
            if (horizontal != from && TileWalkability.IsTileWalkable(location, horizontal.X, horizontal.Y, ignore))
            {
                return horizontal;
            }

            var vertical = new Point(from.X, from.Y + dy);
            if (vertical != from && TileWalkability.IsTileWalkable(location, vertical.X, vertical.Y, ignore))
            {
                return vertical;
            }

            var diagonal = new Point(from.X + dx, from.Y + dy);
            if (diagonal != from && TileWalkability.IsTileWalkable(location, diagonal.X, diagonal.Y, ignore))
            {
                return diagonal;
            }

            return from;
        }

        // ─── Travel State ────────────────────────────────────────────────────

        // 出发阶段基础超时（3秒 = 180 tick）
        private const int DepartureBaseTimeoutTicks = 180;
        // 出发阶段每格耗时（tick/格）：45 tick/格 ≈ 1.33 tiles/s（NPC 速度 2 tiles/s + 余量）
        private const int DepartureTicksPerTile = 45;
        // 出发阶段超时上限（60s），防止极端距离下的无限等待
        private const int DepartureTimeoutCapTicks = 3600;
        /// <summary>距出口超过此距离直接跳过出发阶段，快速旅行（仅任务旅行 FARM/MINE/FORAGE）。可通过 ModConfig.DepartureSkipDistance 配置。</summary>
        public float DepartureSkipDistance { get; set; } = 10f;
        private const float DepartureReachedDistance = 1.5f;

        private enum TravelPhase
        {
            Departing,   // NPC 走向当前地图出口
            Travelling,  // NPC 隐藏，等待旅行时间
        }

        private class TravelState
        {
            public string TargetLocation { get; set; } = "";
            public Point EntryTile { get; set; }
            public int TravelStartTick { get; set; }
            public int TravelDurationTicks { get; set; }
            public int PathHopCount { get; set; }
            public bool IsTravelling { get; set; }
            public TravelPhase Phase { get; set; }
            public Point DepartureTile { get; set; }
            public int DepartStartTick { get; set; }
            /// <summary>旅行发起时 NPC 所在地图（NameOrUniqueName），用于 CancelTravel 位置恢复。</summary>
            public string DepartureLocation { get; set; } = "";
            /// <summary>旅行发起时 NPC 所在瓦片，用于 CancelTravel 位置恢复。</summary>
            public Point DepartureNpcTile { get; set; }
            /// <summary>出发阶段 NPC 距出口的距离（格），用于距离感知超时。</summary>
            public float DepartureDistanceTiles { get; set; }
            /// <summary>
            /// true = 跟随玩家旅行（FOLLOW 状态）：玩家回到 NPC 当前图时取消旅行，
            /// 玩家再次切图时重定向到玩家新位置。
            /// false = 任务旅行（FARM/MINE/FORAGE 状态）：目标固定为任务地点，
            /// 不因玩家移动而取消或重定向。
            /// </summary>
            public bool FollowsPlayer { get; set; } = true;
        }
    }
}
