import { test, expect } from "bun:test";
import { ProtocolAdapter } from "../../src/protocol-adapter";
import { NpcPromptLoader } from "../../src/npc-prompt-loader";
import { PromptBuilder } from "../../src/prompt-builder";
import { VercelAIProvider } from "@valley/core";
import { StardewAgentRegistry } from "../../src/stardew-agent-registry";
import { mkdtempSync, rmSync } from "fs";
import { join } from "path";
import { tmpdir } from "os";
import { resolve } from "path";

const DATA_PATH = resolve(import.meta.dir, "../../data/npc_prompts.json");

const baseSnapshot = {
  season: "summer", day: 28, time: "14:30", weather: "sunny",
  location: "Town", npcTile: { x: 32, y: 18 },
  nearbyObjects: "", friendship: 250, npcState: "IDLE",
  inventory: [], farmerName: "农夫",
};

function makeAdapterWithFailingThenRecoveringLLM(failCount: number): { adapter: ProtocolAdapter; dir: string; triggerRecovery: () => void } {
  const dir = mkdtempSync(join(tmpdir(), "valley-fallback-"));
  const loader = new NpcPromptLoader(DATA_PATH);
  const builder = new PromptBuilder(loader);
  const provider = new VercelAIProvider({
    provider: "minimax",
    apiKey: "fake",
    model: "fake",
    baseUrl: "http://localhost:9999",
    maxRetries: 1,
  });
  let calls = 0;
  let recovered = false;
  provider._setCallOverride(async () => {
    calls++;
    if (!recovered && calls <= failCount) {
      throw new Error("LLM down");
    }
    return { content: "我恢复了。", toolCalls: [] };
  });
  const registry = new StardewAgentRegistry({
    promptBuilder: builder,
    llmProvider: provider,
    agentsDir: dir,
  });
  const adapter = new ProtocolAdapter(registry);
  return {
    adapter, dir,
    triggerRecovery: () => { recovered = true; },
  };
}

test("LLM failure returns fallback response with fallback=true and emote action", async () => {
  const { adapter, dir } = makeAdapterWithFailingThenRecoveringLLM(99);
  try {
    const resp = await adapter.handleDialogue({
      type: "dialogue", requestId: "fb-1", npcName: "Abigail",
      playerInput: "你好", worldSnapshot: baseSnapshot,
    });
    expect(resp.type).toBe("dialogue_response");
    expect(resp.fallback).toBe(true);
    expect(resp.speech.length).toBeGreaterThan(0);
    expect(resp.actions.some(a => a.tool === "emote")).toBe(true);
  } finally {
    await adapter.flushPendingSaves();
    rmSync(dir, { recursive: true, force: true });
  }
});

test("persistent LLM failure keeps returning fallback (does not crash)", async () => {
  const { adapter, dir } = makeAdapterWithFailingThenRecoveringLLM(99);
  try {
    for (let i = 0; i < 5; i++) {
      const resp = await adapter.handleDialogue({
        type: "dialogue", requestId: `fb-persist-${i}`, npcName: "Abigail",
        playerInput: `回合 ${i}`, worldSnapshot: baseSnapshot,
      });
      expect(resp.fallback).toBe(true);
      expect(resp.speech.length).toBeGreaterThan(0);
    }
  } finally {
    await adapter.flushPendingSaves();
    rmSync(dir, { recursive: true, force: true });
  }
});

test("LLM recovery after fallback returns to normal LLM response (fallback=false)", async () => {
  const { adapter, dir, triggerRecovery } = makeAdapterWithFailingThenRecoveringLLM(2);
  try {
    const r1 = await adapter.handleDialogue({
      type: "dialogue", requestId: "rec-1", npcName: "Abigail",
      playerInput: "你好", worldSnapshot: baseSnapshot,
    });
    expect(r1.fallback).toBe(true);

    const r2 = await adapter.handleDialogue({
      type: "dialogue", requestId: "rec-2", npcName: "Abigail",
      playerInput: "你好", worldSnapshot: baseSnapshot,
    });
    expect(r2.fallback).toBe(true);

    triggerRecovery();

    const r3 = await adapter.handleDialogue({
      type: "dialogue", requestId: "rec-3", npcName: "Abigail",
      playerInput: "你好", worldSnapshot: baseSnapshot,
    });
    expect(r3.fallback).toBeFalsy();
    expect(r3.speech).toBe("我恢复了。");
  } finally {
    await adapter.flushPendingSaves();
    rmSync(dir, { recursive: true, force: true });
  }
});
