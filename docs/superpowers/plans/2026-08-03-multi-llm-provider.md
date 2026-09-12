# 多 LLM Provider 支持 — 实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让 ValleyAgent 支持多 LLM API Key、按角色路由（导演/主角/普通 NPC）、MiniMax 欠费自动回退到 DeepSeek，同时修复 WebSocketUrl 断裂和冷却字段未加载的既有 bug。

**Architecture:** C# ModConfig 持有 9 组 provider 字段（3 角色 × 主备）+ 角色标记 + 功能开关，序列化为 runtime JSON 由 TS 端读取。TS 端新增 `LlmRouter` 类持有多个 `VercelAIProvider`，按角色分发，billing 错误（402/429）时回退到备 provider。向后兼容：`--llm-config` 未传时走旧单 provider 路径。

**Tech Stack:** TypeScript（Bun + Vercel AI SDK）+ C#（.NET 6 + SMAPI + GMCM）+ 跨仓库 runtime JSON 协议

**关联设计文档：** `docs/design/2026-08-03-multi-llm-provider-design.md`

**两仓库布局：**
- C# 仓库：`d:\Source\ValleyTalk`（ValleyAgent + ValleyAgent.Abstractions）
- TS 仓库：`D:\Source\ValleyAI`（packages/core + packages/stardew）

**分支：** `feature/multi-llm-provider`（两个仓库都开这个分支）

---

## Task 1: TS 端 LlmRouter 核心类 + 配置类型（纯 TS，可单测）

**Files:**
- Create: `D:\Source\ValleyAI\packages\core\src\llm-router.ts`
- Modify: `D:\Source\ValleyAI\packages\core\src\llm-config.ts`（扩展类型）
- Modify: `D:\Source\ValleyAI\packages\core\src\index.ts`（导出）
- Test: `D:\Source\ValleyAI\packages\core\tests\llm-router.test.ts`

### Step 1.1: 扩展 LLMProviderType 联合类型

- [ ] **修改 `D:\Source\ValleyAI\packages\core\src\llm-config.ts`**

在现有 `LLMProviderType` 联合类型中新增 5 个 provider（moonshot/zhipu/baichuan/qwen/custom），它们都走 OpenAI 兼容分支：

```typescript
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
  | "custom";
```

- [ ] **修改 `D:\Source\ValleyAI\packages\core\src\llm-provider.ts` 的 `createModel()`**

在 `case "minimax":` 分支后追加 5 个新 provider 的 fallthrough：

```typescript
case "openai":
case "deepseek":
case "lmstudio":
case "openrouter":
case "minimax":
case "moonshot":
case "zhipu":
case "baichuan":
case "qwen":
case "custom": {
  const openai = createOpenAI({
    apiKey: this.config.apiKey,
    ...(baseURL ? { baseURL } : {}),
  });
  return openai.chat(this.config.model) as LanguageModel;
}
```

- [ ] **运行类型检查**

Run: `cd D:\Source\ValleyAI && bun run tsc --noEmit`
Expected: 0 errors

- [ ] **Commit**

```bash
cd D:\Source\ValleyAI
git add packages/core/src/llm-config.ts packages/core/src/llm-provider.ts
git commit -m "feat(core): extend LLMProviderType with moonshot/zhipu/baichuan/qwen/custom"
```

### Step 1.2: 写 LlmRouter 的失败测试

- [ ] **创建 `D:\Source\ValleyAI\packages\core\tests\llm-router.test.ts`**

```typescript
import { describe, test, expect, mock } from "bun:test";
import { LlmRouter, type LlmRouterConfig } from "../src/llm-router";
import { VercelAIProvider, LLMBillingError, LLMUnavailableError } from "../src/llm-provider";
import type { LlmMessage } from "../src/types";

function makeRouterConfig(overrides: Partial<LlmRouterConfig> = {}): LlmRouterConfig {
  return {
    version: 1,
    roles: {
      director: {
        primary: { provider: "minimax", apiKey: "sk-dir-pri", model: "MiniMax-M3", baseUrl: "https://api.minimax.chat/v1" },
        fallback: { provider: "deepseek", apiKey: "sk-dir-fb", model: "deepseek-chat", baseUrl: "https://api.deepseek.com/v1" },
      },
      protagonist: {
        primary: { provider: "minimax", apiKey: "sk-pro-pri", model: "MiniMax-M2.7-highspeed", baseUrl: "https://api.minimax.chat/v1" },
        fallback: { provider: "deepseek", apiKey: "sk-pro-fb", model: "deepseek-chat", baseUrl: "https://api.deepseek.com/v1" },
      },
      npc: {
        primary: { provider: "deepseek", apiKey: "sk-npc-pri", model: "deepseek-chat", baseUrl: "https://api.deepseek.com/v1" },
        fallback: null,
      },
    },
    protagonistNpcs: ["Abigail", "Haley"],
    enableProtagonistMapping: true,
    ...overrides,
  };
}

describe("LlmRouter.resolveRole", () => {
  test("protagonist 列表内的 NPC → protagonist", () => {
    const router = new LlmRouter(makeRouterConfig());
    expect(router.resolveRole("Abigail")).toBe("protagonist");
  });

  test("protagonist 列表外的 NPC → npc", () => {
    const router = new LlmRouter(makeRouterConfig());
    expect(router.resolveRole("Pierre")).toBe("npc");
  });

  test("enableProtagonistMapping=false → 全部归 npc", () => {
    const router = new LlmRouter(makeRouterConfig({ enableProtagonistMapping: false }));
    expect(router.resolveRole("Abigail")).toBe("npc");
  });

  test("大小写不敏感", () => {
    const router = new LlmRouter(makeRouterConfig());
    expect(router.resolveRole("abigail")).toBe("protagonist");
    expect(router.resolveRole("ABIGAIL")).toBe("protagonist");
  });
});

describe("LlmRouter.withFallback", () => {
  test("primary 成功 → 不调 fallback", async () => {
    const router = new LlmRouter(makeRouterConfig());
    const primarySpy = mock(() => Promise.resolve({ content: "ok", usage: {} }));
    const fallbackProvider = router.getProvider("director");
    const fallbackSpy = mock(() => Promise.resolve({ content: "fallback", usage: {} }));
    // 注入 override 到 primary provider
    router.getProvider("director")._setCallOverride(primarySpy as any);
    router.getProviderFallback("director")!._setCallOverride(fallbackSpy as any);

    const result = await router.chatCompletion("director", []);
    expect(result.content).toBe("ok");
    expect(primarySpy).toHaveBeenCalledTimes(1);
    expect(fallbackSpy).toHaveBeenCalledTimes(0);
  });

  test("primary 抛 LLMBillingError → 调 fallback", async () => {
    const router = new LlmRouter(makeRouterConfig());
    const primarySpy = mock(() => Promise.reject(new LLMBillingError("HTTP 402")));
    const fallbackSpy = mock(() => Promise.resolve({ content: "fallback-ok", usage: {} }));
    router.getProvider("director")._setCallOverride(primarySpy as any);
    router.getProviderFallback("director")!._setCallOverride(fallbackSpy as any);

    const result = await router.chatCompletion("director", []);
    expect(result.content).toBe("fallback-ok");
    expect(primarySpy).toHaveBeenCalledTimes(1);
    expect(fallbackSpy).toHaveBeenCalledTimes(1);
  });

  test("primary 抛 LLMUnavailableError → 不调 fallback，直接抛", async () => {
    const router = new LlmRouter(makeRouterConfig());
    const primarySpy = mock(() => Promise.reject(new LLMUnavailableError("HTTP 500")));
    const fallbackSpy = mock(() => Promise.resolve({ content: "fallback", usage: {} }));
    router.getProvider("director")._setCallOverride(primarySpy as any);
    router.getProviderFallback("director")!._setCallOverride(fallbackSpy as any);

    await expect(router.chatCompletion("director", [])).rejects.toThrow(LLMUnavailableError);
    expect(primarySpy).toHaveBeenCalledTimes(1);
    expect(fallbackSpy).toHaveBeenCalledTimes(0);
  });

  test("primary 抛网络错误 → 不调 fallback，直接抛", async () => {
    const router = new LlmRouter(makeRouterConfig());
    const primarySpy = mock(() => Promise.reject(new Error("network timeout")));
    const fallbackSpy = mock(() => Promise.resolve({ content: "fallback", usage: {} }));
    router.getProvider("director")._setCallOverride(primarySpy as any);
    router.getProviderFallback("director")!._setCallOverride(fallbackSpy as any);

    await expect(router.chatCompletion("director", [])).rejects.toThrow("network timeout");
    expect(fallbackSpy).toHaveBeenCalledTimes(0);
  });

  test("fallback 也 billing 错误 → 抛 LLMBillingError", async () => {
    const router = new LlmRouter(makeRouterConfig());
    const primarySpy = mock(() => Promise.reject(new LLMBillingError("HTTP 402")));
    const fallbackSpy = mock(() => Promise.reject(new LLMBillingError("HTTP 429")));
    router.getProvider("director")._setCallOverride(primarySpy as any);
    router.getProviderFallback("director")!._setCallOverride(fallbackSpy as any);

    await expect(router.chatCompletion("director", [])).rejects.toThrow(LLMBillingError);
  });

  test("无 fallback 配置 + billing 错误 → 抛 LLMBillingError", async () => {
    const router = new LlmRouter(makeRouterConfig());
    const primarySpy = mock(() => Promise.reject(new LLMBillingError("HTTP 402")));
    router.getProvider("npc")._setCallOverride(primarySpy as any);

    await expect(router.chatCompletion("npc", [])).rejects.toThrow(LLMBillingError);
  });
});

describe("LlmRouter 不变量", () => {
  test("provider 实例数 = 主数 + 备数（3 主 + 2 备 = 5）", () => {
    const router = new LlmRouter(makeRouterConfig());
    expect(router.getProvider("director")).toBeDefined();
    expect(router.getProvider("protagonist")).toBeDefined();
    expect(router.getProvider("npc")).toBeDefined();
    expect(router.getProviderFallback("director")).toBeDefined();
    expect(router.getProviderFallback("protagonist")).toBeDefined();
    expect(router.getProviderFallback("npc")).toBeNull();
  });

  test("同一请求最终只成功调用一个 provider（primary 成功时）", async () => {
    const router = new LlmRouter(makeRouterConfig());
    const primarySpy = mock(() => Promise.resolve({ content: "ok", usage: {} }));
    const fallbackSpy = mock(() => Promise.resolve({ content: "fallback", usage: {} }));
    router.getProvider("director")._setCallOverride(primarySpy as any);
    router.getProviderFallback("director")!._setCallOverride(fallbackSpy as any);

    await router.chatCompletion("director", []);
    const totalSuccess = primarySpy.mock.calls.length + fallbackSpy.mock.calls.length;
    expect(totalSuccess).toBe(1);
  });
});
```

- [ ] **运行测试验证失败**

Run: `cd D:\Source\ValleyAI && bun test packages/core/tests/llm-router.test.ts`
Expected: FAIL（`llm-router.ts` 不存在）

### Step 1.3: 实现 LlmRouter

- [ ] **创建 `D:\Source\ValleyAI\packages\core\src\llm-router.ts`**

```typescript
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

/** 一个角色的完整配置：主 + 可选备 */
export interface RoleConfig {
  primary: RoleProviderConfig;
  fallback?: RoleProviderConfig | null;
}

/** runtime JSON 根结构 */
export interface LlmRouterConfig {
  version: number;
  roles: Record<LlmRole, RoleConfig>;
  protagonistNpcs: string[];
  enableProtagonistMapping: boolean;
  global?: {
    timeoutMs?: number;
    maxRetries?: number;
    temperature?: number;
    maxTokens?: number;
  };
}

/**
 * 多 LLM Provider 路由器：按角色分发，billing 错误时回退。
 * 每个 provider 实例独立预算/熔断/并发池。
 */
export class LlmRouter {
  private readonly providers = new Map<string, VercelAIProvider>();
  private readonly config: LlmRouterConfig;

  constructor(config: LlmRouterConfig) {
    this.config = config;
    // 预构造所有 provider 实例（主+备），避免运行时延迟
    for (const [role, roleCfg] of Object.entries(config.roles) as [LlmRole, RoleConfig][]) {
      this.providers.set(`${role}:primary`, this.createProvider(roleCfg.primary));
      if (roleCfg.fallback) {
        this.providers.set(`${role}:fallback`, this.createProvider(roleCfg.fallback));
      }
    }
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

  /** 获取指定角色的备 provider（无备返回 null） */
  getProviderFallback(role: LlmRole): VercelAIProvider | null {
    return this.providers.get(`${role}:fallback`) ?? null;
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
   * 1. 用主 provider 调用
   * 2. 捕获 LLMBillingError（402/429）→ 切到 fallback provider 重试一次
   * 3. fallback 也 billing 错误 → 抛出（已无路可退）
   * 4. 其他错误（LLMUnavailableError/网络/超时）→ 直接抛出，不回退
   */
  private async withFallback<T>(
    role: LlmRole,
    fn: (p: VercelAIProvider) => Promise<T>,
  ): Promise<T> {
    const primary = this.getProvider(role);
    try {
      return await fn(primary);
    } catch (err) {
      if (!(err instanceof LLMBillingError)) throw err;
      const fallback = this.getProviderFallback(role);
      if (!fallback) throw err;
      console.warn(`[llm-router] ${role} primary billing error, falling back to ${fallback.config.model}`);
      return await fn(fallback);
    }
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
```

- [ ] **修改 `D:\Source\ValleyAI\packages\core\src\llm-provider.ts`**

暴露 `config` 字段供 LlmRouter 日志读取。找到 `private readonly config` 改为 `readonly config`（或新增 getter `getConfig()`）。具体定位 VercelAIProvider 类内的 `this.config` 声明行（约 L107），把 `private` 改为 `public readonly`：

```typescript
// 改前
private readonly config: Required<LLMConfig>;
// 改后
readonly config: Required<LLMConfig>;
```

- [ ] **修改 `D:\Source\ValleyAI\packages\core\src\index.ts`**

导出 LlmRouter 相关类型：

```typescript
export * from "./llm-router";
```

- [ ] **运行测试验证通过**

Run: `cd D:\Source\ValleyAI && bun test packages/core/tests/llm-router.test.ts`
Expected: PASS（所有测试绿）

- [ ] **运行类型检查**

Run: `cd D:\Source\ValleyAI && bun run tsc --noEmit`
Expected: 0 errors

- [ ] **Commit**

```bash
cd D:\Source\ValleyAI
git add packages/core/src/llm-router.ts packages/core/src/index.ts packages/core/src/llm-provider.ts packages/core/tests/llm-router.test.ts
git commit -m "feat(core): add LlmRouter with role-based routing and billing fallback"
```

### Step 1.4: 写 loadRouterConfig 校验测试 + 实现

- [ ] **扩展 `D:\Source\ValleyAI\packages\core\tests\llm-router.test.ts`**

追加配置校验测试：

```typescript
import { loadRouterConfig, validateRouterConfig } from "../src/llm-router";
import { writeFileSync, mkdirSync, rmSync } from "node:fs";
import { join } from "node:path";

describe("loadRouterConfig 校验", () => {
  const tmpDir = join(import.meta.dir, ".tmp-router-test");

  beforeEach(() => {
    try { rmSync(tmpDir, { recursive: true, force: true }); } catch {}
    mkdirSync(tmpDir, { recursive: true });
  });
  afterEach(() => {
    try { rmSync(tmpDir, { recursive: true, force: true }); } catch {}
  });

  test("version != 1 → 抛错", () => {
    const path = join(tmpDir, "bad-version.json");
    writeFileSync(path, JSON.stringify({ version: 2, roles: {}, protagonistNpcs: [], enableProtagonistMapping: true }));
    expect(() => loadRouterConfig(path)).toThrow(/version/);
  });

  test("缺少 director role → 抛错", () => {
    const path = join(tmpDir, "missing-role.json");
    writeFileSync(path, JSON.stringify({
      version: 1,
      roles: { protagonist: { primary: { provider: "minimax", apiKey: "k", model: "m", baseUrl: "u" } }, npc: { primary: { provider: "deepseek", apiKey: "k", model: "m", baseUrl: "u" } } },
      protagonistNpcs: [],
      enableProtagonistMapping: true,
    }));
    expect(() => loadRouterConfig(path)).toThrow(/director/);
  });

  test("primary apiKey 为空 → 抛错", () => {
    const path = join(tmpDir, "empty-key.json");
    writeFileSync(path, JSON.stringify({
      version: 1,
      roles: {
        director: { primary: { provider: "minimax", apiKey: "", model: "m", baseUrl: "u" } },
        protagonist: { primary: { provider: "minimax", apiKey: "k", model: "m", baseUrl: "u" } },
        npc: { primary: { provider: "deepseek", apiKey: "k", model: "m", baseUrl: "u" } },
      },
      protagonistNpcs: [],
      enableProtagonistMapping: true,
    }));
    expect(() => loadRouterConfig(path)).toThrow(/apiKey/);
  });

  test("fallback 可选，缺失不报错", () => {
    const path = join(tmpDir, "no-fallback.json");
    writeFileSync(path, JSON.stringify({
      version: 1,
      roles: {
        director: { primary: { provider: "minimax", apiKey: "k", model: "m", baseUrl: "u" }, fallback: null },
        protagonist: { primary: { provider: "minimax", apiKey: "k", model: "m", baseUrl: "u" }, fallback: null },
        npc: { primary: { provider: "deepseek", apiKey: "k", model: "m", baseUrl: "u" }, fallback: null },
      },
      protagonistNpcs: [],
      enableProtagonistMapping: true,
    }));
    const cfg = loadRouterConfig(path);
    expect(cfg.roles.npc.fallback).toBeNull();
  });

  test("fallback provider 非空但 apiKey 空 → 抛错", () => {
    const path = join(tmpDir, "bad-fallback.json");
    writeFileSync(path, JSON.stringify({
      version: 1,
      roles: {
        director: { primary: { provider: "minimax", apiKey: "k", model: "m", baseUrl: "u" }, fallback: { provider: "deepseek", apiKey: "", model: "m", baseUrl: "u" } },
        protagonist: { primary: { provider: "minimax", apiKey: "k", model: "m", baseUrl: "u" } },
        npc: { primary: { provider: "deepseek", apiKey: "k", model: "m", baseUrl: "u" } },
      },
      protagonistNpcs: [],
      enableProtagonistMapping: true,
    }));
    expect(() => loadRouterConfig(path)).toThrow(/apiKey/);
  });

  test("合法配置 → 正确加载", () => {
    const path = join(tmpDir, "valid.json");
    writeFileSync(path, JSON.stringify({
      version: 1,
      roles: {
        director: { primary: { provider: "minimax", apiKey: "k1", model: "M3", baseUrl: "u1" }, fallback: { provider: "deepseek", apiKey: "k2", model: "chat", baseUrl: "u2" } },
        protagonist: { primary: { provider: "minimax", apiKey: "k3", model: "M2.7", baseUrl: "u3" } },
        npc: { primary: { provider: "deepseek", apiKey: "k4", model: "chat", baseUrl: "u4" } },
      },
      protagonistNpcs: ["Abigail"],
      enableProtagonistMapping: true,
    }));
    const cfg = loadRouterConfig(path);
    expect(cfg.roles.director.primary.model).toBe("M3");
    expect(cfg.protagonistNpcs).toEqual(["Abigail"]);
  });
});
```

- [ ] **运行测试验证失败**

Run: `cd D:\Source\ValleyAI && bun test packages/core/tests/llm-router.test.ts`
Expected: FAIL（`loadRouterConfig`/`validateRouterConfig` 未导出）

- [ ] **在 `D:\Source\ValleyAI\packages\core\src\llm-router.ts` 末尾追加 loadRouterConfig + validateRouterConfig**

```typescript
import { readFileSync } from "node:fs";

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

  return {
    version: 1,
    roles: validatedRoles,
    protagonistNpcs,
    enableProtagonistMapping,
    ...(obj.global ? { global: obj.global as LlmRouterConfig["global"] } : {}),
  };
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
  return { primary, fallback };
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
```

- [ ] **运行测试验证通过**

Run: `cd D:\Source\ValleyAI && bun test packages/core/tests/llm-router.test.ts`
Expected: PASS（全部测试绿）

- [ ] **Commit**

```bash
cd D:\Source\ValleyAI
git add packages/core/src/llm-router.ts packages/core/tests/llm-router.test.ts
git commit -m "feat(core): add loadRouterConfig with strict validation"
```

---

## Task 2: TS 端 StardewAgent/Registry/server/cli/director 改造

**Files:**
- Modify: `D:\Source\ValleyAI\packages\stardew\src\stardew-agent.ts`
- Modify: `D:\Source\ValleyAI\packages\stardew\src\stardew-agent-registry.ts`
- Modify: `D:\Source\ValleyAI\packages\stardew\src\server.ts`
- Modify: `D:\Source\ValleyAI\packages\stardew\src\cli.ts`
- Modify: `D:\Source\ValleyAI\packages\stardew\src\director.ts`
- Test: `D:\Source\ValleyAI\packages\stardew\tests\stardew-agent-router.test.ts`

### Step 2.1: 改造 StardewAgent 持有 LlmRouter + role

- [ ] **修改 `D:\Source\ValleyAI\packages\stardew\src\stardew-agent.ts`**

找到 `StardewAgentConfig`（约 L20-28）和 `llmProvider` 字段声明（约 L119, L133），改造为持有 LlmRouter + role：

```typescript
// 顶部 import 新增
import { LlmRouter, type LlmRole } from "@valley/core";

// StardewAgentConfig 改造
export interface StardewAgentConfig {
  name: string;
  memory: AgentMemory;
  promptBuilder: PromptBuilder;
  /** 多 provider 路由器（与 llmProvider 二选一） */
  llmRouter?: LlmRouter;
  /** NPC 角色（llmRouter 模式下必填） */
  role?: LlmRole;
  /** 单 provider 模式向后兼容（llmRouter 未传时使用） */
  llmProvider?: VercelAIProvider;
  maxTurns?: number;
  transcriptStore?: TranscriptStore;
}
```

类内部字段改造：

```typescript
// 改前
private readonly llmProvider: VercelAIProvider;
// 改后
private readonly llmRouter: LlmRouter | null;
private readonly role: LlmRole;
private readonly llmProvider: VercelAIProvider | null;
```

构造函数改造：

```typescript
constructor(config: StardewAgentConfig) {
  // ...
  if (config.llmRouter) {
    this.llmRouter = config.llmRouter;
    this.role = config.role ?? "npc";
    this.llmProvider = null;
  } else if (config.llmProvider) {
    this.llmRouter = null;
    this.role = "npc";
    this.llmProvider = config.llmProvider;
  } else {
    throw new Error("StardewAgent requires either llmRouter or llmProvider");
  }
  // ...
}
```

`makeLlmCall` 方法改造（约 L525-527）：

```typescript
private async makeLlmCall(messages: LlmMessage[], tools?: Tool[]): Promise<LcmCallResult> {
  if (this.llmRouter) {
    return this.llmRouter.chatWithTools(this.role, messages, tools);
  }
  return this.llmProvider!.chatWithTools(messages, tools);
}
```

- [ ] **运行类型检查**

Run: `cd D:\Source\ValleyAI && bun run tsc --noEmit`
Expected: 编译错误（registry 和 server 还没改），暂时记录错误数量作为基线

### Step 2.2: 改造 StardewAgentRegistry 持有 LlmRouter

- [ ] **修改 `D:\Source\ValleyAI\packages\stardew\src\stardew-agent-registry.ts`**

```typescript
// 顶部 import
import { LlmRouter } from "@valley/core";

// RegistryConfig 改造
export interface RegistryConfig {
  promptBuilder: PromptBuilder;
  /** 多 provider 路由器（与 llmProvider 二选一） */
  llmRouter?: LlmRouter;
  /** 单 provider 向后兼容 */
  llmProvider?: VercelAIProvider;
  agentsDir: string;
  maxTurns?: number;
  transcript?: TranscriptConfig;
}
```

`getOrCreate` 改造（约 L81-101）：

```typescript
getOrCreate(npcName: string): StardewAgent {
  let agent = this.agents.get(npcName);
  if (!agent) {
    const memoryPath = this.getMemoryFilePath(npcName);
    const memory = new AgentMemory(npcName, memoryPath);
    const agentConfig: StardewAgentConfig = {
      name: npcName,
      memory,
      promptBuilder: this.config.promptBuilder,
      ...(this.config.llmRouter
        ? { llmRouter: this.config.llmRouter, role: this.config.llmRouter.resolveRole(npcName) }
        : { llmProvider: this.config.llmProvider }),
      ...(this.config.maxTurns !== undefined ? { maxTurns: this.config.maxTurns } : {}),
      ...(this.transcriptStore !== null ? { transcriptStore: this.transcriptStore } : {}),
    };
    agent = new StardewAgent(agentConfig);
    this.agents.set(npcName, agent);
  }
  return agent;
}
```

- [ ] **运行类型检查**

Run: `cd D:\Source\ValleyAI && bun run tsc --noEmit`
Expected: 仍有 server.ts 的错误，但 registry 错误应消除

### Step 2.3: 改造 server.ts 支持双模式

- [ ] **修改 `D:\Source\ValleyAI\packages\stardew\src\server.ts`**

```typescript
// 顶部 import
import { LlmRouter, type LlmRouterConfig, loadRouterConfig } from "@valley/core";

// ServerConfig 扩展
export interface ServerConfig {
  port: number;
  hostname: string;
  dataPath: string;
  agentsDir: string;
  /** 单 provider 模式（与 llmRouterConfig 二选一） */
  llmConfig?: ServerLLMConfig;
  /** 多 provider 模式：直接传配置对象 */
  llmRouterConfig?: LlmRouterConfig;
  /** 多 provider 模式：传 runtime JSON 文件路径 */
  llmRouterConfigPath?: string;
  llmCallOverride?: (messages: unknown, tools?: unknown) => Promise<ProviderToolCallResult>;
  transcript?: TranscriptConfig;
  /** 导演开关（false 时不构造 Director） */
  enableDirector?: boolean;
}
```

`startServer` 改造（约 L36-61）：

```typescript
export async function startServer(config: ServerConfig): Promise<ServerHandle> {
  const loader = new NpcPromptLoader(config.dataPath);
  const builder = new PromptBuilder(loader);

  // ─── 构造 RegistryConfig ─────────────────────────────────
  let registryConfig: RegistryConfig;

  if (config.llmRouterConfigPath) {
    // 多 provider 模式（从文件加载）
    const routerConfig = loadRouterConfig(config.llmRouterConfigPath);
    const router = new LlmRouter(routerConfig);
    registryConfig = {
      promptBuilder: builder,
      llmRouter: router,
      agentsDir: config.agentsDir,
      ...(config.transcript ? { transcript: config.transcript } : {}),
    };
    // 注入 callOverride 到所有 provider（测试用）
    if (config.llmCallOverride) {
      for (const role of ["director", "protagonist", "npc"] as const) {
        router.getProvider(role)._setCallOverride(config.llmCallOverride);
        const fb = router.getProviderFallback(role);
        if (fb) fb._setCallOverride(config.llmCallOverride);
      }
    }
  } else if (config.llmRouterConfig) {
    // 多 provider 模式（直接传配置对象）
    const router = new LlmRouter(config.llmRouterConfig);
    registryConfig = {
      promptBuilder: builder,
      llmRouter: router,
      agentsDir: config.agentsDir,
      ...(config.transcript ? { transcript: config.transcript } : {}),
    };
    if (config.llmCallOverride) {
      for (const role of ["director", "protagonist", "npc"] as const) {
        router.getProvider(role)._setCallOverride(config.llmCallOverride);
        const fb = router.getProviderFallback(role);
        if (fb) fb._setCallOverride(config.llmCallOverride);
      }
    }
  } else if (config.llmConfig) {
    // 单 provider 模式（向后兼容）
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
    registryConfig = {
      promptBuilder: builder,
      llmProvider: provider,
      agentsDir: config.agentsDir,
      ...(config.transcript ? { transcript: config.transcript } : {}),
    };
  } else {
    throw new Error("ServerConfig requires either llmConfig, llmRouterConfig, or llmRouterConfigPath");
  }

  const registry = new StardewAgentRegistry(registryConfig);
  // ... 后续代码不变
}
```

- [ ] **运行类型检查**

Run: `cd D:\Source\ValleyAI && bun run tsc --noEmit`
Expected: 0 errors（server/registry/agent 三处一致）

### Step 2.4: 改造 cli.ts 支持 --llm-config

- [ ] **修改 `D:\Source\ValleyAI\packages\stardew\src\cli.ts`**

`CliArgs` 接口扩展（约 L20-31）：

```typescript
interface CliArgs {
  port: number;
  hostname: string;
  dataPath: string;
  agentsDir: string;
  // 单 provider 模式
  llmApiKey: string;
  llmModel: string;
  llmBaseUrl: string;
  llmProvider: string;
  // 多 provider 模式
  llmConfigPath?: string;
  // 导演开关
  enableDirector?: boolean;
  transcriptDir?: string;
}
```

`parseArgs` 追加（约 L45-75 的 switch-case 内）：

```typescript
case "--llm-config": if (next) { args.llmConfigPath = next; i++; } break;
case "--enable-director": args.enableDirector = true; break;
case "--disable-director": args.enableDirector = false; break;
```

`--help` 帮助文本追加：

```typescript
console.log("  --llm-config <path>      Path to multi-provider runtime JSON (enables multi-provider mode)");
console.log("  --enable-director        Enable narrative director (default: enabled)");
console.log("  --disable-director       Disable narrative director (NPCs fully autonomous)");
```

`main()` 改造（约 L77-100）：

```typescript
async function main() {
  const args = parseArgs(process.argv.slice(2));

  const serverConfig: ServerConfig = {
    port: args.port,
    hostname: args.hostname,
    dataPath: args.dataPath,
    agentsDir: args.agentsDir,
    ...(args.transcriptDir ? { transcript: { enabled: true, dir: args.transcriptDir } } : {}),
    ...(args.enableDirector !== undefined ? { enableDirector: args.enableDirector } : {}),
  };

  if (args.llmConfigPath) {
    // 多 provider 模式
    serverConfig.llmRouterConfigPath = args.llmConfigPath;
  } else {
    // 单 provider 模式（向后兼容）
    if (!args.llmApiKey) {
      console.error("Error: --llm-api-key is required (or use --llm-config for multi-provider mode)");
      process.exit(1);
    }
    serverConfig.llmConfig = {
      provider: args.llmProvider,
      apiKey: args.llmApiKey,
      model: args.llmModel,
      baseUrl: args.llmBaseUrl,
    };
  }

  const handle = await startServer(serverConfig);
  // ... 现有信号处理
}
```

- [ ] **运行类型检查**

Run: `cd D:\Source\ValleyAI && bun run tsc --noEmit`
Expected: 0 errors

### Step 2.5: 改造 director.ts 通过 LlmRouter 调用

- [ ] **修改 `D:\Source\ValleyAI\packages\stardew\src\director.ts`**

`DirectorConfig` 保持函数式接口不变（已解耦），但在 server.ts 构造 Director 时（当 Director 启用时）用 LlmRouter 适配：

在 `D:\Source\ValleyAI\packages\stardew\src\server.ts` 找到 Director 构造位置（当前是注释 L111-113），改为条件构造：

```typescript
// server.ts startServer 内，registry 构造之后
if (config.enableDirector !== false) {
  // 导演启用（默认）
  const directorCallLlm = config.llmRouterConfig || config.llmRouterConfigPath
    ? async (prompt: string) => {
        const router = (registry as any).config.llmRouter as LlmRouter;
        const result = await router.chatCompletion("director", [{ role: "user", content: prompt }]);
        return {
          text: result.content,
          usage: {
            promptTokens: result.usage?.promptTokens ?? 0,
            completionTokens: result.usage?.completionTokens ?? 0,
          },
        };
      }
    : async (prompt: string) => {
        const provider = (registry as any).config.llmProvider as VercelAIProvider;
        const result = await provider.chatCompletion([{ role: "user", content: prompt }]);
        return {
          text: result.content,
          usage: {
            promptTokens: result.usage?.promptTokens ?? 0,
            completionTokens: result.usage?.completionTokens ?? 0,
          },
        };
      };

  // 构造 Director（现有 beatStore/profileMgr/gameCtxMgr/activityStore 引用不变）
  // director = new Director(beatStore, profileMgr, gameCtxMgr, activityStore, { callLlm: directorCallLlm }, transcriptStore);
}
```

**注意**：Director 当前未实例化（注释 L111-113），此步骤只准备好 `directorCallLlm` 适配层。如果 Director 实例化代码尚未实现，此步骤的适配函数留作 future use，不实际构造 Director。

- [ ] **运行类型检查**

Run: `cd D:\Source\ValleyAI && bun run tsc --noEmit`
Expected: 0 errors

### Step 2.6: 写 StardewAgent + LlmRouter 集成测试

- [ ] **创建 `D:\Source\ValleyAI\packages\stardew\tests\stardew-agent-router.test.ts`**

```typescript
import { describe, test, expect, mock, beforeEach, afterEach } from "bun:test";
import { LlmRouter, type LlmRouterConfig } from "@valley/core";
import { LLMBillingError } from "@valley/core";
import { StardewAgent } from "../src/stardew-agent";
// ... 其他必要 import

function makeRouterConfig(): LlmRouterConfig {
  return {
    version: 1,
    roles: {
      director: {
        primary: { provider: "minimax", apiKey: "sk-d", model: "M3", baseUrl: "u" },
        fallback: { provider: "deepseek", apiKey: "sk-df", model: "chat", baseUrl: "u" },
      },
      protagonist: {
        primary: { provider: "minimax", apiKey: "sk-p", model: "M2.7", baseUrl: "u" },
        fallback: { provider: "deepseek", apiKey: "sk-pf", model: "chat", baseUrl: "u" },
      },
      npc: {
        primary: { provider: "deepseek", apiKey: "sk-n", model: "chat", baseUrl: "u" },
        fallback: null,
      },
    },
    protagonistNpcs: ["Abigail"],
    enableProtagonistMapping: true,
  };
}

describe("StardewAgent with LlmRouter", () => {
  test("agent.role 按 NPC 名正确绑定（protagonist）", () => {
    const router = new LlmRouter(makeRouterConfig());
    // 构造 agent（需要 mock memory/promptBuilder）
    // 验证 agent 内部 role = "protagonist"
  });

  test("agent.role 按 NPC 名正确绑定（npc）", () => {
    const router = new LlmRouter(makeRouterConfig());
    // 构造 Pierre 的 agent
    // 验证 role = "npc"
  });

  test("billing 错误时 agent 透明回退，调用方无感知", async () => {
    const router = new LlmRouter(makeRouterConfig());
    const primarySpy = mock(() => Promise.reject(new LLMBillingError("HTTP 402")));
    const fallbackSpy = mock(() => Promise.resolve({
      content: "fallback response",
      toolCalls: [],
      usage: { promptTokens: 10, completionTokens: 5 },
    }));
    router.getProvider("protagonist")._setCallOverride(primarySpy as any);
    router.getProviderFallback("protagonist")!._setCallOverride(fallbackSpy as any);

    // 构造 Abigail agent，调用 makeLlmCall（或通过 runDialogue 间接调用）
    // 验证最终返回 "fallback response"，不抛错
  });
});
```

**注意**：完整测试需要 mock `AgentMemory` 和 `PromptBuilder`，参考现有 `stardew-agent.test.ts` 的 mock 模式。

- [ ] **运行测试验证通过**

Run: `cd D:\Source\ValleyAI && bun test packages/stardew/tests/stardew-agent-router.test.ts`
Expected: PASS

- [ ] **运行全部 stardew 测试确保不回归**

Run: `cd D:\Source\ValleyAI && bun test packages/stardew`
Expected: 所有现有测试仍绿（向后兼容）

- [ ] **运行协议契约检查**

Run: `cd D:\Source\ValleyAI && bun run check:protocol`
Expected: 全绿

- [ ] **Commit**

```bash
cd D:\Source\ValleyAI
git add packages/stardew/src/ packages/stardew/tests/stardew-agent-router.test.ts
git commit -m "feat(stardew): integrate LlmRouter into agent/registry/server/cli/director"
```

---

## Task 3: C# 端 ModConfig 扩展 + 迁移

**Files:**
- Modify: `d:\Source\ValleyTalk\src\ValleyAgent\Config\ModConfig.cs`
- Create: `d:\Source\ValleyTalk\src\ValleyAgent.UnitTests\ConfigMigrationTests.cs`

### Step 3.1: ModConfig 新增字段

- [ ] **修改 `d:\Source\ValleyTalk\src\ValleyAgent\Config\ModConfig.cs`**

在现有字段之后（约 L383 `DebugLogEnabled` 之后），追加以下字段：

```csharp
// ─── 多 Provider 模式 ──────────────────────────────────────────────

/// <summary>启用多 Provider 模式：true=3 角色主备配置；false=单 Provider。</summary>
[DefaultValue(false)]
public bool MultiProviderEnabled { get; set; } = false;

// 导演（narrative director）
[DefaultValue("minimax")]       public string DirectorPrimaryProvider { get; set; } = "minimax";
[DefaultValue("")]              public string DirectorPrimaryApiKey { get; set; } = "";
[DefaultValue("MiniMax-M3")]    public string DirectorPrimaryModel { get; set; } = "MiniMax-M3";
[DefaultValue("https://api.minimax.chat/v1")] public string DirectorPrimaryBaseUrl { get; set; } = "https://api.minimax.chat/v1";
[DefaultValue("deepseek")]      public string DirectorFallbackProvider { get; set; } = "deepseek";
[DefaultValue("")]              public string DirectorFallbackApiKey { get; set; } = "";
[DefaultValue("deepseek-chat")] public string DirectorFallbackModel { get; set; } = "deepseek-chat";
[DefaultValue("https://api.deepseek.com/v1")] public string DirectorFallbackBaseUrl { get; set; } = "https://api.deepseek.com/v1";

// 主角 NPC
[DefaultValue("minimax")]                public string ProtagonistPrimaryProvider { get; set; } = "minimax";
[DefaultValue("")]                       public string ProtagonistPrimaryApiKey { get; set; } = "";
[DefaultValue("MiniMax-M2.7-highspeed")] public string ProtagonistPrimaryModel { get; set; } = "MiniMax-M2.7-highspeed";
[DefaultValue("https://api.minimax.chat/v1")] public string ProtagonistPrimaryBaseUrl { get; set; } = "https://api.minimax.chat/v1";
[DefaultValue("deepseek")]      public string ProtagonistFallbackProvider { get; set; } = "deepseek";
[DefaultValue("")]              public string ProtagonistFallbackApiKey { get; set; } = "";
[DefaultValue("deepseek-chat")] public string ProtagonistFallbackModel { get; set; } = "deepseek-chat";
[DefaultValue("https://api.deepseek.com/v1")] public string ProtagonistFallbackBaseUrl { get; set; } = "https://api.deepseek.com/v1";

// 普通 NPC
[DefaultValue("deepseek")]      public string NpcPrimaryProvider { get; set; } = "deepseek";
[DefaultValue("")]              public string NpcPrimaryApiKey { get; set; } = "";
[DefaultValue("deepseek-chat")] public string NpcPrimaryModel { get; set; } = "deepseek-chat";
[DefaultValue("https://api.deepseek.com/v1")] public string NpcPrimaryBaseUrl { get; set; } = "https://api.deepseek.com/v1";
[DefaultValue("")]              public string NpcFallbackProvider { get; set; } = "";
[DefaultValue("")]              public string NpcFallbackApiKey { get; set; } = "";
[DefaultValue("")]              public string NpcFallbackModel { get; set; } = "";
[DefaultValue("")]              public string NpcFallbackBaseUrl { get; set; } = "";

// 角色标记
[DefaultValue(true)]
public bool EnableProtagonistMapping { get; set; } = true;

[DefaultValue("Abigail, Haley, Sebastian, Sam, Penny, Alex, Maru, Leah, Elliott, Shane, Emily, Harvey")]
public string ProtagonistNpcs { get; set; } = "Abigail, Haley, Sebastian, Sam, Penny, Alex, Maru, Leah, Elliott, Shane, Emily, Harvey";

// ─── 新增功能开关 ──────────────────────────────────────────────────
[DefaultValue(true)] public bool EnableTrade { get; set; } = true;
[DefaultValue(true)] public bool EnableHire { get; set; } = true;
[DefaultValue(true)] public bool EnableDirector { get; set; } = true;
[DefaultValue(true)] public bool EnableProactiveSpeech { get; set; } = true;
[DefaultValue(true)] public bool EnableInfiniteDialogue { get; set; } = true;

// ─── 新增概率设置 ──────────────────────────────────────────────────
[DefaultValue(0.3f)]  public float ProactiveSpeechProbability { get; set; } = 0.3f;
[DefaultValue(0.1f)]  public float ProactiveGiftProbability { get; set; } = 0.1f;
[DefaultValue(0.2f)]  public float ProactiveFollowProbability { get; set; } = 0.2f;
[DefaultValue(0.15f)] public float ProactiveTradeProbability { get; set; } = 0.15f;

// ─── 冷却改秒（旧字段标记 Obsolete） ────────────────────────────────
[DefaultValue(3)]  public int DialogueCooldownSeconds { get; set; } = 3;
[DefaultValue(30)] public int GiftCooldownSeconds { get; set; } = 30;
```

同时把现有 `WebSocketUrl` 字段标记 Obsolete：

```csharp
[Obsolete("WebSocket URL is auto-bound from ServerPort. Field retained for save compat.")]
[DefaultValue("ws://127.0.0.1:8765")]
public string WebSocketUrl { get; set; } = "ws://127.0.0.1:8765";
```

- [ ] **编译验证**

Run: `cd d:\Source\ValleyTalk && dotnet build src\ValleyAgent\ValleyAgent.csproj`
Expected: 0 errors（可能有 Obsolete 警告，后续步骤处理）

### Step 3.2: MigrateLegacyFields 扩展

- [ ] **修改 `d:\Source\ValleyTalk\src\ValleyAgent\Config\ModConfig.cs` 的 `MigrateLegacyFields()`**

在现有迁移逻辑末尾（约 L441 `#pragma warning restore CS0618` 之前）追加：

```csharp
// 冷却字段迁移：ms → seconds（仅在 seconds 还是默认值且 ms 被修改过时迁移）
if (DialogueCooldownSeconds == 3 && DialogueCooldownMs != 3000)
{
    DialogueCooldownSeconds = Math.Max(1, DialogueCooldownMs / 1000);
}
if (GiftCooldownSeconds == 30 && GiftCooldownMs != 30000)
{
    GiftCooldownSeconds = Math.Max(1, GiftCooldownMs / 1000);
}
```

- [ ] **修改 `Validate()` 方法**

在现有钳位逻辑中追加（找到 Validate 方法体内的合适位置）：

```csharp
// 冷却秒钳制
if (DialogueCooldownSeconds < 1 || DialogueCooldownSeconds > 60) { DialogueCooldownSeconds = 3; changed = true; }
if (GiftCooldownSeconds < 1 || GiftCooldownSeconds > 600) { GiftCooldownSeconds = 30; changed = true; }

// 概率钳制 [0, 1]
if (ProactiveSpeechProbability < 0f || ProactiveSpeechProbability > 1f) { ProactiveSpeechProbability = 0.3f; changed = true; }
if (ProactiveGiftProbability < 0f || ProactiveGiftProbability > 1f) { ProactiveGiftProbability = 0.1f; changed = true; }
if (ProactiveFollowProbability < 0f || ProactiveFollowProbability > 1f) { ProactiveFollowProbability = 0.2f; changed = true; }
if (ProactiveTradeProbability < 0f || ProactiveTradeProbability > 1f) { ProactiveTradeProbability = 0.15f; changed = true; }
```

- [ ] **编译验证**

Run: `cd d:\Source\ValleyTalk && dotnet build src\ValleyAgent\ValleyAgent.csproj`
Expected: 0 errors, 0 warnings（Obsolete 字段用 #pragma 隔离）

### Step 3.3: 写迁移单元测试

- [ ] **创建 `d:\Source\ValleyTalk\src\ValleyAgent.UnitTests\ConfigMigrationTests.cs`**

```csharp
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ValleyAgent.Config;

namespace ValleyAgent.UnitTests
{
    [TestClass]
    public class ConfigMigrationTests
    {
        [TestMethod]
        public void Migrate_DialogueCooldownMs_5000_to_Seconds_5()
        {
            var config = new ModConfig();
            config.DialogueCooldownMs = 5000;
            config.DialogueCooldownSeconds = 3;  // 默认值

            config.MigrateLegacyFields();

            Assert.AreEqual(5, config.DialogueCooldownSeconds);
        }

        [TestMethod]
        public void Migrate_DialogueCooldownMs_Default_NoMigration()
        {
            var config = new ModConfig();
            config.DialogueCooldownMs = 3000;  // 默认值
            config.DialogueCooldownSeconds = 3;

            config.MigrateLegacyFields();

            Assert.AreEqual(3, config.DialogueCooldownSeconds);
        }

        [TestMethod]
        public void Migrate_GiftCooldownMs_60000_to_Seconds_60()
        {
            var config = new ModConfig();
            config.GiftCooldownMs = 60000;
            config.GiftCooldownSeconds = 30;  // 默认值

            config.MigrateLegacyFields();

            Assert.AreEqual(60, config.GiftCooldownSeconds);
        }

        [TestMethod]
        public void Validate_ProbabilityClamp_BelowZero()
        {
            var config = new ModConfig();
            config.ProactiveSpeechProbability = -0.5f;

            config.Validate();

            Assert.AreEqual(0.3f, config.ProactiveSpeechProbability);
        }

        [TestMethod]
        public void Validate_ProbabilityClamp_AboveOne()
        {
            var config = new ModConfig();
            config.ProactiveGiftProbability = 1.5f;

            config.Validate();

            Assert.AreEqual(0.1f, config.ProactiveGiftProbability);
        }

        [TestMethod]
        public void Validate_CooldownSecondsClamp_BelowMin()
        {
            var config = new ModConfig();
            config.DialogueCooldownSeconds = 0;

            config.Validate();

            Assert.AreEqual(3, config.DialogueCooldownSeconds);
        }

        [TestMethod]
        public void Validate_CooldownSecondsClamp_AboveMax()
        {
            var config = new ModConfig();
            config.GiftCooldownSeconds = 9999;

            config.Validate();

            Assert.AreEqual(30, config.GiftCooldownSeconds);
        }

        [TestMethod]
        public void Defaults_NewFieldsCorrect()
        {
            var config = new ModConfig();

            Assert.IsFalse(config.MultiProviderEnabled);
            Assert.IsTrue(config.EnableTrade);
            Assert.IsTrue(config.EnableHire);
            Assert.IsTrue(config.EnableDirector);
            Assert.IsTrue(config.EnableProactiveSpeech);
            Assert.IsTrue(config.EnableInfiniteDialogue);
            Assert.IsTrue(config.EnableProtagonistMapping);
            Assert.AreEqual(0.3f, config.ProactiveSpeechProbability);
            Assert.AreEqual(3, config.DialogueCooldownSeconds);
            Assert.AreEqual(30, config.GiftCooldownSeconds);
            Assert.AreEqual("minimax", config.DirectorPrimaryProvider);
            Assert.AreEqual("MiniMax-M3", config.DirectorPrimaryModel);
        }
    }
}
```

- [ ] **运行测试验证通过**

Run: `cd d:\Source\ValleyTalk && dotnet test src\ValleyAgent.UnitTests\ValleyAgent.UnitTests.csproj --filter "ConfigMigrationTests"`
Expected: PASS

- [ ] **Commit**

```bash
cd d:\Source\ValleyTalk
git add src/ValleyAgent/Config/ModConfig.cs src/ValleyAgent.UnitTests/ConfigMigrationTests.cs
git commit -m "feat(config): add multi-provider fields, feature toggles, probability, seconds cooldown"
```

---

## Task 4: C# 端 LlmConfigWriter + ServerProcessManager

**Files:**
- Create: `d:\Source\ValleyTalk\src\ValleyAgent\Config\LlmConfigWriter.cs`
- Modify: `d:\Source\ValleyTalk\src\ValleyAgent\WebSocket\ServerProcessManager.cs`
- Create: `d:\Source\ValleyTalk\src\ValleyAgent.UnitTests\LlmConfigWriterTests.cs`

### Step 4.1: 写 LlmConfigWriter 失败测试

- [ ] **创建 `d:\Source\ValleyTalk\src\ValleyAgent.UnitTests\LlmConfigWriterTests.cs`**

```csharp
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using ValleyAgent.Config;

namespace ValleyAgent.UnitTests
{
    [TestClass]
    public class LlmConfigWriterTests
    {
        private static ModConfig MakeValidMultiProviderConfig()
        {
            var c = new ModConfig
            {
                MultiProviderEnabled = true,
                DirectorPrimaryProvider = "minimax",
                DirectorPrimaryApiKey = "sk-dir-pri",
                DirectorPrimaryModel = "MiniMax-M3",
                DirectorPrimaryBaseUrl = "https://api.minimax.chat/v1",
                DirectorFallbackProvider = "deepseek",
                DirectorFallbackApiKey = "sk-dir-fb",
                DirectorFallbackModel = "deepseek-chat",
                DirectorFallbackBaseUrl = "https://api.deepseek.com/v1",
                ProtagonistPrimaryProvider = "minimax",
                ProtagonistPrimaryApiKey = "sk-pro-pri",
                ProtagonistPrimaryModel = "MiniMax-M2.7-highspeed",
                ProtagonistPrimaryBaseUrl = "https://api.minimax.chat/v1",
                ProtagonistFallbackProvider = "deepseek",
                ProtagonistFallbackApiKey = "sk-pro-fb",
                ProtagonistFallbackModel = "deepseek-chat",
                ProtagonistFallbackBaseUrl = "https://api.deepseek.com/v1",
                NpcPrimaryProvider = "deepseek",
                NpcPrimaryApiKey = "sk-npc-pri",
                NpcPrimaryModel = "deepseek-chat",
                NpcPrimaryBaseUrl = "https://api.deepseek.com/v1",
                NpcFallbackProvider = "",
                NpcFallbackApiKey = "",
                NpcFallbackModel = "",
                NpcFallbackBaseUrl = "",
                EnableProtagonistMapping = true,
                ProtagonistNpcs = "Abigail, Haley, Sebastian",
                LLMTimeoutSeconds = 60,
                MaxRetries = 3,
                Temperature = 0.7f,
            };
            return c;
        }

        [TestMethod]
        public void WriteRuntimeConfig_GeneratesValidJson()
        {
            var tmpDir = Path.Combine(Path.GetTempPath(), $"llm-test-{System.Guid.NewGuid():N}");
            try
            {
                Directory.CreateDirectory(tmpDir);
                var config = MakeValidMultiProviderConfig();

                var path = LlmConfigWriter.WriteRuntimeConfig(config, tmpDir);
                Assert.IsNotNull(path);
                Assert.IsTrue(File.Exists(path));

                var json = File.ReadAllText(path);
                var obj = JObject.Parse(json);

                Assert.AreEqual(1, obj["version"]!.Value<int>());
                Assert.IsNotNull(obj["roles"]!["director"]);
                Assert.IsNotNull(obj["roles"]!["protagonist"]);
                Assert.IsNotNull(obj["roles"]!["npc"]);
            }
            finally
            {
                if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true);
            }
        }

        [TestMethod]
        public void WriteRuntimeConfig_FallbackEmpty_FallbackIsNull()
        {
            var tmpDir = Path.Combine(Path.GetTempPath(), $"llm-test-{System.Guid.NewGuid():N}");
            try
            {
                Directory.CreateDirectory(tmpDir);
                var config = MakeValidMultiProviderConfig();
                config.NpcFallbackProvider = "";
                config.NpcFallbackModel = "";

                var path = LlmConfigWriter.WriteRuntimeConfig(config, tmpDir);
                var json = File.ReadAllText(path!);
                var obj = JObject.Parse(json);

                Assert.IsNull(obj["roles"]!["npc"]!["fallback"]!.Type == JTokenType.Null
                    ? obj["roles"]!["npc"]!["fallback"]
                    : null);
            }
            finally
            {
                if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true);
            }
        }

        [TestMethod]
        public void WriteRuntimeConfig_ProtagonistNpcsParsedToArray()
        {
            var tmpDir = Path.Combine(Path.GetTempPath(), $"llm-test-{System.Guid.NewGuid():N}");
            try
            {
                Directory.CreateDirectory(tmpDir);
                var config = MakeValidMultiProviderConfig();
                config.ProtagonistNpcs = "Abigail, Haley,Sebastian , Pierre";

                var path = LlmConfigWriter.WriteRuntimeConfig(config, tmpDir);
                var json = File.ReadAllText(path!);
                var obj = JObject.Parse(json);

                var arr = obj["protagonistNpcs"]!.ToObject<string[]>();
                CollectionAssert.AreEquivalent(
                    new[] { "Abigail", "Haley", "Sebastian", "Pierre" },
                    arr);
            }
            finally
            {
                if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true);
            }
        }

        [TestMethod]
        public void ParseProtagonistList_CommaSeparated()
        {
            var list = LlmConfigWriter.ParseProtagonistList("Abigail, Haley, Sebastian");
            CollectionAssert.AreEqual(new[] { "Abigail", "Haley", "Sebastian" }, list);
        }

        [TestMethod]
        public void ParseProtagonistList_CaseInsensitiveDedup()
        {
            var list = LlmConfigWriter.ParseProtagonistList("Abigail, abigail, ABIGAIL");
            Assert.AreEqual(1, list.Count);
        }

        [TestMethod]
        public void ParseProtagonistList_EmptyString()
        {
            var list = LlmConfigWriter.ParseProtagonistList("");
            Assert.AreEqual(0, list.Count);
        }

        [TestMethod]
        public void ParseProtagonistList_TrimsWhitespace()
        {
            var list = LlmConfigWriter.ParseProtagonistList("  Abigail  ,  Haley  ");
            CollectionAssert.AreEqual(new[] { "Abigail", "Haley" }, list);
        }
    }
}
```

- [ ] **运行测试验证失败**

Run: `cd d:\Source\ValleyTalk && dotnet test src\ValleyAgent.UnitTests\ValleyAgent.UnitTests.csproj --filter "LlmConfigWriterTests"`
Expected: FAIL（`LlmConfigWriter` 不存在）

### Step 4.2: 实现 LlmConfigWriter

- [ ] **创建 `d:\Source\ValleyTalk\src\ValleyAgent\Config\LlmConfigWriter.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using StardewModdingAPI;

namespace ValleyAgent.Config
{
    /// <summary>
    /// 把 ModConfig 的多 Provider 字段序列化成 runtime JSON，
    /// 供 TS 端 valley-ai-server.exe --llm-config 读取。
    /// 文件路径：{modDir}/llm-config.runtime.json
    /// </summary>
    public static class LlmConfigWriter
    {
        public static string GetRuntimeConfigPath(string modDir)
            => Path.Combine(modDir, "llm-config.runtime.json");

        /// <summary>
        /// 写 runtime JSON。仅在 MultiProviderEnabled=true 时调用。
        /// 返回写入的文件路径；失败返回 null。
        /// </summary>
        public static string? WriteRuntimeConfig(ModConfig config, string modDir, IMonitor? monitor = null)
        {
            try
            {
                var protagonistNpcs = ParseProtagonistList(config.ProtagonistNpcs);

                var runtimeConfig = new
                {
                    version = 1,
                    roles = new
                    {
                        director = BuildRole(
                            config.DirectorPrimaryProvider, config.DirectorPrimaryApiKey,
                            config.DirectorPrimaryModel, config.DirectorPrimaryBaseUrl,
                            config.DirectorFallbackProvider, config.DirectorFallbackApiKey,
                            config.DirectorFallbackModel, config.DirectorFallbackBaseUrl),
                        protagonist = BuildRole(
                            config.ProtagonistPrimaryProvider, config.ProtagonistPrimaryApiKey,
                            config.ProtagonistPrimaryModel, config.ProtagonistPrimaryBaseUrl,
                            config.ProtagonistFallbackProvider, config.ProtagonistFallbackApiKey,
                            config.ProtagonistFallbackModel, config.ProtagonistFallbackBaseUrl),
                        npc = BuildRole(
                            config.NpcPrimaryProvider, config.NpcPrimaryApiKey,
                            config.NpcPrimaryModel, config.NpcPrimaryBaseUrl,
                            config.NpcFallbackProvider, config.NpcFallbackApiKey,
                            config.NpcFallbackModel, config.NpcFallbackBaseUrl),
                    },
                    protagonistNpcs = protagonistNpcs,
                    enableProtagonistMapping = config.EnableProtagonistMapping,
                    global = new
                    {
                        timeoutMs = config.LLMTimeoutSeconds * 1000,
                        maxRetries = config.MaxRetries,
                        temperature = config.Temperature,
                        maxTokens = 800,
                    },
                };

                var path = GetRuntimeConfigPath(modDir);
                var json = JsonConvert.SerializeObject(runtimeConfig, Formatting.Indented);
                File.WriteAllText(path, json);
                monitor?.Log($"Wrote LLM runtime config to {path}", LogLevel.Debug);
                return path;
            }
            catch (Exception ex)
            {
                monitor?.Log($"Failed to write LLM runtime config: {ex.Message}", LogLevel.Error);
                return null;
            }
        }

        private static object BuildRole(
            string pProvider, string pKey, string pModel, string pUrl,
            string fProvider, string fKey, string fModel, string fUrl)
        {
            var primary = new { provider = pProvider, apiKey = pKey, model = pModel, baseUrl = pUrl };
            object? fallback = null;
            if (!string.IsNullOrWhiteSpace(fProvider) && !string.IsNullOrWhiteSpace(fModel))
            {
                fallback = new { provider = fProvider, apiKey = fKey, model = fModel, baseUrl = fUrl };
            }
            return new { primary, fallback };
        }

        /// <summary>解析逗号分隔的主角 NPC 列表，trim + 空值过滤 + OrdinalIgnoreCase 去重</summary>
        public static List<string> ParseProtagonistList(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return new List<string>();
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                set.Add(name);
            }
            return new List<string>(set);
        }
    }
}
```

- [ ] **运行测试验证通过**

Run: `cd d:\Source\ValleyTalk && dotnet test src\ValleyAgent.UnitTests\ValleyAgent.UnitTests.csproj --filter "LlmConfigWriterTests"`
Expected: PASS

### Step 4.3: 改造 ServerProcessManager

- [ ] **修改 `d:\Source\ValleyTalk\src\ValleyAgent\WebSocket\ServerProcessManager.cs`**

找到 `StartServerCoreAsync` 方法内的参数构造（约 L161-238），改造为双模式：

```csharp
// 在方法开头（约 L161 之前）追加：
string? llmConfigPath = null;

if (_config.MultiProviderEnabled)
{
    llmConfigPath = LlmConfigWriter.WriteRuntimeConfig(_config, _helper.DirectoryPath, _monitor);
    if (llmConfigPath == null)
    {
        _monitor.Log("Failed to write LLM runtime config; falling back to single provider mode.", LogLevel.Warn);
        _config.MultiProviderEnabled = false;  // 降级
    }
}

// 修改 serverArgs 构造（原 L222-238）：
string serverArgs;
if (llmConfigPath != null)
{
    // 多 provider 模式：只传 --llm-config
    serverArgs = $"--port {ServerPort} --agents-dir \"{agentsDir}\" --llm-config \"{llmConfigPath}\"";
}
else
{
    // 单 provider 模式：走现有逻辑（apiKey/llmModel/llmProvider/llmBaseUrl 解析不变）
    serverArgs = $"--port {ServerPort} --llm-api-key {apiKey} --llm-model {llmModel} --llm-provider {llmProvider}"
        + (string.IsNullOrEmpty(llmBaseUrl) ? "" : $" --llm-base-url {llmBaseUrl}")
        + $" --agents-dir \"{agentsDir}\"";
}

// 导演开关
if (!_config.EnableDirector)
{
    serverArgs += " --disable-director";
}
```

**注意**：`apiKey`/`llmModel`/`llmProvider`/`llmBaseUrl` 变量的解析逻辑（L161-203）保持不变，仅在多 provider 模式下不使用它们。需要用 `if (!llmConfigPath.HasValue)` 包裹这部分解析以避免无谓的环境变量读取（可选优化）。

- [ ] **编译验证**

Run: `cd d:\Source\ValleyTalk && dotnet build src\ValleyAgent\ValleyAgent.csproj`
Expected: 0 errors, 0 warnings

- [ ] **Commit**

```bash
cd d:\Source\ValleyTalk
git add src/ValleyAgent/Config/LlmConfigWriter.cs src/ValleyAgent/WebSocket/ServerProcessManager.cs src/ValleyAgent.UnitTests/LlmConfigWriterTests.cs
git commit -m "feat(config): add LlmConfigWriter + ServerProcessManager dual-mode"
```

---

## Task 5: C# 端 WebSocket 自动绑定 + 冷却字段加载修复

**Files:**
- Modify: `d:\Source\ValleyTalk\src\ValleyAgent\Initialization\ServiceInitializer.cs`

### Step 5.1: 修复 WebSocketUrl 断裂 + 冷却字段未加载

- [ ] **读取 `d:\Source\ValleyTalk\src\ValleyAgent\Initialization\ServiceInitializer.cs` 的 L210 和 L162-165**

确认当前代码：
- L210: `var wsClient = new WebSocketClient(config.AgentServerUri, "ValleyAgent");`
- L162-165: `var dialogueStateManager = new Api.DialogueStateManager { CooldownHintCallback = ... };`

- [ ] **修改 L210：WebSocket URL 从 ServerPort 自动绑定**

```csharp
// 改前
var wsClient = new WebSocketClient(config.AgentServerUri, "ValleyAgent");

// 改后
var wsUrl = $"ws://127.0.0.1:{config.ServerPort}";
var wsClient = new WebSocketClient(wsUrl, "ValleyAgent");
```

- [ ] **修改 L162-165：加载冷却字段到 DialogueStateManager**

```csharp
// 改前
var dialogueStateManager = new Api.DialogueStateManager
{
    CooldownHintCallback = (npcName, msg) => _monitor?.Log(...)
};

// 改后
var dialogueStateManager = new Api.DialogueStateManager
{
    DialogueCooldownMs = config.DialogueCooldownSeconds * 1000,
    GiftCooldownMs = config.GiftCooldownSeconds * 1000,
    CooldownHintCallback = (npcName, msg) => _monitor?.Log(...)
};
```

**注意**：如果 `DialogueStateManager` 没有 `GiftCooldownMs` 属性，需要先在 `d:\Source\ValleyTalk\src\ValleyAgent.Abstractions\Api\DialogueStateManager.cs` 追加该属性（参考现有 `DialogueCooldownMs` 的声明方式）。

- [ ] **修改 L233 日志（如果存在引用 AgentServerUri 的日志）**

```csharp
// 改前
_monitor.Log($"Agent Server initialized (WS: {config.AgentServerUri}, ...)", LogLevel.Debug);
// 改后
_monitor.Log($"Agent Server initialized (WS: ws://127.0.0.1:{config.ServerPort}, ...)", LogLevel.Debug);
```

- [ ] **编译验证**

Run: `cd d:\Source\ValleyTalk && dotnet build src\ValleyAgent\ValleyAgent.csproj`
Expected: 0 errors, 0 warnings（如果有 AgentServerUri 引用残留，全部替换为自动绑定）

- [ ] **Commit**

```bash
cd d:\Source\ValleyTalk
git add src/ValleyAgent/Initialization/ServiceInitializer.cs src/ValleyAgent.Abstractions/Api/DialogueStateManager.cs
git commit -m "fix: auto-bind WebSocket URL from ServerPort + load cooldown config into DialogueStateManager"
```

---

## Task 6: C# 端 GMCMIntegration UI 扩展

**Files:**
- Modify: `d:\Source\ValleyTalk\src\ValleyAgent\Config\GMCMIntegration.cs`

### Step 6.1: 基础页新增功能开关 + 概率 + 秒单位冷却

- [ ] **修改 `d:\Source\ValleyTalk\src\ValleyAgent\Config\GMCMIntegration.cs` 的 `AddBasicPageOptions`**

找到 Section 2 "Agent Behavior" 末尾（约 L325 `MaxConsecutiveIdleBeforeRelease` 之后），追加功能开关：

```csharp
// ─── 功能开关扩展 ───
gmcm.AddBoolOption(manifest,
    name: () => T("NPC 交易", "NPC Trade", config),
    tooltip: () => T("允许与 NPC 买卖物品", "Allow trading with NPCs", config),
    getValue: () => config.EnableTrade,
    setValue: v => config.EnableTrade = v);

gmcm.AddBoolOption(manifest,
    name: () => T("NPC 雇佣", "NPC Hire", config),
    tooltip: () => T("允许雇佣 NPC 协助工作", "Allow hiring NPCs", config),
    getValue: () => config.EnableHire,
    setValue: v => config.EnableHire = v);

gmcm.AddBoolOption(manifest,
    name: () => T("导演模式", "Director Mode", config),
    tooltip: () => T("开启叙事编排；关闭则 NPC 完全自主", "Enable director; off = fully autonomous", config),
    getValue: () => config.EnableDirector,
    setValue: v => config.EnableDirector = v);

gmcm.AddBoolOption(manifest,
    name: () => T("NPC 主动发言", "Proactive Speech", config),
    tooltip: () => T("NPC 主动喊话/搭话总开关", "Master switch for proactive speech", config),
    getValue: () => config.EnableProactiveSpeech,
    setValue: v => config.EnableProactiveSpeech = v);

gmcm.AddBoolOption(manifest,
    name: () => T("无限对话", "Infinite Dialogue", config),
    tooltip: () => T("非 Agent NPC 也能 AI 对话", "AI dialogue with all NPCs", config),
    getValue: () => config.EnableInfiniteDialogue,
    setValue: v => config.EnableInfiniteDialogue = v);
```

找到 Section 3 "Dialogue & Social" 中的 `GiftCooldownMs` 和 `DialogueCooldownMs` UI（约 L352-362），替换为秒单位：

```csharp
// 改前（L352-360）：
gmcm.AddNumberOption(manifest,
    name: () => T("送礼冷却(ms)", "Gift Cooldown (ms)", config),
    ...
    getValue: () => config.GiftCooldownMs,
    setValue: v => config.GiftCooldownMs = v,
    min: 0, max: 60000, interval: 1000);

gmcm.AddNumberOption(manifest,
    name: () => T("对话冷却(ms)", "Dialogue Cooldown (ms)", config),
    ...
    getValue: () => config.DialogueCooldownMs,
    setValue: v => config.DialogueCooldownMs = v,
    min: 0, max: 60000, interval: 1000);

// 改后：
gmcm.AddNumberOption(manifest,
    name: () => T("送礼冷却(秒)", "Gift Cooldown (sec)", config),
    tooltip: () => T("两次送礼最小间隔 (1-600)", "Min gap between gifts (1-600 s)", config),
    getValue: () => config.GiftCooldownSeconds,
    setValue: v => config.GiftCooldownSeconds = v,
    min: 1, max: 600, interval: 1);

gmcm.AddNumberOption(manifest,
    name: () => T("对话冷却(秒)", "Dialogue Cooldown (sec)", config),
    tooltip: () => T("同一 NPC 相邻对话最小间隔 (1-60)", "Min gap between dialogue (1-60 s)", config),
    getValue: () => config.DialogueCooldownSeconds,
    setValue: v => config.DialogueCooldownSeconds = v,
    min: 1, max: 60, interval: 1);
```

在 Section 3 末尾追加概率设置：

```csharp
// ─── 概率设置 ───
gmcm.AddNumberOption(manifest,
    name: () => T("主动搭话概率", "Proactive Speech Prob", config),
    tooltip: () => T("NPC 主动搭话触发概率 (0-1)", "Proactive speech probability (0-1)", config),
    getValue: () => config.ProactiveSpeechProbability,
    setValue: v => config.ProactiveSpeechProbability = Math.Clamp(v, 0f, 1f),
    min: 0f, max: 1f, interval: 0.05f);

gmcm.AddNumberOption(manifest,
    name: () => T("主动送礼概率", "Proactive Gift Prob", config),
    tooltip: () => T("NPC 主动送礼触发概率 (0-1)", "Proactive gift probability (0-1)", config),
    getValue: () => config.ProactiveGiftProbability,
    setValue: v => config.ProactiveGiftProbability = Math.Clamp(v, 0f, 1f),
    min: 0f, max: 1f, interval: 0.05f);

gmcm.AddNumberOption(manifest,
    name: () => T("主动跟随概率", "Proactive Follow Prob", config),
    tooltip: () => T("NPC 主动跟随触发概率 (0-1)", "Proactive follow probability (0-1)", config),
    getValue: () => config.ProactiveFollowProbability,
    setValue: v => config.ProactiveFollowProbability = Math.Clamp(v, 0f, 1f),
    min: 0f, max: 1f, interval: 0.05f);

gmcm.AddNumberOption(manifest,
    name: () => T("主动交易概率", "Proactive Trade Prob", config),
    tooltip: () => T("NPC 主动求购触发概率 (0-1)", "Proactive trade probability (0-1)", config),
    getValue: () => config.ProactiveTradeProbability,
    setValue: v => config.ProactiveTradeProbability = Math.Clamp(v, 0f, 1f),
    min: 0f, max: 1f, interval: 0.05f);
```

删除基础页的 `WebSocketUrl` UI（约 L167）：

```csharp
// 删除这段：
gmcm.AddTextOption(manifest,
    name: () => T("WebSocket 地址", "WebSocket URL", config),
    ...
    getValue: () => config.WebSocketUrl,
    setValue: v => config.WebSocketUrl = v);
```

### Step 6.2: 高级页新增多 Provider 区

- [ ] **修改 `AddAdvancedPageOptions`（约 L461 起）**

在方法开头（Section 4 "Server Advanced" 之前）追加多 Provider 区：

```csharp
// ─── 多 Provider 模式 ───
gmcm.AddSectionTitle(manifest, () => T("多 Provider 模式", "Multi-Provider", config));

gmcm.AddBoolOption(manifest,
    name: () => T("启用多 Provider", "Enable Multi-Provider", config),
    tooltip: () => T("开启后单 Provider 配置被忽略；需重启", "When on, single provider ignored. Restart required.", config),
    getValue: () => config.MultiProviderEnabled,
    setValue: v => config.MultiProviderEnabled = v);

gmcm.AddParagraph(manifest, () => T(
    "⚠️ 启用后基础页的单 Provider 配置将被忽略。3 角色（导演/主角/普通 NPC）各可配主备 provider，主 provider 欠费时自动回退到备。",
    "⚠️ When enabled, single provider config on Basic page is ignored. 3 roles (Director/Protagonist/NPC) each have primary+fallback. Auto-fallback on billing error.",
    config));

// ─── 导演 ───
gmcm.AddSectionTitle(manifest, () => T("导演 (Director)", "Director", config));
AddProviderSlotUI(gmcm, manifest, config, "Director", "导演", "Director");

// ─── 主角 NPC ───
gmcm.AddSectionTitle(manifest, () => T("主角 NPC (Protagonist)", "Protagonist", config));
AddProviderSlotUI(gmcm, manifest, config, "Protagonist", "主角", "Protagonist");

// ─── 普通 NPC ───
gmcm.AddSectionTitle(manifest, () => T("普通 NPC (NPC)", "NPC", config));
AddProviderSlotUI(gmcm, manifest, config, "Npc", "普通 NPC", "NPC");

// ─── 角色标记 ───
gmcm.AddSectionTitle(manifest, () => T("角色标记", "Role Mapping", config));

gmcm.AddBoolOption(manifest,
    name: () => T("启用角色区分", "Enable Role Mapping", config),
    tooltip: () => T("关闭时所有 NPC 走普通 NPC 配置", "Off = all NPCs use NPC role", config),
    getValue: () => config.EnableProtagonistMapping,
    setValue: v => config.EnableProtagonistMapping = v);

gmcm.AddTextOption(manifest,
    name: () => T("主角 NPC 列表", "Protagonist NPC List", config),
    tooltip: () => T("逗号分隔；其余归普通 NPC", "Comma-separated; others use NPC role", config),
    getValue: () => config.ProtagonistNpcs ?? "",
    setValue: v => config.ProtagonistNpcs = v ?? "");
```

### Step 6.3: 新增 AddProviderSlotUI 辅助方法

- [ ] **在 `GMCMIntegration.cs` 类内追加静态方法和字段**

```csharp
private static readonly string[] s_providerPresets =
{
    "minimax", "deepseek", "openai", "anthropic",
    "moonshot", "openrouter", "zhipu", "baichuan", "qwen",
    "lmstudio", "custom"
};

private static void AddProviderSlotUI(
    IGenericModConfigMenuApi gmcm, IManifest manifest, ModConfig config,
    string prefix, string labelCn, string labelEn)
{
    // 主 Provider
    gmcm.AddTextOption(manifest,
        name: () => T($"{labelCn} 主 Provider", $"{labelEn} Primary Provider", config),
        tooltip: () => T("LLM 提供商", "LLM provider", config),
        getValue: () => (string)GetConfigField(config, prefix + "PrimaryProvider"),
        setValue: v => SetConfigField(config, prefix + "PrimaryProvider", v),
        allowedValues: s_providerPresets);

    gmcm.AddTextOption(manifest,
        name: () => T($"{labelCn} 主 API Key", $"{labelEn} Primary API Key", config),
        tooltip: () => T("API 密钥", "API key", config),
        getValue: () => (string)GetConfigField(config, prefix + "PrimaryApiKey"),
        setValue: v => SetConfigField(config, prefix + "PrimaryApiKey", v));

    gmcm.AddTextOption(manifest,
        name: () => T($"{labelCn} 主 Model", $"{labelEn} Primary Model", config),
        tooltip: () => T("模型标识", "Model identifier", config),
        getValue: () => (string)GetConfigField(config, prefix + "PrimaryModel"),
        setValue: v => SetConfigField(config, prefix + "PrimaryModel", v));

    gmcm.AddTextOption(manifest,
        name: () => T($"{labelCn} 主 Base URL", $"{labelEn} Primary Base URL", config),
        tooltip: () => T("API 基础 URL", "API base URL", config),
        getValue: () => (string)GetConfigField(config, prefix + "PrimaryBaseUrl"),
        setValue: v => SetConfigField(config, prefix + "PrimaryBaseUrl", v));

    // 备 Provider
    gmcm.AddTextOption(manifest,
        name: () => T($"{labelCn} 备 Provider", $"{labelEn} Fallback Provider", config),
        tooltip: () => T("留空=无回退", "Empty = no fallback", config),
        getValue: () => (string)GetConfigField(config, prefix + "FallbackProvider"),
        setValue: v => SetConfigField(config, prefix + "FallbackProvider", v),
        allowedValues: s_providerPresets);

    gmcm.AddTextOption(manifest,
        name: () => T($"{labelCn} 备 API Key", $"{labelEn} Fallback API Key", config),
        tooltip: () => T("备 provider 密钥", "Fallback API key", config),
        getValue: () => (string)GetConfigField(config, prefix + "FallbackApiKey"),
        setValue: v => SetConfigField(config, prefix + "FallbackApiKey", v));

    gmcm.AddTextOption(manifest,
        name: () => T($"{labelCn} 备 Model", $"{labelEn} Fallback Model", config),
        tooltip: () => T("备模型标识", "Fallback model identifier", config),
        getValue: () => (string)GetConfigField(config, prefix + "FallbackModel"),
        setValue: v => SetConfigField(config, prefix + "FallbackModel", v));

    gmcm.AddTextOption(manifest,
        name: () => T($"{labelCn} 备 Base URL", $"{labelEn} Fallback Base URL", config),
        tooltip: () => T("备 API 基础 URL", "Fallback API base URL", config),
        getValue: () => (string)GetConfigField(config, prefix + "FallbackBaseUrl"),
        setValue: v => SetConfigField(config, prefix + "FallbackBaseUrl", v));
}

private static object GetConfigField(ModConfig config, string fieldName)
{
    var prop = typeof(ModConfig).GetProperty(fieldName);
    return prop?.GetValue(config) ?? "";
}

private static void SetConfigField(ModConfig config, string fieldName, object value)
{
    var prop = typeof(ModConfig).GetProperty(fieldName);
    prop?.SetValue(config, value);
}
```

### Step 6.4: CopyConfig 扩展

- [ ] **修改 `CopyConfig` 方法（约 L899-995）**

在方法末尾追加新增字段的复制：

```csharp
// 多 Provider 模式
target.MultiProviderEnabled = source.MultiProviderEnabled;
target.DirectorPrimaryProvider = source.DirectorPrimaryProvider;
target.DirectorPrimaryApiKey = source.DirectorPrimaryApiKey;
target.DirectorPrimaryModel = source.DirectorPrimaryModel;
target.DirectorPrimaryBaseUrl = source.DirectorPrimaryBaseUrl;
target.DirectorFallbackProvider = source.DirectorFallbackProvider;
target.DirectorFallbackApiKey = source.DirectorFallbackApiKey;
target.DirectorFallbackModel = source.DirectorFallbackModel;
target.DirectorFallbackBaseUrl = source.DirectorFallbackBaseUrl;
target.ProtagonistPrimaryProvider = source.ProtagonistPrimaryProvider;
target.ProtagonistPrimaryApiKey = source.ProtagonistPrimaryApiKey;
target.ProtagonistPrimaryModel = source.ProtagonistPrimaryModel;
target.ProtagonistPrimaryBaseUrl = source.ProtagonistPrimaryBaseUrl;
target.ProtagonistFallbackProvider = source.ProtagonistFallbackProvider;
target.ProtagonistFallbackApiKey = source.ProtagonistFallbackApiKey;
target.ProtagonistFallbackModel = source.ProtagonistFallbackModel;
target.ProtagonistFallbackBaseUrl = source.ProtagonistFallbackBaseUrl;
target.NpcPrimaryProvider = source.NpcPrimaryProvider;
target.NpcPrimaryApiKey = source.NpcPrimaryApiKey;
target.NpcPrimaryModel = source.NpcPrimaryModel;
target.NpcPrimaryBaseUrl = source.NpcPrimaryBaseUrl;
target.NpcFallbackProvider = source.NpcFallbackProvider;
target.NpcFallbackApiKey = source.NpcFallbackApiKey;
target.NpcFallbackModel = source.NpcFallbackModel;
target.NpcFallbackBaseUrl = source.NpcFallbackBaseUrl;
target.EnableProtagonistMapping = source.EnableProtagonistMapping;
target.ProtagonistNpcs = source.ProtagonistNpcs;

// 功能开关
target.EnableTrade = source.EnableTrade;
target.EnableHire = source.EnableHire;
target.EnableDirector = source.EnableDirector;
target.EnableProactiveSpeech = source.EnableProactiveSpeech;
target.EnableInfiniteDialogue = source.EnableInfiniteDialogue;

// 概率
target.ProactiveSpeechProbability = source.ProactiveSpeechProbability;
target.ProactiveGiftProbability = source.ProactiveGiftProbability;
target.ProactiveFollowProbability = source.ProactiveFollowProbability;
target.ProactiveTradeProbability = source.ProactiveTradeProbability;

// 冷却秒
target.DialogueCooldownSeconds = source.DialogueCooldownSeconds;
target.GiftCooldownSeconds = source.GiftCooldownSeconds;
```

- [ ] **编译验证**

Run: `cd d:\Source\ValleyTalk && dotnet build src\ValleyAgent\ValleyAgent.csproj`
Expected: 0 errors, 0 warnings

- [ ] **Commit**

```bash
cd d:\Source\ValleyTalk
git add src/ValleyAgent/Config/GMCMIntegration.cs
git commit -m "feat(gmcm): add multi-provider UI, feature toggles, probability, seconds cooldown"
```

---

## Task 7: C# 端功能开关消费点 + 端到端验证

**Files:**
- Modify: `d:\Source\ValleyTalk\src\ValleyAgent\Features\GiftTradeMenuLogic.cs`（EnableTrade）
- Modify: `d:\Source\ValleyTalk\src\ValleyAgent\Features\ContractService.cs`（EnableHire + ProactiveTradeProbability）
- Modify: `d:\Source\ValleyTalk\src\ValleyAgent\Features\ProactiveSpeechQuota.cs`（EnableProactiveSpeech）
- Modify: `d:\Source\ValleyTalk\src\ValleyAgent\Features\ProactiveSpeechTrigger.cs`（ProactiveSpeechProbability）
- Modify: `d:\Source\ValleyTalk\src\ValleyAgent\Features\SocialCommands.cs`（ProactiveGiftProbability）
- Modify: `d:\Source\ValleyTalk\src\ValleyAgent\Features\RuleBasedDecisionEngine.cs`（ProactiveFollowProbability）
- Modify: `d:\Source\ValleyTalk\src\ValleyAgent\Patches\NPCDialoguePatch.cs`（EnableInfiniteDialogue）

**注意**：以上文件路径是预期的，实际路径需要先用 Grep 确认每个消费点类的真实位置。

### Step 7.1: 定位所有消费点的真实文件路径

- [ ] **用 Grep 定位每个消费点**

```
Grep "class GiftTradeMenuLogic" → 找 EnableTrade 消费点
Grep "class ContractService" → 找 EnableHire 消费点
Grep "class ProactiveSpeechQuota" → 找 EnableProactiveSpeech 消费点
Grep "class ProactiveSpeechTrigger" → 找 ProactiveSpeechProbability 消费点
Grep "class SocialCommands" → 找 ProactiveGiftProbability 消费点
Grep "class RuleBasedDecisionEngine" → 找 ProactiveFollowProbability 消费点
Grep "class NPCDialoguePatch" → 找 EnableInfiniteDialogue 消费点
```

**如果某个类不存在**（例如交易/雇佣功能尚未实现），则该消费点跳过，在 Commit message 中标注"X 功能尚未实现，开关预留"。

### Step 7.2: 在每个消费点入口添加开关判断

- [ ] **对每个消费点，在功能入口处添加 `if (!config.XxxEnabled) return;` 或 `if (!config.XxxEnabled) return false;`**

具体模式（以 EnableTrade 为例）：

```csharp
public bool TryStartTrade(...)
{
    if (!_config.EnableTrade) return false;
    // ... 现有逻辑
}
```

对于概率字段（ProactiveSpeechProbability 等），在触发判定处添加：

```csharp
// ProactiveSpeechTrigger 内
if (Random.Shared.NextDouble() < _config.ProactiveSpeechProbability)
{
    // 触发主动发言
}
```

- [ ] **对每个消费点编译验证**

Run: `cd d:\Source\ValleyTalk && dotnet build src\ValleyAgent\ValleyAgent.csproj`
Expected: 0 errors, 0 warnings

### Step 7.3: 端到端验证清单

- [ ] **TS 端全部测试绿**

Run: `cd D:\Source\ValleyAI && bun test`
Expected: 所有测试 PASS

- [ ] **TS 端类型检查绿**

Run: `cd D:\Source\ValleyAI && bun run tsc --noEmit`
Expected: 0 errors

- [ ] **TS 端协议契约绿**

Run: `cd D:\Source\ValleyAI && bun run check:protocol`
Expected: 全绿

- [ ] **C# 端编译 0 警告**

Run: `cd d:\Source\ValleyTalk && dotnet build src\ValleyAgent\ValleyAgent.csproj -warnaserror`
Expected: 0 errors, 0 warnings

- [ ] **C# 端单元测试全绿**

Run: `cd d:\Source\ValleyTalk && dotnet test src\ValleyAgent.UnitTests\ValleyAgent.UnitTests.csproj`
Expected: 所有测试 PASS

- [ ] **游戏内手动验证（L5 体验打分）**

按设计文档 7.5 节的验收清单逐项验证：
1. 单 provider 模式：行为与现有完全一致
2. 多 provider 正常：导演用 M3，Abigail 用 M2.7，Pierre 用 DeepSeek
3. MiniMax 欠费回退：自动切 DeepSeek，对话不中断
4. 角色区分关闭：所有 NPC 走 npc 角色
5. 5xx 不回退：重试 3 次失败，不切 DeepSeek
6. 功能开关：各开关关闭后功能完全停止
7. 冷却秒单位：UI 显示秒，行为正确
8. WebSocket 自动绑定：改端口→WS 自动跟随

- [ ] **Final Commit**

```bash
cd d:\Source\ValleyTalk
git add src/ValleyAgent/
git commit -m "feat: wire feature toggles + probability consumption points"
```

```bash
cd D:\Source\ValleyAI
git add .
git commit -m "test: verify multi-provider integration end-to-end"
```

---

## Self-Review Checklist

### Spec coverage

| 设计文档章节 | 对应 Task | 覆盖 |
|---|---|---|
| §2 架构概览 | Task 1-2 | ✓ |
| §3.1 ModConfig 新增字段 | Task 3 | ✓ |
| §3.2 Runtime JSON 结构 | Task 4 | ✓ |
| §3.3 内置 Provider 预设 | Task 1.1 | ✓ |
| §4 GMCM UI 布局 | Task 6 | ✓ |
| §5 LlmRouter 实现 | Task 1 | ✓ |
| §6 C# 端改动 | Task 3-6 | ✓ |
| §7 测试策略 | Task 1.2/1.4/2.6/3.3/4.1/7.3 | ✓ |
| WebSocket 自动绑定（设计 2.5） | Task 5 | ✓ |
| 冷却改秒（设计 2.5） | Task 3+5 | ✓ |
| 角色标记开关（设计 2.5） | Task 3+6 | ✓ |
| 功能开关消费点（设计 6.5） | Task 7 | ✓ |

### Placeholder scan

- 无 "TBD"/"TODO"/"implement later"
- 每个代码步骤都有完整代码
- Task 7 的消费点定位用 Grep 是因为文件路径未确认，但给了明确的 Grep 命令

### Type consistency

- `LlmRole` = "director" | "protagonist" | "npc"（Task 1 定义，Task 2 使用）
- `LlmRouterConfig`（Task 1 定义，Task 2/4 使用）
- `RoleProviderConfig`（Task 1 定义，Task 4 序列化）
- C# 字段名 `DirectorPrimaryProvider` 等（Task 3 定义，Task 4/6 使用）
- `DialogueCooldownSeconds`/`GiftCooldownSeconds`（Task 3 定义，Task 5/6 使用）

### 已知风险

1. **Task 7 消费点文件路径未确认**：交易/雇佣功能可能尚未实现，需 Grep 确认。如果不存在，开关预留但不报错。
2. **Director 当前未实例化**：Task 2.5 的 directorCallLlm 适配层是 future use，不实际构造 Director。
3. **GMCM 反射 GetField/SetField**：只处理 string 类型字段，bool/int/float 字段手写闭包。Task 6 的 AddProviderSlotUI 只用于 string 字段（provider/apiKey/model/baseUrl），符合约束。

---

## Execution Handoff

Plan complete and saved to `docs/superpowers/plans/2026-08-03-multi-llm-provider.md`. Two execution options:

**1. Subagent-Driven (recommended)** - I dispatch a fresh subagent per task, review between tasks, fast iteration

**2. Inline Execution** - Execute tasks in this session using executing-plans, batch execution with checkpoints

Which approach?
