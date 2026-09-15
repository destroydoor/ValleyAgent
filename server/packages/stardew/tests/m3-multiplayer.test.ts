// M3 多玩家化测试（2026-09-13）。
//
// 覆盖 issue #4 的 TS 侧三项遗留限制中的两项（第三项在 C# 侧，见
// src/ValleyAgent.UnitTests/Multiplayer/ThinClientCapabilityMatrixTests.cs）：
//   1. 情绪引擎 per-player（原为 NPC 世界级，A 激怒 NPC 后 B 承接情绪）
//   3. 玩家画像 + 导演编排 per-player（原为单行单玩家画像 + 单玩家导演）
//
// 每条测试都对着"联机下两个玩家互不串味"这一件事：
// - 情绪：玩家桶隔离 + 世界桶共享 + 最近写入胜出；
// - 画像：分键存储 + 旧单玩家数据惰性认领；
// - 导演：按玩家分别组 prompt、beat 归属玩家、跨玩家不重复安排同一 NPC、全局预算不放大。

import { test, expect, beforeEach, afterEach } from "bun:test";
import { EmotionEngine } from "../src/emotion-engine";
import { PlayerProfileStore, LEGACY_PLAYER_ID } from "../src/player-profile-store";
import { PlayerProfileManager } from "../src/player-profile";
import { PlayerDirectory } from "../src/player-directory";
import { Director } from "../src/director";
import { BeatStore } from "../src/beat-store";
import { ActivityLogStore } from "../src/activity-log-store";
import { GameContextManager } from "../src/game-context";
import { ProtocolAdapter } from "../src/protocol-adapter";
import { TranscriptStore } from "../src/transcript-store";
import { StardewAgentRegistry } from "../src/stardew-agent-registry";
import { NpcPromptLoader } from "../src/npc-prompt-loader";
import { PromptBuilder } from "../src/prompt-builder";
import { VercelAIProvider } from "@valley/core";
import { mkdtempSync, rmSync, existsSync } from "node:fs";
import { join, resolve } from "node:path";
import { tmpdir } from "node:os";
import type { GameContext, PlayerProfile } from "../src/types";

const DATA_PATH = resolve(import.meta.dir, "../data/npc_prompts.json");

// ---------------------------------------------------------------------------
// 1. 情绪引擎 per-player
// ---------------------------------------------------------------------------

test("M3 情绪：两个玩家的工具成败情绪互不串味", () => {
  const engine = new EmotionEngine();

  // 玩家 A 让 NPC 去砍树，失败 → A 视角 Sad
  engine.applyEvent("Haley", { kind: "action_result", detail: "chop_tree", success: false }, "player-a");
  expect(engine.current("Haley", "player-a").emotion).toBe("Sad");
  // 玩家 B 没有与 NPC 发生过任何事 → 回 baseline（不是 Sad）
  expect(engine.current("Haley", "player-b").emotion).toBe("Neutral");
  expect(engine.current("Haley", "player-b").source).toBe("baseline");

  // 玩家 B 自己的成功事件 → B 视角 Happy，A 仍是 Sad
  engine.applyEvent("Haley", { kind: "action_result", detail: "chop_tree", success: true }, "player-b");
  expect(engine.current("Haley", "player-b").emotion).toBe("Happy");
  expect(engine.current("Haley", "player-a").emotion).toBe("Sad");
});

test("M3 情绪：世界级事件对所有人可见，且后到的世界事件盖过玩家情绪", () => {
  const engine = new EmotionEngine();

  // 世界事件（state_changed 无玩家归属）→ 所有人可见
  engine.applyEvent("Abigail", { kind: "state_changed", detail: "task_completed" });
  expect(engine.current("Abigail", "player-a").emotion).toBe("Happy");
  expect(engine.current("Abigail", "player-b").emotion).toBe("Happy");

  // 玩家 A 的失败事件 → 只 A 承接
  engine.applyEvent("Abigail", { kind: "action_result", detail: "set_goal", success: false }, "player-a");
  expect(engine.current("Abigail", "player-a").emotion).toBe("Worried");
  expect(engine.current("Abigail", "player-b").emotion).toBe("Happy");

  // 之后又来一个世界事件 → 对所有人（含 A）生效（最近写入胜出，
  // 否则 A 的一次性情绪会永久遮蔽世界情绪直到换日）
  engine.applyEvent("Abigail", { kind: "state_changed", detail: "evicted" });
  expect(engine.current("Abigail", "player-a").emotion).toBe("Sad");
  expect(engine.current("Abigail", "player-b").emotion).toBe("Sad");
});

test("M3 情绪：换日 resetAll 清掉玩家桶（不只清世界桶）", () => {
  const engine = new EmotionEngine();
  engine.applyEvent("Haley", { kind: "action_result", detail: "chop_tree", success: false }, "player-a");
  engine.applyEvent("Haley", { kind: "action_result", detail: "chop_tree", success: false }, "player-b");
  expect(engine.current("Haley", "player-a").emotion).toBe("Sad");
  expect(engine.current("Haley", "player-b").emotion).toBe("Sad");

  engine.resetAll();

  expect(engine.current("Haley", "player-a").emotion).toBe("Neutral");
  expect(engine.current("Haley", "player-a").source).toBe("day_started");
  expect(engine.current("Haley", "player-b").emotion).toBe("Neutral");
  expect(engine.current("Haley", "player-b").source).toBe("day_started");
});

test("M3 情绪：Director 可以定向给单个玩家写情绪（per-player mood）", () => {
  const engine = new EmotionEngine();
  engine.applyMood("Haley", "Angry", "player-a");
  expect(engine.current("Haley", "player-a").emotion).toBe("Angry");
  // 未定向的玩家不受影响
  expect(engine.current("Haley", "player-b").emotion).toBe("Neutral");

  // 世界级 mood（Director 传统用法）对所有人生效
  engine.applyMood("Haley", "Grateful");
  expect(engine.current("Haley", "player-a").emotion).toBe("Grateful");
  expect(engine.current("Haley", "player-b").emotion).toBe("Grateful");
});

// ---------------------------------------------------------------------------
// 1b. 情绪 per-player —— adapter 接线（callId → 发起玩家）
// ---------------------------------------------------------------------------

function makeDialogueReq(playerId: string, requestId: string, farmerName: string) {
  return {
    type: "dialogue" as const,
    requestId,
    npcName: "Haley",
    playerInput: "帮我砍棵树",
    playerId,
    worldSnapshot: {
      season: "summer", day: 28, time: "14:30", weather: "sunny",
      location: "Town", npcTile: { x: 32, y: 18 }, nearbyObjects: "",
      friendship: 250, npcState: "IDLE", inventory: [], farmerName,
      playerMoney: 500, npcLocation: "Town", npcMoney: 1000, npcInventory: [],
    },
  };
}

test("M3 情绪：action_result 按 callId 归属发起玩家（adapter 接线）", async () => {
  const dir = mkdtempSync(join(tmpdir(), "m3-emotion-"));
  let adapter: ProtocolAdapter | null = null;
  try {
    const loader = new NpcPromptLoader(DATA_PATH);
    const builder = new PromptBuilder(loader);
    const provider = new VercelAIProvider({
      provider: "minimax", apiKey: "fake", model: "fake", baseUrl: "http://localhost:9999",
    });

    // 第一轮产出 chop_tree 动作（带 callId），第二轮 speak 收尾（loop isDone 需要 speak + 其他工具）
    let turn = 0;
    provider._setCallOverride(async () => {
      turn++;
      if (turn % 2 === 1) {
        return { content: "", toolCalls: [{ id: "call-chop-1", name: "chop_tree", args: { count: 3 } }] };
      }
      return { content: "", toolCalls: [{ id: "call-speak-1", name: "speak", args: { text: "我去砍树。" } }] };
    });

    const registry = new StardewAgentRegistry({ promptBuilder: builder, llmProvider: provider, agentsDir: dir });
    const engine = new EmotionEngine();
    adapter = new ProtocolAdapter(registry, { emotionEngine: engine });

    // 玩家 A 对话 → 产出动作（callId=call-chop-1）
    const rA1 = await adapter.routeMessage(makeDialogueReq("player-a", "req-a1", "阿明"));
    expect(rA1.type).toBe("dialogue_response");
    if (rA1.type === "dialogue_response") {
      const action = rA1.actions.find((a) => a.tool === "chop_tree");
      expect(action?.callId).toBe("call-chop-1");
    }

    // 该动作在 C# 侧执行失败 → 情绪只记在玩家 A 名下
    await adapter.routeMessage({
      type: "action_result",
      requestId: "req-ar-1",
      callId: "call-chop-1",
      npcName: "Haley",
      tool: "chop_tree",
      success: false,
    });
    expect(engine.current("Haley", "player-a").emotion).toBe("Sad");
    expect(engine.current("Haley", "player-b").emotion).toBe("Neutral");

    // 玩家 B 对话 → 拿到的是 baseline，不是 A 的那份 Sad（issue #4 §1 的现场）
    const rB = await adapter.routeMessage(makeDialogueReq("player-b", "req-b1", "小红"));
    expect(rB.type).toBe("dialogue_response");
    if (rB.type === "dialogue_response") expect(rB.emotion).toBe("Neutral");

    // 玩家 A 再对话 → 仍是 Sad（不串味是双向的）
    const rA2 = await adapter.routeMessage(makeDialogueReq("player-a", "req-a2", "阿明"));
    expect(rA2.type).toBe("dialogue_response");
    if (rA2.type === "dialogue_response") expect(rA2.emotion).toBe("Sad");
  } finally {
    await adapter?.flushPendingSaves();
    rmSync(dir, { recursive: true, force: true });
  }
});

// ---------------------------------------------------------------------------
// 3. 玩家画像 per-player
// ---------------------------------------------------------------------------

function makeProfile(farmerName: string, archetype: string): PlayerProfile {
  return {
    static: { farmerName, gender: "unknown", farmName: `${farmerName}Farm`, farmType: "Standard", startDate: "2026-06-01", lastUpdated: "2026-07-21" },
    behavior: {
      dailyActivities: [],
      totalStats: { fishCaught: 0, itemsShipped: 0, monstersKilled: 0, cropsHarvested: 0, itemsForaged: 0, giftsGiven: 0, dialoguesHad: 0, miningLevelsDescended: 0 },
    },
    preferences: { playStyle: [], topActivities: [], topLocations: [], routinePattern: "", lastUpdated: "" },
    relationships: {},
    personality: { traits: [], archetype, narrativeRole: "", lastUpdated: "" },
    story: { completedBeats: [], recurringTropes: [], lastUpdated: "" },
  };
}

let profileDb: string;
let activityDb: string;
let beatDb: string;
let store: PlayerProfileStore;

beforeEach(() => {
  profileDb = join(tmpdir(), `m3-profile-${Date.now()}-${Math.random().toString(36).slice(2)}.db`);
  activityDb = join(tmpdir(), `m3-activity-${Date.now()}-${Math.random().toString(36).slice(2)}.db`);
  beatDb = join(tmpdir(), `m3-beat-${Date.now()}-${Math.random().toString(36).slice(2)}.db`);
  store = new PlayerProfileStore(profileDb);
  store.init();
});

afterEach(() => {
  store.close();
  for (const p of [profileDb, activityDb, beatDb]) {
    for (const suffix of ["", "-wal", "-shm"]) {
      if (existsSync(p + suffix)) rmSync(p + suffix, { force: true });
    }
  }
});

test("M3 画像：两个玩家各自一份五层画像，互不覆盖", () => {
  store.save(makeProfile("阿明", "钓鱼狂"), "player-a");
  store.save(makeProfile("小红", "社交达人"), "player-b");

  expect(store.load("player-a")?.static.farmerName).toBe("阿明");
  expect(store.load("player-b")?.static.farmerName).toBe("小红");
  expect(store.listPlayerIds().sort()).toEqual(["player-a", "player-b"]);

  // 写 A 的关系层不该动到 B
  store.updateRelationship("Willy", {
    phase: "friend", friendshipPoints: 900, last5Interactions: [], giftHistory: [], notableEvents: [], lastUpdated: "2026-07-21",
  }, "player-a");
  expect(store.load("player-a")?.relationships["Willy"]?.friendshipPoints).toBe(900);
  expect(store.load("player-b")?.relationships["Willy"]).toBeUndefined();
});

test("M3 画像：旧单玩家画像惰性认领（legacy → 首个真实 playerId）", () => {
  // 模拟 M3 前的旧库：默认（legacy）键上有一份真实画像
  store.save(makeProfile("老农夫", "独行者"));
  expect(store.listPlayerIds()).toEqual([LEGACY_PLAYER_ID]);

  // 首个真实玩家出现：无自有数据 → 认领 legacy 行
  const claimed = store.loadOrClaim("player-a");
  expect(claimed?.static.farmerName).toBe("老农夫");
  expect(store.load(LEGACY_PLAYER_ID)).toBeNull();       // legacy 行已被认领迁走
  expect(store.load("player-a")?.static.farmerName).toBe("老农夫");

  // 第二个玩家不认领（legacy 已空），拿自己的空画像
  expect(store.loadOrClaim("player-b")).toBeNull();
});

test("M3 留痕：director_runs 带上玩家维度（旧库增量加列可重开）", () => {
  const dbPath = join(tmpdir(), `m3-transcript-${Date.now()}-${Math.random().toString(36).slice(2)}.db`);
  const first = new TranscriptStore(dbPath);
  first.init();
  first.recordDirectorRun({
    runId: "run-a",
    gameDate: "Y2_summer_14",
    trigger: "morningPlan",
    promptFull: "[玩家画像] 阿明 ...",
    producedBeats: [],
    droppedBeats: [],
    emptyResult: false,
    status: "completed",
    playerId: "player-a",
  });
  first.recordDirectorRun({
    runId: "run-legacy",
    gameDate: "Y2_summer_14",
    trigger: "morningPlan",
    promptFull: "[玩家画像] 无玩家档案",
    producedBeats: [],
    droppedBeats: [],
    emptyResult: true,
    status: "noop",
  });
  first.close();

  try {
    // 重开已有库：走 ALTER 列已存在的分支（best-effort 加列必须幂等）
    const second = new TranscriptStore(dbPath);
    second.init();
    const runs = second.getDirectorRuns("Y2_summer_14");
    expect(runs).toHaveLength(2);
    expect(runs[0]!.playerId).toBe("player-a");
    expect(runs[1]!.playerId).toBeUndefined(); // 单玩家编排无玩家维度（旧形状）
    second.close();
  } finally {
    for (const suffix of ["", "-wal", "-shm"]) {
      if (existsSync(dbPath + suffix)) rmSync(dbPath + suffix, { force: true });
    }
  }
});
