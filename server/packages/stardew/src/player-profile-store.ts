// PlayerProfileStore — SQLite persistence for the five-layer PlayerProfile (Task 4).
// Uses Bun's built-in `bun:sqlite` (no native compilation required).
// Spec: docs/superpowers/specs/2026-07-21-narrative-director-design.md §4
//
// M3 多玩家化（2026-09-13）：表由"单表单行"改为按 playerId 分键（一人一行）。
// 范式与 M2 记忆拆分（`{npc}_players/{playerId}_rel.json` + `_legacy` 惰性认领）一致：
// - 旧库（id INTEGER CHECK(id=1) 单行）在 init() 内迁到 `_legacy` 玩家键；
// - 真实 playerId 首次访问时若自己没数据而 `_legacy` 有数据 → 认领（单机无感升级）。

import { Database } from "bun:sqlite";
import type {
  PlayerProfile,
  DailyActivity,
  BeatHistoryEntry,
} from "./types";

interface ProfileRow {
  player_id: string;
  data_json: string;
  updated_at: string;
}

/** 旧单文件格式（多玩家拆分前的那一行画像）的归属占位，首个真实 playerId 认领。 */
export const LEGACY_PLAYER_ID = "_legacy";

const MAX_DAILY_ACTIVITIES = 30;
const MAX_BEAT_HISTORY = 50;

/**
 * Persists one PlayerProfile document per player to SQLite.
 *
 * Design:
 * - `player_profile` is keyed by `player_id TEXT PRIMARY KEY`; each player's
 *   PlayerProfile object is serialized to JSON and stored as `data_json`.
 * - All `updateX`/`appendX` methods perform a read-modify-write cycle: load
 *   the existing profile (or create an empty default if none exists), apply
 *   the mutation, then save. Single-writer safe (WAL mode).
 * - `appendDailyActivity` keeps only the latest 30 entries (by date desc).
 * - `appendBeatHistory` keeps only the latest 50 entries (by insertion order).
 *
 * Why one JSON document per player instead of normalized columns? The
 * PlayerProfile is read whole on every Director call (morningPlan /
 * milestoneReact), and mutations are infrequent (once per day at most). A
 * single JSON document avoids schema-migration pain as the profile structure
 * evolves, and is more than fast enough at the expected scale (one row per
 * player — at most a handful of players in a co-op farm).
 *
 * Backward compatibility: all pre-M3 methods keep working with the default
 * (legacy) player key, so single-player and existing callers are unaffected.
 */
export class PlayerProfileStore {
  private readonly db: Database;

  constructor(dbPath: string) {
    this.db = new Database(dbPath, { create: true });
    this.db.exec("PRAGMA journal_mode = WAL;");
    this.db.exec("PRAGMA synchronous = NORMAL;");
  }

  init(): void {
    // 旧库迁移必须先于建表：表已存在时 CREATE TABLE IF NOT EXISTS 是 no-op，
    // 旧 schema 没有 player_id 列，后续写入会直接报错。
    this.migrateLegacySchema();
    this.db.exec(`
      CREATE TABLE IF NOT EXISTS player_profile (
        player_id TEXT PRIMARY KEY,
        data_json TEXT NOT NULL,
        updated_at TEXT NOT NULL DEFAULT (datetime('now'))
      );
    `);
  }

  /**
   * 旧 schema（id INTEGER PRIMARY KEY CHECK (id = 1)）→ 新 schema（player_id 主键）。
   * 单行数据整体迁到 `_legacy` 玩家键，由首个真实 playerId 惰性认领。
   */
  private migrateLegacySchema(): void {
    const columns = this.db
      .query<{ name: string }, []>("PRAGMA table_info(player_profile);")
      .all();
    if (columns.length === 0) return; // 空库，无需迁移
    if (columns.some((c) => c.name === "player_id")) return; // 已是新 schema

    interface LegacyRow {
      id: number;
      data_json: string;
    }
    let legacy: LegacyRow | null = null;
    try {
      legacy = this.db
        .query<LegacyRow, [number]>("SELECT id, data_json FROM player_profile WHERE id = 1;")
        .get(1) ?? null;
    } catch {
      legacy = null; // 结构异常：按空库处理，直接重建
    }

    this.db.exec("DROP TABLE IF EXISTS player_profile;");
    this.db.exec(`
      CREATE TABLE IF NOT EXISTS player_profile (
        player_id TEXT PRIMARY KEY,
        data_json TEXT NOT NULL,
        updated_at TEXT NOT NULL DEFAULT (datetime('now'))
      );
    `);
    if (legacy?.data_json) {
      this.db
        .query(
          `INSERT INTO player_profile (player_id, data_json, updated_at)
           VALUES (?, ?, datetime('now'));`,
        )
        .run(LEGACY_PLAYER_ID, legacy.data_json);
      console.log(
        `[player-profile] migrated legacy single-row profile → player_id="${LEGACY_PLAYER_ID}" (首个真实玩家画像请求时认领)`,
      );
    }
  }

  /** 保存某玩家画像（playerId 缺省 → legacy 玩家键）。 */
  save(profile: PlayerProfile, playerId?: string): void {
    const id = playerId ?? LEGACY_PLAYER_ID;
    const json = JSON.stringify(profile);
    this.db
      .query(
        `INSERT INTO player_profile (player_id, data_json, updated_at)
         VALUES (?, ?, datetime('now'))
         ON CONFLICT(player_id) DO UPDATE SET data_json = excluded.data_json, updated_at = excluded.updated_at;`,
      )
      .run(id, json);
  }

  /** 读取某玩家画像（无数据返回 null；playerId 缺省 → legacy 玩家键）。 */
  load(playerId?: string): PlayerProfile | null {
    const id = playerId ?? LEGACY_PLAYER_ID;
    const row = this.db
      .query<ProfileRow, [string]>("SELECT * FROM player_profile WHERE player_id = ?;")
      .get(id);
    return row ? (JSON.parse(row.data_json) as PlayerProfile) : null;
  }

  /**
   * 读取某玩家画像；该玩家无数据但 `_legacy` 有数据时**认领** legacy 行
   * （M2 记忆拆分同范式：单机升级无感，联机下首个出现的玩家接手旧画像）。
   * 认领后 legacy 行删除，避免两个玩家共享同一份画像。
   */
  loadOrClaim(playerId?: string): PlayerProfile | null {
    const id = playerId ?? LEGACY_PLAYER_ID;
    const own = this.load(id);
    if (own) return own;
    if (id === LEGACY_PLAYER_ID) return null;

    const legacy = this.load(LEGACY_PLAYER_ID);
    if (!legacy) return null;

    this.save(legacy, id);
    this.deletePlayer(LEGACY_PLAYER_ID);
    console.log(
      `[player-profile] player ${id} claimed the legacy profile (旧单玩家画像迁移完成)`,
    );
    return legacy;
  }

  /** 库中全部玩家 ID（含 `_legacy`，按更新时间升序）。 */
  listPlayerIds(): string[] {
    const rows = this.db
      .query<{ player_id: string }, []>(
        "SELECT player_id FROM player_profile ORDER BY updated_at ASC;",
      )
      .all();
    return rows.map((r) => r.player_id);
  }

  /** 删除某玩家画像（认领迁移 / 测试用）。 */
  deletePlayer(playerId: string): void {
    this.db.query("DELETE FROM player_profile WHERE player_id = ?;").run(playerId);
  }

  // ------------------------------------------------------------------
  // Layer-level update helpers
  // ------------------------------------------------------------------

  updateStatic(staticLayer: PlayerProfile["static"], playerId?: string): void {
    const profile = this.loadOrCreateEmpty(playerId);
    profile.static = { ...staticLayer };
    this.save(profile, playerId);
  }

  appendDailyActivity(activity: DailyActivity, playerId?: string): void {
    const profile = this.loadOrCreateEmpty(playerId);
    // Replace any existing entry with the same date; otherwise add.
    const others = profile.behavior.dailyActivities.filter((a) => a.date !== activity.date);
    others.push(activity);
    // Keep the latest MAX_DAILY_ACTIVITIES entries by date desc.
    others.sort((a, b) => (a.date < b.date ? 1 : a.date > b.date ? -1 : 0));
    profile.behavior.dailyActivities = others.slice(0, MAX_DAILY_ACTIVITIES);
    this.save(profile, playerId);
  }

  updatePreferences(prefs: PlayerProfile["preferences"], playerId?: string): void {
    const profile = this.loadOrCreateEmpty(playerId);
    profile.preferences = { ...prefs };
    this.save(profile, playerId);
  }

  updatePersonality(personality: PlayerProfile["personality"], playerId?: string): void {
    const profile = this.loadOrCreateEmpty(playerId);
    profile.personality = { ...personality };
    this.save(profile, playerId);
  }

  updateRelationship(
    npcName: string,
    rel: PlayerProfile["relationships"][string],
    playerId?: string,
  ): void {
    const profile = this.loadOrCreateEmpty(playerId);
    profile.relationships = { ...profile.relationships, [npcName]: { ...rel } };
    this.save(profile, playerId);
  }

  appendBeatHistory(entry: BeatHistoryEntry, playerId?: string): void {
    const profile = this.loadOrCreateEmpty(playerId);
    // Avoid exact duplicates (same beatId) — overwrite if present.
    const others = profile.story.completedBeats.filter((b) => b.beatId !== entry.beatId);
    others.push(entry);
    // Keep only the latest MAX_BEAT_HISTORY entries by insertion order
    // (most recent are at the end since callers append in chronological order).
    if (others.length > MAX_BEAT_HISTORY) {
      profile.story.completedBeats = others.slice(others.length - MAX_BEAT_HISTORY);
    } else {
      profile.story.completedBeats = others;
    }
    this.save(profile, playerId);
  }

  addRecurringTrope(trope: string, playerId?: string): void {
    const profile = this.loadOrCreateEmpty(playerId);
    if (!profile.story.recurringTropes.includes(trope)) {
      profile.story.recurringTropes = [...profile.story.recurringTropes, trope];
      this.save(profile, playerId);
    }
  }

  close(): void {
    this.db.close();
  }

  // ------------------------------------------------------------------
  // Internal helpers
  // ------------------------------------------------------------------

  /**
   * Loads the existing profile, or returns a fresh empty profile if none
   * exists yet. Used by all `updateX`/`appendX` methods so they are
   * safe to call before `initProfile`/`save` has populated the row.
   */
  private loadOrCreateEmpty(playerId?: string): PlayerProfile {
    const existing = this.loadOrClaim(playerId);
    if (existing) return existing;
    return emptyProfile();
  }
}

/**
 * Constructs an empty PlayerProfile with all required fields initialized
 * to sensible zero values. Used when an `updateX` method is called on a
 * fresh database (no `initProfile` has been invoked yet).
 */
export function emptyProfile(): PlayerProfile {
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
