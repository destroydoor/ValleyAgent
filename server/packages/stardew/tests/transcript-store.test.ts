// Tests for TranscriptStore — Phase 1 E1-1 全量留痕存储。
// Run: bun test packages/stardew/tests/transcript-store.test.ts
// Spec: docs/design/2026-08-01-memory-narrative-extensibility.md §1

import { test, expect, beforeEach, afterEach, spyOn } from "bun:test";
import { TranscriptStore } from "../src/transcript-store";
import type {
  AgentRunRecord,
  AgentTurnRecord,
  DirectorRunRecord,
} from "../src/transcript-types";
import { rmSync, existsSync } from "node:fs";
import { join } from "node:path";
import { tmpdir } from "node:os";

// ---------------------------------------------------------------------------
// 工厂：构造完整 AgentRunRecord（数组字段默认 []，避免到处补字段）
// ---------------------------------------------------------------------------

function makeAgentRun(overrides: Partial<AgentRunRecord> = {}): AgentRunRecord {
  return {
    runId: "run-1",
    npcName: "Abigail",
    trigger: "dialogue",
    startedAt: "2026-08-01T10:00:00.000Z",
    systemPromptHash: "sha256:abc",
    systemPromptFull: "You are Abigail...",
    systemPromptDynamic: "mood=happy",
    actions: [],
    toolCalls: [],
    validation: { valid: true },
    fallback: { flag: false },
    tokens: {},
    status: "completed",
    ...overrides,
  };
}

let dbPath: string;
let store: TranscriptStore;

beforeEach(() => {
  dbPath = join(
    tmpdir(),
    `transcript-store-test-${Date.now()}-${Math.random().toString(36).slice(2)}.sqlite`,
  );
  store = new TranscriptStore(dbPath);
  store.init();
});

afterEach(() => {
  store.close();
  if (existsSync(dbPath)) {
    rmSync(dbPath, { force: true });
    // WAL/SHM 旁路文件也清理。
    rmSync(`${dbPath}-wal`, { force: true });
    rmSync(`${dbPath}-shm`, { force: true });
  }
});

// ---------------------------------------------------------------------------
// 1. init 创建 3 张表
// ---------------------------------------------------------------------------

test("init creates agent_runs / agent_turns / director_runs tables", () => {
  const tables = store._listTables();
  expect(tables).toContain("agent_runs");
  expect(tables).toContain("agent_turns");
  expect(tables).toContain("director_runs");
});

// ---------------------------------------------------------------------------
// 2. recordAgentRun 往返（数组反序列化）
// ---------------------------------------------------------------------------

test("recordAgentRun round-trip preserves arrays and fields", () => {
  const rec = makeAgentRun({
    runId: "run-rt",
    actions: [{ tool: "speak", args: { text: "hi" } }],
    toolCalls: [{ id: "call_1", name: "speak" }],
    userInput: "你好",
    finalSpeech: "嗨！",
    gameDate: "2026-08-01",
    requestId: "req-1",
    finishedAt: "2026-08-01T10:00:01.000Z",
    latencyMs: 1200,
    tokens: { in: 100, out: 50 },
    validation: { valid: false, issues: "missing speech" },
    fallback: { flag: true, reason: "validator-fail" },
  });
  store.recordAgentRun(rec);

  const runs = store.getAgentRuns("Abigail", "2026-08-01");
  expect(runs).toHaveLength(1);
  const got = runs[0]!;
  expect(got.runId).toBe("run-rt");
  expect(got.trigger).toBe("dialogue");
  expect(got.userInput).toBe("你好");
  expect(got.finalSpeech).toBe("嗨！");
  expect(got.actions).toEqual([{ tool: "speak", args: { text: "hi" } }]);
  expect(got.toolCalls).toEqual([{ id: "call_1", name: "speak" }]);
  expect(got.validation).toEqual({ valid: false, issues: "missing speech" });
  expect(got.fallback).toEqual({ flag: true, reason: "validator-fail" });
  expect(got.tokens).toEqual({ in: 100, out: 50 });
  expect(got.latencyMs).toBe(1200);
  expect(got.status).toBe("completed");
});

// ---------------------------------------------------------------------------
// 3. UPSERT 幂等：同 run_id 两次写入不产生重复行
// ---------------------------------------------------------------------------

test("recordAgentRun UPSERT: same run_id twice updates, no dup row", () => {
  store.recordAgentRun(makeAgentRun({ runId: "run-up", status: "running" }));
  store.recordAgentRun(
    makeAgentRun({ runId: "run-up", status: "completed", finalSpeech: "done" }),
  );

  const runs = store.getAgentRuns("Abigail");
  expect(runs).toHaveLength(1);
  expect(runs[0]!.status).toBe("completed");
  expect(runs[0]!.finalSpeech).toBe("done");
});

// ---------------------------------------------------------------------------
// 4. recordAgentTurn：同一 run_id 两轮，getAgentTurns 按序返回
// ---------------------------------------------------------------------------

test("recordAgentTurn: 2 turns under one run_id return in order", () => {
  const runId = "run-turns";
  store.recordAgentRun(makeAgentRun({ runId }));

  const turn0: AgentTurnRecord = {
    runId,
    turnIndex: 0,
    llmRawOutput: "raw-0",
    toolCalls: [{ name: "speak" }],
    toolResults: [{ ok: true }],
  };
  const turn1: AgentTurnRecord = {
    runId,
    turnIndex: 1,
    llmRawOutput: "raw-1",
    toolCalls: [],
    toolResults: [],
  };
  store.recordAgentTurn(turn0);
  store.recordAgentTurn(turn1);

  const turns = store.getAgentTurns(runId);
  expect(turns).toHaveLength(2);
  expect(turns[0]!.turnIndex).toBe(0);
  expect(turns[0]!.llmRawOutput).toBe("raw-0");
  expect(turns[0]!.toolCalls).toEqual([{ name: "speak" }]);
  expect(turns[0]!.toolResults).toEqual([{ ok: true }]);
  expect(turns[1]!.turnIndex).toBe(1);
  expect(turns[1]!.llmRawOutput).toBe("raw-1");
});

// ---------------------------------------------------------------------------
// 5. recordDirectorRun：empty_result=1 读回
// ---------------------------------------------------------------------------

test("recordDirectorRun: empty_result=1 reads back", () => {
  const rec: DirectorRunRecord = {
    runId: "dir-1",
    gameDate: "2026-08-01",
    trigger: "daily",
    promptFull: "plan the day",
    llmRawOutput: "no beats",
    producedBeats: [],
    droppedBeats: [],
    emptyResult: true,
    status: "noop",
  };
  store.recordDirectorRun(rec);

  const got = store.getDirectorRuns("2026-08-01");
  expect(got).toHaveLength(1);
  expect(got[0]!.runId).toBe("dir-1");
  expect(got[0]!.emptyResult).toBe(true);
  expect(got[0]!.status).toBe("noop");
  expect(got[0]!.producedBeats).toEqual([]);
  expect(got[0]!.droppedBeats).toEqual([]);
  expect(got[0]!.llmRawOutput).toBe("no beats");
});

// ---------------------------------------------------------------------------
// 6. 写失败不抛：触发一个违反 NOT NULL 的 INSERT，验证不抛 + warn 被调用
// ---------------------------------------------------------------------------

test("recordAgentRun write-failure does not throw (warns instead)", () => {
  const warnSpy = spyOn(console, "warn");
  // runId/npcName/trigger 是 NOT NULL；用 undefined 会让 TS 报错，这里用
  // as 之外的合法手段：构造一个 trigger 为空字符串仍合法的记录，但把
  // systemPromptHash 设为 undefined 触发 NOT NULL 失败。
  // 为绕过 TS 的类型检查，用 Object.assign 注入 null。
  const bad = { ...makeAgentRun(), systemPromptHash: null as unknown as string };
  expect(() => store.recordAgentRun(bad)).not.toThrow();
  expect(warnSpy).toHaveBeenCalled();
  const msg = String(warnSpy.mock.calls[0]?.[0] ?? "");
  expect(msg).toContain("[transcript] write failed:");
  warnSpy.mockRestore();
});

// ---------------------------------------------------------------------------
// 7. close() 不报错
// ---------------------------------------------------------------------------

test("close() does not throw", () => {
  // afterEach 已会 close；这里新建一个独立 store 验证 close 本身。
  const path2 = join(
    tmpdir(),
    `transcript-close-${Date.now()}-${Math.random().toString(36).slice(2)}.sqlite`,
  );
  const s = new TranscriptStore(path2);
  s.init();
  expect(() => s.close()).not.toThrow();
  if (existsSync(path2)) {
    rmSync(path2, { force: true });
    rmSync(`${path2}-wal`, { force: true });
    rmSync(`${path2}-shm`, { force: true });
  }
});