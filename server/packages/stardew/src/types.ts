// Stardew-side shared types (spec 2.2 data contracts)

/** memorySideEffect 字段固定值：表示记忆已记录。 */
export const MEMORY_SIDE_EFFECT_RECORDED = "recorded" as const;

/** 默认情绪值：用于 fallback/busy 响应。 */
export const DEFAULT_EMOTION = "Neutral";

export interface ToolAction {
  tool: string;                          // "speak" | "emote" | "give_item" | "give_gift" | "set_state" | "show_dialogue" | "remember" | "get_info" | "forget"
  args: Record<string, unknown>;
  // C3 反馈环：透传 LLM tool_call_id，C# 执行后按此 ID 回发 action_result。
  // 缺省空字符串兼容旧 C# 客户端（无 callId 时 C# 跳过回发）。
  callId?: string;
}

/**
 * 玩家手持物（Phase 1 交易用）。C# WorldSnapshotBuilder 从玩家手持物品构建，
 * 是 NPC 作为买家的交易标的（TradeDirection.NpcBuysPlayerItem）。
 */
export interface PlayerHeldItemInfo {
  itemId: string;                        // QualifiedItemId，如 "(O)388"
  name: string;                          // 物品显示名
  qty: number;                           // 手持数量
  marketPrice: number;                   // 公道价（C# 用 sellToStorePrice）
}

/**
 * NPC 当前执行的目标（Phase 2 set_goal）。C# GoalExecutor 后台执行时由
 * WorldSnapshotBuilder 周期性填充进度，NPC 对话时从 L2 摘要感知自己在做什么。
 */
export interface CurrentGoalInfo {
  type: string;                          // "chop_tree" | "mine" | "water_crops" | "fight" | "forage"
  params: Record<string, unknown>;       // 目标参数（如 {quantity: 10}）
  progress: string;                      // C# DescribeProgress() 的进度描述
  status: string;                        // GoalStatus："NotStarted"|"Executing"|"Complete"|"Failed"|"Cancelled"
}

export interface WorldSnapshot {
  season: string;                        // "summer"
  day: number;                           // 28
  time: string;                          // "14:30"
  weather: string;                       // "sunny"
  location: string;                      // "Town"
  npcTile: { x: number; y: number };
  nearbyObjects: string;                 // "2 villagers, Pierre's shop entrance"
  friendship: number;                    // 0-2500
  npcState: string;                      // "IDLE"
  inventory: Array<{ name: string; quantity: number }>;
  farmerName: string;
  // E4-1: 玩家钱包（缺钱/有钱 NPC 差异化接单判定的 LLM 输入）。可选，旧 C# 客户端不携带。
  playerMoney?: number;
  // E0-6: NPC 自己所在的地图名（location 是玩家地图，锚定 NPC 真实位置防幻觉）。
  // 可选，旧 C# 客户端不携带。
  npcLocation?: string;
  // E4-2: NPC 钱包余额。可选，旧 C# 客户端不携带。
  npcMoney?: number;
  // E4-2: NPC 真背包（inventory 字段实为玩家背包）。可选，旧 C# 客户端不携带。
  npcInventory?: Array<{ name: string; quantity: number }>;
  // Phase 1: 玩家手持物（NPC 买家视角的交易标的）。可选，旧 C# 客户端不携带。
  playerHeldItem?: PlayerHeldItemInfo | null;
  // Phase 2: NPC 当前执行目标（set_goal）。可选，旧 C# 客户端不携带。
  currentGoal?: CurrentGoalInfo | null;
  // Phase 3 L2: NPC 心情标签（Director set_npc_mood 写入）。可选，旧 C# 客户端不携带。
  npcMood?: string;
  // Phase 3 L2: NPC 近期事件（AgentBrain.TodayEvents）。可选，旧 C# 客户端不携带。
  npcRecentEvents?: string[];
  // Phase 3 L2: NPC 工作标记（Director set_npc_working_on 写入）。可选，旧 C# 客户端不携带。
  npcWorkingOn?: string | null;
  // Phase 3 L2: NPC 欠款。可选，旧 C# 客户端不携带。
  npcOwedMoney?: number;
}

export interface DialogueRequest {
  type: "dialogue";
  requestId: string;
  npcName: string;
  playerInput: string;
  worldSnapshot: WorldSnapshot;
  // 2026-08-16 联机：发起对话的玩家（UniqueMultiplayerID 的字符串形式）。
  // 可选——单机/旧客户端缺省，回落到 Game1.player。
  playerId?: string;
}

export interface DialogueResponse {
  type: "dialogue_response";
  requestId: string;
  npcName: string;
  speech: string;
  actions: ToolAction[];
  emotion: string;
  memorySideEffect?: "recorded";
  // C# 端据此用灰色系统提示渲染（"正在和别人交流"），不弹打字机对话框。
  fallback?: boolean;
  // 2026-08-16 联机：发起对话的玩家 ID 原样 echo，C# 端据此记录 LastDialoguePlayerId
  // （FOLLOW 等行为的目标玩家解析）。可选，旧客户端不携带。
  playerId?: string;
  // 方案 B（§4.1.1）：好感度评估并回 dialogue 主路径，消灭 friendship_eval 死管道。
  // LLM 通过 evaluate_friendship 工具输出 delta/reason，此处带回给 C# 消费。
  // 可选：LLM 未输出时默认 0（不阻塞对话），此时字段不携带。
  friendshipDelta?: number;
  friendshipReason?: string;
}

export interface HelloRequest {
  type: "hello";
  requestId: string;
  modVersion: string;
}

export interface HelloResponse {
  type: "hello";
  requestId: string;
  status: "ok";
  serverVersion: string;
}

export interface PingRequest {
  type: "ping";
  requestId: string;
}

export interface PongResponse {
  type: "pong";
  requestId: string;
}

export interface ActionResultMessage {
  type: "action_result";
  requestId: string;
  callId: string;
  success: boolean;
  result?: string;
  // C3 feedback routing: which NPC's action produced this result.
  // Optional for backward compat with older C# clients (silently dropped if missing).
  npcName?: string;
  tool?: string;
  // C# ActionResultReason 枚举（None/AgentMissing/TransitionBlocked/InvalidState/
  // TargetUnreachable/ItemNotFound/InventoryFull/LocationInvalid/InternalError）
  // 经 JsonStringNamingPolicy.CamelCase 序列化为 "itemNotFound" 等 camelCase 字符串。
  // 可选，向后兼容旧 C# 客户端（未携带时按原格式渲染）。
  reason?: string;
}

export interface StateChangedMessage {
  type: "state_changed";
  npcName: string;
  previousState: string;
  newState: string;
  wasForced: boolean;
  previousStateDurationMs: number;
  // 自由字符串原因（如 "travel_failed"/"evicted"/"task_completed"/"llm_decision"/"manual"）。
  // 可选，向后兼容旧 C# 客户端（未携带时为空串）。
  reason?: string;
}

export interface ConsolidateDayMessage {
  type: "consolidate_day";
  npcName: string;
  dateIso: string;
  // 可选，向后兼容未携带 requestId 的旧 C# 客户端。
  requestId?: string;
}

/** C# DayStarted 时通知 TS 新的一天开始。 */
export interface DayStartedMessage {
  type: "day_started";
  requestId: string;
  dateIso: string;
  /** 阶段 3: DirectorContextBuilder 拼装的导演上下文（压缩结构化文本，800-1500 token 预算）。可空向后兼容；当前 morningPlan 走 game_context_sync 结构化通道，此字段供工具型 Director 消费。 */
  directorContext?: string;
}

/** 导演请求 C# 分配某 NPC 为 Agent。 */
export interface AllocateAgentMessage {
  type: "allocate_agent";
  requestId: string;
  npcName: string;
  keepUntilIso?: string;
}

/**
 * E5-2 喊话歧义兜底路由请求。C# 4 层确定性路由（名字提及 → 当前会话 → 跟随/雇佣者 →
 * 已醒来+关系最近）全空时发送，TS 用轻量路由 LLM 选目标（最多 1 次调用），
 * LLM 断线时由确定性兜底（候选里最先醒着的）返回。
 */
export interface RouteShoutMessage {
  type: "route_shout";
  npcName: string;                       // 喊话的 NPC
  playerShout: string;                   // 喊话内容
  candidates: Array<{
    name: string;
    awake: boolean;
    friendship: number;                  // 0-2500，选"关系最近"的依据
    location: string;                    // 当前所在场景名（"远方"=不在同场景）
  }>;
  requestId?: string;
}

export interface RouteShoutResponse {
  type: "route_shout_response";
  requestId: string;
  targetName: string | null;             // null = 无人可听（全员未醒/无候选）
  reason: string;                        // 机器可读选择原因
}

/**
 * Phase 3 Director 工具调用（TS→C#）。9 个 Director 工具（set_npc_position/
 * set_npc_inventory/set_npc_money/set_npc_mood/set_npc_recent_events/
 * set_npc_working_on/spawn_beat/spawn_group_beat/inject_memory）统一走此通道：
 * TS 端 Director agent 的工具调用 → routeMessage → sendToCsharp → C# DirectorTools.Execute。
 * C# 侧 CommandExecutor 对 type=director_command 特殊路由（不走 NPC Agent switch）。
 */
export interface DirectorCommandMessage {
  type: "director_command";
  tool: string;                          // 9 个 Director 工具名之一
  args: Record<string, unknown>;         // 工具参数（工具相关，如 {npc, location, tile}）
  requestId: string;                     // 请求 ID，C# 回执可回带
}

// ── 2026-08-15 账本迁移（步骤 1）：execute_adjust / adjust_result ──

/**
 * 原子批经济指令中的单个操作（TS→C#）。kind: "money"（钱包增减，amount 带符号）|
 * "item"（物品转移，itemId/quantity 带符号）；target: "player"（C# 对 Game1.player 实时
 * 物理校验）| "npc"（AgentInventory 执行镜像）。业务校验在 TS 账本（权威），C# 只做物理校验。
 */
export interface AdjustOp {
  kind: "money" | "item";
  target: "player" | "npc";
  /** 钱包操作金额（带符号，正=收入，负=支出）。 */
  amount?: number;
  /** 物品操作 ID（qualified item id 或 ItemId）。 */
  itemId?: string;
  /** 物品显示名（解析 fallback，仅日志/回执详情）。 */
  itemName?: string;
  /** 物品操作数量（带符号，正=增加，负=扣除）。 */
  quantity?: number;
  /** 变更原因（留痕用）。 */
  reason?: string;
}

/**
 * C# AdjustFailureCode 枚举的 camelCase 序列化值（ProtocolV2.cs 经
 * JsonStringNamingPolicy.CamelCase 输出，与 C# 侧枚举一一对应）。
 */
export type AdjustFailureCode =
  | "none"
  | "insufficientFunds"
  | "inventoryFull"
  | "itemNotFound"
  | "agentMissing"
  | "invalidOp"
  | "internalError"
  // 2026-08-16 联机：playerId 解析不到对应 Farmer（已退出/不存在）。
  | "playerNotFound";

/** 原子批经济指令（TS→C#）。C# adjust 执行器物理校验 + 原子执行，回 adjust_result。 */
export interface ExecuteAdjustMessage {
  type: "execute_adjust";
  requestId: string;
  /** 幂等键（TS 生成）。C# 缓存已执行结果，重复指令直接返回缓存回执（设计 §6）。 */
  instructionId: string;
  npcName: string;
  ops: AdjustOp[];
  /** 2026-08-16 联机：发起玩家（UniqueMultiplayerID 字符串）。可选，缺省回落 Game1.player。 */
  playerId?: string;
}

/** 原子批指令单步执行结果（C#→TS）。 */
export interface AdjustStepResult {
  index: number;
  kind: string;
  target: string;
  success: boolean;
  /** 失败时该步的机器可读失败码（成功为 none）。 */
  failureCode: AdjustFailureCode;
  detail?: string;
  /**
   * item 步骤实际执行的 QualifiedItemId（2026-08-17 键归一：C# 已按 ID/名称回落解析，
   * 回执携带权威键供账本按正确键入账，消除显示名/ID 双键漂移）。非 item 步骤缺失。
   */
  itemId?: string;
}

/** 原子批经济指令回执（C#→TS）。success=false 时 failureCode 为首个失败步的失败码。 */
export interface AdjustResultMessage {
  type: "adjust_result";
  requestId: string;
  /** 幂等键（echo execute_adjust 的 instructionId）。 */
  instructionId: string;
  npcName: string;
  success: boolean;
  steps: AdjustStepResult[];
  failureCode?: AdjustFailureCode;
  /** 执行后玩家余额（Game1.player.Money 镜像）。 */
  playerMoney?: number;
  /** 执行后 NPC 余额（AgentInventory 镜像）。 */
  npcMoney?: number;
  /** 2026-08-16 联机：指令发起玩家（echo execute_adjust.playerId）。可选，旧客户端不携带。 */
  playerId?: string;
}

/**
 * 经济工具同步执行器（2026-08-15 账本迁移步骤 2）。
 * ProtocolAdapter 注入到 StardewAgent → ToolContext：trade/give_item/give_gift/
 * receive_payment 在 ReAct 循环内同步编排（账本校验→pending→execute_adjust→await 回执），
 * 不再发 action 给 C# 执行。getMoney/getInventory 供 get_info 读权威账本（null=未播种）。
 */
export interface EconomyExecutor {
  npcName: string;
  adjust: (ops: AdjustOp[]) => Promise<AdjustResultMessage>;
  getMoney: () => number | null;
  getInventory: () => Array<{ name: string; quantity: number }> | null;
}

/**
 * 重连对账消息（C#→TS，2026-08-15 步骤 4）。C# 断线重连成功补发 outbox 后发送；
 * TS 据此对账 in-flight adjust pending（凭 instructionId 重发，C# 幂等返回缓存回执）。
 */
export interface ReconnectSyncMessage {
  type: "reconnect_sync";
  requestId: string;
  /** 本次重连补发的 outbox 消息条数。 */
  replayedOutbox: number;
  /** 当前 active agent 名单（对账遍历范围）。 */
  agents: string[];
  /** 游戏日期（Y{year}_{season}_{day}）。 */
  gameDate?: string;
}

export type IncomingMessage =
  | DialogueRequest
  | HelloRequest
  | PingRequest
  | ActionResultMessage
  | StateChangedMessage
  | ConsolidateDayMessage
  | DayStartedMessage
  | RouteShoutMessage
  | DirectorCommandMessage
  | AdjustResultMessage
  | ReconnectSyncMessage;
export type OutgoingMessage = DialogueResponse | HelloResponse | PongResponse | RouteShoutResponse | AllocateAgentMessage | DirectorCommandMessage | ExecuteAdjustMessage | { type: "ack"; requestId: string };

// SceneState — TS 内部表示，由 WorldSnapshotDecoder 转换得到
export interface SceneState {
  season: string;
  day: number;
  timeStr: string;
  weather: string;
  location: string;
  npcTile: { x: number; y: number };
  nearbyObjects: string;
  farmerName: string;
  friendship: number;
  npcState: string;
  inventory: Array<{ name: string; quantity: number }>;
  // E4-1: 玩家钱包（缺钱/有钱 NPC 差异化接单判定）。旧 C# 客户端不携带时为 null。
  playerMoney: number | null;
  // E0-6: NPC 自己所在的地图名（location 是玩家地图）。旧 C# 客户端不携带时为 null。
  npcLocation: string | null;
  // E4-2: NPC 钱包余额。旧 C# 客户端不携带时为 null。
  npcMoney: number | null;
  // E4-2: NPC 真背包（inventory 字段实为玩家背包）。null=未知（旧客户端不携带），
  // 空数组=真空包——两者必须区分，避免把"未知"当"空包"呈现给 LLM。
  npcInventory: Array<{ name: string; quantity: number }> | null;
  // Phase 1: 玩家手持物（NPC 买家视角的交易标的）。null=未知（旧客户端不携带）。
  playerHeldItem: PlayerHeldItemInfo | null;
  // Phase 2: NPC 当前执行目标。null=未在执行目标（旧客户端不携带或已取消）。
  currentGoal: CurrentGoalInfo | null;
  // Phase 3 L2: NPC 心情标签。null=未知（旧客户端不携带），prompt 显示"平静"。
  npcMood: string | null;
  // Phase 3 L2: NPC 近期事件。null=未知（旧客户端不携带）；空数组=无近期事件。
  npcRecentEvents: string[] | null;
  // Phase 3 L2: NPC 工作标记。null=无工作标记。
  npcWorkingOn: string | null;
  // Phase 3 L2: NPC 欠款。null=无欠款。
  npcOwedMoney: number | null;
}

// Re-export narrative director types for unified import surface.
export * from "./narrative-types";
