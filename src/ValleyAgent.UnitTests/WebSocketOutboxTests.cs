using ValleyAgent.WebSocket;
using Xunit;

namespace ValleyAgent.UnitTests;

/// <summary>
///     验证 WebSocketClient 的 outbox 断线补发机制：
///     - 断连期间 fire-and-forget 消息入队而非静默丢弃
///     - 超限时丢弃最旧消息防止内存溢出
/// </summary>
public class WebSocketOutboxTests
{
    [Fact]
    public async Task SendMessageAsync_WhenDisconnected_QueuesToOutbox()
    {
        using var client = new WebSocketClient("ws://127.0.0.1:1/", "test-npc");

        // 未连接时 SendMessageAsync 应入队而非抛异常
        await client.SendMessageAsync("{\"type\":\"ping\"}", CancellationToken.None);
        await client.SendMessageAsync("{\"type\":\"state_changed\"}", CancellationToken.None);

        Assert.Equal(2, client.OutboxCount);
    }

    [Fact]
    public async Task SendMessageAsync_OutboxOverflow_DropsOldest_AndLogsDrop()
    {
        using var client = new WebSocketClient("ws://127.0.0.1:1/", "test-npc");
        var dropLogs = new List<string>();
        client.LogCallback = dropLogs.Add;

        // 超过 MaxOutboxSize (100) 时应丢弃最旧消息
        for (var i = 0; i < 150; i++)
        {
            await client.SendMessageAsync($"{{\"type\":\"msg\",\"i\":{i}}}", CancellationToken.None);
        }

        // 队列应被截断到 MaxOutboxSize
        Assert.Equal(100, client.OutboxCount);
        // 降级不静默：每次丢弃必须留痕
        Assert.Equal(50, dropLogs.Count);
        Assert.All(dropLogs, l => Assert.Contains("outbox overflow", l));
    }

    [Fact]
    public async Task SendMessageAsync_WhenDisconnected_DoesNotThrow()
    {
        using var client = new WebSocketClient("ws://127.0.0.1:1/", "test-npc");

        // 断连时不应抛异常（入队后正常返回）
        var ex = await Record.ExceptionAsync(() =>
            client.SendMessageAsync("{\"type\":\"test\"}", CancellationToken.None));
        Assert.Null(ex);
    }

    [Fact]
    public async Task IsConnectedAsync_WhenNotConnected_ReturnsFalse()
    {
        using var client = new WebSocketClient("ws://127.0.0.1:1/", "test-npc");

        var connected = await client.IsConnectedAsync(CancellationToken.None);
        Assert.False(connected);
    }

    [Fact]
    public async Task SendMessageAsync_MultipleMessages_AllQueued()
    {
        using var client = new WebSocketClient("ws://127.0.0.1:1/", "test-npc");

        for (var i = 0; i < 50; i++)
        {
            await client.SendMessageAsync($"{{\"i\":{i}}}", CancellationToken.None);
        }

        Assert.Equal(50, client.OutboxCount);
    }
}