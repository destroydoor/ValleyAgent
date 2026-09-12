using System;
using System.Threading;
using System.Threading.Tasks;
using StardewModdingAPI;
using StardewValley;
using ValleyAgent.Brain;
using ValleyAgent.Friendship;

namespace ValleyAgent.Multiplayer.Transports;

/// <summary>
///     主机端送礼传输：直接调用 FriendshipSystem.ApplyDirectChange（mirror NPCGiftPatch.Prefix）
///     + 本地兜底反应文本（gift_eval 管道已删除，TS 端无路由）。
///     不修改 Game1.player.friendshipData——仅计算 delta 返回给调用方（HostRequestHandlers），
///     由 farmhand 端在收到 GiftResponseMessage 后应用到自己本地的 friendshipData。
/// </summary>
public class HostGiftTransport : IGiftTransport
{
    private readonly FriendshipSystem _friendshipSystem;
    private readonly IMonitor? _monitor;

    /// <summary>构造函数。注入主机端 FriendshipSystem。</summary>
    public HostGiftTransport(FriendshipSystem friendshipSystem, IMonitor? monitor = null)
    {
        _friendshipSystem = friendshipSystem ?? throw new ArgumentNullException(nameof(friendshipSystem));
        _monitor = monitor;
    }

    /// <summary>
    ///     评估送礼请求：计算 giftTaste + delta + 应用 FriendshipSystem.ApplyDirectChange（记录历史/每日计数）
    ///     + 生成本地兜底反应文本（gift_eval 管道已删除，不再调用 LLM）。
    /// </summary>
    public async Task<GiftReactionResult> SendAsync(string npcName, string itemId, int quantity,
        long requesterPlayerId = 0, CancellationToken ct = default)
    {
        _ = quantity; // 当前按单件评估（与 NPCGiftPatch.Prefix 一致）；数量保留以便未来扩展

        try
        {
            // 1. 解析 NPC
            var npc = Game1.getCharacterFromName(npcName);
            if (npc == null)
            {
                _monitor?.Log($"[HostGiftTransport] NPC '{npcName}' not found", LogLevel.Warn);
                return BuildFallbackResponse("（NPC 不存在）");
            }

            // 2. 解析物品（SDV 1.6 qualified itemId，如 "(O)16"）
            Item? item = null;
            try
            {
                item = ItemRegistry.Create(itemId, allowNull: true);
            }
            catch (Exception ex)
            {
                _monitor?.Log($"[HostGiftTransport] ItemRegistry.Create failed for '{itemId}': {ex.Message}",
                    LogLevel.Warn);
            }

            if (item == null)
            {
                _monitor?.Log($"[HostGiftTransport] Item '{itemId}' not found", LogLevel.Warn);
                return BuildFallbackResponse("（物品不存在）");
            }

            // 3. 计算 giftTaste（mirror NPCGiftPatch.Prefix）
            int giftTaste;
            try
            {
                giftTaste = npc.getGiftTasteForThisItem(item);
            }
            catch (InvalidOperationException)
            {
                giftTaste = 4;
            }

            // 4. 计算 raw delta（mirror NPCGiftPatch.Prefix）
            var delta = giftTaste switch
            {
                0 => 80, // Love
                2 => 45, // Like
                4 => 20, // Neutral
                6 => -20, // Dislike
                8 => -40, // Hate
                _ => 0
            };
            var tasteLabel = MapGiftTasteToLabel(giftTaste);
            var itemDisplayName = item.DisplayName ?? item.Name ?? "???";

            // 5. 应用 FriendshipSystem.ApplyDirectChange（mirror NPCGiftPatch.Prefix）
            //    注意：不修改任何玩家的 friendshipData——仅计算 delta + 记录历史/每日计数。
            //    2026-08-23 审计 P1：baseline 必须取"送礼发起玩家"的好感。房客中继场景下
            //    Game1.player 是主机，按主机亲密度算递减收益会把 delta 幅度算错；
            //    requesterPlayerId 解析不到（已离线等）时回落 0 基线，不落回主机数据。
            var appliedChange = delta;
            var baselineFriendship = 0;
            var giftFarmer = requesterPlayerId != 0 ? Game1.GetPlayer(requesterPlayerId) : null;
            if (giftFarmer == null && requesterPlayerId == 0)
            {
                giftFarmer = Game1.player;
            }

            if (giftFarmer != null && giftFarmer.friendshipData.TryGetValue(npcName, out var fd))
            {
                baselineFriendship = fd.Points;
            }

            var dateKey = $"{Game1.year}_{Game1.season}_{Game1.dayOfMonth}";
            var specialEvents = SpecialEventModifiers.None;
            if (npc.isBirthday())
            {
                specialEvents |= SpecialEventModifiers.Birthday;
            }

            if (Game1.isFestival())
            {
                specialEvents |= SpecialEventModifiers.Festival;
            }

            var context = new FriendshipChangeContext
            {
                NpcName = npcName,
                CurrentFriendshipPoints = baselineFriendship,
                MaxFriendshipPoints = 2500,
                InteractionContent = $"Gift: {itemDisplayName} ({tasteLabel})",
                InteractionType = InteractionType.Gift,
                CurrentDateKey = dateKey,
                SpecialEvents = specialEvents
            };
            var result = _friendshipSystem.ApplyDirectChange(context, delta, $"Gift: {itemDisplayName} ({tasteLabel})");
            appliedChange = result.AppliedChange;

            // 6. 本地兜底反应文本（gift_eval 管道已删除）
            var reaction = GetLocalGiftFallback(giftTaste, itemDisplayName);
            var emotion = DeriveEmotion(giftTaste);

            return new GiftReactionResult(
                tasteLabel,
                appliedChange,
                reaction,
                emotion);
        }
        catch (Exception ex)
        {
            _monitor?.Log($"[HostGiftTransport] SendAsync failed for {npcName}: {ex}", LogLevel.Error);
            return BuildFallbackResponse("（主机处理礼物失败）");
        }
    }

    private static string MapGiftTasteToLabel(int giftTaste)
    {
        return giftTaste switch
        {
            0 => "最爱",
            2 => "喜欢",
            4 => "一般",
            6 => "不喜欢",
            8 => "讨厌",
            _ => "未知"
        };
    }

    private static string DeriveEmotion(int giftTaste)
    {
        // 2026-08-15 步骤 3：情绪推导已迁 TS 情绪引擎；传输层降级为中性（farmhand 气泡显示）。
        _ = giftTaste;
        return nameof(NpcEmotion.Neutral);
    }

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

    private static GiftReactionResult BuildFallbackResponse(string reaction)
    {
        return new GiftReactionResult(
            "未知",
            0,
            reaction);
    }
}