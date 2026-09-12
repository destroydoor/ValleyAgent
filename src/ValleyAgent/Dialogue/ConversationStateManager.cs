using System;
using System.Collections.Generic;

namespace ValleyAgent.Dialogue;

public struct ChatMessage
{
    public string Text;
    public bool IsPlayer;
    public float DisplayTime;
}

public class ConversationStateManager : IDisposable
{
    // P0-2: 响应最大有效期（秒），超过则视为陈旧
    private const double MaxResponseAgeSeconds = 60.0;

    // P0-2: 记录被 UI 放弃的请求，后端响应到达时丢弃
    private readonly Dictionary<string, DateTime> _abandonedRequests;

    private readonly int _maxHistoryPerNpc;

    // P0-2: pending response 带时间戳，用于丢弃陈旧响应
    private readonly Dictionary<string, (string Text, DateTime Timestamp)> _pendingResponses;
    private readonly Dictionary<string, List<ChatMessage>> _persistentHistories;
    private readonly object _stateLock = new();
    private bool _disposed;

    public ConversationStateManager(int maxHistoryPerNpc = 50)
    {
        _maxHistoryPerNpc = maxHistoryPerNpc;
        _persistentHistories = new Dictionary<string, List<ChatMessage>>(StringComparer.OrdinalIgnoreCase);
        _pendingResponses = new Dictionary<string, (string, DateTime)>(StringComparer.OrdinalIgnoreCase);
        _abandonedRequests = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    public List<ChatMessage> GetOrCreateHistory(string npcName)
    {
        ThrowIfDisposed();
        lock (_stateLock)
        {
            if (!_persistentHistories.TryGetValue(npcName, out var history))
            {
                history = new List<ChatMessage>();
                _persistentHistories[npcName] = history;
            }

            return history;
        }
    }

    public void AddMessageToHistory(string npcName, string text, bool isPlayer)
    {
        ThrowIfDisposed();
        lock (_stateLock)
        {
            if (!_persistentHistories.TryGetValue(npcName, out var history))
            {
                history = new List<ChatMessage>();
                _persistentHistories[npcName] = history;
            }

            history.Add(new ChatMessage { Text = text, IsPlayer = isPlayer, DisplayTime = 0f });

            if (history.Count > _maxHistoryPerNpc)
            {
                history.RemoveRange(0, history.Count - _maxHistoryPerNpc);
            }
        }
    }

    public void TrimAndCleanHistory(string npcName)
    {
        ThrowIfDisposed();
        lock (_stateLock)
        {
            if (!_persistentHistories.TryGetValue(npcName, out var history))
            {
                history = new List<ChatMessage>();
                _persistentHistories[npcName] = history;
            }

            if (history.Count > _maxHistoryPerNpc)
            {
                history.RemoveRange(0, history.Count - _maxHistoryPerNpc);
            }

            _ = history.RemoveAll(m =>
                m.Text.StartsWith("[You closed the chat") ||
                m.Text.StartsWith("[Response timed out") ||
                m.Text.StartsWith("[Error:"));
        }
    }

    public void SetPendingResponse(string npcName, string text)
    {
        ThrowIfDisposed();
        lock (_stateLock)
        {
            // P0-2: 如果请求已被 UI 放弃，丢弃陈旧响应，避免下次打开菜单显示无关回复
            if (_abandonedRequests.Remove(npcName))
            {
                return;
            }

            _pendingResponses[npcName] = (text, DateTime.UtcNow);
        }
    }

    public bool TryGetPendingResponse(string npcName, out string text)
    {
        ThrowIfDisposed();
        lock (_stateLock)
        {
            if (_pendingResponses.TryGetValue(npcName, out var entry))
            {
                // P0-2: 丢弃超过 MaxResponseAgeSeconds 的陈旧响应
                if ((DateTime.UtcNow - entry.Timestamp).TotalSeconds > MaxResponseAgeSeconds)
                {
                    _pendingResponses.Remove(npcName);
                    text = string.Empty;
                    return false;
                }

                text = entry.Text;
                _pendingResponses.Remove(npcName);
                return true;
            }

            text = string.Empty;
            return false;
        }
    }

    /// <summary>
    ///     P0-2: 标记请求被 UI 放弃（超时/关闭菜单）。
    ///     后端响应到达时 SetPendingResponse 会丢弃响应。
    /// </summary>
    public void MarkRequestAbandoned(string npcName)
    {
        ThrowIfDisposed();
        lock (_stateLock)
        {
            _abandonedRequests[npcName] = DateTime.UtcNow;
            _pendingResponses.Remove(npcName);
        }
    }

    /// <summary>
    ///     P0-2: 清除放弃标记（用户重新发送消息时调用）。
    /// </summary>
    public void ClearAbandoned(string npcName)
    {
        ThrowIfDisposed();
        lock (_stateLock)
        {
            _abandonedRequests.Remove(npcName);
        }
    }

    public void ClearNpcState(string npcName)
    {
        ThrowIfDisposed();
        lock (_stateLock)
        {
            _persistentHistories.Remove(npcName);
            _pendingResponses.Remove(npcName);
            _abandonedRequests.Remove(npcName);
        }
    }

    public void ClearHistory(string npcName)
    {
        ThrowIfDisposed();
        lock (_stateLock)
        {
            _persistentHistories.Remove(npcName);
        }
    }

    public void ClearAll()
    {
        ThrowIfDisposed();
        lock (_stateLock)
        {
            _persistentHistories.Clear();
            _pendingResponses.Clear();
            _abandonedRequests.Clear();
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
            lock (_stateLock)
            {
                _persistentHistories.Clear();
                _pendingResponses.Clear();
                _abandonedRequests.Clear();
            }
        }

        _disposed = true;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(ConversationStateManager));
        }
    }
}