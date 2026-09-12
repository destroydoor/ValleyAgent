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
///     Verifies the mock WebSocket server starts, accepts a client connection,
///     and receives a hello handshake message. Tests the server itself rather
///     than the main ValleyAgent mod's client behaviour.
/// </summary>
[RegisteredTest(TestGroup.Pipeline, "WebSocket 连接验证", "pipeline")]
public class PIPE001_WebSocketConnection : V3TestBase
{
    private const int Port = 8801;
    private const int ConnectTimeoutMs = 3000;
    private const int SendTimeoutMs = 3000;
    private readonly string _requestId = Guid.NewGuid().ToString("N");
    private ClientWebSocket? _client;
    private bool _clientConnected;
    private bool _helloReceived;
    private bool _helloSent;

    private MockWebSocketServer? _server;

    public PIPE001_WebSocketConnection(IModHelper helper, IMonitor monitor) : base(helper, monitor)
    {
    }

    public override string TestName
    {
        get => "PIPE001_WebSocketConnection";
    }

    public override int TimeoutTicks
    {
        get => 2000;
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
            Monitor.Log($"[PIPE001] Mock server started on port {Port}.", LogLevel.Info);
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
                Monitor.Log($"[PIPE001] Connect failed: {ex.Message}", LogLevel.Warn);
                _clientConnected = false;
            }
            catch (OperationCanceledException ex)
            {
                Monitor.Log($"[PIPE001] Connect timed out: {ex.Message}", LogLevel.Warn);
                _clientConnected = false;
            }

            Assert("ws_server_listening", _clientConnected, $"client state={_client?.State}");
        }

        // Phase 3: send hello message.
        if (CurrentTick == 90 && _client != null && _client.State == WebSocketState.Open && !_helloSent)
        {
            var hello = new
            {
                type = "hello",
                requestId = _requestId,
                npcName = "Haley",
                version = "2.0"
            };
            var json = MessageProtocol.Serialize(hello);
            try
            {
                using var cts = new CancellationTokenSource(SendTimeoutMs);
                var bytes = Encoding.UTF8.GetBytes(json);
                _client.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cts.Token)
                    .GetAwaiter().GetResult();
                _helloSent = true;
            }
            catch (WebSocketException ex)
            {
                Monitor.Log($"[PIPE001] SendAsync failed: {ex.Message}", LogLevel.Warn);
            }
            catch (OperationCanceledException ex)
            {
                Monitor.Log($"[PIPE001] SendAsync timed out: {ex.Message}", LogLevel.Warn);
            }
        }

        // Phase 4: verify server received hello.
        if (CurrentTick == 150)
        {
            var receivedTypes = _server.ReceivedMessageTypes;
            var lastNpc = _server.LastNpcName;
            _helloReceived = receivedTypes.Contains("hello") && lastNpc == "Haley";
            Assert("ws_hello_received", _helloReceived,
                $"types=[{string.Join(",", receivedTypes)}] npc={lastNpc}");
        }

        return CurrentTick >= 300;
    }

    public override void Teardown()
    {
        DisposeClient();
        DisposeServer();
        Monitor.Log("[PIPE001] Teardown.", LogLevel.Info);
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