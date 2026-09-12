// 导演管道端到端回归测试（adapter 级）：game_context_sync → day_started →
// morningPlan → allocate_agent。
// Run: bun test packages/stardew/tests/director-pipeline.test.ts
//
// 锁定 2026-08-09 发现的关键断链：TS 端缺失 game_context_sync 处理/接线时，
// GameContextManager 恒空 → morningPlan 恒走 no-game-context 分支 → 导演 LLM
// 从未运行、allocate_agent 从未发送。四条断言：
//   A. 未 sync 先 day_started → LLM 不调用、不发 allocate_agent（断链回归）；
//   B. sync → day_started → LLM 调用 1 次、prompt 含可用 NPC、合法 beat 落库
//      并逐 beat 发 allocate_agent（未知 NPC 的 beat 被丢弃不发送）；
//   C. LLM 输出非 JSON → 不发送、不抛；
//   D. 节日上下文 → LLM 不调用。

import { test, expect, spyOn } from "bun:test";
import { ProtocolAdapter } from "../src/protocol-adapter";
import { NpcPromptLoader } from "../src/npc-prompt-loader";
import { PromptBuilder } from "../src/prompt-builder";
import { VercelAIProvider } from "@valley/core";
import { StardewAgentRegistry } from "../src/stardew-agent-registry";
import { Director } from "../src/director";
import { BeatStore } from "../src/beat-store";
import { PlayerProfileStore } from "../src/player-profile-store";
import { PlayerProfileManager } from "../src/player-profile";
import { ActivityLogStore } from "../src/activity-log-store";
import { GameContextManager } from "../src/game-context";
import type { GameContext } from "../src/types";
import { mkdtempSync, rmSync } from "node:fs";
import { join, resolve } from "node:path";
import { tmpdir } from "node:os";

const DATA_PATH = resolve(import.meta.dir, "../data/npc_prompts.json");

const BEATS_JSON = JSON.stringify([
  { npcName: "Willy", triggerTime: "10:00", windowEnd: "12:00", directive: "邀请玩家去海边钓鱼", reasonGenerated: "玩家最近常钓鱼" },
  { npcName: "Ghost", triggerTime: "11:00", windowEnd: "13:00", directive: "不在上下文的 NPC", reasonGenerated: "应被校验丢弃" },
]);

function makeGameContext(isFestivalDay = false): GameContext {
  return {
    time: {
      year: 2, season: "summer", day: 14, dayOfWeek: "Tuesday",
      weather: "sunny", isFestivalDay,
    },
    progress: {
      communityCenterComplete: false, communityCenterBundlesDone: [],
      jojaMartRoute: false, islandsUnlocked: [], desertUnlocked: false,
      railroadUnlocked: false, sewersUnlocked: false, greenhouseRestored: false,
    },
    seasonalResources: { plantableCrops: [], catchableFish: [], forageItems: [], activeFestivals: [] },
    npcStates: [
      { name: "Willy", location: "Beach", tile: { x: 5, y: 8 }, isAvailable: true, currentState: "IDLE", friendshipPoints: 850 },
      { name: "Sebastian", location: "Mountain", tile: { x: 50, y: 10 }, isAvailable: false, currentState: "SLEEP", friendshipPoints: 200 },
    ],
    playerState: {
      location: "Farm", tile: { x: 32, y: 18 }, health: 95, maxHealth: 100,
      energy: 200, maxEnergy: 270, money: 8500, inventory: [],
    },
    lastUpdated: "2026-08-09T06:00:00Z",
  };
}

interface PipelineStack {
  dir: string;
  adapter: ProtocolAdapter;
  gameCtxMgr: GameContextManager;
  beatStore: BeatStore;
  sent: unknown[];
  llmPrompts: string[];
  cleanup: () => void;
}

function makeStack(llmResponse: () => Promise<{ text: string; usage: { promptTokens: number; completionTokens: number } }>): PipelineStack {
  const dir = mkdtempSync(join(tmpdir(), "director-pipeline-"));
  const loader = new NpcPromptLoader(DATA_PATH);
  const builder = new PromptBuilder(loader);
  const provider = new VercelAIProvider({
    provider: "minimax", apiKey: "fake", model: "fake", baseUrl: "http://localhost:9999",
  });
  const registry = new StardewAgentRegistry({
    promptBuilder: builder, llmProvider: provider, agentsDir: dir,
  });

  const beatStore = new BeatStore(join(dir, "beats.sqlite"));
  const profileStore = new PlayerProfileStore(join(dir, "profiles.sqlite"));
  const activityStore = new ActivityLogStore(join(dir, "activity.sqlite"));
  beatStore.init();
  profileStore.init();
  activityStore.init();

  const llmPrompts: string[] = [];
  const profileMgr = new PlayerProfileManager(profileStore, activityStore, {
    callLlm: async () => ({ text: "", usage: { promptTokens: 0, completionTokens: 0 } }),
  });
  const gameCtxMgr = new GameContextManager();
  const director = new Director(beatStore, profileMgr, gameCtxMgr, activityStore, {
    callLlm: async (prompt: string) => {
      llmPrompts.push(prompt);
      return llmResponse();
    },
  });

  const sent: unknown[] = [];
  const adapter = new ProtocolAdapter(registry, {
    sendToCsharp: (msg: unknown) => { sent.push(msg); },
    director,
    gameCtxMgr,
    directorTriggerProbability: 1.0,
  });

  return {
    dir, adapter, gameCtxMgr, beatStore, sent, llmPrompts,
    cleanup: () => {
      beatStore.close();
      profileStore.close();
      activityStore.close();
      rmSync(dir, { recursive: true, force: true });
    },
  };
}

test("A: day_started without prior game_context_sync → LLM not called, nothing sent (dead-pipeline regression)", async () => {
  const stack = makeStack(async () => ({ text: BEATS_JSON, usage: { promptTokens: 1, completionTokens: 1 } }));
  const logSpy = spyOn(console, "log").mockImplementation(() => {});
  try {
    const resp = await stack.adapter.routeMessage({
      type: "day_started",
      requestId: "req-day-a",
      dateIso: "Y2_summer_14",
    });

    expect(resp).toEqual({ type: "ack", requestId: "req-day-a" });
    expect(stack.llmPrompts).toHaveLength(0);
    expect(stack.sent).toHaveLength(0);
  } finally {
    logSpy.mockRestore();
    stack.cleanup();
  }
});

test("B: game_context_sync → day_started → LLM called once, beat persisted, allocate_agent sent per valid beat", async () => {
  const stack = makeStack(async () => ({ text: BEATS_JSON, usage: { promptTokens: 100, completionTokens: 50 } }));
  const logSpy = spyOn(console, "log").mockImplementation(() => {});
  try {
    await stack.adapter.routeMessage({
      type: "game_context_sync",
      requestId: "req-ctx-b",
      context: makeGameContext(),
    });
    const resp = await stack.adapter.routeMessage({
      type: "day_started",
      requestId: "req-day-b",
      dateIso: "Y2_summer_14",
    });

    expect(resp).toEqual({ type: "ack", requestId: "req-day-b" });
    // LLM 恰好调用一次，prompt 带上了结构化上下文里的可用 NPC。
    expect(stack.llmPrompts).toHaveLength(1);
    expect(stack.llmPrompts[0]).toContain("Willy");
    // Ghost 被 NPC 存在性校验丢弃 —— 只有 Willy 的 beat 触发分配。
    expect(stack.sent).toHaveLength(1);
    expect(stack.sent[0]).toMatchObject({
      type: "allocate_agent",
      npcName: "Willy",
      keepUntilIso: "12:00",
    });
    const reqId = (stack.sent[0] as { requestId?: string }).requestId;
    expect(typeof reqId).toBe("string");
    expect(reqId!.length).toBeGreaterThan(0);
    // beat 持久化到 BeatStore。
    const recent = stack.beatStore.listRecent(5);
    expect(recent).toHaveLength(1);
    expect(recent[0]!.npcName).toBe("Willy");
    expect(recent[0]!.status).toBe("scheduled");
  } finally {
    logSpy.mockRestore();
    stack.cleanup();
  }
});

test("C: LLM outputs garbage → no allocate_agent, no throw", async () => {
  const stack = makeStack(async () => ({ text: "今天天气不错，不适合讲故事。", usage: { promptTokens: 5, completionTokens: 5 } }));
  const logSpy = spyOn(console, "log").mockImplementation(() => {});
  try {
    await stack.adapter.routeMessage({
      type: "game_context_sync",
      requestId: "req-ctx-c",
      context: makeGameContext(),
    });
    const resp = await stack.adapter.routeMessage({
      type: "day_started",
      requestId: "req-day-c",
      dateIso: "Y2_summer_14",
    });

    expect(resp).toEqual({ type: "ack", requestId: "req-day-c" });
    expect(stack.llmPrompts).toHaveLength(1);
    expect(stack.sent).toHaveLength(0);
  } finally {
    logSpy.mockRestore();
    stack.cleanup();
  }
});

test("D: festival day context → LLM not called", async () => {
  const stack = makeStack(async () => ({ text: BEATS_JSON, usage: { promptTokens: 1, completionTokens: 1 } }));
  const logSpy = spyOn(console, "log").mockImplementation(() => {});
  try {
    await stack.adapter.routeMessage({
      type: "game_context_sync",
      requestId: "req-ctx-d",
      context: makeGameContext(/* isFestivalDay */ true),
    });
    const resp = await stack.adapter.routeMessage({
      type: "day_started",
      requestId: "req-day-d",
      dateIso: "Y2_summer_24",
    });

    expect(resp).toEqual({ type: "ack", requestId: "req-day-d" });
    expect(stack.llmPrompts).toHaveLength(0);
    expect(stack.sent).toHaveLength(0);
  } finally {
    logSpy.mockRestore();
    stack.cleanup();
  }
});
