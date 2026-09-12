import { test, expect } from "bun:test";
import { buildStardewTools } from "../src/stardew-tools";
import type { ToolContext } from "../src/stardew-tools";
import { AgentMemory } from "../src/agent-memory";
import type { SceneState } from "../src/types";
import { ToolRegistry } from "@valley/core";

const scene: SceneState = {
  season: "summer",
  day: 28,
  timeStr: "14:30",
  weather: "sunny",
  location: "Town",
  npcTile: { x: 0, y: 0 },
  nearbyObjects: "2 villagers",
  farmerName: "新来的农夫",
  friendship: 250,
  npcState: "IDLE",
  inventory: [{ name: "Amethyst", quantity: 2 }],
  playerMoney: null,
  npcLocation: "Town",
  npcMoney: 120,
  npcInventory: [{ name: "Amethyst", quantity: 2 }],
  playerHeldItem: null,
  currentGoal: null,
  npcMood: null,
  npcRecentEvents: null,
  npcWorkingOn: null,
  npcOwedMoney: null,
};

function makeCtx(): ToolContext {
  const memory = new AgentMemory("Abigail", "/tmp/x");
  return {
    memory,
    scene,
    inventory: [{ name: "Amethyst", quantity: 2 }],
    givenToPlayer: [],
    log: [],
  };
}

test("buildStardewTools returns exactly 15 tools", () => {
  const tools = buildStardewTools(makeCtx());
  expect(tools).toHaveLength(15);
  const names = tools.map((t) => t.name).sort();
  expect(names).toEqual([
    "accept_job", "chop_tree", "emote", "evaluate_friendship", "forget", "get_info", "give_gift", "give_item",
    "receive_payment", "remember", "set_goal", "set_state", "show_dialogue", "speak", "trade",
  ]);
});

test("all tools are llm_visible", () => {
  const tools = buildStardewTools(makeCtx());
  for (const t of tools) {
    expect(t.visibility).toBe("llm_visible");
  }
});

test("speak tool records conversation in memory", async () => {
  const ctx = makeCtx();
  const tools = buildStardewTools(ctx);
  const speak = tools.find((t) => t.name === "speak")!;
  const result = await speak.execute({ text: "你好！" });
  expect(result.isError).toBeFalsy();
  expect(ctx.memory.conversationHistory).toHaveLength(1);
  expect(ctx.memory.conversationHistory[0]!.text).toBe("你好！");
  expect(ctx.log.some((l) => l.includes("[speak]"))).toBe(true);
});

test("emote tool logs the emote_id", async () => {
  const ctx = makeCtx();
  const tools = buildStardewTools(ctx);
  const emote = tools.find((t) => t.name === "emote")!;
  await emote.execute({ emote_id: "heart" });
  expect(ctx.log.some((l) => l.includes("heart"))).toBe(true);
});

test("give_item decrements inventory and tracks givenToPlayer", async () => {
  const ctx = makeCtx();
  const tools = buildStardewTools(ctx);
  const give = tools.find((t) => t.name === "give_item")!;
  const result = await give.execute({ item_id: "Amethyst", quantity: 1 });
  expect(result.isError).toBeFalsy();
  expect(ctx.inventory[0]!.quantity).toBe(1);
  expect(ctx.givenToPlayer).toEqual([{ name: "Amethyst", quantity: 1 }]);
});

test("give_item errors when item not in inventory", async () => {
  const ctx = makeCtx();
  const tools = buildStardewTools(ctx);
  const give = tools.find((t) => t.name === "give_item")!;
  const result = await give.execute({ item_id: "Nonexistent", quantity: 1 });
  expect(result.isError).toBe(true);
});

test("give_gift logs intent without writing memory (C1 two-stage)", async () => {
  const ctx = makeCtx();
  const tools = buildStardewTools(ctx);
  const gift = tools.find((t) => t.name === "give_gift")!;
  const result = await gift.execute({ item_id: "Amethyst", quantity: 1 });
  expect(result.isError).toBeFalsy();
  // C1: execute only logs intent, does not write ctx.memory
  expect(ctx.memory.shortTermMemories.some((m) => m.entryType === "gift")).toBe(false);
  expect(ctx.log.some((l) => l.includes("[give_gift]"))).toBe(true);
});

test("set_state records decision in memory", async () => {
  const ctx = makeCtx();
  const tools = buildStardewTools(ctx);
  const setState = tools.find((t) => t.name === "set_state")!;
  await setState.execute({ state: "TALK" });
  expect(ctx.memory.shortTermMemories.some((m) => m.entryType === "decision")).toBe(true);
});

test("set_state 记忆文案是意图化措辞（E0-6）", async () => {
  const ctx = makeCtx();
  const tools = buildStardewTools(ctx);
  const setState = tools.find((t) => t.name === "set_state")!;
  await setState.execute({ state: "MINE" });
  const mem = ctx.memory.shortTermMemories.find((m) => m.entryType === "decision");
  expect(mem).toBeDefined();
  expect(mem!.text).toContain("（意图）我打算进入 MINE 状态");
  // 不再使用"决定切换到"的事实化措辞
  expect(mem!.text).not.toContain("决定切换到");
});

test("receive_payment execute 只写意图日志，不写记忆，details 正确（E4-2 两阶段）", async () => {
  const ctx = makeCtx();
  const tools = buildStardewTools(ctx);
  const pay = tools.find((t) => t.name === "receive_payment")!;
  const result = await pay.execute({ amount: 500, reason: "挖矿工钱" });
  expect(result.isError).toBeFalsy();
  expect(result.details).toEqual({ action: "receive_payment", amount: 500, reason: "挖矿工钱" });
  // 两阶段：execute 不碰 ctx.memory，收款/失败记忆由 routeToolResult 落地
  expect(ctx.memory.shortTermMemories).toHaveLength(0);
  expect(ctx.log.some((l) => l.includes("[receive_payment]") && l.includes("500"))).toBe(true);
});

test("trade tool exists with NPC-as-buyer schema（Phase 1）", () => {
  const tools = buildStardewTools(makeCtx());
  const trade = tools.find((t) => t.name === "trade");
  expect(trade).toBeDefined();
  // 描述明确 NPC 是买家，且必须调用工具才算成交
  expect(trade!.description).toContain("买家");
  expect(trade!.description).toContain("必须调用本工具");
  const props = (trade!.parameters as unknown as { properties: Record<string, { const?: string; anyOf?: Array<{ const?: string }> }> }).properties;
  expect(props.item_id).toBeDefined();
  expect(props.quantity).toBeDefined();
  expect(props.price).toBeDefined();
  // direction 固定 npc_buys（单元素 Union 折叠为 const；多元素时为 anyOf）
  const dirSchema = props.direction!;
  const allowed = dirSchema.const !== undefined ? [dirSchema.const] : dirSchema.anyOf?.map((a) => a.const);
  expect(allowed).toEqual(["npc_buys"]);
  const required = (trade!.parameters as { required?: string[] }).required;
  expect(required).toEqual(["item_id", "quantity", "price"]);
});

test("trade execute 返回意图 details，不写记忆（Phase 1 两阶段）", async () => {
  const ctx = makeCtx();
  const tools = buildStardewTools(ctx);
  const trade = tools.find((t) => t.name === "trade")!;
  const result = await trade.execute({ item_id: "(O)388", quantity: 4, price: 8 });
  expect(result.isError).toBeFalsy();
  // 意图式：details 即 {action:"trade", itemId, quantity, price, direction:"npc_buys"}
  expect(result.details).toEqual({ action: "trade", itemId: "(O)388", quantity: 4, price: 8, direction: "npc_buys" });
  // 两阶段：execute 不碰 ctx.memory，结算/失败记忆由 routeToolResult 落地
  expect(ctx.memory.shortTermMemories).toHaveLength(0);
  expect(ctx.log.some((l) => l.includes("[trade]") && l.includes("4") && l.includes("(O)388"))).toBe(true);
});

test("give_item 扣的是 NPC 自己的包，不是玩家背包（E4-2）", async () => {
  const ctx = makeCtx();
  // 玩家背包有物品，但 NPC 自己没东西 → 不能给
  ctx.scene = { ...ctx.scene, inventory: [{ name: "Stone", quantity: 99 }], npcInventory: [] };
  ctx.inventory = [];
  const tools = buildStardewTools(ctx);
  const give = tools.find((t) => t.name === "give_item")!;
  const result = await give.execute({ item_id: "Stone", quantity: 1 });
  expect(result.isError).toBe(true);
});

test("get_info inventory 字段缺失时报未知而非空包（E4-2）", async () => {
  const ctx = makeCtx();
  ctx.scene = { ...ctx.scene, npcInventory: null };
  const tools = buildStardewTools(ctx);
  const getInfo = tools.find((t) => t.name === "get_info")!;
  const result = await getInfo.execute({ query: "inventory" });
  expect(result.content).toContain("未知");
});

test("get_info location 答 NPC 位置；npcLocation 缺失时回退并标注农场主所在处（E0-6）", async () => {
  const ctx = makeCtx();
  const tools = buildStardewTools(ctx);
  const getInfo = tools.find((t) => t.name === "get_info")!;
  // 有 npcLocation → 直接答 NPC 位置
  const own = await getInfo.execute({ query: "location" });
  expect(own.content).toContain("你的位置：Town");
  // 无 npcLocation → 回退玩家地图并标注
  ctx.scene = { ...ctx.scene, npcLocation: null, location: "Farm" };
  const fallback = await getInfo.execute({ query: "location" });
  expect(fallback.content).toContain("未知");
  expect(fallback.content).toContain("农场主");
  expect(fallback.content).toContain("Farm");
});

test("remember tool calls addSignificantMemory", async () => {
  const ctx = makeCtx();
  const tools = buildStardewTools(ctx);
  const remember = tools.find((t) => t.name === "remember")!;
  const result = await remember.execute({
    text: "我和农场主第一次见面了",
    category: "relationship",
    emotional_weight: "joy",
  });
  expect(result.isError).toBeFalsy();
  expect(ctx.memory.significantMemories).toHaveLength(1);
});

test("get_info returns date/nearby/inventory/player/location data", async () => {
  const ctx = makeCtx();
  const tools = buildStardewTools(ctx);
  const getInfo = tools.find((t) => t.name === "get_info")!;

  const dateResult = await getInfo.execute({ query: "date" });
  expect(dateResult.content).toContain("summer");

  const nearbyResult = await getInfo.execute({ query: "nearby" });
  expect(nearbyResult.content).toContain("2 villagers");

  const invResult = await getInfo.execute({ query: "inventory" });
  expect(invResult.content).toContain("Amethyst");

  const playerResult = await getInfo.execute({ query: "player" });
  expect(playerResult.content).toContain("新来的农夫");

  const locResult = await getInfo.execute({ query: "location" });
  expect(locResult.content).toContain("Town");
});

test("tools register cleanly into ToolRegistry", () => {
  const ctx = makeCtx();
  const tools = buildStardewTools(ctx);
  const registry = new ToolRegistry();
  for (const t of tools) registry.register(t);
  expect(registry.getAll()).toHaveLength(15);
  expect(registry.getByName("speak")).toBeDefined();
  expect(registry.getByName("emote")).toBeDefined();
  expect(registry.getByName("forget")).toBeDefined();
  expect(registry.getByName("trade")).toBeDefined();
  expect(registry.getByName("set_goal")).toBeDefined();
});

test("forget tool removes matching short-term memory and reports count", async () => {
  const ctx = makeCtx();
  const tools = buildStardewTools(ctx);
  ctx.memory.addMemory("捡了一块没用的石头", 2, "event", "Mine", []);
  const forget = tools.find((t) => t.name === "forget")!;
  const result = await forget.execute({ memory_text: "石头", reason: "不重要的小事" });
  expect(result.isError).toBeFalsy();
  expect(ctx.memory.shortTermMemories.length).toBe(0);
  expect(result.content).toContain("1");
  expect(ctx.log.some((l) => l.includes("[forget]"))).toBe(true);
});

test("forget tool refuses to delete significant memories", async () => {
  const ctx = makeCtx();
  const tools = buildStardewTools(ctx);
  ctx.memory.addSignificantMemory("农场主送了我最爱的紫水晶", "relationship", "joy", ["Farmer"], "Town");
  const forget = tools.find((t) => t.name === "forget")!;
  const result = await forget.execute({ memory_text: "紫水晶", reason: "测试" });
  expect(ctx.memory.significantMemories.length).toBe(1);
  expect(result.content).toContain("没有找到");
});

test("forget tool reports when nothing matches", async () => {
  const ctx = makeCtx();
  const tools = buildStardewTools(ctx);
  ctx.memory.addMemory("捡了石头", 2, "event", "Mine", []);
  const forget = tools.find((t) => t.name === "forget")!;
  const result = await forget.execute({ memory_text: "不存在", reason: "测试" });
  expect(result.isError).toBeFalsy();
  expect(result.content).toContain("没有找到");
});

// ── M2 修复（2026-08-23）：玩家桶路由 / 双桶删除 / trade 整数收口 ──

/** 带 playerId（对话发起玩家）的上下文；玩家桶需预加载（protocol-adapter 同流程）。 */
async function makePlayerCtx(playerId = "player-a", playerName = "阿明"): Promise<ToolContext> {
  const memory = new AgentMemory("Abigail", "/tmp/x");
  await memory.getPlayerMemory(playerId, playerName);
  return {
    memory,
    scene,
    inventory: [{ name: "Amethyst", quantity: 2 }],
    givenToPlayer: [],
    log: [],
    playerId,
    playerName,
  };
}

test("speak/show_dialogue route NPC lines into the current player's bucket", async () => {
  const ctx = await makePlayerCtx();
  const tools = buildStardewTools(ctx);

  await tools.find((t) => t.name === "speak")!.execute({ text: "给你看我的新发型。" });
  await tools.find((t) => t.name === "show_dialogue")!.execute({ text: "今天天气真好。" });

  // 台词进玩家桶（对话历史不再缺 NPC 半边），世界桶保持干净
  const playerBucket = ctx.memory.getLoadedPlayerMemory("player-a")!;
  expect(playerBucket.conversationHistory.map((e) => e.text)).toEqual(["给你看我的新发型。", "今天天气真好。"]);
  expect(ctx.memory.conversationHistory).toHaveLength(0);
});

test("speak stays in the world bucket when ctx has no playerId (beat/旧调用)", async () => {
  const ctx = makeCtx();
  const speak = buildStardewTools(ctx).find((t) => t.name === "speak")!;
  await speak.execute({ text: "嗯。" });
  expect(ctx.memory.conversationHistory).toHaveLength(1);
});

test("forget removes from both world and player buckets", async () => {
  const ctx = await makePlayerCtx();
  const tools = buildStardewTools(ctx);
  // 世界桶：任务类；玩家桶：交易失败类（addMemory 走 AgentMemory 玩家路由）
  ctx.memory.addMemory("捡了一块石头", 2, "event", "Mine", []);
  ctx.memory.addMemory("想买农场主的木头但没买成", 3, "event", "Town", ["trade", "failure"], "player-a", "阿明");

  const forget = tools.find((t) => t.name === "forget")!;
  const result = await forget.execute({ memory_text: "买", reason: "已经买到了" });

  expect(result.isError).toBeFalsy();
  expect(String(result.content)).toContain("1"); // 只命中玩家桶那一条
  expect(ctx.memory.shortTermMemories.some((m) => m.text.includes("捡了一块石头"))).toBe(true); // 世界桶不受影响
  expect(
    ctx.memory.getLoadedPlayerMemory("player-a")!.shortTermMemories.some((m) => m.text.includes("没买成")),
  ).toBe(false); // 玩家桶条目已删除

  // 两桶各命中一条时计数合并
  ctx.memory.addMemory("关于矿洞的事A", 2, "event", "Mine", []);
  ctx.memory.addMemory("关于矿洞的事B", 2, "event", "Mine", [], "player-a", "阿明");
  const both = await forget.execute({ memory_text: "矿洞", reason: "测试" });
  expect(String(both.content)).toContain("2");
});

test("trade rejects non-integer quantity/price locally without dispatching ops", async () => {
  const captured: unknown[] = [];
  const memory = new AgentMemory("Abigail", "/tmp/x");
  const ctx: ToolContext = {
    memory,
    scene,
    inventory: [],
    givenToPlayer: [],
    log: [],
    economy: { npcName: "Abigail", adjust: async (ops) => { captured.push(ops); return { type: "adjust_result", requestId: "r", instructionId: "i", npcName: "Abigail", success: true, steps: [] }; }, getMoney: () => 100, getInventory: () => [] },
  };
  const trade = buildStardewTools(ctx).find((t) => t.name === "trade")!;

  const floatQty = await trade.execute({ item_id: "(O)388", quantity: 2.5, price: 10 });
  const floatPrice = await trade.execute({ item_id: "(O)388", quantity: 2, price: 9.9 });

  expect(floatQty.isError).toBe(true);
  expect(floatPrice.isError).toBe(true);
  expect(captured).toHaveLength(0); // 浮点不进账本
});

// === Phase 2：set_goal 目标工具 ===

test("set_goal tool exists with 5-type enum schema（Phase 2）", () => {
  const tools = buildStardewTools(makeCtx());
  const goal = tools.find((t) => t.name === "set_goal");
  expect(goal).toBeDefined();
  // 描述明确：后台执行（不消耗对话）+ 完成后寻路汇报
  expect(goal!.description).toContain("后台执行");
  const props = (goal!.parameters as unknown as { properties: Record<string, { anyOf?: Array<{ const?: string }>; type?: string }> }).properties;
  // type 是 5 个字面量的枚举（chop_tree/mine/water_crops/fight/forage）
  const typeSchema = props.type!;
  const allowed = typeSchema.anyOf?.map((a) => a.const);
  expect(allowed).toEqual(["chop_tree", "mine", "water_crops", "fight", "forage"]);
  // params 是自由对象、reportBack 是可选 boolean
  expect(props.params).toBeDefined();
  expect(props.reportBack).toBeDefined();
  // 只有 type 必填（spec §2.1 required: ["type"]）
  const required = (goal!.parameters as { required?: string[] }).required;
  expect(required).toEqual(["type"]);
});

test("set_goal execute 返回意图 details，不写记忆（Phase 2 两阶段）", async () => {
  const ctx = makeCtx();
  const tools = buildStardewTools(ctx);
  const goal = tools.find((t) => t.name === "set_goal")!;
  const result = await goal.execute({ type: "chop_tree", params: { quantity: 10 } });
  expect(result.isError).toBeFalsy();
  // 意图式：details 即 {action:"set_goal", type, params, reportBack:true}（reportBack 缺省默认 true）
  expect(result.details).toEqual({ action: "set_goal", type: "chop_tree", params: { quantity: 10 }, reportBack: true });
  // 两阶段：execute 不碰 ctx.memory，目标成立/失败记忆由 routeToolResult 落地
  expect(ctx.memory.shortTermMemories).toHaveLength(0);
  expect(ctx.log.some((l) => l.includes("[set_goal]") && l.includes("chop_tree"))).toBe(true);
});

test("set_goal execute reportBack 缺省 true，显式 false 时返回 false", async () => {
  const ctx = makeCtx();
  const tools = buildStardewTools(ctx);
  const goal = tools.find((t) => t.name === "set_goal")!;
  const noArg = await goal.execute({ type: "forage" });
  expect(noArg.details).toMatchObject({ type: "forage", reportBack: true });
  const explicit = await goal.execute({ type: "mine", params: { targetItemId: "(O)378", quantity: 5 }, reportBack: false });
  expect(explicit.details).toMatchObject({ type: "mine", reportBack: false });
});

test("set_state 支持 EXECUTING_GOAL/TRAVELING_TO_REPORT 状态（Phase 2）", async () => {
  const ctx = makeCtx();
  const tools = buildStardewTools(ctx);
  const setState = tools.find((t) => t.name === "set_state")!;
  const res = await setState.execute({ state: "EXECUTING_GOAL" });
  expect(res.isError).toBeFalsy();
  expect(res.details).toEqual({ action: "set_state", state: "EXECUTING_GOAL" });
});
