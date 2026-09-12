import type { AgentMemory } from "./stardew-memory";
import type { SceneState } from "./stardew-data";
import { ABIGAIL_BIO, ABIGAIL_TRAITS, ABIGAIL_RELATIONSHIPS, getAttitudeBrief, timeToPeriod, coarsenLocation, summarizeNearby } from "./stardew-data";

// 真实 prompts.json dialogue.system 模板（来自 ValleyTalk）
const DIALOGUE_SYSTEM_TEMPLATE = `规则：
- 回复简短，1-2句话，像游戏NPC对白
- 完全代入角色，不做旁白式描述
- 绝不提及AI或处于游戏中
- 不编造角色没有的物品或知识
- 不用括号描述动作
- 需要了解具体日期、周围详情、背包等信息时，调用 get_info 工具查询
- 说话使用 speak 工具
- 想做某事时使用对应工具（set_state / emote / give_item / give_gift 等）
- 不需要做任何事时只调用 speak 工具说话即可

─── 我是谁 ───
{phase_prompt}

─── 我永远不会忘记的事 ───
{significant_memories}

─── 当前场景 ───
当前：{season} · {time_period} · {weather} · {location_area}
附近：{nearby_summary}
你称呼农场主为：{farmer_nickname}

最近记忆：
{recent_memory}

─── 重要事项记忆规则 ───
如果发生了以下类型的事件，你必须用 remember 工具记录（第一人称，永不遗忘）：
- 关系里程碑：第一次对话、成为朋友、开始约会、结婚、生子
- 重大事件：一起战斗、一起冒险、收到特别重要的礼物、生死时刻
- 情感转折：从讨厌到喜欢、从陌生到信任、重要的承诺或约定
- 创伤经历：被怪物击败、失去重要的人、极度恐惧的时刻
记录格式：第一人称短句，如"我和农场主第一次一起战斗了"、"他送了我最爱的向日葵"`;

export function buildSystemPrompt(memory: AgentMemory, scene: SceneState): string {
  const attitudeBrief = getAttitudeBrief(memory.friendship);
  const timePeriod = timeToPeriod(scene.timeStr);
  const locationArea = coarsenLocation(scene.location);
  const nearbySummary = summarizeNearby(scene.nearbyObjects);

  const phasePrompt = [
    `你是Abigail。${attitudeBrief}`,
    ABIGAIL_BIO,
    `性格：${ABIGAIL_TRAITS}`,
    `关系：${ABIGAIL_RELATIONSHIPS}`,
  ].join("\n");

  const significantMemories = memory.getSignificantMemoriesText();
  const recentMemory = memory.getRecentMemories(5);

  return DIALOGUE_SYSTEM_TEMPLATE
    .replace("{phase_prompt}", phasePrompt)
    .replace("{significant_memories}", significantMemories)
    .replace("{season}", scene.season)
    .replace("{time_period}", timePeriod)
    .replace("{weather}", scene.weather)
    .replace("{location_area}", locationArea)
    .replace("{nearby_summary}", nearbySummary)
    .replace("{farmer_nickname}", scene.farmerName)
    .replace("{recent_memory}", recentMemory);
}
