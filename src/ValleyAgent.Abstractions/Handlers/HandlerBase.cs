using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Monsters;
using ValleyAgent.Navigation;

namespace ValleyAgent.Handlers
{
    /// <summary>
    /// Handler 基类，提取公共工具方法和冷却/目标管理。
    /// 所有 Handler 继承此类以消除代码重复。
    /// </summary>
    public abstract class HandlerBase
    {
        protected readonly IMonitor? _monitor;
        protected readonly IMovementService _movementService = null!;
        protected readonly AgentNavigator? _navigator;

        // 动作冷却：NPC名 → 上次执行 tick
        private readonly Dictionary<string, int> _lastActionTick = new(StringComparer.OrdinalIgnoreCase);

        // ForcedTarget：NPC名 → 目标信息
        private readonly Dictionary<string, ForcedTargetInfo> _forcedTargets = new(StringComparer.OrdinalIgnoreCase);

        // 连续寻路失败计数：NPC名 → 失败次数
        private readonly Dictionary<string, int> _pathBlockedCount = new(StringComparer.OrdinalIgnoreCase);
        protected const int MaxPathBlockedRetries = 5;

        // 障碍物清障计数：NPC名 → (上次障碍物位置, 连续尝试次数)
        private readonly Dictionary<string, Point> _lastObstacleTile = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _obstacleClearCount = new(StringComparer.OrdinalIgnoreCase);
        protected const int MaxObstacleClearRetries = 3;

        // 全局注册表：所有 Handler 实例，用于 Agent 销毁时批量清理
        private static readonly List<HandlerBase> _allHandlers = new();
        private static readonly object _registryLock = new();

        public HandlerBase()
        {
            lock (_registryLock) { _allHandlers.Add(this); }
        }

        /// <summary>清理所有 Handler 中指定 NPC 的追踪状态（Agent 销毁时调用）。</summary>
        public static void CleanupAll(string npcName)
        {
            lock (_registryLock)
            {
                foreach (var h in _allHandlers)
                {
                    h.Cleanup(npcName);
                }
            }
        }

        /// <summary>统一的目标信息结构，替代三套不兼容的 ForcedTarget 系统。</summary>
        public struct ForcedTargetInfo
        {
            public Point Tile;
            public string? Action;    // 可选动作描述（FarmHandler 用）
            public string? TargetId;  // 可选目标 ID（FightHandler 用）
        }

        // 默认动作冷却 ticks（子类可覆盖）
        protected virtual int DefaultActionCooldownTicks => 48;

        protected HandlerBase(IMonitor? monitor, IMovementService movementService, AgentNavigator? navigator = null)
        {
            lock (_registryLock) { _allHandlers.Add(this); }
            _monitor = monitor;
            _movementService = movementService ?? throw new ArgumentNullException(nameof(movementService));
            _navigator = navigator;
        }

        // ─── 公共工具方法 ────────────────────────────────────────────────

        /// <summary>让 NPC 面朝目标位置。</summary>
        protected static void FacePosition(NPC npc, Vector2 target)
        {
            var dir = target - npc.getStandingPosition();
            if (Math.Abs(dir.X) >= Math.Abs(dir.Y))
            {
                npc.faceDirection(dir.X > 0 ? 1 : 3);
            }
            else
            {
                npc.faceDirection(dir.Y > 0 ? 2 : 0);
            }
        }

        /// <summary>判断两个瓦片是否相邻（Manhattan 距离 ≤ 1，不含对角线，与 4 方向寻路一致）。</summary>
        protected static bool IsAdjacent(Point a, Point b)
        {
            var dx = Math.Abs(a.X - b.X);
            var dy = Math.Abs(a.Y - b.Y);
            return (dx + dy) <= 1;
        }

        /// <summary>判断 NPC 是否与怪物相邻。</summary>
        protected static bool IsAdjacentToMonster(NPC npc, Monster monster)
        {
            var distX = Math.Abs(npc.TilePoint.X - monster.TilePoint.X);
            var distY = Math.Abs(npc.TilePoint.Y - monster.TilePoint.Y);
            return distX <= 1 && distY <= 1;
        }

        /// <summary>判断冷却是否已过，可以执行动作。</summary>
        protected bool CanAct(string npcName, int currentTick, int? cooldownTicks = null)
        {
            var cooldown = cooldownTicks ?? DefaultActionCooldownTicks;
            return !_lastActionTick.TryGetValue(npcName, out var lastTick) || (currentTick - lastTick) >= cooldown;
        }

        /// <summary>记录动作执行 tick。</summary>
        protected void RecordAction(string npcName, int currentTick) => _lastActionTick[npcName] = currentTick;

        /// <summary>判断 NPC 是否正在执行动作（需要 currentTick）。</summary>
        protected bool IsActing(string npcName, int currentTick, int? cooldownTicks = null)
        {
            var cooldown = cooldownTicks ?? DefaultActionCooldownTicks;
            return _lastActionTick.TryGetValue(npcName, out var lastTick) && (currentTick - lastTick) < cooldown;
        }

        /// <summary>清理 NPC 的所有追踪状态（Agent 销毁时调用）。</summary>
        public virtual void Cleanup(string npcName)
        {
            _ = _lastActionTick.Remove(npcName);
            _ = _forcedTargets.Remove(npcName);
            _ = _pathBlockedCount.Remove(npcName);
            _ = _lastObstacleTile.Remove(npcName);
            _ = _obstacleClearCount.Remove(npcName);
        }

        // ─── ForcedTarget 管理 ───────────────────────────────────────────

        public void SetForcedTarget(string npcName, Point tile, string? action = null, string? targetId = null) => _forcedTargets[npcName] = new ForcedTargetInfo { Tile = tile, Action = action, TargetId = targetId };

        public void ClearForcedTarget(string npcName) => _ = _forcedTargets.Remove(npcName);

        public bool HasForcedTarget(string npcName) => _forcedTargets.ContainsKey(npcName);

        public bool TryGetForcedTarget(string npcName, out Point tile)
        {
            if (_forcedTargets.TryGetValue(npcName, out var info))
            {
                tile = info.Tile;
                return true;
            }
            tile = default;
            return false;
        }

        public bool TryGetForcedTargetInfo(string npcName, out ForcedTargetInfo info) => _forcedTargets.TryGetValue(npcName, out info);

        // ─── 日志辅助 ────────────────────────────────────────────────────

        protected void LogTrace(string message) => _monitor?.Log(message, LogLevel.Trace);

        protected void LogDebug(string message) => _monitor?.Log(message, LogLevel.Debug);

        protected void IncrementPathBlocked(string npcName)
        {
            _pathBlockedCount.TryGetValue(npcName, out var count);
            _pathBlockedCount[npcName] = count + 1;
        }

        protected void ResetPathBlocked(string npcName) => _pathBlockedCount.Remove(npcName);

        protected bool IsPathBlockedExhausted(string npcName) =>
            _pathBlockedCount.TryGetValue(npcName, out var count) && count >= MaxPathBlockedRetries;

        protected bool RecordObstacleAttempt(string npcName, Point obstacleTile)
        {
            if (_lastObstacleTile.TryGetValue(npcName, out var lastTile) && lastTile == obstacleTile)
            {
                _obstacleClearCount.TryGetValue(npcName, out var count);
                _obstacleClearCount[npcName] = count + 1;
            }
            else
            {
                _lastObstacleTile[npcName] = obstacleTile;
                _obstacleClearCount[npcName] = 1;
            }
            return _obstacleClearCount[npcName] >= MaxObstacleClearRetries;
        }

        protected void ResetObstacleClear(string npcName)
        {
            _lastObstacleTile.Remove(npcName);
            _obstacleClearCount.Remove(npcName);
        }
    }
}
