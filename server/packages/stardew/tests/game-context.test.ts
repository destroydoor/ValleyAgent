// Tests for GameContextManager (Task 7).
// Run: bun test tests/game-context.test.ts

import { test, expect, beforeEach } from "bun:test";
import { GameContextManager } from "../src/game-context";
import type { GameContext, NpcStateSnapshot } from "../src/types";

// ---------------------------------------------------------------------------
// Factory helpers
// ---------------------------------------------------------------------------

function makeNpc(name: string, available: boolean = true): NpcStateSnapshot {
  return {
    name,
    location: "Town",
    tile: { x: 10, y: 20 },
    isAvailable: available,
    currentState: "IDLE",
    friendshipPoints: 500,
  };
}

function makeContext(overrides: Partial<GameContext> = {}): GameContext {
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
      communityCenterBundlesDone: ["Pantry", "Boiler Room"],
      jojaMartRoute: false,
      islandsUnlocked: ["Ginger Island"],
      desertUnlocked: true,
      railroadUnlocked: true,
      sewersUnlocked: false,
      greenhouseRestored: true,
      ...overrides.progress,
    },
    seasonalResources: {
      plantableCrops: ["Corn", "Sunflower", "Hot Pepper"],
      catchableFish: ["Rainbow Trout", "Red Snapper"],
      forageItems: ["Spice Berry"],
      activeFestivals: [],
      ...overrides.seasonalResources,
    },
    npcStates: [makeNpc("Willy"), makeNpc("Sebastian", false)],
    playerState: {
      location: "Farm",
      tile: { x: 32, y: 18 },
      health: 95,
      maxHealth: 100,
      energy: 200,
      maxEnergy: 270,
      money: 8500,
      inventory: [
        { name: "Corn", quantity: 12 },
        { name: "Hoe", quantity: 1 },
        { name: "Fishing Rod", quantity: 1 },
      ],
      ...overrides.playerState,
    },
    lastUpdated: "2026-07-21T06:00:00Z",
    ...overrides,
  };
}

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

let mgr: GameContextManager;

beforeEach(() => {
  mgr = new GameContextManager();
});

test("update + getCurrent: roundtrip preserves all fields", () => {
  const ctx = makeContext();
  mgr.update(ctx);
  const loaded = mgr.getCurrent();
  expect(loaded).not.toBeNull();
  expect(loaded!.time.year).toBe(2);
  expect(loaded!.time.season).toBe("summer");
  expect(loaded!.progress.communityCenterBundlesDone).toEqual(["Pantry", "Boiler Room"]);
  expect(loaded!.progress.desertUnlocked).toBe(true);
  expect(loaded!.seasonalResources.plantableCrops).toContain("Corn");
  expect(loaded!.npcStates).toHaveLength(2);
  expect(loaded!.playerState.money).toBe(8500);
  expect(loaded!.playerState.inventory[0]?.name).toBe("Corn");
});

test("getCurrent returns null when no context yet", () => {
  expect(mgr.getCurrent()).toBeNull();
});

test("update overwrites previous context", () => {
  mgr.update(makeContext());
  const updated = makeContext();
  updated.time.day = 15;
  updated.playerState.money = 9999;
  mgr.update(updated);
  const loaded = mgr.getCurrent();
  expect(loaded!.time.day).toBe(15);
  expect(loaded!.playerState.money).toBe(9999);
});

test("isNpcAvailable returns true when NPC is available", () => {
  mgr.update(makeContext());
  expect(mgr.isNpcAvailable("Willy")).toBe(true);
});

test("isNpcAvailable returns false when NPC is unavailable", () => {
  mgr.update(makeContext());
  expect(mgr.isNpcAvailable("Sebastian")).toBe(false);
});

test("isNpcAvailable returns false when NPC not in states", () => {
  mgr.update(makeContext());
  expect(mgr.isNpcAvailable("Unknown")).toBe(false);
});

test("isNpcAvailable returns false when no context exists", () => {
  expect(mgr.isNpcAvailable("Willy")).toBe(false);
});

test("summarizeForDirector formats time + weather in Chinese", () => {
  mgr.update(makeContext());
  const summary = mgr.summarizeForDirector();
  // Time format: Year 2 夏 14 (周二), 晴天
  expect(summary).toContain("Year 2");
  expect(summary).toContain("夏");
  expect(summary).toContain("14");
  expect(summary).toContain("周二");
  expect(summary).toContain("晴天");
});

test("summarizeForDirector formats progress layer", () => {
  mgr.update(makeContext());
  const summary = mgr.summarizeForDirector();
  // Bundles: 2 done out of 6 (Pantry, Boiler Room done)
  expect(summary).toMatch(/社区中心.*2.*6/);
  expect(summary).toContain("沙漠");
  expect(summary).toContain("温室");
});

test("summarizeForDirector formats seasonal resources", () => {
  mgr.update(makeContext());
  const summary = mgr.summarizeForDirector();
  // Plantable crops listed (English or Chinese — implementation may translate later)
  expect(summary).toMatch(/玉米|Corn/);
  expect(summary).toMatch(/虹鳟|Rainbow/);
});

test("summarizeForDirector formats player state", () => {
  mgr.update(makeContext());
  const summary = mgr.summarizeForDirector();
  expect(summary).toContain("Farm");
  expect(summary).toContain("95");
  expect(summary).toContain("8500");
  // Inventory items appear (English or Chinese — implementation may translate later)
  expect(summary).toMatch(/玉米|Corn/);
});

test("summarizeForDirector handles festival day specially", () => {
  mgr.update(
    makeContext({
      time: { ...makeContext().time, isFestivalDay: true, festivalName: "夏威夷宴会" },
    }),
  );
  const summary = mgr.summarizeForDirector();
  expect(summary).toContain("夏威夷宴会");
});

test("summarizeForDirector returns placeholder when no context", () => {
  const summary = mgr.summarizeForDirector();
  expect(typeof summary).toBe("string");
  expect(summary.length).toBeGreaterThan(0);
});

test("summarizeForNpc produces NPC-specific summary", () => {
  mgr.update(makeContext());
  const summary = mgr.summarizeForNpc("Willy");
  // NPC-specific fields
  expect(summary).toContain("Willy");
  expect(summary).toContain("Town");
  expect(summary).toContain("500"); // friendship
  // Also includes game time (shared context)
  expect(summary).toMatch(/(夏|Summer)/);
});

test("summarizeForNpc falls back when NPC not found", () => {
  mgr.update(makeContext());
  const summary = mgr.summarizeForNpc("Nonexistent");
  // Should still produce something usable, mentioning the missing NPC
  expect(typeof summary).toBe("string");
  expect(summary.length).toBeGreaterThan(0);
});

test("summarizeForNpc returns placeholder when no context", () => {
  const summary = mgr.summarizeForNpc("Willy");
  expect(typeof summary).toBe("string");
  expect(summary.length).toBeGreaterThan(0);
});
