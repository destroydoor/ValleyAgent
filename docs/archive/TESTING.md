# ValleyTalk 测试文档

本文档说明 ValleyTalk 的测试策略、测试结构以及如何运行和编写测试。

## 测试策略

ValleyTalk 采用 **单元测试为主、集成测试为辅** 的策略：

- **单元测试**：263 个，覆盖所有核心业务逻辑（使用 Moq 模拟依赖）
- **集成测试**：6 个，验证 LLM 提供商与真实 API 的连通性（可选运行）
- **目标**：核心模块测试覆盖率 > 90%

## 测试项目结构

```
src/ValleyTalk.Tests/
├── AgentAllocationManagerTests.cs      # Agent 分配管理（23 个测试）
├── AIDecisionEngineTests.cs            # AI 决策引擎（18 个测试）
├── ActionValidatorTests.cs             # 行为校验器（15 个测试）
├── CircuitBreakerTests.cs              # 断路器（12 个测试）
├── FriendshipSystemTests.cs            # 友谊系统（20 个测试）
├── GiftSystemTests.cs                  # 礼物系统（16 个测试）
├── StateMachineTests.cs                # 状态机（14 个测试）
├── PerformanceTests.cs                 # 性能监控（8 个测试）
├── SaveDataManagerTests.cs             # 存档管理（10 个测试）
├── LMStudioIntegrationTests.cs         # LLM 集成测试（6 个测试）
└── SMAPIStubs.cs                       # SMAPI 类型存根
```

## 运行测试

### 全部测试

```bash
cd src/ValleyTalk.Tests
dotnet test
```

### 特定测试类

```bash
dotnet test --filter "FullyQualifiedName~AgentAllocationManagerTests"
```

### 特定测试方法

```bash
dotnet test --filter "AllocateAgent_ExceedsMax_RemovesLowestPriority"
```

### 详细输出

```bash
dotnet test --logger "console;verbosity=detailed"
```

### 使用 .NET 9 SDK 运行（.NET 6 EOL 警告）

```powershell
$env:DOTNET_ROLL_FORWARD = "LatestMajor"
dotnet test
```

## 测试环境要求

- **.NET 6 SDK**（目标框架）或 **.NET 8/9 SDK**（运行时前滚）
- **无需 SMAPI**：所有测试使用存根和模拟对象
- **无需 Stardew Valley**：纯业务逻辑测试
- **无需 LLM 后端**：LLM 调用使用 Moq 模拟（集成测试除外）

## 核心测试用例说明

### AgentAllocationManagerTests（23 个测试）

**测试目标**：Agent 分配、优先级计算、手动覆盖、优雅降级

| 测试方法 | 说明 |
|----------|------|
| `AllocateAgent_SingleAgent_Success` | 单个 Agent 分配成功 |
| `AllocateAgent_ExceedsMax_RemovesLowestPriority` | 超过上限时移除最低优先级 |
| `AllocateAgent_ManualOverride_IgnoresPriority` | 手动覆盖忽略优先级 |
| `CalculatePriority_ConversationHeavy_HighScore` | 对话频繁 = 高优先级 |
| `DeallocateAgent_EventFired` | 取消分配时触发事件 |
| `ThreadSafety_MultipleThreads_NoRaceCondition` | 多线程安全 |

**关键断言**：
```csharp
Assert.Equal(2, manager.CurrentAgentCount);
Assert.Equal("Abigail", manager.AllocatedAgentNames[0]);
Assert.True(eventFired);
```

### AIDecisionEngineTests（18 个测试）

**测试目标**：决策生成、缓存、并发控制、异常处理

| 测试方法 | 说明 |
|----------|------|
| `DecideAsync_ValidContext_ReturnsDecision` | 正常决策流程 |
| `DecideAsync_CacheHit_ReturnsCachedDecision` | 缓存命中 |
| `DecideAsync_ConcurrentCalls_RespectsSemaphore` | 并发限制 |
| `DecideAsync_LLMThrows_FallsBackToIdle` | LLM 异常回退 |
| `BuildPrompt_IncludesRAGKnowledge` | 提示包含 RAG 知识 |
| `ParseDecision_ValidJSON_ReturnsAction` | JSON 解析 |

**模拟 LLM**：
```csharp
var mockLLM = new Mock<ILLMProvider>();
mockLLM.Setup(x => x.ChatCompletionAsync(It.IsAny<List<LLMMessage>>(), It.IsAny<CancellationToken>()))
    .ReturnsAsync(new LLMResponse { Content = "{\"action\":\"FOLLOW\",\"reason\":\"test\"}" });
```

### CircuitBreakerTests（12 个测试）

**测试目标**：状态转换、阈值触发、半开恢复、超时处理

| 测试方法 | 说明 |
|----------|------|
| `InitialState_IsClosed` | 初始状态关闭 |
| `Execute_FailureBelowThreshold_StaysClosed` | 失败未达阈值保持关闭 |
| `Execute_FailureAtThreshold_Opens` | 达到阈值后打开 |
| `Execute_Open_ThrowsException` | 打开状态拒绝调用 |
| `HalfOpen_AfterTimeout_AllowsOneCall` | 超时后半开允许一次 |
| `HalfOpen_Success_Closes` | 半开成功关闭 |

**状态断言**：
```csharp
Assert.Equal(CircuitState.Closed, breaker.State);
Assert.Equal(CircuitState.Open, breaker.State);
Assert.Equal(CircuitState.HalfOpen, breaker.State);
```

### FriendshipSystemTests（20 个测试）

**测试目标**：友谊计算、防刷机制、生日加成、存档序列化

| 测试方法 | 说明 |
|----------|------|
| `EvaluateInteraction_Quality10_MaxIncrease` | 质量 10 = 最大增量 |
| `EvaluateInteraction_SameAction_DecayingBonus` | 重复行为递减 |
| `EvaluateInteraction_Birthday_TripleBonus` | 生日 3 倍 |
| `GetFriendshipLevel_NewNpc_ReturnsZero` | 新 NPC 友谊为 0 |
| `Serialize_RoundTrip_PreservesData` | 序列化往返 |

### GiftSystemTests（16 个测试）

**测试目标**：礼物校验、双向赠送、冷却、白名单

| 测试方法 | 说明 |
|----------|------|
| `CanGiveGift_WithinDistance_ReturnsTrue` | 距离内允许 |
| `CanGiveGift_TooFar_ReturnsFalse` | 太远拒绝 |
| `CanGiveGift_SameDay_ReturnsFalse` | 同天冷却 |
| `CanGiveGift_NotInWhitelist_ReturnsFalse` | 白名单外拒绝 |
| `ReceiveGift_FromNpc_AddsToInventory` | NPC 赠礼添加到背包 |

### StateMachineTests（14 个测试）

**测试目标**：状态转换、入口/出口逻辑、更新循环

| 测试方法 | 说明 |
|----------|------|
| `Transition_IdleToFollow_Allowed` | IDLE→FOLLOW 允许 |
| `Transition_TalkToAny_Blocked` | TALK 状态不可打断 |
| `Entry_SetsInitialState` | 入口初始化 |
| `Update_CallsCurrentStateUpdate` | 更新调用当前状态 |

### PerformanceTests（8 个测试）

**测试目标**：令牌预算、性能计数器、缓存管理

| 测试方法 | 说明 |
|----------|------|
| `TokenBudget_TracksConsumption` | 令牌消耗追踪 |
| `TokenBudget_Exhausted_DisablesNonEssential` | 耗尽后禁用非必要调用 |
| `CacheManager_Eviction_LRU` | LRU 淘汰策略 |
| `PerformanceMonitor_SamplesLatency` | 延迟采样 |

### SaveDataManagerTests（10 个测试）

**测试目标**：序列化、版本迁移、状态清理、异常恢复

| 测试方法 | 说明 |
|----------|------|
| `Save_V2Format_SerializesCorrectly` | v2 格式序列化 |
| `Load_V1Format_MigratesToV2` | v1 迁移到 v2 |
| `Load_ThinkingState_MappedToIdle` | THINKING 映射为 IDLE |
| `Load_CorruptedData_FallsBackToDefault` | 损坏数据回退 |

## 编写新测试

### 测试类模板

```csharp
using System;
using System.Threading.Tasks;
using Moq;
using Xunit;
using ValleyTalk.Agents;
using ValleyTalk.Config;
using ValleyTalk.LLM;
using ValleyTalk.StateMachine;

namespace ValleyTalk.Tests;

public class MyFeatureTests
{
    private readonly Mock<ILLMProvider> _mockLLM;
    private readonly ModConfig _config;

    public MyFeatureTests()
    {
        _mockLLM = new Mock<ILLMProvider>();
        _config = new ModConfig
        {
            Provider = Provider.LMStudio,
            MaxAgentNpcs = 2,
            DebugMode = true
        };
    }

    [Fact]
    public void MyFeature_ValidInput_ReturnsExpectedResult()
    {
        // Arrange
        var service = new MyFeature(_config, _mockLLM.Object);
        
        // Act
        var result = service.DoSomething("input");
        
        // Assert
        Assert.Equal("expected", result);
    }

    [Theory]
    [InlineData("input1", "output1")]
    [InlineData("input2", "output2")]
    public void MyFeature_MultipleInputs_ReturnsExpectedResults(string input, string expected)
    {
        var service = new MyFeature(_config, _mockLLM.Object);
        var result = service.DoSomething(input);
        Assert.Equal(expected, result);
    }

    [Fact]
    public async Task MyFeature_AsyncOperation_CompletesSuccessfully()
    {
        _mockLLM.Setup(x => x.ChatCompletionAsync(It.IsAny<List<LLMMessage>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LLMResponse { Content = "test" });

        var service = new MyFeature(_config, _mockLLM.Object);
        var result = await service.DoSomethingAsync();
        
        Assert.NotNull(result);
    }

    [Fact]
    public void MyFeature_InvalidInput_ThrowsArgumentException()
    {
        var service = new MyFeature(_config, _mockLLM.Object);
        Assert.Throws<ArgumentException>(() => service.DoSomething(null!));
    }
}
```

### 测试命名规范

```
{MethodName}_{Scenario}_{ExpectedResult}

示例：
- AllocateAgent_SingleAgent_Success
- DecideAsync_CacheHit_ReturnsCachedDecision
- EvaluateInteraction_SameAction_DecayingBonus
```

### Moq 常用模式

```csharp
// 模拟返回值
mock.Setup(x => x.Method()).Returns(value);
mock.Setup(x => x.MethodAsync()).ReturnsAsync(value);

// 模拟异常
mock.Setup(x => x.Method()).Throws(new InvalidOperationException());

// 验证调用次数
mock.Verify(x => x.Method(), Times.Once);
mock.Verify(x => x.Method(), Times.Never);

// 参数匹配
mock.Setup(x => x.Method(It.IsAny<string>())).Returns(true);
mock.Setup(x => x.Method(It.Is<string>(s => s.Length > 5))).Returns(true);

// 异步流
mock.Setup(x => x.StreamAsync()).Returns(GetStream());
async IAsyncEnumerable<string> GetStream()
{
    yield return "chunk1";
    yield return "chunk2";
}
```

## 集成测试

### LMStudioIntegrationTests

**目的**：验证与真实 LM Studio 实例的 HTTP 通信

**运行条件**：
- LM Studio 在 `localhost:1234` 运行
- 已加载模型

**运行命令**：
```bash
cd src/ValleyTalk.ApiTest
dotnet run
```

**输出示例**：
```
========================================
  ValleyTalk LLM Provider API Test
========================================
Provider: LM Studio
Endpoint: http://localhost:1234
Model: local-model

Test 1 - ValidateConnection... PASS
Test 2 - ChatCompletion... PASS
Test 3 - StreamCompletion... PASS
```

**注意**：集成测试不计入 CI，仅用于本地验证。

## 持续集成建议

### GitHub Actions 示例

```yaml
name: Tests

on: [push, pull_request]

jobs:
  test:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      
      - name: Setup .NET
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '6.0.x'
      
      - name: Restore
        run: dotnet restore src/ValleyTalk.Tests/
      
      - name: Build
        run: dotnet build src/ValleyTalk.Tests/ --no-restore
      
      - name: Test
        run: dotnet test src/ValleyTalk.Tests/ --no-build --verbosity normal
```

## 调试测试

### Visual Studio

1. 在测试方法上右键 → "调试测试"
2. 使用断点、监视窗口、即时窗口

### VS Code

```json
// .vscode/launch.json
{
    "version": "0.2.0",
    "configurations": [
        {
            "name": "Debug Tests",
            "type": "coreclr",
            "request": "launch",
            "program": "${workspaceFolder}/src/ValleyTalk.Tests/bin/Debug/net6.0/ValleyTalk.Tests.dll",
            "args": ["--filter", "FullyQualifiedName~AgentAllocationManagerTests"],
            "cwd": "${workspaceFolder}",
            "stopAtEntry": false
        }
    ]
}
```

### 命令行

```bash
dotnet test --filter "FullyQualifiedName~MyTest" -v n --blame-hang-timeout 30s
```

## 常见问题

### Q: 测试失败 "Could not load file or assembly 'StardewModdingAPI'"

**原因**：主项目引用了 SMAPI，但测试项目没有。

**解决**：测试项目使用 `SMAPIStubs.cs` 中的存根类型，或添加 `Pathoschild.Stardew.ModBuildConfig` 引用。

### Q: 异步测试超时

**解决**：使用 `CancellationToken` 并设置合理超时：

```csharp
var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
var result = await service.DoSomethingAsync(cts.Token);
```

### Q: 模拟 LLM 流式响应

**解决**：

```csharp
mock.Setup(x => x.StreamCompletionAsync(It.IsAny<List<LLMMessage>>(), It.IsAny<CancellationToken>()))
    .Returns(async (List<LLMMessage> msgs, CancellationToken ct) =>
    {
        return new[] { "Hello", " world", "!" }.ToAsyncEnumerable();
    });
```

---

*文档版本：v2.1.0 | 263 个测试全部通过*
