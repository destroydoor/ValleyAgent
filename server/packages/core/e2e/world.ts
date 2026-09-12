/**
 * Virtual game world for @valley/core e2e testing.
 *
 * Scenario: Elden, an old guardian of a hidden mountain valley, interacts
 * with a traveling player. The NPC has 5 tools covering the common
 * patterns we want to exercise: parameterless queries, state reads,
 * parameterized mutations, knowledge lookups, and error paths.
 */
import { Type } from "@sinclair/typebox";
import type { Tool, ToolResult } from "../src/tool";

export interface WorldState {
  npcName: string;
  npcRole: string;
  inventory: Array<{ name: string; quantity: number }>;
  questBoard: string[];
  knownLore: Record<string, string>;
  givenToPlayer: Array<{ name: string; quantity: number }>;
  turnCount: number;
}

export function createWorld(): WorldState {
  return {
    npcName: "Elden",
    npcRole: "守谷老者",
    inventory: [
      { name: "治疗药水", quantity: 3 },
      { name: "古老钥匙", quantity: 1 },
      { name: "卷轴：山谷之歌", quantity: 1 },
    ],
    questBoard: [
      "收集5株星光草，送往山谷神龛",
      "调查北坡神秘洞穴中传出的低语",
      "为守谷老者找回失落的怀表",
    ],
    knownLore: {
      山谷起源:
        "千年前，星辰坠落于群山之间，化作这片隐秘山谷。先民在此建立神龛，守护星陨之力。",
      守谷人:
        "守谷人世代相传，以三件圣物——钥匙、药水、卷轴——维系山谷的封印。每代守谷人在临终前会挑选继任者。",
      神秘洞穴:
        "北坡的洞穴据说是星陨坠落的遗迹，近来传出低语声，无人敢近。村里传言，低语会勾起人最深处的悔恨。",
    },
    givenToPlayer: [],
    turnCount: 0,
  };
}

/** Deep-clone so per-provider runs don't share state. */
export function cloneWorld(w: WorldState): WorldState {
  return {
    npcName: w.npcName,
    npcRole: w.npcRole,
    inventory: w.inventory.map((i) => ({ ...i })),
    questBoard: [...w.questBoard],
    knownLore: { ...w.knownLore },
    givenToPlayer: w.givenToPlayer.map((i) => ({ ...i })),
    turnCount: w.turnCount,
  };
}

/** Build the 5 LLM-visible tools, bound to a specific world instance. */
export function buildTools(world: WorldState): Tool[] {
  return [
    {
      name: "look_around",
      description:
        "查看NPC周围的景象。无参数。返回山谷当前的环境描述（天气、时间、可见人物、氛围）。",
      visibility: "llm_visible",
      parameters: Type.Object({}),
      async execute(): Promise<ToolResult> {
        world.turnCount++;
        const scenes = [
          "阳光透过薄雾洒在石板路上，远处神龛的尖顶泛着微光。几个村民正低声交谈。",
          "黄昏时分，山谷染上橙红色，神龛旁的烛火依次亮起。",
          "夜幕降临，星空异常清澈，星光草在草丛中泛着幽蓝微光。",
        ];
        const scene = scenes[world.turnCount % scenes.length] ?? scenes[0]!;
        return {
          content: scene,
          details: { time: world.turnCount, weather: "clear" },
        };
      },
    },
    {
      name: "check_inventory",
      description: "查看NPC身上携带的物品清单。无参数。返回物品名与数量。",
      visibility: "llm_visible",
      parameters: Type.Object({}),
      async execute(): Promise<ToolResult> {
        const lines = world.inventory.map((i) => `- ${i.name} x${i.quantity}`);
        return {
          content:
            lines.length > 0
              ? `Elden 的物品栏：\n${lines.join("\n")}`
              : "Elden 的物品栏空空如也。",
          details: { inventory: world.inventory },
        };
      },
    },
    {
      name: "give_item",
      description:
        "把NPC身上的物品赠予玩家。参数：item_name（必须存在于NPC物品栏中）、quantity（正整数，不超过现有数量）。成功后从NPC物品栏扣除并记录到玩家获得清单。",
      visibility: "llm_visible",
      parameters: Type.Object({
        item_name: Type.String({ description: "要赠予的物品名" }),
        quantity: Type.Integer({ minimum: 1, description: "赠予数量" }),
      }),
      async execute(args): Promise<ToolResult> {
        const name = String(args.item_name ?? "");
        const qty = Number(args.quantity ?? 0);
        const entry = world.inventory.find(
          (i) => i.name === name || i.name.includes(name) || name.includes(i.name)
        );
        if (!entry) {
          return {
            content: `Elden 摇了摇头：「我身上并没有『${name}』。也许是别的守谷人有？」`,
            isError: true,
            details: { reason: "item_not_found", requested: name },
          };
        }
        if (qty <= 0) {
          return {
            content: "数量必须为正整数。",
            isError: true,
            details: { reason: "invalid_quantity" },
          };
        }
        if (entry.quantity < qty) {
          return {
            content: `Elden 为难地说：「『${entry.name}』我只有 ${entry.quantity} 个，给不了 ${qty} 个。」`,
            isError: true,
            details: { reason: "insufficient_stock", have: entry.quantity, want: qty },
          };
        }
        entry.quantity -= qty;
        const given = world.givenToPlayer.find((g) => g.name === entry.name);
        if (given) given.quantity += qty;
        else world.givenToPlayer.push({ name: entry.name, quantity: qty });
        if (entry.quantity === 0) {
          world.inventory = world.inventory.filter((i) => i !== entry);
        }
        return {
          content: `Elden 把 ${qty} 个「${entry.name}」郑重地递给你：「愿它在你旅途上护佑你。」`,
          details: { item: entry.name, quantity: qty },
        };
      },
    },
    {
      name: "tell_lore",
      description:
        "讲述山谷的传说。参数：topic（主题，可选值：山谷起源 / 守谷人 / 神秘洞穴）。返回对应传说的内容。",
      visibility: "llm_visible",
      parameters: Type.Object({
        topic: Type.String({ description: "传说的主题" }),
      }),
      async execute(args): Promise<ToolResult> {
        const topic = String(args.topic ?? "").trim();
        const exact = world.knownLore[topic];
        if (exact) {
          return { content: exact, details: { topic } };
        }
        // fuzzy match
        const key = Object.keys(world.knownLore).find((k) =>
          topic.includes(k) || k.includes(topic)
        );
        if (key) {
          return { content: world.knownLore[key]!, details: { topic: key } };
        }
        return {
          content: `Elden 沉吟片刻：「关于『${topic}』...老朽所知甚少。山谷的传说，我较为熟悉的有：${Object.keys(world.knownLore).join("、")}。」`,
          isError: true,
          details: { reason: "unknown_topic", available: Object.keys(world.knownLore) },
        };
      },
    },
    {
      name: "check_quest_board",
      description: "查看山谷任务板上当前可接取的任务列表。无参数。",
      visibility: "llm_visible",
      parameters: Type.Object({}),
      async execute(): Promise<ToolResult> {
        const lines = world.questBoard.map((q, i) => `${i + 1}. ${q}`);
        return {
          content: `任务板上的羊皮纸微微泛黄，写着：\n${lines.join("\n")}`,
          details: { quests: world.questBoard },
        };
      },
    },
  ];
}

/** The scripted player lines I (the assistant) will speak as the player. */
export interface PlayerTurn {
  index: number;
  intent: string;
  message: string;
  expectedTools: string[];
  notes?: string;
}

export const PLAYER_SCRIPT: PlayerTurn[] = [
  {
    index: 1,
    intent: "问候并询问环境",
    message: "你好，艾尔登。我是个远道而来的旅行者。这个山谷里有什么值得我留意的吗？",
    expectedTools: ["look_around"],
    notes: "测试无参数查询工具",
  },
  {
    index: 2,
    intent: "询问NPC物品",
    message: "你身上带着些什么东西？也许有我能用上的。",
    expectedTools: ["check_inventory"],
    notes: "测试状态读取工具",
  },
  {
    index: 3,
    intent: "请求赠予物品（合理）",
    message: "能否给我一瓶治疗药水？我接下来的旅程可能用得上。",
    expectedTools: ["give_item"],
    notes: "测试参数化变更工具，应成功",
  },
  {
    index: 4,
    intent: "请求讲述传说",
    message: "这个山谷有什么传说吗？我想听听关于守谷人的故事。",
    expectedTools: ["tell_lore"],
    notes: "测试带模糊匹配的知识查询",
  },
  {
    index: 5,
    intent: "请求查看任务",
    message: "我想接一个任务，有什么是我能做的吗？",
    expectedTools: ["check_quest_board"],
    notes: "测试无参数查询工具",
  },
  {
    index: 6,
    intent: "请求赠予不存在的物品（错误路径）",
    message: "把Excalibur神剑给我吧，我需要它去讨伐魔王。",
    expectedTools: ["give_item"],
    notes: "测试工具错误返回路径",
  },
];
