using StardewModdingAPI;
using StardewModdingAPI.Framework.Logging;
using ValleyAgent.Commands;
using ValleyAgent.Protocol;
using ValleyAgent.WebSocket;
using Xunit;

namespace ValleyAgent.UnitTests;

/// <summary>
///     <see cref="CommandExecutor" /> 单元测试。
///     验证 action_result 静默成功修复（要求一：NPC 必须知道自己失败了）。
///     覆盖 ExecuteEmote / ExecuteSetState 的参数校验与失败原因透传，
///     以及 ExecuteAction 端到端回发 action_result 的 default 分支不回归。
///     2026-08-15 步骤 2：经济工具（give_item/give_gift/trade/receive_payment/adjust_*）
///     已迁 TS 同步编排，对应 C# 方法与其单测一并移除（见 AdjustExecutorTests）。
/// </summary>
public static class CommandExecutorTests
{
    // ───────────────────────── 辅助 ─────────────────────────

    private static CommandExecutor CreateExecutor(FakeAgentServerProvider? provider = null)
    {
        var monitor = new StubMonitor();
        var registry = new CommandRegistry(monitor);
        // Execute* 不依赖 AgentService，传 null! 绕过 nullable 检查（运行时不访问该字段）
        return new CommandExecutor(monitor, null!, registry, provider);
    }

    // ───────────────────────── ExecuteEmote ─────────────────────────

    [Fact]
    public static void ExecuteEmote_NullNpc_ReturnsFalseWithAgentMissing()
    {
        var executor = CreateExecutor();
        var args = new Dictionary<string, object> { ["emote_id"] = "happy" };

        // 单测环境游戏未加载，getCharacterFromName 对不存在名字返回 null
        var (success, reason) = executor.ExecuteEmote(args, "NonExistentNpc_12345");

        Assert.False(success);
        Assert.Equal(ProtocolV2.ActionResultReason.AgentMissing, reason);
    }

    [Fact]
    public static void ExecuteEmote_MissingEmoteId_ReturnsFalseWithInvalidState()
    {
        var executor = CreateExecutor();
        var args = new Dictionary<string, object>();

        var (success, reason) = executor.ExecuteEmote(args, "Haley");

        Assert.False(success);
        Assert.Equal(ProtocolV2.ActionResultReason.InvalidState, reason);
    }

    // ───────────────────────── ExecuteSetState ─────────────────────────

    [Fact]
    public static void ExecuteSetState_MissingStateArg_ReturnsFalseWithInvalidState()
    {
        var executor = CreateExecutor();
        var args = new Dictionary<string, object>();

        var (success, reason) = executor.ExecuteSetState(args, "Haley");

        Assert.False(success);
        Assert.Equal(ProtocolV2.ActionResultReason.InvalidState, reason);
    }

    [Fact]
    public static void ExecuteSetState_AgentServiceNull_ReturnsFalseWithAgentMissing()
    {
        // CreateExecutor 传 null! 给 AgentService。
        // ExecuteSetState 检测到 _agentService == null → 返回 AgentMissing，不抛 NullReferenceException。
        // 静态可验证：覆盖海莉事件根因修复后的 null 防御路径。
        // 注：agent 存在 / 状态机转换成功 / TransitionBlocked 等路径需游戏运行验证（真实 AgentService + 状态机）。
        var executor = CreateExecutor();
        var args = new Dictionary<string, object> { ["state"] = "FOLLOW" };

        var (success, reason) = executor.ExecuteSetState(args, "Haley");

        Assert.False(success);
        Assert.Equal(ProtocolV2.ActionResultReason.AgentMissing, reason);
    }

    // ───────────────────────── ExecuteAction 端到端（action_result 回发） ─────────────────────────

    [Fact]
    public static void ExecuteAction_UnknownTool_ReturnsFalse()
    {
        var provider = new FakeAgentServerProvider();
        var executor = CreateExecutor(provider);

        executor.ExecuteAction(
            new ToolAction("unknown_tool_xyz", new Dictionary<string, object>(), "call-1"),
            "Haley");

        Assert.NotNull(provider.LastSentJson);
        var json = provider.LastSentJson!;
        // MessageProtocol 序列化为 camelCase：success: false, reason: "internalError"
        Assert.Contains("\"success\":false", json);
        Assert.Contains("\"reason\":\"internalError\"", json);
        Assert.Contains("\"callId\":\"call-1\"", json);
        Assert.Contains("\"tool\":\"unknown_tool_xyz\"", json);
    }

    [Fact]
    public static void ExecuteAction_NoOpTool_DoesNotSendActionResult()
    {
        var provider = new FakeAgentServerProvider();
        var executor = CreateExecutor(provider);

        // speak 在 NoOpTools 集合中，不应回发 action_result
        executor.ExecuteAction(
            new ToolAction("speak", new Dictionary<string, object>(), "call-2"),
            "Haley");

        Assert.Null(provider.LastSentJson);
    }
}

/// <summary>
///     空实现 <see cref="IMonitor" />，仅用于满足 CommandExecutor 构造依赖。
///     Log 调用被吞掉（单测不验证日志）。
/// </summary>
internal sealed class StubMonitor : IMonitor
{
    public bool IsVerbose { get; set; }

    public void Log(string message, LogLevel level)
    {
        // 单测无需记录日志
    }

    public void LogOnce(string message, LogLevel level)
    {
        // 单测无需记录日志
    }

    public void VerboseLog(string message)
    {
        // 单测无需记录日志
    }

    public void VerboseLog(ref VerboseLogStringHandler message)
    {
        // 单测无需记录日志（SMAPI 4.x interpolated string handler 重载）
    }
}

/// <summary>
///     捕获 <see cref="IAgentServerProvider.SendMessageAsync" /> 的 JSON，
///     用于断言 action_result 内容。其他接口方法未实现（单测不调用）。
/// </summary>
internal sealed class FakeAgentServerProvider : IAgentServerProvider
{
    public string? LastSentJson { get; private set; }

    public event Action? OnConnected
    {
        add { }
        remove { }
    }

    public event Action? OnDisconnected
    {
        add { }
        remove { }
    }

    public Task<DialogueResponse> GenerateDialogueAsync(DialogueRequest request, CancellationToken ct = default)
        => throw new NotImplementedException("CommandExecutor tests do not exercise GenerateDialogueAsync.");

    public Task<bool> IsConnectedAsync(CancellationToken ct = default) => Task.FromResult(true);

    public Task SendMessageAsync(string jsonMessage, CancellationToken ct = default)
    {
        LastSentJson = jsonMessage;
        return Task.CompletedTask;
    }
}
