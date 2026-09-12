import { test, expect, describe } from "bun:test";
import { LlmRouter, LLMBillingError, type LlmRouterConfig } from "@valley/core";
import { StardewAgent } from "../src/stardew-agent";
import { AgentMemory } from "../src/agent-memory";
import { NpcPromptLoader } from "../src/npc-prompt-loader";
import { PromptBuilder } from "../src/prompt-builder";
import type { SceneState } from "../src/types";
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

function makeRouterConfig(): LlmRouterConfig {
  return {
    version: 1,
    roles: {
      director: {
        primary: { provider: "minimax", apiKey: "sk-d", model: "M3", baseUrl: "u" },
        fallback: { provider: "deepseek", apiKey: "sk-df", model: "chat", baseUrl: "u" },
      },
      protagonist: {
        primary: { provider: "minimax", apiKey: "sk-p", model: "M2.7", baseUrl: "u" },
        fallback: { provider: "deepseek", apiKey: "sk-pf", model: "chat", baseUrl: "u" },
      },
      npc: {
        primary: { provider: "deepseek", apiKey: "sk-n", model: "chat", baseUrl: "u" },
        fallback: null,
      },
    },
    protagonistNpcs: ["Abigail"],
    enableProtagonistMapping: true,
  };
}

describe("StardewAgent with LlmRouter", () => {
  test("agent.role 按 NPC 名正确绑定 — protagonist 用 protagonist provider", async () => {
    const router = new LlmRouter(makeRouterConfig());
    // protagonist primary 返回专属响应，npc primary 返回不同响应
    router.getProvider("protagonist")._setCallOverride(async () => ({
      content: "",
      toolCalls: [{ id: "tc-1", name: "speak", args: { text: "主角响应" } }],
    }));
    router.getProvider("npc")._setCallOverride(async () => ({
      content: "",
      toolCalls: [{ id: "tc-1", name: "speak", args: { text: "普通NPC响应" } }],
    }));

    const loader = new NpcPromptLoader(DATA_PATH);
    const builder = new PromptBuilder(loader);
    const memory = new AgentMemory("Abigail", "/tmp/x");

    const agent = new StardewAgent({
      name: "Abigail",
      memory,
      promptBuilder: builder,
      llmRouter: router,
      role: router.resolveRole("Abigail"),
      maxTurns: 5,
    });

    const result = await agent.runDialogue("你好", scene);
    // 验证用的是 protagonist 的 provider（返回 "主角响应"）
    expect(result.speech).toBe("主角响应");
  });

  test("agent.role 按 NPC 名正确绑定 — npc 用 npc provider", async () => {
    const router = new LlmRouter(makeRouterConfig());
    router.getProvider("protagonist")._setCallOverride(async () => ({
      content: "",
      toolCalls: [{ id: "tc-1", name: "speak", args: { text: "主角响应" } }],
    }));
    router.getProvider("npc")._setCallOverride(async () => ({
      content: "",
      toolCalls: [{ id: "tc-1", name: "speak", args: { text: "普通NPC响应" } }],
    }));

    const loader = new NpcPromptLoader(DATA_PATH);
    const builder = new PromptBuilder(loader);
    const memory = new AgentMemory("Pierre", "/tmp/x");

    const agent = new StardewAgent({
      name: "Pierre",
      memory,
      promptBuilder: builder,
      llmRouter: router,
      role: router.resolveRole("Pierre"),
      maxTurns: 5,
    });

    const result = await agent.runDialogue("你好", scene);
    // 验证用的是 npc 的 provider（返回 "普通NPC响应"）
    expect(result.speech).toBe("普通NPC响应");
  });

  test("billing 错误时 agent 透明回退，调用方无感知", async () => {
    const router = new LlmRouter(makeRouterConfig());
    // protagonist primary 抛 LLMBillingError，fallback 返回成功
    router.getProvider("protagonist")._setCallOverride(async () => {
      throw new LLMBillingError("HTTP 402");
    });
    router.getProviderFallback("protagonist")!._setCallOverride(async () => ({
      content: "",
      toolCalls: [{ id: "tc-1", name: "speak", args: { text: "回退响应" } }],
    }));

    const loader = new NpcPromptLoader(DATA_PATH);
    const builder = new PromptBuilder(loader);
    const memory = new AgentMemory("Abigail", "/tmp/x");

    const agent = new StardewAgent({
      name: "Abigail",
      memory,
      promptBuilder: builder,
      llmRouter: router,
      role: router.resolveRole("Abigail"),
      maxTurns: 5,
    });

    const result = await agent.runDialogue("你好", scene);
    // 验证回退成功，调用方无感知（不抛错，返回 fallback 的响应）
    expect(result.speech).toBe("回退响应");
  });

  test("llmRouter 模式构造缺 llmRouter 和 llmProvider 时抛错", () => {
    const loader = new NpcPromptLoader(DATA_PATH);
    const builder = new PromptBuilder(loader);
    const memory = new AgentMemory("Abigail", "/tmp/x");

    expect(() => new StardewAgent({
      name: "Abigail",
      memory,
      promptBuilder: builder,
    })).toThrow(/llmRouter or llmProvider/);
  });

  test("llmProvider 模式向后兼容 — 不传 llmRouter 时仍可用", async () => {
    const router = new LlmRouter(makeRouterConfig());
    // 借用 router 的 protagonist provider 作为单 provider
    const provider = router.getProvider("protagonist");
    provider._setCallOverride(async () => ({
      content: "",
      toolCalls: [{ id: "tc-1", name: "speak", args: { text: "单模式响应" } }],
    }));

    const loader = new NpcPromptLoader(DATA_PATH);
    const builder = new PromptBuilder(loader);
    const memory = new AgentMemory("Abigail", "/tmp/x");

    const agent = new StardewAgent({
      name: "Abigail",
      memory,
      promptBuilder: builder,
      llmProvider: provider,
      maxTurns: 5,
    });

    const result = await agent.runDialogue("你好", scene);
    expect(result.speech).toBe("单模式响应");
  });
});
