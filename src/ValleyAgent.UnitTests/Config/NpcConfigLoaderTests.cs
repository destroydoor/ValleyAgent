using System.Text;
using ValleyAgent.Config;
using Xunit;

namespace ValleyAgent.UnitTests.Config;

/// <summary>
///     阶段 3 NpcConfigLoader 单元测试（spec §2.4 / §2.9）。
///     验证：合法 JSON 解析（全部字段）、大小写不敏感缓存、未知 NPC → null、
///     缺文件/坏 JSON 静默容错、负数钳制、relationships 钳制。
///     镜像 NpcEconomyProfileLoaderTests 的 temp-dir 模式。
/// </summary>
public class NpcConfigLoaderTests
{
    private const string SampleJson = """
                                      {
                                        "name": "Shane",
                                        "personality": "depressive but soft-hearted",
                                        "speechStyle": "blunt, sarcastic",
                                        "birthday": "spring_20",
                                        "isRomanceable": true,
                                        "lovedGifts": ["(O)206", "(O)612"],
                                        "likedGifts": ["(O)194"],
                                        "dislikedGifts": ["(O)152"],
                                        "hatedGifts": ["(O)236"],
                                        "initialMoney": 150,
                                        "initialInventory": ["(O)346"],
                                        "isProtagonist": false,
                                        "defaultMood": "irritable",
                                        "relationships": { "Emily": 6, "Jas": 10 }
                                      }
                                      """;

    private static readonly string[] ExpectedLovedGifts = { "(O)206", "(O)612" };
    private static readonly string[] ExpectedLikedGifts = { "(O)194" };
    private static readonly string[] ExpectedDislikedGifts = { "(O)152" };
    private static readonly string[] ExpectedHatedGifts = { "(O)236" };
    private static readonly string[] ExpectedInitialInventory = { "(O)346" };

    private static string WriteTempJson(string content)
    {
        var dir = Path.Combine(Path.GetTempPath(), "npcconfig_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "Shane.json");
        File.WriteAllText(path, content, Encoding.UTF8);
        return path;
    }

    [Fact]
    public void LoadFromFile_ValidJson_ParsesAllFields()
    {
        var path = WriteTempJson(SampleJson);
        try
        {
            var loader = new NpcConfigLoader();
            loader.LoadFromFile(path);

            Assert.Equal(1, loader.Count);

            var shane = loader.GetProfile("Shane");
            Assert.NotNull(shane);
            Assert.Equal("Shane", shane!.Name);
            Assert.Equal("depressive but soft-hearted", shane.Personality);
            Assert.Equal("blunt, sarcastic", shane.SpeechStyle);
            Assert.Equal("spring_20", shane.Birthday);
            Assert.True(shane.IsRomanceable);
            Assert.Equal(ExpectedLovedGifts, shane.LovedGifts);
            Assert.Equal(ExpectedLikedGifts, shane.LikedGifts);
            Assert.Equal(ExpectedDislikedGifts, shane.DislikedGifts);
            Assert.Equal(ExpectedHatedGifts, shane.HatedGifts);
            Assert.Equal(150, shane.InitialMoney);
            Assert.Equal(ExpectedInitialInventory, shane.InitialInventory);
            Assert.False(shane.IsProtagonist);
            Assert.Equal("irritable", shane.DefaultMood);
            Assert.Equal(2, shane.Relationships.Count);
            Assert.Equal(6, shane.Relationships["Emily"]);
            Assert.Equal(10, shane.Relationships["Jas"]);
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
            var loader = new NpcConfigLoader();
            loader.LoadFromFile(path);

            Assert.NotNull(loader.GetProfile("shane")); // 全小写
            Assert.NotNull(loader.GetProfile("SHANE")); // 全大写
            Assert.Same(loader.GetProfile("shane"), loader.GetProfile("Shane"));
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
            var loader = new NpcConfigLoader();
            loader.LoadFromFile(path);

            Assert.Null(loader.GetProfile("Lewis"));
            Assert.Null(loader.GetProfile(""));
            Assert.Null(loader.GetProfile(null!));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, true);
        }
    }

    [Fact]
    public void LoadFromFile_MissingFile_NoConfigs_NoThrow()
    {
        var loader = new NpcConfigLoader();
        var missing = Path.Combine(Path.GetTempPath(), "does_not_exist_" + Guid.NewGuid().ToString("N") + ".json");

        var ex = Record.Exception(() => loader.LoadFromFile(missing));

        Assert.Null(ex);
        Assert.Equal(0, loader.Count);
        Assert.Null(loader.GetProfile("Shane"));
    }

    [Fact]
    public void LoadFromFile_InvalidJson_NoThrow_Empty()
    {
        var path = WriteTempJson("{ not valid json !!!");
        try
        {
            var loader = new NpcConfigLoader();
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
    public void LoadFromFile_ClampsNegativeMoney_ToZero()
    {
        var path = WriteTempJson("""
                                 {
                                   "name": "Broke",
                                   "personality": "down on his luck",
                                   "initialMoney": -50,
                                   "relationships": {}
                                 }
                                 """);
        try
        {
            var loader = new NpcConfigLoader();
            loader.LoadFromFile(path);

            var profile = loader.GetProfile("Broke");
            Assert.NotNull(profile);
            Assert.Equal(0, profile!.InitialMoney);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, true);
        }
    }

    [Fact]
    public void LoadFromFile_ClampsNegativeRelationships_ToZero()
    {
        var path = WriteTempJson("""
                                 {
                                   "name": "Clint",
                                   "personality": "shy blacksmith",
                                   "initialMoney": 100,
                                   "relationships": { "Emily": -3, "Gus": 2 }
                                 }
                                 """);
        try
        {
            var loader = new NpcConfigLoader();
            loader.LoadFromFile(path);

            var profile = loader.GetProfile("Clint");
            Assert.NotNull(profile);
            Assert.Equal(0, profile!.Relationships["Emily"]);
            Assert.Equal(2, profile.Relationships["Gus"]);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, true);
        }
    }

    [Fact]
    public void LoadFromFile_MissingNameField_Skipped()
    {
        var path = WriteTempJson("""
                                 {
                                   "personality": "no name here",
                                   "initialMoney": 100
                                 }
                                 """);
        try
        {
            var loader = new NpcConfigLoader();
            loader.LoadFromFile(path);

            Assert.Equal(0, loader.Count);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, true);
        }
    }

    [Fact]
    public void LoadFromFile_NullableFields_DefaultEmpty()
    {
        var path = WriteTempJson("""
                                 {
                                   "name": "Minimal"
                                 }
                                 """);
        try
        {
            var loader = new NpcConfigLoader();
            loader.LoadFromFile(path);

            var profile = loader.GetProfile("Minimal");
            Assert.NotNull(profile);
            Assert.Equal("", profile!.Personality);
            Assert.Equal("", profile.SpeechStyle);
            Assert.Equal("", profile.Birthday);
            Assert.False(profile.IsRomanceable);
            Assert.Empty(profile.LovedGifts);
            Assert.Equal(0, profile.InitialMoney);
            Assert.Empty(profile.InitialInventory);
            Assert.False(profile.IsProtagonist);
            Assert.Equal("", profile.DefaultMood);
            Assert.Empty(profile.Relationships);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, true);
        }
    }
}
