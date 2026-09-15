using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using HarmonyLib;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using ValleyAgent.Brain;
using ValleyAgent.Economy;
using ValleyAgent.Friendship;
using ValleyAgent.Infrastructure;
using ValleyAgent.Multiplayer;
using ValleyAgent.Multiplayer.Transports;
using ValleyAgent.Services;
using ValleyAgent.WebSocket;
using Object = StardewValley.Object;

namespace ValleyAgent.Patches;

[HarmonyPatch(typeof(NPC), nameof(NPC.tryToReceiveActiveObject))]
public static class NPCGiftPatch
{
    private const int MaxConsecutiveFailures = 2;

    private static IGiftTransport? _giftTransport;

    private static readonly ConcurrentDictionary<string, CachedGiftInfo> _cachedGifts =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly ConcurrentQueue<Action> _mainThreadActions = new();

    private static readonly ConcurrentDictionary<string, int> _consecutiveFailures =
        new(StringComparer.OrdinalIgnoreCase);

    public static AgentService? AgentService { get; set; }
    public static IAgentServerProvider? AgentServerProvider { get; set; }
    public static IMonitor? Monitor { get; set; }
    public static FriendshipSystem? FriendshipSystem { get; set; }

    /// <summary>
    ///     E3-5: NPC 求购单注册表（长 TTL，当日有效）。2026-08-15 步骤 2 修订：
    ///     玩家献出物品命中求购单时不再由 C# 结算（结算已迁 TS 对话流 trade 工具 →
    ///     execute_adjust 原子批），而是拒绝送礼交接并提示走对话议价。
    ///     由 NpcPurchaseRequestService 持有，EventHandlerInitializer 注入。
    /// </summary>
    public static PendingOfferRegistry? PurchaseOffers { get; set; }

    /// <summary>
    ///     ThinClient 模式下注入的远程 Agent 状态缓存。主机广播的 Agent 名单通过其 _remoteStates 的 key 体现。
    ///     Host 模式保持 null（用 AgentService 判定 isAgent）。
    ///     用于修复联机 farmhand 端 AgentService == null 时 transport 分支不可达的问题。
    /// </summary>
    public static AgentRemoteRenderer? RemoteRenderer { get; set; }

    /// <summary>
    ///     注入送礼传输层。设置后 Prefix 将通过该传输层代理送礼请求到主机（联机模式），
    ///     否则回退到本地评估路径（向后兼容单机/主机端）。
    ///     由 ModEntry 在模式判定后调用：远程 Farmhand 端注入 FarmhandGiftTransport。
    /// </summary>
    public static void SetGiftTransport(IGiftTransport? transport) => _giftTransport = transport;

    public static void ClearDayStart() => _consecutiveFailures.Clear();

    public static void ProcessMainThreadActions()
    {
        while (_mainThreadActions.TryDequeue(out var action))
        {
            try
            {
                action();
            }
            catch (InvalidOperationException ex)
            {
                Monitor?.Log($"[Gift] MainThread action failed: {ex.Message}", LogLevel.Warn);
            }
            catch (Exception ex)
            {
                // 死锁修复（2026-09-12）：原来只吞 InvalidOperationException。
                // 队列动作直写 Game1（friendshipData / getCharacterFromName / drawDialogue），
                // 切图、NPC 离场、玩家为 null 时会抛 NRE/KeyNotFound——异常一旦逃出 while 循环，
                // 本 tick 剩余动作连同**下游泵**（ThinClient 里紧随其后的
                // HostRequestHandlers.ProcessMainThreadActions，即房客唯一的 ModMessage 发送泵）
                // 全部被跳过：请求发不出去 ⇒ 回包收不到 ⇒ 对话/送礼链路整体停摆。
                // 每个动作独立兜底，队列必须排干。
                Monitor?.Log($"[Gift] MainThread action failed: {ex}", LogLevel.Warn);
            }
        }
    }

    /// <summary>
    ///     Prefix：缓存礼物信息，阻止原版 tryToReceiveActiveObject 执行（返回 false）。
    ///     ValleyAgent 完全接管送礼逻辑，避免原版和 TS Agent Server 端双重修改好感度。
    /// </summary>
    private static bool Prefix(NPC __instance, Farmer who)
    {
        var item = who.ActiveObject;
        if (item == null || !__instance.IsVillager)
        {
            return true;
        }

        // isAgent 判定：Host 走 AgentService，ThinClient 走 RemoteRenderer 远程名单。
        // 修复 C2：原代码用 AgentService == null 早退，导致 ThinClient 端 _giftTransport 分支永远到不了。
        // ThinClient 模式下 AgentService 为 null，必须依赖主机广播的 Agent 名单。
        var isThinClient = RemoteRenderer != null && AgentService == null;
        if (isThinClient)
        {
            if (RemoteRenderer!.GetRemoteState(__instance.Name) == null)
            {
                return true; // 非 Agent NPC，交原版
            }
        }
        else
        {
            // 非 Agent NPC 交给原版处理
            if (AgentService == null || !AgentService.TryGetAgent(__instance.Name, out _))
            {
                return true;
            }

            // 任务4.1：EnableGifts 开关 — 关闭时走原版逻辑
            if (AgentService.Config?.EnableGifts == false)
            {
                return true;
            }

            // E3-5（2026-08-15 步骤 2）：玩家献出的物品命中求购单时，拒绝本次送礼交接
            // （物品留在玩家手里，经济结算走对话流 trade 工具 → execute_adjust），
            // 未命中返回 false → 回落正常送礼流程。
            if (TryHandlePurchaseRequest(__instance, item))
            {
                return false;
            }
        }

        // Task 8：联机 Farmhand 模式下通过 IGiftTransport 代理到主机。
        // 主机端不会设置 _giftTransport（保持 null），仍走下方本地评估路径。
        if (_giftTransport != null)
        {
            var qualifiedItemId = item.QualifiedItemId ?? item.ItemId ?? "";
            var npcName = __instance.Name;

            // 消耗物品（与原版 Prefix 一致）
            who.ActiveObject = null;

            // 异步发起 transport 请求，回包通过 _mainThreadActions 在主线程显示对话。
            // 不填充 _cachedGifts，因此 Postfix 会直接 return，不重复显示。
            _ = Task.Run(async () =>
            {
                try
                {
                    var response = await _giftTransport.SendAsync(npcName, qualifiedItemId, 1).ConfigureAwait(false);
                    var reactionText = response.Reaction;
                    if (string.IsNullOrWhiteSpace(reactionText))
                    {
                        reactionText = "……";
                    }

                    // 2026-08-23 审计 P0：friendshipData 是 NetField，写操作随对话显示一起入主线程队列，
                    // 不得留在 Task.Run 续体里直写（与 execute_adjust 主线程纪律同一问题类）。
                    _mainThreadActions.Enqueue(() =>
                    {
                        try
                        {
                            // 在 farmhand 本地应用主机计算的 FriendshipDelta（同步自身 friendshipData）
                            if (response.FriendshipDelta != 0 && Game1.player.friendshipData.TryGetValue(npcName, out var fd))
                            {
                                fd.Points = Math.Clamp(fd.Points + response.FriendshipDelta, 0, 2500);
                            }

                            var npc = Game1.getCharacterFromName(npcName);
                            if (npc != null)
                            {
                                var dialogue = new StardewValley.Dialogue(npc, null, reactionText);
                                npc.setNewDialogue(dialogue);
                                Game1.drawDialogue(npc);
                            }

                            Monitor?.Log($"[Gift] {npcName} received gift via transport: {reactionText}",
                                LogLevel.Debug);
                        }
                        catch (Exception ex)
                        {
                            Monitor?.Log($"[Gift] Transport main-thread display failed for {npcName}: {ex.Message}",
                                LogLevel.Warn);
                        }
                    });
                    // 深度告警：主线程泵停摆时送礼动作堆积的早期信号（2026-09-11 生产化仪器）
                    QueueTelemetry.WarnIfDeep("gift-actions", _mainThreadActions.Count, Monitor);
                }
                catch (Exception ex)
                {
                    Monitor?.Log($"[Gift] Transport SendAsync failed for {npcName}: {ex.Message}", LogLevel.Warn);
                }
            });

            return false;
        }

        // 健壮性守卫：ThinClient 模式下若 _giftTransport 未注入（不应发生，仅 OnReturnedToTitle 期间短暂为 null），
        // 此时 AgentService 为 null 无法走本地评估，安全降级交回原版处理。
        if (AgentService == null)
        {
            Monitor?.Log($"[Gift] {__instance.Name}: ThinClient with null _giftTransport, falling back to vanilla",
                LogLevel.Warn);
            return true;
        }

        // 任务4.2：EnableFriendshipChanges 关闭时只消耗物品，不变更好感度
        if (AgentService.Config?.EnableFriendshipChanges == false)
        {
            int giftTasteOnly;
            try
            {
                giftTasteOnly = __instance.getGiftTasteForThisItem(item);
            }
            catch (InvalidOperationException)
            {
                giftTasteOnly = 4;
            }

            _cachedGifts[who.UniqueMultiplayerID.ToString()] = new CachedGiftInfo
            {
                ItemDisplayName = item.DisplayName ?? item.Name ?? "???",
                ItemName = item.Name ?? "",
                GiftTaste = giftTasteOnly,
                NpcName = __instance.Name
            };
            who.ActiveObject = null;
            Monitor?.Log($"[Gift] {__instance.Name}: EnableFriendshipChanges=false, only consuming item",
                LogLevel.Debug);
            return false;
        }

        int giftTaste;
        try
        {
            giftTaste = __instance.getGiftTasteForThisItem(item);
        }
        catch (InvalidOperationException ex)
        {
            giftTaste = 4;
            Monitor?.Log($"[Gift] getGiftTasteForThisItem failed for {item.Name}: {ex.Message}", LogLevel.Debug);
        }

        _cachedGifts[who.UniqueMultiplayerID.ToString()] = new CachedGiftInfo
        {
            ItemDisplayName = item.DisplayName ?? item.Name ?? "???",
            ItemName = item.Name ?? "",
            GiftTaste = giftTaste,
            NpcName = __instance.Name
        };

        // ValleyAgent 接管：通过 FriendshipSystem 应用好感度变化（递减收益）+ 消耗物品
        var delta = giftTaste switch
        {
            0 => 80, // Love
            2 => 45, // Like
            4 => 20, // Neutral
            6 => -20, // Dislike
            8 => -40, // Hate
            _ => 0
        };

        var tasteLabel = giftTaste switch
        {
            0 => "最爱", 2 => "喜欢", 4 => "一般", 6 => "不喜欢", 8 => "讨厌", _ => "未知"
        };

        if (Game1.player.friendshipData.TryGetValue(__instance.Name, out var fd))
        {
            // Issue 12: 通过 FriendshipSystem 路由好感度变化，应用递减收益
            if (FriendshipSystem != null)
            {
                var dateKey = $"{Game1.year}_{Game1.season}_{Game1.dayOfMonth}";

                // 任务3.3：设置 SpecialEvents，恢复生日/节日倍率
                var specialEvents = SpecialEventModifiers.None;
                if (__instance.isBirthday())
                {
                    specialEvents |= SpecialEventModifiers.Birthday;
                }

                if (Game1.isFestival())
                {
                    specialEvents |= SpecialEventModifiers.Festival;
                }

                var context = new FriendshipChangeContext
                {
                    NpcName = __instance.Name,
                    CurrentFriendshipPoints = fd.Points,
                    MaxFriendshipPoints = 2500,
                    InteractionContent = $"Gift: {item.DisplayName} ({tasteLabel})",
                    InteractionType = InteractionType.Gift,
                    CurrentDateKey = dateKey,
                    SpecialEvents = specialEvents
                };
                var result =
                    FriendshipSystem.ApplyDirectChange(context, delta, $"Gift: {item.DisplayName} ({tasteLabel})");
                fd.Points = Math.Clamp(result.NewPoints, 0, 2500);
                Monitor?.Log(
                    $"[Gift] {__instance.Name}: {item.DisplayName} taste={giftTaste} raw_delta={delta} → actual={result.AppliedChange} (diminishing, special={specialEvents}) → {fd.Points}",
                    LogLevel.Debug);
            }
            else
            {
                // Fallback: 无 FriendshipSystem 时直接应用（无递减收益）
                fd.Points = Math.Clamp(fd.Points + delta, 0, 2500);
                Monitor?.Log(
                    $"[Gift] {__instance.Name}: {item.DisplayName} taste={giftTaste} delta={delta} → {fd.Points}",
                    LogLevel.Debug);
            }
        }

        // 直接记录礼物记忆（不依赖AI响应），确保 TS Agent Server 断连时仍有记忆
        if (AgentService.TryGetAgent(__instance.Name, out var giftAgent))
        {
            giftAgent!.Brain.AddMemory($"玩家送给我{item.DisplayName}，我的感受是：{tasteLabel}。", 3.0, MemoryEntryType.Event);

            // 2026-08-15 步骤 3：情绪推导已迁 TS 情绪引擎（确定性事件→情绪）；
            // C# 不再机械推导（EmotionAnalyzer 删除），送礼情绪回 baseline。
            Monitor?.Log($"[Gift] {__instance.Name}: gift received (taste={giftTaste})", LogLevel.Debug);
        }

        // 消耗物品（原版也会做，但我们阻止了原版执行）
        who.ActiveObject = null;

        // 阻止原版执行
        return false;
    }

    /// <summary>
    ///     E3-3: 尝试按待成交单结算交易。玩家向 Agent NPC 献出物品时命中待成交单 → 原子结算。
    ///     结算成功消耗玩家物品 + 支付成交价，返回 true（送礼流程不再执行）；
    ///     结算失败（无待成交单 / 物品不匹配 / 结算失败）返回 false，回落到送礼流程。
    /// </summary>
    /// <summary>
    ///     E3-5 求购命中处理（2026-08-15 步骤 2 修订）。玩家献出的物品命中 NPC 当日求购单时，
    ///     不再由 C# 直接结算（经济结算已迁 TS：对话流 trade 工具 → execute_adjust 原子批，
    ///     一次完成双方钱物转移），而是拒绝本次送礼交接——物品留在玩家手里，
    ///     聊天栏提示走对话议价。未命中返回 false → 回落正常送礼流程。
    /// </summary>
    private static bool TryHandlePurchaseRequest(NPC npc, Item heldItem)
    {
        if (!(heldItem is Object heldObj) || PurchaseOffers == null)
        {
            return false;
        }

        var itemId = heldObj.QualifiedItemId ?? heldObj.ItemId ?? "";
        if (string.IsNullOrEmpty(itemId))
        {
            return false;
        }

        // 求购单匹配：只读确认（不 TryTake——求购单当日有效，成交由 TS 账本侧结算）
        var now = DateTime.UtcNow;
        var offer = PurchaseOffers.Snapshot.TryGetValue(npc.Name, out var existing) ? existing : null;
        if (offer == null || offer.IsExpired(now)
            || !offer.ItemId.Equals(itemId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // 命中求购：拒绝送礼交接 + 提示走对话（物品未消耗）
        Game1.chatBox?.addMessage(
            $"{npc.Name} 想收购 {offer.ItemName}（出价 {offer.AgreedPrice}g）——跟她聊聊价格吧",
            Color.White);
        Monitor?.Log(
            $"[PurchaseRequest] {npc.Name}: player offered {itemId} — request hit, gift hand-over refused (settle via dialogue)",
            LogLevel.Debug);
        return true;
    }

    private static void Postfix(NPC __instance, Farmer who)
    {
        // Prefix 返回 false 时 __result 为 false，但缓存仍有效
        // 通过缓存是否存在判断是否需要处理
        var farmerKey = who.UniqueMultiplayerID.ToString();
        if (!_cachedGifts.TryRemove(farmerKey, out var cached))
        {
            return;
        }

        if (AgentService == null || AgentServerProvider == null)
        {
            return;
        }

        if (!__instance.IsVillager)
        {
            return;
        }

        var itemName = cached.ItemDisplayName;
        var giftTaste = cached.GiftTaste;
        var tasteText = giftTaste switch
        {
            0 => "最爱 (Love)",
            2 => "喜欢 (Like)",
            4 => "一般 (Neutral)",
            6 => "不喜欢 (Dislike)",
            8 => "讨厌 (Hate)",
            _ => "未知"
        };

        var friendshipPoints = 0;
        if (Game1.player.friendshipData.TryGetValue(__instance.Name, out var friendship))
        {
            friendshipPoints = friendship.Points;
        }

        var npcName = __instance.Name;

        if (_consecutiveFailures.TryGetValue(npcName, out var failCount) && failCount >= MaxConsecutiveFailures)
        {
            Monitor?.Log($"[Gift] {npcName}: {failCount} consecutive failures — using local fallback (G1 fix)",
                LogLevel.Debug);
            // G1 修复：连续失败路径也调 GetLocalGiftFallback 显示本地反应文本，
            // 与 circuitBreaker OPEN 路径（line 336-361）保持一致，避免静默消失。
            var fallbackText = GetLocalGiftFallback(giftTaste, itemName);
            _mainThreadActions.Enqueue(() =>
            {
                try
                {
                    var npc = Game1.getCharacterFromName(npcName);
                    if (npc != null)
                    {
                        var dialogue = new StardewValley.Dialogue(npc, null, fallbackText);
                        npc.setNewDialogue(dialogue);
                        Game1.drawDialogue(npc);
                    }

                    if (AgentService?.TryGetAgent(npcName, out var agent) ?? false)
                    {
                        agent!.Brain.AddMemory($"我对礼物的反应：{fallbackText}", 2.0, MemoryEntryType.Event);
                    }
                }
                catch (Exception ex)
                {
                    Monitor?.Log($"[Gift] Consecutive-failure fallback display failed for {npcName}: {ex.Message}",
                        LogLevel.Warn);
                }
            });
            QueueTelemetry.WarnIfDeep("gift-actions", _mainThreadActions.Count, Monitor);
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                if (AgentServerProvider == null)
                {
                    return;
                }

                // 任务3.2：熔断器检查，TS Agent Server 宕机时使用本地回退而非静默消失
                // P0-3: 使用 TryAcquireExecution 原子获取执行权，避免 HALF_OPEN 并发失控
                var circuitBreaker = AgentService?.CircuitBreaker;
                if (circuitBreaker != null && !circuitBreaker.TryAcquireExecution())
                {
                    Monitor?.Log($"[Gift] {npcName}: circuit breaker OPEN, using local fallback", LogLevel.Warn);
                    var fallbackText = GetLocalGiftFallback(giftTaste, itemName);
                    _mainThreadActions.Enqueue(() =>
                    {
                        try
                        {
                            var npc = Game1.getCharacterFromName(npcName);
                            if (npc != null)
                            {
                                var dialogue = new StardewValley.Dialogue(npc, null, fallbackText);
                                npc.setNewDialogue(dialogue);
                                Game1.drawDialogue(npc);
                            }

                            if (AgentService?.TryGetAgent(npcName, out var agent) ?? false)
                            {
                                agent!.Brain.AddMemory($"我对礼物的反应：{fallbackText}", 2.0, MemoryEntryType.Event);
                            }
                        }
                        catch (Exception ex)
                        {
                            Monitor?.Log($"[Gift] Fallback display failed for {npcName}: {ex.Message}", LogLevel.Warn);
                        }
                    });
                    QueueTelemetry.WarnIfDeep("gift-actions", _mainThreadActions.Count, Monitor);
                    return;
                }

                // Issue 7: 根据 giftTaste 使用差异化 PlayerInput，让 LLM 生成贴合喜好的回复
                var playerInput = giftTaste switch
                {
                    0 => $"玩家送了我最爱的{itemName}！我超级开心，要表达强烈的喜爱和感谢。",
                    2 => $"玩家送了我喜欢的{itemName}。我很高兴，自然地表达感谢。",
                    4 => $"玩家送了我{itemName}。感觉一般，礼貌地表达感谢。",
                    6 => $"玩家送了我不喜欢的{itemName}。重要：我不喜欢这个礼物，要自然地表达失望或不感兴趣，但不要太过分，毕竟人家是好意。",
                    8 => $"玩家送了我最讨厌的{itemName}！重要：我非常讨厌这个礼物，要表达强烈的不满或厌恶，但保持角色性格。",
                    _ => $"玩家送给我{itemName}，我对这个礼物的评价是：{tasteText}。请根据这个评价做出自然反应。"
                };

                var request = new DialogueRequest(
                    "dialogue",
                    Guid.NewGuid().ToString("N"),
                    npcName,
                    playerInput,
                    new WorldSnapshot(
                        Game1.currentSeason,
                        Game1.dayOfMonth,
                        $"{Game1.timeOfDay / 100:D2}:{Game1.timeOfDay % 100:D2}",
                        Game1.isRaining ? Game1.isLightning ? "Stormy" : "Rainy" : Game1.isSnowing ? "Snowy" : "Sunny",
                        __instance.currentLocation?.Name ?? Game1.currentLocation?.Name ?? "Unknown",
                        new TilePosition((int)__instance.Tile.X, (int)__instance.Tile.Y),
                        "",
                        friendshipPoints,
                        "IDLE",
                        Game1.player.Items.Where(i => i != null).Select(i => new InventoryItem(i.Name, i.Stack))
                            .ToList(),
                        Game1.player.Name
                    ),
                    Game1.player.UniqueMultiplayerID.ToString()
                );

                var response = await AgentServerProvider.GenerateDialogueAsync(request).ConfigureAwait(false);

                // P0-3: 礼物对话成功，记录熔断器成功
                circuitBreaker?.RecordSuccess();

                if (!string.IsNullOrWhiteSpace(response.Speech))
                {
                    var feedbackText = response.Speech;
                    var taste = giftTaste;

                    _mainThreadActions.Enqueue(() =>
                    {
                        try
                        {
                            var npc = Game1.getCharacterFromName(npcName);
                            if (npc != null)
                            {
                                var dialogue = new StardewValley.Dialogue(npc, null, feedbackText);
                                npc.setNewDialogue(dialogue);
                                Game1.drawDialogue(npc);
                                _ = _consecutiveFailures.TryRemove(npcName, out _);
                            }

                            Monitor?.Log($"[Gift] {npcName} received {itemName} ({tasteText}): {feedbackText}",
                                LogLevel.Debug);

                            if (AgentService?.TryGetAgent(npcName, out var agent) ?? false)
                            {
                                // AI 响应追加到记忆（基础记忆已在 Prefix 中记录）
                                agent!.Brain.AddMemory($"我对礼物的反应：{feedbackText}", 2.0, MemoryEntryType.Event);
                                // 2026-08-15 步骤 3：情绪推导已迁 TS（EmotionAnalyzer 删除）。
                            }
                        }
                        catch (InvalidOperationException ex)
                        {
                            var currentFails = _consecutiveFailures.AddOrUpdate(npcName, 1, (_, count) => count + 1);
                            Monitor?.Log(
                                $"[Gift] Main-thread action failed for {npcName}: {ex.Message} (consecutive failures: {currentFails})",
                                LogLevel.Warn);
                        }
                    });
                    QueueTelemetry.WarnIfDeep("gift-actions", _mainThreadActions.Count, Monitor);
                }
            }
            catch (Exception ex)
            {
                // P0-3: 礼物对话失败，记录熔断器失败
                AgentService?.CircuitBreaker?.RecordFailure("gift_error");
                Monitor?.Log($"[Gift] Feedback generation failed for {npcName}: {ex.Message}", LogLevel.Warn);
            }
        });
    }

    /// <summary>任务3.2：本地礼物回退文本（熔断器 OPEN 时使用）</summary>
    private static string GetLocalGiftFallback(int giftTaste, string itemName)
    {
        return giftTaste switch
        {
            0 => $"哇，{itemName}！这是我最大的惊喜，谢谢你！",
            2 => $"谢谢你送我{itemName}，我很喜欢。",
            4 => $"嗯，{itemName}，谢谢你的心意。",
            6 => $"呃...{itemName}啊，谢谢，但我不是很喜欢。",
            8 => $"抱歉，{itemName}...我真的无法接受。",
            _ => $"谢谢你送我{itemName}。"
        };
    }

    private class CachedGiftInfo
    {
        public string ItemDisplayName { get; set; } = "";
        public string ItemName { get; set; } = "";
        public int GiftTaste { get; set; }
        public string NpcName { get; set; } = "";
    }
}