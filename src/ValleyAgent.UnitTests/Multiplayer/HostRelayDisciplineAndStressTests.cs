using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ValleyAgent.Agents;
using ValleyAgent.Config;
using ValleyAgent.Multiplayer;
using ValleyAgent.Multiplayer.Transports;
using ValleyAgent.Performance;
using ValleyAgent.Resilience;
using ValleyAgent.Services;
using ValleyAgent.WebSocket;
using Xunit;

namespace ValleyAgent.UnitTests.Multiplayer;

/// <summary>
///     主机侧中继链路：主线程纪律审计 + 3 人（房主 + 2 房客）负载压测（2026-09-09 卡死排查套件 2）。
///     复现环境：虚拟主线程泵（与 OnUpdateTicked 相同排水动作）+ 双房客并发请求流 + 模拟 LLM 延迟。
///     看门狗在泵心跳停滞时抓全进程线程状态——验证「房主随机无日志卡死」是否能在中继链路里复现。
/// </summary>
[Collection("Game1Statics")]
public sealed class HostRelayDisciplineAndStressTests
{
    private const string ModId = "dandm1.ValleyAgent";

    private static (
        HostRequestHandlers Handlers,
        RecordingMonitor Monitor,
        RecordingMultiplayerService Multiplayer,
        FakeAgentServerProvider Provider,
        StubHostGiftTransport Gift) BuildHostStack(FakeAgentServerProvider? provider = null)
    {
        var monitor = new RecordingMonitor();
        var multiplayer = new RecordingMultiplayerService();
        var helper = new RecordingModHelper(multiplayer);
        provider ??= new FakeAgentServerProvider();
        var agentService = new AgentService(
            new ModConfig(),
            new AgentAllocationManager(),
            new TokenBudgetManager(100_000),
            new PerformanceMonitor(),
            new CacheManager(),
            new CircuitBreaker());
        var broadcaster = new AgentSyncBroadcaster(agentService, monitor, helper, ModId);
        var gift = new StubHostGiftTransport();
        var handlers = new HostRequestHandlers(monitor, provider, broadcaster, gift, commandExecutor: null,
            agentService: null);
        return (handlers, monitor, multiplayer, provider, gift);
    }

    private static DialogueRequestMessage DialogueMsg(long playerId, string npc, string input) =>
        new()
        {
            NpcName = npc,
            PlayerMessage = input,
            PlayerId = playerId,
            WorldSnapshotJson = JsonSerializer.Serialize(TestSnapshots.Minimal())
        };

    [Fact]
    public void MainThreadQueue_ThreeProducersHighLoad_NoLoss_AllExecutedOnPumpThread()
    {
        // 主线程队列原语（2026-08-23 审计 P0 修复的核心机制）回归守卫：
        // 3 个生产者高频入队（模拟 2 房客 + 本机并发），零丢失，且全部在主线程泵上执行。
        using var pump = new MainThreadPump();
        long processed = 0;
        var executedThreads = new ConcurrentDictionary<int, byte>();
        const int perProducer = 1000;

        Parallel.For(0, 3, _ =>
        {
            for (var i = 0; i < perProducer; i++)
            {
                HostRequestHandlers.EnqueueMainThread(() =>
                {
                    Interlocked.Increment(ref processed);
                    executedThreads[Environment.CurrentManagedThreadId] = 0;
                });
            }
        });

        Assert.True(Poll.Until(() => Interlocked.Read(ref processed) == 3 * perProducer, TimeSpan.FromSeconds(20)),
            $"20s 内仅处理 {Interlocked.Read(ref processed)}/{3 * perProducer}——主线程队列出现丢失或排水停摆。\n{pump.StallDump}");
        Assert.All(executedThreads.Keys, id => Assert.Equal(pump.ThreadId, id));
    }

    [Fact]
    public async Task HandleDialogueRequest_TwoSimulatedFarmhands_AllResponsesBroadcastOnPumpThread()
    {
        // 房主 + 2 房客：两路并发对话请求穿过真实 HostRequestHandlers 链路
        // （provider 模拟 LLM 延迟 5-25ms），断言：
        //  1) 100% 完成；2) 全部广播发生在主线程泵线程（2026-08-23 审计 P0 的回归守卫）；
        //  3) 泵无卡顿（看门狗无触发）。
        using var scope = new Game1TestScope(hostMode: true);
        _ = scope;
        var provider = new FakeAgentServerProvider(minLatencyMs: 5, maxLatencyMs: 25);
        var (handlers, monitor, multiplayer, _, _) = BuildHostStack(provider);
        using var pump = new MainThreadPump();

        const int perFarmhand = 50;
        var npcs = new[] { "Abigail", "Sebastian", "Haley" };
        var requests = new List<Task>();
        foreach (var farmerId in new long[] { 111222333, 444555666 })
        {
            for (var i = 0; i < perFarmhand; i++)
            {
                var msg = DialogueMsg(farmerId, npcs[i % npcs.Length], $"hi{i}");
                requests.Add(Task.Run(() => handlers.HandleDialogueRequest(msg)));
            }
        }

        await Task.WhenAll(requests); // async void：只等到 pre-await 段，完成以日志为准

        const int total = 2 * perFarmhand;
        var drained = Poll.Until(
            () => monitor.CountContaining("HandleDialogueRequest completed") >= total, TimeSpan.FromSeconds(30));
        Assert.True(drained,
            $"30s 内仅完成 {monitor.CountContaining("HandleDialogueRequest completed")}/{total}（provider 收到 {provider.RequestCount} 个请求）\n{pump.StallDump}");
        Assert.Equal(total, provider.RequestCount);

        var responses = multiplayer.OfType(MessageTypes.DialogueResponse);
        Assert.Equal(total, responses.Count);
        Assert.All(responses, r => Assert.Equal(pump.ThreadId, r.ThreadId));

        Assert.True(pump.MaxStallMs < 2000, $"主线程泵最大卡顿 {pump.MaxStallMs:F0}ms（阈值 2000ms）\n{pump.StallDump}");
        Assert.Null(pump.StallDump);
    }

    [Fact]
    public void HandleGiftRequest_FromFarmhand_ResponseBroadcastOnPumpThread()
    {
        using var scope = new Game1TestScope(hostMode: true);
        _ = scope;
        var (handlers, monitor, multiplayer, _, gift) = BuildHostStack();
        using var pump = new MainThreadPump();

        for (var i = 0; i < 5; i++)
        {
            handlers.HandleGiftRequest(new GiftRequestMessage
            {
                NpcName = "Abigail",
                ItemId = "(O)16",
                Quantity = 1,
                PlayerId = 111222333
            });
        }

        Assert.True(
            Poll.Until(() => gift.CallCount == 5 && multiplayer.OfType(MessageTypes.GiftResponse).Count == 5,
                TimeSpan.FromSeconds(10)),
            $"送礼评估调用 {gift.CallCount}/5，广播回包 {multiplayer.OfType(MessageTypes.GiftResponse).Count}/5，"
            + $"gift失败日志 {monitor.CountContaining("HandleGiftRequest failed")}，队列失败日志 {monitor.CountContaining("main-thread action failed")}，"
            + $"最近日志: {string.Join(" | ", monitor.Snapshot().TakeLast(6).Select(e => $"{e.Level}:{e.Message}"))}");
        Assert.All(multiplayer.OfType(MessageTypes.GiftResponse), r => Assert.Equal(pump.ThreadId, r.ThreadId));
    }

    [Fact]
    public void RelayStress_ThreeConcurrentStreams_PumpNeverStalls()
    {
        // 3 人房压力场景总装：2 路对话流（延迟抖动 5-40ms）+ 2 路送礼流并发打真实中继链路。
        // 这是「房主随机无日志卡死」在虚拟环境里的复现仪器：
        // 泵心跳停滞超过阈值时看门狗抓全进程线程状态，断言失败时附在错误消息里。
        using var scope = new Game1TestScope(hostMode: true);
        _ = scope;
        var provider = new FakeAgentServerProvider(minLatencyMs: 5, maxLatencyMs: 40);
        var (handlers, monitor, multiplayer, _, gift) = BuildHostStack(provider);
        using var pump = new MainThreadPump(stallDumpThresholdMs: 2000);

        const int dialoguesPerStream = 120;
        const int giftsPerStream = 60;
        var npcs = new[] { "Abigail", "Sebastian", "Haley", "Leah" };
        foreach (var farmerId in new long[] { 111222333, 444555666 })
        {
            for (var i = 0; i < dialoguesPerStream; i++)
            {
                var msg = DialogueMsg(farmerId, npcs[i % npcs.Length], $"stress{i}");
                _ = Task.Run(() => handlers.HandleDialogueRequest(msg));
            }

            for (var i = 0; i < giftsPerStream; i++)
            {
                var giftMsg = new GiftRequestMessage
                {
                    NpcName = npcs[(i + 1) % npcs.Length],
                    ItemId = "(O)16",
                    Quantity = 1,
                    PlayerId = farmerId
                };
                _ = Task.Run(() => handlers.HandleGiftRequest(giftMsg));
            }
        }

        const int totalDialogues = 2 * dialoguesPerStream;
        const int totalGifts = 2 * giftsPerStream;
        var drained = Poll.Until(
            () => monitor.CountContaining("HandleDialogueRequest completed") >= totalDialogues
                  && multiplayer.OfType(MessageTypes.GiftResponse).Count >= totalGifts,
            TimeSpan.FromSeconds(60));

        Assert.True(drained,
            $"60s 内未全部完成：对话 {monitor.CountContaining("HandleDialogueRequest completed")}/{totalDialogues}，"
            + $"送礼 {multiplayer.OfType(MessageTypes.GiftResponse).Count}/{totalGifts}（provider 收到 {provider.RequestCount}）\n{pump.StallDump}");

        var responses = multiplayer.OfType(MessageTypes.DialogueResponse);
        Assert.Equal(totalDialogues, responses.Count);
        Assert.All(responses, r => Assert.Equal(pump.ThreadId, r.ThreadId));
        Assert.True(pump.MaxStallMs < 5000, $"主线程泵最大卡顿 {pump.MaxStallMs:F0}ms（阈值 5000ms）\n{pump.StallDump}");
        Assert.Null(pump.StallDump);
    }

    [Fact]
    public void HandleInteractionRequest_IsPlaceholder_NoSideEffects()
    {
        // 客户端功能缺口记录：InteractionRequest 已定义消息类型并路由，但主机侧 handler 是占位——
        // 房客右键 NPC 的交互语义完全没有实现（用户反馈「网络上对了也没法实际使用」的一环）。
        using var scope = new Game1TestScope(hostMode: true);
        _ = scope;
        var (handlers, monitor, multiplayer, _, _) = BuildHostStack();

        handlers.HandleInteractionRequest(new InteractionRequestMessage { NpcName = "Abigail", PlayerId = 111222333 });

        Assert.Empty(multiplayer.Snapshot());
        var logs = string.Join("\n", monitor.Snapshot().Select(e => e.Message));
        Assert.Contains("placeholder", logs, StringComparison.OrdinalIgnoreCase);
    }
}
