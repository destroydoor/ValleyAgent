# ValleyTalk API 文档

本文档面向模组开发者，说明如何扩展 ValleyTalk 的 LLM 提供商系统、配置系统和行为控制器。

## LLM Provider 接口

### ILLMProvider

所有 LLM 提供商必须实现此接口：

```csharp
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ValleyTalk.LLM;

public interface ILLMProvider
{
    /// <summary>
    /// 提供商显示名称（如 "LM Studio"、"Kimi"）
    /// </summary>
    string ProviderName { get; }

    /// <summary>
    /// 发送聊天补全请求，返回完整响应
    /// </summary>
    Task<LLMResponse> ChatCompletionAsync(
        List<LLMMessage> messages,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 发送流式聊天补全请求，实时返回内容片段
    /// </summary>
    IAsyncEnumerable<string> StreamCompletionAsync(
        List<LLMMessage> messages,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 验证与 LLM 提供商的连接是否正常
    /// </summary>
    Task<bool> ValidateConnectionAsync(CancellationToken cancellationToken = default);
}
```

### 数据模型

#### LLMMessage

```csharp
public class LLMMessage
{
    public string Role { get; set; } = "user";  // "system" | "user" | "assistant"
    public string Content { get; set; } = "";
}
```

#### LLMResponse

```csharp
public class LLMResponse
{
    public string Content { get; set; } = "";
    public string Model { get; set; } = "";
    public string FinishReason { get; set; } = "";
    public LLMUsage? Usage { get; set; }
    public bool IsSuccess { get; set; } = true;
    public string ErrorMessage { get; set; } = "";
    public int StatusCode { get; set; }
}
```

### 实现自定义提供商

#### 步骤 1：创建提供商类

```csharp
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ValleyTalk.Config;

namespace ValleyTalk.LLM;

public class MyProvider : ILLMProvider
{
    private readonly HttpClient _httpClient;
    private readonly ModConfig _config;

    public string ProviderName => "MyProvider";

    public MyProvider(ModConfig config)
    {
        _config = config;
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(config.ServerAddress),
            Timeout = TimeSpan.FromSeconds(config.LLMTimeoutSeconds)
        };
        _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {config.ApiKey}");
    }

    public async Task<LLMResponse> ChatCompletionAsync(
        List<LLMMessage> messages,
        CancellationToken cancellationToken = default)
    {
        var requestBody = new
        {
            model = _config.Model,
            messages = messages,
            temperature = 0.7,
            max_tokens = 500
        };

        var json = JsonSerializer.Serialize(requestBody);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await _httpClient.PostAsync("/v1/chat/completions", content, cancellationToken);
        response.EnsureSuccessStatusCode();

        var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
        // 解析响应...

        return new LLMResponse
        {
            Content = "解析后的内容",
            TokensUsed = 100,
            LatencyMs = 1234,
            Model = _config.Model
        };
    }

    public async IAsyncEnumerable<string> StreamCompletionAsync(
        List<LLMMessage> messages,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // 实现 SSE 流式读取
        yield return "流式内容片段";
    }

    public async Task<bool> ValidateConnectionAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var testMessages = new List<LLMMessage>
            {
                new() { Role = "user", Content = "Hi" }
            };
            var response = await ChatCompletionAsync(testMessages, cancellationToken);
            return response.IsSuccess;
        }
        catch
        {
            return false;
        }
    }
}
```

#### 步骤 2：注册到 AgentService

在 `AgentService.cs` 的构造函数中：

```csharp
public AgentService(ModConfig config, ...)
{
    Config = config;
    
    // 注册自定义提供商
    LLMProvider = config.Provider switch
    {
        Provider.LMStudio => new LMStudioProvider(config),
        Provider.Kimi => new KimiProvider(config),
        Provider.DeepSeek => new DeepSeekProvider(config),
        Provider.OpenRouter => new OpenRouterProvider(config),
        Provider.MyProvider => new MyProvider(config),  // 添加此行
        _ => throw new NotSupportedException($"Provider {config.Provider} not supported")
    };
    
    // ...
}
```

#### 步骤 3：更新配置枚举

在 `ModConfig.cs` 中：

```csharp
public enum Provider
{
    LMStudio,
    Kimi,
    DeepSeek,
    OpenRouter,
    MyProvider  // 添加
}
```

#### 步骤 4：添加 GMCM 选项（可选）

在 `ModEntry.cs` 的 `SetupGenericModConfigMenu` 中：

```csharp
gmcmApi.AddTextOption(
    mod: ModManifest,
    name: () => "LLM Provider",
    getValue: () => _config.Provider.ToString(),
    setValue: value => _config.Provider = Enum.Parse<Provider>(value),
    allowedValues: new[] { "LMStudio", "Kimi", "DeepSeek", "OpenRouter", "MyProvider" }
);
```

## 现有提供商参考

### LMStudioProvider（本地）

```csharp
public class LMStudioProvider : OpenAICompatibleProvider
{
    public LMStudioProvider(ModConfig config) 
        : base(config, "LM Studio") { }
    
    // 继承 OpenAICompatibleProvider 的所有逻辑
    // 端点：http://localhost:1234/v1/chat/completions
}
```

### KimiProvider（Moonshot AI）

**注意**：Kimi 使用独立的 API 格式，不兼容 OpenAI。

- 端点：`https://api.moonshot.cn/v1/chat/completions`
- 认证：`Authorization: Bearer {ApiKey}`
- 格式：类似 OpenAI，但部分字段不同

### DeepSeekProvider / OpenRouterProvider

两者均继承 `OpenAICompatibleProvider`：

- DeepSeek 端点：`https://api.deepseek.com/v1/chat/completions`
- OpenRouter 端点：`https://openrouter.ai/api/v1/chat/completions`

### OpenAICompatibleProvider（基类）

```csharp
public abstract class OpenAICompatibleProvider : ILLMProvider
{
    protected readonly HttpClient HttpClient;
    protected readonly ModConfig Config;
    protected readonly string BaseEndpoint;
    protected readonly string DefaultModel;

    protected OpenAICompatibleProvider(
        HttpClient httpClient,
        LLMProviderConfig config,
        string baseEndpoint,
        string defaultModel)
    {
        HttpClient = httpClient;
        Config = config;
        BaseEndpoint = config.Endpoint ?? baseEndpoint;
        DefaultModel = string.IsNullOrEmpty(config.Model) ? defaultModel : config.Model;
        HttpClient.Timeout = TimeSpan.FromSeconds(config.TimeoutSeconds);
    }

    public abstract string ProviderName { get; }

    public virtual async Task<LLMResponse> ChatCompletionAsync(
        List<LLMMessage> messages,
        CancellationToken cancellationToken = default)
    {
        // 标准 OpenAI API 请求格式
        var request = new ChatCompletionRequest
        {
            Model = Config.Model,
            Messages = messages,
            Temperature = 0.7,
            MaxTokens = 500
        };

        var response = await SendRequestAsync(request, cancellationToken);
        return ParseResponse(response);
    }

    public virtual async IAsyncEnumerable<string> StreamCompletionAsync(
        List<LLMMessage> messages,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var request = new ChatCompletionRequest
        {
            Model = Config.Model,
            Messages = messages,
            Temperature = 0.7,
            MaxTokens = 500,
            Stream = true
        };

        using var response = await SendStreamRequestAsync(request, cancellationToken);
        await foreach (var delta in ParseStreamAsync(response, cancellationToken))
        {
            yield return delta;
        }
    }

    public virtual async Task<bool> ValidateConnectionAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var testMessages = new List<LLMMessage>
            {
                new() { Role = "user", Content = "Hello" }
            };
            var response = await ChatCompletionAsync(testMessages, cancellationToken);
            return response.IsSuccess && !string.IsNullOrEmpty(response.Content);
        }
        catch
        {
            return false;
        }
    }

    // 抽象/虚方法，子类可覆盖
    protected abstract Task<HttpResponseMessage> SendRequestAsync(...);
    protected abstract IAsyncEnumerable<string> ParseStreamAsync(...);
}
```

## 配置系统 API

### ModConfig 类

```csharp
public class ModConfig
{
    public Provider Provider { get; set; } = Provider.LMStudio;
    public string ApiKey { get; set; } = "";
    public string ServerAddress { get; set; } = "http://localhost:1234";
    public string Model { get; set; } = "local-model";
    public int MaxAgentNpcs { get; set; } = 1;
    public bool EnableFriendshipChanges { get; set; } = true;
    public bool EnableGifts { get; set; } = true;
    public int TokenBudget { get; set; } = 0;
    public bool DebugMode { get; set; } = false;
    public int LLMTimeoutSeconds { get; set; } = 30;
    public int MaxRetries { get; set; } = 3;
    public int DecisionIntervalMinutes { get; set; } = 5;
    public int CircuitBreakerThreshold { get; set; } = 3;
    public bool EnableCombatAssist { get; set; } = true;
    public bool EnableFarmingAssist { get; set; } = true;
    public bool EnableMiningAssist { get; set; } = true;
    public bool EnableForagingAssist { get; set; } = true;
    public int FollowDistance { get; set; } = 3;
}
```

### GMCM 集成

ValleyTalk 使用 [Generic Mod Config Menu](https://www.nexusmods.com/stardewvalley/mods/5098) 提供游戏内配置界面。

```csharp
private void SetupGenericModConfigMenu()
{
    var gmcmApi = Helper.ModRegistry.GetApi<IGenericModConfigMenuApi>("spacechase0.GenericModConfigMenu");
    if (gmcmApi == null) return;

    gmcmApi.Register(ModManifest, reset: () => _config = new ModConfig(),
        save: () => Helper.WriteConfig(_config));

    gmcmApi.AddTextOption(mod: ModManifest,
        name: () => "Provider",
        getValue: () => _config.Provider.ToString(),
        setValue: value => _config.Provider = Enum.Parse<Provider>(value),
        allowedValues: new[] { "LMStudio", "Kimi", "DeepSeek", "OpenRouter" });

    gmcmApi.AddTextOption(mod: ModManifest,
        name: () => "API Key",
        getValue: () => _config.ApiKey,
        setValue: value => _config.ApiKey = value);

    gmcmApi.AddNumberOption(mod: ModManifest,
        name: () => "Max Agents",
        getValue: () => _config.MaxAgentNpcs,
        setValue: value => _config.MaxAgentNpcs = Math.Clamp(value, 1, 5),
        min: 1, max: 5);

    // ... 更多选项
}
```

## 行为控制器接口

### IAgentController

```csharp
public interface IAgentController
{
    /// <summary>
    /// 控制器名称（用于日志和调试）
    /// </summary>
    string ControllerName { get; }

    /// <summary>
    /// 当前是否处于激活状态
    /// </summary>
    bool IsActive { get; }

    /// <summary>
    /// 是否可以处理给定的 Agent 状态
    /// </summary>
    bool CanHandle(AgentState state);

    /// <summary>
    /// 启动控制器，开始执行行为
    /// </summary>
    void Start();

    /// <summary>
    /// 停止控制器，清理状态
    /// </summary>
    void Stop();

    /// <summary>
    /// 每 tick 更新（由 UpdateTicked 驱动）
    /// </summary>
    void Update(int ticks);

    /// <summary>
    /// 行为开始时触发
    /// </summary>
    event EventHandler<string>? OnActionStarted;

    /// <summary>
    /// 行为完成时触发
    /// </summary>
    event EventHandler<string>? OnActionCompleted;

    /// <summary>
    /// 行为失败时触发
    /// </summary>
    event EventHandler<string>? OnActionFailed;
}
```

### 实现自定义控制器

```csharp
public class DanceController : IAgentController
{
    private int _danceTimer;

    public string ControllerName => "Dance";
    public bool IsActive { get; private set; }

    public bool CanHandle(AgentState state) => state == AgentState.IDLE;

    public void Start()
    {
        IsActive = true;
        _danceTimer = 0;
        OnActionStarted?.Invoke(this, "Started dancing");
    }

    public void Update(int ticks)
    {
        if (!IsActive) return;
        _danceTimer++;
        
        // 跳舞 300 ticks（约 5 秒）后结束
        if (_danceTimer > 300)
        {
            Stop();
            OnActionCompleted?.Invoke(this, "Dance finished");
        }
    }

    public void Stop()
    {
        IsActive = false;
        _danceTimer = 0;
    }

    public event EventHandler<string>? OnActionStarted;
    public event EventHandler<string>? OnActionCompleted;
    public event EventHandler<string>? OnActionFailed;
}
```

### 注册到 AgentService

```csharp
public AgentService(ModConfig config, ...)
{
    // ...
    
    // 添加自定义控制器工厂
    _danceControllerFactory = () => new DanceController();
}
```

## 事件系统

### 订阅 Agent 事件

```csharp
// Agent 被分配
agentService.AllocationManager.OnAgentAllocated += (sender, args) =>
{
    Console.WriteLine($"NPC {args.NpcName} 成为 Agent！");
};

// Agent 被取消分配
agentService.AllocationManager.OnAgentDeallocated += (sender, args) =>
{
    Console.WriteLine($"NPC {args.NpcName} 不再是 Agent");
};
```

### 自定义事件参数

```csharp
public class AgentAllocationEventArgs : EventArgs
{
    public string NpcName { get; set; } = "";
    public double PriorityScore { get; set; }
    public bool IsManualOverride { get; set; }
    public DateTime Timestamp { get; set; }
}
```

## 国际化 API

### ITranslationProvider

```csharp
public interface ITranslationProvider
{
    string Get(string key);
    string Get(string key, object tokens);
    bool HasKey(string key);
}
```

### 添加新语言

1. 在 `i18n/` 目录创建 `{locale}.json`
2. 复制 `default.json` 的所有键
3. 翻译值

```json
// i18n/fr.json
{
  "dialogue.greeting": "Bonjour {0}!",
  "friendship.increased": "L'amitié avec {0} a augmenté de {1}"
}
```

### 使用翻译

```csharp
var translation = Helper.Translation.Get("dialogue.greeting", npcName);
```

---

*文档版本：v2.0.0 | 面向模组开发者*
