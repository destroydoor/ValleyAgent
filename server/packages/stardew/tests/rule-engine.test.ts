import { test, expect } from "bun:test";
import { RuleEngine } from "../src/rule-engine";
import { LLMBillingError, LLMUnavailableError } from "@valley/core";
import type { DialogueRequest } from "../src/types";

const req: DialogueRequest = {
  type: "dialogue",
  requestId: "req-1",
  npcName: "Abigail",
  playerInput: "你好",
  worldSnapshot: {
    season: "summer", day: 28, time: "14:30", weather: "sunny",
    location: "Town", npcTile: { x: 32, y: 18 },
    nearbyObjects: "2 villagers", friendship: 250, npcState: "IDLE",
    inventory: [], farmerName: "新来的农夫",
  },
};

test("buildFallbackResponse for LLMBillingError", () => {
  const engine = new RuleEngine();
  const err = new LLMBillingError("insufficient quota");
  const resp = engine.buildFallbackResponse(req, err);
  expect(resp.type).toBe("dialogue_response");
  expect(resp.requestId).toBe("req-1");
  expect(resp.npcName).toBe("Abigail");
  expect(resp.speech).toContain("走神");
  expect(resp.emotion).toBe("Confused");
  expect(resp.fallback).toBe(true);
  expect(resp.actions.some((a) => a.tool === "emote")).toBe(true);
});

test("buildFallbackResponse for LLMUnavailableError", () => {
  const engine = new RuleEngine();
  const err = new LLMUnavailableError("timeout");
  const resp = engine.buildFallbackResponse(req, err);
  expect(resp.speech).toContain("说不出来");
  expect(resp.emotion).toBe("Tired");
  expect(resp.fallback).toBe(true);
});

test("buildFallbackResponse for generic Error", () => {
  const engine = new RuleEngine();
  const err = new Error("something went wrong");
  const resp = engine.buildFallbackResponse(req, err);
  expect(resp.speech).toBe("......");
  expect(resp.emotion).toBe("Neutral");
  expect(resp.fallback).toBe(true);
  expect(resp.actions).toEqual([{ tool: "emote", args: { emote_id: "question" } }]);
});

test("buildFallbackResponse for non-Error thrown value", () => {
  const engine = new RuleEngine();
  const resp = engine.buildFallbackResponse(req, "string error");
  expect(resp.speech).toBe("......");
  expect(resp.fallback).toBe(true);
});

test("buildFallbackResponse always returns non-empty speech", () => {
  const engine = new RuleEngine();
  for (const err of [
    new LLMBillingError("x"),
    new LLMUnavailableError("x"),
    new Error("x"),
    "string",
    null,
    undefined,
  ]) {
    const resp = engine.buildFallbackResponse(req, err as unknown as Error);
    expect(resp.speech.length).toBeGreaterThan(0);
  }
});

test("buildFallbackResponse includes emote action", () => {
  const engine = new RuleEngine();
  const resp = engine.buildFallbackResponse(req, new Error("x"));
  expect(resp.actions.length).toBeGreaterThan(0);
  expect(resp.actions[0]!.tool).toBe("emote");
});

// R2（2026-09-13 design §6 S1/S2）：fallbackReason 机器可读降级原因——
// 按异常类型分档，wire 层透传 C#/房客端用于灰字诊断。

test("buildFallbackResponse maps error types to machine-readable fallbackReason", () => {
  const engine = new RuleEngine();

  const billing = engine.buildFallbackResponse(req, new LLMBillingError("insufficient quota"));
  expect(billing.fallback).toBe(true);
  expect(billing.fallbackReason).toBe("billing");

  const unavailable = engine.buildFallbackResponse(req, new LLMUnavailableError("timeout"));
  expect(unavailable.fallback).toBe(true);
  expect(unavailable.fallbackReason).toBe("unavailable");

  const generic = engine.buildFallbackResponse(req, new Error("something went wrong"));
  expect(generic.fallback).toBe(true);
  expect(generic.fallbackReason).toBe("llm_error");
});
