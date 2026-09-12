using System.Collections.Generic;

namespace ValleyAgent.Multiplayer
{
    /// <summary>
    /// Mod 消息类型常量。用于 helper.Multiplayer.SendMessage 的 messageType 参数。
    /// </summary>
    public static class MessageTypes
    {
        /// <summary>协议版本号，用于 MultiplayerEventRouter 层版本校验（比较 hostMod.Version.MajorVersion）</summary>
        public const int ProtocolVersion = 1;

        /// <summary>主机 → Farmhand：Agent 状态广播（高频，60 tick/次）</summary>
        public const string AgentState = "AgentState";

        /// <summary>主机 → Farmhand：完整状态同步（低频，Farmhand 加入时）</summary>
        public const string FullSync = "FullSync";

        /// <summary>主机 → Farmhand：对话响应推送</summary>
        public const string DialogueResponse = "DialogueResponse";

        /// <summary>主机 → Farmhand：礼物评估结果推送</summary>
        public const string GiftResponse = "GiftResponse";

        /// <summary>主机 → Farmhand：NPC emote/说话广播</summary>
        public const string NpcAction = "NpcAction";

        /// <summary>Farmhand → 主机：对话请求</summary>
        public const string DialogueRequest = "DialogueRequest";

        /// <summary>Farmhand → 主机：礼物请求</summary>
        public const string GiftRequest = "GiftRequest";

        /// <summary>Farmhand → 主机：交互请求（右键点击 NPC）</summary>
        public const string InteractionRequest = "InteractionRequest";
    }

    /// <summary>
    /// 主机 → Farmhand：Agent 状态广播（高频）。
    /// 每 60 tick 发送一次，包含所有 Agent 的核心状态。
    /// </summary>
    public class AgentStateMessage
    {
        /// <summary>所有 Agent 的状态快照</summary>
        public List<AgentStateSnapshot> Agents { get; set; } = new();
    }

    /// <summary>
    /// 单个 Agent 的状态快照。
    /// 必须有无参构造函数以支持 SMAPI 消息序列化。
    /// </summary>
    public class AgentStateSnapshot
    {
        public string NpcName { get; set; } = "";
        public string State { get; set; } = "IDLE";
        public int Health { get; set; }
        public int MaxHealth { get; set; }
        public string Emotion { get; set; } = "Neutral";
        public string Location { get; set; } = "";
        public float PosX { get; set; }
        public float PosY { get; set; }
        public bool IsDead { get; set; }
        public int FacingDirection { get; set; }
        public bool IsMoving { get; set; }
    }

    /// <summary>
    /// 主机 → Farmhand：完整状态同步（低频，Farmhand 加入时发送）。
    /// 包含更多细节（背包摘要、近期记忆、好感度）。
    /// </summary>
    public class FullSyncMessage
    {
        public List<AgentFullState> Agents { get; set; } = new();
        public long HostPlayerId { get; set; }
    }

    /// <summary>
    /// Agent 完整状态（含背包/记忆/好感度）。
    /// </summary>
    public class AgentFullState
    {
        public string NpcName { get; set; } = "";
        public string State { get; set; } = "IDLE";
        public int Health { get; set; }
        public int MaxHealth { get; set; }
        public string Emotion { get; set; } = "Neutral";
        public string Location { get; set; } = "";
        public float PosX { get; set; }
        public float PosY { get; set; }
        public bool IsDead { get; set; }
        public int Friendship { get; set; }
        public List<string> RecentMemory { get; set; } = new();
        public string FarmerNickname { get; set; } = "新来的农夫";
    }

    /// <summary>
    /// Farmhand → 主机：对话请求。
    /// </summary>
    public class DialogueRequestMessage
    {
        public string NpcName { get; set; } = "";
        public string PlayerMessage { get; set; } = "";
        public long PlayerId { get; set; }
        public string? WorldSnapshotJson { get; set; }

        /// <summary>
        ///     请求关联 ID（房客 SendAsync 生成）。主机必须原样回填到 DialogueResponseMessage，
        ///     房客端据此精确配对 pending；null/空 = 旧版本主机（房客退回 npcName 前缀 FIFO 匹配）。
        /// </summary>
        public string? RequestId { get; set; }
    }

    /// <summary>
    /// 主机 → Farmhand：对话响应。
    /// </summary>
    public class DialogueResponseMessage
    {
        public string NpcName { get; set; } = "";
        public string Text { get; set; } = "";
        public string Emotion { get; set; } = "Neutral";
        public string? Action { get; set; }
        public string? ActionsJson { get; set; }
        public long TargetPlayerId { get; set; }

        /// <summary>
        ///     2026-08-23 联机审计 P1：TS 规则引擎降级标记（BUSY 等）此前没进 ModMessage 契约，
        ///     farmhand 端把"正在和别人交流"当正常台词弹打字机框。true 时 farmhand 走灰色系统提示。
        /// </summary>
        public bool Fallback { get; set; }

        /// <summary>
        ///     对应 DialogueRequestMessage.RequestId 的回填。房客精确匹配 pending；
        ///     null/空（旧主机）时房客退回 FIFO。带值但 pending 已清理（超时）→ 迟到回包直接丢弃，
        ///     绝不回退 FIFO——否则会窃取重试请求的回包。
        /// </summary>
        public string? RequestId { get; set; }
    }

    /// <summary>
    /// Farmhand → 主机：礼物请求。
    /// </summary>
    public class GiftRequestMessage
    {
        public string NpcName { get; set; } = "";
        public string ItemId { get; set; } = "";
        public int Quantity { get; set; }
        public long PlayerId { get; set; }

        /// <summary>
        ///     请求关联 ID（房客 SendAsync 生成）。主机必须原样回填到 GiftResponseMessage；
        ///     null/空 = 旧版本主机（房客退回 npcName 前缀 FIFO 匹配）。
        /// </summary>
        public string? RequestId { get; set; }
    }

    /// <summary>
    /// 主机 → Farmhand：礼物评估结果。
    /// </summary>
    public class GiftResponseMessage
    {
        public string NpcName { get; set; } = "";
        public string ResponseText { get; set; } = "";
        public string Emotion { get; set; } = "Neutral";
        public int FriendshipChange { get; set; }
        public long TargetPlayerId { get; set; }

        /// <summary>
        ///     对应 GiftRequestMessage.RequestId 的回填。语义同 DialogueResponseMessage.RequestId：
        ///     精确匹配 pending；带值但 pending 已清理 → 迟到回包丢弃，防止好感 delta 张冠李戴。
        /// </summary>
        public string? RequestId { get; set; }
    }

    /// <summary>
    /// Farmhand → 主机：交互请求（右键点击 NPC）。
    /// </summary>
    public class InteractionRequestMessage
    {
        public string NpcName { get; set; } = "";
        public long PlayerId { get; set; }
    }

    /// <summary>
    /// 主机 → Farmhand：NPC 动作广播（emote/说话）。
    /// </summary>
    public class NpcActionMessage
    {
        public string NpcName { get; set; } = "";
        public string ActionType { get; set; } = ""; // "emote", "speak", "bubble"
        public int EmoteId { get; set; }
        public string Text { get; set; } = "";
        public int DurationMs { get; set; }
    }
}
