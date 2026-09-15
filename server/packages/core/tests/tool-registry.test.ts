import { test, expect } from "bun:test";
import { ToolRegistry } from "../src/tool-registry";
import type { Tool, ToolVisibility, ToolResult } from "../src/tool";
import { Type } from "@sinclair/typebox";

function makeTool(
  name: string,
  visibility: ToolVisibility,
  fn: (args: Record<string, unknown>) => Promise<ToolResult>
): Tool {
  return {
    name,
    description: `Tool: ${name}`,
    visibility,
    parameters: Type.Object({
      input: Type.String(),
    }),
    execute: fn,
  };
}

test("register and getByName", () => {
  const registry = new ToolRegistry();
  const tool = makeTool("speak", "llm_visible", async () => ({
    content: "ok",
  }));
  registry.register(tool);

  const found = registry.getByName("speak");
  expect(found).toBeDefined();
  expect(found!.name).toBe("speak");
});

test("getByName returns undefined for unknown tool", () => {
  const registry = new ToolRegistry();
  expect(registry.getByName("nonexistent")).toBeUndefined();
});

test("unregister removes tool", () => {
  const registry = new ToolRegistry();
  const tool = makeTool("speak", "llm_visible", async () => ({ content: "ok" }));
  registry.register(tool);
  registry.unregister("speak");
  expect(registry.getByName("speak")).toBeUndefined();
});

test("getByVisibility filters llm_visible only", () => {
  const registry = new ToolRegistry();
  registry.register(makeTool("speak", "llm_visible", async () => ({ content: "ok" })));
  registry.register(makeTool("move_to", "tactical", async () => ({ content: "ok" })));
  registry.register(makeTool("remember", "local", async () => ({ content: "ok" })));

  const visible = registry.getByVisibility("llm_visible");
  expect(visible).toHaveLength(1);
  expect(visible[0]!.name).toBe("speak");
});

test("getByVisibility filters tactical only", () => {
  const registry = new ToolRegistry();
  registry.register(makeTool("speak", "llm_visible", async () => ({ content: "ok" })));
  registry.register(makeTool("move_to", "tactical", async () => ({ content: "ok" })));

  const tactical = registry.getByVisibility("tactical");
  expect(tactical).toHaveLength(1);
  expect(tactical[0]!.name).toBe("move_to");
});

test("getLlmVisibleTools returns only llm_visible", () => {
  const registry = new ToolRegistry();
  registry.register(makeTool("speak", "llm_visible", async () => ({ content: "ok" })));
  registry.register(makeTool("move_to", "tactical", async () => ({ content: "ok" })));
  registry.register(makeTool("remember", "local", async () => ({ content: "ok" })));

  const visible = registry.getLlmVisibleTools();
  expect(visible).toHaveLength(1);
  expect(visible[0]!.name).toBe("speak");
});

test("validateCall accepts valid args", () => {
  const registry = new ToolRegistry();
  const tool: Tool = {
    name: "speak",
    description: "Say something",
    visibility: "llm_visible",
    parameters: Type.Object({
      text: Type.String(),
    }),
    execute: async () => ({ content: "ok" }),
  };
  registry.register(tool);

  const result = registry.validateCall("speak", { text: "hello" });
  expect(result.valid).toBe(true);
});

test("validateCall rejects missing required field", () => {
  const registry = new ToolRegistry();
  const tool: Tool = {
    name: "speak",
    description: "Say something",
    visibility: "llm_visible",
    parameters: Type.Object({
      text: Type.String(),
    }),
    execute: async () => ({ content: "ok" }),
  };
  registry.register(tool);

  const result = registry.validateCall("speak", {});
  expect(result.valid).toBe(false);
  expect(result.errors).toBeDefined();
  expect(result.errors!.length).toBeGreaterThan(0);
});

test("validateCall rejects wrong type", () => {
  const registry = new ToolRegistry();
  const tool: Tool = {
    name: "set_state",
    description: "Set state",
    visibility: "llm_visible",
    parameters: Type.Object({
      state: Type.String(),
    }),
    execute: async () => ({ content: "ok" }),
  };
  registry.register(tool);

  const result = registry.validateCall("set_state", { state: 123 });
  expect(result.valid).toBe(false);
});

test("validateCall returns invalid for unknown tool", () => {
  const registry = new ToolRegistry();
  const result = registry.validateCall("unknown", {});
  expect(result.valid).toBe(false);
  expect(result.errors).toContain("Tool not found: unknown");
});

test("execute calls the tool function and returns result", async () => {
  const registry = new ToolRegistry();
  // TS 无法跟踪闭包内赋值（会一直窄化到 null），用数组收集后按下标断言
  const captured: Array<Record<string, unknown>> = [];
  registry.register({
    name: "speak",
    description: "Say something",
    visibility: "llm_visible",
    parameters: Type.Object({ text: Type.String() }),
    execute: async (args) => {
      captured.push(args);
      return { content: "said: " + (args.text as string) };
    },
  });

  const result = await registry.execute("speak", { text: "hello" });
  expect(result.content).toBe("said: hello");
  expect(captured[0]).toEqual({ text: "hello" });
});

test("execute returns isError for thrown exception", async () => {
  const registry = new ToolRegistry();
  registry.register({
    name: "fail",
    description: "Always fails",
    visibility: "llm_visible",
    parameters: Type.Object({}),
    execute: async () => {
      throw new Error("boom");
    },
  });

  const result = await registry.execute("fail", {});
  expect(result.isError).toBe(true);
  expect(result.content).toContain("boom");
});

test("execute validates args before calling tool", async () => {
  const registry = new ToolRegistry();
  registry.register({
    name: "speak",
    description: "Say something",
    visibility: "llm_visible",
    parameters: Type.Object({ text: Type.String() }),
    execute: async (args) => ({ content: args.text as string }),
  });

  const result = await registry.execute("speak", {});
  expect(result.isError).toBe(true);
  expect(result.content).toContain("Invalid");
});
