using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using StardewModdingAPI;
using ValleyAgent.Multiplayer;
using ValleyAgent.Patches;
using ValleyAgent.WebSocket;
using Xunit;

namespace ValleyAgent.UnitTests.Multiplayer;

/// <summary>
///     死锁回归套件（2026-09-12 联机死锁审计）。
///     三个用例各自钉住一类"无异常、无日志、整机未响应"的活性缺陷：
///     <list type="number">
///     <item><description>
///         <b>主线程泵责任链饿死</b>（联机专属硬死锁）：房客的 ModMessage 发送泵
///         <c>HostRequestHandlers.ProcessMainThreadActions</c> 排在送礼泵下游，
///         而上游泵只吞 <c>InvalidOperationException</c>——直写 Game1 的动作抛 NRE 时异常逃出 while，
///         当 tick 下游全部泵被跳过 ⇒ 请求发不出去 ⇒ 回包永远不来 ⇒
///         <c>DialogueBoxInputPatch._isWaitingForResponse</c> 无人清除 ⇒ 输入框永久"等待回复"。
///     </description></item>
///     <item><description>
///         <b>等待标记陈旧自愈</b>：回包与超时兜底双双丢失时，等待标记必须能自己解除，
///         否则只有 ESC 能救（对话功能事实性死亡）。
///     </description></item>
///     <item><description>
///         <b>断连清理不得吃掉自动重连</b>：<c>PendingRequestTracker.FailAll</c> 与
///         <c>CompleteRequest</c> 并发时，<c>SetException</c> 会抛"Task already completed"，
///         该异常从 <c>WebSocketClient.ReadLoopAsync</c> 的 finally 逃出 ⇒
///         <c>ReconnectIndependentAsync()</c> 永不执行 ⇒ 断线后整个进程再也不会重连。
///     </description></item>
///     </list>
///     与同目录其它套件一致：全部在 Game1Statics 集合内串行执行（触碰进程级静态）。
/// </summary>
[Collection("Game1Statics")]
public sealed class DeadlockRegressionTests : IDisposable
{
    private readonly RecordingMonitor _monitor = new();

    public DeadlockRegressionTests()
    {
        NPCGiftPatch.Monitor = _monitor;
        DrainGiftQueue();
    }

    public void Dispose()
    {
        DrainGiftQueue();
        NPCGiftPatch.Monitor = null;
        DrainHostRequestQueue();
    }

    // ───────────────────────── 1. 泵责任链不得被单个消费者打断 ─────────────────────────

    /// <summary>
    ///     送礼泵里的动作抛 <see cref="KeyNotFoundException"/>（切图瞬间 NPC/好感键失效的真实形态）时，
    ///     异常必须被泵自己吃掉，且**同一 tick 内下游的房客发送泵必须照常排水**。
    /// </summary>
    [Fact]
    public void GiftPumpThrowingAction_DoesNotStarveFarmhandSendPump()
    {
        // 上游：一个必然抛非 InvalidOperationException 的送礼动作（修复前只有 InvalidOperationException 被吞）
        EnqueueGiftAction(() => throw new KeyNotFoundException("simulated: NPC/friendship key gone mid-warp"));
        EnqueueGiftAction(() => _monitor.Log("[Gift] second action still drained"));

        // 下游：房客唯一的 ModMessage 发送泵里排着一个待发动作
        var sendRan = false;
        HostRequestHandlers.EnqueueMainThread(() => sendRan = true);

        // 按 ModEntry / OnUpdateTicked 的隔离模式跑整条链：每个泵各自兜底
        var escaped = Record.Exception(() =>
        {
            Pump("gift-actions", NPCGiftPatch.ProcessMainThreadActions);
            Pump("host-request-mainthread", HostRequestHandlers.ProcessMainThreadActions);
        });

        Assert.Null(escaped);
        Assert.True(sendRan, "房客的 ModMessage 发送泵被上游送礼泵饿死——请求永远发不出去（联机硬死锁）");
        Assert.True(
            _monitor.CountContaining("second action still drained") > 0,
            "送礼泵抛异常后必须继续排干同队列的其余动作，不能整批丢弃");
        Assert.True(
            _monitor.CountContaining("MainThread action failed") > 0,
            "吞掉的异常必须留痕（可观测性铁律：降级不静默）");
    }

    /// <summary>
    ///     反证：把整条泵链塞进一个 try/catch（修复前 ModEntry.ThinClient 的写法）时，
    ///     上游泵抛异常确实会跳过下游发送泵——这就是死锁的成因，不是理论推演。
    /// </summary>
    [Fact]
    public void SingleTryCatchAroundWholePumpChain_StarvesDownstreamPump()
    {
        var sendRan = false;
        HostRequestHandlers.EnqueueMainThread(() => sendRan = true);

        // 修复前 NPCGiftPatch.ProcessMainThreadActions 的净效果：异常逃出 while 循环
        static void LeakingPump() => throw new KeyNotFoundException("pre-fix gift pump");

        var escaped = Record.Exception(() =>
        {
            try
            {
                LeakingPump();
                HostRequestHandlers.ProcessMainThreadActions();
            }
            catch (KeyNotFoundException)
            {
                // ModEntry 的单个 catch 只留痕，本 tick 已经结束
            }
        });

        Assert.Null(escaped);
        Assert.False(sendRan, "单个 try 串起整条链时，下游发送泵确实会被跳过——这正是必须逐泵隔离的原因");

        // 收尾：把没排掉的动作风干，避免污染同集合的其它用例
        DrainHostRequestQueue();
    }

    // ───────────────────────── 2. 等待标记必须能自愈 ─────────────────────────

    /// <summary>
    ///     回包永远不来（且 transport 的超时兜底也被饿死的泵丢掉）时，
    ///     <c>ResetStaleWait</c> 必须强制解除等待标记，把"永久不能输入的对话框"降级为"这轮没回上"。
    /// </summary>
    [Fact]
    public void StaleWaitFlag_SelfHealsAfterTimeout()
    {
        var patch = typeof(DialogueBoxInputPatch);
        const BindingFlags StaticPrivate = BindingFlags.NonPublic | BindingFlags.Static;

        var waitingField = patch.GetField("_isWaitingForResponse", StaticPrivate);
        var sinceField = patch.GetField("_waitingSinceMs", StaticPrivate);
        var timeoutField = patch.GetField("StaleWaitTimeoutMs", StaticPrivate);
        Assert.True(waitingField != null && sinceField != null && timeoutField != null,
            "DialogueBoxInputPatch 的等待标记字段被改名/删除——本用例是防永久卡死的唯一守卫，需同步维护");

        var timeoutMs = (long)timeoutField!.GetRawConstantValue()!;

        try
        {
            // 未超时：标记必须保持（不能把正常慢响应误判为卡死）
            waitingField!.SetValue(null, true);
            sinceField!.SetValue(null, Environment.TickCount64 - (timeoutMs / 2));
            DialogueBoxInputPatch.ResetStaleWait();
            Assert.True((bool)waitingField.GetValue(null)!, "未到陈旧上限就解锁会让正常慢响应被打断");

            // 已超时：必须解锁，且时间戳归零（否则下一轮立刻又被判陈旧）
            sinceField.SetValue(null, Environment.TickCount64 - (timeoutMs + 1_000));
            DialogueBoxInputPatch.ResetStaleWait();
            Assert.False((bool)waitingField.GetValue(null)!, "等待标记未被自愈——对话框将永久停在\"等待回复\"");
            Assert.Equal(0L, (long)sinceField.GetValue(null)!);
        }
        finally
        {
            waitingField!.SetValue(null, false);
            sinceField!.SetValue(null, 0L);
        }
    }

    // ───────────────────────── 3. 断连清理不得吃掉自动重连 ─────────────────────────

    /// <summary>
    ///     <c>FailAll</c> 与 <c>CompleteRequest</c> 并发（读循环收尾 vs 回包到达 / Dispose）时不得抛异常。
    ///     修复前 <c>SetException</c> 会对已完成的 TCS 抛 "A result was already set"，
    ///     异常从 <c>ReadLoopAsync</c> 的 finally 逃出，重连分支永远走不到。
    /// </summary>
    [Fact]
    public void FailAll_RacingWithCompletion_NeverThrows()
    {
        var tracker = new PendingRequestTracker();
        var ids = Enumerable.Range(0, 64).Select(_ => tracker.RegisterRequest()).ToList();

        var escaped = Record.Exception(() =>
        {
            Parallel.For(0, 8, worker =>
            {
                for (var round = 0; round < 40; round++)
                {
                    tracker.FailAll(new InvalidOperationException("WebSocket disconnected"));
                    _ = tracker.CompleteRequest(ids[round % ids.Count], "{}");
                }
            });
        });

        Assert.Null(escaped);
    }

    /// <summary>
    ///     <c>FailAll</c> 之后不得留下"既没成功也没失败"的条目——那种条目的等待方
    ///     在外部 token 可取消时没有自动超时分支，会一直挂到调用方自己取消为止。
    /// </summary>
    [Fact]
    public async Task FailAll_LeavesNoUnfaultedPendingEntries()
    {
        var tracker = new PendingRequestTracker();
        var id = tracker.RegisterRequest();

        tracker.FailAll(new InvalidOperationException("WebSocket disconnected"));

        // 条目已被摘除：再等它只会得到 "No pending request"，而不是无限期挂起
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => tracker.WaitForResponseAsync(id));
        Assert.Contains("No pending request", ex.Message, StringComparison.Ordinal);
    }

    // ───────────────────────── 测试基建 ─────────────────────────

    /// <summary>
    ///     生产侧 <c>ModEntry.Pump</c> / <c>EventHandlerInitializer.Pump</c> 的同构镜像：
    ///     每个泵独立兜底，单泵失败不断链。用例通过它跑泵，等价于跑生产时序。
    /// </summary>
    private void Pump(string name, Action pump)
    {
        try
        {
            pump();
        }
        catch (Exception ex)
        {
            _monitor.Log($"[Pump] '{name}' failed this tick (downstream pumps unaffected): {ex}", LogLevel.Error);
        }
    }

    private static ConcurrentQueue<Action> GiftQueue()
    {
        var field = typeof(NPCGiftPatch).GetField("_mainThreadActions", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.True(field != null, "NPCGiftPatch._mainThreadActions 被改名——本用例依赖它注入故障动作");
        return (ConcurrentQueue<Action>)field!.GetValue(null)!;
    }

    private static void EnqueueGiftAction(Action action) => GiftQueue().Enqueue(action);

    private static void DrainGiftQueue()
    {
        var queue = GiftQueue();
        while (queue.TryDequeue(out _))
        {
        }
    }

    private static void DrainHostRequestQueue()
    {
        // 没有公开的清空入口：借泵排干（泵自己兜底，故障动作不会逃出来）
        HostRequestHandlers.ProcessMainThreadActions();
    }
}
