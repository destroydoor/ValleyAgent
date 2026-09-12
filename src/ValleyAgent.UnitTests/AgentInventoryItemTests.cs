using StardewValley;
using ValleyAgent.Inventory;
using Xunit;
using SObject = StardewValley.Object;

namespace ValleyAgent.UnitTests;

/// <summary>
///     AgentInventory 物品槽位单元测试（2026-08-17 审计补全）。
///     验证：TryAdd 的堆叠/空槽/跨槽位路径，以及原子性回归——
///     旧实现"先合并再找空槽"，容量不足时部分堆叠已入栈（P1#3），
///     新实现先预检可吸收总量，不足整批零副作用拒绝。
///     单测环境游戏数据未加载：合成物品走 FakeObject（同 AdjustExecutorTests 约定）。
/// </summary>
public class AgentInventoryItemTests
{
    [Fact]
    public void TryAdd_ToFreeSlot_Succeeds()
    {
        var inv = new AgentInventory();

        var ok = inv.TryAdd(MakeItem("apple", 3));

        Assert.True(ok);
        var item = Assert.Single(NonNull(inv.GetAllItems()));
        Assert.Equal("apple", item.ItemId);
        Assert.Equal(3, item.Stack);
    }

    [Fact]
    public void TryAdd_StacksWithExistingSameType()
    {
        var inv = new AgentInventory();
        Assert.True(inv.TryAdd(MakeItem("apple", 500)));

        var ok = inv.TryAdd(MakeItem("apple", 100));

        Assert.True(ok);
        var item = Assert.Single(NonNull(inv.GetAllItems()));
        Assert.Equal(600, item.Stack); // 合并而非占新槽
    }

    [Fact]
    public void TryAdd_OverflowingStackSpreadsAcrossSlots()
    {
        var inv = new AgentInventory();
        // 998/999 + 1 个空槽：加 5 个 → 1 个并入（999），剩余 4 个落空槽
        Assert.True(inv.TryAdd(MakeItem("apple", 998)));

        var ok = inv.TryAdd(MakeItem("apple", 5));

        Assert.True(ok);
        var items = NonNull(inv.GetAllItems()).ToList();
        Assert.Equal(2, items.Count);
        Assert.Contains(items, i => i.Stack == 999);
        Assert.Contains(items, i => i.Stack == 4);
    }

    [Fact]
    public void TryAdd_CapacityInsufficient_ReturnsFalse_ZeroSideEffects()
    {
        // 回归测试（P1#3）：12 槽全满 + 可堆叠余量只有 1，加 5 个必然失败——
        // 旧实现会先把 1 个并入现有堆叠再返回 false（部分副作用）；
        // 原子语义要求零副作用：现有堆叠不变、传入物品 Stack 不被吞。
        var inv = new AgentInventory();
        for (var i = 0; i < 11; i++)
        {
            Assert.True(inv.TryAdd(MakeItem($"fill{i}", 1)));
        }

        Assert.True(inv.TryAdd(MakeItem("apple", 998))); // 第 12 槽：998/999
        var incoming = MakeItem("apple", 5);

        var ok = inv.TryAdd(incoming);

        Assert.False(ok);
        Assert.Equal(12, inv.Count); // 未占新槽
        var apple = NonNull(inv.GetAllItems()).Single(i => i.ItemId == "apple");
        Assert.Equal(998, apple.Stack); // 现有堆叠未被部分合并
        Assert.Equal(5, incoming.Stack); // 传入物品 Stack 未被吞
    }

    [Fact]
    public void TryAdd_NullItem_ReturnsFalse()
    {
        var inv = new AgentInventory();

        Assert.False(inv.TryAdd(null!));
    }

    // ── 测试物品工厂（同 AdjustExecutorTests 约定：游戏数据未加载，走合成物品）──

    private sealed class FakeObject : SObject
    {
        public FakeObject(string itemId, int stack)
        {
            ItemId = itemId;
            Stack = stack;
        }

        protected override Item GetOneNew() => new FakeObject(ItemId, Stack);
    }

    private static FakeObject MakeItem(string itemId, int stack) => new(itemId, stack);

    private static IEnumerable<Item> NonNull(Item?[] slots) => slots.Where(i => i != null)!;
}
