using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using ValleyAgent.Navigation;
using ValleyAgent.Services;
using ValleyAgent.Utils;

namespace ValleyAgent.Handlers
{
    /// <summary>
    /// IDLE 状态的环境漫步：让待机 Agent 不死板站桩。
    /// 每隔几秒随机决定：原地张望（随机转身）或慢速散步到附近随机可走瓦片。
    /// 对话中、跨图旅行隐藏中、已交还原版日程（vanilla-release）、
    /// 事件/节日期间不漫步。
    /// </summary>
    public class IdleWanderHandler
    {
        private readonly IMovementService _movementService;
        private readonly IMonitor? _monitor;
        private readonly Func<string, bool>? _isReleasedToVanilla;
        private readonly Func<string, bool>? _isInDialogue;
        private readonly Dictionary<string, int> _nextWanderTick = new(StringComparer.OrdinalIgnoreCase);

        // 漫步间隔：4~10 秒随机（240~600 tick）
        private const int MinWanderIntervalTicks = 240;
        private const int MaxWanderIntervalTicks = 600;
        private const int WanderRadiusTiles = 5;
        // 原地张望概率：让 NPC 有动有静，不像巡逻机器人
        private const double StandStillProbability = 0.35;

        public IdleWanderHandler(
            IMonitor? monitor,
            IMovementService movementService,
            Func<string, bool>? isReleasedToVanilla = null,
            Func<string, bool>? isInDialogue = null)
        {
            _monitor = monitor;
            _movementService = movementService;
            _isReleasedToVanilla = isReleasedToVanilla;
            _isInDialogue = isInDialogue;
        }

        public void Update(NPC npc, AgentInstance agent, int currentTick)
        {
            var npcName = npc.Name;

            if (GameEventGuard.IsEventOrFestivalActive)
            {
                return;
            }

            // 已交还原版日程：由 schedule 驱动，不干预
            if (_isReleasedToVanilla?.Invoke(npcName) == true)
            {
                return;
            }

            // 正在和玩家对话：站住面对玩家，不漫步
            if (_isInDialogue?.Invoke(npcName) == true)
            {
                return;
            }

            // 跨图旅行隐藏中（IsInvisible 或旧约定的负坐标位置）
            if (npc.IsInvisible || npc.Position.X < 0 || npc.Position.Y < 0)
            {
                return;
            }

            if (_movementService.IsMoving(npcName) || _movementService.IsFrozen(npcName))
            {
                return;
            }

            if (!_nextWanderTick.TryGetValue(npcName, out var nextTick))
            {
                // 首次：错峰启动，避免多个 Agent 同帧同时散步
                _nextWanderTick[npcName] = currentTick + RandomNumberGenerator.GetInt32(60, MinWanderIntervalTicks);
                return;
            }

            if (currentTick < nextTick)
            {
                return;
            }

            _nextWanderTick[npcName] = currentTick + RandomNumberGenerator.GetInt32(MinWanderIntervalTicks, MaxWanderIntervalTicks + 1);

            // 35% 概率原地张望（随机转身），有动有静更像活人
            if (RandomNumberGenerator.GetInt32(100) < (int)(StandStillProbability * 100))
            {
                npc.faceDirection(RandomNumberGenerator.GetInt32(4));
                return;
            }

            // 随机附近可走瓦片散步（最多试 6 次，找不到就本轮放弃）
            for (var attempt = 0; attempt < 6; attempt++)
            {
                var dx = RandomNumberGenerator.GetInt32(-WanderRadiusTiles, WanderRadiusTiles + 1);
                var dy = RandomNumberGenerator.GetInt32(-WanderRadiusTiles, WanderRadiusTiles + 1);
                if (Math.Abs(dx) + Math.Abs(dy) < 2)
                {
                    continue;
                }

                var tx = (int)npc.Tile.X + dx;
                var ty = (int)npc.Tile.Y + dy;
                if (!TileWalkability.IsTileWalkable(npc.currentLocation, tx, ty, npc))
                {
                    continue;
                }

                var result = _movementService.MoveTo(npc, new Point(tx, ty), MovementMode.ShortRange, currentTick);
                if (result == MoveResult.Success)
                {
                    // 慢速散步感（MoveTo 内部设的是寻路速度，这里压低）
                    npc.Speed = 1;
                    _monitor?.Log($"[IdleWander] {npcName}: strolling to ({tx},{ty})", LogLevel.Trace);
                }
                return;
            }
        }
    }
}
