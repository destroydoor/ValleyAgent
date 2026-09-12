using System.Threading;
using System.Threading.Tasks;
using ValleyAgent.WebSocket;

namespace ValleyAgent.Multiplayer.Transports;

/// <summary>
///     对话传输抽象。主机端直连 IAgentServerProvider；farmhand 端通过 ModMessage 代理到主机。
/// </summary>
public interface IDialogueTransport
{
    /// <summary>
    ///     发起对话请求。
    /// </summary>
    /// <param name="npcName">NPC 名称。</param>
    /// <param name="playerInput">玩家输入文本。</param>
    /// <param name="worldSnapshot">场景上下文（已采集）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>对话响应（speech 文本与可选 actions）。</returns>
    public Task<DialogueResponse> SendAsync(string npcName, string playerInput, WorldSnapshot worldSnapshot,
        CancellationToken ct = default);
}