import { test, expect } from "bun:test";
import { ProtocolAdapter } from "../src/protocol-adapter";
import { NpcPromptLoader } from "../src/npc-prompt-loader";
import { PromptBuilder } from "../src/prompt-builder";
import { VercelAIProvider, LLMBillingError } from "@valley/core";
import { StardewAgentRegistry } from "../src/stardew-agent-registry";
import { AgentMemory } from "../src/agent-memory";
import { buildStardewTools } from "../src/stardew-tools";
import type { ToolContext } from "../src/stardew-tools";
import type { SceneState, WorldSnapshot } from "../src/types";
import { mkdtempSync, rmSync } from "fs";
import { join } from "path";
import { tmpdir } from "os";
import { resolve } from "path";

// ---------------------------------------------------------------------------
// T1: L3 故障注入框架 + CH 用例
// ---------------------------------------------------------------------------
// 模拟各种故障场景，断言系统行为正确。对应思路文档 §6 T1。
// 故障注入边界：只 mock 外部依赖（LLM provider），不 mock 被测对象
// （ProtocolAdapter / StardewAgent / RuleEngine / AgentMemory 全部走真实代码路径）。
// ---------------------------------------------------------------------------

const DATA_PATH = resolve(import.meta.dir, "../data/npc_prompts.json");

const baseSnapshot: WorldSnapshot = {
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
}

function makeFullStack(): FullStack {
  const dir = mkdtempSync(join(tmpdir(), "valley-fault-injection-"));
  const loader = new NpcPromptLoader(DATA_PATH);
  const builder = new PromptBuilder(loader);
  const provider = new VercelAIProvider({
    provider: "minimax",
    apiKey: "fake",
    model: "fake",
    baseUrl: "http://localhost:9999",
    maxRetries: 1,
  });
  let lastSystemPrompt = "";
  provider._setCallOverride(async (messages) => {
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
  };
}

function makeDialogueRequest(npcName: string, playerInput: string) {
  return {
    type: "dialogue" as const,
    requestId: `req-${Math.random().toString(36).slice(2, 8)}`,
    npcName,
    playerInput,
    worldSnapshot: baseSnapshot,
  };
}

// ════════════════════════════════════════════════════════════════════════
// CH-01：对话超时降级
// ════════════════════════════════════════════════════════════════════════
// 故障场景：LLM provider 抛 TimeoutError（模拟请求超时）。
// 期望行为：runDialogue 失败 → protocol-adapter catch → rule-engine.buildFallbackResponse
//          返回 fallback response（speech 非空、fallback=true、emotion 合理）。
// 注：JS 没有内置 TimeoutError，且 VercelAIProvider.withRetry 对超时类错误走
//     max-retries 兜底包装为 LLMUnavailableError，rule-engine 命中 tier 2
//     （emotion=Tired, speech="话到嘴边说不出来..."）。
// ════════════════════════════════════════════════════════════════════════

test("CH-01: LLM timeout falls back to non-empty speech with fallback=true and reasonable emotion", async () => {
  // 直接 throw Error 模拟超时（无 statusCode → 走 max-retries → LLMUnavailableError）
  const dir = mkdtempSync(join(tmpdir(), "valley-fault-ch01-"));
  const loader = new NpcPromptLoader(DATA_PATH);
  const builder = new PromptBuilder(loader);
  const provider = new VercelAIProvider({
    provider: "minimax",
    apiKey: "fake",
    model: "fake",
    baseUrl: "http://localhost:9999",
    maxRetries: 1,
  });
  provider._setCallOverride(async () => {
    throw new Error("LLM request timed out");
  });
  const registry = new StardewAgentRegistry({
    promptBuilder: builder,
    llmProvider: provider,
    agentsDir: dir,
  });
  const adapter = new ProtocolAdapter(registry);
  try {
    const resp = await adapter.handleDialogue({
      type: "dialogue", requestId: "ch01-1", npcName: "Abigail",
      playerInput: "你好", worldSnapshot: baseSnapshot,
    });
    expect(resp.type).toBe("dialogue_response");
    expect(resp.fallback).toBe(true);
    expect(resp.speech.length).toBeGreaterThan(0);
    // 超时归为 LLMUnavailableError tier：emotion=Tired
    expect(resp.emotion).toBe("Tired");
    // fallback 始终携带 emote 动作
    expect(resp.actions.some((a) => a.tool === "emote")).toBe(true);
  } finally {
    await adapter.flushPendingSaves();
    rmSync(dir, { recursive: true, force: true });
  }
});

// ════════════════════════════════════════════════════════════════════════
// CH-02：LLM 计费耗尽降级
// ════════════════════════════════════════════════════════════════════════
// 故障场景：LLM provider 抛带 statusCode=402 的错误（模拟 ai-sdk APICallError）。
// 期望行为：VercelAIProvider.withRetry 分类为 LLMBillingError（不重试立即抛出）→
//          agent-loop emit ErrorEvent{error: LLMBillingError} → stardew-agent.runOnce
//          重抛原 error → protocol-adapter catch → rule-engine.buildFallbackResponse
//          命中 tier 1（speech 含"走神"、emotion="Confused"）。
// ════════════════════════════════════════════════════════════════════════

test("CH-02: LLMBillingError (HTTP 402) falls back to tier-1 response with 走神 + Confused", async () => {
  const dir = mkdtempSync(join(tmpdir(), "valley-fault-ch02-"));
  const loader = new NpcPromptLoader(DATA_PATH);
  const builder = new PromptBuilder(loader);
  const provider = new VercelAIProvider({
    provider: "minimax",
    apiKey: "fake",
    model: "fake",
    baseUrl: "http://localhost:9999",
    maxRetries: 3,
  });
  provider._setCallOverride(async () => {
    // 模拟 ai-sdk APICallError 形状：error 上挂 statusCode 字段
    // VercelAIProvider.withRetry 用 extractHttpStatusCode 识别 402 → LLMBillingError
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
      type: "dialogue", requestId: "ch02-1", npcName: "Abigail",
      playerInput: "你好", worldSnapshot: baseSnapshot,
    });
    expect(resp.type).toBe("dialogue_response");
    expect(resp.fallback).toBe(true);
    // tier 1 特征：speech 含"走神"、emotion="Confused"
    expect(resp.speech).toContain("走神");
    expect(resp.emotion).toBe("Confused");
  } finally {
    await adapter.flushPendingSaves();
    rmSync(dir, { recursive: true, force: true });
  }
});

test("CH-02: direct LLMBillingError instance also hits tier-1 (类型透传保真)", async () => {
  // 验证 E5 接线：原 LLMBillingError 实例应被 agent-loop / stardew-agent 透传
  // 而非被包装为 LLMUnavailableError（早期 bug 是 withRetry 不识别 402）
  const dir = mkdtempSync(join(tmpdir(), "valley-fault-ch02b-"));
  const loader = new NpcPromptLoader(DATA_PATH);
  const builder = new PromptBuilder(loader);
  const provider = new VercelAIProvider({
    provider: "minimax",
    apiKey: "fake",
    model: "fake",
    baseUrl: "http://localhost:9999",
    maxRetries: 1,
  });
  provider._setCallOverride(async () => {
    throw new LLMBillingError("Insufficient credits");
  });
  const registry = new StardewAgentRegistry({
    promptBuilder: builder,
    llmProvider: provider,
    agentsDir: dir,
  });
  const adapter = new ProtocolAdapter(registry);
  try {
    const resp = await adapter.handleDialogue({
      type: "dialogue", requestId: "ch02-2", npcName: "Abigail",
      playerInput: "你好", worldSnapshot: baseSnapshot,
    });
    expect(resp.fallback).toBe(true);
    expect(resp.speech).toContain("走神");
    expect(resp.emotion).toBe("Confused");
  } finally {
    await adapter.flushPendingSaves();
    rmSync(dir, { recursive: true, force: true });
  }
});

// ════════════════════════════════════════════════════════════════════════
// CH-05：state_changed 镜像一致性
// ════════════════════════════════════════════════════════════════════════
// 故障场景：旅行失败，C# 发 state_changed{previousState=FOLLOW, newState=IDLE,
//          reason=travel_failed}。
// 期望行为：
//   1. protocol-adapter.routeMessage 路由到 handleStateChanged
//   2. registry.updateActualState 写入 actualState="IDLE"
//   3. 下轮 dialogue 的 prompt 包含"当前实际状态：IDLE"
// 不变量 I1（actualState 只由 state_changed 写）的对应行为验证。
// ════════════════════════════════════════════════════════════════════════

test("CH-05: state_changed with reason=travel_failed updates actualState mirror and prompts '当前实际状态：IDLE'", async () => {
  const { adapter, registry, dir, getLastSystemPrompt } = makeFullStack();
  try {
    // 初始状态：未收到 state_changed，actualState 为 undefined
    expect(registry.getActualState("Haley")).toBeUndefined();

    // 模拟旅行失败：C# 发 state_changed
    const resp = await adapter.routeMessage({
      type: "state_changed",
      npcName: "Haley",
      previousState: "FOLLOW",
      newState: "IDLE",
      wasForced: true,
      previousStateDurationMs: 8000,
      reason: "travel_failed",
    });
    expect(resp.type).toBe("ack");

    // 断言 1：actualState 镜像已更新
    expect(registry.getActualState("Haley")).toBe("IDLE");

    // 触发一轮 dialogue，捕获 system prompt
    await adapter.routeMessage(makeDialogueRequest("Haley", "你刚才去哪了？"));
    const prompt = getLastSystemPrompt();

    // 断言 2：prompt 包含"当前实际状态：IDLE"
    expect(prompt).toContain("当前实际状态：IDLE");
    expect(prompt).toContain("我现在的真实状态");
  } finally {
    await adapter.flushPendingSaves();
    rmSync(dir, { recursive: true, force: true });
  }
});

test("CH-05: state_changed without reason still updates actualState (向后兼容)", async () => {
  const { adapter, registry, dir } = makeFullStack();
  try {
    // 旧 C# 客户端不带 reason 字段
    await adapter.routeMessage({
      type: "state_changed",
      npcName: "Sebastian",
      previousState: "IDLE",
      newState: "FOLLOW",
      wasForced: false,
      previousStateDurationMs: 1500,
    });
    expect(registry.getActualState("Sebastian")).toBe("FOLLOW");
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

// ════════════════════════════════════════════════════════════════════════
// CH-03：action_result 失败原因消费
// ════════════════════════════════════════════════════════════════════════
// 故障场景：C# 执行 give_gift 失败，回 action_result{success=false, reason=InventoryFull}。
// 期望行为：
//   1. protocol-adapter.routeToolResult 透传 reason 到 ToolResultRecord
//   2. ToolResultRecord.reason === "inventoryFull"
//   3. 下轮 dialogue 的 prompt 反馈段渲染中文映射"背包已满"
// ════════════════════════════════════════════════════════════════════════

test("CH-03: action_result with reason=inventoryFull routes to ToolResultRecord and renders Chinese in prompt", async () => {
  const { adapter, registry, dir, getLastSystemPrompt } = makeFullStack();
  try {
    // C# 回 action_result 失败 + reason
    await adapter.routeMessage({
      type: "action_result",
      requestId: "ch03-ar-1",
      callId: "call-1",
      npcName: "Haley",
      tool: "give_gift",
      success: false,
      result: "玩家背包已满",
      reason: "inventoryFull",
    });

    // 断言 1：ToolResultRecord.reason 透传
    const records = registry.drainToolResults("Haley");
    expect(records.length).toBe(1);
    expect(records[0]!.reason).toBe("inventoryFull");
    expect(records[0]!.success).toBe(false);

    // 触发一轮 dialogue，捕获 system prompt
    await adapter.routeMessage(makeDialogueRequest("Haley", "为什么没送出去？"));

    // 断言 2：prompt 反馈段渲染中文映射
    const prompt = getLastSystemPrompt();
    expect(prompt).toContain("上次行动结果");
    expect(prompt).toContain("give_gift：失败");
    // reason="inventoryFull" → 中文映射"背包已满"渲染在括号内
    expect(prompt).toContain("give_gift：失败（背包已满）");
  } finally {
    await adapter.flushPendingSaves();
    rmSync(dir, { recursive: true, force: true });
  }
});

test("CH-03: action_result with reason=itemNotFound renders 物品不存在", async () => {
  const { adapter, registry, dir, getLastSystemPrompt } = makeFullStack();
  try {
    await adapter.routeMessage({
      type: "action_result",
      requestId: "ch03-ar-2",
      callId: "call-2",
      npcName: "Abigail",
      tool: "give_gift",
      success: false,
      result: "物品不存在",
      reason: "itemNotFound",
    });
    expect(registry.drainToolResults("Abigail")[0]!.reason).toBe("itemNotFound");

    await adapter.routeMessage(makeDialogueRequest("Abigail", "你送我什么了？"));
    const prompt = getLastSystemPrompt();
    expect(prompt).toContain("物品不存在");
    expect(prompt).toContain("give_gift：失败（物品不存在）");
  } finally {
    await adapter.flushPendingSaves();
    rmSync(dir, { recursive: true, force: true });
  }
});

test("CH-03: unknown reason falls back to 未知原因 (REASON_CN 兜底)", async () => {
  // C# 未来新增枚举值而 TS REASON_CN 未同步时，统一使用中文兜底，避免污染 prompt。
  const { adapter, registry, dir, getLastSystemPrompt } = makeFullStack();
  try {
    await adapter.routeMessage({
      type: "action_result",
      requestId: "ch03-ar-3",
      callId: "call-3",
      npcName: "Haley",
      tool: "give_gift",
      success: false,
      result: "未知错误",
      reason: "newFutureReason",
    });
    expect(registry.drainToolResults("Haley")[0]!.reason).toBe("newFutureReason");

    await adapter.routeMessage(makeDialogueRequest("Haley", "怎么了？"));
    const prompt = getLastSystemPrompt();
    expect(prompt).toContain("give_gift：失败（未知原因）");
  } finally {
    await adapter.flushPendingSaves();
    rmSync(dir, { recursive: true, force: true });
  }
});

// ════════════════════════════════════════════════════════════════════════
// CH-04：give_gift 两阶段账本
// ════════════════════════════════════════════════════════════════════════
// 故障场景：LLM 决定 give_gift → execute 只写意图日志（不扣库存、不写记忆）→
//          C# 执行后回 action_result → TS 根据 success 写"送礼成功/失败"记忆。
// 期望行为：
//   1. give_gift.execute 不扣 ctx.inventory，不写 ctx.memory，只写 ctx.log
//   2. action_result success=true → 写"送给农场主一份礼物"记忆
//   3. action_result success=false → 写"想送礼物但没送成"记忆
// 对应思路文档 §4.6。
// ════════════════════════════════════════════════════════════════════════

const giftScene: SceneState = {
  season: "summer", day: 28, timeStr: "14:30", weather: "sunny",
  location: "Town", nearbyObjects: "2 villagers", farmerName: "新来的农夫",
  friendship: 250, npcState: "IDLE",
  inventory: [{ name: "Amethyst", quantity: 2 }],
  playerMoney: null,
  npcLocation: null,
  npcMoney: null,
  npcInventory: null,
  playerHeldItem: null,
  currentGoal: null,
  npcMood: null,
  npcRecentEvents: null,
  npcWorkingOn: null,
  npcOwedMoney: null,
  npcTile: { x: 0, y: 0 },
};

test("CH-04 stage 1: give_gift.execute writes only intent log, no inventory/memory side-effects", async () => {
  const memory = new AgentMemory("Abigail", "/tmp/ch04-stage1");
  const ctx: ToolContext = {
    memory,
    scene: giftScene,
    inventory: [{ name: "Amethyst", quantity: 2 }],
    givenToPlayer: [],
    log: [],
  };
  const tools = buildStardewTools(ctx);
  const gift = tools.find((t) => t.name === "give_gift")!;

  const before = {
    inventoryLen: ctx.inventory.length,
    inventoryQty: ctx.inventory[0]!.quantity,
    memoryLen: memory.shortTermMemories.length,
    givenLen: ctx.givenToPlayer.length,
  };

  const result = await gift.execute({ item_id: "Amethyst", quantity: 1 });

  const after = {
    inventoryLen: ctx.inventory.length,
    inventoryQty: ctx.inventory[0]!.quantity,
    memoryLen: memory.shortTermMemories.length,
    givenLen: ctx.givenToPlayer.length,
  };

  // execute 不扣库存
  expect(after.inventoryLen).toBe(before.inventoryLen);
  expect(after.inventoryQty).toBe(before.inventoryQty);
  // execute 不写送礼记忆（gift entryType 记忆由 routeToolResult 落地）
  expect(after.memoryLen).toBe(before.memoryLen);
  expect(memory.shortTermMemories.some((m) => m.entryType === "gift")).toBe(false);
  // execute 不写 givenToPlayer（这是 give_item 的行为，不是 give_gift）
  expect(after.givenLen).toBe(0);
  // execute 只写意图日志
  expect(result.isError).toBeFalsy();
  expect(ctx.log.some((l) => l.includes("[give_gift]"))).toBe(true);
  expect(ctx.log.some((l) => l.includes("intent"))).toBe(true);
});

test("CH-04 stage 2a: give_gift 同步执行 success → 写送礼成功记忆（步骤 2）", async () => {
  const memory = new AgentMemory("Abigail", "/tmp/ch04-stage2a");
  const ctx: ToolContext = {
    memory,
    scene: giftScene,
    inventory: [{ name: "Amethyst", quantity: 2 }],
    givenToPlayer: [],
    log: [],
    economy: {
      npcName: "Abigail",
      adjust: async () => ({
        type: "adjust_result", requestId: "r", instructionId: "i",
        npcName: "Abigail", success: true, steps: [],
      }),
      getMoney: () => 500,
      getInventory: () => [{ name: "Amethyst", quantity: 1 }],
    },
  };
  const gift = buildStardewTools(ctx).find((t) => t.name === "give_gift")!;

  const result = await gift.execute({ item_id: "Amethyst", quantity: 1 });

  // 同步执行成功：非错误结果 + 送礼成功记忆（importance=6.0，entryType=gift）
  expect(result.isError).toBeFalsy();
  expect(
    memory.shortTermMemories.some(
      (m) => m.text === "送了农场主一份礼物（Amethyst）" && m.importance === 6.0 && m.entryType === "gift",
    ),
  ).toBe(true);
});

test("CH-04 stage 2b: give_gift 同步执行失败 → 写失败记忆并回错（步骤 2）", async () => {
  const memory = new AgentMemory("Abigail", "/tmp/ch04-stage2b");
  const ctx: ToolContext = {
    memory,
    scene: giftScene,
    inventory: [{ name: "Amethyst", quantity: 2 }],
    givenToPlayer: [],
    log: [],
    economy: {
      npcName: "Abigail",
      adjust: async () => ({
        type: "adjust_result", requestId: "r", instructionId: "i",
        npcName: "Abigail", success: false, failureCode: "inventoryFull", steps: [],
      }),
      getMoney: () => 500,
      getInventory: () => [{ name: "Amethyst", quantity: 2 }],
    },
  };
  const gift = buildStardewTools(ctx).find((t) => t.name === "give_gift")!;

  const result = await gift.execute({ item_id: "Amethyst", quantity: 1 });

  // 同步执行失败：isError + 失败码中文文案 + 失败记忆（importance=3.0，entryType=gift）
  expect(result.isError).toBe(true);
  expect(String(result.content)).toContain("背包满了");
  expect(
    memory.shortTermMemories.some(
      (m) => m.text.includes("想送礼物但没送成") && m.text.includes("背包满了")
        && m.importance === 3.0 && m.entryType === "gift",
    ),
  ).toBe(true);
});

test("CH-04 stage 2c: give_gift 失败无详情 → 记忆不含括号化原因（步骤 2）", async () => {
  const memory = new AgentMemory("Abigail", "/tmp/ch04-stage2c");
  const ctx: ToolContext = {
    memory,
    scene: giftScene,
    inventory: [{ name: "Amethyst", quantity: 2 }],
    givenToPlayer: [],
    log: [],
    economy: {
      npcName: "Abigail",
      adjust: async () => ({
        type: "adjust_result", requestId: "r", instructionId: "i",
        npcName: "Abigail", success: false, failureCode: "internalError", steps: [],
      }),
      getMoney: () => 500,
      getInventory: () => [{ name: "Amethyst", quantity: 2 }],
    },
  };
  const gift = buildStardewTools(ctx).find((t) => t.name === "give_gift")!;

  const result = await gift.execute({ item_id: "Amethyst", quantity: 1 });

  expect(result.isError).toBe(true);
  const failMemory = memory.shortTermMemories.find((m) => m.text.includes("想送礼物但没送成"));
  expect(failMemory).toBeDefined();
  expect(failMemory!.text).not.toContain("（"); // 无详情时不渲染括号化原因
});


test("CH-06 stage 1: chop_tree.execute writes only intent log, no memory side-effects", async () => {
  const memory = new AgentMemory("Sebastian", "/tmp/ch06-stage1");
  const ctx: ToolContext = {
    memory,
    scene: giftScene,
    inventory: [],
    givenToPlayer: [],
    log: [],
  };
  const tools = buildStardewTools(ctx);
  const chop = tools.find((t) => t.name === "chop_tree")!;

  const memoryBefore = memory.shortTermMemories.length;

  const result = await chop.execute({ tree_id: "tree_42" });

  // execute 不写记忆（chop 记忆由 routeToolResult 落地）
  expect(memory.shortTermMemories.length).toBe(memoryBefore);
  expect(memory.shortTermMemories.some((m) => m.tags.includes("chop"))).toBe(false);
  // execute 只写意图日志
  expect(result.isError).toBeFalsy();
  expect(ctx.log.some((l) => l.includes("[chop_tree]"))).toBe(true);
  expect(ctx.log.some((l) => l.includes("intent"))).toBe(true);
});

test("CH-06 stage 1: chop_tree.execute without tree_id logs 'nearest'", async () => {
  const memory = new AgentMemory("Sebastian", "/tmp/ch06-stage1b");
  const ctx: ToolContext = {
    memory,
    scene: giftScene,
    inventory: [],
    givenToPlayer: [],
    log: [],
  };
  const tools = buildStardewTools(ctx);
  const chop = tools.find((t) => t.name === "chop_tree")!;

  await chop.execute({});
  expect(ctx.log.some((l) => l.includes("(nearest)"))).toBe(true);
});

test("CH-06 stage 2a: action_result success=true writes '砍树获取了一些木材' memory", async () => {
  const { adapter, registry, dir } = makeFullStack();
  try {
    await adapter.routeMessage({
      type: "action_result",
      requestId: "ch06-success",
      callId: "call-chop-1",
      npcName: "Sebastian",
      tool: "chop_tree",
      success: true,
      result: "砍倒了树",
    });

    const agent = registry.getOrCreate("Sebastian");
    const memory = (agent as unknown as { memory: AgentMemory }).memory;
    expect(memory.shortTermMemories.some(
      (m) => m.text === "砍树获取了一些木材" && m.importance === 4.0 && m.entryType === "event",
    )).toBe(true);
    expect(memory.shortTermMemories.some(
      (m) => m.tags.includes("chop") && m.tags.includes("wood"),
    )).toBe(true);
  } finally {
    await adapter.flushPendingSaves();
    rmSync(dir, { recursive: true, force: true });
  }
});

test("CH-06 stage 2b: action_result success=false writes '想砍树但没砍成' memory", async () => {
  const { adapter, registry, dir } = makeFullStack();
  try {
    await adapter.routeMessage({
      type: "action_result",
      requestId: "ch06-fail",
      callId: "call-chop-2",
      npcName: "Sebastian",
      tool: "chop_tree",
      success: false,
      result: "找不到树",
      reason: "targetUnreachable",
    });

    const agent = registry.getOrCreate("Sebastian");
    const memory = (agent as unknown as { memory: AgentMemory }).memory;
    expect(memory.shortTermMemories.some(
      (m) => m.text.includes("想砍树但没砍成") && m.importance === 2.0 && m.entryType === "event",
    )).toBe(true);
    // 失败记忆包含 reason 文本
    expect(memory.shortTermMemories.some(
      (m) => m.text.includes("targetUnreachable"),
    )).toBe(true);
    // 失败记忆 tags 含 chop + failure
    expect(memory.shortTermMemories.some(
      (m) => m.tags.includes("chop") && m.tags.includes("failure"),
    )).toBe(true);
  } finally {
    await adapter.flushPendingSaves();
    rmSync(dir, { recursive: true, force: true });
  }
});

// ════════════════════════════════════════════════════════════════════════
// 综合故障场景：连续多次故障后系统不崩
// ════════════════════════════════════════════════════════════════════════

test("综合：连续 5 次 LLMBillingError 后系统仍返回 fallback 不崩", async () => {
  const dir = mkdtempSync(join(tmpdir(), "valley-fault-multi-"));
  const loader = new NpcPromptLoader(DATA_PATH);
  const builder = new PromptBuilder(loader);
  const provider = new VercelAIProvider({
    provider: "minimax",
    apiKey: "fake",
    model: "fake",
    baseUrl: "http://localhost:9999",
    maxRetries: 1,
  });
  provider._setCallOverride(async () => {
    throw Object.assign(new Error("Insufficient credits"), { statusCode: 402 });
  });
  const registry = new StardewAgentRegistry({
    promptBuilder: builder,
    llmProvider: provider,
    agentsDir: dir,
  });
  const adapter = new ProtocolAdapter(registry);
  try {
    for (let i = 0; i < 5; i++) {
      const resp = await adapter.handleDialogue({
        type: "dialogue", requestId: `multi-${i}`, npcName: "Abigail",
        playerInput: `第${i}轮`, worldSnapshot: baseSnapshot,
      });
      expect(resp.fallback).toBe(true);
      expect(resp.speech).toContain("走神");
      expect(resp.emotion).toBe("Confused");
    }
  } finally {
    await adapter.flushPendingSaves();
    rmSync(dir, { recursive: true, force: true });
  }
});

test("综合：LLM 故障期间 toolResults 队列不蒸发（§4.4 不变量）", async () => {
  // LLM 故障 → fallback → toolResults 队列保留 → 下轮 LLM 恢复 → 队列被消费
  const dir = mkdtempSync(join(tmpdir(), "valley-fault-queue-"));
  const loader = new NpcPromptLoader(DATA_PATH);
  const builder = new PromptBuilder(loader);
  const provider = new VercelAIProvider({
    provider: "minimax",
    apiKey: "fake",
    model: "fake",
    baseUrl: "http://localhost:9999",
    maxRetries: 1,
  });
  let recovered = false;
  provider._setCallOverride(async () => {
    if (!recovered) throw new Error("LLM down");
    return { content: "", toolCalls: [{ id: "tc-1", name: "speak", args: { text: "好的。" } }] };
  });
  const registry = new StardewAgentRegistry({
    promptBuilder: builder,
    llmProvider: provider,
    agentsDir: dir,
  });
  const adapter = new ProtocolAdapter(registry);
  try {
    // 注入反馈
    registry.enqueueToolResult("Haley", {
      callId: "c1", tool: "give_gift", success: false, result: "玩家背包已满", reason: "inventoryFull",
    });

    // LLM 故障：fallback，队列保留
    const r1 = await adapter.handleDialogue({
      type: "dialogue", requestId: "queue-1", npcName: "Haley",
      playerInput: "再送我一次", worldSnapshot: baseSnapshot,
    });
    expect(r1.fallback).toBe(true);
    expect(registry.drainToolResults("Haley").length).toBe(1);

    // LLM 恢复：队列被消费
    recovered = true;
    const r2 = await adapter.handleDialogue({
      type: "dialogue", requestId: "queue-2", npcName: "Haley",
      playerInput: "再试一次", worldSnapshot: baseSnapshot,
    });
    expect(r2.fallback).toBeFalsy();
    expect(registry.drainToolResults("Haley").length).toBe(0);
  } finally {
    await adapter.flushPendingSaves();
    rmSync(dir, { recursive: true, force: true });
  }
});
