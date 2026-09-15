// Tests for ProtocolAdapter day_started routing (Task 13).
// Run: bun test packages/stardew/tests/protocol-adapter-day-started.test.ts
//
// 2026-09-14 旧叙事 Director 砍除后，day_started 的行为只剩：
//   1. 无条件触发情绪引擎 resetAll（2026-08-17 步骤 3）
//   2. 同日重复 day_started 告警（2026-09-11 观测补强）

import { test, expect, spyOn } from "bun:test";
import { ProtocolAdapter } from "../src/protocol-adapter";
import { NpcPromptLoader } from "../src/npc-prompt-loader";
import { PromptBuilder } from "../src/prompt-builder";
import { VercelAIProvider } from "@valley/core";
import { StardewAgentRegistry } from "../src/stardew-agent-registry";
import { EmotionEngine } from "../src/emotion-engine";
import { mkdtempSync, rmSync } from "fs";
import { join } from "path";
import { tmpdir } from "os";
import { resolve } from "path";

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

test("day_started resets the emotion engine and returns ack", async () => {
  const dir = mkdtempSync(join(tmpdir(), "valley-day-started-reset-"));
  const logSpy = spyOn(console, "log").mockImplementation(() => {});
  try {
    const registry = makeRegistry(dir);
    const emotionEngine = new EmotionEngine();
    let resetCalls = 0;
    emotionEngine.resetAll = () => { resetCalls++; };
    const adapter = new ProtocolAdapter(registry, {
      sendToCsharp: () => {},
      emotionEngine,
    });

    const resp = await adapter.routeMessage({
      type: "day_started",
      requestId: "req-day-1",
      dateIso: "Y2_summer_14",
    });

    expect(resp).toEqual({ type: "ack", requestId: "req-day-1" });
    expect(resetCalls).toBe(1);
  } finally {
    logSpy.mockRestore();
    rmSync(dir, { recursive: true, force: true });
  }
});

// 2026-09-11 观测补强：联机下同一游戏日多条 day_started 此前无重复标记
// （"每玩家一个导演"误读的直接来源）。只加告警不去重——首次不告警，
// 第二次起 console.warn 带 count，行为不变（仍正常路由返回 ack）。
test("duplicate day_started for the same date warns from the second occurrence onward (no behavior change)", async () => {
  const dir = mkdtempSync(join(tmpdir(), "valley-day-started-dup-"));
  const logSpy = spyOn(console, "log").mockImplementation(() => {});
  const warnSpy = spyOn(console, "warn").mockImplementation(() => {});
  const dupWarns = () => warnSpy.mock.calls.map((c) => c.join(" ")).filter((s) => s.includes("duplicate day_started"));
  try {
    const registry = makeRegistry(dir);
    const adapter = new ProtocolAdapter(registry, {
      sendToCsharp: () => {},
    });

    // 第一次：不告警，正常 ack
    const resp1 = await adapter.routeMessage({ type: "day_started", requestId: "req-dup-1", dateIso: "Y2_fall_03" });
    expect(resp1).toEqual({ type: "ack", requestId: "req-dup-1" });
    expect(dupWarns().length).toBe(0);

    // 第二次：告警 count=2，仍正常 ack（不去重、不改行为）
    const resp2 = await adapter.routeMessage({ type: "day_started", requestId: "req-dup-2", dateIso: "Y2_fall_03" });
    expect(resp2).toEqual({ type: "ack", requestId: "req-dup-2" });
    expect(dupWarns().length).toBe(1);
    expect(dupWarns()[0]).toContain("date=Y2_fall_03");
    expect(dupWarns()[0]).toContain("count=2");

    // 第三次：count=3（计数递增，非布尔标记）
    await adapter.routeMessage({ type: "day_started", requestId: "req-dup-3", dateIso: "Y2_fall_03" });
    expect(dupWarns().length).toBe(2);
    expect(dupWarns()[1]).toContain("count=3");

    // 不同日期：独立计数，首次不告警
    await adapter.routeMessage({ type: "day_started", requestId: "req-dup-4", dateIso: "Y2_fall_04" });
    expect(dupWarns().length).toBe(2);
  } finally {
    warnSpy.mockRestore();
    logSpy.mockRestore();
    rmSync(dir, { recursive: true, force: true });
  }
});
