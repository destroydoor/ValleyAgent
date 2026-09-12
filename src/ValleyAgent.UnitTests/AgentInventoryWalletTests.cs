using System.Reflection;
using ValleyAgent.Inventory;
using Xunit;

namespace ValleyAgent.UnitTests;

/// <summary>
///     E3-1 钱包单元测试。
///     验证：默认余额 0；AddMoney/TrySpend/TrySpendWithChange 的原子语义与事件回执；
///     非法参数不破坏状态；并发扣款不超支不为负；事件类型与 ItemChangeRecord 同构。
///     设计文档：docs/ideas/e31-implementation-思路.md §2.1 / §6。
/// </summary>
public class AgentInventoryWalletTests
{
    [Fact]
    public void Money_DefaultsToZero()
    {
        var inv = new AgentInventory();
        Assert.Equal(0, inv.Money);
    }

    [Fact]
    public void AddMoney_IncreasesBalance_AndFiresEventWithPositiveDelta()
    {
        var inv = new AgentInventory { NpcName = "Abigail" };
        var events = new List<OnWalletChangedEventArgs>();
        inv.OnWalletChanged += events.Add;

        var newBalance = inv.AddMoney(500, "paid_wage");

        Assert.Equal(500, newBalance);
        Assert.Equal(500, inv.Money);
        var evt = Assert.Single(events);
        Assert.Equal("Abigail", evt.NpcName);
        Assert.Equal(500, evt.Amount); // 收入为正
        Assert.Equal(500, evt.NewBalance); // 回执 = 事件后余额
        Assert.Equal("paid_wage", evt.Reason);
    }

    [Fact]
    public void AddMoney_NonPositiveAmount_Throws_AndDoesNotMutate()
    {
        var inv = new AgentInventory();
        inv.Money = 100;

        Assert.Throws<ArgumentOutOfRangeException>(() => inv.AddMoney(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => inv.AddMoney(-50));
        Assert.Equal(100, inv.Money);
    }

    [Fact]
    public void TrySpend_SufficientBalance_Deducts_AndFiresEventWithNegativeDelta()
    {
        var inv = new AgentInventory { NpcName = "Clint" };
        inv.Money = 1000;
        var events = new List<OnWalletChangedEventArgs>();
        inv.OnWalletChanged += events.Add;

        var success = inv.TrySpend(300, "bought_copper");

        Assert.True(success);
        Assert.Equal(700, inv.Money);
        var evt = Assert.Single(events);
        Assert.Equal(-300, evt.Amount); // 支出为负
        Assert.Equal(700, evt.NewBalance);
        Assert.Equal("bought_copper", evt.Reason);
    }

    [Fact]
    public void TrySpend_InsufficientBalance_ReturnsFalse_NoChange_NoEvent()
    {
        var inv = new AgentInventory();
        inv.Money = 100;
        var eventCount = 0;
        inv.OnWalletChanged += _ => eventCount++;

        var success = inv.TrySpend(101);

        Assert.False(success);
        Assert.Equal(100, inv.Money);
        Assert.Equal(0, eventCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void TrySpend_NonPositiveAmount_ReturnsFalse_NoEvent(int amount)
    {
        var inv = new AgentInventory();
        inv.Money = 100;
        var eventCount = 0;
        inv.OnWalletChanged += _ => eventCount++;

        var success = inv.TrySpend(amount);

        Assert.False(success);
        Assert.Equal(100, inv.Money);
        Assert.Equal(0, eventCount);
    }

    [Fact]
    public void TrySpendWithChange_ReturnsRemainingBalanceAsChange()
    {
        var inv = new AgentInventory();
        inv.Money = 500;

        var success = inv.TrySpendWithChange(120, out var change);

        Assert.True(success);
        Assert.Equal(380, change); // 找零 = 花费后剩余余额
        Assert.Equal(380, inv.Money);
    }

    [Fact]
    public void TrySpendWithChange_InsufficientBalance_ReturnsFalse_ChangeZero()
    {
        var inv = new AgentInventory();
        inv.Money = 50;

        var success = inv.TrySpendWithChange(60, out var change);

        Assert.False(success);
        Assert.Equal(0, change);
        Assert.Equal(50, inv.Money);
    }

    [Fact]
    public void Money_DirectAssignment_IsSilent_NoEvent()
    {
        var inv = new AgentInventory();
        var eventCount = 0;
        inv.OnWalletChanged += _ => eventCount++;

        inv.Money = 999; // 存档恢复/初始化的静默路径

        Assert.Equal(999, inv.Money);
        Assert.Equal(0, eventCount);
    }

    [Fact]
    public void SubscriberException_DoesNotBreakWalletOperation()
    {
        var inv = new AgentInventory();
        inv.Money = 100;
        inv.OnWalletChanged += _ => throw new InvalidOperationException("observer bug");

        var success = inv.TrySpend(40);

        Assert.True(success); // 订阅者异常被隔离，操作本身成功
        Assert.Equal(60, inv.Money);
    }

    [Fact]
    public async Task ConcurrentSpends_NeverExceedBalance_AndNeverGoNegative()
    {
        var inv = new AgentInventory();
        inv.Money = 1000;
        var spent = 0;
        inv.OnWalletChanged += e =>
        {
            if (e.Amount < 0)
            {
                Interlocked.Add(ref spent, -e.Amount);
            }
        };

        var tasks = Enumerable.Range(0, 40)
            .Select(_ => Task.Run(() => inv.TrySpend(100)))
            .ToArray();
        await Task.WhenAll(tasks);

        var successCount = tasks.Count(t => t.Result);
        Assert.True(successCount is >= 0 and <= 10, $"最多 10 次成功（余额 1000 / 每次 100），实际 {successCount}");
        Assert.True(inv.Money >= 0, $"余额不允许为负，实际 {inv.Money}");
        Assert.Equal(1000, inv.Money + spent); // 守恒：剩余 + 已花 = 初始
    }

    [Fact]
    public void OnWalletChanged_Declared_WithExpectedArgsType()
    {
        // 镜像 TranscriptSinkTests 对 OnItemChanged 的反射断言：契约显式化，防止签名漂移
        var inv = new AgentInventory();
        var evt = inv.GetType().GetEvent("OnWalletChanged", BindingFlags.Public | BindingFlags.Instance)
                  ?? throw new InvalidOperationException("OnWalletChanged event not declared");
        Assert.Equal(typeof(Action<OnWalletChangedEventArgs>), evt.EventHandlerType);

        // 事件可被订阅/取消订阅（无订阅者时为 null，C# 语言保证调用安全）
        Action<OnWalletChangedEventArgs>? handler = _ => { };
        inv.OnWalletChanged += handler;
        inv.OnWalletChanged -= handler;
    }
}