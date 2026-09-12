import { test, expect } from "bun:test";
import { StardewAgent } from "../src/stardew-agent";
import { AgentMemory } from "../src/agent-memory";
import { NpcPromptLoader } from "../src/npc-prompt-loader";
import { PromptBuilder } from "../src/prompt-builder";
import { VercelAIProvider } from "@valley/core";
import type { SceneState } from "../src/types";
import { mkdtempSync, rmSync } from "fs";
import { join, resolve } from "path";
import { tmpdir } from "os";

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

function makeMockLlmProvider(): VercelAIProvider {
  const provider = new VercelAIProvider({
    provider: "minimax",
    apiKey: "fake-key",
    model: "fake-model",
    baseUrl: "http://localhost:9999",
  });
  // Override the call to return a canned response with tool call
  provider._setCallOverride(async () => ({
    content: "（思考中）",
    toolCalls: [
      { id: "tc-1", name: "speak", args: { text: "你好啊，新来的农夫。" } },
    ],
  }));
  return provider;
}

test("runDialogue renders unknown failure reason in Chinese", async () => {
  const loader = new NpcPromptLoader(DATA_PATH);
  const builder = new PromptBuilder(loader);
  const memory = new AgentMemory("Abigail", "/tmp/x");
  const provider = makeMockLlmProvider();
  const originalBuildPrompt = builder.buildDialogueSystemPrompt.bind(builder);
  let toolResultsText = "";
  builder.buildDialogueSystemPrompt = (agentMemory, currentScene, npcName, feedback, actualState) => {
    toolResultsText = feedback ?? "";
    return originalBuildPrompt(agentMemory, currentScene, npcName, feedback, actualState);
  };
  const agent = new StardewAgent({
    name: "Abigail",
    memory,
    promptBuilder: builder,
    llmProvider: provider,
  });

  await agent.runDialogue("你好", scene, [{
    callId: "call-unknown-reason",
    tool: "set_state",
    success: false,
    reason: "completelyUnknownEnumValue",
  }]);

  expect(toolResultsText).toContain("未知原因");
  expect(toolResultsText).not.toContain("completelyUnknownEnumValue");
});

test("StardewAgent constructs with all dependencies", () => {
  const loader = new NpcPromptLoader(DATA_PATH);
  const builder = new PromptBuilder(loader);
  const memory = new AgentMemory("Abigail", "/tmp/x");
  const provider = makeMockLlmProvider();

  const agent = new StardewAgent({
    name: "Abigail",
    memory,
    promptBuilder: builder,
    llmProvider: provider,
    maxTurns: 5,
  });

  expect(agent.name).toBe("Abigail");
  expect(agent.isIdle()).toBe(true);
});

test("StardewAgent.runDialogue returns speech + actions from LLM tool call", async () => {
  const loader = new NpcPromptLoader(DATA_PATH);
  const builder = new PromptBuilder(loader);
  const memory = new AgentMemory("Abigail", "/tmp/x");
  const provider = makeMockLlmProvider();

  const agent = new StardewAgent({
    name: "Abigail",
    memory,
    promptBuilder: builder,
    llmProvider: provider,
    maxTurns: 5,
  });

  const result = await agent.runDialogue("你好", scene);
  expect(result.speech).toBe("你好啊，新来的农夫。");
  expect(result.actions).toEqual([]);
  expect(result.emotion).toBe("Neutral");
});

test("StardewAgent.runDialogue records player + npc conversation in memory", async () => {
  const loader = new NpcPromptLoader(DATA_PATH);
  const builder = new PromptBuilder(loader);
  const memory = new AgentMemory("Abigail", "/tmp/x");
  const provider = makeMockLlmProvider();

  const agent = new StardewAgent({
    name: "Abigail",
    memory,
    promptBuilder: builder,
    llmProvider: provider,
    maxTurns: 5,
  });

  await agent.runDialogue("你好", scene);
  expect(memory.conversationHistory).toHaveLength(2);
  expect(memory.conversationHistory[0]!.role).toBe("player");
  expect(memory.conversationHistory[0]!.text).toBe("你好");
  expect(memory.conversationHistory[1]!.role).toBe("npc");
  expect(memory.conversationHistory[1]!.text).toBe("你好啊，新来的农夫。");
});

test("StardewAgent.runDialogue captures non-speak tool calls as actions", async () => {
  const loader = new NpcPromptLoader(DATA_PATH);
  const builder = new PromptBuilder(loader);
  const memory = new AgentMemory("Abigail", "/tmp/x");
  const provider = makeMockLlmProvider();
  // Override to return speak + emote + set_state
  provider._setCallOverride(async () => ({
    content: "",
    toolCalls: [
      { id: "tc-1", name: "emote", args: { emote_id: "heart" } },
      { id: "tc-2", name: "set_state", args: { state: "TALK" } },
      { id: "tc-3", name: "speak", args: { text: "很高兴见到你。" } },
    ],
  }));

  const agent = new StardewAgent({
    name: "Abigail",
    memory,
    promptBuilder: builder,
    llmProvider: provider,
    maxTurns: 5,
  });

  const result = await agent.runDialogue("你好", scene);
  expect(result.speech).toBe("很高兴见到你。");
  const actionTools = result.actions.map((a) => a.tool).sort();
  expect(actionTools).toEqual(["emote", "set_state"]);
});

test("StardewAgent.runDialogue falls back to raw LLM text when no speak tool", async () => {
  const loader = new NpcPromptLoader(DATA_PATH);
  const builder = new PromptBuilder(loader);
  const memory = new AgentMemory("Abigail", "/tmp/x");
  const provider = makeMockLlmProvider();
  provider._setCallOverride(async () => ({
    content: "哦，是你啊。有什么事吗？",
    toolCalls: [],
  }));

  const agent = new StardewAgent({
    name: "Abigail",
    memory,
    promptBuilder: builder,
    llmProvider: provider,
    maxTurns: 5,
  });

  const result = await agent.runDialogue("你好", scene);
  expect(result.speech).toBe("哦，是你啊。有什么事吗？");
  expect(result.actions).toEqual([]);
});

test("StardewAgent.runDialogue with multi-turn LLM (speak on 2nd turn)", async () => {
  const loader = new NpcPromptLoader(DATA_PATH);
  const builder = new PromptBuilder(loader);
  const memory = new AgentMemory("Abigail", "/tmp/x");
  const provider = makeMockLlmProvider();

  let callCount = 0;
  provider._setCallOverride(async () => {
    callCount++;
    if (callCount === 1) {
      return {
        content: "",
        toolCalls: [{ id: "tc-1", name: "get_info", args: { query: "date" } }],
      };
    }
    return {
      content: "",
      toolCalls: [{ id: "tc-2", name: "speak", args: { text: "现在是夏天啊。" } }],
    };
  });

  const agent = new StardewAgent({
    name: "Abigail",
    memory,
    promptBuilder: builder,
    llmProvider: provider,
    maxTurns: 5,
  });

  const result = await agent.runDialogue("现在是什么季节？", scene);
  expect(result.speech).toBe("现在是夏天啊。");
});

test("StardewAgent.runDialogue throws when LLM fails (caller handles fallback)", async () => {
  const loader = new NpcPromptLoader(DATA_PATH);
  const builder = new PromptBuilder(loader);
  const memory = new AgentMemory("Abigail", "/tmp/x");
  const provider = makeMockLlmProvider();
  provider._setCallOverride(async () => {
    throw new Error("LLM API down");
  });

  const agent = new StardewAgent({
    name: "Abigail",
    memory,
    promptBuilder: builder,
    llmProvider: provider,
    maxTurns: 5,
  });

  await expect(agent.runDialogue("你好", scene)).rejects.toThrow(/LLM API down/);
});

test("StardewAgent uses maxTurns=5 by default", () => {
  const loader = new NpcPromptLoader(DATA_PATH);
  const builder = new PromptBuilder(loader);
  const memory = new AgentMemory("Abigail", "/tmp/x");
  const provider = makeMockLlmProvider();

  const agent = new StardewAgent({
    name: "Abigail",
    memory,
    promptBuilder: builder,
    llmProvider: provider,
  });

  // Access internal config maxTurns via behavior: 6 LLM calls without speak → should stop at 5.
  // Mock returns Chinese-dominant content so OutputValidator does not trigger a retry
  // (this test verifies maxTurns, not the retry path).
  let calls = 0;
  provider._setCallOverride(async () => {
    calls++;
    return { content: `第${calls}次回复`, toolCalls: [] };
  });

  return agent.runDialogue("test", scene).then(() => {
    expect(calls).toBe(5);
  });
});

test("runDialogue returns friendshipDelta when LLM calls evaluate_friendship", async () => {
  const loader = new NpcPromptLoader(DATA_PATH);
  const builder = new PromptBuilder(loader);
  const memory = new AgentMemory("Abigail", "/tmp/x");
  const provider = makeMockLlmProvider();
  provider._setCallOverride(async () => ({
    content: "",
    toolCalls: [
      { id: "tc-1", name: "speak", args: { text: "谢谢你的夸奖。" } },
      { id: "tc-2", name: "evaluate_friendship", args: { delta: 5, reason: "他夸了我的头发" } },
    ],
  }));

  const agent = new StardewAgent({
    name: "Abigail",
    memory,
    promptBuilder: builder,
    llmProvider: provider,
    maxTurns: 5,
  });

  const result = await agent.runDialogue("你的头发真好看", scene);
  expect(result.friendshipDelta).toBe(5);
  expect(result.friendshipReason).toBe("他夸了我的头发");
  // evaluate_friendship 是内部评估工具，被过滤掉，不进入 actions
  expect(result.actions.some((a) => a.tool === "evaluate_friendship")).toBe(false);
});

test("runDialogue returns friendshipDelta=0 when LLM does not call evaluate_friendship", async () => {
  const loader = new NpcPromptLoader(DATA_PATH);
  const builder = new PromptBuilder(loader);
  const memory = new AgentMemory("Abigail", "/tmp/x");
  const provider = makeMockLlmProvider();
  // makeMockLlmProvider 默认只返回 speak tool call（无 evaluate_friendship）

  const agent = new StardewAgent({
    name: "Abigail",
    memory,
    promptBuilder: builder,
    llmProvider: provider,
    maxTurns: 5,
  });

  const result = await agent.runDialogue("你好", scene);
  expect(result.friendshipDelta).toBe(0);
  expect(result.friendshipReason).toBe("");
});

// ── M2 修复（2026-08-23）：好感钳制 / 兜底台词玩家桶路由 ──

test("friendshipDelta is clamped to [-100, 100] (防幻觉泵值)", async () => {
  const loader = new NpcPromptLoader(DATA_PATH);
  const builder = new PromptBuilder(loader);
  const memory = new AgentMemory("Abigail", "/tmp/x");
  const provider = makeMockLlmProvider();
  provider._setCallOverride(async () => ({
    content: "",
    toolCalls: [
      { id: "tc-1", name: "speak", args: { text: "哇。" } },
      { id: "tc-2", name: "evaluate_friendship", args: { delta: 2500, reason: "幻觉" } },
    ],
  }));

  const agent = new StardewAgent({ name: "Abigail", memory, promptBuilder: builder, llmProvider: provider });
  const pumped = await agent.runDialogue("你好", scene);
  expect(pumped.friendshipDelta).toBe(100); // +2500 被钳到上限

  provider._setCallOverride(async () => ({
    content: "",
    toolCalls: [
      { id: "tc-1", name: "speak", args: { text: "唉。" } },
      { id: "tc-2", name: "evaluate_friendship", args: { delta: -2500, reason: "幻觉" } },
    ],
  }));
  const drained = await agent.runDialogue("你真讨厌", scene);
  expect(drained.friendshipDelta).toBe(-100); // -2500 被钳到下限
});

test("fallback raw-text NPC line goes into the current player's bucket when playerId present", async () => {
  const dir = mkdtempSync(join(tmpdir(), "agent-fallback-pid-"));
  try {
    const loader = new NpcPromptLoader(DATA_PATH);
    const builder = new PromptBuilder(loader);
    const memory = new AgentMemory("Abigail", join(dir, "Abigail_memory.json"));
    await memory.getPlayerMemory("player-a", "阿明"); // protocol-adapter 对话前预加载
    const provider = makeMockLlmProvider();
    // 无 speak 工具调用 → extractResult 走 rawText 兜底
    provider._setCallOverride(async () => ({ content: "今天矿洞见。", toolCalls: [] }));

    const agent = new StardewAgent({ name: "Abigail", memory, promptBuilder: builder, llmProvider: provider });
    const result = await agent.runDialogue("待会儿去哪？", scene, [], undefined, "player-a", "阿明");

    expect(result.speech).toBe("今天矿洞见。");
    const bucket = memory.getLoadedPlayerMemory("player-a")!;
    expect(bucket.conversationHistory.map((e) => e.role)).toEqual(["player", "npc"]);
    expect(bucket.conversationHistory[1]!.text).toBe("今天矿洞见。");
    expect(memory.conversationHistory).toHaveLength(0); // 世界桶不收
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});
