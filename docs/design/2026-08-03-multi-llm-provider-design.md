# 多 LLM Provider 支持 — 设计文档

> **日期**：2026-08-03
> **状态**：设计已与用户对齐，待实施
> **分支**：`feature/multi-llm-provider`（C# 仓库 `ValleyTalk` + TS 仓库 `ValleyAI`）
> **上游需求**：用户要求支持设置多个 LLM API Key，按角色路由（导演用 MiniMax M3、主角 NPC 用 M2.7、普通 NPC 用 DeepSeek），MiniMax 欠费时自动回退到 DeepSeek，同时支持 Anthropic 和 OpenAI 两种通用接口，内置常见厂商，支持自定义 URL。

---

## 1. 决策摘要

| 决策点 | 选择 | 理由 |
|---|---|---|
| 配置存储 | C# ModConfig + GMCM UI | 用户明确要求游戏内可视化编辑 |
| UI 布局 | 单界面，简单在前复杂在后 | 用户明确要求不拆两个 mod 设置 |
| 多 provider 呈现 | 固定槽位（主+备，9 组字段平铺） | GMCM 对数组/嵌套支持差，固定槽位原生支持 |
| 角色路由粒度 | 按角色类型（director/protagonist/npc） | 配置量小、语义清晰、覆盖目标场景 |
| 回退触发条件 | 仅 billing 错误（402/429） | 语义清晰：回退=provider 没钱，重试=provider 抽风 |
| 实现方案 | runtime JSON 文件传递 | key 不进命令行（安全），JSON 表达力强，可热重载 |
| WebSocket 地址 | 从 ServerPort 自动绑定 | 用户要求，减少配置项 |
| 角色标记 | 支持开关 | 用户要求，关闭时所有 NPC 走 npc 角色 |
| 冷却单位 | 秒（从 ms 改） | 用户要求，更直观 |

---

## 2. 架构概览

### 2.1 数据流

```
C# ModConfig (9 组字段 + 角色标记 + 功能开关)
        │ 序列化
        ▼
{modDir}/llm-config.runtime.json
        │
C# ServerProcessManager ──启动──> valley-ai-server.exe --llm-config <path>
        │
TS cli.ts ──读取 JSON──> LlmRouterConfig
        │
TS server.ts ──构造──> LlmRouter (多 VercelAIProvider + 角色→provider 映射 + 回退链)
        │
StardewAgentRegistry ──持有 LlmRouter── 替代原单一 VercelAIProvider
        │
Registry.getOrCreate(npcName) ──按角色查──> LlmRouter.getProvider(role) ──> VercelAIProvider
Director ──按角色查──> LlmRouter.getProvider("director") ──> VercelAIProvider
```

### 2.2 新组件

| 端 | 文件 | 职责 |
|---|---|---|
| C# | `ModConfig.cs` | 加 9 组字段 + 角色标记 + 功能开关 + 概率 + 秒单位冷却 |
| C# | `LlmConfigWriter.cs`（新） | 把 9 组配置序列化成 runtime JSON |
| C# | `ServerProcessManager.cs` | 加 `--llm-config <path>` 参数 |
| C# | `GMCMIntegration.cs` | 加 9 组字段 UI + 功能开关 + 概率 UI |
| TS | `llm-router.ts`（新） | LlmRouter 类：多 provider + 角色路由 + 回退 |
| TS | `llm-config.ts` | 扩展支持多 provider + 角色路由配置类型 |
| TS | `cli.ts` | 加 `--llm-config` 参数 |
| TS | `server.ts` | 构造 LlmRouter 替代单一 provider |
| TS | `stardew-agent-registry.ts` | 持有 LlmRouter，按角色分发 |
| TS | `stardew-agent.ts` | 持有 LlmRouter + role，调用走 router |
| TS | `director.ts` | 通过 LlmRouter 用 "director" 角色调用 |

### 2.3 向后兼容

runtime JSON 缺失或 `--llm-config` 未传时，TS 回退到现有单一 provider 模式（读 `--llm-api-key/--llm-model/--llm-provider/--llm-base-url`），保证旧配置不破。

---

## 3. 配置结构

### 3.1 C# ModConfig 新增字段

#### 多 Provider 模式

```csharp
public bool MultiProviderEnabled { get; set; } = false;

// 导演（narrative director）
public string DirectorPrimaryProvider { get; set; } = "minimax";
public string DirectorPrimaryApiKey { get; set; } = "";
public string DirectorPrimaryModel { get; set; } = "MiniMax-M3";
public string DirectorPrimaryBaseUrl { get; set; } = "https://api.minimax.chat/v1";
public string DirectorFallbackProvider { get; set; } = "deepseek";
public string DirectorFallbackApiKey { get; set; } = "";
public string DirectorFallbackModel { get; set; } = "deepseek-chat";
public string DirectorFallbackBaseUrl { get; set; } = "https://api.deepseek.com/v1";

// 主角 NPC
public string ProtagonistPrimaryProvider { get; set; } = "minimax";
public string ProtagonistPrimaryApiKey { get; set; } = "";
public string ProtagonistPrimaryModel { get; set; } = "MiniMax-M2.7-highspeed";
public string ProtagonistPrimaryBaseUrl { get; set; } = "https://api.minimax.chat/v1";
public string ProtagonistFallbackProvider { get; set; } = "deepseek";
public string ProtagonistFallbackApiKey { get; set; } = "";
public string ProtagonistFallbackModel { get; set; } = "deepseek-chat";
public string ProtagonistFallbackBaseUrl { get; set; } = "https://api.deepseek.com/v1";

// 普通 NPC
public string NpcPrimaryProvider { get; set; } = "deepseek";
public string NpcPrimaryApiKey { get; set; } = "";
public string NpcPrimaryModel { get; set; } = "deepseek-chat";
public string NpcPrimaryBaseUrl { get; set; } = "https://api.deepseek.com/v1";
public string NpcFallbackProvider { get; set; } = "";  // 空=无回退
public string NpcFallbackApiKey { get; set; } = "";
public string NpcFallbackModel { get; set; } = "";
public string NpcFallbackBaseUrl { get; set; } = "";

// 角色标记
public bool EnableProtagonistMapping { get; set; } = true;
public string ProtagonistNpcs { get; set; } = "Abigail, Haley, Sebastian, Sam, Penny, Alex, Maru, Leah, Elliott, Shane, Emily, Harvey";
```

#### 新增功能开关

```csharp
public bool EnableTrade { get; set; } = true;
public bool EnableHire { get; set; } = true;
public bool EnableDirector { get; set; } = true;
public bool EnableProactiveSpeech { get; set; } = true;
public bool EnableInfiniteDialogue { get; set; } = true;
```

#### 新增概率设置

```csharp
public float ProactiveSpeechProbability { get; set; } = 0.3f;
public float ProactiveGiftProbability { get; set; } = 0.1f;
public float ProactiveFollowProbability { get; set; } = 0.2f;
public float ProactiveTradeProbability { get; set; } = 0.15f;
```

#### 冷却改秒（旧字段标记 Obsolete）

```csharp
public int DialogueCooldownSeconds { get; set; } = 3;
public int GiftCooldownSeconds { get; set; } = 30;

[Obsolete("Use DialogueCooldownSeconds instead.")]
public int DialogueCooldownMs { get; set; } = 3000;
[Obsolete("Use GiftCooldownSeconds instead.")]
public int GiftCooldownMs { get; set; } = 30000;
```

#### WebSocket URL 自动绑定

```csharp
[Obsolete("WebSocket URL is auto-bound from ServerPort. Field retained for save compat.")]
public string WebSocketUrl { get; set; } = "ws://127.0.0.1:8765";
```

### 3.2 Runtime JSON 结构

```json
{
  "version": 1,
  "roles": {
    "director": {
      "primary": {
        "provider": "minimax",
        "apiKey": "sk-xxx",
        "model": "MiniMax-M3",
        "baseUrl": "https://api.minimax.chat/v1"
      },
      "fallback": {
        "provider": "deepseek",
        "apiKey": "sk-yyy",
        "model": "deepseek-chat",
        "baseUrl": "https://api.deepseek.com/v1"
      }
    },
    "protagonist": { "primary": {...}, "fallback": {...} },
    "npc": { "primary": {...}, "fallback": null }
  },
  "protagonistNpcs": ["Abigail", "Haley", "Sebastian"],
  "enableProtagonistMapping": true,
  "global": {
    "timeoutMs": 60000,
    "maxRetries": 3,
    "temperature": 0.7,
    "maxTokens": 800
  }
}
```

### 3.3 内置 Provider 预设

| provider 值 | 接口类型 | 默认 baseUrl |
|---|---|---|
| `minimax` | OpenAI 兼容 | `https://api.minimax.chat/v1` |
| `deepseek` | OpenAI 兼容 | `https://api.deepseek.com/v1` |
| `openai` | OpenAI | `https://api.openai.com/v1` |
| `anthropic` | Anthropic | `https://api.anthropic.com/v1` |
| `moonshot` (Kimi) | OpenAI 兼容 | `https://api.moonshot.cn/v1` |
| `openrouter` | OpenAI 兼容 | `https://openrouter.ai/api/v1` |
| `zhipu` (智谱) | OpenAI 兼容 | `https://open.bigmodel.cn/api/paas/v4` |
| `baichuan` (百川) | OpenAI 兼容 | `https://api.baichuan-ai.com/v1` |
| `qwen` (通义千问) | OpenAI 兼容 | `https://dashscope.aliyuncs.com/compatible-mode/v1` |
| `lmstudio` | OpenAI 兼容 | `http://localhost:1234/v1` |
| `custom` | 用户填 baseUrl | 用户填 |

TS 端 `createModel()` 新增 moonshot/zhipu/baichuan/qwen/custom 走 OpenAI 兼容分支。

---

## 4. GMCM UI 布局

单界面，简单在前复杂在后。

### 4.1 基础页

```
── 基础设置 ───
[启用模组] ✓
[语言] Chinese
[LLM Provider] DeepSeek  ← 单 provider 模式（默认）
[API Key] ********
[Model] deepseek-chat
[Base URL] https://api.deepseek.com/v1
[温度] 0.7
[服务器端口] 8765  ← WebSocket 自动绑定，无 URL 字段
[最大 Agent NPC 数] 2
[决策间隔(分钟)] 0.5

── 功能开关 ───
[✓ 战斗辅助] [✓ 农场辅助] [✓ 采矿辅助] [✓ 采集辅助]
[✓ 送礼] [✓ 好感度变化] [✓ 聊天气泡]
[✓ NPC 交易] [✓ NPC 雇佣] [✓ 导演模式]
[✓ NPC 主动发言] [✓ 无限对话] [✓ 聊天栏路由]
[✓ 动态速度]
[✓ 暂停所有对话（紧急）]

── 概率设置 ───
[AI 混入概率] 0.50
[实时生成概率] 0.15
[NPC 主动搭话概率] 0.30
[NPC 主动送礼概率] 0.10
[NPC 主动跟随概率] 0.20
[NPC 主动交易概率] 0.15

── 对话与社交 ───
[对话冷却(秒)] 3
[送礼冷却(秒)] 30
[每日主动发言额度] 2
[主动发言冷却(分钟)] 30
[每日 AI 话题数] 3
[单次好感度变化上限] 80
[长文分句间隔(ms)] 2000
[首次点击播放原版对话] ✓
[非 Agent NPC AI 续聊] ✓
[空闲释放阈值(秒)] 90
[跟随距离(格)] 3
[连续空闲释放次数] 3

[高级设置 →]
```

### 4.2 高级页

```
[← 返回基础设置]

── 多 Provider 模式 ───
[多 Provider 模式] ✗  ← 开启后基础页单 Provider 配置被忽略

── 导演 ──
[主 Provider] MiniMax  [主 API Key] ****  [主 Model] MiniMax-M3
[主 Base URL] https://api.minimax.chat/v1
[备 Provider] DeepSeek  [备 API Key] ****  [备 Model] deepseek-chat
[备 Base URL] https://api.deepseek.com/v1

── 主角 NPC ──
（同上 8 字段）

── 普通 NPC ──
（同上 8 字段，备 provider 可留空）

── 角色标记 ──
[启用角色区分] ✓
[主角 NPC 列表] Abigail, Haley, Sebastian, Sam, ... (逗号分隔)

── 服务器高级 ───
[自动启动服务器] ✓  [控制台窗口] ✓
[启动超时(秒)] 60  [最大重启次数] 3
[可执行文件路径]  [工作目录]

── LLM 性能 ───
[超时(秒)] 60  [最大重试] 3  [Token 预算] 0  [熔断阈值] 5

── 动态速度 ───
[远距阈值] 8  [中距阈值] 3
[近距倍率] 1.0  [中距倍率] 1.5  [远距倍率] 2.0

── 聊天栏路由 ───
[会话超时(秒)] 60  [交互窗口(秒)] 30  [附近阈值(格)] 8
[群体接话上限] 2

── 经济与还价 ───
[✓ NPC 经济]  [✓ 还价]  [最大轮次] 3  [恶意阈值] 0.5
[最大精明浮动] 0.5  [最小精明浮动] 0.05

── 紧急/状态机/情绪/健康 ───
（现有全部字段，保持原样）

── 调试 ───
[调试日志] ✗  [开发者模式] ✗  [自定义系统提示]
[留痕根目录] transcript
```

---

## 5. LlmRouter 实现

### 5.1 核心类

```typescript
// <VALLEYAI_ROOT>\packages\core\src\llm-router.ts
export type LlmRole = "director" | "protagonist" | "npc";

export interface RoleProviderConfig {
  provider: string;
  apiKey: string;
  model: string;
  baseUrl: string;
  timeoutMs?: number;
  maxRetries?: number;
  maxConcurrency?: number;
}

export interface RoleConfig {
  primary: RoleProviderConfig;
  fallback?: RoleProviderConfig | null;
}

export interface LlmRouterConfig {
  version: number;
  roles: Record<LlmRole, RoleConfig>;
  protagonistNpcs: string[];
  enableProtagonistMapping: boolean;
}

export class LlmRouter {
  private readonly providers = new Map<string, VercelAIProvider>();
  private readonly config: LlmRouterConfig;

  constructor(config: LlmRouterConfig) {
    this.config = config;
    for (const [role, roleCfg] of Object.entries(config.roles)) {
      this.providers.set(`${role}:primary`, this.createProvider(roleCfg.primary));
      if (roleCfg.fallback) {
        this.providers.set(`${role}:fallback`, this.createProvider(roleCfg.fallback));
      }
    }
  }

  resolveRole(npcName: string): LlmRole {
    if (!this.config.enableProtagonistMapping) return "npc";
    return this.config.protagonistNpcs.includes(npcName) ? "protagonist" : "npc";
  }

  getProvider(role: LlmRole): VercelAIProvider {
    return this.providers.get(`${role}:primary`)!;
  }

  async chatCompletion(role: LlmRole, messages: LlmMessage[]) {
    return this.withFallback(role, (p) => p.chatCompletion(messages));
  }

  async chatWithTools(role: LlmRole, messages: LlmMessage[], tools?: Tool[]) {
    return this.withFallback(role, (p) => p.chatWithTools(messages, tools));
  }

  async chatCompletionJson<T>(role: LlmRole, messages: LlmMessage[]) {
    return this.withFallback(role, (p) => p.chatCompletionJson<T>(messages));
  }

  private async withFallback<T>(
    role: LlmRole,
    fn: (p: VercelAIProvider) => Promise<T>
  ): Promise<T> {
    const primary = this.providers.get(`${role}:primary`)!;
    try {
      return await fn(primary);
    } catch (err) {
      if (!(err instanceof LLMBillingError)) throw err;
      const fallback = this.providers.get(`${role}:fallback`);
      if (!fallback) throw err;
      console.warn(`[llm-router] ${role} primary billing error, falling back`);
      return await fn(fallback);
    }
  }

  private createProvider(cfg: RoleProviderConfig): VercelAIProvider {
    return new VercelAIProvider({
      provider: cfg.provider as LLMConfig["provider"],
      apiKey: cfg.apiKey,
      model: cfg.model,
      baseUrl: cfg.baseUrl,
      ...(cfg.timeoutMs ? { timeout: cfg.timeoutMs } : {}),
      ...(cfg.maxRetries ? { maxRetries: cfg.maxRetries } : {}),
      ...(cfg.maxConcurrency ? { maxConcurrency: cfg.maxConcurrency } : {}),
    });
  }
}
```

### 5.2 回退流程时序

```
NPC 对话请求 (npcName="Abigail")
    │
    ▼
Registry.getOrCreate("Abigail")
    │ resolveRole("Abigail") → "protagonist"
    │ agent.role = "protagonist"
    ▼
agent.callLlm(messages, tools)
    │ router.chatWithTools("protagonist", messages, tools)
    ▼
LlmRouter.withFallback("protagonist", fn)
    │ primary = providers.get("protagonist:primary")  // MiniMax M2.7
    │ try: fn(primary)
    ▼
VercelAIProvider.chatWithTools
    │ generateText(...) → HTTP 402 (MiniMax 欠费)
    ▼
withRetry 捕获 → 抛 LLMBillingError
    │
    ▼
LlmRouter.withFallback catch
    │ err instanceof LLMBillingError? ✓
    │ fallback = providers.get("protagonist:fallback")  // DeepSeek
    │ try: fn(fallback)
    ▼
VercelAIProvider.chatWithTools (DeepSeek)
    │ generateText(...) → 200 OK
    ▼
返回结果（用户无感知，仅日志记录一次回退）
```

### 5.3 错误处理矩阵

| 错误类型 | HTTP 码 | 主 provider 行为 | fallback 行为 | 最终抛出 |
|---|---|---|---|---|
| `LLMBillingError` | 402/429 | 不重试，抛出 | 切到 fallback 重试一次 | fallback 也 billing → 抛 `LLMBillingError` |
| `LLMUnavailableError` | 5xx | max-retries 重试耗尽后抛出 | **不回退**，直接抛出 | `LLMUnavailableError` |
| 超时 | - | `AbortSignal.timeout` 触发，重试耗尽抛 `LLMUnavailableError` | **不回退** | `LLMUnavailableError` |
| 网络断连 | - | 同上 | **不回退** | `LLMUnavailableError` |
| JSON 解析失败 | - | `chatCompletionJson` 抛 `Error` | **不回退** | `Error` |

---

## 6. C# 端改动

### 6.1 LlmConfigWriter.cs（新）

```csharp
public static class LlmConfigWriter
{
    public static string GetRuntimeConfigPath(string modDir)
        => Path.Combine(modDir, "llm-config.runtime.json");

    public static string? WriteRuntimeConfig(ModConfig config, string modDir, IMonitor? monitor = null)
    {
        // 序列化 9 组字段 + 角色标记 + 全局参数到 runtime JSON
        // 失败返回 null，调用方降级到单 provider 模式
    }

    public static List<string> ParseProtagonistList(string? raw)
    {
        // 逗号分隔解析 + trim + OrdinalIgnoreCase 去重
    }
}
```

### 6.2 ServerProcessManager.cs 改动

```csharp
if (_config.MultiProviderEnabled)
{
    llmConfigPath = LlmConfigWriter.WriteRuntimeConfig(_config, _helper.DirectoryPath, _monitor);
    if (llmConfigPath == null) _config.MultiProviderEnabled = false;  // 降级
}

if (!_config.MultiProviderEnabled)
{
    // 单 provider 模式：走现有 --llm-api-key/--llm-model/--llm-provider/--llm-base-url
}

// 构造 serverArgs：
// 多 provider: --port X --agents-dir Y --llm-config <path>
// 单 provider: --port X --agents-dir Y --llm-api-key K --llm-model M --llm-provider P --llm-base-url U
```

### 6.3 WebSocketClient 改动

```csharp
// WebSocket URL 从 ServerPort 自动绑定
_wsUrl = $"ws://127.0.0.1:{config.ServerPort}";
```

### 6.4 GMCMIntegration.cs 改动

- 基础页：新增功能开关（5 个 bool）+ 概率设置（4 个 float）+ 冷却改秒 + 删除 WebSocket URL
- 高级页：新增多 Provider 区（1 开关 + 9 组 × 4 字段 = 36 字段 + 角色标记 2 字段）
- Provider 槽位 UI 提取公共方法 `AddProviderSlotUI`，用反射 `GetField/SetField` 减少 24 个闭包
- `CopyConfig` 用反射批量复制新增字段

### 6.5 功能开关消费点

| 新字段 | C# 消费点 | TS 消费点 |
|---|---|---|
| `EnableTrade` | `GiftTradeMenuLogic.cs` 入口判断 | - |
| `EnableHire` | `ContractService.cs` 入口判断 | - |
| `EnableDirector` | 不传 `--enable-director` 参数 | `cli.ts` 读取，`server.ts` 不构造 `Director` |
| `EnableProactiveSpeech` | `ProactiveSpeechQuota.cs` 额度归零 | `morning-shout-router.ts` 不触发 |
| `EnableInfiniteDialogue` | `NPCDialoguePatch.cs` 非 Agent NPC 不开 AI 输入框 | - |
| `ProactiveSpeechProbability` | `ProactiveSpeechTrigger.cs` 概率判定 | - |
| `ProactiveGiftProbability` | `SocialCommands.cs` 主动送礼判定 | - |
| `ProactiveFollowProbability` | `RuleBasedDecisionEngine.cs` FOLLOW 决策 | - |
| `ProactiveTradeProbability` | `ContractService.cs` 求购触发 | - |
| `EnableProtagonistMapping` | 写入 runtime JSON | `LlmRouter.resolveRole()` 读取 |

---

## 7. 测试策略

### 7.1 L1 契约测试（TS）

- `resolveRole`: protagonist 列表内/外、mapping 开关、大小写不敏感
- `withFallback`: primary 成功不调 fallback、billing 错误调 fallback、非 billing 错误不调 fallback、fallback 也 billing 抛错、无 fallback + billing 抛错
- `loadRouterConfig`: version 校验、role 完整性、apiKey 非空、fallback 可选

### 7.2 L2 组件测试（C#）

- `LlmConfigWriter.WriteRuntimeConfig`: 生成合法 JSON、fallback 空时为 null
- `ParseProtagonistList`: 解析、去重、trim、空字符串
- `ModConfigMigration`: ms→seconds 迁移、概率钳制、冷却钳制

### 7.3 L3 故障注入测试

- 主 provider 模拟 402 → 回退成功
- 主+备都 402 → 失败明确
- 主 provider 超时/500 → 不回退

### 7.4 L4 不变量测试

- 回退次数 ≤ 1
- 非 billing 错误绝不触发回退
- 同一请求最终只成功调用一个 provider
- provider 实例数 = 主数 + 备数，运行时不新增

### 7.5 L5 体验打分（手动）

| 场景 | 验收标准 |
|---|---|
| 单 provider 模式 | 行为与现有完全一致 |
| 多 provider 正常 | 导演用 M3，Abigail 用 M2.7，Pierre 用 DeepSeek |
| MiniMax 欠费回退 | 自动切 DeepSeek，对话不中断 |
| DeepSeek 也欠费 | 失败明确，不卡死 |
| 角色区分关闭 | 所有 NPC 走 npc 角色 |
| 5xx 不回退 | 重试 3 次失败，不切 DeepSeek |
| 功能开关 | 各开关关闭后功能完全停止 |
| 冷却秒单位 | UI 显示秒，行为正确 |
| WebSocket 自动绑定 | 改端口→WS 自动跟随 |

---

## 8. 验收标准

- TS: `bun test packages/stardew` + `tsc --noEmit` + `check:protocol` 全绿
- C#: 编译 0 警告（Obsolete 字段 `#pragma warning disable CS0618` 隔离）
- L1-L4 自动化测试全绿
- L5 体验打分手动游戏内验证（录像/截图留证）
- 向后兼容：旧 config.json 不破

---

## 9. 风险与缓解

| 风险 | 概率 | 影响 | 缓解 |
|---|---|---|---|
| GMCM 反射 `GetField/SetField` 在非 string 字段失败 | 中 | UI 字段空白 | 非字符串字段手写闭包，反射只处理 string |
| runtime JSON 写入失败 | 低 | 多 provider 不可用 | 降级单 provider + 日志 |
| TS provider 构造失败 | 低 | 服务器启动失败 | 启动时校验，失败即退出 |
| token 预算跨 provider 不共享 | 中 | 单 provider 限流不影响 fallback | 设计决策：各 provider 独立预算 |
| role 在 `getOrCreate` 时绑定，NPC 列表变更后不动态更新 | 中 | role 不切换 | 配置变更需重启服务器（可接受） |
| `CopyConfig` 反射遗漏嵌套对象 | 中 | GMCM reset 丢失 Economy/Haggle | 嵌套对象手动复制 |

---

## 10. 实施顺序

分支 `feature/multi-llm-provider`，按顺序实施，每步可独立验证：

1. **TS 端 `LlmRouter` + 配置类型**（纯 TS，可单测）
2. **TS 端 `StardewAgent`/`Registry`/`server`/`cli`/`director` 改造**
3. **C# 端 `ModConfig` 扩展 + 迁移**
4. **C# 端 `LlmConfigWriter` + `ServerProcessManager`**
5. **C# 端 `GMCMIntegration` UI 扩展**
6. **C# 端功能开关消费点**
7. **端到端集成测试**

---

## 11. 性能影响

| 维度 | 影响 |
|---|---|
| 启动时间 | +50ms（6 个 provider 实例） |
| 内存 | +~2MB（6 个 provider 实例） |
| 单次对话延迟 | 0（正常路径零额外开销） |
| 回退时延迟 | +1 次 LLM 调用（~2-5s） |
| 配置文件 IO | 启动时写一次（~2KB） |
