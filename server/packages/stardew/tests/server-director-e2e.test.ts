// 导演系统 server 级端到端测试：真实 Bun.serve + WebSocket 客户端。
// Run: bun test packages/stardew/tests/server-director-e2e.test.ts
//
// 验证 startServer 的导演接线全链路（2026-09-12 DirectorAgent 工具大脑版）：
//   WS 客户端 → game_context_sync → day_started（probability=1）
//   → DirectorAgent.runDayPlan（chatWithTools 走 llmCallOverride，注入 spawn_beat 工具调用）
//   → director_command（tool=spawn_beat）+ allocate_agent 推回 WS 客户端
//   → director_runs 落 transcript.sqlite（留痕活化）。

import { test, expect, spyOn } from "bun:test";
import { startServer } from "../src/server";
import { TranscriptStore } from "../src/transcript-store";
import { mkdtempSync, rmSync, copyFileSync, existsSync } from "node:fs";
import { join, resolve } from "node:path";
import { tmpdir } from "node:os";

const DATA_PATH = resolve(import.meta.dir, "../data/npc_prompts.json");

function makeContext() {
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

interface WsMessage {
  type: string;
  requestId?: string;
  npcName?: string;
  keepUntilIso?: string;
  tool?: string;
}

test(
  "server e2e: game_context_sync + day_started over real WS → director_command + allocate_agent pushed back + director_runs recorded",
  async () => {
    const tmp = mkdtempSync(join(tmpdir(), "valley-director-e2e-"));
    // dataPath 必须指向临时副本 —— server 会在 dirname(dataPath)/director 建 SQLite。
    const dataPath = join(tmp, "npc_prompts.json");
    copyFileSync(DATA_PATH, dataPath);

    // 状态化 override：第 1 轮返回 spawn_beat 工具调用，第 2 轮返回纯文本收尾
    // （工具循环自然结束：最后一轮 assistant 无 toolCalls → shouldStopAfterTurn）。
    let llmTurn = 0;
    const handle = await startServer({
      port: 0,
      hostname: "127.0.0.1",
      dataPath,
      agentsDir: join(tmp, "agents"),
      llmConfig: {
        provider: "minimax",
        apiKey: "fake",
        model: "fake",
        baseUrl: "http://localhost:9999",
      },
      llmCallOverride: async () => {
        llmTurn++;
        if (llmTurn === 1) {
          return {
            content: "（编排中）",
            toolCalls: [
              {
                id: "tc-spawn-1",
                name: "spawn_beat",
                args: { npc: "Willy", sceneDesc: "他在码头修补渔网，看见玩家会自然打招呼", durationMinutes: 120 },
              },
            ],
          };
        }
        return { content: "今天的编排完成了。", toolCalls: [] };
      },
      transcript: { enabled: true, dir: tmp },
      directorTriggerProbability: 1.0,
    });

    const logSpy = spyOn(console, "log").mockImplementation(() => {});
    try {
      // --- WS 客户端连接 ---
      const ws = new WebSocket(`ws://127.0.0.1:${handle.port}`);
      const received: WsMessage[] = [];
      const opened = new Promise<void>((res, rej) => {
        ws.onopen = () => res();
        ws.onerror = (e) => rej(new Error(`ws error: ${String(e)}`));
      });
      ws.onmessage = (ev) => {
        try {
          received.push(JSON.parse(String(ev.data)) as WsMessage);
        } catch { /* 非 JSON 帧忽略 */ }
      };
      await opened;

      // --- game_context_sync → ack ---
      ws.send(JSON.stringify({
        type: "game_context_sync",
        requestId: "e2e-ctx",
        context: makeContext(),
      }));
      await Bun.sleep(200);

      // --- day_started → director_command + allocate_agent + ack ---
      ws.send(JSON.stringify({
        type: "day_started",
        requestId: "e2e-day",
        dateIso: "Y2_summer_14",
      }));

      const deadline = Date.now() + 8000;
      while (Date.now() < deadline) {
        const gotDirectorCmd = received.some((m) => m.type === "director_command");
        const gotAlloc = received.some((m) => m.type === "allocate_agent");
        const gotAck = received.some((m) => m.type === "ack" && m.requestId === "e2e-day");
        if (gotDirectorCmd && gotAlloc && gotAck) break;
        await Bun.sleep(100);
      }

      // --- 断言 WS 侧结果 ---
      const ackCtx = received.find((m) => m.type === "ack" && m.requestId === "e2e-ctx");
      expect(ackCtx).toBeDefined();

      // director_command：spawn_beat 真实下发（DirectorAgent → C#）。
      const cmd = received.find((m) => m.type === "director_command");
      expect(cmd).toBeDefined();
      expect(cmd!.tool).toBe("spawn_beat");

      // allocate_agent：beat NPC 保活，keepUntilIso 为 ISO 8601。
      const alloc = received.find((m) => m.type === "allocate_agent");
      expect(alloc).toBeDefined();
      expect(alloc!.npcName).toBe("Willy");
      expect(alloc!.keepUntilIso).toBeDefined();
      expect(!Number.isNaN(Date.parse(alloc!.keepUntilIso!))).toBe(true);
      expect(typeof alloc!.requestId).toBe("string");
      expect(alloc!.requestId!.length).toBeGreaterThan(0);
      const ackDay = received.find((m) => m.type === "ack" && m.requestId === "e2e-day");
      expect(ackDay).toBeDefined();

      ws.close();
      await handle.stop();

      // --- 断言留痕侧结果：director_runs 已记录 completed 调度 ---
      const storePath = join(tmp, "transcript.sqlite");
      expect(existsSync(storePath)).toBe(true);
      const store = new TranscriptStore(storePath);
      store.init();
      try {
        const runs = store.getDirectorRuns("Y2_summer_14");
        expect(runs.length).toBeGreaterThanOrEqual(1);
        const completed = runs.find((r) => r.status === "completed");
        expect(completed).toBeDefined();
        expect(completed!.trigger).toBe("dayPlan");
        expect(completed!.emptyResult).toBe(false);
        expect(completed!.promptFull).toContain("叙事导演");
        expect((completed!.producedBeats as unknown[]).length).toBe(1);
      } finally {
        store.close();
      }
    } finally {
      logSpy.mockRestore();
      rmSync(tmp, { recursive: true, force: true });
    }
  },
);
