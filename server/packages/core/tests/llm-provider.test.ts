import { test, expect, spyOn } from "bun:test";
import { LLMConfig } from "../src/llm-config";
import { VercelAIProvider, LLMBillingError, LLMUnavailableError, LLMBudgetError } from "../src/llm-provider";

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

  // chatCompletionJson 的 T 缺省为 unknown（JSON.parse 结果），测试按契约显式声明形状
  const result = await provider.chatCompletionJson<{ targetState: string; reason: string }>([]);
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

test("budget exhaustion throws LLMBudgetError without retrying (审计 B4)", async () => {
  // 预算耗尽后重试只会继续烧真实 API 调用且必然再次失败——必须直接抛不可重试错误。
  // maxRetries=3 时若被当可重试错误，override 会被调用 3 次（真实 API 3 次计费）。
  const config = makeConfig({ tokenBudget: 50, maxRetries: 3 });
  const provider = new VercelAIProvider(config);

  let calls = 0;
  provider._setCallOverride(async () => {
    calls++;
    return { content: "ok", usage: { totalTokens: 60 } };
  });

  await expect(provider.chatCompletion([])).rejects.toThrow(LLMBudgetError);
  expect(calls).toBe(1); // 未重试：预算耗尽是确定性失败
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

test("chatWithTools returns toolCalls from LLM response", async () => {
  const config = makeConfig({ maxRetries: 1 });
  const provider = new VercelAIProvider(config);

  provider._setCallOverride(async () => {
    return {
      content: "Let me speak",
      usage: { totalTokens: 10 },
      toolCalls: [{ id: "call_1", name: "speak", args: { text: "hi" } }],
    };
  });

  const result = await provider.chatWithTools(
    [{ role: "user", content: "say hi" }],
    undefined
  );
  expect(result.content).toBe("Let me speak");
  expect(result.toolCalls).toBeDefined();
  expect(result.toolCalls).toHaveLength(1);
  // noUncheckedIndexedAccess：先取出首元素，避免 [0] 带 undefined 直接取属性
  const firstCall = result.toolCalls![0]!;
  expect(firstCall.id).toBe("call_1");
  expect(firstCall.name).toBe("speak");
  expect(firstCall.args).toEqual({ text: "hi" });
});

// ─── E5: HTTP error classification (402/429 → LLMBillingError, 5xx → LLMUnavailableError) ───

test("HTTP 402 from generateText is classified as LLMBillingError without retry", async () => {
  const config = makeConfig({ maxRetries: 3 });
  const provider = new VercelAIProvider(config);

  let calls = 0;
  provider._setCallOverride(async () => {
    calls++;
    // Simulate ai-sdk APICallError shape: error carries statusCode
    throw Object.assign(new Error("Insufficient credits"), { statusCode: 402 });
  });

  await expect(provider.chatCompletion([])).rejects.toThrow(LLMBillingError);
  expect(calls).toBe(1); // billing errors skip retry — retrying 402 is pointless
});

test("HTTP 429 from generateText is classified as LLMBillingError without retry", async () => {
  const config = makeConfig({ maxRetries: 3 });
  const provider = new VercelAIProvider(config);

  let calls = 0;
  provider._setCallOverride(async () => {
    calls++;
    throw Object.assign(new Error("Rate limited"), { statusCode: 429 });
  });

  await expect(provider.chatCompletion([])).rejects.toThrow(LLMBillingError);
  expect(calls).toBe(1);
});

test("HTTP 503 from generateText is retried then wrapped as LLMUnavailableError", async () => {
  const config = makeConfig({ maxRetries: 2 });
  const provider = new VercelAIProvider(config);

  let calls = 0;
  provider._setCallOverride(async () => {
    calls++;
    throw Object.assign(new Error("Service Unavailable"), { statusCode: 503 });
  });

  await expect(provider.chatCompletion([])).rejects.toThrow(LLMUnavailableError);
  expect(calls).toBe(2); // retried up to maxRetries, then wrapped
});

test("HTTP 402 nested in error.cause is also detected", async () => {
  const config = makeConfig({ maxRetries: 3 });
  const provider = new VercelAIProvider(config);

  const inner = Object.assign(new Error("Payment Required"), { statusCode: 402 });
  provider._setCallOverride(async () => {
    throw Object.assign(new Error("wrapped"), { cause: inner });
  });

  await expect(provider.chatCompletion([])).rejects.toThrow(LLMBillingError);
});

test("chatWithTools also classifies HTTP 402 as LLMBillingError", async () => {
  const config = makeConfig({ maxRetries: 3 });
  const provider = new VercelAIProvider(config);

  provider._setCallOverride(async () => {
    throw Object.assign(new Error("Payment Required"), { statusCode: 402 });
  });

  await expect(provider.chatWithTools([], undefined)).rejects.toThrow(LLMBillingError);
});

// __TASK2_LOG_TESTS__

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

// 2026-09-10 修复回归：provider 大小写必须规范化——"MiniMax" 曾在 createModel 的
// switch 里落 default 分支（无 baseURL → api.openai.com），全部对话 FALLBACK。
test("provider string is normalized to lowercase", async () => {
  const config = makeConfig({
    provider: "MiniMax" as LLMConfig["provider"],
    baseUrl: "https://api.minimax.chat/v1",
  });
  const provider = new VercelAIProvider(config);
  // override 只负责把内部 config.provider 值读出来断言
  let seenProvider = "";
  provider._setCallOverride(async () => {
    seenProvider = provider.config.provider;
    return { content: "ok" };
  });
  const res = await provider.chatCompletion([{ role: "user", content: "hi" }]);
  expect(seenProvider).toBe("minimax");
  expect(res.content).toBe("ok");
});
