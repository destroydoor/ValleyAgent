using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ValleyAgent.Protocol;

/// <summary>
///     Protocol V2 message types for C# ↔ TS Agent Server communication.
///     All messages use JSON with camelCase property names.
/// </summary>
public static class ProtocolV2
{
    /// <summary>
    ///     action_result 失败时的机器可读原因枚举。
    ///     对应设计文档 docs/design/2026-08-01-npc-feedback-architecture.md §1.3(a)。
    ///     序列化为 camelCase 字符串（由 MessageProtocol 的全局 JsonStringEnumConverter 处理），
    ///     与 TS 端 narrative-types.ts 的字符串联合类型对齐。
    /// </summary>
    public enum ActionResultReason
    {
        /// <summary>成功或无具体原因。</summary>
        None,

        /// <summary>AGENT_MISSING: agent 未分配 / NPC 不存在。</summary>
        AgentMissing,

        /// <summary>TRANSITION_BLOCKED: 状态机拒绝转换。</summary>
        TransitionBlocked,

        /// <summary>INVALID_STATE: 非法状态名 / 参数缺失或非法。</summary>
        InvalidState,

        /// <summary>TARGET_UNREACHABLE: 目标不可达。</summary>
        TargetUnreachable,

        /// <summary>ITEM_NOT_FOUND: 物品不存在。</summary>
        ItemNotFound,

        /// <summary>INVENTORY_FULL: 背包满。</summary>
        InventoryFull,

        /// <summary>LOCATION_INVALID: 地点无效。</summary>
        LocationInvalid,

        /// <summary>INTERNAL_ERROR: 内部异常。</summary>
        InternalError,

        /// <summary>ALLOCATED: allocate_agent 成功分配。</summary>
        Allocated,

        /// <summary>MAX_CAPACITY_REACHED: allocate_agent 失败，已达 MaxAgentNpcs 且无可替槽。</summary>
        MaxCapacityReached
    }

    /// <summary>
    ///     execute_adjust 原子批失败时的机器可读失败码枚举（2026-08-15 账本迁移设计 §6）。
    ///     camelCase 序列化（"insufficientFunds" 等），与 TS 端 adjust failure code 字符串联合类型对齐。
    /// </summary>
    public enum AdjustFailureCode
    {
        /// <summary>成功或无具体失败码。</summary>
        None,

        /// <summary>INSUFFICIENT_FUNDS: 钱包余额不足（玩家或 NPC）。</summary>
        InsufficientFunds,

        /// <summary>INVENTORY_FULL: 背包空间不足（玩家或 NPC）。</summary>
        InventoryFull,

        /// <summary>ITEM_NOT_FOUND: 物品不存在或数量不足。</summary>
        ItemNotFound,

        /// <summary>AGENT_MISSING: NPC Agent 未分配。</summary>
        AgentMissing,

        /// <summary>INVALID_OP: 操作参数非法（kind/target/amount/quantity）。</summary>
        InvalidOp,

        /// <summary>PLAYER_NOT_FOUND: playerId 解析不到对应 Farmer（2026-08-16 联机）。</summary>
        PlayerNotFound,

        /// <summary>INTERNAL_ERROR: 执行器内部异常。</summary>
        InternalError
    }

    public const string MessageTypeDialogueRequest = "dialogue_request";
    public const string MessageTypeDialogueResponse = "dialogue_response";
    public const string MessageTypeActionResult = "action_result";

    /// <summary>导演请求 C# 分配某 NPC 为 Agent（TS→C#, fire_and_forget）。设计文档 §4.2.2。</summary>
    public const string MessageTypeAllocateAgent = "allocate_agent";

    /// <summary>
    ///     阶段 3 Director 工具命令（TS→C#, fire_and_forget）。Director agent 的工具调用入口：
    ///     { type, tool, args, requestId }。C# 侧 CommandExecutor.ExecuteDirectorCommand 执行，
    ///     回 action_result（echo requestId）。Director 工具不是 NPC 角色扮演工具——不走 dialogue 的 ToolAction 通道。
    /// </summary>
    public const string MessageTypeDirectorCommand = "director_command";

    /// <summary>C# DayStarted 时通知 TS 新的一天开始（C#→TS, fire_and_forget）。设计文档 §4.2.2 导演触发。</summary>
    public const string MessageTypeDayStarted = "day_started";

    /// <summary>
    ///     C# 推送游戏世界上下文快照给 TS GameContextManager（C#→TS, fire_and_forget）。
    ///     Director.morningPlan 依赖该上下文，缺失时直接返回空计划——2026-08-09 实测发现
    ///     C# 从未发送该消息，导演 LLM 在游戏内从未真正运行。随 day_started 一并发送。
    /// </summary>
    public const string MessageTypeGameContextSync = "game_context_sync";

    /// <summary>
    ///     原子批经济指令（TS→C#, request/response）。2026-08-15 账本迁移设计 §4.1/§4.3。
    ///     TS 账本业务校验后下发：money/item 操作列表 + instructionId（幂等键）。
    ///     C# adjust 执行器物理校验并原子执行，回 adjust_result。
    /// </summary>
    public const string MessageTypeExecuteAdjust = "execute_adjust";

    /// <summary>
    ///     原子批经济指令回执（C#→TS）。每步成败 + 失败码 + 变更后余额。
    ///     TS 账本 pending → committed / rolled_back 的依据。
    /// </summary>
    public const string MessageTypeAdjustResult = "adjust_result";

    /// <summary>
    ///     重连对账消息（C#→TS, 步骤 4 预留）。outbox 补发 + 全量状态。
    /// </summary>
    public const string MessageTypeReconnectSync = "reconnect_sync";

    public class CommandAction
    {
        [JsonPropertyName("action")] public string Action { get; set; } = "";
        [JsonPropertyName("parameters")] public Dictionary<string, object> Parameters { get; set; } = new();
        [JsonPropertyName("reason")] public string Reason { get; set; } = "";
        [JsonPropertyName("callId")] public string CallId { get; set; } = "";
    }


    public class DialogueRequestMessage
    {
        [JsonPropertyName("type")] public string Type { get; set; } = MessageTypeDialogueRequest;
        [JsonPropertyName("requestId")] public string RequestId { get; set; } = Guid.NewGuid().ToString();
        [JsonPropertyName("npcName")] public string NpcName { get; set; } = "";
        [JsonPropertyName("playerInput")] public string PlayerInput { get; set; } = "";
        [JsonPropertyName("context")] public Dictionary<string, object> Context { get; set; } = new();
    }

    public class DialogueResponseMessage
    {
        [JsonPropertyName("type")] public string Type { get; set; } = MessageTypeDialogueResponse;
        [JsonPropertyName("requestId")] public string RequestId { get; set; } = "";
        [JsonPropertyName("npcName")] public string NpcName { get; set; } = "";
        [JsonPropertyName("text")] public string Text { get; set; } = "";
        [JsonPropertyName("emotion")] public string Emotion { get; set; } = "Neutral";
        [JsonPropertyName("action")] public string? Action { get; set; }
    }



    public class ActionResultMessage
    {
        [JsonPropertyName("type")] public string Type { get; set; } = MessageTypeActionResult;
        [JsonPropertyName("requestId")] public string RequestId { get; set; } = "";
        [JsonPropertyName("npcName")] public string NpcName { get; set; } = "";
        [JsonPropertyName("action")] public string Action { get; set; } = "";

        /// <summary>LLM 工具调用 ID（C3 反馈环匹配）。新路径必填，旧路径为空。</summary>
        [JsonPropertyName("callId")]
        public string CallId { get; set; } = "";

        /// <summary>工具名（与 action 镜像；TS routeToolResult 优先读 tool）。</summary>
        [JsonPropertyName("tool")]
        public string Tool { get; set; } = "";

        [JsonPropertyName("success")] public bool Success { get; set; }

        /// <summary>
        ///     结果负载：新路径为 string（人类可读反馈），旧路径为 Dictionary。
        ///     TS routeToolResult 直接透传到 ToolResultRecord.result。
        /// </summary>
        [JsonPropertyName("result")]
        public object? Result { get; set; }

        /// <summary>
        ///     失败时的机器可读原因（success=false 时非 None）。
        ///     TS 端可据此分支写入不同语义的失败记忆。成功时为 None，序列化为 "none"。
        /// </summary>
        [JsonPropertyName("reason")]
        public ActionResultReason Reason { get; set; } = ActionResultReason.None;
    }

    /// <summary>
    ///     导演请求 C# 分配某 NPC 为 Agent（TS→C#, fire_and_forget）。
    ///     设计文档 §4.2.2。C# ForceAllocate 并设置 keepUntil 豁免互动空闲淘汰。
    /// </summary>
    public class AllocateAgentMessage
    {
        [JsonPropertyName("type")] public string Type { get; set; } = MessageTypeAllocateAgent;
        [JsonPropertyName("requestId")] public string RequestId { get; set; } = "";
        [JsonPropertyName("npcName")] public string NpcName { get; set; } = "";

        /// <summary>ISO 8601 豁免截止时间，对应 beat.windowEnd。可空。</summary>
        [JsonPropertyName("keepUntilIso")]
        public string? KeepUntilIso { get; set; }
    }

    /// <summary>
    ///     阶段 3 Director 工具命令（TS→C#）。工具名 + 参数 + requestId。
    ///     消息格式：{ type: "director_command", tool, args, requestId }（spec §1.1）。
    ///     npc 在 args 内（如 {"npc": "Shane", ...}），顶层不重复。
    /// </summary>
    public class DirectorCommandMessage
    {
        [JsonPropertyName("type")] public string Type { get; set; } = MessageTypeDirectorCommand;

        /// <summary>请求 id（协议纪律：action_result 回带相同 id）。</summary>
        [JsonPropertyName("requestId")]
        public string RequestId { get; set; } = "";

        /// <summary>Director 工具名（set_npc_position / set_npc_mood / spawn_beat / ...）。</summary>
        [JsonPropertyName("tool")]
        public string Tool { get; set; } = "";

        /// <summary>工具参数（值可能是 string / number / array，经 System.Text.Json 反序列化为 JsonElement）。</summary>
        [JsonPropertyName("args")]
        public Dictionary<string, object> Args { get; set; } = new();

        /// <summary>可选归属 NPC 名（仅日志用，工具实际以 args.npc 为准）。</summary>
        [JsonPropertyName("npcName")]
        public string? NpcName { get; set; }
    }

    /// <summary>
    ///     C# 通知 TS 新的一天开始（C#→TS, fire_and_forget）。
    ///     设计文档 §4.2.2。TS 触发导演 morningPlan（10% 概率）。
    /// </summary>
    public class DayStartedMessage
    {
        [JsonPropertyName("type")] public string Type { get; set; } = MessageTypeDayStarted;
        [JsonPropertyName("requestId")] public string RequestId { get; set; } = "";

        /// <summary>游戏日期 ISO key（如 "Y1_spring_1"）。</summary>
        [JsonPropertyName("dateIso")]
        public string DateIso { get; set; } = "";

        /// <summary>阶段 3: DirectorContextBuilder 拼装的导演上下文（压缩结构化文本，800-1500 token 预算）。可空向后兼容。</summary>
        [JsonPropertyName("directorContext")]
        public string? DirectorContext { get; set; }
    }

    /// <summary>
    ///     C# 推送游戏世界上下文快照给 TS GameContextManager（C#→TS, fire_and_forget）。
    ///     Context 为字典形式（System.Text.Json 序列化为 GameContext JSON），
    ///     结构对应 TS narrative-types.ts 的 GameContext 接口。
    /// </summary>
    public class GameContextSyncMessage
    {
        [JsonPropertyName("type")] public string Type { get; set; } = MessageTypeGameContextSync;
        [JsonPropertyName("requestId")] public string RequestId { get; set; } = "";

        /// <summary>GameContext 对象（time/progress/seasonalResources/npcStates/playerState）。</summary>
        [JsonPropertyName("context")]
        public Dictionary<string, object> Context { get; set; } = new();
    }

    /// <summary>
    ///     原子批经济指令中的单个操作（TS→C#）。
    ///     kind: "money"（钱包增减，amount 带符号：正=收入，负=支出）| "item"（物品转移，itemId/quantity）。
    ///     target: "player"（Game1.player 实时物理校验）| "npc"（AgentInventory 执行镜像）。
    /// </summary>
    public class AdjustOp
    {
        [JsonPropertyName("kind")] public string Kind { get; set; } = "";

        [JsonPropertyName("target")] public string Target { get; set; } = "";

        /// <summary>钱包操作金额（带符号，正=收入，负=支出）。</summary>
        [JsonPropertyName("amount")] public int? Amount { get; set; }

        /// <summary>物品操作 ID（qualified item id 或 ItemId）。</summary>
        [JsonPropertyName("itemId")] public string? ItemId { get; set; }

        /// <summary>物品显示名（解析 fallback，仅日志/回执详情）。</summary>
        [JsonPropertyName("itemName")] public string? ItemName { get; set; }

        /// <summary>物品操作数量。</summary>
        [JsonPropertyName("quantity")] public int? Quantity { get; set; }

        /// <summary>变更原因（留痕用，进 OnWalletChanged / transcript）。</summary>
        [JsonPropertyName("reason")] public string? Reason { get; set; }
    }

    /// <summary>
    ///     原子批经济指令（TS→C#）。2026-08-15 账本迁移设计 §4.1。
    ///     C# adjust 执行器物理校验 + 原子执行（全量预校验 → 按序提交 → 失败回滚已提交项），回 adjust_result。
    ///     instructionId 为幂等键：已执行过的指令直接返回缓存回执，不重复执行。
    /// </summary>
    public class ExecuteAdjustMessage
    {
        [JsonPropertyName("type")] public string Type { get; set; } = MessageTypeExecuteAdjust;
        [JsonPropertyName("requestId")] public string RequestId { get; set; } = "";

        /// <summary>幂等键（TS 生成），重复指令直接返回缓存回执。</summary>
        [JsonPropertyName("instructionId")]
        public string InstructionId { get; set; } = "";

        [JsonPropertyName("npcName")] public string NpcName { get; set; } = "";
        [JsonPropertyName("ops")] public List<AdjustOp> Ops { get; set; } = new();

        /// <summary>发起玩家 ID（UniqueMultiplayerID 字符串，2026-08-16 联机）。缺省/null → Game1.player。</summary>
        [JsonPropertyName("playerId")]
        public string? PlayerId { get; set; }
    }

    /// <summary>
    ///     原子批指令单步执行结果（C#→TS）。
    /// </summary>
    public class AdjustStepResult
    {
        [JsonPropertyName("index")] public int Index { get; set; }
        [JsonPropertyName("kind")] public string Kind { get; set; } = "";
        [JsonPropertyName("target")] public string Target { get; set; } = "";
        [JsonPropertyName("success")] public bool Success { get; set; }

        /// <summary>失败时该步的机器可读失败码（成功为 none）。</summary>
        [JsonPropertyName("failureCode")]
        public AdjustFailureCode FailureCode { get; set; } = AdjustFailureCode.None;

        /// <summary>人类可读详情（日志用）。</summary>
        [JsonPropertyName("detail")] public string? Detail { get; set; }

        /// <summary>
        ///     item 步骤实际执行的 QualifiedItemId（2026-08-17 键归一：C# 已按 ID/名称回落解析，
        ///     回执携带权威键供 TS 账本按正确键入账，消除显示名/ID 双键漂移）。非 item 步骤为 null。
        /// </summary>
        [JsonPropertyName("itemId")] public string? ItemId { get; set; }
    }

    /// <summary>
    ///     原子批经济指令回执（C#→TS）。2026-08-15 账本迁移设计 §4.1。
    ///     success=false 时 failureCode 为首个失败步的失败码；
    ///     成功时携带双方变更后余额（playerMoney/npcMoney），TS 据此推进账本 pending → committed。
    /// </summary>
    public class AdjustResultMessage
    {
        [JsonPropertyName("type")] public string Type { get; set; } = MessageTypeAdjustResult;
        [JsonPropertyName("requestId")] public string RequestId { get; set; } = "";

        /// <summary>幂等键（echo execute_adjust 的 instructionId）。</summary>
        [JsonPropertyName("instructionId")]
        public string InstructionId { get; set; } = "";

        [JsonPropertyName("npcName")] public string NpcName { get; set; } = "";
        [JsonPropertyName("success")] public bool Success { get; set; }
        [JsonPropertyName("steps")] public List<AdjustStepResult> Steps { get; set; } = new();

        /// <summary>整体失败码（success=false 时非 none）。</summary>
        [JsonPropertyName("failureCode")]
        public AdjustFailureCode? FailureCode { get; set; }

        /// <summary>执行后玩家余额（Game1.player.Money 镜像）。</summary>
        [JsonPropertyName("playerMoney")]
        public int? PlayerMoney { get; set; }

        /// <summary>执行后 NPC 余额（AgentInventory 镜像）。</summary>
        [JsonPropertyName("npcMoney")]
        public int? NpcMoney { get; set; }

        /// <summary>指令发起玩家 ID（echo execute_adjust.playerId，2026-08-16 联机）。可选。</summary>
        [JsonPropertyName("playerId")]
        public string? PlayerId { get; set; }
    }

    /// <summary>
    ///     重连对账消息（C#→TS，2026-08-15 步骤 4）。断线重连成功后发送：
    ///     outbox 补发已完成（replayedOutbox 条），附 active agent 名单供 TS 对账
    ///     （in-flight adjust pending 凭 instructionId 重发，C# 幂等返回缓存回执）。
    /// </summary>
    public class ReconnectSyncMessage
    {
        [JsonPropertyName("type")] public string Type { get; set; } = MessageTypeReconnectSync;
        [JsonPropertyName("requestId")] public string RequestId { get; set; } = "";

        /// <summary>本次重连补发的 outbox 消息条数。</summary>
        [JsonPropertyName("replayedOutbox")]
        public int ReplayedOutbox { get; set; }

        /// <summary>当前 active agent 名单（TS 对账遍历范围）。</summary>
        [JsonPropertyName("agents")] public List<string> Agents { get; set; } = new();

        /// <summary>游戏日期（Y{year}_{season}_{day}，TS 侧可感知断线跨度）。</summary>
        [JsonPropertyName("gameDate")] public string? GameDate { get; set; }
    }

    /// <summary>
    ///     TS Agent Server tool-calling format: an action function name with parameters.
    ///     Mirrors the OpenAI-style tool call schema produced by the agent loop in
    ///     @valley/stardew (stardew-tools.ts buildStardewTools).
    /// </summary>
    public class ToolCall
    {
        /// <summary>Function name to invoke (e.g. "harvest", "mine", "water").</summary>
        [JsonPropertyName("action")]
        public string Action { get; set; } = "";

        /// <summary>Named parameters for the function call (x, y, itemId, etc.).</summary>
        [JsonPropertyName("parameters")]
        public Dictionary<string, object> Parameters { get; set; } = new();

        /// <summary>Human-readable reason why this tool was chosen.</summary>
        [JsonPropertyName("reason")]
        public string Reason { get; set; } = "";

        /// <summary>Unique call ID from the LLM for correlation.</summary>
        [JsonPropertyName("callId")]
        public string CallId { get; set; } = "";
    }

    /// <summary>
    ///     Wraps one or more tool calls from the TS Agent Server.
    /// </summary>
}