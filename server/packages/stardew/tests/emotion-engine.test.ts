import { test, expect } from "bun:test";
import { EmotionEngine } from "../src/emotion-engine";
import { ProtocolAdapter } from "../src/protocol-adapter";
import { NpcPromptLoader } from "../src/npc-prompt-loader";
import { PromptBuilder } from "../src/prompt-builder";
import { VercelAIProvider } from "@valley/core";
import { StardewAgentRegistry } from "../src/stardew-agent-registry";
import { mkdtempSync, rmSync } from "fs";
import { join } from "path";
import { tmpdir } from "os";
import { resolve } from "path";

const DATA_PATH = resolve(import.meta.dir, "../data/npc_prompts.json");

/**
 * 2026-08-15 步骤 3：确定性情绪引擎测试。
 * 事件（state_changed reason / action_result 成败）→ 情绪；Director mood 覆盖权；
 * dialogue_response.emotion 注入真实情绪。
 */

test("applyEvent maps state_changed reasons to emotions", () => {
  const engine = new EmotionEngine();

  expect(engine.current("Haley").emotion).toBe("Neutral"); // 无事件 → baseline

  engine.applyEvent("Haley", { kind: "state_changed", detail: "task_completed" });
  expect(engine.current("Haley").emotion).toBe("Happy");
  expect(engine.current("Haley").source).toBe("state_changed:task_completed");

  engine.applyEvent("Haley", { kind: "state_changed", detail: "travel_failed" });
  expect(engine.current("Haley").emotion).toBe("Worried");

  engine.applyEvent("Haley", { kind: "state_changed", detail: "evicted" });
  expect(engine.current("Haley").emotion).toBe("Sad");

  // 未知 reason → 回 baseline（事件不携带情绪信息）
  engine.applyEvent("Haley", { kind: "state_changed", detail: "llm_decision" });
  expect(engine.current("Haley").emotion).toBe("Neutral");
});

test("applyEvent maps action_result success/failure", () => {
  const engine = new EmotionEngine();

  engine.applyEvent("Abigail", { kind: "action_result", detail: "chop_tree", success: true });
  expect(engine.current("Abigail").emotion).toBe("Happy");

  engine.applyEvent("Abigail", { kind: "action_result", detail: "set_goal", success: false });
  expect(engine.current("Abigail").emotion).toBe("Worried");

  // 未知工具：成败给默认情绪
  engine.applyEvent("Abigail", { kind: "action_result", detail: "unknown_tool", success: true });
  expect(engine.current("Abigail").emotion).toBe("Happy");
  engine.applyEvent("Abigail", { kind: "action_result", detail: "unknown_tool", success: false });
  expect(engine.current("Abigail").emotion).toBe("Sad");
});

test("same emotion repeat bumps intensity (capped at 1.0)", () => {
  const engine = new EmotionEngine();
  for (let i = 0; i < 9; i++) {
    engine.applyEvent("Abigail", { kind: "state_changed", detail: "task_completed" });
  }
  const state = engine.current("Abigail");
  expect(state.emotion).toBe("Happy");
  expect(state.intensity).toBeCloseTo(1.0, 5);
});

test("volatility scales event intensity; resetDay returns to baseline", () => {
  const engine = new EmotionEngine({ Sebastian: { baseline: "Neutral", volatility: 0.2 } });
  engine.applyEvent("Sebastian", { kind: "state_changed", detail: "task_completed" });
  expect(engine.current("Sebastian").intensity).toBeCloseTo(0.1, 5); // 0.5 × 0.2

  engine.resetDay("Sebastian");
  expect(engine.current("Sebastian").emotion).toBe("Neutral");
  expect(engine.current("Sebastian").source).toBe("day_started");
});

test("resetAll resets every tracked NPC to baseline (换日全量消退)", () => {
  const engine = new EmotionEngine();
  engine.applyEvent("Haley", { kind: "state_changed", detail: "travel_failed" }); // Worried
  engine.applyEvent("Abigail", { kind: "action_result", detail: "chop_tree", success: true }); // Happy
  engine.applyEvent("Sebastian", { kind: "action_result", detail: "set_goal", success: false }); // Worried

  engine.resetAll();

  expect(engine.current("Haley").emotion).toBe("Neutral");
  expect(engine.current("Haley").source).toBe("day_started");
  expect(engine.current("Abigail").emotion).toBe("Neutral");
  expect(engine.current("Sebastian").emotion).toBe("Neutral");
  // 未跟踪 NPC 无状态，current() 回 baseline（不受 resetAll 影响）
  expect(engine.current("Emily").emotion).toBe("Neutral");
});

test("day_started via adapter resets tracked emotions (handleDayStarted 接线)", async () => {
  const dir = mkdtempSync(join(tmpdir(), "valley-emotion-day-"));
  let adapter: ProtocolAdapter | null = null;
  try {
    const loader = new NpcPromptLoader(DATA_PATH);
    const builder = new PromptBuilder(loader);
    const provider = new VercelAIProvider({
      provider: "minimax", apiKey: "fake", model: "fake", baseUrl: "http://localhost:9999",
    });
    provider._setCallOverride(async () => ({
      content: "",
      toolCalls: [{ id: "tc-1", name: "speak", args: { text: "你好。" } }],
    }));
    const registry = new StardewAgentRegistry({ promptBuilder: builder, llmProvider: provider, agentsDir: dir });
    const engine = new EmotionEngine();
    adapter = new ProtocolAdapter(registry, { emotionEngine: engine });

    // 推入负面情绪
    await adapter.routeMessage({
      type: "state_changed", npcName: "Haley", previousState: "TRAVELING", newState: "IDLE",
      wasForced: false, previousStateDurationMs: 100, reason: "travel_failed",
    });
    expect(engine.current("Haley").emotion).toBe("Worried");

    // 换日 → 情绪回 baseline（此前 handleDayStarted 从不 reset，昨天的情绪会挂进今天的 prompt）
    await adapter.routeMessage({ type: "day_started", requestId: "req-day", dateIso: "Y1_summer_15" });

    expect(engine.current("Haley").emotion).toBe("Neutral");
    expect(engine.current("Haley").source).toBe("day_started");
  } finally {
    await adapter?.flushPendingSaves();
    rmSync(dir, { recursive: true, force: true });
  }
});

test("Director mood overrides engine in dialogue response", async () => {
  const dir = mkdtempSync(join(tmpdir(), "valley-emotion-"));
  let adapter: ProtocolAdapter | null = null;
  try {
    const loader = new NpcPromptLoader(DATA_PATH);
    const builder = new PromptBuilder(loader);
    const provider = new VercelAIProvider({
      provider: "minimax", apiKey: "fake", model: "fake", baseUrl: "http://localhost:9999",
    });
    provider._setCallOverride(async () => ({
      content: "",
      toolCalls: [{ id: "tc-1", name: "speak", args: { text: "你好。" } }],
    }));
    const registry = new StardewAgentRegistry({ promptBuilder: builder, llmProvider: provider, agentsDir: dir });
    const engine = new EmotionEngine();
    adapter = new ProtocolAdapter(registry, { emotionEngine: engine });

    // 先推一个负面事件 → 引擎情绪 Worried
    await adapter.routeMessage({
      type: "state_changed", npcName: "Haley", previousState: "TRAVELING", newState: "IDLE",
      wasForced: false, previousStateDurationMs: 100, reason: "travel_failed",
    });
    expect(engine.current("Haley").emotion).toBe("Worried");

    const snap = {
      type: "dialogue" as const,
      requestId: "req-e1",
      npcName: "Haley",
      playerInput: "你还好吗",
      worldSnapshot: {
        season: "summer", day: 28, time: "14:30", weather: "sunny",
        location: "Town", npcTile: { x: 32, y: 18 }, nearbyObjects: "",
        friendship: 250, npcState: "IDLE", inventory: [], farmerName: "农夫",
        playerMoney: 500, npcLocation: "Town", npcMoney: 1000, npcInventory: [],
      },
    };
    // 无 Director mood → 引擎情绪注入 dialogue_response
    const resp1 = await adapter.routeMessage(snap);
    expect(resp1.type).toBe("dialogue_response");
    if (resp1.type === "dialogue_response") expect(resp1.emotion).toBe("Worried");

    // Director set_npc_mood 覆盖（worldSnapshot.npcMood 非空）→ 响应用 mood
    const resp2 = await adapter.routeMessage({
      ...snap,
      requestId: "req-e2",
      worldSnapshot: { ...snap.worldSnapshot, npcMood: "Happy" },
    });
    expect(resp2.type).toBe("dialogue_response");
    if (resp2.type === "dialogue_response") expect(resp2.emotion).toBe("Happy");
  } finally {
    await adapter?.flushPendingSaves();
    rmSync(dir, { recursive: true, force: true });
  }
});
