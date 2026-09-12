import { test, expect } from "bun:test";
import { StardewAgentRegistry } from "../src/stardew-agent-registry";
import { NpcPromptLoader } from "../src/npc-prompt-loader";
import { PromptBuilder } from "../src/prompt-builder";
import { VercelAIProvider } from "@valley/core";
import { mkdtempSync, rmSync } from "fs";
import { join } from "path";
import { tmpdir } from "os";
import { resolve } from "path";

const DATA_PATH = resolve(import.meta.dir, "../data/npc_prompts.json");

function makeRegistry(): { registry: StardewAgentRegistry; dir: string } {
  const dir = mkdtempSync(join(tmpdir(), "valley-registry-test-"));
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
    toolCalls: [{ id: "tc-1", name: "speak", args: { text: "你好。" } }],
  }));
  const registry = new StardewAgentRegistry({
    promptBuilder: builder,
    llmProvider: provider,
    agentsDir: dir,
  });
  return { registry, dir };
}

test("getOrCreate returns same instance for same NPC", () => {
  const { registry, dir } = makeRegistry();
  try {
    const a1 = registry.getOrCreate("Abigail");
    const a2 = registry.getOrCreate("Abigail");
    expect(a1).toBe(a2);
    expect(a1.name).toBe("Abigail");
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("getOrCreate returns different instances for different NPCs", () => {
  const { registry, dir } = makeRegistry();
  try {
    const a = registry.getOrCreate("Abigail");
    const h = registry.getOrCreate("Haley");
    expect(a).not.toBe(h);
    expect(a.name).toBe("Abigail");
    expect(h.name).toBe("Haley");
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("listAgents returns all registered NPCs", () => {
  const { registry, dir } = makeRegistry();
  try {
    registry.getOrCreate("Abigail");
    registry.getOrCreate("Haley");
    registry.getOrCreate("Sebastian");
    const names = registry.listAgents().map((a) => a.name).sort();
    expect(names).toEqual(["Abigail", "Haley", "Sebastian"]);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("acquireLock serializes concurrent dialogue for same NPC", async () => {
  const { registry, dir } = makeRegistry();
  try {
    const lock1 = await registry.acquireLock("Abigail");
    expect(lock1).toBe(true);

    // Second acquire should fail (NPC busy)
    const lock2 = await registry.acquireLock("Abigail");
    expect(lock2).toBe(false);

    registry.releaseLock("Abigail");
    const lock3 = await registry.acquireLock("Abigail");
    expect(lock3).toBe(true);
    registry.releaseLock("Abigail");
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("different NPCs can acquire lock simultaneously", async () => {
  const { registry, dir } = makeRegistry();
  try {
    const l1 = await registry.acquireLock("Abigail");
    const l2 = await registry.acquireLock("Haley");
    expect(l1).toBe(true);
    expect(l2).toBe(true);
    registry.releaseLock("Abigail");
    registry.releaseLock("Haley");
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("hasAgent returns false for unknown NPC, true after getOrCreate", () => {
  const { registry, dir } = makeRegistry();
  try {
    expect(registry.hasAgent("Abigail")).toBe(false);
    registry.getOrCreate("Abigail");
    expect(registry.hasAgent("Abigail")).toBe(true);
    expect(registry.hasAgent("Haley")).toBe(false);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("getMemoryFilePath returns expected path", () => {
  const { registry, dir } = makeRegistry();
  try {
    const path = registry.getMemoryFilePath("Abigail");
    expect(path).toBe(join(dir, "Abigail_memory.json"));
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});


test("updateActualState / getActualState round-trip", () => {
  const { registry, dir } = makeRegistry();
  try {
    expect(registry.getActualState("Abigail")).toBeUndefined();
    registry.updateActualState("Abigail", "FOLLOW");
    expect(registry.getActualState("Abigail")).toBe("FOLLOW");
    registry.updateActualState("Abigail", "IDLE");
    expect(registry.getActualState("Abigail")).toBe("IDLE");
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("updateActualState creates agent lazily for unknown NPC", () => {
  const { registry, dir } = makeRegistry();
  try {
    expect(registry.hasAgent("Sebastian")).toBe(false);
    registry.updateActualState("Sebastian", "FOLLOW");
    expect(registry.hasAgent("Sebastian")).toBe(true);
    expect(registry.getActualState("Sebastian")).toBe("FOLLOW");
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("getActualState is independent per NPC", () => {
  const { registry, dir } = makeRegistry();
  try {
    registry.updateActualState("Abigail", "FOLLOW");
    registry.updateActualState("Haley", "IDLE");
    expect(registry.getActualState("Abigail")).toBe("FOLLOW");
    expect(registry.getActualState("Haley")).toBe("IDLE");
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});
