import { test, expect } from "bun:test";
import { ProtocolAdapter } from "../src/protocol-adapter";
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
// T2: 长跑模式 — 50 轮连续对话
// ---------------------------------------------------------------------------
// 对应思路文档 §6 T2：L4 长跑模式。
// 模拟 50 轮玩家与 NPC 的连续对话，验证：
//   1. 系统稳定性：无异常、无死锁、无内存泄漏
//   2. 队列行为：toolResults 队列在多轮中正确消费/积累
//   3. 记忆有界：conversationHistory / shortTermMemories 不超过上限
//   4. 锁正确释放：每轮结束后 NPC 锁释放，下轮可重新获取
//   5. 状态镜像稳定：actualState 在 50 轮中正确反映 state_changed
//   6. 降级恢复：故障注入→恢复→继续 50 轮不崩
// ---------------------------------------------------------------------------

const DATA_PATH = resolve(import.meta.dir, "../data/npc_prompts.json");
const ROUNDS = 50;

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

function makeFullStack(): FullStack {
  const dir = mkdtempSync(join(tmpdir(), "valley-longrun-"));
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
  const adapter = new ProtocolAdapter(registry);
  return {
    adapter, registry, dir,
    setLlmBehavior: (fn) => { behavior = fn; },
    getLastSystemPrompt: () => lastSystemPrompt,
  };
}

function asDialogueResponse(r: OutgoingMessage): DialogueResponse {
  if (r.type !== "dialogue_response") throw new Error(`expected dialogue_response, got ${r.type}`);
  return r;
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
// 长跑 1：纯成功路径 50 轮
// ════════════════════════════════════════════════════════════════════════

test("长跑 1: 50 轮连续成功对话 — 无异常、锁释放、记忆有界", async () => {
  const { adapter, registry, dir, setLlmBehavior } = makeFullStack();
  try {
    let callCount = 0;
    // LLM 每轮返回不同文本（防止去重逻辑触发）
    setLlmBehavior(async () => {
      callCount++;
      return {
        content: "",
        toolCalls: [{ id: `tc-${callCount}`, name: "speak", args: { text: `第${callCount}轮回复。` } }],
      };
    });

    const responses: { type: string; fallback?: boolean; speech: string }[] = [];
    for (let i = 0; i < ROUNDS; i++) {
      const r = asDialogueResponse(await adapter.routeMessage(makeDialogueRequest("Abigail", `第${i + 1}轮`)));
      responses.push({ type: r.type, ...(r.fallback !== undefined ? { fallback: r.fallback } : {}), speech: r.speech });
    }

    // 断言 1：50 轮全部成功
    expect(responses.length).toBe(ROUNDS);
    expect(responses.every((r) => r.type === "dialogue_response")).toBe(true);
    expect(responses.every((r) => !r.fallback)).toBe(true);
    // 断言 2：每轮 speech 非空
    expect(responses.every((r) => r.speech.length > 0)).toBe(true);

    // 断言 3：锁已释放（可立即发起新对话）
    const locked = await registry.acquireLock("Abigail");
    expect(locked).toBe(true);
    registry.releaseLock("Abigail");

    // 断言 4：记忆有界（conversationHistory ≤ 50, shortTermMemories ≤ 30）
    const agent = registry.getOrCreate("Abigail");
    const memory = (agent as unknown as { memory: { conversationHistory: unknown[]; shortTermMemories: unknown[] } }).memory;
    expect(memory.conversationHistory.length).toBeLessThanOrEqual(50);
    expect(memory.shortTermMemories.length).toBeLessThanOrEqual(30);
  } finally {
    await adapter.flushPendingSaves();
    rmSync(dir, { recursive: true, force: true });
  }
});

// ════════════════════════════════════════════════════════════════════════
// 长跑 2：故障注入→恢复→继续 50 轮不崩
// ════════════════════════════════════════════════════════════════════════

test("长跑 2: 前 5 轮故障 + 中间 45 轮恢复 — 故障期间 fallback，恢复后正常", async () => {
  const { adapter, registry, dir, setLlmBehavior } = makeFullStack();
  try {
    let round = 0;
    setLlmBehavior(async () => {
      round++;
      if (round <= 5) {
        throw new Error("LLM down");
      }
      return {
        content: "",
        toolCalls: [{ id: `tc-${round}`, name: "speak", args: { text: `恢复后第${round}轮。` } }],
      };
    });

    const results: { fallback: boolean; speech: string }[] = [];
    for (let i = 0; i < ROUNDS; i++) {
      const r = asDialogueResponse(await adapter.routeMessage(makeDialogueRequest("Haley", `第${i + 1}轮`)));
      results.push({ fallback: !!r.fallback, speech: r.speech });
    }

    // 前 5 轮 fallback
    expect(results.slice(0, 5).every((r) => r.fallback)).toBe(true);
    // 后 45 轮成功
    expect(results.slice(5).every((r) => !r.fallback)).toBe(true);
    // 全部 speech 非空（fallback 也有非空 speech）
    expect(results.every((r) => r.speech.length > 0)).toBe(true);

    // 锁释放
    const locked = await registry.acquireLock("Haley");
    expect(locked).toBe(true);
    registry.releaseLock("Haley");
  } finally {
    await adapter.flushPendingSaves();
    rmSync(dir, { recursive: true, force: true });
  }
});

// ════════════════════════════════════════════════════════════════════════
// 长跑 3：50 轮中交替注入 tool_result 反馈，验证队列消费/积累不泄漏
// ════════════════════════════════════════════════════════════════════════

test("长跑 3: 50 轮中每 5 轮注入 tool_result，验证队列在成功后被消费不积累", async () => {
  const { adapter, registry, dir, setLlmBehavior } = makeFullStack();
  try {
    let round = 0;
    setLlmBehavior(async () => {
      round++;
      return {
        content: "",
        toolCalls: [{ id: `tc-${round}`, name: "speak", args: { text: `第${round}轮。` } }],
      };
    });

    for (let i = 0; i < ROUNDS; i++) {
      // 每 5 轮注入一条 tool_result
      if (i % 5 === 0 && i > 0) {
        registry.enqueueToolResult("Sebastian", {
          callId: `c-${i}`, tool: "give_gift", success: true, result: `成功${i}`,
        });
      }
      const r = asDialogueResponse(await adapter.routeMessage(makeDialogueRequest("Sebastian", `第${i + 1}轮`)));
      expect(r.fallback).toBeFalsy();

      // 不变量：成功路径后队列应被消费并清空（除非本轮注入后还未消费）
      // 由于 dialogue 是串行的，注入后下一轮 dialogue 会消费并清空
      // 所以除"本轮刚注入还未到 dialogue"外，队列应为空
    }

    // 50 轮后队列应为空（最后一轮 dialogue 已消费任何遗留）
    expect(registry.drainToolResults("Sebastian").length).toBe(0);
  } finally {
    await adapter.flushPendingSaves();
    rmSync(dir, { recursive: true, force: true });
  }
});

// ════════════════════════════════════════════════════════════════════════
// 长跑 4：50 轮中交替发送 state_changed，验证 actualState 始终反映最新
// ════════════════════════════════════════════════════════════════════════

test("长跑 4: 50 轮中交替发送 state_changed，actualState 始终反映最新状态", async () => {
  const { adapter, registry, dir, setLlmBehavior, getLastSystemPrompt } = makeFullStack();
  try {
    setLlmBehavior(async () => ({
      content: "",
      toolCalls: [{ id: "tc-1", name: "speak", args: { text: "好的。" } }],
    }));

    const states = ["IDLE", "FOLLOW", "FARM", "MINE", "FORAGE"];
    let lastSentState = "IDLE";

    for (let i = 0; i < ROUNDS; i++) {
      // 每 10 轮切换一次状态
      if (i % 10 === 0 && i > 0) {
        lastSentState = states[(i / 10) % states.length]!;
        await adapter.routeMessage({
          type: "state_changed",
          npcName: "Penny",
          previousState: states[((i / 10) - 1 + states.length) % states.length]!,
          newState: lastSentState,
          wasForced: false,
          previousStateDurationMs: i * 1000,
          reason: "llm_decision",
        });
        // 不变量：actualState 立即更新
        expect(registry.getActualState("Penny")).toBe(lastSentState);
      }

      // 每轮 dialogue 不影响 actualState（I1 不变量在长跑中保持）
      await adapter.routeMessage(makeDialogueRequest("Penny", `第${i + 1}轮`));
      if (i > 0 && i % 10 === 0) {
        // state_changed 发送后 actualState 不被 dialogue 改写
        expect(registry.getActualState("Penny")).toBe(lastSentState);
      }
    }

    // 最终 actualState 仍反映最后一次 state_changed
    if (lastSentState !== "IDLE") {
      expect(registry.getActualState("Penny")).toBe(lastSentState);
    }

    // prompt 包含最新状态
    await adapter.routeMessage(makeDialogueRequest("Penny", "最终轮"));
    const prompt = getLastSystemPrompt();
    if (lastSentState !== "IDLE") {
      expect(prompt).toContain(`当前实际状态：${lastSentState}`);
    }
  } finally {
    await adapter.flushPendingSaves();
    rmSync(dir, { recursive: true, force: true });
  }
});

// ════════════════════════════════════════════════════════════════════════
// 长跑 5：50 轮多 NPC 混合对话，验证 NPC 隔离
// ════════════════════════════════════════════════════════════════════════

test("长跑 5: 50 轮多 NPC 混合对话 — NPC 间状态/队列/记忆完全隔离", async () => {
  const { adapter, registry, dir, setLlmBehavior } = makeFullStack();
  try {
    setLlmBehavior(async () => ({
      content: "",
      toolCalls: [{ id: "tc-1", name: "speak", args: { text: "好的。" } }],
    }));

    const npcs = ["Abigail", "Haley", "Sebastian"];
    for (let i = 0; i < ROUNDS; i++) {
      const npc = npcs[i % npcs.length]!;
      await adapter.routeMessage(makeDialogueRequest(npc, `第${i + 1}轮`));
    }

    // 每个 NPC 都有独立的 agent 实例
    for (const npc of npcs) {
      expect(registry.hasAgent(npc)).toBe(true);
      // 锁已释放
      const locked = await registry.acquireLock(npc);
      expect(locked).toBe(true);
      registry.releaseLock(npc);
    }

    // 注入 state_changed 到一个 NPC，不影响其他
    await adapter.routeMessage({
      type: "state_changed",
      npcName: "Abigail",
      previousState: "IDLE",
      newState: "FOLLOW",
      wasForced: false,
      previousStateDurationMs: 1000,
    });
    expect(registry.getActualState("Abigail")).toBe("FOLLOW");
    expect(registry.getActualState("Haley")).toBeUndefined();
    expect(registry.getActualState("Sebastian")).toBeUndefined();

    // 注入 tool_result 到一个 NPC，不影响其他
    registry.enqueueToolResult("Haley", {
      callId: "h1", tool: "give_gift", success: true, result: "成功",
    });
    expect(registry.drainToolResults("Haley").length).toBe(1);
    expect(registry.drainToolResults("Abigail").length).toBe(0);
    expect(registry.drainToolResults("Sebastian").length).toBe(0);
  } finally {
    await adapter.flushPendingSaves();
    rmSync(dir, { recursive: true, force: true });
  }
});

// ════════════════════════════════════════════════════════════════════════
// 长跑 6：50 轮全部 fallback，验证降级路径稳定性
// ════════════════════════════════════════════════════════════════════════

test("长跑 6: 50 轮全部 LLM 故障 — 全部 fallback 但系统不崩，锁正确释放", async () => {
  const { adapter, registry, dir, setLlmBehavior } = makeFullStack();
  try {
    setLlmBehavior(async () => { throw new Error("LLM permanently down"); });

    let fallbackCount = 0;
    for (let i = 0; i < ROUNDS; i++) {
      const r = asDialogueResponse(await adapter.routeMessage(makeDialogueRequest("Maru", `第${i + 1}轮`)));
      if (r.fallback) fallbackCount++;
      expect(r.speech.length).toBeGreaterThan(0);
    }

    // 全部 50 轮 fallback
    expect(fallbackCount).toBe(ROUNDS);

    // 锁仍能正常获取（finally 块释放）
    const locked = await registry.acquireLock("Maru");
    expect(locked).toBe(true);
    registry.releaseLock("Maru");

    // 记忆有界（fallback 也写 conversationHistory）
    const agent = registry.getOrCreate("Maru");
    const memory = (agent as unknown as { memory: { conversationHistory: unknown[]; shortTermMemories: unknown[] } }).memory;
    expect(memory.conversationHistory.length).toBeLessThanOrEqual(50);
    expect(memory.shortTermMemories.length).toBeLessThanOrEqual(30);
  } finally {
    await adapter.flushPendingSaves();
    rmSync(dir, { recursive: true, force: true });
  }
});

// ════════════════════════════════════════════════════════════════════════
// 长跑 7：50 轮中 evaluate_friendship 多次触发，验证好感度累计字段稳定
// ════════════════════════════════════════════════════════════════════════

test("长跑 7: 50 轮中 LLM 多次输出 evaluate_friendship — friendshipDelta 字段每次正确透传", async () => {
  const { adapter, dir, setLlmBehavior } = makeFullStack();
  try {
    let round = 0;
    setLlmBehavior(async () => {
      round++;
      // 偶数轮输出 evaluate_friendship，奇数轮不输出
      if (round % 2 === 0) {
        return {
          content: "",
          toolCalls: [
            { id: `tc-s-${round}`, name: "speak", args: { text: `第${round}轮。` } },
            { id: `tc-f-${round}`, name: "evaluate_friendship", args: { delta: round, reason: `第${round}轮好感度变化` } },
          ],
        };
      }
      return {
        content: "",
        toolCalls: [{ id: `tc-s-${round}`, name: "speak", args: { text: `第${round}轮。` } }],
      };
    });

    let withDelta = 0;
    let withoutDelta = 0;
    for (let i = 0; i < ROUNDS; i++) {
      const r = asDialogueResponse(await adapter.routeMessage(makeDialogueRequest("Leah", `第${i + 1}轮`)));
      expect(r.fallback).toBeFalsy();
      if (r.friendshipDelta !== undefined) {
        withDelta++;
        // delta 应等于当前轮数（round 从 1 开始）
        expect(r.friendshipDelta).toBe(i + 1);
        expect(r.friendshipReason).toBe(`第${i + 1}轮好感度变化`);
      } else {
        withoutDelta++;
      }
    }

    // 50 轮中 25 轮有 delta，25 轮无
    expect(withDelta).toBe(25);
    expect(withoutDelta).toBe(25);
  } finally {
    await adapter.flushPendingSaves();
    rmSync(dir, { recursive: true, force: true });
  }
});
