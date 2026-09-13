import { test, expect, spyOn } from "bun:test";
import { ProtocolAdapter, type ProtocolAdapterOptions } from "../src/protocol-adapter";
import { NpcPromptLoader } from "../src/npc-prompt-loader";
import { PromptBuilder } from "../src/prompt-builder";
import { VercelAIProvider } from "@valley/core";
import { StardewAgentRegistry } from "../src/stardew-agent-registry";
import { mkdtempSync, rmSync } from "fs";
import { join } from "path";
import { tmpdir } from "os";
import { resolve } from "path";
import type { DirectorCommandMessage } from "../src/types";

const DATA_PATH = resolve(import.meta.dir, "../data/npc_prompts.json");

function makeAdapter(options?: ProtocolAdapterOptions): { adapter: ProtocolAdapter; dir: string } {
  const dir = mkdtempSync(join(tmpdir(), "valley-adapter-test-"));
  const loader = new NpcPromptLoader(DATA_PATH);
  const builder = new PromptBuilder(loader);
  const provider = new VercelAIProvider({
    provider: "minimax",
    apiKey: "fake",
    model: "fake",
    baseUrl: "http://localhost:9999",
  });
  provider._setCallOverride(async () => ({
    content: "",
    toolCalls: [{ id: "tc-1", name: "speak", args: { text: "你好啊。" } }],
  }));
  const registry = new StardewAgentRegistry({
    promptBuilder: builder,
    llmProvider: provider,
    agentsDir: dir,
  });
  const adapter = new ProtocolAdapter(registry, options);
  return { adapter, dir };
}

test("handleHello returns hello response with status ok", async () => {
  const { adapter, dir } = makeAdapter();
  try {
    const resp = await adapter.handleHello({
      type: "hello",
      requestId: "req-1",
      modVersion: "1.0.0",
    });
    expect(resp.type).toBe("hello");
    expect(resp.requestId).toBe("req-1");
    expect(resp.status).toBe("ok");
    expect(resp.serverVersion).toBe("0.1.0");
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("handlePing returns pong response", async () => {
  const { adapter, dir } = makeAdapter();
  try {
    const resp = await adapter.handlePing({
      type: "ping",
      requestId: "req-2",
    });
    expect(resp.type).toBe("pong");
    expect(resp.requestId).toBe("req-2");
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("handleActionResult returns ack", async () => {
  const { adapter, dir } = makeAdapter();
  try {
    const resp = await adapter.handleActionResult({
      type: "action_result",
      requestId: "req-4",
      callId: "call-1",
      success: true,
      result: "ok",
    });
    expect(resp.type).toBe("ack");
    expect(resp.requestId).toBe("req-4");
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("handleDialogue returns dialogue response with speech", async () => {
  const { adapter, dir } = makeAdapter();
  try {
    const resp = await adapter.handleDialogue({
      type: "dialogue",
      requestId: "req-5",
      npcName: "Abigail",
      playerInput: "你好",
      worldSnapshot: {
        season: "summer", day: 28, time: "14:30", weather: "sunny",
        location: "Town", npcTile: { x: 32, y: 18 },
        nearbyObjects: "2 villagers", friendship: 250, npcState: "IDLE",
        inventory: [], farmerName: "新来的农夫",
      },
    });
    expect(resp.type).toBe("dialogue_response");
    expect(resp.requestId).toBe("req-5");
    expect(resp.npcName).toBe("Abigail");
    expect(resp.speech).toBe("你好啊。");
    expect(resp.emotion).toBe("Neutral");
  } finally {
    await adapter.flushPendingSaves();
    rmSync(dir, { recursive: true, force: true });
  }
});

test("handleDialogue returns fallback on LLM failure", async () => {
  const dir = mkdtempSync(join(tmpdir(), "valley-adapter-test-"));
  const loader = new NpcPromptLoader(DATA_PATH);
  const builder = new PromptBuilder(loader);
  const provider = new VercelAIProvider({
    provider: "minimax",
    apiKey: "fake",
    model: "fake",
    baseUrl: "http://localhost:9999",
  });
  provider._setCallOverride(async () => {
    throw new Error("LLM down");
  });
  const registry = new StardewAgentRegistry({
    promptBuilder: builder,
    llmProvider: provider,
    agentsDir: dir,
  });
  const adapter = new ProtocolAdapter(registry);
  try {
    const resp = await adapter.handleDialogue({
      type: "dialogue",
      requestId: "req-6",
      npcName: "Abigail",
      playerInput: "你好",
      worldSnapshot: {
        season: "summer", day: 28, time: "14:30", weather: "sunny",
        location: "Town", npcTile: { x: 32, y: 18 },
        nearbyObjects: "2 villagers", friendship: 250, npcState: "IDLE",
        inventory: [], farmerName: "新来的农夫",
      },
    });
    expect(resp.type).toBe("dialogue_response");
    expect(resp.fallback).toBe(true);
    expect(resp.speech.length).toBeGreaterThan(0);
    expect(resp.actions.some((a) => a.tool === "emote")).toBe(true);
  } finally {
    await adapter.flushPendingSaves();
    rmSync(dir, { recursive: true, force: true });
  }
});

test("handleDialogue rejects when NPC is locked (busy)", async () => {
  // R1：adapter 默认等锁 15s，此测试要"立即 BUSY"——显式传 0 保持断言不变且不拖慢测试。
  const { adapter, dir } = makeAdapter({ dialogueLockTimeoutMs: 0 });
  try {
    // Pre-acquire lock
    await adapter["registry"].acquireLock("Abigail");
    const resp = await adapter.handleDialogue({
      type: "dialogue",
      requestId: "req-7",
      npcName: "Abigail",
      playerInput: "你好",
      worldSnapshot: {
        season: "summer", day: 28, time: "14:30", weather: "sunny",
        location: "Town", npcTile: { x: 32, y: 18 },
        nearbyObjects: "2 villagers", friendship: 250, npcState: "IDLE",
        inventory: [], farmerName: "新来的农夫",
      },
    });
    expect(resp.type).toBe("dialogue_response");
    expect(resp.fallback).toBe(true);
    expect(resp.speech).toContain("正在和别人交流");
  } finally {
    await adapter.flushPendingSaves();
    rmSync(dir, { recursive: true, force: true });
  }
});

// R1（2026-09-13 design §3）：dialogueLockTimeoutMs 限时等待——等锁期间释放则正常对话，
// 等满超时才回 BUSY fallback。

test("handleDialogue waits for lock release and proceeds when dialogueLockTimeoutMs allows", async () => {
  const { adapter, dir } = makeAdapter({ dialogueLockTimeoutMs: 800 });
  try {
    await adapter["registry"].acquireLock("Abigail");
    // 持锁方 ~100ms 后释放；第二请求应等到锁并正常对话（非 fallback）。
    setTimeout(() => adapter["registry"].releaseLock("Abigail"), 100);
    const resp = await adapter.handleDialogue({
      type: "dialogue",
      requestId: "req-lock-wait",
      npcName: "Abigail",
      playerInput: "你好",
      worldSnapshot: {
        season: "summer", day: 28, time: "14:30", weather: "sunny",
        location: "Town", npcTile: { x: 32, y: 18 },
        nearbyObjects: "2 villagers", friendship: 250, npcState: "IDLE",
        inventory: [], farmerName: "新来的农夫",
      },
    });
    expect(resp.type).toBe("dialogue_response");
    expect(resp.fallback).toBeUndefined();
    expect(resp.speech).toBe("你好啊。");
  } finally {
    await adapter.flushPendingSaves();
    rmSync(dir, { recursive: true, force: true });
  }
});

test("handleDialogue returns busy fallback when lock wait times out", async () => {
  const { adapter, dir } = makeAdapter({ dialogueLockTimeoutMs: 200 });
  try {
    await adapter["registry"].acquireLock("Abigail");
    // 无人释放 → 等满 200ms 超时 → BUSY fallback（fallbackReason="busy"）。
    const resp = await adapter.handleDialogue({
      type: "dialogue",
      requestId: "req-lock-timeout",
      npcName: "Abigail",
      playerInput: "你好",
      worldSnapshot: {
        season: "summer", day: 28, time: "14:30", weather: "sunny",
        location: "Town", npcTile: { x: 32, y: 18 },
        nearbyObjects: "2 villagers", friendship: 250, npcState: "IDLE",
        inventory: [], farmerName: "新来的农夫",
      },
    });
    expect(resp.type).toBe("dialogue_response");
    expect(resp.fallback).toBe(true);
    expect(resp.fallbackReason).toBe("busy");
    expect(resp.speech).toContain("正在和别人交流");
  } finally {
    adapter["registry"].releaseLock("Abigail");
    await adapter.flushPendingSaves();
    rmSync(dir, { recursive: true, force: true });
  }
});


test("handleConsolidateDay returns ack with correct requestId", async () => {
  const { adapter, dir } = makeAdapter();
  try {
    const resp = await adapter.handleConsolidateDay({
      type: "consolidate_day",
      npcName: "Abigail",
      dateIso: "2026-08-03",
      requestId: "req-consolidate",
    });
    expect(resp).toEqual({ type: "ack", requestId: "req-consolidate" });
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("handleConsolidateDay is idempotent on same npcName|dateIso", async () => {
  const { adapter, dir } = makeAdapter();
  try {
    await adapter.handleConsolidateDay({
      type: "consolidate_day",
      npcName: "Abigail",
      dateIso: "2026-08-03",
      requestId: "req-consolidate-1",
    });
    await adapter.handleConsolidateDay({
      type: "consolidate_day",
      npcName: "Abigail",
      dateIso: "2026-08-03",
      requestId: "req-consolidate-2",
    });

    const seen = (adapter as unknown as { seenConsolidations: Set<string> }).seenConsolidations;
    expect(seen).toEqual(new Set(["Abigail|2026-08-03"]));
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("routeMessage routes consolidate_day to handleConsolidateDay", async () => {
  const { adapter, dir } = makeAdapter();
  try {
    const resp = await adapter.routeMessage({
      type: "consolidate_day",
      npcName: "Haley",
      dateIso: "2026-08-03",
      requestId: "req-route-consolidate",
    });
    expect(resp).toEqual({ type: "ack", requestId: "req-route-consolidate" });
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("handleStateChanged updates registry actualState and returns ack", async () => {
  const { adapter, dir } = makeAdapter();
  try {
    const resp = await adapter.handleStateChanged({
      type: "state_changed",
      npcName: "Abigail",
      previousState: "IDLE",
      newState: "FOLLOW",
      wasForced: false,
      previousStateDurationMs: 1000,
    });
    expect(resp.type).toBe("ack");
    // 验证副作用：registry.actualState 已更新
    const reg = (adapter as unknown as { registry: import("../src/stardew-agent-registry").StardewAgentRegistry }).registry;
    expect(reg.getActualState("Abigail")).toBe("FOLLOW");
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("routeMessage routes state_changed to handleStateChanged", async () => {
  const { adapter, dir } = makeAdapter();
  try {
    const resp = await adapter.routeMessage({
      type: "state_changed",
      npcName: "Haley",
      previousState: "IDLE",
      newState: "FOLLOW",
      wasForced: true,
      previousStateDurationMs: 500,
    });
    expect(resp.type).toBe("ack");
    const reg = (adapter as unknown as { registry: import("../src/stardew-agent-registry").StardewAgentRegistry }).registry;
    expect(reg.getActualState("Haley")).toBe("FOLLOW");
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("handleDialogue returns LLMBillingError-tier fallback when LLM returns HTTP 402", async () => {
  const dir = mkdtempSync(join(tmpdir(), "valley-adapter-test-"));
  const loader = new NpcPromptLoader(DATA_PATH);
  const builder = new PromptBuilder(loader);
  const provider = new VercelAIProvider({
    provider: "minimax",
    apiKey: "fake",
    model: "fake",
    baseUrl: "http://localhost:9999",
  });
  provider._setCallOverride(async () => {
    // Simulate ai-sdk APICallError: statusCode on the error object.
    // VercelAIProvider.withRetry classifies 402 → LLMBillingError (no retry).
    // agent-loop emits ErrorEvent with original error attached.
    // stardew-agent.runOnce rethrows the original LLMBillingError.
    // protocol-adapter catch → rule-engine.buildFallbackResponse hits tier 1.
    throw Object.assign(new Error("Insufficient credits"), { statusCode: 402 });
  });
  const registry = new StardewAgentRegistry({
    promptBuilder: builder,
    llmProvider: provider,
    agentsDir: dir,
  });
  const adapter = new ProtocolAdapter(registry);
  try {
    const resp = await adapter.handleDialogue({
      type: "dialogue",
      requestId: "req-billing",
      npcName: "Abigail",
      playerInput: "你好",
      worldSnapshot: {
        season: "summer", day: 28, time: "14:30", weather: "sunny",
        location: "Town", npcTile: { x: 32, y: 18 },
        nearbyObjects: "2 villagers", friendship: 250, npcState: "IDLE",
        inventory: [], farmerName: "新来的农夫",
      },
    });
    expect(resp.type).toBe("dialogue_response");
    expect(resp.fallback).toBe(true);
    // LLMBillingError tier: speech contains "走神", emotion is "Confused"
    expect(resp.speech).toContain("走神");
    expect(resp.emotion).toBe("Confused");
  } finally {
    await adapter.flushPendingSaves();
    rmSync(dir, { recursive: true, force: true });
  }
});

// __TASK3_LOG_TESTS__

test("log: handleDialogue emits recv and send lines paired", async () => {
  const logSpy = spyOn(console, "log").mockImplementation(() => {});
  const { adapter, dir } = makeAdapter();
  try {
    const resp = await adapter.handleDialogue({
      type: "dialogue",
      requestId: "req-log-1",
      npcName: "Abigail",
      playerInput: "你好啊",
      worldSnapshot: {
        season: "summer", day: 28, time: "14:30", weather: "sunny",
        location: "Town", npcTile: { x: 32, y: 18 },
        nearbyObjects: "2 villagers", friendship: 250, npcState: "IDLE",
        inventory: [], farmerName: "新来的农夫",
      },
    });
    expect(resp.type).toBe("dialogue_response");

    const lines = logSpy.mock.calls.map((c) => String(c[0]));
    expect(lines.some((l) => l.includes("[recv]") && l.includes("dialogue") && l.includes("Abigail") && l.includes("你好啊"))).toBe(true);
    expect(lines.some((l) => l.includes("[send]") && l.includes("dialogue") && l.includes("Abigail"))).toBe(true);
  } finally {
    await adapter.flushPendingSaves();
    rmSync(dir, { recursive: true, force: true });
    logSpy.mockRestore();
  }
});

test("log: handleActionResult emits recv line", async () => {
  const logSpy = spyOn(console, "log").mockImplementation(() => {});
  const { adapter, dir } = makeAdapter();
  try {
    await adapter.handleActionResult({
      type: "action_result",
      requestId: "req-log-2",
      callId: "call-1",
      success: false,
      result: "背包已满",
      reason: "inventoryFull",
    } as any);
    const lines = logSpy.mock.calls.map((c) => String(c[0]));
    expect(lines.some((l) => l.includes("[recv]") && l.includes("action_result") && l.includes("inventoryFull"))).toBe(true);
  } finally {
    rmSync(dir, { recursive: true, force: true });
    logSpy.mockRestore();
  }
});

// === Phase 3 director_command：Director 工具调用（TS→C#）转发 ===

test("handleDirectorCommand forwards to sendToCsharp and returns ack", async () => {
  const sent: unknown[] = [];
  const dir = mkdtempSync(join(tmpdir(), "valley-adapter-director-"));
  const loader = new NpcPromptLoader(DATA_PATH);
  const builder = new PromptBuilder(loader);
  const provider = new VercelAIProvider({
    provider: "minimax",
    apiKey: "fake",
    model: "fake",
    baseUrl: "http://localhost:9999",
  });
  const registry = new StardewAgentRegistry({
    promptBuilder: builder,
    llmProvider: provider,
    agentsDir: dir,
  });
  const adapter = new ProtocolAdapter(registry, { sendToCsharp: (msg) => sent.push(msg) });
  try {
    const cmd: DirectorCommandMessage = {
      type: "director_command",
      tool: "set_npc_mood",
      args: { npc: "Abigail", moodTag: "烦躁" },
      requestId: "req-dc-1",
    };
    const resp = await adapter.routeMessage(cmd);
    expect(resp).toEqual({ type: "ack", requestId: "req-dc-1" });
    // 原样转发给 C#（不改造消息体）
    expect(sent).toHaveLength(1);
    expect(sent[0]).toEqual(cmd);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("handleDirectorCommand without sendToCsharp returns ack and drops", async () => {
  const logSpy = spyOn(console, "log").mockImplementation(() => {});
  const { adapter, dir } = makeAdapter();
  try {
    const cmd: DirectorCommandMessage = {
      type: "director_command",
      tool: "spawn_beat",
      args: { npc: "Abigail", sceneDesc: "午后来酒吧坐坐" },
      requestId: "req-dc-2",
    };
    const resp = await adapter.handleDirectorCommand(cmd);
    expect(resp).toEqual({ type: "ack", requestId: "req-dc-2" });
    const lines = logSpy.mock.calls.map((c) => String(c[0]));
    expect(lines.some((l) => l.includes("[director_command]") && l.includes("dropped"))).toBe(true);
  } finally {
    rmSync(dir, { recursive: true, force: true });
    logSpy.mockRestore();
  }
});
