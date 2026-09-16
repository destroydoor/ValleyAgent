using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StardewModdingAPI;
using StardewValley;
using ValleyAgent.WebSocket;

namespace ValleyAgent.Multiplayer.Transports;

/// <summary>
///     Farmhand 端对话传输：通过 ModMessage 把请求转发给主机，等待 DialogueResponseMessage 回包。
///     主机端由 HostRequestHandlers.HandleDialogueRequest 处理。
/// </summary>
public class FarmhandDialogueTransport : IDialogueTransport
{
    private readonly IModHelper _helper;
    private readonly string _modId;
    private readonly IMonitor _monitor;

    // 等待回包的 TaskCompletionSource 队列，按 npcName + requestId 索引
    private readonly ConcurrentDictionary<string, TaskCompletionSource<DialogueResponse>> _pending = new();
    private readonly TimeSpan _timeout;

    public FarmhandDialogueTransport(IModHelper helper, IMonitor monitor, string modId, TimeSpan? timeout = null)
    {
        _helper = helper ?? throw new ArgumentNullException(nameof(helper));
        _monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        _modId = modId ?? throw new ArgumentNullException(nameof(modId));
        _timeout = timeout ?? TimeSpan.FromSeconds(60);
    }

    public async Task<DialogueResponse> SendAsync(string npcName, string playerInput, WorldSnapshot worldSnapshot,
        CancellationToken ct = default)
    {
        // 1. 序列化 worldSnapshot 到 JSON
        string worldSnapshotJson;
        try
        {
            worldSnapshotJson = JsonSerializer.Serialize(worldSnapshot);
        }
        catch (Exception ex)
        {
            _monitor.Log($"[FarmhandDialogueTransport] Failed to serialize WorldSnapshot: {ex}", LogLevel.Error);
            return BuildFallbackResponse(npcName, "（场景数据序列化失败）");
        }

        // 2. 注册 TaskCompletionSource 等待回包（同步完成，先于发送——回包不会早于请求到达）
        var requestId = $"{npcName}_{Guid.NewGuid():N}";
        var tcs = new TaskCompletionSource<DialogueResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[requestId] = tcs;

        // 3. 通过 ModMessage 发送到主机。本方法运行在调用方的 Task.Run 后台线程上，
        // 而 Game1 静态读取与 SMAPI 底层消息队列都不是线程安全的（2026-09-09 缺口③）——
        // 构造消息与 SendMessage 整体投递主线程执行（房客 UpdateTicked 每 tick 排水）。
        // 发送失败经 TCS 完成 fallback，调用方 await 语义不变。
        HostRequestHandlers.EnqueueMainThread(() =>
        {
            try
            {
                var masterId = Game1.MasterPlayer?.UniqueMultiplayerID ?? 0;
                var msg = new DialogueRequestMessage
                {
                    NpcName = npcName,
                    PlayerMessage = playerInput,
                    PlayerId = Game1.player.UniqueMultiplayerID,
                    WorldSnapshotJson = worldSnapshotJson,
                    RequestId = requestId // 主机原样回填，房客 HandleResponse 精确配对
                };
                _monitor.Log(
                    $"[FarmhandDialogueTransport] Sending request {requestId} for {npcName} to master player {masterId}",
                    LogLevel.Debug);
                _helper.Multiplayer.SendMessage(
                    msg,
                    MessageTypes.DialogueRequest,
                    new[] { _modId },
                    new[] { masterId });
            }
            catch (Exception ex)
            {
                _pending.TryRemove(requestId, out _);
                _monitor.Log($"[FarmhandDialogueTransport] Failed to send request: {ex}", LogLevel.Error);
                tcs.TrySetResult(BuildFallbackResponse(npcName, "网络错误，无法联系主机"));
            }
        });

        // 5. 等待回包或超时
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_timeout);
        try
        {
            using var reg = cts.Token.Register(() => tcs.TrySetCanceled());
            return await tcs.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException ex)
        {
            _pending.TryRemove(requestId, out _);
            _monitor.Log($"[FarmhandDialogueTransport] Request {requestId} timed out ({ex})", LogLevel.Warn);
            return BuildFallbackResponse(npcName, "（主机响应超时）");
        }
    }

    /// <summary>
    ///     由 MultiplayerEventRouter 在收到 DialogueResponseMessage 时调用，完成 pending 请求。
    ///     新协议（2026-09-09）：msg.RequestId 非空 → 精确 TryRemove 对应 pending；
    ///     pending 已清理（请求超时）→ 迟到回包直接丢弃，绝不回退 FIFO——否则会窃取重试请求的回包。
    ///     msg.RequestId 为空（旧版本主机）→ 退回按 npcName 前缀取最早 pending 的 FIFO 兼容策略。
    /// </summary>
    public void HandleResponse(DialogueResponseMessage msg)
    {
        if (!string.IsNullOrEmpty(msg.RequestId))
        {
            if (_pending.TryRemove(msg.RequestId, out var exact))
            {
                Complete(msg.NpcName, msg, exact);
            }
            else
            {
                _monitor.Log(
                    $"[FarmhandDialogueTransport] Response requestId {msg.RequestId} for {msg.NpcName} has no pending (timed out or already served), dropping late response");
            }

            return;
        }

        // 旧主机兼容：取该 NPC 的第一个 pending（FIFO）
        var matchingKey =
            _pending.Keys.FirstOrDefault(k => k.StartsWith(msg.NpcName + "_", StringComparison.OrdinalIgnoreCase));
        if (matchingKey == null)
        {
            _monitor.Log($"[FarmhandDialogueTransport] No pending request for {msg.NpcName}, dropping response");
            return;
        }

        if (_pending.TryRemove(matchingKey, out var tcs))
        {
            Complete(msg.NpcName, msg, tcs);
        }
    }

    private void Complete(string npcName, DialogueResponseMessage msg, TaskCompletionSource<DialogueResponse> tcs)
    {
        var actions = ParseActions(msg.ActionsJson ?? msg.Action) ?? new List<ToolAction>();
        // 2026-09-13 R3：FallbackReason 必须随广播回包落回本地 record，
        // 否则房客侧最后一跳丢字段，灰字诊断留痕（busy vs LLM 故障）在房客端失明。
        var response = new DialogueResponse(
            msg.Text ?? "",
            actions,
            msg.Emotion ?? "Neutral",
            Fallback: msg.Fallback,
            FallbackReason: msg.FallbackReason
        );
        tcs.TrySetResult(response);
    }

    private static List<ToolAction>? ParseActions(string? actionsJsonOrSingle)
    {
        if (string.IsNullOrEmpty(actionsJsonOrSingle))
        {
            return null;
        }

        // ActionsJson 是数组 JSON
        if (actionsJsonOrSingle.TrimStart().StartsWith("["))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<List<ToolAction>>(actionsJsonOrSingle);
                return parsed ?? new List<ToolAction>();
            }
            catch
            {
                return null;
            }
        }

        // 旧版单 Action 字段
        return new List<ToolAction> { new(actionsJsonOrSingle, new Dictionary<string, object>()) };
    }

    private static DialogueResponse BuildFallbackResponse(string npcName, string speech)
    {
        _ = npcName; // 占位，保留参数以备将来按 NPC 区分 fallback 文案
        return new DialogueResponse(
            speech,
            new List<ToolAction>()
        );
    }
}