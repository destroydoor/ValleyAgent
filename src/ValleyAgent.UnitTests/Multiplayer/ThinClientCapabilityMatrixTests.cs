using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace ValleyAgent.UnitTests.Multiplayer;

/// <summary>
///     客户端（ThinClient）功能 parity 特征测试（2026-09-09 卡死排查套件 3）。
///     复现用户反馈：「客户端根本没有实现主机的功能，网络上对了也没法实际使用」。
///     接线缺口用源码审计钉住（审计的是接线事实，与运行环境无关、确定性红绿）；
///     修复落地后应替换为行为测试（模拟房客 tick 驱动并观察队列消费）。
/// </summary>
public class ThinClientCapabilityMatrixTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "AGENTS.md")))
        {
            dir = dir.Parent;
        }

        Assert.True(dir != null,
            $"从 {AppContext.BaseDirectory} 向上找不到 AGENTS.md，无法定位仓库根——源码审计测试依赖源码树在位");
        return dir!.FullName;
    }

    private static string ReadSource(params string[] parts) =>
        File.ReadAllText(Path.Combine(new[] { RepoRoot() }.Concat(parts).ToArray()));

    /// <summary>按大括号配对提取方法体（源码审计用，足够可靠：审计目标是本仓库自己的规整代码）。</summary>
    private static string ExtractMethodBody(string source, string signatureFragment)
    {
        var sig = source.IndexOf(signatureFragment, StringComparison.Ordinal);
        Assert.True(sig >= 0, $"找不到方法签名片段: {signatureFragment}");
        var open = source.IndexOf('{', sig);
        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            if (source[i] == '{')
            {
                depth++;
            }
            else if (source[i] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return source.Substring(open, i - open + 1);
                }
            }
        }

        throw new InvalidOperationException("大括号不配对，提取失败");
    }

    private static string ThinClientInitBody() =>
        ExtractMethodBody(ReadSource("src", "ValleyAgent", "ModEntry.cs"), "InitializeThinClientMode(IModHelper helper)");

    /// <summary>绿色回归守卫：主机侧确实每 tick 排空三个主线程队列。</summary>
    [Fact]
    public void Host_UpdateTicked_DrainsAllMainThreadQueues()
    {
        var src = ReadSource("src", "ValleyAgent", "Initialization", "EventHandlerInitializer.cs");
        Assert.Contains("NPCGiftPatch.ProcessMainThreadActions()", src);
        Assert.Contains("DialogueBoxInputPatch.ProcessPendingReplies()", src);
        Assert.Contains("Multiplayer.HostRequestHandlers.ProcessMainThreadActions()", src);
    }

    /// <summary>
    ///     红色缺口：房客 UpdateTicked 只驱动 renderer 位置插值，三个主线程队列无人排水——
    ///     对话回包渲染（_pendingReplies）、送礼反应与好感落账（_mainThreadActions）、
    ///     HostRequestHandlers 主线程队列在房客机器上永不消费。
    ///     网络层全部正确（请求到达主机、回包到达房客），但结果永远不落地——
    ///     即「网络上对了也没法实际使用」。
    /// </summary>
    [Fact]
    public void ThinClient_UpdateTicked_MustDrainMainThreadQueues()
    {
        var body = ThinClientInitBody();
        var missing = new[]
            {
                "DialogueBoxInputPatch.ProcessPendingReplies()",
                "NPCGiftPatch.ProcessMainThreadActions()",
                "Multiplayer.HostRequestHandlers.ProcessMainThreadActions()"
            }
            .Where(fragment => !body.Contains(fragment, StringComparison.Ordinal))
            .ToList();

        Assert.True(missing.Count == 0,
            "ThinClient 的 UpdateTicked 订阅（ModEntry.InitializeThinClientMode 第 6 步）只驱动 AgentRemoteRenderer.Update，"
            + "未排空主线程队列: " + string.Join("; ", missing)
            + " → 后果：房客 AI 回复不渲染、送礼反应与好感写入不落地（后台线程入队后无人消费）。"
            + "修复方向：房客 tick 内追加与主机相同的三处排水调用。");
    }

    /// <summary>
    ///     红色缺口：ChatBarRouter 只在主机 EventHandlerInitializer 初始化，ThinClient 不创建——
    ///     ChatBoxInputPatch 在房客侧捕获了聊天输入但路由器未初始化，聊天栏发起的 NPC 对话静默丢弃。
    /// </summary>
    [Fact]
    public void ThinClient_MustWireChatBarRouter()
    {
        var body = ThinClientInitBody();

        Assert.True(body.Contains("ChatBarRouter", StringComparison.Ordinal),
            "ModEntry.InitializeThinClientMode 未初始化 ChatBarRouter（主机侧在 EventHandlerInitializer 初始化）→ "
            + "房客聊天栏输入被 ChatBoxInputPatch 捕获后静默丢弃，聊天栏 NPC 对话在客户端不可用。"
            + "修复方向：ThinClient 注入 FarmhandDialogueTransport 版 ChatBarRouter 或在判定后显式禁用入口并提示。");
    }
}
