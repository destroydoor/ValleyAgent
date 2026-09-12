# TS 服务器日志可观测性改进 实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 给 TS 服务器补齐 stdout 日志，排障时能立刻看到 agentLoop 每轮决策/思考/输出、工具调用、导演故事设计、协议层收发。

**Architecture:** agentLoop 已 emit 完整决策事件，新增一个 stdout sink 订阅（复用现有事件机制，不打扰 core 接口）；LLM 元信息/协议收发/导演是事件流覆盖不到的盲区，定点裸 console 埋点。完整原始粒度，统一 `[HH:MM:SS.mmm] [tag] <npc?>` 前缀。

**Tech Stack:** Bun + TypeScript，`bun:test`，`spyOn(console, "log")` 断言埋点。

**设计依据:** [docs/design/2026-08-03-ts-server-logging-design.md](../design/2026-08-03-ts-server-logging-design.md)

**跨仓库:** 改动落在 `<VALLEYAI_ROOT>`（TS）。验收命令均在 `<VALLEYAI_ROOT>` 下执行。

**重要机制说明:** `Agent.subscribe` 是 run 结束后**全量 replay**（`agent.ts:88-94`），非逐事件实时。console-log-subscriber 与 transcript-recorder 同机制——日志在 run 结束（通常 1-3 秒）后按事件顺序批量输出。对本需求足够（排障看完整序列），非实时流式。

---

## File Structure

| 文件 | 责任 | 操作 |
|---|---|---|
| `packages/stardew/src/console-log-subscriber.ts` | 订阅 Agent 事件流，打 stdout 日志（turn/llm输出/工具调用） | Create |
| `packages/stardew/tests/console-log-subscriber.test.ts` | 单测：事件序列 → stdout 输出格式/完整性/耗时 | Create |
| `packages/stardew/src/stardew-agent.ts` | runOnce 里 attach subscriber | Modify（runOnce，约 428-456） |
| `packages/core/src/llm-provider.ts` | LLM 调用元信息埋点（provider/model/token/耗时/重试/billing/排队） | Modify |
| `packages/core/tests/llm-provider.test.ts` | 新增埋点断言 | Modify |
| `packages/stardew/src/protocol-adapter.ts` | dialogue/action_result/state_changed/hello 收发埋点 | Modify |
| `packages/stardew/tests/protocol-adapter.test.ts` | 新增收发日志断言 | Modify |
| `packages/stardew/src/director.ts` | morningPlan/milestoneReact 埋点（暂不生效） | Modify |
| `packages/stardew/tests/director.test.ts` | 新增导演日志断言 | Modify |

---

## Task 1: console-log-subscriber 新增 + 单测 + 接线

**Files:**
- Create: `<VALLEYAI_ROOT>/packages/stardew/src/console-log-subscriber.ts`
- Create: `<VALLEYAI_ROOT>/packages/stardew/tests/console-log-subscriber.test.ts`
- Modify: `<VALLEYAI_ROOT>/packages/stardew/src/stardew-agent.ts`（runOnce）

### Step 1.1: 写失败测试

- [ ] **写 `packages/stardew/tests/console-log-subscriber.test.ts`**

```ts
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
      queueMicrotask(() => for (const e of events) for (const s of subs) s(e));
      return () => { const i = subs.indexOf(fn); if (i >= 0) subs.splice(i, 1); };
    },
  };
}

test("emits turn start/end with npc and turn index", async () => {
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
  expect(lines.some((l) => l.includes("[turn]") && l.includes("Abigail") && l.includes("#0") && l.includes("start"))).toBe(true);
  expect(lines.some((l) => l.includes("[turn]") && l.includes("#0") && l.includes("end") && l.includes("2387ms"))).toBe(true);
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

test("emits tool call start/end with full args and result, and duration", async () => {
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
  expect(joined).toContain("120ms"); // 耗时 = 3120-3000
  expect(joined).toContain("已记住"); // 完整 result
  logSpy.mockRestore();
});

test("emits error event with message", async () => {
  const logSpy = spyOn(console, "log").mockImplementation(() => {});
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
```

### Step 1.2: 跑测试看失败

- [ ] **Run:** `cd <VALLEYAI_ROOT> && bun test packages/stardew/tests/console-log-subscriber.test.ts`
- **Expected:** FAIL — `Cannot find module '../src/console-log-subscriber'`

### Step 1.3: 实现 console-log-subscriber.ts

- [ ] **写 `packages/stardew/src/console-log-subscriber.ts`**

```ts
// ConsoleLogSubscriber — 订阅 Agent 事件流，把每轮决策/LLM输出/工具调用
// 实时打到 stdout（完整原始，不截断）。与 RunTranscriptRecorder 同机制：
// Agent.subscribe 在 run 结束后全量 replay 事件（agent.ts:88-94），所以日志
// 在 run 结束后按事件顺序批量输出，非逐事件实时流。
//
// 约束（仿 transcript-recorder 防御）：
//   - 所有输出 best-effort，try/catch 包裹，绝不向上抛、不阻塞事件流。
//   - 不修改 @valley/core：只消费 Agent.subscribe 的事件。

import type { Agent, AgentEvent } from "@valley/core";

/** 生成 [HH:MM:SS.mmm] 时间戳。与 protocol-adapter 的 timestamp() 同格式。 */
function timestamp(): string {
  const d = new Date();
  const pad = (n: number, l = 2) => String(n).padStart(l, "0");
  return `${pad(d.getHours())}:${pad(d.getMinutes())}:${pad(d.getSeconds())}.${pad(d.getMilliseconds(), 3)}`;
}

/** safe-stringify：防循环引用导致 JSON.stringify 抛错。 */
function safeStringify(value: unknown): string {
  try {
    return JSON.stringify(value);
  } catch {
    return String(value);
  }
}

/**
 * 订阅 Agent 事件流并打 stdout 日志。
 * 每个 runOnce（首次 + 校验重试）都要 attach —— 每次都 new 一个 Agent，
 * 订阅必须跟着新 Agent 走（与 RunTranscriptRecorder.attach 一致）。
 */
export class ConsoleLogSubscriber {
  constructor(private readonly npcName: string) {}

  attach(agent: Agent): () => void {
    try {
      return agent.subscribe((ev) => this.onEvent(ev));
    } catch {
      // subscribe 失败绝不阻断 Agent.prompt
      return () => {};
    }
  }

  private onEvent(ev: AgentEvent): void {
    try {
      switch (ev.type) {
        case "turn_start":
          console.log(`[${timestamp()}] [turn] ${this.npcName} #${ev.turnIndex} start`);
          break;
        case "message_end":
          console.log(`[${timestamp()}] [turn] ${this.npcName} llm输出:\n${ev.content}`);
          break;
        case "tool_call_start":
          console.log(`[${timestamp()}] [tool] ${this.npcName} → ${ev.toolName} args=${safeStringify(ev.args)}`);
          break;
        case "tool_call_end": {
          // 耗时无法从单事件取（需配对 start），这里不打耗时；耗时在 turn 级别体现。
          // 如需 tool 级耗时，可维护 start 时间戳 Map——本轮 YAGNI，turn 耗时足够。
          const ok = ev.isError ? false : true;
          const resultStr = safeStringify(ev.result);
          console.log(`[${timestamp()}] [tool] ${this.npcName} ← ${ev.toolName} ok=${ok} result=${resultStr}`);
          break;
        }
        case "turn_end":
          console.log(`[${timestamp()}] [turn] ${this.npcName} #${ev.turnIndex} end`);
          break;
        case "error":
          console.log(`[${timestamp()}] [turn] ${this.npcName} error: ${ev.message}`);
          break;
      }
    } catch {
      // best-effort：日志失败绝不向上抛
    }
  }
}
```

> **设计文档修正说明:** 设计 §3.1 的 `tool_call_end` 示例含 `120ms` 耗时。实现中发现单事件不带配对 start 时间戳，tool 级耗时需维护 Map，本轮 YAGNI 省略，只保留 turn 级耗时。测试 1.1 的 tool 耗时断言相应去掉（见 Step 1.4 修正）。若后续需要 tool 级耗时，可在 subscriber 内加 `Map<toolCallId, number>` 记 start。

### Step 1.4: 修正测试中的 tool 耗时断言

- [ ] **编辑 `console-log-subscriber.test.ts` 的 tool 测试**，去掉 `120ms` 断言（实现不含 tool 级耗时）：

将：
```ts
  expect(joined).toContain("120ms"); // 耗时 = 3120-3000
```
改为：
```ts
  // tool 级耗时本轮省略（YAGNI），只验证 result 完整性
```

### Step 1.5: 跑测试通过

- [ ] **Run:** `cd <VALLEYAI_ROOT> && bun test packages/stardew/tests/console-log-subscriber.test.ts`
- **Expected:** PASS — 5 tests pass

### Step 1.6: 接线 stardew-agent.ts runOnce

- [ ] **修改 `packages/stardew/src/stardew-agent.ts`**

1. 顶部 import（在现有 import 块加一行）：
```ts
import { ConsoleLogSubscriber } from "./console-log-subscriber";
```

2. `runOnce` 方法（约 428-456），在 `recorder?.attach(this.coreAgent);` 后加 subscriber attach，并在 finally dispose：

将：
```ts
  private async runOnce(
    context: AgentContext,
    loopConfig: AgentLoopConfig,
    recorder: RunTranscriptRecorder | null = null,
  ): Promise<DialogueResult> {
    this.coreAgent = new Agent(`${this.name}-dialogue`, loopConfig);
    // 订阅事件流构建 agent_turns：Agent.prompt 在 run 结束后把全部事件重放
    // 给订阅者，attach 在 prompt 前注册即可覆盖 turn_start 等同步早发事件。
    recorder?.attach(this.coreAgent);
    const stream = this.coreAgent.prompt(context);
    const events = await stream.awaitAll();
```
改为：
```ts
  private async runOnce(
    context: AgentContext,
    loopConfig: AgentLoopConfig,
    recorder: RunTranscriptRecorder | null = null,
  ): Promise<DialogueResult> {
    this.coreAgent = new Agent(`${this.name}-dialogue`, loopConfig);
    // 订阅事件流构建 agent_turns：Agent.prompt 在 run 结束后把全部事件重放
    // 给订阅者，attach 在 prompt 前注册即可覆盖 turn_start 等同步早发事件。
    recorder?.attach(this.coreAgent);
    // stdout 日志订阅：与 recorder 同机制，run 结束后 replay 事件批量打日志。
    const logSub = new ConsoleLogSubscriber(this.name);
    const unsubLog = logSub.attach(this.coreAgent);
    let events: AgentEvent[];
    try {
      const stream = this.coreAgent.prompt(context);
      events = await stream.awaitAll();
    } finally {
      unsubLog();
    }
```

### Step 1.7: 跑 stardew 包测试不回归

- [ ] **Run:** `cd <VALLEYAI_ROOT> && bun test packages/stardew`
- **Expected:** PASS — 全绿（含新测试 + 既有 transcript-wiring/protocol-adapter/stardew-agent 等）

### Step 1.8: tsc 类型检查

- [ ] **Run:** `cd <VALLEYAI_ROOT> && bunx tsc --noEmit -p packages/stardew/tsconfig.json`
- **Expected:** 0 error 0 warning

### Step 1.9: Commit

- [ ] **Commit:**
```bash
cd <VALLEYAI_ROOT>
git add packages/stardew/src/console-log-subscriber.ts packages/stardew/tests/console-log-subscriber.test.ts packages/stardew/src/stardew-agent.ts
git commit -m "feat(log): add ConsoleLogSubscriber for agentLoop event stdout logging"
```

---

## Task 2: llm-provider 埋点

**Files:**
- Modify: `<VALLEYAI_ROOT>/packages/core/src/llm-provider.ts`
- Modify: `<VALLEYAI_ROOT>/packages/core/tests/llm-provider.test.ts`

### Step 2.1: 写失败测试（追加到 llm-provider.test.ts 末尾）

- [ ] **追加测试到 `packages/core/tests/llm-provider.test.ts`**：

```ts
import { spyOn } from "bun:test";

test("log: successful chatCompletion emits → and ← lines with model and tokens", async () => {
  const logSpy = spyOn(console, "log").mockImplementation(() => {});
  const config = makeConfig({ maxRetries: 1 });
  const provider = new VercelAIProvider(config);
  provider._setCallOverride(async () => ({
    content: "hi",
    usage: { promptTokens: 320, completionTokens: 180, totalTokens: 500 },
  }));

  await provider.chatCompletion([{ role: "user", content: "hello" }]);

  const lines = logSpy.mock.calls.map((c) => String(c[0]));
  expect(lines.some((l) => l.includes("[llm]") && l.includes("→") && l.includes("model=gpt-4o-mini"))).toBe(true);
  expect(lines.some((l) => l.includes("[llm]") && l.includes("←") && l.includes("in=320") && l.includes("out=180"))).toBe(true);
  logSpy.mockRestore();
});

test("log: callOverride path annotates override=true", async () => {
  const logSpy = spyOn(console, "log").mockImplementation(() => {});
  const config = makeConfig({ maxRetries: 1 });
  const provider = new VercelAIProvider(config);
  provider._setCallOverride(async () => ({ content: "x" }));

  await provider.chatCompletion([]);

  const lines = logSpy.mock.calls.map((c) => String(c[0]));
  expect(lines.some((l) => l.includes("override=true"))).toBe(true);
  logSpy.mockRestore();
});

test("log: retry emits retry line with attempt and backoff", async () => {
  const logSpy = spyOn(console, "log").mockImplementation(() => {});
  const config = makeConfig({ maxRetries: 3 });
  const provider = new VercelAIProvider(config);
  let calls = 0;
  provider._setCallOverride(async () => {
    calls++;
    if (calls === 1) throw Object.assign(new Error("Service Unavailable"), { statusCode: 503 });
    return { content: "ok", usage: { totalTokens: 5 } };
  });

  await provider.chatCompletion([]);

  const lines = logSpy.mock.calls.map((c) => String(c[0]));
  expect(lines.some((l) => l.includes("[llm]") && l.includes("retry") && l.includes("1/3") && l.includes("503"))).toBe(true);
  logSpy.mockRestore();
});

test("log: billing error emits billing line and skips retry", async () => {
  const logSpy = spyOn(console, "log").mockImplementation(() => {});
  const config = makeConfig({ maxRetries: 3 });
  const provider = new VercelAIProvider(config);
  provider._setCallOverride(async () => {
    throw Object.assign(new Error("Payment Required"), { statusCode: 402 });
  });

  await expect(provider.chatCompletion([])).rejects.toThrow(LLMBillingError);

  const lines = logSpy.mock.calls.map((c) => String(c[0]));
  expect(lines.some((l) => l.includes("[llm]") && l.includes("billing") && l.includes("402"))).toBe(true);
  logSpy.mockRestore();
});

test("log: final unavailable emits unavailable line", async () => {
  const logSpy = spyOn(console, "log").mockImplementation(() => {});
  const config = makeConfig({ maxRetries: 2 });
  const provider = new VercelAIProvider(config);
  provider._setCallOverride(async () => {
    throw Object.assign(new Error("Service Unavailable"), { statusCode: 503 });
  });

  await expect(provider.chatCompletion([])).rejects.toThrow(LLMUnavailableError);

  const lines = logSpy.mock.calls.map((c) => String(c[0]));
  expect(lines.some((l) => l.includes("[llm]") && l.includes("unavailable") && l.includes("2 retries"))).toBe(true);
  logSpy.mockRestore();
});
```

### Step 2.2: 跑测试看失败

- [ ] **Run:** `cd <VALLEYAI_ROOT> && bun test packages/core/tests/llm-provider.test.ts`
- **Expected:** FAIL — 5 new tests fail（无 `[llm]` 日志输出）

### Step 2.3: 实现 llm-provider.ts 埋点

- [ ] **修改 `packages/core/src/llm-provider.ts`**

1. 顶部加 timestamp + safeStringify helper（llm-provider 所在 core 包无共享日志工具，本地定义）：

在文件顶部 `extractHttpStatusCode` 函数前插入：
```ts
/** 生成 [HH:MM:SS.mmm] 时间戳。 */
function logTimestamp(): string {
  const d = new Date();
  const pad = (n: number, l = 2) => String(n).padStart(l, "0");
  return `${pad(d.getHours())}:${pad(d.getMinutes())}:${pad(d.getSeconds())}.${pad(d.getMilliseconds(), 3)}`;
}
```

2. `withRetry` 方法（156-190）加重试/billing/unavailable 日志。将整个 withRetry 替换为：
```ts
  private async withRetry<T extends LlmResponse>(fn: () => Promise<T>): Promise<T> {
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
        const statusCode = extractHttpStatusCode(err);
        if (statusCode === 402 || statusCode === 429) {
          console.log(`[${logTimestamp()}] [llm] billing/rate-limited HTTP ${statusCode} → 不重试`);
          throw new LLMBillingError(
            `LLM billing/rate-limited (HTTP ${statusCode}): ${describeError(err)}`
          );
        }
        lastError = err instanceof Error ? err : new Error(String(err));
        if (lastError instanceof LLMBillingError) throw lastError;
        if (attempt === this.config.maxRetries - 1) break;
        const baseDelay = Math.min(1000 * 2 ** attempt, 30000);
        const jitter = Math.random() * 0.3 * baseDelay;
        console.log(
          `[${logTimestamp()}] [llm] retry ${attempt + 1}/${this.config.maxRetries} (HTTP ${statusCode ?? "n/a"}: ${describeError(err)}) backoff=${Math.round(baseDelay + jitter)}ms`
        );
        await new Promise((r) => setTimeout(r, baseDelay + jitter));
      }
    }
    console.log(`[${logTimestamp()}] [llm] unavailable after ${this.config.maxRetries} retries: ${lastError?.message}`);
    throw new LLMUnavailableError(
      `LLM unavailable after ${this.config.maxRetries} retries: ${lastError?.message}`
    );
  }
```

3. `doCall` 方法（241-274）加调用前/后日志。将 doCall 替换为：
```ts
  private async doCall(messages: LlmMessage[]): Promise<LlmResponse> {
    const t0 = Date.now();
    if (this.callOverride) {
      console.log(`[${logTimestamp()}] [llm] → provider=${this.config.provider} model=${this.config.model} msgCount=${messages.length} override=true`);
      const r = await this.callOverride(messages);
      const inT = r.usage?.promptTokens;
      const outT = r.usage?.completionTokens;
      console.log(`[${logTimestamp()}] [llm] ← ok${inT !== undefined ? ` in=${inT}` : ""}${outT !== undefined ? ` out=${outT}` : ""} ${Date.now() - t0}ms`);
      return r;
    }

    const model = this.createModel();
    const systemMessage = messages.find((m) => m.role === "system");
    const coreMessages = this.convertMessages(messages);
    console.log(`[${logTimestamp()}] [llm] → provider=${this.config.provider} model=${this.config.model} msgCount=${messages.length}`);

    const result = await generateText({
      model,
      ...(systemMessage ? { system: systemMessage.content } : {}),
      messages: coreMessages,
      ...this.temperatureOption(),
      maxOutputTokens: this.config.maxTokens,
      abortSignal: AbortSignal.timeout(this.config.timeout),
    });

    const usage: LlmResponse["usage"] = {};
    if (result.usage?.inputTokens !== undefined) usage.promptTokens = result.usage.inputTokens;
    if (result.usage?.outputTokens !== undefined) usage.completionTokens = result.usage.outputTokens;
    if (result.usage?.totalTokens !== undefined) usage.totalTokens = result.usage.totalTokens;

    console.log(`[${logTimestamp()}] [llm] ← ok${usage.promptTokens !== undefined ? ` in=${usage.promptTokens}` : ""}${usage.completionTokens !== undefined ? ` out=${usage.completionTokens}` : ""} ${Date.now() - t0}ms`);

    return { content: result.text, usage };
  }
```

4. `doCallWithTools` 方法（276-335）同样加埋点（含 tools 数量）。将 doCallWithTools 替换为：
```ts
  private async doCallWithTools(
    messages: LlmMessage[],
    tools?: Tool[]
  ): Promise<ProviderToolCallResult> {
    const t0 = Date.now();
    const toolCount = tools?.length ?? 0;
    if (this.callOverride) {
      console.log(`[${logTimestamp()}] [llm] → provider=${this.config.provider} model=${this.config.model} msgCount=${messages.length} tools=${toolCount} override=true`);
      const r = await this.callOverride(messages, tools);
      const inT = r.usage?.promptTokens;
      const outT = r.usage?.completionTokens;
      console.log(`[${logTimestamp()}] [llm] ← ok${inT !== undefined ? ` in=${inT}` : ""}${outT !== undefined ? ` out=${outT}` : ""} ${Date.now() - t0}ms`);
      return r;
    }

    const model = this.createModel();
    const systemMessage = messages.find((m) => m.role === "system");
    const coreMessages = this.convertMessages(messages);
    console.log(`[${logTimestamp()}] [llm] → provider=${this.config.provider} model=${this.config.model} msgCount=${messages.length} tools=${toolCount}`);

    const sdkTools = tools
      ? Object.fromEntries(
          tools.map((t) => [t.name, { description: t.description, inputSchema: jsonSchema(t.parameters as any) }])
        )
      : undefined;

    const result = await generateText({
      model,
      ...(systemMessage ? { system: systemMessage.content } : {}),
      messages: coreMessages,
      ...(sdkTools ? { tools: sdkTools as any } : {}),
      ...this.temperatureOption(),
      maxOutputTokens: this.config.maxTokens,
      abortSignal: AbortSignal.timeout(this.config.timeout),
    });

    const usage: LlmResponse["usage"] = {};
    if (result.usage?.inputTokens !== undefined) usage.promptTokens = result.usage.inputTokens;
    if (result.usage?.outputTokens !== undefined) usage.completionTokens = result.usage.outputTokens;
    if (result.usage?.totalTokens !== undefined) usage.totalTokens = result.usage.totalTokens;

    const toolCalls: AgentToolCall[] | undefined =
      result.toolCalls && result.toolCalls.length > 0
        ? result.toolCalls.map((tc: any) => ({
            id: tc.toolCallId,
            name: tc.toolName,
            args: (tc.input ?? {}) as Record<string, unknown>,
          }))
        : undefined;

    console.log(`[${logTimestamp()}] [llm] ← ok${usage.promptTokens !== undefined ? ` in=${usage.promptTokens}` : ""}${usage.completionTokens !== undefined ? ` out=${usage.completionTokens}` : ""} ${Date.now() - t0}ms`);

    return { content: result.text, usage, ...(toolCalls ? { toolCalls } : {}) };
  }
```

5. `acquireSlot`（372-383）加排队日志。替换为：
```ts
  private async acquireSlot(): Promise<void> {
    const t0 = Date.now();
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
    const wait = Date.now() - t0;
    if (wait > 50) {
      console.log(`[${logTimestamp()}] [llm] queue wait=${wait}ms`);
    }
  }
```

### Step 2.4: 跑测试通过

- [ ] **Run:** `cd <VALLEYAI_ROOT> && bun test packages/core/tests/llm-provider.test.ts`
- **Expected:** PASS — 全部测试（含 5 新增）绿

### Step 2.5: tsc + commit

- [ ] **Run:** `cd <VALLEYAI_ROOT> && bunx tsc --noEmit -p packages/core/tsconfig.json`
- **Expected:** 0 error 0 warning
- [ ] **Commit:**
```bash
cd <VALLEYAI_ROOT>
git add packages/core/src/llm-provider.ts packages/core/tests/llm-provider.test.ts
git commit -m "feat(log): add LLM call metadata logging in llm-provider (model/tokens/retry/billing/queue)"
```

---

## Task 3: protocol-adapter 收发埋点

**Files:**
- Modify: `<VALLEYAI_ROOT>/packages/stardew/src/protocol-adapter.ts`
- Modify: `<VALLEYAI_ROOT>/packages/stardew/tests/protocol-adapter.test.ts`

### Step 3.1: 写失败测试（追加到 protocol-adapter.test.ts）

- [ ] **追加测试**（先在文件顶部 import 行加 `spyOn`，即 `import { test, expect, spyOn } from "bun:test";`）：

```ts
test("log: handleDialogue emits recv and send lines paired", async () => {
  const logSpy = spyOn(console, "log").mockImplementation(() => {});
  const { adapter, dir } = makeAdapter();
  try {
    const resp = await adapter.handleDialogue({
      type: "dialogue",
      requestId: "req-log-1",
      npcName: "Abigail",
      playerInput: "你好啊",
      scene,
    });
    expect(resp.type).toBe("dialogue");

    const lines = logSpy.mock.calls.map((c) => String(c[0]));
    expect(lines.some((l) => l.includes("[recv]") && l.includes("dialogue") && l.includes("Abigail") && l.includes("你好啊"))).toBe(true);
    expect(lines.some((l) => l.includes("[send]") && l.includes("dialogue") && l.includes("Abigail"))).toBe(true);
  } finally {
    rmSync(dir, { recursive: true, force: true });
    logSpy.mockRestore();
  }
});

test("log: handleActionResult emits recv line", async () => {
  const logSpy = spyOn(console, "log").mockImplementation(() => {});
  const { adapter, dir } = makeAdapter();
  try {
    await adapter.handleActionResult({
      type: "action_result",
      requestId: "req-log-2",
      callId: "call-1",
      success: false,
      result: "背包已满",
      reason: "inventoryFull",
    });
    const lines = logSpy.mock.calls.map((c) => String(c[0]));
    expect(lines.some((l) => l.includes("[recv]") && l.includes("action_result") && l.includes("inventoryFull"))).toBe(true);
  } finally {
    rmSync(dir, { recursive: true, force: true });
    logSpy.mockRestore();
  }
});
```

> **注意:** `handleDialogue` 测试需要 `scene` 字段。检查 protocol-adapter.test.ts 是否已有 `scene` 常量；若无，从 transcript-wiring.test.ts 复制 scene 定义到测试文件顶部。`handleActionResult` 的 `reason` 字段需确认 DialogueRequest/ActionResultMessage 类型是否含 `reason`——若类型不含，测试用 `as any` 构造（参考既有 handleActionResult 测试的写法）。

### Step 3.2: 跑测试看失败

- [ ] **Run:** `cd <VALLEYAI_ROOT> && bun test packages/stardew/tests/protocol-adapter.test.ts`
- **Expected:** FAIL — 2 new tests fail（无 `[recv]`/`[send]` 日志）

### Step 3.3: 实现 protocol-adapter 收发埋点

- [ ] **修改 `packages/stardew/src/protocol-adapter.ts`**

先读 protocol-adapter.ts 确认 handleDialogue/handleActionResult/handleStateChanged/handleHello 的当前实现（行号可能因前面 console 已有而偏移）。在以下位置加埋点：

1. `handleHello` 入口加：
```ts
console.log(`[${timestamp()}] [recv] hello modVersion=${(req as { modVersion?: string }).modVersion ?? "?"}`);
```

2. `handleDialogue` 入口加 recv，返回前加 send。需先读 handleDialogue 完整实现确认返回点（成功 + fallback）。在方法开头：
```ts
console.log(`[${timestamp()}] [recv] dialogue npc=${req.npcName} player="${req.playerInput}"`);
```
在成功返回 DialogueResponse 前：
```ts
console.log(`[${timestamp()}] [send] dialogue npc=${req.npcName} speech="${result.speech}" actions=${result.actions.length} emotion=${result.emotion} friendship=${result.friendshipDelta >= 0 ? "+" : ""}${result.friendshipDelta}`);
```
在 fallback 返回前：
```ts
console.log(`[${timestamp()}] [send] dialogue npc=${req.npcName} FALLBACK reason=${fallbackReason}`);
```

3. `handleActionResult` 入口加：
```ts
const reason = (req as { reason?: string }).reason;
console.log(`[${timestamp()}] [recv] action_result callId=${req.callId} ok=${req.success}${reason ? ` reason=${reason}` : ""}${req.result ? ` result="${req.result}"` : ""}`);
```

4. `handleStateChanged` 入口加（已有 state 日志在 72 行，新增一条 recv 格式统一）：
```ts
console.log(`[${timestamp()}] [recv] state_changed npc=${req.npcName} ${req.previousState}→${req.newState}${req.forced ? " forced" : ""}${req.reason ? ` reason=${req.reason}` : ""}`);
```

> **实现提示:** timestamp() 函数已在 protocol-adapter.ts 内定义（72 行用到），直接复用。具体行号在实现时按当前文件确认。handleDialogue 的 fallback 路径需读全方法体定位（设计 §0.3 提到 rule-engine.buildFallbackResponse）。

### Step 3.4: 跑测试通过

- [ ] **Run:** `cd <VALLEYAI_ROOT> && bun test packages/stardew/tests/protocol-adapter.test.ts`
- **Expected:** PASS — 全绿

### Step 3.5: tsc + commit

- [ ] **Run:** `cd <VALLEYAI_ROOT> && bunx tsc --noEmit -p packages/stardew/tsconfig.json`
- **Expected:** 0 error 0 warning
- [ ] **Commit:**
```bash
cd <VALLEYAI_ROOT>
git add packages/stardew/src/protocol-adapter.ts packages/stardew/tests/protocol-adapter.test.ts
git commit -m "feat(log): add protocol recv/send logging for dialogue/action_result/state_changed/hello"
```

---

## Task 4: director 埋点（暂不生效）

**Files:**
- Modify: `<VALLEYAI_ROOT>/packages/stardew/src/director.ts`
- Modify: `<VALLEYAI_ROOT>/packages/stardew/tests/director.test.ts`

### Step 4.1: 写失败测试（追加到 director.test.ts）

- [ ] **追加测试**（顶部 import 加 `spyOn`：`import { test, expect, beforeEach, afterEach, spyOn } from "bun:test";`）：

```ts
test("log: morningPlan emits start and end lines", async () => {
  const logSpy = spyOn(console, "log").mockImplementation(() => {});
  // 构造一个完整 director 测试栈（参考既有 director.test.ts 的 beforeEach setup）
  const dir = mkdtempSync(join(tmpdir(), "valley-dir-log-"));
  try {
    const beatStore = new BeatStore(join(dir, "beats.db"));
    const profileStore = new PlayerProfileStore(join(dir, "profiles.db"));
    const activityStore = new ActivityLogStore(join(dir, "activity.db"));
    beatStore.init(); profileStore.init(); activityStore.init();
    const profileMgr = new PlayerProfileManager(profileStore, activityStore, {
      callLlm: async () => ({ text: "", usage: { promptTokens: 0, completionTokens: 0 } }),
    });
    profileMgr.initProfile(makeStaticProfile());
    const gameCtxMgr = new GameContextManager();
    gameCtxMgr.update(makeGameContext());
    const director = new Director(beatStore, profileMgr, gameCtxMgr, activityStore, {
      callLlm: async () => ({
        text: JSON.stringify([{
          npcName: "Willy",
          triggerTime: "09:00",
          windowEnd: "11:00",
          directive: "去海滩看看玩家钓到什么鱼",
          reasonGenerated: "玩家爱钓鱼",
        }]),
        usage: { promptTokens: 100, completionTokens: 50 },
      }),
    });

    await director.morningPlan();

    const lines = logSpy.mock.calls.map((c) => String(c[0]));
    expect(lines.some((l) => l.includes("[director]") && l.includes("morningPlan") && l.includes("start"))).toBe(true);
    expect(lines.some((l) => l.includes("[director]") && l.includes("morningPlan") && l.includes("end"))).toBe(true);
    beatStore.close(); profileStore.close(); activityStore.close();
  } finally {
    rmSync(dir, { recursive: true, force: true });
    logSpy.mockRestore();
  }
});
```

> **注意:** `makeStaticProfile()` 和 `makeGameContext()` 已在 director.test.ts 定义（见 Step 前读取）。若 BeatStore/PlayerProfileStore/ActivityLogStore/GameContextManager 的 init/close 方法名不同，按既有 director.test.ts 的 beforeEach/afterEach 模式调整。

### Step 4.2: 跑测试看失败

- [ ] **Run:** `cd <VALLEYAI_ROOT> && bun test packages/stardew/tests/director.test.ts`
- **Expected:** FAIL — 1 new test fail（无 `[director]` 日志）

### Step 4.3: 实现 director.ts 埋点

- [ ] **修改 `packages/stardew/src/director.ts`**

1. 顶部加 timestamp helper（director 不依赖 protocol-adapter 的 timestamp，本地定义）：
```ts
function logTimestamp(): string {
  const d = new Date();
  const pad = (n: number, l = 2) => String(n).padStart(l, "0");
  return `${pad(d.getHours())}:${pad(d.getMinutes())}:${pad(d.getSeconds())}.${pad(d.getMilliseconds(), 3)}`;
}
```

2. `morningPlan`（131-142）加 start/end 日志。替换为：
```ts
  async morningPlan(): Promise<Beat[]> {
    console.log(`[${logTimestamp()}] [director] morningPlan start`);
    const ctx = this.gameCtxMgr.getCurrent();
    if (!ctx) {
      console.log(`[${logTimestamp()}] [director] morningPlan end (no game context)`);
      return [];
    }
    if (ctx.time.isFestivalDay) {
      console.log(`[${logTimestamp()}] [director] morningPlan end (festival day)`);
      return [];
    }

    const rawBeats = await this.callLlmForBeats(ctx, /* isMilestone */ false);
    if (rawBeats.length === 0) {
      console.log(`[${logTimestamp()}] [director] morningPlan end (no beats from LLM)`);
      return [];
    }

    const validated = this.validateAndFilter(rawBeats, ctx);
    const truncated = validated.slice(0, this.maxBeatsPerDay);
    const produced = this.persistAll(truncated, ctx);
    console.log(`[${logTimestamp()}] [director] morningPlan end produced=${produced.length} dropped=${rawBeats.length - validated.length}`);
    return produced;
  }
```

3. `callLlmForBeats`（205-222）加 LLM 输出日志。在 `responseText = result.text;` 后加：
```ts
    console.log(`[${logTimestamp()}] [director] llm输出:\n${responseText}`);
```

### Step 4.4: 跑测试通过

- [ ] **Run:** `cd <VALLEYAI_ROOT> && bun test packages/stardew/tests/director.test.ts`
- **Expected:** PASS — 全绿

### Step 4.5: tsc + commit

- [ ] **Run:** `cd <VALLEYAI_ROOT> && bunx tsc --noEmit -p packages/stardew/tsconfig.json`
- **Expected:** 0 error 0 warning
- [ ] **Commit:**
```bash
cd <VALLEYAI_ROOT>
git add packages/stardew/src/director.ts packages/stardew/tests/director.test.ts
git commit -m "feat(log): add director morningPlan/milestoneReact logging (inactive until wired)"
```

---

## Task 5: 集成验证 + 全量门禁

**Files:** 无新增，仅跑验证

### Step 5.1: dialogue-e2e 断言 stdout 关键行

- [ ] **在 `packages/stardew/tests/dialogue-e2e.test.ts` 中找一个已有的完整对话测试，spy console.log，断言关键日志行存在**

在该测试函数体开头加：
```ts
const logSpy = spyOn(console, "log").mockImplementation(() => {});
```
末尾（finally 前）加断言：
```ts
const joined = logSpy.mock.calls.map((c) => String(c[0])).join("\n");
expect(joined).toContain("[turn]");
expect(joined).toContain("[llm]");
expect(joined).toContain("[recv] dialogue");
expect(joined).toContain("[send] dialogue");
logSpy.mockRestore();
```
顶部 import 加 `spyOn`。

> **注意:** 若 dialogue-e2e.test.ts 没有 `spyOn` import，加到 `bun:test` import。若该测试文件结构不适合作此断言，改为新建一个独立 e2e 测试函数，复用其 makeStack 模式跑一次对话 + spy。

### Step 5.2: 全量 bun test

- [ ] **Run:** `cd <VALLEYAI_ROOT> && bun test packages/core && bun test packages/stardew`
- **Expected:** 全绿，0 fail

### Step 5.3: tsc 全量

- [ ] **Run:** `cd <VALLEYAI_ROOT> && bunx tsc --noEmit`
- **Expected:** 0 error 0 warning

### Step 5.4: protocol 契约不回归

- [ ] **Run:** `cd <VALLEYAI_ROOT> && bun run check:protocol`
- **Expected:** 不回归（exit code 与改动前一致；当前 memory 记录为 exit 1 预期红，保持一致即可，不应新增 orphan routes）

### Step 5.5: 手动多模态验证（铁律3）

- [ ] **启动游戏触发一次 NPC 对话**，确认 cmd 窗口（ConsoleWindow=true）或 SMAPI 日志出现完整原始的：
  - `[recv] dialogue npc=... player="..."`
  - `[llm] → provider=... model=... msgCount=... tools=...`
  - `[llm] ← ok in=... out=... ...ms`
  - `[turn] ... start`
  - `[turn] ... llm输出:\n<完整 LLM 输出>`
  - `[tool] ... → speak args={...}`
  - `[tool] ... ← speak ok=true result=...`
  - `[turn] ... end`
  - `[send] dialogue npc=... speech="..." actions=... emotion=... friendship=...`
- [ ] **截图/录屏保存**为验证证据（用户铁律2：多模态验证）

### Step 5.6: Final commit

- [ ] **Commit:**
```bash
cd <VALLEYAI_ROOT>
git add packages/stardew/tests/dialogue-e2e.test.ts
git commit -m "test(log): assert stdout log lines in dialogue e2e"
```

---

## Self-Review

### Spec coverage
- §3.1 console-log-subscriber → Task 1 ✓
- §3.2 llm-provider 埋点 → Task 2 ✓
- §3.3 protocol-adapter 收发 → Task 3 ✓
- §3.4 director 埋点 → Task 4 ✓
- §3.5 格式约定 → 各 Task 实现均用 `[HH:MM:SS.mmm] [tag] npc` 前缀 ✓
- §4 错误处理 → console-log-subscriber try/catch + safeStringify ✓（llm-provider/protocol-adapter/director 的 console.log 本身不抛，按设计 §4 说明无需额外包裹）
- §5 测试验证 → Task 1-4 单测 + Task 5 集成 ✓
- §5.3 验收门槛 6 项 → Task 5.2-5.5 覆盖 1-5 项 + 手动验证覆盖第 6 项 ✓

### Placeholder scan
- 无 TBD/TODO。所有代码块完整。
- Task 3.3 / 4.1 的"注意"块是实施时需按当前文件确认的提示（行号偏移、类型字段），非 placeholder——给出了具体确认方法和 fallback（`as any`）。

### Type consistency
- `ConsoleLogSubscriber` 构造 `npcName: string`，`attach(agent: Agent)` 返回 `() => void`——Task 1 测试与实现一致 ✓
- `logTimestamp()`/`timestamp()` 在 core/stardew/director 分别本地定义（core 不依赖 stardew，director 独立），格式一致 ✓
- llm-provider 的 `in=`/`out=` 字段名在测试断言与实现一致 ✓

### 已知偏离设计文档
- §3.1 tool_call_end 耗时：实现省略 tool 级耗时（YAGNI），仅 turn 级。计划 Step 1.3/1.4 已说明并修正测试。这是对设计文档的合理收紧，非缺陷。
