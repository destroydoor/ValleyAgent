using ValleyAgent.Config;
using ValleyAgent.Services;
using ValleyAgent.StateMachine;
using Xunit;

namespace ValleyAgent.UnitTests.Services;

/// <summary>
///     阶段 3 扩展：AgentService.ApplyNpcConfigInitialization（npc-configs 钩子）单测。
///     验证：合法配置写入 Money + Inventory；无配置 no-op；非法 itemId 静默跳过；InitialMoney==0 不覆盖。
///     注：ItemRegistry.Create 在游戏数据未初始化的环境（标题屏/某些单测路径）抛 NullReferenceException
///     或返回 null，钩子必须 try/catch + null check 跳过该项（与 DirectorTools.set_npc_inventory 同款契约）。
/// </summary>
public class NpcConfigInitializationTests
{
    private static readonly string[] UnknownItemIdArray = { "(O)99999" };

    private static AgentInstance NewAgent(string name) =>
        new(name, new AgentStateMachine(), maxHealth: 100);

    [Fact]
    public void Apply_NullConfig_IsNoop()
    {
        var agent = NewAgent("Nobody");
        agent.Inventory.Money = 999;

        AgentService.ApplyNpcConfigInitialization(agent, null);

        Assert.Equal(999, agent.Inventory.Money); // 未变
        Assert.Equal(0, agent.Inventory.Count);
    }

    [Fact]
    public void Apply_ConfigWithMoney_OverridesExisting()
    {
        var agent = NewAgent("Shane");
        agent.Inventory.Money = 0;
        var config = MakeConfig("Shane", initialMoney: 250, initialInventory: Array.Empty<string>());

        AgentService.ApplyNpcConfigInitialization(agent, config);

        Assert.Equal(250, agent.Inventory.Money);
    }

    [Fact]
    public void Apply_ConfigWithZeroMoney_KeepsExisting()
    {
        // 语义约定：InitialMoney==0（缺省值）表示"不覆盖"，保留 EconomyProfiles 已写入的值。
        var agent = NewAgent("Pierre");
        agent.Inventory.Money = 1234;
        var config = MakeConfig("Pierre", initialMoney: 0, initialInventory: Array.Empty<string>());

        AgentService.ApplyNpcConfigInitialization(agent, config);

        Assert.Equal(1234, agent.Inventory.Money);
    }

    [Fact]
    public void Apply_ConfigWithoutInventory_KeepsInventoryEmpty()
    {
        var agent = NewAgent("Abigail");
        var config = MakeConfig("Abigail", initialMoney: 300, initialInventory: Array.Empty<string>());

        AgentService.ApplyNpcConfigInitialization(agent, config);

        Assert.Equal(300, agent.Inventory.Money);
        // Inventory.Count 由 ItemRegistry.Create 决定；空数组 → 0 项
        Assert.Equal(0, agent.Inventory.Count);
    }

    [Fact]
    public void Apply_ConfigWithUnknownItemId_SkipsItemSilently()
    {
        // (O)99999 不存在 → ItemRegistry.Create 返回 null（或在某些环境抛 NRE）→ 钩子静默跳过
        var agent = NewAgent("Marnie");
        agent.Inventory.Money = 0;
        var config = MakeConfig("Marnie", initialMoney: 800, initialInventory: UnknownItemIdArray);

        // 不抛异常 = 通过
        AgentService.ApplyNpcConfigInitialization(agent, config);

        Assert.Equal(800, agent.Inventory.Money);
        Assert.Equal(0, agent.Inventory.Count); // 非法 item_id 被跳过
    }

    private static NpcConfig MakeConfig(string npcName, int initialMoney, string[] initialInventory) =>
        new(
            npcName,
            Personality: "",
            SpeechStyle: "",
            Birthday: "",
            IsRomanceable: false,
            LovedGifts: Array.Empty<string>(),
            LikedGifts: Array.Empty<string>(),
            DislikedGifts: Array.Empty<string>(),
            HatedGifts: Array.Empty<string>(),
            InitialMoney: initialMoney,
            InitialInventory: initialInventory,
            IsProtagonist: false,
            DefaultMood: "",
            Relationships: new Dictionary<string, int>());
}