// issue #22：统一 error 帧分类 + requestId 回填测试。
// Run: bun test packages/stardew/tests/server-error-frame.test.ts
//
// 两层验证：
//   1. 纯函数层——extractRequestIdFromFrame / buildErrorFrame 的分类与回填规则；
//   2. 实线层——真实 Bun.serve + WebSocket 客户端打四种错误场景，断言回包
//      （bad_request / validation_failed / unknown_type / internal_error 各就各位、
//      requestId 能解析就回填）且 unknown_type 有 WARN 日志（AGENTS.md §3.6）。

import { test, expect, spyOn } from "bun:test";
import { startServer, buildErrorFrame, extractRequestIdFromFrame } from "../src/server";
import { mkdtempSync, rmSync, copyFileSync } from "node:fs";
import { join, resolve } from "node:path";
import { tmpdir } from "node:os";
import type { ErrorFrame } from "../src/types";

const DATA_PATH = resolve(import.meta.dir, "../data/npc_prompts.json");

// ─── 纯函数层：requestId 提取与 error 帧构造 ───

test("extractRequestIdFromFrame returns requestId only for parseable object frames", () => {
  expect(extractRequestIdFromFrame(JSON.stringify({ type: "dialogue", requestId: "req-1" }))).toBe("req-1");
  // 帧合法 JSON 但 requestId 不是 string（数字 ID 也拒绝——C# 侧 Guid N 格式）
  expect(extractRequestIdFromFrame(JSON.stringify({ type: "dialogue", requestId: 42 }))).toBeUndefined();
  expect(extractRequestIdFromFrame(JSON.stringify({ type: "dialogue" }))).toBeUndefined();
  // 畸形帧 / 非对象帧 → 无 requestId 可提取
  expect(extractRequestIdFromFrame("not json {{{")).toBeUndefined();
  expect(extractRequestIdFromFrame("null")).toBeUndefined();
  expect(extractRequestIdFromFrame("[1,2,3]")).toBeUndefined();
});

test("buildErrorFrame backfills requestId when raw frame is parseable", () => {
  const raw = JSON.stringify({ type: "reconnect_sync", requestId: "req-9" });
  const frame = buildErrorFrame("internal_error", new TypeError("agents is undefined"), raw);
  expect(frame.type).toBe("error");
  expect(frame.code).toBe("internal_error");
  expect(frame.requestId).toBe("req-9");
  // String(err) 保留异常类型名前缀（归因线索）
  expect(frame.message).toContain("TypeError");
  expect(frame.message).toContain("agents is undefined");
});

test("buildErrorFrame omits requestId for unparseable frames (bad_request)", () => {
  const frame = buildErrorFrame("bad_request", new SyntaxError("Unexpected token"), "not json");
  expect(frame.code).toBe("bad_request");
  expect(frame.requestId).toBeUndefined();
  expect("requestId" in frame).toBe(false); // 字段省略而非 undefined，wire 上不出现
});

test("buildErrorFrame works without raw frame", () => {
  const frame = buildErrorFrame("internal_error", new Error("boom"));
  expect(frame.code).toBe("internal_error");
  expect(frame.requestId).toBeUndefined();
  expect(frame.message).toBe("Error: boom");
});

// ─── 实线层：真实 WS 往返四种错误场景 ───

function openWs(url: string): Promise<WebSocket> {
  return new Promise((res, rej) => {
    const ws = new WebSocket(url);
    ws.onopen = () => res(ws);
    ws.onerror = () => rej(new Error("ws error"));
  });
}

test(
  "server wire: malformed/unknown/throwing frames all produce classified error frames with requestId backfill",
  async () => {
    const tmp = mkdtempSync(join(tmpdir(), "valley-err-frame-"));
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

    const warnSpy = spyOn(console, "warn").mockImplementation(() => {});
    const errorSpy = spyOn(console, "error").mockImplementation(() => {});
    try {
      const ws = await openWs(`ws://127.0.0.1:${handle.port}`);
      const replies: string[] = [];
      ws.onmessage = (ev) => replies.push(String(ev.data));
      const waitReply = async (needle: string): Promise<string> => {
        const deadline = Date.now() + 3000;
        while (Date.now() < deadline) {
          const hit = replies.find((r) => r.includes(needle));
          if (hit) return hit;
          await Bun.sleep(50);
        }
        throw new Error(`no reply containing "${needle}"; got: ${replies.join(" | ")}`);
      };

      // ① 畸形帧（不是 JSON）→ bad_request，无 requestId（解析不出）
      ws.send("this is not json {{{");
      const badReq = JSON.parse(await waitReply("bad_request")) as ErrorFrame;
      expect(badReq.type).toBe("error");
      expect(badReq.code).toBe("bad_request");
      expect(badReq.requestId).toBeUndefined();

      // ② payload 为 null（合法 JSON 但不是对象）→ validation_failed，无 requestId
      ws.send("null");
      const validation = JSON.parse(await waitReply("validation_failed")) as ErrorFrame;
      expect(validation.type).toBe("error");
      expect(validation.code).toBe("validation_failed");

      // ③ 未知类型（协议漂移：consolidate_day 两端均已删）→ unknown_type + requestId 回填 + WARN
      ws.send(JSON.stringify({ type: "consolidate_day", requestId: "req-unknown-77" }));
      const unknown = JSON.parse(await waitReply("unknown_type")) as ErrorFrame;
      expect(unknown.type).toBe("error");
      expect(unknown.code).toBe("unknown_type");
      expect(unknown.requestId).toBe("req-unknown-77");
      expect(unknown.message).toContain("consolidate_day");
      const warns = () => warnSpy.mock.calls.map((c) => c.join(" "));
      expect(warns().some((s) => s.includes("[protocol]") && s.includes("consolidate_day"))).toBe(true);

      // ④ handler 抛异常（reconnect_sync 缺 agents 字段）→ internal_error + requestId 回填
      ws.send(JSON.stringify({ type: "reconnect_sync", requestId: "req-throw-88", replayedOutbox: 0 }));
      const internal = JSON.parse(await waitReply("internal_error")) as ErrorFrame;
      expect(internal.type).toBe("error");
      expect(internal.code).toBe("internal_error");
      expect(internal.requestId).toBe("req-throw-88");

      ws.close();
      await handle.stop();
    } finally {
      warnSpy.mockRestore();
      errorSpy.mockRestore();
      rmSync(tmp, { recursive: true, force: true });
    }
  },
  20000,
);
