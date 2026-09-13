using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using StardewModdingAPI;

namespace ValleyAgent.Api
{
    /// <summary>
    /// Manages dialogue state: cooldowns, pending requests, pause flag, and main-thread action queue.
    /// Extracted from ValleyAgentApi to eliminate static mutable state and improve testability.
    /// </summary>
    public class DialogueStateManager
    {
        private readonly HashSet<string> _pendingDialogueRequests = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, long> _lastDialogueTimestamp = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentQueue<Action> _mainThreadActions = new();

        /// <summary>
        /// Callback invoked when a cooldown rejection occurs, to push a hint to the player.
        /// Set during initialization to decouple from UI layer.
        /// Parameters: (npcName, cooldownMessage)
        /// </summary>
        public Action<string, string>? CooldownHintCallback { get; set; }

        /// <summary>
        /// Dialogue cooldown in milliseconds. Default 30000 (30 seconds).
        /// </summary>
        public int DialogueCooldownMs { get; set; } = 30000;

        /// <summary>
        /// Gift cooldown in milliseconds. Default 30000 (30 seconds).
        /// </summary>
        public int GiftCooldownMs { get; set; } = 30000;

        /// <summary>
        /// When true, all dialogue generation requests are rejected.
        /// </summary>
        public bool PauseAllDialogue { get; set; }

        /// <summary>
        /// Optional monitor for logging.
        /// </summary>
        public IMonitor? Monitor { get; set; }

        /// <summary>
        /// Returns true if the given NPC has a pending dialogue request in flight.
        /// </summary>
        public bool HasPendingDialogue(string npcName)
        {
            lock (_pendingDialogueRequests)
            {
                return _pendingDialogueRequests.Contains(npcName);
            }
        }

        /// <summary>
        /// Attempts to start a dialogue request for the given NPC.
        /// Checks pause flag, cooldown, and duplicate requests.
        /// Returns true if the request is allowed to proceed.
        /// </summary>
        public bool TryStartDialogueRequest(string npcName, out string? rejectionReason)
        {
            rejectionReason = null;

            if (PauseAllDialogue)
            {
                rejectionReason = "PauseAllDialogue=true";
                return false;
            }

            lock (_lastDialogueTimestamp)
            {
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                if (_lastDialogueTimestamp.TryGetValue(npcName, out var lastTime))
                {
                    var elapsed = now - lastTime;
                    if (elapsed < DialogueCooldownMs)
                    {
                        var remaining = (int)((DialogueCooldownMs - elapsed) / 1000);
                        rejectionReason = $"cooldown {elapsed}ms < {DialogueCooldownMs}ms";
                        // Push cooldown hint to the player via callback (decoupled from UI)
                        var cooldownMsg = $"（{npcName}还在想刚才的话题...{remaining}秒后再聊吧）";
                        CooldownHintCallback?.Invoke(npcName, cooldownMsg);
                        return false;
                    }
                }
                _lastDialogueTimestamp[npcName] = now;
            }

            lock (_pendingDialogueRequests)
            {
                if (_pendingDialogueRequests.Contains(npcName))
                {
                    rejectionReason = "request already in flight";
                    return false;
                }
                _ = _pendingDialogueRequests.Add(npcName);
            }

            return true;
        }

        /// <summary>
        /// Marks the dialogue request as completed (or failed) for the given NPC,
        /// removing it from the pending set.
        /// </summary>
        public void EndDialogueRequest(string npcName)
        {
            lock (_pendingDialogueRequests)
            {
                _ = _pendingDialogueRequests.Remove(npcName);
            }
        }

        /// <summary>
        /// Clears the dialogue cooldown for the given NPC, allowing an immediate next request.
        /// </summary>
        public void ClearDialogueCooldown(string npcName)
        {
            lock (_lastDialogueTimestamp)
            {
                _ = _lastDialogueTimestamp.Remove(npcName);
            }
        }

        /// <summary>
        /// Clears all dialogue state (cooldown + pending) for the given NPC.
        /// </summary>
        public void ClearDialogueState(string npcName)
        {
            lock (_lastDialogueTimestamp)
            {
                _ = _lastDialogueTimestamp.Remove(npcName);
            }
            lock (_pendingDialogueRequests)
            {
                _ = _pendingDialogueRequests.Remove(npcName);
            }
        }

        /// <summary>
        /// P1-10: 清除所有 NPC 的对话状态（冷却 + 挂起请求 + 主线程动作队列）。
        /// 返回标题画面时调用，防止旧存档状态污染新存档。
        /// </summary>
        public void ClearAllDialogueState()
        {
            lock (_lastDialogueTimestamp)
            {
                _lastDialogueTimestamp.Clear();
            }
            lock (_pendingDialogueRequests)
            {
                _pendingDialogueRequests.Clear();
            }
            while (_mainThreadActions.TryDequeue(out _)) { }
        }

        /// <summary>
        /// Enqueues an action to be executed on the main game thread.
        /// </summary>
        public void EnqueueMainThreadAction(Action action)
        {
            _mainThreadActions.Enqueue(action);
        }

        /// <summary>
        /// Processes all queued main-thread actions. Must be called from the game's update loop.
        /// </summary>
        public void ProcessMainThreadActions()
        {
            while (_mainThreadActions.TryDequeue(out var action))
            {
                try { action(); }
                catch (InvalidOperationException ex) { Monitor?.Log($"[DialogueStateManager] MainThread action failed: {ex.Message}", LogLevel.Warn); }
                catch (ArgumentException ex) { Monitor?.Log($"[DialogueStateManager] MainThread action failed: {ex.Message}", LogLevel.Warn); }
                catch (Exception ex)
                {
                    // 死锁修复（2026-09-12）：原来只吞 InvalidOperationException/ArgumentException。
                    // 队列动作直写 Game1（fd.Points / getCharacterFromName / drawDialogue），
                    // 切图与玩家为 null 时会抛 NRE 等——异常逃出 while 会连带跳过调用方
                    // 下游的所有主线程泵（泵是串行责任链），对话回复因此永远渲染不出来。
                    Monitor?.Log($"[DialogueStateManager] MainThread action failed: {ex}", LogLevel.Warn);
                }
            }
        }
    }
}
