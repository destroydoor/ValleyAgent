using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ValleyAgent.Multiplayer;
using ValleyAgent.Multiplayer.Transports;
using ValleyAgent.WebSocket;
using Xunit;

namespace ValleyAgent.UnitTests.Multiplayer;

/// <summary>
///     房客回包匹配测试（2026-09-09 卡死排查套件 1）。
///     修复前：ModMessage 不携带 requestId，HandleResponse 按 npcName 前缀 FIFO 匹配 pending——
///     同 NPC 并发两请求、或超时重试后迟到回包到达时回包错配（错配的 TCS 拿到别人的答复，
///     真正等待的 TCS 空等到 60s 超时；好感 delta 记到错误礼物上）。
///     修复后：请求/回包都携带 RequestId，本组测试模拟新主机（从记录的发送请求中提取
///     RequestId 原样回填），断言精确配对；旧主机（无 RequestId）回退路径由
///     HandleResponse_OldHostWithoutRequestId_FallsBackToFifo 与
///     Dialogue_ConcurrentDifferentNpcs_RoutesByPrefixCorrectly 守卫。
/// </summary>
[Collection("Game1Statics")]
public sealed class FarmhandTransportResponseMatchingTests : IDisposable
{
    private const string ModId = "dandm1.ValleyAgent";

    /// <summary>短超时：测试不需要等真实 60s。</summary>
    private static readonly TimeSpan TestTimeout = TimeSpan.FromMilliseconds(400);

    private static readonly TimeSpan RegisterTimeout = TimeSpan.FromSeconds(5);

    private readonly Game1TestScope _scope = new();
    private readonly RecordingMonitor _monitor = new();
    private readonly RecordingMultiplayerService _multiplayer = new();
    private readonly FarmhandDialogueTransport _dialogue;
    private readonly FarmhandGiftTransport _gift;

    // 修复后（2026-09-09 缺口③）transport 的 SendMessage 经 HostRequestHandlers.EnqueueMainThread
    // 投递主线程——测试用专用泵线程模拟游戏 UpdateTicked 每 tick 排水，与真实时序一致。
    private readonly MainThreadPump _pump = new();

    public FarmhandTransportResponseMatchingTests()
    {
        var helper = new RecordingModHelper(_multiplayer);
        _dialogue = new FarmhandDialogueTransport(helper, _monitor, ModId, TestTimeout);
        _gift = new FarmhandGiftTransport(helper, _monitor, ModId, TestTimeout);
    }

    public void Dispose()
    {
        _pump.Dispose();
        _scope.Dispose();
    }

    private static WorldSnapshot Snapshot() => TestSnapshots.Minimal();

    private Task<DialogueResponse> SendDialogueAsync(string npc, string input) =>
        Task.Run(() => _dialogue.SendAsync(npc, input, Snapshot()).WaitAsync(TimeSpan.FromSeconds(15)));

    private Task<GiftReactionResult> SendGiftAsync(string npc, string itemId) =>
        Task.Run(() => _gift.SendAsync(npc, itemId, 1).WaitAsync(TimeSpan.FromSeconds(15)));

    private int SentCount() => _multiplayer.OfType(MessageTypes.DialogueRequest).Count;

    private int GiftSentCount() => _multiplayer.OfType(MessageTypes.GiftRequest).Count;

    /// <summary>模拟新主机行为：从记录的发送请求中提取 RequestId（与发送顺序一致）。</summary>
    private List<string> SentDialogueRequestIds() =>
        _multiplayer.OfType(MessageTypes.DialogueRequest)
            .Select(m => ((DialogueRequestMessage)m.Message).RequestId ?? "")
            .ToList();

    private List<string> SentGiftRequestIds() =>
        _multiplayer.OfType(MessageTypes.GiftRequest)
            .Select(m => ((GiftRequestMessage)m.Message).RequestId ?? "")
            .ToList();

    [Fact]
    public async Task Dialogue_SameNpc50ConcurrentPairs_InOrderResponses_MustPairCorrectly()
    {
        // 50 对同 NPC 并发请求，回包按发起顺序送达（对主机侧最友好的时序）。
        // 修复前：FIFO 每次取「任意一个」同前缀 pending（ConcurrentDictionary 无序），
        // 每对约有 50% 概率错配——50 对全对的概率 ≈ 2^-50，断言必然失败（确定性红）。
        // 修复后（回包回填 requestId 精确匹配）：应 100% 正确配对。
        const int pairs = 50;
        for (var i = 0; i < pairs; i++)
        {
            var baseline = SentCount();
            var npc = $"Abigail{i % 3}";

            var taskA = SendDialogueAsync(npc, $"问题A{i}");
            Assert.True(Poll.Until(() => SentCount() >= baseline + 1, RegisterTimeout), $"对{i}：请求A未注册");
            var taskB = SendDialogueAsync(npc, $"问题B{i}");
            Assert.True(Poll.Until(() => SentCount() >= baseline + 2, RegisterTimeout), $"对{i}：请求B未注册");

            // 模拟新主机：提取两个请求各自的 requestId 原样回填。
            var ids = SentDialogueRequestIds().Skip(baseline).ToList();
            var idA = ids[0];
            var idB = ids[1];

            // 按发起顺序回包：答A 归请求A，答B 归请求B。
            _dialogue.HandleResponse(new DialogueResponseMessage { NpcName = npc, Text = $"答A{i}", RequestId = idA });
            _dialogue.HandleResponse(new DialogueResponseMessage { NpcName = npc, Text = $"答B{i}", RequestId = idB });

            var replyA = await taskA;
            var replyB = await taskB;
            Assert.True(replyA.Speech == $"答A{i}" && replyB.Speech == $"答B{i}",
                $"对{i} 回包错配：请求A（问题A{i}）收到「{replyA.Speech}」，请求B（问题B{i}）收到「{replyB.Speech}」。"
                + "根因：回包未按 requestId 精确匹配 pending。");
        }
    }

    [Fact]
    public async Task Dialogue_LateResponseAfterTimeout_MustNotBeStolenByRetry()
    {
        // 超时重试场景（AGENTS.md 已知限制的确定性复现）：
        // 请求1 超时 → 玩家重试发请求2 → 请求1 的迟到回包到达 →
        // 修复前会把迟到回包塞给请求2（拿到过期答案），而请求2 自己的真正回包随后被丢弃。
        // 修复后：迟到回包按自己的 requestId 匹配——pending 已被超时清理，直接丢弃。
        var task1 = SendDialogueAsync("Abigail", "问题1");
        Assert.True(Poll.Until(() => SentCount() >= 1, RegisterTimeout), "请求1未注册");
        var id1 = SentDialogueRequestIds()[0];

        var reply1 = await task1;
        Assert.Contains("超时", reply1.Speech, StringComparison.Ordinal);

        var task2 = SendDialogueAsync("Abigail", "问题2");
        Assert.True(Poll.Until(() => SentCount() >= 2, RegisterTimeout), "请求2未注册");
        var id2 = SentDialogueRequestIds()[1];

        _dialogue.HandleResponse(new DialogueResponseMessage { NpcName = "Abigail", Text = "迟到的答1", RequestId = id1 });
        _dialogue.HandleResponse(new DialogueResponseMessage { NpcName = "Abigail", Text = "新鲜的答2", RequestId = id2 });

        var reply2 = await task2;
        Assert.True(reply2.Speech == "新鲜的答2",
            $"重试请求拿到了「{reply2.Speech}」（期望「新鲜的答2」）——迟到回包未被丢弃，窃取了新请求的回包位。");
    }

    [Fact]
    public async Task Dialogue_ConcurrentDifferentNpcs_RoutesByPrefixCorrectly()
    {
        // 绿色回归基线：不同 NPC 的前缀可区分，乱序回包也能正确路由。
        var taskA = SendDialogueAsync("Abigail", "问题A");
        Assert.True(Poll.Until(() => SentCount() >= 1, RegisterTimeout), "请求A未注册");
        var taskS = SendDialogueAsync("Sebastian", "问题S");
        Assert.True(Poll.Until(() => SentCount() >= 2, RegisterTimeout), "请求S未注册");

        _dialogue.HandleResponse(new DialogueResponseMessage { NpcName = "Sebastian", Text = "答S" });
        _dialogue.HandleResponse(new DialogueResponseMessage { NpcName = "Abigail", Text = "答A" });

        Assert.Equal("答A", (await taskA).Speech);
        Assert.Equal("答S", (await taskS).Speech);
    }

    [Fact]
    public async Task Gift_LateResponseAfterTimeout_MustNotBeStolenByRetry()
    {
        // 送礼传输同样的迟到回包错配（好感 delta 会张冠李戴）。
        var task1 = SendGiftAsync("Abigail", "(O)16");
        Assert.True(Poll.Until(() => GiftSentCount() >= 1, RegisterTimeout), "送礼请求1未注册");
        var id1 = SentGiftRequestIds()[0];

        var reply1 = await task1;
        Assert.Contains("超时", reply1.Reaction, StringComparison.Ordinal);

        var task2 = SendGiftAsync("Abigail", "(O)20");
        Assert.True(Poll.Until(() => GiftSentCount() >= 2, RegisterTimeout), "送礼请求2未注册");
        var id2 = SentGiftRequestIds()[1];

        _gift.HandleResponse(new GiftResponseMessage
        {
            NpcName = "Abigail",
            ResponseText = "迟到的评1",
            FriendshipChange = 30,
            RequestId = id1
        });
        _gift.HandleResponse(new GiftResponseMessage
        {
            NpcName = "Abigail",
            ResponseText = "新鲜的评2",
            FriendshipChange = -5,
            RequestId = id2
        });

        var reply2 = await task2;
        Assert.True(reply2.Reaction == "新鲜的评2" && reply2.FriendshipDelta == -5,
            $"重试送礼拿到「{reply2.Reaction}」(delta={reply2.FriendshipDelta})——迟到回包未被丢弃，好感 delta 会记到错误礼物上。");
    }

    [Fact]
    public async Task HandleResponse_OldHostWithoutRequestId_FallsBackToFifo()
    {
        // 旧版本主机兼容绿基线：回包不带 RequestId → 退回 npcName 前缀匹配。
        // 同 NPC 单 pending 场景下 FIFO 无歧义，必须命中（多 pending 旧主机场景由
        // Dialogue_ConcurrentDifferentNpcs_RoutesByPrefixCorrectly 守卫——不同 NPC 前缀可区分）。
        var task = SendDialogueAsync("Abigail", "问题O");
        Assert.True(Poll.Until(() => SentCount() >= 1, RegisterTimeout), "请求未注册");

        _dialogue.HandleResponse(new DialogueResponseMessage { NpcName = "Abigail", Text = "旧主机答" });

        Assert.Equal("旧主机答", (await task).Speech);
    }

    [Fact]
    public async Task DialogueRequest_MustNotBeSentFromBackgroundThread()
    {
        // 主线程纪律审计：SMAPI/游戏底层 ModMessage 队列非线程安全，
        // SendMessage 必须发生在主线程（泵）上，而不是 Task.Run 调用者线程。
        // 修复后：transport 经 HostRequestHandlers.EnqueueMainThread 投递，fixture 泵排水。
        var callerThreadId = 0;
        var task = Task.Run(async () =>
        {
            Interlocked.Exchange(ref callerThreadId, Environment.CurrentManagedThreadId);
            await _dialogue.SendAsync("Abigail", "问题X", Snapshot()).WaitAsync(TimeSpan.FromSeconds(10));
        });

        Assert.True(Poll.Until(() => _multiplayer.OfType(MessageTypes.DialogueRequest).Count == 1, RegisterTimeout),
            "请求未发出");

        var sends = _multiplayer.OfType(MessageTypes.DialogueRequest);
        var callerId = Volatile.Read(ref callerThreadId);
        Assert.True(sends.All(m => m.ThreadId != callerId),
            $"SendMessage 在 Task.Run 调用者线程 {callerId} 上直接发生——跨线程操作游戏底层消息队列，多人高并发下可污染 Queue 结构（房主随机无日志卡死的候选根因之一）。实际发送线程: [{string.Join(", ", sends.Select(m => m.ThreadId))}]");
        Assert.All(sends, m => Assert.Equal(_pump.ThreadId, m.ThreadId));

        await task;
    }
}
