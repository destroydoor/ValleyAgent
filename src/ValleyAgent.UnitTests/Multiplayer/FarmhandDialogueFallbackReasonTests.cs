using System;
using System.Linq;
using System.Threading.Tasks;
using ValleyAgent.Multiplayer;
using ValleyAgent.Multiplayer.Transports;
using ValleyAgent.WebSocket;
using Xunit;

namespace ValleyAgent.UnitTests.Multiplayer;

/// <summary>
///     房客对话"最后一跳"映射测试（2026-09-13 R2/R3，对话连续性修复 S4）。
///     灰字文案单一来源在 TS speech（R3）；灰字渲染本身依赖 Game1.chatBox 无法单测，
///     本组测试覆盖可测的诚实单元：主机广播的 DialogueResponseMessage 经
///     FarmhandDialogueTransport.HandleResponse → Complete 落到房客本地 DialogueResponse 时，
///     speech / fallback / fallbackReason 三者必须完整映射——
///     此前 FallbackReason 不在广播契约里，房客端灰字诊断（busy vs LLM 故障）失明。
///     fixture 模式与 FarmhandTransportResponseMatchingTests 相同（Game1Statics 串行 + 主线程泵排水）。
/// </summary>
[Collection("Game1Statics")]
public sealed class FarmhandDialogueFallbackReasonTests : IDisposable
{
    private const string ModId = "dandm1.ValleyAgent";

    /// <summary>短超时：测试不需要等真实 60s。</summary>
    private static readonly TimeSpan TestTimeout = TimeSpan.FromMilliseconds(400);

    private static readonly TimeSpan RegisterTimeout = TimeSpan.FromSeconds(5);

    private readonly Game1TestScope _scope = new();
    private readonly RecordingMonitor _monitor = new();
    private readonly RecordingMultiplayerService _multiplayer = new();
    private readonly FarmhandDialogueTransport _dialogue;

    // transport 的 SendMessage 经 HostRequestHandlers.EnqueueMainThread 投递主线程，
    // 泵线程模拟游戏 UpdateTicked 每 tick 排水（否则 pending 永不注册）。
    private readonly MainThreadPump _pump = new();

    public FarmhandDialogueFallbackReasonTests()
    {
        _dialogue = new FarmhandDialogueTransport(new RecordingModHelper(_multiplayer), _monitor, ModId, TestTimeout);
    }

    public void Dispose()
    {
        _pump.Dispose();
        _scope.Dispose();
    }

    private Task<DialogueResponse> SendDialogueAsync(string npc, string input) =>
        Task.Run(() => _dialogue.SendAsync(npc, input, TestSnapshots.Minimal()).WaitAsync(TimeSpan.FromSeconds(15)));

    private string FirstSentRequestId() =>
        _multiplayer.OfType(MessageTypes.DialogueRequest)
            .Select(m => ((DialogueRequestMessage)m.Message).RequestId ?? "")
            .First();

    [Fact]
    public async Task BusyFallbackReply_MapsSpeechFallbackAndReasonToLastHop()
    {
        // TS BUSY 回包（protocol-adapter buildBusyResponse → AgentSyncBroadcaster 广播）：
        // fallback=true + fallbackReason="busy"，speech 为 TS 文案单一来源——
        // 房客端灰字渲染直接用回包自带的 speech，本地不得重写/丢失。
        var task = SendDialogueAsync("Abigail", "在吗");
        Assert.True(Poll.Until(() => _multiplayer.OfType(MessageTypes.DialogueRequest).Count >= 1, RegisterTimeout),
            "请求未注册（主线程泵未排水）");
        var requestId = FirstSentRequestId();

        _dialogue.HandleResponse(new DialogueResponseMessage
        {
            NpcName = "Abigail",
            Text = "（Abigail 正在和别人交流）",
            Emotion = "Neutral",
            Fallback = true,
            FallbackReason = "busy",
            RequestId = requestId
        });

        var reply = await task;
        Assert.Equal("（Abigail 正在和别人交流）", reply.Speech);
        Assert.True(reply.Fallback);
        Assert.Equal("busy", reply.FallbackReason);
    }

    [Fact]
    public async Task ReplyWithoutFallbackReason_MapsNullReasonButKeepsFallbackAndSpeech()
    {
        // 旧 TS 客户端 / 旧主机不带 fallbackReason（R2 缺省语义）：映射必须为 null 而非抛异常，
        // fallback 标志与 speech 仍要完整落地——房客按现有 fallback 行为渲染灰字。
        var task = SendDialogueAsync("Sebastian", "在吗");
        Assert.True(Poll.Until(() => _multiplayer.OfType(MessageTypes.DialogueRequest).Count >= 1, RegisterTimeout),
            "请求未注册（主线程泵未排水）");
        var requestId = FirstSentRequestId();

        _dialogue.HandleResponse(new DialogueResponseMessage
        {
            NpcName = "Sebastian",
            Text = "（我有点走神了…）",
            Emotion = "Neutral",
            Fallback = true,
            RequestId = requestId
        });

        var reply = await task;
        Assert.Equal("（我有点走神了…）", reply.Speech);
        Assert.True(reply.Fallback);
        Assert.Null(reply.FallbackReason);
    }
}
