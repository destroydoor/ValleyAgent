// DirectorAgent 单元测试（2026-09-12 Director 有效化）。
// Run: bun test packages/stardew/tests/director-agent.test.ts
//
// 覆盖：
//   1. 无 GameContext / 节日 → noop（零下发）
//   2. spawn_beat 工具调用 → director_command 下发（不带 npcName）+ BeatStore 记录 + 摘要
//   3. 校验拒绝：NPC 不在 gameContext / 冷却中 / 超每日上限
//   4. 状态类工具（set_npc_mood）正常下发
//   5. LLM 失败 → status=error 不上抛

import { test, expect, spyOn } from "bun:test";
import { DirectorAgent, type DirectorLlm } from "../src/director-agent";
import { BeatStore } from "../src/beat-store";
import { PlayerProfileManager } from "../src/player-profile";
import { PlayerProfileStore } from "../src/player-profile-store";
import { ActivityLogStore } from "../src/activity-log-store";
import { GameContextManager } from "../src/game-context";
import type { GameContext, DirectorCommandMessage, PlayerProfile } from "../src/types";
import type { LlmCallResult } from "@valley/core";
import { mkdtempSync, rmSync } from "node:fs";
import { join } from "node:path";
import { tmpdir } from "os";

function makeContext(overrides: Partial<GameContext["time"]> = {}): GameContext {
  return {
    time: {
      year: 2, season: "summer", day: 14, dayOfWeek: "Tuesday",
      weather: "sunny", isFestivalDay: false, ...overrides,
    },
    progress: {
      communityCenterComplete: false, communityCenterBundlesDone: [],
      jojaMartRoute: false, islandsUnlocked: [], desertUnlocked: false,
      railroadUnlocked: false, sewersUnlocked: false, greenhouseRestored: false,
    },
    seasonalResources: { plantableCrops: [], catchableFish: [], forageItems: [], activeFestivals: [] },
    npcStates: [
      { name: "Willy", location: "Beach", tile: { x: 5, y: 8 }, isAvailable: true, currentState: "IDLE", friendshipPoints: 850 },
      { name: "Abigail", location: "Town", tile: { x: 20, y: 10 }, isAvailable: true, currentState: "IDLE", friendshipPoints: 400 },
    ],
    playerState: {
      location: "Farm", tile: { x: 32, y: 18 }, health: 95, maxHealth: 100,
      energy: 200, maxEnergy: 270, money: 8500, inventory: [],
    },
    lastUpdated: "2026-09-12T06:00:00Z",
  };
}

interface Stack {
  dir: string;
  agent: DirectorAgent;
  beatStore: BeatStore;
  gameCtxMgr: GameContextManager;
  sent: DirectorCommandMessage[];
  llmScript: Array<() => Promise<LlmCallResult>>;
}

function makeStack(llmScript: Array<() => Promise<LlmCallResult>>, opts: { maxBeatsPerDay?: number } = {}): Stack {
  const dir = mkdtempSync(join(tmpdir(), "valley-director-agent-"));
  const beatStore = new BeatStore(join(dir, "beats.sqlite"));
  beatStore.init();
  const activityStore = new ActivityLogStore(join(dir, "activity.sqlite"));
  activityStore.init();
  const profileStore = new PlayerProfileStore(join(dir, "profiles.sqlite"));
  profileStore.init();
  const profileMgr = new PlayerProfileManager(profileStore, activityStore, {
    callLlm: async () => ({ text: "", usage: { promptTokens: 0, completionTokens: 0 } }),
  });
  const gameCtxMgr = new GameContextManager();
  gameCtxMgr.update(makeContext());
  const sent: DirectorCommandMessage[] = [];
  const llm: DirectorLlm = async () => {
    const next = llmScript.shift();
    return next ? next() : Promise.resolve({ content: "（无更多输出）" });
  };
  const agent = new DirectorAgent(
    beatStore,
    profileMgr,
    gameCtxMgr,
    { llm, sendToCsharp: (msg) => { sent.push(msg as DirectorCommandMessage); }, ...(opts.maxBeatsPerDay !== undefined ? { maxBeatsPerDay: opts.maxBeatsPerDay } : {}) },
  );
  return { dir, agent, beatStore, gameCtxMgr, sent, llmScript };
}

test("runDayPlan: no game context → noop, nothing sent", async () => {
  const dir = mkdtempSync(join(tmpdir(), "valley-director-agent-noctx-"));
  const logSpy = spyOn(console, "log").mockImplementation(() => {});
  try {
    const beatStore = new BeatStore(join(dir, "beats.sqlite"));
    beatStore.init();
    const activityStore = new ActivityLogStore(join(dir, "activity.sqlite"));
    activityStore.init();
    const profileStore = new PlayerProfileStore(join(dir, "profiles.sqlite"));
    profileStore.init();
    const profileMgr = new PlayerProfileManager(profileStore, activityStore, {
      callLlm: async () => ({ text: "", usage: { promptTokens: 0, completionTokens: 0 } }),
    });
    const gameCtxMgr = new GameContextManager(); // 不 update → 无上下文
    const sent: DirectorCommandMessage[] = [];
    const agent = new DirectorAgent(beatStore, profileMgr, gameCtxMgr, {
      llm: async () => { throw new Error("should not be called"); },
      sendToCsharp: (msg) => { sent.push(msg as DirectorCommandMessage); },
    });
    const summary = await agent.runDayPlan();
    expect(summary.status).toBe("noop");
    expect(summary.spawnedBeats.length).toBe(0);
    expect(sent.length).toBe(0);
  } finally {
    logSpy.mockRestore();
    rmSync(dir, { recursive: true, force: true });
  }
});

test("runDayPlan: festival day → noop, nothing sent", async () => {
  const logSpy = spyOn(console, "log").mockImplementation(() => {});
  const stack = makeStack([]);
  try {
    stack.gameCtxMgr.update(makeContext({ isFestivalDay: true, festivalName: "Luau" }));
    const summary = await stack.agent.runDayPlan();
    expect(summary.status).toBe("noop");
    expect(stack.sent.length).toBe(0);
  } finally {
    logSpy.mockRestore();
    rmSync(stack.dir, { recursive: true, force: true });
  }
});

test("runDayPlan: spawn_beat tool call → director_command + beat record + summary", async () => {
  const logSpy = spyOn(console, "log").mockImplementation(() => {});
  const stack = makeStack([
    () => Promise.resolve({
      content: "（编排中）",
      toolCalls: [{ id: "tc-1", name: "spawn_beat", args: { npc: "Willy", sceneDesc: "他在码头修补渔网", durationMinutes: 120 } }],
    }),
    () => Promise.resolve({ content: "今天的编排完成了。" }),
  ]);
  try {
    const summary = await stack.agent.runDayPlan("今日全局视野");
    expect(summary.status).toBe("completed");
    expect(summary.commandsSent).toBe(1);
    expect(summary.spawnedBeats).toEqual([{ npcName: "Willy", durationMinutes: 120 }]);
    // director_command 下发：tool/args 正确，且绝不带 npcName（职责隔离——见 types.ts）
    expect(stack.sent.length).toBe(1);
    const cmd = stack.sent[0]!;
    expect(cmd.type).toBe("director_command");
    expect(cmd.tool).toBe("spawn_beat");
    expect(cmd.args).toMatchObject({ npc: "Willy", sceneDesc: "他在码头修补渔网", durationMinutes: 120 });
    expect((cmd as { npcName?: string }).npcName).toBeUndefined();
    // BeatStore 记录（冷却/预算历史）
    const recent = stack.beatStore.listRecent(5);
    expect(recent.length).toBe(1);
    expect(recent[0]!.npcName).toBe("Willy");
  } finally {
    logSpy.mockRestore();
    rmSync(stack.dir, { recursive: true, force: true });
  }
});

test("runDayPlan: NPC 不在 gameContext → 工具被拒，零下发", async () => {
  const logSpy = spyOn(console, "log").mockImplementation(() => {});
  const stack = makeStack([
    () => Promise.resolve({
      content: "（编排中）",
      toolCalls: [{ id: "tc-1", name: "spawn_beat", args: { npc: "Krobus", sceneDesc: "他在下水道里发呆" } }],
    }),
    () => Promise.resolve({ content: "换个思路。" }),
  ]);
  try {
    const summary = await stack.agent.runDayPlan();
    expect(summary.status).toBe("noop");
    expect(summary.spawnedBeats.length).toBe(0);
    expect(summary.rejectedCalls.length).toBe(1);
    expect(summary.rejectedCalls[0]!.reason).toContain("Krobus");
    expect(stack.sent.length).toBe(0);
  } finally {
    logSpy.mockRestore();
    rmSync(stack.dir, { recursive: true, force: true });
  }
});

test("runDayPlan: 同 NPC 冷却（BeatStore 近期记录）→ 拒绝", async () => {
  const logSpy = spyOn(console, "log").mockImplementation(() => {});
  const stack = makeStack([
    () => Promise.resolve({
      content: "（编排中）",
      toolCalls: [{ id: "tc-1", name: "spawn_beat", args: { npc: "Willy", sceneDesc: "他又在修渔网" } }],
    }),
    () => Promise.resolve({ content: "好吧。" }),
  ]);
  try {
    // 预置一条 Willy 近期 beat → 冷却生效（context 快照用最小合法对象）
    stack.beatStore.save({
      id: "recent-1", npcName: "Willy", triggerTime: "10:00", windowEnd: "12:00",
      directive: "之前的 beat",
      context: {
        reasonGenerated: "test",
        playerProfileSnapshot: null as unknown as PlayerProfile,
        gameContextSnapshot: makeContext(),
        recentBeats: [],
      },
      status: "active",
    });
    const summary = await stack.agent.runDayPlan();
    expect(summary.spawnedBeats.length).toBe(0);
    expect(summary.rejectedCalls[0]!.reason).toContain("冷却");
    expect(stack.sent.length).toBe(0);
  } finally {
    logSpy.mockRestore();
    rmSync(stack.dir, { recursive: true, force: true });
  }
});

test("runDayPlan: 每日上限（maxBeatsPerDay=1）→ 第二个 beat 拒绝", async () => {
  const logSpy = spyOn(console, "log").mockImplementation(() => {});
  const stack = makeStack([
    () => Promise.resolve({
      content: "（编排中）",
      toolCalls: [{ id: "tc-1", name: "spawn_beat", args: { npc: "Willy", sceneDesc: "修渔网" } }],
    }),
    () => Promise.resolve({
      content: "（再编一个）",
      toolCalls: [{ id: "tc-2", name: "spawn_beat", args: { npc: "Abigail", sceneDesc: "在湖边画画" } }],
    }),
    () => Promise.resolve({ content: "收工。" }),
  ], { maxBeatsPerDay: 1 });
  try {
    const summary = await stack.agent.runDayPlan();
    expect(summary.spawnedBeats.length).toBe(1);
    expect(summary.spawnedBeats[0]!.npcName).toBe("Willy");
    expect(summary.rejectedCalls.some((r) => r.reason.includes("每日上限"))).toBe(true);
    expect(stack.sent.filter((c) => c.tool === "spawn_beat").length).toBe(1);
  } finally {
    logSpy.mockRestore();
    rmSync(stack.dir, { recursive: true, force: true });
  }
});

test("runDayPlan: set_npc_mood 状态工具正常下发", async () => {
  const logSpy = spyOn(console, "log").mockImplementation(() => {});
  const stack = makeStack([
    () => Promise.resolve({
      content: "（调整状态）",
      toolCalls: [{ id: "tc-1", name: "set_npc_mood", args: { npc: "Abigail", moodTag: "期待" } }],
    }),
    () => Promise.resolve({ content: "好了。" }),
  ]);
  try {
    const summary = await stack.agent.runDayPlan();
    expect(summary.commandsSent).toBe(1);
    expect(stack.sent[0]!.tool).toBe("set_npc_mood");
    expect(stack.sent[0]!.args).toEqual({ npc: "Abigail", moodTag: "期待" });
    // 状态类工具不算 beat
    expect(summary.spawnedBeats.length).toBe(0);
  } finally {
    logSpy.mockRestore();
    rmSync(stack.dir, { recursive: true, force: true });
  }
});

test("runDayPlan: LLM 失败 → status=error，不上抛", async () => {
  const logSpy = spyOn(console, "log").mockImplementation(() => {});
  const errSpy = spyOn(console, "error").mockImplementation(() => {});
  const stack = makeStack([
    () => Promise.reject(new Error("LLM down")),
  ]);
  try {
    const summary = await stack.agent.runDayPlan();
    expect(summary.status).toBe("error");
    expect(summary.error).toContain("LLM down");
    expect(stack.sent.length).toBe(0);
  } finally {
    logSpy.mockRestore();
    errSpy.mockRestore();
    rmSync(stack.dir, { recursive: true, force: true });
  }
});
