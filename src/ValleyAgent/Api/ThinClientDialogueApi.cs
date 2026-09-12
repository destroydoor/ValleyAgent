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

        _ = Task.Run(async () =>
        {
            try
            {
                var worldSnapshot = WorldSnapshotBuilder.Build(npcName);
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