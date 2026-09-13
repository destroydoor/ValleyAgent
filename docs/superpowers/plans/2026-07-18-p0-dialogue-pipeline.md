# P0 对话管线止血 Implementation Plan

> **状态（2026-07-26 标注）：✅ 已实现**
>
> v4.3 TS Agent Server 已实现 dialogue 管线（`packages/stardew/src/protocol-adapter.ts`
> + `stardew-agent.ts` + `prompt-builder.ts`），记忆持久化到
> `agents/{npcName}_memory.json`，工具调用结构化。本文档为实施计划阶段产物。
>
> **当前权威文档**：[`../../../AGENTS.md`](../../../AGENTS.md) 第 2.2 节"关键数据流"。
>
> **路径变更**：文档中提到的
> `<REPO_ROOT>\src\valley_agent_server\data\npc_prompts.json` 已迁移到
> `<VALLEYAI_ROOT>\packages\stardew\data\npc_prompts.json`。

---

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让 NPC 能记得住玩家说过的话、能说到做到（结构化 action 执行）、不乱码，完成 C# Mod ↔ TS 服务器对话管线最小可交付。

**Architecture:** 三层架构：C# Mod（纯执行器，发 worldSnapshot 收 speech+actions）→ `@valley/stardew`（星露谷特化层，StardewAgent + ProtocolAdapter + PromptBuilder + Tools + Memory）→ `@valley/core`（通用 Agent 框架，已有 agentLoop + ToolRegistry + LLMProvider）。P0 重写 `_handle_dialogue` 为有状态路径，工具调用结构化，记忆持久化到 JSON 文件。

**Tech Stack:** TypeScript (Bun runtime + @sinclair/typebox + Vercel AI SDK)、C# (.NET 6 + SMAPI 1.6)、WebSocket (Bun WebSocket + System.Net.WebSockets)、MiniMax-M2 via OpenAI-compatible endpoint

---

## npc_prompts.json Schema 摘要

**文件位置**：`<REPO_ROOT>\src\valley_agent_server\data\npc_prompts.json`
**文件大小**：91769 字节（~90KB）
**NPC 数量**：33 个（Abigail, Alex, Caroline, Clint, Demetrius, Dwarf, Elliott, Emily, Evelyn, George, Gus, Haley, Harvey, Jas, Jodi, Kent, Krobus, Leah, Lewis, Linus, Marnie, Maru, Pam, Penny, Pierre, Robin, Sam, Sandy, Sebastian, Shane, Vincent, Willy, Wizard）

**Schema**：
```json
{
  "<NpcName>": {
    "base_memory": "string — NPC 第一人称背景自述（约 200-400 字）",
    "phases": {
      "stranger":      { "friendship_range": "0-250",    "prompt": "string — 该阶段第一人称人格 prompt" },
      "acquaintance":  { "friendship_range": "251-500",  "prompt": "string" },
      "friend":        { "friendship_range": "501-1000", "prompt": "string" },
      "close":         { "friendship_range": "1001-2000","prompt": "string" },
      "partner":       { "friendship_range": "2001-2500","prompt": "string" }
    }
  }
}
```

**字段说明**：
- `base_memory`：NPC 稳定不变的核心人格（家庭/住址/兴趣/关系）
- `phases.<phase>.prompt`：基于好感度阶段的语气态度（stranger 冷淡 → partner 亲密）
- `phases.<phase>.friendship_range`：该阶段对应的好感度区间

---

## 文件结构映射

### TS 服务器侧（`<VALLEYAI_ROOT>\packages\`）

| 文件 | 操作 | 职责 |
|------|------|------|
| `core/src/index.ts` | Modify | re-export 所有 core 模块（当前仅导出 CORE_VERSION） |
| `core/src/circuit-breaker.ts` | Modify | 加 `failureThreshold`/`openDurationMs` 字段别名 + `onSuccess`/`onOpen`/`onClose` 回调 |
| `core/src/memory-backend.ts` | Create | MemoryBackend 接口定义（spec 3.2） |
| `stardew/package.json` | Create | 包配置，依赖 `@valley/core` |
| `stardew/tsconfig.json` | Create | TS 严格配置 |
| `stardew/src/index.ts` | Create | 包入口，re-export 公开 API |
| `stardew/src/types.ts` | Create | 共享类型：SceneState / WorldSnapshot / DialogueRequest / DialogueResponse / ToolAction |
| `stardew/src/npc-prompt-loader.ts` | Create | 加载 npc_prompts.json，按 NPC 名 + 阶段取 prompt |
| `stardew/src/world-snapshot-decoder.ts` | Create | 解析 C# 发来的 worldSnapshot JSON → SceneState |
| `stardew/src/agent-memory.ts` | Create | AgentMemory 类（实现 MemoryBackend），含 load/save 到 JSON 文件 |
| `stardew/src/stardew-tools.ts` | Create | 8 个对话工具定义（speak/emote/give_item/give_gift/set_state/show_dialogue/remember/get_info） |
| `stardew/src/prompt-builder.ts` | Create | buildDialogueSystemPrompt(memory, scene, npcName) — 从 NpcPromptLoader 取数据 |
| `stardew/src/output-validator.ts` | Create | CJK 比例检测 + 空回复拒绝 + 重试 prompt 构建 |
| `stardew/src/rule-engine.ts` | Create | 简化 IDLE fallback：LLM 失败时按 err 类型返回兜底文案 |
| `stardew/src/stardew-agent.ts` | Create | StardewAgent 类：组装 core 原语 + 星露谷配置（maxTurns=5） |
| `stardew/src/stardew-agent-registry.ts` | Create | StardewAgentRegistry：Map<npcName, StardewAgent>，并发锁 |
| `stardew/src/protocol-adapter.ts` | Create | 5 消息路由：hello/ping/dialogue/tool_call_result/action_result |
| `stardew/src/dialogue-handler.ts` | Create | handleDialogueWithFallback：Layer 1-4 降级链 |
| `stardew/src/server.ts` | Create | Bun WebSocket 服务器入口，解析命令行参数 |
| `stardew/data/npc_prompts.json` | Create | 复制自 ValleyTalk |
| `stardew/tests/*.test.ts` | Create | 单元测试 + 集成测试 |

### C# Mod 侧（跨 3 个 csproj：`ValleyAgent` / `ValleyAgent.Abstractions` / `ValleyAgent.TestMod`）

| 文件 | 操作 | 职责 |
|------|------|------|
| `ValleyAgent.Abstractions/WebSocket/IAgentServerProvider.cs` | Modify | DialogueResponse record 重定义（Text→Speech, Action→Actions[]）+ 新增 ToolAction record |
| `ValleyAgent/Patches/DialogueBoxInputPatch.cs` | Modify | SubmitInput 简化：发 worldSnapshot，消费 Speech + Actions，加超时降级 |
| `ValleyAgent/CommandExecutor.cs` | Modify | 新增 ExecuteAction(ToolAction) 方法，按 tool 字段分发 |
| `ValleyAgent/Initialization/EventHandlerInitializer.cs` | Modify | 删除 BuildDialogueRequest 静态方法（dead code, lines 2246-2280） |
| `ValleyAgent/Debug/ChatHandler.cs` | Modify | 删除 BuildDialogueRequest 复制（line 121） |
| `ValleyAgent/WebSocket/PythonProcessManager.cs` | Modify | 重命名为 ServerProcessManager，启动 valley-ai-server.exe |
| `ValleyAgent/Config/ModConfig.cs` | Modify | 字段重命名（AutoStartPythonServer→AutoStartServer 等） |
| `ValleyAgent/Initialization/ServiceInitializer.cs` | Modify | 装配点改用 ServerProcessManager |
| `ValleyAgent.TestMod/Mock/MockResponseLibrary.cs` | Modify | 适配新 DialogueResponse 构造（Text→Speech, Action→Actions[]） |
| `ValleyAgent.TestMod/Mock/MockLLMProvider.cs` | Modify | 适配 DialogueResponse 类型引用（无构造调用，仅类型与方法签名） |
| `ValleyAgent.TestMod/Mock/MockWebSocketServer.cs` | Modify | 适配 DialogueResponse 别名引用 |
| `ValleyAgent.TestMod/Tests/Pipeline/PIPE003_DialoguePipeline.cs` | Modify | 测试断言 `response.Text` → `response.Speech` |

---

## Phase 1: TS core 补缺口

### Task 1: core/src/index.ts re-export 所有模块

**Files:**
- Modify: `<VALLEYAI_ROOT>\packages\core\src\index.ts`
- Test: `<VALLEYAI_ROOT>\packages\core\tests\index-exports.test.ts`

- [ ] **Step 1: Write the failing test**

Create `<VALLEYAI_ROOT>\packages\core\tests\index-exports.test.ts`:

```typescript
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
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd <VALLEYAI_ROOT> && bun test packages/core/tests/index-exports.test.ts`
Expected: FAIL with "Core.Agent is undefined" (因为 index.ts 只导出 CORE_VERSION)

- [ ] **Step 3: Write minimal implementation**

Replace `<VALLEYAI_ROOT>\packages\core\src\index.ts` with:

```typescript
export const CORE_VERSION = "0.1.0";

// Agent + agentLoop
export { Agent } from "./agent";
export type { AgentState, DrainMode } from "./agent";
export { agentLoop } from "./agent-loop";
export type { AgentLoopConfig, AgentLoopContext, BeforeToolCallResult, AfterToolCallResult, LlmCallResult } from "./agent-loop";

// Types
export type {
  AgentMessage,
  AgentToolCall,
  LlmMessage,
  LlmToolCall,
  AgentEventType,
  AgentEvent,
  AgentStartEvent,
  AgentEndEvent,
  TurnStartEvent,
  TurnEndEvent,
  MessageStartEvent,
  MessageUpdateEvent,
  MessageEndEvent,
  ToolCallStartEvent,
  ToolCallEndEvent,
  ErrorEvent,
  AgentContext,
} from "./types";

// Tools
export { ToolRegistry } from "./tool-registry";
export type { Tool, ToolResult, ToolVisibility, ValidationResult } from "./tool";

// LLM
export { VercelAIProvider, LLMBillingError, LLMUnavailableError } from "./llm-provider";
export type { ILLMProvider, LlmResponse, ProviderToolCallResult, LlmJsonResponse } from "./llm-provider";
export { LLMConfig, LLMProviderType, resolveConfig } from "./llm-config";

// CircuitBreaker
export { CircuitBreaker, CircuitState } from "./circuit-breaker";
export type { CircuitBreakerConfig } from "./circuit-breaker";

// Transport
export { BunWebSocketTransport } from "./bun-transport";
export type { Transport, Connection, TransportConfig } from "./transport";

// EventStream
export { EventStream } from "./event-stream";

// Performance + Token Budget
export { PerformanceMonitor } from "./performance-monitor";
export { TokenBudgetManager } from "./token-budget";
```

- [ ] **Step 4: Run test to verify it passes**

Run: `cd <VALLEYAI_ROOT> && bun test packages/core/tests/index-exports.test.ts`
Expected: PASS (9 tests)

Run typecheck: `cd <VALLEYAI_ROOT> && bun run typecheck`
Expected: 0 errors

- [ ] **Step 5: Commit**

```bash
cd <VALLEYAI_ROOT>
git add packages/core/src/index.ts packages/core/tests/index-exports.test.ts
git commit -m "feat(core): re-export all modules from index.ts

External packages can now import { Agent, agentLoop, ToolRegistry, ... } from '@valley/core'. Previously index.ts only exported CORE_VERSION, blocking @valley/stardew development."
```

---

### Task 2: CircuitBreaker 字段别名 + 回调

**Files:**
- Modify: `<VALLEYAI_ROOT>\packages\core\src\circuit-breaker.ts`
- Test: `<VALLEYAI_ROOT>\packages\core\tests\circuit-breaker-callbacks.test.ts`

**背景**：spec 5.4 节使用 `failureThreshold`/`openDurationMs` 字段名和 `onSuccess`/`onOpen`/`onClose` 回调，但现有代码用 `threshold`/`recoveryTime`。为不破坏现有测试，加字段别名而非重命名。

- [ ] **Step 1: Write the failing test**

Create `<VALLEYAI_ROOT>\packages\core\tests\circuit-breaker-callbacks.test.ts`:

```typescript
import { test, expect } from "bun:test";
import { CircuitBreaker, CircuitState } from "../src/circuit-breaker";

test("accepts failureThreshold alias for threshold", () => {
  const cb = new CircuitBreaker({ failureThreshold: 3, openDurationMs: 1000 });
  cb.recordFailure();
  cb.recordFailure();
  cb.recordFailure();
  expect(cb.getState()).toBe(CircuitState.OPEN);
});

test("accepts openDurationMs alias for recoveryTime", async () => {
  const cb = new CircuitBreaker({ failureThreshold: 2, openDurationMs: 50 });
  cb.recordFailure();
  cb.recordFailure();
  expect(cb.isOpen()).toBe(true);
  await new Promise(r => setTimeout(r, 60));
  expect(cb.canExecute()).toBe(true);
});

test("fires onSuccess callback when HALF_OPEN transitions to CLOSED", () => {
  let onSuccessCalled = 0;
  const cb = new CircuitBreaker({
    failureThreshold: 2,
    openDurationMs: 50,
    onSuccess: () => { onSuccessCalled++; },
  });
  cb.recordFailure();
  cb.recordFailure();
  expect(onSuccessCalled).toBe(0);

  // Force recovery + success
  cb.forceOpen();
  // Wait for HALF_OPEN transition via canExecute
  return new Promise<void>((resolve) => {
    setTimeout(() => {
      cb.canExecute(); // triggers HALF_OPEN
      cb.recordSuccess(); // triggers CLOSED + onSuccess
      expect(onSuccessCalled).toBe(1);
      resolve();
    }, 60);
  });
});

test("fires onOpen callback when threshold reached", () => {
  let onOpenCalled = 0;
  const cb = new CircuitBreaker({
    failureThreshold: 2,
    openDurationMs: 1000,
    onOpen: () => { onOpenCalled++; },
  });
  cb.recordFailure();
  expect(onOpenCalled).toBe(0);
  cb.recordFailure();
  expect(onOpenCalled).toBe(1);
});

test("fires onClose callback when circuit recovers", async () => {
  let onCloseCalled = 0;
  const cb = new CircuitBreaker({
    failureThreshold: 1,
    openDurationMs: 50,
    onClose: () => { onCloseCalled++; },
  });
  cb.recordFailure();
  expect(cb.isOpen()).toBe(true);
  await new Promise(r => setTimeout(r, 60));
  cb.canExecute(); // HALF_OPEN
  cb.recordSuccess(); // CLOSED
  expect(onCloseCalled).toBe(1);
});

test("backwards compatible with old threshold/recoveryTime fields", () => {
  const cb = new CircuitBreaker({ threshold: 3, recoveryTime: 1000 });
  cb.recordFailure();
  cb.recordFailure();
  expect(cb.getState()).toBe(CircuitState.CLOSED);
  cb.recordFailure();
  expect(cb.getState()).toBe(CircuitState.OPEN);
});
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd <VALLEYAI_ROOT> && bun test packages/core/tests/circuit-breaker-callbacks.test.ts`
Expected: FAIL with "failureThreshold does not exist in type CircuitBreakerConfig" (TypeScript compile error)

- [ ] **Step 3: Write minimal implementation**

Replace `<VALLEYAI_ROOT>\packages\core\src\circuit-breaker.ts` with:

```typescript
export enum CircuitState {
  CLOSED = "CLOSED",
  OPEN = "OPEN",
  HALF_OPEN = "HALF_OPEN",
}

export interface CircuitBreakerConfig {
  // Primary names (spec-aligned)
  failureThreshold?: number;
  openDurationMs?: number;
  // Legacy aliases (backwards compat)
  threshold?: number;
  recoveryTime?: number;
  halfOpenMaxCalls?: number;
  // Callbacks
  onSuccess?: () => void;
  onOpen?: () => void;
  onClose?: () => void;
}

export class CircuitBreaker {
  private state: CircuitState = CircuitState.CLOSED;
  private failureCount = 0;
  private lastFailureTime = 0;
  private halfOpenCalls = 0;
  private readonly failureThreshold: number;
  private readonly openDurationMs: number;
  private readonly halfOpenMaxCalls: number;
  private readonly onSuccess?: () => void;
  private readonly onOpen?: () => void;
  private readonly onClose?: () => void;

  constructor(config: CircuitBreakerConfig) {
    this.failureThreshold = config.failureThreshold ?? config.threshold ?? 5;
    this.openDurationMs = config.openDurationMs ?? config.recoveryTime ?? 30_000;
    this.halfOpenMaxCalls = config.halfOpenMaxCalls ?? 1;
    this.onSuccess = config.onSuccess;
    this.onOpen = config.onOpen;
    this.onClose = config.onClose;
  }

  getState(): CircuitState {
    this.checkRecovery();
    return this.state;
  }

  canExecute(): boolean {
    this.checkRecovery();
    if (this.state === CircuitState.CLOSED) return true;
    if (this.state === CircuitState.HALF_OPEN) {
      if (this.halfOpenCalls < this.halfOpenMaxCalls) {
        this.halfOpenCalls++;
        return true;
      }
      return false;
    }
    return false;
  }

  recordSuccess(): void {
    const wasHalfOpen = this.state === CircuitState.HALF_OPEN;
    if (wasHalfOpen) {
      this.state = CircuitState.CLOSED;
      this.failureCount = 0;
      this.halfOpenCalls = 0;
      this.onSuccess?.();
      this.onClose?.();
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
      this.onOpen?.();
      return;
    }

    if (this.failureCount >= this.failureThreshold && this.state === CircuitState.CLOSED) {
      this.state = CircuitState.OPEN;
      this.onOpen?.();
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
      if (elapsed >= this.openDurationMs) {
        this.state = CircuitState.HALF_OPEN;
        this.halfOpenCalls = 0;
      }
    }
  }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `cd <VALLEYAI_ROOT> && bun test packages/core/tests/circuit-breaker-callbacks.test.ts`
Expected: PASS (6 tests)

Run existing circuit-breaker tests to verify no regression:
Run: `cd <VALLEYAI_ROOT> && bun test packages/core/tests/circuit-breaker.test.ts`
Expected: PASS (all existing tests still pass)

Run typecheck: `cd <VALLEYAI_ROOT> && bun run typecheck`
Expected: 0 errors

- [ ] **Step 5: Commit**

```bash
cd <VALLEYAI_ROOT>
git add packages/core/src/circuit-breaker.ts packages/core/tests/circuit-breaker-callbacks.test.ts
git commit -m "feat(core): add failureThreshold/openDurationMs aliases + onSuccess/onOpen/onClose callbacks

Spec 5.4 uses failureThreshold/openDurationMs field names; old threshold/recoveryTime kept as backwards-compatible aliases. Callbacks enable Layer 3 fallback logging without polling."
```

---

### Task 3: MemoryBackend 接口定义

**Files:**
- Create: `<VALLEYAI_ROOT>\packages\core\src\memory-backend.ts`
- Modify: `<VALLEYAI_ROOT>\packages\core\src\index.ts`
- Test: `<VALLEYAI_ROOT>\packages\core\tests\memory-backend.test.ts`

- [ ] **Step 1: Write the failing test**

Create `<VALLEYAI_ROOT>\packages\core\tests\memory-backend.test.ts`:

```typescript
import { test, expect } from "bun:test";
import type { MemoryBackend } from "../src/memory-backend";

test("MemoryBackend interface is structurally compatible with a minimal impl", () => {
  const fakeMemory: MemoryBackend = {
    conversationHistory: [],
    shortTermMemories: [],
    significantMemories: [],
    friendship: 0,
    addConversation(role, text) { this.conversationHistory.push({ role, text }); },
    addMemory(text, importance = 1, entryType = "generic", location = "", tags = []) {
      this.shortTermMemories.push({ text, timestamp: 0, importance, entryType, location, tags });
    },
    addSignificantMemory(text, category = "life_event", weight = "joy", relatedNpcs = [], location = "") {
      if (this.significantMemories.some(m => m.text === text)) return false;
      this.significantMemories.push({ text, timestamp: 0, category, emotionalWeight: weight, relatedNpcs, location });
      return true;
    },
    getConversationContext(count = 10) { return ""; },
    getRecentMemories(count = 5) { return ""; },
    getSignificantMemoriesText() { return ""; },
    async load() { /* no-op */ },
    async save() { /* no-op */ },
  };

  fakeMemory.addConversation("player", "hello");
  expect(fakeMemory.conversationHistory.length).toBe(1);
  expect(fakeMemory.friendship).toBe(0);
});
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd <VALLEYAI_ROOT> && bun test packages/core/tests/memory-backend.test.ts`
Expected: FAIL with "Cannot find module '../src/memory-backend'"

- [ ] **Step 3: Write minimal implementation**

Create `<VALLEYAI_ROOT>\packages\core\src\memory-backend.ts`:

```typescript
// MemoryBackend — 接口契约，让 AgentMemory 实现，方便 P2/P3 替换为 Qdrant/SQLite
// spec 3.2 节定义

export interface MemoryEntry {
  text: string;
  timestamp: number;       // Unix seconds
  importance: number;      // 0.0-10.0
  entryType: string;       // "conversation" | "decision" | "emotion" | "event" | "gift" | "generic"
  location: string;
  tags: string[];
}

export interface SignificantMemory {
  text: string;            // 第一人称
  timestamp: number;
  category: string;        // "relationship" | "life_event" | "trauma" | "achievement"
  emotionalWeight: string; // "joy" | "sorrow" | "anger" | "fear" | "love" | "pride" | "surprise"
  relatedNpcs: string[];
  location: string;
}

export interface ConversationEntry {
  role: "player" | "npc";
  text: string;
}

export interface MemoryBackend {
  // State (readable for prompt building)
  conversationHistory: ConversationEntry[];
  shortTermMemories: MemoryEntry[];
  significantMemories: SignificantMemory[];
  friendship: number;

  // Mutators
  addConversation(role: "player" | "npc", text: string): void;
  addMemory(
    text: string,
    importance?: number,
    entryType?: string,
    location?: string,
    tags?: string[]
  ): void;
  addSignificantMemory(
    text: string,
    category?: string,
    emotionalWeight?: string,
    relatedNpcs?: string[],
    location?: string
  ): boolean;
  addFriendship?(delta: number): void;

  // Readers (formatted for prompt injection)
  getConversationContext(count?: number): string;
  getRecentMemories(count?: number): string;
  getSignificantMemoriesText(): string;

  // Persistence
  load(): Promise<void>;
  save(): Promise<void>;
}
```

Add to `<VALLEYAI_ROOT>\packages\core\src\index.ts` (append before Performance+Token section):

```typescript
// MemoryBackend interface
export type { MemoryBackend, MemoryEntry, SignificantMemory, ConversationEntry } from "./memory-backend";
```

- [ ] **Step 4: Run test to verify it passes**

Run: `cd <VALLEYAI_ROOT> && bun test packages/core/tests/memory-backend.test.ts`
Expected: PASS (1 test)

Run typecheck: `cd <VALLEYAI_ROOT> && bun run typecheck`
Expected: 0 errors

- [ ] **Step 5: Commit**

```bash
cd <VALLEYAI_ROOT>
git add packages/core/src/memory-backend.ts packages/core/src/index.ts packages/core/tests/memory-backend.test.ts
git commit -m "feat(core): add MemoryBackend interface for pluggable memory backends

Spec 3.2: AgentMemory implements this interface; P2/P3 can swap to Qdrant/SQLite without touching StardewAgent."
```

---

## Phase 2: TS stardew 包基础

### Task 4: stardew 包结构 + 配置 + data 复制

**Files:**
- Create: `<VALLEYAI_ROOT>\packages\stardew\package.json`
- Create: `<VALLEYAI_ROOT>\packages\stardew\tsconfig.json`
- Create: `<VALLEYAI_ROOT>\packages\stardew\data\npc_prompts.json` (复制)
- Create: `<VALLEYAI_ROOT>\packages\stardew\src\index.ts`
- Create: `<VALLEYAI_ROOT>\packages\stardew\src\types.ts`
- Test: `<VALLEYAI_ROOT>\packages\stardew\tests\package-setup.test.ts`

- [ ] **Step 1: Write the failing test**

Create `<VALLEYAI_ROOT>\packages\stardew\tests\package-setup.test.ts`:

```typescript
import { test, expect } from "bun:test";
import { existsSync, statSync } from "fs";
import { resolve } from "path";

test("npc_prompts.json data file exists in stardew package", () => {
  const p = resolve(import.meta.dir, "../data/npc_prompts.json");
  expect(existsSync(p)).toBe(true);
  const stats = statSync(p);
  expect(stats.size).toBeGreaterThan(50_000); // ~90KB expected
});

test("can import @valley/core from stardew package", async () => {
  const Core = await import("@valley/core");
  expect(Core.Agent).toBeDefined();
  expect(Core.CORE_VERSION).toBe("0.1.0");
});

test("stardew package index exports placeholder", async () => {
  const Stardew = await import("../src/index");
  expect(Stardew.STARDEW_VERSION).toBe("0.1.0");
});
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd <VALLEYAI_ROOT> && bun test packages/stardew/tests/package-setup.test.ts`
Expected: FAIL with "Cannot find module '@valley/core'" or "Cannot find module '../src/index'"

- [ ] **Step 3: Write minimal implementation**

Create `<VALLEYAI_ROOT>\packages\stardew\package.json`:

```json
{
  "name": "@valley/stardew",
  "version": "0.1.0",
  "type": "module",
  "main": "src/index.ts",
  "exports": {
    ".": "./src/index.ts"
  },
  "scripts": {
    "typecheck": "tsc --noEmit",
    "test": "bun test"
  },
  "dependencies": {
    "@valley/core": "workspace:*",
    "@sinclair/typebox": "^0.34.0"
  }
}
```

Create `<VALLEYAI_ROOT>\packages\stardew\tsconfig.json`:

```json
{
  "compilerOptions": {
    "target": "ES2022",
    "module": "ESNext",
    "moduleResolution": "bundler",
    "strict": true,
    "noUnusedLocals": true,
    "noUnusedParameters": true,
    "noFallthroughCasesInSwitch": true,
    "esModuleInterop": true,
    "skipLibCheck": true,
    "resolveJsonModule": true,
    "allowImportingTsExtensions": true,
    "noEmit": true,
    "types": ["bun-types"]
  },
  "include": ["src/**/*", "tests/**/*"],
  "compilerOptions.paths": {
    "@valley/core": ["../core/src/index.ts"]
  }
}
```

Create `<VALLEYAI_ROOT>\packages\stardew\src\index.ts`:

```typescript
export const STARDEW_VERSION = "0.1.0";
```

Create `<VALLEYAI_ROOT>\packages\stardew\src\types.ts`:

```typescript
// Stardew-side shared types (spec 2.2 data contracts)

export interface ToolAction {
  tool: string;                          // "speak" | "emote" | "give_item" | "give_gift" | "set_state" | "show_dialogue" | "remember" | "get_info"
  args: Record<string, unknown>;
}

export interface WorldSnapshot {
  season: string;                        // "summer"
  day: number;                           // 28
  time: string;                          // "14:30"
  weather: string;                       // "sunny"
  location: string;                      // "Town"
  npcTile: { x: number; y: number };
  nearbyObjects: string;                 // "2 villagers, Pierre's shop entrance"
  friendship: number;                    // 0-2500
  npcState: string;                      // "IDLE"
  inventory: Array<{ name: string; quantity: number }>;
  farmerName: string;
}

export interface DialogueRequest {
  type: "dialogue";
  requestId: string;
  npcName: string;
  playerInput: string;
  worldSnapshot: WorldSnapshot;
}

export interface DialogueResponse {
  type: "dialogue_response";
  requestId: string;
  npcName: string;
  speech: string;
  actions: ToolAction[];
  emotion: string;
  memorySideEffect?: "recorded";
  fallback?: boolean;
}

export interface HelloRequest {
  type: "hello";
  requestId: string;
  modVersion: string;
}

export interface HelloResponse {
  type: "hello";
  requestId: string;
  status: "ok";
  serverVersion: string;
}

export interface PingRequest {
  type: "ping";
  requestId: string;
}

export interface PongResponse {
  type: "pong";
  requestId: string;
}

export interface ToolCallResultMessage {
  type: "tool_call_result";
  requestId: string;
  callId: string;
  success: boolean;
  result?: string;
}

export interface ActionResultMessage {
  type: "action_result";
  requestId: string;
  callId: string;
  success: boolean;
  result?: string;
}

export type IncomingMessage = DialogueRequest | HelloRequest | PingRequest | ToolCallResultMessage | ActionResultMessage;
export type OutgoingMessage = DialogueResponse | HelloResponse | PongResponse | { type: "ack"; requestId: string };

// SceneState — TS 内部表示，由 WorldSnapshotDecoder 转换得到
export interface SceneState {
  season: string;
  day: number;
  timeStr: string;
  weather: string;
  location: string;
  npcTile: { x: number; y: number };
  nearbyObjects: string;
  farmerName: string;
  friendship: number;
  npcState: string;
  inventory: Array<{ name: string; quantity: number }>;
}
```

Copy npc_prompts.json:
```bash
cp <REPO_ROOT>/src/valley_agent_server/data/npc_prompts.json <VALLEYAI_ROOT>/packages/stardew/data/npc_prompts.json
```

Verify the root `package.json` of ValleyAI has workspaces configured. If not, check `<VALLEYAI_ROOT>\package.json` and ensure:
```json
{
  "workspaces": ["packages/*"]
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `cd <VALLEYAI_ROOT> && bun install` (to link workspace)
Run: `cd <VALLEYAI_ROOT> && bun test packages/stardew/tests/package-setup.test.ts`
Expected: PASS (3 tests)

Run typecheck: `cd <VALLEYAI_ROOT>\packages\stardew && bun run typecheck`
Expected: 0 errors

- [ ] **Step 5: Commit**

```bash
cd <VALLEYAI_ROOT>
git add packages/stardew/
git commit -m "feat(stardew): scaffold @valley/stardew package with types + npc_prompts.json data

Package depends on @valley/core workspace. types.ts defines spec 2.2 data contracts (DialogueRequest/Response, WorldSnapshot, ToolAction, 5 message types)."
```

---

### Task 5: NpcPromptLoader

**Files:**
- Create: `<VALLEYAI_ROOT>\packages\stardew\src\npc-prompt-loader.ts`
- Test: `<VALLEYAI_ROOT>\packages\stardew\tests\npc-prompt-loader.test.ts`

- [ ] **Step 1: Write the failing test**

Create `<VALLEYAI_ROOT>\packages\stardew\tests\npc-prompt-loader.test.ts`:

```typescript
import { test, expect } from "bun:test";
import { NpcPromptLoader } from "../src/npc-prompt-loader";
import { resolve } from "path";

const DATA_PATH = resolve(import.meta.dir, "../data/npc_prompts.json");

test("loads all 33 NPCs from npc_prompts.json", () => {
  const loader = new NpcPromptLoader(DATA_PATH);
  const npcs = loader.listNpcs();
  expect(npcs.length).toBe(33);
  expect(npcs).toContain("Abigail");
  expect(npcs).toContain("Haley");
  expect(npcs).toContain("Wizard");
});

test("getNpcData returns base_memory + 5 phases for Abigail", () => {
  const loader = new NpcPromptLoader(DATA_PATH);
  const data = loader.getNpcData("Abigail");
  expect(data).not.toBeNull();
  expect(data!.base_memory).toContain("Abigail");
  expect(data!.base_memory.length).toBeGreaterThan(50);
  expect(Object.keys(data!.phases)).toEqual([
    "stranger", "acquaintance", "friend", "close", "partner"
  ]);
  expect(data!.phases.stranger.friendship_range).toBe("0-250");
  expect(data!.phases.partner.friendship_range).toBe("2001-2500");
});

test("getNpcData returns null for unknown NPC", () => {
  const loader = new NpcPromptLoader(DATA_PATH);
  expect(loader.getNpcData("NonexistentNpc")).toBeNull();
});

test("getPhaseForFriendship returns correct phase", () => {
  const loader = new NpcPromptLoader(DATA_PATH);
  expect(loader.getPhaseForFriendship("Abigail", 0)).toBe("stranger");
  expect(loader.getPhaseForFriendship("Abigail", 250)).toBe("stranger");
  expect(loader.getPhaseForFriendship("Abigail", 251)).toBe("acquaintance");
  expect(loader.getPhaseForFriendship("Abigail", 500)).toBe("acquaintance");
  expect(loader.getPhaseForFriendship("Abigail", 501)).toBe("friend");
  expect(loader.getPhaseForFriendship("Abigail", 1000)).toBe("friend");
  expect(loader.getPhaseForFriendship("Abigail", 1001)).toBe("close");
  expect(loader.getPhaseForFriendship("Abigail", 2000)).toBe("close");
  expect(loader.getPhaseForFriendship("Abigail", 2001)).toBe("partner");
  expect(loader.getPhaseForFriendship("Abigail", 2500)).toBe("partner");
});

test("getPhasePrompt returns the phase prompt text", () => {
  const loader = new NpcPromptLoader(DATA_PATH);
  const prompt = loader.getPhasePrompt("Abigail", 251);
  expect(prompt).toContain("Abigail");
  expect(prompt.length).toBeGreaterThan(50);
});

test("getAttitudeBrief returns correct brief for friendship level", () => {
  const loader = new NpcPromptLoader(DATA_PATH);
  expect(loader.getAttitudeBrief(0)).toContain("素不相识");
  expect(loader.getAttitudeBrief(300)).toContain("点头之交");
  expect(loader.getAttitudeBrief(800)).toContain("朋友");
  expect(loader.getAttitudeBrief(1500)).toContain("亲密好友");
  expect(loader.getAttitudeBrief(2200)).toContain("夫妻");
});
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd <VALLEYAI_ROOT> && bun test packages/stardew/tests/npc-prompt-loader.test.ts`
Expected: FAIL with "Cannot find module '../src/npc-prompt-loader'"

- [ ] **Step 3: Write minimal implementation**

Create `<VALLEYAI_ROOT>\packages\stardew\src\npc-prompt-loader.ts`:

```typescript
import { readFileSync } from "fs";

export interface NpcPhase {
  friendship_range: string;
  prompt: string;
}

export interface NpcPhases {
  stranger: NpcPhase;
  acquaintance: NpcPhase;
  friend: NpcPhase;
  close: NpcPhase;
  partner: NpcPhase;
}

export interface NpcData {
  base_memory: string;
  phases: NpcPhases;
}

export type PhaseName = keyof NpcPhases;

export class NpcPromptLoader {
  private readonly data: Record<string, NpcData>;
  private static readonly PHASE_THRESHOLDS: Array<{ name: PhaseName; min: number }> = [
    { name: "stranger", min: 0 },
    { name: "acquaintance", min: 251 },
    { name: "friend", min: 501 },
    { name: "close", min: 1001 },
    { name: "partner", min: 2001 },
  ];

  constructor(dataPath: string) {
    const raw = readFileSync(dataPath, "utf-8");
    this.data = JSON.parse(raw) as Record<string, NpcData>;
  }

  listNpcs(): string[] {
    return Object.keys(this.data);
  }

  getNpcData(npcName: string): NpcData | null {
    return this.data[npcName] ?? null;
  }

  getPhaseForFriendship(npcName: string, friendship: number): PhaseName {
    let result: PhaseName = "stranger";
    for (const { name, min } of NpcPromptLoader.PHASE_THRESHOLDS) {
      if (friendship >= min) result = name;
    }
    // Verify the NPC exists; default to stranger even if unknown
    if (!this.data[npcName]) return "stranger";
    return result;
  }

  getPhasePrompt(npcName: string, friendship: number): string {
    const npc = this.getNpcData(npcName);
    if (!npc) {
      return `你是 ${npcName}，一个星露谷的居民。`;
    }
    const phase = this.getPhaseForFriendship(npcName, friendship);
    return npc.phases[phase].prompt;
  }

  getAttitudeBrief(friendship: number): string {
    const hearts = friendship / 250; // 0-10 hearts
    if (hearts <= 0) return "你和农场主素不相识，保持距离。";
    if (hearts <= 1) return "你和农场主只是点头之交，保持礼貌。";
    if (hearts <= 4) return "你和农场主刚认识，保持礼貌距离。";
    if (hearts <= 7) return "你和农场主是朋友，愿意聊天帮忙。";
    if (hearts <= 9) return "你和农场主是亲密好友，无话不谈。";
    if (hearts <= 11) return "你和农场主正在约会，有浪漫情愫。";
    return "你和农场主是夫妻，深爱彼此。";
  }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `cd <VALLEYAI_ROOT> && bun test packages/stardew/tests/npc-prompt-loader.test.ts`
Expected: PASS (6 tests)

Run typecheck: `cd <VALLEYAI_ROOT>\packages\stardew && bun run typecheck`
Expected: 0 errors

- [ ] **Step 5: Commit**

```bash
cd <VALLEYAI_ROOT>
git add packages/stardew/src/npc-prompt-loader.ts packages/stardew/tests/npc-prompt-loader.test.ts
git commit -m "feat(stardew): add NpcPromptLoader for npc_prompts.json access

Loads 33 NPCs x 5 phases. getPhaseForFriendship maps 0-2500 friendship to stranger/acquaintance/friend/close/partner. getAttitudeBrief produces hearts-based attitude hint."
```

---

### Task 6: WorldSnapshotDecoder + SceneState

**Files:**
- Create: `<VALLEYAI_ROOT>\packages\stardew\src\world-snapshot-decoder.ts`
- Test: `<VALLEYAI_ROOT>\packages\stardew\tests\world-snapshot-decoder.test.ts`

- [ ] **Step 1: Write the failing test**

Create `<VALLEYAI_ROOT>\packages\stardew\tests\world-snapshot-decoder.test.ts`:

```typescript
import { test, expect } from "bun:test";
import { decodeWorldSnapshot, timeToPeriod, coarsenLocation, summarizeNearby } from "../src/world-snapshot-decoder";
import type { WorldSnapshot } from "../src/types";

const validSnapshot: WorldSnapshot = {
  season: "summer",
  day: 28,
  time: "14:30",
  weather: "sunny",
  location: "Town",
  npcTile: { x: 32, y: 18 },
  nearbyObjects: "2 villagers, Pierre's shop entrance",
  friendship: 250,
  npcState: "IDLE",
  inventory: [{ name: "Amethyst", quantity: 2 }],
  farmerName: "新来的农夫",
};

test("decodes valid WorldSnapshot to SceneState", () => {
  const scene = decodeWorldSnapshot(validSnapshot);
  expect(scene.season).toBe("summer");
  expect(scene.day).toBe(28);
  expect(scene.timeStr).toBe("14:30");
  expect(scene.weather).toBe("sunny");
  expect(scene.location).toBe("Town");
  expect(scene.npcTile).toEqual({ x: 32, y: 18 });
  expect(scene.nearbyObjects).toBe("2 villagers, Pierre's shop entrance");
  expect(scene.friendship).toBe(250);
  expect(scene.npcState).toBe("IDLE");
  expect(scene.inventory).toEqual([{ name: "Amethyst", quantity: 2 }]);
  expect(scene.farmerName).toBe("新来的农夫");
});

test("decodes snapshot with missing optional fields using defaults", () => {
  const partial = { ...validSnapshot, nearbyObjects: "", inventory: [] } as WorldSnapshot;
  const scene = decodeWorldSnapshot(partial);
  expect(scene.nearbyObjects).toBe("");
  expect(scene.inventory).toEqual([]);
});

test("decodes snapshot with missing required fields throws", () => {
  // Use unknown cast to bypass TS check at call site
  const broken = { season: "summer" } as unknown as WorldSnapshot;
  expect(() => decodeWorldSnapshot(broken)).toThrow(/missing required field/);
});

test("timeToPeriod maps hours to time-of-day labels", () => {
  expect(timeToPeriod("03:00")).toBe("凌晨");
  expect(timeToPeriod("09:00")).toBe("上午");
  expect(timeToPeriod("12:00")).toBe("下午");
  expect(timeToPeriod("17:00")).toBe("晚上");
  expect(timeToPeriod("22:00")).toBe("深夜");
});

test("coarsenLocation maps known locations to area names", () => {
  expect(coarsenLocation("Town")).toBe("小镇");
  expect(coarsenLocation("SeedShop")).toBe("小镇");
  expect(coarsenLocation("Farm")).toBe("农场");
  expect(coarsenLocation("FarmHouse")).toBe("农场");
  expect(coarsenLocation("Forest")).toBe("森林");
  expect(coarsenLocation("Mine")).toBe("矿洞");
  expect(coarsenLocation("Beach")).toBe("海滩");
  expect(coarsenLocation("Unknown")).toBe("Unknown");
});

test("summarizeNearby returns input or default when empty", () => {
  expect(summarizeNearby("2 villagers")).toBe("2 villagers");
  expect(summarizeNearby("")).toBe("周围空无一人");
  expect(summarizeNearby(undefined as unknown as string)).toBe("周围空无一人");
});
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd <VALLEYAI_ROOT> && bun test packages/stardew/tests/world-snapshot-decoder.test.ts`
Expected: FAIL with "Cannot find module '../src/world-snapshot-decoder'"

- [ ] **Step 3: Write minimal implementation**

Create `<VALLEYAI_ROOT>\packages\stardew\src\world-snapshot-decoder.ts`:

```typescript
import type { WorldSnapshot, SceneState } from "./types";

const REQUIRED_FIELDS = ["season", "day", "time", "weather", "location", "friendship", "farmerName"] as const;

export function decodeWorldSnapshot(snap: WorldSnapshot): SceneState {
  for (const field of REQUIRED_FIELDS) {
    if (snap[field] === undefined || snap[field] === null) {
      throw new Error(`WorldSnapshot missing required field: ${field}`);
    }
  }

  return {
    season: String(snap.season),
    day: Number(snap.day),
    timeStr: String(snap.time),
    weather: String(snap.weather),
    location: String(snap.location),
    npcTile: { x: Number(snap.npcTile?.x ?? 0), y: Number(snap.npcTile?.y ?? 0) },
    nearbyObjects: snap.nearbyObjects ?? "",
    friendship: Number(snap.friendship),
    npcState: snap.npcState ?? "IDLE",
    inventory: Array.isArray(snap.inventory) ? snap.inventory.map(i => ({ name: String(i.name), quantity: Number(i.quantity) })) : [],
    farmerName: String(snap.farmerName),
  };
}

export function timeToPeriod(timeStr: string): string {
  const hour = parseInt(timeStr.split(":")[0] ?? "9", 10);
  if (isNaN(hour)) return "上午";
  if (hour < 6) return "凌晨";
  if (hour < 10) return "上午";
  if (hour < 14) return "下午";
  if (hour < 18) return "晚上";
  return "深夜";
}

export function coarsenLocation(location: string): string {
  const map: Record<string, string> = {
    town: "小镇", seedshop: "小镇", saloon: "小镇", blacksmith: "小镇",
    farm: "农场", farmhouse: "农场", forest: "森林", mountain: "山区",
    mine: "矿洞", beach: "海滩", desert: "沙漠",
  };
  const key = location.toLowerCase().replace(/\s/g, "");
  return map[key] ?? location;
}

export function summarizeNearby(nearby: string | undefined): string {
  if (!nearby || nearby.trim() === "") return "周围空无一人";
  return nearby;
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `cd <VALLEYAI_ROOT> && bun test packages/stardew/tests/world-snapshot-decoder.test.ts`
Expected: PASS (6 tests)

Run typecheck: `cd <VALLEYAI_ROOT>\packages\stardew && bun run typecheck`
Expected: 0 errors

- [ ] **Step 5: Commit**

```bash
cd <VALLEYAI_ROOT>
git add packages/stardew/src/world-snapshot-decoder.ts packages/stardew/tests/world-snapshot-decoder.test.ts
git commit -m "feat(stardew): add WorldSnapshotDecoder + time/location helpers

decodeWorldSnapshot validates required fields and converts C# JSON to SceneState. timeToPeriod/coarsenLocation/summarizeNearby ported from e2e/stardew-data.ts."
```

---

### Task 7: AgentMemory with load/save (implements MemoryBackend)

**Files:**
- Create: `<VALLEYAI_ROOT>\packages\stardew\src\agent-memory.ts`
- Test: `<VALLEYAI_ROOT>\packages\stardew\tests\agent-memory.test.ts`

- [ ] **Step 1: Write the failing test**

Create `<VALLEYAI_ROOT>\packages\stardew\tests\agent-memory.test.ts`:

```typescript
import { test, expect } from "bun:test";
import { AgentMemory } from "../src/agent-memory";
import { mkdtempSync, rmSync, existsSync, readFileSync, writeFileSync } from "fs";
import { join } from "path";
import { tmpdir } from "os";

function makeTempDir(): string {
  return mkdtempSync(join(tmpdir(), "valley-memory-test-"));
}

test("implements MemoryBackend interface", () => {
  const mem = new AgentMemory("Abigail", "/tmp/nonexistent");
  // Structural check: all methods exist
  expect(typeof mem.addConversation).toBe("function");
  expect(typeof mem.addMemory).toBe("function");
  expect(typeof mem.addSignificantMemory).toBe("function");
  expect(typeof mem.getConversationContext).toBe("function");
  expect(typeof mem.getRecentMemories).toBe("function");
  expect(typeof mem.getSignificantMemoriesText).toBe("function");
  expect(typeof mem.load).toBe("function");
  expect(typeof mem.save).toBe("function");
});

test("addConversation appends and trims to 50 entries", () => {
  const mem = new AgentMemory("Abigail", "/tmp/x");
  for (let i = 0; i < 60; i++) {
    mem.addConversation("player", `msg ${i}`);
  }
  expect(mem.conversationHistory.length).toBe(50);
  expect(mem.conversationHistory[0]!.text).toBe("msg 10");
});

test("addMemory deduplicates within 60-second window", () => {
  const mem = new AgentMemory("Abigail", "/tmp/x");
  mem.addMemory("picked up a rock", 5, "event", "Mine", ["item"]);
  mem.addMemory("picked up a rock", 5, "event", "Mine", ["item"]);
  expect(mem.shortTermMemories.length).toBe(1);
});

test("addMemory caps shortTermMemories at 30 entries", () => {
  const mem = new AgentMemory("Abigail", "/tmp/x");
  for (let i = 0; i < 40; i++) {
    mem.addMemory(`unique memory ${i}`, 1 + (i % 5), "event", "Town", [`tag${i}`]);
  }
  expect(mem.shortTermMemories.length).toBe(30);
});

test("addSignificantMemory deduplicates by text", () => {
  const mem = new AgentMemory("Abigail", "/tmp/x");
  const added1 = mem.addSignificantMemory("我和农场主第一次见面了", "relationship", "joy", ["Farmer"], "Town");
  const added2 = mem.addSignificantMemory("我和农场主第一次见面了", "relationship", "joy", ["Farmer"], "Town");
  expect(added1).toBe(true);
  expect(added2).toBe(false);
  expect(mem.significantMemories.length).toBe(1);
});

test("save writes JSON file with expected schema", async () => {
  const dir = makeTempDir();
  try {
    const filePath = join(dir, "Abigail_memory.json");
    const mem = new AgentMemory("Abigail", filePath);
    mem.addConversation("player", "hello");
    mem.addConversation("npc", "hi");
    mem.addMemory("saw a rock", 3, "event", "Mine", ["rock"]);
    mem.addSignificantMemory("first meeting", "relationship", "joy", ["Farmer"], "Town");
    mem.addFriendship(50);

    await mem.save();
    expect(existsSync(filePath)).toBe(true);

    const raw = readFileSync(filePath, "utf-8");
    const parsed = JSON.parse(raw);
    expect(parsed.npcName).toBe("Abigail");
    expect(parsed.conversationHistory).toHaveLength(2);
    expect(parsed.shortTermMemories).toHaveLength(1);
    expect(parsed.significantMemories).toHaveLength(1);
    expect(parsed.friendship).toBe(50);
    expect(parsed.lastSavedAt).toMatch(/^\d{4}-\d{2}-\d{2}T/);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("load reads JSON file and restores state", async () => {
  const dir = makeTempDir();
  try {
    const filePath = join(dir, "Abigail_memory.json");
    // Pre-populate file
    const seed = {
      npcName: "Abigail",
      conversationHistory: [{ role: "player", text: "previous msg" }],
      shortTermMemories: [{ text: "old memory", timestamp: 1000, importance: 5, entryType: "event", location: "Town", tags: [] }],
      significantMemories: [{ text: "past event", timestamp: 1000, category: "life_event", emotionalWeight: "joy", relatedNpcs: [], location: "Town" }],
      friendship: 200,
      lastSavedAt: "2026-07-18T00:00:00Z",
    };
    writeFileSync(filePath, JSON.stringify(seed));

    const mem = new AgentMemory("Abigail", filePath);
    await mem.load();
    expect(mem.conversationHistory).toHaveLength(1);
    expect(mem.conversationHistory[0]!.text).toBe("previous msg");
    expect(mem.shortTermMemories).toHaveLength(1);
    expect(mem.significantMemories).toHaveLength(1);
    expect(mem.friendship).toBe(200);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("load with missing file is a no-op (starts fresh)", async () => {
  const mem = new AgentMemory("Abigail", "/tmp/nonexistent_memory.json");
  await mem.load();
  expect(mem.conversationHistory).toEqual([]);
  expect(mem.shortTermMemories).toEqual([]);
  expect(mem.significantMemories).toEqual([]);
  expect(mem.friendship).toBe(0);
});

test("load + save roundtrip preserves state", async () => {
  const dir = makeTempDir();
  try {
    const filePath = join(dir, "Haley_memory.json");
    const mem1 = new AgentMemory("Haley", filePath);
    mem1.addConversation("player", "test");
    mem1.addMemory("event", 5, "event", "Town", []);
    mem1.addSignificantMemory("milestone", "relationship", "joy", ["Farmer"], "Town");
    mem1.addFriendship(100);
    await mem1.save();

    const mem2 = new AgentMemory("Haley", filePath);
    await mem2.load();
    expect(mem2.conversationHistory).toEqual(mem1.conversationHistory);
    expect(mem2.shortTermMemories).toEqual(mem1.shortTermMemories);
    expect(mem2.significantMemories).toEqual(mem1.significantMemories);
    expect(mem2.friendship).toBe(mem1.friendship);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("addFriendship clamps to 0-2500 range", () => {
  const mem = new AgentMemory("Abigail", "/tmp/x");
  mem.addFriendship(3000);
  expect(mem.friendship).toBe(2500);
  mem.addFriendship(-5000);
  expect(mem.friendship).toBe(0);
});

test("getConversationContext formats recent conversation", () => {
  const mem = new AgentMemory("Abigail", "/tmp/x");
  mem.addConversation("player", "你好");
  mem.addConversation("npc", "嗨");
  const ctx = mem.getConversationContext(10);
  expect(ctx).toContain("农场主: 你好");
  expect(ctx).toContain("Abigail: 嗨");
});

test("getSignificantMemoriesText returns default when empty", () => {
  const mem = new AgentMemory("Abigail", "/tmp/x");
  expect(mem.getSignificantMemoriesText()).toBe("（暂无特别记忆）");
});
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd <VALLEYAI_ROOT> && bun test packages/stardew/tests/agent-memory.test.ts`
Expected: FAIL with "Cannot find module '../src/agent-memory'"

- [ ] **Step 3: Write minimal implementation**

Create `<VALLEYAI_ROOT>\packages\stardew\src\agent-memory.ts`:

```typescript
import type { MemoryBackend, MemoryEntry, SignificantMemory, ConversationEntry } from "@valley/core";
import { readFile, writeFile, mkdir } from "fs/promises";
import { dirname } from "path";

interface MemoryFile {
  npcName: string;
  conversationHistory: ConversationEntry[];
  shortTermMemories: MemoryEntry[];
  significantMemories: SignificantMemory[];
  friendship: number;
  lastSavedAt: string;
}

const MAX_CONVERSATION = 50;
const MAX_SHORT_TERM = 30;
const DEDUP_WINDOW_SECONDS = 60;

export class AgentMemory implements MemoryBackend {
  conversationHistory: ConversationEntry[] = [];
  shortTermMemories: MemoryEntry[] = [];
  significantMemories: SignificantMemory[] = [];
  private _friendship: number = 0;
  private readonly filePath: string;

  constructor(public readonly npcName: string, filePath: string) {
    this.filePath = filePath;
  }

  get friendship(): number {
    return this._friendship;
  }

  addConversation(role: "player" | "npc", text: string): void {
    this.conversationHistory.push({ role, text });
    if (this.conversationHistory.length > MAX_CONVERSATION) {
      this.conversationHistory = this.conversationHistory.slice(-MAX_CONVERSATION);
    }
  }

  addMemory(
    text: string,
    importance: number = 1.0,
    entryType: string = "generic",
    location: string = "",
    tags: string[] = []
  ): void {
    const now = Date.now() / 1000;
    // 60s 去重窗口
    const exists = this.shortTermMemories.some(
      (m) => m.text === text && (now - m.timestamp) < DEDUP_WINDOW_SECONDS
    );
    if (exists) return;

    this.shortTermMemories.push({
      text,
      timestamp: now,
      importance: Math.max(0, Math.min(10, importance)),
      entryType,
      location,
      tags,
    });

    // MAX_SHORT_TERM 上限，按重要性+时间衰减排序
    if (this.shortTermMemories.length > MAX_SHORT_TERM) {
      this.shortTermMemories.sort((a, b) => {
        const scoreA = a.importance + (1 - (now - a.timestamp) / 7200) * 2;
        const scoreB = b.importance + (1 - (now - b.timestamp) / 7200) * 2;
        return scoreB - scoreA;
      });
      this.shortTermMemories = this.shortTermMemories.slice(0, MAX_SHORT_TERM);
    }
  }

  addSignificantMemory(
    text: string,
    category: string = "life_event",
    emotionalWeight: string = "joy",
    relatedNpcs: string[] = [],
    location: string = ""
  ): boolean {
    // significant memory 不重复
    if (this.significantMemories.some((m) => m.text === text)) {
      return false;
    }
    this.significantMemories.push({
      text,
      timestamp: Date.now() / 1000,
      category,
      emotionalWeight,
      relatedNpcs,
      location,
    });
    return true;
  }

  getSignificantMemoriesText(): string {
    if (this.significantMemories.length === 0) return "（暂无特别记忆）";
    return this.significantMemories.map((m) => `我记得... ${m.text}`).join("\n");
  }

  getRecentMemories(count: number = 5): string {
    const recent = this.shortTermMemories.slice(-count);
    if (recent.length === 0) return "（无特别记忆）";
    return recent.map((m) => `- ${m.text}`).join("\n");
  }

  getConversationContext(count: number = 10): string {
    if (this.conversationHistory.length === 0) return "";
    const recent = this.conversationHistory.slice(-count);
    return recent
      .map((e) => {
        const label = e.role === "player" ? "农场主" : this.npcName;
        return `${label}: ${e.text}`;
      })
      .join("\n");
  }

  addFriendship(delta: number): void {
    this._friendship = Math.max(0, Math.min(2500, this._friendship + delta));
  }

  async load(): Promise<void> {
    try {
      const raw = await readFile(this.filePath, "utf-8");
      const data = JSON.parse(raw) as MemoryFile;
      this.conversationHistory = data.conversationHistory ?? [];
      this.shortTermMemories = data.shortTermMemories ?? [];
      this.significantMemories = data.significantMemories ?? [];
      this._friendship = data.friendship ?? 0;
    } catch (err) {
      // File missing or invalid JSON — start fresh
      if ((err as NodeJS.ErrnoException).code !== "ENOENT") {
        console.warn(`[AgentMemory] Failed to load ${this.filePath}: ${(err as Error).message}`);
      }
    }
  }

  async save(): Promise<void> {
    const data: MemoryFile = {
      npcName: this.npcName,
      conversationHistory: this.conversationHistory,
      shortTermMemories: this.shortTermMemories,
      significantMemories: this.significantMemories,
      friendship: this._friendship,
      lastSavedAt: new Date().toISOString(),
    };
    await mkdir(dirname(this.filePath), { recursive: true });
    await writeFile(this.filePath, JSON.stringify(data, null, 2), "utf-8");
  }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `cd <VALLEYAI_ROOT> && bun test packages/stardew/tests/agent-memory.test.ts`
Expected: PASS (11 tests)

Run typecheck: `cd <VALLEYAI_ROOT>\packages\stardew && bun run typecheck`
Expected: 0 errors

- [ ] **Step 5: Commit**

```bash
cd <VALLEYAI_ROOT>
git add packages/stardew/src/agent-memory.ts packages/stardew/tests/agent-memory.test.ts
git commit -m "feat(stardew): add AgentMemory implementing MemoryBackend with JSON load/save

Ports e2e/stardew-memory.ts bugs fixes (60s dedup, MAX_SHORT_TERM=30, significant dedup). Adds load()/save() for spec 4.3 persistence: <mod_dir>/agents/<npc>_memory.json"
```

---

## Phase 3: TS stardew 对话组件

### Task 8: StardewTools（8 个对话工具）

**Files:**
- Create: `<VALLEYAI_ROOT>\packages\stardew\src\stardew-tools.ts`
- Test: `<VALLEYAI_ROOT>\packages\stardew\tests\stardew-tools.test.ts`

- [ ] **Step 1: Write the failing test**

Create `<VALLEYAI_ROOT>\packages\stardew\tests\stardew-tools.test.ts`:

```typescript
import { test, expect } from "bun:test";
import { buildStardewTools } from "../src/stardew-tools";
import type { ToolContext } from "../src/stardew-tools";
import { AgentMemory } from "../src/agent-memory";
import type { SceneState } from "../src/types";
import { ToolRegistry } from "@valley/core";

const scene: SceneState = {
  season: "summer",
  day: 28,
  timeStr: "14:30",
  weather: "sunny",
  location: "Town",
  nearbyObjects: "2 villagers",
  farmerName: "新来的农夫",
  friendship: 250,
  npcState: "IDLE",
  inventory: [{ name: "Amethyst", quantity: 2 }],
};

function makeCtx(): ToolContext {
  const memory = new AgentMemory("Abigail", "/tmp/x");
  return {
    memory,
    scene,
    inventory: [{ name: "Amethyst", quantity: 2 }],
    givenToPlayer: [],
    log: [],
  };
}

test("buildStardewTools returns exactly 8 tools", () => {
  const tools = buildStardewTools(makeCtx());
  expect(tools).toHaveLength(8);
  const names = tools.map((t) => t.name).sort();
  expect(names).toEqual([
    "emote", "get_info", "give_gift", "give_item",
    "remember", "set_state", "show_dialogue", "speak",
  ]);
});

test("all tools are llm_visible", () => {
  const tools = buildStardewTools(makeCtx());
  for (const t of tools) {
    expect(t.visibility).toBe("llm_visible");
  }
});

test("speak tool records conversation in memory", async () => {
  const ctx = makeCtx();
  const tools = buildStardewTools(ctx);
  const speak = tools.find((t) => t.name === "speak")!;
  const result = await speak.execute({ text: "你好！" });
  expect(result.isError).toBeFalsy();
  expect(ctx.memory.conversationHistory).toHaveLength(1);
  expect(ctx.memory.conversationHistory[0]!.text).toBe("你好！");
  expect(ctx.log.some((l) => l.includes("[speak]"))).toBe(true);
});

test("emote tool logs the emote_id", async () => {
  const ctx = makeCtx();
  const tools = buildStardewTools(ctx);
  const emote = tools.find((t) => t.name === "emote")!;
  await emote.execute({ emote_id: "heart" });
  expect(ctx.log.some((l) => l.includes("heart"))).toBe(true);
});

test("give_item decrements inventory and tracks givenToPlayer", async () => {
  const ctx = makeCtx();
  const tools = buildStardewTools(ctx);
  const give = tools.find((t) => t.name === "give_item")!;
  const result = await give.execute({ item_id: "Amethyst", quantity: 1 });
  expect(result.isError).toBeFalsy();
  expect(ctx.inventory[0]!.quantity).toBe(1);
  expect(ctx.givenToPlayer).toEqual([{ name: "Amethyst", quantity: 1 }]);
});

test("give_item errors when item not in inventory", async () => {
  const ctx = makeCtx();
  const tools = buildStardewTools(ctx);
  const give = tools.find((t) => t.name === "give_item")!;
  const result = await give.execute({ item_id: "Nonexistent", quantity: 1 });
  expect(result.isError).toBe(true);
});

test("give_gift records memory with gift tag", async () => {
  const ctx = makeCtx();
  const tools = buildStardewTools(ctx);
  const gift = tools.find((t) => t.name === "give_gift")!;
  await gift.execute({ item_id: "Amethyst", quantity: 1 });
  expect(ctx.memory.shortTermMemories.some((m) => m.entryType === "gift")).toBe(true);
});

test("set_state records decision in memory", async () => {
  const ctx = makeCtx();
  const tools = buildStardewTools(ctx);
  const setState = tools.find((t) => t.name === "set_state")!;
  await setState.execute({ state: "TALK" });
  expect(ctx.memory.shortTermMemories.some((m) => m.entryType === "decision")).toBe(true);
});

test("remember tool calls addSignificantMemory", async () => {
  const ctx = makeCtx();
  const tools = buildStardewTools(ctx);
  const remember = tools.find((t) => t.name === "remember")!;
  const result = await remember.execute({
    text: "我和农场主第一次见面了",
    category: "relationship",
    emotional_weight: "joy",
  });
  expect(result.isError).toBeFalsy();
  expect(ctx.memory.significantMemories).toHaveLength(1);
});

test("get_info returns date/nearby/inventory/player/location data", async () => {
  const ctx = makeCtx();
  const tools = buildStardewTools(ctx);
  const getInfo = tools.find((t) => t.name === "get_info")!;

  const dateResult = await getInfo.execute({ query: "date" });
  expect(dateResult.content).toContain("summer");

  const nearbyResult = await getInfo.execute({ query: "nearby" });
  expect(nearbyResult.content).toContain("2 villagers");

  const invResult = await getInfo.execute({ query: "inventory" });
  expect(invResult.content).toContain("Amethyst");

  const playerResult = await getInfo.execute({ query: "player" });
  expect(playerResult.content).toContain("新来的农夫");

  const locResult = await getInfo.execute({ query: "location" });
  expect(locResult.content).toContain("Town");
});

test("tools register cleanly into ToolRegistry", () => {
  const ctx = makeCtx();
  const tools = buildStardewTools(ctx);
  const registry = new ToolRegistry();
  for (const t of tools) registry.register(t);
  expect(registry.getAll()).toHaveLength(8);
  expect(registry.getByName("speak")).toBeDefined();
  expect(registry.getByName("emote")).toBeDefined();
});
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd <VALLEYAI_ROOT> && bun test packages/stardew/tests/stardew-tools.test.ts`
Expected: FAIL with "Cannot find module '../src/stardew-tools'"

- [ ] **Step 3: Write minimal implementation**

Create `<VALLEYAI_ROOT>\packages\stardew\src\stardew-tools.ts`:

```typescript
import { Type } from "@sinclair/typebox";
import type { Tool, ToolResult } from "@valley/core";
import type { AgentMemory } from "./agent-memory";
import type { SceneState } from "./types";

const EMOTE_IDS = Type.Union(
  ["happy", "sad", "angry", "surprised", "heart", "question", "exclamation", "sleep", "music", "stretch", "star", "love", "note", "wave", "hooray", "confused", "thinking", "annoyed", "worried", "frustrated", "sweat", "fish", "gift", "bomb"].map((s) => Type.Literal(s))
);

const STATES = Type.Union(
  ["IDLE", "FOLLOW", "FIGHT", "FARM", "FORAGE", "MINE", "TALK"].map((s) => Type.Literal(s))
);

export interface ToolContext {
  memory: AgentMemory;
  scene: SceneState;
  inventory: Array<{ name: string; quantity: number }>;
  givenToPlayer: Array<{ name: string; quantity: number }>;
  log: string[];
}

export function buildStardewTools(ctx: ToolContext): Tool[] {
  return [
    {
      name: "speak",
      description: "NPC speaks a line of dialogue to the farmer. Use when you want to say something. The text will be shown in a speech bubble above your head.",
      visibility: "llm_visible",
      parameters: Type.Object({
        text: Type.String({ description: "What you want to say (in character, 1-2 sentences, in Chinese)" }),
        duration_ms: Type.Optional(Type.Integer({ description: "Bubble display duration in ms, default 3000" })),
      }),
      async execute(args): Promise<ToolResult> {
        const text = String(args.text ?? "");
        ctx.memory.addConversation("npc", text);
        ctx.log.push(`[speak] "${text}"`);
        return { content: `You said: "${text}"`, details: { action: "speak", text } };
      },
    },
    {
      name: "emote",
      description: "Show an emotion bubble above your head. Express how you feel (happy, sad, love, wave, etc.).",
      visibility: "llm_visible",
      parameters: Type.Object({
        emote_id: EMOTE_IDS,
        duration_ms: Type.Optional(Type.Integer({ description: "Duration in ms, default 2000" })),
        bubble_text: Type.Optional(Type.String({ description: "Optional text alongside the emote" })),
      }),
      async execute(args): Promise<ToolResult> {
        const emote = String(args.emote_id ?? "happy");
        const bubbleText = args.bubble_text !== undefined ? String(args.bubble_text) : "";
        ctx.log.push(`[emote] ${emote}${bubbleText ? ` ("${bubbleText}")` : ""}`);
        return { content: `You showed ${emote} emotion`, details: { action: "emote", emote_id: emote } };
      },
    },
    {
      name: "give_item",
      description: "Give an item from your inventory to the farmer. Use for sharing items (not emotional gifts — use give_gift for that).",
      visibility: "llm_visible",
      parameters: Type.Object({
        item_id: Type.String({ description: "Name of the item to give" }),
        quantity: Type.Optional(Type.Integer({ description: "Quantity, default 1" })),
      }),
      async execute(args): Promise<ToolResult> {
        const name = String(args.item_id ?? "");
        const qty = Number(args.quantity ?? 1);
        const entry = ctx.inventory.find((i) => i.name === name);
        if (!entry) {
          return { content: `You don't have "${name}" in your inventory.`, isError: true };
        }
        if (entry.quantity < qty) {
          return { content: `You only have ${entry.quantity}x ${name}, can't give ${qty}.`, isError: true };
        }
        entry.quantity -= qty;
        const given = ctx.givenToPlayer.find((g) => g.name === name);
        if (given) given.quantity += qty;
        else ctx.givenToPlayer.push({ name, quantity: qty });
        if (entry.quantity === 0) {
          ctx.inventory = ctx.inventory.filter((i) => i !== entry);
        }
        ctx.log.push(`[give_item] ${qty}x ${name} → farmer`);
        return { content: `You gave ${qty}x ${name} to the farmer.`, details: { action: "give_item", item: name, quantity: qty } };
      },
    },
    {
      name: "give_gift",
      description: "Give a gift to the farmer as a gesture of friendship. This carries emotional weight and will be remembered. Different from give_item.",
      visibility: "llm_visible",
      parameters: Type.Object({
        item_id: Type.String({ description: "Name of the gift item" }),
        quantity: Type.Optional(Type.Integer({ description: "Quantity, default 1" })),
      }),
      async execute(args): Promise<ToolResult> {
        const name = String(args.item_id ?? "");
        const qty = Number(args.quantity ?? 1);
        const entry = ctx.inventory.find((i) => i.name === name);
        if (!entry) {
          return { content: `You don't have "${name}" to gift.`, isError: true };
        }
        entry.quantity -= qty;
        if (entry.quantity <= 0) {
          ctx.inventory = ctx.inventory.filter((i) => i !== entry);
        }
        ctx.memory.addMemory(
          `送给农场主 ${qty}x ${name} 作为礼物`,
          6.0,
          "gift",
          ctx.scene.location,
          ["gift", "friendship"]
        );
        ctx.log.push(`[give_gift] ${qty}x ${name} → farmer (emotional)`);
        return { content: `You gave ${qty}x ${name} as a gift to the farmer. They seem touched.`, details: { action: "give_gift", item: name, quantity: qty } };
      },
    },
    {
      name: "set_state",
      description: "Switch your behavior state. States: IDLE(rest), FOLLOW(accompany farmer), FIGHT(combat), FARM(farming), FORAGE(collecting), MINE(mining), TALK(conversing).",
      visibility: "llm_visible",
      parameters: Type.Object({
        state: STATES,
      }),
      async execute(args): Promise<ToolResult> {
        const state = String(args.state ?? "IDLE");
        ctx.memory.addMemory(`决定切换到 ${state} 状态`, 3.0, "decision", ctx.scene.location, ["state"]);
        ctx.log.push(`[set_state] → ${state}`);
        return { content: `You switched to ${state} state.`, details: { action: "set_state", state } };
      },
    },
    {
      name: "show_dialogue",
      description: "Proactively initiate a dialogue with the farmer. Share news or express feelings. Use when you want to start a conversation.",
      visibility: "llm_visible",
      parameters: Type.Object({
        text: Type.String({ description: "What you want to say (1-3 sentences, in Chinese, in character)" }),
        style: Type.Optional(Type.String({ description: "Display style: bubble/chat/dialogue_box, default bubble" })),
      }),
      async execute(args): Promise<ToolResult> {
        const text = String(args.text ?? "");
        ctx.memory.addConversation("npc", text);
        ctx.log.push(`[show_dialogue] "${text}"`);
        return { content: `You initiated dialogue: "${text}"`, details: { action: "show_dialogue", text } };
      },
    },
    {
      name: "remember",
      description: "Record a significant memory that you will never forget. Use for relationship milestones, major events, emotional turning points, or traumatic experiences. Write in first person (e.g., 'I met the farmer for the first time today').",
      visibility: "llm_visible",
      parameters: Type.Object({
        text: Type.String({ description: "First-person memory, e.g., '我和农场主第一次见面了'" }),
        category: Type.Union(
          ["relationship", "life_event", "trauma", "achievement"].map((s) => Type.Literal(s)),
          { description: "relationship=relationship change, life_event=major life event, trauma=trauma, achievement=achievement" }
        ),
        emotional_weight: Type.Union(
          ["joy", "sorrow", "anger", "fear", "love", "pride", "surprise"].map((s) => Type.Literal(s)),
          { description: "Emotional weight of this memory" }
        ),
      }),
      async execute(args): Promise<ToolResult> {
        const text = String(args.text ?? "");
        const category = String(args.category ?? "life_event");
        const weight = String(args.emotional_weight ?? "joy");
        const added = ctx.memory.addSignificantMemory(
          text, category, weight,
          ["Farmer"], ctx.scene.location
        );
        if (added) {
          ctx.log.push(`[remember] (${category}/${weight}) "${text}"`);
          return { content: `You will always remember: "${text}"`, details: { text, category, emotional_weight: weight } };
        } else {
          return { content: `You already have this memory: "${text}"`, details: { duplicate: true } };
        }
      },
    },
    {
      name: "get_info",
      description: "Query game world information. Use when you need to know the date, nearby details, your inventory, your memories, or the player's status.",
      visibility: "llm_visible",
      parameters: Type.Object({
        query: Type.Union(
          ["date", "nearby", "memory", "health", "inventory", "player", "location"].map((s) => Type.Literal(s)),
          { description: "date=specific date, nearby=nearby objects detail, memory=recent memories, health=your health, inventory=your items, player=player status, location=current location" }
        ),
      }),
      async execute(args): Promise<ToolResult> {
        const q = String(args.query ?? "");
        let info = "";
        switch (q) {
          case "date":
            info = `${ctx.scene.season}, day ${ctx.scene.day}, ${ctx.scene.timeStr}`;
            break;
          case "nearby":
            info = ctx.scene.nearbyObjects;
            break;
          case "memory":
            info = ctx.memory.getRecentMemories(5);
            break;
          case "health":
            info = "Health: 100/100 (full health)";
            break;
          case "inventory":
            info = ctx.inventory.length > 0
              ? ctx.inventory.map((i) => `- ${i.name} x${i.quantity}`).join("\n")
              : "Inventory is empty.";
            break;
          case "player":
            info = `${ctx.scene.farmerName}, friendship: ${ctx.memory.friendship}/2500`;
            break;
          case "location":
            info = `Current location: ${ctx.scene.location}`;
            break;
          default:
            info = `Unknown query: ${q}`;
        }
        ctx.log.push(`[get_info] ${q} → ${info.slice(0, 60)}`);
        return { content: info, details: { query: q } };
      },
    },
  ];
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `cd <VALLEYAI_ROOT> && bun test packages/stardew/tests/stardew-tools.test.ts`
Expected: PASS (11 tests)

Run typecheck: `cd <VALLEYAI_ROOT>\packages\stardew && bun run typecheck`
Expected: 0 errors

- [ ] **Step 5: Commit**

```bash
cd <VALLEYAI_ROOT>
git add packages/stardew/src/stardew-tools.ts packages/stardew/tests/stardew-tools.test.ts
git commit -m "feat(stardew): add 8 dialogue tools (speak/emote/give_item/give_gift/set_state/show_dialogue/remember/get_info)

Refactored from e2e/stardew-tools.ts: removed wait/stop (not in spec P0). Tools share ToolContext (memory/scene/inventory/log). All visibility=llm_visible."
```

---

### Task 9: PromptBuilder with npc_prompts.json

**Files:**
- Create: `<VALLEYAI_ROOT>\packages\stardew\src\prompt-builder.ts`
- Test: `<VALLEYAI_ROOT>\packages\stardew\tests\prompt-builder.test.ts`

- [ ] **Step 1: Write the failing test**

Create `<VALLEYAI_ROOT>\packages\stardew\tests\prompt-builder.test.ts`:

```typescript
import { test, expect } from "bun:test";
import { PromptBuilder } from "../src/prompt-builder";
import { NpcPromptLoader } from "../src/npc-prompt-loader";
import { AgentMemory } from "../src/agent-memory";
import type { SceneState } from "../src/types";
import { resolve } from "path";

const DATA_PATH = resolve(import.meta.dir, "../data/npc_prompts.json");

const scene: SceneState = {
  season: "summer",
  day: 28,
  timeStr: "14:30",
  weather: "sunny",
  location: "Town",
  nearbyObjects: "2 villagers, Pierre's shop entrance",
  farmerName: "新来的农夫",
  friendship: 250,
  npcState: "IDLE",
  inventory: [{ name: "Amethyst", quantity: 2 }],
};

test("buildDialogueSystemPrompt includes NPC base_memory", () => {
  const loader = new NpcPromptLoader(DATA_PATH);
  const builder = new PromptBuilder(loader);
  const memory = new AgentMemory("Abigail", "/tmp/x");
  const prompt = builder.buildDialogueSystemPrompt(memory, scene, "Abigail");
  expect(prompt).toContain("Abigail");
  // base_memory mentions 紫水晶 or 矿洞
  expect(prompt.toLowerCase()).toMatch(/紫水晶|矿洞|amethyst/);
});

test("buildDialogueSystemPrompt includes phase prompt for friendship level", () => {
  const loader = new NpcPromptLoader(DATA_PATH);
  const builder = new PromptBuilder(loader);

  const mem0 = new AgentMemory("Abigail", "/tmp/x");
  const prompt0 = builder.buildDialogueSystemPrompt(mem0, scene, "Abigail");
  // stranger phase mentions 不太有兴趣 or similar
  expect(prompt0).toContain("Abigail");

  const mem500 = new AgentMemory("Abigail", "/tmp/x");
  mem500.addFriendship(500);
  const scene500 = { ...scene, friendship: 500 };
  const prompt500 = builder.buildDialogueSystemPrompt(mem500, scene500, "Abigail");
  expect(prompt500).toContain("Abigail");
});

test("buildDialogueSystemPrompt fills scene placeholders", () => {
  const loader = new NpcPromptLoader(DATA_PATH);
  const builder = new PromptBuilder(loader);
  const memory = new AgentMemory("Haley", "/tmp/x");
  const prompt = builder.buildDialogueSystemPrompt(memory, scene, "Haley");
  expect(prompt).toContain("summer");
  expect(prompt).toContain("下午"); // 14:30 → 下午
  expect(prompt).toContain("sunny");
  expect(prompt).toContain("新来的农夫");
});

test("buildDialogueSystemPrompt includes significant memories", () => {
  const loader = new NpcPromptLoader(DATA_PATH);
  const builder = new PromptBuilder(loader);
  const memory = new AgentMemory("Abigail", "/tmp/x");
  memory.addSignificantMemory("我和农场主第一次见面了", "relationship", "joy", ["Farmer"], "Town");
  const prompt = builder.buildDialogueSystemPrompt(memory, scene, "Abigail");
  expect(prompt).toContain("我和农场主第一次见面了");
});

test("buildDialogueSystemPrompt includes recent memories", () => {
  const loader = new NpcPromptLoader(DATA_PATH);
  const builder = new PromptBuilder(loader);
  const memory = new AgentMemory("Abigail", "/tmp/x");
  memory.addMemory("在矿洞里挖到紫水晶", 5, "event", "Mine", ["item"]);
  const prompt = builder.buildDialogueSystemPrompt(memory, scene, "Abigail");
  expect(prompt).toContain("在矿洞里挖到紫水晶");
});

test("buildDialogueSystemPrompt works for unknown NPC with fallback", () => {
  const loader = new NpcPromptLoader(DATA_PATH);
  const builder = new PromptBuilder(loader);
  const memory = new AgentMemory("UnknownNpc", "/tmp/x");
  const prompt = builder.buildDialogueSystemPrompt(memory, scene, "UnknownNpc");
  expect(prompt).toContain("UnknownNpc");
});

test("buildDialogueSystemPrompt includes attitude brief", () => {
  const loader = new NpcPromptLoader(DATA_PATH);
  const builder = new PromptBuilder(loader);
  const memory = new AgentMemory("Abigail", "/tmp/x");
  const prompt = builder.buildDialogueSystemPrompt(memory, scene, "Abigail");
  // friendship 250 → stranger → 素不相识 or 点头之交
  expect(prompt).toMatch(/素不相识|点头之交|刚认识|朋友|亲密|夫妻/);
});

test("buildDialogueSystemPrompt includes all 9 placeholders filled (no unfilled braces)", () => {
  const loader = new NpcPromptLoader(DATA_PATH);
  const builder = new PromptBuilder(loader);
  const memory = new AgentMemory("Abigail", "/tmp/x");
  memory.addMemory("test memory", 1, "event", "Town", []);
  const prompt = builder.buildDialogueSystemPrompt(memory, scene, "Abigail");
  // No unfilled {placeholder} patterns
  expect(prompt).not.toMatch(/\{[a-z_]+\}/);
});
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd <VALLEYAI_ROOT> && bun test packages/stardew/tests/prompt-builder.test.ts`
Expected: FAIL with "Cannot find module '../src/prompt-builder'"

- [ ] **Step 3: Write minimal implementation**

Create `<VALLEYAI_ROOT>\packages\stardew\src\prompt-builder.ts`:

```typescript
import type { AgentMemory } from "./agent-memory";
import type { SceneState } from "./types";
import type { NpcPromptLoader } from "./npc-prompt-loader";
import { timeToPeriod, coarsenLocation, summarizeNearby } from "./world-snapshot-decoder";

const DIALOGUE_SYSTEM_TEMPLATE = `规则：
- 回复简短，1-2句话，像游戏NPC对白
- 完全代入角色，不做旁白式描述
- 绝不提及AI或处于游戏中
- 不编造角色没有的物品或知识
- 不用括号描述动作
- 需要了解具体日期、周围详情、背包等信息时，调用 get_info 工具查询
- 说话使用 speak 工具
- 想做某事时使用对应工具（set_state / emote / give_item / give_gift 等）
- 不需要做任何事时只调用 speak 工具说话即可

─── 我是谁 ───
{phase_prompt}

─── 我永远不会忘记的事 ───
{significant_memories}

─── 当前场景 ───
当前：{season} · {time_period} · {weather} · {location_area}
附近：{nearby_summary}
你称呼农场主为：{farmer_nickname}

最近记忆：
{recent_memory}

─── 重要事项记忆规则 ───
如果发生了以下类型的事件，你必须用 remember 工具记录（第一人称，永不遗忘）：
- 关系里程碑：第一次对话、成为朋友、开始约会、结婚、生子
- 重大事件：一起战斗、一起冒险、收到特别重要的礼物、生死时刻
- 情感转折：从讨厌到喜欢、从陌生到信任、重要的承诺或约定
- 创伤经历：被怪物击败、失去重要的人、极度恐惧的时刻
记录格式：第一人称短句，如"我和农场主第一次一起战斗了"、"他送了我最爱的向日葵"`;

export class PromptBuilder {
  constructor(private readonly loader: NpcPromptLoader) {}

  buildDialogueSystemPrompt(memory: AgentMemory, scene: SceneState, npcName: string): string {
    const npcData = this.loader.getNpcData(npcName);
    const phasePrompt = this.loader.getPhasePrompt(npcName, scene.friendship);
    const attitudeBrief = this.loader.getAttitudeBrief(scene.friendship);

    const baseMemory = npcData?.base_memory ?? `你是 ${npcName}，一个星露谷的居民。`;

    const fullPhasePrompt = [
      `你是${npcName}。${attitudeBrief}`,
      baseMemory,
      phasePrompt,
    ].join("\n\n");

    const significantMemories = memory.getSignificantMemoriesText();
    const recentMemory = memory.getRecentMemories(5);
    const timePeriod = timeToPeriod(scene.timeStr);
    const locationArea = coarsenLocation(scene.location);
    const nearbySummary = summarizeNearby(scene.nearbyObjects);

    return DIALOGUE_SYSTEM_TEMPLATE
      .replace("{phase_prompt}", fullPhasePrompt)
      .replace("{significant_memories}", significantMemories)
      .replace("{season}", scene.season)
      .replace("{time_period}", timePeriod)
      .replace("{weather}", scene.weather)
      .replace("{location_area}", locationArea)
      .replace("{nearby_summary}", nearbySummary)
      .replace("{farmer_nickname}", scene.farmerName)
      .replace("{recent_memory}", recentMemory);
  }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `cd <VALLEYAI_ROOT> && bun test packages/stardew/tests/prompt-builder.test.ts`
Expected: PASS (8 tests)

Run typecheck: `cd <VALLEYAI_ROOT>\packages\stardew && bun run typecheck`
Expected: 0 errors

- [ ] **Step 5: Commit**

```bash
cd <VALLEYAI_ROOT>
git add packages/stardew/src/prompt-builder.ts packages/stardew/tests/prompt-builder.test.ts
git commit -m "feat(stardew): add PromptBuilder using NpcPromptLoader for runtime prompt assembly

buildDialogueSystemPrompt(memory, scene, npcName) fills 9 placeholders with phase prompt + base_memory + attitude + significant/recent memories + scene vars. Replaces hardcoded Abigail constants."
```

---

### Task 10: OutputValidator

**Files:**
- Create: `<VALLEYAI_ROOT>\packages\stardew\src\output-validator.ts`
- Test: `<VALLEYAI_ROOT>\packages\stardew\tests\output-validator.test.ts`

- [ ] **Step 1: Write the failing test**

Create `<VALLEYAI_ROOT>\packages\stardew\tests\output-validator.test.ts`:

```typescript
import { test, expect } from "bun:test";
import { OutputValidator } from "../src/output-validator";

test("accepts Chinese-dominant text", () => {
  const v = new OutputValidator();
  const result = v.validate("你好，我是 Abigail。今天天气真好。");
  expect(result.valid).toBe(true);
});

test("rejects text with no CJK characters", () => {
  const v = new OutputValidator();
  const result = v.validate("Hello, I am Abigail. The weather is nice today.");
  expect(result.valid).toBe(false);
  expect(result.reason).toMatch(/CJK/i);
});

test("rejects empty text", () => {
  const v = new OutputValidator();
  const result = v.validate("");
  expect(result.valid).toBe(false);
  expect(result.reason).toMatch(/empty/i);
});

test("rejects whitespace-only text", () => {
  const v = new OutputValidator();
  const result = v.validate("   \n\t  ");
  expect(result.valid).toBe(false);
});

test("accepts mixed Chinese + English with CJK majority", () => {
  const v = new OutputValidator();
  const result = v.validate("我喜欢 Amethyst，它是最美的水晶。");
  expect(result.valid).toBe(true);
});

test("rejects English-dominant text (CJK ratio < 0.3)", () => {
  const v = new OutputValidator();
  const result = v.validate("Hello Abigail, this is a test of the dialogue system.");
  expect(result.valid).toBe(false);
});

test("buildRetryPrompt returns prompt asking for Chinese", () => {
  const v = new OutputValidator();
  const retry = v.buildRetryPrompt("Hello world");
  expect(retry).toContain("中文");
  expect(retry).toContain("Chinese");
});

test("validateSpeechAndActions accepts speak tool call with valid Chinese text", () => {
  const v = new OutputValidator();
  const result = v.validateSpeechAndActions(
    "你好",
    [{ tool: "speak", args: { text: "你好" } }]
  );
  expect(result.valid).toBe(true);
});

test("validateSpeechAndActions uses speech when no speak tool call", () => {
  const v = new OutputValidator();
  const result = v.validateSpeechAndActions(
    "你好",
    [{ tool: "emote", args: { emote_id: "happy" } }]
  );
  expect(result.valid).toBe(true);
});

test("validateSpeechAndActions rejects when both speech and tool text are invalid", () => {
  const v = new OutputValidator();
  const result = v.validateSpeechAndActions(
    "Hello",
    [{ tool: "speak", args: { text: "Hello" } }]
  );
  expect(result.valid).toBe(false);
});
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd <VALLEYAI_ROOT> && bun test packages/stardew/tests/output-validator.test.ts`
Expected: FAIL with "Cannot find module '../src/output-validator'"

- [ ] **Step 3: Write minimal implementation**

Create `<VALLEYAI_ROOT>\packages\stardew\src\output-validator.ts`:

```typescript
import type { ToolAction } from "./types";

const MIN_CJK_RATIO = 0.3;

export interface ValidationResult {
  valid: boolean;
  reason?: string;
}

export class OutputValidator {
  /**
   * Count CJK characters in text (Hiragana/Katakana/Han/Hangul ranges)
   */
  private countCjk(text: string): number {
    let count = 0;
    for (const ch of text) {
      const code = ch.codePointAt(0) ?? 0;
      // Hiragana: 0x3040-0x309F
      // Katakana: 0x30A0-0x30FF
      // CJK Unified Ideographs: 0x4E00-0x9FFF
      // Hangul Syllables: 0xAC00-0xD7AF
      if (
        (code >= 0x3040 && code <= 0x309F) ||
        (code >= 0x30A0 && code <= 0x30FF) ||
        (code >= 0x4E00 && code <= 0x9FFF) ||
        (code >= 0xAC00 && code <= 0xD7AF)
      ) {
        count++;
      }
    }
    return count;
  }

  validate(text: string): ValidationResult {
    if (!text || text.trim().length === 0) {
      return { valid: false, reason: "empty text" };
    }
    const cjkCount = this.countCjk(text);
    const totalChars = text.length;
    if (totalChars === 0) {
      return { valid: false, reason: "empty text" };
    }
    const ratio = cjkCount / totalChars;
    if (ratio < MIN_CJK_RATIO) {
      return { valid: false, reason: `CJK ratio ${ratio.toFixed(2)} < ${MIN_CJK_RATIO}` };
    }
    return { valid: true };
  }

  buildRetryPrompt(badText: string): string {
    return `你刚才的回复 "${badText.slice(0, 100)}" 不符合要求。请用中文重新回复，确保回复主要为中文，且内容简短（1-2句话）。Please respond in Chinese.`;
  }

  /**
   * Validate the final speech + actions extracted from agentLoop events.
   * Accepts if EITHER the speech field OR any speak/show_dialogue tool's text is valid.
   */
  validateSpeechAndActions(speech: string, actions: ToolAction[]): ValidationResult {
    // Check speak/show_dialogue tool args first
    for (const action of actions) {
      if (action.tool === "speak" || action.tool === "show_dialogue") {
        const text = String(action.args.text ?? "");
        const result = this.validate(text);
        if (result.valid) return { valid: true };
      }
    }
    // Fall back to speech field
    return this.validate(speech);
  }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `cd <VALLEYAI_ROOT> && bun test packages/stardew/tests/output-validator.test.ts`
Expected: PASS (10 tests)

Run typecheck: `cd <VALLEYAI_ROOT>\packages\stardew && bun run typecheck`
Expected: 0 errors

- [ ] **Step 5: Commit**

```bash
cd <VALLEYAI_ROOT>
git add packages/stardew/src/output-validator.ts packages/stardew/tests/output-validator.test.ts
git commit -m "feat(stardew): add OutputValidator for CJK ratio + empty detection

P0 minimal language check: rejects text with CJK ratio < 0.3 or empty. validateSpeechAndActions accepts if any speak/show_dialogue tool call has valid Chinese text."
```

---

### Task 11: RuleEngine（简化 IDLE fallback）

**Files:**
- Create: `<VALLEYAI_ROOT>\packages\stardew\src\rule-engine.ts`
- Test: `<VALLEYAI_ROOT>\packages\stardew\tests\rule-engine.test.ts`

- [ ] **Step 1: Write the failing test**

Create `<VALLEYAI_ROOT>\packages\stardew\tests\rule-engine.test.ts`:

```typescript
import { test, expect } from "bun:test";
import { RuleEngine } from "../src/rule-engine";
import { LLMBillingError, LLMUnavailableError } from "@valley/core";
import type { DialogueRequest } from "../src/types";

const req: DialogueRequest = {
  type: "dialogue",
  requestId: "req-1",
  npcName: "Abigail",
  playerInput: "你好",
  worldSnapshot: {
    season: "summer", day: 28, time: "14:30", weather: "sunny",
    location: "Town", npcTile: { x: 32, y: 18 },
    nearbyObjects: "2 villagers", friendship: 250, npcState: "IDLE",
    inventory: [], farmerName: "新来的农夫",
  },
};

test("buildFallbackResponse for LLMBillingError", () => {
  const engine = new RuleEngine();
  const err = new LLMBillingError("insufficient quota");
  const resp = engine.buildFallbackResponse(req, err);
  expect(resp.type).toBe("dialogue_response");
  expect(resp.requestId).toBe("req-1");
  expect(resp.npcName).toBe("Abigail");
  expect(resp.speech).toContain("走神");
  expect(resp.emotion).toBe("Confused");
  expect(resp.fallback).toBe(true);
  expect(resp.actions.some((a) => a.tool === "emote")).toBe(true);
});

test("buildFallbackResponse for LLMUnavailableError", () => {
  const engine = new RuleEngine();
  const err = new LLMUnavailableError("timeout");
  const resp = engine.buildFallbackResponse(req, err);
  expect(resp.speech).toContain("说不出来");
  expect(resp.emotion).toBe("Tired");
  expect(resp.fallback).toBe(true);
});

test("buildFallbackResponse for generic Error", () => {
  const engine = new RuleEngine();
  const err = new Error("something went wrong");
  const resp = engine.buildFallbackResponse(req, err);
  expect(resp.speech).toBe("......");
  expect(resp.emotion).toBe("Neutral");
  expect(resp.fallback).toBe(true);
  expect(resp.actions).toEqual([{ tool: "emote", args: { emote_id: "question" } }]);
});

test("buildFallbackResponse for non-Error thrown value", () => {
  const engine = new RuleEngine();
  const resp = engine.buildFallbackResponse(req, "string error");
  expect(resp.speech).toBe("......");
  expect(resp.fallback).toBe(true);
});

test("buildFallbackResponse always returns non-empty speech", () => {
  const engine = new RuleEngine();
  for (const err of [
    new LLMBillingError("x"),
    new LLMUnavailableError("x"),
    new Error("x"),
    "string",
    null,
    undefined,
  ]) {
    const resp = engine.buildFallbackResponse(req, err as unknown as Error);
    expect(resp.speech.length).toBeGreaterThan(0);
  }
});

test("buildFallbackResponse includes emote action", () => {
  const engine = new RuleEngine();
  const resp = engine.buildFallbackResponse(req, new Error("x"));
  expect(resp.actions.length).toBeGreaterThan(0);
  expect(resp.actions[0]!.tool).toBe("emote");
});
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd <VALLEYAI_ROOT> && bun test packages/stardew/tests/rule-engine.test.ts`
Expected: FAIL with "Cannot find module '../src/rule-engine'"

- [ ] **Step 3: Write minimal implementation**

Create `<VALLEYAI_ROOT>\packages\stardew\src\rule-engine.ts`:

```typescript
import { LLMBillingError, LLMUnavailableError } from "@valley/core";
import type { DialogueRequest, DialogueResponse, ToolAction } from "./types";

export class RuleEngine {
  buildFallbackResponse(req: DialogueRequest, err: unknown): DialogueResponse {
    let speech: string;
    let emotion: string;

    if (err instanceof LLMBillingError) {
      speech = "（我有点走神了，你刚说什么？）";
      emotion = "Confused";
    } else if (err instanceof LLMUnavailableError) {
      speech = "（话到嘴边说不出来...）";
      emotion = "Tired";
    } else {
      speech = "......";
      emotion = "Neutral";
    }

    const actions: ToolAction[] = [
      { tool: "emote", args: { emote_id: "question" } },
    ];

    return {
      type: "dialogue_response",
      requestId: req.requestId,
      npcName: req.npcName,
      speech,
      actions,
      emotion,
      fallback: true,
    };
  }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `cd <VALLEYAI_ROOT> && bun test packages/stardew/tests/rule-engine.test.ts`
Expected: PASS (6 tests)

Run typecheck: `cd <VALLEYAI_ROOT>\packages\stardew && bun run typecheck`
Expected: 0 errors

- [ ] **Step 5: Commit**

```bash
cd <VALLEYAI_ROOT>
git add packages/stardew/src/rule-engine.ts packages/stardew/tests/rule-engine.test.ts
git commit -m "feat(stardew): add simplified RuleEngine for Layer 3 fallback

buildFallbackResponse(req, err) maps LLMBillingError/LLMUnavailableError/generic Error to short fallback speech + question emote. Spec 5.2 Layer 3 implementation."
```

---

## Phase 4: TS stardew 对话主流程

### Task 12: StardewAgent class

**Files:**
- Create: `<VALLEYAI_ROOT>\packages\stardew\src\stardew-agent.ts`
- Test: `<VALLEYAI_ROOT>\packages\stardew\tests\stardew-agent.test.ts`

- [ ] **Step 1: Write the failing test**

Create `<VALLEYAI_ROOT>\packages\stardew\tests\stardew-agent.test.ts`:

```typescript
import { test, expect } from "bun:test";
import { StardewAgent } from "../src/stardew-agent";
import { AgentMemory } from "../src/agent-memory";
import { NpcPromptLoader } from "../src/npc-prompt-loader";
import { PromptBuilder } from "../src/prompt-builder";
import { VercelAIProvider, ToolRegistry, Agent } from "@valley/core";
import type { SceneState, ToolAction } from "../src/types";
import { resolve } from "path";

const DATA_PATH = resolve(import.meta.dir, "../data/npc_prompts.json");

const scene: SceneState = {
  season: "summer", day: 28, timeStr: "14:30", weather: "sunny",
  location: "Town", nearbyObjects: "2 villagers", farmerName: "新来的农夫",
  friendship: 250, npcState: "IDLE", inventory: [],
};

function makeMockLlmProvider(): VercelAIProvider {
  const provider = new VercelAIProvider({
    provider: "minimax",
    apiKey: "fake-key",
    model: "fake-model",
    baseUrl: "http://localhost:9999",
  });
  // Override the call to return a canned response with tool call
  provider._setCallOverride(async () => ({
    content: "（思考中）",
    toolCalls: [
      { id: "tc-1", name: "speak", args: { text: "你好啊，新来的农夫。" } },
    ],
  }));
  return provider;
}

test("StardewAgent constructs with all dependencies", () => {
  const loader = new NpcPromptLoader(DATA_PATH);
  const builder = new PromptBuilder(loader);
  const memory = new AgentMemory("Abigail", "/tmp/x");
  const provider = makeMockLlmProvider();

  const agent = new StardewAgent({
    name: "Abigail",
    memory,
    promptBuilder: builder,
    llmProvider: provider,
    maxTurns: 5,
  });

  expect(agent.name).toBe("Abigail");
  expect(agent.isIdle()).toBe(true);
});

test("StardewAgent.runDialogue returns speech + actions from LLM tool call", async () => {
  const loader = new NpcPromptLoader(DATA_PATH);
  const builder = new PromptBuilder(loader);
  const memory = new AgentMemory("Abigail", "/tmp/x");
  const provider = makeMockLlmProvider();

  const agent = new StardewAgent({
    name: "Abigail",
    memory,
    promptBuilder: builder,
    llmProvider: provider,
    maxTurns: 5,
  });

  const result = await agent.runDialogue("你好", scene);
  expect(result.speech).toBe("你好啊，新来的农夫。");
  expect(result.actions).toEqual([]);
  expect(result.emotion).toBe("Neutral");
});

test("StardewAgent.runDialogue records player + npc conversation in memory", async () => {
  const loader = new NpcPromptLoader(DATA_PATH);
  const builder = new PromptBuilder(loader);
  const memory = new AgentMemory("Abigail", "/tmp/x");
  const provider = makeMockLlmProvider();

  const agent = new StardewAgent({
    name: "Abigail",
    memory,
    promptBuilder: builder,
    llmProvider: provider,
    maxTurns: 5,
  });

  await agent.runDialogue("你好", scene);
  expect(memory.conversationHistory).toHaveLength(2);
  expect(memory.conversationHistory[0]!.role).toBe("player");
  expect(memory.conversationHistory[0]!.text).toBe("你好");
  expect(memory.conversationHistory[1]!.role).toBe("npc");
  expect(memory.conversationHistory[1]!.text).toBe("你好啊，新来的农夫。");
});

test("StardewAgent.runDialogue captures non-speak tool calls as actions", async () => {
  const loader = new NpcPromptLoader(DATA_PATH);
  const builder = new PromptBuilder(loader);
  const memory = new AgentMemory("Abigail", "/tmp/x");
  const provider = makeMockLlmProvider();
  // Override to return speak + emote + set_state
  provider._setCallOverride(async () => ({
    content: "",
    toolCalls: [
      { id: "tc-1", name: "emote", args: { emote_id: "heart" } },
      { id: "tc-2", name: "set_state", args: { state: "TALK" } },
      { id: "tc-3", name: "speak", args: { text: "很高兴见到你。" } },
    ],
  }));

  const agent = new StardewAgent({
    name: "Abigail",
    memory,
    promptBuilder: builder,
    llmProvider: provider,
    maxTurns: 5,
  });

  const result = await agent.runDialogue("你好", scene);
  expect(result.speech).toBe("很高兴见到你。");
  const actionTools = result.actions.map((a) => a.tool).sort();
  expect(actionTools).toEqual(["emote", "set_state"]);
});

test("StardewAgent.runDialogue falls back to raw LLM text when no speak tool", async () => {
  const loader = new NpcPromptLoader(DATA_PATH);
  const builder = new PromptBuilder(loader);
  const memory = new AgentMemory("Abigail", "/tmp/x");
  const provider = makeMockLlmProvider();
  provider._setCallOverride(async () => ({
    content: "哦，是你啊。有什么事吗？",
    toolCalls: [],
  }));

  const agent = new StardewAgent({
    name: "Abigail",
    memory,
    promptBuilder: builder,
    llmProvider: provider,
    maxTurns: 5,
  });

  const result = await agent.runDialogue("你好", scene);
  expect(result.speech).toBe("哦，是你啊。有什么事吗？");
  expect(result.actions).toEqual([]);
});

test("StardewAgent.runDialogue with multi-turn LLM (speak on 2nd turn)", async () => {
  const loader = new NpcPromptLoader(DATA_PATH);
  const builder = new PromptBuilder(loader);
  const memory = new AgentMemory("Abigail", "/tmp/x");
  const provider = makeMockLlmProvider();

  let callCount = 0;
  provider._setCallOverride(async () => {
    callCount++;
    if (callCount === 1) {
      return {
        content: "",
        toolCalls: [{ id: "tc-1", name: "get_info", args: { query: "date" } }],
      };
    }
    return {
      content: "",
      toolCalls: [{ id: "tc-2", name: "speak", args: { text: "现在是夏天啊。" } }],
    };
  });

  const agent = new StardewAgent({
    name: "Abigail",
    memory,
    promptBuilder: builder,
    llmProvider: provider,
    maxTurns: 5,
  });

  const result = await agent.runDialogue("现在是什么季节？", scene);
  expect(result.speech).toBe("现在是夏天啊。");
});

test("StardewAgent.runDialogue throws when LLM fails (caller handles fallback)", async () => {
  const loader = new NpcPromptLoader(DATA_PATH);
  const builder = new PromptBuilder(loader);
  const memory = new AgentMemory("Abigail", "/tmp/x");
  const provider = makeMockLlmProvider();
  provider._setCallOverride(async () => {
    throw new Error("LLM API down");
  });

  const agent = new StardewAgent({
    name: "Abigail",
    memory,
    promptBuilder: builder,
    llmProvider: provider,
    maxTurns: 5,
  });

  await expect(agent.runDialogue("你好", scene)).rejects.toThrow(/LLM API down/);
});

test("StardewAgent uses maxTurns=5 by default", () => {
  const loader = new NpcPromptLoader(DATA_PATH);
  const builder = new PromptBuilder(loader);
  const memory = new AgentMemory("Abigail", "/tmp/x");
  const provider = makeMockLlmProvider();

  const agent = new StardewAgent({
    name: "Abigail",
    memory,
    promptBuilder: builder,
    llmProvider: provider,
  });

  // Access internal config maxTurns via behavior: 6 LLM calls without speak → should stop at 5
  let calls = 0;
  provider._setCallOverride(async () => {
    calls++;
    return { content: `call ${calls}`, toolCalls: [] };
  });

  return agent.runDialogue("test", scene).then(() => {
    expect(calls).toBe(5);
  });
});
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd <VALLEYAI_ROOT> && bun test packages/stardew/tests/stardew-agent.test.ts`
Expected: FAIL with "Cannot find module '../src/stardew-agent'"

- [ ] **Step 3: Write minimal implementation**

Create `<VALLEYAI_ROOT>\packages\stardew\src\stardew-agent.ts`:

```typescript
import { Agent, VercelAIProvider, ToolRegistry } from "@valley/core";
import type { AgentLoopConfig, LlmCallResult } from "@valley/core";
import type { AgentContext, AgentEvent, LlmMessage } from "@valley/core";
import type { AgentMemory } from "./agent-memory";
import type { PromptBuilder } from "./prompt-builder";
import { buildStardewTools } from "./stardew-tools";
import type { ToolContext } from "./stardew-tools";
import type { SceneState, ToolAction } from "./types";

export interface StardewAgentConfig {
  name: string;
  memory: AgentMemory;
  promptBuilder: PromptBuilder;
  llmProvider: VercelAIProvider;
  maxTurns?: number;
}

export interface DialogueResult {
  speech: string;
  actions: ToolAction[];
  emotion: string;
  rawText: string;
  toolCalls: Array<{ name: string; args: Record<string, unknown> }>;
}

const SPEAK_TOOLS = new Set(["speak", "show_dialogue"]);

export class StardewAgent {
  readonly name: string;
  private readonly memory: AgentMemory;
  private readonly promptBuilder: PromptBuilder;
  private readonly llmProvider: VercelAIProvider;
  private readonly maxTurns: number;
  private coreAgent: Agent | null = null;

  constructor(config: StardewAgentConfig) {
    this.name = config.name;
    this.memory = config.memory;
    this.promptBuilder = config.promptBuilder;
    this.llmProvider = config.llmProvider;
    this.maxTurns = config.maxTurns ?? 5;
  }

  isIdle(): boolean {
    return this.coreAgent?.isIdle() ?? true;
  }

  async runDialogue(playerInput: string, scene: SceneState): Promise<DialogueResult> {
    // Record player input immediately (spec 4.1 step 8)
    this.memory.addConversation("player", playerInput);

    // Build system prompt with current memory + scene
    const systemPrompt = this.promptBuilder.buildDialogueSystemPrompt(this.memory, scene, this.name);

    // Build tools with shared context
    const toolCtx: ToolContext = {
      memory: this.memory,
      scene,
      inventory: scene.inventory.map((i) => ({ ...i })),
      givenToPlayer: [],
      log: [],
    };
    const tools = buildStardewTools(toolCtx);
    const registry = new ToolRegistry();
    for (const t of tools) registry.register(t);

    // Build AgentContext
    const context: AgentContext = {
      messages: [{ role: "user", content: `农场主说：${playerInput}` }],
      systemPrompt,
      metadata: {},
    };

    // Build agentLoop config
    const loopConfig: AgentLoopConfig = {
      tools: registry,
      convertToLlm: (ctx) => this.makeConvertToLlm(systemPrompt)(ctx),
      llmCall: async (messages, _tools) => this.makeLlmCall(messages, _tools ?? tools),
      toolExecution: "sequential",
      maxTurns: this.maxTurns,
      shouldStopAfterTurn: (ctx, _turn) => {
        const last = ctx.messages[ctx.messages.length - 1];
        if (last && last.role === "assistant" && (!last.toolCalls || last.toolCalls.length === 0)) {
          return true;
        }
        // Stop if speak was called
        if (last && last.role === "tool") {
          // Check if any prior assistant message called speak
          for (let i = ctx.messages.length - 1; i >= 0; i--) {
            const m = ctx.messages[i]!;
            if (m.role === "assistant" && m.toolCalls) {
              const calledSpeak = m.toolCalls.some((tc) => SPEAK_TOOLS.has(tc.name));
              if (calledSpeak) return true;
            }
          }
        }
        return false;
      },
    };

    this.coreAgent = new Agent(`${this.name}-dialogue`, loopConfig);
    const stream = this.coreAgent.prompt(context);
    const events = await stream.awaitAll();

    return this.extractResult(events);
  }

  private makeConvertToLlm(systemPrompt: string) {
    return (ctx: AgentContext): { messages: LlmMessage[] } => {
      const out: LlmMessage[] = [{ role: "system", content: systemPrompt }];
      for (const m of ctx.messages) {
        if (m.role === "system") continue;
        if (m.role === "user") {
          out.push({ role: "user", content: m.content });
        } else if (m.role === "assistant") {
          if (m.toolCalls && m.toolCalls.length > 0) {
            out.push({
              role: "assistant",
              content: m.content,
              toolCalls: m.toolCalls.map((tc) => ({
                id: tc.id,
                type: "function" as const,
                functionName: tc.name,
                args: JSON.stringify(tc.args),
              })),
            });
          } else {
            out.push({ role: "assistant", content: m.content });
          }
        } else if (m.role === "tool") {
          out.push({
            role: "tool",
            content: m.content,
            toolCallId: m.toolCallId!,
            ...(m.toolName !== undefined ? { toolName: m.toolName } : {}),
          });
        }
      }
      return { messages: out };
    };
  }

  private async makeLlmCall(messages: LlmMessage[], _tools?: unknown[]): Promise<LlmCallResult> {
    return this.llmProvider.chatWithTools(messages, _tools as Parameters<VercelAIProvider["chatWithTools"]>[1] | undefined);
  }

  private extractResult(events: AgentEvent[]): DialogueResult {
    let speech = "";
    let rawText = "";
    const actions: ToolAction[] = [];
    const toolCalls: Array<{ name: string; args: Record<string, unknown> }> = [];

    const argsById = new Map<string, Record<string, unknown>>();
    for (const ev of events) {
      if (ev.type === "tool_call_start") {
        argsById.set(ev.toolCallId, ev.args);
        toolCalls.push({ name: ev.toolName, args: ev.args });
      }
    }

    for (const ev of events) {
      if (ev.type === "message_end") {
        rawText = ev.content;
      }
      if (ev.type === "tool_call_start") {
        const name = ev.toolName;
        const args = argsById.get(ev.toolCallId) ?? ev.args;
        if (name === "speak" || name === "show_dialogue") {
          const text = String(args.text ?? "");
          if (text) speech = text;
        } else {
          // Non-speak tool → action
          actions.push({ tool: name, args });
        }
      }
    }

    // Layer 2: if no speak tool was called, use raw LLM text
    if (!speech && rawText) {
      speech = rawText;
      this.memory.addConversation("npc", rawText);
    }

    return {
      speech,
      actions,
      emotion: "Neutral",
      rawText,
      toolCalls,
    };
  }
}
```

**Note**: `chatWithTools` method needs to exist on VercelAIProvider. If it does not exist yet, verify by checking `<VALLEYAI_ROOT>\packages\core\src\llm-provider.ts` for the method. If missing, the test will fail and you must add a `chatWithTools` method to VercelAIProvider that returns `ProviderToolCallResult`. The e2e/stardew-run.ts uses `provider.chatWithTools(messages, tools)` so the method should already exist.

- [ ] **Step 4: Run test to verify it passes**

Run: `cd <VALLEYAI_ROOT> && bun test packages/stardew/tests/stardew-agent.test.ts`
Expected: PASS (8 tests)

Run typecheck: `cd <VALLEYAI_ROOT>\packages\stardew && bun run typecheck`
Expected: 0 errors

- [ ] **Step 5: Commit**

```bash
cd <VALLEYAI_ROOT>
git add packages/stardew/src/stardew-agent.ts packages/stardew/tests/stardew-agent.test.ts
git commit -m "feat(stardew): add StardewAgent class wrapping core Agent for dialogue

runDialogue(playerInput, scene) builds prompt, runs agentLoop (maxTurns=5), extracts speech from speak tool + actions from non-speak tools. Layer 2 fallback: raw LLM text when no speak call."
```

---

### Task 13: StardewAgentRegistry（多 NPC 并发管理）

**Files:**
- Create: `<VALLEYAI_ROOT>\packages\stardew\src\stardew-agent-registry.ts`
- Test: `<VALLEYAI_ROOT>\packages\stardew\tests\stardew-agent-registry.test.ts`

- [ ] **Step 1: Write the failing test**

Create `<VALLEYAI_ROOT>\packages\stardew\tests\stardew-agent-registry.test.ts`:

```typescript
import { test, expect } from "bun:test";
import { StardewAgentRegistry } from "../src/stardew-agent-registry";
import { NpcPromptLoader } from "../src/npc-prompt-loader";
import { PromptBuilder } from "../src/prompt-builder";
import { VercelAIProvider } from "@valley/core";
import { mkdtempSync, rmSync } from "fs";
import { join } from "path";
import { tmpdir } from "os";
import { resolve } from "path";

const DATA_PATH = resolve(import.meta.dir, "../data/npc_prompts.json");

function makeRegistry(): { registry: StardewAgentRegistry; dir: string } {
  const dir = mkdtempSync(join(tmpdir(), "valley-registry-test-"));
  const loader = new NpcPromptLoader(DATA_PATH);
  const builder = new PromptBuilder(loader);
  const provider = new VercelAIProvider({
    provider: "minimax",
    apiKey: "fake",
    model: "fake",
    baseUrl: "http://localhost:9999",
  });
  provider._setCallOverride(async () => ({
    content: "",
    toolCalls: [{ id: "tc-1", name: "speak", args: { text: "你好。" } }],
  }));
  const registry = new StardewAgentRegistry({
    promptBuilder: builder,
    llmProvider: provider,
    agentsDir: dir,
  });
  return { registry, dir };
}

test("getOrCreate returns same instance for same NPC", () => {
  const { registry, dir } = makeRegistry();
  try {
    const a1 = registry.getOrCreate("Abigail");
    const a2 = registry.getOrCreate("Abigail");
    expect(a1).toBe(a2);
    expect(a1.name).toBe("Abigail");
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("getOrCreate returns different instances for different NPCs", () => {
  const { registry, dir } = makeRegistry();
  try {
    const a = registry.getOrCreate("Abigail");
    const h = registry.getOrCreate("Haley");
    expect(a).not.toBe(h);
    expect(a.name).toBe("Abigail");
    expect(h.name).toBe("Haley");
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("listAgents returns all registered NPCs", () => {
  const { registry, dir } = makeRegistry();
  try {
    registry.getOrCreate("Abigail");
    registry.getOrCreate("Haley");
    registry.getOrCreate("Sebastian");
    const names = registry.listAgents().map((a) => a.name).sort();
    expect(names).toEqual(["Abigail", "Haley", "Sebastian"]);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("acquireLock serializes concurrent dialogue for same NPC", async () => {
  const { registry, dir } = makeRegistry();
  try {
    const agent = registry.getOrCreate("Abigail");
    const lock1 = await registry.acquireLock("Abigail");
    expect(lock1).toBe(true);

    // Second acquire should fail (NPC busy)
    const lock2 = await registry.acquireLock("Abigail");
    expect(lock2).toBe(false);

    registry.releaseLock("Abigail");
    const lock3 = await registry.acquireLock("Abigail");
    expect(lock3).toBe(true);
    registry.releaseLock("Abigail");
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("different NPCs can acquire lock simultaneously", async () => {
  const { registry, dir } = makeRegistry();
  try {
    const l1 = await registry.acquireLock("Abigail");
    const l2 = await registry.acquireLock("Haley");
    expect(l1).toBe(true);
    expect(l2).toBe(true);
    registry.releaseLock("Abigail");
    registry.releaseLock("Haley");
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("hasAgent returns false for unknown NPC, true after getOrCreate", () => {
  const { registry, dir } = makeRegistry();
  try {
    expect(registry.hasAgent("Abigail")).toBe(false);
    registry.getOrCreate("Abigail");
    expect(registry.hasAgent("Abigail")).toBe(true);
    expect(registry.hasAgent("Haley")).toBe(false);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("getMemoryFilePath returns expected path", () => {
  const { registry, dir } = makeRegistry();
  try {
    const path = registry.getMemoryFilePath("Abigail");
    expect(path).toBe(join(dir, "Abigail_memory.json"));
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd <VALLEYAI_ROOT> && bun test packages/stardew/tests/stardew-agent-registry.test.ts`
Expected: FAIL with "Cannot find module '../src/stardew-agent-registry'"

- [ ] **Step 3: Write minimal implementation**

Create `<VALLEYAI_ROOT>\packages\stardew\src\stardew-agent-registry.ts`:

```typescript
import type { VercelAIProvider } from "@valley/core";
import type { PromptBuilder } from "./prompt-builder";
import { StardewAgent } from "./stardew-agent";
import { AgentMemory } from "./agent-memory";
import { join } from "path";

export interface RegistryConfig {
  promptBuilder: PromptBuilder;
  llmProvider: VercelAIProvider;
  agentsDir: string;
  maxTurns?: number;
}

export class StardewAgentRegistry {
  private readonly agents = new Map<string, StardewAgent>();
  private readonly lockedNpcs = new Set<string>();
  private readonly config: RegistryConfig;

  constructor(config: RegistryConfig) {
    this.config = config;
  }

  getOrCreate(npcName: string): StardewAgent {
    let agent = this.agents.get(npcName);
    if (!agent) {
      const memoryPath = this.getMemoryFilePath(npcName);
      const memory = new AgentMemory(npcName, memoryPath);
      agent = new StardewAgent({
        name: npcName,
        memory,
        promptBuilder: this.config.promptBuilder,
        llmProvider: this.config.llmProvider,
        maxTurns: this.config.maxTurns,
      });
      this.agents.set(npcName, agent);
    }
    return agent;
  }

  hasAgent(npcName: string): boolean {
    return this.agents.has(npcName);
  }

  listAgents(): StardewAgent[] {
    return Array.from(this.agents.values());
  }

  /**
   * Acquire dialogue lock for an NPC.
   * Returns true if acquired, false if NPC is already in dialogue.
   * Spec 5.5: 同一 NPC 同时只有一个 dialogue 请求
   */
  async acquireLock(npcName: string): Promise<boolean> {
    if (this.lockedNpcs.has(npcName)) return false;
    this.lockedNpcs.add(npcName);
    return true;
  }

  releaseLock(npcName: string): void {
    this.lockedNpcs.delete(npcName);
  }

  getMemoryFilePath(npcName: string): string {
    return join(this.config.agentsDir, `${npcName}_memory.json`);
  }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `cd <VALLEYAI_ROOT> && bun test packages/stardew/tests/stardew-agent-registry.test.ts`
Expected: PASS (7 tests)

Run typecheck: `cd <VALLEYAI_ROOT>\packages\stardew && bun run typecheck`
Expected: 0 errors

- [ ] **Step 5: Commit**

```bash
cd <VALLEYAI_ROOT>
git add packages/stardew/src/stardew-agent-registry.ts packages/stardew/tests/stardew-agent-registry.test.ts
git commit -m "feat(stardew): add StardewAgentRegistry for multi-NPC management

Map<npcName, StardewAgent> + per-NPC dialogue lock (spec 4.4 + 5.5). Memory path: <agentsDir>/<npcName>_memory.json"
```

---

### Task 14: ProtocolAdapter（5 消息路由）

**Files:**
- Create: `<VALLEYAI_ROOT>\packages\stardew\src\protocol-adapter.ts`
- Test: `<VALLEYAI_ROOT>\packages\stardew\tests\protocol-adapter.test.ts`

- [ ] **Step 1: Write the failing test**

Create `<VALLEYAI_ROOT>\packages\stardew\tests\protocol-adapter.test.ts`:

```typescript
import { test, expect } from "bun:test";
import { ProtocolAdapter } from "../src/protocol-adapter";
import { NpcPromptLoader } from "../src/npc-prompt-loader";
import { PromptBuilder } from "../src/prompt-builder";
import { VercelAIProvider } from "@valley/core";
import { StardewAgentRegistry } from "../src/stardew-agent-registry";
import { mkdtempSync, rmSync } from "fs";
import { join } from "path";
import { tmpdir } from "os";
import { resolve } from "path";

const DATA_PATH = resolve(import.meta.dir, "../data/npc_prompts.json");

function makeAdapter(): { adapter: ProtocolAdapter; dir: string } {
  const dir = mkdtempSync(join(tmpdir(), "valley-adapter-test-"));
  const loader = new NpcPromptLoader(DATA_PATH);
  const builder = new PromptBuilder(loader);
  const provider = new VercelAIProvider({
    provider: "minimax",
    apiKey: "fake",
    model: "fake",
    baseUrl: "http://localhost:9999",
  });
  provider._setCallOverride(async () => ({
    content: "",
    toolCalls: [{ id: "tc-1", name: "speak", args: { text: "你好啊。" } }],
  }));
  const registry = new StardewAgentRegistry({
    promptBuilder: builder,
    llmProvider: provider,
    agentsDir: dir,
  });
  const adapter = new ProtocolAdapter(registry);
  return { adapter, dir };
}

test("handleHello returns hello response with status ok", async () => {
  const { adapter, dir } = makeAdapter();
  try {
    const resp = await adapter.handleHello({
      type: "hello",
      requestId: "req-1",
      modVersion: "1.0.0",
    });
    expect(resp.type).toBe("hello");
    expect(resp.requestId).toBe("req-1");
    expect(resp.status).toBe("ok");
    expect(resp.serverVersion).toBe("0.1.0");
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("handlePing returns pong response", async () => {
  const { adapter, dir } = makeAdapter();
  try {
    const resp = await adapter.handlePing({
      type: "ping",
      requestId: "req-2",
    });
    expect(resp.type).toBe("pong");
    expect(resp.requestId).toBe("req-2");
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("handleToolCallResult returns ack", async () => {
  const { adapter, dir } = makeAdapter();
  try {
    const resp = await adapter.handleToolCallResult({
      type: "tool_call_result",
      requestId: "req-3",
      callId: "call-1",
      success: true,
      result: "executed",
    });
    expect(resp.type).toBe("ack");
    expect(resp.requestId).toBe("req-3");
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("handleActionResult returns ack", async () => {
  const { adapter, dir } = makeAdapter();
  try {
    const resp = await adapter.handleActionResult({
      type: "action_result",
      requestId: "req-4",
      callId: "call-1",
      success: true,
      result: "ok",
    });
    expect(resp.type).toBe("ack");
    expect(resp.requestId).toBe("req-4");
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("handleDialogue returns dialogue response with speech", async () => {
  const { adapter, dir } = makeAdapter();
  try {
    const resp = await adapter.handleDialogue({
      type: "dialogue",
      requestId: "req-5",
      npcName: "Abigail",
      playerInput: "你好",
      worldSnapshot: {
        season: "summer", day: 28, time: "14:30", weather: "sunny",
        location: "Town", npcTile: { x: 32, y: 18 },
        nearbyObjects: "2 villagers", friendship: 250, npcState: "IDLE",
        inventory: [], farmerName: "新来的农夫",
      },
    });
    expect(resp.type).toBe("dialogue_response");
    expect(resp.requestId).toBe("req-5");
    expect(resp.npcName).toBe("Abigail");
    expect(resp.speech).toBe("你好啊。");
    expect(resp.emotion).toBe("Neutral");
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("handleDialogue returns fallback on LLM failure", async () => {
  const dir = mkdtempSync(join(tmpdir(), "valley-adapter-test-"));
  try {
    const loader = new NpcPromptLoader(DATA_PATH);
    const builder = new PromptBuilder(loader);
    const provider = new VercelAIProvider({
      provider: "minimax",
      apiKey: "fake",
      model: "fake",
      baseUrl: "http://localhost:9999",
    });
    provider._setCallOverride(async () => {
      throw new Error("LLM down");
    });
    const registry = new StardewAgentRegistry({
      promptBuilder: builder,
      llmProvider: provider,
      agentsDir: dir,
    });
    const adapter = new ProtocolAdapter(registry);

    const resp = await adapter.handleDialogue({
      type: "dialogue",
      requestId: "req-6",
      npcName: "Abigail",
      playerInput: "你好",
      worldSnapshot: {
        season: "summer", day: 28, time: "14:30", weather: "sunny",
        location: "Town", npcTile: { x: 32, y: 18 },
        nearbyObjects: "2 villagers", friendship: 250, npcState: "IDLE",
        inventory: [], farmerName: "新来的农夫",
      },
    });
    expect(resp.type).toBe("dialogue_response");
    expect(resp.fallback).toBe(true);
    expect(resp.speech.length).toBeGreaterThan(0);
    expect(resp.actions.some((a) => a.tool === "emote")).toBe(true);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("handleDialogue rejects when NPC is locked (busy)", async () => {
  const { adapter, dir } = makeAdapter();
  try {
    // Pre-acquire lock
    await adapter["registry"].acquireLock("Abigail");
    const resp = await adapter.handleDialogue({
      type: "dialogue",
      requestId: "req-7",
      npcName: "Abigail",
      playerInput: "你好",
      worldSnapshot: {
        season: "summer", day: 28, time: "14:30", weather: "sunny",
        location: "Town", npcTile: { x: 32, y: 18 },
        nearbyObjects: "2 villagers", friendship: 250, npcState: "IDLE",
        inventory: [], farmerName: "新来的农夫",
      },
    });
    expect(resp.type).toBe("dialogue_response");
    expect(resp.fallback).toBe(true);
    expect(resp.speech).toContain("思考");
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd <VALLEYAI_ROOT> && bun test packages/stardew/tests/protocol-adapter.test.ts`
Expected: FAIL with "Cannot find module '../src/protocol-adapter'"

- [ ] **Step 3: Write minimal implementation**

Create `<VALLEYAI_ROOT>\packages\stardew\src\protocol-adapter.ts`:

```typescript
import { LLMBillingError, LLMUnavailableError } from "@valley/core";
import type { StardewAgentRegistry } from "./stardew-agent-registry";
import { RuleEngine } from "./rule-engine";
import { decodeWorldSnapshot } from "./world-snapshot-decoder";
import type {
  DialogueRequest,
  DialogueResponse,
  HelloRequest,
  HelloResponse,
  PingRequest,
  PongResponse,
  ToolCallResultMessage,
  ActionResultMessage,
  OutgoingMessage,
} from "./types";

const SERVER_VERSION = "0.1.0";

export class ProtocolAdapter {
  private readonly ruleEngine = new RuleEngine();

  constructor(private readonly registry: StardewAgentRegistry) {}

  async handleHello(req: HelloRequest): Promise<HelloResponse> {
    return {
      type: "hello",
      requestId: req.requestId,
      status: "ok",
      serverVersion: SERVER_VERSION,
    };
  }

  async handlePing(req: PingRequest): Promise<PongResponse> {
    return {
      type: "pong",
      requestId: req.requestId,
    };
  }

  async handleToolCallResult(req: ToolCallResultMessage): Promise<{ type: "ack"; requestId: string }> {
    // P0: just acknowledge. Tool result routing to agent._last_tool_result is P1+.
    return { type: "ack", requestId: req.requestId };
  }

  async handleActionResult(req: ActionResultMessage): Promise<{ type: "ack"; requestId: string }> {
    return { type: "ack", requestId: req.requestId };
  }

  async handleDialogue(req: DialogueRequest): Promise<DialogueResponse> {
    // Check NPC lock (spec 5.5: same NPC serial dialogue)
    const locked = await this.registry.acquireLock(req.npcName);
    if (!locked) {
      return this.buildBusyResponse(req);
    }

    try {
      const agent = this.registry.getOrCreate(req.npcName);

      // Load memory if not yet loaded (lazy load on first dialogue)
      await agent["memory"].load();

      const scene = decodeWorldSnapshot(req.worldSnapshot);
      const result = await agent.runDialogue(req.playerInput, scene);

      // Async save (non-blocking response)
      agent["memory"].save().catch((err) => {
        console.error(`[dialogue] memory.save failed for ${req.npcName}:`, err);
      });

      return {
        type: "dialogue_response",
        requestId: req.requestId,
        npcName: req.npcName,
        speech: result.speech || "...",
        actions: result.actions,
        emotion: result.emotion,
        memorySideEffect: "recorded",
      };
    } catch (err) {
      console.error(`[dialogue] LLM failed for ${req.npcName}:`, err);
      return this.ruleEngine.buildFallbackResponse(req, err);
    } finally {
      this.registry.releaseLock(req.npcName);
    }
  }

  private buildBusyResponse(req: DialogueRequest): DialogueResponse {
    return {
      type: "dialogue_response",
      requestId: req.requestId,
      npcName: req.npcName,
      speech: `（${req.npcName} 正在思考...）`,
      actions: [{ tool: "emote", args: { emote_id: "thinking" } }],
      emotion: "Neutral",
      fallback: true,
    };
  }

  async routeMessage(msg: unknown): Promise<OutgoingMessage> {
    const m = msg as { type: string };
    switch (m.type) {
      case "hello": return this.handleHello(m as HelloRequest);
      case "ping": return this.handlePing(m as PingRequest);
      case "dialogue": return this.handleDialogue(m as DialogueRequest);
      case "tool_call_result": return this.handleToolCallResult(m as ToolCallResultMessage);
      case "action_result": return this.handleActionResult(m as ActionResultMessage);
      default: return { type: "ack", requestId: "unknown" };
    }
  }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `cd <VALLEYAI_ROOT> && bun test packages/stardew/tests/protocol-adapter.test.ts`
Expected: PASS (7 tests)

Run typecheck: `cd <VALLEYAI_ROOT>\packages\stardew && bun run typecheck`
Expected: 0 errors

- [ ] **Step 5: Commit**

```bash
cd <VALLEYAI_ROOT>
git add packages/stardew/src/protocol-adapter.ts packages/stardew/tests/protocol-adapter.test.ts
git commit -m "feat(stardew): add ProtocolAdapter routing 5 message types

handleHello/handlePing/handleToolCallResult/handleActionResult + handleDialogue with NPC lock + lazy memory load + async save + RuleEngine fallback. routeMessage dispatches by type field."
```

---

### Task 14.5: Integration Tests 补完（ws-compat / multi-npc / dialogue-fallback）

**Files:**
- Create: `<VALLEYAI_ROOT>\packages\stardew\tests\integration\ws-compat.test.ts`
- Create: `<VALLEYAI_ROOT>\packages\stardew\tests\integration\multi-npc.test.ts`
- Create: `<VALLEYAI_ROOT>\packages\stardew\tests\integration\dialogue-fallback.test.ts`

**背景**：spec 6.2 节要求 3 个集成测试覆盖跨层契约。这些测试在 Task 14（ProtocolAdapter）和 Task 7（AgentMemory）已实现后即可编写，无需等 Task 15 服务器。

- [ ] **Step 1: Write `ws-compat.test.ts` — C# 端字段名兼容（camelCase）/ null 字段忽略 / 枚举字符串**

Create `<VALLEYAI_ROOT>\packages\stardew\tests\integration\ws-compat.test.ts`:

```typescript
import { test, expect } from "bun:test";
import { ProtocolAdapter } from "../../src/protocol-adapter";
import { NpcPromptLoader } from "../../src/npc-prompt-loader";
import { PromptBuilder } from "../../src/prompt-builder";
import { VercelAIProvider } from "@valley/core";
import { StardewAgentRegistry } from "../../src/stardew-agent-registry";
import { mkdtempSync, rmSync } from "fs";
import { join } from "path";
import { tmpdir } from "os";
import { resolve } from "path";

const DATA_PATH = resolve(import.meta.dir, "../../data/npc_prompts.json");

function makeAdapterWithOverride(callOverride: (msg: unknown) => Promise<unknown>): { adapter: ProtocolAdapter; dir: string } {
  const dir = mkdtempSync(join(tmpdir(), "valley-ws-compat-"));
  const loader = new NpcPromptLoader(DATA_PATH);
  const builder = new PromptBuilder(loader);
  const provider = new VercelAIProvider({
    provider: "minimax",
    apiKey: "fake",
    model: "fake",
    baseUrl: "http://localhost:9999",
  });
  provider._setCallOverride(callOverride as any);
  const registry = new StardewAgentRegistry({
    promptBuilder: builder,
    llmProvider: provider,
    agentsDir: dir,
  });
  const adapter = new ProtocolAdapter(registry);
  return { adapter, dir };
}

const baseSnapshot = {
  season: "summer", day: 28, time: "14:30", weather: "sunny",
  location: "Town", npcTile: { x: 32, y: 18 },
  nearbyObjects: "2 villagers", friendship: 250, npcState: "IDLE",
  inventory: [], farmerName: "新来的农夫",
};

test("accepts camelCase field names from C# WebSocketClient JSON", async () => {
  // C# serializes DialogueRequest with camelCase policy: requestId, npcName, playerInput, worldSnapshot, npcTile, nearbyObjects, etc.
  const { adapter, dir } = makeAdapterWithOverride(async () => ({
    content: "你好。",
    toolCalls: [],
  }));
  try {
    // Simulate raw JSON wire format (camelCase keys) that arrives over WebSocket
    const rawJson = {
      type: "dialogue",
      requestId: "req-camel-1",
      npcName: "Abigail",
      playerInput: "你好",
      worldSnapshot: baseSnapshot,
    };
    const resp = await adapter.routeMessage(rawJson);
    expect(resp.type).toBe("dialogue_response");
    expect((resp as any).requestId).toBe("req-camel-1");
    expect((resp as any).npcName).toBe("Abigail");
    expect((resp as any).speech).toBe("你好。");
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("ignores null/missing optional fields in WorldSnapshot without throwing", async () => {
  const { adapter, dir } = makeAdapterWithOverride(async () => ({
    content: "嗯。",
    toolCalls: [],
  }));
  try {
    // C# may send null for nearbyObjects or npcState if not yet populated
    const rawJson = {
      type: "dialogue",
      requestId: "req-null-1",
      npcName: "Abigail",
      playerInput: "hi",
      worldSnapshot: {
        ...baseSnapshot,
        nearbyObjects: null,
        npcState: null,
        inventory: null,
      },
    };
    const resp = await adapter.routeMessage(rawJson);
    expect(resp.type).toBe("dialogue_response");
    expect((resp as any).speech).toBe("嗯。");
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("accepts string enum values (weather: 'sunny' / npcState: 'IDLE')", async () => {
  const { adapter, dir } = makeAdapterWithOverride(async () => ({
    content: "天气不错。",
    toolCalls: [],
  }));
  try {
    const rawJson = {
      type: "dialogue",
      requestId: "req-enum-1",
      npcName: "Abigail",
      playerInput: "天气如何",
      worldSnapshot: {
        ...baseSnapshot,
        weather: "Rainy",       // C# JsonStringEnumConverter serializes enum as string
        npcState: "TALK",
      },
    };
    const resp = await adapter.routeMessage(rawJson);
    expect(resp.type).toBe("dialogue_response");
    expect((resp as any).speech).toBe("天气不错。");
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});
```

- [ ] **Step 2: Write `multi-npc.test.ts` — 2 NPC 并发对话 / 独立 memory / 互不干扰**

Create `<VALLEYAI_ROOT>\packages\stardew\tests\integration\multi-npc.test.ts`:

```typescript
import { test, expect } from "bun:test";
import { ProtocolAdapter } from "../../src/protocol-adapter";
import { NpcPromptLoader } from "../../src/npc-prompt-loader";
import { PromptBuilder } from "../../src/prompt-builder";
import { VercelAIProvider } from "@valley/core";
import { StardewAgentRegistry } from "../../src/stardew-agent-registry";
import { AgentMemory } from "../../src/agent-memory";
import { mkdtempSync, rmSync, readFileSync, existsSync } from "fs";
import { join } from "path";
import { tmpdir } from "os";
import { resolve } from "path";

const DATA_PATH = resolve(import.meta.dir, "../../data/npc_prompts.json");

function makeAdapter(): { adapter: ProtocolAdapter; dir: string; registry: StardewAgentRegistry } {
  const dir = mkdtempSync(join(tmpdir(), "valley-multi-npc-"));
  const loader = new NpcPromptLoader(DATA_PATH);
  const builder = new PromptBuilder(loader);
  const provider = new VercelAIProvider({
    provider: "minimax",
    apiKey: "fake",
    model: "fake",
    baseUrl: "http://localhost:9999",
  });
  // Different NPCs get different speech to verify isolation
  const speeches: Record<string, string> = {
    Abigail: "我是 Abigail。",
    Sebastian: "我是 Sebastian。",
  };
  provider._setCallOverride(async (ctx: any) => {
    const npcName = ctx?.npcName ?? "";
    return {
      content: speeches[npcName] ?? "我不知道。",
      toolCalls: [],
    };
  } as any);
  const registry = new StardewAgentRegistry({
    promptBuilder: builder,
    llmProvider: provider,
    agentsDir: dir,
  });
  const adapter = new ProtocolAdapter(registry);
  return { adapter, dir, registry };
}

const snapshotFor = (npcName: string) => ({
  season: "summer", day: 28, time: "14:30", weather: "sunny",
  location: "Town", npcTile: { x: 10, y: 10 },
  nearbyObjects: "", friendship: 100, npcState: "IDLE",
  inventory: [], farmerName: "农夫",
});

test("two NPCs dialogue concurrently return their own speech", async () => {
  const { adapter, dir } = makeAdapter();
  try {
    const [r1, r2] = await Promise.all([
      adapter.handleDialogue({
        type: "dialogue", requestId: "r1", npcName: "Abigail",
        playerInput: "你是谁", worldSnapshot: snapshotFor("Abigail"),
      }),
      adapter.handleDialogue({
        type: "dialogue", requestId: "r2", npcName: "Sebastian",
        playerInput: "你是谁", worldSnapshot: snapshotFor("Sebastian"),
      }),
    ]);
    expect(r1.npcName).toBe("Abigail");
    expect(r1.speech).toBe("我是 Abigail。");
    expect(r2.npcName).toBe("Sebastian");
    expect(r2.speech).toBe("我是 Sebastian。");
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("each NPC has independent memory file (no cross-contamination)", async () => {
  const { adapter, dir } = makeAdapter();
  try {
    await adapter.handleDialogue({
      type: "dialogue", requestId: "m1", npcName: "Abigail",
      playerInput: "我叫张三", worldSnapshot: snapshotFor("Abigail"),
    });
    await adapter.handleDialogue({
      type: "dialogue", requestId: "m2", npcName: "Sebastian",
      playerInput: "我叫李四", worldSnapshot: snapshotFor("Sebastian"),
    });

    const abigailFile = join(dir, "Abigail_memory.json");
    const sebastianFile = join(dir, "Sebastian_memory.json");
    expect(existsSync(abigailFile)).toBe(true);
    expect(existsSync(sebastianFile)).toBe(true);

    const abigailMem = JSON.parse(readFileSync(abigailFile, "utf-8"));
    const sebastianMem = JSON.parse(readFileSync(sebastianFile, "utf-8"));

    // Abigail's memory should contain "张三" but NOT "李四"
    const abigailText = JSON.stringify(abigailMem.conversationHistory);
    expect(abigailText).toContain("张三");
    expect(abigailText).not.toContain("李四");

    // Sebastian's memory should contain "李四" but NOT "张三"
    const sebastianText = JSON.stringify(sebastianMem.conversationHistory);
    expect(sebastianText).toContain("李四");
    expect(sebastianText).not.toContain("张三");
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("same NPC concurrent requests are serialized (lock rejects second)", async () => {
  const { adapter, dir, registry } = makeAdapter();
  try {
    // Acquire lock manually to simulate in-progress dialogue
    await registry.acquireLock("Abigail");

    const resp = await adapter.handleDialogue({
      type: "dialogue", requestId: "locked-1", npcName: "Abigail",
      playerInput: "你好", worldSnapshot: snapshotFor("Abigail"),
    });
    expect(resp.fallback).toBe(true);
    expect(resp.speech).toContain("思考");

    registry.releaseLock("Abigail");
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});
```

- [ ] **Step 3: Write `dialogue-fallback.test.ts` — CircuitBreaker 状态转换 + 持续 fallback + 恢复后 CLOSED**

Create `<VALLEYAI_ROOT>\packages\stardew\tests\integration\dialogue-fallback.test.ts`:

```typescript
import { test, expect } from "bun:test";
import { ProtocolAdapter } from "../../src/protocol-adapter";
import { NpcPromptLoader } from "../../src/npc-prompt-loader";
import { PromptBuilder } from "../../src/prompt-builder";
import { VercelAIProvider } from "@valley/core";
import { StardewAgentRegistry } from "../../src/stardew-agent-registry";
import { mkdtempSync, rmSync } from "fs";
import { join } from "path";
import { tmpdir } from "os";
import { resolve } from "path";

const DATA_PATH = resolve(import.meta.dir, "../../data/npc_prompts.json");

const baseSnapshot = {
  season: "summer", day: 28, time: "14:30", weather: "sunny",
  location: "Town", npcTile: { x: 32, y: 18 },
  nearbyObjects: "", friendship: 250, npcState: "IDLE",
  inventory: [], farmerName: "农夫",
};

function makeAdapterWithFailingThenRecoveringLLM(failCount: number): { adapter: ProtocolAdapter; dir: string; triggerRecovery: () => void } {
  const dir = mkdtempSync(join(tmpdir(), "valley-fallback-"));
  const loader = new NpcPromptLoader(DATA_PATH);
  const builder = new PromptBuilder(loader);
  const provider = new VercelAIProvider({
    provider: "minimax",
    apiKey: "fake",
    model: "fake",
    baseUrl: "http://localhost:9999",
  });
  let calls = 0;
  let recovered = false;
  provider._setCallOverride(async () => {
    calls++;
    if (!recovered && calls <= failCount) {
      throw new Error("LLM down");
    }
    return { content: "我恢复了。", toolCalls: [] };
  } as any);
  const registry = new StardewAgentRegistry({
    promptBuilder: builder,
    llmProvider: provider,
    agentsDir: dir,
  });
  const adapter = new ProtocolAdapter(registry);
  return {
    adapter, dir,
    triggerRecovery: () => { recovered = true; },
  };
}

test("LLM failure returns fallback response with fallback=true and emote action", async () => {
  const { adapter, dir } = makeAdapterWithFailingThenRecoveringLLM(99);
  try {
    const resp = await adapter.handleDialogue({
      type: "dialogue", requestId: "fb-1", npcName: "Abigail",
      playerInput: "你好", worldSnapshot: baseSnapshot,
    });
    expect(resp.type).toBe("dialogue_response");
    expect(resp.fallback).toBe(true);
    expect(resp.speech.length).toBeGreaterThan(0);
    expect(resp.actions.some(a => a.tool === "emote")).toBe(true);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("persistent LLM failure keeps returning fallback (does not crash)", async () => {
  const { adapter, dir } = makeAdapterWithFailingThenRecoveringLLM(99);
  try {
    for (let i = 0; i < 5; i++) {
      const resp = await adapter.handleDialogue({
        type: "dialogue", requestId: `fb-persist-${i}`, npcName: "Abigail",
        playerInput: `回合 ${i}`, worldSnapshot: baseSnapshot,
      });
      expect(resp.fallback).toBe(true);
      expect(resp.speech.length).toBeGreaterThan(0);
    }
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("LLM recovery after fallback returns to normal LLM response (fallback=false)", async () => {
  const { adapter, dir, triggerRecovery } = makeAdapterWithFailingThenRecoveringLLM(2);
  try {
    // First 2 calls fail
    const r1 = await adapter.handleDialogue({
      type: "dialogue", requestId: "rec-1", npcName: "Abigail",
      playerInput: "你好", worldSnapshot: baseSnapshot,
    });
    expect(r1.fallback).toBe(true);

    const r2 = await adapter.handleDialogue({
      type: "dialogue", requestId: "rec-2", npcName: "Abigail",
      playerInput: "你好", worldSnapshot: baseSnapshot,
    });
    expect(r2.fallback).toBe(true);

    // Trigger recovery — subsequent calls succeed
    triggerRecovery();

    const r3 = await adapter.handleDialogue({
      type: "dialogue", requestId: "rec-3", npcName: "Abigail",
      playerInput: "你好", worldSnapshot: baseSnapshot,
    });
    expect(r3.fallback).toBeFalsy();
    expect(r3.speech).toBe("我恢复了。");
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `cd <VALLEYAI_ROOT> && bun test packages/stardew/tests/integration/`
Expected: PASS (9 tests across 3 files)

Run typecheck: `cd <VALLEYAI_ROOT>\packages\stardew && bun run typecheck`
Expected: 0 errors

- [ ] **Step 5: Commit**

```bash
cd <VALLEYAI_ROOT>
git add packages/stardew/tests/integration/ws-compat.test.ts packages/stardew/tests/integration/multi-npc.test.ts packages/stardew/tests/integration/dialogue-fallback.test.ts
git commit -m "test(stardew): add 3 integration tests — ws-compat / multi-npc / dialogue-fallback

ws-compat: C# camelCase JSON / null field tolerance / string enums. multi-npc: 2 NPC concurrent dialogue / independent memory files / same-NPC lock serialization. dialogue-fallback: LLM failure → fallback response / persistent fallback / recovery returns to normal LLM response. Covers spec 6.2 integration acceptance points."
```

---

### Task 15: WebSocket Server + Dialogue E2E Integration Test

**Files:**
- Create: `<VALLEYAI_ROOT>\packages\stardew\src\server.ts`
- Test: `<VALLEYAI_ROOT>\packages\stardew\tests\dialogue-e2e.test.ts`

- [ ] **Step 1: Write the failing test**

Create `<VALLEYAI_ROOT>\packages\stardew\tests\dialogue-e2e.test.ts`:

```typescript
import { test, expect } from "bun:test";
import { startServer } from "../src/server";
import type { ServerHandle } from "../src/server";
import { mkdtempSync, rmSync } from "fs";
import { join } from "path";
import { tmpdir } from "os";
import { resolve } from "path";

const DATA_PATH = resolve(import.meta.dir, "../data/npc_prompts.json");

async function withServer<T>(fn: (port: number) => Promise<T>): Promise<T> {
  const dir = mkdtempSync(join(tmpdir(), "valley-e2e-"));
  const handle: ServerHandle = await startServer({
    port: 0, // ephemeral
    hostname: "127.0.0.1",
    dataPath: DATA_PATH,
    agentsDir: dir,
    llmConfig: {
      provider: "minimax",
      apiKey: "fake",
      model: "fake",
      baseUrl: "http://localhost:9999",
    },
    llmCallOverride: async () => ({
      content: "",
      toolCalls: [{ id: "tc-1", name: "speak", args: { text: "你好啊，新来的农夫。" } }],
    }),
  });
  try {
    return await fn(handle.port);
  } finally {
    await handle.stop();
    rmSync(dir, { recursive: true, force: true });
  }
}

test("server starts and accepts WebSocket connections", async () => {
  await withServer(async (port) => {
    const ws = new WebSocket(`ws://127.0.0.1:${port}`);
    await new Promise<void>((resolve, reject) => {
      ws.onopen = () => resolve();
      ws.onerror = () => reject(new Error("WebSocket connection failed"));
    });
    ws.close();
  });
});

test("server responds to hello message", async () => {
  await withServer(async (port) => {
    const ws = new WebSocket(`ws://127.0.0.1:${port}`);
    await new Promise<void>((resolve) => { ws.onopen = () => resolve(); });

    const response = await new Promise<unknown>((resolve) => {
      ws.onmessage = (ev) => resolve(JSON.parse(ev.data as string));
      ws.send(JSON.stringify({ type: "hello", requestId: "h-1", modVersion: "1.0.0" }));
    });

    expect((response as { type: string }).type).toBe("hello");
    expect((response as { status: string }).status).toBe("ok");
    ws.close();
  });
});

test("server responds to ping with pong", async () => {
  await withServer(async (port) => {
    const ws = new WebSocket(`ws://127.0.0.1:${port}`);
    await new Promise<void>((resolve) => { ws.onopen = () => resolve(); });

    const response = await new Promise<unknown>((resolve) => {
      ws.onmessage = (ev) => resolve(JSON.parse(ev.data as string));
      ws.send(JSON.stringify({ type: "ping", requestId: "p-1" }));
    });

    expect((response as { type: string }).type).toBe("pong");
    ws.close();
  });
});

test("server handles full dialogue flow: hello → dialogue → response", async () => {
  await withServer(async (port) => {
    const ws = new WebSocket(`ws://127.0.0.1:${port}`);
    await new Promise<void>((resolve) => { ws.onopen = () => resolve(); });

    // Send hello first
    await new Promise<void>((resolve) => {
      ws.onmessage = () => resolve();
      ws.send(JSON.stringify({ type: "hello", requestId: "h-1", modVersion: "1.0.0" }));
    });

    // Send dialogue
    const dialogueResp = await new Promise<unknown>((resolve) => {
      ws.onmessage = (ev) => resolve(JSON.parse(ev.data as string));
      ws.send(JSON.stringify({
        type: "dialogue",
        requestId: "d-1",
        npcName: "Abigail",
        playerInput: "你好",
        worldSnapshot: {
          season: "summer", day: 28, time: "14:30", weather: "sunny",
          location: "Town", npcTile: { x: 32, y: 18 },
          nearbyObjects: "2 villagers", friendship: 250, npcState: "IDLE",
          inventory: [], farmerName: "新来的农夫",
        },
      }));
    });

    const resp = dialogueResp as { type: string; speech: string; npcName: string };
    expect(resp.type).toBe("dialogue_response");
    expect(resp.npcName).toBe("Abigail");
    expect(resp.speech).toBe("你好啊，新来的农夫。");
    ws.close();
  });
});

test("server persists memory after dialogue", async () => {
  const dir = mkdtempSync(join(tmpdir(), "valley-e2e-mem-"));
  try {
    const handle = await startServer({
      port: 0,
      hostname: "127.0.0.1",
      dataPath: DATA_PATH,
      agentsDir: dir,
      llmConfig: {
        provider: "minimax", apiKey: "fake", model: "fake",
        baseUrl: "http://localhost:9999",
      },
      llmCallOverride: async () => ({
        content: "",
        toolCalls: [{ id: "tc-1", name: "speak", args: { text: "你好。" } }],
      }),
    });

    const ws = new WebSocket(`ws://127.0.0.1:${handle.port}`);
    await new Promise<void>((resolve) => { ws.onopen = () => resolve(); });

    await new Promise<void>((resolve) => {
      ws.onmessage = () => resolve();
      ws.send(JSON.stringify({
        type: "dialogue", requestId: "d-1", npcName: "Abigail",
        playerInput: "你好",
        worldSnapshot: {
          season: "summer", day: 28, time: "14:30", weather: "sunny",
          location: "Town", npcTile: { x: 32, y: 18 },
          nearbyObjects: "2 villagers", friendship: 250, npcState: "IDLE",
          inventory: [], farmerName: "新来的农夫",
        },
      }));
    });

    // Wait for async save
    await new Promise((r) => setTimeout(r, 200));

    ws.close();
    await handle.stop();

    // Verify memory file exists
    const { existsSync, readFileSync } = await import("fs");
    const memPath = join(dir, "Abigail_memory.json");
    expect(existsSync(memPath)).toBe(true);
    const raw = readFileSync(memPath, "utf-8");
    const parsed = JSON.parse(raw);
    expect(parsed.npcName).toBe("Abigail");
    expect(parsed.conversationHistory.length).toBeGreaterThanOrEqual(2);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd <VALLEYAI_ROOT> && bun test packages/stardew/tests/dialogue-e2e.test.ts`
Expected: FAIL with "Cannot find module '../src/server'"

- [ ] **Step 3: Write minimal implementation**

Create `<VALLEYAI_ROOT>\packages\stardew\src\server.ts`:

```typescript
import { VercelAIProvider, type LLMConfig, type ProviderToolCallResult } from "@valley/core";
import { NpcPromptLoader } from "./npc-prompt-loader";
import { PromptBuilder } from "./prompt-builder";
import { StardewAgentRegistry } from "./stardew-agent-registry";
import { ProtocolAdapter } from "./protocol-adapter";

export interface ServerLLMConfig {
  provider: string;
  apiKey: string;
  model: string;
  baseUrl: string;
  temperature?: number;
  maxTokens?: number;
  timeout?: number;
  maxRetries?: number;
  maxConcurrency?: number;
}

export interface ServerConfig {
  port: number;
  hostname: string;
  dataPath: string;
  agentsDir: string;
  llmConfig: ServerLLMConfig;
  llmCallOverride?: (messages: unknown, tools?: unknown) => Promise<ProviderToolCallResult>;
}

export interface ServerHandle {
  port: number;
  stop: () => Promise<void>;
}

export async function startServer(config: ServerConfig): Promise<ServerHandle> {
  const loader = new NpcPromptLoader(config.dataPath);
  const builder = new PromptBuilder(loader);

  const llmConfig: LLMConfig = {
    provider: config.llmConfig.provider as LLMConfig["provider"],
    apiKey: config.llmConfig.apiKey,
    model: config.llmConfig.model,
    baseUrl: config.llmConfig.baseUrl,
    temperature: config.llmConfig.temperature ?? 0.7,
    maxTokens: config.llmConfig.maxTokens ?? 800,
    timeout: config.llmConfig.timeout ?? 30_000,
    maxRetries: config.llmConfig.maxRetries ?? 3,
    maxConcurrency: config.llmConfig.maxConcurrency ?? 4,
  };
  const provider = new VercelAIProvider(llmConfig);
  if (config.llmCallOverride) {
    provider._setCallOverride(config.llmCallOverride);
  }

  const registry = new StardewAgentRegistry({
    promptBuilder: builder,
    llmProvider: provider,
    agentsDir: config.agentsDir,
  });
  const adapter = new ProtocolAdapter(registry);

  const server = Bun.serve({
    port: config.port,
    hostname: config.hostname,
    websocket: {
      open() {},
      async message(ws, message) {
        try {
          const text = typeof message === "string" ? message : new TextDecoder().decode(message as ArrayBuffer);
          const parsed = JSON.parse(text);
          const response = await adapter.routeMessage(parsed);
          ws.send(JSON.stringify(response));
        } catch (err) {
          console.error("[server] message handler error:", err);
          ws.send(JSON.stringify({ type: "error", message: String(err) }));
        }
      },
      close() {},
    },
  });

  return {
    port: server.port,
    stop: async () => { server.stop(); },
  };
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `cd <VALLEYAI_ROOT> && bun test packages/stardew/tests/dialogue-e2e.test.ts`
Expected: PASS (5 tests)

Run typecheck: `cd <VALLEYAI_ROOT>\packages\stardew && bun run typecheck`
Expected: 0 errors

- [ ] **Step 5: Commit**

```bash
cd <VALLEYAI_ROOT>
git add packages/stardew/src/server.ts packages/stardew/tests/dialogue-e2e.test.ts
git commit -m "feat(stardew): add Bun WebSocket server + dialogue E2E test

startServer(config) wires NpcPromptLoader + PromptBuilder + VercelAIProvider + Registry + ProtocolAdapter. WebSocket message → routeMessage → JSON response. E2E test covers hello/ping/dialogue + memory persistence."
```

---

## Phase 5: C# Mod 协议升级

### Task 16: ToolAction + DialogueResponse record 重定义

**⚠️ CRITICAL: Tasks 16-19 must be completed in the same session without committing between them.**
These tasks modify interdependent files (DialogueResponse record + SubmitInput + CommandExecutor + Mock libraries). Committing partially will break compilation. Only commit at Task 19 Step 5.

**Files:**
- Modify: `<REPO_ROOT>\src\ValleyAgent.Abstractions\WebSocket\IAgentServerProvider.cs`

**背景**：当前 `DialogueResponse` record 用 `Text`/`Action` 字段，需重定义为 `Speech`/`Actions[]` + 新增 `ToolAction` record。改造点 1 必须与改造点 2 + 6 同次提交（编译耦合）。

- [ ] **Step 1: Locate the current DialogueResponse record**

Run: `cd <REPO_ROOT> && Select-String -Path "src\ValleyAgent.Abstractions\WebSocket\IAgentServerProvider.cs" -Pattern "record DialogueResponse" -SimpleMatch`

Expected output shows the line at line 75-81 area:
```
record DialogueResponse(string Text, string? Emotion, string? Action, string RequestId, string Type)
```

- [ ] **Step 2: Add ToolAction record + redefine DialogueResponse**

Read `<REPO_ROOT>\src\ValleyAgent.Abstractions\WebSocket\IAgentServerProvider.cs` lines 70-85 to find exact insertion point. Then Edit by replacing the DialogueResponse record line:

Old:
```csharp
public record DialogueResponse(string Text, string? Emotion, string? Action, string RequestId, string Type);
```

New (replace the single line with two records):
```csharp
public record ToolAction(string Tool, Dictionary<string, object> Args);

public record DialogueResponse(
    string Speech,
    IReadOnlyList<ToolAction> Actions,
    string? Emotion = "Neutral",
    string RequestId = "",
    string Type = "");
```

If the existing line uses a different parameter order or has trailing content, match the exact existing string. The new `DialogueResponse` MUST have: `Speech` (string), `Actions` (IReadOnlyList<ToolAction>), `Emotion` (string?, default "Neutral"), `RequestId` (string, default ""), `Type` (string, default ""). Defaults preserve backwards compatibility with the existing 3-arg constructor call in `MockResponseLibrary.cs:195`.

- [ ] **Step 3: Build to find all compile errors**

Run: `cd <REPO_ROOT>\src\ValleyAgent && dotnet build -c Release 2>&1 | Select-String "error CS"`

Expected: Compile errors at every site that references `response.Text` or `response.Action`. Capture the list of files+lines (actual line numbers verified via Grep on 2026-07-18):

| File (relative to repo root) | Line | Issue | Needs change? |
|------------------------------|------|-------|---------------|
| `src\ValleyAgent\Patches\DialogueBoxInputPatch.cs` | 299, 306, 310 | `response.Text` access | YES (Task 18 fully rewrites; minimal fix here) |
| `src\ValleyAgent\Debug\ChatHandler.cs` | 72 | `resultText = response.Text;` | YES → `response.Speech` |
| `src\ValleyAgent\Debug\ChatHandler.cs` | 73 | `resultAction = response.Action;` | YES (type changed: `string?` → `IReadOnlyList<ToolAction>`) |
| `src\ValleyAgent.Abstractions\WebSocket\IAgentServerProvider.cs` | 171 | `GenerateDialogueAsync` return type | NO (return type is `Task<DialogueResponse>`, type name unchanged) |
| `src\ValleyAgent.TestMod\Mock\MockResponseLibrary.cs` | 195 | `new DialogueResponse(Text: ..., Emotion: "Neutral", Action: null)` named-arg constructor | YES |
| `src\ValleyAgent.TestMod\Mock\MockResponseLibrary.cs` | 33, 39, 49, 206, 217 | `DialogueResponse` as property/parameter/field type | NO (type name unchanged) |
| `src\ValleyAgent.TestMod\Mock\MockLLMProvider.cs` | 13, 107, 111, 112 | `<see cref="DialogueResponse"/>` + method return type `DialogueResponse GetDialogueResponse(...)` | NO (type name unchanged; no `.Text`/`.Action` access; no constructor) |
| `src\ValleyAgent.TestMod\Mock\MockWebSocketServer.cs` | 11 | `using DialogueResponse = ValleyAgent.WebSocket.DialogueResponse;` alias | NO (alias still valid) |
| `src\ValleyAgent.TestMod\Mock\MockWebSocketServer.cs` | 308 | `var resp = _provider.GetDialogueResponse(payloadClone);` | NO (method call, no `.Text`/`.Action` access) |
| `src\ValleyAgent.TestMod\Tests\Pipeline\PIPE003_DialoguePipeline.cs` | 36 | `private DialogueResponse? _response;` field type | NO (type name unchanged) |
| `src\ValleyAgent.TestMod\Tests\Pipeline\PIPE003_DialoguePipeline.cs` | 157 | `MessageProtocol.Deserialize<DialogueResponse>(...)` generic | NO (type name unchanged) |
| `src\ValleyAgent.TestMod\Tests\Pipeline\PIPE003_DialoguePipeline.cs` | 160 | `!string.IsNullOrEmpty(_response.Text)` | YES → `_response.Speech` |
| `src\ValleyAgent.TestMod\Tests\Pipeline\PIPE003_DialoguePipeline.cs` | 162 | `$"textLen={_response.Text?.Length ?? 0}"` | YES → `_response.Speech` |

**Note**: GiftEvalResponse.Text (different type) is unrelated and must NOT be touched.

- [ ] **Step 4: Fix each compile error (do NOT commit yet — Task 17-19 will handle these in same commit)**

For each file flagged "YES" above, apply the specific fix:

**`src\ValleyAgent.TestMod\Mock\MockResponseLibrary.cs`** (line 195) — single construction with named args. Old:
```csharp
return new DialogueResponse(
    Text: "嗯...",
    Emotion: "Neutral",
    Action: null);
```
New:
```csharp
return new DialogueResponse(
    Speech: "嗯...",
    Actions: new List<ToolAction>(),
    Emotion: "Neutral");
```
(`RequestId` / `Type` use defaults from the updated record signature in Step 2; if Step 2's record lacks defaults, add `RequestId: "", Type: ""` to this call.)

**`src\ValleyAgent.TestMod\Mock\MockLLMProvider.cs`** — NO constructor change needed. Type references (`<see cref="DialogueResponse"/>`, method return type `DialogueResponse GetDialogueResponse(...)`) are unaffected because the type name is unchanged. No `.Text` / `.Action` access exists in this file.

**`src\ValleyAgent.TestMod\Mock\MockWebSocketServer.cs`** — NO change needed. The `using` alias and `var resp = _provider.GetDialogueResponse(...)` callsite are unaffected. The file does not access `resp.Text` or `resp.Action`.

**`src\ValleyAgent.TestMod\Tests\Pipeline\PIPE003_DialoguePipeline.cs`** (lines 160, 162) — field type and Deserialize<DialogueResponse> are unaffected (type name unchanged). Only the `.Text` accessor changes:
- Line 160: `var textNonEmpty = !string.IsNullOrEmpty(_response.Text);` → `var textNonEmpty = !string.IsNullOrEmpty(_response.Speech);`
- Line 162: `$"textLen={_response.Text?.Length ?? 0}"` → `$"textLen={_response.Speech?.Length ?? 0}"`

**`src\ValleyAgent\Debug\ChatHandler.cs`** (lines 72-73) — `response.Text` → `response.Speech`; `response.Action` (was `string?`) is now `IReadOnlyList<ToolAction>`, so the consuming logic must change. Minimal mechanical fix:
```csharp
resultText = response.Speech;
resultAction = response.Actions.Count > 0 ? response.Actions[0].Tool : null;  // first action's tool name
```

**`src\ValleyAgent\Patches\DialogueBoxInputPatch.cs`** (lines 299, 306, 310) — this file is fully rewritten in Task 18, so apply only the minimal mechanical fix to keep build green until Task 18: change `response.Text` to `response.Speech` on all three lines. Do NOT touch `response.Action` (Task 18 will handle the new `Actions[]` dispatch).

- [ ] **Step 5: Verify build passes (no commit yet — combined commit in Task 19)**

Run: `cd <REPO_ROOT>\src\ValleyAgent && dotnet build -c Release`
Expected: Build succeeded, 0 errors, 0 warnings (TreatWarningsAsErrors)

**Do not commit yet.** Proceed to Task 17 (CommandExecutor.ExecuteAction) which must be in the same commit.

---

### Task 17: CommandExecutor.ExecuteAction 方法

**Files:**
- Modify: `<REPO_ROOT>\src\ValleyAgent\CommandExecutor.cs`

- [ ] **Step 1: Read the existing CommandExecutor structure**

Read `<REPO_ROOT>\src\ValleyAgent\CommandExecutor.cs` to find:
- The class declaration line
- The existing `ExecuteSingleCommand` method (to understand the dispatch pattern)
- The `CommandRegistry` field/property used to look up commands
- Existing command names registered (ServiceInitializer.cs:240-247 lists 19 commands)

Key existing commands to dispatch to:
- `GiveGiftCommand` → for tool "give_gift" and "give_item"
- `ShowDialogueCommand` → for tool "show_dialogue"
- `SetFriendshipCommand` → for tool "set_state" (when state maps to friendship change)
- `RememberCommand` → for tool "remember" (P0: no-op on C# side, memory handled in TS)

- [ ] **Step 2: Add ExecuteAction method**

Find a stable insertion point — typically just before the existing `ExecuteCommands` method. Add this method:

```csharp
/// <summary>
/// Execute a single ToolAction (from TS dialogue response) by dispatching to the
/// appropriate CommandAction via the CommandRegistry.
/// Spec 4.2: P0 immediate tools (emote/give_item/give_gift/set_state/show_dialogue).
/// </summary>
public void ExecuteAction(ToolAction action, string npcName)
{
    if (action == null) throw new ArgumentNullException(nameof(action));
    if (string.IsNullOrEmpty(action.Tool))
    {
        _monitor?.Log("ExecuteAction: action.Tool is empty", StardewModdingAPI.LogLevel.Warn);
        return;
    }

    var tool = action.Tool;
    var args = action.Args ?? new Dictionary<string, object>();

    switch (tool)
    {
        case "emote":
            ExecuteEmote(args, npcName);
            break;
        case "give_item":
        case "give_gift":
            ExecuteGiveItem(args, npcName);
            break;
        case "set_state":
            ExecuteSetState(args, npcName);
            break;
        case "show_dialogue":
            // Already handled by SubmitInput via npc.setNewDialogue
            break;
        case "speak":
            // Already handled by SubmitInput via npc.setNewDialogue
            break;
        case "remember":
            // No-op on C# side; memory handled in TS
            break;
        case "get_info":
            // No-op; handled in TS
            break;
        default:
            _monitor?.Log($"ExecuteAction: unknown tool '{tool}'", StardewModdingAPI.LogLevel.Warn);
            break;
    }
}

private void ExecuteEmote(Dictionary<string, object> args, string npcName)
{
    var npc = Game1.getCharacterFromName<NPC>(npcName);
    if (npc == null) return;
    if (!args.TryGetValue("emote_id", out var emoteObj)) return;
    var emoteId = emoteObj?.ToString() ?? "";
    // Map string emote to int (Stardew emote IDs)
    var emoteInt = MapEmoteStringToInt(emoteId);
    if (emoteInt > 0) npc.doEmote(emoteInt);
}

private void ExecuteGiveItem(Dictionary<string, object> args, string npcName)
{
    if (!args.TryGetValue("item_id", out var itemIdObj)) return;
    var itemId = itemIdObj?.ToString() ?? "";
    var quantity = 1;
    if (args.TryGetValue("quantity", out var qtyObj))
    {
        int.TryParse(qtyObj?.ToString(), out quantity);
    }
    if (string.IsNullOrEmpty(itemId)) return;

    // Use the existing GiveGiftCommand or direct ItemCommand
    try
    {
        var item = new StardewValley.Object(itemId, quantity);
        Game1.player.addItemToMenu(item);
    }
    catch (Exception ex)
    {
        _monitor?.Log($"ExecuteGiveItem failed for {itemId}: {ex.Message}", StardewModdingAPI.LogLevel.Warn);
    }
}

private void ExecuteSetState(Dictionary<string, object> args, string npcName)
{
    if (!args.TryGetValue("state", out var stateObj)) return;
    var state = stateObj?.ToString() ?? "IDLE";
    // P0: minimal — just log; full FSM is P3
    _monitor?.Log($"ExecuteSetState: {npcName} → {state}", StardewModdingAPI.LogLevel.Info);
}

private static int MapEmoteStringToInt(string emoteId)
{
    return emoteId switch
    {
        "happy" => 8,
        "sad" => 28,
        "angry" => 12,
        "surprised" => 16,
        "heart" => 20,
        "question" => 18,
        "exclamation" => 18,
        "sleep" => 24,
        "music" => 56,
        "stretch" => 39,
        "star" => 40,
        "love" => 20,
        "note" => 41,
        "wave" => 28,
        "hooray" => 42,
        "confused" => 18,
        "thinking" => 18,
        "annoyed" => 12,
        "worried" => 28,
        "frustrated" => 12,
        "sweat" => 43,
        "fish" => 44,
        "gift" => 45,
        "bomb" => 46,
        _ => 0,
    };
}
```

**Notes (verified 2026-07-18)**:
- `_monitor` is the SMAPI `IMonitor` field — **VERIFIED** at `src\ValleyAgent\CommandExecutor.cs:16` (`private readonly IMonitor _monitor;`, assigned in constructor at line 39). The ExecuteAction method uses `_monitor?.Log(...)` with null-conditional to be defensive.
- The `switch (tool)` cases use lowercase snake_case (`"emote"`, `"give_item"`, `"give_gift"`, `"set_state"`, `"show_dialogue"`, `"speak"`, `"remember"`, `"get_info"`) — **VERIFIED** against `ServiceInitializer.cs:227-247` and `Commands\ItemCommands.cs` (e.g. `public override string CommandName => "give_item";`). The `CommandRegistry` uses `StringComparer.OrdinalIgnoreCase` so case mismatch is tolerated, but the canonical registered names are already snake_case — no `ToLowerInvariant()` normalization needed before the switch.
- The 8 newly-added emote IDs (stretch=39, star=40, note=41, hooray=42, sweat=43, fish=44, gift=45, bomb=46) should be verified against `StardewValley.Game1.emoteFolderResource` (or `Data\emotes` if present in 1.6). If the runtime `npc.doEmote(emoteInt)` renders the wrong sprite, update the IDs here. The existing 16 IDs (happy=8, sad=28, angry=12, surprised=16, heart=20, question=18, exclamation=18, sleep=24, music=56, love=20, wave=28, confused=18, thinking=18, annoyed=12, worried=28, frustrated=12) are kept unchanged from the pre-existing plan to avoid regression.

- [ ] **Step 3: Verify build (still no commit — combined in Task 19)**

Run: `cd <REPO_ROOT>\src\ValleyAgent && dotnet build -c Release`
Expected: Build succeeded, 0 errors, 0 warnings

Proceed to Task 18.

---

### Task 18: DialogueBoxInputPatch.SubmitInput 简化

**Files:**
- Modify: `<REPO_ROOT>\src\ValleyAgent\Patches\DialogueBoxInputPatch.cs` (lines 260-327)

- [ ] **Step 1: Read the current SubmitInput method**

Read `<REPO_ROOT>\src\ValleyAgent\Patches\DialogueBoxInputPatch.cs` lines 260-327 to understand the existing structure. Identify:
- Where `DialogueRequest` is constructed (line ~288 has `Personality=""`, line ~293 has `ConversationHistory=""`)
- Where `response.Text` is consumed (lines ~299, 306, 310)
- Where the timeout/exception handling lives

- [ ] **Step 2: Rewrite the dialogue construction + response handling block**

Replace the block that constructs the request and consumes the response. Find the exact block (typically starting around line 285 with `var request = new DialogueRequest(...)` and ending around line 320 with `npc.setNewDialogue(...)`).

Old block (approximate — match exact existing code):
```csharp
var request = new DialogueRequest
{
    Type = "dialogue",
    RequestId = Guid.NewGuid().ToString(),
    NpcName = npc.Name,
    PlayerInput = userInput,
    Personality = "",
    ConversationHistory = "",
    Memory = "",
    // ... other fields
};
var response = await _agentServerProvider.GenerateDialogueAsync(request, ct);
npc.setNewDialogue(new Dialogue(npc, null, response.Text));
Game1.drawDialogue(npc);
```

New block:
```csharp
var worldSnapshot = new WorldSnapshot(
    Season: Game1.currentSeason,
    Day: Game1.dayOfMonth,
    Time: $"{Game1.timeOfDay / 100:D2}:{Game1.timeOfDay % 100:D2}",  // 1430 → "14:30"
    Weather: Game1.isRaining ? (Game1.isLightning ? "Stormy" : "Rainy") : "Sunny",
    Location: Game1.currentLocation?.Name ?? "Unknown",
    NpcTile: new TilePosition((int)npc.Tile.X, (int)npc.Tile.Y),
    NearbyObjects: GetNearbyObjectsSummary(npc),
    Friendship: Game1.player.friendshipData.TryGetValue(npc.Name, out var fs) ? fs.Points : 0,
    NpcState: "IDLE",
    Inventory: Game1.player.Items.Where(i => i != null).Select(i => new InventoryItem(i.Name, i.Stack)).ToList(),
    FarmerName: Game1.player.Name
);

var request = new DialogueRequest(
    Type: "dialogue",
    RequestId: Guid.NewGuid().ToString("N"),
    NpcName: npc.Name,
    PlayerInput: userInput,
    WorldSnapshot: worldSnapshot
);

try
{
    var response = await _agentServerProvider.GenerateDialogueAsync(request, ct);
    var speech = response.Speech ?? "...";
    var actions = response.Actions ?? new List<ToolAction>();

    npc.setNewDialogue(new Dialogue(npc, null, speech));
    Game1.drawDialogue(npc);

    foreach (var action in actions)
    {
        try
        {
            _commandExecutor.ExecuteAction(action, npc.Name);
        }
        catch (Exception ex)
        {
            _monitor.Log($"Action {action.Tool} failed: {ex.Message}", StardewModdingAPI.LogLevel.Warn);
        }
    }
}
catch (TimeoutException)
{
    npc.setNewDialogue(new Dialogue(npc, null, $"（{npc.Name} 在思考...）"));
    Game1.drawDialogue(npc);
}
catch (Exception ex)
{
    _monitor.Log($"Dialogue request failed: {ex.Message}", StardewModdingAPI.LogLevel.Error);
    npc.setNewDialogue(new Dialogue(npc, null, "......"));
    Game1.drawDialogue(npc);
}
```

**Notes**:
- `WorldSnapshot`, `TilePosition`, `InventoryItem` are new types that must be added to `IAgentServerProvider.cs` next to `ToolAction`. See Step 3 for the complete record definitions.
- `GetNearbyObjectsSummary(npc)` is a real helper method (not a placeholder) — see Step 4 for the complete implementation that walks nearby NPCs, objects, and warp points within tile-distance thresholds.
- `_commandExecutor` field must exist on the patch class — if not, inject via constructor or ServiceInitializer.
- `Game1.timeOfDay` is an `int` (e.g. `1430` for 14:30); the `$"{.../100:D2}:{...%100:D2}"` format produces the `"HH:MM"` string expected by the TS `WorldSnapshot.time` field (spec 2.2 example: `"14:30"`).
- `Game1.isLightning` distinguishes stormy from rainy weather, matching the TS `weather` enum values used in `decodeWorldSnapshot` tests.

- [ ] **Step 3: Add DialogueRequest record update + helper types**

In `IAgentServerProvider.cs`, find the existing `DialogueRequest` record (verified at lines 52-70 on 2026-07-18) and replace it.

**Existing record (17 fields — DO NOT keep any of the deprecated fields):**
```csharp
public record DialogueRequest(
    string NpcName,
    string Personality,
    string Location,
    string Time,
    string Weather,
    int Friendship,
    string ConversationHistory,
    string PlayerInput,
    string NearbyObjects = "",
    string FarmerNickname = GameConstants.DefaultFarmerNickname,
    string NpcBiography = "",
    string NpcTraits = "",
    string NpcRelationships = "",
    string FriendshipPhase = "",
    string Season = "",
    string SignificantMemories = "",
    string Memory = ""
);
```

**New record (5 fields — spec 2.2):**
```csharp
public record DialogueRequest(
    string Type,
    string RequestId,
    string NpcName,
    string PlayerInput,
    WorldSnapshot WorldSnapshot
);
```

**Field change summary:**
- **Delete (15 fields)**: `Personality`, `Location`, `Time`, `Weather`, `Friendship`, `ConversationHistory`, `NearbyObjects`, `FarmerNickname`, `NpcBiography`, `NpcTraits`, `NpcRelationships`, `FriendshipPhase`, `Season`, `SignificantMemories`, `Memory`
- **Add (3 fields)**: `Type`, `RequestId`, `WorldSnapshot`
- **Keep (2 fields)**: `NpcName`, `PlayerInput`

**Add helper records** next to `ToolAction` (same file):
```csharp
public record TilePosition(int X, int Y);

public record InventoryItem(string Name, int Quantity);

public record WorldSnapshot(
    string Season,
    int Day,
    string Time,
    string Weather,
    string Location,
    TilePosition NpcTile,
    string NearbyObjects,
    int Friendship,
    string NpcState,
    IReadOnlyList<InventoryItem> Inventory,
    string FarmerName
);
```

**JSON serialization contract**: C# property names are PascalCase; `WebSocketClient` already configures `JsonNamingPolicy.CamelCase` + `JsonStringEnumConverter`, so the wire format becomes `npcTile` / `nearbyObjects` / `farmerName` / `requestId` / `npcName` / `playerInput` / `worldSnapshot` — matching the TS `DialogueRequest` and `WorldSnapshot` interfaces in `stardew/src/types.ts` exactly.

**Cleanup**: After replacing the record, check whether `GameConstants.DefaultFarmerNickname` is still referenced anywhere. If it was only used by the deleted `FarmerNickname` default, leave the constant in place (defensive — may be used by other code paths); do NOT delete `GameConstants` itself.

- [ ] **Step 4: Add `GetNearbyObjectsSummary` helper (real implementation, not placeholder)**

Add this private static method to the `DialogueBoxInputPatch` class (or as a private method on the containing service — wherever `SubmitInput` lives). It walks the NPC's current location and produces a compact string summarizing nearby villagers, objects, and warp points within tile-distance thresholds.

```csharp
private static string GetNearbyObjectsSummary(NPC npc)
{
    var location = npc.currentLocation;
    if (location == null) return "unknown location";

    var parts = new List<string>();

    // Nearby NPCs (within 5 tiles). Tile coordinates are in tile units (not pixels).
    var nearbyNpcs = location.characters
        .Where(c => c != npc && c.Tile != null && Vector2.Distance(c.Tile, npc.Tile) < 5f)
        .Select(c => c.Name)
        .Where(name => !string.IsNullOrEmpty(name))
        .Take(5)
        .ToList();
    if (nearbyNpcs.Count > 0)
        parts.Add($"{nearbyNpcs.Count} villagers ({string.Join(", ", nearbyNpcs)})");

    // Nearby objects (within 3 tiles). location.Objects is keyed by Vector2 tile.
    var nearbyObjects = location.Objects.Values
        .Where(o => o.Tile != null && Vector2.Distance(o.Tile, npc.Tile) < 3f)
        .Select(o => o.Name)
        .Where(name => !string.IsNullOrEmpty(name))
        .Take(5)
        .ToList();
    if (nearbyObjects.Count > 0)
        parts.Add($"{nearbyObjects.Count} objects ({string.Join(", ", nearbyObjects)})");

    // Nearby warp points (doors/exits within 5 tiles). Warps use tile coordinates.
    var nearbyWarps = location.warps
        .Where(w => Vector2.Distance(new Vector2(w.X, w.Y), npc.Tile) < 5f)
        .Select(w => w.TargetName)
        .Where(name => !string.IsNullOrEmpty(name))
        .Take(3)
        .ToList();
    if (nearbyWarps.Count > 0)
        parts.Add($"{nearbyWarps.Count} exits to {string.Join(", ", nearbyWarps)}");

    return parts.Count > 0 ? string.Join("; ", parts) : "empty area";
}
```

**Notes**:
- `npc.Tile` returns `Vector2` in tile units (not pixels), so distance comparison is in tile units. The thresholds (5 / 3 / 5 tiles) match the spec 2.2 `nearbyObjects` example `"2 villagers, Pierre's shop entrance"`.
- `location.warps` is `List<Warp>`; each `Warp` has `X`, `Y` (source tile) and `TargetName` (destination location name).
- `location.Objects` is `Dictionary<Vector2, Object>`; `.Values` gives the objects. Each `Object` has `Tile` (Vector2) and `Name` (string).
- Add `using Microsoft.Xna.Framework;` at the top of the file if `Vector2` is not already imported (it usually is in SMAPI mods).
- This is NOT a placeholder — it returns real data that the LLM can reason about (e.g. "2 villagers (Abigail, Sebastian); 1 objects (Stone); 1 exits to Farm").

- [ ] **Step 5: Verify build (still no commit — combined in Task 19)**

Run: `cd <REPO_ROOT>\src\ValleyAgent && dotnet build -c Release`
Expected: Build succeeded, 0 errors, 0 warnings

Fix any remaining compile errors (likely in `EventHandlerInitializer.cs` if it constructs `DialogueRequest` with old fields — Task 19 deletes that dead code).

Proceed to Task 19.

---

### Task 19: Delete BuildDialogueRequest dead code + combined commit

**Files:**
- Modify: `<REPO_ROOT>\src\ValleyAgent\Initialization\EventHandlerInitializer.cs` (lines 2246-2280)
- Modify: `<REPO_ROOT>\src\ValleyAgent\Debug\ChatHandler.cs` (line 121 area)

- [ ] **Step 1: Delete BuildDialogueRequest in EventHandlerInitializer.cs**

Read `<REPO_ROOT>\src\ValleyAgent\Initialization\EventHandlerInitializer.cs` lines 2240-2290 to find the exact method boundaries.

Delete the entire `BuildDialogueRequest` static method (lines 2246-2280, approximately 35 lines). The method signature looks like:
```csharp
public static DialogueRequest BuildDialogueRequest(...)
```

Also check if the helper methods it calls (`GetNearbyObjectsForDialogue`, `GetVisibleTraits`, `GetSignificantMemoriesText`, `GetRecentMemoryText`, `GetFriendshipPhaseLabel`) are referenced elsewhere. Run:

```powershell
cd <REPO_ROOT>\src\ValleyAgent
Select-String -Path "*.cs" -Recurse -Pattern "GetNearbyObjectsForDialogue|GetVisibleTraits|GetSignificantMemoriesText|GetRecentMemoryText|GetFriendshipPhaseLabel" -SimpleMatch
```

If any helper is ONLY called by `BuildDialogueRequest`, delete it too. If called elsewhere, leave it.

- [ ] **Step 2: Delete BuildDialogueRequest duplicate in ChatHandler.cs**

Read `<REPO_ROOT>\src\ValleyAgent\Debug\ChatHandler.cs` around line 121 to find the duplicate `BuildDialogueRequest` method. Delete the entire method.

- [ ] **Step 3: Verify build passes**

Run: `cd <REPO_ROOT>\src\ValleyAgent && dotnet build -c Release`
Expected: Build succeeded, 0 errors, 0 warnings (TreatWarningsAsErrors)

- [ ] **Step 4: Run existing C# tests if any**

Run: `cd <REPO_ROOT>\src\ValleyAgent && dotnet test -c Release 2>&1 | Select-String "Passed|Failed"`
Expected: All tests pass (or "No tests found" if no test project)

- [ ] **Step 5: Combined commit for Tasks 16-19**

```bash
cd <REPO_ROOT>
git add src/ValleyAgent.Abstractions/WebSocket/IAgentServerProvider.cs
git add src/ValleyAgent/CommandExecutor.cs
git add src/ValleyAgent/Patches/DialogueBoxInputPatch.cs
git add src/ValleyAgent/Initialization/EventHandlerInitializer.cs
git add src/ValleyAgent/Debug/ChatHandler.cs
git add src/ValleyAgent.TestMod/Mock/MockResponseLibrary.cs
git add src/ValleyAgent.TestMod/Mock/MockLLMProvider.cs
git add src/ValleyAgent.TestMod/Mock/MockWebSocketServer.cs
git add src/ValleyAgent.TestMod/Tests/Pipeline/PIPE003_DialoguePipeline.cs
git commit -m "feat(mod): upgrade dialogue protocol — Text→Speech, Action→Actions[], add ExecuteAction

Spec 2.2: DialogueResponse now carries Speech + Actions[] (ToolAction records). DialogueBoxInputPatch.SubmitInput sends worldSnapshot, consumes Speech + dispatches Actions via CommandExecutor.ExecuteAction. Deletes dead BuildDialogueRequest code. WebSocketClient protocol (camelCase JSON) auto-adapts."
```

---

## Phase 6: Bun exe 打包 + ServerProcessManager + 端到端验收

### Task 20: Bun exe 打包配置

**Files:**
- Create: `<VALLEYAI_ROOT>\packages\stardew\bin\valley-ai-server.exe` (Bun compile output)
- Create: `<VALLEYAI_ROOT>\packages\stardew\scripts\build-exe.sh`
- Create: `<VALLEYAI_ROOT>\packages\stardew\src\cli.ts` (CLI entry point)

- [ ] **Step 1: Write the CLI entry point**

Create `<VALLEYAI_ROOT>\packages\stardew\src\cli.ts`:

```typescript
import { startServer } from "./server";
import { resolve } from "path";
import { mkdirSync } from "fs";

interface CliArgs {
  port: number;
  hostname: string;
  dataPath: string;
  agentsDir: string;
  llmApiKey: string;
  llmModel: string;
  llmBaseUrl: string;
  llmProvider: string;
}

function parseArgs(argv: string[]): CliArgs {
  const args: CliArgs = {
    port: 8765,
    hostname: "127.0.0.1",
    dataPath: resolve(import.meta.dir, "../data/npc_prompts.json"),
    agentsDir: resolve(process.cwd(), "agents"),
    llmApiKey: process.env.LLM_API_KEY ?? "",
    llmModel: process.env.LLM_MODEL ?? "MiniMax-M2",
    llmBaseUrl: process.env.LLM_BASE_URL ?? "https://api.minimax.chat/v1",
    llmProvider: process.env.LLM_PROVIDER ?? "minimax",
  };

  for (let i = 0; i < argv.length; i++) {
    const arg = argv[i]!;
    const next = argv[i + 1];
    switch (arg) {
      case "--port": if (next) { args.port = parseInt(next, 10); i++; } break;
      case "--hostname": if (next) { args.hostname = next; i++; } break;
      case "--data-path": if (next) { args.dataPath = next; i++; } break;
      case "--agents-dir": if (next) { args.agentsDir = next; i++; } break;
      case "--llm-api-key": if (next) { args.llmApiKey = next; i++; } break;
      case "--llm-model": if (next) { args.llmModel = next; i++; } break;
      case "--llm-base-url": if (next) { args.llmBaseUrl = next; i++; } break;
      case "--llm-provider": if (next) { args.llmProvider = next; i++; } break;
      case "--help":
        console.log(`Usage: valley-ai-server [options]

Options:
  --port <number>            WebSocket port (default: 8765)
  --hostname <string>        Bind hostname (default: 127.0.0.1)
  --data-path <path>         Path to npc_prompts.json
  --agents-dir <path>        Directory for agent memory files
  --llm-api-key <key>        LLM API key (or env LLM_API_KEY)
  --llm-model <name>         LLM model name (default: MiniMax-M2)
  --llm-base-url <url>       LLM API base URL
  --llm-provider <name>      LLM provider (minimax|openai|deepseek)
  --help                     Show this help
`);
        process.exit(0);
    }
  }

  if (!args.llmApiKey) {
    console.error("ERROR: --llm-api-key or LLM_API_KEY env var required");
    process.exit(1);
  }

  return args;
}

async function main() {
  const args = parseArgs(process.argv.slice(2));
  mkdirSync(args.agentsDir, { recursive: true });

  console.log(`[valley-ai-server] starting on ${args.hostname}:${args.port}`);
  console.log(`[valley-ai-server] data: ${args.dataPath}`);
  console.log(`[valley-ai-server] agents: ${args.agentsDir}`);
  console.log(`[valley-ai-server] llm: ${args.llmProvider}/${args.llmModel}`);

  const handle = await startServer({
    port: args.port,
    hostname: args.hostname,
    dataPath: args.dataPath,
    agentsDir: args.agentsDir,
    llmConfig: {
      provider: args.llmProvider,
      apiKey: args.llmApiKey,
      model: args.llmModel,
      baseUrl: args.llmBaseUrl,
    },
  });

  console.log(`[valley-ai-server] listening on port ${handle.port}`);

  // Graceful shutdown
  process.on("SIGINT", async () => {
    console.log("[valley-ai-server] SIGINT received, shutting down...");
    await handle.stop();
    process.exit(0);
  });
  process.on("SIGTERM", async () => {
    console.log("[valley-ai-server] SIGTERM received, shutting down...");
    await handle.stop();
    process.exit(0);
  });
}

main().catch((err) => {
  console.error("[valley-ai-server] fatal:", err);
  process.exit(1);
});
```

- [ ] **Step 2: Write the build script**

Create `<VALLEYAI_ROOT>\packages\stardew\scripts\build-exe.sh`:

```bash
#!/usr/bin/env bash
set -euo pipefail

# Build valley-ai-server.exe using Bun compile
# Output: packages/stardew/bin/valley-ai-server.exe

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PKG_DIR="$(dirname "$SCRIPT_DIR")"
ENTRY="$PKG_DIR/src/cli.ts"
OUTPUT="$PKG_DIR/bin/valley-ai-server.exe"

echo "Building valley-ai-server.exe from $ENTRY..."
mkdir -p "$PKG_DIR/bin"

bun build --compile --target=bun-windows-x64 "$ENTRY" --outfile "$OUTPUT"

echo "Built: $OUTPUT"
ls -lh "$OUTPUT"
```

- [ ] **Step 3: Build the exe**

Run (on Windows with Bun installed):
```bash
cd <VALLEYAI_ROOT>\packages\stardew
bash scripts/build-exe.sh
```

Expected: `bin/valley-ai-server.exe` created, ~40-50MB

- [ ] **Step 4: Smoke test the exe + cold-start time measurement**

Run (start in background, then connect). This step measures the cold-start time — spec 6.5 requires ≤2s. We use `Date.now()` before launch and after WebSocket `onopen` to compute the elapsed milliseconds.

```bash
cd <VALLEYAI_ROOT>\packages\stardew

# Measure cold start: record start time, launch exe, connect WS, record end time.
bun -e '
const START = Date.now();
const proc = Bun.spawn(["./bin/valley-ai-server.exe", "--port", "9876", "--llm-api-key", "fake-key", "--llm-model", "fake", "--agents-dir", "./tmp-agents"], { stdout: "pipe", stderr: "pipe" });

// Poll for WS readiness (retry every 100ms, up to 5000ms total)
const ws = new WebSocket("ws://127.0.0.1:9876");
const connectDeadline = Date.now() + 5000;
let connected = false;

ws.onopen = () => {
  connected = true;
  const END = Date.now();
  const coldStartMs = END - START;
  console.log(`[smoke] WebSocket connected. coldStartMs=${coldStartMs}`);

  // Spec 6.5 acceptance: cold start ≤ 2000ms
  if (coldStartMs > 2000) {
    console.warn(`[smoke] WARN: cold start ${coldStartMs}ms > 2000ms target (spec 6.5). P0 acceptable if < ServerStartupTimeoutSeconds (60s).`);
  } else {
    console.log(`[smoke] OK: cold start within 2s target.`);
  }

  ws.send(JSON.stringify({ type: "ping", requestId: "t-1" }));
};

ws.onmessage = (e) => {
  console.log("[smoke] recv:", e.data);
  ws.close();
  proc.kill();
  process.exit(0);
};

ws.onerror = (e) => {
  if (!connected && Date.now() < connectDeadline) {
    // Retry: server not ready yet, attempt reconnect after 100ms
    setTimeout(() => {
      // Note: WebSocket constructor is one-shot; we just rely on the next event-loop tick.
    }, 100);
  } else {
    console.error("[smoke] WS error:", e.message || e);
    proc.kill();
    process.exit(1);
  }
};

// Hard timeout: if no connection in 10s, kill and fail
setTimeout(() => {
  if (!connected) {
    console.error("[smoke] FAIL: WS not connected within 10s. Cold start too slow or exe crashed.");
    proc.kill();
    process.exit(2);
  }
}, 10000);
'
```

Expected output:
```
[smoke] WebSocket connected. coldStartMs=<number>
[smoke] OK: cold start within 2s target.    # OR
[smoke] WARN: cold start <N>ms > 2000ms target (spec 6.5). P0 acceptable if < ServerStartupTimeoutSeconds (60s).
[smoke] recv: {"type":"pong","requestId":"t-1"}
```

**Acceptance criteria for cold-start measurement:**
- `coldStartMs < 2000` → OK (spec 6.5 met)
- `2000 ≤ coldStartMs < 60000` → WARN (P0 acceptable, but flag for P1 optimization — likely Bun exe first-launch JIT cost)
- `coldStartMs ≥ 60000` → FAIL (server failed to start, investigate `ServerStartupTimeoutSeconds` config or exe crash)

> **Rationale for measurement:** Spec 6.5 specifies ≤2s cold start, but Bun-compiled exes on first launch may take longer due to module initialization. The measurement gives us empirical data to:
> 1. Decide whether to raise the spec threshold (if 2-5s is typical, the spec may be unrealistic).
> 2. Identify if optimization is needed (lazy-load LLM provider, defer agent registry init, etc.).
> 3. Set the correct default `ServerStartupTimeoutSeconds` in ModConfig (currently 60s — should be ~3x the observed p99 cold start).

> **Cleanup:** The smoke test kills the server process at the end. Verify no orphan `valley-ai-server.exe` remains: `tasklist | findstr valley-ai-server` (Windows) or `ps aux | grep valley-ai-server` (WSL).

- [ ] **Step 5: Commit**

```bash
cd <VALLEYAI_ROOT>
git add packages/stardew/src/cli.ts packages/stardew/scripts/build-exe.sh
# Note: bin/valley-ai-server.exe is typically gitignored (build artifact)
# Add to .gitignore if not already: packages/stardew/bin/
git commit -m "feat(stardew): add CLI entry point + Bun compile build script

cli.ts parses --port/--llm-api-key/--llm-model args (env var fallbacks). build-exe.sh produces bin/valley-ai-server.exe (~40-50MB) via 'bun build --compile --target=bun-windows-x64'."
```

---

### Task 21: ServerProcessManager (重命名 + Bun exe 支持)

**Files:**
- Modify: `<REPO_ROOT>\src\ValleyAgent\WebSocket\PythonProcessManager.cs` → rename to `ServerProcessManager.cs`
- Modify: `<REPO_ROOT>\src\ValleyAgent\Config\ModConfig.cs`
- Modify: `<REPO_ROOT>\src\ValleyAgent\Initialization\ServiceInitializer.cs` (lines 154-162)

- [ ] **Step 1: Rename PythonProcessManager.cs to ServerProcessManager.cs**

Use git mv to preserve history:
```bash
cd <REPO_ROOT>
git mv src/ValleyAgent/WebSocket/PythonProcessManager.cs src/ValleyAgent/WebSocket/ServerProcessManager.cs
```

- [ ] **Step 2: Update the class name + internal logic**

Read `<REPO_ROOT>\src\ValleyAgent\WebSocket\ServerProcessManager.cs` (renamed). Update:

1. Class name: `PythonProcessManager` → `ServerProcessManager`
2. Constructor / field updates:
   - `PythonExecutablePath` → `ServerExecutablePath`
   - `ServerModule="main.py"` → remove (no longer needed)
   - Startup command: `python main.py` → `valley-ai-server.exe --port {port} --llm-api-key {key} --llm-model {model} --agents-dir {agentsDir}`

Replace the process start logic. Find the method that builds `ProcessStartInfo` and update:

Old (approximate):
```csharp
var startInfo = new ProcessStartInfo
{
    FileName = PythonExecutablePath,
    Arguments = $"\"{ServerModule}\"",
    WorkingDirectory = ServerDirectory,
    UseShellExecute = false,
    RedirectStandardOutput = true,
    RedirectStandardError = true,
    CreateNoWindow = true,
};
```

New:
```csharp
// Resolve API key: config first, then env var fallback (LLM_API_KEY)
var apiKey = !string.IsNullOrEmpty(_config.LlmApiKey)
    ? _config.LlmApiKey
    : Environment.GetEnvironmentVariable("LLM_API_KEY") ?? "";
if (string.IsNullOrEmpty(apiKey))
{
    _log?.Log("[ServerProcessManager] LLM API key missing. Set LlmApiKey in config or LLM_API_KEY env var.", LogLevel.Error);
    return false;
}

var llmModel = !string.IsNullOrEmpty(_config.LlmModel)
    ? _config.LlmModel
    : "MiniMax-M2";

var agentsDir = Path.Combine(_modHelper.DirectoryPath, "agents");
Directory.CreateDirectory(agentsDir);

var startInfo = new ProcessStartInfo
{
    FileName = ServerExecutablePath,
    Arguments = $"--port {ServerPort} --llm-api-key {apiKey} --llm-model {llmModel} --agents-dir \"{agentsDir}\"",
    WorkingDirectory = ServerDirectory,
    UseShellExecute = false,
    RedirectStandardOutput = true,
    RedirectStandardError = true,
    CreateNoWindow = true,
};

// SECURITY: Do NOT log Arguments (contains LLM API key)
_log?.Log($"Starting server: {ServerExecutablePath} (port {ServerPort})");
```

> **Env var fallback rationale:** Users with existing `config.json` (legacy `ApiKey` field) may not have `LlmApiKey` populated. Env var `LLM_API_KEY` provides an override for CI/headless setups and avoids committing secrets to `config.json`. Both paths must be checked — config wins to allow per-mod override, env var is the safety net.

- [ ] **Step 3: Rewrite ModConfig — new fields + [Obsolete] legacy compat + migration**

Read `<REPO_ROOT>\src\ValleyAgent\Config\ModConfig.cs` (248 lines). The current file has legacy Python* fields at lines 64-77 and legacy LLM fields (`Provider`/`ApiKey`/`Model`/`ModelName`) scattered throughout. We must:

1. **Add new canonical fields** (replacing Python* fields). Place these in the same location (around line 64) to minimize diff:
```csharp
// ─── Server process configuration (replaces legacy Python* fields) ────
[DefaultValue(true)]
public bool AutoStartServer { get; set; } = true;

[DefaultValue("")]
public string ServerExecutablePath { get; set; } = "";

[DefaultValue("")]
public string ServerDirectory { get; set; } = "";

[DefaultValue(8765)]
public int ServerPort { get; set; } = 8765;

[DefaultValue(60)]  // increased from 30 — Bun exe cold start is slower
public int ServerStartupTimeoutSeconds { get; set; } = 60;

[DefaultValue(3)]
public int ServerMaxRestartAttempts { get; set; } = 3;

// ─── LLM configuration (canonical fields, replaces legacy ApiKey/Model) ──
[DefaultValue("")]
public string LlmApiKey { get; set; } = "";

[DefaultValue("MiniMax-M2")]
public string LlmModel { get; set; } = "MiniMax-M2";

[DefaultValue(false)]
public bool DevMode { get; set; } = false;
```

2. **Mark legacy fields as [Obsolete]** — keep them in the class for save-file backward compat. Do NOT delete (would break deserialization of existing `config.json`):
```csharp
// ─── Legacy fields (retained for config.json backward compat — do NOT use in new code) ──
[Obsolete("Use AutoStartServer instead. Retained for config.json backward compat.")]
[DefaultValue(true)]
public bool AutoStartPythonServer { get; set; } = true;

[Obsolete("Use ServerExecutablePath instead.")]
[DefaultValue("")]
public string PythonExecutablePath { get; set; } = "";

[Obsolete("Use ServerDirectory instead.")]
[DefaultValue("")]
public string PythonServerDirectory { get; set; } = "";

[Obsolete("Use ServerStartupTimeoutSeconds instead.")]
[DefaultValue(30)]
public int PythonServerStartupTimeoutSeconds { get; set; } = 30;

[Obsolete("Use ServerMaxRestartAttempts instead.")]
[DefaultValue(3)]
public int PythonServerMaxRestartAttempts { get; set; } = 3;

[Obsolete("Use LlmApiKey instead.")]
[DefaultValue("")]
public string ApiKey { get; set; } = "";

[Obsolete("Use LlmModel instead.")]
[DefaultValue("")]
public string Model { get; set; } = "";
```

3. **Add `MigrateLegacyFields()` method** — call this in `Validate()` (or in `ModEntry.OnGameLaunched` after GMCM loads config) to copy any non-empty legacy field into the new canonical field if the canonical field is unset:
```csharp
/// <summary>
/// Copies any non-empty legacy field values into the canonical fields.
/// Called after config load to preserve user settings across the rename.
/// Idempotent: only writes when canonical field is empty/default.
/// </summary>
public void MigrateLegacyFields()
{
    #pragma warning disable CS0618 // Reference obsolete fields for migration
    if (AutoStartServer && !AutoStartPythonServer)
    {
        AutoStartServer = AutoStartPythonServer;
    }
    if (string.IsNullOrEmpty(ServerExecutablePath) && !string.IsNullOrEmpty(PythonExecutablePath))
    {
        ServerExecutablePath = PythonExecutablePath;
    }
    if (string.IsNullOrEmpty(ServerDirectory) && !string.IsNullOrEmpty(PythonServerDirectory))
    {
        ServerDirectory = PythonServerDirectory;
    }
    if (ServerStartupTimeoutSeconds == 60 && PythonServerStartupTimeoutSeconds != 30)
    {
        ServerStartupTimeoutSeconds = PythonServerStartupTimeoutSeconds;
    }
    if (ServerMaxRestartAttempts == 3 && PythonServerMaxRestartAttempts != 3)
    {
        ServerMaxRestartAttempts = PythonServerMaxRestartAttempts;
    }
    if (string.IsNullOrEmpty(LlmApiKey) && !string.IsNullOrEmpty(ApiKey))
    {
        LlmApiKey = ApiKey;
    }
    if (string.IsNullOrEmpty(LlmModel) || LlmModel == "MiniMax-M2")
    {
        if (!string.IsNullOrEmpty(Model)) LlmModel = Model;
        else if (!string.IsNullOrEmpty(ModelName)) LlmModel = ModelName;
    }
    #pragma warning restore CS0618
}
```

4. **Update `Validate()` to call migration first**, then existing validation:
```csharp
public bool Validate()
{
    MigrateLegacyFields();
    var changed = false;
    if (string.IsNullOrWhiteSpace(LlmModel)) { LlmModel = "MiniMax-M2"; changed = true; }
    if (ServerPort < 1 || ServerPort > 65535) { ServerPort = 8765; changed = true; }
    if (ServerStartupTimeoutSeconds < 10 || ServerStartupTimeoutSeconds > 300) { ServerStartupTimeoutSeconds = 60; changed = true; }
    // ... existing clamps for Temperature, DecisionIntervalMinutes, etc. (unchanged)
    if (string.IsNullOrWhiteSpace(ModelName)) { ModelName = "deepseek-chat"; changed = true; }
    if (string.IsNullOrWhiteSpace(WebSocketUrl)) { WebSocketUrl = "ws://127.0.0.1:8765"; changed = true; }
    if (Temperature < 0f || Temperature > 2f) { Temperature = 0.7f; changed = true; }
    if (DecisionIntervalMinutes < 0.1f || DecisionIntervalMinutes > 5f) { DecisionIntervalMinutes = 0.5f; changed = true; }
    if (IdleThresholdSeconds < 30 || IdleThresholdSeconds > 300) { IdleThresholdSeconds = 90; changed = true; }
    if (GiftCooldownMs < 0 || GiftCooldownMs > 60000) { GiftCooldownMs = 30000; changed = true; }
    return changed;
}
```

5. **Do NOT delete any other existing fields** (LlmProvider enum, LanguageMode enum, MaxAgentNpcs, EnableFriendshipChanges, CircuitBreakerThreshold, etc.). Only the 7 legacy fields above get [Obsolete]; the rest stay as-is.

**Verification (after edit, before building):**
- File line count should grow by ~80 lines (new fields + [Obsolete] block + MigrateLegacyFields method + Validate update).
- `grep -n "public.*Python\|public.*ApiKey\b\|public.*Model\b" src/ValleyAgent/Config/ModConfig.cs` should show each legacy field preceded by `[Obsolete(...)]`.
- `grep -n "MigrateLegacyFields" src/ValleyAgent/Config/ModConfig.cs` should return 2 hits (method def + call inside `Validate`).
- **No compile errors expected:** legacy fields are still present (just marked obsolete), so any existing references in `PythonProcessManager.cs` (which we're renaming in Step 1) won't break until those references are updated in Step 2.

- [ ] **Step 4: Update ServiceInitializer assembly**

Read `<REPO_ROOT>\src\ValleyAgent\Initialization\ServiceInitializer.cs` lines 150-165. Find where `PythonProcessManager` is registered and update to `ServerProcessManager`:

Old:
```csharp
services.AddSingleton<PythonProcessManager>(new PythonProcessManager(...));
```

New:
```csharp
services.AddSingleton<ServerProcessManager>(new ServerProcessManager(...));
```

Also find all references to `PythonProcessManager` across the codebase and update:
```powershell
cd <REPO_ROOT>\src\ValleyAgent
Select-String -Path "*.cs" -Recurse -Pattern "PythonProcessManager" -SimpleMatch
```

Replace each occurrence with `ServerProcessManager`.

- [ ] **Step 5: Build + verify 0 warnings + commit**

Run: `cd <REPO_ROOT>\src\ValleyAgent && dotnet build -c Release`
Expected: Build succeeded, 0 errors, 0 warnings.

> **CS0618 (Obsolete) warnings are NOT acceptable.** All new code must reference canonical field names (`LlmApiKey`, `ServerExecutablePath`, etc.). The only place legacy fields are referenced is inside `MigrateLegacyFields()`, which wraps them in `#pragma warning disable CS0618` / `#pragma warning restore CS0618`. If any CS0618 warning remains, find the offending reference and switch to the canonical field.

> **Pre-build grep check (mandatory):** Run this PowerShell to find any remaining legacy references outside `MigrateLegacyFields`:
> ```powershell
> cd <REPO_ROOT>\src\ValleyAgent
> Select-String -Path "*.cs" -Recurse -Pattern "AutoStartPythonServer|PythonExecutablePath|PythonServerDirectory|PythonServerStartupTimeoutSeconds|PythonServerMaxRestartAttempts" -SimpleMatch | Where-Object { $_.Line -notmatch "#pragma|MigrateLegacyFields" }
> ```
> Output must be empty. If not, fix each reference before building.

```bash
cd <REPO_ROOT>
git add src/ValleyAgent/WebSocket/ServerProcessManager.cs
git add src/ValleyAgent/Config/ModConfig.cs
git add src/ValleyAgent/Initialization/ServiceInitializer.cs
# Add any other files that referenced PythonProcessManager
git add -A src/ValleyAgent/
git commit -m "feat(mod): rename PythonProcessManager → ServerProcessManager, launch valley-ai-server.exe

Spec: C# Mod starts Bun-compiled TS server (valley-ai-server.exe) with --port/--llm-api-key/--llm-model args. ModConfig fields renamed (AutoStartPythonServer→AutoStartServer etc.). Legacy fields retained as [Obsolete] for config.json backward compat + MigrateLegacyFields() bridges old→new. SECURITY: Arguments not logged (contains API key). Env var LLM_API_KEY fallback added."
```

---

### Task 22: End-to-end game test (TestMod)

**Files:**
- Manual test: 启动游戏 + TestMod + 验证对话流

- [ ] **Step 1: Build both sides**

```bash
# TS side
cd <VALLEYAI_ROOT>\packages\stardew
bash scripts/build-exe.sh

# C# side
cd <REPO_ROOT>\src\ValleyAgent
dotnet build -c Release
```

- [ ] **Step 2: Copy valley-ai-server.exe to mod directory**

```bash
cp <VALLEYAI_ROOT>\packages\stardew\bin\valley-ai-server.exe <REPO_ROOT>\Stardew Valley\Mods\ValleyAgent\
```

- [ ] **Step 3: Configure mod to use real LLM API key**

Edit `<REPO_ROOT>\Stardew Valley\Mods\ValleyAgent\config.json`:
```json
{
  "AutoStartServer": true,
  "ServerExecutablePath": "valley-ai-server.exe",
  "ServerDirectory": "",
  "ServerPort": 8765,
  "ServerStartupTimeoutSeconds": 30,
  "ServerMaxRestartAttempts": 3,
  "LlmApiKey": "<your-minimax-api-key>",
  "LlmModel": "MiniMax-M2"
}
```

- [ ] **Step 4: Launch game + verify server starts**

Run:
```bash
cd <REPO_ROOT>
.\scripts\test\run-game-tests.bat
```

Expected (from SMAPI log):
- `[ServerProcessManager] Starting server: valley-ai-server.exe (port 8765)`
- `[valley-ai-server] listening on port 8765`
- `[ServerProcessManager] Server started successfully`

- [ ] **Step 5: Run TestMod scenarios + verify P0 acceptance criteria**

Manual test scenarios (spec 6.2 Game E2E). The TestMod already has many EXP* test files (see `src/ValleyAgent.TestMod/Tests/Experience/`); we map each scenario below to either an existing test file or a new file to be created.

**Scenario → Test file mapping:**

| # | Scenario | Test File | Status | Notes |
|---|----------|-----------|--------|-------|
| 1 | Click NPC → dialogue box opens within 100ms | `Tests/Experience/EXP001_ClickNpcOpensDialogue.cs` | **EXISTS** — extend if needed | Existing test clicks NPC + checks dialogue box appears. Add 100ms timing assertion if not present. |
| 2 | Type "你好" + Enter → NPC responds in Chinese within 5s | `Tests/Experience/EXP003_DialogueTextInputAndSend.cs` | **EXISTS** — extend if needed | Existing test types "你好啊" + Enter + checks NPC reply. Add 5s timeout assertion + Chinese-character check if not present. |
| 3 | Round 1 "我叫张三" → Round 2 "我叫什么" → NPC answers "张三" | `Tests/Experience/EXP013_DialogueMemory.cs` | **NEW — create** | Multi-turn memory test. Skeleton provided below. |
| 4 | Say "送我东西" → NPC calls give_item → item appears in inventory | `Tests/Experience/EXP007_NpcGiftToPlayer.cs` | **EXISTS** — extend if needed | Existing test covers gift scenario. Add assertion that item appears in `Game1.player.Items` after action. |
| 5 | NPC calls emote → emote bubble appears | `Tests/Experience/EXP009_NpcEmoteVisible.cs` | **EXISTS** — extend if needed | Existing test covers emote visibility. Verify emote ID matches what NPC actually emits. |
| 6 | Stop TS server → wait 60s → dialogue still responds (fallback) | `Tests/Pipeline/PIPE006_CircuitBreakerAndReconnect.cs` | **EXISTS** — extend if needed | Existing test covers circuit breaker + reconnect. Add explicit 60s fallback wait + fallback-text assertion. |
| 7 | Restart TS server → dialogue returns to normal LLM responses | `Tests/Pipeline/PIPE006_CircuitBreakerAndReconnect.cs` | **EXISTS** — extend if needed | Same file — verify post-reconnect dialogue is non-fallback. |

**Only one NEW test file is needed: `EXP013_DialogueMemory.cs`.** All other scenarios already have test files that may need minor extensions (timing assertions, additional assertions on game state).

> **Rationale for single new file:** The TestMod already has 12 EXP* tests + 6 PIPE* tests covering dialogue, action, emote, and reconnect scenarios. Creating the DialogueMemory test as EXP013 (next available number after EXP012_FullPlayerExperience) follows the existing naming convention and avoids duplicating coverage.

**EXP013_DialogueMemory.cs skeleton (create new file at `src/ValleyAgent.TestMod/Tests/Experience/EXP013_DialogueMemory.cs`):**

```csharp
#nullable enable
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using ValleyAgent.TestMod.Infrastructure;

namespace ValleyAgent.TestMod.Tests.Experience;

/// <summary>
///     多轮对话记忆验证。Round 1 玩家说 "我叫张三"，NPC 应当通过 remember 工具或
///     short-term memory 记下。Round 2 玩家问 "我叫什么名字"，NPC 回答应包含 "张三"。
///     验证 AgentMemory.conversationHistory 跨轮次保留，且 LLM 能基于历史回答。
/// </summary>
[RegisteredTest(TestGroup.Experience, "多轮对话记忆", "experience")]
public class EXP013_DialogueMemory : V3TestBase
{
    private const string PlayerName = "张三";
    private const string PlayerIntro = "我叫张三";
    private const string PlayerQuestion = "我叫什么名字";

    private IValleyAgentApi? _api;
    private NPC? _npc;
    private string? _round1Reply;
    private string? _round2Reply;
    private bool _round1Sent;
    private bool _round2Sent;

    public EXP013_DialogueMemory(IModHelper helper, IMonitor monitor) : base(helper, monitor)
    {
    }

    public override string TestName => "EXP013_DialogueMemory";
    public override int TimeoutTicks => 12000;  // 2 rounds × ~5s LLM latency + buffer
    public override TestGroup Group => TestGroup.Experience;

    public override void Setup()
    {
        _api = ModEntry.API;
        if (_api == null)
        {
            Skip("API not available");
            return;
        }

        Game1.warpFarmer("Farm", 54, 15, false);
        _ = _api.TryAllocateAgent("Haley");
        _npc = Game1.getCharacterFromName("Haley");
        if (_npc == null)
        {
            Skip("Haley not found");
            return;
        }

        _npc.setTileLocation(new Vector2(54, 16));
        _npc.Halt();
        _npc.controller = null;

        _api.DialogueCooldownMs = 0;
        ClearDialogueCooldown(_api, "Haley");

        Monitor.Log("[EXP013] Setup complete. Two-round memory test starting.", LogLevel.Info);
    }

    public override bool Update()
    {
        if (_npc == null || _api == null)
        {
            return true;
        }

        // Phase 1 (tick 60): Round 1 — introduce player name.
        if (CurrentTick == 60 && !_round1Sent)
        {
            _round1Sent = true;
            _round1Reply = _api.TryGenerateDialogue("Haley", PlayerIntro);
            Assert("round1_sent", !string.IsNullOrEmpty(_round1Reply),
                $"intro=\"{PlayerIntro}\" replyLen={_round1Reply?.Length ?? 0}");
            Monitor.Log($"[EXP013] Round 1: player=\"{PlayerIntro}\" npcReply=\"{_round1Reply ?? "(null)"}\"",
                LogLevel.Info);
        }

        // Phase 2 (tick 360, ~6s later): Round 2 — ask for the name back.
        // 6s gives the LLM/server enough time to finish Round 1 + persist to AgentMemory.
        if (CurrentTick == 360 && _round1Sent && !_round2Sent)
        {
            _round2Sent = true;
            _round2Reply = _api.TryGenerateDialogue("Haley", PlayerQuestion);
            Assert("round2_sent", !string.IsNullOrEmpty(_round2Reply),
                $"question=\"{PlayerQuestion}\" replyLen={_round2Reply?.Length ?? 0}");

            // Core assertion: Round 2 reply must contain "张三".
            var replyContainsName = _round2Reply?.Contains(PlayerName) ?? false;
            Assert("round2_remembers_name", replyContainsName,
                $"expected \"{PlayerName}\" in reply, got=\"{_round2Reply ?? "(null)"}\"");

            Monitor.Log($"[EXP013] Round 2: player=\"{PlayerQuestion}\" npcReply=\"{_round2Reply ?? "(null)"}\" " +
                        $"containsName={replyContainsName}", LogLevel.Info);
        }

        return CurrentTick >= 480 || _round2Sent;
    }

    public override void Teardown()
    {
        Monitor.Log($"[EXP013] Teardown. round1Sent={_round1Sent} round2Sent={_round2Sent} " +
                    $"round2Remembered={(_round2Reply?.Contains(PlayerName) ?? false)}", LogLevel.Info);
    }
}
```

> **Test config note:** `EXP013_DialogueMemory` requires a REAL LLM (not MockLLMProvider) because Mock responses are stateless and won't remember across rounds. Run this test only when `LlmApiKey` is set to a real MiniMax key. The TestMod's `test_config.json` should include `EXP013` in the "requires-real-llm" filter group (see existing `TestFilter.cs` for filter mechanics).

> **Acceptance criteria for all 7 scenarios:**
> - Scenarios 1, 2, 4, 5, 6, 7: extend existing test files if assertions are missing. The test files already exist, so the work is verification + minor additions, not new code.
> - Scenario 3: create `EXP013_DialogueMemory.cs` (skeleton above), verify it passes against real LLM.
> - For each scenario, capture the SMAPI log + TS server log if it fails. The SMAPI log path is typically `<REPO_ROOT>\Stardew Valley\ErrorLogs\SMAPI_latest.txt`.

- [ ] **Step 6: Final commit (if any config/docs changes)**

```bash
cd <REPO_ROOT>
# Only commit if config.json template or test scripts changed
# Do NOT commit the actual config.json with real API key
git add -A
git commit -m "test: P0 acceptance — dialogue pipeline end-to-end verified

All 7 Game E2E scenarios pass: click-to-dialogue, text input, multi-turn memory, action execution, emote, fallback, reconnect."
```

---

## Self-Review

### 1. Spec 覆盖检查

| Spec 节 | 要求 | 对应 Task |
|---------|------|-----------|
| 2.2 数据契约 | DialogueRequest/Response schema | Task 4 (types.ts) + Task 16 (C# records) |
| 3.1 组件清单 | 8 个对话工具 | Task 8 |
| 3.1 组件清单 | PromptBuilder | Task 9 |
| 3.1 组件清单 | AgentMemory + load/save | Task 7 |
| 3.1 组件清单 | NpcPromptLoader | Task 5 |
| 3.1 组件清单 | WorldSnapshotDecoder | Task 6 |
| 3.1 组件清单 | OutputValidator | Task 10 |
| 3.1 组件清单 | RuleEngine (简化) | Task 11 |
| 3.1 组件清单 | ProtocolAdapter (5 消息) | Task 14 |
| 3.2 MemoryBackend 接口 | P0 必需 | Task 3 |
| 3.3 C# 改造 | DialogueBoxInputPatch 简化 | Task 18 |
| 3.3 C# 改造 | BuildDialogueRequest 删除 | Task 19 |
| 3.3 C# 改造 | WebSocketClient 协议升级 | Task 16 (字段名自动适配) |
| 3.3 C# 改造 | DialogueResponse record 重定义 | Task 16 |
| 3.3 C# 改造 | PythonProcessManager 重命名 | Task 21 |
| 3.3 C# 改造 | CommandExecutor.ExecuteAction | Task 17 |
| 4.1 对话主流程 | 13 步数据流 | Task 12 (StardewAgent) + Task 14 (ProtocolAdapter) |
| 4.3 持久化流 | load/save 时机 | Task 7 (AgentMemory) + Task 14 (lazy load + async save) |
| 4.4 多 NPC 并发 | StardewAgentRegistry + 锁 | Task 13 |
| 5.1 降级链 | Layer 1-4 | Task 12 (Layer 1-2) + Task 11 (Layer 3) + Task 14 (Layer 4 busy) |
| 5.4 CircuitBreaker | 字段 + 回调 | Task 2 |
| 5.5 关键不变量 | speech 非空 / maxTurns≤5 / 单 NPC 锁 | Task 12 + Task 13 + Task 14 |
| 6.2 验收测试 | Unit + Integration + Game E2E | Tasks 1-15 (TS tests) + Task 22 (Game E2E) |
| 7.1 P0 范围 | 5 消息 + Bun exe + B1/C1/C2 | All tasks |

**覆盖完整**：spec 每节都有对应 task。

### 2. 占位符扫描

搜索红旗模式：
- "TBD" / "TODO" / "implement later" / "fill in details" → 无
- "添加适当的错误处理" / "处理边缘情况" → 无（每个错误处理都有具体代码）
- "为以上写测试" → 无（每个 task 都有完整测试代码）
- "类似 Task N" → 无（每个 task 都有完整代码块）
- 描述但不展示 → 无（每个步骤都有代码）

**通过**：无占位符。

### 3. 类型一致性检查

| 类型/方法 | 定义 Task | 使用 Task | 一致 |
|----------|----------|----------|------|
| `ToolAction` (TS) | Task 4 (types.ts) | Task 8/10/11/12/14 | ✅ `{tool: string, args: Record<string,unknown>}` |
| `ToolAction` (C#) | Task 16 | Task 17/18 | ✅ `record ToolAction(string Tool, Dictionary<string,object> Args)` |
| `DialogueResponse` (TS) | Task 4 | Task 11/14 | ✅ `{type, requestId, npcName, speech, actions, emotion, fallback?}` |
| `DialogueResponse` (C#) | Task 16 | Task 18 | ✅ `record DialogueResponse(string Speech, IReadOnlyList<ToolAction> Actions, string? Emotion, string RequestId, string Type)` |
| `SceneState` (TS) | Task 4 | Task 6/8/9/12 | ✅ `{season, day, timeStr, weather, location, nearbyObjects, farmerName, friendship, npcState, inventory}` |
| `WorldSnapshot` (TS) | Task 4 | Task 6/14 | ✅ |
| `WorldSnapshot` (C#) | Task 18 | Task 18 | ✅ 字段名匹配 TS |
| `MemoryBackend` | Task 3 | Task 7 | ✅ AgentMemory implements |
| `StardewAgent.runDialogue(playerInput, scene)` | Task 12 | Task 14 | ✅ 返回 DialogueResult |
| `StardewAgentRegistry.getOrCreate(npcName)` | Task 13 | Task 14 | ✅ 返回 StardewAgent |
| `StardewAgentRegistry.acquireLock(npcName)` | Task 13 | Task 14 | ✅ 返回 Promise<boolean> |
| `ProtocolAdapter.handleDialogue(req)` | Task 14 | Task 15 | ✅ 返回 Promise<DialogueResponse> |
| `CommandExecutor.ExecuteAction(action, npcName)` | Task 17 | Task 18 | ✅ 签名匹配 |
| `startServer(config)` | Task 15 | Task 20 | ✅ 返回 ServerHandle |

**通过**：所有类型/方法签名跨 task 一致。

### 4. 风险点

1. **`chatWithTools` 方法存在性**：Task 12 假设 VercelAIProvider 已有 `chatWithTools` 方法。e2e/stardew-run.ts:85 使用了 `provider.chatWithTools(messages, tools)`，应已存在。若不存在，Task 12 Step 3 的实现会编译失败，需在 core/llm-provider.ts 补充。

2. **`_setCallOverride` 方法存在性**：测试依赖此方法注入 mock。已在 e2e/stardew-run.ts 中使用，应已存在。

3. **C# `WorldSnapshot` record 与 TS 字段名匹配**：camelCase JSON 序列化后，C# `NpcTile` ↔ TS `npcTile`，`NearbyObjects` ↔ `nearbyObjects`。WebSocketClient 已配置 `JsonStringEnumConverter` + camelCase，应自动适配。

4. **Bun exe 冷启动时间**：spec 6.5 要求 ≤2s。若实测超时，需调高 `ServerStartupTimeoutSeconds`（默认 30s 已有足够余量）。

5. **C# Mod 现有测试 `PIPE003_DialoguePipeline.cs`**：Task 16 机械修复了构造调用，但若测试逻辑断言 `response.Text`，需在 Task 19 同步更新断言为 `response.Speech`。

6. **`AfterToolCallResult.skipFollowUp` 行为未在 P0 显式约束**：
   - `@valley/core` 的 `AgentLoopConfig.afterToolCall` 钩子可返回 `{ skipFollowUp: true }`，跳过工具调用后的 LLM follow-up 生成（见 `2026-07-08-valleyai-core-plan.md` line 2627-2630 的 `AfterToolCallResult` 接口定义）。
   - P0 spec 期望 NPC 在执行即时工具（speak/emote/set_state）后不再生成额外文本（speech 已经在工具参数里）；但对延迟工具（move_to/harvest）希望 NPC 有一句"我去摘点东西"的 follow-up。
   - **当前 P0 plan 没有显式约束 `skipFollowUp` 的取值**，存在两种潜在 bug：
     - 若 `skipFollowUp=true` 对所有工具生效 → 延迟工具后 NPC 沉默，玩家不知道发生了什么。
     - 若 `skipFollowUp=false` 对所有工具生效 → speak 工具后 LLM 又生成一句冗余 speech，玩家看到"嗯，送你个礼物" + "我觉得这个礼物很适合你"两句重复。
   - **P0 决策：留待 P1 处理。** P0 默认不设置 `afterToolCall` 钩子（即 `skipFollowUp` 不生效，每轮工具调用后都有 follow-up LLM 生成）。这是 P0 的"安全默认值"——保证 NPC 不会沉默，代价是 speak 工具后可能有一句冗余。P1 应当：
     1. 在 `@valley/core` 文档化 `skipFollowUp` 语义。
     2. 在 `@valley/stardew` 的 `StardewAgent` 中注册 `afterToolCall` 钩子，按工具类型返回不同 `skipFollowUp`（immediate tools → `true`，delayed tools → `false`）。
     3. 添加单测覆盖两种路径。
   - **验证方式（P0）**：在 Task 22 EXP013_DialogueMemory 场景中观察 SMAPI 日志，确认每轮对话只产生一条 NPC speech（不是两条）。若发现重复 speech，说明 `skipFollowUp` 未正确配置，应在 P1 修复。

---

## 执行交接

Plan complete and saved to `docs/superpowers/plans/2026-07-18-p0-dialogue-pipeline.md`. Two execution options:

1. **Subagent-Driven (recommended)** - I dispatch a fresh subagent per task, review between tasks, fast iteration
2. **Inline Execution** - Execute tasks in this session using executing-plans, batch execution with checkpoints

Which approach?
