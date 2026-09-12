using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using StardewModdingAPI;
using StardewValley;

namespace ValleyAgent.Multiplayer.Transports;

/// <summary>
///     Farmhand 端送礼传输：通过 ModMessage 把礼物请求转发给主机，等待 GiftResponseMessage 回包。
///     主机端由 HostRequestHandlers.HandleGiftRequest 处理（调用 HostGiftTransport 评估）。
///     与 FarmhandDialogueTransport 一致：按 npcName + requestId 前缀 FIFO 匹配 pending 请求。
/// </summary>
public class FarmhandGiftTransport : IGiftTransport
{
    private readonly IModHelper _helper;
    private readonly string _modId;
    private readonly IMonitor _monitor;

    // 等待回包的 TaskCompletionSource 队列，按 npcName + requestId 索引
    private readonly ConcurrentDictionary<string, TaskCompletionSource<GiftReactionResult>> _pending = new();
    private readonly TimeSpan _timeout;

    /// <summary>构造函数。注入 SMAPI ModHelper（用于 ModMessage）、Monitor、modId 与可选超时。</summary>
    public FarmhandGiftTransport(IModHelper helper, IMonitor monitor, string modId, TimeSpan? timeout = null)
    {
        _helper = helper ?? throw new ArgumentNullException(nameof(helper));
        _monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        _modId = modId ?? throw new ArgumentNullException(nameof(modId));
        _timeout = timeout ?? TimeSpan.FromSeconds(60);
    }

    /// <summary>
    ///     发起送礼评估请求：构造 GiftRequestMessage → ModMessage 发送给主机 → 等待 GiftResponseMessage 回包。
    ///     超时（默认 60s）返回 fallback 响应，避免 farmhand 长时间阻塞。
    /// </summary>
    public async Task<GiftReactionResult> SendAsync(string npcName, string itemId, int quantity,
        long requesterPlayerId = 0, CancellationToken ct = default)
    {
        _ = requesterPlayerId; // farmhand 端发起者恒为本机玩家，PlayerId 直接取 Game1.player

        // 2. 注册 TaskCompletionSource 等待回包（同步完成，先于发送——回包不会早于请求到达）
        var requestId = $"{npcName}_{Guid.NewGuid():N}";
        var tcs = new TaskCompletionSource<GiftReactionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[requestId] = tcs;

        // 3. 通过 ModMessage 发送到主机。调用方（NPCGiftPatch 房客分支）在 Task.Run 后台线程上
        // 发起，而 Game1 静态读取与 SMAPI 底层消息队列都不是线程安全的（2026-09-09 缺口③）——
        // 构造消息与 SendMessage 整体投递主线程执行（房客 UpdateTicked 每 tick 排水）。
        // 发送失败经 TCS 完成 fallback，调用方 await 语义不变。
        HostRequestHandlers.EnqueueMainThread(() =>
        {
            try
            {
                var msg = new GiftRequestMessage
                {
                    NpcName = npcName,
                    ItemId = itemId,
                    Quantity = quantity,
                    PlayerId = Game1.player.UniqueMultiplayerID,
                    RequestId = requestId // 主机原样回填，房客 HandleResponse 精确配对
                };
                _helper.Multiplayer.SendMessage(
                    msg,
                    MessageTypes.GiftRequest,
                    new[] { _modId },
                    new[] { Game1.MasterPlayer.UniqueMultiplayerID });
            }
            catch (Exception ex)
            {
                _pending.TryRemove(requestId, out _);
                _monitor.Log($"[FarmhandGiftTransport] Failed to send request: {ex}", LogLevel.Error);
                tcs.TrySetResult(BuildFallbackResponse("网络错误，无法联系主机"));
            }
        });

        // 4. 等待回包或超时
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_timeout);
        try
        {
            using var reg = cts.Token.Register(() => tcs.TrySetCanceled());
            return await tcs.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _pending.TryRemove(requestId, out _);
            _monitor.Log($"[FarmhandGiftTransport] Request {requestId} timed out", LogLevel.Warn);
            return BuildFallbackResponse("（主机响应超时）");
        }
    }

    /// <summary>
    ///     由 MultiplayerEventRouter 在收到 GiftResponseMessage 时调用，完成 pending 请求。
    ///     语义与 FarmhandDialogueTransport.HandleResponse 一致（2026-09-09）：
    ///     msg.RequestId 非空 → 精确匹配，pending 已清理则迟到回包丢弃（防好感 delta 张冠李戴）；
    ///     为空（旧版本主机）→ 按 npcName 前缀 FIFO 兼容匹配。
    /// </summary>
    public void HandleResponse(GiftResponseMessage msg)
    {
        if (!string.IsNullOrEmpty(msg.RequestId))
        {
            if (_pending.TryRemove(msg.RequestId, out var exact))
            {
                Complete(msg, exact);
            }
            else
            {
                _monitor.Log(
                    $"[FarmhandGiftTransport] Response requestId {msg.RequestId} for {msg.NpcName} has no pending, dropping late response");
            }

            return;
        }

        var matchingKey =
            _pending.Keys.FirstOrDefault(k => k.StartsWith(msg.NpcName + "_", StringComparison.OrdinalIgnoreCase));
        if (matchingKey == null)
        {
            _monitor.Log($"[FarmhandGiftTransport] No pending request for {msg.NpcName}, dropping response");
            return;
        }

        if (_pending.TryRemove(matchingKey, out var tcs))
        {
            Complete(msg, tcs);
        }
    }

    private static void Complete(GiftResponseMessage msg, TaskCompletionSource<GiftReactionResult> tcs)
    {
        var response = new GiftReactionResult(
            "",
            msg.FriendshipChange,
            msg.ResponseText ?? "",
            msg.Emotion ?? "Neutral");
        tcs.TrySetResult(response);
    }

    private static GiftReactionResult BuildFallbackResponse(string reaction)
    {
        return new GiftReactionResult(
            "",
            0,
            reaction);
    }
}