using System;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace ValleyAgent.Economy;

/// <summary>
///     E3-5 NPC 求购服务。逐人设生成求购（格斯收食材、克林特收矿石等），
///     求购价 1.0~1.1× 公道价，每 NPC 每天频率受限，当日有效（长 TTL）。
///     交付路径（2026-08-15 步骤 2 起）：求购单写入自持的 <see cref="PurchaseOffers" />
///     （长 TTL Registry）；NPCGiftPatch 命中时拒绝送礼交接并提示走对话议价，
///     经济结算由 TS 对话流 trade 工具 → execute_adjust 原子批完成（SettleNpcBuys
///     已随 TradeSettlement 删除——本注释 2026-09-12 修正）。
///     2026-09-12 L2 接线：<see cref="Current" /> 供 WorldSnapshotBuilder 把活跃求购
///     注入 worldSnapshot.npcPurchaseOffers（NPC 议价的价格锚）。
///     设计文档：docs/ideas/e35-implementation-思路.md
/// </summary>
public sealed class NpcPurchaseRequestService
{
    /// <summary>求购单默认有效期（覆盖到当天结束的兜底）。</summary>
    public static readonly TimeSpan DefaultRequestTtl = TimeSpan.FromHours(20);

    /// <summary>
    ///     当前服务实例（ServiceInitializer 注册时写入，BeatStore.Current 同模式）。
    ///     WorldSnapshotBuilder 读取活跃求购注入 worldSnapshot。测试环境可为 null。
    /// </summary>
    public static NpcPurchaseRequestService? Current { get; internal set; }

    // 每 NPC 每日已生成数（gameDate 隔离）。
    private readonly Dictionary<string, int> _dailyCounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private string _currentGameDate = string.Empty;

    /// <summary>
    ///     创建服务。
    /// </summary>
    /// <param name="requestTtl">求购单有效期（默认覆盖当天）。</param>
    public NpcPurchaseRequestService(TimeSpan? requestTtl = null)
    {
        PurchaseOffers = new PendingOfferRegistry(requestTtl ?? DefaultRequestTtl);
    }

    /// <summary>每 NPC 每天最大求购条数。</summary>
    public int MaxRequestsPerNpcPerDay { get; set; } = 1;

    /// <summary>求购价倍率下限（× 公道价）。</summary>
    public double PriceMultiplierMin { get; set; } = 1.0;

    /// <summary>求购价倍率上限（× 公道价，含）。</summary>
    public double PriceMultiplierMax { get; set; } = 1.1;

    /// <summary>
    ///     主动求购触发概率 [0,1]（来自 ModConfig.ProactiveTradeProbability，默认 1.0）。
    ///     在 TryGenerate 入口做概率门控：未通过则不生成求购单。
    ///     默认 1.0 保证既有测试全量通过；0.0 = 禁用主动求购；0.15 = 15% 概率触发。
    /// </summary>
    public float TradeProbability { get; set; } = 1.0f;

    /// <summary>当日有效求购单注册表（长 TTL，与还价 30s Registry 分离）。</summary>
    public PendingOfferRegistry PurchaseOffers { get; }

    /// <summary>
    ///     尝试为 NPC 生成一条求购。生成条件：
    ///     1) 当天未超频（&lt; MaxRequestsPerNpcPerDay）；
    ///     2) 该 NPC 无未过期求购单（Registry 单待成交单约束）；
    ///     3) 档案有求购偏好（PurchaseItems 非空）。
    ///     生成成功后写入 PurchaseOffers 并返回请求（用于聊天栏公告）。
    /// </summary>
    /// <param name="profile">NPC 经济档案（含求购偏好）。</param>
    /// <param name="gameDate">游戏日期（"Y1_spring_1"），频率隔离。</param>
    /// <param name="itemSalePrice">求购物品基准售价解析器（itemId → 售价，无此物品返回 ≤0）。</param>
    /// <param name="itemDisplayName">求购物品显示名解析器（itemId → 名称）。</param>
    /// <param name="nowUtc">当前 UTC（测试注入）。</param>
    public PurchaseRequest? TryGenerate(
        NpcEconomyProfile? profile,
        string gameDate,
        Func<string, int> itemSalePrice,
        Func<string, string> itemDisplayName,
        DateTime? nowUtc = null)
    {
        if (profile == null || string.IsNullOrWhiteSpace(profile.Name))
        {
            return null;
        }

        if (profile.PurchaseItems == null || profile.PurchaseItems.Count == 0)
        {
            return null; // 无求购偏好 → 不生成
        }

        // 概率门控：在频率/偏好判定前做随机检查，未通过则不生成求购单
        if (RandomNumberGenerator.GetInt32(100) >= TradeProbability * 100)
        {
            return null;
        }

        var now = nowUtc ?? DateTime.UtcNow;

        lock (_lock)
        {
            // 日切：新日期重置计数
            if (!string.Equals(_currentGameDate, gameDate, StringComparison.OrdinalIgnoreCase))
            {
                _dailyCounts.Clear();
                _currentGameDate = gameDate;
            }

            if (_dailyCounts.TryGetValue(profile.Name, out var count) && count >= MaxRequestsPerNpcPerDay)
            {
                return null; // 当日已超频
            }
        }

        // 选求购物品：确定性 FNV 哈希从偏好池选一个（同 NPC+日期 → 稳定，跨天变化）
        var itemId = PickPreferredItem(profile.Name, profile.PurchaseItems, gameDate);
        if (string.IsNullOrWhiteSpace(itemId))
        {
            return null;
        }

        var salePrice = itemSalePrice(itemId);
        if (salePrice <= 0)
        {
            return null; // 游戏内无此物品（配置漂移）→ 跳过
        }

        var fair = PricingEngine.CalculateFairPrice(salePrice);
        var multiplier = PickMultiplier(profile.Name, itemId, gameDate);
        var price = Math.Max(1, (int)(fair * multiplier));

        var offer = new PendingOffer(
            profile.Name,
            itemId,
            itemDisplayName(itemId),
            price,
            1,
            TradeDirection.NpcBuysPlayerItem,
            now + DefaultRequestTtl);

        if (!PurchaseOffers.TryCreate(offer, now))
        {
            return null; // 已有未过期求购单 → 不重复
        }

        lock (_lock)
        {
            _dailyCounts[profile.Name] = _dailyCounts.TryGetValue(profile.Name, out var c) ? c + 1 : 1;
        }

        return new PurchaseRequest(profile.Name, itemId, offer.ItemName, 1, price);
    }

    /// <summary>作废某 NPC 的求购单（跨图/离场）。返回是否确实作废了一张。</summary>
    public bool VoidFor(string npcName) => PurchaseOffers.VoidFor(npcName);

    /// <summary>清空全部求购单与当日计数（换日/睡眠）。</summary>
    public void ResetDaily(string gameDate)
    {
        _ = gameDate;
        PurchaseOffers.VoidAll();
        lock (_lock)
        {
            _dailyCounts.Clear();
            _currentGameDate = string.Empty;
        }
    }

    /// <summary>FNV-1a 确定性选偏好物品（同 NPC+日期 → 稳定）。</summary>
    private static string PickPreferredItem(string npcName, List<string> pool, string gameDate)
    {
        const uint offset = 2166136261;
        const uint prime = 16777619;
        var hash = offset;
        foreach (var c in npcName + "\u0001" + gameDate)
        {
            hash ^= c;
            hash *= prime;
        }

        return pool[(int)(hash % (uint)pool.Count)];
    }

    /// <summary>FNV-1a 确定性倍率：[Min, Max] 区间内 100 分位（1.00~1.10 步进 0.01）。</summary>
    private double PickMultiplier(string npcName, string itemId, string gameDate)
    {
        const uint offset = 2166136261;
        const uint prime = 16777619;
        var hash = offset;
        foreach (var c in npcName + "\u0001" + itemId + "\u0001" + gameDate)
        {
            hash ^= c;
            hash *= prime;
        }

        var steps = (int)((PriceMultiplierMax - PriceMultiplierMin) * 100);
        var idx = (int)(hash % (uint)(steps + 1));
        return PriceMultiplierMin + idx / 100.0;
    }

    /// <summary>一条生成的求购单（发布用，含 ItemName 便于聊天栏公告）。</summary>
    public sealed record PurchaseRequest(
        string NpcName,
        string ItemId,
        string ItemName,
        int Quantity,
        int Price);
}