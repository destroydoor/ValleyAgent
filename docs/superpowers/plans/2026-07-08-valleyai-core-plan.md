# ValleyAI Core (@valley/core) Implementation Plan

> **状态（2026-07-26 更新）：✅ 已实现**
>
> `@valley/core` 抽象框架已实现，位于 `D:\Source\ValleyAI\packages\core`。
> 配套的 `@valley/stardew` 星露谷实现也已完成，编译产物为
> `valley-ai-server.exe`。本文档为实施计划阶段产物，保留作为任务追踪依据。

---

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the abstract Agent execution framework and universal game AI primitives as `@valley/core`, a standalone TypeScript package runnable on Bun.

**Architecture:** Pi-style thin kernel — a pure-function `agentLoop` with 7 hook extension points, a stateful `Agent` wrapper with dual interrupt queues (steering + follow-up), and game AI primitives (Emotion/Memory/Affection/Goal/Health) as interfaces with default implementations. Core depends on zero game-specific types.

**Tech Stack:** Bun >= 1.3.13, TypeScript 5.x strict, Vercel AI SDK v5+ (@ai-sdk/openai, @ai-sdk/anthropic, @ai-sdk/google), @sinclair/typebox (schema validation), bun:sqlite (persistence), Bun native WebSocket, bun test (testing).

---

## File Structure

```
D:\Source\ValleyAI\
├── package.json                      npm workspaces root
├── tsconfig.base.json                shared TS strict config
├── bunfig.toml                       Bun config
├── .dependency-cruiser.cjs           import boundary enforcement
├── packages/
│   └── core/
│       ├── package.json              @valley/core
│       ├── tsconfig.json             extends base
│       ├── src/
│       │   ├── index.ts              public re-exports
│       │   ├── types.ts              AgentMessage, LlmMessage, AgentEvent union
│       │   ├── event-stream.ts       EventStream class
│       │   ├── tool.ts               Tool interface, ToolVisibility, ToolResult
│       │   ├── tool-registry.ts      ToolRegistry class
│       │   ├── circuit-breaker.ts    3-state circuit breaker
│       │   ├── token-budget.ts       TokenBudgetManager
│       │   ├── performance-monitor.ts PerformanceMonitor
│       │   ├── llm-config.ts         LLMConfig interface
│       │   ├── llm-provider.ts       LLMProvider interface + VercelAIProvider impl
│       │   ├── transport.ts          Transport + Connection interfaces
│       │   ├── bun-transport.ts      BunWebSocketTransport impl
│       │   ├── agent-loop.ts         agentLoop pure function + AgentLoopConfig
│       │   ├── agent.ts              Agent stateful wrapper
│       │   └── primitives/
│       │       ├── emotion.ts        EmotionSystem interface + DefaultEmotionSystem
│       │       ├── memory.ts         MemoryBackend interface + DefaultMemoryBackend
│       │       ├── affection.ts      AffectionSystem interface + DefaultAffectionSystem
│       │       ├── goals.ts          GoalSystem interface
│       │       └── health.ts         HealthSystem interface + DefaultHealthSystem
│       └── tests/
│           ├── types.test.ts
│           ├── event-stream.test.ts
│           ├── tool-registry.test.ts
│           ├── circuit-breaker.test.ts
│           ├── token-budget.test.ts
│           ├── performance-monitor.test.ts
│           ├── llm-provider.test.ts
│           ├── transport.test.ts
│           ├── agent-loop.test.ts
│           ├── agent.test.ts
│           ├── emotion.test.ts
│           ├── memory.test.ts
│           ├── affection.test.ts
│           ├── goals.test.ts
│           └── health.test.ts
```

**Responsibility boundaries:**
- `types.ts` — All shared type definitions (no runtime code)
- `event-stream.ts` — Async event publishing/subscribing
- `tool.ts` + `tool-registry.ts` — Tool definition, registration, schema validation, visibility filtering
- `circuit-breaker.ts` — Failure rate protection for LLM calls
- `token-budget.ts` — Token consumption tracking with time-window reset
- `performance-monitor.ts` — Operation timing stats
- `llm-config.ts` + `llm-provider.ts` — LLM abstraction over Vercel AI SDK
- `transport.ts` + `bun-transport.ts` — WebSocket transport abstraction
- `agent-loop.ts` — Pure function: context + config + abort → event stream
- `agent.ts` — Stateful wrapper: queues, lifecycle, public API
- `primitives/*.ts` — Game AI primitive interfaces + default implementations

---

## Task 1: Project Skeleton

**Files:**
- Create: `D:\Source\ValleyAI\package.json`
- Create: `D:\Source\ValleyAI\tsconfig.base.json`
- Create: `D:\Source\ValleyAI\bunfig.toml`
- Create: `D:\Source\ValleyAI\.dependency-cruiser.cjs`
- Create: `D:\Source\ValleyAI\packages\core\package.json`
- Create: `D:\Source\ValleyAI\packages\core\tsconfig.json`
- Create: `D:\Source\ValleyAI\packages\core\src\index.ts`
- Create: `D:\Source\ValleyAI\packages\core\tests\skeleton.test.ts`

- [ ] **Step 1: Create root package.json**

```json
{
  "name": "valleyai",
  "private": true,
  "workspaces": ["packages/*"],
  "scripts": {
    "test": "bun test",
    "typecheck": "tsc --noEmit",
    "check:imports": "depcruise --config .dependency-cruiser.cjs packages/core/src"
  },
  "devDependencies": {
    "typescript": "^5.7.0",
    "dependency-cruiser": "^16.0.0"
  }
}
```

- [ ] **Step 2: Create tsconfig.base.json**

```json
{
  "compilerOptions": {
    "target": "ES2022",
    "module": "ESNext",
    "moduleResolution": "bundler",
    "strict": true,
    "noUnusedLocals": true,
    "noUnusedParameters": true,
    "noImplicitReturns": true,
    "noFallthroughCasesInSwitch": true,
    "exactOptionalPropertyTypes": true,
    "noUncheckedIndexedAccess": true,
    "esModuleInterop": true,
    "skipLibCheck": true,
    "forceConsistentCasingInFileNames": true,
    "declaration": true,
    "declarationMap": true,
    "sourceMap": true,
    "lib": ["ES2022"]
  }
}
```

- [ ] **Step 3: Create bunfig.toml**

```toml
[test]
coverage = true
coverageThreshold = 0.8
```

- [ ] **Step 4: Create .dependency-cruiser.cjs**

```javascript
/** @type {import('dependency-cruiser').IConfiguration} */
module.exports = {
  forbidden: [
    {
      name: "core-cannot-depend-on-stardew",
      severity: "error",
      from: { path: "^packages/core/src/" },
      to: { path: "^packages/stardew/" }
    },
    {
      name: "core-cannot-depend-on-gamespecific",
      severity: "error",
      from: { path: "^packages/core/src/" },
      to: { path: "^packages/(?!core)" }
    }
  ]
};
```

- [ ] **Step 5: Create packages/core/package.json**

```json
{
  "name": "@valley/core",
  "version": "0.1.0",
  "type": "module",
  "main": "src/index.ts",
  "exports": {
    ".": "./src/index.ts"
  },
  "dependencies": {
    "ai": "^5.0.0",
    "@ai-sdk/openai": "^2.0.0",
    "@ai-sdk/anthropic": "^2.0.0",
    "@ai-sdk/google": "^2.0.0",
    "@sinclair/typebox": "^0.34.0"
  }
}
```

- [ ] **Step 6: Create packages/core/tsconfig.json**

```json
{
  "extends": "../../tsconfig.base.json",
  "compilerOptions": {
    "outDir": "./dist",
    "rootDir": "./src"
  },
  "include": ["src/**/*.ts"],
  "exclude": ["tests/**/*.ts", "dist/**/*.ts"]
}
```

- [ ] **Step 7: Create skeleton test and index.ts**

`packages/core/src/index.ts`:
```typescript
export const CORE_VERSION = "0.1.0";
```

`packages/core/tests/skeleton.test.ts`:
```typescript
import { test, expect } from "bun:test";
import { CORE_VERSION } from "../src/index";

test("CORE_VERSION is defined", () => {
  expect(CORE_VERSION).toBe("0.1.0");
});
```

- [ ] **Step 8: Install dependencies and run test**

Run: `cd D:\Source\ValleyAI && bun install`
Expected: dependencies installed, lockfile created

Run: `cd D:\Source\ValleyAI && bun test packages/core/tests/skeleton.test.ts`
Expected: 1 pass, 0 fail

Run: `cd D:\Source\ValleyAI && bun run typecheck`
Expected: 0 errors, 0 warnings

- [ ] **Step 9: Commit**

```bash
cd D:\Source\ValleyAI
git init
git add -A
git commit -m "feat(core): project skeleton with npm workspaces and bun test"
```

---

## Task 2: Types & EventStream

**Files:**
- Create: `packages/core/src/types.ts`
- Create: `packages/core/src/event-stream.ts`
- Test: `packages/core/tests/types.test.ts`
- Test: `packages/core/tests/event-stream.test.ts`

- [ ] **Step 1: Write failing test for types**

`packages/core/tests/types.test.ts`:
```typescript
import { test, expect } from "bun:test";
import type {
  AgentMessage,
  LlmMessage,
  AgentEvent,
  AgentEventType,
} from "../src/types";

test("AgentMessage user type has role and content", () => {
  const msg: AgentMessage = { role: "user", content: "hello" };
  expect(msg.role).toBe("user");
  expect(msg.content).toBe("hello");
});

test("AgentMessage assistant type has role and content", () => {
  const msg: AgentMessage = { role: "assistant", content: "hi there" };
  expect(msg.role).toBe("assistant");
});

test("LlmMessage system type", () => {
  const msg: LlmMessage = { role: "system", content: "you are an NPC" };
  expect(msg.role).toBe("system");
});

test("AgentEvent agent_start has type and timestamp", () => {
  const event: AgentEvent = {
    type: "agent_start",
    timestamp: Date.now(),
  };
  expect(event.type).toBe("agent_start");
});

test("AgentEvent tool_call_start has toolName and args", () => {
  const event: AgentEvent = {
    type: "tool_call_start",
    timestamp: Date.now(),
    toolName: "speak",
    toolCallId: "call_1",
    args: { text: "hello" },
  };
  expect(event.type).toBe("tool_call_start");
  if (event.type === "tool_call_start") {
    expect(event.toolName).toBe("speak");
  }
});

test("AgentEvent error has message", () => {
  const event: AgentEvent = {
    type: "error",
    timestamp: Date.now(),
    message: "something went wrong",
  };
  if (event.type === "error") {
    expect(event.message).toBe("something went wrong");
  }
});

test("all 10 event types are representable", () => {
  const types: AgentEventType[] = [
    "agent_start", "agent_end",
    "turn_start", "turn_end",
    "message_start", "message_update", "message_end",
    "tool_call_start", "tool_call_end",
    "error",
  ];
  expect(types).toHaveLength(10);
});
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd D:\Source\ValleyAI && bun test packages/core/tests/types.test.ts`
Expected: FAIL — cannot find module `../src/types`

- [ ] **Step 3: Implement types.ts**

`packages/core/src/types.ts`:
```typescript
// ─── Agent Messages ───

export interface AgentMessage {
  role: "user" | "assistant" | "system";
  content: string;
  toolCallId?: string;
  toolCalls?: AgentToolCall[];
}

export interface AgentToolCall {
  id: string;
  name: string;
  args: Record<string, unknown>;
}

// ─── LLM Messages (Vercel AI SDK compatible) ───

export type LlmMessage =
  | { role: "system"; content: string }
  | { role: "user"; content: string }
  | { role: "assistant"; content: string; toolCalls?: LlmToolCall[] }
  | { role: "tool"; content: string; toolCallId: string };

export interface LlmToolCall {
  id: string;
  type: "function";
  functionName: string;
  args: string;
}

// ─── Agent Events (discriminated union, 10 types) ───

export type AgentEventType =
  | "agent_start"
  | "agent_end"
  | "turn_start"
  | "turn_end"
  | "message_start"
  | "message_update"
  | "message_end"
  | "tool_call_start"
  | "tool_call_end"
  | "error";

export interface BaseEvent {
  type: AgentEventType;
  timestamp: number;
}

export interface AgentStartEvent extends BaseEvent {
  type: "agent_start";
}

export interface AgentEndEvent extends BaseEvent {
  type: "agent_end";
}

export interface TurnStartEvent extends BaseEvent {
  type: "turn_start";
  turnIndex: number;
}

export interface TurnEndEvent extends BaseEvent {
  type: "turn_end";
  turnIndex: number;
}

export interface MessageStartEvent extends BaseEvent {
  type: "message_start";
}

export interface MessageUpdateEvent extends BaseEvent {
  type: "message_update";
  delta: string;
}

export interface MessageEndEvent extends BaseEvent {
  type: "message_end";
  content: string;
}

export interface ToolCallStartEvent extends BaseEvent {
  type: "tool_call_start";
  toolName: string;
  toolCallId: string;
  args: Record<string, unknown>;
}

export interface ToolCallEndEvent extends BaseEvent {
  type: "tool_call_end";
  toolName: string;
  toolCallId: string;
  result: unknown;
  isError: boolean;
}

export interface ErrorEvent extends BaseEvent {
  type: "error";
  message: string;
}

export type AgentEvent =
  | AgentStartEvent
  | AgentEndEvent
  | TurnStartEvent
  | TurnEndEvent
  | MessageStartEvent
  | MessageUpdateEvent
  | MessageEndEvent
  | ToolCallStartEvent
  | ToolCallEndEvent
  | ErrorEvent;

// ─── Agent Context ───

export interface AgentContext {
  messages: AgentMessage[];
  systemPrompt: string;
  metadata: Record<string, unknown>;
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `cd D:\Source\ValleyAI && bun test packages/core/tests/types.test.ts`
Expected: 7 pass, 0 fail

- [ ] **Step 5: Write failing test for EventStream**

`packages/core/tests/event-stream.test.ts`:
```typescript
import { test, expect } from "bun:test";
import { EventStream } from "../src/event-stream";
import type { AgentEvent } from "../src/types";

test("subscribe receives emitted events", async () => {
  const stream = new EventStream<AgentEvent>();
  const received: AgentEvent[] = [];
  stream.subscribe((event) => received.push(event));

  const event: AgentEvent = { type: "agent_start", timestamp: Date.now() };
  stream.emit(event);

  expect(received).toHaveLength(1);
  expect(received[0]!.type).toBe("agent_start");
});

test("multiple subscribers all receive events", () => {
  const stream = new EventStream<AgentEvent>();
  const received1: AgentEvent[] = [];
  const received2: AgentEvent[] = [];
  stream.subscribe((e) => received1.push(e));
  stream.subscribe((e) => received2.push(e));

  stream.emit({ type: "agent_end", timestamp: Date.now() });

  expect(received1).toHaveLength(1);
  expect(received2).toHaveLength(1);
});

test("unsubscribe stops receiving events", () => {
  const stream = new EventStream<AgentEvent>();
  const received: AgentEvent[] = [];
  const unsub = stream.subscribe((e) => received.push(e));

  stream.emit({ type: "turn_start", timestamp: Date.now(), turnIndex: 0 });
  unsub();
  stream.emit({ type: "turn_end", timestamp: Date.now(), turnIndex: 0 });

  expect(received).toHaveLength(1);
});

test("awaitAll resolves when done() is called", async () => {
  const stream = new EventStream<AgentEvent>();
  const promise = stream.awaitAll();

  stream.emit({ type: "agent_start", timestamp: Date.now() });
  stream.done();

  const events = await promise;
  expect(events).toHaveLength(1);
  expect(events[0]!.type).toBe("agent_start");
});

test("awaitAll collects all events emitted before done", async () => {
  const stream = new EventStream<AgentEvent>();
  const promise = stream.awaitAll();

  stream.emit({ type: "agent_start", timestamp: 1 });
  stream.emit({ type: "turn_start", timestamp: 2, turnIndex: 0 });
  stream.emit({ type: "agent_end", timestamp: 3 });
  stream.done();

  const events = await promise;
  expect(events).toHaveLength(3);
});

test("isDone returns true after done() is called", () => {
  const stream = new EventStream<AgentEvent>();
  expect(stream.isDone()).toBe(false);
  stream.done();
  expect(stream.isDone()).toBe(true);
});

test("emit after done throws", () => {
  const stream = new EventStream<AgentEvent>();
  stream.done();
  expect(() => stream.emit({ type: "agent_start", timestamp: 1 })).toThrow(
    "EventStream is done"
  );
});
```

- [ ] **Step 6: Run test to verify it fails**

Run: `cd D:\Source\ValleyAI && bun test packages/core/tests/event-stream.test.ts`
Expected: FAIL — cannot find module `../src/event-stream`

- [ ] **Step 7: Implement event-stream.ts**

`packages/core/src/event-stream.ts`:
```typescript
type Subscriber<T> = (event: T) => void;

export class EventStream<T> {
  private subscribers: Set<Subscriber<T>> = new Set();
  private collectedEvents: T[] = [];
  private _isDone = false;
  private doneResolve: ((events: T[]) => void) | null = null;
  private donePromise: Promise<T[]> | null = null;

  subscribe(fn: Subscriber<T>): () => void {
    if (this._isDone) {
      throw new Error("EventStream is done");
    }
    this.subscribers.add(fn);
    return () => {
      this.subscribers.delete(fn);
    };
  }

  emit(event: T): void {
    if (this._isDone) {
      throw new Error("EventStream is done");
    }
    this.collectedEvents.push(event);
    for (const sub of this.subscribers) {
      sub(event);
    }
  }

  done(): void {
    if (this._isDone) return;
    this._isDone = true;
    this.subscribers.clear();
    if (this.doneResolve) {
      this.doneResolve([...this.collectedEvents]);
    }
  }

  isDone(): boolean {
    return this._isDone;
  }

  awaitAll(): Promise<T[]> {
    if (this._isDone) {
      return Promise.resolve([...this.collectedEvents]);
    }
    if (!this.donePromise) {
      this.donePromise = new Promise<T[]>((resolve) => {
        this.doneResolve = resolve;
      });
    }
    return this.donePromise;
  }
}
```

- [ ] **Step 8: Run test to verify it passes**

Run: `cd D:\Source\ValleyAI && bun test packages/core/tests/event-stream.test.ts`
Expected: 7 pass, 0 fail

- [ ] **Step 9: Run typecheck**

Run: `cd D:\Source\ValleyAI && bun run typecheck`
Expected: 0 errors, 0 warnings

- [ ] **Step 10: Commit**

```bash
cd D:\Source\ValleyAI
git add -A
git commit -m "feat(core): types and EventStream"
```

---

## Task 3: Tool System

**Files:**
- Create: `packages/core/src/tool.ts`
- Create: `packages/core/src/tool-registry.ts`
- Test: `packages/core/tests/tool-registry.test.ts`

- [ ] **Step 1: Write failing test for tool system**

`packages/core/tests/tool-registry.test.ts`:
```typescript
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
  let capturedArgs: Record<string, unknown> | null = null;
  registry.register({
    name: "speak",
    description: "Say something",
    visibility: "llm_visible",
    parameters: Type.Object({ text: Type.String() }),
    execute: async (args) => {
      capturedArgs = args;
      return { content: "said: " + (args.text as string) };
    },
  });

  const result = await registry.execute("speak", { text: "hello" });
  expect(result.content).toBe("said: hello");
  expect(capturedArgs).toEqual({ text: "hello" });
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
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd D:\Source\ValleyAI && bun test packages/core/tests/tool-registry.test.ts`
Expected: FAIL — cannot find module

- [ ] **Step 3: Implement tool.ts**

`packages/core/src/tool.ts`:
```typescript
import type { TSchema } from "@sinclair/typebox";

export type ToolVisibility = "llm_visible" | "tactical" | "local";

export interface ToolResult {
  content: string;
  details?: Record<string, unknown>;
  isError?: boolean;
  terminate?: boolean;
}

export interface Tool {
  name: string;
  description: string;
  visibility: ToolVisibility;
  parameters: TSchema;
  execute: (args: Record<string, unknown>) => Promise<ToolResult>;
}

export interface ValidationResult {
  valid: boolean;
  errors?: string[];
}
```

- [ ] **Step 4: Implement tool-registry.ts**

`packages/core/src/tool-registry.ts`:
```typescript
import { Value } from "@sinclair/typebox/value";
import type { Tool, ToolResult, ToolVisibility, ValidationResult } from "./tool";

export class ToolRegistry {
  private tools: Map<string, Tool> = new Map();

  register(tool: Tool): void {
    if (this.tools.has(tool.name)) {
      throw new Error(`Tool already registered: ${tool.name}`);
    }
    this.tools.set(tool.name, tool);
  }

  unregister(name: string): void {
    this.tools.delete(name);
  }

  getByName(name: string): Tool | undefined {
    return this.tools.get(name);
  }

  getByVisibility(visibility: ToolVisibility): Tool[] {
    return [...this.tools.values()].filter((t) => t.visibility === visibility);
  }

  getLlmVisibleTools(): Tool[] {
    return this.getByVisibility("llm_visible");
  }

  getAll(): Tool[] {
    return [...this.tools.values()];
  }

  validateCall(name: string, args: Record<string, unknown>): ValidationResult {
    const tool = this.tools.get(name);
    if (!tool) {
      return { valid: false, errors: [`Tool not found: ${name}`] };
    }
    const errors = [...Value.Errors(tool.parameters, args)];
    if (errors.length > 0) {
      return {
        valid: false,
        errors: errors.map((e) => `${e.path}: ${e.message}`),
      };
    }
    return { valid: true };
  }

  async execute(name: string, args: Record<string, unknown>): Promise<ToolResult> {
    const validation = this.validateCall(name, args);
    if (!validation.valid) {
      return {
        content: `Invalid arguments: ${validation.errors!.join(", ")}`,
        isError: true,
      };
    }
    const tool = this.tools.get(name)!;
    try {
      return await tool.execute(args);
    } catch (err) {
      const message = err instanceof Error ? err.message : String(err);
      return {
        content: `Tool execution failed: ${message}`,
        isError: true,
      };
    }
  }
}
```

- [ ] **Step 5: Run test to verify it passes**

Run: `cd D:\Source\ValleyAI && bun test packages/core/tests/tool-registry.test.ts`
Expected: 12 pass, 0 fail

- [ ] **Step 6: Run typecheck and commit**

Run: `cd D:\Source\ValleyAI && bun run typecheck`
Expected: 0 errors, 0 warnings

```bash
cd D:\Source\ValleyAI
git add -A
git commit -m "feat(core): tool system with schema validation and visibility tiers"
```

---

## Task 4: Circuit Breaker

**Files:**
- Create: `packages/core/src/circuit-breaker.ts`
- Test: `packages/core/tests/circuit-breaker.test.ts`

- [ ] **Step 1: Write failing test**

`packages/core/tests/circuit-breaker.test.ts`:
```typescript
import { test, expect } from "bun:test";
import { CircuitBreaker, CircuitState } from "../src/circuit-breaker";

test("starts in CLOSED state", () => {
  const cb = new CircuitBreaker({ threshold: 3, recoveryTime: 1000 });
  expect(cb.getState()).toBe(CircuitState.CLOSED);
  expect(cb.canExecute()).toBe(true);
});

test("opens after threshold failures", () => {
  const cb = new CircuitBreaker({ threshold: 3, recoveryTime: 1000 });
  cb.recordFailure();
  cb.recordFailure();
  expect(cb.getState()).toBe(CircuitState.CLOSED);
  cb.recordFailure();
  expect(cb.getState()).toBe(CircuitState.OPEN);
  expect(cb.canExecute()).toBe(false);
});

test("CLOSED state allows execution", () => {
  const cb = new CircuitBreaker({ threshold: 5, recoveryTime: 1000 });
  expect(cb.canExecute()).toBe(true);
});

test("OPEN state blocks execution", () => {
  const cb = new CircuitBreaker({ threshold: 2, recoveryTime: 1000 });
  cb.recordFailure();
  cb.recordFailure();
  expect(cb.canExecute()).toBe(false);
});

test("transitions to HALF_OPEN after recoveryTime", async () => {
  const cb = new CircuitBreaker({ threshold: 2, recoveryTime: 50, halfOpenMaxCalls: 1 });
  cb.recordFailure();
  cb.recordFailure();
  expect(cb.getState()).toBe(CircuitState.OPEN);

  await new Promise((resolve) => setTimeout(resolve, 60));

  expect(cb.canExecute()).toBe(true);
  expect(cb.getState()).toBe(CircuitState.HALF_OPEN);
});

test("HALF_OPEN success closes the circuit", async () => {
  const cb = new CircuitBreaker({ threshold: 2, recoveryTime: 50, halfOpenMaxCalls: 1 });
  cb.recordFailure();
  cb.recordFailure();

  await new Promise((resolve) => setTimeout(resolve, 60));
  cb.canExecute();
  expect(cb.getState()).toBe(CircuitState.HALF_OPEN);

  cb.recordSuccess();
  expect(cb.getState()).toBe(CircuitState.CLOSED);
});

test("HALF_OPEN failure reopens the circuit", async () => {
  const cb = new CircuitBreaker({ threshold: 2, recoveryTime: 50, halfOpenMaxCalls: 1 });
  cb.recordFailure();
  cb.recordFailure();

  await new Promise((resolve) => setTimeout(resolve, 60));
  cb.canExecute();
  expect(cb.getState()).toBe(CircuitState.HALF_OPEN);

  cb.recordFailure();
  expect(cb.getState()).toBe(CircuitState.OPEN);
});

test("recordSuccess resets failure count in CLOSED", () => {
  const cb = new CircuitBreaker({ threshold: 3, recoveryTime: 1000 });
  cb.recordFailure();
  cb.recordFailure();
  cb.recordSuccess();
  cb.recordFailure();
  expect(cb.getState()).toBe(CircuitState.CLOSED);
});

test("reset returns to CLOSED", () => {
  const cb = new CircuitBreaker({ threshold: 2, recoveryTime: 1000 });
  cb.recordFailure();
  cb.recordFailure();
  expect(cb.getState()).toBe(CircuitState.OPEN);

  cb.reset();
  expect(cb.getState()).toBe(CircuitState.CLOSED);
});

test("forceOpen sets state to OPEN", () => {
  const cb = new CircuitBreaker({ threshold: 5, recoveryTime: 1000 });
  cb.forceOpen();
  expect(cb.getState()).toBe(CircuitState.OPEN);
  expect(cb.canExecute()).toBe(false);
});

test("isOpen returns true only in OPEN state", () => {
  const cb = new CircuitBreaker({ threshold: 2, recoveryTime: 1000 });
  expect(cb.isOpen()).toBe(false);
  cb.recordFailure();
  cb.recordFailure();
  expect(cb.isOpen()).toBe(true);
});
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd D:\Source\ValleyAI && bun test packages/core/tests/circuit-breaker.test.ts`
Expected: FAIL — cannot find module

- [ ] **Step 3: Implement circuit-breaker.ts**

`packages/core/src/circuit-breaker.ts`:
```typescript
export enum CircuitState {
  CLOSED = "CLOSED",
  OPEN = "OPEN",
  HALF_OPEN = "HALF_OPEN",
}

export interface CircuitBreakerConfig {
  threshold: number;
  recoveryTime: number; // ms
  halfOpenMaxCalls?: number;
}

export class CircuitBreaker {
  private state: CircuitState = CircuitState.CLOSED;
  private failureCount = 0;
  private lastFailureTime = 0;
  private halfOpenCalls = 0;
  private readonly config: Required<CircuitBreakerConfig>;

  constructor(config: CircuitBreakerConfig) {
    this.config = {
      threshold: config.threshold,
      recoveryTime: config.recoveryTime,
      halfOpenMaxCalls: config.halfOpenMaxCalls ?? 1,
    };
  }

  getState(): CircuitState {
    this.checkRecovery();
    return this.state;
  }

  canExecute(): boolean {
    this.checkRecovery();
    if (this.state === CircuitState.CLOSED) return true;
    if (this.state === CircuitState.HALF_OPEN) {
      if (this.halfOpenCalls < this.config.halfOpenMaxCalls) {
        this.halfOpenCalls++;
        return true;
      }
      return false;
    }
    return false;
  }

  recordSuccess(): void {
    if (this.state === CircuitState.HALF_OPEN) {
      this.state = CircuitState.CLOSED;
      this.failureCount = 0;
      this.halfOpenCalls = 0;
      return;
    }
    if (this.state === CircuitState.CLOSED) {
      this.failureCount = 0;
    }
  }

  recordFailure(): void {
    this.failureCount++;
    this.lastFailureTime = Date.now();

    if (this.state === CircuitState.HALF_OPEN) {
      this.state = CircuitState.OPEN;
      this.halfOpenCalls = 0;
      return;
    }

    if (this.failureCount >= this.config.threshold) {
      this.state = CircuitState.OPEN;
    }
  }

  isOpen(): boolean {
    return this.getState() === CircuitState.OPEN;
  }

  reset(): void {
    this.state = CircuitState.CLOSED;
    this.failureCount = 0;
    this.halfOpenCalls = 0;
  }

  forceOpen(): void {
    this.state = CircuitState.OPEN;
    this.lastFailureTime = Date.now();
  }

  private checkRecovery(): void {
    if (this.state === CircuitState.OPEN) {
      const elapsed = Date.now() - this.lastFailureTime;
      if (elapsed >= this.config.recoveryTime) {
        this.state = CircuitState.HALF_OPEN;
        this.halfOpenCalls = 0;
      }
    }
  }
}
```

- [ ] **Step 4: Run test, typecheck, commit**

Run: `cd D:\Source\ValleyAI && bun test packages/core/tests/circuit-breaker.test.ts`
Expected: 11 pass, 0 fail

Run: `cd D:\Source\ValleyAI && bun run typecheck`
Expected: 0 errors

```bash
cd D:\Source\ValleyAI
git add -A
git commit -m "feat(core): circuit breaker with 3-state protection"
```

---

## Task 5: Token Budget Manager

**Files:**
- Create: `packages/core/src/token-budget.ts`
- Test: `packages/core/tests/token-budget.test.ts`

- [ ] **Step 1: Write failing test**

`packages/core/tests/token-budget.test.ts`:
```typescript
import { test, expect } from "bun:test";
import { TokenBudgetManager } from "../src/token-budget";

test("starts with full budget", () => {
  const mgr = new TokenBudgetManager({ budget: 10000, windowMs: 60000 });
  expect(mgr.getRemaining()).toBe(10000);
});

test("consume reduces remaining", () => {
  const mgr = new TokenBudgetManager({ budget: 10000, windowMs: 60000 });
  mgr.consume(3000);
  expect(mgr.getRemaining()).toBe(7000);
});

test("consume returns true when within budget", () => {
  const mgr = new TokenBudgetManager({ budget: 10000, windowMs: 60000 });
  expect(mgr.consume(5000)).toBe(true);
  expect(mgr.consume(5000)).toBe(true);
});

test("consume returns false when exceeding budget", () => {
  const mgr = new TokenBudgetManager({ budget: 10000, windowMs: 60000 });
  mgr.consume(8000);
  expect(mgr.consume(3000)).toBe(false);
  expect(mgr.getRemaining()).toBe(2000);
});

test("budget resets after window", async () => {
  const mgr = new TokenBudgetManager({ budget: 10000, windowMs: 50 });
  mgr.consume(8000);
  expect(mgr.getRemaining()).toBe(2000);

  await new Promise((r) => setTimeout(r, 60));
  expect(mgr.getRemaining()).toBe(10000);
});

test("budget 0 means unlimited", () => {
  const mgr = new TokenBudgetManager({ budget: 0, windowMs: 60000 });
  expect(mgr.getRemaining()).toBe(Infinity);
  expect(mgr.consume(999999)).toBe(true);
  expect(mgr.getRemaining()).toBe(Infinity);
});

test("reset restores full budget", () => {
  const mgr = new TokenBudgetManager({ budget: 10000, windowMs: 60000 });
  mgr.consume(5000);
  mgr.reset();
  expect(mgr.getRemaining()).toBe(10000);
});

test("getUsed returns consumed amount", () => {
  const mgr = new TokenBudgetManager({ budget: 10000, windowMs: 60000 });
  mgr.consume(3000);
  mgr.consume(2000);
  expect(mgr.getUsed()).toBe(5000);
});
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd D:\Source\ValleyAI && bun test packages/core/tests/token-budget.test.ts`
Expected: FAIL — cannot find module

- [ ] **Step 3: Implement token-budget.ts**

`packages/core/src/token-budget.ts`:
```typescript
export interface TokenBudgetConfig {
  budget: number; // 0 = unlimited
  windowMs: number;
}

export class TokenBudgetManager {
  private used = 0;
  private windowStart = 0;
  private readonly config: TokenBudgetConfig;

  constructor(config: TokenBudgetConfig) {
    this.config = config;
    this.windowStart = Date.now();
  }

  consume(amount: number): boolean {
    if (this.config.budget === 0) return true;

    this.checkWindowReset();

    if (this.used + amount > this.config.budget) {
      return false;
    }
    this.used += amount;
    return true;
  }

  getRemaining(): number {
    if (this.config.budget === 0) return Infinity;
    this.checkWindowReset();
    return Math.max(0, this.config.budget - this.used);
  }

  getUsed(): number {
    this.checkWindowReset();
    return this.used;
  }

  reset(): void {
    this.used = 0;
    this.windowStart = Date.now();
  }

  private checkWindowReset(): void {
    const elapsed = Date.now() - this.windowStart;
    if (elapsed >= this.config.windowMs) {
      this.used = 0;
      this.windowStart = Date.now();
    }
  }
}
```

- [ ] **Step 4: Run test, typecheck, commit**

Run: `cd D:\Source\ValleyAI && bun test packages/core/tests/token-budget.test.ts`
Expected: 8 pass, 0 fail

Run: `cd D:\Source\ValleyAI && bun run typecheck`
Expected: 0 errors

```bash
cd D:\Source\ValleyAI
git add -A
git commit -m "feat(core): token budget manager with time-window reset"
```

---

## Task 6: Performance Monitor

**Files:**
- Create: `packages/core/src/performance-monitor.ts`
- Test: `packages/core/tests/performance-monitor.test.ts`

- [ ] **Step 1: Write failing test**

`packages/core/tests/performance-monitor.test.ts`:
```typescript
import { test, expect } from "bun:test";
import { PerformanceMonitor } from "../src/performance-monitor";

test("recordOperation stores timing", () => {
  const monitor = new PerformanceMonitor();
  monitor.recordOperation("llm_call", 150);
  monitor.recordOperation("llm_call", 250);

  const stats = monitor.getStats("llm_call");
  expect(stats.count).toBe(2);
  expect(stats.totalMs).toBe(400);
  expect(stats.avgMs).toBe(200);
  expect(stats.maxMs).toBe(250);
  expect(stats.minMs).toBe(150);
});

test("getStats returns zeros for unknown operation", () => {
  const monitor = new PerformanceMonitor();
  const stats = monitor.getStats("unknown");
  expect(stats.count).toBe(0);
  expect(stats.avgMs).toBe(0);
});

test("getSlowOperations returns operations above threshold", () => {
  const monitor = new PerformanceMonitor();
  monitor.recordOperation("llm_call", 50);
  monitor.recordOperation("llm_call", 500);
  monitor.recordOperation("tool_exec", 300);

  const slow = monitor.getSlowOperations(200);
  expect(slow).toHaveLength(2);
  const names = slow.map((s) => s.name);
  expect(names).toContain("llm_call");
  expect(names).toContain("tool_exec");
});

test("getSlowOperations on specific operation", () => {
  const monitor = new PerformanceMonitor();
  monitor.recordOperation("llm_call", 50);
  monitor.recordOperation("llm_call", 500);

  const slow = monitor.getSlowOperations(200, "llm_call");
  expect(slow).toHaveLength(1);
  expect(slow[0]!.name).toBe("llm_call");
  expect(slow[0]!.maxMs).toBe(500);
});

test("reset clears all stats", () => {
  const monitor = new PerformanceMonitor();
  monitor.recordOperation("llm_call", 100);
  monitor.reset();
  expect(monitor.getStats("llm_call").count).toBe(0);
});

test("measure wraps async function and records timing", async () => {
  const monitor = new PerformanceMonitor();
  const result = await monitor.measure("test_op", async () => {
    await new Promise((r) => setTimeout(r, 10));
    return 42;
  });
  expect(result).toBe(42);
  const stats = monitor.getStats("test_op");
  expect(stats.count).toBe(1);
  expect(stats.avgMs).toBeGreaterThanOrEqual(8);
});
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd D:\Source\ValleyAI && bun test packages/core/tests/performance-monitor.test.ts`
Expected: FAIL — cannot find module

- [ ] **Step 3: Implement performance-monitor.ts**

`packages/core/src/performance-monitor.ts`:
```typescript
interface OperationStats {
  count: number;
  totalMs: number;
  minMs: number;
  maxMs: number;
}

interface SlowOperation {
  name: string;
  count: number;
  totalMs: number;
  minMs: number;
  maxMs: number;
  avgMs: number;
}

export class PerformanceMonitor {
  private stats: Map<string, OperationStats> = new Map();

  recordOperation(name: string, durationMs: number): void {
    const existing = this.stats.get(name);
    if (existing) {
      existing.count++;
      existing.totalMs += durationMs;
      existing.minMs = Math.min(existing.minMs, durationMs);
      existing.maxMs = Math.max(existing.maxMs, durationMs);
    } else {
      this.stats.set(name, {
        count: 1,
        totalMs: durationMs,
        minMs: durationMs,
        maxMs: durationMs,
      });
    }
  }

  getStats(name: string): OperationStats & { avgMs: number } {
    const s = this.stats.get(name);
    if (!s) {
      return { count: 0, totalMs: 0, minMs: 0, maxMs: 0, avgMs: 0 };
    }
    return { ...s, avgMs: s.totalMs / s.count };
  }

  getSlowOperations(thresholdMs: number, operationName?: string): SlowOperation[] {
    const result: SlowOperation[] = [];
    const entries = operationName
      ? [[operationName, this.stats.get(operationName)] as const].filter(([, v]) => v !== undefined)
      : [...this.stats.entries()];

    for (const [name, s] of entries) {
      if (s && s.maxMs >= thresholdMs) {
        result.push({
          name,
          count: s.count,
          totalMs: s.totalMs,
          minMs: s.minMs,
          maxMs: s.maxMs,
          avgMs: s.totalMs / s.count,
        });
      }
    }
    return result;
  }

  reset(): void {
    this.stats.clear();
  }

  async measure<T>(name: string, fn: () => Promise<T>): Promise<T> {
    const start = performance.now();
    try {
      return await fn();
    } finally {
      const duration = performance.now() - start;
      this.recordOperation(name, duration);
    }
  }
}
```

- [ ] **Step 4: Run test, typecheck, commit**

Run: `cd D:\Source\ValleyAI && bun test packages/core/tests/performance-monitor.test.ts`
Expected: 6 pass, 0 fail

Run: `cd D:\Source\ValleyAI && bun run typecheck`
Expected: 0 errors

```bash
cd D:\Source\ValleyAI
git add -A
git commit -m "feat(core): performance monitor with measure wrapper"
```

---

## Task 7: LLM Provider

**Files:**
- Create: `packages/core/src/llm-config.ts`
- Create: `packages/core/src/llm-provider.ts`
- Test: `packages/core/tests/llm-provider.test.ts`

- [ ] **Step 1: Write failing test**

`packages/core/tests/llm-provider.test.ts`:
```typescript
import { test, expect } from "bun:test";
import { LLMConfig } from "../src/llm-config";
import { VercelAIProvider, LLMBillingError, LLMUnavailableError } from "../src/llm-provider";

function makeConfig(overrides: Partial<LLMConfig> = {}): LLMConfig {
  return {
    provider: "openai",
    baseUrl: "http://localhost:9999",
    apiKey: "test-key",
    model: "gpt-4o-mini",
    temperature: 0.7,
    maxTokens: 2000,
    timeout: 5000,
    maxRetries: 3,
    maxConcurrency: 2,
    tokenBudget: 0,
    ...overrides,
  };
}

test("LLMBillingError is thrown for 402 status", async () => {
  const config = makeConfig({ maxRetries: 1 });
  const provider = new VercelAIProvider(config);

  provider._setCallOverride(async () => {
    throw new LLMBillingError("Insufficient credits");
  });

  await expect(provider.chatCompletion([])).rejects.toThrow(LLMBillingError);
});

test("LLMBillingError is thrown for 429 status", async () => {
  const config = makeConfig({ maxRetries: 1 });
  const provider = new VercelAIProvider(config);

  provider._setCallOverride(async () => {
    throw new LLMBillingError("Rate limited");
  });

  await expect(provider.chatCompletion([])).rejects.toThrow(LLMBillingError);
});

test("LLMUnavailableError after max retries", async () => {
  const config = makeConfig({ maxRetries: 2 });
  const provider = new VercelAIProvider(config);

  let calls = 0;
  provider._setCallOverride(async () => {
    calls++;
    throw new Error("Connection refused");
  });

  await expect(provider.chatCompletion([])).rejects.toThrow(LLMUnavailableError);
  expect(calls).toBe(2);
});

test("successful call returns content", async () => {
  const config = makeConfig({ maxRetries: 2 });
  const provider = new VercelAIProvider(config);

  provider._setCallOverride(async () => {
    return { content: "Hello from LLM", usage: { totalTokens: 10 } };
  });

  const result = await provider.chatCompletion([
    { role: "user", content: "hi" },
  ]);
  expect(result.content).toBe("Hello from LLM");
});

test("retry succeeds on second attempt", async () => {
  const config = makeConfig({ maxRetries: 3 });
  const provider = new VercelAIProvider(config);

  let calls = 0;
  provider._setCallOverride(async () => {
    calls++;
    if (calls === 1) throw new Error("Transient error");
    return { content: "Success", usage: { totalTokens: 5 } };
  });

  const result = await provider.chatCompletion([]);
  expect(result.content).toBe("Success");
  expect(calls).toBe(2);
});

test("chatCompletionJson returns parsed object", async () => {
  const config = makeConfig();
  const provider = new VercelAIProvider(config);

  provider._setCallOverride(async () => {
    return {
      content: '{"targetState":"FARM","reason":"helping"}',
      usage: { totalTokens: 20 },
    };
  });

  const result = await provider.chatCompletionJson([]);
  expect(result.parsed.targetState).toBe("FARM");
  expect(result.parsed.reason).toBe("helping");
});

test("chatCompletionJson throws on invalid JSON", async () => {
  const config = makeConfig({ maxRetries: 1 });
  const provider = new VercelAIProvider(config);

  provider._setCallOverride(async () => {
    return { content: "not json", usage: { totalTokens: 5 } };
  });

  await expect(provider.chatCompletionJson([])).rejects.toThrow();
});

test("token budget is tracked", async () => {
  const config = makeConfig({ tokenBudget: 100, maxRetries: 1 });
  const provider = new VercelAIProvider(config);

  provider._setCallOverride(async () => {
    return { content: "ok", usage: { totalTokens: 30 } };
  });

  await provider.chatCompletion([]);
  await provider.chatCompletion([]);
  await provider.chatCompletion([]);

  await expect(provider.chatCompletion([])).rejects.toThrow(/budget/i);
});

test("concurrency limit prevents parallel calls beyond max", async () => {
  const config = makeConfig({ maxConcurrency: 2, maxRetries: 1 });
  const provider = new VercelAIProvider(config);

  let activeCalls = 0;
  let maxActiveCalls = 0;
  provider._setCallOverride(async () => {
    activeCalls++;
    maxActiveCalls = Math.max(maxActiveCalls, activeCalls);
    await new Promise((r) => setTimeout(r, 20));
    activeCalls--;
    return { content: "ok", usage: { totalTokens: 1 } };
  });

  await Promise.all([
    provider.chatCompletion([]),
    provider.chatCompletion([]),
    provider.chatCompletion([]),
    provider.chatCompletion([]),
  ]);

  expect(maxActiveCalls).toBeLessThanOrEqual(2);
});

test("stripThinking removes think tags", () => {
  const config = makeConfig();
  const provider = new VercelAIProvider(config);

  expect(provider._stripThinking("<think>internal</think>actual response")).toBe(
    "actual response"
  );
  expect(provider._stripThinking("no tags here")).toBe("no tags here");
  expect(provider._stripThinking("<think>only think</think>")).toBe("");
});

test("stripThinking handles channel tags", () => {
  const config = makeConfig();
  const provider = new VercelAIProvider(config);

  expect(
    provider._stripThinking("<|channel|>analysis<|end|>final answer")
  ).toBe("final answer");
});

test("stripThinking handles answer tags", () => {
  const config = makeConfig();
  const provider = new VercelAIProvider(config);

  expect(
    provider._stripThinking("reasoning here<answer>the answer</answer>")
  ).toBe("the answer");
});

test("preWarm initializes without error", async () => {
  const config = makeConfig();
  const provider = new VercelAIProvider(config);
  await expect(provider.preWarm()).resolves.toBeUndefined();
});
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd D:\Source\ValleyAI && bun test packages/core/tests/llm-provider.test.ts`
Expected: FAIL — cannot find module

- [ ] **Step 3: Implement llm-config.ts**

`packages/core/src/llm-config.ts`:
```typescript
export type LLMProviderType =
  | "openai"
  | "anthropic"
  | "google"
  | "deepseek"
  | "openrouter"
  | "lmstudio"
  | "minimax";

export interface LLMConfig {
  provider: LLMProviderType;
  baseUrl?: string;
  apiKey: string;
  model: string;
  temperature?: number;
  maxTokens?: number;
  timeout?: number;
  maxRetries?: number;
  maxConcurrency?: number;
  tokenBudget?: number;
  budgetWindowMs?: number;
}

export function resolveConfig(config: LLMConfig): Required<LLMConfig> {
  return {
    provider: config.provider,
    baseUrl: config.baseUrl ?? "",
    apiKey: config.apiKey,
    model: config.model,
    temperature: config.temperature ?? 0.7,
    maxTokens: config.maxTokens ?? 2000,
    timeout: config.timeout ?? 30000,
    maxRetries: config.maxRetries ?? 3,
    maxConcurrency: config.maxConcurrency ?? 4,
    tokenBudget: config.tokenBudget ?? 0,
    budgetWindowMs: config.budgetWindowMs ?? 3600000,
  };
}
```

- [ ] **Step 4: Implement llm-provider.ts**

`packages/core/src/llm-provider.ts`:
```typescript
import { generateText } from "ai";
import { createOpenAI } from "@ai-sdk/openai";
import { createAnthropic } from "@ai-sdk/anthropic";
import { createGoogleGenerativeAI } from "@ai-sdk/google";
import { LLMConfig, resolveConfig } from "./llm-config";
import { TokenBudgetManager } from "./token-budget";
import { PerformanceMonitor } from "./performance-monitor";
import type { LlmMessage } from "./types";

export class LLMBillingError extends Error {
  constructor(message: string) {
    super(message);
    this.name = "LLMBillingError";
  }
}

export class LLMUnavailableError extends Error {
  constructor(message: string) {
    super(message);
    this.name = "LLMUnavailableError";
  }
}

export interface LlmResponse {
  content: string;
  usage?: {
    promptTokens?: number;
    completionTokens?: number;
    totalTokens?: number;
  };
}

export interface LlmJsonResponse<T = unknown> {
  parsed: T;
  raw: string;
  usage?: { totalTokens?: number };
}

type CallOverride = () => Promise<LlmResponse>;

export interface ILLMProvider {
  chatCompletion(messages: LlmMessage[]): Promise<LlmResponse>;
  chatCompletionJson<T = unknown>(messages: LlmMessage[]): Promise<LlmJsonResponse<T>>;
  preWarm(): Promise<void>;
}

export class VercelAIProvider implements ILLMProvider {
  private readonly config: Required<LLMConfig>;
  private readonly budget: TokenBudgetManager;
  private readonly perf: PerformanceMonitor;
  private activeCalls = 0;
  private callQueue: Array<() => void> = [];
  private callOverride: CallOverride | null = null;

  constructor(config: LLMConfig, perf?: PerformanceMonitor) {
    this.config = resolveConfig(config);
    this.budget = new TokenBudgetManager({
      budget: this.config.tokenBudget,
      windowMs: this.config.budgetWindowMs,
    });
    this.perf = perf ?? new PerformanceMonitor();
  }

  _setCallOverride(fn: CallOverride): void {
    this.callOverride = fn;
  }

  _stripThinking(text: string): string {
    text = text.replace(/<think>[\s\S]*?<\/think>/g, "");
    text = text.replace(/<\|channel\|>[\s\S]*?<\|end\|>/g, "");
    const answerMatch = text.match(/<answer>([\s\S]*?)<\/answer>/);
    if (answerMatch) {
      return answerMatch[1]!.trim();
    }
    return text.trim();
  }

  async chatCompletion(messages: LlmMessage[]): Promise<LlmResponse> {
    return this.withRetry(async () => {
      await this.acquireSlot();
      try {
        return await this.perf.measure("llm_call", () => this.doCall(messages));
      } finally {
        this.releaseSlot();
      }
    });
  }

  async chatCompletionJson<T = unknown>(messages: LlmMessage[]): Promise<LlmJsonResponse<T>> {
    const response = await this.chatCompletion(messages);
    const stripped = this._stripThinking(response.content);
    try {
      const parsed = JSON.parse(stripped) as T;
      return { parsed, raw: response.content, usage: response.usage };
    } catch {
      throw new Error(`LLM returned invalid JSON: ${stripped.slice(0, 200)}`);
    }
  }

  async preWarm(): Promise<void> {
    await Promise.resolve();
  }

  private async withRetry(fn: () => Promise<LlmResponse>): Promise<LlmResponse> {
    let lastError: Error | null = null;
    for (let attempt = 0; attempt < this.config.maxRetries; attempt++) {
      try {
        const result = await fn();
        if (result.usage?.totalTokens) {
          if (!this.budget.consume(result.usage.totalTokens)) {
            throw new Error(
              `Token budget exceeded: used ${this.budget.getUsed()}, budget ${this.config.tokenBudget}`
            );
          }
        }
        return result;
      } catch (err) {
        lastError = err instanceof Error ? err : new Error(String(err));
        if (lastError instanceof LLMBillingError) throw lastError;
        if (attempt === this.config.maxRetries - 1) break;
        const baseDelay = Math.min(1000 * 2 ** attempt, 30000);
        const jitter = Math.random() * 0.3 * baseDelay;
        await new Promise((r) => setTimeout(r, baseDelay + jitter));
      }
    }
    throw new LLMUnavailableError(
      `LLM unavailable after ${this.config.maxRetries} retries: ${lastError?.message}`
    );
  }

  private async doCall(messages: LlmMessage[]): Promise<LlmResponse> {
    if (this.callOverride) {
      return this.callOverride();
    }

    const model = this.createModel();
    const systemMessage = messages.find((m) => m.role === "system");
    const nonSystemMessages = messages.filter((m) => m.role !== "system");

    const result = await generateText({
      model,
      system: systemMessage?.content,
      messages: nonSystemMessages.map((m) => ({
        role: m.role,
        content: m.content,
      })),
      temperature: this.config.temperature,
      maxTokens: this.config.maxTokens,
      abortSignal: AbortSignal.timeout(this.config.timeout),
    });

    return {
      content: result.text,
      usage: {
        promptTokens: result.usage?.promptTokens,
        completionTokens: result.usage?.completionTokens,
        totalTokens: result.usage?.totalTokens,
      },
    };
  }

  private createModel(): unknown {
    switch (this.config.provider) {
      case "openai":
      case "deepseek":
      case "lmstudio":
      case "openrouter": {
        const openai = createOpenAI({
          apiKey: this.config.apiKey,
          baseURL: this.config.baseUrl || undefined,
        });
        return openai(this.config.model);
      }
      case "anthropic": {
        const anthropic = createAnthropic({
          apiKey: this.config.apiKey,
          baseURL: this.config.baseUrl || undefined,
        });
        return anthropic(this.config.model);
      }
      case "google":
      case "minimax": {
        const google = createGoogleGenerativeAI({
          apiKey: this.config.apiKey,
          baseURL: this.config.baseUrl || undefined,
        });
        return google(this.config.model);
      }
      default: {
        const openai = createOpenAI({ apiKey: this.config.apiKey });
        return openai(this.config.model);
      }
    }
  }

  private async acquireSlot(): Promise<void> {
    if (this.activeCalls < this.config.maxConcurrency) {
      this.activeCalls++;
      return;
    }
    await new Promise<void>((resolve) => {
      this.callQueue.push(() => {
        this.activeCalls++;
        resolve();
      });
    });
  }

  private releaseSlot(): void {
    this.activeCalls--;
    const next = this.callQueue.shift();
    if (next) next();
  }
}
```

- [ ] **Step 5: Run test, typecheck, commit**

Run: `cd D:\Source\ValleyAI && bun test packages/core/tests/llm-provider.test.ts`
Expected: 13 pass, 0 fail

Run: `cd D:\Source\ValleyAI && bun run typecheck`
Expected: 0 errors

```bash
cd D:\Source\ValleyAI
git add -A
git commit -m "feat(core): LLM provider with retry, concurrency, budget, think-stripping"
```

---

## Task 8: Transport Layer

**Files:**
- Create: `packages/core/src/transport.ts`
- Create: `packages/core/src/bun-transport.ts`
- Test: `packages/core/tests/transport.test.ts`

- [ ] **Step 1: Write failing test**

`packages/core/tests/transport.test.ts`:
```typescript
import { test, expect } from "bun:test";
import { BunWebSocketTransport } from "../src/bun-transport";

test("BunWebSocketTransport implements Transport interface", () => {
  const transport = new BunWebSocketTransport({ port: 18799 });
  expect(typeof transport.start).toBe("function");
  expect(typeof transport.stop).toBe("function");
  expect(typeof transport.broadcast).toBe("function");
  expect(typeof transport.sendTo).toBe("function");
  expect(typeof transport.onMessage).toBe("function");
  expect(typeof transport.onConnect).toBe("function");
  expect(typeof transport.onDisconnect).toBe("function");
});

test("start begins listening on specified port", async () => {
  const transport = new BunWebSocketTransport({ port: 18801 });
  await transport.start();
  expect(transport.isRunning()).toBe(true);
  await transport.stop();
  expect(transport.isRunning()).toBe(false);
});

test("stop is idempotent", async () => {
  const transport = new BunWebSocketTransport({ port: 18802 });
  await transport.start();
  await transport.stop();
  await transport.stop();
});

test("onConnect callback fires when client connects", async () => {
  const transport = new BunWebSocketTransport({ port: 18803 });
  let connected = false;
  transport.onConnect((conn) => {
    connected = true;
    expect(conn.id).toBeDefined();
  });
  await transport.start();

  const ws = new WebSocket("ws://localhost:18803");
  await new Promise((resolve) => { ws.onopen = () => resolve(null); });
  await new Promise((resolve) => setTimeout(resolve, 50));
  expect(connected).toBe(true);

  ws.close();
  await transport.stop();
});

test("onMessage callback fires when client sends message", async () => {
  const transport = new BunWebSocketTransport({ port: 18804 });
  let receivedMessage: string | null = null;

  transport.onMessage((conn, data) => {
    receivedMessage = typeof data === "string" ? data : data.toString();
  });
  await transport.start();

  const ws = new WebSocket("ws://localhost:18804");
  await new Promise((resolve) => { ws.onopen = () => resolve(null); });
  ws.send("hello world");
  await new Promise((resolve) => setTimeout(resolve, 50));
  expect(receivedMessage).toBe("hello world");

  ws.close();
  await transport.stop();
});

test("sendTo sends message to specific connection", async () => {
  const transport = new BunWebSocketTransport({ port: 18805 });
  let connId: string | null = null;
  transport.onConnect((conn) => { connId = conn.id; });
  await transport.start();

  const ws = new WebSocket("ws://localhost:18805");
  let received = "";
  await new Promise((resolve) => { ws.onopen = () => resolve(null); });
  ws.onmessage = (event) => { received = event.data as string; };
  await new Promise((resolve) => setTimeout(resolve, 50));
  transport.sendTo(connId!, "server says hi");
  await new Promise((resolve) => setTimeout(resolve, 50));
  expect(received).toBe("server says hi");

  ws.close();
  await transport.stop();
});

test("broadcast sends to all connections", async () => {
  const transport = new BunWebSocketTransport({ port: 18806 });
  await transport.start();

  const ws1 = new WebSocket("ws://localhost:18806");
  const ws2 = new WebSocket("ws://localhost:18806");
  let received1 = "";
  let received2 = "";

  await Promise.all([
    new Promise((r) => { ws1.onopen = () => r(null); }),
    new Promise((r) => { ws2.onopen = () => r(null); }),
  ]);
  ws1.onmessage = (e) => (received1 = e.data as string);
  ws2.onmessage = (e) => (received2 = e.data as string);
  await new Promise((resolve) => setTimeout(resolve, 50));
  transport.broadcast("broadcast message");
  await new Promise((resolve) => setTimeout(resolve, 50));

  expect(received1).toBe("broadcast message");
  expect(received2).toBe("broadcast message");

  ws1.close();
  ws2.close();
  await transport.stop();
});

test("onDisconnect fires when client disconnects", async () => {
  const transport = new BunWebSocketTransport({ port: 18807 });
  let disconnected = false;
  transport.onDisconnect((conn) => {
    disconnected = true;
    expect(conn.id).toBeDefined();
  });
  await transport.start();

  const ws = new WebSocket("ws://localhost:18807");
  await new Promise((resolve) => { ws.onopen = () => resolve(null); });
  ws.close();
  await new Promise((resolve) => setTimeout(resolve, 100));
  expect(disconnected).toBe(true);

  await transport.stop();
});

test("getConnectionCount returns active connections", async () => {
  const transport = new BunWebSocketTransport({ port: 18808 });
  await transport.start();

  const ws1 = new WebSocket("ws://localhost:18808");
  const ws2 = new WebSocket("ws://localhost:18808");
  await Promise.all([
    new Promise((r) => { ws1.onopen = () => r(null); }),
    new Promise((r) => { ws2.onopen = () => r(null); }),
  ]);
  await new Promise((resolve) => setTimeout(resolve, 50));
  expect(transport.getConnectionCount()).toBe(2);

  ws1.close();
  ws2.close();
  await new Promise((resolve) => setTimeout(resolve, 100));
  expect(transport.getConnectionCount()).toBe(0);

  await transport.stop();
});
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd D:\Source\ValleyAI && bun test packages/core/tests/transport.test.ts`
Expected: FAIL — cannot find module

- [ ] **Step 3: Implement transport.ts**

`packages/core/src/transport.ts`:
```typescript
export interface Connection {
  id: string;
  metadata: Record<string, unknown>;
}

export type MessageHandler = (conn: Connection, data: string | Buffer) => void;
export type ConnectHandler = (conn: Connection) => void;
export type DisconnectHandler = (conn: Connection) => void;

export interface Transport {
  start(): Promise<void>;
  stop(): Promise<void>;
  broadcast(message: string): void;
  sendTo(connId: string, message: string): void;
  onMessage(handler: MessageHandler): void;
  onConnect(handler: ConnectHandler): void;
  onDisconnect(handler: DisconnectHandler): void;
}

export interface TransportConfig {
  port: number;
  hostname?: string;
  idleTimeoutMs?: number;
  backpressureLimit?: number;
}
```

- [ ] **Step 4: Implement bun-transport.ts**

`packages/core/src/bun-transport.ts`:
```typescript
import type {
  Transport,
  Connection,
  MessageHandler,
  ConnectHandler,
  DisconnectHandler,
  TransportConfig,
} from "./transport";

interface BunWebSocketData {
  connId: string;
  metadata: Record<string, unknown>;
}

export class BunWebSocketTransport implements Transport {
  private server: ReturnType<typeof Bun.serve> | null = null;
  private connections: Map<string, { ws: WebSocket; conn: Connection }> = new Map();
  private messageHandler: MessageHandler | null = null;
  private connectHandler: ConnectHandler | null = null;
  private disconnectHandler: DisconnectHandler | null = null;
  private connCounter = 0;
  private readonly config: Required<TransportConfig>;

  constructor(config: TransportConfig) {
    this.config = {
      port: config.port,
      hostname: config.hostname ?? "127.0.0.1",
      idleTimeoutMs: config.idleTimeoutMs ?? 120000,
      backpressureLimit: config.backpressureLimit ?? 1024 * 1024,
    };
  }

  async start(): Promise<void> {
    if (this.server) return;

    this.server = Bun.serve<BunWebSocketData>({
      port: this.config.port,
      hostname: this.config.hostname,
      websocket: {
        idleTimeout: this.config.idleTimeoutMs / 1000,
        backpressureLimit: this.config.backpressureLimit,
        open: (ws) => {
          const connId = ws.data.connId;
          const conn: Connection = { id: connId, metadata: ws.data.metadata };
          this.connections.set(connId, { ws, conn });
          this.connectHandler?.(conn);
        },
        message: (ws, message) => {
          const connId = ws.data.connId;
          const entry = this.connections.get(connId);
          if (entry) this.messageHandler?.(entry.conn, message);
        },
        close: (ws) => {
          const connId = ws.data.connId;
          const entry = this.connections.get(connId);
          if (entry) {
            this.connections.delete(connId);
            this.disconnectHandler?.(entry.conn);
          }
        },
      },
      fetch: (req, server) => {
        if (req.headers.get("upgrade") === "websocket") {
          const connId = `conn_${++this.connCounter}`;
          if (server.upgrade(req, { data: { connId, metadata: {} } })) {
            return;
          }
        }
        return new Response("Not Found", { status: 404 });
      },
    });
  }

  async stop(): Promise<void> {
    if (!this.server) return;
    for (const [, { ws }] of this.connections) {
      ws.close();
    }
    this.connections.clear();
    this.server.stop(true);
    this.server = null;
  }

  broadcast(message: string): void {
    for (const [, { ws }] of this.connections) {
      ws.send(message);
    }
  }

  sendTo(connId: string, message: string): void {
    const entry = this.connections.get(connId);
    if (entry) entry.ws.send(message);
  }

  onMessage(handler: MessageHandler): void { this.messageHandler = handler; }
  onConnect(handler: ConnectHandler): void { this.connectHandler = handler; }
  onDisconnect(handler: DisconnectHandler): void { this.disconnectHandler = handler; }

  isRunning(): boolean { return this.server !== null; }
  getConnectionCount(): number { return this.connections.size; }
  getConnections(): Connection[] {
    return [...this.connections.values()].map((e) => e.conn);
  }
}
```

- [ ] **Step 5: Run test, typecheck, commit**

Run: `cd D:\Source\ValleyAI && bun test packages/core/tests/transport.test.ts`
Expected: 8 pass, 0 fail

Run: `cd D:\Source\ValleyAI && bun run typecheck`
Expected: 0 errors

```bash
cd D:\Source\ValleyAI
git add -A
git commit -m "feat(core): WebSocket transport with Bun native implementation"
```

---

## Task 9: Agent Loop

**Files:**
- Create: `packages/core/src/agent-loop.ts`
- Test: `packages/core/tests/agent-loop.test.ts`

- [ ] **Step 1: Write failing test**

`packages/core/tests/agent-loop.test.ts`:
```typescript
import { test, expect } from "bun:test";
import { agentLoop } from "../src/agent-loop";
import type { AgentLoopConfig } from "../src/agent-loop";
import type { AgentContext, AgentMessage } from "../src/types";
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
        ...ctx.messages.map((m) => ({ role: m.role, content: m.content })),
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
  const stream = agentLoop({
    context: makeContext([{ role: "user", content: "hi" }]),
    config: makeLoopConfig({
      shouldStopAfterTurn: () => false,
      llmCall: async () => {
        controller.abort();
        return { content: "aborted", usage: { totalTokens: 1 } };
      },
    }),
    signal: controller.signal,
  });

  const events = await stream.awaitAll();
  expect(events.length).toBeGreaterThan(0);
  expect(events.some((e) => e.type === "agent_end" || e.type === "error")).toBe(true);
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
            ...ctx.messages.map((m) => ({ role: m.role, content: m.content })),
          ],
        };
      },
    }),
    signal: new AbortController().signal,
  }).awaitAll();

  expect(capturedSystemPrompt).toContain("You are happy.");
});
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd D:\Source\ValleyAI && bun test packages/core/tests/agent-loop.test.ts`
Expected: FAIL — cannot find module

- [ ] **Step 3: Implement agent-loop.ts**

`packages/core/src/agent-loop.ts`:
```typescript
import { EventStream } from "./event-stream";
import type { AgentContext, AgentMessage, AgentEvent, LlmMessage, AgentToolCall } from "./types";
import type { ToolRegistry } from "./tool-registry";
import type { ToolResult } from "./tool";
import type { LlmResponse } from "./llm-provider";

export interface LlmCallResult extends LlmResponse {
  toolCalls?: AgentToolCall[];
}

export interface AgentLoopContext {
  context: AgentContext;
  config: AgentLoopConfig;
  signal: AbortSignal;
}

export interface BeforeToolCallResult {
  allow: boolean;
  reason?: string;
  overrideArgs?: Record<string, unknown>;
}

export interface AfterToolCallResult {
  result: ToolResult;
  skipFollowUp?: boolean;
}

export interface AgentLoopConfig {
  tools: ToolRegistry;
  convertToLlm: (ctx: AgentContext) => { messages: LlmMessage[] };
  llmCall: (messages: LlmMessage[]) => Promise<LlmCallResult>;
  transformContext?: (ctx: AgentContext) => AgentContext;
  beforeToolCall?: (
    name: string,
    args: Record<string, unknown>,
    ctx: AgentContext
  ) => Promise<BeforeToolCallResult>;
  afterToolCall?: (
    name: string,
    args: Record<string, unknown>,
    result: ToolResult,
    ctx: AgentContext
  ) => Promise<AfterToolCallResult>;
  shouldStopAfterTurn?: (ctx: AgentContext, turnIndex: number) => boolean;
  prepareNextTurn?: (ctx: AgentContext, turnIndex: number) => AgentContext;
  toolExecution?: "sequential" | "parallel";
  maxTurns?: number;
}

export function agentLoop(params: AgentLoopContext): EventStream<AgentEvent> {
  const stream = new EventStream<AgentEvent>();
  const { context, config, signal } = params;
  const maxTurns = config.maxTurns ?? 10;
  const execMode = config.toolExecution ?? "sequential";

  (async () => {
    stream.emit({ type: "agent_start", timestamp: Date.now() });

    let currentContext = context;
    let aborted = false;

    signal.addEventListener("abort", () => { aborted = true; });

    try {
      for (let turn = 0; turn < maxTurns && !aborted; turn++) {
        stream.emit({ type: "turn_start", timestamp: Date.now(), turnIndex: turn });

        if (config.transformContext) {
          currentContext = config.transformContext(currentContext);
        }

        const { messages } = config.convertToLlm(currentContext);

        let llmResult: LlmCallResult;
        try {
          llmResult = await config.llmCall(messages);
        } catch (err) {
          const message = err instanceof Error ? err.message : String(err);
          stream.emit({ type: "error", timestamp: Date.now(), message });
          break;
        }

        if (aborted) break;

        stream.emit({ type: "message_start", timestamp: Date.now() });
        stream.emit({ type: "message_update", timestamp: Date.now(), delta: llmResult.content });
        stream.emit({ type: "message_end", timestamp: Date.now(), content: llmResult.content });

        const assistantMsg: AgentMessage = {
          role: "assistant",
          content: llmResult.content,
          toolCalls: llmResult.toolCalls,
        };
        currentContext = {
          ...currentContext,
          messages: [...currentContext.messages, assistantMsg],
        };

        if (llmResult.toolCalls && llmResult.toolCalls.length > 0) {
          if (execMode === "parallel") {
            await Promise.all(
              llmResult.toolCalls.map((tc) => processToolCall(stream, config, currentContext, tc))
            );
          } else {
            for (const tc of llmResult.toolCalls) {
              if (aborted) break;
              await processToolCall(stream, config, currentContext, tc);
            }
          }
        }

        stream.emit({ type: "turn_end", timestamp: Date.now(), turnIndex: turn });

        if (config.shouldStopAfterTurn?.(currentContext, turn)) break;

        if (config.prepareNextTurn) {
          currentContext = config.prepareNextTurn(currentContext, turn + 1);
        }
      }
    } catch (err) {
      const message = err instanceof Error ? err.message : String(err);
      stream.emit({ type: "error", timestamp: Date.now(), message });
    }

    stream.emit({ type: "agent_end", timestamp: Date.now() });
    stream.done();
  })();

  return stream;
}

async function processToolCall(
  stream: EventStream<AgentEvent>,
  config: AgentLoopConfig,
  ctx: AgentContext,
  toolCall: AgentToolCall
): Promise<void> {
  stream.emit({
    type: "tool_call_start",
    timestamp: Date.now(),
    toolName: toolCall.name,
    toolCallId: toolCall.id,
    args: toolCall.args,
  });

  let result: ToolResult;

  if (config.beforeToolCall) {
    const gateResult = await config.beforeToolCall(toolCall.name, toolCall.args, ctx);
    if (!gateResult.allow) {
      result = { content: gateResult.reason ?? "Tool call blocked", isError: true };
      stream.emit({
        type: "tool_call_end",
        timestamp: Date.now(),
        toolName: toolCall.name,
        toolCallId: toolCall.id,
        result,
        isError: true,
      });
      return;
    }
    if (gateResult.overrideArgs) {
      toolCall = { ...toolCall, args: gateResult.overrideArgs };
    }
  }

  result = await config.tools.execute(toolCall.name, toolCall.args);

  if (config.afterToolCall) {
    const hookResult = await config.afterToolCall(toolCall.name, toolCall.args, result, ctx);
    result = hookResult.result;
  }

  stream.emit({
    type: "tool_call_end",
    timestamp: Date.now(),
    toolName: toolCall.name,
    toolCallId: toolCall.id,
    result,
    isError: result.isError ?? false,
  });
}
```

- [ ] **Step 4: Run test, typecheck, commit**

Run: `cd D:\Source\ValleyAI && bun test packages/core/tests/agent-loop.test.ts`
Expected: 11 pass, 0 fail

Run: `cd D:\Source\ValleyAI && bun run typecheck`
Expected: 0 errors

```bash
cd D:\Source\ValleyAI
git add -A
git commit -m "feat(core): agentLoop pure function with 7 hooks and dual exec mode"
```

---

## Task 10: Agent (Stateful Wrapper)

**Files:**
- Create: `packages/core/src/agent.ts`
- Test: `packages/core/tests/agent.test.ts`

- [ ] **Step 1: Write failing test**

`packages/core/tests/agent.test.ts`:
```typescript
import { test, expect } from "bun:test";
import { Agent } from "../src/agent";
import type { AgentContext, AgentMessage } from "../src/types";
import type { AgentLoopConfig } from "../src/agent-loop";
import { ToolRegistry } from "../src/tool-registry";

function makeLoopConfig(overrides: Partial<AgentLoopConfig> = {}): AgentLoopConfig {
  return {
    tools: new ToolRegistry(),
    convertToLlm: (ctx) => ({
      messages: [
        { role: "system", content: ctx.systemPrompt },
        ...ctx.messages.map((m) => ({ role: m.role, content: m.content })),
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
  const agent = new Agent("test", makeLoopConfig({
    shouldStopAfterTurn: (_ctx, turn) => turn >= 0,
  }));

  const stream1 = agent.prompt(makeContext([{ role: "user", content: "first" }]));
  agent.followUp({ role: "user", content: "second" });
  await stream1.awaitAll();
  await agent.waitForIdle();
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
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd D:\Source\ValleyAI && bun test packages/core/tests/agent.test.ts`
Expected: FAIL — cannot find module

- [ ] **Step 3: Implement agent.ts**

`packages/core/src/agent.ts`:
```typescript
import { EventStream } from "./event-stream";
import { agentLoop } from "./agent-loop";
import type { AgentLoopConfig } from "./agent-loop";
import type { AgentContext, AgentMessage, AgentEvent } from "./types";

export type DrainMode = "one_at_a_time" | "all";

export interface AgentState {
  isStreaming: boolean;
  streamingMessage: string | null;
  pendingToolCalls: number;
  errorMessage: string | null;
}

interface ActiveRun {
  stream: EventStream<AgentEvent>;
  abortController: AbortController;
}

export class Agent {
  readonly name: string;
  private readonly config: AgentLoopConfig;
  private activeRun: ActiveRun | null = null;
  private steeringQueue: AgentMessage[] = [];
  private followUpQueue: Array<{ message: AgentMessage; mode: DrainMode }> = [];
  private subscribers: Set<(event: AgentEvent) => void> = new Set();
  private state: AgentState = {
    isStreaming: false,
    streamingMessage: null,
    pendingToolCalls: 0,
    errorMessage: null,
  };

  constructor(name: string, config: AgentLoopConfig) {
    this.name = name;
    this.config = config;
  }

  isIdle(): boolean {
    return this.activeRun === null;
  }

  getState(): AgentState {
    return { ...this.state };
  }

  prompt(context: AgentContext): EventStream<AgentEvent> {
    if (this.activeRun) {
      throw new Error(`Agent ${this.name} is already running`);
    }

    const abortController = new AbortController();
    const wrappedConfig: AgentLoopConfig = {
      ...this.config,
      transformContext: (ctx) => {
        let result = ctx;
        if (this.steeringQueue.length > 0) {
          const steered = [...this.steeringQueue];
          this.steeringQueue = [];
          result = { ...result, messages: [...result.messages, ...steered] };
        }
        if (this.config.transformContext) {
          result = this.config.transformContext(result);
        }
        return result;
      },
      shouldStopAfterTurn: (ctx, turn) => {
        if (this.steeringQueue.length > 0) return false;
        return this.config.shouldStopAfterTurn?.(ctx, turn) ?? (turn >= 0);
      },
    };

    const stream = agentLoop({ context, config: wrappedConfig, signal: abortController.signal });

    stream.subscribe((event) => {
      this.updateState(event);
      for (const sub of this.subscribers) sub(event);
    });

    this.activeRun = { stream, abortController };
    this.state = { ...this.state, isStreaming: true, errorMessage: null };

    const proxyStream = new EventStream<AgentEvent>();
    stream.subscribe((event) => {
      if (!proxyStream.isDone()) proxyStream.emit(event);
    });

    stream.awaitAll().then(() => {
      if (!proxyStream.isDone()) proxyStream.done();
      this.activeRun = null;
      this.state = { ...this.state, isStreaming: false, streamingMessage: null };
      this.drainFollowUps();
    });

    return proxyStream;
  }

  steer(message: AgentMessage): void {
    this.steeringQueue.push(message);
  }

  followUp(message: AgentMessage, mode: DrainMode = "all"): void {
    this.followUpQueue.push({ message, mode });
    if (this.isIdle()) this.drainFollowUps();
  }

  abort(): void {
    if (this.activeRun) this.activeRun.abortController.abort();
    this.steeringQueue = [];
    this.followUpQueue = [];
  }

  reset(): void {
    this.abort();
    this.state = {
      isStreaming: false,
      streamingMessage: null,
      pendingToolCalls: 0,
      errorMessage: null,
    };
  }

  subscribe(fn: (event: AgentEvent) => void): () => void {
    this.subscribers.add(fn);
    return () => { this.subscribers.delete(fn); };
  }

  async waitForIdle(): Promise<void> {
    if (!this.activeRun) return;
    await this.activeRun.stream.awaitAll();
    while (this.activeRun) {
      await this.activeRun.stream.awaitAll();
    }
  }

  private drainFollowUps(): void {
    if (this.followUpQueue.length === 0 || !this.isIdle()) return;

    const followUp = this.followUpQueue.shift()!;
    const context: AgentContext = {
      messages: [followUp.message],
      systemPrompt: "",
      metadata: {},
    };

    if (followUp.mode === "all") {
      while (this.followUpQueue.length > 0) {
        const next = this.followUpQueue.shift()!;
        context.messages.push(next.message);
      }
    }
    this.prompt(context);
  }

  private updateState(event: AgentEvent): void {
    switch (event.type) {
      case "message_start":
        this.state.streamingMessage = "";
        break;
      case "message_update":
        this.state.streamingMessage = (this.state.streamingMessage ?? "") + event.delta;
        break;
      case "message_end":
        this.state.streamingMessage = event.content;
        break;
      case "tool_call_start":
        this.state.pendingToolCalls++;
        break;
      case "tool_call_end":
        this.state.pendingToolCalls = Math.max(0, this.state.pendingToolCalls - 1);
        break;
      case "error":
        this.state.errorMessage = event.message;
        break;
    }
  }
}
```

- [ ] **Step 4: Run test, typecheck, commit**

Run: `cd D:\Source\ValleyAI && bun test packages/core/tests/agent.test.ts`
Expected: 9 pass, 0 fail

Run: `cd D:\Source\ValleyAI && bun run typecheck`
Expected: 0 errors

```bash
cd D:\Source\ValleyAI
git add -A
git commit -m "feat(core): Agent stateful wrapper with dual queues and lifecycle"
```

---

<!-- PART 3: Tasks 11-16 will be appended below -->
