import { Type } from "@sinclair/typebox";
import type { Tool, ToolResult } from "../src/tool";
import type { AgentMemory } from "./stardew-memory";
import type { SceneState, InventoryItem } from "./stardew-data";
import { ABIGAIL_LOVED_GIFTS, ABIGAIL_LIKED_GIFTS, ABIGAIL_DISLIKED_GIFTS } from "./stardew-data";

const EMOTE_IDS = Type.Union(
  ["happy", "sad", "angry", "surprised", "heart", "question", "exclamation", "sleep", "music", "stretch", "star", "love", "note", "wave", "hooray", "confused", "thinking", "annoyed", "worried", "frustrated", "sweat", "fish", "gift", "bomb"].map((s) => Type.Literal(s))
);

const STATES = Type.Union(
  ["IDLE", "FOLLOW", "FIGHT", "FARM", "FORAGE", "MINE", "TALK"].map((s) => Type.Literal(s))
);

export interface ToolContext {
  memory: AgentMemory;
  scene: SceneState;
  inventory: InventoryItem[];
  givenToPlayer: Array<{ name: string; quantity: number }>;
  log: string[];  // 记录工具执行日志
}

export function buildStardewTools(ctx: ToolContext): Tool[] {
  return [
    {
      name: "speak",
      description: "NPC speaks a line of dialogue to the farmer. Use when you want to say something. The text will be shown in a speech bubble above your head.",
      visibility: "llm_visible",
      parameters: Type.Object({
        text: Type.String({ description: "What you want to say (in character as Abigail, 1-2 sentences, in Chinese)" }),
        duration_ms: Type.Optional(Type.Integer({ description: "Bubble display duration in ms, default 3000" })),
      }),
      async execute(args): Promise<ToolResult> {
        const text = String(args.text ?? "");
        ctx.memory.addConversation("npc", text);
        ctx.log.push(`[speak] "${text}"`);
        return { content: `You said: "${text}"`, details: { action: "speak", text } };
      },
    },
    {
      name: "emote",
      description: "Show an emotion bubble above your head. Express how you feel (happy, sad, love, wave, etc.).",
      visibility: "llm_visible",
      parameters: Type.Object({
        emote_id: EMOTE_IDS,
        duration_ms: Type.Optional(Type.Integer({ description: "Duration in ms, default 2000" })),
        bubble_text: Type.Optional(Type.String({ description: "Optional text alongside the emote" })),
      }),
      async execute(args): Promise<ToolResult> {
        const emote = String(args.emote_id ?? "happy");
        const bubbleText = args.bubble_text !== undefined ? String(args.bubble_text) : "";
        ctx.log.push(`[emote] ${emote}${bubbleText ? ` ("${bubbleText}")` : ""}`);
        return { content: `You showed ${emote} emotion`, details: { action: "emote", emote_id: emote } };
      },
    },
    {
      name: "give_item",
      description: "Give an item from your inventory to the farmer. Use for sharing items (not emotional gifts — use give_gift for that).",
      visibility: "llm_visible",
      parameters: Type.Object({
        item_id: Type.String({ description: "Name of the item to give" }),
        quantity: Type.Optional(Type.Integer({ description: "Quantity, default 1" })),
      }),
      async execute(args): Promise<ToolResult> {
        const name = String(args.item_id ?? "");
        const qty = Number(args.quantity ?? 1);
        const entry = ctx.inventory.find(i => i.name === name);
        if (!entry) {
          return { content: `You don't have "${name}" in your inventory.`, isError: true };
        }
        if (entry.quantity < qty) {
          return { content: `You only have ${entry.quantity}x ${name}, can't give ${qty}.`, isError: true };
        }
        entry.quantity -= qty;
        const given = ctx.givenToPlayer.find(g => g.name === name);
        if (given) given.quantity += qty;
        else ctx.givenToPlayer.push({ name, quantity: qty });
        if (entry.quantity === 0) {
          ctx.inventory = ctx.inventory.filter(i => i !== entry);
        }
        ctx.log.push(`[give_item] ${qty}x ${name} → farmer`);
        return { content: `You gave ${qty}x ${name} to the farmer.`, details: { item: name, quantity: qty } };
      },
    },
    {
      name: "give_gift",
      description: "Give a gift to the farmer as a gesture of friendship. This carries emotional weight and will be remembered. Different from give_item.",
      visibility: "llm_visible",
      parameters: Type.Object({
        item_id: Type.String({ description: "Name of the gift item" }),
        quantity: Type.Optional(Type.Integer({ description: "Quantity, default 1" })),
      }),
      async execute(args): Promise<ToolResult> {
        const name = String(args.item_id ?? "");
        const qty = Number(args.quantity ?? 1);
        const entry = ctx.inventory.find(i => i.name === name);
        if (!entry) {
          return { content: `You don't have "${name}" to gift.`, isError: true };
        }
        entry.quantity -= qty;
        if (entry.quantity <= 0) {
          ctx.inventory = ctx.inventory.filter(i => i !== entry);
        }
        ctx.memory.addMemory(
          `送给农场主 ${qty}x ${name} 作为礼物`,
          6.0,
          "gift",
          ctx.scene.location,
          ["gift", "friendship"]
        );
        ctx.log.push(`[give_gift] ${qty}x ${name} → farmer (emotional)`);
        return { content: `You gave ${qty}x ${name} as a gift to the farmer. They seem touched.`, details: { item: name, quantity: qty } };
      },
    },
    {
      name: "set_state",
      description: "Switch your behavior state. States: IDLE(rest), FOLLOW(accompany farmer), FIGHT(combat), FARM(farming), FORAGE(collecting), MINE(mining), TALK(conversing).",
      visibility: "llm_visible",
      parameters: Type.Object({
        state: STATES,
      }),
      async execute(args): Promise<ToolResult> {
        const state = String(args.state ?? "IDLE");
        ctx.memory.addMemory(`决定切换到 ${state} 状态`, 3.0, "decision", ctx.scene.location, ["state"]);
        ctx.log.push(`[set_state] → ${state}`);
        return { content: `You switched to ${state} state.`, details: { action: "set_state", state } };
      },
    },
    {
      name: "show_dialogue",
      description: "Proactively initiate a dialogue with the farmer. Share news or express feelings. Use when you want to start a conversation.",
      visibility: "llm_visible",
      parameters: Type.Object({
        text: Type.String({ description: "What you want to say (1-3 sentences, in Chinese, in character)" }),
        style: Type.Optional(Type.String({ description: "Display style: bubble/chat/dialogue_box, default bubble" })),
      }),
      async execute(args): Promise<ToolResult> {
        const text = String(args.text ?? "");
        ctx.memory.addConversation("npc", text);
        ctx.log.push(`[show_dialogue] "${text}"`);
        return { content: `You initiated dialogue: "${text}"`, details: { action: "show_dialogue", text } };
      },
    },
    {
      name: "wait",
      description: "Wait/pause for a moment. Use when resting, observing, or thinking.",
      visibility: "llm_visible",
      parameters: Type.Object({
        duration_ms: Type.Optional(Type.Integer({ description: "Wait duration in ms, default 1000" })),
      }),
      async execute(args): Promise<ToolResult> {
        const ms = Number(args.duration_ms ?? 1000);
        ctx.log.push(`[wait] ${ms}ms`);
        return { content: `You waited for ${ms}ms.`, details: { action: "wait", duration_ms: ms } };
      },
    },
    {
      name: "stop",
      description: "Stop your current action and return to idle state.",
      visibility: "llm_visible",
      parameters: Type.Object({
        reason: Type.Optional(Type.String({ description: "Why you're stopping" })),
      }),
      async execute(args): Promise<ToolResult> {
        const reason = args.reason !== undefined ? String(args.reason) : "";
        ctx.log.push(`[stop] ${reason}`);
        return { content: `You stopped. ${reason}`.trim(), details: { action: "stop" } };
      },
    },
    {
      name: "remember",
      description: "Record a significant memory that you will never forget. Use for relationship milestones, major events, emotional turning points, or traumatic experiences. Write in first person (e.g., 'I met the farmer for the first time today').",
      visibility: "llm_visible",
      parameters: Type.Object({
        text: Type.String({ description: "First-person memory, e.g., '我和农场主第一次见面了'" }),
        category: Type.Union(
          ["relationship", "life_event", "trauma", "achievement"].map((s) => Type.Literal(s)),
          { description: "relationship=relationship change, life_event=major life event, trauma=trauma, achievement=achievement" }
        ),
        emotional_weight: Type.Union(
          ["joy", "sorrow", "anger", "fear", "love", "pride", "surprise"].map((s) => Type.Literal(s)),
          { description: "Emotional weight of this memory" }
        ),
      }),
      async execute(args): Promise<ToolResult> {
        const text = String(args.text ?? "");
        const category = String(args.category ?? "life_event");
        const weight = String(args.emotional_weight ?? "joy");
        const added = ctx.memory.addSignificantMemory(
          text, category, weight,
          ["Farmer"], ctx.scene.location
        );
        if (added) {
          ctx.log.push(`[remember] (${category}/${weight}) "${text}"`);
          return { content: `You will always remember: "${text}"`, details: { text, category, emotional_weight: weight } };
        } else {
          return { content: `You already have this memory: "${text}"`, details: { duplicate: true } };
        }
      },
    },
    {
      name: "get_info",
      description: "Query game world information. Use when you need to know the date, nearby details, your inventory, your memories, or the player's status.",
      visibility: "llm_visible",
      parameters: Type.Object({
        query: Type.Union(
          ["date", "nearby", "memory", "health", "inventory", "player", "location"].map((s) => Type.Literal(s)),
          { description: "date=specific date, nearby=nearby objects detail, memory=recent memories, health=your health, inventory=your items, player=player status, location=current location" }
        ),
      }),
      async execute(args): Promise<ToolResult> {
        const q = String(args.query ?? "");
        let info = "";
        switch (q) {
          case "date":
            info = `${ctx.scene.season}, ${ctx.scene.timeStr}`;
            break;
          case "nearby":
            info = ctx.scene.nearbyObjects;
            break;
          case "memory":
            info = ctx.memory.getRecentMemories(5);
            break;
          case "health":
            info = "Health: 100/100 (full health)";
            break;
          case "inventory":
            info = ctx.inventory.length > 0
              ? ctx.inventory.map(i => `- ${i.name} x${i.quantity}`).join("\n")
              : "Inventory is empty.";
            break;
          case "player":
            info = `${ctx.scene.farmerName}, friendship: ${ctx.memory.friendship}/2500`;
            break;
          case "location":
            info = `Current location: ${ctx.scene.location}`;
            break;
          default:
            info = `Unknown query: ${q}`;
        }
        ctx.log.push(`[get_info] ${q} → ${info.slice(0, 60)}`);
        return { content: info, details: { query: q } };
      },
    },
  ];
}

// 评估玩家送的礼物对 Abigail 的影响
export function evaluateGiftForAbigail(itemName: string): { taste: string; friendshipDelta: number; reaction: string } {
  if (ABIGAIL_LOVED_GIFTS.some(g => g.toLowerCase() === itemName.toLowerCase())) {
    return { taste: "Love", friendshipDelta: 80, reaction: "最爱！Abigail 眼睛亮了起来。" };
  }
  if (ABIGAIL_LIKED_GIFTS.some(g => g.toLowerCase() === itemName.toLowerCase())) {
    return { taste: "Like", friendshipDelta: 45, reaction: "喜欢。Abigail 微微一笑。" };
  }
  if (ABIGAIL_DISLIKED_GIFTS.some(g => g.toLowerCase() === itemName.toLowerCase())) {
    return { taste: "Dislike", friendshipDelta: -20, reaction: "不喜欢。Abigail 皱了皱眉。" };
  }
  return { taste: "Neutral", friendshipDelta: 20, reaction: "普通。Abigail 礼貌地收下了。" };
}
