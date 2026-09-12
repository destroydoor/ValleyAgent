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

            return await tcs.Task.WaitAsync(ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Complete a pending request with the given UUID and response.
        /// </summary>
        public bool CompleteRequest(string uuid, string response)
        {
            if (_pending.TryRemove(uuid, out var tcs))
            {
                tcs.SetResult(response);
                return true;
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
                tcs.SetException(ex);
                return true;
            }
            return false;
        }

        /// <summary>
        /// Fail all pending requests with the given exception.
        /// </summary>
        public void FailAll(Exception ex)
        {
            foreach (var kvp in _pending)
            {
                kvp.Value.SetException(ex);
            }
            _pending.Clear();
        }
    }
}
