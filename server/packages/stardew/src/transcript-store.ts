// TranscriptStore — SQLite 全量留痕存储（Phase 1 E1-1）。
// 三张表：agent_runs / agent_turns / director_runs，记录 NPC 决策与导演
// 调度的完整 trace，供事后回放/审计/调试（"它当时为什么这么想"）。
// 使用 Bun 内置 `bun:sqlite`，无需原生编译。
// Spec: docs/design/2026-08-01-memory-narrative-extensibility.md §1
//
// 关键约束：
// - 写方法 best-effort，绝不抛异常 —— 留痕失败不得打断对话/导演主流程，
//   失败时 console.warn 并吞掉错误。
// - 仅 init() / close() 允许抛异常（构造期/关闭期属于调用方职责）。
// - 时间戳一律 ISO 8601 UTC（new Date().toISOString()）；created_at 走
//   SQLite datetime('now')。
// - 数组字段 JSON.stringify 落库，读回 JSON.parse 还原。

import { Database } from "bun:sqlite";
import type {
  AgentRunRecord,
  AgentRunStatus,
  AgentRunTrigger,
  AgentTurnRecord,
  DirectorRunRecord,
  DirectorRunStatus,
} from "./transcript-types";

// ---------------------------------------------------------------------------
// 行类型（DB 列名 snake_case → 读取时映射回 camelCase Record）
// ---------------------------------------------------------------------------

interface AgentRunRow {
  run_id: string;
  npc_name: string;
  trigger: string;
  game_date: string | null;
  request_id: string | null;
  beat_id: string | null;
  started_at: string;
  finished_at: string | null;
  system_prompt_hash: string;
  system_prompt_full: string;
  system_prompt_dynamic: string;
  user_input: string | null;
  final_speech: string | null;
  actions_json: string;
  tool_calls_json: string;
  validation_valid: number;
  validation_issues: string | null;
  fallback: number;
  fallback_reason: string | null;
  tokens_in: number | null;
  tokens_out: number | null;
  latency_ms: number | null;
  status: string;
  error_json: string | null;
  created_at: string;
}

interface AgentTurnRow {
  seq: number;
  run_id: string;
  turn_index: number;
  llm_raw_output: string | null;
  tool_calls_json: string;
  tool_results_json: string;
  game_date: string | null;
  created_at: string;
}

interface DirectorRunRow {
  run_id: string;
  game_date: string;
  trigger: string;
  prompt_full: string;
  llm_raw_output: string | null;
  produced_beats_json: string;
  dropped_beats_json: string;
  empty_result: number;
  status: string;
  error_json: string | null;
  created_at: string;
}

interface TableNameRow {
  name: string;
}

interface CountRow {
  c: number;
}

// ---------------------------------------------------------------------------
// 摘要（summary() 返回）—— 轻量统计，不拉全量行
// ---------------------------------------------------------------------------

export interface TranscriptSummary {
  totalRuns: number;
  completedRuns: number;
  errorRuns: number;
  fallbackRuns: number;
  totalTurns: number;
}

/**
 * 全量留痕存储。单写线程安全（WAL 模式）。每个实例独占一个 Database
 * 句柄；用完调 close() 释放。
 *
 * 用法：
 *   const store = new TranscriptStore(path);
 *   store.init();
 *   store.recordAgentRun(rec);   // 不抛
 *   store.close();               // 可抛
 */
export class TranscriptStore {
  private readonly db: Database;

  constructor(dbPath: string) {
    this.db = new Database(dbPath, { create: true });
    // WAL 提升并发读性能（spec §1）。
    this.db.exec("PRAGMA journal_mode = WAL;");
    this.db.exec("PRAGMA synchronous = NORMAL;");
  }

  init(): void {
    this.db.exec(`
      CREATE TABLE IF NOT EXISTS agent_runs (
        run_id TEXT PRIMARY KEY, npc_name TEXT NOT NULL, trigger TEXT NOT NULL,
        game_date TEXT, request_id TEXT, beat_id TEXT,
        started_at TEXT NOT NULL, finished_at TEXT,
        system_prompt_hash TEXT NOT NULL, system_prompt_full TEXT NOT NULL, system_prompt_dynamic TEXT NOT NULL,
        user_input TEXT, final_speech TEXT,
        actions_json TEXT NOT NULL DEFAULT '[]', tool_calls_json TEXT NOT NULL DEFAULT '[]',
        validation_valid INTEGER NOT NULL DEFAULT 1, validation_issues TEXT,
        fallback INTEGER NOT NULL DEFAULT 0, fallback_reason TEXT,
        tokens_in INTEGER, tokens_out INTEGER, latency_ms INTEGER,
        status TEXT NOT NULL, error_json TEXT,
        created_at TEXT NOT NULL DEFAULT (datetime('now'))
      );
      CREATE INDEX IF NOT EXISTS idx_agent_runs_date_npc ON agent_runs(game_date, npc_name);
      CREATE INDEX IF NOT EXISTS idx_agent_runs_npc ON agent_runs(npc_name);

      CREATE TABLE IF NOT EXISTS agent_turns (
        seq INTEGER PRIMARY KEY AUTOINCREMENT, run_id TEXT NOT NULL,
        turn_index INTEGER NOT NULL, llm_raw_output TEXT,
        tool_calls_json TEXT NOT NULL DEFAULT '[]', tool_results_json TEXT NOT NULL DEFAULT '[]',
        game_date TEXT,
        created_at TEXT NOT NULL DEFAULT (datetime('now'))
      );
      CREATE INDEX IF NOT EXISTS idx_agent_turns_run ON agent_turns(run_id);

      CREATE TABLE IF NOT EXISTS director_runs (
        run_id TEXT PRIMARY KEY, game_date TEXT NOT NULL, trigger TEXT NOT NULL,
        prompt_full TEXT NOT NULL, llm_raw_output TEXT,
        produced_beats_json TEXT NOT NULL DEFAULT '[]', dropped_beats_json TEXT NOT NULL DEFAULT '[]',
        empty_result INTEGER NOT NULL DEFAULT 0, status TEXT NOT NULL, error_json TEXT,
        created_at TEXT NOT NULL DEFAULT (datetime('now'))
      );
      CREATE INDEX IF NOT EXISTS idx_director_runs_date ON director_runs(game_date);
    `);
  }

  // -------------------------------------------------------------------------
  // 写方法 —— best-effort，绝不抛
  // -------------------------------------------------------------------------

  /** 写入/更新 agent_runs（按 run_id UPSERT）。失败仅 console.warn。 */
  recordAgentRun(rec: AgentRunRecord): void {
    try {
      const actionsJson = JSON.stringify(rec.actions);
      const toolCallsJson = JSON.stringify(rec.toolCalls);
      const validationIssues = rec.validation.issues ?? null;
      const fallbackReason = rec.fallback.reason ?? null;
      const tokensIn = rec.tokens.in ?? null;
      const tokensOut = rec.tokens.out ?? null;
      const errorJson = rec.error ?? null;
      this.db
        .query(
          `INSERT INTO agent_runs (
             run_id, npc_name, trigger, game_date, request_id, beat_id,
             started_at, finished_at,
             system_prompt_hash, system_prompt_full, system_prompt_dynamic,
             user_input, final_speech,
             actions_json, tool_calls_json,
             validation_valid, validation_issues,
             fallback, fallback_reason,
             tokens_in, tokens_out, latency_ms,
             status, error_json
           )
           VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
           ON CONFLICT(run_id) DO UPDATE SET
             npc_name = excluded.npc_name,
             trigger = excluded.trigger,
             game_date = excluded.game_date,
             request_id = excluded.request_id,
             beat_id = excluded.beat_id,
             started_at = excluded.started_at,
             finished_at = excluded.finished_at,
             system_prompt_hash = excluded.system_prompt_hash,
             system_prompt_full = excluded.system_prompt_full,
             system_prompt_dynamic = excluded.system_prompt_dynamic,
             user_input = excluded.user_input,
             final_speech = excluded.final_speech,
             actions_json = excluded.actions_json,
             tool_calls_json = excluded.tool_calls_json,
             validation_valid = excluded.validation_valid,
             validation_issues = excluded.validation_issues,
             fallback = excluded.fallback,
             fallback_reason = excluded.fallback_reason,
             tokens_in = excluded.tokens_in,
             tokens_out = excluded.tokens_out,
             latency_ms = excluded.latency_ms,
             status = excluded.status,
             error_json = excluded.error_json;`,
        )
        .run(
          rec.runId,
          rec.npcName,
          rec.trigger,
          rec.gameDate ?? null,
          rec.requestId ?? null,
          rec.beatId ?? null,
          rec.startedAt,
          rec.finishedAt ?? null,
          rec.systemPromptHash,
          rec.systemPromptFull,
          rec.systemPromptDynamic,
          rec.userInput ?? null,
          rec.finalSpeech ?? null,
          actionsJson,
          toolCallsJson,
          rec.validation.valid ? 1 : 0,
          validationIssues,
          rec.fallback.flag ? 1 : 0,
          fallbackReason,
          tokensIn,
          tokensOut,
          rec.latencyMs ?? null,
          rec.status,
          errorJson,
        );
    } catch (err) {
      // 留痕失败不得阻断主流程：吞掉并告警。
      console.warn(`[transcript] write failed: ${err}`);
    }
  }

  /** 追加 agent_turns 一行（同一 run_id 多轮）。失败仅 console.warn。 */
  recordAgentTurn(rec: AgentTurnRecord): void {
    try {
      const toolCallsJson = JSON.stringify(rec.toolCalls);
      const toolResultsJson = JSON.stringify(rec.toolResults);
      this.db
        .query(
          `INSERT INTO agent_turns (
             run_id, turn_index, llm_raw_output,
             tool_calls_json, tool_results_json, game_date
           )
           VALUES (?, ?, ?, ?, ?, ?);`,
        )
        .run(
          rec.runId,
          rec.turnIndex,
          rec.llmRawOutput ?? null,
          toolCallsJson,
          toolResultsJson,
          rec.gameDate ?? null,
        );
    } catch (err) {
      console.warn(`[transcript] write failed: ${err}`);
    }
  }

  /** 写入/更新 director_runs（按 run_id UPSERT）。失败仅 console.warn。 */
  recordDirectorRun(rec: DirectorRunRecord): void {
    try {
      const producedJson = JSON.stringify(rec.producedBeats);
      const droppedJson = JSON.stringify(rec.droppedBeats);
      const errorJson = rec.error ?? null;
      this.db
        .query(
          `INSERT INTO director_runs (
             run_id, game_date, trigger, prompt_full, llm_raw_output,
             produced_beats_json, dropped_beats_json,
             empty_result, status, error_json
           )
           VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
           ON CONFLICT(run_id) DO UPDATE SET
             game_date = excluded.game_date,
             trigger = excluded.trigger,
             prompt_full = excluded.prompt_full,
             llm_raw_output = excluded.llm_raw_output,
             produced_beats_json = excluded.produced_beats_json,
             dropped_beats_json = excluded.dropped_beats_json,
             empty_result = excluded.empty_result,
             status = excluded.status,
             error_json = excluded.error_json;`,
        )
        .run(
          rec.runId,
          rec.gameDate,
          rec.trigger,
          rec.promptFull,
          rec.llmRawOutput ?? null,
          producedJson,
          droppedJson,
          rec.emptyResult ? 1 : 0,
          rec.status,
          errorJson,
        );
    } catch (err) {
      console.warn(`[transcript] write failed: ${err}`);
    }
  }

  // -------------------------------------------------------------------------
  // 读方法 —— 供回放/测试用
  // -------------------------------------------------------------------------

  /** 查询某 NPC 的 agent_runs，可按 game_date 过滤。按 started_at 升序。 */
  getAgentRuns(npcName: string, gameDate?: string): AgentRunRecord[] {
    if (gameDate === undefined) {
      const rows = this.db
        .query<AgentRunRow, [string]>(
          "SELECT * FROM agent_runs WHERE npc_name = ? ORDER BY started_at ASC;",
        )
        .all(npcName);
      return rows.map((r) => this.deserializeAgentRun(r));
    }
    const rows = this.db
      .query<AgentRunRow, [string, string]>(
        "SELECT * FROM agent_runs WHERE npc_name = ? AND game_date = ? ORDER BY started_at ASC;",
      )
      .all(npcName, gameDate);
    return rows.map((r) => this.deserializeAgentRun(r));
  }

  /** 查询某 run_id 下的所有 agent_turns，按 turn_index 升序。 */
  getAgentTurns(runId: string): AgentTurnRecord[] {
    const rows = this.db
      .query<AgentTurnRow, [string]>(
        "SELECT * FROM agent_turns WHERE run_id = ? ORDER BY turn_index ASC, seq ASC;",
      )
      .all(runId);
    return rows.map((r) => this.deserializeAgentTurn(r));
  }

  /** 查询导演运行记录，可按 game_date 过滤。按 created_at 升序。 */
  getDirectorRuns(gameDate?: string): DirectorRunRecord[] {
    if (gameDate === undefined) {
      const rows = this.db
        .query<DirectorRunRow, []>(
          "SELECT * FROM director_runs ORDER BY created_at ASC;",
        )
        .all();
      return rows.map((r) => this.deserializeDirectorRun(r));
    }
    const rows = this.db
      .query<DirectorRunRow, [string]>(
        "SELECT * FROM director_runs WHERE game_date = ? ORDER BY created_at ASC;",
      )
      .all(gameDate);
    return rows.map((r) => this.deserializeDirectorRun(r));
  }

  /** 轻量统计：某 NPC（可按日）的运行计数。 */
  summary(npcName: string, gameDate?: string): TranscriptSummary {
    // 用动态参数数组避免 spread + 额外实参在 tuple 类型上的摩擦。
    const baseParams = gameDate === undefined ? [npcName] : [npcName, gameDate];
    const where = gameDate === undefined ? "WHERE npc_name = ?" : "WHERE npc_name = ? AND game_date = ?";

    const totalRow = this.db
      .query<CountRow, string[]>(`SELECT COUNT(*) AS c FROM agent_runs ${where};`)
      .get(...baseParams);
    const totalRuns = totalRow?.c ?? 0;

    const completedRow = this.db
      .query<CountRow, string[]>(`SELECT COUNT(*) AS c FROM agent_runs ${where} AND status = ?;`)
      .get(...baseParams, "completed");
    const errorRow = this.db
      .query<CountRow, string[]>(`SELECT COUNT(*) AS c FROM agent_runs ${where} AND status = ?;`)
      .get(...baseParams, "error");
    const fallbackRow = this.db
      .query<CountRow, string[]>(`SELECT COUNT(*) AS c FROM agent_runs ${where} AND status = ?;`)
      .get(...baseParams, "fallback");

    // agent_turns 没有 npc_name 列，按 run_id 子查询关联。
    const turnWhere =
      gameDate === undefined
        ? "WHERE run_id IN (SELECT run_id FROM agent_runs WHERE npc_name = ?)"
        : "WHERE run_id IN (SELECT run_id FROM agent_runs WHERE npc_name = ? AND game_date = ?)";
    const turnRow = this.db
      .query<CountRow, string[]>(`SELECT COUNT(*) AS c FROM agent_turns ${turnWhere};`)
      .get(...baseParams);

    return {
      totalRuns,
      completedRuns: completedRow?.c ?? 0,
      errorRuns: errorRow?.c ?? 0,
      fallbackRuns: fallbackRow?.c ?? 0,
      totalTurns: turnRow?.c ?? 0,
    };
  }

  // -------------------------------------------------------------------------
  // 关闭
  // -------------------------------------------------------------------------

  close(): void {
    // 主动 checkpoint 把 WAL 刷回主库，避免残留 -wal 文件。
    this.db.exec("PRAGMA wal_checkpoint(PASSIVE);");
    this.db.close();
  }

  // -------------------------------------------------------------------------
  // 反序列化（DB snake_case → Record camelCase，可选字段按需挂载）
  // -------------------------------------------------------------------------

  private deserializeAgentRun(row: AgentRunRow): AgentRunRecord {
    // exactOptionalPropertyTypes: true —— 可选字段仅在非 null 时挂载，
    // 避免把 undefined 赋给可选属性。
    const rec: AgentRunRecord = {
      runId: row.run_id,
      npcName: row.npc_name,
      trigger: row.trigger as AgentRunTrigger,
      startedAt: row.started_at,
      systemPromptHash: row.system_prompt_hash,
      systemPromptFull: row.system_prompt_full,
      systemPromptDynamic: row.system_prompt_dynamic,
      actions: JSON.parse(row.actions_json) as unknown[],
      toolCalls: JSON.parse(row.tool_calls_json) as unknown[],
      validation: {
        valid: row.validation_valid === 1,
      },
      fallback: {
        flag: row.fallback === 1,
      },
      tokens: {},
      status: row.status as AgentRunStatus,
    };
    if (row.game_date !== null) rec.gameDate = row.game_date;
    if (row.request_id !== null) rec.requestId = row.request_id;
    if (row.beat_id !== null) rec.beatId = row.beat_id;
    if (row.finished_at !== null) rec.finishedAt = row.finished_at;
    if (row.user_input !== null) rec.userInput = row.user_input;
    if (row.final_speech !== null) rec.finalSpeech = row.final_speech;
    if (row.validation_issues !== null) rec.validation.issues = row.validation_issues;
    if (row.fallback_reason !== null) rec.fallback.reason = row.fallback_reason;
    if (row.tokens_in !== null) rec.tokens.in = row.tokens_in;
    if (row.tokens_out !== null) rec.tokens.out = row.tokens_out;
    if (row.latency_ms !== null) rec.latencyMs = row.latency_ms;
    if (row.error_json !== null) rec.error = row.error_json;
    return rec;
  }

  private deserializeAgentTurn(row: AgentTurnRow): AgentTurnRecord {
    const rec: AgentTurnRecord = {
      runId: row.run_id,
      turnIndex: row.turn_index,
      toolCalls: JSON.parse(row.tool_calls_json) as unknown[],
      toolResults: JSON.parse(row.tool_results_json) as unknown[],
    };
    if (row.llm_raw_output !== null) rec.llmRawOutput = row.llm_raw_output;
    if (row.game_date !== null) rec.gameDate = row.game_date;
    return rec;
  }

  private deserializeDirectorRun(row: DirectorRunRow): DirectorRunRecord {
    const rec: DirectorRunRecord = {
      runId: row.run_id,
      gameDate: row.game_date,
      trigger: row.trigger,
      promptFull: row.prompt_full,
      producedBeats: JSON.parse(row.produced_beats_json) as unknown[],
      droppedBeats: JSON.parse(row.dropped_beats_json) as unknown[],
      emptyResult: row.empty_result === 1,
      status: row.status as DirectorRunStatus,
    };
    if (row.llm_raw_output !== null) rec.llmRawOutput = row.llm_raw_output;
    if (row.error_json !== null) rec.error = row.error_json;
    return rec;
  }

  /** 仅供测试窥探表是否存在。@internal */
  _listTables(): string[] {
    const rows = this.db
      .query<TableNameRow, []>(
        "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name;",
      )
      .all();
    return rows.map((r) => r.name);
  }
}