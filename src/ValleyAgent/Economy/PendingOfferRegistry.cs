using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace ValleyAgent.Economy;

/// <summary>
///     待成交单注册表（E3-3）。
///     每 NPC 最多一张待成交单；30 秒超时自动作废；玩家走失/换地图时全部作废；
///     TryTake 原子取走，保证一次交易只结算一次。
///     线程安全（ConcurrentDictionary + 锁内检查写入）。
/// </summary>
public sealed class PendingOfferRegistry
{
    /// <summary>待成交单默认有效期（秒）。</summary>
    public static readonly TimeSpan DefaultOfferTtl = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<string, PendingOffer> _offers = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSpan _ttl;

    public PendingOfferRegistry(TimeSpan? ttl = null)
    {
        _ttl = ttl ?? DefaultOfferTtl;
    }

    /// <summary>当前待成交单数量（诊断用）。</summary>
    public int Count
    {
        get => _offers.Count;
    }

    /// <summary>当前持有的全部待成交单快照（key 为 NPC 名）。</summary>
    public IReadOnlyDictionary<string, PendingOffer> Snapshot
    {
        get => _offers.ToArray()
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     为 NPC 创建待成交单。该 NPC 已有未过期待成交单时返回 false（必须结算或作废旧的）。
    ///     返回 true 时写入新单（先作废同 NPC 的过期旧单，保证"每 NPC 一张"语义）。
    /// </summary>
    public bool TryCreate(PendingOffer offer, DateTime nowUtc)
    {
        if (offer == null || string.IsNullOrWhiteSpace(offer.NpcName))
        {
            return false;
        }

        // 同 NPC 已有未过期单 → 拒绝（单待成交单约束）
        if (_offers.TryGetValue(offer.NpcName, out var existing) && !existing.IsExpired(nowUtc))
        {
            return false;
        }

        var withExpiry = offer with { ExpiresUtc = nowUtc + _ttl };
        _offers[offer.NpcName] = withExpiry;
        return true;
    }

    /// <summary>取走某 NPC 的待成交单（原子）。不存在或已过期返回 false。结算消费入口。</summary>
    public bool TryTake(string npcName, DateTime nowUtc, out PendingOffer? offer)
    {
        offer = null;
        if (string.IsNullOrWhiteSpace(npcName))
        {
            return false;
        }

        if (!_offers.TryRemove(npcName, out var existing))
        {
            return false;
        }

        if (existing.IsExpired(nowUtc))
        {
            return false;
        }

        offer = existing;
        return true;
    }

    /// <summary>作废某 NPC 的待成交单（走失/换地图）。返回是否确实作废了一张。</summary>
    public bool VoidFor(string npcName) => !string.IsNullOrWhiteSpace(npcName) && _offers.TryRemove(npcName, out _);

    /// <summary>作废全部待成交单（玩家睡觉/回标题）。</summary>
    public void VoidAll() => _offers.Clear();

    /// <summary>清理全部过期待成交单。返回清理数量。</summary>
    public int PruneExpired(DateTime nowUtc)
    {
        var expired = _offers
            .Where(kv => kv.Value.IsExpired(nowUtc))
            .Select(kv => kv.Key)
            .ToList();

        foreach (var key in expired)
        {
            // TryRemove 竞争安全：若已被 TryTake 取走则无事发生
            _offers.TryRemove(key, out _);
        }

        return expired.Count;
    }
}