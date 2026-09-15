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

    /// <summary>绿色回归守卫：主机侧确实每 tick 排空全部主线程队列（逐泵隔离形态）。</summary>
    [Fact]
    public void Host_UpdateTicked_DrainsAllMainThreadQueues()
    {
        var src = ReadSource("src", "ValleyAgent", "Initialization", "EventHandlerInitializer.cs");
        // 2026-09-13 死锁专项：泵改为逐泵隔离形态（Pump 助手，每泵独立 try/catch）——
        // 单个泵抛异常不得跳过下游泵（下游含房客唯一的 ModMessage 发送泵）。
        // 断言锁定 Pump 包裹形态：退回裸调用/共用一个 try 的旧写法会在这里红。
        Assert.Contains("Pump(\"gift-actions\", NPCGiftPatch.ProcessMainThreadActions)", src);
        Assert.Contains("Pump(\"dialogue-replies\", DialogueBoxInputPatch.ProcessPendingReplies)", src);
        Assert.Contains("Pump(\"host-request-mainthread\", Multiplayer.HostRequestHandlers.ProcessMainThreadActions)", src);
        // 陈旧等待自愈泵：回包与超时兜底双双丢失时强制解锁输入框，必须每 tick 被驱动
        Assert.Contains("Pump(\"dialogue-stale-wait\", DialogueBoxInputPatch.ResetStaleWait)", src);
    }

    /// <summary>
    ///     绿色回归守卫（2026-09-12 死锁专项后更新）：房客 UpdateTicked 必须逐泵隔离排空
    ///     全部主线程队列。2026-09-13 死锁专项前这里的缺口是"只驱动 renderer、队列无人排水"；
    ///     修复后又出现过一次回退风险——泵被改回共用一个 try/catch 的裸调用形态时，
    ///     上游泵抛异常会饿死下游发送泵（房客唯一的 ModMessage 出口）⇒ 对话框永久锁死。
    ///     所以断言锁定 Pump 包裹形态 + 自愈泵在位：回退旧写法在这里红。
    /// </summary>
    [Fact]
    public void ThinClient_UpdateTicked_MustDrainMainThreadQueues()
    {
        var body = ThinClientInitBody();
        var missing = new[]
            {
                "Pump(\"dialogue-replies\", DialogueBoxInputPatch.ProcessPendingReplies)",
                "Pump(\"chat-replies\", ChatBarRouter.ProcessPendingReplies)",
                "Pump(\"gift-actions\", NPCGiftPatch.ProcessMainThreadActions)",
                "Pump(\"host-request-mainthread\", Multiplayer.HostRequestHandlers.ProcessMainThreadActions)",
                "Pump(\"dialogue-stale-wait\", DialogueBoxInputPatch.ResetStaleWait)"
            }
            .Where(fragment => !body.Contains(fragment, StringComparison.Ordinal))
            .ToList();

        Assert.True(missing.Count == 0,
            "ThinClient 的 UpdateTicked 订阅（ModEntry.InitializeThinClientMode 第 6 步）必须逐泵隔离排空主线程队列，"
            + "缺失的泵: " + string.Join("; ", missing)
            + " → 后果：房客 AI 回复不渲染、送礼反应与好感写入不落地；或单个泵抛异常饿死下游发送泵"
            + "（对话框永久锁死，2026-09-12 死锁专项已修——禁止回退成共用一个 try/catch 的裸调用）。");
    }

    /// <summary>
    ///     M3 已修复（2026-09-13）：房客聊天栏路由。
    ///     原缺口：ChatBarRouter 只在主机 EventHandlerInitializer 初始化，ThinClient 不创建——
    ///     ChatBoxInputPatch 在房客侧捕获了聊天输入但路由器未初始化，聊天栏发起的 NPC 对话静默丢弃。
    ///     修复：InitializeThinClientMode 注入 FarmhandDialogueTransport 版路由器
    ///     （InitializeFarmhand），并在 tick 排水里消费其回复队列。
    ///
    ///     这里仍是源码审计（无法在本机驱动真实房客 tick），但断言从"提过 ChatBarRouter 这个名字"
    ///     升级为"初始化 + 回复排水"两处接线都在位——缺任一处房客聊天栏依旧是死的。
    /// </summary>
    [Fact]
    public void ThinClient_MustWireChatBarRouter()
    {
        var body = ThinClientInitBody();

        Assert.True(body.Contains("ChatBarRouter.InitializeFarmhand", StringComparison.Ordinal),
            "ModEntry.InitializeThinClientMode 未初始化房客版 ChatBarRouter → "
            + "房客聊天栏输入被 ChatBoxInputPatch 捕获后无人路由，聊天栏 NPC 对话在客户端不可用。");

        Assert.True(body.Contains("Pump(\"chat-replies\", ChatBarRouter.ProcessPendingReplies)", StringComparison.Ordinal),
            "房客 UpdateTicked 未排空 ChatBarRouter 的回复队列 → "
            + "主机回包到达房客后只入队不渲染（后台线程入队、主线程消费，缺排水即黑屏）。");

        // 聊天输入拦截补丁也要接上 monitor，否则路由异常在房客侧完全静默。
        Assert.True(body.Contains("ChatBoxInputPatch.Initialize", StringComparison.Ordinal),
            "房客未初始化 ChatBoxInputPatch（只设 IMonitor）→ 聊天栏路由出错时无任何日志，故障不可观测。");
    }

    /// <summary>
    ///     房客形态的对话请求必须经 FarmhandDialogueTransport 转发主机（房客没有本地 LLM 通道）。
    ///     审计 ChatBarRouter：存在 transport 分支，且房客不重复执行 actions（实体在主机权威）。
    ///     2026-09-14 PR2（B2）：BuildPresence 的房客名单过滤已拆除（对话不需要身体，
    ///     全部在场村民都是候选）——候选过滤断言移入 FarmhandBodyLinkTests（反向守卫）。
    /// </summary>
    [Fact]
    public void ChatBarRouter_Farmhand_ForwardsViaTransport()
    {
        var src = ReadSource("src", "ValleyAgent", "Chat", "ChatBarRouter.cs");

        Assert.Contains("IDialogueTransport? _dialogueTransport", src);
        Assert.Contains("InitializeFarmhand", src);
        // 主机已执行过 actions（含广播同步），房客重复执行会造成双份效果
        Assert.Contains("if (!IsFarmhand)", src);
    }
}
