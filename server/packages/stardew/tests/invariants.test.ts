import { test, expect } from "bun:test";
import { ProtocolAdapter, type ProtocolAdapterOptions } from "../src/protocol-adapter";
import { NpcPromptLoader } from "../src/npc-prompt-loader";
import { PromptBuilder } from "../src/prompt-builder";
import { VercelAIProvider, type ProviderToolCallResult } from "@valley/core";
import { StardewAgentRegistry } from "../src/stardew-agent-registry";
import type { WorldSnapshot, DialogueResponse, OutgoingMessage } from "../src/types";
import { mkdtempSync, rmSync } from "fs";
import { join } from "path";
import { tmpdir } from "os";
import { resolve } from "path";

// ---------------------------------------------------------------------------
// T2: L4 不变量测试 I1 / I2 / I3
// ---------------------------------------------------------------------------
// 对应思路文档 §6 T2。不变量是"系统在任何执行路径下必须保持的属性"。
// 单测通过构造特定场景断言不变量成立，防止后续重构静默破坏。
//
// I1: actualState 只由 state_changed 消息更新（worldSnapshot.npcState 不影响）
// I2: friendshipDelta 只由 dialogue_response 携带（其他消息类型不携带）
// I3: toolResults 队列在 LLM 失败时不蒸发（保留供下轮重注入）
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
  setLlmBehavior: (fn: () => Promise<ProviderToolCallResult>) => void;
  getLastSystemPrompt: () => string;
}

function makeFullStack(options?: ProtocolAdapterOptions): FullStack {
  const dir = mkdtempSync(join(tmpdir(), "valley-invariants-"));
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
  let behavior: (() => Promise<ProviderToolCallResult>) | null = null;
  provider._setCallOverride(async (messages) => {
    if (messages.length > 0 && messages[0]!.role === "system") {
      lastSystemPrompt = messages[0]!.content as string;
    }
    if (behavior) return behavior();
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
  const adapter = new ProtocolAdapter(registry, options);
  return {
    adapter, registry, dir,
    setLlmBehavior: (fn) => { behavior = fn; },
    getLastSystemPrompt: () => lastSystemPrompt,
  };
}

/** routeMessage 对 dialogue 请求返回 dialogue_response；测试直接断言该形态。 */
function asDialogueResponse(r: OutgoingMessage): DialogueResponse {
  if (r.type !== "dialogue_response") throw new Error(`expected dialogue_response, got ${r.type}`);
  return r;
}

function makeDialogueRequest(npcName: string, playerInput: string, npcState?: string) {
  return {
    type: "dialogue" as const,
    requestId: `req-${Math.random().toString(36).slice(2, 8)}`,
    npcName,
    playerInput,
    worldSnapshot: npcState ? { ...baseSnapshot, npcState } : baseSnapshot,
  };
}

// ════════════════════════════════════════════════════════════════════════
// I1: actualState 只由 state_changed 消息更新
// ════════════════════════════════════════════════════════════════════════
// 不变量：StardewAgent.actualState 字段只由 registry.updateActualState 写入，
// 而 updateActualState 只由 ProtocolAdapter.handleStateChanged 调用。
// worldSnapshot.npcState 是 C# 推送的"游戏内状态"，不直接写入 actualState 镜像。
// 这保证 prompt 的 {actual_state_section} 段永远反映 C# 状态机的真实转换，
// 而非 dialogue 请求中可能过期的快照。
// ════════════════════════════════════════════════════════════════════════

test("I1: actualState is undefined before any state_changed message arrives", () => {
  const { registry, dir } = makeFullStack();
  try {
    // 即使 registry 内部 Map 为空，getActualState 也应返回 undefined（不是抛异常）
    expect(registry.getActualState("Abigail")).toBeUndefined();
    expect(registry.getActualState("Haley")).toBeUndefined();
    expect(registry.getActualState("NonexistentNpc")).toBeUndefined();
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("I1: dialogue request with worldSnapshot.npcState='FARM' does NOT update actualState", async () => {
  const { adapter, registry, dir } = makeFullStack();
  try {
    // 初始状态：actualState 未定义
    expect(registry.getActualState("Abigail")).toBeUndefined();

    // 发送 dialogue 请求，worldSnapshot.npcState='FARM'
    await adapter.routeMessage(makeDialogueRequest("Abigail", "你好", "FARM"));

    // 不变量断言：actualState 仍然未定义（dialogue 不写 actualState）
    expect(registry.getActualState("Abigail")).toBeUndefined();

    // 即便再发一次 dialogue，npcState='MINE'，actualState 仍未定义
    await adapter.routeMessage(makeDialogueRequest("Abigail", "再聊", "MINE"));
    expect(registry.getActualState("Abigail")).toBeUndefined();
  } finally {
    await adapter.flushPendingSaves();
    rmSync(dir, { recursive: true, force: true });
  }
});

test("I1: only state_changed message updates actualState mirror", async () => {
  const { adapter, registry, dir } = makeFullStack();
  try {
    // 一系列 dialogue 请求后 actualState 仍未定义
    await adapter.routeMessage(makeDialogueRequest("Haley", "1", "FOLLOW"));
    await adapter.routeMessage(makeDialogueRequest("Haley", "2", "IDLE"));
    expect(registry.getActualState("Haley")).toBeUndefined();

    // 发送 state_changed 后 actualState 更新
    await adapter.routeMessage({
      type: "state_changed",
      npcName: "Haley",
      previousState: "IDLE",
      newState: "FOLLOW",
      wasForced: false,
      previousStateDurationMs: 1000,
    });
    expect(registry.getActualState("Haley")).toBe("FOLLOW");

    // 再发 dialogue，actualState 不变（即便 worldSnapshot.npcState 不同）
    await adapter.routeMessage(makeDialogueRequest("Haley", "3", "IDLE"));
    expect(registry.getActualState("Haley")).toBe("FOLLOW");

    // 第二次 state_changed 才更新
    await adapter.routeMessage({
      type: "state_changed",
      npcName: "Haley",
      previousState: "FOLLOW",
      newState: "TALK",
      wasForced: true,
      previousStateDurationMs: 5000,
      reason: "llm_decision",
    });
    expect(registry.getActualState("Haley")).toBe("TALK");
  } finally {
    await adapter.flushPendingSaves();
    rmSync(dir, { recursive: true, force: true });
  }
});

test("I1: state_changed to same NPC isolation — different NPCs have independent actualState", async () => {
  const { adapter, registry, dir } = makeFullStack();
  try {
    await adapter.routeMessage({
      type: "state_changed",
      npcName: "Abigail",
      previousState: "IDLE",
      newState: "FOLLOW",
      wasForced: false,
      previousStateDurationMs: 1000,
    });
    await adapter.routeMessage({
      type: "state_changed",
      npcName: "Sebastian",
      previousState: "IDLE",
      newState: "MINE",
      wasForced: false,
      previousStateDurationMs: 2000,
    });

    // 两个 NPC 的 actualState 独立
    expect(registry.getActualState("Abigail")).toBe("FOLLOW");
    expect(registry.getActualState("Sebastian")).toBe("MINE");
    // 第三方 NPC 仍未定义
    expect(registry.getActualState("Haley")).toBeUndefined();
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

// ════════════════════════════════════════════════════════════════════════
// I2: friendshipDelta 只由 dialogue_response 携带
// ════════════════════════════════════════════════════════════════════════
// 不变量：friendshipDelta / friendshipReason 是"对话期间好感度评估"的产物，
// 只能由 dialogue_response 携带回 C#（方案 B §4.1.1）。
// 其他消息类型（action_result / state_changed / hello / ping）不应携带此字段。
// 这保证好感度评估并回 dialogue 主路径，而非走独立的 friendship_eval 死管道。
// ════════════════════════════════════════════════════════════════════════

test("I2: action_result response (ack) does NOT carry friendshipDelta", async () => {
  const { adapter, dir } = makeFullStack();
  try {
    const resp = await adapter.routeMessage({
      type: "action_result",
      requestId: "i2-ar-1",
      callId: "call-1",
      npcName: "Abigail",
      tool: "give_gift",
      success: true,
      result: "礼物已送出",
    });
    // action_result 的响应只能是 ack
    expect(resp.type).toBe("ack");
    // ack 没有 friendshipDelta 字段
    expect((resp as { friendshipDelta?: number }).friendshipDelta).toBeUndefined();
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("I2: state_changed response (ack) does NOT carry friendshipDelta", async () => {
  const { adapter, dir } = makeFullStack();
  try {
    const resp = await adapter.routeMessage({
      type: "state_changed",
      npcName: "Abigail",
      previousState: "IDLE",
      newState: "FOLLOW",
      wasForced: false,
      previousStateDurationMs: 1000,
    });
    expect(resp.type).toBe("ack");
    expect((resp as { friendshipDelta?: number }).friendshipDelta).toBeUndefined();
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("I2: fallback dialogue_response does NOT carry friendshipDelta (fallback 不是 LLM 评估)", async () => {
  const { adapter, dir, setLlmBehavior } = makeFullStack();
  try {
    // LLM 抛错 → fallback
    setLlmBehavior(async () => { throw new Error("LLM down"); });

    const resp = asDialogueResponse(await adapter.routeMessage(makeDialogueRequest("Abigail", "你好")));
    expect(resp.type).toBe("dialogue_response");
    expect(resp.fallback).toBe(true);
    // 不变量：fallback 路径不应携带 friendshipDelta（未走 LLM 评估）
    expect(resp.friendshipDelta).toBeUndefined();
    expect(resp.friendshipReason).toBeUndefined();
  } finally {
    await adapter.flushPendingSaves();
    rmSync(dir, { recursive: true, force: true });
  }
});

test("I2: busy dialogue_response does NOT carry friendshipDelta", async () => {
  // R1：adapter 默认等锁 15s，此测试要"立即 BUSY"——显式传 0 保持断言不变且不拖慢测试。
  const { adapter, registry, dir } = makeFullStack({ dialogueLockTimeoutMs: 0 });
  try {
    // 占用 NPC 锁
    const locked = await registry.acquireLock("Abigail");
    expect(locked).toBe(true);

    // 第二次请求应返回 busy
    const resp = asDialogueResponse(await adapter.routeMessage(makeDialogueRequest("Abigail", "你好")));
    expect(resp.type).toBe("dialogue_response");
    expect(resp.fallback).toBe(true);
    // busy 响应不应携带 friendshipDelta
    expect(resp.friendshipDelta).toBeUndefined();
    expect(resp.friendshipReason).toBeUndefined();

    registry.releaseLock("Abigail");
  } finally {
    await adapter.flushPendingSaves();
    rmSync(dir, { recursive: true, force: true });
  }
});

test("I2: successful dialogue_response carries friendshipDelta when LLM emits evaluate_friendship", async () => {
  const { adapter, dir, setLlmBehavior } = makeFullStack();
  try {
    // LLM 输出 evaluate_friendship 工具调用
    setLlmBehavior(async () => ({
      content: "",
      toolCalls: [
        { id: "tc-1", name: "speak", args: { text: "谢谢夸奖。" } },
        { id: "tc-2", name: "evaluate_friendship", args: { delta: 10, reason: "他夸了我" } },
      ],
    }));

    const resp = asDialogueResponse(await adapter.routeMessage(makeDialogueRequest("Abigail", "你真好看")));
    expect(resp.type).toBe("dialogue_response");
    expect(resp.fallback).toBeFalsy();
    // LLM 评估了好感度 → dialogue_response 携带 friendshipDelta
    expect(resp.friendshipDelta).toBe(10);
    expect(resp.friendshipReason).toBe("他夸了我");
  } finally {
    await adapter.flushPendingSaves();
    rmSync(dir, { recursive: true, force: true });
  }
});

test("I2: successful dialogue_response omits friendshipDelta when LLM does NOT emit evaluate_friendship", async () => {
  const { adapter, dir, setLlmBehavior } = makeFullStack();
  try {
    // LLM 只输出 speak，不输出 evaluate_friendship
    setLlmBehavior(async () => ({
      content: "",
      toolCalls: [
        { id: "tc-1", name: "speak", args: { text: "你好。" } },
      ],
    }));

    const resp = asDialogueResponse(await adapter.routeMessage(makeDialogueRequest("Abigail", "你好")));
    expect(resp.type).toBe("dialogue_response");
    expect(resp.fallback).toBeFalsy();
    // LLM 未评估好感度 → friendshipDelta 不携带（C# 端按 0 处理）
    expect(resp.friendshipDelta).toBeUndefined();
    expect(resp.friendshipReason).toBeUndefined();
  } finally {
    await adapter.flushPendingSaves();
    rmSync(dir, { recursive: true, force: true });
  }
});

test("I2: hello/ping responses do NOT carry friendshipDelta", async () => {
  const { adapter, dir } = makeFullStack();
  try {
    const helloResp = await adapter.routeMessage({
      type: "hello",
      requestId: "i2-hello-1",
      modVersion: "1.0.0",
    });
    expect(helloResp.type).toBe("hello");
    expect((helloResp as { friendshipDelta?: number }).friendshipDelta).toBeUndefined();

    const pingResp = await adapter.routeMessage({
      type: "ping",
      requestId: "i2-ping-1",
    });
    expect(pingResp.type).toBe("pong");
    expect((pingResp as { friendshipDelta?: number }).friendshipDelta).toBeUndefined();
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

// ════════════════════════════════════════════════════════════════════════
// I3: toolResults 队列在 LLM 失败时不蒸发
// ════════════════════════════════════════════════════════════════════════
// 不变量：drainToolResults 改为 peek 语义后（§4.4），队列只在 dialogue 成功路径
// 被 clearToolResults 清空。LLM 失败 → fallback 路径保留队列，供下轮重注入。
// 这防止"LLM 故障期间到达的 action_result 反馈被静默丢弃"。
// ════════════════════════════════════════════════════════════════════════

test("I3: drainToolResults is peek-only — does NOT clear the queue", () => {
  const { registry, dir } = makeFullStack();
  try {
    registry.enqueueToolResult("Haley", {
      callId: "c1", tool: "give_gift", success: false, result: "失败", reason: "inventoryFull",
    });

    // 第一次 drain
    const drained1 = registry.drainToolResults("Haley");
    expect(drained1.length).toBe(1);
    // 不变量：drain 后队列仍存在
    expect(registry.drainToolResults("Haley").length).toBe(1);

    // 第二次 drain 应返回相同内容（peek 语义）
    const drained2 = registry.drainToolResults("Haley");
    expect(drained2.length).toBe(1);
    expect(drained2[0]!.callId).toBe("c1");
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("I3: clearToolResults is the only way to remove queue entries", () => {
  const { registry, dir } = makeFullStack();
  try {
    registry.enqueueToolResult("Haley", {
      callId: "c1", tool: "give_gift", success: true, result: "成功",
    });
    expect(registry.drainToolResults("Haley").length).toBe(1);

    registry.clearToolResults("Haley");
    expect(registry.drainToolResults("Haley").length).toBe(0);

    // clear 不存在的 NPC 不抛异常
    registry.clearToolResults("NonexistentNpc");
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("I3: LLM failure preserves queue for next round (单条反馈)", async () => {
  const { adapter, registry, dir, setLlmBehavior } = makeFullStack();
  try {
    // 注入反馈
    registry.enqueueToolResult("Haley", {
      callId: "c1", tool: "give_gift", success: false, result: "玩家背包已满", reason: "inventoryFull",
    });

    // LLM 故障
    setLlmBehavior(async () => { throw new Error("LLM down"); });
    const r1 = asDialogueResponse(await adapter.routeMessage(makeDialogueRequest("Haley", "再送一次")));
    expect(r1.fallback).toBe(true);

    // 不变量：fallback 后队列保留
    expect(registry.drainToolResults("Haley").length).toBe(1);
  } finally {
    await adapter.flushPendingSaves();
    rmSync(dir, { recursive: true, force: true });
  }
});

test("I3: queue preserved across multiple consecutive LLM failures", async () => {
  const { adapter, registry, dir, setLlmBehavior } = makeFullStack();
  try {
    registry.enqueueToolResult("Abigail", {
      callId: "c1", tool: "chop_tree", success: false, result: "找不到树", reason: "targetUnreachable",
    });

    setLlmBehavior(async () => { throw new Error("LLM down"); });

    // 连续 3 次 LLM 故障
    for (let i = 0; i < 3; i++) {
      const r = asDialogueResponse(await adapter.routeMessage(makeDialogueRequest("Abigail", `第${i}轮`)));
      expect(r.fallback).toBe(true);
      // 不变量：每次 fallback 后队列仍保留 1 条
      expect(registry.drainToolResults("Abigail").length).toBe(1);
    }
  } finally {
    await adapter.flushPendingSaves();
    rmSync(dir, { recursive: true, force: true });
  }
});

test("I3: queue accumulated across LLM failures (多条反馈全保留)", async () => {
  const { adapter, registry, dir, setLlmBehavior } = makeFullStack();
  try {
    setLlmBehavior(async () => { throw new Error("LLM down"); });

    // 第 1 轮故障 + 注入反馈 1
    registry.enqueueToolResult("Haley", {
      callId: "c1", tool: "give_gift", success: false, result: "背包满", reason: "inventoryFull",
    });
    await adapter.routeMessage(makeDialogueRequest("Haley", "1"));
    expect(registry.drainToolResults("Haley").length).toBe(1);

    // 第 2 轮故障 + 注入反馈 2
    registry.enqueueToolResult("Haley", {
      callId: "c2", tool: "chop_tree", success: false, result: "无树", reason: "targetUnreachable",
    });
    await adapter.routeMessage(makeDialogueRequest("Haley", "2"));
    // 不变量：两条反馈都保留
    const records = registry.drainToolResults("Haley");
    expect(records.length).toBe(2);
    expect(records[0]!.callId).toBe("c1");
    expect(records[1]!.callId).toBe("c2");
  } finally {
    await adapter.flushPendingSaves();
    rmSync(dir, { recursive: true, force: true });
  }
});

test("I3: queue cleared only after successful dialogue (单次成功清空)", async () => {
  const { adapter, registry, dir, setLlmBehavior } = makeFullStack();
  try {
    registry.enqueueToolResult("Haley", {
      callId: "c1", tool: "give_gift", success: false, result: "失败", reason: "inventoryFull",
    });

    // 第 1 轮故障 → 队列保留
    setLlmBehavior(async () => { throw new Error("LLM down"); });
    await adapter.routeMessage(makeDialogueRequest("Haley", "1"));
    expect(registry.drainToolResults("Haley").length).toBe(1);

    // 第 2 轮恢复 → 队列被消费并清空
    setLlmBehavior(async () => ({
      content: "",
      toolCalls: [{ id: "tc-1", name: "speak", args: { text: "好的。" } }],
    }));
    const r2 = asDialogueResponse(await adapter.routeMessage(makeDialogueRequest("Haley", "2")));
    expect(r2.fallback).toBeFalsy();
    // 不变量：成功路径清空队列
    expect(registry.drainToolResults("Haley").length).toBe(0);
  } finally {
    await adapter.flushPendingSaves();
    rmSync(dir, { recursive: true, force: true });
  }
});

test("I3: queue cap MAX_TOOL_RESULT_QUEUE=10 drops oldest on overflow", () => {
  const { registry, dir } = makeFullStack();
  try {
    // 注入 12 条反馈，超过 MAX_TOOL_RESULT_QUEUE=10
    for (let i = 0; i < 12; i++) {
      registry.enqueueToolResult("Haley", {
        callId: `c${i}`, tool: "give_gift", success: true, result: `成功${i}`,
      });
    }
    const records = registry.drainToolResults("Haley");
    // 上限 10
    expect(records.length).toBe(10);
    // 最旧的 2 条被丢弃（c0, c1）
    expect(records[0]!.callId).toBe("c2");
    expect(records[9]!.callId).toBe("c11");
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("I3: queue per-NPC isolation — different NPCs have independent queues", () => {
  const { registry, dir } = makeFullStack();
  try {
    registry.enqueueToolResult("Abigail", {
      callId: "a1", tool: "give_gift", success: true, result: "成功",
    });
    registry.enqueueToolResult("Sebastian", {
      callId: "s1", tool: "chop_tree", success: false, result: "失败", reason: "targetUnreachable",
    });
    registry.enqueueToolResult("Sebastian", {
      callId: "s2", tool: "chop_tree", success: true, result: "成功",
    });

    expect(registry.drainToolResults("Abigail").length).toBe(1);
    expect(registry.drainToolResults("Sebastian").length).toBe(2);
    expect(registry.drainToolResults("Haley").length).toBe(0);

    // clear 一个 NPC 不影响其他
    registry.clearToolResults("Abigail");
    expect(registry.drainToolResults("Abigail").length).toBe(0);
    expect(registry.drainToolResults("Sebastian").length).toBe(2);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

// ════════════════════════════════════════════════════════════════════════
// I1 + I3 综合：state_changed 与 tool_result 同时到达，两者独立
// ════════════════════════════════════════════════════════════════════════

test("I1+I3: state_changed and action_result independent — both update their respective state", async () => {
  const { adapter, registry, dir } = makeFullStack();
  try {
    // 同时发送 state_changed 和 action_result
    await adapter.routeMessage({
      type: "state_changed",
      npcName: "Haley",
      previousState: "IDLE",
      newState: "FOLLOW",
      wasForced: false,
      previousStateDurationMs: 1000,
      reason: "llm_decision",
    });
    await adapter.routeMessage({
      type: "action_result",
      requestId: "i1i3-1",
      callId: "c1",
      npcName: "Haley",
      tool: "give_gift",
      success: false,
      result: "失败",
      reason: "inventoryFull",
    });

    // 不变量 I1：actualState 由 state_changed 更新
    expect(registry.getActualState("Haley")).toBe("FOLLOW");
    // 不变量 I3：tool_result 由 action_result 入队
    expect(registry.drainToolResults("Haley").length).toBe(1);
    expect(registry.drainToolResults("Haley")[0]!.reason).toBe("inventoryFull");
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});
