using System;
using System.Threading;
using System.Threading.Tasks;

namespace ValleyAgent.WebSocket;

/// <summary>
///     WebSocket-only provider: all requests go through WebSocketClient.
///     No fallback — if WebSocket is unavailable, throws InvalidOperationException.
/// </summary>
public class DualPathAgentServerProvider : IAgentServerProvider, IDisposable
{
    private bool _disposed;

    public DualPathAgentServerProvider(WebSocketClient client)
    {
        WebSocketClient = client ?? throw new ArgumentNullException(nameof(client));
    }

    public Action<string, bool>? LogCallback { get; set; }

    /// <summary>
    ///     2026-08-23 审计 P1：每次对话响应到达后回调 (npcName, 发起玩家Id)。
    ///     此前 LastDialoguePlayerId 只有房客中继路径会写，本地对话（对话框/聊天栏/送礼附言）从不更新，
    ///     多人下 FOLLOW 的"最近对话发起玩家"语义破损——TS echo 的 playerId 在此统一消费。
    ///     回调发生在后台续体，订阅方需自行保证线程安全。
    /// </summary>
    public Action<string, string?>? DialogueCompleted { get; set; }

    public WebSocketClient WebSocketClient { get; }

    public event Action? OnConnected
    {
        add => WebSocketClient.OnConnected += value;
        remove => WebSocketClient.OnConnected -= value;
    }

    public event Action? OnDisconnected
    {
        add => WebSocketClient.OnDisconnected += value;
        remove => WebSocketClient.OnDisconnected -= value;
    }

    /// <summary>重连成功（2026-08-15 步骤 4）：转发 WebSocketClient.OnReconnected（参数=outbox 补发数）。</summary>
    public event Action<int>? OnReconnected
    {
        add => WebSocketClient.OnReconnected += value;
        remove => WebSocketClient.OnReconnected -= value;
    }

    public Task<bool> IsConnectedAsync(CancellationToken ct = default) => WebSocketClient.IsConnectedAsync(ct);

    public async Task<DialogueResponse> GenerateDialogueAsync(DialogueRequest request, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        var response = await WebSocketClient.GenerateDialogueAsync(request, ct).ConfigureAwait(false);

        var initiator = string.IsNullOrEmpty(response.PlayerId) ? request.PlayerId : response.PlayerId;
        if (!string.IsNullOrEmpty(initiator))
        {
            DialogueCompleted?.Invoke(request.NpcName, initiator);
        }

        return response;
    }

    public async Task SendMessageAsync(string jsonMessage, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        await WebSocketClient.SendMessageAsync(jsonMessage, ct).ConfigureAwait(false);
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    public async Task ConnectAsync(CancellationToken ct = default) =>
        await WebSocketClient.ConnectAsync(ct).ConfigureAwait(false);

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(DualPathAgentServerProvider));
        }
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        if (disposing)
        {
            WebSocketClient.Dispose();
        }

        _disposed = true;
    }
}