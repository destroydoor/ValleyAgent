#nullable enable
using System;
using System.IO;
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
///     Verifies the dialogue pipeline: a dialogue request sent over WebSocket
///     is processed by the mock server and a valid DialogueResponse with a
///     non-empty Text field is returned to the client.
/// </summary>
[RegisteredTest(TestGroup.Pipeline, "对话管道验证", "pipeline")]
public class PIPE003_DialoguePipeline : V3TestBase
{
    private const int Port = 8803;
    private const int ConnectTimeoutMs = 3000;
    private const int SendTimeoutMs = 3000;
    private const int ReceiveTimeoutMs = 3000;
    private const int ReceiveBufferSize = 8192;
    private readonly string _requestId = Guid.NewGuid().ToString("N");
    private ClientWebSocket? _client;
    private bool _clientConnected;
    private bool _requestSent;
    private DialogueResponse? _response;
    private string? _responseJson;

    private MockWebSocketServer? _server;

    public PIPE003_DialoguePipeline(IModHelper helper, IMonitor monitor) : base(helper, monitor)
    {
    }

    public override string TestName
    {
        get => "PIPE003_DialoguePipeline";
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
        var mockPath = Path.Combine(Helper.DirectoryPath, "Data", "mock_llm_responses.json");
        var library = MockResponseLibrary.LoadSafely(mockPath, msg => Monitor.Log(msg, LogLevel.Warn));
        var provider = new MockLLMProvider(library);
        _server = new MockWebSocketServer(provider);
        try
        {
            _server.Start(Port);
            Monitor.Log($"[PIPE003] Mock server started on port {Port}.", LogLevel.Info);
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
                Monitor.Log($"[PIPE003] Connect failed: {ex.Message}", LogLevel.Warn);
                _clientConnected = false;
            }
            catch (OperationCanceledException ex)
            {
                Monitor.Log($"[PIPE003] Connect timed out: {ex.Message}", LogLevel.Warn);
                _clientConnected = false;
            }

            Assert("ws_client_connected", _clientConnected, $"client state={_client?.State}");
        }

        // Phase 3: send dialogue request (empty playerInput triggers greeting).
        if (CurrentTick == 120 && _client != null && _client.State == WebSocketState.Open && !_requestSent)
        {
            var request = new
            {
                type = "dialogue",
                requestId = _requestId,
                npcName = "Haley",
                personality = "Cheerful",
                location = "Farm",
                time = "600",
                weather = "Sunny",
                friendship = 0,
                conversationHistory = "",
                playerInput = ""
            };
            var json = MessageProtocol.Serialize(request);
            try
            {
                using var cts = new CancellationTokenSource(SendTimeoutMs);
                var bytes = Encoding.UTF8.GetBytes(json);
                _client.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cts.Token)
                    .GetAwaiter().GetResult();
                _requestSent = true;
                Assert("dialogue_request_sent", true, $"requestId={_requestId}");
            }
            catch (WebSocketException ex)
            {
                Assert("dialogue_request_sent", false, $"SendAsync failed: {ex.Message}");
            }
            catch (OperationCanceledException ex)
            {
                Assert("dialogue_request_sent", false, $"SendAsync timed out: {ex.Message}");
            }
        }

        // Phase 4: receive response and validate.
        if (CurrentTick == 180 && _requestSent && _responseJson == null && _client != null)
        {
            _responseJson = TryReceiveJson(_client);
            var received = !string.IsNullOrEmpty(_responseJson);
            Assert("dialogue_response_received", received,
                received ? $"len={_responseJson?.Length ?? 0}" : "no response");

            if (received && _responseJson != null)
            {
                try
                {
                    _response = MessageProtocol.Deserialize<DialogueResponse>(_responseJson);
                    Assert("dialogue_json_valid", true,
                        $"type={_response.Type} reqId={_response.RequestId}");
                    var textNonEmpty = !string.IsNullOrEmpty(_response.Speech);
                    Assert("dialogue_text_nonempty", textNonEmpty,
                        $"speechLen={_response.Speech?.Length ?? 0}");
                }
                catch (InvalidOperationException ex)
                {
                    Assert("dialogue_json_valid", false, $"deserialize failed: {ex.Message}");
                }
            }
        }

        return CurrentTick >= 300;
    }

    public override void Teardown()
    {
        DisposeClient();
        DisposeServer();
        Monitor.Log("[PIPE003] Teardown.", LogLevel.Info);
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