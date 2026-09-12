using System.Text;
using ValleyAgent.Economy;
using Xunit;

namespace ValleyAgent.UnitTests;

/// <summary>
///     E3-1 NPC 经济档案装载器单元测试。
///     验证：合法 JSON 解析；大小写不敏感缓存；未知 NPC → null；缺文件/坏 JSON 容错；
///     BudgetTier 枚举大小写解析；数值钳制。镜像 MemoryRuleLoader 的容错契约。
///     设计文档：docs/ideas/e31-implementation-思路.md §2.3 / §6。
/// </summary>
public class NpcEconomyProfileLoaderTests
{
    private const string SampleJson = """
                                      [
                                        {
                                          "name": "Abigail",
                                          "initialMoney": 300,
                                          "initialItems": ["(O)286", "(O)66"],
                                          "savvy": 0.35,
                                          "budgetTier": "normal",
                                          "dailyWage": 20,
                                          "talkativeness": 0.75
                                        },
                                        {
                                          "name": "Clint",
                                          "initialMoney": 1500,
                                          "initialItems": ["(O)334", "(O)335"],
                                          "savvy": 0.7,
                                          "budgetTier": "cautious",
                                          "dailyWage": 50,
                                          "talkativeness": 0.3
                                        },
                                        {
                                          "name": "Gus",
                                          "initialMoney": 2500,
                                          "initialItems": [],
                                          "savvy": 0.8,
                                          "budgetTier": "GENEROUS",
                                          "dailyWage": 60,
                                          "talkativeness": 0.65
                                        }
                                      ]
                                      """;

    private static string WriteTempJson(string content)
    {
        var dir = Path.Combine(Path.GetTempPath(), "economy_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "npc_economy.json");
        File.WriteAllText(path, content, Encoding.UTF8);
        return path;
    }

    [Fact]
    public void LoadFromFile_ValidJson_ParsesProfiles()
    {
        var path = WriteTempJson(SampleJson);
        try
        {
            var loader = new NpcEconomyProfileLoader();
            loader.LoadFromFile(path);

            Assert.Equal(3, loader.Count);

            var abigail = loader.GetProfile("Abigail");
            Assert.NotNull(abigail);
            Assert.Equal(300, abigail!.InitialMoney);
            Assert.Equal(2, abigail.InitialItems.Count);
            Assert.Equal("(O)286", abigail.InitialItems[0]);
            Assert.Equal(0.35, abigail.Savvy, 6);
            Assert.Equal(BudgetTier.Normal, abigail.BudgetTier);
            Assert.Equal(20, abigail.DailyWage);
            Assert.Equal(0.75, abigail.Talkativeness, 6);

            Assert.Equal(BudgetTier.Cautious, loader.GetProfile("Clint")!.BudgetTier);
            // 枚举解析大小写不敏感
            Assert.Equal(BudgetTier.Generous, loader.GetProfile("Gus")!.BudgetTier);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, true);
        }
    }

    [Fact]
    public void GetProfile_IsCaseInsensitive()
    {
        var path = WriteTempJson(SampleJson);
        try
        {
            var loader = new NpcEconomyProfileLoader();
            loader.LoadFromFile(path);

            Assert.NotNull(loader.GetProfile("abigail")); // 全小写
            Assert.NotNull(loader.GetProfile("ABIGAIL")); // 全大写
            Assert.Same(loader.GetProfile("abigail"), loader.GetProfile("Abigail"));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, true);
        }
    }

    [Fact]
    public void GetProfile_UnknownNpc_ReturnsNull()
    {
        var path = WriteTempJson(SampleJson);
        try
        {
            var loader = new NpcEconomyProfileLoader();
            loader.LoadFromFile(path);

            Assert.Null(loader.GetProfile("Lewis"));
            Assert.Null(loader.GetProfile(""));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, true);
        }
    }

    [Fact]
    public void LoadFromFile_MissingFile_NoProfiles_NoThrow()
    {
        var loader = new NpcEconomyProfileLoader();
        var missing = Path.Combine(Path.GetTempPath(), "does_not_exist_" + Guid.NewGuid().ToString("N") + ".json");

        var ex = Record.Exception(() => loader.LoadFromFile(missing));

        Assert.Null(ex);
        Assert.Equal(0, loader.Count);
        Assert.Null(loader.GetProfile("Abigail"));
    }

    [Fact]
    public void LoadFromFile_InvalidJson_NoThrow_Empty()
    {
        var path = WriteTempJson("{ not valid json !!!");
        try
        {
            var loader = new NpcEconomyProfileLoader();
            var ex = Record.Exception(() => loader.LoadFromFile(path));

            Assert.Null(ex);
            Assert.Equal(0, loader.Count);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, true);
        }
    }

    [Fact]
    public void LoadFromFile_ClampsSavvyAndTalkativeness_To01()
    {
        var path = WriteTempJson("""
                                 [
                                   {
                                     "name": "OutOfRange",
                                     "initialMoney": 100,
                                     "initialItems": [],
                                     "savvy": 1.5,
                                     "budgetTier": "normal",
                                     "dailyWage": 10,
                                     "talkativeness": -0.3
                                   }
                                 ]
                                 """);
        try
        {
            var loader = new NpcEconomyProfileLoader();
            loader.LoadFromFile(path);

            var profile = loader.GetProfile("OutOfRange");
            Assert.NotNull(profile);
            Assert.Equal(1.0, profile!.Savvy, 6); // 1.5 钳到 1.0
            Assert.Equal(0.0, profile.Talkativeness, 6); // -0.3 钳到 0.0
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, true);
        }
    }

    [Fact]
    public void LoadFromFile_ClampsNegativeMoney_ToZero()
    {
        var path = WriteTempJson("""
                                 [
                                   {
                                     "name": "Broke",
                                     "initialMoney": -50,
                                     "initialItems": [],
                                     "savvy": 0.5,
                                     "budgetTier": "cautious",
                                     "dailyWage": -3,
                                     "talkativeness": 0.5
                                   }
                                 ]
                                 """);
        try
        {
            var loader = new NpcEconomyProfileLoader();
            loader.LoadFromFile(path);

            var profile = loader.GetProfile("Broke");
            Assert.NotNull(profile);
            Assert.Equal(0, profile!.InitialMoney);
            Assert.Equal(0, profile.DailyWage);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, true);
        }
    }

    [Fact]
    public void LoadFromFile_UnknownBudgetTier_FallsBackToNormal()
    {
        var path = WriteTempJson("""
                                 [
                                   {
                                     "name": "Mystery",
                                     "initialMoney": 100,
                                     "initialItems": [],
                                     "savvy": 0.5,
                                     "budgetTier": "EXTRAVAGANT",
                                     "dailyWage": 10,
                                     "talkativeness": 0.5
                                   }
                                 ]
                                 """);
        try
        {
            var loader = new NpcEconomyProfileLoader();
            loader.LoadFromFile(path);

            var profile = loader.GetProfile("Mystery");
            Assert.NotNull(profile);
            Assert.Equal(BudgetTier.Normal, profile!.BudgetTier);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, true);
        }
    }
}