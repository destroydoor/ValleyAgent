// Tests for PlayerProfileManager (Task 6).
// Run: bun test tests/player-profile.test.ts

import { test, expect, beforeEach, afterEach } from "bun:test";
import { PlayerProfileManager } from "../src/player-profile";
import { PlayerProfileStore } from "../src/player-profile-store";
import { ActivityLogStore } from "../src/activity-log-store";
import { rmSync, existsSync } from "node:fs";
import { join } from "node:path";
import { tmpdir } from "node:os";
import type {
  PlayerProfile,
  DailyActivity,
  PlayStyle,
  Interaction,
  GiftRecord,
  BeatHistoryEntry,
} from "../src/types";

// ---------------------------------------------------------------------------
// Factory helpers
// ---------------------------------------------------------------------------

function makeStaticLayer(): PlayerProfile["static"] {
  return {
    farmerName: "Alice",
    gender: "female",
    farmName: "Riverland",
    farmType: "Riverland",
    startDate: "2026-06-01",
    lastUpdated: "2026-07-21",
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

function makeInteraction(date: string, summary: string): Interaction {
  return {
    date,
    type: "dialogue",
    summary,
    emotionTag: "happy",
  };
}

function makeGift(itemId: string): GiftRecord {
  return { to: "Willy", itemId };
}

function makeBeatEntry(beatId: string, date: string): BeatHistoryEntry {
  return {
    beatId,
    date,
    npcName: "Willy",
    directive: "海边偶遇",
    outcome: "completed",
  };
}

// ---------------------------------------------------------------------------
// Setup
// ---------------------------------------------------------------------------

let profileDbPath: string;
let activityDbPath: string;
let profileStore: PlayerProfileStore;
let activityStore: ActivityLogStore;

beforeEach(() => {
  profileDbPath = join(
    tmpdir(),
    `pp-mgr-test-profile-${Date.now()}-${Math.random().toString(36).slice(2)}.db`,
  );
  activityDbPath = join(
    tmpdir(),
    `pp-mgr-test-activity-${Date.now()}-${Math.random().toString(36).slice(2)}.db`,
  );
  profileStore = new PlayerProfileStore(profileDbPath);
  activityStore = new ActivityLogStore(activityDbPath);
  profileStore.init();
  activityStore.init();
});

afterEach(() => {
  profileStore.close();
  activityStore.close();
  if (existsSync(profileDbPath)) rmSync(profileDbPath, { force: true });
  if (existsSync(activityDbPath)) rmSync(activityDbPath, { force: true });
});

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

test("initProfile creates an empty profile with the static layer populated", () => {
  const mgr = new PlayerProfileManager(profileStore, activityStore);
  mgr.initProfile(makeStaticLayer());
  const loaded = profileStore.load();
  expect(loaded).not.toBeNull();
  expect(loaded!.static.farmerName).toBe("Alice");
  expect(loaded!.behavior.dailyActivities).toEqual([]);
  expect(loaded!.relationships).toEqual({});
});

test("recordDailyActivity writes to both activityStore and profileStore", () => {
  const mgr = new PlayerProfileManager(profileStore, activityStore);
  mgr.initProfile(makeStaticLayer());
  const activity = makeActivity("2026-07-21", { fishingMinutes: 90, fishCaught: 12 });
  mgr.recordDailyActivity(activity);

  // activityStore has it
  const activityLog = activityStore.getDaily("2026-07-21");
  expect(activityLog).not.toBeNull();
  expect(activityLog!.fishingMinutes).toBe(90);

  // profileStore has it in the behavior layer
  const profile = profileStore.load();
  const inProfile = profile!.behavior.dailyActivities.find((a) => a.date === "2026-07-21");
  expect(inProfile?.fishCaught).toBe(12);
});

test("updateRelationship delegates to profileStore", () => {
  const mgr = new PlayerProfileManager(profileStore, activityStore);
  mgr.initProfile(makeStaticLayer());
  mgr.updateRelationship("Willy", {
    phase: "friend",
    friendshipPoints: 850,
    last5Interactions: [],
    giftHistory: [],
    notableEvents: [],
    lastUpdated: "2026-07-21",
  });
  const profile = profileStore.load();
  expect(profile!.relationships["Willy"]?.friendshipPoints).toBe(850);
});

test("appendInteraction keeps only the latest 5 per NPC", () => {
  const mgr = new PlayerProfileManager(profileStore, activityStore);
  mgr.initProfile(makeStaticLayer());
  // Insert 7 interactions
  for (let i = 1; i <= 7; i++) {
    mgr.appendInteraction("Willy", makeInteraction(`2026-07-${20 + i}`, `chat #${i}`));
  }
  const profile = profileStore.load();
  const interactions = profile!.relationships["Willy"]?.last5Interactions ?? [];
  expect(interactions).toHaveLength(5);
  // Should keep the latest 5 (chat #3 through chat #7)
  const summaries = interactions.map((i) => i.summary);
  expect(summaries).toContain("chat #7");
  expect(summaries).toContain("chat #3");
  expect(summaries).not.toContain("chat #1");
  expect(summaries).not.toContain("chat #2");
});

test("appendGiftHistory keeps only the latest 10 per NPC", () => {
  const mgr = new PlayerProfileManager(profileStore, activityStore);
  mgr.initProfile(makeStaticLayer());
  // Insert 12 gifts
  for (let i = 1; i <= 12; i++) {
    mgr.appendGiftHistory("Willy", makeGift(`item_${i}`));
  }
  const profile = profileStore.load();
  const gifts = profile!.relationships["Willy"]?.giftHistory ?? [];
  expect(gifts).toHaveLength(10);
  // Should keep the latest 10 (item_3 through item_12)
  const itemIds = gifts.map((g) => g.itemId);
  expect(itemIds).toContain("item_12");
  expect(itemIds).toContain("item_3");
  expect(itemIds).not.toContain("item_1");
  expect(itemIds).not.toContain("item_2");
});

test("appendBeatHistory delegates to profileStore", () => {
  const mgr = new PlayerProfileManager(profileStore, activityStore);
  mgr.initProfile(makeStaticLayer());
  mgr.appendBeatHistory(makeBeatEntry("b1", "2026-07-15"));
  const profile = profileStore.load();
  expect(profile!.story.completedBeats[0]?.beatId).toBe("b1");
});

test("addRecurringTrope delegates to profileStore", () => {
  const mgr = new PlayerProfileManager(profileStore, activityStore);
  mgr.initProfile(makeStaticLayer());
  mgr.addRecurringTrope("海边偶遇");
  const profile = profileStore.load();
  expect(profile!.story.recurringTropes).toContain("海边偶遇");
});

test("inferPlayStylesFromActivityLog returns latest FarmSnapshot from activityStore", () => {
  const mgr = new PlayerProfileManager(profileStore, activityStore);
  // No snapshot yet -> []
  expect(mgr.inferPlayStylesFromActivityLog()).toEqual([]);

  // Save a snapshot and read it back
  const styles: PlayStyle[] = [
    { tag: "brewer", confidence: 0.8, evidence: "12 个酒桶" },
    { tag: "farmer", confidence: 0.6, evidence: "150 块种植" },
  ];
  activityStore.saveFarmSnapshot("2026-07-21", styles);
  const inferred = mgr.inferPlayStylesFromActivityLog();
  expect(inferred).toHaveLength(2);
  expect(inferred[0]?.tag).toBe("brewer");
  expect(inferred[1]?.confidence).toBe(0.6);
});
