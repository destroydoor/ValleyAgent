// Tests for ReActGuard soft-brake controller (Task 5).
// Run: bun test tests/react-guard.test.ts

import { test, expect, beforeEach } from "bun:test";
import { ReActGuard } from "../src/react-guard";
import type { GuardContext, ReActStepRecord } from "../src/react-guard";
import type { Beat } from "../src/types";

// ---------------------------------------------------------------------------
// Factory helpers
// ---------------------------------------------------------------------------

function makeBeat(overrides: Partial<Beat> = {}): Beat {
  return {
    id: "b1",
    npcName: "Willy",
    triggerTime: "10:00",
    windowEnd: "12:00",
    directive: "去海边看看玩家",
    context: {
      reasonGenerated: "玩家连续钓鱼 3 天",
      playerProfileSnapshot: {} as Beat["context"]["playerProfileSnapshot"],
      gameContextSnapshot: {} as Beat["context"]["gameContextSnapshot"],
      recentBeats: [],
    },
    status: "active",
    ...overrides,
  };
}

function makeCtx(overrides: Partial<GuardContext> = {}): GuardContext {
  return {
    beat: makeBeat(),
    accumulatedTokens: 1000,
    consecutiveLlmFailures: 0,
    consecutiveToolFailures: 0,
    npcTile: { x: 10, y: 20 },
    playerTile: { x: 12, y: 22 },
    playerLeftAt: null,
    recentSteps: [],
    currentGameTime: "11:00",
    ...overrides,
  };
}

function step(record: Partial<ReActStepRecord>): ReActStepRecord {
  const out: ReActStepRecord = {
    npcTileAfter: record.npcTileAfter ?? { x: 10, y: 20 },
    timestamp: record.timestamp ?? Date.now(),
  };
  // exactOptionalPropertyTypes: only assign optional fields when defined.
  if (record.toolName !== undefined) out.toolName = record.toolName;
  if (record.argsHash !== undefined) out.argsHash = record.argsHash;
  return out;
}

// ---------------------------------------------------------------------------
// Tests — each brake condition + non-trigger + reset
// ---------------------------------------------------------------------------

let guard: ReActGuard;

beforeEach(() => {
  guard = new ReActGuard();
});

test("player_left triggers when elapsed since playerLeftAt exceeds threshold", () => {
  const ctx = makeCtx({
    playerLeftAt: Date.now() - 200_000, // 200s ago > 120s default threshold
  });
  const trigger = guard.check(ctx);
  expect(trigger).not.toBeNull();
  expect(trigger!.reason).toBe("player_left");
  expect(trigger!.detail).toMatch(/player/i);
});

test("player_left does NOT trigger when playerLeftAt is null (player present)", () => {
  const ctx = makeCtx({ playerLeftAt: null });
  expect(guard.check(ctx)).toBeNull();
});

test("player_left does NOT trigger when elapsed is below threshold", () => {
  const ctx = makeCtx({
    playerLeftAt: Date.now() - 60_000, // 60s ago < 120s threshold
  });
  expect(guard.check(ctx)).toBeNull();
});

test("token_budget_exceeded triggers when accumulatedTokens exceeds maxTokensPerBeat", () => {
  const ctx = makeCtx({
    accumulatedTokens: 10_000, // > default 8000
  });
  const trigger = guard.check(ctx);
  expect(trigger).not.toBeNull();
  expect(trigger!.reason).toBe("token_budget_exceeded");
});

test("token_budget_exceeded does NOT trigger when budget not exhausted", () => {
  const ctx = makeCtx({
    accumulatedTokens: 5000, // < default 8000
  });
  expect(guard.check(ctx)).toBeNull();
});

test("llm_failures triggers when consecutiveLlmFailures >= maxLlmFailures", () => {
  const ctx = makeCtx({
    consecutiveLlmFailures: 3, // == default maxLlmFailures
  });
  const trigger = guard.check(ctx);
  expect(trigger).not.toBeNull();
  expect(trigger!.reason).toBe("llm_failures");
});

test("tool_failures triggers when consecutiveToolFailures >= maxToolFailures", () => {
  const ctx = makeCtx({
    consecutiveToolFailures: 3,
  });
  const trigger = guard.check(ctx);
  expect(trigger).not.toBeNull();
  expect(trigger!.reason).toBe("tool_failures");
});

test("repeated_tool_calls triggers when last N steps have same tool+argsHash", () => {
  const ctx = makeCtx({
    recentSteps: [
      step({ toolName: "speak", argsHash: "h1" }),
      step({ toolName: "speak", argsHash: "h1" }),
      step({ toolName: "speak", argsHash: "h1" }), // 3 in a row (default max)
    ],
  });
  const trigger = guard.check(ctx);
  expect(trigger).not.toBeNull();
  expect(trigger!.reason).toBe("repeated_tool_calls");
});

test("repeated_tool_calls does NOT trigger when steps vary", () => {
  const ctx = makeCtx({
    recentSteps: [
      step({ toolName: "speak", argsHash: "h1" }),
      step({ toolName: "emote", argsHash: "h2" }),
      step({ toolName: "speak", argsHash: "h1" }),
    ],
  });
  expect(guard.check(ctx)).toBeNull();
});

test("no_progress triggers when last N steps have same npcTileAfter", () => {
  const ctx = makeCtx({
    recentSteps: [
      step({ npcTileAfter: { x: 5, y: 5 } }),
      step({ npcTileAfter: { x: 5, y: 5 } }),
      step({ npcTileAfter: { x: 5, y: 5 } }),
      step({ npcTileAfter: { x: 5, y: 5 } }),
      step({ npcTileAfter: { x: 5, y: 5 } }), // 5 in a row (default max)
    ],
  });
  const trigger = guard.check(ctx);
  expect(trigger).not.toBeNull();
  expect(trigger!.reason).toBe("no_progress");
});

test("no_progress does NOT trigger when NPC has moved", () => {
  const ctx = makeCtx({
    recentSteps: [
      step({ npcTileAfter: { x: 5, y: 5 } }),
      step({ npcTileAfter: { x: 6, y: 5 } }),
      step({ npcTileAfter: { x: 7, y: 5 } }),
    ],
  });
  expect(guard.check(ctx)).toBeNull();
});

test("window_expired triggers when currentGameTime > beat.windowEnd", () => {
  const ctx = makeCtx({
    beat: makeBeat({ windowEnd: "11:30" }),
    currentGameTime: "11:45",
  });
  const trigger = guard.check(ctx);
  expect(trigger).not.toBeNull();
  expect(trigger!.reason).toBe("window_expired");
});

test("window_expired does NOT trigger when currentGameTime <= windowEnd", () => {
  const ctx = makeCtx({
    beat: makeBeat({ windowEnd: "12:00" }),
    currentGameTime: "12:00", // exactly at window end is OK (still inside)
  });
  expect(guard.check(ctx)).toBeNull();
});

test("check returns null when no brake conditions are met", () => {
  const ctx = makeCtx({
    accumulatedTokens: 1000,
    consecutiveLlmFailures: 0,
    consecutiveToolFailures: 0,
    playerLeftAt: null,
    recentSteps: [
      step({ toolName: "speak", argsHash: "h1", npcTileAfter: { x: 1, y: 1 } }),
      step({ toolName: "emote", argsHash: "h2", npcTileAfter: { x: 2, y: 1 } }),
    ],
    currentGameTime: "11:00",
  });
  expect(guard.check(ctx)).toBeNull();
});

test("priority: player_left wins over token_budget_exceeded when both apply", () => {
  const ctx = makeCtx({
    playerLeftAt: Date.now() - 200_000, // player_left
    accumulatedTokens: 10_000,         // token_budget_exceeded
  });
  const trigger = guard.check(ctx);
  expect(trigger!.reason).toBe("player_left");
});

test("priority: window_expired wins over llm_failures when both apply", () => {
  const ctx = makeCtx({
    beat: makeBeat({ windowEnd: "10:30" }),
    currentGameTime: "11:00",         // window_expired
    consecutiveLlmFailures: 5,         // llm_failures
  });
  const trigger = guard.check(ctx);
  expect(trigger!.reason).toBe("window_expired");
});

test("custom config overrides defaults", () => {
  const g = new ReActGuard({ maxLlmFailures: 1, maxTokensPerBeat: 100 });
  // maxLlmFailures=1: single failure should trigger (keep other fields clean
  // so token_budget_exceeded — a higher-priority brake — does NOT fire first)
  const t1 = g.check(
    makeCtx({ consecutiveLlmFailures: 1, accumulatedTokens: 0 }),
  );
  expect(t1?.reason).toBe("llm_failures");
  // maxTokensPerBeat=100: 200 tokens should trigger (keep llmFailures clean)
  const t2 = g.check(
    makeCtx({ accumulatedTokens: 200, consecutiveLlmFailures: 0 }),
  );
  expect(t2?.reason).toBe("token_budget_exceeded");
});

test("reset() does not throw and does not affect subsequent checks", () => {
  expect(() => guard.reset()).not.toThrow();
  // After reset, a clean context should still return null
  expect(guard.check(makeCtx())).toBeNull();
  // And a brake condition should still trigger correctly
  const ctx = makeCtx({ accumulatedTokens: 10_000 });
  expect(guard.check(ctx)?.reason).toBe("token_budget_exceeded");
});

test("repeated_tool_calls requires steps to have both toolName and argsHash", () => {
  // Steps with no argsHash should not count as "identical" tool calls.
  const ctx = makeCtx({
    recentSteps: [
      step({ toolName: "speak" }),
      step({ toolName: "speak" }),
      step({ toolName: "speak" }),
    ],
  });
  // Without argsHash we can't confidently say "same call" — be lenient.
  expect(guard.check(ctx)).toBeNull();
});

test("repeated_tool_calls respects custom maxRepeatedToolCalls", () => {
  const g = new ReActGuard({ maxRepeatedToolCalls: 2 });
  const ctx = makeCtx({
    recentSteps: [
      step({ toolName: "speak", argsHash: "h1" }),
      step({ toolName: "speak", argsHash: "h1" }), // 2 in a row triggers
    ],
  });
  expect(g.check(ctx)?.reason).toBe("repeated_tool_calls");
});
