using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace ValleyAgent.WebSocket
{
    /// <summary>
    /// Tracks pending WebSocket requests using correlation IDs, enabling request-response correlation.
    /// </summary>
    public class PendingRequestTracker
    {
        private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _pending = new();

        /// <summary>
        /// Register a new pending request and return its UUID.
        /// </summary>
        public string RegisterRequest()
        {
            var uuid = Guid.NewGuid().ToString("N");
            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = _pending.TryAdd(uuid, tcs);
            return uuid;
        }

        /// <summary>
        /// Wait for a response to the request with the given UUID.
        /// If no CancellationToken is provided, a default 120-second timeout is applied.
        /// </summary>
        public async Task<string> WaitForResponseAsync(string uuid, CancellationToken ct = default)
        {
            if (!_pending.TryGetValue(uuid, out var tcs))
                throw new InvalidOperationException($"No pending request with UUID '{uuid}'.");

            // 调用方未提供可取消的 Token 时，自动添加 120 秒超时。
            // 2026-09-10 对齐修复：TS 侧 LLM 失败重试链约 67s（3 × ~21s 连接超时 + 退避），
            // 旧值 60s 会在重试链结束前判超时——fallback 回包迟到被当作 unsolicited 丢弃，
            // 房客什么也收不到（soak 前 4 轮 C3 FAIL 的直接原因）。
            if (!ct.CanBeCanceled)
            {
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
                try
                {
                    return await tcs.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // 超时而非外部取消，清理并抛 TimeoutException
                    _pending.TryRemove(uuid, out _);
                    throw new TimeoutException($"Request '{uuid}' timed out after 120 seconds.");
                }
            }

            try
            {
                return await tcs.Task.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 死锁/泄漏修复（2026-09-12）：外部 token 取消时也要摘掉 pending 条目。
                // 原来只有"自动 120s 超时"分支清理，可取消分支直接抛出——条目留在 _pending 里，
                // 之后 CompleteRequest 命中它会给一个没人 await 的 TCS 设值（无害），
                // 但 FailAll 会把它算进"断连时待失败"的集合，且字典只增不减（长跑泄漏）。
                _pending.TryRemove(uuid, out _);
                throw;
            }
        }

        /// <summary>
        /// Complete a pending request with the given UUID and response.
        /// </summary>
        public bool CompleteRequest(string uuid, string response)
        {
            if (_pending.TryRemove(uuid, out var tcs))
            {
                // TrySet*（而非 Set*）：与 FailAll / 超时清理并发时，Set* 会抛
                // InvalidOperationException("Task already completed")——那异常会从
                // WebSocketClient.ReadLoopAsync 的 finally 里逃出去，吃掉重连（见该类注释）。
                return tcs.TrySetResult(response);
            }
            return false;
        }

        /// <summary>
        /// Fail a pending request with the given UUID and exception.
        /// </summary>
        public bool FailRequest(string uuid, Exception ex)
        {
            if (_pending.TryRemove(uuid, out var tcs))
            {
                return tcs.TrySetException(ex);
            }
            return false;
        }

        /// <summary>
        /// Fail all pending requests with the given exception.
        /// </summary>
        /// <summary>
        ///     Fail all pending requests with the given exception.
        ///     死锁修复（2026-09-12）：原实现"边遍历边 SetException，最后 Clear()"有两个缺陷——
        ///     (1) SetException 与 CompleteRequest/超时清理并发时抛 InvalidOperationException，
        ///         异常从 WebSocketClient.ReadLoopAsync 的 finally 逃出，
        ///         后面的 OnDisconnected / ws.Dispose / ReconnectIndependentAsync 全部不执行
        ///         ⇒ 断线后**永不重连**，AI 对话到本次进程结束都是死的；
        ///     (2) 遍历与 Clear 之间新注册的条目会被 Clear 静默抹掉而从未被置为失败，
        ///         其 WaitForResponseAsync 只能干等到自己的超时。
        ///     改为"逐条 TryRemove 再 TrySetException"：摘除与置值都是原子的，
        ///     不抛、不遗漏、与并发注册/完成天然相容。
        /// </summary>
        public void FailAll(Exception ex)
        {
            foreach (var kvp in _pending)
            {
                if (_pending.TryRemove(kvp.Key, out var tcs))
                {
                    _ = tcs.TrySetException(ex);
                }
            }
        }
    }
}
