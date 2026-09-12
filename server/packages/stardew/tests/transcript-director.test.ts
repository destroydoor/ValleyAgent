// Director 的 director_runs 写接缝测试（Wave 2 Task 4）。
// Run: bun test packages/stardew/tests/transcript-director.test.ts
// Spec: docs/design/2026-08-01-memory-narrative-extensibility.md §1
//
// 覆盖：
//   1. recordPlanRun 经 TranscriptStore 往返 —— 产出 beat + 丢弃 beat（含原因）
//      + "今日无叙事"空结果标记（emptyResult=true）都落 director_runs；
//   2. 未接线 store（Director 当前未在 server 实例化）→ recordPlanRun 是
//      no-op：不写、不抛（零开销接缝）。

import { test, expect, beforeEach, afterEach } from "bun:test";
import { Director } from "../src/director";
import { BeatStore } from "../src/beat-store";
import { PlayerProfileStore } from "../src/player-profile-store";
import { PlayerProfileManager } from "../src/player-profile";
import { ActivityLogStore } from "../src/activity-log-store";
import { GameContextManager } from "../src/game-context";
import { TranscriptStore } from "../src/transcript-store";
import { rmSync, existsSync } from "node:fs";
import { join } from "node:path";
import { tmpdir } from "node:os";

// ---------------------------------------------------------------------------
// 共享依赖：TranscriptStore（被测） + Director 的构造栈
// ---------------------------------------------------------------------------

let dbPath: string;
let store: TranscriptStore;

let beatDbPath: string;
let profileDbPath: string;
let activityDbPath: string;
let beatStore: BeatStore;
let profileStore: PlayerProfileStore;
let activityStore: ActivityLogStore;
let profileMgr: PlayerProfileManager;
let gameCtxMgr: GameContextManager;

beforeEach(() => {
  dbPath = join(
    tmpdir(),
    `transcript-director-test-${Date.now()}-${Math.random().toString(36).slice(2)}.sqlite`,
  );
  store = new TranscriptStore(dbPath);
  store.init();

  // recordPlanRun 不触碰 beat/profile/activity，但这些是 Director 构造必需。
  beatDbPath = join(tmpdir(), `tdir-beat-${Date.now()}-${Math.random().toString(36).slice(2)}.db`);
  profileDbPath = join(tmpdir(), `tdir-profile-${Date.now()}-${Math.random().toString(36).slice(2)}.db`);
  activityDbPath = join(tmpdir(), `tdir-activity-${Date.now()}-${Math.random().toString(36).slice(2)}.db`);
  beatStore = new BeatStore(beatDbPath);
  profileStore = new PlayerProfileStore(profileDbPath);
  activityStore = new ActivityLogStore(activityDbPath);
  beatStore.init();
  profileStore.init();
  activityStore.init();
  profileMgr = new PlayerProfileManager(profileStore, activityStore, {
    callLlm: async () => ({ text: "", usage: { promptTokens: 0, completionTokens: 0 } }),
  });
  gameCtxMgr = new GameContextManager();
});

afterEach(() => {
  store.close();
  beatStore.close();
  profileStore.close();
  activityStore.close();
  for (const p of [dbPath, beatDbPath, profileDbPath, activityDbPath]) {
    if (existsSync(p)) rmSync(p, { force: true });
    // WAL/SHM 旁路文件也清理。
    if (existsSync(`${p}-wal`)) rmSync(`${p}-wal`, { force: true });
    if (existsSync(`${p}-shm`)) rmSync(`${p}-shm`, { force: true });
  }
});

function makeDirector(transcriptStore?: TranscriptStore): Director {
  return new Director(beatStore, profileMgr, gameCtxMgr, activityStore, {
    callLlm: async () => ({ text: "", usage: { promptTokens: 0, completionTokens: 0 } }),
  }, transcriptStore);
}

// ---------------------------------------------------------------------------
// 1. recordPlanRun 往返：产出 beat + 丢弃 beat（含原因） + 空结果标记
// ---------------------------------------------------------------------------

test("recordPlanRun round-trips produced/dropped beats and empty-result marker", () => {
  const director = makeDirector(store);
  const date = "2026-08-03";

  // 早晨计划：产出 1 个 beat，丢弃 1 个（NPC 冷却中）。
  director.recordPlanRun({
    runId: "director-run-morning",
    gameDate: date,
    trigger: "morningPlan",
    promptFull: "你是星露谷的叙事导演…",
    producedBeats: [
      { npcName: "Abigail", triggerTime: "10:00", windowEnd: "12:00", directive: "邀请玩家一起钓鱼" },
    ],
    droppedBeats: [
      { npcName: "Willy", directive: "去海边", reasonDropped: "NPC 冷却中" },
    ],
    emptyResult: false,
    status: "completed",
  });

  // 里程碑反应：本轮无产出 —— "今日无叙事"也是一个记录。
  director.recordPlanRun({
    runId: "director-run-milestone",
    gameDate: date,
    trigger: "milestoneReact",
    producedBeats: [],
    droppedBeats: [],
    emptyResult: true,
    status: "noop",
  });

  const runs = store.getDirectorRuns(date);
  expect(runs).toHaveLength(2);

  const produced = runs.find((r) => r.runId === "director-run-morning")!;
  expect(produced.trigger).toBe("morningPlan");
  expect(produced.status).toBe("completed");
  expect(produced.emptyResult).toBe(false);
  expect(produced.promptFull).toContain("星露谷");
  expect(produced.producedBeats).toHaveLength(1);
  expect((produced.producedBeats[0] as { npcName: string }).npcName).toBe("Abigail");
  expect(produced.droppedBeats).toHaveLength(1);
  expect((produced.droppedBeats[0] as { reasonDropped: string }).reasonDropped).toContain("冷却");

  const empty = runs.find((r) => r.runId === "director-run-milestone")!;
  expect(empty.trigger).toBe("milestoneReact");
  expect(empty.emptyResult).toBe(true);
  expect(empty.status).toBe("noop");
  expect(empty.producedBeats).toEqual([]);
  expect(empty.droppedBeats).toEqual([]);
});

// ---------------------------------------------------------------------------
// 2. 未接线 store → recordPlanRun 是 no-op（零开销接缝）
// ---------------------------------------------------------------------------

test("recordPlanRun is a no-op when no transcript store is wired", () => {
  const director = makeDirector(); // 不注入 store —— 留痕未启用时的生产路径
  expect(() =>
    director.recordPlanRun({
      runId: "director-run-nowired",
      gameDate: "2026-08-03",
      trigger: "morningPlan",
      producedBeats: [],
      droppedBeats: [],
      emptyResult: true,
      status: "noop",
    }),
  ).not.toThrow();
});
