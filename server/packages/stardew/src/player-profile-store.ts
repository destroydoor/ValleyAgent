// PlayerProfileStore — SQLite persistence for the five-layer PlayerProfile (Task 4).
// Uses Bun's built-in `bun:sqlite` (no native compilation required).
// Spec: docs/superpowers/specs/2026-07-21-narrative-director-design.md §4

import { Database } from "bun:sqlite";
import type {
  PlayerProfile,
  DailyActivity,
  BeatHistoryEntry,
} from "./types";

interface ProfileRow {
  id: number;
  data_json: string;
  updated_at: string;
}

const MAX_DAILY_ACTIVITIES = 30;
const MAX_BEAT_HISTORY = 50;

/**
 * Persists the single-row PlayerProfile document to SQLite.
 *
 * Design:
 * - `player_profile` is a single-row table (CHECK (id = 1)); the entire
 *   PlayerProfile object is serialized to JSON and stored as `data_json`.
 * - All `updateX`/`appendX` methods perform a read-modify-write cycle: load
 *   the existing profile (or create an empty default if none exists), apply
 *   the mutation, then save. Single-writer safe (WAL mode).
 * - `appendDailyActivity` keeps only the latest 30 entries (by date desc).
 * - `appendBeatHistory` keeps only the latest 50 entries (by insertion order).
 *
 * Why single-row JSON instead of normalized columns? The PlayerProfile is
 * read whole on every Director dayPlan / profile refresh, and
 * mutations are infrequent (once per day at most). A single JSON document
 * avoids schema-migration pain as the profile structure evolves, and is
 * more than fast enough at the expected scale (one player, one row).
 */
export class PlayerProfileStore {
  private readonly db: Database;

  constructor(dbPath: string) {
    this.db = new Database(dbPath, { create: true });
    this.db.exec("PRAGMA journal_mode = WAL;");
    this.db.exec("PRAGMA synchronous = NORMAL;");
  }

  init(): void {
    this.db.exec(`
      CREATE TABLE IF NOT EXISTS player_profile (
        id INTEGER PRIMARY KEY CHECK (id = 1),
        data_json TEXT NOT NULL,
        updated_at TEXT NOT NULL DEFAULT (datetime('now'))
      );
    `);
  }

  save(profile: PlayerProfile): void {
    const json = JSON.stringify(profile);
    this.db
      .query(
        `INSERT INTO player_profile (id, data_json, updated_at)
         VALUES (1, ?, datetime('now'))
         ON CONFLICT(id) DO UPDATE SET data_json = excluded.data_json, updated_at = excluded.updated_at;`,
      )
      .run(json);
  }

  load(): PlayerProfile | null {
    const row = this.db
      .query<ProfileRow, [number]>("SELECT * FROM player_profile WHERE id = 1;")
      .get(1);
    return row ? (JSON.parse(row.data_json) as PlayerProfile) : null;
  }

  // ------------------------------------------------------------------
  // Layer-level update helpers
  // ------------------------------------------------------------------

  updateStatic(staticLayer: PlayerProfile["static"]): void {
    const profile = this.loadOrCreateEmpty();
    profile.static = { ...staticLayer };
    this.save(profile);
  }

  appendDailyActivity(activity: DailyActivity): void {
    const profile = this.loadOrCreateEmpty();
    // Replace any existing entry with the same date; otherwise add.
    const others = profile.behavior.dailyActivities.filter((a) => a.date !== activity.date);
    others.push(activity);
    // Keep the latest MAX_DAILY_ACTIVITIES entries by date desc.
    others.sort((a, b) => (a.date < b.date ? 1 : a.date > b.date ? -1 : 0));
    profile.behavior.dailyActivities = others.slice(0, MAX_DAILY_ACTIVITIES);
    this.save(profile);
  }

  updatePreferences(prefs: PlayerProfile["preferences"]): void {
    const profile = this.loadOrCreateEmpty();
    profile.preferences = { ...prefs };
    this.save(profile);
  }

  updatePersonality(personality: PlayerProfile["personality"]): void {
    const profile = this.loadOrCreateEmpty();
    profile.personality = { ...personality };
    this.save(profile);
  }

  updateRelationship(npcName: string, rel: PlayerProfile["relationships"][string]): void {
    const profile = this.loadOrCreateEmpty();
    profile.relationships = { ...profile.relationships, [npcName]: { ...rel } };
    this.save(profile);
  }

  appendBeatHistory(entry: BeatHistoryEntry): void {
    const profile = this.loadOrCreateEmpty();
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
    this.save(profile);
  }

  addRecurringTrope(trope: string): void {
    const profile = this.loadOrCreateEmpty();
    if (!profile.story.recurringTropes.includes(trope)) {
      profile.story.recurringTropes = [...profile.story.recurringTropes, trope];
      this.save(profile);
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
  private loadOrCreateEmpty(): PlayerProfile {
    const existing = this.load();
    if (existing) return existing;
    return emptyProfile();
  }
}

/**
 * Constructs an empty PlayerProfile with all required fields initialized
 * to sensible zero values. Used when an `updateX` method is called on a
 * fresh database (no `initProfile` has been invoked yet).
 */
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
