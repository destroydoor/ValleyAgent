// BeatStore — SQLite persistence for narrative director beats (Task 2).
// Uses Bun's built-in `bun:sqlite` (no native compilation required).
// Spec: docs/superpowers/specs/2026-07-21-narrative-director-design.md §3, §8.3

import { Database } from "bun:sqlite";
import type { Beat, BeatStatus, ReActStep } from "./types";

interface BeatRow {
  seq: number;
  id: string;
  npc_name: string;
  trigger_time: string;
  window_end: string;
  directive: string;
  context_json: string;
  status: string;
  react_steps_json: string | null;
  created_at: string;
}

interface CountRow {
  c: number;
}

/**
 * Persists Beat entities to SQLite. Single-writer safe (WAL mode).
 * Each instance owns its own Database handle; call close() to release.
 *
 * Schema note: `seq INTEGER PRIMARY KEY AUTOINCREMENT` provides deterministic
 * insertion ordering (used by listRecent) since `datetime('now')` has only
 * second-level precision and rapid inserts would otherwise tie.
 */
export class BeatStore {
  private readonly db: Database;

  constructor(dbPath: string) {
    this.db = new Database(dbPath, { create: true });
    // WAL mode for better concurrent-read behavior (spec §8.3).
    this.db.exec("PRAGMA journal_mode = WAL;");
    this.db.exec("PRAGMA synchronous = NORMAL;");
  }

  init(): void {
    this.db.exec(`
      CREATE TABLE IF NOT EXISTS beats (
        seq INTEGER PRIMARY KEY AUTOINCREMENT,
        id TEXT NOT NULL UNIQUE,
        npc_name TEXT NOT NULL,
        trigger_time TEXT NOT NULL,
        window_end TEXT NOT NULL,
        directive TEXT NOT NULL,
        context_json TEXT NOT NULL,
        status TEXT NOT NULL DEFAULT 'scheduled',
        react_steps_json TEXT,
        created_at TEXT NOT NULL DEFAULT (datetime('now'))
      );
      CREATE INDEX IF NOT EXISTS idx_beats_id ON beats(id);
      CREATE INDEX IF NOT EXISTS idx_beats_status ON beats(status);
      CREATE INDEX IF NOT EXISTS idx_beats_npc ON beats(npc_name);
    `);
  }

  save(beat: Beat): void {
    const contextJson = JSON.stringify(beat.context);
    const reactStepsJson = beat.reactSteps !== undefined ? JSON.stringify(beat.reactSteps) : null;
    this.db
      .query(
        `INSERT INTO beats (id, npc_name, trigger_time, window_end, directive, context_json, status, react_steps_json)
         VALUES (?, ?, ?, ?, ?, ?, ?, ?)
         ON CONFLICT(id) DO UPDATE SET
           npc_name = excluded.npc_name,
           trigger_time = excluded.trigger_time,
           window_end = excluded.window_end,
           directive = excluded.directive,
           context_json = excluded.context_json,
           status = excluded.status,
           react_steps_json = excluded.react_steps_json;`,
      )
      .run(
        beat.id,
        beat.npcName,
        beat.triggerTime,
        beat.windowEnd,
        beat.directive,
        contextJson,
        beat.status,
        reactStepsJson,
      );
  }

  getById(id: string): Beat | null {
    const row = this.db
      .query<BeatRow, [string]>("SELECT * FROM beats WHERE id = ?;")
      .get(id);
    return row ? this.deserialize(row) : null;
  }

  updateStatus(id: string, status: BeatStatus): void {
    this.db
      .query("UPDATE beats SET status = ? WHERE id = ?;")
      .run(status, id);
  }

  listScheduled(): Beat[] {
    return this.listByStatus("scheduled");
  }

  listActive(): Beat[] {
    return this.listByStatus("active");
  }

  private listByStatus(status: BeatStatus): Beat[] {
    const rows = this.db
      .query<BeatRow, [BeatStatus]>("SELECT * FROM beats WHERE status = ? ORDER BY seq ASC;")
      .all(status);
    return rows.map((r) => this.deserialize(r));
  }

  listRecent(limit: number): Beat[] {
    const rows = this.db
      .query<BeatRow, [number]>("SELECT * FROM beats ORDER BY seq DESC LIMIT ?;")
      .all(limit);
    return rows.map((r) => this.deserialize(r));
  }

  listByNpc(npcName: string): Beat[] {
    const rows = this.db
      .query<BeatRow, [string]>("SELECT * FROM beats WHERE npc_name = ? ORDER BY seq DESC;")
      .all(npcName);
    return rows.map((r) => this.deserialize(r));
  }

  countActiveByNpc(npcName: string): number {
    const row = this.db
      .query<CountRow, [string, string]>("SELECT COUNT(*) AS c FROM beats WHERE npc_name = ? AND status = ?;")
      .get(npcName, "active");
    return row ? row.c : 0;
  }

  countActiveTotal(): number {
    const row = this.db
      .query<CountRow, [string]>("SELECT COUNT(*) AS c FROM beats WHERE status = ?;")
      .get("active");
    return row ? row.c : 0;
  }

  delete(id: string): void {
    this.db.query("DELETE FROM beats WHERE id = ?;").run(id);
  }

  close(): void {
    this.db.close();
  }

  private deserialize(row: BeatRow): Beat {
    const context = JSON.parse(row.context_json) as Beat["context"];
    // exactOptionalPropertyTypes: true forbids assigning `undefined` to optional
    // properties — only set reactSteps when we actually have steps to store.
    const beat: Beat = {
      id: row.id,
      npcName: row.npc_name,
      triggerTime: row.trigger_time,
      windowEnd: row.window_end,
      directive: row.directive,
      context,
      status: row.status as BeatStatus,
    };
    if (row.react_steps_json !== null) {
      beat.reactSteps = JSON.parse(row.react_steps_json) as ReActStep[];
    }
    return beat;
  }
}
