import { describe, test, expect, mock, beforeEach, afterEach } from "bun:test";
import { LlmRouter, type LlmRouterConfig } from "../src/llm-router";
import { LLMBillingError, LLMUnavailableError } from "../src/llm-provider";
import { loadRouterConfig } from "../src/llm-router";
import { writeFileSync, mkdirSync, rmSync } from "node:fs";
import { join } from "node:path";

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