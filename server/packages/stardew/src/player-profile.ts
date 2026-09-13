// PlayerProfileManager — orchestrates the five-layer PlayerProfile (Task 6).
// Spec: docs/superpowers/specs/2026-07-21-narrative-director-design.md §4

import type {
  PlayerProfile,
  DailyActivity,
  PlayStyle,
  Interaction,
  GiftRecord,
  BeatHistoryEntry,
} from "./types";
import type { PlayerProfileStore } from "./player-profile-store";
import type { ActivityLogStore } from "./activity-log-store";

const MAX_INTERACTIONS_PER_NPC = 5;
const MAX_GIFTS_PER_NPC = 10;

/**
 * LLM caller signature injected into PlayerProfileManager.
 * Implementations should return the model's text response and token usage;
 * they MUST NOT throw on transient LLM errors — instead, callers handle
 * failure modes (see refreshPreferences / refreshPersonality).
 *
 * M3 多玩家化（2026-09-13）：所有读写方法接受可选 playerId（缺省 → legacy/单玩家
 * 键，行为等价现状）。联机下每个玩家一份五层画像，导演按玩家各自取摘要编排。
 */
export interface PlayerProfileLlmConfig {
  callLlm: (prompt: string) => Promise<{
    text: string;
    usage: { promptTokens: number; completionTokens: number };
  }>;
}

/**
 * Owns the read-modify-write lifecycle of the PlayerProfile across the
 * PlayerProfileStore (canonical store) and the ActivityLogStore (raw
 * daily activity log + farm snapshots). The Director delegates to this
 * manager for all profile mutations and for producing the compact
 * prompt-injection summary.
 *
 * Failure-mode policy: LLM-dependent refreshes (refreshPreferences /
 * refreshPersonality) swallow errors and preserve the existing profile
 * value rather than corrupting it — the Director must not crash because
 * a single LLM call failed.
 */
export class PlayerProfileManager {
  constructor(
    public readonly profileStore: PlayerProfileStore,
    private readonly activityStore: ActivityLogStore,
    private readonly config: PlayerProfileLlmConfig,
  ) {}

  /**
   * Close the underlying PlayerProfileStore (release SQLite handle).
   * Called by server shutdown to flush WAL and release the DB file.
   */
  close(): void {
    this.profileStore.close();
  }
  /**
   * Create an empty PlayerProfile with the static layer populated.
   * Safe to call once per game (typically when the player starts a new
   * save). If a profile already exists it is overwritten.
   */
  initProfile(staticLayer: PlayerProfile["static"], playerId?: string): void {
    this.profileStore.save(emptyProfileWithStatic(staticLayer), playerId);
  }

  /**
   * Persist a daily activity to BOTH stores:
   *   - ActivityLogStore: raw activity log (for median/sum aggregates)
   *   - PlayerProfileStore: profile.behavior.dailyActivities (latest 30)
   */
  recordDailyActivity(activity: DailyActivity, playerId?: string): void {
    this.activityStore.saveDaily(activity);
    this.profileStore.appendDailyActivity(activity, playerId);
  }

  updateRelationship(
    npcName: string,
    rel: PlayerProfile["relationships"][string],
    playerId?: string,
  ): void {
    this.profileStore.updateRelationship(npcName, rel, playerId);
  }

  /**
   * Append an interaction to a NPC's last5Interactions list, keeping
   * only the most recent MAX_INTERACTIONS_PER_NPC entries. If the NPC
   * has no relationship entry yet, one is created with empty defaults.
   */
  appendInteraction(npcName: string, interaction: Interaction, playerId?: string): void {
    const profile = this.loadOrInit(playerId);
    const rel = profile.relationships[npcName] ?? emptyRelationship();
    const next = [...rel.last5Interactions, interaction];
    rel.last5Interactions =
      next.length > MAX_INTERACTIONS_PER_NPC
        ? next.slice(next.length - MAX_INTERACTIONS_PER_NPC)
        : next;
    rel.lastUpdated = interaction.date;
    this.profileStore.updateRelationship(npcName, rel, playerId);
  }

  /**
   * Append a gift to a NPC's giftHistory list, keeping only the most
   * recent MAX_GIFTS_PER_NPC entries.
   */
  appendGiftHistory(npcName: string, gift: GiftRecord, playerId?: string): void {
    const profile = this.loadOrInit(playerId);
    const rel = profile.relationships[npcName] ?? emptyRelationship();
    const next = [...rel.giftHistory, gift];
    rel.giftHistory =
      next.length > MAX_GIFTS_PER_NPC
        ? next.slice(next.length - MAX_GIFTS_PER_NPC)
        : next;
    this.profileStore.updateRelationship(npcName, rel, playerId);
  }

  appendBeatHistory(entry: BeatHistoryEntry, playerId?: string): void {
    this.profileStore.appendBeatHistory(entry, playerId);
  }

  addRecurringTrope(trope: string, playerId?: string): void {
    this.profileStore.addRecurringTrope(trope, playerId);
  }

  /**
   * Pull the most recent farm snapshot from ActivityLogStore and return
   * its play styles. Returns an empty array if no snapshot exists.
   *
   * Note: this DOES NOT write to PlayerProfileStore.preferences.playStyle —
   * callers (the Director's periodic refresh) decide when to persist.
   */
  inferPlayStylesFromActivityLog(): PlayStyle[] {
    const snapshot = this.activityStore.getLatestFarmSnapshot();
    return snapshot ? snapshot.playStyles : [];
  }

  /**
   * Produce the compact prompt-injection summary used by the Director
   * (spec §4 example). Falls back to a minimal placeholder when no
   * profile exists yet so the Director can still issue a generic plan.
   */
  /**
   * 已登记画像的玩家（供导演 per-player 编排）。返回值的元素可能是旧单玩家
   * 格式的占位键 `_legacy`——导演侧按"未知玩家"处理（等价于今天的单玩家行为）。
   */
  listPlayerIds(): string[] {
    return this.profileStore.listPlayerIds();
  }

  /**
   * 某玩家的画像摘要（playerId 缺省 → legacy/单玩家键）。
   * M3：联机下导演对每个玩家各取一份摘要，互不串味。
   */
  summarizeForDirector(playerId?: string): string {
    const profile = this.profileStore.loadOrClaim(playerId);
    if (!profile) return "[玩家画像]\n无玩家档案\n";

    const lines: string[] = ["[玩家画像]"];
    // Static
    lines.push(
      `姓名: ${profile.static.farmerName || "(未知)"}, 农场: ${profile.static.farmName || "(未知)"} (${profile.static.farmType || "(未知)"})`,
    );
    // Preferences — playStyle
    if (profile.preferences.playStyle.length > 0) {
      const styles = profile.preferences.playStyle
        .map((p) => `${p.tag}(${Math.round(p.confidence * 100)}%)`)
        .join(", ");
      lines.push(`流派: ${styles}`);
    } else {
      lines.push("流派: (未知)");
    }
    // Personality
    const traits = profile.personality.traits.length > 0
      ? profile.personality.traits.join(", ")
      : "(未知)";
    lines.push(`性格: ${traits}, 原型: ${profile.personality.archetype || "(未知)"}`);
    lines.push(`叙事角色: ${profile.personality.narrativeRole || "(未知)"}`);
    // Behavior — last 3 daily activities
    const recent = profile.behavior.dailyActivities.slice(0, 3);
    if (recent.length > 0) {
      const summary = recent
        .map(
          (a) =>
            `${a.date}: 钓鱼${a.fishingMinutes}m/种地${a.farmingMinutes}m/挖矿${a.miningMinutes}m`,
        )
        .join(" | ");
      lines.push(`最近活动: ${summary}`);
    } else {
      lines.push("最近活动: (无)");
    }
    // Relationships
    const relEntries = Object.entries(profile.relationships);
    if (relEntries.length > 0) {
      const relSummary = relEntries
        .map(([name, rel]) => `${name}(${rel.phase || "?"},${rel.friendshipPoints}pt)`)
        .join(", ");
      lines.push(`关系状态: ${relSummary}`);
    } else {
      lines.push("关系状态: (无)");
    }
    return lines.join("\n") + "\n";
  }

  /**
   * Ask the LLM to infer a routinePattern string from recent activity.
   * On LLM failure, preserves the existing routinePattern and returns
   * without throwing — the Director should not crash on a single bad
   * LLM call.
   */
  async refreshPreferences(playerId?: string): Promise<void> {
    const profile = this.profileStore.loadOrClaim(playerId);
    if (!profile) return; // nothing to refresh

    const prompt = this.buildPreferencesPrompt(profile);
    let responseText: string;
    try {
      const result = await this.config.callLlm(prompt);
      responseText = result.text;
    } catch {
      // Preserve existing routinePattern — do not overwrite with empty.
      return;
    }
    const trimmed = responseText.trim();
    if (!trimmed) return;

    this.profileStore.updatePreferences(
      {
        ...profile.preferences,
        routinePattern: trimmed,
        lastUpdated: nowIso(),
      },
      playerId,
    );
  }

  /**
   * Ask the LLM to infer an archetype from player behavior + history.
   * On LLM failure, preserves the existing archetype and returns
   * without throwing.
   */
  async refreshPersonality(playerId?: string): Promise<void> {
    const profile = this.profileStore.loadOrClaim(playerId);
    if (!profile) return;

    const prompt = this.buildPersonalityPrompt(profile);
    let responseText: string;
    try {
      const result = await this.config.callLlm(prompt);
      responseText = result.text;
    } catch {
      return;
    }
    const trimmed = responseText.trim();
    if (!trimmed) return;

    this.profileStore.updatePersonality(
      {
        ...profile.personality,
        archetype: trimmed,
        lastUpdated: nowIso(),
      },
      playerId,
    );
  }

  // ------------------------------------------------------------------
  // Internal helpers
  // ------------------------------------------------------------------

  /**
   * Load the existing profile, or initialize an empty one if none exists
   * yet (so appendInteraction / appendGiftHistory work pre-initProfile).
   */
  private loadOrInit(playerId?: string): PlayerProfile {
    const existing = this.profileStore.loadOrClaim(playerId);
    if (existing) return existing;
    const fresh = emptyProfile();
    this.profileStore.save(fresh, playerId);
    return fresh;
  }

  private buildPreferencesPrompt(profile: PlayerProfile): string {
    const recent = profile.behavior.dailyActivities.slice(0, 7);
    const recentSummary = recent
      .map(
        (a) =>
          `${a.date}: 钓鱼${a.fishingMinutes}m/种地${a.farmingMinutes}m/挖矿${a.miningMinutes}m/社交${a.socialMinutes}m/战斗${a.combatMinutes}m`,
      )
      .join("\n");
    const playStyles = profile.preferences.playStyle
      .map((p) => `${p.tag}(${Math.round(p.confidence * 100)}%)`)
      .join(", ");

    return [
      "你是星露谷的玩家行为分析师。根据玩家最近 7 天的活动日志和已知流派，",
      "用一句话总结玩家的日常规律（routinePattern）。",
      "",
      `[玩家] ${profile.static.farmerName || "(未知)"}, 农场 ${profile.static.farmType}`,
      `[流派] ${playStyles || "(未知)"}`,
      `[最近活动]`,
      recentSummary || "(无)",
      "",
      "要求：",
      "1. 用中文，20-40 字以内",
      "2. 直接输出规律描述，不要任何前后缀（例如不要 'routinePattern:' 前缀）",
      "3. 例如：早晨种地，下午钓鱼，傍晚回农场整理物品",
    ].join("\n");
  }

  private buildPersonalityPrompt(profile: PlayerProfile): string {
    const total = profile.behavior.totalStats;
    const tropes = profile.story.recurringTropes.join(", ");
    const recent = profile.behavior.dailyActivities.slice(0, 7);
    const recentSummary = recent
      .map((a) => `${a.date}: 钓${a.fishingMinutes}m/种${a.farmingMinutes}m/矿${a.miningMinutes}m/战${a.combatMinutes}m`)
      .join("\n");

    return [
      "你是星露谷的角色分析师。根据玩家的长期行为统计和故事历史，",
      "用一个简短的词或短语概括玩家的原型（archetype）。",
      "",
      `[玩家] ${profile.static.farmerName || "(未知)"}`,
      `[累计统计] 钓鱼${total.fishCaught}条, 出货${total.itemsShipped}件, 击杀${total.monstersKilled}只, 收获${total.cropsHarvested}株, 对话${total.dialoguesHad}次`,
      `[最近活动]`,
      recentSummary || "(无)",
      `[已发生套路] ${tropes || "(无)"}`,
      "",
      "要求：",
      "1. 用中文，2-8 字以内",
      "2. 直接输出原型名（如：独行者 / 冒险家 / 社交达人 / 钓鱼狂 / 酿酒师）",
      "3. 不要任何前后缀或解释",
    ].join("\n");
  }
}

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

function emptyRelationship(): PlayerProfile["relationships"][string] {
  return {
    phase: "stranger",
    friendshipPoints: 0,
    last5Interactions: [],
    giftHistory: [],
    notableEvents: [],
    lastUpdated: "",
  };
}

function emptyProfile(): PlayerProfile {
  return {
    static: {
      farmerName: "",
      gender: "unknown",
      farmName: "",
      farmType: "",
      startDate: "",
      lastUpdated: "",
    },
    behavior: {
      dailyActivities: [],
      totalStats: {
        fishCaught: 0,
        itemsShipped: 0,
        monstersKilled: 0,
        cropsHarvested: 0,
        itemsForaged: 0,
        giftsGiven: 0,
        dialoguesHad: 0,
        miningLevelsDescended: 0,
      },
    },
    preferences: {
      playStyle: [],
      topActivities: [],
      topLocations: [],
      routinePattern: "",
      lastUpdated: "",
    },
    relationships: {},
    personality: {
      traits: [],
      archetype: "",
      narrativeRole: "",
      lastUpdated: "",
    },
    story: {
      completedBeats: [],
      recurringTropes: [],
      lastUpdated: "",
    },
  };
}

function emptyProfileWithStatic(
  staticLayer: PlayerProfile["static"],
): PlayerProfile {
  return { ...emptyProfile(), static: { ...staticLayer } };
}

/** Returns the current ISO timestamp (YYYY-MM-DDTHH:MM:SSZ). */
function nowIso(): string {
  return new Date().toISOString();
}
