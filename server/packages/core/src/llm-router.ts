import { readFileSync } from "node:fs";
import { VercelAIProvider, LLMBillingError } from "./llm-provider";
import type { LLMConfig } from "./llm-config";
import type { LlmMessage } from "./types";
import type { Tool } from "./tool";

/** 角色类型：导演 / 主角 NPC / 普通 NPC */
export type LlmRole = "director" | "protagonist" | "npc";

/** 单个 provider 配置（主或备） */
export interface RoleProviderConfig {
  provider: string;
  apiKey: string;
  model: string;
  baseUrl: string;
  timeoutMs?: number;
  maxRetries?: number;
  maxConcurrency?: number;
}

/** 一个角色的完整配置：主 + 可选备用链 */
export interface RoleConfig {
  primary: RoleProviderConfig;
  /**
   * 旧式单个备用（等价于 fallbacks 首位）。
   * 保留是为了版本兼容：旧 exe 只认这个字段，新写入端总是同时写 fallback+fallbacks。
   */
  fallback?: RoleProviderConfig | null;
  /**
   * 备用链（billing 错误时按序回退）。
   * 与 fallback 同时出现时合并，fallback 排在最前。
   */
  fallbacks?: RoleProviderConfig[];
}

/** runtime JSON 根结构 */
export interface LlmRouterConfig {
  version: number;
  roles: Record<LlmRole, RoleConfig>;
  protagonistNpcs: string[];
  enableProtagonistMapping: boolean;
}

/**
 * 多 LLM Provider 路由器：按角色分发，billing 错误时回退。
 * 每个 provider 实例独立预算/熔断/并发池。
 */
export class LlmRouter {
  private readonly providers = new Map<string, VercelAIProvider>();
  private readonly fallbackChains = new Map<LlmRole, VercelAIProvider[]>();
  private readonly config: LlmRouterConfig;

  constructor(config: LlmRouterConfig) {
    this.config = config;
    // 预构造所有 provider 实例（主+备链），避免运行时延迟
    for (const [role, roleCfg] of Object.entries(config.roles) as [LlmRole, RoleConfig][]) {
      this.providers.set(`${role}:primary`, this.createProvider(roleCfg.primary));
      const chain = this.fallbackChainOf(roleCfg).map(cfg => this.createProvider(cfg));
      this.fallbackChains.set(role, chain);
      for (const [i, provider] of chain.entries()) {
        this.providers.set(`${role}:fallback${i}`, provider);
      }
    }
  }

  /** 归并单个 fallback 与 fallbacks 数组为一条按序备用链（fallback 在前） */
  private fallbackChainOf(roleCfg: RoleConfig): RoleProviderConfig[] {
    const chain: RoleProviderConfig[] = [];
    if (roleCfg.fallback) chain.push(roleCfg.fallback);
    if (roleCfg.fallbacks) chain.push(...roleCfg.fallbacks);
    return chain;
  }

  /** 按 NPC 名解析角色（大小写不敏感） */
  resolveRole(npcName: string): LlmRole {
    if (!this.config.enableProtagonistMapping) return "npc";
    const lower = npcName.toLowerCase();
    return this.config.protagonistNpcs.some(n => n.toLowerCase() === lower)
      ? "protagonist"
      : "npc";
  }

  /** 获取指定角色的主 provider */
  getProvider(role: LlmRole): VercelAIProvider {
    const p = this.providers.get(`${role}:primary`);
    if (!p) throw new Error(`No primary provider for role: ${role}`);
    return p;
  }

  /** 获取指定角色的备 provider（无备返回 null）。多级备用时返回第一级。 */
  getProviderFallback(role: LlmRole): VercelAIProvider | null {
    return this.fallbackChains.get(role)?.[0] ?? null;
  }

  /** 获取指定角色的完整备用链（按回退顺序；空数组=无备）。 */
  getProviderFallbacks(role: LlmRole): VercelAIProvider[] {
    return this.fallbackChains.get(role) ?? [];
  }

  /** 带回退的 chatCompletion */
  async chatCompletion(role: LlmRole, messages: LlmMessage[]) {
    return this.withFallback(role, (p) => p.chatCompletion(messages));
  }

  /** 带回退的 chatWithTools */
  async chatWithTools(role: LlmRole, messages: LlmMessage[], tools?: Tool[]) {
    return this.withFallback(role, (p) => p.chatWithTools(messages, tools));
  }

  /** 带回退的 chatCompletionJson */
  async chatCompletionJson<T>(role: LlmRole, messages: LlmMessage[]) {
    return this.withFallback(role, (p) => p.chatCompletionJson<T>(messages));
  }

  /**
   * 核心回退逻辑：
   * 1. 沿 主 → 备用链 依次调用
   * 2. 捕获 LLMBillingError（402/429）→ 切到下一级备用
   * 3. 链尾也 billing 错误 → 抛出最后一个（已无路可退）
   * 4. 其他错误（LLMUnavailableError/网络/超时）→ 直接抛出，不回退
   */
  private async withFallback<T>(
    role: LlmRole,
    fn: (p: VercelAIProvider) => Promise<T>,
  ): Promise<T> {
    const chain = [this.getProvider(role), ...this.getProviderFallbacks(role)];
    let lastBillingError: unknown;
    for (const [i, provider] of chain.entries()) {
      try {
        return await fn(provider);
      } catch (err) {
        if (!(err instanceof LLMBillingError)) throw err;
        lastBillingError = err;
        const next = chain[i + 1];
        if (next) {
          console.warn(
            `[llm-router] ${role} provider ${provider.config.model} billing error, falling back to ${next.config.model}`,
          );
        }
      }
    }
    throw lastBillingError;
  }

  private createProvider(cfg: RoleProviderConfig): VercelAIProvider {
    const llmConfig: LLMConfig = {
      provider: cfg.provider as LLMConfig["provider"],
      apiKey: cfg.apiKey,
      model: cfg.model,
      baseUrl: cfg.baseUrl,
      ...(cfg.timeoutMs ? { timeout: cfg.timeoutMs } : {}),
      ...(cfg.maxRetries ? { maxRetries: cfg.maxRetries } : {}),
      ...(cfg.maxConcurrency ? { maxConcurrency: cfg.maxConcurrency } : {}),
    };
    return new VercelAIProvider(llmConfig);
  }
}

/** 从文件加载并校验 router 配置 */
export function loadRouterConfig(path: string): LlmRouterConfig {
  const raw = JSON.parse(readFileSync(path, "utf-8"));
  return validateRouterConfig(raw);
}

/** 校验 runtime JSON 结构，失败时抛明确错误（不静默降级） */
export function validateRouterConfig(raw: unknown): LlmRouterConfig {
  if (typeof raw !== "object" || raw === null) {
    throw new Error("Invalid router config: root must be an object");
  }
  const obj = raw as Record<string, unknown>;

  if (obj.version !== 1) {
    throw new Error(`Invalid router config: version must be 1, got ${obj.version}`);
  }

  const roles = obj.roles as Record<string, unknown> | undefined;
  if (!roles || typeof roles !== "object") {
    throw new Error("Invalid router config: roles must be an object");
  }
  for (const requiredRole of ["director", "protagonist", "npc"] as const) {
    if (!roles[requiredRole]) {
      throw new Error(`Invalid router config: missing role '${requiredRole}'`);
    }
  }

  const validatedRoles = {} as Record<LlmRole, RoleConfig>;
  for (const [roleName, roleRaw] of Object.entries(roles) as [string, unknown][]) {
    if (!["director", "protagonist", "npc"].includes(roleName)) continue;
    validatedRoles[roleName as LlmRole] = validateRoleConfig(roleRaw, roleName);
  }

  const protagonistNpcs = Array.isArray(obj.protagonistNpcs)
    ? obj.protagonistNpcs.filter((n): n is string => typeof n === "string")
    : [];

  const enableProtagonistMapping = typeof obj.enableProtagonistMapping === "boolean"
    ? obj.enableProtagonistMapping
    : true;

  const result: LlmRouterConfig = {
    version: 1,
    roles: validatedRoles,
    protagonistNpcs,
    enableProtagonistMapping,
  };
  return result;
}

function validateRoleConfig(raw: unknown, roleName: string): RoleConfig {
  if (typeof raw !== "object" || raw === null) {
    throw new Error(`Invalid router config: role '${roleName}' must be an object`);
  }
  const obj = raw as Record<string, unknown>;
  const primary = validateProviderConfig(obj.primary, `${roleName}.primary`);
  let fallback: RoleProviderConfig | null = null;
  if (obj.fallback != null) {
    fallback = validateProviderConfig(obj.fallback, `${roleName}.fallback`);
  }
  let fallbacks: RoleProviderConfig[] | undefined;
  if (obj.fallbacks != null) {
    if (!Array.isArray(obj.fallbacks)) {
      throw new Error(`Invalid router config: ${roleName}.fallbacks must be an array`);
    }
    fallbacks = obj.fallbacks.map((fb, i) =>
      validateProviderConfig(fb, `${roleName}.fallbacks[${i}]`),
    );
  }
  return { primary, fallback, ...(fallbacks ? { fallbacks } : {}) };
}

function validateProviderConfig(raw: unknown, path: string): RoleProviderConfig {
  if (typeof raw !== "object" || raw === null) {
    throw new Error(`Invalid router config: ${path} must be an object`);
  }
  const obj = raw as Record<string, unknown>;
  const provider = typeof obj.provider === "string" ? obj.provider : "";
  const apiKey = typeof obj.apiKey === "string" ? obj.apiKey : "";
  const model = typeof obj.model === "string" ? obj.model : "";
  const baseUrl = typeof obj.baseUrl === "string" ? obj.baseUrl : "";

  if (!provider) throw new Error(`Invalid router config: ${path}.provider is empty`);
  if (!apiKey) throw new Error(`Invalid router config: ${path}.apiKey is empty`);
  if (!model) throw new Error(`Invalid router config: ${path}.model is empty`);
  if (!baseUrl) throw new Error(`Invalid router config: ${path}.baseUrl is empty`);

  return {
    provider, apiKey, model, baseUrl,
    ...(typeof obj.timeoutMs === "number" ? { timeoutMs: obj.timeoutMs } : {}),
    ...(typeof obj.maxRetries === "number" ? { maxRetries: obj.maxRetries } : {}),
    ...(typeof obj.maxConcurrency === "number" ? { maxConcurrency: obj.maxConcurrency } : {}),
  };
}