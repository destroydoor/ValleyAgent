import { test, expect, spyOn } from "bun:test";
import { Agent } from "../src/agent";
import type { AgentContext, AgentMessage, LlmMessage } from "../src/types";
import type { AgentLoopConfig } from "../src/agent-loop";
import { ToolRegistry } from "../src/tool-registry";

function makeLoopConfig(overrides: Partial<AgentLoopConfig> = {}): AgentLoopConfig {
  return {
    tools: new ToolRegistry(),
    // tool 变体在 LlmMessage 里 toolCallId 为必填（AgentMessage 里可选），
    // 与 agent-loop.test.ts 同款显式映射，避免窄化丢失 tool 字段（2026-09-15 typecheck:tests 收口）。
    convertToLlm: (ctx) => ({
      messages: [
        { role: "system", content: ctx.systemPrompt },
        ...ctx.messages.map((m): LlmMessage => {
          if (m.role === "tool") {
            return {
              role: "tool",
              content: m.content,
              toolCallId: m.toolCallId ?? "",
              ...(m.toolName ? { toolName: m.toolName } : {}),
            };
          }
          return { role: m.role, content: m.content };
        }),
      ],
    }),
    llmCall: async () => ({ content: "response", usage: { totalTokens: 5 } }),
    shouldStopAfterTurn: (_ctx, turn) => turn >= 0,
    maxTurns: 5,
    ...overrides,
  };
}

function makeContext(messages: AgentMessage[] = []): AgentContext {
  return { messages, systemPrompt: "You are a test NPC.", metadata: {} };
}

test("Agent starts idle", () => {
  const agent = new Agent("test_agent", makeLoopConfig());
  expect(agent.isIdle()).toBe(true);
  expect(agent.getState().isStreaming).toBe(false);
});

test("prompt starts a run and transitions to streaming", async () => {
  const agent = new Agent("test", makeLoopConfig());
  const stream = agent.prompt(makeContext([{ role: "user", content: "hi" }]));
  expect(agent.isIdle()).toBe(false);
  await stream.awaitAll();
  expect(agent.isIdle()).toBe(true);
});

test("prompt returns EventStream that collects events", async () => {
  const agent = new Agent("test", makeLoopConfig());
  const stream = agent.prompt(makeContext([{ role: "user", content: "hi" }]));
  const events = await stream.awaitAll();
  expect(events.some((e) => e.type === "agent_start")).toBe(true);
  expect(events.some((e) => e.type === "agent_end")).toBe(true);
});

test("steer injects message into current run", async () => {
  let capturedMessages: AgentMessage[] = [];
  const agent = new Agent("test", makeLoopConfig({
    shouldStopAfterTurn: (ctx, turn) => {
      capturedMessages = [...ctx.messages];
      return turn >= 1;
    },
    llmCall: async () => ({ content: "ok", usage: { totalTokens: 1 } }),
  }));

  const stream = agent.prompt(makeContext([{ role: "user", content: "initial" }]));
  agent.steer({ role: "user", content: "steered!" });
  await stream.awaitAll();

  const contents = capturedMessages.map((m) => m.content);
  expect(contents).toContain("steered!");
});

test("followUp queues message for after current run", async () => {
  const receivedContents: string[] = [];
  const agent = new Agent("test", makeLoopConfig({
    shouldStopAfterTurn: (_ctx, turn) => turn >= 0, // 每次运行 1 turn 结束
    llmCall: async (messages) => {
      receivedContents.push(...messages.map((m) => m.content));
      return { content: "response", usage: { totalTokens: 5 } };
    },
  }));

  const stream1 = agent.prompt(makeContext([{ role: "user", content: "first" }]));
  agent.followUp({ role: "user", content: "second" });
  await stream1.awaitAll();
  await agent.waitForIdle();

  // 第一次运行必须处理 first；drainFollowUps 必须用排队的 second 发起第二次运行
  // （旧断言只查最终 isIdle——drainFollowUps 变 no-op 测试照样绿）。
  expect(receivedContents.some((c) => c.includes("first"))).toBe(true);
  expect(receivedContents.some((c) => c.includes("second")), "followUp 消息必须触发第二次 LLM 调用").toBe(true);
  expect(agent.isIdle()).toBe(true);
});

test("abort cancels current run", async () => {
  const agent = new Agent("test", makeLoopConfig({
    shouldStopAfterTurn: () => false,
    llmCall: async () => {
      await new Promise((r) => setTimeout(r, 100));
      return { content: "slow", usage: { totalTokens: 1 } };
    },
  }));

  const stream = agent.prompt(makeContext([{ role: "user", content: "hi" }]));
  agent.abort();
  await stream.awaitAll();
  expect(agent.isIdle()).toBe(true);
});

test("reset clears messages and queues", () => {
  const agent = new Agent("test", makeLoopConfig());
  agent.followUp({ role: "user", content: "queued" });
  agent.reset();
  expect(agent.isIdle()).toBe(true);
  expect(agent.getState().streamingMessage).toBe(null);
});

test("subscribe receives events from all runs", async () => {
  const agent = new Agent("test", makeLoopConfig());
  const received: string[] = [];
  agent.subscribe((event) => received.push(event.type));

  const stream = agent.prompt(makeContext([{ role: "user", content: "hi" }]));
  await stream.awaitAll();

  expect(received).toContain("agent_start");
  expect(received).toContain("agent_end");
});

test("AgentState exposes streaming status", async () => {
  const agent = new Agent("test", makeLoopConfig({
    llmCall: async () => {
      await new Promise((r) => setTimeout(r, 50));
      return { content: "delayed", usage: { totalTokens: 1 } };
    },
    shouldStopAfterTurn: (_ctx, turn) => turn >= 0,
  }));

  const stream = agent.prompt(makeContext([{ role: "user", content: "hi" }]));
  await new Promise((r) => setTimeout(r, 10));
  expect(agent.getState().isStreaming).toBe(true);
  await stream.awaitAll();
  expect(agent.getState().isStreaming).toBe(false);
});

// ── 订阅者抛异常不得卡死（issue #23 / 审计 §3.8）──────────────────────────
// 此前 stream.awaitAll().then(...) 无 .catch、回放循环不隔离订阅者：任一订阅者
// 抛异常 → 回放中断 → proxyStream 永不 done()、activeRun 永不清空 → NPC 永久 BUSY。

test("必抛订阅者：prompt 正常终态化（awaitAll 不挂起、isIdle、errorMessage、可再次 prompt）", async () => {
  const errSpy = spyOn(console, "error").mockImplementation(() => {});
  try {
    const agent = new Agent("wedge-test", makeLoopConfig());
    // 模拟"未自防的订阅者"：统计/审计/UI 推送类订阅者忘了 try/catch 就是这个形状
    agent.subscribe(() => { throw new Error("subscriber boom"); });

    const stream = agent.prompt(makeContext([{ role: "user", content: "hi" }]));
    // 修复前：awaitAll 永久挂起（回放中抛出 → then 链 rejection → proxyStream 永不 done）
    const events = await stream.awaitAll();
    expect(stream.isDone()).toBe(true);
    expect(events.some((e) => e.type === "agent_end")).toBe(true);

    expect(agent.isIdle()).toBe(true);
    expect(agent.getState().isStreaming).toBe(false);
    // 错误必须可见：进 AgentState（修复前 errorMessage 为 null，错误连状态里都看不见）
    expect(agent.getState().errorMessage).toContain("subscriber boom");
    // 隔离留痕：单播失败被 console.error 记录（含订阅者上下文）
    expect(errSpy.mock.calls.some((args) => String(args[0]).includes("wedge-test"))).toBe(true);

    // 再次 prompt 不得抛 "already running"（修复前 activeRun 永不清空 → 永久 BUSY）
    const stream2 = agent.prompt(makeContext([{ role: "user", content: "again" }]));
    await stream2.awaitAll();
    expect(agent.isIdle()).toBe(true);
  } finally {
    errSpy.mockRestore();
  }
});

test("必抛订阅者不拖累其它订阅者：健壮订阅者仍收到全部回放事件", async () => {
  const errSpy = spyOn(console, "error").mockImplementation(() => {});
  try {
    const agent = new Agent("wedge-mixed", makeLoopConfig());
    const received: string[] = [];
    agent.subscribe(() => { throw new Error("bad subscriber"); });
    agent.subscribe((event) => received.push(event.type));

    const stream = agent.prompt(makeContext([{ role: "user", content: "hi" }]));
    await stream.awaitAll();

    expect(received).toContain("agent_start");
    expect(received).toContain("agent_end");
    expect(agent.isIdle()).toBe(true);
  } finally {
    errSpy.mockRestore();
  }
});

test("followUp 排水在订阅者异常路径后仍工作（finalizeRun 必经）", async () => {
  const errSpy = spyOn(console, "error").mockImplementation(() => {});
  try {
    const agent = new Agent("wedge-followup", makeLoopConfig({
      shouldStopAfterTurn: (_ctx, turn) => turn >= 0,
    }));
    agent.subscribe(() => { throw new Error("subscriber boom"); });

    const stream1 = agent.prompt(makeContext([{ role: "user", content: "first" }]));
    agent.followUp({ role: "user", content: "second" });
    await stream1.awaitAll();
    await agent.waitForIdle();
    expect(agent.isIdle()).toBe(true);
  } finally {
    errSpy.mockRestore();
  }
});
