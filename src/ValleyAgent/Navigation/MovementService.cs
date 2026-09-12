using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Pathfinding;
using ValleyAgent.Infrastructure;
using ValleyAgent.Utils;

namespace ValleyAgent.Navigation;

/// <summary>
///     Single-owner NPC movement service. Manages all PathFindController lifecycle,
///     cooldowns, stuck detection, and freeze/unfreeze.
///     This is THE only component that directly touches npc.controller.
///     Fix #1: Removed validation PathFindController creation — no more side effects.
///     Fix #2: Increased path failure detection window from 2 to 5 ticks.
///     Fix #3: Reduced AlreadyMoving grace period from 30 to 15 ticks, integrated with stuck detection.
///     Fix #4: Removed VerifyTilePathfindable (position-tampering validation).
///     Fix #5: Uses PathfindingUtility.FindWalkableTileNear (nearest-first spiral).
/// </summary>
public class MovementService : IMovementService
{
    private const int MaxFailedTilesBeforeGiveUp = 3;
    private const int FailedTilesCleanupIntervalTicks = 300; // 每5秒清理一次（60fps下300tick）

    private const int MaxSameTargetRetries = 2;

    // SDV 的 PathFindController.findPath 用 byte 存储 g（起点到节点步数），
    // 超过 255 溢出（decompiled PathFindController.cs:205 (byte)(pathNode.g+1)）。
    // Farm 等大地图迷宫（NPCBarrier/建筑/随机 debris）中，>40 格路径的 A* 探索
    // 深度易超 255 → 启发式错乱 → openList 耗尽 → findPath 返回 null。
    // 长距离目标分段为 ≤MaxSegmentDistance 格的中间点，保证每段 A* 深度可控。
    private const float MaxSegmentDistance = 10f;

    // 找不到可走瓦片的 NPC 的重复检查冷却（10 分钟 = 36000 tick）
    private const int InvalidTileUnfixableCooldownTicks = 36000;
    private const int MaxConsecutiveNoPathFound = 5; // 连续5次NoPathFound后触发熔断

    // Fix #2: increased from 2 to 5 ticks — SDV's PathFindController may take 3-5 ticks
    // to destroy itself for medium-distance unreachable targets
    private const int PathDestroyedQuicklyTicks = 5;

    // Fix #3: reduced from 30 to 15 ticks — closes the 90-tick gap between
    // AlreadyMoving grace period and HandleStuck activation
    private const int StaleControllerGraceTicks = 15;

    // 熔断器：连续NoPathFound计数，防止同一目标反复失败导致死循环
    private readonly Dictionary<string, int> _consecutiveNoPathCount = new(StringComparer.OrdinalIgnoreCase);

    // Bug B fix: track specific tiles that have failed — prevents infinite retry loops
    private readonly Dictionary<string, HashSet<Point>> _failedTargetTiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _frozenNpcs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _invalidTileUnfixableSinceTick = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _lastFailedTileCleanupTick = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _lastImmediateCreation = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _lastInvalidTileWarningTick = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _lastLongRangeCreation = new(StringComparer.OrdinalIgnoreCase);

    // ─── Per-NPC State ──────────────────────────────────────────────────
    private readonly Dictionary<string, int> _lastShortRangeCreation = new(StringComparer.OrdinalIgnoreCase);

    // ─── Path failure tracking ──────────────────────────────────────────
    private readonly Dictionary<string, Vector2> _lastTarget = new(StringComparer.OrdinalIgnoreCase);
    private readonly IMonitor? _monitor;
    private readonly Dictionary<string, MovementState> _movementStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _stuckStartTick = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _targetFailCount = new(StringComparer.OrdinalIgnoreCase);

    public MovementService(IMonitor? monitor = null)
    {
        _monitor = monitor;
    }

    // 加速感知的有效值：除以 GameSpeedMultiplier，保持游戏时间节奏一致
    private static int EffectiveStaleControllerGraceTicks
    {
        get => Math.Max(3, StaleControllerGraceTicks / DebugFlags.GameSpeedMultiplier);
    }

    private static int EffectivePathDestroyedQuicklyTicks
    {
        get => Math.Max(1, PathDestroyedQuicklyTicks / DebugFlags.GameSpeedMultiplier);
    }

    private static int EffectiveFailedTilesCleanupIntervalTicks
    {
        get => Math.Max(60, FailedTilesCleanupIntervalTicks / DebugFlags.GameSpeedMultiplier);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Public API
    // ═══════════════════════════════════════════════════════════════════════

    public MoveResult MoveTo(NPC npc, Point targetTile, MovementMode mode, int currentTick) =>
        MoveTo(npc, new Vector2(targetTile.X, targetTile.Y), mode, currentTick);

    public MoveResult Follow(NPC npc, Farmer target) => MoveResult.InvalidNpc;

    public MoveResult MoveTo(NPC npc, Vector2 targetTile, MovementMode mode, int currentTick)
    {
        // U1 终验判据是主线程栈出现在 A*/MovementService——该计时给出亚看门狗阈值（<5s）的
        // 寻路负载证据：单次 MoveTo >500ms 即便不触发冻结 dump，也在 SMAPI 日志/ModErrorLog 留痕。
        // Point 重载委托到本包装，自动被覆盖。
        var npcName = npc.Name;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var result = MoveToCore(npc, targetTile, mode, currentTick);
        var elapsedMs = stopwatch.Elapsed.TotalMilliseconds;
        if (elapsedMs > 500 && QueueTelemetry.ShouldWarn($"moveto-slow:{npcName}", 10000))
        {
            _monitor?.Log(
                $"[Movement] {npcName}: MoveTo took {elapsedMs:F0}ms (mode={mode} result={result}) — pathfinding load",
                LogLevel.Warn);
        }

        return result;
    }

    private MoveResult MoveToCore(NPC npc, Vector2 targetTile, MovementMode mode, int currentTick)
    {
        var npcName = npc.Name;

        // ── Validation ──
        if (npc.currentLocation == null)
        {
            return MoveResult.InvalidNpc;
        }

        if (_frozenNpcs.Contains(npcName))
        {
            return MoveResult.Frozen;
        }

        // ── Skip if target is the same tile as NPC ──
        if (Vector2.Distance(npc.Tile, targetTile) < 0.5f)
        {
            return MoveResult.AlreadyAtTarget;
        }

        // ── 目标变更检测：若新目标远离旧失败目标，清除失败缓存 ──
        if (_lastTarget.TryGetValue(npcName, out var prevTarget)
            && Vector2.Distance(prevTarget, targetTile) > 3f)
        {
            _targetFailCount.Remove(npcName);
            _failedTargetTiles.Remove(npcName);
        }

        // ── Validate NPC current position (only on first pathfinding attempt) ──
        // Throttled: skip if we warned within the last 60 ticks；
        // 找不到可走瓦片的 NPC 记入 unfixable 名单，10 分钟内不再重复检查（防日志刷屏）
        if (npc.controller == null)
        {
            var lastWarn = _lastInvalidTileWarningTick.TryGetValue(npcName, out var lt) ? lt : 0;
            var unfixable = _invalidTileUnfixableSinceTick.TryGetValue(npcName, out var ut)
                            && currentTick - ut < InvalidTileUnfixableCooldownTicks;
            if (!unfixable && currentTick - lastWarn >= 60)
            {
                // 传 npc 作为碰撞忽略对象：否则自己的碰撞箱会把所站瓦片误判为不可走
                var isWalkable =
                    TileWalkability.IsTileWalkable(npc.currentLocation, (int)npc.Tile.X, (int)npc.Tile.Y, npc);

                if (!isWalkable)
                {
                    _lastInvalidTileWarningTick[npcName] = currentTick;
                    LogDebug(npcName, $"NPC on invalid tile {npc.Tile} — searching for safe tile");
                    // Fix #5: uses nearest-first spiral search
                    var safeTile = FindWalkableTileNearValidated(npc.currentLocation, npc.Tile, 15, npc);
                    if (safeTile != npc.Tile)
                    {
                        if (IsNpcOnScreen(npc))
                        {
                            // 视野内禁止肉眼可见的传送“纠错”：记入防抖名单，交给正常寻路
                            LogDebug(npcName,
                                $"NPC on unwalkable tile {npc.Tile} — on-screen, skipping teleport correction");
                            _invalidTileUnfixableSinceTick[npcName] = currentTick;
                        }
                        else
                        {
                            LogDebug(npcName, $"NPC on unwalkable tile {npc.Tile} — teleporting to {safeTile}");
                            npc.setTileLocation(safeTile);
                        }
                    }
                    else
                    {
                        LogDebug(npcName,
                            $"WARNING: Could not find walkable tile near {npc.Tile} — suppressing further checks for 10min");
                        _invalidTileUnfixableSinceTick[npcName] = currentTick;
                    }
                }
            }
        }

        // ── Get cooldown dict ──
        var cooldownDict = GetCooldownDict(mode);

        // ── Already moving check ──
        // Fix #3: reduced grace period from 30 to 15 ticks
        if (npc.controller is not null)
        {
            var isMoving = npc.isMoving();
            var age = cooldownDict.TryGetValue(npcName, out var lastCreate)
                ? currentTick - lastCreate
                : int.MaxValue;

            if (isMoving || age <= EffectiveStaleControllerGraceTicks)
            {
                return MoveResult.AlreadyMoving;
            }

            // Not moving and older than grace period — clear stale controller
            npc.controller = null;
        }

        // ── Cooldown check ──
        var cooldownTicks = mode switch
        {
            MovementMode.ShortRange => MovementConstants.EffectiveShortRangeCooldownTicks,
            MovementMode.LongRange => MovementConstants.EffectiveLongRangeCooldownTicks,
            MovementMode.Immediate => MovementConstants.ImmediateCooldownTicks,
            _ => MovementConstants.EffectiveShortRangeCooldownTicks
        };

        if (cooldownTicks > 0 && cooldownDict.TryGetValue(npcName, out var lastCreated)
                              && currentTick - lastCreated < cooldownTicks)
        {
            return MoveResult.BlockedByCooldown;
        }

        // ── Path failure detection ──
        // Fix #2: wider detection window catches medium-distance unreachable targets
        if (cooldownDict.TryGetValue(npcName, out var prevCreation)
            && currentTick - prevCreation < EffectivePathDestroyedQuicklyTicks + cooldownTicks)
        {
            if (_lastTarget.TryGetValue(npcName, out var lastTgt)
                && Vector2.Distance(lastTgt, targetTile) < 0.5f)
            {
                _targetFailCount[npcName] = _targetFailCount.TryGetValue(npcName, out var fc) ? fc + 1 : 1;

                if (_targetFailCount[npcName] >= MaxSameTargetRetries)
                {
                    // Record this specific tile as failed for Bug B fix
                    if (!_failedTargetTiles.TryGetValue(npcName, out var failedSet))
                    {
                        failedSet = new HashSet<Point>();
                        _failedTargetTiles[npcName] = failedSet;
                    }

                    _ = failedSet.Add(new Point((int)targetTile.X, (int)targetTile.Y));

                    // Periodic cleanup to prevent unbounded growth
                    var lastCleanup = _lastFailedTileCleanupTick.TryGetValue(npcName, out var lc) ? lc : 0;
                    if (currentTick - lastCleanup >= EffectiveFailedTilesCleanupIntervalTicks)
                    {
                        failedSet.Clear();
                        _lastFailedTileCleanupTick[npcName] = currentTick;
                    }

                    // Give up if too many distinct tiles have failed
                    if (failedSet.Count >= MaxFailedTilesBeforeGiveUp)
                    {
                        if (IsNpcOnScreen(npc))
                        {
                            // 视野内禁止肉眼可见的瞬移：放弃本次移动，让上层换目标/重试
                            LogDebug(npcName,
                                $"Too many distinct target tiles failed ({failedSet.Count}) — on-screen, giving up instead of teleporting");
                            _targetFailCount[npcName] = 0;
                            failedSet.Clear();
                            IncrementNoPathCount(npcName);
                            return MoveResult.NoPathFound;
                        }

                        LogDebug(npcName,
                            $"Too many distinct target tiles failed ({failedSet.Count}) — teleporting near target");
                        var teleportTile = FindWalkableTileNearValidated(npc.currentLocation, npc.Tile, 15, npc);
                        npc.setTileLocation(teleportTile);
                        _targetFailCount[npcName] = 0;
                        failedSet.Clear();
                        return MoveResult.Success;
                    }

                    var distance = Vector2.Distance(npc.Tile, targetTile);
                    if (distance > 10f)
                    {
                        if (IsNpcOnScreen(npc))
                        {
                            LogDebug(npcName,
                                $"Long-distance path to {targetTile} failed repeatedly (dist {distance:F1}) — on-screen, giving up instead of teleporting");
                            _targetFailCount[npcName] = 0;
                            IncrementNoPathCount(npcName);
                            return MoveResult.NoPathFound;
                        }

                        var teleportTile = FindWalkableTileNearValidated(npc.currentLocation, targetTile, 10, npc);
                        LogDebug(npcName,
                            $"Long-distance path to {targetTile} failed repeatedly (dist {distance:F1}, from {npc.Tile}) — teleporting near target to {teleportTile}");
                        npc.setTileLocation(teleportTile);
                        _targetFailCount[npcName] = 0;
                    }
                    else
                    {
                        var altTile = PathfindingUtility.FindAlternativeTile(
                            new Point((int)targetTile.X, (int)targetTile.Y),
                            (x, y) => IsSdvTileWalkable(npc.currentLocation, x, y),
                            npc.currentLocation.Map?.Layers[0]?.LayerWidth ?? 0,
                            npc.currentLocation.Map?.Layers[0]?.LayerHeight ?? 0);
                        if (altTile.X != (int)targetTile.X || altTile.Y != (int)targetTile.Y)
                        {
                            // 同时记录原目标和替代坐标到失败集合，防止反复重试同一邻居
                            _ = failedSet.Add(altTile);
                            LogDebug(npcName,
                                $"Path to {targetTile} failed {_targetFailCount[npcName]}x — retrying neighbor {altTile}");
                            targetTile = new Vector2(altTile.X, altTile.Y);
                            // Note: fail count intentionally NOT reset here — we want to count toward the limit
                        }
                        else
                        {
                            // 没有找到替代坐标，原目标也已记录失败→返回无路径
                            LogDebug(npcName, $"No alternative tile found near {targetTile} — reporting NoPathFound");
                            IncrementNoPathCount(npcName);
                            return MoveResult.NoPathFound;
                        }
                    }
                }
            }
            else
            {
                _targetFailCount[npcName] = 0;
            }
        }

        // ── 长距离分段寻路（Fix 2026-08-02）──
        // SDV A* 的 g 是 byte，探索深度 >255 溢出导致寻路失败（Farm 迷宫实测：
        // 50 格路径起点/终点都可走但 findPath 返回 null）。距离 > MaxSegmentDistance
        // 时把目标改为连线上的中途点，PFC 完成后上层（UpdateLocalFollowing/
        // UpdateTravel）下次 MoveTo 自然续走下一段，直至接近玩家。
        var distToTarget = Vector2.Distance(npc.Tile, targetTile);
        if (distToTarget > MaxSegmentDistance)
        {
            var dir = targetTile - npc.Tile;
            dir.Normalize();
            var mid = npc.Tile + dir * MaxSegmentDistance;
            var midPoint = new Point((int)Math.Round(mid.X), (int)Math.Round(mid.Y));
            if (!TileWalkability.IsTileWalkable(npc.currentLocation, midPoint.X, midPoint.Y, npc))
            {
                var safeMid =
                    FindWalkableTileNearValidated(npc.currentLocation, new Vector2(midPoint.X, midPoint.Y), 6, npc);
                midPoint = new Point((int)safeMid.X, (int)safeMid.Y);
            }

            targetTile = new Vector2(midPoint.X, midPoint.Y);
        }

        _lastTarget[npcName] = targetTile;

        // ── 熔断器检查：连续NoPathFound达到阈值时清除失败缓存，允许重新尝试 ──
        if (_consecutiveNoPathCount.TryGetValue(npcName, out var consecCount) &&
            consecCount >= MaxConsecutiveNoPathFound)
        {
            LogDebug(npcName, $"连续NoPathFound达{consecCount}次 — 触发熔断，清除失败缓存重试");
            _failedTargetTiles.Remove(npcName);
            _consecutiveNoPathCount[npcName] = 0;
        }

        // ── Bug B fix: skip tiles that have already failed ──
        var targetPoint = new Point((int)targetTile.X, (int)targetTile.Y);
        if (_failedTargetTiles.TryGetValue(npcName, out var failedTiles) && failedTiles.Contains(targetPoint))
        {
            LogDebug(npcName, $"Target {targetPoint} already failed previously — skipping path attempt");
            // Try a nearby alternative instead
            var altTarget = PathfindingUtility.FindAlternativeTile(
                targetPoint,
                (x, y) => IsSdvTileWalkable(npc.currentLocation, x, y),
                npc.currentLocation.Map?.Layers[0]?.LayerWidth ?? 0,
                npc.currentLocation.Map?.Layers[0]?.LayerHeight ?? 0);

            // 如果替代坐标也已经在失败集合中，直接放弃
            if (failedTiles.Contains(altTarget))
            {
                LogDebug(npcName, $"Alternative tile {altTarget} also failed before — giving up");
                IncrementNoPathCount(npcName);
                return MoveResult.NoPathFound;
            }

            if (altTarget.X != targetPoint.X || altTarget.Y != targetPoint.Y)
            {
                targetTile = new Vector2(altTarget.X, altTarget.Y);
                targetPoint = altTarget;
                LogDebug(npcName, $"Retrying with alternative tile {altTarget}");
            }
            else
            {
                // All alternatives exhausted — give up on this target
                LogDebug(npcName, $"All alternatives exhausted for {targetPoint} — giving up");
                IncrementNoPathCount(npcName);
                return MoveResult.NoPathFound;
            }
        }

        // ── Create PathFindController ──
        // SDV's PathFindController handles unwalkable targets internally by
        // pathing to adjacent walkable tiles. If the path is truly unreachable,
        // it will self-destruct and the failure detection above will catch it.
        npc.controller = null;

        // E2-1 动态速度：速度按到最终目标的距离分段（默认 >8 格 2×、3–8 格 1.5×、<3 格 1×）。
        // distToTarget 在分段寻路（MaxSegmentDistance 中途点）之前计算，
        // 反映到最终目标的真实距离而非中途点距离——跟随场景即"到玩家的距离"。
        // 走行动画帧间隔随倍率等比缩短（interval = 100ms / 倍率），
        // 让脚步动画与加速同步，避免"滑步"观感。无体力惩罚。
        npc.Speed = MovementConstants.GetEffectiveDynamicSpeed(distToTarget);
        if (npc.Sprite != null)
        {
            npc.Sprite.interval = MovementConstants.GetEffectiveWalkAnimationIntervalMs(distToTarget);
        }

        // Fix (2026-08-02): PathFindController 的 (Character, GameLocation, Point, int[, bool])
        // 构造函数有 bug——不设置 this.endPoint（恒为 Point.Zero=(0,0)），控制器实际寻路到
        // (0,0)，长距离目标必然 NoPathFound → 快速自毁 → MovementService 失败计数 → 传送兜底
        // （"跟随=传送"根因，已用 endPoint 校验日志实锤）。
        // 改用带 endBehaviorFunction 的 Action 重载（该重载正确初始化 endPoint），
        // 并校验 endPoint 被正确设置。
        //
        // Fix (2026-08-02): 5 参构造的 A* limit 默认 10000。Farm 等大地图（80x65）+ 多建筑
        // + 随机 debris 时，长距离路径（>50 格）的 A* 搜索空间可超过 10000 节点，
        // findPath 返回 null → 误判 NoPathFound → 传送兜底（"传送贴脸"）。
        // 6 参构造显式提高 limit 至 50000。
        npc.controller = new PathFindController(npc, npc.currentLocation, targetPoint, 2,
            null!, 50000);

        if (npc.controller?.endPoint != targetPoint)
        {
            _monitor?.Log(
                $"[MovementService] {npcName}: PFC endPoint mismatch! target={targetPoint} pfc.endPoint={npc.controller?.endPoint}",
                LogLevel.Warn);
        }

        // Fix (2026-08-02): A* 同步无路检测。SDV 的 findPath 在 PFC 构造时同步执行
        // （decompiled PathFindController.cs:139 pathToEndPoint = findPath(...)），
        // pathToEndPoint==null 即 findPath 未找到路径。实测发现 SDV 的 A* 存在实现缺陷：
        // 在部分路径（起点/终点均可走、BFS 连通性验证可达）上返回 null（closedList 入队
        // 时机 + byte g 等限制）。此时用自定义 A*（标准实现，int g，dequeue 时 closed）
        // 生成路径，喂给 PFC 的 Stack 构造执行，绕开 SDV A* 缺陷。
        if (npc.controller is { } createdPfc && createdPfc.pathToEndPoint == null)
        {
            var fallbackPath = CustomAStar(npc.currentLocation, npc.TilePoint, targetPoint, npc);
            if (fallbackPath != null)
            {
                npc.controller = new PathFindController(fallbackPath, npc.currentLocation, npc, targetPoint);
                GetCooldownDict(mode)[npcName] = currentTick;
                _ = _stuckStartTick.Remove(npcName);
                _ = _consecutiveNoPathCount.Remove(npcName);
                _movementStates[npcName] = new MovementState
                {
                    IsMoving = true,
                    IsFrozen = false,
                    CurrentMode = mode,
                    TargetTile = targetTile,
                    TickStarted = currentTick,
                    StuckStartTick = null
                };
                return MoveResult.Success;
            }

            npc.controller = null;
            _ = _targetFailCount.Remove(npcName);
            _ = _lastTarget.Remove(npcName);
            return MoveResult.NoPathFound;
        }

        GetCooldownDict(mode)[npcName] = currentTick;
        _ = _stuckStartTick.Remove(npcName);
        // 寻路成功，重置连续NoPathFound计数
        _ = _consecutiveNoPathCount.Remove(npcName);
        _movementStates[npcName] = new MovementState
        {
            IsMoving = true,
            IsFrozen = false,
            CurrentMode = mode,
            TargetTile = targetTile,
            TickStarted = currentTick,
            StuckStartTick = null
        };

        return MoveResult.Success;
    }

    public void Stop(NPC npc, string reason)
    {
        var npcName = npc.Name;
        if (npc.controller != null)
        {
            npc.controller = null;
        }

        if (npc.isMoving())
        {
            npc.Halt();
        }

        // E2-1 动态速度：停止时恢复基础走行动画帧间隔，避免 NPC 交还原版/空闲时
        // 残留加速态的缩放间隔（原版速度 2 不缩放动画，残留间隔会使其走得过快）。
        if (npc.Sprite != null)
        {
            npc.Sprite.interval = MovementConstants.BaseWalkAnimationIntervalMs;
        }

        _ = _stuckStartTick.Remove(npcName);
        _ = _movementStates.Remove(npcName);
        _ = _targetFailCount.Remove(npcName);
        // Bug B fix: clear failed tiles so they can retry on next MoveTo
        _ = _failedTargetTiles.Remove(npcName);
        // 停止时清除无效坐标警告节流，避免下次MoveTo被旧节流跳过
        _ = _lastInvalidTileWarningTick.Remove(npcName);
        // 停止时重置连续NoPathFound计数
        _ = _consecutiveNoPathCount.Remove(npcName);
    }

    public void Freeze(NPC npc)
    {
        var npcName = npc.Name;
        _ = _frozenNpcs.Add(npcName);

        var anchor = npc.Tile;

        npc.controller = null;
        npc.Speed = MovementConstants.FrozenSpeed;
        npc.Halt();
        npc.Position = anchor * 64f;
        npc.followSchedule = false;
        npc.ignoreScheduleToday = true;

        _ = _stuckStartTick.Remove(npcName);
        _movementStates[npcName] = new MovementState
        {
            IsMoving = false,
            IsFrozen = true,
            CurrentMode = MovementMode.ShortRange,
            TargetTile = anchor,
            TickStarted = 0,
            StuckStartTick = null
        };
    }

    public void Unfreeze(NPC npc)
    {
        _ = _frozenNpcs.Remove(npc.Name);
        _ = _movementStates.Remove(npc.Name);
    }

    public bool IsMoving(string npcName) =>
        _movementStates.TryGetValue(npcName, out var state) && state.IsMoving && !state.IsFrozen;

    public bool IsFrozen(string npcName) => _frozenNpcs.Contains(npcName);

    public bool CanMove(string npcName) => !_frozenNpcs.Contains(npcName);

    public bool HandleStuck(NPC npc, Vector2 targetTile, int currentTick)
    {
        var npcName = npc.Name;

        var dist = Vector2.Distance(npc.Tile, targetTile);
        if (dist > MovementConstants.StuckDistanceThreshold)
        {
            _ = _stuckStartTick.Remove(npcName);
            return false;
        }

        if (!_stuckStartTick.TryGetValue(npcName, out var stuckStart))
        {
            _stuckStartTick[npcName] = currentTick;
            return false;
        }

        if (currentTick - stuckStart > MovementConstants.EffectiveStuckTimeThresholdTicks)
        {
            LogDebug(npcName, $"NPC stuck near {targetTile} — attempting recovery");
            _ = _stuckStartTick.Remove(npcName);

            npc.controller = null;

            if (IsNpcOnScreen(npc))
            {
                // 视野内禁止肉眼可见的瞬移：只清 controller，让上层重新寻路
                LogDebug(npcName, "On-screen — skipping teleport recovery, will re-path instead");
                if (_movementStates.TryGetValue(npcName, out var osState))
                {
                    _movementStates[npcName] = osState with { IsMoving = false };
                }

                return true;
            }

            var currentTile = npc.Tile;

            // Fix #5: uses nearest-first search via PathfindingUtility
            var betterTile = PathfindingUtility.FindWalkableTileCloserToTarget(
                new Point((int)currentTile.X, (int)currentTile.Y),
                new Point((int)targetTile.X, (int)targetTile.Y),
                (x, y) => IsSdvTileWalkable(npc.currentLocation, x, y),
                npc.currentLocation.Map?.Layers[0]?.LayerWidth ?? 0,
                npc.currentLocation.Map?.Layers[0]?.LayerHeight ?? 0);

            if (betterTile.HasValue)
            {
                npc.setTileLocation(new Vector2(betterTile.Value.X, betterTile.Value.Y));
                // Issue 3: Stuck recovery feedback — brief emote
                npc.doEmote(16); // sweat drop emote
                LogDebug(npcName, $"Teleported to {betterTile.Value} (closer to target)");
            }
            else
            {
                // Fallback: diagonal-capable step toward target
                var step = PathfindingUtility.GetStepToward(
                    new Point((int)currentTile.X, (int)currentTile.Y),
                    new Point((int)targetTile.X, (int)targetTile.Y));

                // GetStepToward may return diagonal — apply both components
                var stepTile = new Point(
                    (int)currentTile.X + step.X,
                    (int)currentTile.Y + step.Y);

                if (IsSdvTileWalkable(npc.currentLocation, stepTile.X, stepTile.Y))
                {
                    npc.setTileLocation(new Vector2(stepTile.X, stepTile.Y));
                    LogDebug(npcName, $"Stepped toward target to {stepTile}");
                }
                else
                {
                    // Try cardinal-only step if diagonal failed
                    if (step.X != 0 && step.Y != 0)
                    {
                        var cardinalStep = new Point((int)currentTile.X + step.X, (int)currentTile.Y);
                        if (IsSdvTileWalkable(npc.currentLocation, cardinalStep.X, cardinalStep.Y))
                        {
                            npc.setTileLocation(new Vector2(cardinalStep.X, cardinalStep.Y));
                            LogDebug(npcName, $"Cardinal step toward target to {cardinalStep}");
                        }
                        else
                        {
                            cardinalStep = new Point((int)currentTile.X, (int)currentTile.Y + step.Y);
                            if (IsSdvTileWalkable(npc.currentLocation, cardinalStep.X, cardinalStep.Y))
                            {
                                npc.setTileLocation(new Vector2(cardinalStep.X, cardinalStep.Y));
                                LogDebug(npcName, $"Cardinal step toward target to {cardinalStep}");
                            }
                            else
                            {
                                DoLastResortTeleport(npc, targetTile);
                            }
                        }
                    }
                    else
                    {
                        DoLastResortTeleport(npc, targetTile);
                    }
                }
            }

            if (_movementStates.TryGetValue(npcName, out var state))
            {
                _movementStates[npcName] = state with { IsMoving = false };
            }

            return true;
        }

        return false;
    }

    public MovementState GetMovementState(string npcName)
    {
        return _movementStates.TryGetValue(npcName, out var state)
            ? state
            : new MovementState
            {
                IsMoving = false,
                IsFrozen = _frozenNpcs.Contains(npcName),
                CurrentMode = MovementMode.ShortRange,
                TargetTile = Vector2.Zero,
                TickStarted = 0,
                StuckStartTick = null
            };
    }

    public void RemoveNpc(string npcName)
    {
        _ = _lastShortRangeCreation.Remove(npcName);
        _ = _lastLongRangeCreation.Remove(npcName);
        _ = _lastImmediateCreation.Remove(npcName);
        _ = _stuckStartTick.Remove(npcName);
        _ = _movementStates.Remove(npcName);
        _ = _frozenNpcs.Remove(npcName);
        _ = _lastTarget.Remove(npcName);
        _ = _targetFailCount.Remove(npcName);
        // Bug B fix: clear failed tiles tracking
        _ = _failedTargetTiles.Remove(npcName);
        _ = _lastFailedTileCleanupTick.Remove(npcName);
        _ = _lastInvalidTileWarningTick.Remove(npcName);
        _ = _consecutiveNoPathCount.Remove(npcName);
        _ = _invalidTileUnfixableSinceTick.Remove(npcName);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Helpers
    // ═══════════════════════════════════════════════════════════════════════

    private static bool IsSdvTileWalkable(GameLocation location, int x, int y)
        => TileWalkability.IsTileWalkable(location, x, y);

    /// <summary>
    ///     NPC 是否在玩家视野内（当前地图 + viewport 扩展一格范围）。
    ///     视野内禁止一切传送类恢复，避免肉眼可见的瞬移。
    /// </summary>
    private static bool IsNpcOnScreen(NPC npc)
    {
        if (npc.currentLocation == null || npc.currentLocation != Game1.currentLocation)
        {
            return false;
        }

        var viewport = Game1.viewport;
        var pos = npc.Position;
        return pos.X >= viewport.X - 64 && pos.X <= viewport.X + viewport.Width + 64
                                        && pos.Y >= viewport.Y - 64 && pos.Y <= viewport.Y + viewport.Height + 64;
    }

    /// <summary>
    ///     Finds the nearest walkable tile near anchor using PathfindingUtility
    ///     (nearest-first spiral search). Replaces the old FindWalkableTileNearWithValidation
    ///     which used VerifyTilePathfindable (Fix #4: removed position-tampering validation).
    /// </summary>
    private static Vector2 FindWalkableTileNearValidated(
        GameLocation location, Vector2 anchor, int maxRadius = 10, Character? ignore = null)
    {
        if (location == null)
        {
            return anchor;
        }

        var mapWidth = location.Map?.Layers[0]?.LayerWidth ?? 0;
        var mapHeight = location.Map?.Layers[0]?.LayerHeight ?? 0;

        var result = PathfindingUtility.FindWalkableTileNear(
            new Point((int)anchor.X, (int)anchor.Y),
            (x, y) => TileWalkability.IsTileWalkable(location, x, y, ignore),
            mapWidth > 0 ? mapWidth : int.MaxValue,
            mapHeight > 0 ? mapHeight : int.MaxValue,
            maxRadius);

        return new Vector2(result.X, result.Y);
    }

    /// <summary>
    ///     Last resort: teleport NPC near the target tile.
    /// </summary>
    private static void DoLastResortTeleport(NPC npc, Vector2 targetTile)
    {
        var walkableTile = PathfindingUtility.FindWalkableTileNear(
            new Point((int)targetTile.X, (int)targetTile.Y),
            (x, y) => IsSdvTileWalkable(npc.currentLocation, x, y),
            npc.currentLocation.Map?.Layers[0]?.LayerWidth ?? 0,
            npc.currentLocation.Map?.Layers[0]?.LayerHeight ?? 0);
        npc.setTileLocation(new Vector2(walkableTile.X, walkableTile.Y));
        // Issue 3: Last resort stuck feedback — annoyed emote
        npc.doEmote(28); // cloud emote
        npc.showTextAboveHead("！", duration: 2000);
    }

    private Dictionary<string, int> GetCooldownDict(MovementMode mode) => mode switch
    {
        MovementMode.ShortRange => _lastShortRangeCreation,
        MovementMode.LongRange => _lastLongRangeCreation,
        MovementMode.Immediate => _lastImmediateCreation,
        _ => _lastShortRangeCreation
    };

    private void LogDebug(string npcName, string message) => _monitor?.Log($"[MovementService] {npcName}: {message}");

    /// <summary>
    ///     递增连续NoPathFound计数。达到阈值时自动触发熔断：清除失败缓存并重置计数。
    /// </summary>
    private void IncrementNoPathCount(string npcName)
    {
        var count = _consecutiveNoPathCount.TryGetValue(npcName, out var c) ? c + 1 : 1;
        _consecutiveNoPathCount[npcName] = count;

        // 达到阈值时立即熔断，下次MoveTo调用将重新尝试
        if (count >= MaxConsecutiveNoPathFound)
        {
            LogDebug(npcName, $"连续NoPathFound达{count}次 — 熔断触发，清除失败缓存");
            _failedTargetTiles.Remove(npcName);
            _consecutiveNoPathCount[npcName] = 0;
        }
    }

    /// <summary>
    ///     自定义标准 A*（诊断/兜底用）：int g（无 SDV byte g 溢出），dequeue 时加入 closedList
    ///     （标准时机）。判定与 SDV PFC 一致（TileWalkability：character=npc, pathfinding:true）。
    ///     SDV 的 findPath 在部分可达路径上返回 null（实现缺陷），此方法作为兜底生成路径，
    ///     喂给 PathFindController 的 Stack 构造执行。
    ///     返回路径不含起点（第一个元素是起点后的第一个移动瓦片），Peek 即下一个移动点。
    /// </summary>
    private static Stack<Point>? CustomAStar(GameLocation? loc, Point start, Point end, Character? ignore)
    {
        if (loc?.Map?.Layers == null || loc.Map.Layers.Count == 0)
        {
            return null;
        }

        if (start == end)
        {
            return new Stack<Point>(new[] { end });
        }

        var mapW = loc.Map.Layers[0].LayerWidth;
        var mapH = loc.Map.Layers[0].LayerHeight;

        int Key(int x, int y)
        {
            return x * 1000 + y;
        }

        var closed = new HashSet<int>();
        var gScore = new Dictionary<int, int> { [Key(start.X, start.Y)] = 0 };
        var cameFrom = new Dictionary<int, int>();
        var open = new SortedSet<(int F, int G, int X, int Y)>();
        open.Add((Math.Abs(end.X - start.X) + Math.Abs(end.Y - start.Y), 0, start.X, start.Y));

        var dirs = new[] { new Point(1, 0), new Point(-1, 0), new Point(0, 1), new Point(0, -1) };

        while (open.Count > 0)
        {
            var cur = open.Min;
            _ = open.Remove(cur);
            if (closed.Contains(Key(cur.X, cur.Y)))
            {
                continue;
            }

            if (cur.X == end.X && cur.Y == end.Y)
            {
                var path = new Stack<Point>();
                var ckey = Key(cur.X, cur.Y);
                while (ckey != Key(start.X, start.Y))
                {
                    var cx = ckey / 1000;
                    var cy = ckey % 1000;
                    path.Push(new Point(cx, cy));
                    ckey = cameFrom[ckey];
                }

                return path;
            }

            closed.Add(Key(cur.X, cur.Y));
            foreach (var d in dirs)
            {
                var nx = cur.X + d.X;
                var ny = cur.Y + d.Y;
                if (nx < 0 || ny < 0 || nx >= mapW || ny >= mapH)
                {
                    continue;
                }

                var nkey = Key(nx, ny);
                if (closed.Contains(nkey))
                {
                    continue;
                }

                if (!TileWalkability.IsTileWalkable(loc, nx, ny, ignore))
                {
                    continue;
                }

                var ng = cur.G + 1;
                if (gScore.TryGetValue(nkey, out var oldG) && oldG <= ng)
                {
                    continue;
                }

                gScore[nkey] = ng;
                cameFrom[nkey] = Key(cur.X, cur.Y);
                open.Add((ng + Math.Abs(end.X - nx) + Math.Abs(end.Y - ny), ng, nx, ny));
            }

            if (closed.Count > 50000)
            {
                break;
            }
        }

        return null;
    }
}