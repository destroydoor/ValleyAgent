// director_runs 留痕活化测试：morningPlan / milestoneReact 每条退出路径自动落库。
// Run: bun test packages/stardew/tests/director-run-recording.test.ts
//
// 背景：recordPlanRun 接缝此前无人调用（server 未注入 store + 入口点不自记录）
// → director_runs 永远为空，违反"AI 的决定必须可追溯"。本文件锁定：
//   1. 正常产出：completed + promptFull + llmRawOutput + produced/dropped（含原因）；
//   2. LLM 空产出 "[]"：noop + emptyResult=true；
//   3. LLM 抛错：status=error + error 信息，morningPlan 返回 [] 不抛；
//   4. 无 store：morningPlan 正常工作（零开销 no-op 接缝）。

import { test, expect, beforeEach, afterEach, spyOn } from "bun:test";
import { Director } from "../src/director";
import { BeatStore } from "../src/beat-store";
import { PlayerProfileStore } from "../src/player-profile-store";
import { PlayerProfileManager } from "../src/player-profile";
import { ActivityLogStore } from "../src/activity-log-store";
import { GameContextManager } from "../src/game-context";
import { TranscriptStore } from "../src/transcript-store";
import type { GameContext } from "../src/types";
import { rmSync, existsSync, mkdtempSync } from "node:fs";
import { join } from "node:path";
import { tmpdir } from "node:os";

const GAME_DATE = "Y2_summer_14";

const BEATS_JSON = JSON.stringify([
  { npcName: "Willy", triggerTime: "10:00", windowEnd: "12:00", directive: "邀请玩家去海边钓鱼", reasonGenerated: "玩家最近常钓鱼" },
  { npcName: "Ghost", triggerTime: "11:00", windowEnd: "13:00", directive: "幽灵不存在", reasonGenerated: "测试丢弃路径" },
]);

function makeGameContext(): GameContext {
  return {
    time: {
      year: 2, season: "summer", day: 14, dayOfWeek: "Tuesday",
      weather: "sunny", isFestivalDay: false,
    },
    progress: {
      communityCenterComplete: false, communityCenterBundlesDone: [],
      jojaMartRoute: false, islandsUnlocked: [], desertUnlocked: false,
      railroadUnlocked: false, sewersUnlocked: false, greenhouseRestored: false,
    },
    seasonalResources: { plantableCrops: [], catchableFish: [], forageItems: [], activeFestivals: [] },
    npcStates: [
      { name: "Willy", location: "Beach", tile: { x: 5, y: 8 }, isAvailable: true, currentState: "IDLE", friendshipPoints: 850 },
    ],
    playerState: {
      location: "Farm", tile: { x: 32, y: 18 }, health: 95, maxHealth: 100,
      energy: 200, maxEnergy: 270, money: 8500, inventory: [],
    },
    lastUpdated: "2026-08-09T06:00:00Z",
  };
}

let dir: string;
let beatStore: BeatStore;
let profileStore: PlayerProfileStore;
let activityStore: ActivityLogStore;
let profileMgr: PlayerProfileManager;
let gameCtxMgr: GameContextManager;
let store: TranscriptStore;
let storePath: string;

beforeEach(() => {
  dir = mkdtempSync(join(tmpdir(), "director-run-rec-"));
  beatStore = new BeatStore(join(dir, "beats.sqlite"));
  profileStore = new PlayerProfileStore(join(dir, "profiles.sqlite"));
  activityStore = new ActivityLogStore(join(dir, "activity.sqlite"));
  storePath = join(dir, "transcript.sqlite");
  store = new TranscriptStore(storePath);
  beatStore.init();
  profileStore.init();
  activityStore.init();
  store.init();
  profileMgr = new PlayerProfileManager(profileStore, activityStore, {
    callLlm: async () => ({ text: "", usage: { promptTokens: 0, completionTokens: 0 } }),
  });
  gameCtxMgr = new GameContextManager();
  gameCtxMgr.update(makeGameContext());
});

afterEach(() => {
  store.close();
  beatStore.close();
  profileStore.close();
  activityStore.close();
  rmSync(dir, { recursive: true, force: true });
});

function makeDirector(callLlm: (prompt: string) => Promise<{ text: string; usage: { promptTokens: number; completionTokens: number } }>, withStore = true): Director {
  return new Director(beatStore, profileMgr, gameCtxMgr, activityStore, { callLlm }, withStore ? store : undefined);
}

test("morningPlan records completed run: prompt + raw output + produced/dropped with reasons", async () => {
  const logSpy = spyOn(console, "log").mockImplementation(() => {});
  try {
    const director = makeDirector(async () => ({
      text: BEATS_JSON,
      usage: { promptTokens: 100, completionTokens: 50 },
    }));

    const beats = await director.morningPlan();

    // Willy 保留，Ghost（不在 gameContext）丢弃。
    expect(beats).toHaveLength(1);
    expect(beats[0]!.npcName).toBe("Willy");

    const runs = store.getDirectorRuns(GAME_DATE);
    expect(runs).toHaveLength(1);
    const run = runs[0]!;
    expect(run.trigger).toBe("morningPlan");
    expect(run.status).toBe("completed");
    expect(run.emptyResult).toBe(false);
    expect(run.promptFull).toContain("叙事导演");
    expect(run.llmRawOutput).toContain("Willy");
    expect(run.producedBeats).toHaveLength(1);
    expect((run.producedBeats[0] as { npcName: string }).npcName).toBe("Willy");
    expect(run.droppedBeats).toHaveLength(1);
    const dropped = run.droppedBeats[0] as { npcName: string; reasonDropped: string };
    expect(dropped.npcName).toBe("Ghost");
    expect(dropped.reasonDropped).toContain("不在 gameContext");
  } finally {
    logSpy.mockRestore();
  }
});

test("morningPlan records noop run when LLM outputs empty array", async () => {
  const logSpy = spyOn(console, "log").mockImplementation(() => {});
  try {
    const director = makeDirector(async () => ({
      text: "[]",
      usage: { promptTokens: 10, completionTokens: 2 },
    }));

    const beats = await director.morningPlan();
    expect(beats).toHaveLength(0);

    const runs = store.getDirectorRuns(GAME_DATE);
    expect(runs).toHaveLength(1);
    expect(runs[0]!.status).toBe("noop");
    expect(runs[0]!.emptyResult).toBe(true);
    expect(runs[0]!.producedBeats).toEqual([]);
  } finally {
    logSpy.mockRestore();
  }
});

test("morningPlan records error run when LLM throws, returns [] without throwing", async () => {
  const logSpy = spyOn(console, "log").mockImplementation(() => {});
  const errSpy = spyOn(console, "error").mockImplementation(() => {});
  try {
    const director = makeDirector(async () => {
      throw new Error("provider down");
    });

    const beats = await director.morningPlan();
    expect(beats).toHaveLength(0);

    const runs = store.getDirectorRuns(GAME_DATE);
    expect(runs).toHaveLength(1);
    expect(runs[0]!.status).toBe("error");
    expect(runs[0]!.error).toContain("provider down");
  } finally {
    logSpy.mockRestore();
    errSpy.mockRestore();
  }
});

test("morningPlan works without transcript store (zero-overhead no-op seam)", async () => {
  const logSpy = spyOn(console, "log").mockImplementation(() => {});
  try {
    const director = makeDirector(async () => ({
      text: BEATS_JSON,
      usage: { promptTokens: 100, completionTokens: 50 },
    }), /* withStore */ false);

    const beats = await director.morningPlan();
    expect(beats).toHaveLength(1);
    // store 里没有新行（该 store 属于另一个 Director 实例，本测试的未写入）。
    expect(store.getDirectorRuns(GAME_DATE)).toHaveLength(0);
    expect(existsSync(storePath)).toBe(true);
  } finally {
    logSpy.mockRestore();
  }
});
