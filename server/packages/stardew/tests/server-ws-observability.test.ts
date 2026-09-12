// Server 级 WS 观测测试（2026-09-11 观测补强：连接生命周期可见性）。
// Run: bun test packages/stardew/tests/server-ws-observability.test.ts
//
// 验证 startServer 的 WS 观测三件套（真实 Bun.serve + WebSocket 客户端，
// 基建仿 server-director-e2e.test.ts）：
//   1. open：第二连接顶替仍开着的活跃连接 → REPLACING 告警（"每玩家一个导演"
//      误读的观测面）；
//   2. close：活跃连接断开 → activeWs 清除 + "(was active connection)" 标记；
//   3. sendToCsharp：无活跃连接时主动消息不再静默丢弃 → dropped 告警（只带 type）。
//
// 场景编排：A 连接 → B 连接（顶替 A，REPLACING 告警）→ B 断开（was active，
// activeWs=null）→ A（stale 但仍开着）发 day_started(prob=1) → DirectorAgent 产出
// allocate_agent 经 sendToCsharp 投递 → 无活跃连接 → dropped 告警；ack 仍经
// 请求级 ws 正常回到 A。

import { test, expect, spyOn } from "bun:test";
import { startServer } from "../src/server";
import { mkdtempSync, rmSync, copyFileSync } from "node:fs";
import { join, resolve } from "node:path";
import { tmpdir } from "node:os";

const DATA_PATH = resolve(import.meta.dir, "../data/npc_prompts.json");

// DirectorAgent（2026-09-12 工具大脑）：第 1 轮返回 spawn_beat 工具调用，
// 第 2 轮纯文本收尾。
let llmTurn = 0;
const toolCallReply = () =>
  llmTurn++ === 0
    ? {
        content: "（编排中）",
        toolCalls: [
          { id: "tc-1", name: "spawn_beat", args: { npc: "Willy", sceneDesc: "他在码头修补渔网", durationMinutes: 120 } },
        ],
      }
    : { content: "今天的编排完成了。", toolCalls: [] };

// DirectorAgent 无 game context 时直接返回空（"no game context" 分支），
// 发不出 allocate_agent —— 必须先发 game_context_sync（与 e2e 测试一致）。
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
    lastUpdated: "2026-09-11T06:00:00Z",
  };
}

function openWs(url: string): Promise<WebSocket> {
  return new Promise((res, rej) => {
    const ws = new WebSocket(url);
    ws.onopen = () => res(ws);
    ws.onerror = (e) => rej(new Error(`ws error: ${String(e)}`));
  });
}

function closeWs(ws: WebSocket): Promise<void> {
  return new Promise((res) => {
    ws.onclose = () => res();
    ws.close();
  });
}

test(
  "server ws observability: replace warn on superseded connection, active-close marker, drop warn for sendToCsharp without active ws",
  async () => {
    const tmp = mkdtempSync(join(tmpdir(), "valley-ws-obs-"));
    // dataPath 必须指向临时副本 —— server 会在 dirname(dataPath)/director 建 SQLite。
    const dataPath = join(tmp, "npc_prompts.json");
    copyFileSync(DATA_PATH, dataPath);

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
      llmCallOverride: async () => toolCallReply(),
      directorTriggerProbability: 1.0,
    });

    const logSpy = spyOn(console, "log").mockImplementation(() => {});
    const warnSpy = spyOn(console, "warn").mockImplementation(() => {});
    const logs = () => logSpy.mock.calls.map((c) => c.join(" "));
    const warns = () => warnSpy.mock.calls.map((c) => c.join(" "));
    try {
      const url = `ws://127.0.0.1:${handle.port}`;

      // --- A 连接（首个连接：connected 日志，无 REPLACING） ---
      const a = await openWs(url);
      a.onmessage = () => { /* 帧内容不重要，此处只关心观测日志 */ };
      a.onclose = () => { /* A 的关闭在测试尾部 */ };
      await Bun.sleep(100);
      expect(logs().filter((s) => s.includes("[server] websocket connected")).length).toBe(1);
      expect(warns().some((s) => s.includes("REPLACING existing active connection"))).toBe(false);

      // --- B 连接（A 仍开着 → 单槽 activeWs 顶替告警） ---
      const b = await openWs(url);
      b.onmessage = () => {};
      await Bun.sleep(100);
      expect(logs().filter((s) => s.includes("[server] websocket connected")).length).toBe(2);
      expect(warns().some((s) => s.includes("REPLACING existing active connection"))).toBe(true);

      // --- B 断开（活跃连接 → was active 标记 + activeWs 清除） ---
      await closeWs(b);
      await Bun.sleep(100);
      expect(logs().some((s) => s.includes("[server] websocket closed:") && s.includes("(was active connection)"))).toBe(true);

      // --- A（stale 但仍开着）先发 game_context_sync（否则 DirectorAgent 走
      //     no-game-context 空分支，产不出 allocate_agent），再发 day_started(prob=1)
      //     → director_command/allocate_agent 经 sendToCsharp 投递时无活跃连接
      //     → dropped 告警（只带 type）；ack 仍经请求级 ws 回到 A ---
      a.send(JSON.stringify({ type: "game_context_sync", requestId: "ws-obs-ctx", context: makeContext() }));
      await Bun.sleep(200);
      a.send(JSON.stringify({ type: "day_started", requestId: "ws-obs-day", dateIso: "Y2_summer_14" }));
      const deadline = Date.now() + 8000;
      while (Date.now() < deadline) {
        if (warns().some((s) => s.includes("[sendToCsharp] dropped message") && s.includes("type=allocate_agent"))) break;
        await Bun.sleep(100);
      }
      // director_command 会先于 allocate_agent 被 dropped——按 type 精确找后者。
      const dropped = warns().find((s) => s.includes("[sendToCsharp] dropped message") && s.includes("type=allocate_agent"));
      expect(dropped).toBeDefined();
      // 只打 type，不打消息全文（大消息防刷爆）
      expect(dropped).not.toContain("sceneDesc");

      await closeWs(a);
      await handle.stop();
    } finally {
      warnSpy.mockRestore();
      logSpy.mockRestore();
      rmSync(tmp, { recursive: true, force: true });
    }
  },
  20000,
);
