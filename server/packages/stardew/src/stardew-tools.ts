import { Type } from "@sinclair/typebox";
import type { Tool, ToolResult } from "@valley/core";
import type { AgentMemory } from "./agent-memory";
import type { SceneState, EconomyExecutor, AdjustOp, AdjustResultMessage } from "./types";

const EMOTE_IDS = Type.Union(
  ["happy", "sad", "angry", "surprised", "heart", "question", "exclamation", "sleep", "music", "stretch", "star", "love", "note", "wave", "hooray", "confused", "thinking", "annoyed", "worried", "frustrated", "sweat", "fish", "gift", "bomb"].map((s) => Type.Literal(s))
);

const STATES = Type.Union(
  ["IDLE", "FOLLOW", "FIGHT", "FARM", "FORAGE", "MINE", "CHOP", "TALK", "EXECUTING_GOAL", "TRAVELING_TO_REPORT"].map((s) => Type.Literal(s))
);

// Phase 2: set_goal 支持的五种目标类型（C# GoalExecutor 对应五种 Handler）。
const GOAL_TYPES = Type.Union(
  ["chop_tree", "mine", "water_crops", "fight", "forage"].map((s) => Type.Literal(s))
);

export interface ToolContext {
  memory: AgentMemory;
  scene: SceneState;
  // E4-2: NPC 自己的背包（来源 scene.npcInventory；未知时为空数组）。
  // 注意区分 scene.inventory——那是玩家背包，give_item/give_gift 不得基于它。
  inventory: Array<{ name: string; quantity: number }>;
  givenToPlayer: Array<{ name: string; quantity: number }>;
  log: string[];
  // 2026-08-15 步骤 2：经济工具同步执行器（ProtocolAdapter 注入）。
  // 未接线（测试/旧路径）时经济工具回退到意图式（只记日志，由 C# 执行）。
  economy?: EconomyExecutor;
  // 2026-08-17 M2b：对话发起玩家（玩家关系记忆路由 + 文案玩家化）。无 playerId 时
  // 记忆落世界桶（旧调用/单机兼容），文案用"农场主"。
  playerId?: string;
  playerName?: string;
}

/** M2b：对话玩家显示名（工具记忆文案主语）。仅 playerId 存在（多玩家路由激活）
 * 时才用玩家名；无 playerId（旧调用/单机语义）一律"农场主"——playerName 可能
 * 在无 playerId 时也被传入（场景 farmerName），不能作为激活信号。 */
function playerLabelOf(ctx: ToolContext): string {
  return ctx.playerId && ctx.playerName && ctx.playerName.trim().length > 0 ? ctx.playerName.trim() : "农场主";
}

/** AdjustFailureCode（camelCase）→ LLM 可读中文（步骤 2：同步执行失败时工具直接回错）。 */
const ECONOMY_FAIL_CN: Record<string, string> = {
  insufficientFunds: "钱不够",
  inventoryFull: "背包满了",
  itemNotFound: "没有这个物品",
  agentMissing: "我这边出了点状况",
  invalidOp: "这笔交易不对",
  internalError: "出了点问题",
};

/** 从回执构建 LLM 可读的失败文案（失败码 + 首个失败步详情）。 */
function adjustFailureText(r: AdjustResultMessage): string {
  // 2026-08-17 对账闭环：超时回执（steps[0].detail 标记）不是确定失败——C# 可能已执行，
  // 文案必须阻止 LLM 重复付款/交付（重试 = 双倍扣钱），引导其等核实。
  const detail = r.steps[0]?.detail;
  if (detail?.includes("receipt timeout")) {
    return "结果尚未确认，请不要重复付款或交付，稍后我会核实";
  }
  const cn = ECONOMY_FAIL_CN[r.failureCode ?? "internalError"] ?? "出了点问题";
  return `${cn}${detail ? `（${detail}）` : ""}`;
}

/** 账本/场景里查物品显示名（trade 的 itemName 用；查不到回退 itemId）。 */
function resolveItemDisplayName(ctx: ToolContext, itemId: string): string {
  const held = ctx.scene.playerHeldItem;
  if (held && (held.itemId === itemId || held.name === itemId)) return held.name;
  const entry = ctx.inventory.find((i) => i.name === itemId);
  return entry?.name ?? itemId;
}

export function buildStardewTools(ctx: ToolContext): Tool[] {
  return [
    {
      name: "speak",
      description: "NPC speaks a line of dialogue to the farmer. Use when you want to say something. The text will be shown in a speech bubble above your head.",
      visibility: "llm_visible",
      parameters: Type.Object({
        text: Type.String({ description: "What you want to say (in character, 1-2 sentences, in Chinese)" }),
        duration_ms: Type.Optional(Type.Integer({ description: "Bubble display duration in ms, default 3000" })),
      }),
      async execute(args): Promise<ToolResult> {
        const text = String(args.text ?? "");
        // M2 修复（2026-08-23）：NPC 台词与玩家输入同权进当前对话玩家的桶，
        // 否则玩家桶历史只有半边对话。无 playerId（beat/旧调用）保持世界桶。
        ctx.memory.addConversation("npc", text, ctx.playerId, ctx.playerName);
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
        // Phase 1: item_id 必须是 QualifiedItemId（如 (O)388 for Wood），不要传 DisplayName，
        // 否则 C# 端 ItemRegistry.Create 无法解析。C# 侧已加名称回落，但规范优先传 ID。
        item_id: Type.String({ description: "传 QualifiedItemId（如 (O)388 for Wood），不要传 DisplayName" }),
        quantity: Type.Optional(Type.Integer({ description: "Quantity, default 1" })),
      }),
      async execute(args): Promise<ToolResult> {
        const name = String(args.item_id ?? "");
        const qty = Number(args.quantity ?? 1);
        if (!name || qty <= 0) {
          return { content: "give_item failed: 物品或数量无效。", isError: true };
        }
        if (ctx.economy) {
          // 步骤 2 同步编排：NPC 扣物 + 玩家收物，原子批一次完成（不再需要 C# 二次执行）。
          const ops: AdjustOp[] = [
            { kind: "item", target: "npc", itemId: name, itemName: name, quantity: -qty, reason: "give_item" },
            { kind: "item", target: "player", itemId: name, itemName: name, quantity: qty, reason: "give_item" },
          ];
          const r = await ctx.economy.adjust(ops);
          ctx.log.push(`[give_item] ${qty}x ${name} → farmer ${r.success ? "ok" : `fail:${r.failureCode ?? "?"}`}`);
          if (!r.success) {
            const fail = adjustFailureText(r);
            ctx.memory.addMemory(`想给${playerLabelOf(ctx)} ${qty} 个 ${name} 但没给成：${fail}`, 3.0, "event", ctx.scene.location, ["give_item", "failure"], ctx.playerId, ctx.playerName);
            return { content: `give_item 失败：${fail}`, isError: true };
          }
          ctx.memory.addMemory(`送了${playerLabelOf(ctx)} ${qty} 个 ${name}`, 4.0, "event", ctx.scene.location, ["give_item"], ctx.playerId, ctx.playerName);
          return { content: `You gave ${qty}x ${name} to the farmer.`, details: { action: "give_item", item: name, quantity: qty } };
        }
        // 未接线回退（测试/旧路径）：本地背包扣减 + 意图日志。
        const entry = ctx.inventory.find((i) => i.name === name);
        if (!entry) {
          return { content: `You don't have "${name}" in your inventory.`, isError: true };
        }
        if (entry.quantity < qty) {
          return { content: `You only have ${entry.quantity}x ${name}, can't give ${qty}.`, isError: true };
        }
        entry.quantity -= qty;
        const given = ctx.givenToPlayer.find((g) => g.name === name);
        if (given) given.quantity += qty;
        else ctx.givenToPlayer.push({ name, quantity: qty });
        if (entry.quantity === 0) {
          ctx.inventory = ctx.inventory.filter((i) => i !== entry);
        }
        ctx.log.push(`[give_item] ${qty}x ${name} → farmer`);
        return { content: `You gave ${qty}x ${name} to the farmer.`, details: { action: "give_item", item: name, quantity: qty } };
      },
    },
    {
      name: "give_gift",
      description: "Give a gift to the farmer as a gesture of friendship. This carries emotional weight and will be remembered. Different from give_item.",
      visibility: "llm_visible",
      parameters: Type.Object({
        // 2026-08-17 键归一：与 give_item 同口径，传 QualifiedItemId（如 (O)388 for Wood）。
        // LLM 传显示名时 C# 名称回落能执行，但 TS 账本以回执权威键入账——描述收口减少歧义。
        item_id: Type.String({ description: "传 QualifiedItemId（如 (O)388 for Wood），不要传 DisplayName" }),
        quantity: Type.Optional(Type.Integer({ description: "Quantity, default 1" })),
      }),
      async execute(args): Promise<ToolResult> {
        const name = String(args.item_id ?? "");
        const qty = Number(args.quantity ?? 1);
        if (!name || qty <= 0) {
          return { content: "give_gift failed: 物品或数量无效。", isError: true };
        }
        if (ctx.economy) {
          // 步骤 2 同步编排：与 give_item 同批（NPC 扣物 + 玩家收物），送礼语义由记忆区分。
          const ops: AdjustOp[] = [
            { kind: "item", target: "npc", itemId: name, itemName: name, quantity: -qty, reason: "give_gift" },
            { kind: "item", target: "player", itemId: name, itemName: name, quantity: qty, reason: "give_gift" },
          ];
          const r = await ctx.economy.adjust(ops);
          ctx.log.push(`[give_gift] ${qty}x ${name} → farmer ${r.success ? "ok" : `fail:${r.failureCode ?? "?"}`}`);
          if (!r.success) {
            const fail = adjustFailureText(r);
            ctx.memory.addMemory(`想送礼物但没送成：${fail}`, 3.0, "gift", ctx.scene.location, ["gift", "failure"], ctx.playerId, ctx.playerName);
            return { content: `give_gift 失败：${fail}`, isError: true };
          }
          ctx.memory.addMemory(`送了${playerLabelOf(ctx)}一份礼物（${name}）`, 6.0, "gift", ctx.scene.location, ["gift", "friendship"], ctx.playerId, ctx.playerName);
          return { content: `You gave ${qty}x ${name} to the farmer as a gift.`, details: { action: "give_gift", itemId: name, quantity: qty } };
        }
        // 未接线回退（旧客户端/测试）：意图式。
        ctx.log.push(`[give_gift] intent: give ${qty}x ${name} to farmer`);
        return { content: `I want to give ${qty}x ${name} to the farmer.`, details: { action: "give_gift", itemId: name, quantity: qty } };
      },
    },
    {
      name: "receive_payment",
      description: "当农场主要付钱给你（报酬/还款/赠送）且你同意收时使用。金额以农场主说的为准，不要乱编。实际收款由游戏执行，结果会通过回执告知。",
      visibility: "llm_visible",
      parameters: Type.Object({
        amount: Type.Integer({ description: "收款金额（农场主付给你的金币数）" }),
        reason: Type.Optional(Type.String({ description: "付款缘由，如'挖矿工钱'" })),
      }),
      async execute(args): Promise<ToolResult> {
        const amount = Number(args.amount ?? 0);
        const reason = args.reason !== undefined ? String(args.reason) : "";
        if (amount <= 0) {
          return { content: "receive_payment failed: 金额无效。", isError: true };
        }
        if (ctx.economy) {
          // 步骤 2 同步编排：玩家扣钱 + NPC 收钱（雇佣报酬/偿还借款），原子批一次完成。
          const ops: AdjustOp[] = [
            { kind: "money", target: "player", amount: -amount, reason: `receive_payment${reason ? `:${reason}` : ""}` },
            { kind: "money", target: "npc", amount, reason: `receive_payment${reason ? `:${reason}` : ""}` },
          ];
          const r = await ctx.economy.adjust(ops);
          ctx.log.push(`[receive_payment] ${amount}g ${r.success ? "ok" : `fail:${r.failureCode ?? "?"}`}`);
          if (!r.success) {
            const fail = adjustFailureText(r);
            ctx.memory.addMemory(`${playerLabelOf(ctx)}想付我 ${amount}g 但没付成：${fail}`, 3.0, "event", ctx.scene.location, ["payment", "failure"], ctx.playerId, ctx.playerName);
            return { content: `receive_payment 失败：${fail}`, isError: true };
          }
          const reasonText = reason ? `（${reason}）` : "";
          ctx.memory.addMemory(`${playerLabelOf(ctx)}付了我 ${amount}g${reasonText}`, 5.0, "event", ctx.scene.location, ["payment"], ctx.playerId, ctx.playerName);
          return { content: `I received ${amount}g from the farmer.`, details: { action: "receive_payment", amount, reason } };
        }
        // 未接线回退（旧客户端/测试）：意图式。
        ctx.log.push(`[receive_payment] intent: receive ${amount}g${reason ? ` (${reason})` : ""} from farmer`);
        return { content: `I agreed to receive ${amount}g from the farmer.`, details: { action: "receive_payment", amount, reason } };
      },
    },
    {
      name: "trade",
      description: "与玩家交易：你是买家，按 price 单价购买玩家手里 quantity 个 item_id。" +
        "只有玩家手持该物品时才能成交。达成一致必须调用本工具，不能只嘴上说。",
      visibility: "llm_visible",
      parameters: Type.Object({
        item_id: Type.String({ description: "物品的 QualifiedItemId，如 (O)388" }),
        // 整数收口（2026-08-23 修复）：浮点数量/单价会原样进账本（0.5 个木头、0.1g 单价），
        // 与 receive_payment 的 Type.Integer 同口径。
        quantity: Type.Integer({ minimum: 1, description: "购买数量（正整数）" }),
        price: Type.Integer({ minimum: 1, description: "单价（g，正整数）" }),
        // direction 固定 npc_buys（你是买家），可选：缺省时 execute 默认 npc_buys。
        direction: Type.Optional(
          Type.Union(
            ["npc_buys"].map((s) => Type.Literal(s)),
            { description: "固定为 npc_buys（你是买家）" },
          ),
        ),
      }),
      async execute(args): Promise<ToolResult> {
        const itemId = String(args.item_id ?? "");
        const quantity = Number(args.quantity ?? 1);
        const price = Number(args.price ?? 0);
        const direction = String(args.direction ?? "npc_buys");
        // 运行时与 schema 同口径：非整数（浮点/NaN）一律本地拒绝，不进账本。
        if (!itemId || !Number.isInteger(quantity) || !Number.isInteger(price) || quantity <= 0 || price <= 0) {
          return { content: "trade failed: 物品/数量/单价无效（数量与单价必须为正整数）。", isError: true };
        }
        if (ctx.economy) {
          // 步骤 2 同步编排：原子批一次完成双方钱物转移（玩家收钱扣物、NPC 付钱收物）。
          // 不再需要"递给 NPC"手势与 30s 待成交单——C# 物理校验（玩家真有货/真有钱）在回执侧兜底。
          const itemName = resolveItemDisplayName(ctx, itemId);
          const total = price * quantity;
          const ops: AdjustOp[] = [
            { kind: "money", target: "player", amount: total, reason: "trade" },
            { kind: "item", target: "player", itemId, itemName, quantity: -quantity, reason: "trade" },
            { kind: "money", target: "npc", amount: -total, reason: "trade" },
            { kind: "item", target: "npc", itemId, itemName, quantity, reason: "trade" },
          ];
          const r = await ctx.economy.adjust(ops);
          ctx.log.push(`[trade] buy ${quantity}x ${itemId} @ ${price}g ${r.success ? "ok" : `fail:${r.failureCode ?? "?"}`}`);
          if (!r.success) {
            const fail = adjustFailureText(r);
            ctx.memory.addMemory(`想买${playerLabelOf(ctx)}的 ${quantity} 个 ${itemName} 但没买成：${fail}`, 3.0, "event", ctx.scene.location, ["trade", "failure"], ctx.playerId, ctx.playerName);
            return { content: `trade 失败：${fail}`, isError: true };
          }
          ctx.memory.addMemory(`买下了${playerLabelOf(ctx)}的 ${quantity} 个 ${itemName}，花了 ${total}g`, 5.0, "event", ctx.scene.location, ["trade"], ctx.playerId, ctx.playerName);
          return {
            content: `You bought ${quantity}x ${itemName} from the farmer for ${total}g.`,
            details: { action: "trade", itemId, quantity, price, direction },
          };
        }
        // 未接线回退（旧客户端/测试）：意图式。
        ctx.log.push(`[trade] intent: buy ${quantity}x ${itemId} from farmer at ${price}g each`);
        return {
          content: `I want to buy ${quantity}x ${itemId} from you at ${price}g each.`,
          details: { action: "trade", itemId, quantity, price, direction },
        };
      },
    },
    {
      name: "set_goal",
      description: "给自己设定一个要完成的任务。设定后你会进入执行态，C# 会后台执行" +
        "（不消耗对话），完成后会寻路回来向你汇报。",
      visibility: "llm_visible",
      parameters: Type.Object({
        type: GOAL_TYPES,
        // params 为自由对象（如 {quantity: 10} 或 {targetItemId: '(O)378', quantity: 5}），
        // 缺省空对象，C# GoalExecutor 按类型解释。
        params: Type.Optional(Type.Object({}, { description: "目标参数，如 {quantity: 10} 或 {targetItemId: '(O)378', quantity: 5}" })),
        reportBack: Type.Optional(Type.Boolean({ description: "完成后是否寻路回玩家汇报，默认 true" })),
      }),
      async execute(args): Promise<ToolResult> {
        const type = String(args.type ?? "");
        const params = args.params && typeof args.params === "object" ? args.params : {};
        const reportBack = args.reportBack === undefined ? true : Boolean(args.reportBack);
        // Phase 2 意图式（仿 trade）：execute 只写意图日志，不碰 ctx.memory、不改 scene。
        // 实际执行由 C# GoalExecutor 后台跑（零 LLM），reportBack=true 时完成后寻路回来汇报，
        // 结果通过 action_result 回执告知（routeToolResult 写目标成立/失败记忆）。
        ctx.log.push(`[set_goal] intent: ${type} params=${JSON.stringify(params)} reportBack=${reportBack}`);
        return { content: `I set a goal to ${type}.`, details: { action: "set_goal", type, params, reportBack } };
      },
    },
    {
      name: "chop_tree",
      description: "Chop a nearby tree to gather wood. You must be near a tree. The actual chopping is executed by the game engine; this records your intent.",
      visibility: "llm_visible",
      parameters: Type.Object({
        tree_id: Type.Optional(Type.String({ description: "Specific tree identifier (optional; defaults to nearest tree)" })),
      }),
      async execute(args): Promise<ToolResult> {
        const treeId = args.tree_id !== undefined ? String(args.tree_id) : "";
        // D2 two-stage: execute only logs intent, does not write ctx.memory.
        // Actual chop + memory writing handled by routeToolResult on action_result
        // (success=true writes chop success memory; success=false writes failure memory).
        ctx.log.push(`[chop_tree] intent: chop tree ${treeId || "(nearest)"}`);
        return { content: `I want to chop a tree.`, details: { action: "chop_tree", treeId } };
      },
    },
    {
      name: "set_state",
      description: "Switch your behavior state. States: IDLE(rest), FOLLOW(accompany farmer), FIGHT(combat), FARM(farming), FORAGE(collecting), MINE(mining), CHOP(chopping trees), TALK(conversing).",
      visibility: "llm_visible",
      parameters: Type.Object({
        state: STATES,
      }),
      async execute(args): Promise<ToolResult> {
        const state = String(args.state ?? "IDLE");
        // E0-6: 意图化措辞——execute 只表达"打算"，实际转换由 C# 状态机执行，
        // 结果通过 state_changed/回执告知；避免意图被当事实留在最近记忆里。
        ctx.memory.addMemory(`（意图）我打算进入 ${state} 状态，成没成看后续回执`, 3.0, "decision", ctx.scene.location, ["state"]);
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
        // 同 speak：有当前对话玩家时台词进玩家桶（修复对话历史缺 NPC 半边）。
        ctx.memory.addConversation("npc", text, ctx.playerId, ctx.playerName);
        ctx.log.push(`[show_dialogue] "${text}"`);
        return { content: `You initiated dialogue: "${text}"`, details: { action: "show_dialogue", text } };
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
        // M2b：relationship 类记忆归属对话玩家（relatedNpcs 填玩家名）；其余落世界桶。
        const isRelationship = category === "relationship";
        const related = isRelationship ? [playerLabelOf(ctx)] : ["Farmer"];
        const added = ctx.memory.addSignificantMemory(
          text, category, weight,
          related, ctx.scene.location,
          isRelationship ? ctx.playerId : undefined,
          isRelationship ? ctx.playerName : undefined,
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
      name: "forget",
      description: "删除一条不重要的短期记忆。只能删除普通记忆，重要的里程碑记忆（remember 记录的）无法删除。用于清理过时或错误的记忆。参数 memory_text 为部分匹配即可。",
      visibility: "llm_visible",
      parameters: Type.Object({
        memory_text: Type.String({ description: "要删除的记忆内容（部分匹配即可）" }),
        reason: Type.String({ description: "为什么要删除这条记忆" }),
      }),
      async execute(args): Promise<ToolResult> {
        const memoryText = String(args.memory_text ?? "");
        const reason = String(args.reason ?? "");
        // 双桶语义（2026-08-23 修复）：交易失败等记忆写在玩家桶、任务类写世界桶，
        // LLM 无法区分——先试世界桶再试当前玩家桶，任一命中即成功，计数合并上报。
        const removed =
          ctx.memory.removeMemory(memoryText) +
          (ctx.playerId ? ctx.memory.removeMemory(memoryText, ctx.playerId) : 0);
        if (removed === 0) {
          ctx.log.push(`[forget] no match for "${memoryText}" (reason: ${reason})`);
          return {
            content: `没有找到包含"${memoryText}"的普通记忆（重要记忆无法删除）`,
            details: { action: "forget", memory_text: memoryText, reason, removed: 0 },
          };
        }
        ctx.log.push(`[forget] removed ${removed} memory(s) matching "${memoryText}" (reason: ${reason})`);
        return {
          content: `已删除 ${removed} 条记忆`,
          details: { action: "forget", memory_text: memoryText, reason, removed },
        };
      },
    },
    {
      name: "get_info",
      description: "Query game world information. Use when you need to know the date, nearby details, your inventory, your memories, or the player's status.",
      visibility: "llm_visible",
      parameters: Type.Object({
        query: Type.Union(
          ["date", "nearby", "memory", "health", "inventory", "player", "location"].map((s) => Type.Literal(s)),
          { description: "date=specific date, nearby=nearby objects detail, memory=recent memories, health=your health, inventory=your own items (not the farmer's), player=player status, location=your current location" }
        ),
      }),
      async execute(args): Promise<ToolResult> {
        const q = String(args.query ?? "");
        let info = "";
        switch (q) {
          case "date":
            info = `${ctx.scene.season}, day ${ctx.scene.day}, ${ctx.scene.timeStr}`;
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
            // E4-2: 答 NPC 自己的背包。步骤 2 起以权威账本为准（economy.getInventory，
            // null=未播种）；未接线时回退 scene.npcInventory（null=未知，与空包区分）。
            {
              const fromLedger = ctx.economy?.getInventory() ?? null;
              const source = fromLedger ?? (ctx.scene.npcInventory === null || ctx.scene.npcInventory === undefined ? null : ctx.inventory);
              info = source === null
                ? "（背包情况未知）"
                : source.length > 0
                  ? source.map((i) => `- ${i.name} x${i.quantity}`).join("\n")
                  : "Inventory is empty.";
            }
            break;
          case "player":
            // M2b：好感读对话玩家桶（TS 权威）；无 playerId（旧调用）回落 scene 镜像。
            info = `${ctx.scene.farmerName}, friendship: ${ctx.playerId ? ctx.memory.getFriendship(ctx.playerId) : ctx.scene.friendship}/2500`;
            break;
          case "location":
            // E0-6: 答 NPC 自己的位置；旧客户端不携带时回退玩家地图并标注是农场主所在处。
            info = ctx.scene.npcLocation
              ? `你的位置：${ctx.scene.npcLocation}`
              : `你的位置未知；农场主所在处：${ctx.scene.location}`;
            break;
          default:
            info = `Unknown query: ${q}`;
        }
        ctx.log.push(`[get_info] ${q} → ${info.slice(0, 60)}`);
        return { content: info, details: { query: q } };
      },
    },
    {
      name: "accept_job",
      description: "接受农场主的雇佣契约。任务类型：follow(跟随陪伴)/watering(收菜)/mining(挖矿)/bodyguard(下矿保镖)；期限 half(半天)/full(一天)。报酬已由 C# 按人设日薪/危险系数/缺钱/好感修正计算，你只需表态接受。契约生效期间你的自主行为让位于契约任务。",
      visibility: "llm_visible",
      parameters: Type.Object({
        task_type: Type.Union(
          ["follow", "watering", "mining", "bodyguard"].map((s) => Type.Literal(s)),
          { description: "契约任务类型" }
        ),
        duration: Type.Union(
          ["half", "full"].map((s) => Type.Literal(s)),
          { description: "契约期限：half=半天 / full=一天" }
        ),
        reward: Type.Integer({ description: "契约报酬（C# 已计价，直接确认）" }),
        response: Type.String({ description: "接受雇佣时说的话（1-2 句，符合人设；缺钱/朋友价/拒绝时表达对应态度）" }),
      }),
      async execute(args): Promise<ToolResult> {
        const taskType = String(args.task_type ?? "follow");
        const duration = String(args.duration ?? "half");
        const reward = Number(args.reward ?? 0);
        const response = String(args.response ?? "");
        ctx.memory.addMemory(
          `接受了农场主的雇佣：${taskType}/${duration}，报酬 ${reward}g`,
          5.0,
          "decision",
          ctx.scene.location,
          ["hire", "contract"],
        );
        ctx.log.push(`[accept_job] ${taskType}/${duration} reward=${reward} "${response}"`);
        return {
          content: `Accepted job: ${taskType}/${duration} for ${reward}g.`,
          details: { action: "accept_job", taskType, duration, reward, response },
        };
      },
    },
    {
      name: "evaluate_friendship",
      description: "评估本轮对话对好感度的影响。每次对话结束后必须调用一次。delta 为正表示增好感，为负表示减好感，为 0 表示无变化。",
      visibility: "llm_visible",
      parameters: Type.Object({
        delta: Type.Integer({ description: "好感度变化（整数，可正可负可为零）。+10 表示明显增好感，-10 表示明显减好感，0 表示无变化。一般范围 -20 到 +20" }),
        reason: Type.Optional(Type.String({ description: "好感变化的原因（第一人称短句，如'他夸了我的头发'）" })),
      }),
      async execute(args): Promise<ToolResult> {
        const delta = Number(args.delta ?? 0);
        const reason = args.reason !== undefined ? String(args.reason) : "";
        ctx.log.push(`[evaluate_friendship] delta=${delta} reason="${reason}"`);
        return { content: `Friendship delta recorded: ${delta}`, details: { action: "evaluate_friendship", delta, reason } };
      },
    },
  ];
}
