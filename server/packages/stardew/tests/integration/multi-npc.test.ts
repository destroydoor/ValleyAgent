import { test, expect } from "bun:test";
import { ProtocolAdapter } from "../../src/protocol-adapter";
import { NpcPromptLoader } from "../../src/npc-prompt-loader";
import { PromptBuilder } from "../../src/prompt-builder";
import { VercelAIProvider } from "@valley/core";
import type { LlmMessage } from "@valley/core";
import { StardewAgentRegistry } from "../../src/stardew-agent-registry";
import { mkdtempSync, rmSync, readFileSync, existsSync } from "fs";
import { join } from "path";
import { tmpdir } from "os";
import { resolve } from "path";

const DATA_PATH = resolve(import.meta.dir, "../../data/npc_prompts.json");

function makeAdapter(): { adapter: ProtocolAdapter; dir: string; registry: StardewAgentRegistry } {
  const dir = mkdtempSync(join(tmpdir(), "valley-multi-npc-"));
  const loader = new NpcPromptLoader(DATA_PATH);
  const builder = new PromptBuilder(loader);
  const provider = new VercelAIProvider({
    provider: "minimax",
    apiKey: "fake",
    model: "fake",
    baseUrl: "http://localhost:9999",
  });
  // Use Chinese-only speeches so OutputValidator's CJK ratio check passes.
  // These tests verify multi-NPC concurrency + memory isolation, not validator behavior.
  const speeches: Record<string, string> = {
    Abigail: "我是阿比盖尔。",
    Sebastian: "我是塞巴斯蒂安。",
  };
  provider._setCallOverride(async (messages: LlmMessage[]) => {
    const sysMsg = messages.find(m => m.role === "system");
    const sysContent = sysMsg?.content ?? "";
    const match = sysContent.match(/你是([^\s。]+)。/);
    const npcName = match?.[1] ?? "";
    return {
      content: speeches[npcName] ?? "我不知道。",
      toolCalls: [],
    };
  });
  const registry = new StardewAgentRegistry({
    promptBuilder: builder,
    llmProvider: provider,
    agentsDir: dir,
  });
  const adapter = new ProtocolAdapter(registry);
  return { adapter, dir, registry };
}

const snapshotFor = (_npcName: string) => ({
  season: "summer", day: 28, time: "14:30", weather: "sunny",
  location: "Town", npcTile: { x: 10, y: 10 },
  nearbyObjects: "", friendship: 100, npcState: "IDLE",
  inventory: [], farmerName: "农夫",
});

test("two NPCs dialogue concurrently return their own speech", async () => {
  const { adapter, dir } = makeAdapter();
  try {
    const [r1, r2] = await Promise.all([
      adapter.handleDialogue({
        type: "dialogue", requestId: "r1", npcName: "Abigail",
        playerInput: "你是谁", worldSnapshot: snapshotFor("Abigail"),
      }),
      adapter.handleDialogue({
        type: "dialogue", requestId: "r2", npcName: "Sebastian",
        playerInput: "你是谁", worldSnapshot: snapshotFor("Sebastian"),
      }),
    ]);
    expect(r1.npcName).toBe("Abigail");
    expect(r1.speech).toBe("我是阿比盖尔。");
    expect(r2.npcName).toBe("Sebastian");
    expect(r2.speech).toBe("我是塞巴斯蒂安。");
  } finally {
    await adapter.flushPendingSaves();
    rmSync(dir, { recursive: true, force: true });
  }
});

test("each NPC has independent memory file (no cross-contamination)", async () => {
  const { adapter, dir } = makeAdapter();
  try {
    await adapter.handleDialogue({
      type: "dialogue", requestId: "m1", npcName: "Abigail",
      playerInput: "我叫张三", worldSnapshot: snapshotFor("Abigail"),
    });
    await adapter.handleDialogue({
      type: "dialogue", requestId: "m2", npcName: "Sebastian",
      playerInput: "我叫李四", worldSnapshot: snapshotFor("Sebastian"),
    });

    // Flush fire-and-forget memory.save() to disk before reading files
    await adapter.flushPendingSaves();

    const abigailFile = join(dir, "Abigail_memory.json");
    const sebastianFile = join(dir, "Sebastian_memory.json");
    expect(existsSync(abigailFile)).toBe(true);
    expect(existsSync(sebastianFile)).toBe(true);

    const abigailMem = JSON.parse(readFileSync(abigailFile, "utf-8"));
    const sebastianMem = JSON.parse(readFileSync(sebastianFile, "utf-8"));

    const abigailText = JSON.stringify(abigailMem.conversationHistory);
    expect(abigailText).toContain("张三");
    expect(abigailText).not.toContain("李四");

    const sebastianText = JSON.stringify(sebastianMem.conversationHistory);
    expect(sebastianText).toContain("李四");
    expect(sebastianText).not.toContain("张三");
  } finally {
    await adapter.flushPendingSaves();
    rmSync(dir, { recursive: true, force: true });
  }
});

test("same NPC concurrent requests are serialized (lock rejects second)", async () => {
  const { adapter, dir, registry } = makeAdapter();
  try {
    await registry.acquireLock("Abigail");

    const resp = await adapter.handleDialogue({
      type: "dialogue", requestId: "locked-1", npcName: "Abigail",
      playerInput: "你好", worldSnapshot: snapshotFor("Abigail"),
    });
    expect(resp.fallback).toBe(true);
    expect(resp.speech).toContain("正在和别人交流");

    registry.releaseLock("Abigail");
  } finally {
    await adapter.flushPendingSaves();
    rmSync(dir, { recursive: true, force: true });
  }
});
