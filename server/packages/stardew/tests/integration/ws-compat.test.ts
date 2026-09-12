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

function makeAdapterWithOverride(callOverride: (msg: unknown) => Promise<unknown>): { adapter: ProtocolAdapter; dir: string } {
  const dir = mkdtempSync(join(tmpdir(), "valley-ws-compat-"));
  const loader = new NpcPromptLoader(DATA_PATH);
  const builder = new PromptBuilder(loader);
  const provider = new VercelAIProvider({
    provider: "minimax",
    apiKey: "fake",
    model: "fake",
    baseUrl: "http://localhost:9999",
  });
  provider._setCallOverride(callOverride as any);
  const registry = new StardewAgentRegistry({
    promptBuilder: builder,
    llmProvider: provider,
    agentsDir: dir,
  });
  const adapter = new ProtocolAdapter(registry);
  return { adapter, dir };
}

const baseSnapshot = {
  season: "summer", day: 28, time: "14:30", weather: "sunny",
  location: "Town", npcTile: { x: 32, y: 18 },
  nearbyObjects: "2 villagers", friendship: 250, npcState: "IDLE",
  inventory: [], farmerName: "新来的农夫",
};

test("accepts camelCase field names from C# WebSocketClient JSON", async () => {
  const { adapter, dir } = makeAdapterWithOverride(async () => ({
    content: "你好。",
    toolCalls: [],
  }));
  try {
    const rawJson = {
      type: "dialogue",
      requestId: "req-camel-1",
      npcName: "Abigail",
      playerInput: "你好",
      worldSnapshot: baseSnapshot,
    };
    const resp = await adapter.routeMessage(rawJson);
    expect(resp.type).toBe("dialogue_response");
    expect((resp as any).requestId).toBe("req-camel-1");
    expect((resp as any).npcName).toBe("Abigail");
    expect((resp as any).speech).toBe("你好。");
  } finally {
    await adapter.flushPendingSaves();
    rmSync(dir, { recursive: true, force: true });
  }
});

test("ignores null/missing optional fields in WorldSnapshot without throwing", async () => {
  const { adapter, dir } = makeAdapterWithOverride(async () => ({
    content: "嗯。",
    toolCalls: [],
  }));
  try {
    const rawJson = {
      type: "dialogue",
      requestId: "req-null-1",
      npcName: "Abigail",
      playerInput: "hi",
      worldSnapshot: {
        ...baseSnapshot,
        nearbyObjects: null,
        npcState: null,
        inventory: null,
      },
    };
    const resp = await adapter.routeMessage(rawJson);
    expect(resp.type).toBe("dialogue_response");
    expect((resp as any).speech).toBe("嗯。");
  } finally {
    await adapter.flushPendingSaves();
    rmSync(dir, { recursive: true, force: true });
  }
});

test("accepts string enum values (weather: 'sunny' / npcState: 'IDLE')", async () => {
  const { adapter, dir } = makeAdapterWithOverride(async () => ({
    content: "天气不错。",
    toolCalls: [],
  }));
  try {
    const rawJson = {
      type: "dialogue",
      requestId: "req-enum-1",
      npcName: "Abigail",
      playerInput: "天气如何",
      worldSnapshot: {
        ...baseSnapshot,
        weather: "Rainy",
        npcState: "TALK",
      },
    };
    const resp = await adapter.routeMessage(rawJson);
    expect(resp.type).toBe("dialogue_response");
    expect((resp as any).speech).toBe("天气不错。");
  } finally {
    await adapter.flushPendingSaves();
    rmSync(dir, { recursive: true, force: true });
  }
});
