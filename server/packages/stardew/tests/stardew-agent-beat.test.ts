// Tests for StardewAgent.runBeat (Task 9).
// Run: bun test tests/stardew-agent-beat.test.ts

import { test, expect } from "bun:test";
import { StardewAgent } from "../src/stardew-agent";
import { AgentMemory } from "../src/agent-memory";
import { NpcPromptLoader } from "../src/npc-prompt-loader";
import { PromptBuilder } from "../src/prompt-builder";
import { PlayerProfileManager } from "../src/player-profile";
import { PlayerProfileStore } from "../src/player-profile-store";
import { ActivityLogStore } from "../src/activity-log-store";
import { GameContextManager } from "../src/game-context";
import { VercelAIProvider } from "@valley/core";
import { resolve } from "path";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { rmSync, existsSync } from "node:fs";
import type { Beat, GameContext, PlayerProfile } from "../src/types";

const DATA_PATH = resolve(import.meta.dir, "../data/npc_prompts.json");

// ---------------------------------------------------------------------------
// Factory helpers
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
    id: "beat-test-1",
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

/**
 * Build a mock LLM provider that returns a canned beat response with
 * BOTH a speak tool call AND a non-speak (emote) tool call. This satisfies
 * runBeat's shouldStopAfterTurn (hasSpeak && hasOther).
 */
function makeBeatMockLlmProvider(): VercelAIProvider {
  const provider = new VercelAIProvider({
    provider: "minimax",
    apiKey: "fake-key",
    model: "fake-model",
    baseUrl: "http://localhost:9999",
  });
  provider._setCallOverride(async () => ({
    content: "（思考中）",
    toolCalls: [
      { id: "tc-1", name: "speak", args: { text: "嘿，听说你最近在酿酒？我能看看吗？" } },
      { id: "tc-2", name: "emote", args: { emote_id: "happy" } },
    ],
  }));
  return provider;
}

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

test("runBeat returns speech + actions when LLM calls speak + emote", async () => {
  const loader = new NpcPromptLoader(DATA_PATH);
  const builder = new PromptBuilder(loader);
  const memory = new AgentMemory("Abigail", "/tmp/x");
  const provider = makeBeatMockLlmProvider();

  const agent = new StardewAgent({
    name: "Abigail",
    memory,
    promptBuilder: builder,
    llmProvider: provider,
    maxTurns: 8,
  });

  // Wire up profile + game-context managers (required by runBeat).
  const profileDbPath = join(tmpdir(), `beat-test-profile-${Date.now()}-${Math.random().toString(36).slice(2)}.db`);
  const activityDbPath = join(tmpdir(), `beat-test-activity-${Date.now()}-${Math.random().toString(36).slice(2)}.db`);
  const profileStore = new PlayerProfileStore(profileDbPath);
  const activityStore = new ActivityLogStore(activityDbPath);
  profileStore.init();
  activityStore.init();
  const profileMgr = new PlayerProfileManager(profileStore, activityStore, {
    callLlm: async () => ({ text: "", usage: { promptTokens: 0, completionTokens: 0 } }),
  });
  profileMgr.initProfile(makePlayerProfile().static);

  const gameCtxMgr = new GameContextManager();
  gameCtxMgr.update(makeGameContext());

  const result = await agent.runBeat(
    makeBeat(),
    makeGameContext(),
    profileMgr,
    gameCtxMgr,
  );

  // Speech comes from the speak tool's text arg.
  expect(result.speech).toBe("嘿，听说你最近在酿酒？我能看看吗？");
  // emote is captured as a non-speak action.
  expect(result.actions).toHaveLength(1);
  expect(result.actions[0]!.tool).toBe("emote");
  // Both tool calls recorded.
  expect(result.toolCalls).toHaveLength(2);
  expect(result.toolCalls[0]!.name).toBe("speak");
  expect(result.toolCalls[1]!.name).toBe("emote");

  // Cleanup
  profileStore.close();
  activityStore.close();
  if (existsSync(profileDbPath)) rmSync(profileDbPath, { force: true });
  if (existsSync(activityDbPath)) rmSync(activityDbPath, { force: true });
});
