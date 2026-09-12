import { test, expect } from "bun:test";
import { agentLoop } from "../src/agent-loop";
import type { AgentLoopConfig } from "../src/agent-loop";
import type { AgentContext, AgentMessage, LlmMessage } from "../src/types";
import { ToolRegistry } from "../src/tool-registry";
import { Type } from "@sinclair/typebox";

function makeContext(messages: AgentMessage[] = []): AgentContext {
  return { messages, systemPrompt: "You are a test NPC.", metadata: {} };
}

function makeLoopConfig(overrides: Partial<AgentLoopConfig> = {}): AgentLoopConfig {
  return {
    tools: new ToolRegistry(),
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
    llmCall: async () => ({ content: "I am responding", usage: { totalTokens: 10 } }),
    shouldStopAfterTurn: (_ctx, turnIndex) => turnIndex >= 1,
    maxTurns: 10,
    ...overrides,
  };
}

test("agentLoop emits agent_start and agent_end", async () => {
  const events = await agentLoop({
    context: makeContext(),
    config: makeLoopConfig(),
    signal: new AbortController().signal,
  }).awaitAll();

  const types = events.map((e) => e.type);
  expect(types).toContain("agent_start");
  expect(types).toContain("agent_end");
});

test("agentLoop emits turn_start and turn_end", async () => {
  const events = await agentLoop({
    context: makeContext([{ role: "user", content: "hi" }]),
    config: makeLoopConfig(),
    signal: new AbortController().signal,
  }).awaitAll();

  const types = events.map((e) => e.type);
  expect(types).toContain("turn_start");
  expect(types).toContain("turn_end");
});

test("agentLoop emits message events for LLM responses", async () => {
  const events = await agentLoop({
    context: makeContext([{ role: "user", content: "hi" }]),
    config: makeLoopConfig({
      llmCall: async () => ({ content: "Hello there!", usage: { totalTokens: 10 } }),
    }),
    signal: new AbortController().signal,
  }).awaitAll();

  expect(events.some((e) => e.type === "message_start")).toBe(true);
  const endEvent = events.find((e) => e.type === "message_end");
  if (endEvent && endEvent.type === "message_end") {
    expect(endEvent.content).toBe("Hello there!");
  }
});

test("agentLoop executes tool calls from LLM response", async () => {
  const tools = new ToolRegistry();
  let toolExecuted = false;
  tools.register({
    name: "speak",
    description: "Say something",
    visibility: "llm_visible",
    parameters: Type.Object({ text: Type.String() }),
    execute: async (args) => {
      toolExecuted = true;
      return { content: `Said: ${args.text}` };
    },
  });

  const events = await agentLoop({
    context: makeContext([{ role: "user", content: "say hi" }]),
    config: makeLoopConfig({
      tools,
      llmCall: async () => ({
        content: "Let me speak",
        toolCalls: [{ id: "call_1", name: "speak", args: { text: "hi" } }],
        usage: { totalTokens: 10 },
      }),
      shouldStopAfterTurn: (_ctx, turn) => turn >= 2,
    }),
    signal: new AbortController().signal,
  }).awaitAll();

  expect(toolExecuted).toBe(true);
  expect(events.some((e) => e.type === "tool_call_start")).toBe(true);
  expect(events.some((e) => e.type === "tool_call_end")).toBe(true);
});

test("agentLoop stops when shouldStopAfterTurn returns true", async () => {
  const events = await agentLoop({
    context: makeContext([{ role: "user", content: "hi" }]),
    config: makeLoopConfig({
      shouldStopAfterTurn: (_ctx, turn) => turn >= 2,
      llmCall: async () => ({ content: "continuing...", usage: { totalTokens: 5 } }),
    }),
    signal: new AbortController().signal,
  }).awaitAll();

  const turnStarts = events.filter((e) => e.type === "turn_start");
  expect(turnStarts.length).toBe(3);
});

test("agentLoop respects maxTurns limit", async () => {
  const events = await agentLoop({
    context: makeContext([{ role: "user", content: "hi" }]),
    config: makeLoopConfig({
      maxTurns: 2,
      shouldStopAfterTurn: () => false,
      llmCall: async () => ({ content: "never stopping", usage: { totalTokens: 5 } }),
    }),
    signal: new AbortController().signal,
  }).awaitAll();

  const turnStarts = events.filter((e) => e.type === "turn_start");
  expect(turnStarts.length).toBe(2);
});

test("agentLoop emits error event on LLM failure", async () => {
  const events = await agentLoop({
    context: makeContext([{ role: "user", content: "hi" }]),
    config: makeLoopConfig({
      llmCall: async () => { throw new Error("LLM exploded"); },
    }),
    signal: new AbortController().signal,
  }).awaitAll();

  const errorEvent = events.find((e) => e.type === "error");
  expect(errorEvent).toBeDefined();
  if (errorEvent && errorEvent.type === "error") {
    expect(errorEvent.message).toContain("LLM exploded");
  }
});

test("agentLoop aborts cleanly on AbortSignal", async () => {
  const controller = new AbortController();
  let llmCalls = 0;
  const stream = agentLoop({
    context: makeContext([{ role: "user", content: "hi" }]),
    config: makeLoopConfig({
      shouldStopAfterTurn: () => false, // 不靠 turn 计数终止——只有 abort 能停
      llmCall: async () => {
        llmCalls++;
        controller.abort(); // 第一次调用后立即 abort
        return { content: "aborted", usage: { totalTokens: 1 } };
      },
    }),
    signal: controller.signal,
  });

  const events = await stream.awaitAll();
  // abort 必须真正终止循环：llmCall 只被调用一次（若 abort 监听器失效，shouldStopAfterTurn=false 会一直转）
  expect(llmCalls).toBe(1);
  expect(events.some((e) => e.type === "agent_end")).toBe(true);
  expect(events.some((e) => e.type === "error")).toBe(false); // abort 是正常终止，不是失败
});

test("beforeToolCall hook can block tool execution", async () => {
  const tools = new ToolRegistry();
  let executed = false;
  tools.register({
    name: "speak",
    description: "Say something",
    visibility: "llm_visible",
    parameters: Type.Object({ text: Type.String() }),
    execute: async () => { executed = true; return { content: "said" }; },
  });

  const events = await agentLoop({
    context: makeContext([{ role: "user", content: "hi" }]),
    config: makeLoopConfig({
      tools,
      beforeToolCall: async () => ({ allow: false, reason: "blocked" }),
      llmCall: async () => ({
        content: "trying to speak",
        toolCalls: [{ id: "c1", name: "speak", args: { text: "hi" } }],
        usage: { totalTokens: 5 },
      }),
      shouldStopAfterTurn: (_ctx, turn) => turn >= 1,
    }),
    signal: new AbortController().signal,
  }).awaitAll();

  expect(executed).toBe(false);
  const toolEnd = events.find((e) => e.type === "tool_call_end");
  if (toolEnd && toolEnd.type === "tool_call_end") {
    expect(toolEnd.isError).toBe(true);
  }
});

test("afterToolCall hook receives result", async () => {
  const tools = new ToolRegistry();
  tools.register({
    name: "speak",
    description: "Say something",
    visibility: "llm_visible",
    parameters: Type.Object({ text: Type.String() }),
    execute: async (args) => ({ content: `Said: ${args.text}` }),
  });

  let hookResult: unknown = null;
  await agentLoop({
    context: makeContext([{ role: "user", content: "hi" }]),
    config: makeLoopConfig({
      tools,
      afterToolCall: async (_name, _args, result) => {
        hookResult = result;
        return { result };
      },
      llmCall: async () => ({
        content: "speaking",
        toolCalls: [{ id: "c1", name: "speak", args: { text: "hello" } }],
        usage: { totalTokens: 5 },
      }),
      shouldStopAfterTurn: (_ctx, turn) => turn >= 1,
    }),
    signal: new AbortController().signal,
  }).awaitAll();

  expect(hookResult).not.toBeNull();
});

test("transformContext hook modifies context before LLM call", async () => {
  let capturedSystemPrompt = "";
  await agentLoop({
    context: makeContext([{ role: "user", content: "hi" }]),
    config: makeLoopConfig({
      transformContext: (ctx) => ({
        ...ctx,
        systemPrompt: ctx.systemPrompt + " You are happy.",
      }),
      convertToLlm: (ctx) => {
        capturedSystemPrompt = ctx.systemPrompt;
        return {
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
        };
      },
    }),
    signal: new AbortController().signal,
  }).awaitAll();

  expect(capturedSystemPrompt).toContain("You are happy.");
});

test("agentLoop feeds tool results back into context messages", async () => {
  const tools = new ToolRegistry();
  tools.register({
    name: "query",
    description: "Query something",
    visibility: "llm_visible",
    parameters: Type.Object({ q: Type.String() }),
    execute: async (args) => ({ content: `Result for: ${args.q}` }),
  });

  let capturedMessages: AgentMessage[] = [];
  await agentLoop({
    context: makeContext([{ role: "user", content: "query hello" }]),
    config: makeLoopConfig({
      tools,
      llmCall: async () => ({
        content: "let me query",
        toolCalls: [{ id: "c1", name: "query", args: { q: "hello" } }],
        usage: { totalTokens: 10 },
      }),
      shouldStopAfterTurn: (ctx, turn) => {
        capturedMessages = [...ctx.messages];
        return turn >= 1;
      },
    }),
    signal: new AbortController().signal,
  }).awaitAll();

  const toolMsg = capturedMessages.find(m => m.role === "tool");
  expect(toolMsg).toBeDefined();
  expect(toolMsg!.content).toContain("Result for: hello");
  expect(toolMsg!.toolCallId).toBe("c1");
  expect(toolMsg!.toolName).toBe("query");
});

test("agentLoop passes llm-visible tools to llmCall", async () => {
  const tools = new ToolRegistry();
  tools.register({
    name: "speak",
    description: "Say something",
    visibility: "llm_visible",
    parameters: Type.Object({ text: Type.String() }),
    execute: async () => ({ content: "said" }),
  });
  tools.register({
    name: "internal_only",
    description: "Internal tool",
    visibility: "tactical",
    parameters: Type.Object({}),
    execute: async () => ({ content: "internal" }),
  });

  let receivedTools: any = undefined;
  await agentLoop({
    context: makeContext([{ role: "user", content: "hi" }]),
    config: makeLoopConfig({
      tools,
      llmCall: async (_messages, toolList?) => {
        receivedTools = toolList;
        return { content: "ok", usage: { totalTokens: 5 } };
      },
      shouldStopAfterTurn: (_ctx, turn) => turn >= 0,
    }),
    signal: new AbortController().signal,
  }).awaitAll();

  expect(receivedTools).toBeDefined();
  expect(receivedTools).toHaveLength(1);
  expect(receivedTools[0].name).toBe("speak");
});
