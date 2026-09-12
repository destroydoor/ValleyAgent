import { test, expect } from "bun:test";
import { StardewAgentRegistry } from "../src/stardew-agent-registry";
import { ProtocolAdapter } from "../src/protocol-adapter";
import { NpcPromptLoader } from "../src/npc-prompt-loader";
import { PromptBuilder } from "../src/prompt-builder";
import { VercelAIProvider } from "@valley/core";
import { AgentMemory } from "../src/agent-memory";
import { buildStardewTools } from "../src/stardew-tools";
import type { ToolContext } from "../src/stardew-tools";
import type { WorldSnapshot, AdjustResultMessage, SceneState } from "../src/types";
import { mkdtempSync, rmSync } from "fs";
import { join } from "path";
import { tmpdir } from "os";
import { resolve } from "path";

const DATA_PATH = resolve(import.meta.dir, "../data/npc_prompts.json");

function makeRegistry(): { registry: StardewAgentRegistry; dir: string } {
  const dir = mkdtempSync(join(tmpdir(), "valley-registry-feedback-"));
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

function makeAdapter(): { adapter: ProtocolAdapter; registry: StardewAgentRegistry; dir: string } {
  const { registry, dir } = makeRegistry();
  const adapter = new ProtocolAdapter(registry);
  return { adapter, registry, dir };
}

test("enqueueToolResult stores per NPC; drainToolResults peeks; clearToolResults clears", () => {
  const { registry, dir } = makeRegistry();
  try {
    registry.enqueueToolResult("Haley", { callId: "c1", tool: "give_gift", success: true, result: "已送出" });
    registry.enqueueToolResult("Haley", { callId: "c2", tool: "give_item", success: false, result: "玩家背包已满" });
    registry.enqueueToolResult("Abigail", { callId: "c3", tool: "emote", success: true, result: "ok" });

    const haleyResults = registry.drainToolResults("Haley");
    expect(haleyResults.length).toBe(2);
    expect(haleyResults[1]!.result).toContain("背包已满");
    // drain 后调 clearToolResults 再 drain 期望空
    registry.clearToolResults("Haley");
    expect(registry.drainToolResults("Haley").length).toBe(0);
    // Abigail 的不受 Haley drain 影响
    expect(registry.drainToolResults("Abigail").length).toBe(1);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("queue is capped at 10 entries per NPC (oldest dropped)", () => {
  const { registry, dir } = makeRegistry();
  try {
    for (let i = 0; i < 15; i++) {
      registry.enqueueToolResult("Haley", { callId: `c${i}`, tool: "emote", success: true, result: `r${i}` });
    }
    const results = registry.drainToolResults("Haley");
    expect(results.length).toBe(10);
    expect(results[0]!.callId).toBe("c5"); // 最老的 5 条被丢弃
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("drainToolResults returns empty array for unknown NPC", () => {
  const { registry, dir } = makeRegistry();
  try {
    const results = registry.drainToolResults("UnknownNpc");
    expect(results).toEqual([]);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("drainToolResults peeks (returns copy without clearing); clearToolResults clears", () => {
  const { registry, dir } = makeRegistry();
  try {
    registry.enqueueToolResult("Haley", { callId: "c1", tool: "emote", success: true, result: "ok" });
    registry.drainToolResults("Haley");
    // 在两次 drain 之间插入 clearToolResults；第二次 drain 返回空数组
    registry.clearToolResults("Haley");
    const results = registry.drainToolResults("Haley");
    expect(results).toEqual([]);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("action_result message is routed to NPC tool-result queue", async () => {
  const { adapter, registry, dir } = makeAdapter();
  try {
    await adapter.routeMessage({
      type: "action_result",
      requestId: "r1",
      callId: "c1",
      npcName: "Haley",
      tool: "give_gift",
      success: false,
      result: "玩家背包已满",
    });
    const results = registry.drainToolResults("Haley");
    expect(results.length).toBe(1);
    expect(results[0]!.success).toBe(false);
    expect(results[0]!.tool).toBe("give_gift");
    expect(results[0]!.result).toContain("背包已满");
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("action_result without npcName is silently dropped (no crash)", async () => {
  const { adapter, registry, dir } = makeAdapter();
  try {
    // Old C# client without npcName field — should not crash, should not enqueue
    const resp = await adapter.routeMessage({
      type: "action_result",
      requestId: "r3",
      callId: "c3",
      success: true,
      result: "ok",
    });
    expect(resp.type).toBe("ack");
    expect(registry.drainToolResults("Abigail").length).toBe(0);
    expect(registry.drainToolResults("Haley").length).toBe(0);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

// === Task F: dialogue drains queue → prompt feedback section + failure → memory ===

const feedbackSnapshot: WorldSnapshot = {
  season: "summer", day: 28, time: "14:30", weather: "sunny",
  location: "Town", npcTile: { x: 32, y: 18 },
  nearbyObjects: "", friendship: 250, npcState: "IDLE",
  inventory: [], farmerName: "农夫",
};

interface FullStack {
  adapter: ProtocolAdapter;
  registry: StardewAgentRegistry;
  dir: string;
  getLastSystemPrompt: () => string;
  getMemoryOf: (npcName: string) => AgentMemory;
}

function makeFullStack(): FullStack {
  const dir = mkdtempSync(join(tmpdir(), "valley-fb-stack-"));
  const loader = new NpcPromptLoader(DATA_PATH);
  const builder = new PromptBuilder(loader);
  const provider = new VercelAIProvider({
    provider: "minimax",
    apiKey: "fake",
    model: "fake",
    baseUrl: "http://localhost:9999",
  });
  let lastSystemPrompt = "";
  provider._setCallOverride(async (messages) => {
    // Capture the system prompt (first message in the LLM call)
    if (messages.length > 0 && messages[0]!.role === "system") {
      lastSystemPrompt = messages[0]!.content as string;
    }
    return {
      content: "",
      toolCalls: [{ id: "tc-1", name: "speak", args: { text: "好的。" } }],
    };
  });
  const registry = new StardewAgentRegistry({
    promptBuilder: builder,
    llmProvider: provider,
    agentsDir: dir,
  });
  const adapter = new ProtocolAdapter(registry);
  return {
    adapter, registry, dir,
    getLastSystemPrompt: () => lastSystemPrompt,
    getMemoryOf: (npcName: string) => {
      const agent = registry.getOrCreate(npcName);
      // Access private memory via bracket notation (TS allows this for testing)
      return (agent as unknown as { memory: AgentMemory }).memory;
    },
  };
}

function makeDialogueRequest(npcName: string, playerInput: string) {
  return {
    type: "dialogue" as const,
    requestId: `req-${Math.random().toString(36).slice(2, 8)}`,
    npcName,
    playerInput,
    worldSnapshot: feedbackSnapshot,
  };
}

// handleDialogue triggers a fire-and-forget memory.save() which races with the
// test cleanup rmSync. Flush a short tick to let pending saves settle before
// the finally block deletes the temp dir, avoiding spurious ENOENT noise.
function flushPendingSaves(): Promise<void> {
  return new Promise((r) => setTimeout(r, 50));
}

test("dialogue drains tool-result queue into prompt feedback section", async () => {
  const { adapter, registry, dir, getLastSystemPrompt } = makeFullStack();
  try {
    registry.enqueueToolResult("Haley", {
      callId: "c1", tool: "give_gift", success: false, result: "玩家背包已满",
    });
    await adapter.routeMessage(makeDialogueRequest("Haley", "再送我一次"));
    const prompt = getLastSystemPrompt();
    expect(prompt).toContain("上次行动结果");
    expect(prompt).toContain("玩家背包已满");
    // Queue cleared after dialogue (clearToolResults called on success path)
    expect(registry.drainToolResults("Haley").length).toBe(0);
    await flushPendingSaves();
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("failed tool results are written to NPC memory after dialogue", async () => {
  const { adapter, dir, getMemoryOf } = makeFullStack();
  try {
    await adapter.routeMessage({
      type: "action_result",
      requestId: "ar-1",
      callId: "c1",
      npcName: "Haley",
      tool: "give_gift",
      success: false,
      result: "玩家背包已满",
    });
    await adapter.routeMessage(makeDialogueRequest("Haley", "再试一次"));
    const memory = getMemoryOf("Haley");
    expect(
      memory.shortTermMemories.some(
        (m) => m.text.includes("背包已满") && m.importance >= 4
      )
    ).toBe(true);
    await flushPendingSaves();
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("dialogue without pending tool results omits feedback section", async () => {
  const { adapter, dir, getLastSystemPrompt } = makeFullStack();
  try {
    await adapter.routeMessage(makeDialogueRequest("Abigail", "你好"));
    const prompt = getLastSystemPrompt();
    expect(prompt).not.toContain("上次行动结果");
    await flushPendingSaves();
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

// === §4.4: drainToolResults peek semantics + handleDialogue evaporation fix ===

test("handleDialogue preserves tool-result queue on LLM failure (no evaporation)", async () => {
  const dir = mkdtempSync(join(tmpdir(), "valley-fb-evap-"));
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
    registry.enqueueToolResult("Haley", {
      callId: "c1", tool: "give_gift", success: false, result: "玩家背包已满",
    });
    const resp = await adapter.routeMessage(makeDialogueRequest("Haley", "再送我一次"));
    expect(resp.type).toBe("dialogue_response");
    if (resp.type === "dialogue_response") {
      expect(resp.fallback).toBe(true);
    }
    // 队列保留，未蒸发——下轮可重新注入
    expect(registry.drainToolResults("Haley").length).toBe(1);
    await flushPendingSaves();
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("handleDialogue clears tool-result queue on success", async () => {
  const { adapter, registry, dir } = makeFullStack();
  try {
    registry.enqueueToolResult("Haley", {
      callId: "c1", tool: "give_gift", success: false, result: "玩家背包已满",
    });
    const resp = await adapter.routeMessage(makeDialogueRequest("Haley", "再送我一次"));
    expect(resp.type).toBe("dialogue_response");
    if (resp.type === "dialogue_response") {
      expect(resp.fallback).not.toBe(true);
    }
    // 成功后队列被清空
    expect(registry.drainToolResults("Haley").length).toBe(0);
    await flushPendingSaves();
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

// === §2.5: action_result.reason routing + Chinese rendering ===

test("action_result with reason is routed to ToolResultRecord", async () => {
  const { adapter, registry, dir } = makeAdapter();
  try {
    await adapter.routeMessage({
      type: "action_result",
      requestId: "r-reason-1",
      callId: "c1",
      npcName: "Haley",
      tool: "give_gift",
      success: false,
      result: "玩家背包已满",
      reason: "inventoryFull",
    });
    const results = registry.drainToolResults("Haley");
    expect(results.length).toBe(1);
    expect(results[0]!.reason).toBe("inventoryFull");
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("failed tool result with reason renders Chinese in prompt feedback section", async () => {
  const { adapter, registry, dir, getLastSystemPrompt } = makeFullStack();
  try {
    registry.enqueueToolResult("Haley", {
      callId: "c1", tool: "give_gift", success: false, result: "玩家背包已满", reason: "inventoryFull",
    });
    await adapter.routeMessage(makeDialogueRequest("Haley", "再送我一次"));
    const prompt = getLastSystemPrompt();
    expect(prompt).toContain("背包已满");
    expect(prompt).toContain("give_gift：失败");
    // 括号内为中文映射（"inventoryFull" → "背包已满"）
    expect(prompt).toContain("give_gift：失败（背包已满）");
    await flushPendingSaves();
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("failed tool result without reason falls back to original format", async () => {
  const { adapter, registry, dir, getLastSystemPrompt } = makeFullStack();
  try {
    registry.enqueueToolResult("Haley", {
      callId: "c1", tool: "give_gift", success: false, result: "玩家背包已满",
    });
    await adapter.routeMessage(makeDialogueRequest("Haley", "再送我一次"));
    const prompt = getLastSystemPrompt();
    expect(prompt).toContain("give_gift：失败");
    // 无 reason 时该反馈行不应出现括号化原因
    // （精确断言该行无括号原因；prompt 其他段可能含全角括号，故针对反馈行断言）
    expect(prompt).not.toContain("give_gift：失败（");
    await flushPendingSaves();
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

// === §4.1.1 方案 B：friendship_eval 并回 dialogue 主路径 ===

test("dialogue_response carries friendshipDelta when non-zero", async () => {
  const dir = mkdtempSync(join(tmpdir(), "valley-fb-friend-"));
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
    toolCalls: [
      { id: "tc-1", name: "speak", args: { text: "谢谢你的夸奖。" } },
      { id: "tc-2", name: "evaluate_friendship", args: { delta: 10, reason: "他夸了我的头发" } },
    ],
  }));
  const registry = new StardewAgentRegistry({
    promptBuilder: builder,
    llmProvider: provider,
    agentsDir: dir,
  });
  const adapter = new ProtocolAdapter(registry);
  try {
    const resp = await adapter.routeMessage(makeDialogueRequest("Haley", "你的头发真好看"));
    expect(resp.type).toBe("dialogue_response");
    if (resp.type === "dialogue_response") {
      expect(resp.friendshipDelta).toBe(10);
    }
    await flushPendingSaves();
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("dialogue_response omits friendshipDelta when zero", async () => {
  const { adapter, dir } = makeFullStack();
  try {
    // makeFullStack 的 provider 只返回 speak（无 evaluate_friendship）→ delta=0
    const resp = await adapter.routeMessage(makeDialogueRequest("Haley", "你好"));
    expect(resp.type).toBe("dialogue_response");
    if (resp.type === "dialogue_response") {
      expect(resp.friendshipDelta).toBe(undefined);
    }
    await flushPendingSaves();
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

// === E4-2：receive_payment 两阶段落地（action_result → 收款/失败记忆）===

/**
 * 步骤 2 同步收款测试台：receive_payment 工具 + 注入回执的 fake economy executor。
 * 收款在工具内同步完成（账本→execute_adjust→回执），不再走 action_result 记忆分支。
 */
function makeSyncPaymentHarness(receipt: AdjustResultMessage) {
  const memory = new AgentMemory("Abigail", "/tmp/pay-sync");
  const scene: SceneState = {
    season: "summer", day: 28, timeStr: "14:30", weather: "sunny",
    location: "Mine", nearbyObjects: "", farmerName: "农夫",
    friendship: 250, npcState: "IDLE", inventory: [],
    npcTile: { x: 0, y: 0 }, playerMoney: 500, npcLocation: "Mine",
    npcMoney: 100, npcInventory: [], playerHeldItem: null, currentGoal: null,
    npcMood: null, npcRecentEvents: null, npcWorkingOn: null, npcOwedMoney: null,
  };
  const ctx: ToolContext = {
    memory,
    scene,
    inventory: [],
    givenToPlayer: [],
    log: [],
    economy: {
      npcName: "Abigail",
      adjust: async () => receipt,
      getMoney: () => 100,
      getInventory: () => [],
    },
  };
  const pay = buildStardewTools(ctx).find((t) => t.name === "receive_payment")!;
  return { memory, ctx, pay };
}

test("receive_payment 同步执行 success → 写收款记忆（金额/缘由，步骤 2）", async () => {
  const { memory, pay } = makeSyncPaymentHarness({
    type: "adjust_result", requestId: "r", instructionId: "i",
    npcName: "Abigail", success: true, steps: [],
  });

  const result = await pay.execute({ amount: 500, reason: "挖矿工钱" });

  expect(result.isError).toBeFalsy();
  expect(
    memory.shortTermMemories.some(
      (m) => m.text === "农场主付了我 500g（挖矿工钱）" && m.importance === 5.0 && m.entryType === "event",
    ),
  ).toBe(true);
});

test("receive_payment 同步执行 fail → 写失败记忆（失败码走中文映射，步骤 2）", async () => {
  const { memory, pay } = makeSyncPaymentHarness({
    type: "adjust_result", requestId: "r", instructionId: "i",
    npcName: "Abigail", success: false, failureCode: "insufficientFunds", steps: [],
  });

  const result = await pay.execute({ amount: 500 });

  expect(result.isError).toBe(true);
  expect(String(result.content)).toContain("钱不够");
  expect(
    memory.shortTermMemories.some(
      (m) => m.text.includes("农场主想付我 500g 但没付成") && m.text.includes("钱不够")
        && m.importance === 3.0 && m.entryType === "event",
    ),
  ).toBe(true);
});

test("receive_payment 同步执行 fail 无详情 → 中文兜底", async () => {
  const { memory, pay } = makeSyncPaymentHarness({
    type: "adjust_result", requestId: "r", instructionId: "i",
    npcName: "Abigail", success: false, failureCode: "internalError", steps: [],
  });

  const result = await pay.execute({ amount: 500 });

  expect(result.isError).toBe(true);
  const failMemory = memory.shortTermMemories.find((m) => m.text.includes("农场主想付我"));
  expect(failMemory).toBeDefined();
  expect(failMemory!.text).toContain("出了点问题"); // failureCode 中文兜底
});

test("receive_payment 同步执行 fail → 失败文案带 C# 物理校验详情（步骤 2）", async () => {
  // C# 物理校验失败详情经 adjust_result.steps[0].detail 透传，工具应渲染给 LLM。
  const { memory, pay } = makeSyncPaymentHarness({
    type: "adjust_result", requestId: "r", instructionId: "i",
    npcName: "Abigail", success: false, failureCode: "insufficientFunds",
    steps: [{ index: 0, kind: "money", target: "player", success: false, failureCode: "insufficientFunds", detail: "insufficient funds (player wallet 100g, need 500g)" }],
  });

  const result = await pay.execute({ amount: 500 });

  expect(result.isError).toBe(true);
  expect(String(result.content)).toContain("100g");
  expect(
    memory.shortTermMemories.some((m) => m.text.includes("钱不够") && m.text.includes("100g")),
  ).toBe(true);
});

