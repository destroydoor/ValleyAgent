// Tests for ActivityLogStore SQLite persistence (Task 3).
// Run: bun test tests/activity-log-store.test.ts

import { test, expect, beforeEach, afterEach } from "bun:test";
import { ActivityLogStore } from "../src/activity-log-store";
import { rmSync, existsSync } from "node:fs";
import { join } from "node:path";
import { tmpdir } from "node:os";
import type { DailyActivity, PlayStyle } from "../src/types";

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

// Format a Date as "YYYY-MM-DD" using local time (matches SQLite date('now') semantics
// closely enough for the milestone-window test, which only cares about day granularity).
function fmtDate(d: Date): string {
  const y = d.getFullYear();
  const m = String(d.getMonth() + 1).padStart(2, "0");
  const day = String(d.getDate()).padStart(2, "0");
  return `${y}-${m}-${day}`;
}

function daysAgo(n: number): string {
  const d = new Date();
  d.setDate(d.getDate() - n);
  return fmtDate(d);
}

let dbPath: string;
let store: ActivityLogStore;

beforeEach(() => {
  dbPath = join(tmpdir(), `activity-log-test-${Date.now()}-${Math.random().toString(36).slice(2)}.db`);
  store = new ActivityLogStore(dbPath);
  store.init();
});

afterEach(() => {
  store.close();
  if (existsSync(dbPath)) {
    rmSync(dbPath, { force: true });
  }
});

test("saveDaily + getDaily: roundtrip preserves all fields", () => {
  const activity = makeActivity("2026-07-21", {
    fishingMinutes: 90,
    farmingMinutes: 60,
    fishCaught: 12,
    itemsShipped: 5,
    npcsTalkedTo: ["Willy", "Pierre"],
    locationsVisited: ["Beach", "Farm"],
    giftsGiven: [{ to: "Willy", itemId: "Oceanfish_6" }],
  });
  store.saveDaily(activity);
  const loaded = store.getDaily("2026-07-21");
  expect(loaded).not.toBeNull();
  expect(loaded!.date).toBe("2026-07-21");
  expect(loaded!.fishingMinutes).toBe(90);
  expect(loaded!.farmingMinutes).toBe(60);
  expect(loaded!.fishCaught).toBe(12);
  expect(loaded!.itemsShipped).toBe(5);
  expect(loaded!.npcsTalkedTo).toEqual(["Willy", "Pierre"]);
  expect(loaded!.locationsVisited).toEqual(["Beach", "Farm"]);
  expect(loaded!.giftsGiven).toEqual([{ to: "Willy", itemId: "Oceanfish_6" }]);
});

test("getDaily returns null for missing date", () => {
  const loaded = store.getDaily("2099-01-01");
  expect(loaded).toBeNull();
});

test("saveDaily is idempotent: same date overwrites", () => {
  store.saveDaily(makeActivity("2026-07-21", { fishingMinutes: 30, fishCaught: 5 }));
  store.saveDaily(makeActivity("2026-07-21", { fishingMinutes: 90, fishCaught: 12 }));
  const loaded = store.getDaily("2026-07-21");
  expect(loaded!.fishingMinutes).toBe(90);
  expect(loaded!.fishCaught).toBe(12);
  // No duplicate rows
  expect(store.listRecent(10)).toHaveLength(1);
});

test("listRecent returns N most recent days in descending order", () => {
  store.saveDaily(makeActivity("2026-07-19"));
  store.saveDaily(makeActivity("2026-07-21"));
  store.saveDaily(makeActivity("2026-07-20"));
  const recent = store.listRecent(2);
  expect(recent).toHaveLength(2);
  expect(recent[0]?.date).toBe("2026-07-21");
  expect(recent[1]?.date).toBe("2026-07-20");
});

test("saveFarmSnapshot + getLatestFarmSnapshot returns latest snapshot", () => {
  store.saveFarmSnapshot("2026-07-20", makePlayStyles());
  store.saveFarmSnapshot("2026-07-21", [
    { tag: "miner", confidence: 0.9, evidence: "深矿挖矿 7 天" },
  ]);
  const latest = store.getLatestFarmSnapshot();
  expect(latest).not.toBeNull();
  expect(latest!.date).toBe("2026-07-21");
  expect(latest!.playStyles).toHaveLength(1);
  expect(latest!.playStyles[0]?.tag).toBe("miner");
});

test("getLatestFarmSnapshot returns null when no snapshots exist", () => {
  const latest = store.getLatestFarmSnapshot();
  expect(latest).toBeNull();
});

test("saveFarmSnapshot is idempotent: same date overwrites", () => {
  store.saveFarmSnapshot("2026-07-21", [
    { tag: "brewer", confidence: 0.5, evidence: "first" },
  ]);
  store.saveFarmSnapshot("2026-07-21", [
    { tag: "farmer", confidence: 0.9, evidence: "second" },
  ]);
  const latest = store.getLatestFarmSnapshot();
  expect(latest!.playStyles).toHaveLength(1);
  expect(latest!.playStyles[0]?.tag).toBe("farmer");
  expect(latest!.playStyles[0]?.confidence).toBe(0.9);
});

test("getMedian computes median for fishingMinutes over N days", () => {
  // Values: 30, 60, 90 -> median = 60
  store.saveDaily(makeActivity("2026-07-19", { fishingMinutes: 30 }));
  store.saveDaily(makeActivity("2026-07-20", { fishingMinutes: 60 }));
  store.saveDaily(makeActivity("2026-07-21", { fishingMinutes: 90 }));
  const median = store.getMedian("fishingMinutes", 3);
  expect(median).toBe(60);
});

test("getMedian computes median for even count (average of two middle)", () => {
  // Values: 10, 20, 30, 40 -> median = (20+30)/2 = 25
  store.saveDaily(makeActivity("2026-07-18", { fishCaught: 10 }));
  store.saveDaily(makeActivity("2026-07-19", { fishCaught: 20 }));
  store.saveDaily(makeActivity("2026-07-20", { fishCaught: 30 }));
  store.saveDaily(makeActivity("2026-07-21", { fishCaught: 40 }));
  const median = store.getMedian("fishCaught", 4);
  expect(median).toBe(25);
});

test("getSum computes sum for a numeric field over N days", () => {
  store.saveDaily(makeActivity("2026-07-19", { monstersKilled: 5 }));
  store.saveDaily(makeActivity("2026-07-20", { monstersKilled: 10 }));
  store.saveDaily(makeActivity("2026-07-21", { monstersKilled: 15 }));
  const sum = store.getSum("monstersKilled", 3);
  expect(sum).toBe(30);
});

test("getMedian/getSum return 0 when no data exists", () => {
  expect(store.getMedian("fishingMinutes", 7)).toBe(0);
  expect(store.getSum("fishCaught", 7)).toBe(0);
});

test("markMilestoneFired + hasMilestoneFired roundtrip", () => {
  store.markMilestoneFired("FishingStreak", "2026-07-21");
  expect(store.hasMilestoneFired("FishingStreak", "2026-07-21")).toBe(true);
  expect(store.hasMilestoneFired("MiningStreak", "2026-07-21")).toBe(false);
  expect(store.hasMilestoneFired("FishingStreak", "2026-07-22")).toBe(false);
});

test("hasMilestoneFiredWithin checks N days window", () => {
  // Use dates relative to "today" so the test is deterministic regardless of run date.
  const recentDate = daysAgo(3);    // 3 days ago — within 7-day window
  const farDate = daysAgo(40);      // 40 days ago — outside 7-day, inside 60-day window

  store.markMilestoneFired("FishingStreak", recentDate);
  expect(store.hasMilestoneFiredWithin("FishingStreak", 7)).toBe(true);

  store.markMilestoneFired("MiningStreak", farDate);
  expect(store.hasMilestoneFiredWithin("MiningStreak", 7)).toBe(false);
  expect(store.hasMilestoneFiredWithin("MiningStreak", 60)).toBe(true);
});
