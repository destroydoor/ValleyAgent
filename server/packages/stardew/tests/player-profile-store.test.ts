// Tests for PlayerProfileStore SQLite persistence (Task 4).
// Run: bun test tests/player-profile-store.test.ts

import { test, expect, beforeEach, afterEach } from "bun:test";
import { PlayerProfileStore } from "../src/player-profile-store";
import { rmSync, existsSync } from "node:fs";
import { join } from "node:path";
import { tmpdir } from "node:os";
import type {
  PlayerProfile,
  DailyActivity,
  PlayStyle,
  ActivityRank,
  LocationRank,
  Interaction,
  BeatHistoryEntry,
} from "../src/types";

// ---------------------------------------------------------------------------
// Factory helpers
// ---------------------------------------------------------------------------

function makeStaticLayer(overrides: Partial<PlayerProfile["static"]> = {}): PlayerProfile["static"] {
  return {
    farmerName: "Alice",
    gender: "female",
    farmName: "Riverland",
    farmType: "Riverland",
    startDate: "2026-06-01",
    lastUpdated: "2026-07-21",
    ...overrides,
  };
}

function makeActivity(date: string, overrides: Partial<DailyActivity> = {}): DailyActivity {
  return {
    date,
    fishingMinutes: 0,
    farmingMinutes: 0,
    miningMinutes: 0,
    foragingMinutes: 0,
    socialMinutes: 0,
    combatMinutes: 0,
    locationsVisited: [],
    fishCaught: 0,
    cropsHarvested: 0,
    itemsShipped: 0,
    itemsForaged: 0,
    monstersKilled: 0,
    npcsTalkedTo: [],
    giftsGiven: [],
    ...overrides,
  };
}

function makePlayStyles(): PlayStyle[] {
  return [
    { tag: "brewer", confidence: 0.8, evidence: "12 个酒桶" },
    { tag: "farmer", confidence: 0.6, evidence: "150 块种植" },
  ];
}

function makePreferences(overrides: Partial<PlayerProfile["preferences"]> = {}): PlayerProfile["preferences"] {
  return {
    playStyle: makePlayStyles(),
    topActivities: [
      { activity: "fishing", rank: 1, share: 0.45, evidence: "90m/day avg" },
    ] as ActivityRank[],
    topLocations: [
      { location: "Beach", rank: 1, visitCount: 12, share: 0.6 },
    ] as LocationRank[],
    routinePattern: "早晨种地下午钓鱼",
    lastUpdated: "2026-07-21",
    ...overrides,
  };
}

function makePersonality(overrides: Partial<PlayerProfile["personality"]> = {}): PlayerProfile["personality"] {
  return {
    traits: ["内向", "细心"],
    archetype: "独行者",
    narrativeRole: "不情愿的农场主",
    lastUpdated: "2026-07-21",
    ...overrides,
  };
}

function makeInteraction(date: string): Interaction {
  return {
    date,
    type: "dialogue",
    summary: "讨论了钓鱼技巧",
    emotionTag: "happy",
  };
}

function makeBeatHistory(beatId: string, date: string): BeatHistoryEntry {
  return {
    beatId,
    date,
    npcName: "Willy",
    directive: "去海边表达对玩家钓鱼技巧的赞赏",
    outcome: "completed",
  };
}

function makeFullProfile(): PlayerProfile {
  return {
    static: makeStaticLayer(),
    behavior: {
      dailyActivities: [makeActivity("2026-07-21", { fishingMinutes: 90 })],
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
    preferences: makePreferences(),
    relationships: {
      Willy: {
        phase: "friend",
        friendshipPoints: 850,
        last5Interactions: [makeInteraction("2026-07-21")],
        giftHistory: [{ to: "Willy", itemId: "Oceanfish_6" }],
        notableEvents: ["第一次送鱼给他"],
        lastUpdated: "2026-07-21",
      },
    },
    personality: makePersonality(),
    story: {
      completedBeats: [makeBeatHistory("b1", "2026-07-15")],
      recurringTropes: ["海边偶遇"],
      lastUpdated: "2026-07-15",
    },
  };
}

// ---------------------------------------------------------------------------
// Setup
// ---------------------------------------------------------------------------

let dbPath: string;
let store: PlayerProfileStore;

beforeEach(() => {
  dbPath = join(tmpdir(), `player-profile-test-${Date.now()}-${Math.random().toString(36).slice(2)}.db`);
  store = new PlayerProfileStore(dbPath);
  store.init();
});

afterEach(() => {
  store.close();
  if (existsSync(dbPath)) {
    rmSync(dbPath, { force: true });
  }
});

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

test("save + load: roundtrip preserves all five layers", () => {
  const profile = makeFullProfile();
  store.save(profile);
  const loaded = store.load();
  expect(loaded).not.toBeNull();
  // static
  expect(loaded!.static.farmerName).toBe("Alice");
  expect(loaded!.static.farmType).toBe("Riverland");
  // behavior
  expect(loaded!.behavior.dailyActivities).toHaveLength(1);
  expect(loaded!.behavior.dailyActivities[0]?.fishingMinutes).toBe(90);
  expect(loaded!.behavior.totalStats.fishCaught).toBe(47);
  // preferences
  expect(loaded!.preferences.playStyle).toHaveLength(2);
  expect(loaded!.preferences.playStyle[0]?.tag).toBe("brewer");
  expect(loaded!.preferences.routinePattern).toBe("早晨种地下午钓鱼");
  // relationships
  expect(loaded!.relationships["Willy"]?.friendshipPoints).toBe(850);
  expect(loaded!.relationships["Willy"]?.last5Interactions[0]?.summary).toBe("讨论了钓鱼技巧");
  // personality
  expect(loaded!.personality.archetype).toBe("独行者");
  // story
  expect(loaded!.story.completedBeats[0]?.beatId).toBe("b1");
  expect(loaded!.story.recurringTropes).toContain("海边偶遇");
});

test("load returns null when no profile exists", () => {
  expect(store.load()).toBeNull();
});

test("save is idempotent: overwrites previous profile", () => {
  store.save(makeFullProfile());
  const updated = makeFullProfile();
  updated.static.farmerName = "Bob";
  updated.personality.archetype = "冒险家";
  store.save(updated);
  const loaded = store.load();
  expect(loaded!.static.farmerName).toBe("Bob");
  expect(loaded!.personality.archetype).toBe("冒险家");
});

test("updateStatic merges static layer and bumps lastUpdated", () => {
  store.save(makeFullProfile());
  store.updateStatic({
    farmerName: "Charlie",
    gender: "male",
    farmName: "Forest",
    farmType: "Forest",
    startDate: "2026-06-01",
    lastUpdated: "2026-07-22",
  });
  const loaded = store.load();
  expect(loaded!.static.farmerName).toBe("Charlie");
  expect(loaded!.static.gender).toBe("male");
  // Other layers untouched
  expect(loaded!.personality.archetype).toBe("独行者");
});

test("updateStatic auto-creates empty profile when none exists", () => {
  store.updateStatic(makeStaticLayer({ farmerName: "Solo" }));
  const loaded = store.load();
  expect(loaded).not.toBeNull();
  expect(loaded!.static.farmerName).toBe("Solo");
  // Other layers should be empty defaults
  expect(loaded!.behavior.dailyActivities).toEqual([]);
  expect(loaded!.relationships).toEqual({});
});

test("appendDailyActivity adds new activity and dedupes by date", () => {
  store.save(makeFullProfile());
  store.appendDailyActivity(makeActivity("2026-07-22", { fishingMinutes: 60 }));
  // Same date overwrites
  store.appendDailyActivity(makeActivity("2026-07-22", { fishingMinutes: 120 }));
  const loaded = store.load();
  const jul22 = loaded!.behavior.dailyActivities.find((a) => a.date === "2026-07-22");
  expect(jul22?.fishingMinutes).toBe(120);
  // No duplicate date rows
  const dateCount = loaded!.behavior.dailyActivities.filter((a) => a.date === "2026-07-22").length;
  expect(dateCount).toBe(1);
});

test("appendDailyActivity keeps only the latest 30 days", () => {
  store.save(makeFullProfile());
  // Insert 35 activities with distinct dates
  for (let i = 1; i <= 35; i++) {
    const date = `2026-08-${String(i).padStart(2, "0")}`;
    store.appendDailyActivity(makeActivity(date, { fishingMinutes: i }));
  }
  const loaded = store.load();
  expect(loaded!.behavior.dailyActivities.length).toBeLessThanOrEqual(30);
  // The oldest entries (2026-08-01..2026-08-05) should be dropped
  const hasAug1 = loaded!.behavior.dailyActivities.some((a) => a.date === "2026-08-01");
  expect(hasAug1).toBe(false);
  // The newest (2026-08-35 -> invalid date but still string, test ordering) should remain
  const hasAug35 = loaded!.behavior.dailyActivities.some((a) => a.date === "2026-08-35");
  expect(hasAug35).toBe(true);
});

test("updatePreferences replaces the preferences layer", () => {
  store.save(makeFullProfile());
  const newPrefs = makePreferences({
    routinePattern: "夜猫子，凌晨钓鱼",
    playStyle: [{ tag: "miner", confidence: 0.9, evidence: "深矿挖矿" }],
  });
  store.updatePreferences(newPrefs);
  const loaded = store.load();
  expect(loaded!.preferences.routinePattern).toBe("夜猫子，凌晨钓鱼");
  expect(loaded!.preferences.playStyle[0]?.tag).toBe("miner");
  // Other layers untouched
  expect(loaded!.static.farmerName).toBe("Alice");
});

test("updatePersonality replaces the personality layer", () => {
  store.save(makeFullProfile());
  store.updatePersonality(
    makePersonality({
      archetype: "社交达人",
      traits: ["外向", "健谈"],
    }),
  );
  const loaded = store.load();
  expect(loaded!.personality.archetype).toBe("社交达人");
  expect(loaded!.personality.traits).toEqual(["外向", "健谈"]);
});

test("updateRelationship adds new NPC and updates existing NPC", () => {
  store.save(makeFullProfile());
  // Add new NPC
  store.updateRelationship("Sebastian", {
    phase: "stranger",
    friendshipPoints: 100,
    last5Interactions: [],
    giftHistory: [],
    notableEvents: [],
    lastUpdated: "2026-07-22",
  });
  // Update existing NPC
  store.updateRelationship("Willy", {
    phase: "close_friend",
    friendshipPoints: 1500,
    last5Interactions: [makeInteraction("2026-07-22")],
    giftHistory: [{ to: "Willy", itemId: "Oceanfish_6" }],
    notableEvents: ["连续3天送鱼"],
    lastUpdated: "2026-07-22",
  });
  const loaded = store.load();
  expect(loaded!.relationships["Sebastian"]?.friendshipPoints).toBe(100);
  expect(loaded!.relationships["Willy"]?.friendshipPoints).toBe(1500);
  expect(loaded!.relationships["Willy"]?.phase).toBe("close_friend");
});

test("appendBeatHistory adds entry and keeps only the latest 50", () => {
  store.save(makeFullProfile());
  // Insert 55 beat history entries
  for (let i = 1; i <= 55; i++) {
    store.appendBeatHistory(makeBeatHistory(`b${i}`, `2026-08-${String(i).padStart(2, "0")}`));
  }
  const loaded = store.load();
  expect(loaded!.story.completedBeats.length).toBeLessThanOrEqual(50);
  // The first entries (b1..b5) should be dropped, the last (b55) retained
  const hasB1 = loaded!.story.completedBeats.some((b) => b.beatId === "b1");
  expect(hasB1).toBe(false);
  const hasB55 = loaded!.story.completedBeats.some((b) => b.beatId === "b55");
  expect(hasB55).toBe(true);
});

test("addRecurringTrope adds trope and dedupes", () => {
  store.save(makeFullProfile());
  store.addRecurringTrope("海边偶遇");
  store.addRecurringTrope("送礼惊喜");
  store.addRecurringTrope("海边偶遇"); // duplicate, should be ignored
  const loaded = store.load();
  expect(loaded!.story.recurringTropes).toContain("海边偶遇");
  expect(loaded!.story.recurringTropes).toContain("送礼惊喜");
  // Deduped: only 2 entries total (original "海边偶遇" + "送礼惊喜")
  const tropeCount = loaded!.story.recurringTropes.filter((t) => t === "海边偶遇").length;
  expect(tropeCount).toBe(1);
  expect(loaded!.story.recurringTropes.length).toBe(2);
});

test("updateRelationship auto-creates profile when none exists", () => {
  store.updateRelationship("Willy", {
    phase: "stranger",
    friendshipPoints: 0,
    last5Interactions: [],
    giftHistory: [],
    notableEvents: [],
    lastUpdated: "2026-07-22",
  });
  const loaded = store.load();
  expect(loaded).not.toBeNull();
  expect(loaded!.relationships["Willy"]?.friendshipPoints).toBe(0);
  // Other layers are empty defaults
  expect(loaded!.static.farmerName).toBe("");
});
