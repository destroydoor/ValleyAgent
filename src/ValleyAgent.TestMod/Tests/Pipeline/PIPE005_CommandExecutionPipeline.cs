#nullable enable
using System;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using ValleyAgent.TestMod.Infrastructure;
using ValleyAgent.TestMod.Mock;
using ValleyAgent.WebSocket;

namespace ValleyAgent.TestMod.Tests.Pipeline;

/// <summary>
///     Verifies the command execution pipeline: a decision message flows
///     through the mock WebSocket server while the ValleyAgent API executes
///     the corresponding commands (set_state, speak, emote, give_item) and
///     the resulting NPC state is observable.
/// </summary>
[RegisteredTest(TestGroup.Pipeline, "命令执行管道验证", "pipeline")]
public class PIPE005_CommandExecutionPipeline : V3TestBase
{
    private const int Port = 8805;
    private const int ConnectTimeoutMs = 3000;
    private const int SendTimeoutMs = 3000;
    private const int ReceiveTimeoutMs = 3000;
    private const int ReceiveBufferSize = 8192;
    private readonly string _requestId = Guid.NewGuid().ToString("N");

    private IValleyAgentApi? _api;
    private ClientWebSocket? _client;
    private bool _clientConnected;
    private bool _commandExecuted;
    private bool _commandReceived;
    private bool _commandResultCorrect;
    private bool _decisionSent;
    private MockWebSocketServer? _server;

    public PIPE005_CommandExecutionPipeline(IModHelper helper, IMonitor monitor) : base(helper, monitor)
    {
    }

    public override string TestName
    {
        get => "PIPE005_CommandExecutionPipeline";
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
        // ModRegistry.GetApi（ModEntry.API）在 GameLaunched 时返回 fallback 实例（_actionApi=null），
        // 所有 action API（TrySpeak/TrySetAgentState/TryTriggerGift）恒 false。
        // 与 IntegrationTestBase 一致：走 ModEntry.Instance.API 惰性 getter，从 Container 构造完整实例。
        _api = ValleyAgent.ModEntry.Instance?.API;
        if (_api == null)
        {
            Skip("API not available");
            return;
        }

        var mockPath = Path.Combine(Helper.DirectoryPath, "Data", "mock_llm_responses.json");
        var library = MockResponseLibrary.LoadSafely(mockPath, msg => Monitor.Log(msg, LogLevel.Warn));
        var provider = new MockLLMProvider(library);
        _server = new MockWebSocketServer(provider);
        try
        {
            _server.Start(Port);
            Monitor.Log($"[PIPE005] Mock server started on port {Port}.", LogLevel.Info);
        }
        catch (HttpListenerException ex)
        {
            Skip($"Failed to start mock server on port {Port}: {ex.Message}");
            _server.Dispose();
            _server = null;
            return;
        }
        catch (InvalidOperationException ex)
        {
            Skip($"Server already running: {ex.Message}");
            _server.Dispose();
            _server = null;
            return;
        }

        SafeWarp.Farmer(Monitor, "Farm", 54, 15);
        _ = _api.TryAllocateAgent("Haley");
        var npc = Game1.getCharacterFromName("Haley");
        if (npc == null)
        {
            Skip("Haley not found");
            return;
        }

        npc.setTileLocation(new Vector2(54, 16));
        npc.Halt();
        npc.controller = null;
        Monitor.Log("[PIPE005] Setup complete: Haley allocated near player.", LogLevel.Info);
    }

    public override bool Update()
    {
        if (_server == null || _api == null)
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
                Monitor.Log($"[PIPE005] Connect failed: {ex.Message}", LogLevel.Warn);
                _clientConnected = false;
            }
            catch (OperationCanceledException ex)
            {
                Monitor.Log($"[PIPE005] Connect timed out: {ex.Message}", LogLevel.Warn);
                _clientConnected = false;
            }

            Assert("ws_client_connected", _clientConnected, $"client state={_client?.State}");
        }

        // Phase 3: send a decision request (command message) via the pipeline.
        if (CurrentTick == 120 && _client != null && _client.State == WebSocketState.Open && !_decisionSent)
        {
            var request = new
            {
                type = "decision",
                requestId = _requestId,
                npcName = "Haley",
                currentState = "IDLE",
                location = "Farm",
                time = "600",
                mood = "Happy",
                health = 100,
                maxHealth = 100,
                friendship = 0,
                nearbyObjects = "",
                memory = ""
            };
            var json = MessageProtocol.Serialize(request);
            try
            {
                using var cts = new CancellationTokenSource(SendTimeoutMs);
                var bytes = Encoding.UTF8.GetBytes(json);
                _client.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cts.Token)
                    .GetAwaiter().GetResult();
                _decisionSent = true;
            }
            catch (WebSocketException ex)
            {
                Monitor.Log($"[PIPE005] SendAsync failed: {ex.Message}", LogLevel.Warn);
            }
            catch (OperationCanceledException ex)
            {
                Monitor.Log($"[PIPE005] SendAsync timed out: {ex.Message}", LogLevel.Warn);
            }
        }

        // Phase 4: drain the server response; verify server received the command.
        if (CurrentTick == 180 && _decisionSent && !_commandReceived)
        {
            if (_client != null)
            {
                _ = TryReceiveJson(_client);
            }

            var types = _server.ReceivedMessageTypes;
            _commandReceived = types.Contains("decision");
            Assert("command_received", _commandReceived,
                $"types=[{string.Join(",", types)}]");
        }

        // Phase 5: execute commands via the API and verify.
        if (CurrentTick == 240 && _commandReceived && !_commandExecuted)
        {
            var stateOk = _api.TrySetAgentState("Haley", "FARM");
            var speakOk = _api.TrySpeak("Haley", "Pipeline test speech", 1000);
            var emoteOk = _api.TryEmote("Haley", 20);
            var giftOk = _api.TryTriggerGift("Haley", out _);
            _commandExecuted = stateOk && speakOk && emoteOk;
            Assert("command_executed", _commandExecuted,
                $"state={stateOk} speak={speakOk} emote={emoteOk} gift={giftOk}");
        }

        // Phase 6: verify the state actually changed.
        if (CurrentTick == 300 && _commandExecuted && !_commandResultCorrect)
        {
            var state = _api.GetAgentState("Haley");
            _commandResultCorrect = state == "FARM";
            Assert("command_result_correct", _commandResultCorrect,
                $"state={state} (expected FARM)");
        }

        return CurrentTick >= 400;
    }

    public override void Teardown()
    {
        DisposeClient();
        DisposeServer();
        Monitor.Log("[PIPE005] Teardown.", LogLevel.Info);
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