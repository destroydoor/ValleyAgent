using System;

namespace ValleyAgent.Economy;

/// <summary>
///     交易方向：NPC 买玩家物品（现阶段唯一开放方向，NPC 卖物品给玩家由 E3-5 求购覆盖）。
/// </summary>
public enum TradeDirection
{
    NpcBuysPlayerItem
}

/// <summary>
///     单个待成交单（E3-3）。由还价状态机成交后创建，玩家献出物品时由 TradeSettlement 消费。
///     不可变 record：创建后不可改，作废/结算均从 Registry 移除而非原地修改。
/// </summary>
/// <param name="NpcName">NPC 名（key，大小写不敏感）。</param>
/// <param name="ItemId">成交物品的 qualified item id（如 "(O)128"）。</param>
/// <param name="ItemName">成交物品显示名（用于话术与结算回执）。</param>
/// <param name="AgreedPrice">还价后双方同意的成交价（NPC 付给玩家的金额）。</param>
/// <param name="Quantity">成交数量（默认 1）。</param>
/// <param name="Direction">交易方向。</param>
/// <param name="ExpiresUtc">过期时间（UtcNow + 30s，到期自动作废）。</param>
public sealed record PendingOffer(
    string NpcName,
    string ItemId,
    string ItemName,
    int AgreedPrice,
    int Quantity,
    TradeDirection Direction,
    DateTime ExpiresUtc)
{
    /// <summary>是否已过期（用注入的 now 判定，便于测试）。</summary>
    public bool IsExpired(DateTime nowUtc) => nowUtc >= ExpiresUtc;
}