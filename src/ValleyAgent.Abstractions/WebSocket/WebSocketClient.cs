using System;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace ValleyAgent.WebSocket
{
    /// <summary>
    /// WebSocket client with auto-reconnect, hello handshake, and thread-safe request sending.
    /// </summary>
    public class WebSocketClient : IAgentServerProvider, IDisposable
    {
        private readonly string _uri;
        private readonly string _npcName;
        private ClientWebSocket? _ws;
        private CancellationTokenSource? _readCts;
        private Task? _readLoopTask;
        private readonly PendingRequestTracker _tracker = new();
        private readonly object _sendLock = new();
        private bool _disposed;
        private CancellationTokenSource? _heartbeatCts;
        private Task? _heartbeatTask;

        /// <summary>
        /// 断线 outbox：fire-and-forget 消息在 WebSocket 断连期间入队，
        /// 重连成功后 FlushOutboxAsync 批量补发。
        /// 上限 MaxOutboxSize 防止长时间断连导致内存溢出，超限时丢弃最旧消息。
        /// </summary>
        private readonly ConcurrentQueue<string> _outbox = new();
        private const int MaxOutboxSize = 100;

        /// <summary>测试钩子：当前 outbox 中待补发的消息数。</summary>
        internal int OutboxCount => _outbox.Count;

        /// <inheritdoc />
        public event Action? OnConnected;

        /// <summary>
        ///     重连成功事件（2026-08-15 步骤 4）。参数 = 本次补发的 outbox 消息数。
        ///     与 OnConnected 区分：仅断线重连成功后触发（首次连接不触发）。
        ///     EventHandlerInitializer 订阅后发 reconnect_sync（断线对账信号）。
        /// </summary>
        public event Action<int>? OnReconnected;

        /// <inheritdoc />
        public event Action? OnDisconnected;

        /// <summary>
        /// 收到非 pending request 响应的 unsolicited 消息时触发（如 TS 主动下发的 allocate_agent）。
        /// 消息原文（JSON 字符串）传给订阅者按 type 路由。
        /// </summary>
        public event Action<string>? OnUnsolicitedMessage;

        /// <summary>日志回调，用于记录连接错误。</summary>
        public Action<string>? LogCallback { get; set; }

        public WebSocketClient(string uri, string npcName = "agent")
        {
            _uri = uri;
            _npcName = npcName;
        }

        /// <inheritdoc />
        public async Task<DialogueResponse> GenerateDialogueAsync(DialogueRequest request, CancellationToken ct = default)
        {
            var response = await SendRequestAsync("dialogue", request, ct).ConfigureAwait(false);
            CheckErrorResponse(response);
            return MessageProtocol.Deserialize<DialogueResponse>(response)
                ?? throw new InvalidOperationException("Failed to deserialize DialogueResponse.");
        }

        /// <summary>
        ///     error 帧检测（issue #22）。兼容三代 TS 服务器：新协议发 message+code（+可选
        ///     requestId），老 TS 只发 message，更老版本只填 error 字段。code/requestId 一并
        ///     拼进异常消息，保证崩溃可归因，而不是退化成"Unknown error"或 120s 盲等。
        /// </summary>
        private static void CheckErrorResponse(string response)
        {
            using var doc = System.Text.Json.JsonDocument.Parse(response);
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var t) ? t.GetString() : "";
            if (type == "error")
            {
                string detail = "Unknown error";
                if (root.TryGetProperty("message", out var msgProp) && msgProp.ValueKind == JsonValueKind.String)
                {
                    detail = msgProp.GetString() ?? "Unknown error";
                }
                else if (root.TryGetProperty("error", out var errProp) && errProp.ValueKind == JsonValueKind.String)
                {
                    // 老 TS 服务器用 error 字段承载详情
                    detail = errProp.GetString() ?? "Unknown error";
                }

                string? code = null;
                if (root.TryGetProperty("code", out var codeProp) && codeProp.ValueKind == JsonValueKind.String)
                {
                    code = codeProp.GetString();
                }

                string? requestId = null;
                if (root.TryGetProperty("requestId", out var reqProp) && reqProp.ValueKind == JsonValueKind.String)
                {
                    requestId = reqProp.GetString();
                }

                var attribution = $"Server error: {detail}";
                if (!string.IsNullOrEmpty(code))
                {
                    attribution += $" [code={code}]";
                }

                if (!string.IsNullOrEmpty(requestId))
                {
                    attribution += $" [requestId={requestId}]";
                }

                throw new InvalidOperationException(attribution);
            }
        }

        /// <inheritdoc />
        public Task<bool> IsConnectedAsync(CancellationToken ct = default)
        {
            lock (_sendLock)
            {
                return Task.FromResult(_ws?.State == WebSocketState.Open);
            }
        }

        private int _connecting; // 0 = not connecting, 1 = connecting
        private int _reconnecting; // 0 = not reconnecting, 1 = reconnecting

        /// <summary>
        /// 创建 ClientWebSocket。必须显式 Proxy=null：默认值是系统代理，
        /// 本机装有 Clash/VPN 类代理时 ws://127.0.0.1 会被代理截胡，表现为
        /// 裸 TCP 探测通（TcpClient 不走代理）但 WS 握手永远失败。
        /// </summary>
        private static ClientWebSocket CreateClientWebSocket()
        {
            var ws = new ClientWebSocket();
            ws.Options.Proxy = null;
            return ws;
        }

        /// <summary>
        /// Connect to the WebSocket server and perform hello handshake, then start the read loop.
        /// </summary>
        public async Task ConnectAsync(CancellationToken ct = default)
        {
            if (Interlocked.CompareExchange(ref _connecting, 1, 0) != 0)
            {
                return;
            }

            try
            {
                var ws = CreateClientWebSocket();
                _ws = ws;

                await ws.ConnectAsync(new Uri(_uri), ct).ConfigureAwait(false);

                // Hello handshake
                var hello = MessageProtocol.Serialize(new { type = "hello", requestId = Guid.NewGuid().ToString("N"), npcName = _npcName });
                await SendUnsafeAsync(ws, hello, ct).ConfigureAwait(false);

                _readCts?.Dispose();
                _readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                _readLoopTask = ReadLoopAsync(ws, _readCts.Token);

                OnConnected?.Invoke();
                StartHeartbeat();
                _ = FlushOutboxAsync();
            }
            finally
            {
                _ = Interlocked.Exchange(ref _connecting, 0);
            }
        }

        private async Task ReadLoopAsync(ClientWebSocket ws, CancellationToken ct)
        {
            var buffer = new byte[8192];
            var sb = new StringBuilder();

            try
            {
                while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        break;
                    }

                    _ = sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));

                    if (result.EndOfMessage)
                    {
                        var message = sb.ToString();
                        _ = sb.Clear();
                        HandleMessage(message);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // expected on disconnect
            }
            catch (WebSocketException ex)
            {
                LogCallback?.Invoke($"[WS] ReadLoop error: {ex.Message}");
            }
            catch (Exception ex)
            {
                // issue #24：窄捕（取消/WS 协议错误）之外的意外异常（订阅者 OnUnsolicitedMessage
                // 抛出、消息处理缺陷等）同样不得逃出读循环——逃出即任务 faulted，unobserved 异常
                // 只能靠 GC 时机落盘。循环退出后 finally 仍会触发重连（finally 是唯一重连入口）。
                LogCallback?.Invoke($"[WS] ReadLoop unexpected error (reconnect still kicks in via finally): {ex}");
            }
            finally
            {
                // 死锁修复（2026-09-12）：这段 finally 是**唯一的自动重连入口**，
                // 原来 5 个步骤裸串在一起——任何一步抛异常，后面的 ReconnectIndependentAsync()
                // 就永远执行不到，WebSocket 断线后整个进程再也不会重连
                //（所有对话抛 "WebSocket is not connected"，AI 静默死亡，无告警）。
                // 每一步独立兜底，重连必须走到。
                try { StopHeartbeat(); }
                catch (Exception ex) { LogCallback?.Invoke($"[WS] StopHeartbeat failed in read-loop teardown: {ex.Message}"); }

                // 断连时立即 fail 所有 pending 请求，避免调用方等 30 秒超时
                try { _tracker.FailAll(new InvalidOperationException("WebSocket disconnected")); }
                catch (Exception ex) { LogCallback?.Invoke($"[WS] FailAll failed in read-loop teardown: {ex.Message}"); }

                // 订阅者抛异常同样不得吃掉重连（OnDisconnected 的订阅方在 EventHandlerInitializer 侧发 WS 消息）
                try { OnDisconnected?.Invoke(); }
                catch (Exception ex) { LogCallback?.Invoke($"[WS] OnDisconnected handler failed: {ex.Message}"); }

                // 释放旧 ws 实例
                try { ws?.Dispose(); } catch (Exception) { /* ws 可能已被 Abort/Dispose */ }

                // 使用独立 CTS 重连，不依赖 ReadLoop 的 ct（可能已被取消）
                try { _ = ReconnectIndependentAsync(); }
                catch (Exception ex) { LogCallback?.Invoke($"[WS] Reconnect kick-off failed: {ex.Message}"); }
            }
        }

        private void HandleMessage(string message)
        {
            try
            {
                using var doc = JsonDocument.Parse(message);
                var root = doc.RootElement;

                // 先尝试匹配 pending request
                if (root.TryGetProperty("requestId", out var reqIdProp)
                    && reqIdProp.ValueKind == JsonValueKind.String)
                {
                    var requestId = reqIdProp.GetString();
                    if (!string.IsNullOrEmpty(requestId))
                    {
                        if (_tracker.CompleteRequest(requestId, message))
                        {
                            return; // 匹配到 pending request，消费完毕
                        }
                    }
                }

                // 未匹配 pending request → unsolicited 消息，交给订阅者按 type 路由
                OnUnsolicitedMessage?.Invoke(message);
            }
            catch (JsonException)
            {
                // 畸形消息无法解析，记录原始内容前100字符用于诊断
                var preview = message?.Length > 100 ? message[..100] + "..." : message;
                LogCallback?.Invoke($"[WS] Malformed message ignored: {preview}");
            }
        }

        /// <summary>
        /// 独立重连流程：使用自己的 CancellationToken，不依赖 ReadLoop 的 ct。
        /// 通过 _reconnecting 互斥锁防止并发重连。
        /// </summary>
        private async Task ReconnectIndependentAsync()
        {
            if (_disposed) return;
            if (Interlocked.CompareExchange(ref _reconnecting, 1, 0) != 0) return;

            var delay = 1000;
            const int maxDelay = 15000;

            try
            {
                using var reconnectCts = new CancellationTokenSource();
                var ct = reconnectCts.Token;

                while (!_disposed && !ct.IsCancellationRequested)
                {
                    await Task.Delay(delay, CancellationToken.None).ConfigureAwait(false);

                    if (_disposed) break;

                    try
                    {
                        var ws = CreateClientWebSocket();
                        _ws = ws;
                        await ws.ConnectAsync(new Uri(_uri), ct).ConfigureAwait(false);

                        var hello = MessageProtocol.Serialize(new { type = "hello", requestId = Guid.NewGuid().ToString("N"), npcName = _npcName });
                        await SendUnsafeAsync(ws, hello, ct).ConfigureAwait(false);

                        _readCts?.Dispose();
                        _readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        _readLoopTask = ReadLoopAsync(ws, _readCts.Token);

                        // issue #24：连接已建立后的通知链各自兜底——订阅者/心跳/outbox 抛异常
                        // 不得让"已连接"落入下方 catch 被误判为"连接失败"再次重连
                        // （重试会新建 ws 顶掉刚建立的这条连接）。
                        try { OnConnected?.Invoke(); }
                        catch (Exception ex) { LogCallback?.Invoke($"[WS] OnConnected handler failed: {ex}"); }

                        try { StartHeartbeat(); }
                        catch (Exception ex) { LogCallback?.Invoke($"[WS] StartHeartbeat failed: {ex}"); }

                        // 2026-08-15 步骤 4：补发 outbox 后通知重连（带补发数），
                        // 上层据此发 reconnect_sync 供 TS 对账。
                        var replayed = 0;
                        try { replayed = await FlushOutboxAsync().ConfigureAwait(false); }
                        catch (Exception ex) { LogCallback?.Invoke($"[WS] Outbox flush after reconnect failed: {ex}"); }

                        try { OnReconnected?.Invoke(replayed); }
                        catch (Exception ex) { LogCallback?.Invoke($"[WS] OnReconnected handler failed: {ex}"); }

                        return;
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (WebSocketException)
                    {
                        // 重连失败必须留痕（此前静默吞掉，服务器长期不可用时无任何可观测信号）
                        LogCallback?.Invoke($"[WS] Reconnect attempt failed (next retry in {delay}ms): server not reachable at {_uri}");
                        delay = Math.Min(delay * 2, maxDelay);
                    }
                    catch (Exception ex)
                    {
                        // issue #24：连接建立阶段的意外异常（hello 序列化/CTS 处置等）按连接失败
                        // 同语义退避重试；已建连后的通知链异常在上面各自兜底，走不到这里。
                        LogCallback?.Invoke($"[WS] Reconnect attempt failed (unexpected, next retry in {delay}ms): {ex}");
                        delay = Math.Min(delay * 2, maxDelay);
                    }
                }
            }
            finally
            {
                _ = Interlocked.Exchange(ref _reconnecting, 0);
            }
        }

        /// <summary>启动心跳后台任务。先停止旧任务再启动新的。</summary>
        private void StartHeartbeat()
        {
            StopHeartbeat();
            _heartbeatCts = new CancellationTokenSource();
            _heartbeatTask = HeartbeatLoopAsync(_heartbeatCts.Token);
        }

        /// <summary>停止心跳后台任务。</summary>
        private void StopHeartbeat()
        {
            _heartbeatCts?.Cancel();
            _heartbeatCts?.Dispose();
            _heartbeatCts = null;
        }

        /// <summary>每30秒发送一次 ping，失败时 Abort 当前连接以触发重连。</summary>
        private async Task HeartbeatLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException ex)
                {
                    // StopHeartbeat 取消：正常收尾，不该作为 unobserved exception 冒出去（§3.6 铁律：留痕）
                    LogCallback?.Invoke($"[WS] Heartbeat loop exiting on cancellation: {ex}");
                    break;
                }
                catch (ObjectDisposedException ex)
                {
                    // StopHeartbeat 已 Dispose 该 CTS：同上
                    LogCallback?.Invoke($"[WS] Heartbeat loop exiting on disposed CTS: {ex}");
                    break;
                }

                if (ct.IsCancellationRequested) break;

                ClientWebSocket? capturedWs = null;
                try
                {
                    lock (_sendLock)
                    {
                        capturedWs = _ws;
                        if (capturedWs?.State != WebSocketState.Open)
                            return;
                    }

                    var ping = MessageProtocol.Serialize(new { type = "ping", requestId = Guid.NewGuid().ToString("N") });
                    await SendUnsafeAsync(capturedWs!, ping, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (WebSocketException ex)
                {
                    LogCallback?.Invoke($"[WS] Heartbeat failed, triggering reconnect: {ex.Message}");
                    // Abort 当前捕获的 ws，使 ReadLoop 的 ReceiveAsync 抛异常退出，自然触发重连
                    try { capturedWs?.Abort(); } catch (InvalidOperationException abortEx) { LogCallback?.Invoke($"[WS] Abort failed during heartbeat: {abortEx.Message}"); }
                    return;
                }
                catch (Exception ex)
                {
                    // issue #24：send 段的意外异常（与 Dispose 竞态的 ObjectDisposedException 等）
                    // 与 WS 协议错误同语义——Abort 触发重连，不让心跳任务静默死亡。
                    LogCallback?.Invoke($"[WS] Heartbeat failed (unexpected), triggering reconnect: {ex}");
                    try { capturedWs?.Abort(); } catch (InvalidOperationException abortEx) { LogCallback?.Invoke($"[WS] Abort failed during heartbeat: {abortEx.Message}"); }
                    return;
                }
            }
        }

        private async Task<string> SendRequestAsync(string type, object payload, CancellationToken ct)
        {
            var requestId = _tracker.RegisterRequest();

            var payloadJson = MessageProtocol.Serialize(payload);
            var node = JsonNode.Parse(payloadJson)!.AsObject();
            node["type"] = type;
            node["requestId"] = requestId;
            var flatMessage = node.ToJsonString();

            ClientWebSocket? capturedWs;
            lock (_sendLock)
            {
                capturedWs = _ws;
                if (capturedWs?.State != WebSocketState.Open)
                {
                    throw new InvalidOperationException("WebSocket is not connected.");
                }
            }

            await SendUnsafeAsync(capturedWs, flatMessage, ct).ConfigureAwait(false);
            return await _tracker.WaitForResponseAsync(requestId, ct).ConfigureAwait(false);
        }

        private static async Task SendUnsafeAsync(ClientWebSocket ws, string message, CancellationToken ct)
        {
            var bytes = Encoding.UTF8.GetBytes(message);
            await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task SendMessageAsync(string jsonMessage, CancellationToken ct = default)
        {
            ClientWebSocket? capturedWs;
            lock (_sendLock)
            {
                capturedWs = _ws;
                if (capturedWs?.State != WebSocketState.Open)
                {
                    // 断线期间入队 outbox，重连后 FlushOutboxAsync 补发。
                    // 超限时丢弃最旧消息防止内存溢出。
                    EnqueueOutbox(jsonMessage);
                    return;
                }
            }
            await SendUnsafeAsync(capturedWs, jsonMessage, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// 将消息入队 outbox。超限时丢弃最旧消息并记日志（降级不静默：丢弃是数据丢失，
        /// 必须可观测——断线对账只能靠 reconnect_sync 补对账范围，丢掉的补不回来）。
        /// 调用方需持 _sendLock 或保证线程安全。
        /// </summary>
        private void EnqueueOutbox(string jsonMessage)
        {
            _outbox.Enqueue(jsonMessage);
            while (_outbox.Count > MaxOutboxSize && _outbox.TryDequeue(out var dropped))
            {
                LogCallback?.Invoke($"[WebSocketClient] outbox overflow ({MaxOutboxSize}): dropping oldest message, replay lost: {TruncateForLog(dropped)}");
            }
        }

        /// <summary>日志用截断（outbox 丢弃消息只记头 120 字符，避免刷屏）。</summary>
        private static string TruncateForLog(string message)
            => message.Length <= 120 ? message : message[..120] + "...";

        /// <summary>
        /// 重连成功后批量补发 outbox 中的消息。
        /// 发送失败的消息丢弃（fire-and-forget 语义，不重试）。
        /// </summary>
        /// <summary>重连成功后批量补发 outbox 中的消息。返回实际补发条数（0=无积压）。</summary>
        private async Task<int> FlushOutboxAsync()
        {
            ClientWebSocket? capturedWs;
            lock (_sendLock)
            {
                capturedWs = _ws;
                if (capturedWs?.State != WebSocketState.Open)
                {
                    return 0;
                }
            }

            var replayed = 0;
            while (_outbox.TryDequeue(out var message))
            {
                try
                {
                    await SendUnsafeAsync(capturedWs, message, CancellationToken.None).ConfigureAwait(false);
                    replayed++;
                }
                catch (WebSocketException ex)
                {
                    LogCallback?.Invoke($"[WS] Outbox flush send failed, dropping remaining: {ex.Message}");
                    // 连接又断了，剩余消息清空（下次重连会重新积累）
                    while (_outbox.TryDequeue(out _)) { }
                    return replayed;
                }
                catch (OperationCanceledException)
                {
                    while (_outbox.TryDequeue(out _)) { }
                    return replayed;
                }
            }

            return replayed;
        }

        /// <inheritdoc />
        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed)
            {
                return;
            }

            if (disposing)
            {
                StopHeartbeat();
                _readCts?.Cancel();
                _tracker.FailAll(new ObjectDisposedException(nameof(WebSocketClient)));

                // 等待 ReadLoop 退出（最多 2 秒），避免 ObjectDisposedException
                if (_readLoopTask != null)
                {
                    try { _ = _readLoopTask.Wait(TimeSpan.FromSeconds(2)); } catch (InvalidOperationException ex) { LogCallback?.Invoke($"[WS] ReadLoop shutdown wait error: {ex.Message}"); }
                }

                _readCts?.Dispose();

                lock (_sendLock)
                {
                    _ws?.Dispose();
                    _ws = null;
                }
            }

            _disposed = true;
        }

    }
}
