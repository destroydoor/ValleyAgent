using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using StardewModdingAPI;
using ValleyAgent.AI;
using ValleyAgent.Brain;
using ValleyAgent.Multiplayer.Transports;
using ValleyAgent.WebSocket;

namespace ValleyAgent.Api;

/// <summary>
///     Farmhand（ThinClient）模式下的对话 API 实现。
///     不依赖 AgentService，直接把请求转发给 <see cref="IDialogueTransport" />（即 FarmhandDialogueTransport），
///     由主机处理 LLM 并执行 Actions，再把响应通过 ModMessage 广播回 farmhand。
/// </summary>
public sealed class ThinClientDialogueApi
{
    // 每个 NPC 最近一次响应文本与来源
    private readonly ConcurrentDictionary<string, DialogueResponse> _lastResponses =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, DialogueResponseSource> _lastSources =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly IMonitor? _monitor;

    // 防止同一 NPC 并发请求导致 pending 队列膨胀
    private readonly ConcurrentDictionary<string, byte> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly IDialogueTransport _transport;

    public ThinClientDialogueApi(IDialogueTransport transport, IMonitor? monitor)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _monitor = monitor;
    }

    /// <summary>
    ///     向主机发起对话请求。立即返回 true 表示已入队后台发送；farmhand 通过 <see cref="TryGetLastDialogue" /> 轮询响应。
    /// </summary>
    public bool TryGenerateDialogue(string npcName, string playerInput)
    {
        if (!_pending.TryAdd(npcName, 0))
        {
            _monitor?.Log($"[ThinClientDialogueApi] {npcName}: request already pending, skipped", LogLevel.Debug);
            return false;
        }

        _lastResponses.TryRemove(npcName, out _);
        _lastSources.TryRemove(npcName, out _);

        // 死锁/冻结修复（2026-09-12）：快照采集必须在**调用方线程（主线程）**完成。
        // WorldSnapshotBuilder.Build 裸读 Game1.getCharacterFromName（NetList 遍历）、
        // location.characters / location.Objects.Values / location.warps、
        // Game1.player.Items / friendshipData / Money——这些集合与 NetField 都不是线程安全的。
        // 原来整段落在 Task.Run 的 ThreadPool 线程上，与主线程每 tick 的读写并发：
        // Dictionary/NetList 扩容竞争会把桶链写成环，主线程随后的 TryGetValue/遍历
        // 就变成**无异常、无日志的死循环**（= 2026-09-10 结案文档"候选 2"的机理，
        // 而这条路径在房客端每次对话必走，是热路径而非冷路径）。
        // 与 DialogueBoxInputPatch.SubmitInput 的"缺口③b"纪律对齐：主线程采快照，后台只等网络。
        WorldSnapshot worldSnapshot;
        try
        {
            worldSnapshot = WorldSnapshotBuilder.Build(npcName);
        }
        catch (Exception ex)
        {
            _pending.TryRemove(npcName, out _);
            _monitor?.Log($"[ThinClientDialogueApi] {npcName}: snapshot build failed: {ex}",
                LogLevel.Error);
            _lastResponses[npcName] = new DialogueResponse(
                "（场景数据采集失败）", new List<ToolAction>(), "Neutral", string.Empty, string.Empty);
            _lastSources[npcName] = DialogueResponseSource.Error;
            return false;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var response = await _transport.SendAsync(npcName, playerInput, worldSnapshot).ConfigureAwait(false);

                _lastResponses[npcName] = response;
                _lastSources[npcName] = DialogueResponseSource.LLM;

                var preview = (response.Speech ?? string.Empty).Length > 60
                    ? response.Speech![..60] + "..."
                    : response.Speech ?? string.Empty;
                _monitor?.Log($"[ThinClientDialogueApi] {npcName}: response received — {preview}", LogLevel.Debug);
            }
            catch (Exception ex)
            {
                _monitor?.Log($"[ThinClientDialogueApi] {npcName}: request failed — {ex.GetType().Name}: {ex.Message}",
                    LogLevel.Error);
                _lastResponses[npcName] = new DialogueResponse(
                    "（对话请求失败）",
                    new List<ToolAction>(),
                    "Neutral",
                    string.Empty,
                    string.Empty);
                _lastSources[npcName] = DialogueResponseSource.Error;
            }
            finally
            {
                _pending.TryRemove(npcName, out _);
            }
        });

        return true;
    }

    public bool TryGetLastDialogue(string npcName, out string response)
    {
        response = string.Empty;
        if (!_lastResponses.TryGetValue(npcName, out var value) || value == null)
        {
            return false;
        }

        response = value.Speech ?? string.Empty;
        return !string.IsNullOrEmpty(response);
    }

    public bool TryGetLastDialogueSource(string npcName, out DialogueResponseSource source)
    {
        if (_lastSources.TryGetValue(npcName, out source))
        {
            return true;
        }

        source = DialogueResponseSource.None;
        return false;
    }
}