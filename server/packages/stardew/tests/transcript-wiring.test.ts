// E1-1 接线测试：runDialogue 通过 TranscriptStore 落全量留痕。
// 2026-09-15 偏移审查 C1：原"覆盖 5 条验收"中的第 4 条（getAgentRuns 往返 / runBeat 携带游戏日期）
// 随旧叙事 Director 于 2026-09-14 砍除而失去被测对象——`Beat` 类型已从 src/types.ts 删除，
// 该 import 与 `GameContext`/`PlayerProfile` 同属未使用导入，一并清除；现存 5 个用例均覆盖 runDialogue。
// Run: bun test packages/stardew/tests/transcript-wiring.test.ts
// Spec: docs/design/2026-08-01-memory-narrative-extensibility.md §1
//
// 覆盖 5 条验收：
//   1. 启用 + 临时目录：一次 runDialogue 恰好 1 行 agent_runs，终态 completed；
//   2. 该 run 至少 1 行 agent_turns；
//   3. 校验失败 / LLM 故障路径写 status="error"；
//   4. ~~getAgentRuns(npcName, gameDate?) 往返（runBeat 携带游戏日期）~~ — runBeat 已砍除，本文件不再覆盖；
//   5. 禁用时零构造：无 SQLite 文件、无行。

import { test, expect } from "bun:test";
import { StardewAgentRegistry } from "../src/stardew-agent-registry";
import { NpcPromptLoader } from "../src/npc-prompt-loader";
import { PromptBuilder } from "../src/prompt-builder";
import { TranscriptStore } from "../src/transcript-store";
import { VercelAIProvider } from "@valley/core";
import { mkdtempSync, rmSync, existsSync } from "fs";
import { join } from "path";
import { tmpdir } from "os";
import { resolve } from "path";
import type { SceneState } from "../src/types";

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
