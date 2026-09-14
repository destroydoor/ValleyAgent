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
 * Owns the read-modify-write lifecycle of the PlayerProfile across the
 * PlayerProfileStore (canonical store) and the ActivityLogStore (raw
 * daily activity log + farm snapshots).
 *
 * 2026-09-14 旧叙事 Director 砍除后本管理器暂无生产消费者——作为未来
 * 工具型 Director 造脑的画像数据层保留；LLM 推断方法（refreshPreferences /
 * refreshPersonality / summarizeForDirector，均无生产调用方）已随之删除。
 */
export class PlayerProfileManager {
  constructor(
    public readonly profileStore: PlayerProfileStore,
    private readonly activityStore: ActivityLogStore,
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
   * 已登记画像的玩家（供导演 per-player 编排）。返回值的元素可能是旧单玩家
   * 格式的占位键 `_legacy`——导演侧按"未知玩家"处理（等价于今天的单玩家行为）。
   */
  listPlayerIds(): string[] {
    return this.profileStore.listPlayerIds();
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
}

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

