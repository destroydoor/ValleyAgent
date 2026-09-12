import { test, expect } from "bun:test";
import * as Core from "../src/index";

test("index re-exports Agent class", () => {
  expect(Core.Agent).toBeDefined();
  expect(typeof Core.Agent).toBe("function");
});

test("index re-exports agentLoop function", () => {
  expect(Core.agentLoop).toBeDefined();
  expect(typeof Core.agentLoop).toBe("function");
});

test("index re-exports ToolRegistry class", () => {
  expect(Core.ToolRegistry).toBeDefined();
  expect(typeof Core.ToolRegistry).toBe("function");
});

test("index re-exports CircuitBreaker class", () => {
  expect(Core.CircuitBreaker).toBeDefined();
  expect(typeof Core.CircuitBreaker).toBe("function");
});

test("index re-exports VercelAIProvider class", () => {
  expect(Core.VercelAIProvider).toBeDefined();
  expect(typeof Core.VercelAIProvider).toBe("function");
});

test("index re-exports LLMBillingError and LLMUnavailableError", () => {
  expect(Core.LLMBillingError).toBeDefined();
  expect(Core.LLMUnavailableError).toBeDefined();
});

test("index re-exports BunWebSocketTransport class", () => {
  expect(Core.BunWebSocketTransport).toBeDefined();
  expect(typeof Core.BunWebSocketTransport).toBe("function");
});

test("index re-exports CORE_VERSION constant", () => {
  expect(Core.CORE_VERSION).toBe("0.1.0");
});

test("index re-exports type interfaces (compile-time check)", () => {
  // Type-only imports are erased at runtime; verify by constructing values
  const agent = new Core.Agent("test", {
    tools: new Core.ToolRegistry(),
    convertToLlm: (ctx) => ({ messages: [{ role: "system", content: ctx.systemPrompt }] }),
    llmCall: async () => ({ content: "" }),
  });
  expect(agent.name).toBe("test");
});
