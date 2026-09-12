using System.Threading;
using System.Threading.Tasks;

namespace ValleyAgent.Multiplayer.Transports;

/// <summary>
///     送礼评估结果（本地化精简版，替代原 GiftEvalResponse）。
///     gift_eval 管道已删除（TS 端无路由），HostGiftTransport 始终用本地兜底；
///     FarmhandGiftTransport 通过 ModMessage 透传主机计算的结果。
/// </summary>
public record GiftReactionResult(
    string Taste,
    int FriendshipDelta,
    string Reaction,
    string Emotion = "Neutral"
);

/// <summary>
///     送礼传输抽象。主机端直连 FriendshipSystem（本地兜底反应文本）；
///     farmhand 端通过 ModMessage 代理到主机。
/// </summary>
public interface IGiftTransport
{
    /// <summary>
    ///     发起送礼评估请求。
    /// </summary>
    /// <param name="npcName">NPC 名称。</param>
    /// <param name="itemId">物品 ID（SDV 1.6 qualified ID，如 "(O)16"）。</param>
    /// <param name="quantity">送礼数量。</param>
    /// <param name="requesterPlayerId">送礼发起玩家 UniqueMultiplayerID；0 表示当前本地玩家（单机/主机本人）。
    ///     主机代房客评估时必须传发起者，好感基线/每日计数才落在正确玩家身上（2026-08-23 审计 P1）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>礼物评估结果（含 NPC 反应文本、表情、好感变化）。</returns>
    public Task<GiftReactionResult> SendAsync(string npcName, string itemId, int quantity,
        long requesterPlayerId = 0, CancellationToken ct = default);
}