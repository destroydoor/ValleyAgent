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

describe("LlmRouter 备用链（fallbacks）", () => {
  function makeChainedConfig(): LlmRouterConfig {
    const cfg = makeRouterConfig();
    cfg.roles.npc = {
      primary: { provider: "minimax", apiKey: "sk-npc-pri", model: "MiniMax-M3", baseUrl: "https://api.minimax.chat/v1" },
      fallback: { provider: "sensenova", apiKey: "sk-fb1", model: "sensenova-6.8-flash-lite", baseUrl: "https://token.sensenova.cn/v1" },
      fallbacks: [
        { provider: "anthropic", apiKey: "sk-fb2", model: "deepseek-flash", baseUrl: "https://api.deepseek.com/anthropic/v1" },
        { provider: "minimax", apiKey: "sk-fb3", model: "MiniMax-M2.7-highspeed", baseUrl: "https://api.minimax.chat/v1" },
      ],
    };
    return cfg;
  }

  test("fallback 与 fallbacks 合并，fallback 在前", () => {
    const router = new LlmRouter(makeChainedConfig());
    const chain = router.getProviderFallbacks("npc");
    expect(chain).toHaveLength(3);
    expect(chain[0]!.config.model).toBe("sensenova-6.8-flash-lite");
    expect(chain[1]!.config.model).toBe("deepseek-flash");
    expect(chain[2]!.config.model).toBe("MiniMax-M2.7-highspeed");
    // getProviderFallback 语义不变：返回第一级备用
    expect(router.getProviderFallback("npc")!.config.model).toBe("sensenova-6.8-flash-lite");
  });

  test("billing 错误沿链逐级回退", async () => {
    const router = new LlmRouter(makeChainedConfig());
    const called: string[] = [];
    const spy = (model: string, ok: boolean) => mock(() => {
      called.push(model);
      return ok ? Promise.resolve({ content: model, usage: {} }) : Promise.reject(new LLMBillingError(`402 ${model}`));
    });
    router.getProvider("npc")._setCallOverride(spy("primary", false) as any);
    const chain = router.getProviderFallbacks("npc");
    chain[0]!._setCallOverride(spy("fb1", false) as any);
    chain[1]!._setCallOverride(spy("fb2", true) as any);
    chain[2]!._setCallOverride(spy("fb3", true) as any);

    const result = await router.chatCompletion("npc", []);
    expect(result.content).toBe("fb2");
    expect(called).toEqual(["primary", "fb1", "fb2"]);
  });

  test("整链 billing 错误 → 抛最后一个 LLMBillingError", async () => {
    const router = new LlmRouter(makeChainedConfig());
    const boom = () => mock(() => Promise.reject(new LLMBillingError("429")));
    router.getProvider("npc")._setCallOverride(boom() as any);
    for (const p of router.getProviderFallbacks("npc")) p._setCallOverride(boom() as any);

    await expect(router.chatCompletion("npc", [])).rejects.toThrow(LLMBillingError);
  });

  test("链中某级抛非 billing 错误 → 直接抛出不继续回退", async () => {
    const router = new LlmRouter(makeChainedConfig());
    router.getProvider("npc")._setCallOverride(
      mock(() => Promise.reject(new LLMBillingError("402"))) as any,
    );
    const chain = router.getProviderFallbacks("npc");
    chain[0]!._setCallOverride(mock(() => Promise.reject(new LLMUnavailableError("500"))) as any);
    const lastSpy = mock(() => Promise.resolve({ content: "fb3", usage: {} }));
    chain[1]!._setCallOverride(lastSpy as any);
    chain[2]!._setCallOverride(lastSpy as any);

    await expect(router.chatCompletion("npc", [])).rejects.toThrow(LLMUnavailableError);
    expect(lastSpy).toHaveBeenCalledTimes(0);
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

  test("fallbacks 数组元素缺 apiKey → 抛错", () => {
    const path = join(tmpDir, "bad-fallbacks.json");
    writeFileSync(path, JSON.stringify({
      version: 1,
      roles: {
        director: {
          primary: { provider: "minimax", apiKey: "k", model: "m", baseUrl: "u" },
          fallbacks: [{ provider: "deepseek", apiKey: "", model: "m", baseUrl: "u" }],
        },
        protagonist: { primary: { provider: "minimax", apiKey: "k", model: "m", baseUrl: "u" } },
        npc: { primary: { provider: "deepseek", apiKey: "k", model: "m", baseUrl: "u" } },
      },
      protagonistNpcs: [],
      enableProtagonistMapping: true,
    }));
    expect(() => loadRouterConfig(path)).toThrow(/apiKey/);
  });

  test("fallbacks 非数组 → 抛错", () => {
    const path = join(tmpDir, "bad-fallbacks-type.json");
    writeFileSync(path, JSON.stringify({
      version: 1,
      roles: {
        director: {
          primary: { provider: "minimax", apiKey: "k", model: "m", baseUrl: "u" },
          fallbacks: "not-an-array",
        },
        protagonist: { primary: { provider: "minimax", apiKey: "k", model: "m", baseUrl: "u" } },
        npc: { primary: { provider: "deepseek", apiKey: "k", model: "m", baseUrl: "u" } },
      },
      protagonistNpcs: [],
      enableProtagonistMapping: true,
    }));
    expect(() => loadRouterConfig(path)).toThrow(/fallbacks/);
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