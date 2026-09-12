// Type compilation tests for narrative director types (Task 1).
// Verifies every newly-added interface/type can be constructed as an object literal.
// Run: bun test tests/types-extended.test.ts

import { test, expect } from "bun:test";
import type {
  Beat,
  BeatStatus,
  ReActStep,
  PlayStyleTag,
  PlayStyle,
  ActivityRank,
  LocationRank,
  DailyActivity,
  Interaction,
  GiftRecord,
  BeatHistoryEntry,
  PlayerProfile,
  NpcStateSnapshot,
  InventorySlot,
  GameContext,
  GameContextSyncMessage,
} from "../src/types";

// Helper for compile-time type checks.
function expectType<T>(value: T): T {
  return value;
}

test("BeatStatus union accepts all 5 states", () => {
  const scheduled: BeatStatus = "scheduled";
  const active: BeatStatus = "active";
  const completed: BeatStatus = "completed";
  const skipped: BeatStatus = "skipped";
  const failed: BeatStatus = "failed";
  expect([scheduled, active, completed, skipped, failed]).toHaveLength(5);
});

test("ReActStep interface compiles", () => {
  const step: ReActStep = {
    stepIndex: 0,
    thought: "玩家在钓鱼，我走过去打招呼",
    toolCall: { tool: "move_to", args: { x: 32, y: 18 }, callId: "c1" },
    toolResult: { success: true, result: "已到达" },
    timestamp: "2026-07-21T14:05:00Z",
    tokensUsed: 320,
  };
  expectType<ReActStep>(step);
  expect(step.stepIndex).toBe(0);
});

test("Beat interface compiles", () => {
  const beat: Beat = {
    id: "beat-001",
    npcName: "Willy",
    triggerTime: "14:00",
    windowEnd: "16:00",
    directive: "下午去农场表达对玩家劳作的关心",
    context: {
      reasonGenerated: "玩家连续3天钓鱼，Willy 是钓鱼 NPC",
      playerProfileSnapshot: null as unknown as PlayerProfile,
      gameContextSnapshot: null as unknown as GameContext,
      recentBeats: [],
    },
    status: "scheduled",
    reactSteps: [],
  };
  expectType<Beat>(beat);
  expect(beat.npcName).toBe("Willy");
});

test("PlayStyleTag union accepts all 7 tags", () => {
  const tags: PlayStyleTag[] = [
    "brewer",
    "farmer",
    "rancher",
    "miner",
    "warrior",
    "forager",
    "socializer",
  ];
  expect(tags).toHaveLength(7);
});

test("PlayStyle + ActivityRank + LocationRank compile", () => {
  const playStyle: PlayStyle = {
    tag: "brewer",
    confidence: 0.8,
    evidence: "检测到 12 个酒桶",
  };
  const activityRank: ActivityRank = {
    activity: "fishing",
    rank: 1,
    share: 0.45,
    evidence: "近7天累计钓鱼 180 分钟",
  };
  const locationRank: LocationRank = {
    location: "Beach",
    rank: 1,
    visitCount: 14,
    share: 0.62,
  };
  expectType<PlayStyle>(playStyle);
  expectType<ActivityRank>(activityRank);
  expectType<LocationRank>(locationRank);
  expect(playStyle.tag).toBe("brewer");
});

test("DailyActivity + GiftRecord + Interaction compile", () => {
  const gift: GiftRecord = { to: "Willy", itemId: "Ooceanfish_6" };
  const daily: DailyActivity = {
    date: "2026-07-21",
    fishingMinutes: 90,
    farmingMinutes: 60,
    miningMinutes: 0,
    foragingMinutes: 15,
    socialMinutes: 30,
    combatMinutes: 0,
    locationsVisited: ["Beach", "Farm"],
    fishCaught: 12,
    cropsHarvested: 0,
    itemsShipped: 5,
    itemsForaged: 0,
    monstersKilled: 0,
    npcsTalkedTo: ["Willy"],
    giftsGiven: [gift],
  };
  const interaction: Interaction = {
    date: "2026-07-21",
    type: "dialogue",
    summary: "Willy 提醒明天有风暴",
    emotionTag: "friendly",
  };
  expectType<DailyActivity>(daily);
  expectType<GiftRecord>(gift);
  expectType<Interaction>(interaction);
  expect(daily.fishCaught).toBe(12);
});

test("BeatHistoryEntry interface compiles", () => {
  const entry: BeatHistoryEntry = {
    beatId: "beat-001",
    date: "2026-07-21",
    npcName: "Willy",
    directive: "下午去农场表达对玩家劳作的关心",
    outcome: "completed",
    playerReaction: "玩家主动询问钓鱼技巧",
  };
  expectType<BeatHistoryEntry>(entry);
  expect(entry.outcome).toBe("completed");
});

test("PlayerProfile five layers compile", () => {
  const profile: PlayerProfile = {
    static: {
      farmerName: "Alice",
      gender: "female",
      farmName: "Riverland",
      farmType: "Riverland",
      startDate: "2026-07-01",
      lastUpdated: "2026-07-21T06:00:00Z",
    },
    behavior: {
      dailyActivities: [],
      totalStats: {
        fishCaught: 0,
        itemsShipped: 0,
        monstersKilled: 0,
        cropsHarvested: 0,
        itemsForaged: 0,
        giftsGiven: 0,
        dialoguesHad: 0,
        miningLevelsDescended: 0,
      },
    },
    preferences: {
      playStyle: [],
      topActivities: [],
      topLocations: [],
      routinePattern: "",
      lastUpdated: "2026-07-21T06:00:00Z",
    },
    relationships: {},
    personality: {
      traits: [],
      archetype: "",
      narrativeRole: "",
      lastUpdated: "2026-07-21T06:00:00Z",
    },
    story: {
      completedBeats: [],
      recurringTropes: [],
      lastUpdated: "2026-07-21T06:00:00Z",
    },
  };
  expectType<PlayerProfile>(profile);
  expect(profile.static.farmerName).toBe("Alice");
});

test("NpcStateSnapshot + InventorySlot + GameContext compile", () => {
  const npc: NpcStateSnapshot = {
    name: "Willy",
    location: "Beach",
    tile: { x: 12, y: 8 },
    isAvailable: true,
    currentState: "IDLE",
    friendshipPoints: 850,
  };
  const slot: InventorySlot = { name: "Corn", quantity: 12 };
  const ctx: GameContext = {
    time: {
      year: 2,
      season: "summer",
      day: 14,
      dayOfWeek: "Tuesday",
      weather: "sunny",
      isFestivalDay: false,
    },
    progress: {
      communityCenterComplete: false,
      communityCenterBundlesDone: ["Pantry/Berry"],
      jojaMartRoute: false,
      islandsUnlocked: [],
      desertUnlocked: true,
      railroadUnlocked: false,
      sewersUnlocked: false,
      greenhouseRestored: false,
    },
    seasonalResources: {
      plantableCrops: ["Corn", "Sunflower", "Pepper"],
      catchableFish: ["RainbowTrout", "RedSnapper"],
      forageItems: ["SpiceBerry"],
      activeFestivals: [],
    },
    npcStates: [npc],
    playerState: {
      location: "Farm",
      tile: { x: 32, y: 18 },
      health: 95,
      maxHealth: 100,
      energy: 270,
      maxEnergy: 300,
      money: 8500,
      inventory: [slot],
    },
    lastUpdated: "2026-07-21T14:00:00Z",
  };
  expectType<NpcStateSnapshot>(npc);
  expectType<InventorySlot>(slot);
  expectType<GameContext>(ctx);
  expect(ctx.time.season).toBe("summer");
});

test("GameContextSyncMessage compiles (dead wire types removed 2026-09-12)", () => {
  const ctxSync: GameContextSyncMessage = {
    type: "game_context_sync",
    requestId: "r3",
    context: null as unknown as GameContext,
  };
  expectType<GameContextSyncMessage>(ctxSync);
  expect(ctxSync.type).toBe("game_context_sync");
});
