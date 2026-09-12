// Tests for ProtocolAdapter day_started routing.
// Run: bun test packages/stardew/tests/protocol-adapter-day-started.test.ts
//
// Verifies:
//   1. day_started with directorTriggerProbability=1.0 runs DirectorAgent.runDayPlan
//      and sends one allocate_agent per spawned beat (keepUntilIso = ISO 8601)
//   2. day_started with directorTriggerProbability=0.0 does not run dayPlan
//   3. duplicate day_started still passes through (观测告警，不去重)
//
// 2026-09-12 Director 有效化：Director（morningPlan beat-JSON 管线）退役，
// 由 DirectorAgent（工具大脑）替代——stub 结构随接口更新。

import { test, expect, spyOn } from "bun:test";
import { ProtocolAdapter, type ProtocolAdapterOptions } from "../src/protocol-adapter";
import type { DirectorAgent, DirectorDayPlanSummary, SpawnedBeatInfo } from "../src/director-agent";
import { NpcPromptLoader } from "../src/npc-prompt-loader";
import { PromptBuilder } from "../src/prompt-builder";
import { VercelAIProvider } from "@valley/core";
import { StardewAgentRegistry } from "../src/stardew-agent-registry";
import { mkdtempSync, rmSync } from "fs";
import { join, resolve } from "path";
import { tmpdir } from "os";

const DATA_PATH = resolve(import.meta.dir, "../data/npc_prompts.json");

/**
 * Minimal DirectorAgent stub: runDayPlan returns a fixed spawned-beat list and
 * records how many times it was called. Cast via `unknown` since the stub only
 * implements the runDayPlan() method that handleDayStarted calls.
 */
interface DirectorAgentStub {
  dayPlanCalls: number;
  lastDirectorContext?: string;
  runDayPlan(directorContext?: string): Promise<DirectorDayPlanSummary>;
}

function makeDirectorAgentStub(beats: SpawnedBeatInfo[]): DirectorAgentStub {
  return {
    dayPlanCalls: 0,
    async runDayPlan(directorContext?: string) {
      this.dayPlanCalls++;
      this.lastDirectorContext = directorContext;
      return {
        spawnedBeats: beats,
        rejectedCalls: [],
        commandsSent: beats.length,
        status: beats.length > 0 ? "completed" : "noop",
      };
    },
  };
}

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

test("day_started with probability=1.0 runs dayPlan and sends allocate_agent per beat (ISO keepUntil)", async () => {
  const dir = mkdtempSync(join(tmpdir(), "valley-day-started-trigger-"));
  const logSpy = spyOn(console, "log").mockImplementation(() => {});
  try {
    const registry = makeRegistry(dir);
    const beats = [
      { npcName: "Abigail", durationMinutes: 120 },
      { npcName: "Sebastian", durationMinutes: 90 },
    ];
    const directorAgent = makeDirectorAgentStub(beats);
    const sent: unknown[] = [];
    const options: ProtocolAdapterOptions = {
      sendToCsharp: (msg: unknown) => { sent.push(msg); },
      directorAgent: directorAgent as unknown as DirectorAgent,
      directorTriggerProbability: 1.0,
    };
    const adapter = new ProtocolAdapter(registry, options);

    const resp = await adapter.routeMessage({
      type: "day_started",
      requestId: "req-day-1",
      dateIso: "Y2_summer_14",
      directorContext: "今日全局视野文本",
    });

    expect(resp).toEqual({ type: "ack", requestId: "req-day-1" });
    expect(directorAgent.dayPlanCalls).toBe(1);
    // directorContext 必须透传给导演大脑（2026-09-12 起被消费）。
    expect(directorAgent.lastDirectorContext).toBe("今日全局视野文本");
    expect(sent.length).toBe(2);
    expect(sent[0]).toMatchObject({ type: "allocate_agent", npcName: "Abigail" });
    expect(sent[1]).toMatchObject({ type: "allocate_agent", npcName: "Sebastian" });
    // keepUntilIso 必须是 ISO 8601（旧实现传 "16:00" 非 ISO → C# 解析 fail-open）。
    for (const msg of sent) {
      const m = msg as { keepUntilIso?: string; requestId?: string };
      expect(typeof m.requestId).toBe("string");
      expect(m.requestId!.length).toBeGreaterThan(0);
      expect(m.keepUntilIso).toBeDefined();
      expect(!Number.isNaN(Date.parse(m.keepUntilIso!))).toBe(true);
    }
  } finally {
    logSpy.mockRestore();
    rmSync(dir, { recursive: true, force: true });
  }
});

test("day_started with probability=0.0 does not run dayPlan and sends nothing", async () => {
  const dir = mkdtempSync(join(tmpdir(), "valley-day-started-skip-"));
  const logSpy = spyOn(console, "log").mockImplementation(() => {});
  try {
    const registry = makeRegistry(dir);
    const directorAgent = makeDirectorAgentStub([
      { npcName: "Abigail", durationMinutes: 120 },
    ]);
    const sent: unknown[] = [];
    const options: ProtocolAdapterOptions = {
      sendToCsharp: (msg: unknown) => { sent.push(msg); },
      directorAgent: directorAgent as unknown as DirectorAgent,
      directorTriggerProbability: 0.0,
    };
    const adapter = new ProtocolAdapter(registry, options);

    const resp = await adapter.routeMessage({
      type: "day_started",
      requestId: "req-day-2",
      dateIso: "Y2_summer_15",
    });

    expect(resp).toEqual({ type: "ack", requestId: "req-day-2" });
    expect(directorAgent.dayPlanCalls).toBe(0);
    expect(sent.length).toBe(0);
  } finally {
    logSpy.mockRestore();
    rmSync(dir, { recursive: true, force: true });
  }
});

test("day_started without directorAgent wired degrades silently (ack, no sends)", async () => {
  const dir = mkdtempSync(join(tmpdir(), "valley-day-started-unwired-"));
  const logSpy = spyOn(console, "log").mockImplementation(() => {});
  try {
    const registry = makeRegistry(dir);
    const sent: unknown[] = [];
    const options: ProtocolAdapterOptions = {
      sendToCsharp: (msg: unknown) => { sent.push(msg); },
      directorTriggerProbability: 1.0,
    };
    const adapter = new ProtocolAdapter(registry, options);

    const resp = await adapter.routeMessage({
      type: "day_started",
      requestId: "req-day-3",
      dateIso: "Y2_summer_16",
    });

    expect(resp).toEqual({ type: "ack", requestId: "req-day-3" });
    expect(sent.length).toBe(0);
  } finally {
    logSpy.mockRestore();
    rmSync(dir, { recursive: true, force: true });
  }
});
