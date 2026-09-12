// Tests for BeatStore SQLite persistence (Task 2).
// Run: bun test tests/beat-store.test.ts

import { test, expect, beforeEach, afterEach } from "bun:test";
import { BeatStore } from "../src/beat-store";
import { rmSync, existsSync } from "node:fs";
import { join } from "node:path";
import { tmpdir } from "node:os";
import type { Beat, PlayerProfile, GameContext } from "../src/types";

// Helper: build a minimal valid PlayerProfile.
function makeProfile(): PlayerProfile {
  return {
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
}

// Helper: build a minimal valid GameContext.
function makeGameContext(): GameContext {
  return {
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
      communityCenterBundlesDone: [],
      jojaMartRoute: false,
      islandsUnlocked: [],
      desertUnlocked: false,
      railroadUnlocked: false,
      sewersUnlocked: false,
      greenhouseRestored: false,
    },
    seasonalResources: {
      plantableCrops: [],
      catchableFish: [],
      forageItems: [],
      activeFestivals: [],
    },
    npcStates: [],
    playerState: {
      location: "Farm",
      tile: { x: 0, y: 0 },
      health: 100,
      maxHealth: 100,
      energy: 300,
      maxEnergy: 300,
      money: 0,
      inventory: [],
    },
    lastUpdated: "2026-07-21T06:00:00Z",
  };
}

// Helper: build a minimal valid Beat.
function makeBeat(overrides: Partial<Beat> = {}): Beat {
  return {
    id: "beat-001",
    npcName: "Willy",
    triggerTime: "14:00",
    windowEnd: "16:00",
    directive: "下午去农场表达对玩家劳作的关心",
    context: {
      reasonGenerated: "玩家连续3天钓鱼",
      playerProfileSnapshot: makeProfile(),
      gameContextSnapshot: makeGameContext(),
      recentBeats: [],
    },
    status: "scheduled",
    reactSteps: [],
    ...overrides,
  };
}

let dbPath: string;
let store: BeatStore;

beforeEach(() => {
  dbPath = join(tmpdir(), `beat-store-test-${Date.now()}-${Math.random().toString(36).slice(2)}.db`);
  store = new BeatStore(dbPath);
  store.init();
});

afterEach(() => {
  store.close();
  if (existsSync(dbPath)) {
    rmSync(dbPath, { force: true });
  }
});

test("save + load: roundtrip preserves all fields", () => {
  const beat = makeBeat();
  store.save(beat);
  const loaded = store.getById(beat.id);
  expect(loaded).not.toBeNull();
  expect(loaded!.id).toBe(beat.id);
  expect(loaded!.npcName).toBe(beat.npcName);
  expect(loaded!.triggerTime).toBe(beat.triggerTime);
  expect(loaded!.windowEnd).toBe(beat.windowEnd);
  expect(loaded!.directive).toBe(beat.directive);
  expect(loaded!.status).toBe(beat.status);
  expect(loaded!.context.reasonGenerated).toBe(beat.context.reasonGenerated);
});

test("getById returns null for missing id", () => {
  const loaded = store.getById("does-not-exist");
  expect(loaded).toBeNull();
});

test("updateStatus transitions scheduled -> active -> completed", () => {
  const beat = makeBeat();
  store.save(beat);
  store.updateStatus(beat.id, "active");
  expect(store.getById(beat.id)!.status).toBe("active");
  store.updateStatus(beat.id, "completed");
  expect(store.getById(beat.id)!.status).toBe("completed");
});

test("listScheduled returns only scheduled beats", () => {
  store.save(makeBeat({ id: "b1", status: "scheduled" }));
  store.save(makeBeat({ id: "b2", status: "active" }));
  store.save(makeBeat({ id: "b3", status: "scheduled" }));
  store.save(makeBeat({ id: "b4", status: "completed" }));
  const scheduled = store.listScheduled();
  expect(scheduled).toHaveLength(2);
  expect(scheduled.map((b) => b.id).sort()).toEqual(["b1", "b3"]);
});

test("listActive returns only active beats", () => {
  store.save(makeBeat({ id: "b1", status: "scheduled" }));
  store.save(makeBeat({ id: "b2", status: "active" }));
  store.save(makeBeat({ id: "b3", status: "active" }));
  const active = store.listActive();
  expect(active).toHaveLength(2);
  expect(active.map((b) => b.id).sort()).toEqual(["b2", "b3"]);
});

test("listRecent returns N most recent beats ordered by created_at desc", () => {
  store.save(makeBeat({ id: "b1" }));
  store.save(makeBeat({ id: "b2" }));
  store.save(makeBeat({ id: "b3" }));
  const recent = store.listRecent(2);
  expect(recent).toHaveLength(2);
  // Most recent first (b3 inserted last). noUncheckedIndexedAccess: index access is T | undefined.
  const first = recent[0];
  const second = recent[1];
  expect(first?.id).toBe("b3");
  expect(second?.id).toBe("b2");
});

test("listByNpc returns beats filtered by npcName", () => {
  store.save(makeBeat({ id: "b1", npcName: "Willy" }));
  store.save(makeBeat({ id: "b2", npcName: "Abigail" }));
  store.save(makeBeat({ id: "b3", npcName: "Willy" }));
  const willyBeats = store.listByNpc("Willy");
  expect(willyBeats).toHaveLength(2);
  expect(willyBeats.map((b) => b.id).sort()).toEqual(["b1", "b3"]);
});

test("countActiveByNpc counts active beats for one NPC", () => {
  store.save(makeBeat({ id: "b1", npcName: "Willy", status: "active" }));
  store.save(makeBeat({ id: "b2", npcName: "Willy", status: "scheduled" }));
  store.save(makeBeat({ id: "b3", npcName: "Willy", status: "completed" }));
  store.save(makeBeat({ id: "b4", npcName: "Abigail", status: "active" }));
  expect(store.countActiveByNpc("Willy")).toBe(1);
  expect(store.countActiveByNpc("Abigail")).toBe(1);
  expect(store.countActiveByNpc("Sebastian")).toBe(0);
});

test("countActiveTotal counts all active beats across NPCs", () => {
  store.save(makeBeat({ id: "b1", npcName: "Willy", status: "active" }));
  store.save(makeBeat({ id: "b2", npcName: "Abigail", status: "active" }));
  store.save(makeBeat({ id: "b3", npcName: "Sebastian", status: "scheduled" }));
  expect(store.countActiveTotal()).toBe(2);
});

test("delete removes a beat by id", () => {
  const beat = makeBeat();
  store.save(beat);
  expect(store.getById(beat.id)).not.toBeNull();
  store.delete(beat.id);
  expect(store.getById(beat.id)).toBeNull();
});

test("upsert is idempotent: saving same id twice overwrites", () => {
  const beat = makeBeat({ id: "b1", directive: "原指令" });
  store.save(beat);
  const updated = makeBeat({ id: "b1", directive: "新指令", status: "active" });
  store.save(updated);
  const loaded = store.getById("b1");
  expect(loaded!.directive).toBe("新指令");
  expect(loaded!.status).toBe("active");
  // No duplicate rows
  expect(store.listRecent(10)).toHaveLength(1);
});

test("context JSON roundtrip preserves nested playerProfileSnapshot and gameContextSnapshot", () => {
  const beat = makeBeat({
    id: "b-ctx",
    context: {
      reasonGenerated: "复杂嵌套测试",
      playerProfileSnapshot: {
        ...makeProfile(),
        static: {
          farmerName: "Bob",
          gender: "male",
          farmName: "Hilltop",
          farmType: "Standard",
          startDate: "2026-01-01",
          lastUpdated: "2026-07-21T06:00:00Z",
        },
        behavior: {
          dailyActivities: [],
          totalStats: {
            fishCaught: 42,
            itemsShipped: 100,
            monstersKilled: 7,
            cropsHarvested: 250,
            itemsForaged: 30,
            giftsGiven: 5,
            dialoguesHad: 20,
            miningLevelsDescended: 3,
          },
        },
      },
      gameContextSnapshot: {
        ...makeGameContext(),
        time: {
          year: 3,
          season: "fall",
          day: 7,
          dayOfWeek: "Friday",
          weather: "rainy",
          isFestivalDay: true,
          festivalName: "Stardew Valley Fair",
        },
      },
      recentBeats: [],
    },
  });
  store.save(beat);
  const loaded = store.getById("b-ctx");
  expect(loaded).not.toBeNull();
  expect(loaded!.context.reasonGenerated).toBe("复杂嵌套测试");
  expect(loaded!.context.playerProfileSnapshot.static.farmerName).toBe("Bob");
  expect(loaded!.context.playerProfileSnapshot.behavior.totalStats.fishCaught).toBe(42);
  expect(loaded!.context.gameContextSnapshot.time.festivalName).toBe("Stardew Valley Fair");
  expect(loaded!.context.gameContextSnapshot.time.year).toBe(3);
});

test("reactSteps persist when provided", () => {
  const beat = makeBeat({
    id: "b-steps",
    reactSteps: [
      {
        stepIndex: 0,
        thought: "玩家在钓鱼，我走过去打招呼",
        toolCall: { tool: "move_to", args: { x: 32, y: 18 }, callId: "c1" },
        toolResult: { success: true, result: "已到达" },
        timestamp: "2026-07-21T14:05:00Z",
        tokensUsed: 320,
      },
    ],
  });
  store.save(beat);
  const loaded = store.getById("b-steps");
  expect(loaded!.reactSteps).toBeDefined();
  expect(loaded!.reactSteps).toHaveLength(1);
  // noUncheckedIndexedAccess: index access yields T | undefined; extract to local for safe access.
  const step = loaded!.reactSteps![0];
  expect(step?.thought).toBe("玩家在钓鱼，我走过去打招呼");
  expect(step?.toolCall?.tool).toBe("move_to");
  expect(step?.tokensUsed).toBe(320);
});
