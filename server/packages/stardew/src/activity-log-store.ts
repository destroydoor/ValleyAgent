// ActivityLogStore — SQLite persistence for player daily activities, farm snapshots,
// and milestone firing history (Task 3).
// Uses Bun's built-in `bun:sqlite` (no native compilation required).
// Spec: docs/superpowers/specs/2026-07-21-narrative-director-design.md §4, §6.6

import { Database } from "bun:sqlite";
import type { DailyActivity, PlayStyle } from "./types";

/**
 * Numeric fields of DailyActivity that can be aggregated with median/sum.
 * Keep this list in sync with the DailyActivity interface.
 */
export type NumericField =
  | "fishingMinutes"
  | "farmingMinutes"
  | "miningMinutes"
  | "foragingMinutes"
  | "socialMinutes"
  | "combatMinutes"
  | "fishCaught"
  | "cropsHarvested"
  | "itemsShipped"
  | "itemsForaged"
  | "monstersKilled";

interface DailyActivityRow {
  seq: number;
  date: string;
  data_json: string;
}

interface FarmSnapshotRow {
  seq: number;
  date: string;
  play_styles_json: string;
}

interface CountRow {
  c: number;
}

interface NumericValueRow {
  v: number | null;
}

interface LatestDateRow {
  d: string | null;
}

/**
 * Persists player daily activity logs, periodic farm snapshots, and milestone
 * firing history to SQLite. Single-writer safe (WAL mode).
 *
 * Design notes:
 * - `seq INTEGER PRIMARY KEY AUTOINCREMENT` provides deterministic ordering
 *   for `listRecent` and `getLatestFarmSnapshot` (datetime('now') has only
 *   second-level precision).
 * - `date` is the canonical "YYYY-MM-DD" game-day string (C# supplies it).
 * - `saveDaily` and `saveFarmSnapshot` are idempotent on `date` (UPSERT).
 * - `getMedian` / `getSum` operate over the N most recent rows by date desc.
 * - `hasMilestoneFiredWithin` uses SQLite `date('now', '-N days')` for the
 *   rolling window, so day-granularity boundaries match wall-clock dates.
 */
export class ActivityLogStore {
  private readonly db: Database;

  constructor(dbPath: string) {
    this.db = new Database(dbPath, { create: true });
    // WAL mode for better concurrent-read behavior (spec §8.3).
    this.db.exec("PRAGMA journal_mode = WAL;");
    this.db.exec("PRAGMA synchronous = NORMAL;");
  }

  init(): void {
    this.db.exec(`
      CREATE TABLE IF NOT EXISTS daily_activities (
        seq INTEGER PRIMARY KEY AUTOINCREMENT,
        date TEXT NOT NULL UNIQUE,
        data_json TEXT NOT NULL,
        created_at TEXT NOT NULL DEFAULT (datetime('now'))
      );
      CREATE INDEX IF NOT EXISTS idx_daily_date ON daily_activities(date);

      CREATE TABLE IF NOT EXISTS farm_snapshots (
        seq INTEGER PRIMARY KEY AUTOINCREMENT,
        date TEXT NOT NULL UNIQUE,
        play_styles_json TEXT NOT NULL,
        created_at TEXT NOT NULL DEFAULT (datetime('now'))
      );
      CREATE INDEX IF NOT EXISTS idx_farm_date ON farm_snapshots(date);

      CREATE TABLE IF NOT EXISTS milestone_fired (
        type TEXT NOT NULL,
        date TEXT NOT NULL,
        fired_at TEXT NOT NULL DEFAULT (datetime('now')),
        PRIMARY KEY (type, date)
      );
      CREATE INDEX IF NOT EXISTS idx_milestone_type ON milestone_fired(type);
      CREATE INDEX IF NOT EXISTS idx_milestone_date ON milestone_fired(date);
    `);
  }

  // ------------------------------------------------------------------
  // DailyActivity
  // ------------------------------------------------------------------

  saveDaily(activity: DailyActivity): void {
    const dataJson = JSON.stringify(activity);
    this.db
      .query(
        `INSERT INTO daily_activities (date, data_json)
         VALUES (?, ?)
         ON CONFLICT(date) DO UPDATE SET data_json = excluded.data_json;`,
      )
      .run(activity.date, dataJson);
  }

  getDaily(date: string): DailyActivity | null {
    const row = this.db
      .query<DailyActivityRow, [string]>("SELECT * FROM daily_activities WHERE date = ?;")
      .get(date);
    return row ? (JSON.parse(row.data_json) as DailyActivity) : null;
  }

  listRecent(days: number): DailyActivity[] {
    const rows = this.db
      .query<DailyActivityRow, [number]>(
        "SELECT * FROM daily_activities ORDER BY date DESC, seq DESC LIMIT ?;",
      )
      .all(days);
    return rows.map((r) => JSON.parse(r.data_json) as DailyActivity);
  }

  // ------------------------------------------------------------------
  // FarmSnapshot
  // ------------------------------------------------------------------

  saveFarmSnapshot(date: string, playStyles: PlayStyle[]): void {
    const json = JSON.stringify(playStyles);
    this.db
      .query(
        `INSERT INTO farm_snapshots (date, play_styles_json)
         VALUES (?, ?)
         ON CONFLICT(date) DO UPDATE SET play_styles_json = excluded.play_styles_json;`,
      )
      .run(date, json);
  }

  getLatestFarmSnapshot(): { date: string; playStyles: PlayStyle[] } | null {
    const row = this.db
      .query<FarmSnapshotRow, [number]>(
        "SELECT * FROM farm_snapshots ORDER BY date DESC, seq DESC LIMIT 1;",
      )
      .get(1);
    if (!row) return null;
    return {
      date: row.date,
      playStyles: JSON.parse(row.play_styles_json) as PlayStyle[],
    };
  }

  // ------------------------------------------------------------------
  // Numeric aggregates (median / sum)
  // ------------------------------------------------------------------

  /**
   * Returns the median of `field` over the `days` most recent daily activities.
   * Returns 0 when no rows exist. For even counts, averages the two middle values.
   */
  getMedian(field: NumericField, days: number): number {
    const values = this.collectNumeric(field, days);
    if (values.length === 0) return 0;
    const sorted = [...values].sort((a, b) => a - b);
    const mid = Math.floor(sorted.length / 2);
    if (sorted.length % 2 === 1) {
      // noUncheckedIndexedAccess: guard with optional chaining fallback to 0.
      return sorted[mid] ?? 0;
    }
    const lo = sorted[mid - 1] ?? 0;
    const hi = sorted[mid] ?? 0;
    return (lo + hi) / 2;
  }

  /**
   * Returns the sum of `field` over the `days` most recent daily activities.
   * Returns 0 when no rows exist.
   */
  getSum(field: NumericField, days: number): number {
    const values = this.collectNumeric(field, days);
    return values.reduce((acc, v) => acc + v, 0);
  }

  /**
   * Pulls the numeric values for `field` from the `days` most recent rows.
   * NULL / missing values are coerced to 0 to keep aggregates stable.
   *
   * Implementation note: SQLite's JSON functions (`->>` / `->`) require SQLite
   * ≥ 3.38; `bun:sqlite` ships a recent enough build. We project the JSON
   * column directly and parse in TS to avoid depending on JSON1 column-naming
   * rules across SQL flavors.
   */
  private collectNumeric(field: NumericField, days: number): number[] {
    const rows = this.db
      .query<NumericValueRow, [number]>(
        `SELECT json_extract(data_json, '$.${field}') AS v
         FROM daily_activities
         ORDER BY date DESC, seq DESC
         LIMIT ?;`,
      )
      .all(days);
    return rows.map((r) => (r.v === null ? 0 : r.v));
  }

  // ------------------------------------------------------------------
  // Milestones
  // ------------------------------------------------------------------

  markMilestoneFired(type: string, date: string): void {
    this.db
      .query(
        `INSERT INTO milestone_fired (type, date)
         VALUES (?, ?)
         ON CONFLICT(type, date) DO NOTHING;`,
      )
      .run(type, date);
  }

  hasMilestoneFired(type: string, date: string): boolean {
    const row = this.db
      .query<CountRow, [string, string]>(
        "SELECT COUNT(*) AS c FROM milestone_fired WHERE type = ? AND date = ?;",
      )
      .get(type, date);
    return row ? row.c > 0 : false;
  }

  /**
   * Returns true if any milestone of `type` was fired within the last `days`
   * days (inclusive). Uses SQLite's `date('now', '-N days')` so the window
   * boundary is anchored to wall-clock day boundaries.
   *
   * Comparison works because both `milestone_fired.date` (C#-supplied
   * "YYYY-MM-DD") and `date('now', ...)` produce lexicographically sortable
   * strings — string `>=` therefore matches calendar `>=`.
   */
  hasMilestoneFiredWithin(type: string, days: number): boolean {
    const row = this.db
      .query<CountRow, [string, string]>(
        `SELECT COUNT(*) AS c FROM milestone_fired
         WHERE type = ? AND date >= date('now', ?);`,
      )
      .get(type, `-${days} days`);
    return row ? row.c > 0 : false;
  }

  close(): void {
    this.db.close();
  }

  // Re-exported for tests that need to peek at the latest stored date.
  // Not part of the public store API used by Director.
  /** @internal */
  _latestActivityDate(): string | null {
    const row = this.db
      .query<LatestDateRow, [number]>(
        "SELECT date AS d FROM daily_activities ORDER BY date DESC, seq DESC LIMIT 1;",
      )
      .get(1);
    return row?.d ?? null;
  }
}
