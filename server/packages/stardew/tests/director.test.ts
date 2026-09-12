// Tests for Director morningPlan + milestoneReact (Task 8).
// Run: bun test tests/director.test.ts

import { test, expect, beforeEach, afterEach, spyOn } from "bun:test";
import { Director } from "../src/director";
import { BeatStore } from "../src/beat-store";
import { PlayerProfileStore } from "../src/player-profile-store";
import { PlayerProfileManager } from "../src/player-profile";
import { ActivityLogStore } from "../src/activity-log-store";
import { GameContextManager } from "../src/game-context";
import { rmSync, existsSync } from "node:fs";
import { join } from "node:path";
import { tmpdir } from "node:os";
import type { GameContext, PlayerProfile, Beat } from "../src/types";

// ---------------------------------------------------------------------------
// Factory helpers
// ---------------------------------------------------------------------------

function makeGameContext(overrides: Partial<GameContext> = {}): GameContext {
  return {
    time: {
      year: 2,
      season: "summer",
      day: 14,
      dayOfWeek: "Tuesday",
      weather: "sunny",
      isFestivalDay: false,
      ...overrides.time,
    },
    progress: {
      communityCenterComplete: false,
      communityCenterBundlesDone: ["Pantry"],
      jojaMartRoute: false,
      islandsUnlocked: [],
      desertUnlocked: true,
      railroadUnlocked: false,
      sewersUnlocked: false,
      greenhouseRestored: true,
      ...overrides.progress,
    },
    seasonalResources: {
      plantableCrops: ["Corn"],
      catchableFish: ["Rainbow Trout"],
      forageItems: ["Spice Berry"],
      activeFestivals: [],
      ...overrides.seasonalResources,
    },
    npcStates: [
      { name: "Willy", location: "Beach", tile: { x: 5, y: 8 }, isAvailable: true, currentState: "IDLE", friendshipPoints: 850 },
      { name: "Abigail", location: "Town", tile: { x: 30, y: 20 }, isAvailable: true, currentState: "IDLE", friendshipPoints: 500 },
      { name: "Sebastian", location: "Mountain", tile: { x: 50, y: 10 }, isAvailable: true, currentState: "IDLE", friendshipPoints: 200 },
    ],
    playerState: {
      location: "Farm",
      tile: { x: 32, y: 18 },
      health: 95,
      maxHealth: 100,
      energy: 200,
      maxEnergy: 270,
      money: 8500,
      inventory: [{ name: "Corn", quantity: 12 }],
    },
    lastUpdated: "2026-07-21T06:00:00Z",
    ...overrides,
  };
}

function makeStaticProfile(): PlayerProfile {
  return {
    static: {
      farmerName: "Alice",
      gender: "female",
      farmName: "Riverland",
      farmType: "Riverland",
      startDate: "2026-06-01",
      lastUpdated: "2026-07-21",
    },
    behavior: {
      dailyActivities: [],
      totalStats: {
        fishCaught: 47,
        itemsShipped: 25,
        monstersKilled: 3,
        cropsHarvested: 80,
        itemsForaged: 15,
        giftsGiven: 8,
        dialoguesHad: 32,
        miningLevelsDescended: 5,
      },
    },
    preferences: {
      playStyle: [{ tag: "brewer", confidence: 0.8, evidence: "12 个酒桶" }],
      topActivities: [],
      topLocations: [],
      routinePattern: "早晨种地下午钓鱼",
      lastUpdated: "2026-07-21",
    },
    relationships: {
      Willy: {
        phase: "friend",
        friendshipPoints: 850,
        last5Interactions: [],
        giftHistory: [],
        notableEvents: [],
        lastUpdated: "2026-07-21",
      },
    },
    personality: {
      traits: ["内向"],
      archetype: "独行者",
      narrativeRole: "不情愿的农场主",
      lastUpdated: "2026-07-21",
    },
    story: {
      completedBeats: [],
      recurringTropes: [],
      lastUpdated: "2026-07-21",
    },
  };
}

interface LlmCall {
  prompt: string;
}

function makeLlm(responseText: string, calls: LlmCall[]) {
  return async (prompt: string) => {
    calls.push({ prompt });
    return {
      text: responseText,
      usage: { promptTokens: 100, completionTokens: 50 },
    };
  };
}

// ---------------------------------------------------------------------------
// Setup
// ---------------------------------------------------------------------------

let beatDbPath: string;
let profileDbPath: string;
let activityDbPath: string;
let beatStore: BeatStore;
let profileStore: PlayerProfileStore;
let activityStore: ActivityLogStore;
let profileMgr: PlayerProfileManager;
let gameCtxMgr: GameContextManager;

beforeEach(() => {
  beatDbPath = join(tmpdir(), `director-beat-${Date.now()}-${Math.random().toString(36).slice(2)}.db`);
  profileDbPath = join(tmpdir(), `director-profile-${Date.now()}-${Math.random().toString(36).slice(2)}.db`);
  activityDbPath = join(tmpdir(), `director-activity-${Date.now()}-${Math.random().toString(36).slice(2)}.db`);
  beatStore = new BeatStore(beatDbPath);
  profileStore = new PlayerProfileStore(profileDbPath);
  activityStore = new ActivityLogStore(activityDbPath);
  beatStore.init();
  profileStore.init();
  activityStore.init();
  profileMgr = new PlayerProfileManager(profileStore, activityStore, {
    callLlm: async () => ({ text: "", usage: { promptTokens: 0, completionTokens: 0 } }),
  });
  profileMgr.initProfile(makeStaticProfile().static);
  gameCtxMgr = new GameContextManager();
});

afterEach(() => {
  beatStore.close();
  profileStore.close();
  activityStore.close();
  for (const p of [beatDbPath, profileDbPath, activityDbPath]) {
    if (existsSync(p)) rmSync(p, { force: true });
  }
});

// ---------------------------------------------------------------------------
// morningPlan tests
// ---------------------------------------------------------------------------

test("morningPlan: returns beats from LLM JSON array response", async () => {
  gameCtxMgr.update(makeGameContext());
  const calls: LlmCall[] = [];
  const director = new Director(beatStore, profileMgr, gameCtxMgr, activityStore, {
    callLlm: makeLlm(
      JSON.stringify([
        {
          npcName: "Willy",
          triggerTime: "14:00",
          windowEnd: "16:00",
          directive: "去海边看看玩家钓鱼",
          reasonGenerated: "玩家最近常钓鱼",
        },
      ]),
      calls,
    ),
  });

  const beats = await director.morningPlan();
  expect(beats).toHaveLength(1);
  expect(beats[0]!.npcName).toBe("Willy");
  expect(beats[0]!.triggerTime).toBe("14:00");
  expect(beats[0]!.windowEnd).toBe("16:00");
  expect(beats[0]!.status).toBe("scheduled");
  // LLM was called once with prompt
  expect(calls).toHaveLength(1);
  // Beat was persisted to BeatStore
  const stored = beatStore.getById(beats[0]!.id);
  expect(stored).not.toBeNull();
});

test("morningPlan: returns empty array when LLM outputs empty array", async () => {
  gameCtxMgr.update(makeGameContext());
  const director = new Director(beatStore, profileMgr, gameCtxMgr, activityStore, {
    callLlm: makeLlm("[]", []),
  });
  const beats = await director.morningPlan();
  expect(beats).toEqual([]);
});

test("morningPlan: skips entirely when festival day", async () => {
  gameCtxMgr.update(makeGameContext({ time: { ...makeGameContext().time, isFestivalDay: true, festivalName: "Luau" } }));
  const calls: LlmCall[] = [];
  const director = new Director(beatStore, profileMgr, gameCtxMgr, activityStore, {
    callLlm: makeLlm("[{\"npcName\":\"Willy\",\"triggerTime\":\"10:00\",\"windowEnd\":\"12:00\",\"directive\":\"x\",\"reasonGenerated\":\"y\"}]", calls),
  });
  const beats = await director.morningPlan();
  expect(beats).toEqual([]);
  // LLM should NOT be called on festival day
  expect(calls).toHaveLength(0);
});

test("morningPlan: returns empty array when no GameContext", async () => {
  // gameCtxMgr has no context set
  const calls: LlmCall[] = [];
  const director = new Director(beatStore, profileMgr, gameCtxMgr, activityStore, {
    callLlm: makeLlm("[]", calls),
  });
  const beats = await director.morningPlan();
  expect(beats).toEqual([]);
  expect(calls).toHaveLength(0);
});

test("morningPlan: validates NPC availability — drops beats for unavailable NPCs", async () => {
  gameCtxMgr.update(makeGameContext());
  // Make Willy unavailable
  gameCtxMgr.update({
    ...makeGameContext(),
    npcStates: [
      { name: "Willy", location: "Beach", tile: { x: 5, y: 8 }, isAvailable: false, currentState: "BUSY", friendshipPoints: 850 },
      { name: "Abigail", location: "Town", tile: { x: 30, y: 20 }, isAvailable: true, currentState: "IDLE", friendshipPoints: 500 },
    ],
  });
  const director = new Director(beatStore, profileMgr, gameCtxMgr, activityStore, {
    callLlm: makeLlm(
      JSON.stringify([
        { npcName: "Willy", triggerTime: "14:00", windowEnd: "16:00", directive: "去海边", reasonGenerated: "理由" },
        { npcName: "Abigail", triggerTime: "15:00", windowEnd: "17:00", directive: "去镇上", reasonGenerated: "理由" },
      ]),
      [],
    ),
  });
  const beats = await director.morningPlan();
  expect(beats).toHaveLength(1);
  expect(beats[0]!.npcName).toBe("Abigail");
});

test("morningPlan: validates NPC name — drops beats for unknown NPCs", async () => {
  gameCtxMgr.update(makeGameContext());
  const director = new Director(beatStore, profileMgr, gameCtxMgr, activityStore, {
    callLlm: makeLlm(
      JSON.stringify([
        { npcName: "UnknownNPC", triggerTime: "14:00", windowEnd: "16:00", directive: "x", reasonGenerated: "y" },
        { npcName: "Willy", triggerTime: "14:00", windowEnd: "16:00", directive: "去海边", reasonGenerated: "理由" },
      ]),
      [],
    ),
  });
  const beats = await director.morningPlan();
  expect(beats).toHaveLength(1);
  expect(beats[0]!.npcName).toBe("Willy");
});

test("morningPlan: validates time format — drops beats with invalid triggerTime", async () => {
  gameCtxMgr.update(makeGameContext());
  const director = new Director(beatStore, profileMgr, gameCtxMgr, activityStore, {
    callLlm: makeLlm(
      JSON.stringify([
        { npcName: "Willy", triggerTime: "25:00", windowEnd: "26:00", directive: "x", reasonGenerated: "y" },
        { npcName: "Abigail", triggerTime: "14:00", windowEnd: "16:00", directive: "去镇", reasonGenerated: "理由" },
      ]),
      [],
    ),
  });
  const beats = await director.morningPlan();
  expect(beats).toHaveLength(1);
  expect(beats[0]!.npcName).toBe("Abigail");
});

test("morningPlan: validates windowEnd > triggerTime — drops beats with reversed window", async () => {
  gameCtxMgr.update(makeGameContext());
  const director = new Director(beatStore, profileMgr, gameCtxMgr, activityStore, {
    callLlm: makeLlm(
      JSON.stringify([
        { npcName: "Willy", triggerTime: "16:00", windowEnd: "14:00", directive: "x", reasonGenerated: "y" },
        { npcName: "Abigail", triggerTime: "14:00", windowEnd: "16:00", directive: "去镇", reasonGenerated: "理由" },
      ]),
      [],
    ),
  });
  const beats = await director.morningPlan();
  expect(beats).toHaveLength(1);
  expect(beats[0]!.npcName).toBe("Abigail");
});

test("morningPlan: validates directive non-empty and length ≤ 200 chars", async () => {
  gameCtxMgr.update(makeGameContext());
  const longDirective = "a".repeat(201);
  const director = new Director(beatStore, profileMgr, gameCtxMgr, activityStore, {
    callLlm: makeLlm(
      JSON.stringify([
        { npcName: "Willy", triggerTime: "14:00", windowEnd: "16:00", directive: "", reasonGenerated: "y" },
        { npcName: "Abigail", triggerTime: "14:00", windowEnd: "16:00", directive: longDirective, reasonGenerated: "y" },
        { npcName: "Sebastian", triggerTime: "14:00", windowEnd: "16:00", directive: "ok", reasonGenerated: "理由" },
      ]),
      [],
    ),
  });
  const beats = await director.morningPlan();
  expect(beats).toHaveLength(1);
  expect(beats[0]!.npcName).toBe("Sebastian");
});

test("morningPlan: dedupes beats for same NPC in single response", async () => {
  gameCtxMgr.update(makeGameContext());
  const director = new Director(beatStore, profileMgr, gameCtxMgr, activityStore, {
    callLlm: makeLlm(
      JSON.stringify([
        { npcName: "Willy", triggerTime: "10:00", windowEnd: "12:00", directive: "first", reasonGenerated: "y" },
        { npcName: "Willy", triggerTime: "14:00", windowEnd: "16:00", directive: "second", reasonGenerated: "y" },
      ]),
      [],
    ),
  });
  const beats = await director.morningPlan();
  expect(beats).toHaveLength(1);
  expect(beats[0]!.directive).toBe("first");
});

test("morningPlan: enforces 5-day NPC cooldown from BeatStore history", async () => {
  gameCtxMgr.update(makeGameContext());
  // Pre-seed a recent beat for Willy from 2 days ago
  const recentBeat: Beat = {
    id: "old-beat-1",
    npcName: "Willy",
    triggerTime: "10:00",
    windowEnd: "12:00",
    directive: "old directive",
    context: {
      reasonGenerated: "old reason",
      playerProfileSnapshot: makeStaticProfile(),
      gameContextSnapshot: makeGameContext(),
      recentBeats: [],
    },
    status: "completed",
  };
  beatStore.save(recentBeat);

  const director = new Director(beatStore, profileMgr, gameCtxMgr, activityStore, {
    callLlm: makeLlm(
      JSON.stringify([
        { npcName: "Willy", triggerTime: "14:00", windowEnd: "16:00", directive: "去海边", reasonGenerated: "理由" },
        { npcName: "Abigail", triggerTime: "14:00", windowEnd: "16:00", directive: "去镇", reasonGenerated: "理由" },
      ]),
      [],
    ),
    npcCooldownDays: 5,
  });
  const beats = await director.morningPlan();
  // Willy is on cooldown -> dropped, only Abigail remains
  expect(beats).toHaveLength(1);
  expect(beats[0]!.npcName).toBe("Abigail");
});

test("morningPlan: truncates to maxBeatsPerDay (default 3)", async () => {
  gameCtxMgr.update(makeGameContext());
  const director = new Director(beatStore, profileMgr, gameCtxMgr, activityStore, {
    callLlm: makeLlm(
      JSON.stringify([
        { npcName: "Willy", triggerTime: "10:00", windowEnd: "12:00", directive: "a", reasonGenerated: "r" },
        { npcName: "Abigail", triggerTime: "10:00", windowEnd: "12:00", directive: "b", reasonGenerated: "r" },
        { npcName: "Sebastian", triggerTime: "10:00", windowEnd: "12:00", directive: "c", reasonGenerated: "r" },
      ]),
      [],
    ),
    maxBeatsPerDay: 2,
  });
  const beats = await director.morningPlan();
  expect(beats).toHaveLength(2);
});

test("morningPlan: LLM failure returns empty array without throwing", async () => {
  gameCtxMgr.update(makeGameContext());
  const director = new Director(beatStore, profileMgr, gameCtxMgr, activityStore, {
    callLlm: async () => {
      throw new Error("LLM unavailable");
    },
  });
  await expect(director.morningPlan()).resolves.toEqual([]);
});

test("morningPlan: tolerates LLM output with surrounding prose (extracts JSON array)", async () => {
  gameCtxMgr.update(makeGameContext());
  const director = new Director(beatStore, profileMgr, gameCtxMgr, activityStore, {
    callLlm: makeLlm(
      `好的，今天建议如下：\n${JSON.stringify([
        { npcName: "Willy", triggerTime: "14:00", windowEnd: "16:00", directive: "去海边", reasonGenerated: "理由" },
      ])}\n以上是建议。`,
      [],
    ),
  });
  const beats = await director.morningPlan();
  expect(beats).toHaveLength(1);
  expect(beats[0]!.npcName).toBe("Willy");
});

test("morningPlan: LLM outputs non-JSON returns empty array", async () => {
  gameCtxMgr.update(makeGameContext());
  const director = new Director(beatStore, profileMgr, gameCtxMgr, activityStore, {
    callLlm: makeLlm("抱歉，今天没有合适的安排。", []),
  });
  const beats = await director.morningPlan();
  expect(beats).toEqual([]);
});

// ---------------------------------------------------------------------------
// milestoneReact tests
// ---------------------------------------------------------------------------

test("milestoneReact: returns a beat from LLM response", async () => {
  gameCtxMgr.update(makeGameContext());
  const director = new Director(beatStore, profileMgr, gameCtxMgr, activityStore, {
    callLlm: makeLlm(
      JSON.stringify([
        { npcName: "Willy", triggerTime: "14:00", windowEnd: "16:00", directive: "祝贺玩家连续钓鱼", reasonGenerated: "里程碑触发" },
      ]),
      [],
    ),
  });
  const beat = await director.milestoneReact({
    type: "FishingStreak",
    description: "玩家连续 3 天钓鱼",
    detectedAt: "2026-07-21T14:30:00Z",
  });
  expect(beat).not.toBeNull();
  expect(beat!.npcName).toBe("Willy");
  // Beat persisted
  const stored = beatStore.getById(beat!.id);
  expect(stored).not.toBeNull();
});

test("milestoneReact: returns null when LLM outputs empty array", async () => {
  gameCtxMgr.update(makeGameContext());
  const director = new Director(beatStore, profileMgr, gameCtxMgr, activityStore, {
    callLlm: makeLlm("[]", []),
  });
  const beat = await director.milestoneReact({
    type: "FishingStreak",
    description: "玩家连续 3 天钓鱼",
    detectedAt: "2026-07-21T14:30:00Z",
  });
  expect(beat).toBeNull();
});

test("milestoneReact: skips on festival day", async () => {
  gameCtxMgr.update(makeGameContext({ time: { ...makeGameContext().time, isFestivalDay: true, festivalName: "Luau" } }));
  const calls: LlmCall[] = [];
  const director = new Director(beatStore, profileMgr, gameCtxMgr, activityStore, {
    callLlm: makeLlm(
      JSON.stringify([{ npcName: "Willy", triggerTime: "10:00", windowEnd: "12:00", directive: "x", reasonGenerated: "y" }]),
      calls,
    ),
  });
  const beat = await director.milestoneReact({
    type: "FishingStreak",
    description: "玩家连续 3 天钓鱼",
    detectedAt: "2026-07-21T14:30:00Z",
  });
  expect(beat).toBeNull();
  expect(calls).toHaveLength(0);
});

test("milestoneReact: returns null when no GameContext", async () => {
  const director = new Director(beatStore, profileMgr, gameCtxMgr, activityStore, {
    callLlm: makeLlm("[]", []),
  });
  const beat = await director.milestoneReact({
    type: "FishingStreak",
    description: "玩家连续 3 天钓鱼",
    detectedAt: "2026-07-21T14:30:00Z",
  });
  expect(beat).toBeNull();
});

test("milestoneReact: returns at most one beat (truncates if LLM returns more)", async () => {
  gameCtxMgr.update(makeGameContext());
  const director = new Director(beatStore, profileMgr, gameCtxMgr, activityStore, {
    callLlm: makeLlm(
      JSON.stringify([
        { npcName: "Willy", triggerTime: "14:00", windowEnd: "16:00", directive: "a", reasonGenerated: "r" },
        { npcName: "Abigail", triggerTime: "14:00", windowEnd: "16:00", directive: "b", reasonGenerated: "r" },
      ]),
      [],
    ),
  });
  const beat = await director.milestoneReact({
    type: "FishingStreak",
    description: "玩家连续 3 天钓鱼",
    detectedAt: "2026-07-21T14:30:00Z",
  });
  expect(beat).not.toBeNull();
  expect(beat!.directive).toBe("a");
  // Only one beat persisted
  const allBeats = beatStore.listRecent(10);
  expect(allBeats).toHaveLength(1);
});

test("milestoneReact: LLM failure returns null without throwing", async () => {
  gameCtxMgr.update(makeGameContext());
  const director = new Director(beatStore, profileMgr, gameCtxMgr, activityStore, {
    callLlm: async () => {
      throw new Error("LLM unavailable");
    },
  });
  await expect(
    director.milestoneReact({
      type: "FishingStreak",
      description: "玩家连续 3 天钓鱼",
      detectedAt: "2026-07-21T14:30:00Z",
    }),
  ).resolves.toBeNull();
});

// __TASK4_LOG_TESTS__

test("log: morningPlan emits start and end lines", async () => {
  const logSpy = spyOn(console, "log").mockImplementation(() => {});
  gameCtxMgr.update(makeGameContext());
  const director = new Director(beatStore, profileMgr, gameCtxMgr, activityStore, {
    callLlm: async () => ({
      text: JSON.stringify([{
        npcName: "Willy",
        triggerTime: "09:00",
        windowEnd: "11:00",
        directive: "去海滩看看玩家钓到什么鱼",
        reasonGenerated: "玩家爱钓鱼",
      }]),
      usage: { promptTokens: 100, completionTokens: 50 },
    }),
  });

  await director.morningPlan();

  const lines = logSpy.mock.calls.map((c) => String(c[0]));
  expect(lines.some((l) => l.includes("[director]") && l.includes("morningPlan") && l.includes("start"))).toBe(true);
  expect(lines.some((l) => l.includes("[director]") && l.includes("morningPlan") && l.includes("end"))).toBe(true);
  logSpy.mockRestore();
});
