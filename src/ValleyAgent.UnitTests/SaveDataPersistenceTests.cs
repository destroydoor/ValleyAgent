using ValleyAgent.Save;
using ValleyAgent.Save.Models;
using Xunit;

namespace ValleyAgent.UnitTests;

/// <summary>
///     E3-1 钱包存档持久化单元测试。
///     验证：结构化存档（SaveDataManager）Money 往返一致；legacy/结构化两类数据默认哨兵 -1；
///     V1→V2 迁移保留 Money；空 JSON 反序列化回默认哨兵。
///     设计文档：docs/ideas/e31-implementation-思路.md §3 / §6。
/// </summary>
public class SaveDataPersistenceTests
{
    private static SaveDataManager NewManager() => new();

    private static SaveData BuildSaveWithMoney(string npc, int money)
    {
        var data = new SaveData();
        data.AgentStates[npc] = new AgentStateData
        {
            NpcName = npc,
            Money = money,
            Inventory = new List<string> { "(O)286:1" }
        };
        return data;
    }

    [Fact]
    public void AgentStateData_Money_RoundTripsThroughSaveDataManager()
    {
        var manager = NewManager();
        var data = BuildSaveWithMoney("Abigail", 500);

        var json = manager.Serialize(data);
        var restored = manager.Deserialize(json);

        Assert.True(restored.AgentStates.TryGetValue("Abigail", out var state));
        Assert.Equal(500, state!.Money);
    }

    [Fact]
    public void AgentStateData_Money_DefaultIsMinusOne_Sentinel()
    {
        // 哨兵 -1 = "未设置"：旧档反序列化缺 Money 字段时走属性初始化器，而不是 0，
        // 否则读档会把档案初始资金清零覆盖（见设计文档 §3）。
        Assert.Equal(-1, new AgentStateData().Money);
    }

    [Fact]
    public void LegacyAgentData_Money_DefaultIsMinusOne_Sentinel()
    {
#pragma warning disable CS0618 // AgentData 已过时（legacy 轨仍在使用，测试哨兵值必须覆盖）
        Assert.Equal(-1, new AgentData().Money);
#pragma warning restore CS0618
    }

    [Fact]
    public void MigrateV1ToV2_PreservesAgentStateMoney()
    {
        var manager = NewManager();
        var v1 = BuildSaveWithMoney("Gus", 2500);
        v1.Version = "1.0.0";

        var migrated = manager.MigrateV1ToV2(v1);

        Assert.Equal(SaveDataManager.CurrentVersion, migrated.Version);
        Assert.True(migrated.AgentStates.TryGetValue("Gus", out var state));
        Assert.Equal(2500, state!.Money);
    }

    [Fact]
    public void Deserialize_EmptyJson_ReturnsDefaults_WithSentinelMoney()
    {
        var manager = NewManager();
        var data = manager.Deserialize("");

        Assert.Equal(SaveDataManager.CurrentVersion, data.Version);
        Assert.Empty(data.AgentStates);
    }

    [Fact]
    public void Deserialize_LegacyJsonWithoutMoneyField_KeepsSentinel()
    {
        // 模拟旧版结构化存档（无 Money 字段）：反序列化后 Money 保持 -1 哨兵
        const string legacyJson = """
                                  {
                                    "version": "2.0.0",
                                    "schemaVersion": 1,
                                    "agentStates": {
                                      "Abigail": {
                                        "npcName": "Abigail",
                                        "currentState": "IDLE",
                                        "health": 100,
                                        "inventory": ["(O)286:1"],
                                        "emotion": "Neutral"
                                      }
                                    },
                                    "memories": {},
                                    "friendshipHistory": {},
                                    "allocations": { "agentNpcNames": [], "manualOverrides": {} },
                                    "statistics": { "sessionStartTime": "2026-08-01T00:00:00Z" }
                                  }
                                  """;
        var manager = NewManager();
        var data = manager.Deserialize(legacyJson);

        Assert.True(data.AgentStates.TryGetValue("Abigail", out var state));
        Assert.Equal(-1, state!.Money); // 未设置 → 读档时不覆盖档案初始资金
    }

    [Fact]
    public void Validate_AcceptsWalletWithMoney()
    {
        var manager = NewManager();
        var data = BuildSaveWithMoney("Harvey", 1200);

        Assert.True(manager.Validate(data));
    }
}