import { generateText, jsonSchema } from "ai";
import type { LanguageModel, CoreMessage } from "ai";
import { createOpenAI } from "@ai-sdk/openai";
import { createAnthropic } from "@ai-sdk/anthropic";
import { createGoogleGenerativeAI } from "@ai-sdk/google";
import { LLMConfig, resolveConfig } from "./llm-config";
import { TokenBudgetManager } from "./token-budget";
import { PerformanceMonitor } from "./performance-monitor";
import type { LlmMessage, AgentToolCall } from "./types";
import type { Tool } from "./tool";

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

/**
 * Token 预算耗尽（2026-08-17 审计 B4）。预算用尽后重试 LLM 只会继续烧真实 API 调用
 * 且必然再次失败——withRetry 必须像 billing 错误一样直接抛出，不做重试。
 */
export class LLMBudgetError extends Error {
  constructor(message: string) {
    super(message);
    this.name = "LLMBudgetError";
  }
}

/**
 * HTTP 状态码提取：从 ai-sdk generateText 抛出的错误中识别 statusCode。
 *
 * ai-sdk 抛 APICallError/HTTPError 时，错误对象上挂 `statusCode` 数字字段；
 * 部分场景会被外层 wrap，挂在 `cause.statusCode`。本辅助函数兼容两种。
 *
 * 返回 undefined 表示非 HTTP 类错误（如 JSON 解析失败、网络断连）。
 */
/** 生成 [HH:MM:SS.mmm] 时间戳。 */
function logTimestamp(): string {
  const d = new Date();
  const pad = (n: number, l = 2) => String(n).padStart(l, "0");
  return `${pad(d.getHours())}:${pad(d.getMinutes())}:${pad(d.getSeconds())}.${pad(d.getMilliseconds(), 3)}`;
}

function extractHttpStatusCode(err: unknown): number | undefined {
  if (typeof err !== "object" || err === null) return undefined;
  const e = err as Record<string, unknown>;
  if (typeof e.statusCode === "number") return e.statusCode;
  if (typeof e.status === "number") return e.status;
  const cause = e.cause;
  if (cause && typeof cause === "object") {
    const c = cause as Record<string, unknown>;
    if (typeof c.statusCode === "number") return c.statusCode;
    if (typeof c.status === "number") return c.status;
  }
  return undefined;
}

/**
 * 描述信息提取：分类后的错误信息携带原始 message，便于排障。
 */
function describeError(err: unknown): string {
  if (err instanceof Error) return err.message;
  return String(err);
}

/**
 * 第三方 OpenAI 兼容端点的请求体改写：
 * @ai-sdk/openai v2 对非 gpt-* 模型一律按新 OpenAI 协议发请求（developer 角色 /
 * max_completion_tokens / 工具 strict 标记），商汤等端点直接 400。改写回老式协议：
 *   - messages[].role "developer" → "system"
 *   - max_completion_tokens → max_tokens（无冲突时）
 *   - tools[].function.strict 删除
 * 解析失败时原样发送（不因改写失败阻断调用）。
 */
function createCompatFetch(): (input: Parameters<typeof fetch>[0], init?: RequestInit) => Promise<Response> {
  return async (input, init) => {
    let requestInit = init;
    if (init?.body && typeof init.body === "string") {
      try {
        const body = JSON.parse(init.body) as Record<string, unknown>;
        if (Array.isArray(body.messages)) {
          for (const m of body.messages as Array<{ role?: string }>) {
            if (m.role === "developer") m.role = "system";
          }
        }
        if (typeof body.max_completion_tokens === "number") {
          body.max_tokens = body.max_tokens ?? body.max_completion_tokens;
          delete body.max_completion_tokens;
        }
        if (Array.isArray(body.tools)) {
          for (const t of body.tools as Array<{ function?: { strict?: unknown } }>) {
            if (t.function && "strict" in t.function) delete t.function.strict;
          }
        }
        requestInit = { ...init, body: JSON.stringify(body) };
      } catch {
        // JSON 解析失败 → 原样转发
      }
    }
    return fetch(input, requestInit);
  };
}

export interface LlmResponse {
  content: string;
  usage?: {
    promptTokens?: number;
    completionTokens?: number;
    totalTokens?: number;
  };
}

export interface ProviderToolCallResult extends LlmResponse {
  toolCalls?: AgentToolCall[];
}

export interface LlmJsonResponse<T = unknown> {
  parsed: T;
  raw: string;
  usage?: { totalTokens?: number };
}

type CallOverride = (messages: LlmMessage[], tools?: Tool[]) => Promise<ProviderToolCallResult>;

export interface ILLMProvider {
  chatCompletion(messages: LlmMessage[]): Promise<LlmResponse>;
  chatCompletionJson<T = unknown>(messages: LlmMessage[]): Promise<LlmJsonResponse<T>>;
  preWarm(): Promise<void>;
}

export class VercelAIProvider implements ILLMProvider {
  readonly config: Required<LLMConfig>;
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

  async chatWithTools(messages: LlmMessage[], tools?: Tool[]): Promise<ProviderToolCallResult> {
    return this.withRetry(async () => {
      await this.acquireSlot();
      try {
        return await this.perf.measure("llm_call", () => this.doCallWithTools(messages, tools));
      } finally {
        this.releaseSlot();
      }
    });
  }

  async chatCompletionJson<T = unknown>(messages: LlmMessage[]): Promise<LlmJsonResponse<T>> {
    const response = await this.chatCompletion(messages);
    const stripped = this._stripThinking(response.content);
    let parsed: T;
    try {
      parsed = JSON.parse(stripped) as T;
    } catch {
      throw new Error(`LLM returned invalid JSON: ${stripped.slice(0, 200)}`);
    }
    const result: LlmJsonResponse<T> = { parsed, raw: response.content };
    if (response.usage?.totalTokens !== undefined) {
      result.usage = { totalTokens: response.usage.totalTokens };
    }
    return result;
  }

  async preWarm(): Promise<void> {
    await Promise.resolve();
  }

  private async withRetry<T extends LlmResponse>(fn: () => Promise<T>): Promise<T> {
    let lastError: Error | null = null;
    for (let attempt = 0; attempt < this.config.maxRetries; attempt++) {
      try {
        const result = await fn();
        if (result.usage?.totalTokens) {
          if (!this.budget.consume(result.usage.totalTokens)) {
            // 预算耗尽不可重试：重试只会继续烧真实 API 调用且必然再次失败（审计 B4）。
            throw new LLMBudgetError(
              `Token budget exceeded: used ${this.budget.getUsed()}, budget ${this.config.tokenBudget}`
            );
          }
        }
        return result;
      } catch (err) {
        // E5: HTTP 错误分类，使 rule-engine 三档人设兜底全部可达。
        // 402/429 → LLMBillingError（立即抛出，不重试，billing 失败重试无意义）
        // 5xx    → 走下方 max-retries 兜底，最终包装为 LLMUnavailableError
        const statusCode = extractHttpStatusCode(err);
        if (statusCode === 402 || statusCode === 429) {
          console.log(`[${logTimestamp()}] [llm] billing/rate-limited HTTP ${statusCode} → 不重试`);
          throw new LLMBillingError(
            `LLM billing/rate-limited (HTTP ${statusCode}): ${describeError(err)}`
          );
        }
        lastError = err instanceof Error ? err : new Error(String(err));
        if (lastError instanceof LLMBillingError) throw lastError;
        // LLMUnavailableError 表示重试已耗尽，不应再次重试
        if (lastError instanceof LLMUnavailableError) throw lastError;
        // LLMBudgetError 表示预算耗尽，重试只会烧钱且必然失败
        if (lastError instanceof LLMBudgetError) throw lastError;
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

  private convertMessages(messages: LlmMessage[]): CoreMessage[] {
    return messages
      .filter((m) => m.role !== "system")
      .map((m) => {
        if (m.role === "user") {
          return { role: "user", content: m.content };
        }
        if (m.role === "assistant") {
          if (m.toolCalls && m.toolCalls.length > 0) {
            return {
              role: "assistant",
              content: [
                ...(m.content ? [{ type: "text" as const, text: m.content }] : []),
                ...m.toolCalls.map((tc) => ({
                  type: "tool-call" as const,
                  toolCallId: tc.id,
                  toolName: tc.functionName,
                  input: JSON.parse(tc.args),
                })),
              ],
            };
          }
          return { role: "assistant", content: m.content };
        }
        return {
          role: "tool",
          content: [{
            type: "tool-result" as const,
            toolCallId: m.toolCallId,
            toolName: m.toolName ?? m.toolCallId ?? "",
            output: { type: "text" as const, value: m.content },
          }],
        };
      }) as CoreMessage[];
  }

  /**
   * 推理模型（MiniMax-M2、o1/o3、DeepSeek-R1 等）不接受 temperature 参数，
   * 传入会触发 AI SDK 警告且被忽略。按模型名识别并跳过。
   */
  private isReasoningModel(): boolean {
    const m = (this.config.model ?? "").toLowerCase();
    return /minimax-m2|reasoning|(^|[^a-z0-9])o[13]([^a-z0-9]|$)|-r1\b/.test(m);
  }

  private temperatureOption(): { temperature?: number } {
    return this.isReasoningModel() ? {} : { temperature: this.config.temperature };
  }

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
    if (result.usage?.inputTokens !== undefined) {
      usage.promptTokens = result.usage.inputTokens;
    }
    if (result.usage?.outputTokens !== undefined) {
      usage.completionTokens = result.usage.outputTokens;
    }
    if (result.usage?.totalTokens !== undefined) {
      usage.totalTokens = result.usage.totalTokens;
    }

    console.log(`[${logTimestamp()}] [llm] ← ok${usage.promptTokens !== undefined ? ` in=${usage.promptTokens}` : ""}${usage.completionTokens !== undefined ? ` out=${usage.completionTokens}` : ""} ${Date.now() - t0}ms`);

    return {
      content: result.text,
      usage,
    };
  }

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
          tools.map((t) => [
            t.name,
            {
              description: t.description,
              inputSchema: jsonSchema(t.parameters as any),
            },
          ])
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
    if (result.usage?.inputTokens !== undefined) {
      usage.promptTokens = result.usage.inputTokens;
    }
    if (result.usage?.outputTokens !== undefined) {
      usage.completionTokens = result.usage.outputTokens;
    }
    if (result.usage?.totalTokens !== undefined) {
      usage.totalTokens = result.usage.totalTokens;
    }

    const toolCalls: AgentToolCall[] | undefined =
      result.toolCalls && result.toolCalls.length > 0
        ? result.toolCalls.map((tc: any) => ({
            id: tc.toolCallId,
            name: tc.toolName,
            args: (tc.input ?? {}) as Record<string, unknown>,
          }))
        : undefined;

    console.log(`[${logTimestamp()}] [llm] ← ok${usage.promptTokens !== undefined ? ` in=${usage.promptTokens}` : ""}${usage.completionTokens !== undefined ? ` out=${usage.completionTokens}` : ""} ${Date.now() - t0}ms`);

    return {
      content: result.text,
      usage,
      ...(toolCalls ? { toolCalls } : {}),
    };
  }

  private createModel(): LanguageModel {
    const baseURL = this.config.baseUrl || undefined;
    switch (this.config.provider) {
      case "openai":
      case "deepseek":
      case "lmstudio":
      case "openrouter":
      case "minimax":
      case "moonshot":
      case "zhipu":
      case "baichuan":
      case "qwen":
      case "sensenova":
      case "mimo": {
        // 商汤/小米 Token Plan 等第三方兼容端点只认老式 OpenAI 协议：
        // @ai-sdk/openai v2 对所有非 gpt-* 模型按"推理模型"处理，会发 developer 角色 +
        // max_completion_tokens + strict 工具标记，商汤返回 "inference request is invalid"。
        // 用自定义 fetch 把请求体改写回 max_tokens + system 角色再发出去。
        const compat = createOpenAI({
          apiKey: this.config.apiKey,
          ...(baseURL ? { baseURL } : {}),
          fetch: createCompatFetch() as unknown as typeof fetch,
        });
        return compat.chat(this.config.model) as LanguageModel;
      }
      case "custom": {
        const openai = createOpenAI({
          apiKey: this.config.apiKey,
          ...(baseURL ? { baseURL } : {}),
        });
        return openai.chat(this.config.model) as LanguageModel;
      }
      case "anthropic": {
        const anthropic = createAnthropic({
          apiKey: this.config.apiKey,
          ...(baseURL ? { baseURL } : {}),
        });
        return anthropic(this.config.model) as LanguageModel;
      }
      case "google": {
        const google = createGoogleGenerativeAI({
          apiKey: this.config.apiKey,
          ...(baseURL ? { baseURL } : {}),
        });
        return google(this.config.model) as LanguageModel;
      }
      default: {
        const openai = createOpenAI({ apiKey: this.config.apiKey });
        return openai.chat(this.config.model) as LanguageModel;
      }
    }
  }

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

  private releaseSlot(): void {
    this.activeCalls--;
    const next = this.callQueue.shift();
    if (next) next();
  }
}
