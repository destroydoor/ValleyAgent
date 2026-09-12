#nullable enable
using System;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using StardewModdingAPI;
using ValleyAgent.TestMod.Infrastructure;
using ValleyAgent.TestMod.Mock;
using ValleyAgent.WebSocket;

namespace ValleyAgent.TestMod.Tests.Pipeline;

/// <summary>
///     Verifies the state-sync pipeline: a one-way action_result message sent
///     over WebSocket is received by the mock server, increments MessageCount,
///     and is recorded in ReceivedMessageTypes. The server sends an error
///     response for unknown message types, which is also consumed.
/// </summary>
[RegisteredTest(TestGroup.Pipeline, "状态同步管道验证", "pipeline")]
public class PIPE004_StateSyncPipeline : V3TestBase
{
    private const int Port = 8804;
    private const int ConnectTimeoutMs = 3000;
    private const int SendTimeoutMs = 3000;
    private const int ReceiveTimeoutMs = 3000;
    private const int ReceiveBufferSize = 8192;
    private readonly string _requestId = Guid.NewGuid().ToString("N");
    private ClientWebSocket? _client;
    private bool _clientConnected;
    private int _messageCountBefore;
    private bool _messageSent;

    private MockWebSocketServer? _server;

    public PIPE004_StateSyncPipeline(IModHelper helper, IMonitor monitor) : base(helper, monitor)
    {
    }

    public override string TestName
    {
        get => "PIPE004_StateSyncPipeline";
    }

    public override int TimeoutTicks
    {
        get => 3000;
    }

    public override TestGroup Group
    {
        get => TestGroup.Pipeline;
    }

    public override void Setup()
    {
        var library = MockResponseLibrary.CreateDefault();
        var provider = new MockLLMProvider(library);
        _server = new MockWebSocketServer(provider);
        try
        {
            _server.Start(Port);
            Monitor.Log($"[PIPE004] Mock server started on port {Port}.", LogLevel.Info);
        }
        catch (HttpListenerException ex)
        {
            Skip($"Failed to start mock server on port {Port}: {ex.Message}");
            _server.Dispose();
            _server = null;
        }
        catch (InvalidOperationException ex)
        {
            Skip($"Server already running: {ex.Message}");
            _server.Dispose();
            _server = null;
        }
    }

    public override bool Update()
    {
        if (_server == null)
        {
            return true;
        }

        // Phase 1: verify server started.
        if (CurrentTick == 30)
        {
            Assert("ws_server_started", _server.IsRunning, $"IsRunning={_server.IsRunning}");
        }

        // Phase 2: connect a test client.
        if (CurrentTick == 60 && _client == null)
        {
            _client = new ClientWebSocket();
            try
            {
                using var cts = new CancellationTokenSource(ConnectTimeoutMs);
                _client.ConnectAsync(new Uri($"ws://127.0.0.1:{Port}/"), cts.Token)
                    .GetAwaiter().GetResult();
                _clientConnected = _client.State == WebSocketState.Open;
            }
            catch (WebSocketException ex)
            {
                Monitor.Log($"[PIPE004] Connect failed: {ex.Message}", LogLevel.Warn);
                _clientConnected = false;
            }
            catch (OperationCanceledException ex)
            {
                Monitor.Log($"[PIPE004] Connect timed out: {ex.Message}", LogLevel.Warn);
                _clientConnected = false;
            }

            Assert("ws_client_connected", _clientConnected, $"client state={_client?.State}");
        }

        // Phase 3: send action_result (state sync) message.
        if (CurrentTick == 120 && _client != null && _client.State == WebSocketState.Open && !_messageSent)
        {
            _messageCountBefore = _server.MessageCount;
            var request = new
            {
                type = "action_result",
                requestId = _requestId,
                npcName = "Haley",
                action = "farm_harvest",
                result = "success",
                tile = new { x = 32, y = 30 },
                timestamp = "2026-07-17T12:00:00"
            };
            var json = MessageProtocol.Serialize(request);
            try
            {
                using var cts = new CancellationTokenSource(SendTimeoutMs);
                var bytes = Encoding.UTF8.GetBytes(json);
                _client.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cts.Token)
                    .GetAwaiter().GetResult();
                _messageSent = true;
                Assert("state_sync_sent", true, $"requestId={_requestId} before={_messageCountBefore}");
            }
            catch (WebSocketException ex)
            {
                Assert("state_sync_sent", false, $"SendAsync failed: {ex.Message}");
            }
            catch (OperationCanceledException ex)
            {
                Assert("state_sync_sent", false, $"SendAsync timed out: {ex.Message}");
            }
        }

        // Phase 4: drain the server's error response and verify receipt.
        if (CurrentTick == 180 && _messageSent && _client != null)
        {
            // The server sends an error response for the unknown action_result type.
            // Drain it so the receive buffer does not back up; ignore the content.
            _ = TryReceiveJson(_client);

            var messageCountAfter = _server.MessageCount;
            var receivedTypes = _server.ReceivedMessageTypes;
            var countIncremented = messageCountAfter > _messageCountBefore;
            Assert("state_sync_received", countIncremented,
                $"before={_messageCountBefore} after={messageCountAfter}");

            var typeRecorded = receivedTypes.Contains("action_result");
            Assert("state_data_complete", typeRecorded,
                $"types=[{string.Join(",", receivedTypes)}]");
        }

        return CurrentTick >= 300;
    }

    public override void Teardown()
    {
        DisposeClient();
        DisposeServer();
        Monitor.Log("[PIPE004] Teardown.", LogLevel.Info);
    }

    private static string? TryReceiveJson(ClientWebSocket client)
    {
        if (client.State != WebSocketState.Open)
        {
            return null;
        }

        var buffer = new byte[ReceiveBufferSize];
        var sb = new StringBuilder();
        using var cts = new CancellationTokenSource(ReceiveTimeoutMs);
        try
        {
            WebSocketReceiveResult result;
            do
            {
                result = client.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token)
                    .GetAwaiter().GetResult();
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return null;
                }

                _ = sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            } while (!result.EndOfMessage);

            return sb.ToString();
        }
        catch (WebSocketException)
        {
            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    private void DisposeClient()
    {
        if (_client == null)
        {
            return;
        }

        try
        {
            if (_client.State == WebSocketState.Open)
            {
                using var cts = new CancellationTokenSource(1000);
                _client.CloseAsync(WebSocketCloseStatus.NormalClosure, "test done", cts.Token)
                    .GetAwaiter().GetResult();
            }
        }
        catch (WebSocketException)
        {
            /* ignore */
        }
        catch (OperationCanceledException)
        {
            /* ignore */
        }

        _client.Dispose();
        _client = null;
    }

    private void DisposeServer()
    {
        if (_server == null)
        {
            return;
        }

        try
        {
            _server.StopAsync().GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            /* ignore */
        }
        catch (AggregateException)
        {
            /* ignore */
        }
        catch (InvalidOperationException)
        {
            /* ignore */
        }

        _server.Dispose();
        _server = null;
    }
}