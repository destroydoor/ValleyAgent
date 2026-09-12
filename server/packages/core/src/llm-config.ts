export type LLMProviderType =
  | "openai"
  | "anthropic"
  | "google"
  | "deepseek"
  | "openrouter"
  | "lmstudio"
  | "minimax"
  | "moonshot"
  | "zhipu"
  | "baichuan"
  | "qwen"
  | "sensenova"
  | "mimo"
  | "custom";

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
    // 规范化小写：createModel 的 switch 区分大小写（2026-09-10 soak 实证——
    // "MiniMax" 落 default 分支无 baseURL 打 api.openai.com，全对话 FALLBACK）。
    provider: config.provider.toLowerCase() as LLMProviderType,
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
