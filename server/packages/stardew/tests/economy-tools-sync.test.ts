import { test, expect } from "bun:test";
import { buildStardewTools } from "../src/stardew-tools";
import type { ToolContext } from "../src/stardew-tools";
import { AgentMemory } from "../src/agent-memory";
import type { AdjustOp, AdjustResultMessage, SceneState } from "../src/types";

/**
 * 2026-08-15 账本迁移步骤 2：经济工具同步编排测试。
 * trade/give_item/give_gift/receive_payment 在 ReAct 内经 economy.adjust 同步完成
 * （账本校验→execute_adjust→回执），不再作为 action 发 C#。
 * 覆盖：ops 组成与守恒不变量（money/item 增减各自守恒）、成功/失败记忆、
 * get_info inventory 读权威账本。
 */

function makeScene(overrides: Partial<SceneState> = {}): SceneState {
  return {
    season: "summer", day: 28, timeStr: "14:30", weather: "sunny",
    location: "Town", nearbyObjects: "", farmerName: "农夫",
    friendship: 250, npcState: "IDLE", inventory: [],
    npcTile: { x: 0, y: 0 }, playerMoney: 500, npcLocation: "Town",
    npcMoney: 1000, npcInventory: [{ name: "Amethyst", quantity: 3 }],
    playerHeldItem: { itemId: "(O)66", name: "Amethyst", qty: 5, marketPrice: 200 },
    currentGoal: null, npcMood: null, npcRecentEvents: null,
    npcWorkingOn: null, npcOwedMoney: null,
    ...overrides,
  };
}

function receipt(success: boolean, failureCode?: AdjustResultMessage["failureCode"], detail?: string): AdjustResultMessage {
  return {
    type: "adjust_result",
    requestId: "r",
    instructionId: "i",
    npcName: "Abigail",
    success,
    steps: success
      ? []
      : [{ index: 0, kind: "money", target: "npc", success: false, failureCode: failureCode ?? "internalError", ...(detail ? { detail } : {}) }],
    ...(failureCode !== undefined && !success ? { failureCode } : {}),
  };
}

function makeHarness(adjustResult: AdjustResultMessage) {
  const memory = new AgentMemory("Abigail", "/tmp/econ-sync");
  const captured: AdjustOp[][] = [];
  const ctx: ToolContext = {
    memory,
    scene: makeScene(),
    inventory: [{ name: "Amethyst", quantity: 3 }],
    givenToPlayer: [],
    log: [],
    economy: {
      npcName: "Abigail",
      adjust: async (ops) => {
        captured.push(ops);
        return adjustResult;
      },
      getMoney: () => 1000,
      getInventory: () => [{ name: "Amethyst", quantity: 2 }],
    },
  };
  const tools = buildStardewTools(ctx);
  return { memory, ctx, captured, tools };
}

/** 守恒不变量：money 增减和 = 0；每个 itemId 的数量增减和 = 0（player+npc 双向）。 */
function assertConserved(ops: AdjustOp[]): void {
  let moneySum = 0;
  const itemDeltas = new Map<string, number>();
  for (const op of ops) {
    if (op.kind === "money") moneySum += op.amount ?? 0;
    else if (op.kind === "item" && op.itemId) {
      itemDeltas.set(op.itemId, (itemDeltas.get(op.itemId) ?? 0) + (op.quantity ?? 0));
    }
  }
  expect(moneySum).toBe(0);
  for (const [itemId, delta] of itemDeltas) {
    expect(delta, `item ${itemId} 守恒`).toBe(0);
  }
}

// ── trade（NPC 买玩家物品）───────────────────────────

test("trade 同步成功：4 步 ops 守恒 + 记忆", async () => {
  const { memory, captured, tools } = makeHarness(receipt(true));
  const trade = tools.find((t) => t.name === "trade")!;

  const result = await trade.execute({ item_id: "(O)66", quantity: 3, price: 150 });

  expect(result.isError).toBeFalsy();
  expect(captured).toHaveLength(1);
  const ops = captured[0]!;
  // 玩家收钱 + 扣物，NPC 付钱 + 收物（单价 × 数量 = 总价）
  expect(ops).toEqual([
    { kind: "money", target: "player", amount: 450, reason: "trade" },
    { kind: "item", target: "player", itemId: "(O)66", itemName: "Amethyst", quantity: -3, reason: "trade" },
    { kind: "money", target: "npc", amount: -450, reason: "trade" },
    { kind: "item", target: "npc", itemId: "(O)66", itemName: "Amethyst", quantity: 3, reason: "trade" },
  ]);
  assertConserved(ops);
  expect(
    memory.shortTermMemories.some((m) => m.text === "买下了农场主的 3 个 Amethyst，花了 450g" && m.importance === 5.0),
  ).toBe(true);
});

test("trade 同步失败（业务校验拒绝）→ isError + 失败记忆", async () => {
  const { memory, captured, tools } = makeHarness(receipt(false, "insufficientFunds"));
  const trade = tools.find((t) => t.name === "trade")!;

  const result = await trade.execute({ item_id: "(O)66", quantity: 3, price: 150 });

  expect(result.isError).toBe(true);
  expect(String(result.content)).toContain("钱不够");
  expect(captured).toHaveLength(1); // 已下发（业务校验通过），C# 物理校验拒绝
  expect(
    memory.shortTermMemories.some((m) => m.text.includes("想买农场主的 3 个 Amethyst 但没买成") && m.text.includes("钱不够")),
  ).toBe(true);
});

test("trade 参数无效 → 本地拒绝，不下发", async () => {
  const { captured, tools } = makeHarness(receipt(true));
  const trade = tools.find((t) => t.name === "trade")!;

  const result = await trade.execute({ item_id: "(O)66", quantity: 0, price: 100 });

  expect(result.isError).toBe(true);
  expect(captured).toHaveLength(0);
});

// ── give_item / give_gift（NPC 送玩家物品）───────────

test("give_item 同步成功：守恒 + 记忆", async () => {
  const { memory, captured, tools } = makeHarness(receipt(true));
  const giveItem = tools.find((t) => t.name === "give_item")!;

  const result = await giveItem.execute({ item_id: "(O)388", quantity: 2 });

  expect(result.isError).toBeFalsy();
  assertConserved(captured[0]!);
  expect(captured[0]![0]).toEqual({ kind: "item", target: "npc", itemId: "(O)388", itemName: "(O)388", quantity: -2, reason: "give_item" });
  expect(
    memory.shortTermMemories.some((m) => m.text === "送了农场主 2 个 (O)388" && m.importance === 4.0),
  ).toBe(true);
});

test("give_gift 同步失败（物品不足）→ isError + gift 失败记忆", async () => {
  const { memory, tools } = makeHarness(receipt(false, "itemNotFound"));
  const gift = tools.find((t) => t.name === "give_gift")!;

  const result = await gift.execute({ item_id: "Amethyst", quantity: 5 });

  expect(result.isError).toBe(true);
  expect(
    memory.shortTermMemories.some((m) => m.text.includes("想送礼物但没送成") && m.entryType === "gift" && m.importance === 3.0),
  ).toBe(true);
});

// ── receive_payment（玩家付 NPC）─────────────────────

test("receive_payment 同步成功：守恒 + 记忆", async () => {
  const { memory, captured, tools } = makeHarness(receipt(true));
  const pay = tools.find((t) => t.name === "receive_payment")!;

  const result = await pay.execute({ amount: 200, reason: "挖矿工钱" });

  expect(result.isError).toBeFalsy();
  assertConserved(captured[0]!);
  expect(captured[0]![0]).toEqual({ kind: "money", target: "player", amount: -200, reason: "receive_payment:挖矿工钱" });
  expect(captured[0]![1]).toEqual({ kind: "money", target: "npc", amount: 200, reason: "receive_payment:挖矿工钱" });
  expect(
    memory.shortTermMemories.some((m) => m.text === "农场主付了我 200g（挖矿工钱）" && m.importance === 5.0),
  ).toBe(true);
});

// ── get_info 读权威账本 ──────────────────────────────

test("get_info inventory 读账本（economy.getInventory 优先于 scene 镜像）", async () => {
  const { ctx, tools } = makeHarness(receipt(true));
  const info = tools.find((t) => t.name === "get_info")!;

  const result = await info.execute({ query: "inventory" });

  // fake economy.getInventory 返回 [{ Amethyst, 2 }]（账本权威值），非 scene 的 3
  expect(String(result.content)).toContain("Amethyst x2");
  expect(String(result.content)).not.toContain("x3");
  void ctx;
});

test("get_info inventory 未接线 economy → 回退 scene 镜像", async () => {
  const memory = new AgentMemory("Abigail", "/tmp/econ-sync2");
  const ctx: ToolContext = {
    memory,
    scene: makeScene(),
    inventory: [{ name: "Amethyst", quantity: 3 }],
    givenToPlayer: [],
    log: [],
  };
  const info = buildStardewTools(ctx).find((t) => t.name === "get_info")!;

  const result = await info.execute({ query: "inventory" });

  expect(String(result.content)).toContain("Amethyst x3");
});
