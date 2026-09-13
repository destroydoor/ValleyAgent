// 叙事导演引擎类型 (Narrative Director types)
// 详见 spec: docs/superpowers/specs/2026-07-21-narrative-director-design.md
// 通过 src/types.ts 的 `export * from "./narrative-types"` 重导出给外部消费者。

export type BeatStatus = "scheduled" | "active" | "completed" | "skipped" | "failed";

export interface ReActStep {
  stepIndex: number;
  thought: string;
  toolCall?: { tool: string; args: Record<string, unknown>; callId?: string };
  toolResult?: { success: boolean; result?: string };
  timestamp: string;
  tokensUsed: number;
}

export type PlayStyleTag =
  | "brewer"
  | "farmer"
  | "rancher"
  | "miner"
  | "warrior"
  | "forager"
  | "socializer";

export interface PlayStyle {
  tag: PlayStyleTag;
  confidence: number; // 0.0~1.0
  evidence: string;
}

export interface ActivityRank {
  activity: string;
  rank: number;
  share: number; // 0.0~1.0
  evidence: string;
}

export interface LocationRank {
  location: string;
  rank: number;
  visitCount: number;
  share: number; // 0.0~1.0
}

export interface GiftRecord {
  to: string;
  itemId: string;
}

export interface DailyActivity {
  date: string;
  fishingMinutes: number;
  farmingMinutes: number;
  miningMinutes: number;
  foragingMinutes: number;
  socialMinutes: number;
  combatMinutes: number;
  locationsVisited: string[];
  fishCaught: number;
  cropsHarvested: number;
  itemsShipped: number;
  itemsForaged: number;
  monstersKilled: number;
  npcsTalkedTo: string[];
  giftsGiven: GiftRecord[];
}

export interface Interaction {
  date: string;
  type: "dialogue" | "gift" | "quest" | "combat_together";
  summary: string;
  emotionTag: string;
}

export interface BeatHistoryEntry {
  beatId: string;
  date: string;
  npcName: string;
  directive: string;
  outcome: "completed" | "skipped" | "failed";
  playerReaction?: string;
}

export interface NpcStateSnapshot {
  name: string;
  location: string;
  tile: { x: number; y: number };
  isAvailable: boolean;
  currentState: string;
  friendshipPoints: number;
}

export interface InventorySlot {
  name: string;
  quantity: number;
}

export interface GameContext {
  time: {
    year: number;
    season: "spring" | "summer" | "fall" | "winter";
    day: number;
    dayOfWeek: string;
    weather: "sunny" | "rainy" | "snowy" | "stormy";
    isFestivalDay: boolean;
    festivalName?: string;
  };
  progress: {
    communityCenterComplete: boolean;
    communityCenterBundlesDone: string[];
    jojaMartRoute: boolean;
    islandsUnlocked: string[];
    desertUnlocked: boolean;
    railroadUnlocked: boolean;
    sewersUnlocked: boolean;
    greenhouseRestored: boolean;
  };
  seasonalResources: {
    plantableCrops: string[];
    catchableFish: string[];
    forageItems: string[];
    activeFestivals: string[];
  };
  npcStates: NpcStateSnapshot[];
  playerState: {
    location: string;
    tile: { x: number; y: number };
    health: number;
    maxHealth: number;
    energy: number;
    maxEnergy: number;
    money: number;
    inventory: InventorySlot[];
  };
  lastUpdated: string;
}

export interface PlayerProfile {
  // 静态层 — 游戏开始时填充
  static: {
    farmerName: string;
    gender: "male" | "female" | "unknown";
    farmName: string;
    farmType: string;
    startDate: string;
    lastUpdated: string;
  };
  // 行为层 — 每日由 ActivityTracker 上报，TS 累积存储
  behavior: {
    dailyActivities: DailyActivity[]; // 最近 30 天
    totalStats: {
      fishCaught: number;
      itemsShipped: number;
      monstersKilled: number;
      cropsHarvested: number;
      itemsForaged: number;
      giftsGiven: number;
      dialoguesHad: number;
      miningLevelsDescended: number;
    };
  };
  // 偏好层 — 由行为层推导，每周刷新
  preferences: {
    playStyle: PlayStyle[];
    topActivities: ActivityRank[];
    topLocations: LocationRank[];
    routinePattern: string;
    lastUpdated: string;
  };
  // 关系层 — 每 NPC 独立，每日刷新
  relationships: {
    [npcName: string]: {
      phase: string;
      friendshipPoints: number;
      last5Interactions: Interaction[];
      giftHistory: GiftRecord[];
      notableEvents: string[];
      lastUpdated: string;
    };
  };
  // 性格层 — 导演 LLM 推断，每季节（28 天）刷新
  personality: {
    traits: string[];
    archetype: string;
    narrativeRole: string;
    lastUpdated: string;
  };
  // 故事层 — 已发生的 beat 历史
  story: {
    completedBeats: BeatHistoryEntry[];
    recurringTropes: string[];
    lastUpdated: string;
  };
}

export interface Beat {
  id: string; // UUID
  npcName: string; // 目标 NPC
  triggerTime: string; // "14:00" 当日触发
  windowEnd: string; // "16:00" 窗口结束
  directive: string; // 高层指令，自然语言
  context: {
    reasonGenerated: string;
    playerProfileSnapshot: PlayerProfile;
    gameContextSnapshot: GameContext;
    recentBeats: Beat[];
    /**
     * M3 多玩家化：这个 beat 是为哪个玩家编排的（缺省 = 单玩家/未归属）。
     * 旧存档与旧代码没有该字段，读取方一律按可选处理。
     */
    playerId?: string;
  };
  status: BeatStatus;
  reactSteps?: ReActStep[]; // 执行轨迹（调试用）
}

// ============================================================================
// 8 种 WebSocket 消息类型 (Narrative Director protocol extension)
// ============================================================================

export interface ActivityReportMessage {
  type: "activity_report";
  requestId: string;
  dailyActivity: DailyActivity;
  farmSnapshot: PlayStyle[];
}

export interface ActivityMilestoneMessage {
  type: "activity_milestone";
  requestId: string;
  milestone: {
    type: string;
    description: string;
    detectedAt: string;
  };
}

export interface GameContextSyncMessage {
  type: "game_context_sync";
  requestId: string;
  context: GameContext;
}

export interface BeatDirectiveMessage {
  type: "beat_directive";
  requestId: string;
  beat: Beat;
}

export interface BeatActivateMessage {
  type: "beat_activate";
  requestId: string;
  beatId: string;
  npcName: string;
}

export interface BeatEventMessage {
  type: "beat_event";
  requestId: string;
  beatId: string;
  eventType: string;
  payload: Record<string, unknown>;
}

export interface BeatStateMessage {
  type: "beat_state";
  requestId: string;
  beatId: string;
  status: BeatStatus;
  toolCall?: { tool: string; args: Record<string, unknown> };
}

export interface PlayerStateUpdateMessage {
  type: "player_state_update";
  requestId: string;
  playerState: GameContext["playerState"];
}