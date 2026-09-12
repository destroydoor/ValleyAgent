using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ValleyAgent.WebSocket
{
    /// <summary>
    /// Record DTO for dialogue generation requests sent over WebSocket.
    /// </summary>
    public record TilePosition(int X, int Y);

    public record InventoryItem(string Name, int Quantity);

    /// <summary>
    /// 玩家视角下的在场 NPC 摘要（E2-2 聊天栏路由）。
    /// 供 LLM 行使"沉默权"（要不要接话）时感知场景：谁在场、多远、什么角色、
    /// 是否跟随、是否处于会话中。Role 为 AgentState 字符串（FOLLOW/IDLE 等）。
    /// </summary>
    public record PresentNpcInfo(
        string Name,
        string Role,
        int DistanceTiles,
        bool IsFollowing,
        bool InSession);

    /// <summary>
    ///     玩家手持物摘要（E3-6 交易市场价锚定）。
    ///     当玩家手持可赠送物右键 NPC 触发"送礼/交易"菜单时，由 WorldSnapshotBuilder 注入。
    ///     MarketPrice 为游戏内 sellToStorePrice（卖给商店的公道价），用作 NPC 出价锚点；
    ///     NPC 可在 ±30% 内根据玩家说辞让步，但不应大幅偏离（防 LLM 臆想定价）。
    ///     ItemId 为空表示玩家未手持可交易物（默认 null 向后兼容）。
    /// </summary>
    public record PlayerHeldItem(
        string ItemId,
        string DisplayName,
        int Quantity,
        int MarketPrice);

    /// <summary>
    ///     E3-5: NPC 当日求购单条目（2026-09-12 接线）。NPC 在对话中以此锚定求购价，
    ///     命中送礼时提示玩家"走对话议价"后 NPC 有价格依据。UnitPrice 为单价。
    /// </summary>
    public record PurchaseOfferInfo(
        string ItemId,
        string ItemName,
        int Quantity,
        int UnitPrice);

    public record WorldSnapshot(
        string Season,
        int Day,
        string Time,
        string Weather,
        string Location,
        TilePosition NpcTile,
        string NearbyObjects,
        int Friendship,
        string NpcState,
        IReadOnlyList<InventoryItem> Inventory,
        string FarmerName,
        // E2-2: 尾部默认 null 保持既有调用点向后兼容；由 WorldSnapshotBuilder 填充。
        IReadOnlyList<PresentNpcInfo>? PresentNpcs = null,
        // E4-1: 玩家钱包（缺钱/有钱 NPC 差异化接单判定的 LLM 输入）。默认 null 向后兼容。
        int? PlayerMoney = null,
        // E0-6: NPC 自身所在地图（认知对齐——NPC 知道自己在哪，而非只知玩家 Location）。默认 null 向后兼容。
        string? NpcLocation = null,
        // E0-6: NPC 自己的钱包余额（认知对齐；NPC 未分配为 Agent 时无背包可取，为 null）。默认 null 向后兼容。
        int? NpcMoney = null,
        // E0-6: NPC 自己的背包非空格位（认知对齐；NPC 未分配为 Agent 时为 null）。默认 null 向后兼容。
        IReadOnlyList<InventoryItem>? NpcInventory = null,
        // E3-6: 玩家手持物 + 市场参考价（交易定价锚点）。null=未手持可交易物。
        PlayerHeldItem? PlayerHeldItem = null,
        // 阶段3 L2: NPC 心情标签（Director set_npc_mood 写入，prompt「你的状态」段）。默认 null 向后兼容。
        string? NpcMood = null,
        // 阶段3 L2: NPC 近期事件（todayEvents 正文）。默认 null 向后兼容。
        IReadOnlyList<string>? NpcRecentEvents = null,
        // 阶段3 L2: NPC 工作标记（Director set_npc_working_on 写入）。默认 null 向后兼容。
        string? NpcWorkingOn = null,
        // 阶段3 L2: NPC 欠款（g）。默认 null 向后兼容。
        int? NpcOwedMoney = null,
        // 阶段3 L3: 当前活跃 beat 场景描述（BeatStore 提供，beat 有效期内注入）。默认 null 向后兼容。
        string? CurrentBeat = null,
        // E3-5: NPC 当日求购单（NpcPurchaseRequestService.PurchaseOffers；对话议价的价格锚）。
        // 默认 null 向后兼容（无求购/服务未接线）。
        IReadOnlyList<PurchaseOfferInfo>? NpcPurchaseOffers = null
    );

    public record DialogueRequest(
        string Type,
        string RequestId,
        string NpcName,
        string PlayerInput,
        WorldSnapshot WorldSnapshot,
        // 2026-08-16 联机：发起对话的玩家 ID（UniqueMultiplayerID 字符串）。null → 单机/旧客户端回落 Game1.player。
        string? PlayerId = null
    );

    /// <summary>
    /// Record DTO for a single tool action returned by the LLM (spec 4.2).
    /// CallId 透传自 TS 下发的 LLM tool_call_id，用于 C3 action_result 反馈环。
    /// 默认空字符串兼容旧 TS 客户端（无 callId 时跳过回发）。
    /// </summary>
    public record ToolAction(string Tool, Dictionary<string, object> Args, string CallId = "");

    /// <summary>
    /// Record DTO for dialogue generation responses received over WebSocket.
    /// 方案 B：friendshipDelta/friendshipReason 并回 dialogue 主路径，消灭 friendship_eval 死管道；
    /// LLM 未输出时为 null（默认按 0 处理）。
    /// </summary>
    public record DialogueResponse(
        string Speech,
        IReadOnlyList<ToolAction> Actions,
        string? Emotion = "Neutral",
        string RequestId = "",
        string Type = "",
        int? FriendshipDelta = null,
        string? FriendshipReason = null,
        // 2026-08-16 联机：TS 规则引擎降级响应标记（BUSY 等），C# 用灰色系统提示渲染。
        bool? Fallback = null,
        // 2026-08-16 联机：发起对话的玩家 ID 原样 echo，据此记录 LastDialoguePlayerId（FOLLOW 目标解析）。
        string? PlayerId = null,
        // 2026-08-20 Phase 2：对齐 protocol/messages.json dialogue_response 字段（原缺失，契约漂移修复）。
        // npcName（schema required）与 memorySideEffect（"recorded" 标记）此前被反序列化静默丢弃。
        string? NpcName = null,
        string? MemorySideEffect = null
    );

    /// <summary>
    /// Interface for WebSocket-based LLM server communication.
    /// </summary>
    public interface IAgentServerProvider
    {
        /// <summary>
        /// Event fired when the WebSocket connection is established.
        /// </summary>
        public event Action? OnConnected;

        /// <summary>
        /// Event fired when the WebSocket connection is lost.
        /// </summary>
        public event Action? OnDisconnected;

        /// <summary>
        /// Request dialogue generation for the given request.
        /// </summary>
        public Task<DialogueResponse> GenerateDialogueAsync(DialogueRequest request, CancellationToken ct = default);

        /// <summary>
        /// Check whether the provider is currently connected to the server.
        /// </summary>
        public Task<bool> IsConnectedAsync(CancellationToken ct = default);

        /// <summary>
        /// Send a one-way JSON message to the server without expecting a response.
        /// Used for action_result notifications, event dispatches, etc.
        /// </summary>
        public Task SendMessageAsync(string jsonMessage, CancellationToken ct = default);
    }

}
