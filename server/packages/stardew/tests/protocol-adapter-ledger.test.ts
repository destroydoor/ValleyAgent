import { test, expect } from "bun:test";
import { ProtocolAdapter } from "../src/protocol-adapter";
import { AgentLedger } from "../src/agent-ledger";
import { AgentMemory } from "../src/agent-memory";
import { NpcPromptLoader } from "../src/npc-prompt-loader";
import { PromptBuilder } from "../src/prompt-builder";
import { VercelAIProvider } from "@valley/core";
import { StardewAgentRegistry } from "../src/stardew-agent-registry";
import { mkdtempSync, rmSync } from "fs";
import { join } from "path";
import { tmpdir } from "os";
import { resolve } from "path";
import type { ExecuteAdjustMessage, AdjustResultMessage, AdjustOp, DialogueRequest, WorldSnapshot } from "../src/types";

const DATA_PATH = resolve(import.meta.dir, "../data/npc_prompts.json");

interface Harness {
  adapter: ProtocolAdapter;
  ledger: AgentLedger;
  sent: unknown[];
  dir: string;
}

/**
 * 2026-08-15 账本迁移（步骤 1）协议测试台：ProtocolAdapter + AgentLedger +
 * sendToCsharp 捕获器。验证 execute_adjust 下发、adjust_result 回执路由、
 * 超时回滚、账本 pending→committed/rolled_back 闭环。
 */
function makeHarness(opts: { timeoutMs?: number; reconcileRetryMs?: number; noChannel?: boolean } = {}): Harness {
  const dir = mkdtempSync(join(tmpdir(), "valley-ledger-adapter-"));
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
  const ledger = new AgentLedger(dir);
  const sent: unknown[] = [];
  const adapter = new ProtocolAdapter(registry, {
    ledger,
    ...(opts.noChannel ? {} : { sendToCsharp: (msg) => { sent.push(msg); } }),
    ...(opts.timeoutMs !== undefined ? { adjustTimeoutMs: opts.timeoutMs } : {}),
    ...(opts.reconcileRetryMs !== undefined ? { adjustReconcileRetryMs: opts.reconcileRetryMs } : {}),
  });
  return { adapter, ledger, sent, dir };
}

function moneyOp(target: "player" | "npc", amount: number): AdjustOp {
  return { kind: "money", target, amount, reason: "test" };
}

function makeAdjust(npcName: string, instructionId: string, ops: AdjustOp[]): ExecuteAdjustMessage {
  return { type: "execute_adjust", requestId: `req-${instructionId}`, instructionId, npcName, ops };
}

function makeResult(instructionId: string, npcName: string, success: boolean, npcMoney?: number): AdjustResultMessage {
  return {
    type: "adjust_result",
    requestId: `req-${instructionId}`,
    instructionId,
    npcName,
    success,
    steps: [],
    ...(npcMoney !== undefined ? { npcMoney } : {}),
  };
}

/** 完整对话消息（含 2026-08-16 联机 playerId 字段）。 */
function makeDialogue(npcName: string, playerId?: string): DialogueRequest {
  return {
    type: "dialogue",
    requestId: `req-dlg-${npcName}`,
    npcName,
    playerInput: "你好。",
    ...(playerId !== undefined ? { playerId } : {}),
    worldSnapshot: {
      season: "summer", day: 28, time: "14:30", weather: "sunny",
      location: "Town", npcTile: { x: 32, y: 18 }, nearbyObjects: "",
      friendship: 250, npcState: "IDLE", inventory: [], farmerName: "农夫",
      playerMoney: 500, npcLocation: "Town", npcMoney: 1000,
      npcInventory: [{ name: "Amethyst", quantity: 3 }],
      playerHeldItem: { itemId: "(O)66", name: "Amethyst", qty: 5, marketPrice: 200 },
    } satisfies WorldSnapshot,
  };
}

/**
 * 轮询等待谓词成立（审计 D4：替代固定 sleep——CI 慢机下 20/80ms 不够就挂，
 * 且失败形态是干净的断言超时而非 TypeError）。默认 2s 截止、10ms 步进。
 */
async function waitFor(predicate: () => boolean, timeoutMs = 2000, stepMs = 10): Promise<void> {
  const deadline = Date.now() + timeoutMs;
  while (!predicate()) {
    if (Date.now() > deadline) throw new Error(`waitFor timeout after ${timeoutMs}ms`);
    await new Promise((r) => setTimeout(r, stepMs));
  }
}

test("sendAdjust sends execute_adjust via sendToCsharp with instructionId", async () => {
  const { adapter, sent, dir } = makeHarness();
  try {
    const msg = makeAdjust("Abigail", "ins-1", [moneyOp("npc", -100)]);
    const promise = adapter.sendAdjust(msg);
    expect(sent).toHaveLength(1);
    const sentMsg = sent[0] as ExecuteAdjustMessage;
    expect(sentMsg.type).toBe("execute_adjust");
    expect(sentMsg.instructionId).toBe("ins-1");
    expect(sentMsg.npcName).toBe("Abigail");
    expect(sentMsg.ops).toHaveLength(1);

    // 回执到达前 promise 未决
    let settled = false;
    promise.then(() => { settled = true; });
    await new Promise((r) => setTimeout(r, 20));
    expect(settled).toBe(false);

    await adapter.routeMessage(makeResult("ins-1", "Abigail", true, 900));
    const result = await promise;
    expect(result.success).toBe(true);
    expect(result.npcMoney).toBe(900);
  } finally {
    await adapter?.flushPendingSaves();
    rmSync(dir, { recursive: true, force: true });
  }
});

test("adjust_result commits the ledger pending → committed with applied deltas", async () => {
  const { adapter, ledger, dir } = makeHarness();
  try {
    await ledger.load("Abigail");
    ledger.seedFromSnapshot("Abigail", {
      season: "summer", day: 1, timeStr: "09:00", weather: "sunny", location: "Town",
      nearbyObjects: "", farmerName: "农夫", friendship: 0, npcState: "IDLE",
      inventory: [], npcTile: { x: 0, y: 0 }, playerMoney: 0, npcLocation: "Town",
      npcMoney: 500, npcInventory: [], playerHeldItem: null, currentGoal: null,
      npcMood: null, npcRecentEvents: null, npcWorkingOn: null, npcOwedMoney: null,
    });
    const msg = makeAdjust("Abigail", "ins-2", [moneyOp("npc", -100)]);
    expect(ledger.beginPending("Abigail", "ins-2", msg.requestId, msg.ops).ok).toBe(true);
    const promise = adapter.sendAdjust(msg);
    await adapter.routeMessage(makeResult("ins-2", "Abigail", true, 400));

    const result = await promise;
    expect(result.success).toBe(true);
    expect(ledger.getMoney("Abigail")).toBe(400); // 500 - 100
    // 2026-08-23 终态清理契约：committed 条目从 pending 表删除（无界增长修复），
    // 入账结果以余额/lastConfirmedNpcMoney 为准。
    expect(ledger.getPending("Abigail", "ins-2")).toBeUndefined();
  } finally {
    await adapter?.flushPendingSaves();
    rmSync(dir, { recursive: true, force: true });
  }
});

test("failed adjust_result rolls back the ledger pending (zero side effects)", async () => {
  const { adapter, ledger, dir } = makeHarness();
  try {
    await ledger.load("Abigail");
    ledger.seedFromSnapshot("Abigail", {
      season: "summer", day: 1, timeStr: "09:00", weather: "sunny", location: "Town",
      nearbyObjects: "", farmerName: "农夫", friendship: 0, npcState: "IDLE",
      inventory: [], npcTile: { x: 0, y: 0 }, playerMoney: 0, npcLocation: "Town",
      npcMoney: 500, npcInventory: [], playerHeldItem: null, currentGoal: null,
      npcMood: null, npcRecentEvents: null, npcWorkingOn: null, npcOwedMoney: null,
    });
    const msg = makeAdjust("Abigail", "ins-3", [moneyOp("npc", -100)]);
    expect(ledger.beginPending("Abigail", "ins-3", msg.requestId, msg.ops).ok).toBe(true);
    const promise = adapter.sendAdjust(msg);
    await adapter.routeMessage({
      ...makeResult("ins-3", "Abigail", false),
      failureCode: "insufficientFunds",
    });

    const result = await promise;
    expect(result.success).toBe(false);
    expect(result.failureCode).toBe("insufficientFunds");
    expect(ledger.getMoney("Abigail")).toBe(500); // 未入账
    // 终态清理契约（2026-08-23）：rolled_back 即删除，零副作用以余额不变为准。
    expect(ledger.getPending("Abigail", "ins-3")).toBeUndefined();
  } finally {
    await adapter?.flushPendingSaves();
    rmSync(dir, { recursive: true, force: true });
  }
});

test("receipt timeout keeps pending for reconciliation (no rollback) and returns unconfirmed failure", async () => {
  const { adapter, ledger, dir, sent } = makeHarness({ timeoutMs: 50, reconcileRetryMs: 120 });
  try {
    await ledger.load("Abigail");
    ledger.seedFromSnapshot("Abigail", {
      season: "summer", day: 1, timeStr: "09:00", weather: "sunny", location: "Town",
      nearbyObjects: "", farmerName: "农夫", friendship: 0, npcState: "IDLE",
      inventory: [], npcTile: { x: 0, y: 0 }, playerMoney: 0, npcLocation: "Town",
      npcMoney: 500, npcInventory: [], playerHeldItem: null, currentGoal: null,
      npcMood: null, npcRecentEvents: null, npcWorkingOn: null, npcOwedMoney: null,
    });
    const msg = makeAdjust("Abigail", "ins-4", [moneyOp("npc", -100)]);
    expect(ledger.beginPending("Abigail", "ins-4", msg.requestId, msg.ops).ok).toBe(true);

    // 无回执 → 50ms 超时：合成"未确认"失败，但 pending 保留（不进终态——C# 可能已执行，
    // 回滚会让 LLM 重试导致双倍扣钱）。
    const result = await adapter.sendAdjust(msg);
    expect(result.success).toBe(false);
    expect(result.failureCode).toBe("internalError");
    expect(result.steps[0]?.detail).toContain("receipt timeout");
    expect(ledger.getMoney("Abigail")).toBe(500); // 未入账
    expect(ledger.getPending("Abigail", "ins-4")?.status).toBe("pending"); // 关键：保留 pending

    // reconcileRetryMs 后凭原 instructionId 重发（C# 幂等缓存应返回原回执）
    await new Promise((r) => setTimeout(r, 200));
    const resends = sent.filter((m) => (m as ExecuteAdjustMessage).instructionId === "ins-4");
    expect(resends.length).toBe(2); // 首次下发 + 对账重发

    // 迟到回执到达 → 正常 commit 闭环
    await adapter.routeMessage({
      ...makeResult("ins-4", "Abigail", true, 400),
      steps: [{ index: 0, kind: "money", target: "npc", success: true, failureCode: "none" }],
    });
    expect(ledger.getMoney("Abigail")).toBe(400);
    // 终态清理契约（2026-08-23）：迟到回执 commit 后条目删除，入账以余额为准。
    expect(ledger.getPending("Abigail", "ins-4")).toBeUndefined();
  } finally {
    await adapter?.flushPendingSaves();
    rmSync(dir, { recursive: true, force: true });
  }
});

test("sendAdjust without channel fails immediately with rollback", async () => {
  const { adapter, ledger, dir } = makeHarness({ noChannel: true });
  try {
    await ledger.load("Abigail");
    ledger.seedFromSnapshot("Abigail", {
      season: "summer", day: 1, timeStr: "09:00", weather: "sunny", location: "Town",
      nearbyObjects: "", farmerName: "农夫", friendship: 0, npcState: "IDLE",
      inventory: [], npcTile: { x: 0, y: 0 }, playerMoney: 0, npcLocation: "Town",
      npcMoney: 500, npcInventory: [], playerHeldItem: null, currentGoal: null,
      npcMood: null, npcRecentEvents: null, npcWorkingOn: null, npcOwedMoney: null,
    });
    const msg = makeAdjust("Abigail", "ins-5", [moneyOp("npc", -100)]);
    expect(ledger.beginPending("Abigail", "ins-5", msg.requestId, msg.ops).ok).toBe(true);

    const result = await adapter.sendAdjust(msg);
    expect(result.success).toBe(false);
    // 终态清理契约（2026-08-23）：无通道失败回滚后条目删除。
    expect(ledger.getPending("Abigail", "ins-5")).toBeUndefined();
    expect(ledger.getMoney("Abigail")).toBe(500);
  } finally {
    await adapter?.flushPendingSaves();
    rmSync(dir, { recursive: true, force: true });
  }
});

test("duplicate instructionId reuses the pending waiter (幂等重发)", async () => {
  const { adapter, sent, dir } = makeHarness();
  try {
    const msg = makeAdjust("Abigail", "ins-6", [moneyOp("npc", -10)]);
    const first = adapter.sendAdjust(msg);
    const second = adapter.sendAdjust(msg); // 重发：复用等待，不重复投递
    expect(sent).toHaveLength(1);

    await adapter.routeMessage(makeResult("ins-6", "Abigail", true, 90));
    const [r1, r2] = await Promise.all([first, second]);
    expect(r1.success).toBe(true);
    expect(r2.success).toBe(true);
  } finally {
    await adapter?.flushPendingSaves();
    rmSync(dir, { recursive: true, force: true });
  }
});

test("late duplicate adjust_result without waiter is ignored gracefully", async () => {
  const { adapter, ledger, dir } = makeHarness();
  try {
    await ledger.load("Abigail");
    ledger.seedFromSnapshot("Abigail", {
      season: "summer", day: 1, timeStr: "09:00", weather: "sunny", location: "Town",
      nearbyObjects: "", farmerName: "农夫", friendship: 0, npcState: "IDLE",
      inventory: [], npcTile: { x: 0, y: 0 }, playerMoney: 0, npcLocation: "Town",
      npcMoney: 500, npcInventory: [], playerHeldItem: null, currentGoal: null,
      npcMood: null, npcRecentEvents: null, npcWorkingOn: null, npcOwedMoney: null,
    });
    // 先正常走完一笔（waiter 已消费）
    const msg = makeAdjust("Abigail", "ins-7", [moneyOp("npc", -100)]);
    expect(ledger.beginPending("Abigail", "ins-7", msg.requestId, msg.ops).ok).toBe(true);
    const promise = adapter.sendAdjust(msg);
    await adapter.routeMessage(makeResult("ins-7", "Abigail", true, 400));
    await promise;

    // 重复回执：无 waiter → 幂等跳过，不崩溃、不重复入账
    const ack = await adapter.routeMessage(makeResult("ins-7", "Abigail", true, 400));
    expect(ack.type).toBe("ack");
    expect(ledger.getMoney("Abigail")).toBe(400);
  } finally {
    await adapter?.flushPendingSaves();
    rmSync(dir, { recursive: true, force: true });
  }
});

test("routeMessage routes execute_adjust and adjust_result to handlers", async () => {
  const { adapter, sent, dir } = makeHarness();
  try {
    const adjustAck = await adapter.routeMessage(makeAdjust("Abigail", "ins-8", [moneyOp("npc", -1)]));
    expect(adjustAck.type).toBe("ack");
    expect(sent).toHaveLength(1);

    const resultAck = await adapter.routeMessage(makeResult("ins-8", "Abigail", true, 99));
    expect(resultAck.type).toBe("ack");
  } finally {
    await adapter?.flushPendingSaves();
    rmSync(dir, { recursive: true, force: true });
  }
});

test("dialogue seeds the ledger from worldSnapshot (首次见 NPC 播种)", async () => {
  const { adapter, ledger, dir } = makeHarness();
  try {
    const snap = {
      type: "dialogue" as const,
      requestId: "req-d1",
      npcName: "Abigail",
      playerInput: "你好",
      worldSnapshot: {
        season: "summer", day: 28, time: "14:30", weather: "sunny",
        location: "Town", npcTile: { x: 32, y: 18 },
        nearbyObjects: "", friendship: 250, npcState: "IDLE",
        inventory: [], farmerName: "农夫",
        playerMoney: 500,
        npcLocation: "Town",
        npcMoney: 777,
        npcInventory: [{ name: "Amethyst", quantity: 2 }],
      },
    };
    await adapter.routeMessage(snap);

    expect(ledger.getMoney("Abigail")).toBe(777);
    expect(ledger.getOrCreate("Abigail").items["Amethyst"]?.quantity).toBe(2);
    expect(ledger.getOrCreate("Abigail").seeded).toBe(true);
  } finally {
    await adapter?.flushPendingSaves();
    rmSync(dir, { recursive: true, force: true });
  }
});

// ── 2026-08-15 步骤 2：adjustEconomy 编排 + 对话拦截 ──

/** 种子一个已播种账本（npcMoney/npcInventory）并确保已 load。 */
async function seedLedger(ledger: AgentLedger, npcName: string, money: number, items: Array<{ name: string; quantity: number }>): Promise<void> {
  await ledger.load(npcName);
  ledger.seedFromSnapshot(npcName, {
    season: "summer", day: 1, timeStr: "09:00", weather: "sunny", location: "Town",
    nearbyObjects: "", farmerName: "农夫", friendship: 0, npcState: "IDLE",
    inventory: [], npcTile: { x: 0, y: 0 }, playerMoney: 0, npcLocation: "Town",
    npcMoney: money, npcInventory: items, playerHeldItem: null, currentGoal: null,
    npcMood: null, npcRecentEvents: null, npcWorkingOn: null, npcOwedMoney: null,
  });
}

test("adjustEconomy 业务校验拒绝：不发指令、不记 pending、返回失败回执", async () => {
  const { adapter, ledger, sent, dir } = makeHarness();
  try {
    await seedLedger(ledger, "Abigail", 100, []);

    const result = await adapter.adjustEconomy("Abigail", [moneyOp("npc", -150)]);

    expect(result.success).toBe(false);
    expect(result.failureCode).toBe("insufficientFunds");
    expect(sent).toHaveLength(0); // 业务校验失败 → 不下发 execute_adjust
    expect(ledger.getPending("Abigail", result.instructionId)).toBeUndefined();
  } finally {
    await adapter?.flushPendingSaves();
    rmSync(dir, { recursive: true, force: true });
  }
});

test("adjustEconomy 成功闭环：下发 → 回执 → 账本 committed", async () => {
  const { adapter, ledger, sent, dir } = makeHarness();
  try {
    await seedLedger(ledger, "Abigail", 500, []);
    const promise = adapter.adjustEconomy("Abigail", [moneyOp("npc", -100)]);
    // adjustEconomy 内部先 await ledger.load() 再下发——轮询等待 execute_adjust 到达捕获器
    await waitFor(() => sent.length === 1);
    expect(sent).toHaveLength(1);
    const sentMsg = sent[0] as ExecuteAdjustMessage;
    expect(sentMsg.type).toBe("execute_adjust");
    expect(sentMsg.ops).toEqual([moneyOp("npc", -100)]);

    await adapter.routeMessage({ ...makeResult(sentMsg.instructionId, "Abigail", true), npcMoney: 400 });
    const result = await promise;
    expect(result.success).toBe(true);
    expect(ledger.getMoney("Abigail")).toBe(400);
    // 终态清理契约（2026-08-23）：adjustEconomy 成功闭环后条目删除。
    expect(ledger.getPending("Abigail", sentMsg.instructionId)).toBeUndefined();
  } finally {
    await adapter?.flushPendingSaves();
    rmSync(dir, { recursive: true, force: true });
  }
});

test("adjustEconomy C# 物理校验失败：回执失败 → 账本 rolled_back 零变更", async () => {
  const { adapter, ledger, dir } = makeHarness();
  try {
    await seedLedger(ledger, "Abigail", 500, []);
    const promise = adapter.adjustEconomy("Abigail", [moneyOp("npc", -100)]);

    // 从 pending 拿到 instructionId（无 channel 捕获时直接查账本）——轮询等 pending 落账
    await waitFor(() => Object.keys(ledger.getOrCreate("Abigail").pending).length > 0);
    const instructionId = Object.keys(ledger.getOrCreate("Abigail").pending)[0]!;
    await adapter.routeMessage({ ...makeResult(instructionId, "Abigail", false), failureCode: "insufficientFunds" });

    const result = await promise;
    expect(result.success).toBe(false);
    expect(ledger.getMoney("Abigail")).toBe(500); // 未入账
    // 终态清理契约（2026-08-23）：C# 物理校验失败回滚后条目删除。
    expect(ledger.getPending("Abigail", instructionId)).toBeUndefined();
  } finally {
    await adapter?.flushPendingSaves();
    rmSync(dir, { recursive: true, force: true });
  }
});

test("对话拦截：give_item 同步执行落账，actions 不含 give_item", async () => {
  // 定制 LLM：先调 give_item 再 speak —— 经济工具必须在 ReAct 内同步完成
  const dir = mkdtempSync(join(tmpdir(), "valley-ledger-dialogue-"));
  const loader = new NpcPromptLoader(DATA_PATH);
  const builder = new PromptBuilder(loader);
  const provider = new VercelAIProvider({
    provider: "minimax", apiKey: "fake", model: "fake", baseUrl: "http://localhost:9999",
  });
  provider._setCallOverride(async () => ({
    content: "",
    toolCalls: [
      { id: "tc-item", name: "give_item", args: { item_id: "(O)66", quantity: 1 } },
      { id: "tc-speak", name: "speak", args: { text: "这个送给你。" } },
    ],
  }));
  const registry = new StardewAgentRegistry({ promptBuilder: builder, llmProvider: provider, agentsDir: dir });
  const ledger = new AgentLedger(dir);
  await seedLedger(ledger, "Abigail", 500, [{ name: "Amethyst", quantity: 2 }]);
  const sent: unknown[] = [];
  const adapter = new ProtocolAdapter(registry, {
    ledger,
    sendToCsharp: (msg) => { sent.push(msg); },
  });
  try {
    // 对话是异步的：先发起，等 execute_adjust 发出后模拟 C# 回执，再收对话结果。
    // （ReAct 循环内 give_item 同步执行会 await 回执——不回执则 10s 超时回滚。）
    const respPromise = adapter.routeMessage({
      type: "dialogue",
      requestId: "req-d2",
      npcName: "Abigail",
      playerInput: "送我一个紫水晶吧",
      worldSnapshot: {
        season: "summer", day: 28, time: "14:30", weather: "sunny",
        location: "Town", npcTile: { x: 32, y: 18 }, nearbyObjects: "",
        friendship: 250, npcState: "IDLE", inventory: [], farmerName: "农夫",
        playerMoney: 500, npcLocation: "Town", npcMoney: 500,
        npcInventory: [{ name: "Amethyst", quantity: 2 }],
      },
    });
    // 轮询等 ReAct 循环发出 execute_adjust（固定 80ms 在 CI 慢机上不够）
    await waitFor(() => sent.some((m) => (m as ExecuteAdjustMessage).type === "execute_adjust"));
    const adjust = sent.find((m) => (m as ExecuteAdjustMessage).type === "execute_adjust") as ExecuteAdjustMessage | undefined;
    expect(adjust).toBeDefined();
    expect(adjust!.ops.some((op) => op.kind === "item" && op.target === "npc" && op.quantity === -1)).toBe(true);
    await adapter.routeMessage({
      type: "adjust_result",
      requestId: adjust!.requestId,
      instructionId: adjust!.instructionId,
      npcName: "Abigail",
      success: true,
      steps: [],
    });
    const resp = await respPromise;

    expect(resp.type).toBe("dialogue_response");
    if (resp.type === "dialogue_response") {
      // 经济工具同步执行过：不出现在发往 C# 的 actions
      expect(resp.actions.find((a) => a.tool === "give_item")).toBeUndefined();
      expect(resp.actions.find((a) => a.tool === "speak")).toBeUndefined(); // speak 同样不发（已完成对话）
    }
    // 工具内同步写记忆
    const agent = registry.getOrCreate("Abigail");
    const memory = (agent as unknown as { memory: AgentMemory }).memory;
    expect(memory.shortTermMemories.some((m) => m.text.includes("送了农场主 1 个"))).toBe(true);
  } finally {
    await adapter?.flushPendingSaves();
    rmSync(dir, { recursive: true, force: true });
  }
});

// ── 2026-08-15 步骤 4：断线对账（reconnect_sync）──

test("断线对账：pending adjust 重连后凭 instructionId 重发闭环", async () => {
  const dir = mkdtempSync(join(tmpdir(), "valley-reconcile-"));
  let adapter: ProtocolAdapter | null = null;
  try {
    const loader = new NpcPromptLoader(DATA_PATH);
    const builder = new PromptBuilder(loader);
    const provider = new VercelAIProvider({
      provider: "minimax", apiKey: "fake", model: "fake", baseUrl: "http://localhost:9999",
    });
    provider._setCallOverride(async () => ({
      content: "",
      toolCalls: [{ id: "tc-1", name: "speak", args: { text: "你好。" } }],
    }));
    const registry = new StardewAgentRegistry({ promptBuilder: builder, llmProvider: provider, agentsDir: dir });
    const ledger = new AgentLedger(dir);
    await seedLedger(ledger, "Abigail", 500, []);

    // 断线模拟：第一次下发（execute_adjust）被静默丢弃（sendToCsharp 不投递），
    // 重连后（reconnect_sync 到达）reconcile 重发，第二次投递成功。
    let delivered = false;
    const sent: unknown[] = [];
    adapter = new ProtocolAdapter(registry, {
      ledger,
      sendToCsharp: (msg) => {
        if ((msg as ExecuteAdjustMessage).type === "execute_adjust") {
          if (!delivered) return; // 断线期：丢弃
          delivered = true;
        }
        sent.push(msg);
      },
    });

    // 断线期发起交易：execute_adjust 被丢弃 → 无回执 → 10s 后超时回滚。
    // 为测试对账，用 200ms 超时并直接依赖对账路径（不等超时）。
    const promise = adapter.adjustEconomy("Abigail", [moneyOp("npc", -100)]);
    await new Promise((r) => setTimeout(r, 30));
    // pending 已记（未回滚）
    const pendingIds = Object.keys(ledger.getOrCreate("Abigail").pending);
    expect(pendingIds).toHaveLength(1);
    expect(ledger.getPending("Abigail", pendingIds[0]!)?.status).toBe("pending");

    // 重连：C# 发 reconnect_sync（active agent 名单含 Abigail）→ TS 对账重发。
    // 先恢复通道（reconnect_sync 到达即视为已重连），再对账。
    delivered = true;
    const ack = await adapter.routeMessage({
      type: "reconnect_sync",
      requestId: "rs-1",
      replayedOutbox: 2,
      agents: ["Abigail"],
      gameDate: "Y1_summer_28",
    });
    expect(ack.type).toBe("ack");
    await new Promise((r) => setTimeout(r, 30));

    // 重发到达 C#（本次投递成功），回执返回 → 账本 committed
    const adjust = sent.find((m) => (m as ExecuteAdjustMessage).type === "execute_adjust") as ExecuteAdjustMessage | undefined;
    expect(adjust).toBeDefined();
    expect(adjust!.instructionId).toBe(pendingIds[0]!); // 原 instructionId 重发（幂等键）
    await adapter.routeMessage({ ...makeResult(adjust!.instructionId, "Abigail", true), npcMoney: 400 });

    const result = await promise; // adjustEconomy 的原始等待也在回执到达后完成
    expect(result.success).toBe(true);
    expect(ledger.getMoney("Abigail")).toBe(400); // 500 - 100
    // 终态清理契约（2026-08-23）：对账闭环 commit 后条目删除。
    expect(ledger.getPending("Abigail", pendingIds[0]!)).toBeUndefined();
  } finally {
    await adapter?.flushPendingSaves();
    rmSync(dir, { recursive: true, force: true });
  }
});

test("断线对账：非名单内 NPC 的 pending 不重发", async () => {
  const dir = mkdtempSync(join(tmpdir(), "valley-reconcile2-"));
  let adapter: ProtocolAdapter | null = null;
  try {
    const loader = new NpcPromptLoader(DATA_PATH);
    const builder = new PromptBuilder(loader);
    const provider = new VercelAIProvider({
      provider: "minimax", apiKey: "fake", model: "fake", baseUrl: "http://localhost:9999",
    });
    provider._setCallOverride(async () => ({
      content: "",
      toolCalls: [{ id: "tc-1", name: "speak", args: { text: "你好。" } }],
    }));
    const registry = new StardewAgentRegistry({ promptBuilder: builder, llmProvider: provider, agentsDir: dir });
    const ledger = new AgentLedger(dir);
    await seedLedger(ledger, "Abigail", 500, []);
    const sent: unknown[] = [];
    adapter = new ProtocolAdapter(registry, {
      ledger,
      sendToCsharp: (msg) => { sent.push(msg); },
    });

    // 断线模拟：sendToCsharp 全部丢弃（不投递）→ adjustEconomy 超时返回"未确认失败"。
    // 审计 D3：必须 await p0 并 flush——旧写法不 await（默认 10s 超时），
    // 超时回执会在测试结束后于已删除的临时目录里重建 ledger 文件 + 留下跨测试活 timer。
    const dropped: unknown[] = [];
    const noopAdapter = new ProtocolAdapter(registry, {
      ledger,
      adjustTimeoutMs: 50, // 短超时：p0 快速以"未确认失败"落定（超时不再回滚，pending 保留）
      adjustReconcileRetryMs: 60_000, // 对账重发拉长到套件之外，杜绝跨测试 timer
      sendToCsharp: (m) => { dropped.push(m); },
    });
    const p0 = noopAdapter.adjustEconomy("Abigail", [moneyOp("npc", -100)]);
    const result = await p0; // 50ms 超时后返回，不回滚账本（2026-08-17 对账语义）
    expect(result.success).toBe(false);
    expect(ledger.getPending("Abigail", result.instructionId)?.status).toBe("pending");
    await noopAdapter.flushPendingSaves();
    const before = sent.length;

    // reconnect_sync 名单不含 Abigail → 不重发
    await adapter.routeMessage({
      type: "reconnect_sync", requestId: "rs-2", replayedOutbox: 0, agents: ["Haley"],
    });
    await new Promise((r) => setTimeout(r, 30));
    expect(sent.length).toBe(before); // 无新 execute_adjust
  } finally {
    await adapter?.flushPendingSaves();
    rmSync(dir, { recursive: true, force: true });
  }
});

// ════════════════════════════════════════════════════════════════════════
// 2026-08-16 联机 playerId 契约（发送侧）
// C# 侧 IT14 已覆盖接收侧三分支（own-id/ghost-id/缺省），此处补 TS 发送侧：
//   dialogue.playerId → execute_adjust.playerId 透传（全链路）
//   adjust_result.playerId echo 返回调用方
//   playerNotFound 失败码 → 账本 pending 回滚
//   reconnect_sync 重发保留原 playerId
// ════════════════════════════════════════════════════════════════════════

test("playerId contract: dialogue.playerId flows into execute_adjust (trade 全链路透传)", async () => {
  const dir = mkdtempSync(join(tmpdir(), "valley-ledger-pid-"));
  const loader = new NpcPromptLoader(DATA_PATH);
  const builder = new PromptBuilder(loader);
  const provider = new VercelAIProvider({
    provider: "minimax", apiKey: "fake", model: "fake", baseUrl: "http://localhost:9999",
  });
  // 第一轮返回 trade 工具调用（触发经济同步编排），后续轮返回 speak 结束循环。
  let llmCalls = 0;
  provider._setCallOverride(async () => {
    llmCalls++;
    if (llmCalls === 1) {
      return {
        content: "",
        toolCalls: [{ id: "tc-trade", name: "trade", args: { item_id: "(O)66", quantity: 3, price: 150 } }],
      };
    }
    return { content: "", toolCalls: [{ id: "tc-speak", name: "speak", args: { text: "成交。" } }] };
  });
  const registry = new StardewAgentRegistry({ promptBuilder: builder, llmProvider: provider, agentsDir: dir });
  const ledger = new AgentLedger(dir);
  const sent: unknown[] = [];
  const adapter = new ProtocolAdapter(registry, {
    ledger,
    adjustTimeoutMs: 50, // 快速超时：回执未投递时 trade 以"未确认失败"结束，对话不挂 10s
    adjustReconcileRetryMs: 10000, // 拉长对账重发，避免测试结束后 timer 干扰
    sendToCsharp: (msg) => { sent.push(msg); },
  });
  try {
    const playerId = "9876543210";
    const resp = await adapter.routeMessage(makeDialogue("Abigail", playerId));

    // 下发的 execute_adjust 必须携带发起玩家 ID（C# 按此解析目标 Farmer）
    const adjust = sent.find((m) => (m as ExecuteAdjustMessage).type === "execute_adjust") as ExecuteAdjustMessage | undefined;
    expect(adjust, "dialogue 内 trade 应下发 execute_adjust").toBeDefined();
    expect(adjust!.playerId).toBe(playerId);
    expect(adjust!.npcName).toBe("Abigail");
    expect(adjust!.ops.length).toBeGreaterThan(0);

    // dialogue_response 原样 echo playerId（C# 端记录 LastDialoguePlayerId）
    expect(resp.type).toBe("dialogue_response");
    expect((resp as { playerId?: string }).playerId).toBe(playerId);
  } finally {
    await adapter?.flushPendingSaves();
    rmSync(dir, { recursive: true, force: true });
  }
});

test("playerId contract: execute_adjust.playerId echoes through adjust_result to caller", async () => {
  const { adapter, sent, dir } = makeHarness();
  try {
    const msg: ExecuteAdjustMessage = {
      ...makeAdjust("Abigail", "ins-pid-1", [moneyOp("player", 100)]),
      playerId: "111122223333",
    };
    const promise = adapter.sendAdjust(msg);
    expect((sent[0] as ExecuteAdjustMessage).playerId).toBe("111122223333");

    // C# 回执 echo playerId → 调用方（经济工具）拿到的回执保留玩家身份
    await adapter.routeMessage({
      ...makeResult("ins-pid-1", "Abigail", true, 900),
      playerMoney: 600,
      playerId: "111122223333",
    });
    const result = await promise;
    expect(result.success).toBe(true);
    expect(result.playerId).toBe("111122223333");
  } finally {
    await adapter?.flushPendingSaves();
    rmSync(dir, { recursive: true, force: true });
  }
});

test("playerId contract: playerNotFound failure rolls back ledger pending", async () => {
  const { adapter, ledger, dir } = makeHarness();
  try {
    await ledger.load("Abigail");
    ledger.seedFromSnapshot("Abigail", {
      season: "summer", day: 1, timeStr: "09:00", weather: "sunny", location: "Town",
      nearbyObjects: "", farmerName: "农夫", friendship: 0, npcState: "IDLE",
      inventory: [], npcTile: { x: 0, y: 0 }, playerMoney: 0, npcLocation: "Town",
      npcMoney: 500, npcInventory: [], playerHeldItem: null, currentGoal: null,
      npcMood: null, npcRecentEvents: null, npcWorkingOn: null, npcOwedMoney: null,
    });
    const msg: ExecuteAdjustMessage = {
      ...makeAdjust("Abigail", "ins-pid-2", [moneyOp("player", -100)]),
      playerId: "999999999999", // C# 解析不到该 Farmer → playerNotFound
    };
    expect(ledger.beginPending("Abigail", "ins-pid-2", msg.requestId, msg.ops, msg.playerId).ok).toBe(true);
    const promise = adapter.sendAdjust(msg);

    await adapter.routeMessage({
      ...makeResult("ins-pid-2", "Abigail", false),
      failureCode: "playerNotFound",
      steps: [{ index: 0, kind: "batch", target: "", success: false, failureCode: "playerNotFound" }],
    });

    const result = await promise;
    expect(result.success).toBe(false);
    expect(result.failureCode).toBe("playerNotFound");
    expect(ledger.getMoney("Abigail")).toBe(500); // 零副作用
    // 终态清理契约（2026-08-23）：playerNotFound 回滚后条目删除。
    expect(ledger.getPending("Abigail", "ins-pid-2")).toBeUndefined();
  } finally {
    await adapter?.flushPendingSaves();
    rmSync(dir, { recursive: true, force: true });
  }
});

test("playerId contract: reconnect_sync resend preserves original playerId", async () => {
  const { adapter, ledger, sent, dir } = makeHarness();
  try {
    await ledger.load("Abigail");
    ledger.seedFromSnapshot("Abigail", {
      season: "summer", day: 1, timeStr: "09:00", weather: "sunny", location: "Town",
      nearbyObjects: "", farmerName: "农夫", friendship: 0, npcState: "IDLE",
      inventory: [], npcTile: { x: 0, y: 0 }, playerMoney: 0, npcLocation: "Town",
      npcMoney: 500, npcInventory: [], playerHeldItem: null, currentGoal: null,
      npcMood: null, npcRecentEvents: null, npcWorkingOn: null, npcOwedMoney: null,
    });
    // 断线前 in-flight：pending 带 playerId（waiter 已超时清除，走"重新挂等待 + 重发"分支）
    const msg: ExecuteAdjustMessage = {
      ...makeAdjust("Abigail", "ins-pid-3", [moneyOp("npc", -100)]),
      playerId: "555566667777",
    };
    expect(ledger.beginPending("Abigail", "ins-pid-3", msg.requestId, msg.ops, msg.playerId).ok).toBe(true);
    const before = sent.length;

    await adapter.routeMessage({
      type: "reconnect_sync", requestId: "rs-pid", replayedOutbox: 0, agents: ["Abigail"],
    });

    const resends = sent.slice(before).filter((m) => (m as ExecuteAdjustMessage).type === "execute_adjust");
    expect(resends).toHaveLength(1);
    expect((resends[0] as ExecuteAdjustMessage).instructionId).toBe("ins-pid-3");
    expect((resends[0] as ExecuteAdjustMessage).playerId).toBe("555566667777"); // 对账重发不丢玩家身份
  } finally {
    await adapter?.flushPendingSaves();
    rmSync(dir, { recursive: true, force: true });
  }
});
