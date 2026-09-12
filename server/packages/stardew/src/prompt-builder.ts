import type { AgentMemory } from "./agent-memory";
import type { SceneState } from "./types";
import type { NpcPromptLoader } from "./npc-prompt-loader";
import { timeToPeriod, coarsenLocation, summarizeNearby } from "./world-snapshot-decoder";

// 段序按变动频率从低到高排列（静态在前、动态在后），以维持前缀缓存高命中率。
// 防回归断言见 tests/prompt-builder.test.ts 的段序单调递增测试。
// Phase 3 L2 状态摘要段强制注入 prompt 开头（设计 §5.1：钱包/背包/位置/心情/近期事件/
// 工作标记/欠款 每次对话强制注入，不依赖 RAG 检索）。
export const DIALOGUE_SYSTEM_TEMPLATE = `## 你的状态
心情：{mood_tag}
近期事件：{recent_events}
正在做：{working_on}
{owed_money_line}当前目标：{current_goal_desc}

规则：
- 回复简短，1-2句话，像游戏NPC对白
- 完全代入角色，不做旁白式描述
- 绝不提及AI或处于游戏中
- 不编造角色没有的物品或知识
- 不用括号描述动作
- 需要了解具体日期、周围详情、背包等信息时，调用 get_info 工具查询
- 说话使用 speak 工具
- 想做某事时使用对应工具（set_state / emote / give_item / give_gift 等）
- 不需要做任何事时只调用 speak 工具说话即可
- 重要：当{farmer_nickname}邀请你跟随、同行、一起做事或要求你行动，而你愿意时，必须在 speak 的同时调用对应工具——同意跟随/同行就调用 set_state(state=FOLLOW)，同意帮忙干活就调用 set_state 到对应状态。只嘴上答应却不调用工具是绝对不允许的；拒绝时只调用 speak
- 对话结束前必须调用 evaluate_friendship 工具评估本轮对话对好感度的影响（delta 为整数，可正可负可为零）
- 交易：玩家手持物 = 玩家想卖给你的东西，你是买家
- 交易：公道价以市场价为准，你可在 ±30% 内让步
- 交易：达成交易必须调用 trade 工具，不能只嘴上说说

─── 重要事项记忆规则 ───
如果发生了以下类型的事件，你必须用 remember 工具记录（第一人称，永不遗忘）：
- 关系里程碑：第一次对话、成为朋友、开始约会、结婚、生子
- 重大事件：一起战斗、一起冒险、收到特别重要的礼物、生死时刻
- 情感转折：从讨厌到喜欢、从陌生到信任、重要的承诺或约定
- 创伤经历：被怪物击败、失去重要的人、极度恐惧的时刻
记录格式：第一人称短句，如"我和{farmer_nickname}第一次一起战斗了"、"他送了我最爱的向日葵"

─── 我是谁 ───
{phase_prompt}

─── 我永远不会忘记的事 ───
{significant_memories}

{beat_section}{actual_state_section}最近记忆：
{recent_memory}

─── 最近对话 ───
{conversation_history}

─── 当前场景 ───
当前：{season} · {time_period} · {weather} · {location_area}
附近：{nearby_summary}
你的状态：{npc_state}
你的钱包：{npc_money_desc}
你的背包：{npc_inventory_summary}
你称呼{farmer_nickname}为：{farmer_nickname}
{farmer_nickname}钱包：{player_money_desc}
{farmer_nickname}手持：{playerHeldItem_desc}
{purchase_offers_line}{tool_results_section}`;

// Phase 3 L3 临时剧本段（2026-09-12 接线）：当前活跃 beat 的第三人称场景描述。
// beat 有效期内注入（C# BeatStore → worldSnapshot.currentBeat → decoder）；
// 无 beat 时整段省略。措辞绝不提"导演"——NPC 不知道导演存在（职责隔离 P0）。
const BEAT_SECTION_TEMPLATE = `─── 眼下正发生的事 ───
{beat_scene}

`;

// 真实状态镜像段（中频，状态变化才变）。由 state_changed 消息驱动，
// actualState 为空时整段省略，避免向 LLM 呈现空头标题。
const ACTUAL_STATE_SECTION = `─── 我现在的真实状态 ───
{actual_state}

`;

const TOOL_RESULTS_SECTION = `─── 上次行动结果 ───
{tool_results}

`;

export class PromptBuilder {
  constructor(private readonly loader: NpcPromptLoader) {}

  buildDialogueSystemPrompt(
    memory: AgentMemory,
    scene: SceneState,
    npcName: string,
    toolResultsText: string = "",
    actualState?: string,
    playerId?: string,
    playerName?: string,
  ): string {
    const npcData = this.loader.getNpcData(npcName);
    // M2a：好感度按对话发起玩家取（TS 权威在玩家桶）；无 playerId（单机/旧调用）用 scene 镜像。
    const friendship = playerId ? memory.getFriendship(playerId) : scene.friendship;
    const phasePrompt = this.loader.getPhasePrompt(npcName, friendship);
    const attitudeBrief = this.loader.getAttitudeBrief(friendship, playerName);

    const baseMemory = npcData?.base_memory ?? `你是 ${npcName}，一个星露谷的居民。`;

    const fullPhasePrompt = [
      `你是${npcName}。${attitudeBrief}`,
      baseMemory,
      phasePrompt,
    ].join("\n\n");

    const significantMemories = memory.getSignificantMemoriesText(playerId);
    const conversationHistory = memory.getConversationContext(8, playerId) || "（今天还没说过话）";
    const recentMemory = memory.getRecentMemories(5, playerId);
    const timePeriod = timeToPeriod(scene.timeStr);
    // E0-6: 位置锚定用 NPC 自己所在地图（防"刚从矿洞出来"式幻觉）；
    // 旧客户端不携带 npcLocation 时回退玩家地图 scene.location。
    const locationArea = coarsenLocation(scene.npcLocation ?? scene.location);
    const nearbySummary = summarizeNearby(scene.nearbyObjects);

    // 真实状态镜像由 state_changed 消息驱动，actualState 参数来自 registry 的 per-NPC 镜像。
    // actualState 为空/undefined 时整段省略，避免呈现空头标题误导 LLM。
    const actualStateSection = actualState && actualState.trim().length > 0
      ? ACTUAL_STATE_SECTION.replace("{actual_state}", `当前实际状态：${actualState.trim()}`)
      : "";

    // E4-1: 玩家钱包感知（缺钱/有钱 NPC 差异化接单判定）。旧 C# 客户端不携带 → 略过。
    const playerMoneyDesc = scene.playerMoney === null || scene.playerMoney === undefined
      ? "（未知）"
      : `${scene.playerMoney} 金币`;

    // E4-2: NPC 自己的钱包。旧 C# 客户端不携带 → "（未知）"（与 playerMoney 同款降级）。
    const npcMoneyDesc = scene.npcMoney === null || scene.npcMoney === undefined
      ? "（未知）"
      : `${scene.npcMoney} 金币`;

    // E4-2: NPC 自己的背包。null=未知（字段缺失）；空数组=真空包 → "空"。
    // 粗化：最多列 8 项"石头×13"式条目，超出以"、等 N 样"收尾。
    const npcInventorySummary = scene.npcInventory === null || scene.npcInventory === undefined
      ? "（未知）"
      : scene.npcInventory.length === 0
        ? "空"
        : scene.npcInventory.length <= 8
          ? scene.npcInventory.map((i) => `${i.name}×${i.quantity}`).join("、")
          : `${scene.npcInventory.slice(0, 8).map((i) => `${i.name}×${i.quantity}`).join("、")}、等 ${scene.npcInventory.length - 8} 样`;

    // Phase 1 交易：玩家手持物描述（NPC 是买家）。null=未知（旧客户端不携带）→ "没有手持可交易物品"。
    const playerHeldItemDesc = scene.playerHeldItem === null || scene.playerHeldItem === undefined
      ? "玩家没有手持可交易物品"
      : `玩家手持 ${scene.playerHeldItem.name}×${scene.playerHeldItem.qty}（公道价约 ${scene.playerHeldItem.marketPrice}g）`;

    // Phase 2: NPC 当前执行目标描述。null=未在执行目标 → 空串（整行不输出）。
    // 注入 {current_goal_desc} 占位符：目标存在时渲染"你正在执行的目标：{type}（{progress}）"。
    // Phase 3 起位于 prompt 开头的「## 你的状态」L2 段（spec §1.3），不再是 当前场景 段。
    const currentGoalDesc = scene.currentGoal
      ? `你正在执行的目标：${scene.currentGoal.type}（${scene.currentGoal.progress}）`
      : "";

    // Phase 3 L2: 心情标签。null=未知（旧客户端不携带）→ "平静"（默认心情，不惊扰 LLM）。
    const moodTag = scene.npcMood === null || scene.npcMood === undefined
      ? "平静"
      : scene.npcMood;

    // Phase 3 L2: 近期事件。null=未知 → "无"；空数组 → "无"；非空 → 顿号连接。
    const recentEvents = scene.npcRecentEvents === null || scene.npcRecentEvents === undefined
      ? "无"
      : scene.npcRecentEvents.length === 0
        ? "无"
        : scene.npcRecentEvents.join("、");

    // Phase 3 L2: 工作标记。null=无工作标记 → "无"。
    const workingOn = scene.npcWorkingOn === null || scene.npcWorkingOn === undefined
      ? "无"
      : scene.npcWorkingOn;

    // Phase 3 L2: 欠款。null/0=无欠款 → 整行省略（常态噪音，2026-09-12 起条件渲染）；
    // 有值 → "欠款：Ng"。
    const owedMoneyLine = scene.npcOwedMoney !== null && scene.npcOwedMoney !== undefined && scene.npcOwedMoney > 0
      ? `欠款：${scene.npcOwedMoney}g\n`
      : "";

    // Phase 3 L3: 当前活跃 beat 场景（第三人称）。无 beat 时整段省略（防空头标题）。
    const beatSection = scene.currentBeat !== null && scene.currentBeat !== undefined
      ? BEAT_SECTION_TEMPLATE.replace("{beat_scene}", scene.currentBeat)
      : "";

    // E3-5: NPC 当日求购单（价格锚）。null=未知（旧客户端）/空数组=无求购 → 整行省略。
    const purchaseOffersLine = scene.npcPurchaseOffers !== null && scene.npcPurchaseOffers !== undefined && scene.npcPurchaseOffers.length > 0
      ? `你想收购：${scene.npcPurchaseOffers.map((o) => `${o.itemName}×${o.quantity}（出价 ${o.unitPrice}g/个）`).join("、")}——玩家手持该物品时可以主动提收购\n`
      : "";

    // Tool-results section is optional: only included when there is feedback text.
    // When empty, the entire section (title + body) is removed so the prompt stays clean.
    const toolResultsSection = toolResultsText.trim().length > 0
      ? TOOL_RESULTS_SECTION.replace("{tool_results}", toolResultsText.trim())
      : "";

    return DIALOGUE_SYSTEM_TEMPLATE
      .replace("{phase_prompt}", fullPhasePrompt)
      .replace("{significant_memories}", significantMemories)
      .replace("{beat_section}", beatSection)
      .replace("{actual_state_section}", actualStateSection)
      .replace("{recent_memory}", recentMemory)
      .replace("{conversation_history}", conversationHistory)
      .replace("{season}", scene.season)
      .replace("{time_period}", timePeriod)
      .replace("{weather}", scene.weather)
      .replace("{location_area}", locationArea)
      .replace("{nearby_summary}", nearbySummary)
      .replace("{npc_state}", scene.npcState ?? "IDLE")
      .replace(/{farmer_nickname}/g, scene.farmerName)
      .replace("{player_money_desc}", playerMoneyDesc)
      .replace("{npc_money_desc}", npcMoneyDesc)
      .replace("{npc_inventory_summary}", npcInventorySummary)
      .replace("{playerHeldItem_desc}", playerHeldItemDesc)
      .replace("{purchase_offers_line}", purchaseOffersLine)
      .replace("{current_goal_desc}", currentGoalDesc)
      .replace("{mood_tag}", moodTag)
      .replace("{recent_events}", recentEvents)
      .replace("{working_on}", workingOn)
      .replace("{owed_money_line}", owedMoneyLine)
      .replace("{tool_results_section}", toolResultsSection);
  }
}

