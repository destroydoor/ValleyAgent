using System;
using System.Linq;
using System.Threading.Tasks;
using StardewModdingAPI;
using StardewValley;
using ValleyAgent.Brain;
using ValleyAgent.Friendship;
using ValleyAgent.Services;
using ValleyAgent.StateMachine;
using ValleyAgent.WebSocket;

namespace ValleyAgent.Api;

/// <summary>
///     Handles dialogue generation, cooldown management, and dialogue state queries.
///     Extracted from ValleyAgentApi to follow single responsibility principle.
/// </summary>
public class DialogueManagementApi
{
    private readonly AgentService _agentService;
    private readonly DialogueStateManager _dialogueState;
    private readonly FriendshipSystem? _friendshipSystem;
    private readonly IMonitor? _monitor;

    public DialogueManagementApi(
        AgentService agentService,
        DialogueStateManager dialogueState,
        FriendshipSystem? friendshipSystem = null,
        IMonitor? monitor = null)
    {
        _agentService = agentService ?? throw new ArgumentNullException(nameof(agentService));
        _dialogueState = dialogueState ?? throw new ArgumentNullException(nameof(dialogueState));
        _friendshipSystem = friendshipSystem;
        _monitor = monitor;
    }

    public bool TryGenerateDialogue(string npcName, string playerInput)
    {
        var inputPreview = playerInput?.Length > 40 ? playerInput[..40] + "..." : playerInput ?? "";
        _monitor?.Log(
            $"[Dialogue] {npcName}: TryGenerateDialogue called — input=\"{inputPreview}\" cooldown={_dialogueState.DialogueCooldownMs}ms pause={_dialogueState.PauseAllDialogue}",
            LogLevel.Debug);

        if (!_agentService.TryGetAgent(npcName, out var agent) || agent == null)
        {
            _monitor?.Log($"[Dialogue] {npcName}: rejected — agent not found", LogLevel.Info);
            return false;
        }

        if (!_dialogueState.TryStartDialogueRequest(npcName, out var rejectionReason))
        {
            _monitor?.Log($"[Dialogue] {npcName}: rejected — {rejectionReason}",
                rejectionReason?.Contains("cooldown") == true ? LogLevel.Debug : LogLevel.Info);
            return false;
        }

        // 清除上一次对话响应，确保调用方（包括测试）能区分新旧响应
        agent.LastDialogueResponse = string.Empty;
        agent.LastDialogueSource = DialogueResponseSource.None;

        try
        {
            var agentServerProvider = _agentService.AgentServerProvider;
            if (agentServerProvider == null)
            {
                _monitor?.Log($"[Dialogue] {npcName}: rejected — no server provider", LogLevel.Warn);
                _dialogueState.EndDialogueRequest(npcName);
                return false;
            }

            var npc = Game1.getCharacterFromName(npcName);
            var friendshipPoints = Game1.player.friendshipData.TryGetValue(npcName, out var fd) ? fd.Points : 0;

            var request = new DialogueRequest(
                "dialogue",
                Guid.NewGuid().ToString("N"),
                npcName,
                playerInput ?? string.Empty,
                new WorldSnapshot(
                    Game1.currentSeason,
                    Game1.dayOfMonth,
                    $"{Game1.timeOfDay / 100:D2}:{Game1.timeOfDay % 100:D2}",
                    Game1.isRaining ? Game1.isLightning ? "Stormy" : "Rainy" : Game1.isSnowing ? "Snowy" : "Sunny",
                    npc?.currentLocation?.Name ?? Game1.currentLocation?.Name ?? "Unknown",
                    npc != null ? new TilePosition((int)npc.Tile.X, (int)npc.Tile.Y) : new TilePosition(0, 0),
                    "",
                    friendshipPoints,
                    "IDLE",
                    Game1.player.Items.Where(i => i != null).Select(i => new InventoryItem(i.Name, i.Stack)).ToList(),
                    Game1.player.Name
                ),
                Game1.player.UniqueMultiplayerID.ToString()
            );

            _monitor?.Log($"[Dialogue] {npcName}: sending async request...", LogLevel.Debug);

            _ = Task.Run(async () =>
            {
                var circuitBreaker = _agentService.CircuitBreaker;
                var acquired = false;
                try
                {
                    // P0-1/P0-3: 使用 TryAcquireExecution 原子获取执行权，避免 HALF_OPEN 并发失控
                    if (circuitBreaker != null && !circuitBreaker.TryAcquireExecution())
                    {
                        _monitor?.Log($"[Dialogue] {npcName}: circuit breaker OPEN, using local fallback",
                            LogLevel.Warn);
                        var fallbackText = GetLocalDialogueFallback(playerInput ?? "");
                        _dialogueState.EnqueueMainThreadAction(() =>
                        {
                            try
                            {
                                agent.LastDialogueResponse = fallbackText;
                                // 任务5.1：标记来源为 Fallback（熔断器 OPEN）
                                agent.LastDialogueSource = DialogueResponseSource.Fallback;
                                // 2026-08-20 Phase 2：熔断兜底也写对话记忆（防"玩家说了话没下文"失忆，
                                // 与 LLM 成功路径的 AddMemory 行为对齐——§0.5 fallback 失忆教训）。
                                if (!string.IsNullOrWhiteSpace(fallbackText))
                                {
                                    var memPlayer = $"Player said: \"{(playerInput ?? "").Trim()}\"";
                                    var memResponse = $"I responded (local fallback): \"{fallbackText}\"";
                                    agent.Brain?.AddMemory(memPlayer, entryType: MemoryEntryType.Conversation);
                                    agent.Brain?.AddMemory(memResponse, entryType: MemoryEntryType.Conversation);
                                }

                                _monitor?.Log(
                                    $"[DialogueManagementApi] Fallback response for {npcName}: {fallbackText}",
                                    LogLevel.Debug);
                            }
                            catch (Exception inner)
                            {
                                _monitor?.Log($"[DialogueManagementApi] Fallback failed: {inner}",
                                    LogLevel.Debug);
                            }
                        });
                        return;
                    }

                    acquired = true;
                    // issue #27 ②：RecordResponseTime 需要真实 LLM 耗时——从发出请求起计时
                    // （慢响应均值超阈值会自动打开熔断器，此前该路径只记成败不记耗时）。
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var result = await agentServerProvider.GenerateDialogueAsync(request).ConfigureAwait(false);
                    var responseText = result.Speech ?? "";
                    var responseAction = result.Actions.Count > 0 ? result.Actions[0].Tool : null;
                    var responsePreview = responseText.Length > 60 ? responseText[..60] + "..." : responseText;
                    _monitor?.Log(
                        $"[Dialogue] {npcName}: response received — text=\"{responsePreview}\" action={responseAction}",
                        LogLevel.Info);

                    // 对话成功，记录熔断器成功 + 响应耗时（issue #27 ② 补齐：TryAcquire/RecordSuccess/
                    // RecordFailure 已有，唯缺耗时采样——慢响应熔断在本地对话路径此前不可达）。
                    circuitBreaker?.RecordSuccess();
                    circuitBreaker?.RecordResponseTime(sw.Elapsed);

                    // 先捕获快照（Game1 状态可能在 await 后变化）
                    var playerInputSnapshot = playerInput ?? string.Empty;
                    var responseTextSnapshot = responseText;
                    var initialFriendship =
                        Game1.player.friendshipData.TryGetValue(npcName, out var fd2) ? fd2.Points : 0;
                    var dateKey = $"{Game1.currentSeason}_{Game1.dayOfMonth}_Year{Game1.year}";
                    // 方案 B：从 dialogue_response 携带的好感度 delta/reason（LLM 未输出时为 null，默认 0）
                    var fsDelta = result.FriendshipDelta ?? 0;
                    var fsReason = result.FriendshipReason ?? "";

                    _dialogueState.EnqueueMainThreadAction(() =>
                    {
                        try
                        {
                            agent.LastDialogueResponse = responseTextSnapshot;
                            // 任务5.1：标记来源为 LLM（TS Agent Server 成功响应）
                            agent.LastDialogueSource = DialogueResponseSource.LLM;

                            if (!string.IsNullOrWhiteSpace(responseTextSnapshot))
                            {
                                var memPlayer = $"Player said: \"{playerInputSnapshot}\"";
                                var memResponse = $"I responded: \"{responseTextSnapshot}\"";
                                try
                                {
                                    // 任务3.1：指定 entryType=Conversation，修复对话历史查询过滤不匹配的致命 bug
                                    agent.Brain?.AddMemory(memPlayer, entryType: MemoryEntryType.Conversation);
                                    agent.Brain?.AddMemory(memResponse, entryType: MemoryEntryType.Conversation);
                                }
                                catch (Exception ex)
                                {
                                    _monitor?.Log(
                                        $"[DialogueManagementApi] Memory write failed for {npcName}: {ex}",
                                        LogLevel.Warn);
                                }

                                _monitor?.Log(
                                    $"[DialogueManagementApi] LLM response for {npcName}: {responseTextSnapshot}",
                                    LogLevel.Debug);
                            }

                            if (!string.IsNullOrEmpty(responseAction))
                            {
                                if (Enum.TryParse<AgentState>(responseAction, true, out var targetState))
                                {
                                    try
                                    {
                                        agent.StateMachine.ForceTransition(targetState);
                                    }
                                    catch (Exception ex)
                                    {
                                        _monitor?.Log(
                                            $"[DialogueManagementApi] ForceTransition failed for {npcName}: {ex}",
                                            LogLevel.Warn);
                                    }
                                }
                            }

                            if (!string.IsNullOrEmpty(result.Emotion) &&
                                !result.Emotion.Equals("Neutral", StringComparison.OrdinalIgnoreCase))
                            {
                                if (Enum.TryParse<NpcEmotion>(result.Emotion, true, out var parsedEmotion))
                                {
                                    agent.Brain?.SyncEmotion(parsedEmotion, 0.8f, "DialogueResponse");
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _monitor?.Log(
                                $"[DialogueManagementApi] MainThread dialogue action failed for {npcName}: {ex}",
                                LogLevel.Error);
                        }
                    });

                    // 应用外部评估的好感度变化（方案 B：delta 来自 dialogue_response.friendshipDelta）
                    // 后台 fire-and-forget，不阻塞对话完成。
                    // 必须 fire-and-forget：否则 await 会让 EndDialogueRequest 延迟 10-30s，
                    // 阻塞 TryStartDialogueRequest，并触发熔断器 OPEN（avg response time >30s）。
                    if (_friendshipSystem != null && !string.IsNullOrWhiteSpace(playerInputSnapshot))
                    {
                        var fsContext = new FriendshipChangeContext
                        {
                            NpcName = npcName,
                            InteractionType = InteractionType.Conversation,
                            CurrentFriendshipPoints = initialFriendship,
                            MaxFriendshipPoints = 2500,
                            CurrentDateKey = dateKey,
                            InteractionContent = $"{playerInputSnapshot}\n[NPC回复]{responseTextSnapshot}"
                        };
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                var fsResult = await _friendshipSystem
                                    .ApplyWithExternalDeltaAsync(fsContext, fsDelta, fsReason).ConfigureAwait(false);
                                if (fsResult.AppliedChange != 0)
                                {
                                    _dialogueState.EnqueueMainThreadAction(() =>
                                    {
                                        try
                                        {
                                            if (Game1.player.friendshipData.TryGetValue(npcName, out var fd3))
                                            {
                                                fd3.Points = Math.Clamp(fsResult.NewPoints, 0, 2500);
                                            }

                                            _monitor?.Log(
                                                $"[Dialogue] {npcName}: friendship {fsResult.AppliedChange:+#;-#;0} → {fsResult.NewPoints} ({fsResult.Reason})",
                                                LogLevel.Info);
                                        }
                                        catch (Exception ex)
                                        {
                                            _monitor?.Log(
                                                $"[DialogueManagementApi] Friendship apply failed for {npcName}: {ex}",
                                                LogLevel.Debug);
                                        }
                                    });
                                }
                                else
                                {
                                    _monitor?.Log(
                                        $"[Dialogue] {npcName}: friendship eval returned 0 change ({fsResult.Reason})",
                                        LogLevel.Debug);
                                }
                            }
                            catch (Exception ex)
                            {
                                _monitor?.Log(
                                    $"[Dialogue] {npcName}: friendship eval failed — {ex}",
                                    LogLevel.Debug);
                            }
                        });
                    }
                }
                catch (Exception ex)
                {
                    // P0-1: 捕获所有异常类型（TimeoutException/WebSocketException/OperationCanceledException 等）
                    // 避免 unobserved task exception 和 EndDialogueRequest 未调用
                    if (acquired)
                    {
                        _agentService.CircuitBreaker?.RecordFailure("dialogue_error");
                    }

                    _monitor?.Log($"[Dialogue] {npcName}: async failed — {ex}",
                        LogLevel.Error);
                    // P0-4: 对话失败时降级到本地关键词回退，而不是显示错误消息
                    var fallbackText = GetLocalDialogueFallback(playerInput ?? "");
                    _dialogueState.EnqueueMainThreadAction(() =>
                    {
                        try
                        {
                            agent.LastDialogueResponse = fallbackText;
                            // 任务5.1：标记来源为 Error（异常路径降级）
                            agent.LastDialogueSource = DialogueResponseSource.Error;
                            // 2026-08-20 Phase 2：异常降级同样写对话记忆（与熔断 fallback 一致，防失忆）。
                            if (!string.IsNullOrWhiteSpace(fallbackText))
                            {
                                var memPlayer = $"Player said: \"{(playerInput ?? "").Trim()}\"";
                                var memResponse = $"I responded (error fallback): \"{fallbackText}\"";
                                agent.Brain?.AddMemory(memPlayer, entryType: MemoryEntryType.Conversation);
                                agent.Brain?.AddMemory(memResponse, entryType: MemoryEntryType.Conversation);
                            }

                            _monitor?.Log($"[DialogueManagementApi] Error fallback for {npcName}: {fallbackText}",
                                LogLevel.Debug);
                        }
                        catch (Exception inner)
                        {
                            _monitor?.Log($"[DialogueManagementApi] Failed to set fallback response: {inner}",
                                LogLevel.Debug);
                        }
                    });
                }
                finally
                {
                    // P0-1: 确保无论成功/失败/异常，EndDialogueRequest 必被调用，避免 NPC 对话永久卡死
                    _dialogueState.EndDialogueRequest(npcName);
                }
            });

            return true;
        }
        catch (Exception ex)
        {
            // 死锁修复（2026-09-12）：原来只捕获 InvalidOperationException。
            // TryStartDialogueRequest 已把 npcName 记进 _pendingDialogueRequests，而释放点
            // 只有两处——Task.Run 里的 finally（异常发生在 Task.Run 之前就到不了）和这个 catch。
            // 这段同步代码裸读 Game1.player.friendshipData / .Items / .UniqueMultiplayerID /
            // Game1.getCharacterFromName：联机切图、玩家瞬态为 null、Net 字段未同步时抛的是
            // NullReferenceException / KeyNotFoundException，不是 InvalidOperationException
            // ⇒ 守卫永久泄漏 ⇒ 该 NPC 之后每次对话都被 "request already in flight" 拒绝，
            //    直到返回标题才 ClearAllDialogueState。这是 NPC 级的永久逻辑死锁，
            //    且主机是房客对话的唯一出口，一个 NPC 卡死对所有玩家生效。
            _monitor?.Log($"[Dialogue] {npcName}: sync exception — {ex}", LogLevel.Error);
            _dialogueState.EndDialogueRequest(npcName);
            return false;
        }
    }

    public bool TryGetLastDialogue(string npcName, out string response)
    {
        response = string.Empty;
        if (!_agentService.TryGetAgent(npcName, out var agent) || agent == null)
        {
            return false;
        }

        response = agent.LastDialogueResponse;
        return !string.IsNullOrEmpty(response);
    }

    /// <summary>任务5.1：获取最近对话响应的来源，供测试系统断言 LLM 是否真的被调用。</summary>
    public bool TryGetLastDialogueSource(string npcName, out DialogueResponseSource source)
    {
        source = DialogueResponseSource.None;
        if (!_agentService.TryGetAgent(npcName, out var agent) || agent == null)
        {
            return false;
        }

        source = agent.LastDialogueSource;
        return source != DialogueResponseSource.None;
    }

    public bool TryGetLastDecision(string npcName, out string state, out string reason)
    {
        state = string.Empty;
        reason = string.Empty;
        if (!_agentService.TryGetAgent(npcName, out var agent) || agent == null)
        {
            return false;
        }

        state = agent.LastDecisionState;
        reason = agent.LastDecisionReason;
        return !string.IsNullOrEmpty(state);
    }

    /// <summary>
    ///     P0-4: LLM 不可用时基于关键词的本地对话回退。
    ///     与 TS Agent Server 端 rule-engine.ts buildFallbackResponse 保持语义一致：
    ///     正向 &gt; 负向 → 感谢；负向 &gt; 正向 → 难过；疑问 → 思考；默认 → 应答。
    ///     同时识别中文关键词，避免中文输入完全无响应。
    /// </summary>
    /// <param name="playerInput">玩家原始输入文本（可为 null）。</param>
    /// <returns>回退对话文本（永不为 null）。</returns>
    private static string GetLocalDialogueFallback(string? playerInput)
    {
        if (string.IsNullOrWhiteSpace(playerInput))
        {
            return "嗯？你想要说什么？";
        }

        var text = playerInput.ToLowerInvariant();

        // 中英文正向关键词
        var positiveKeywords = new[]
        {
            "love", "great", "happy", "wonderful", "beautiful", "thank", "like", "best",
            "喜欢", "爱", "开心", "高兴", "谢谢", "感谢", "棒", "好", "美", "可爱"
        };
        // 中英文负向关键词
        var negativeKeywords = new[]
        {
            "hate", "annoying", "stupid", "worst", "ugly", "angry", "terrible", "sad",
            "讨厌", "烦", "蠢", "笨", "丑", "生气", "难过", "伤心", "糟糕", "坏"
        };
        // 中英文疑问标记
        var questionKeywords = new[]
        {
            "?", "？", "how", "what", "why", "where", "when", "who",
            "怎么", "为什么", "什么", "哪里", "哪儿", "何时", "谁", "吗", "呢"
        };

        var posCount = 0;
        var negCount = 0;
        foreach (var kw in positiveKeywords)
        {
            if (text.Contains(kw, StringComparison.Ordinal))
            {
                posCount++;
            }
        }

        foreach (var kw in negativeKeywords)
        {
            if (text.Contains(kw, StringComparison.Ordinal))
            {
                negCount++;
            }
        }

        var hasQuestion = false;
        foreach (var kw in questionKeywords)
        {
            if (text.Contains(kw, StringComparison.Ordinal))
            {
                hasQuestion = true;
                break;
            }
        }

        if (posCount > negCount)
        {
            return "谢谢你这么说，让我很开心。";
        }

        if (negCount > posCount)
        {
            return "你这样说让我有点难过...";
        }

        if (hasQuestion)
        {
            return "嗯，让我想想...";
        }

        return "嗯，我听到了。";
    }
}