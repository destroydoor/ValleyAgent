// Server 级 WS 观测测试（2026-09-11 观测补强：连接生命周期可见性）。
// Run: bun test packages/stardew/tests/server-ws-observability.test.ts
//
// 验证 startServer 的 WS 观测（真实 Bun.serve + WebSocket 客户端）：
//   1. open：第二连接顶替仍开着的活跃连接 → REPLACING 告警（"每玩家一个导演"
//      误读的观测面）；
//   2. close：活跃连接断开 → activeWs 清除 + "(was active connection)" 标记；
//   3. 2026-09-14 旧叙事 Director 砍除后 day_started 不再产 allocate_agent——
//      stale 连接发 day_started 只收 ack，无任何 sendToCsharp dropped 告警。
//
// 场景编排：A 连接 → B 连接（顶替 A，REPLACING 告警）→ B 断开（was active，
// activeWs=null）→ A（stale 但仍开着）发 day_started → 仅 ack 回 A。

import { test, expect, spyOn } from "bun:test";
import { startServer } from "../src/server";
import { mkdtempSync, rmSync, copyFileSync } from "node:fs";
import { join, resolve } from "node:path";
import { tmpdir } from "node:os";

const DATA_PATH = resolve(import.meta.dir, "../data/npc_prompts.json");

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
  "server ws observability: replace warn on superseded connection, active-close marker, day_started acks without proactive sends",
  async () => {
    const tmp = mkdtempSync(join(tmpdir(), "valley-ws-obs-"));
    // dataPath 必须指向临时副本 —— server 启动会读该文件。
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
    });

    const logSpy = spyOn(console, "log").mockImplementation(() => {});
    const warnSpy = spyOn(console, "warn").mockImplementation(() => {});
    const logs = () => logSpy.mock.calls.map((c) => c.join(" "));
    const warns = () => warnSpy.mock.calls.map((c) => c.join(" "));
    try {
      const url = `ws://127.0.0.1:${handle.port}`;

      // --- A 连接（首个连接：connected 日志，无 REPLACING） ---
      const acks: string[] = [];
      const a = await openWs(url);
      a.onmessage = (ev) => { acks.push(String(ev.data)); };
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

      // --- A（stale 但仍开着）发 day_started：旧叙事 Director 已砍除，
      //     不再产出 allocate_agent → 无 sendToCsharp dropped 告警；ack 仍经
      //     请求级 ws 回到 A ---
      a.send(JSON.stringify({ type: "day_started", requestId: "ws-obs-day", dateIso: "Y2_summer_14" }));
      const deadline = Date.now() + 3000;
      while (Date.now() < deadline) {
        if (acks.some((s) => s.includes("ws-obs-day"))) break;
        await Bun.sleep(100);
      }
      expect(acks.some((s) => s.includes("ws-obs-day"))).toBe(true);
      await Bun.sleep(300);
      expect(warns().some((s) => s.includes("[sendToCsharp] dropped message"))).toBe(false);

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
