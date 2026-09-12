using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using ValleyAgent.Brain;

namespace ValleyAgent.Multiplayer
{
    /// <summary>
    /// Farmhand 端远程 Agent 状态缓存和渲染器。
    /// 接收主机广播的 Agent 状态，缓存到本地，用于 UI 渲染（血条、emote、气泡文字）。
    /// 不执行任何游戏操作，纯只读渲染。
    /// </summary>
    public class AgentRemoteRenderer
    {
        private readonly IMonitor _monitor;

        /// <summary>远程 Agent 状态缓存，key 为 NPC 名字（小写）</summary>
        private readonly Dictionary<string, AgentFullState> _remoteStates = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>待显示的对话响应队列，key 为 NPC 名字</summary>
        private readonly Dictionary<string, DialogueResponseMessage> _pendingDialogueResponses = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>待显示的礼物响应队列</summary>
        private readonly Dictionary<string, GiftResponseMessage> _pendingGiftResponses = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>待执行的 NPC 动作队列</summary>
        private readonly Queue<NpcActionMessage> _pendingActions = new();

        /// <summary>远程 NPC 位置目标缓存，key 为 NPC 名字（小写）。用于位置插值平滑移动。</summary>
        private readonly Dictionary<string, RemotePositionTarget> _positionTargets = new(StringComparer.OrdinalIgnoreCase);

        public AgentRemoteRenderer(IMonitor monitor)
        {
            _monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        }

        /// <summary>
        /// 获取指定 NPC 的远程状态。如果不存在返回 null。
        /// </summary>
        public AgentFullState? GetRemoteState(string npcName)
        {
            _remoteStates.TryGetValue(npcName, out var state);
            return state;
        }

        /// <summary>
        /// 获取所有远程 Agent 状态。
        /// </summary>
        public IReadOnlyDictionary<string, AgentFullState> GetAllRemoteStates() => _remoteStates;

        /// <summary>
        /// 获取并移除待处理的对话响应。
        /// </summary>
        public DialogueResponseMessage? DequeueDialogueResponse(string npcName)
        {
            if (_pendingDialogueResponses.Remove(npcName, out var response))
                return response;
            return null;
        }

        /// <summary>
        /// 获取并移除待处理的礼物响应。
        /// </summary>
        public GiftResponseMessage? DequeueGiftResponse(string npcName)
        {
            if (_pendingGiftResponses.Remove(npcName, out var response))
                return response;
            return null;
        }

        /// <summary>
        /// 获取并移除待处理的 NPC 动作。
        /// </summary>
        public NpcActionMessage? DequeueAction()
        {
            return _pendingActions.Count > 0 ? _pendingActions.Dequeue() : null;
        }

        /// <summary>
        /// 处理收到的 Agent 状态广播消息。
        /// </summary>
        public void HandleAgentStateMessage(AgentStateMessage message)
        {
            foreach (var snapshot in message.Agents)
            {
                if (!_remoteStates.TryGetValue(snapshot.NpcName, out var existing))
                {
                    existing = new AgentFullState { NpcName = snapshot.NpcName };
                    _remoteStates[snapshot.NpcName] = existing;
                }

                existing.State = snapshot.State;
                existing.Health = snapshot.Health;
                existing.MaxHealth = snapshot.MaxHealth;
                existing.Emotion = snapshot.Emotion;
                existing.Location = snapshot.Location;
                existing.PosX = snapshot.PosX;
                existing.PosY = snapshot.PosY;
                existing.IsDead = snapshot.IsDead;

                // 死亡 NPC 不插值位置，避免在尸体上滑动
                if (snapshot.IsDead)
                    continue;

                _positionTargets[snapshot.NpcName] = new RemotePositionTarget(
                    snapshot.Location,
                    new Vector2(snapshot.PosX, snapshot.PosY),
                    snapshot.FacingDirection,
                    snapshot.IsMoving);
            }
        }

        /// <summary>
        /// 处理收到的完整状态同步消息（Farmhand 加入时）。
        /// </summary>
        public void HandleFullSyncMessage(FullSyncMessage message)
        {
            _remoteStates.Clear();

            foreach (var state in message.Agents)
            {
                _remoteStates[state.NpcName] = state;
            }

            _monitor.Log($"[Multiplayer] Received full sync: {message.Agents.Count} agents from host", LogLevel.Info);
        }

        /// <summary>
        /// 处理收到的对话响应消息。
        /// </summary>
        public void HandleDialogueResponse(DialogueResponseMessage message)
        {
            _pendingDialogueResponses[message.NpcName] = message;
        }

        /// <summary>
        /// 处理收到的礼物响应消息。
        /// </summary>
        public void HandleGiftResponse(GiftResponseMessage message)
        {
            _pendingGiftResponses[message.NpcName] = message;
        }

        /// <summary>
        /// 处理收到的 NPC 动作消息。
        /// </summary>
        public void HandleNpcAction(NpcActionMessage message)
        {
            _pendingActions.Enqueue(message);
        }

        /// <summary>
        /// 每 tick 调用，处理待执行的 NPC 动作（emote/说话）。
        /// 只在 Farmhand 端执行。
        /// </summary>
        public void Update(int currentTick)
        {
            if (!MultiplayerHelper.IsFarmhand)
                return;

            // 位置插值：在两次快照间平滑移动 NPC
            UpdatePositionInterpolation();

            // 处理 NPC 动作（emote/说话）
            while (_pendingActions.Count > 0)
            {
                var action = _pendingActions.Dequeue();
                ApplyNpcAction(action);
            }
        }

        /// <summary>
        /// 在两次状态快照间对 NPC 进行位置插值，避免 farmhand 视角下"瞬移 + 静止"的体验。
        /// 标记为 internal 以便单元测试在单机模式下直接驱动（绕过 Update 的 IsFarmhand 守卫）。
        /// </summary>
        internal void UpdatePositionInterpolation()
        {
            if (_positionTargets.Count == 0)
                return;

            foreach (var kvp in _positionTargets)
            {
                var npcName = kvp.Key;
                var target = kvp.Value;

                try
                {
                    var npc = Game1.getCharacterFromName(npcName);
                    if (npc?.currentLocation == null)
                        continue;

                    // 激活图约束：只对激活图内的 NPC 应用位置写
                    if (!npc.currentLocation.IsActiveLocation())
                        continue;

                    // 跨图：当前图 != 目标图 → 跳过（由 vanilla warp 同步处理）
                    if (!string.Equals(npc.currentLocation.NameOrUniqueName, target.LocationName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var dist = Vector2.Distance(npc.Position, target.Position);
                    const float snapThreshold = 8 * 64f; // 8 tiles in pixels

                    if (dist > snapThreshold)
                    {
                        // 距离过远 → 直接吸附到目标像素位置（主机侧传送/旅行到达的合理反映）
                        npc.Position = target.Position;
                        continue;
                    }

                    if (dist < 4f)
                    {
                        // 已到达目标 → 应用朝向（静止时）
                        if (!target.IsMoving)
                        {
                            npc.faceDirection(target.FacingDirection);
                        }
                        continue;
                    }

                    // 中等距离 → 按 NPC Speed 步进，逐轴碰撞检查
                    var velocity = Utility.getVelocityTowardPoint(npc.Position, target.Position, npc.Speed);
                    ApplyAxisMovement(npc, velocity);
                }
                catch (Exception ex)
                {
                    _monitor.Log($"[AgentRemoteRenderer] Position interpolation failed for {npcName}: {ex}", LogLevel.Error);
                }
            }
        }

        /// <summary>
        /// 逐轴应用移动，每轴独立碰撞检查。使用 NPC 自身 bounding box 与完整地图视口，
        /// 避免非玩家所在图的视口偏移导致 isCollidingPosition 误判。
        /// 注：SDV 1.6 中 NPC.isCollidingPosition 非公开 API，统一走 GameLocation.isCollidingPosition
        /// （参考 ValleyAgent.Abstractions/Navigation/TileWalkability.cs 既有模式）。
        /// </summary>
        private static void ApplyAxisMovement(NPC npc, Vector2 velocity)
        {
            var location = npc.currentLocation;
            if (location == null)
                return;

            var map = location.Map;
            if (map?.Layers == null || map.Layers.Count == 0)
                return;

            int mapWidth = map.Layers[0].LayerWidth;
            int mapHeight = map.Layers[0].LayerHeight;
            if (mapWidth == 0 || mapHeight == 0)
                return;

            // 完整地图视口，避免 Game1.viewport 跨图误判（参考 TileWalkability.cs 的修复模式）
            var fullViewport = new xTile.Dimensions.Rectangle(0, 0, mapWidth * 64, mapHeight * 64);
            var currentBbox = npc.GetBoundingBox();

            // X 轴
            if (Math.Abs(velocity.X) > 0.01f)
            {
                var proposedBbox = new Rectangle(
                    currentBbox.X + (int)velocity.X,
                    currentBbox.Y,
                    currentBbox.Width,
                    currentBbox.Height);

                if (!location.isCollidingPosition(proposedBbox, fullViewport, isFarmer: false, 0, glider: false, null))
                {
                    npc.Position = new Vector2(npc.Position.X + velocity.X, npc.Position.Y);
                }
            }

            // Y 轴
            if (Math.Abs(velocity.Y) > 0.01f)
            {
                var proposedBbox = new Rectangle(
                    currentBbox.X,
                    currentBbox.Y + (int)velocity.Y,
                    currentBbox.Width,
                    currentBbox.Height);

                if (!location.isCollidingPosition(proposedBbox, fullViewport, isFarmer: false, 0, glider: false, null))
                {
                    npc.Position = new Vector2(npc.Position.X, npc.Position.Y + velocity.Y);
                }
            }
        }

        /// <summary>
        /// 在 Farmhand 端本地应用 NPC 动作（渲染用，不修改游戏状态）。
        /// </summary>
        private static void ApplyNpcAction(NpcActionMessage action)
        {
            var npc = Game1.getCharacterFromName(action.NpcName);
            if (npc == null)
                return;

            switch (action.ActionType?.ToLowerInvariant())
            {
                case "emote":
                    npc.doEmote(action.EmoteId);
                    break;

                case "speak":
                case "bubble":
                    if (!string.IsNullOrEmpty(action.Text))
                    {
                        var dialogue = new StardewValley.Dialogue(npc, null, action.Text);
                        npc.setNewDialogue(dialogue);
                        Game1.drawDialogue(npc);
                    }
                    break;
            }
        }

        /// <summary>
        /// 清除所有缓存状态（断线重连时调用）。
        /// </summary>
        public void Clear()
        {
            _remoteStates.Clear();
            _pendingDialogueResponses.Clear();
            _pendingGiftResponses.Clear();
            _pendingActions.Clear();
            _positionTargets.Clear();
        }

        /// <summary>
        /// 远程 NPC 位置目标缓存项。记录主机侧最近一次广播的 NPC 像素位置/朝向/移动状态，
        /// 供 UpdatePositionInterpolation 在两次快照间平滑插值。
        /// </summary>
        private sealed class RemotePositionTarget
        {
            /// <summary>目标图名字（与 NPC.currentLocation.NameOrUniqueName 比较）</summary>
            public string LocationName { get; }

            /// <summary>像素级目标位置（1 tile = 64 像素）</summary>
            public Vector2 Position { get; }

            /// <summary>目标朝向（0=down, 1=left, 2=up, 3=right）</summary>
            public int FacingDirection { get; }

            /// <summary>主机侧是否正在移动；静止时插值末端应用朝向</summary>
            public bool IsMoving { get; }

            public RemotePositionTarget(string locationName, Vector2 position, int facingDirection, bool isMoving)
            {
                LocationName = locationName;
                Position = position;
                FacingDirection = facingDirection;
                IsMoving = isMoving;
            }
        }
    }
}
