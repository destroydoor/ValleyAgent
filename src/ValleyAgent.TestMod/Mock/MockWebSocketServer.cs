#nullable enable
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MessageProtocol = ValleyAgent.WebSocket.MessageProtocol;

namespace ValleyAgent.TestMod.Mock;

/// <summary>
///     Local WebSocket server that simulates the TS Agent Server (valley-ai-server.exe)
///     for offline test runs. Accepts Protocol V2 connections, routes messages by type,
///     and delegates decision/dialogue matching to <see cref="MockLLMProvider" />.
/// </summary>
public class MockWebSocketServer : IDisposable
{
    private const int DefaultPort = 8766;
    private const int ReceiveBufferSize = 8192;

    private readonly MockLLMProvider _provider;
    private readonly List<string> _receivedMessageTypes = new();
    private readonly object _stateLock = new();
    private Task? _acceptTask;
    private int _connectionCount;

    /// <summary>收到的 action_result 回执数（2026-08-20：反馈环测试断言用）。</summary>
    private int _actionResultCount;

    public int ActionResultCount => Volatile.Read(ref _actionResultCount);
    private CancellationTokenSource? _cts;
    private volatile bool _disposed;
    private volatile bool _isRunning;
    private string? _lastNpcName;

    private HttpListener? _listener;
    private int _messageCount;

    /// <summary>
    ///     Create a new <see cref="MockWebSocketServer" /> backed by the given provider.
    /// </summary>
    /// <param name="provider">The mock LLM provider used for decision/dialogue matching.</param>
    public MockWebSocketServer(MockLLMProvider provider)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
    }

    /// <summary>True while the server is listening for connections.</summary>
    public bool IsRunning
    {
        get => _isRunning;
    }

    /// <summary>Total number of connections accepted since Start.</summary>
    public int ConnectionCount
    {
        get => Volatile.Read(ref _connectionCount);
    }

    /// <summary>Total number of messages received since Start.</summary>
    public int MessageCount
    {
        get => Volatile.Read(ref _messageCount);
    }

    /// <summary>The last npcName received in a hello handshake, if any.</summary>
    public string? LastNpcName
    {
        get
        {
            lock (_stateLock)
            {
                return _lastNpcName;
            }
        }
    }

    /// <summary>
    ///     Snapshot of all received message types, in arrival order. Each access
    ///     returns a fresh copy safe for test assertions.
    /// </summary>
    public List<string> ReceivedMessageTypes
    {
        get
        {
            lock (_stateLock)
            {
                return new List<string>(_receivedMessageTypes);
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    ///     Fired for every received message with (messageType, jsonPayload). Useful
    ///     for debug logging or custom test hooks.
    /// </summary>
    public event Action<string, string>? OnMessageReceived;

    /// <summary>
    ///     Start listening on the given port. Defaults to <c>8766</c>.
    /// </summary>
    /// <param name="port">The TCP port to listen on (1-65535).</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if port is out of range.</exception>
    /// <exception cref="InvalidOperationException">Thrown if the server is already running.</exception>
    /// <exception cref="ObjectDisposedException">Thrown if the server has been disposed.</exception>
    public void Start(int port = DefaultPort)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(MockWebSocketServer));
        }

        if (_isRunning)
        {
            throw new InvalidOperationException("Server is already running.");
        }

        if (port < 1 || port > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port), "Port must be between 1 and 65535.");
        }

        _cts = new CancellationTokenSource();
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.Start();
        _isRunning = true;
        _acceptTask = AcceptConnectionsAsync(_cts.Token);
    }

    /// <summary>
    ///     Stop the server, close all connections, and release the listener.
    ///     Safe to call multiple times.
    /// </summary>
    public async Task StopAsync()
    {
        if (!_isRunning)
        {
            return;
        }

        _isRunning = false;
        _cts?.Cancel();

        try
        {
            _listener?.Close();
        }
        catch (ObjectDisposedException)
        {
            // already disposed
        }

        if (_acceptTask != null)
        {
            try
            {
                await _acceptTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // expected on shutdown
            }
            catch (HttpListenerException)
            {
                // expected when listener stops during GetContextAsync
            }
        }

        _cts?.Dispose();
        _cts = null;
        _listener = null;
        _acceptTask = null;
    }

    private async Task AcceptConnectionsAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener?.IsListening == true)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (HttpListenerException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            if (ct.IsCancellationRequested)
            {
                context.Response.StatusCode = 503;
                context.Response.Close();
                break;
            }

            if (context.Request.IsWebSocketRequest)
            {
                _ = HandleConnectionAsync(context, ct);
            }
            else
            {
                context.Response.StatusCode = 400;
                context.Response.Close();
            }
        }
    }

    private async Task HandleConnectionAsync(HttpListenerContext httpContext, CancellationToken ct)
    {
        System.Net.WebSockets.WebSocket? ws = null;
        try
        {
            var wsContext = await httpContext.AcceptWebSocketAsync(null).ConfigureAwait(false);
            ws = wsContext.WebSocket;
        }
        catch (WebSocketException)
        {
            httpContext.Response.StatusCode = 500;
            httpContext.Response.Close();
            return;
        }

        _ = Interlocked.Increment(ref _connectionCount);

        var buffer = new byte[ReceiveBufferSize];
        var sb = new StringBuilder();

        try
        {
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                sb.Clear();
                WebSocketReceiveResult result;
                do
                {
                    result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Client closed", ct)
                            .ConfigureAwait(false);
                        return;
                    }

                    _ = sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                } while (!result.EndOfMessage);

                var message = sb.ToString();
                _ = Interlocked.Increment(ref _messageCount);
                await ProcessMessageAsync(ws, message, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // server shutting down
        }
        catch (WebSocketException)
        {
            // client disconnected abruptly
        }
        finally
        {
            try
            {
                ws.Dispose();
            }
            catch (InvalidOperationException)
            {
            }
        }
    }

    private async Task ProcessMessageAsync(System.Net.WebSockets.WebSocket ws, string message, CancellationToken ct)
    {
        var type = string.Empty;
        var requestId = string.Empty;
        JsonElement payloadClone = default;

        try
        {
            using var doc = JsonDocument.Parse(message);
            var root = doc.RootElement;
            type = root.TryGetProperty("type", out var t) ? t.GetString() ?? string.Empty : string.Empty;
            requestId = root.TryGetProperty("requestId", out var r) ? r.GetString() ?? string.Empty : string.Empty;
            payloadClone = root.Clone();
        }
        catch (JsonException)
        {
            await TrySendErrorAsync(ws, "malformed json", ct).ConfigureAwait(false);
            return;
        }

        lock (_stateLock)
        {
            _receivedMessageTypes.Add(type);
        }

        OnMessageReceived?.Invoke(type, message);

        string responseJson;
        switch (type)
        {
            case "hello":
                RecordHello(payloadClone);
                return;
            case "ping":
                responseJson = MessageProtocol.Serialize(new { requestId, type = "pong" });
                break;
            case "dialogue":
            {
                var resp = _provider.GetDialogueResponse(payloadClone);
                resp = resp with { RequestId = requestId, Type = "dialogue" };
                responseJson = MessageProtocol.Serialize(resp);
                break;
            }
            case "action_result":
                // 2026-08-20 Phase 5：工具执行回执（C#→TS fire-and-forget 反馈环）。
                // 此前落 default 分支回 "unknown type" error——Complex01 发回执后不读
                // error 导致消息堆积、下轮 dialogue 响应错位（RESPONSE MISMATCH 根因）。
                RecordActionResult(payloadClone);
                return;
            default:
                await TrySendErrorAsync(ws, "unknown type", ct).ConfigureAwait(false);
                return;
        }

        await TrySendAsync(ws, responseJson, ct).ConfigureAwait(false);
    }

    private void RecordHello(JsonElement payload)
    {
        var npcName = ExtractString(payload, "npcName");
        lock (_stateLock)
        {
            _lastNpcName = npcName;
        }
    }

    /// <summary>记录工具执行回执（fire-and-forget，不回复——与真实 TS server 一致）。</summary>
    private void RecordActionResult(JsonElement payload)
    {
        lock (_stateLock)
        {
            _actionResultCount++;
        }
    }

    private static async Task TrySendAsync(System.Net.WebSockets.WebSocket ws, string json, CancellationToken ct)
    {
        if (ws.State != WebSocketState.Open)
        {
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(json);
        try
        {
            await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        catch (WebSocketException)
        {
            // client gone
        }
    }

    private static Task TrySendErrorAsync(System.Net.WebSockets.WebSocket ws, string error, CancellationToken ct)
    {
        var json = MessageProtocol.Serialize(new { type = "error", error });
        return TrySendAsync(ws, json, ct);
    }

    private static string ExtractString(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out var prop)
            && prop.ValueKind == JsonValueKind.String)
        {
            return prop.GetString() ?? string.Empty;
        }

        return string.Empty;
    }

    /// <summary>
    ///     Releases managed resources. Safe to call multiple times.
    /// </summary>
    /// <param name="disposing">True when called from Dispose.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (!disposing)
        {
            return;
        }

        _isRunning = false;
        _cts?.Cancel();

        try
        {
            _listener?.Close();
        }
        catch (ObjectDisposedException)
        {
            // already disposed
        }

        if (_acceptTask != null)
        {
            try
            {
                _acceptTask.Wait(TimeSpan.FromSeconds(2));
            }
            catch (AggregateException)
            {
                // expected during shutdown
            }
        }

        _cts?.Dispose();
        _cts = null;
        _listener = null;
        _acceptTask = null;
    }
}