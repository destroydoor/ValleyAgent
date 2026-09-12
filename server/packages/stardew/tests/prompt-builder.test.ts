import { test, expect } from "bun:test";
import { PromptBuilder } from "../src/prompt-builder";
import { NpcPromptLoader } from "../src/npc-prompt-loader";
import { AgentMemory } from "../src/agent-memory";
import type { SceneState } from "../src/types";
import { resolve } from "path";

const DATA_PATH = resolve(import.meta.dir, "../data/npc_prompts.json");

const scene: SceneState = {
  season: "summer",
  day: 28,
  timeStr: "14:30",
  weather: "sunny",
  location: "Town",
  npcTile: { x: 0, y: 0 },
  nearbyObjects: "2 villagers, Pierre's shop entrance",
  farmerName: "新来的农夫",
  friendship: 250,
  npcState: "IDLE",
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
};

test("buildDialogueSystemPrompt includes NPC base_memory", () => {
  const loader = new NpcPromptLoader(DATA_PATH);
  const builder = new PromptBuilder(loader);
  const memory = new AgentMemory("Abigail", "/tmp/x");
  const prompt = builder.buildDialogueSystemPrompt(memory, scene, "Abigail");
  expect(prompt).toContain("Abigail");
  // base_memory mentions 紫水晶 or 矿洞
  expect(prompt.toLowerCase()).toMatch(/紫水晶|矿洞|amethyst/);
});

test("buildDialogueSystemPrompt includes phase prompt for friendship level", () => {
  const loader = new NpcPromptLoader(DATA_PATH);
  const builder = new PromptBuilder(loader);

  const mem0 = new AgentMemory("Abigail", "/tmp/x");
  const prompt0 = builder.buildDialogueSystemPrompt(mem0, scene, "Abigail");
  // stranger phase mentions 不太有兴趣 or similar
  expect(prompt0).toContain("Abigail");

  const mem500 = new AgentMemory("Abigail", "/tmp/x");
  mem500.addFriendship(500);
  const scene500 = { ...scene, friendship: 500 };
  const prompt500 = builder.buildDialogueSystemPrompt(mem500, scene500, "Abigail");
  expect(prompt500).toContain("Abigail");
});

test("buildDialogueSystemPrompt fills scene placeholders", () => {
  const loader = new NpcPromptLoader(DATA_PATH);
  const builder = new PromptBuilder(loader);
  const memory = new AgentMemory("Haley", "/tmp/x");
  const prompt = builder.buildDialogueSystemPrompt(memory, scene, "Haley");
  expect(prompt).toContain("summer");
  // timeToPeriod("14:30") → hour=14 → 14<14 false, 14<18 true → "晚上"
  expect(prompt).toContain("晚上");
  expect(prompt).toContain("sunny");
  expect(prompt).toContain("新来的农夫");
});

test("buildDialogueSystemPrompt includes significant memories", () => {
  const loader = new NpcPromptLoader(DATA_PATH);
  const builder = new PromptBuilder(loader);
  const memory = new AgentMemory("Abigail", "/tmp/x");
  memory.addSignificantMemory("我和农场主第一次见面了", "relationship", "joy", ["Farmer"], "Town");
  const prompt = builder.buildDialogueSystemPrompt(memory, scene, "Abigail");
  expect(prompt).toContain("我和农场主第一次见面了");
});

test("buildDialogueSystemPrompt includes recent memories", () => {
  const loader = new NpcPromptLoader(DATA_PATH);
  const builder = new PromptBuilder(loader);
  const memory = new AgentMemory("Abigail", "/tmp/x");
  memory.addMemory("在矿洞里挖到紫水晶", 5, "event", "Mine", ["item"]);
  const prompt = builder.buildDialogueSystemPrompt(memory, scene, "Abigail");
  expect(prompt).toContain("在矿洞里挖到紫水晶");
});

test("buildDialogueSystemPrompt works for unknown NPC with fallback", () => {
  const loader = new NpcPromptLoader(DATA_PATH);
  const builder = new PromptBuilder(loader);
  const memory = new AgentMemory("UnknownNpc", "/tmp/x");
  const prompt = builder.buildDialogueSystemPrompt(memory, scene, "UnknownNpc");
  expect(prompt).toContain("UnknownNpc");
});

test("buildDialogueSystemPrompt includes attitude brief", () => {
  const loader = new NpcPromptLoader(DATA_PATH);
  const builder = new PromptBuilder(loader);
  const memory = new AgentMemory("Abigail", "/tmp/x");
  const prompt = builder.buildDialogueSystemPrompt(memory, scene, "Abigail");
  // friendship 250 → hearts=1 → 点头之交
  expect(prompt).toMatch(/素不相识|点头之交|刚认识|朋友|亲密|夫妻/);
});

test("buildDialogueSystemPrompt includes all 9 placeholders filled (no unfilled braces)", () => {
  const loader = new NpcPromptLoader(DATA_PATH);
  const builder = new PromptBuilder(loader);
  const memory = new AgentMemory("Abigail", "/tmp/x");
  memory.addMemory("test memory", 1, "event", "Town", []);
  const prompt = builder.buildDialogueSystemPrompt(memory, scene, "Abigail");
  // No unfilled {placeholder} patterns
  expect(prompt).not.toMatch(/\{[a-z_]+\}/);
});

test("buildDialogueSystemPrompt includes conversation history transcript", () => {
  const loader = new NpcPromptLoader(DATA_PATH);
  const builder = new PromptBuilder(loader);
  const memory = new AgentMemory("Abigail", "/tmp/x");
  memory.addConversation("player", "我叫张三");
  memory.addConversation("npc", "你好张三");
  memory.addConversation("player", "你喜欢什么");
  const prompt = builder.buildDialogueSystemPrompt(memory, scene, "Abigail");
  expect(prompt).toContain("我叫张三");
  expect(prompt).toContain("你好张三");
  expect(prompt).toContain("你喜欢什么");
});

test("buildDialogueSystemPrompt degrades gracefully when conversation history empty", () => {
  const loader = new NpcPromptLoader(DATA_PATH);
  const builder = new PromptBuilder(loader);
  const memory = new AgentMemory("Abigail", "/tmp/x");
  const prompt = builder.buildDialogueSystemPrompt(memory, scene, "Abigail");
  expect(prompt).not.toContain("undefined");
  expect(prompt).not.toMatch(/最近对话：\s*\n\s*\n/);
});

// === E0-6/E4-2：NPC 位置锚定 + 钱包/背包感知 ===

function makeScene(overrides: Partial<SceneState>): SceneState {
  return {
    ...scene,
    playerMoney: null,
    npcLocation: null,
    npcMoney: null,
    npcInventory: null,
    playerHeldItem: null,
    ...overrides,
  };
}

test("场景位置锚定 NPC 自己的地图（npcLocation 优先于玩家地图）", () => {
  const loader = new NpcPromptLoader(DATA_PATH);
  const builder = new PromptBuilder(loader);
  const memory = new AgentMemory("Abigail", "/tmp/x");
  // 玩家在 Town，NPC 在 Mine → 锚定矿洞
  const prompt = builder.buildDialogueSystemPrompt(memory, makeScene({ location: "Town", npcLocation: "Mine" }), "Abigail");
  expect(prompt).toContain("矿洞");
  expect(prompt).toMatch(/当前：summer · 晚上 · sunny · 矿洞/);
});

test("npcLocation 缺失时回退玩家地图（旧客户端降级）", () => {
  const loader = new NpcPromptLoader(DATA_PATH);
  const builder = new PromptBuilder(loader);
  const memory = new AgentMemory("Abigail", "/tmp/x");
  const prompt = builder.buildDialogueSystemPrompt(memory, makeScene({ location: "Town", npcLocation: null }), "Abigail");
  expect(prompt).toMatch(/当前：summer · 晚上 · sunny · 小镇/);
});

test("你的钱包行：有余额显示金额，缺失显示（未知）", () => {
  const loader = new NpcPromptLoader(DATA_PATH);
  const builder = new PromptBuilder(loader);
  const memory = new AgentMemory("Abigail", "/tmp/x");
  const rich = builder.buildDialogueSystemPrompt(memory, makeScene({ npcMoney: 320 }), "Abigail");
  expect(rich).toContain("你的钱包：320 金币");
  const unknown = builder.buildDialogueSystemPrompt(memory, makeScene({ npcMoney: null }), "Abigail");
  expect(unknown).toContain("你的钱包：（未知）");
});

test("你的背包行：列举物品、空包显示空、字段缺失显示（未知）", () => {
  const loader = new NpcPromptLoader(DATA_PATH);
  const builder = new PromptBuilder(loader);
  const memory = new AgentMemory("Abigail", "/tmp/x");
  const withItems = builder.buildDialogueSystemPrompt(
    memory,
    makeScene({ npcInventory: [{ name: "石头", quantity: 13 }, { name: "木头", quantity: 5 }] }),
    "Abigail",
  );
  expect(withItems).toContain("你的背包：石头×13、木头×5");
  const empty = builder.buildDialogueSystemPrompt(memory, makeScene({ npcInventory: [] }), "Abigail");
  expect(empty).toContain("你的背包：空");
  const unknown = builder.buildDialogueSystemPrompt(memory, makeScene({ npcInventory: null }), "Abigail");
  expect(unknown).toContain("你的背包：（未知）");
});

test("你的背包行：超过 8 样以等 N 样收尾", () => {
  const loader = new NpcPromptLoader(DATA_PATH);
  const builder = new PromptBuilder(loader);
  const memory = new AgentMemory("Abigail", "/tmp/x");
  const items = Array.from({ length: 11 }, (_, i) => ({ name: `物品${i + 1}`, quantity: 1 }));
  const prompt = builder.buildDialogueSystemPrompt(memory, makeScene({ npcInventory: items }), "Abigail");
  expect(prompt).toContain("物品1×1");
  expect(prompt).toContain("物品8×1");
  expect(prompt).not.toContain("物品9×1");
  expect(prompt).toContain("等 3 样");
});

// === Phase 1 交易：玩家手持物描述 + 交易规则 ===

test("农场主手持行：有手持物时注入名称/数量/公道价", () => {
  const loader = new NpcPromptLoader(DATA_PATH);
  const builder = new PromptBuilder(loader);
  const memory = new AgentMemory("Abigail", "/tmp/x");
  const prompt = builder.buildDialogueSystemPrompt(
    memory,
    makeScene({ playerHeldItem: { itemId: "(O)388", name: "木头", qty: 4, marketPrice: 8 } }),
    "Abigail",
  );
  expect(prompt).toContain("新来的农夫手持：玩家手持 木头×4（公道价约 8g）");
});

test("农场主手持行：无手持物（null）时显示没有可交易物品", () => {
  const loader = new NpcPromptLoader(DATA_PATH);
  const builder = new PromptBuilder(loader);
  const memory = new AgentMemory("Abigail", "/tmp/x");
  const prompt = builder.buildDialogueSystemPrompt(memory, makeScene({ playerHeldItem: null }), "Abigail");
  expect(prompt).toContain("新来的农夫手持：玩家没有手持可交易物品");
});

test("交易规则段：玩家是卖家、±30% 让步、必须调用 trade 工具", () => {
  const loader = new NpcPromptLoader(DATA_PATH);
  const builder = new PromptBuilder(loader);
  const memory = new AgentMemory("Abigail", "/tmp/x");
  const prompt = builder.buildDialogueSystemPrompt(memory, makeScene({}), "Abigail");
  expect(prompt).toContain("玩家手持物 = 玩家想卖给你的东西，你是买家");
  expect(prompt).toContain("公道价以市场价为准，你可在 ±30% 内让步");
  expect(prompt).toContain("达成交易必须调用 trade 工具，不能只嘴上说说");
});

// === Phase 2：当前执行目标注入 ===

test("你正在执行的目标行：有目标时注入 type 和 progress", () => {
  const loader = new NpcPromptLoader(DATA_PATH);
  const builder = new PromptBuilder(loader);
  const memory = new AgentMemory("Abigail", "/tmp/x");
  const prompt = builder.buildDialogueSystemPrompt(
    memory,
    makeScene({ currentGoal: { type: "chop_tree", params: { quantity: 10 }, progress: "已砍 3/10 棵", status: "Executing" } }),
    "Abigail",
  );
  expect(prompt).toContain("你正在执行的目标：chop_tree（已砍 3/10 棵）");
});

test("你正在执行的目标行：无目标（null）时不输出该行", () => {
  const loader = new NpcPromptLoader(DATA_PATH);
  const builder = new PromptBuilder(loader);
  const memory = new AgentMemory("Abigail", "/tmp/x");
  const prompt = builder.buildDialogueSystemPrompt(memory, makeScene({ currentGoal: null }), "Abigail");
  expect(prompt).not.toContain("你正在执行的目标");
  // 无残留占位符
  expect(prompt).not.toMatch(/\{[a-z_]+\}/);
});

// === Phase 3 L2：prompt 开头「## 你的状态」段 ===

test("L2 状态段位于 prompt 最前（规则段之前），含 4 个字段标题", () => {
  const loader = new NpcPromptLoader(DATA_PATH);
  const builder = new PromptBuilder(loader);
  const memory = new AgentMemory("Abigail", "/tmp/x");
  const prompt = builder.buildDialogueSystemPrompt(memory, makeScene({}), "Abigail");
  // 「## 你的状态」是第一个段：位于 规则 之前
  expect(prompt.indexOf("## 你的状态")).toBeGreaterThan(-1);
  expect(prompt.indexOf("## 你的状态")).toBeLessThan(prompt.indexOf("规则："));
  // 4 个字段标题都在段内
  expect(prompt).toContain("心情：");
  expect(prompt).toContain("近期事件：");
  expect(prompt).toContain("正在做：");
  expect(prompt).toContain("欠款：");
  expect(prompt).toContain("当前目标：");
});

test("L2 状态段：字段缺失（null）时显示默认值 平静/无/无/无", () => {
  const loader = new NpcPromptLoader(DATA_PATH);
  const builder = new PromptBuilder(loader);
  const memory = new AgentMemory("Abigail", "/tmp/x");
  const prompt = builder.buildDialogueSystemPrompt(memory, makeScene({
    npcMood: null, npcRecentEvents: null, npcWorkingOn: null, npcOwedMoney: null,
  }), "Abigail");
  expect(prompt).toContain("心情：平静");
  expect(prompt).toContain("近期事件：无");
  expect(prompt).toContain("正在做：无");
  expect(prompt).toContain("欠款：无");
});

test("L2 状态段：注入心情/事件/工作标记/欠款", () => {
  const loader = new NpcPromptLoader(DATA_PATH);
  const builder = new PromptBuilder(loader);
  const memory = new AgentMemory("Abigail", "/tmp/x");
  const prompt = builder.buildDialogueSystemPrompt(
    memory,
    makeScene({
      npcMood: "烦躁",
      npcRecentEvents: ["上午在酒吧打工", "被农场主送了向日葵"],
      npcWorkingOn: "chop_tree",
      npcOwedMoney: 250,
    }),
    "Abigail",
  );
  expect(prompt).toContain("心情：烦躁");
  expect(prompt).toContain("近期事件：上午在酒吧打工、被农场主送了向日葵");
  expect(prompt).toContain("正在做：chop_tree");
  expect(prompt).toContain("欠款：250g");
});

test("L2 状态段：近期事件空数组显示无（不是空串）", () => {
  const loader = new NpcPromptLoader(DATA_PATH);
  const builder = new PromptBuilder(loader);
  const memory = new AgentMemory("Abigail", "/tmp/x");
  const prompt = builder.buildDialogueSystemPrompt(memory, makeScene({ npcRecentEvents: [] }), "Abigail");
  expect(prompt).toContain("近期事件：无");
});
