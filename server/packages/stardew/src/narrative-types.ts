// 游戏上下文与玩家画像类型（原"叙事导演类型"，2026-09-14 旧叙事 Director 砍除后
// 仅保留画像数据层与活链路类型；beat/wire 死类型已删，详见
// docs/plan/2026-09-12-architecture-drift-audit.md 建议 #1 裁决）
// 通过 src/types.ts 的 `export * from "./narrative-types"` 重导出给外部消费者。

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

export interface GameContextSyncMessage {
  type: "game_context_sync";
  requestId: string;
  context: GameContext;
}
