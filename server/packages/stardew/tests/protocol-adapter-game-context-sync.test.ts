// game_context_sync 处理器测试（导演管道复活关键路径）。
// Run: bun test packages/stardew/tests/protocol-adapter-game-context-sync.test.ts
//
// 背景：C# 自 2026-08-09 起在 day_started 前发送 game_context_sync；TS 端缺失
// 该处理器时 GameContextManager 永远为空 → Director.morningPlan 恒走
// no-game-context 分支返回 []（导演 LLM 从未运行的根因）。
//
// 覆盖：
//   1. gameCtxMgr 已接线：routeMessage(game_context_sync) → ack + 上下文入库；
//   2. gameCtxMgr 未接线：ack + 不抛（降级可观测，行为不崩溃）；
//   3. 最新快照覆盖旧快照（多次 sync 取最后一次）。

import { test, expect, spyOn } from "bun:test";
import { ProtocolAdapter } from "../src/protocol-adapter";
import { NpcPromptLoader } from "../src/npc-prompt-loader";
import { PromptBuilder } from "../src/prompt-builder";
import { VercelAIProvider } from "@valley/core";
import { StardewAgentRegistry } from "../src/stardew-agent-registry";
import { GameContextManager } from "../src/game-context";
import type { GameContext } from "../src/types";
import { mkdtempSync, rmSync } from "node:fs";
import { join, resolve } from "node:path";
import { tmpdir } from "node:os";

const DATA_PATH = resolve(import.meta.dir, "../data/npc_prompts.json");

function makeRegistry(dir: string): StardewAgentRegistry {
  const loader = new NpcPromptLoader(DATA_PATH);
  const builder = new PromptBuilder(loader);
  const provider = new VercelAIProvider({
    provider: "minimax",
    apiKey: "fake",
    model: "fake",
    baseUrl: "http://localhost:9999",
  });
  return new StardewAgentRegistry({
    promptBuilder: builder,
    llmProvider: provider,
    agentsDir: dir,
  });
}

function makeContext(day: number, money: number): GameContext {
  return {
    time: {
      year: 2,
      season: "summer",
      day,
      dayOfWeek: "Tuesday",
      weather: "sunny",
      isFestivalDay: false,
    },
    progress: {
      communityCenterComplete: false,
      communityCenterBundlesDone: [],
      jojaMartRoute: false,
      islandsUnlocked: [],
      desertUnlocked: false,
      railroadUnlocked: false,
      sewersUnlocked: false,
      greenhouseRestored: false,
    },
    seasonalResources: {
      plantableCrops: [],
      catchableFish: [],
      forageItems: [],
      activeFestivals: [],
    },
    npcStates: [
      { name: "Willy", location: "Town", tile: { x: 10, y: 20 }, isAvailable: true, currentState: "IDLE", friendshipPoints: 500 },
    ],
    playerState: {
      location: "Farm",
      tile: { x: 32, y: 18 },
      health: 95,
      maxHealth: 100,
      energy: 200,
      maxEnergy: 270,
      money,
      inventory: [],
    },
    lastUpdated: "2026-08-09T06:00:00Z",
  };
}

test("game_context_sync with wired gameCtxMgr: ack + context stored in GameContextManager", async () => {
  const dir = mkdtempSync(join(tmpdir(), "valley-ctx-sync-wired-"));
  const logSpy = spyOn(console, "log").mockImplementation(() => {});
  try {
    const registry = makeRegistry(dir);
    const gameCtxMgr = new GameContextManager();
    const adapter = new ProtocolAdapter(registry, { gameCtxMgr });

    const resp = await adapter.routeMessage({
      type: "game_context_sync",
      requestId: "req-ctx-1",
      context: makeContext(14, 8500),
    });

    expect(resp).toEqual({ type: "ack", requestId: "req-ctx-1" });
    const ctx = gameCtxMgr.getCurrent();
    expect(ctx).not.toBeNull();
    expect(ctx!.time.day).toBe(14);
    expect(ctx!.playerState.money).toBe(8500);
    expect(ctx!.npcStates[0]!.name).toBe("Willy");
  } finally {
    logSpy.mockRestore();
    rmSync(dir, { recursive: true, force: true });
  }
});

test("game_context_sync without gameCtxMgr: ack + no throw (graceful, observable)", async () => {
  const dir = mkdtempSync(join(tmpdir(), "valley-ctx-sync-unwired-"));
  const logSpy = spyOn(console, "log").mockImplementation(() => {});
  try {
    const registry = makeRegistry(dir);
    const adapter = new ProtocolAdapter(registry); // 无 options —— gameCtxMgr 未接线

    const resp = await adapter.routeMessage({
      type: "game_context_sync",
      requestId: "req-ctx-2",
      context: makeContext(15, 100),
    });

    expect(resp).toEqual({ type: "ack", requestId: "req-ctx-2" });
  } finally {
    logSpy.mockRestore();
    rmSync(dir, { recursive: true, force: true });
  }
});

test("game_context_sync: latest snapshot overwrites previous", async () => {
  const dir = mkdtempSync(join(tmpdir(), "valley-ctx-sync-overwrite-"));
  const logSpy = spyOn(console, "log").mockImplementation(() => {});
  try {
    const registry = makeRegistry(dir);
    const gameCtxMgr = new GameContextManager();
    const adapter = new ProtocolAdapter(registry, { gameCtxMgr });

    await adapter.routeMessage({
      type: "game_context_sync",
      requestId: "req-ctx-3a",
      context: makeContext(14, 8500),
    });
    await adapter.routeMessage({
      type: "game_context_sync",
      requestId: "req-ctx-3b",
      context: makeContext(15, 4200),
    });

    const ctx = gameCtxMgr.getCurrent();
    expect(ctx!.time.day).toBe(15);
    expect(ctx!.playerState.money).toBe(4200);
  } finally {
    logSpy.mockRestore();
    rmSync(dir, { recursive: true, force: true });
  }
});
