import { test, expect } from "bun:test";
import { StardewAgent } from "../src/stardew-agent";
import { StardewAgentRegistry } from "../src/stardew-agent-registry";
import { ProtocolAdapter } from "../src/protocol-adapter";
import { AgentMemory } from "../src/agent-memory";
import { NpcPromptLoader } from "../src/npc-prompt-loader";
import { PromptBuilder } from "../src/prompt-builder";
import { VercelAIProvider } from "@valley/core";
import type { SceneState, WorldSnapshot } from "../src/types";
import { mkdtempSync, rmSync } from "fs";
import { join } from "path";
import { tmpdir } from "os";
import { resolve } from "path";

const DATA_PATH = resolve(import.meta.dir, "../data/npc_prompts.json");

const scene: SceneState = {
    season: "summer", day: 28, timeStr: "14:30", weather: "sunny",
    location: "Town", nearbyObjects: "2 villagers", farmerName: "新来的农夫",
    friendship: 250, npcState: "IDLE", inventory: [],
  playerMoney: null,
  npcLocation: null,
  npcMoney: null,
  npcInventory: null,
    npcTile: { x: 0, y: 0 },
    playerHeldItem: null,
    currentGoal: null,
    npcMood: null,
    npcRecentEvents: null,
    npcWorkingOn: null,
    npcOwedMoney: null,
};

const snapshot: WorldSnapshot = {
    season: "summer", day: 28, time: "14:30", weather: "sunny",
    location: "Town", npcTile: { x: 0, y: 0 },
    nearbyObjects: "", friendship: 250, npcState: "IDLE",
    inventory: [], farmerName: "新来的农夫",
};

interface AgentFixture {
    agent: StardewAgent;
    memory: AgentMemory;
    provider: VercelAIProvider;
    dir: string;
}

function makeAgentFixture(maxTurns: number = 5): AgentFixture {
    const dir = mkdtempSync(join(tmpdir(), "valley-validator-"));
    const loader = new NpcPromptLoader(DATA_PATH);
    const builder = new PromptBuilder(loader);
    const memory = new AgentMemory("Abigail", join(dir, "Abigail_memory.json"));
    const provider = new VercelAIProvider({
        provider: "minimax",
        apiKey: "fake",
        model: "fake",
        baseUrl: "http://localhost:9999",
    });
    const agent = new StardewAgent({
        name: "Abigail",
        memory,
        promptBuilder: builder,
        llmProvider: provider,
        maxTurns,
    });
    return { agent, memory, provider, dir };
}

interface FullStackFixture {
    adapter: ProtocolAdapter;
    registry: StardewAgentRegistry;
    provider: VercelAIProvider;
    dir: string;
}

function makeFullStackFixture(): FullStackFixture {
    const dir = mkdtempSync(join(tmpdir(), "valley-validator-stack-"));
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
    const adapter = new ProtocolAdapter(registry);
    return { adapter, registry, provider, dir };
}

// handleDialogue triggers a fire-and-forget memory.save() which races with the
// test cleanup rmSync. Flush a short tick to let pending saves settle before
// the finally block deletes the temp dir, avoiding spurious ENOENT noise.
function flushPendingSaves(): Promise<void> {
    return new Promise((r) => setTimeout(r, 50));
}

test("CJK failure triggers retry, second attempt with Chinese succeeds", async () => {
    const { agent, provider, dir } = makeAgentFixture();
    try {
        let callCount = 0;
        provider._setCallOverride(async () => {
            callCount++;
            if (callCount === 1) {
                // First attempt: English-only speak text → fails CJK ratio check
                return {
                    content: "",
                    toolCalls: [{ id: "tc-1", name: "speak", args: { text: "Hello world" } }],
                };
            }
            // Second attempt: Chinese speak text → passes
            return {
                content: "",
                toolCalls: [{ id: "tc-2", name: "speak", args: { text: "你好啊，新来的农夫。" } }],
            };
        });

        const result = await agent.runDialogue("你好", scene);
        expect(result.speech).toBe("你好啊，新来的农夫。");
        expect(callCount).toBe(2);
    } finally {
        rmSync(dir, { recursive: true, force: true });
    }
});

test("two consecutive CJK failures trigger fallback via protocol-adapter", async () => {
    const { adapter, provider, dir } = makeFullStackFixture();
    try {
        let callCount = 0;
        provider._setCallOverride(async () => {
            callCount++;
            // Both attempts return English — fails validation both times
            return {
                content: "",
                toolCalls: [{ id: `tc-${callCount}`, name: "speak", args: { text: "Hello world" } }],
            };
        });

        const response = await adapter.handleDialogue({
            type: "dialogue",
            requestId: "req-1",
            npcName: "Abigail",
            playerInput: "你好",
            worldSnapshot: snapshot,
        });

        expect(response.fallback).toBe(true);
        expect(callCount).toBe(2);
        await flushPendingSaves();
    } finally {
        rmSync(dir, { recursive: true, force: true });
    }
});

test("empty speech after retry throws (caller handles fallback)", async () => {
    // maxTurns=1: each runOnce calls LLM once; total 2 calls across initial + retry.
    const { agent, provider, dir } = makeAgentFixture(1);
    try {
        let callCount = 0;
        provider._setCallOverride(async () => {
            callCount++;
            // Both attempts: empty content + no toolCalls → extractResult yields speech=""
            return { content: "", toolCalls: [] };
        });

        await expect(agent.runDialogue("你好", scene)).rejects.toThrow(/Output validation failed/);
        expect(callCount).toBe(2);
    } finally {
        rmSync(dir, { recursive: true, force: true });
    }
});
