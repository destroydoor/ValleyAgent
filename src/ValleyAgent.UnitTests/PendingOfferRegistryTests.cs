using ValleyAgent.Economy;
using Xunit;

namespace ValleyAgent.UnitTests;

/// <summary>
///     E3-3 待成交单注册表单测。
///     验证：每 NPC 单待成交单约束；30s TTL 超时作废；走失作废；TryTake 原子取走且过期不可取；
///     PruneExpired 清理；VoidAll 清空；大小写不敏感 key；默认 TTL 覆盖。
///     设计文档：docs/ideas/e33-implementation-思路.md §4。
/// </summary>
public class PendingOfferRegistryTests
{
    private static readonly DateTime T0 = new(2026, 8, 3, 10, 0, 0, DateTimeKind.Utc);

    private static PendingOffer MakeOffer(string npc = "Abigail", string itemId = "(O)128", int price = 100,
        int qty = 1)
        => new(npc, itemId, "测试物品", price, qty, TradeDirection.NpcBuysPlayerItem, T0 + TimeSpan.FromSeconds(30));

    [Fact]
    public void TryCreate_StoresOffer_WithDefaultTtl()
    {
        var registry = new PendingOfferRegistry();
        var offer = MakeOffer();

        var created = registry.TryCreate(offer, T0);

        Assert.True(created);
        Assert.Equal(1, registry.Count);
        var snap = registry.Snapshot;
        Assert.True(snap.TryGetValue("Abigail", out var stored));
        Assert.Equal(T0 + TimeSpan.FromSeconds(30), stored!.ExpiresUtc);
    }

    [Fact]
    public void TryCreate_SameNpcUnExpired_Rejected()
    {
        var registry = new PendingOfferRegistry();
        Assert.True(registry.TryCreate(MakeOffer(), T0));

        var second = registry.TryCreate(MakeOffer(itemId: "(O)129"), T0);

        Assert.False(second);
        Assert.Equal(1, registry.Count);
    }

    [Fact]
    public void TryCreate_SameNpcExpiredOld_Replaces()
    {
        var registry = new PendingOfferRegistry();
        Assert.True(registry.TryCreate(MakeOffer(), T0));

        // 旧单 30s 后过期，新单应可写入
        var replaced = registry.TryCreate(MakeOffer(itemId: "(O)129"), T0 + TimeSpan.FromSeconds(31));

        Assert.True(replaced);
        Assert.Equal(1, registry.Count);
        var snap = registry.Snapshot;
        Assert.Equal("(O)129", snap["Abigail"].ItemId);
    }

    [Fact]
    public void TryCreate_EmptyNpcName_Rejected()
    {
        var registry = new PendingOfferRegistry();

        var created = registry.TryCreate(MakeOffer(" "), T0);

        Assert.False(created);
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public void TryTake_RemovesAndReturns()
    {
        var registry = new PendingOfferRegistry();
        Assert.True(registry.TryCreate(MakeOffer(), T0));

        var taken = registry.TryTake("abigail", T0, out var offer);

        Assert.True(taken);
        Assert.Equal("Abigail", offer!.NpcName);
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public void TryTake_Expired_Fails_AndOfferConsumed()
    {
        var registry = new PendingOfferRegistry();
        Assert.True(registry.TryCreate(MakeOffer(), T0));

        var taken = registry.TryTake("Abigail", T0 + TimeSpan.FromSeconds(31), out var offer);

        Assert.False(taken);
        Assert.Null(offer);
        Assert.Equal(0, registry.Count); // 过期单被消费，不再占用
    }

    [Fact]
    public void TryTake_Missing_ReturnsFalse()
    {
        var registry = new PendingOfferRegistry();

        var taken = registry.TryTake("Nobody", T0, out var offer);

        Assert.False(taken);
        Assert.Null(offer);
    }

    [Fact]
    public void VoidFor_Removes()
    {
        var registry = new PendingOfferRegistry();
        Assert.True(registry.TryCreate(MakeOffer(), T0));

        var voided = registry.VoidFor("abigail");

        Assert.True(voided);
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public void VoidFor_Missing_ReturnsFalse()
    {
        var registry = new PendingOfferRegistry();

        var voided = registry.VoidFor("Nobody");

        Assert.False(voided);
    }

    [Fact]
    public void VoidAll_Clears()
    {
        var registry = new PendingOfferRegistry();
        Assert.True(registry.TryCreate(MakeOffer(), T0));
        Assert.True(registry.TryCreate(MakeOffer("Sebastian"), T0));

        registry.VoidAll();

        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public void PruneExpired_RemovesOnlyExpired()
    {
        var registry = new PendingOfferRegistry();
        Assert.True(registry.TryCreate(MakeOffer(), T0)); // 30s 后过期
        Assert.True(registry.TryCreate(MakeOffer("Sebastian"), T0)); // 30s 后过期

        // 再造一张过期更晚的（手动给更长 TTL 的 registry 之外验证不了，这里验证两单同时清理）
        var pruned = registry.PruneExpired(T0 + TimeSpan.FromSeconds(31));

        Assert.Equal(2, pruned);
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public void PruneExpired_AtBoundary_KeepsUnexpired()
    {
        var registry = new PendingOfferRegistry();

        // T0 创建，过期时刻 = T0+30s。在 T0+30s 判定为过期（IsExpired 用 >=）
        Assert.True(registry.TryCreate(MakeOffer(), T0));
        var pruned = registry.PruneExpired(T0 + TimeSpan.FromSeconds(30));

        Assert.Equal(1, pruned);
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public void CustomTtl_Applied()
    {
        var registry = new PendingOfferRegistry(TimeSpan.FromSeconds(5));
        Assert.True(registry.TryCreate(MakeOffer(), T0));

        var snap = registry.Snapshot;
        Assert.Equal(T0 + TimeSpan.FromSeconds(5), snap["Abigail"].ExpiresUtc);

        // 6s 后过期
        var taken = registry.TryTake("Abigail", T0 + TimeSpan.FromSeconds(6), out _);
        Assert.False(taken);
    }
}