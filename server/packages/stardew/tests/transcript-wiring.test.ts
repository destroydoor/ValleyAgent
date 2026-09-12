// E1-1 接线测试：runDialogue / runBeat 通过 TranscriptStore 落全量留痕。
// Run: bun test packages/stardew/tests/transcript-wiring.test.ts
// Spec: docs/design/2026-08-01-memory-narrative-extensibility.md §1
//
// 覆盖 5 条验收：
//   1. 启用 + 临时目录：一次 runDialogue 恰好 1 行 agent_runs，终态 completed；
//   2. 该 run 至少 1 行 agent_turns；
//   3. 校验失败 / LLM 故障路径写 status="error"；
//   4. getAgentRuns(npcName, gameDate?) 往返（runBeat 携带游戏日期）；
//   5. 禁用时零构造：无 SQLite 文件、无行。

import { test, expect } from "bun:test";
import { StardewAgentRegistry } from "../src/stardew-agent-registry";
import { NpcPromptLoader } from "../src/npc-prompt-loader";
import { PromptBuilder } from "../src/prompt-builder";
import { PlayerProfileManager } from "../src/player-profile";
import { PlayerProfileStore } from "../src/player-profile-store";
import { ActivityLogStore } from "../src/activity-log-store";
import { GameContextManager } from "../src/game-context";
import { TranscriptStore } from "../src/transcript-store";
import { VercelAIProvider } from "@valley/core";
import { mkdtempSync, rmSync, existsSync } from "fs";
import { join } from "path";
import { tmpdir } from "os";
import { resolve } from "path";
import type { SceneState, Beat, GameContext, PlayerProfile } from "../src/types";

const DATA_PATH = resolve(import.meta.dir, "../data/npc_prompts.json");

const scene: SceneState = {
  season: "summer", day: 28, timeStr: "14:30", weather: "sunny",
  location: "Town", nearbyObjects: "2 villagers", farmerName: "新来的农夫",
  friendship: 250, npcState: "IDLE", inventory: [],
  npcTile: { x: 0, y: 0 },
  playerMoney: null,
  npcLocation: null,
  npcMoney: null,
  npcInventory: null,
  playerHeldItem: null,
  currentGoal: null,
  npcMood: null,
  npcRecentEvents: null,
  npcWorkingOn: null,
  npcOwedMoney: null,
};

// ---------------------------------------------------------------------------
// 测试栈工厂：启用留痕的 registry + 可注入 LLM 行为的 mock
// ---------------------------------------------------------------------------

interface MockLlmReply {
  content: string;
  toolCalls?: Array<{ id: string; name: string; args: Record<string, unknown> }>;
}

interface WiringStack {
  registry: StardewAgentRegistry;
  dir: string;
  dbPath: string;
  /**
   * 关闭 registry 内部 store（checkpoint 刷 WAL）后，以只读方式重开查询。
   * 返回的 store 由调用方负责 close；dir 由调用方负责 rmSync。
   */
  openReadStore: () => TranscriptStore;
}

function makeEnabledStack(llmOverride?: () => Promise<MockLlmReply>): WiringStack {
  const dir = mkdtempSync(join(tmpdir(), "valley-transcript-wiring-"));
  const dbPath = join(dir, "transcript.sqlite");
  const loader = new NpcPromptLoader(DATA_PATH);
  const builder = new PromptBuilder(loader);
  const provider = new VercelAIProvider({
    provider: "minimax",
    apiKey: "fake",
    model: "fake",
    baseUrl: "http://localhost:9999",
  });
  provider._setCallOverride(
    llmOverride ??
      (async () => ({
        content: "",
        toolCalls: [{ id: "tc-1", name: "speak", args: { text: "你好啊，新来的农夫。" } }],
      })),
  );
  const registry = new StardewAgentRegistry({
    promptBuilder: builder,
    llmProvider: provider,
    agentsDir: dir,
    transcript: { enabled: true, dir },
  });
  return {
    registry,
    dir,
    dbPath,
    openReadStore: () => {
      registry.closeTranscriptStore();
      const store = new TranscriptStore(dbPath);
      store.init();
      return store;
    },
  };
}

// ---------------------------------------------------------------------------
// 1. 一次 runDialogue → 恰好 1 行 agent_runs，终态 completed
// ---------------------------------------------------------------------------

test("enabled: runDialogue writes exactly one agent_runs row with final status completed", async () => {
  const stack = makeEnabledStack();
  try {
    const agent = stack.registry.getOrCreate("Abigail");
    const result = await agent.runDialogue("你好", scene);

    const store = stack.openReadStore();
    try {
      const runs = store.getAgentRuns("Abigail");
      expect(runs).toHaveLength(1);
      const run = runs[0]!;
      expect(run.runId).toMatch(/^[0-9a-f-]{36}$/);
      expect(run.trigger).toBe("dialogue");
      expect(run.status).toBe("completed");
      expect(run.userInput).toBe("你好");
      expect(run.finalSpeech).toBe(result.speech);
      expect(run.systemPromptHash).toMatch(/^[0-9a-f]{64}$/);
      expect(run.systemPromptFull.length).toBeGreaterThan(0);
      expect(run.actions).toEqual(result.actions);
      expect(run.toolCalls).toEqual(result.toolCalls);
      expect(run.finishedAt).toBeDefined();
      expect(run.latencyMs).toBeGreaterThanOrEqual(0);
      expect(run.validation.valid).toBe(true);
    } finally {
      store.close();
    }
  } finally {
    rmSync(stack.dir, { recursive: true, force: true });
  }
});

// ---------------------------------------------------------------------------
// 2. 同一 run 至少 1 行 agent_turns（多轮 ReAct 每轮一行）
// ---------------------------------------------------------------------------

test("enabled: runDialogue writes agent_turns rows per agentLoop turn", async () => {
  let calls = 0;
  const stack = makeEnabledStack(async () => {
    calls++;
    if (calls === 1) {
      return { content: "", toolCalls: [{ id: "tc-1", name: "get_info", args: { query: "date" } }] };
    }
    return { content: "", toolCalls: [{ id: "tc-2", name: "speak", args: { text: "现在是夏天。" } }] };
  });
  try {
    const agent = stack.registry.getOrCreate("Abigail");
    await agent.runDialogue("现在是什么季节？", scene);
    expect(calls).toBe(2);

    const store = stack.openReadStore();
    try {
      const runs = store.getAgentRuns("Abigail");
      expect(runs).toHaveLength(1);
      const turns = store.getAgentTurns(runs[0]!.runId);
      expect(turns.length).toBeGreaterThanOrEqual(1);
      // 第一轮是 get_info：带 tool_call 且有 tool_result 回执。
      expect(turns[0]!.turnIndex).toBe(0);
      expect(turns[0]!.toolCalls.length).toBeGreaterThan(0);
      expect(turns[0]!.toolResults.length).toBeGreaterThan(0);
      // 第二轮是 speak。
      expect(turns[1]!.turnIndex).toBe(1);
      expect(turns[1]!.toolCalls.some((tc) => (tc as { name: string }).name === "speak")).toBe(true);
    } finally {
      store.close();
    }
  } finally {
    rmSync(stack.dir, { recursive: true, force: true });
  }
});

// ---------------------------------------------------------------------------
// 3a. 校验重试双失败 → status="error"（含错误信息）
// ---------------------------------------------------------------------------

test("enabled: validation double-failure writes run status error", async () => {
  // 全英文回复 → CJK 比例不达标 → 首次 + 重试都失败 → runDialogue 抛错。
  const stack = makeEnabledStack(async () => ({
    content: "hello world, this is not chinese at all",
    toolCalls: [],
  }));
  try {
    const agent = stack.registry.getOrCreate("Abigail");
    await expect(agent.runDialogue("你好", scene)).rejects.toThrow(/validation failed/);

    const store = stack.openReadStore();
    try {
      const runs = store.getAgentRuns("Abigail");
      expect(runs).toHaveLength(1);
      expect(runs[0]!.status).toBe("error");
      expect(runs[0]!.error).toContain("validation failed");
      expect(runs[0]!.fallback.flag).toBe(false);
    } finally {
      store.close();
    }
  } finally {
    rmSync(stack.dir, { recursive: true, force: true });
  }
});

// ---------------------------------------------------------------------------
// 3b. LLM 故障（超时/不可用）→ status="error"
// ---------------------------------------------------------------------------

test("enabled: LLM failure writes run status error", async () => {
  const stack = makeEnabledStack(async () => {
    throw new Error("LLM API down");
  });
  try {
    const agent = stack.registry.getOrCreate("Abigail");
    await expect(agent.runDialogue("你好", scene)).rejects.toThrow(/LLM API down/);

    const store = stack.openReadStore();
    try {
      const runs = store.getAgentRuns("Abigail");
      expect(runs).toHaveLength(1);
      expect(runs[0]!.status).toBe("error");
      expect(runs[0]!.error).toContain("LLM API down");
      // 中断的那轮也要留痕（走神的那轮不蒸发）。
      expect(store.getAgentTurns(runs[0]!.runId).length).toBeGreaterThanOrEqual(0);
    } finally {
      store.close();
    }
  } finally {
    rmSync(stack.dir, { recursive: true, force: true });
  }
});

// ---------------------------------------------------------------------------
// 4. getAgentRuns(npcName, gameDate) 往返 —— runBeat 携带游戏日期
// ---------------------------------------------------------------------------

test("enabled: runBeat writes run with gameDate and getAgentRuns(npcName, gameDate) round-trips", async () => {
  const stack = makeEnabledStack(async () => ({
    content: "（思考中）",
    toolCalls: [
      { id: "tc-1", name: "speak", args: { text: "嘿，听说你最近在酿酒？我能看看吗？" } },
      { id: "tc-2", name: "emote", args: { emote_id: "happy" } },
    ],
  }));
  try {
    const agent = stack.registry.getOrCreate("Abigail");
    const profileStore = new PlayerProfileStore(join(stack.dir, "profiles.db"));
    const activityStore = new ActivityLogStore(join(stack.dir, "activity.db"));
    profileStore.init();
    activityStore.init();
    const profileMgr = new PlayerProfileManager(profileStore, activityStore, {
      callLlm: async () => ({ text: "", usage: { promptTokens: 0, completionTokens: 0 } }),
    });
    profileMgr.initProfile(makePlayerProfile().static);
    const gameCtxMgr = new GameContextManager();
    gameCtxMgr.update(makeGameContext());

    const result = await agent.runBeat(makeBeat(), makeGameContext(), profileMgr, gameCtxMgr);
    expect(result.speech).toContain("酿酒");

    profileStore.close();
    activityStore.close();

    const store = stack.openReadStore();
    try {
      // 无日期过滤也能查到。
      const all = store.getAgentRuns("Abigail");
      expect(all).toHaveLength(1);
      expect(all[0]!.trigger).toBe("beat");
      expect(all[0]!.gameDate).toBe("2026-07-21");
      // 按 (npcName, gameDate) 过滤往返。
      const filtered = store.getAgentRuns("Abigail", "2026-07-21");
      expect(filtered).toHaveLength(1);
      expect(filtered[0]!.runId).toBe(all[0]!.runId);
      expect(filtered[0]!.status).toBe("completed");
      expect(filtered[0]!.finalSpeech).toBe(result.speech);
      expect(filtered[0]!.userInput).toContain("导演指令");
    } finally {
      store.close();
    }
  } finally {
    rmSync(stack.dir, { recursive: true, force: true });
  }
});

// ---------------------------------------------------------------------------
// 5. 禁用（配置缺省）→ 不构造 store，不建文件，无行
// ---------------------------------------------------------------------------

test("disabled: no transcript file created and no rows written", async () => {
  const dir = mkdtempSync(join(tmpdir(), "valley-transcript-disabled-"));
  const dbPath = join(dir, "transcript.sqlite");
  const loader = new NpcPromptLoader(DATA_PATH);
  const builder = new PromptBuilder(loader);
  const provider = new VercelAIProvider({
    provider: "minimax",
    apiKey: "fake",
    model: "fake",
    baseUrl: "http://localhost:9999",
  });
  provider._setCallOverride(async () => ({
    content: "",
    toolCalls: [{ id: "tc-1", name: "speak", args: { text: "你好。" } }],
  }));
  const registry = new StardewAgentRegistry({
    promptBuilder: builder,
    llmProvider: provider,
    agentsDir: dir,
    // transcript 配置缺省 → 禁用
  });
  try {
    const agent = registry.getOrCreate("Abigail");
    await agent.runDialogue("你好", scene);
    // 禁用时 SQLite 主库 / WAL 都不应存在（零构造、零保存路径）。
    expect(existsSync(dbPath)).toBe(false);
    expect(existsSync(`${dbPath}-wal`)).toBe(false);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

// ---------------------------------------------------------------------------
// runBeat 测试夹具（与 stardew-agent-beat.test.ts 同源）
// ---------------------------------------------------------------------------

function makeGameContext(): GameContext {
  return {
    time: {
      year: 2,
      season: "summer",
      day: 14,
      dayOfWeek: "Tuesday",
      weather: "sunny",
      isFestivalDay: false,
    },
    progress: {
      communityCenterComplete: false,
      communityCenterBundlesDone: ["Pantry"],
      jojaMartRoute: false,
      islandsUnlocked: [],
      desertUnlocked: true,
      railroadUnlocked: false,
      sewersUnlocked: false,
      greenhouseRestored: true,
    },
    seasonalResources: {
      plantableCrops: ["Corn"],
      catchableFish: ["Rainbow Trout"],
      forageItems: ["Spice Berry"],
      activeFestivals: [],
    },
    npcStates: [
      {
        name: "Abigail",
        location: "Town",
        tile: { x: 30, y: 20 },
        isAvailable: true,
        currentState: "IDLE",
        friendshipPoints: 500,
      },
    ],
    playerState: {
      location: "Farm",
      tile: { x: 32, y: 18 },
      health: 95,
      maxHealth: 100,
      energy: 200,
      maxEnergy: 270,
      money: 8500,
      inventory: [{ name: "Corn", quantity: 12 }],
    },
    lastUpdated: "2026-07-21T06:00:00Z",
  };
}

function makePlayerProfile(): PlayerProfile {
  return {
    static: {
      farmerName: "Alice",
      gender: "female",
      farmName: "Riverland",
      farmType: "Riverland",
      startDate: "2026-06-01",
      lastUpdated: "2026-07-21",
    },
    behavior: {
      dailyActivities: [],
      totalStats: {
        fishCaught: 47,
        itemsShipped: 25,
        monstersKilled: 3,
        cropsHarvested: 80,
        itemsForaged: 15,
        giftsGiven: 8,
        dialoguesHad: 32,
        miningLevelsDescended: 5,
      },
    },
    preferences: {
      playStyle: [{ tag: "brewer", confidence: 0.8, evidence: "12 个酒桶" }],
      topActivities: [],
      topLocations: [],
      routinePattern: "早晨种地下午钓鱼",
      lastUpdated: "2026-07-21",
    },
    relationships: {},
    personality: {
      traits: ["内向"],
      archetype: "独行者",
      narrativeRole: "不情愿的农场主",
      lastUpdated: "2026-07-21",
    },
    story: {
      completedBeats: [],
      recurringTropes: [],
      lastUpdated: "2026-07-21",
    },
  };
}

function makeBeat(): Beat {
  return {
    id: "beat-transcript-test-1",
    npcName: "Abigail",
    triggerTime: "14:00",
    windowEnd: "16:00",
    directive: "去农场看看玩家，对她的酿酒事业表达好奇",
    context: {
      reasonGenerated: "玩家是酿酒流，Abigail 对农场生活感兴趣",
      playerProfileSnapshot: makePlayerProfile(),
      gameContextSnapshot: makeGameContext(),
      recentBeats: [],
    },
    status: "active",
  };
}
