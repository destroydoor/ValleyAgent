// Tests for ConsoleLogSubscriber — stdout 日志订阅器。
// 验证 Agent 事件序列 → stdout 输出格式/完整性/turn 耗时。
// Run: bun test packages/stardew/tests/console-log-subscriber.test.ts

import { test, expect, spyOn } from "bun:test";
import { ConsoleLogSubscriber } from "../src/console-log-subscriber";
import type { AgentEvent } from "@valley/core";

// Fake Agent：实现 subscribe 接口，手动 replay 事件序列（模拟 agent.ts:88-94 的 replay 机制）。
function makeFakeAgent(events: AgentEvent[]): { subscribe: (fn: (e: AgentEvent) => void) => () => void } {
  const subs: Array<(e: AgentEvent) => void> = [];
  return {
    subscribe(fn) {
      subs.push(fn);
      // 模拟 run 结束后全量 replay
      queueMicrotask(() => {
        for (const e of events) for (const s of subs) s(e);
      });
      return () => {
        const i = subs.indexOf(fn);
        if (i >= 0) subs.splice(i, 1);
      };
    },
  };
}

test("emits turn start/end with npc and turn index, and turn duration", async () => {
  const logSpy = spyOn(console, "log").mockImplementation(() => {});
  const events: AgentEvent[] = [
    { type: "turn_start", timestamp: 1000, turnIndex: 0 },
    { type: "turn_end", timestamp: 3387, turnIndex: 0 },
  ];
  const fakeAgent = makeFakeAgent(events);
  const sub = new ConsoleLogSubscriber("Abigail");
  sub.attach(fakeAgent as any);
  await new Promise((r) => setTimeout(r, 10)); // 等 queueMicrotask replay

  const lines = logSpy.mock.calls.map((c) => String(c[0]));
  expect(
    lines.some((l) => l.includes("[turn]") && l.includes("Abigail") && l.includes("#0") && l.includes("start")),
  ).toBe(true);
  expect(
    lines.some((l) => l.includes("[turn]") && l.includes("#0") && l.includes("end") && l.includes("2387ms")),
  ).toBe(true);
  logSpy.mockRestore();
});

test("emits full llm output verbatim from message_end", async () => {
  const logSpy = spyOn(console, "log").mockImplementation(() => {});
  const fullOutput = "我想想...\n今天天气不错，该去钓鱼。";
  const events: AgentEvent[] = [
    { type: "turn_start", timestamp: 1000, turnIndex: 0 },
    { type: "message_end", timestamp: 2000, content: fullOutput },
    { type: "turn_end", timestamp: 2100, turnIndex: 0 },
  ];
  const fakeAgent = makeFakeAgent(events);
  const sub = new ConsoleLogSubscriber("Abigail");
  sub.attach(fakeAgent as any);
  await new Promise((r) => setTimeout(r, 10));

  const joined = logSpy.mock.calls.map((c) => String(c[0])).join("\n");
  expect(joined).toContain("llm输出");
  expect(joined).toContain(fullOutput); // 完整原始，不截断
  logSpy.mockRestore();
});

test("emits tool call start/end with full args and result", async () => {
  const logSpy = spyOn(console, "log").mockImplementation(() => {});
  const args = { text: "你好啊，新来的农夫。", emotion: "happy" };
  const result = { content: "已记住", isError: false };
  const events: AgentEvent[] = [
    { type: "tool_call_start", timestamp: 3000, toolName: "speak", toolCallId: "tc-1", args },
    { type: "tool_call_end", timestamp: 3120, toolName: "speak", toolCallId: "tc-1", result, isError: false },
  ];
  const fakeAgent = makeFakeAgent(events);
  const sub = new ConsoleLogSubscriber("Abigail");
  sub.attach(fakeAgent as any);
  await new Promise((r) => setTimeout(r, 10));

  const joined = logSpy.mock.calls.map((c) => String(c[0])).join("\n");
  expect(joined).toContain("[tool]");
  expect(joined).toContain("→ speak");
  expect(joined).toContain(JSON.stringify(args)); // 完整 args
  expect(joined).toContain("← speak");
  expect(joined).toContain("已记住"); // 完整 result
  logSpy.mockRestore();
});

test("emits error event with message", async () => {
  // issue #26 批④：agent 的 error 事件落在 console.error（可按 ERROR grep）
  const logSpy = spyOn(console, "error").mockImplementation(() => {});
  const events: AgentEvent[] = [
    { type: "error", timestamp: 5000, message: "LLM API down" },
  ];
  const fakeAgent = makeFakeAgent(events);
  const sub = new ConsoleLogSubscriber("Abigail");
  sub.attach(fakeAgent as any);
  await new Promise((r) => setTimeout(r, 10));

  const joined = logSpy.mock.calls.map((c) => String(c[0])).join("\n");
  expect(joined).toContain("error");
  expect(joined).toContain("LLM API down");
  logSpy.mockRestore();
});

test("never throws on malformed event (best-effort)", async () => {
  const logSpy = spyOn(console, "log").mockImplementation(() => {
    throw new Error("console exploded");
  });
  const events: AgentEvent[] = [
    { type: "turn_start", timestamp: 1000, turnIndex: 0 },
  ];
  const fakeAgent = makeFakeAgent(events);
  const sub = new ConsoleLogSubscriber("Abigail");
  expect(() => sub.attach(fakeAgent as any)).not.toThrow();
  await new Promise((r) => setTimeout(r, 10));
  logSpy.mockRestore();
});
