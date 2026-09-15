import { test, expect } from "bun:test";
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
