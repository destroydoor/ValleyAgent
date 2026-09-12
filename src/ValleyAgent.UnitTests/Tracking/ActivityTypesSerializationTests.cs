using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;
using ValleyAgent.Tracking;
using Xunit;

namespace ValleyAgent.UnitTests.Tracking;

/// <summary>
///     验证 <see cref="ActivityTypes" /> 各类型在 JSON 序列化时与 TS narrative-types.ts
///     的字段名 / 枚举值大小写保持一致。C# 通过 MessageProtocol 全局
///     <c>JsonStringEnumConverter(JsonNamingPolicy.CamelCase)</c> 将枚举名转为 camelCase，
///     普通属性由 CamelCase 命名策略转换。
/// </summary>
public static class ActivityTypesSerializationTests
{
    /// <summary>
    ///     与 TS 端 WebSocket 协议一致的 JSON 选项：camelCase 属性 + camelCase 字符串枚举。
    ///     与 ValleyAgent.WebSocket.MessageProtocol 的全局配置对齐。
    ///     注意：MessageProtocol 未启用 UnsafeRelaxedJsonEscaping，但本测试启用以便断言中文字符串。
    /// </summary>
    private static JsonSerializerOptions ProtocolOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, true) },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    };

    // ───────────────────────── DailyActivity ─────────────────────────

    [Fact]
    public static void DailyActivity_Roundtrips_All_Fields()
    {
        var activity = new DailyActivity
        {
            Date = "2026-07-26",
            FishingMinutes = 90,
            FarmingMinutes = 60,
            MiningMinutes = 0,
            ForagingMinutes = 15,
            SocialMinutes = 30,
            CombatMinutes = 0,
            FishCaught = 12,
            CropsHarvested = 8,
            ItemsShipped = 5,
            ItemsForaged = 7,
            MonstersKilled = 0,
            LocationsVisited = new List<string> { "Beach", "Farm" },
            NpcsTalkedTo = new List<string> { "Willy", "Pierre" },
            GiftsGiven = new List<GiftRecord>
            {
                new() { To = "Willy", ItemId = "Oceanfish_6" }
            }
        };

        var json = JsonSerializer.Serialize(activity, ProtocolOptions);
        var roundtrip = JsonSerializer.Deserialize<DailyActivity>(json, ProtocolOptions)!;

        Assert.Equal("2026-07-26", roundtrip.Date);
        Assert.Equal(90, roundtrip.FishingMinutes);
        Assert.Equal(60, roundtrip.FarmingMinutes);
        Assert.Equal(15, roundtrip.ForagingMinutes);
        Assert.Equal(30, roundtrip.SocialMinutes);
        Assert.Equal(12, roundtrip.FishCaught);
        Assert.Equal(8, roundtrip.CropsHarvested);
        Assert.Equal(5, roundtrip.ItemsShipped);
        Assert.Equal(7, roundtrip.ItemsForaged);
        Assert.Equal(new List<string> { "Beach", "Farm" }, roundtrip.LocationsVisited);
        Assert.Equal(new List<string> { "Willy", "Pierre" }, roundtrip.NpcsTalkedTo);
        Assert.Single(roundtrip.GiftsGiven);
        Assert.Equal("Willy", roundtrip.GiftsGiven[0].To);
        Assert.Equal("Oceanfish_6", roundtrip.GiftsGiven[0].ItemId);
    }

    [Fact]
    public static void DailyActivity_Properties_Are_CamelCase_In_Json()
    {
        var activity = new DailyActivity
        {
            Date = "2026-07-26",
            FishingMinutes = 30,
            FishCaught = 5,
            CropsHarvested = 2,
            ItemsShipped = 1,
            ItemsForaged = 4,
            MonstersKilled = 3,
            MiningMinutes = 60,
            FarmingMinutes = 45,
            ForagingMinutes = 20,
            SocialMinutes = 10,
            CombatMinutes = 15
        };

        var json = JsonSerializer.Serialize(activity, ProtocolOptions);

        Assert.Contains("\"date\":", json);
        Assert.Contains("\"fishingMinutes\":", json);
        Assert.Contains("\"farmingMinutes\":", json);
        Assert.Contains("\"miningMinutes\":", json);
        Assert.Contains("\"foragingMinutes\":", json);
        Assert.Contains("\"socialMinutes\":", json);
        Assert.Contains("\"combatMinutes\":", json);
        Assert.Contains("\"fishCaught\":", json);
        Assert.Contains("\"cropsHarvested\":", json);
        Assert.Contains("\"itemsShipped\":", json);
        Assert.Contains("\"itemsForaged\":", json);
        Assert.Contains("\"monstersKilled\":", json);
        Assert.Contains("\"locationsVisited\":", json);
        Assert.Contains("\"npcsTalkedTo\":", json);
        Assert.Contains("\"giftsGiven\":", json);
    }

    // ───────────────────────── GiftRecord ─────────────────────────

    [Fact]
    public static void GiftRecord_Properties_Are_CamelCase_In_Json()
    {
        var gift = new GiftRecord { To = "Abigail", ItemId = "emerald" };
        var json = JsonSerializer.Serialize(gift, ProtocolOptions);

        Assert.Contains("\"to\":\"Abigail\"", json);
        Assert.Contains("\"itemId\":\"emerald\"", json);
    }

    // ───────────────────────── PlayStyleTag ─────────────────────────

    [Theory]
    [InlineData(PlayStyleTag.Brewer, "brewer")]
    [InlineData(PlayStyleTag.Farmer, "farmer")]
    [InlineData(PlayStyleTag.Rancher, "rancher")]
    [InlineData(PlayStyleTag.Miner, "miner")]
    [InlineData(PlayStyleTag.Warrior, "warrior")]
    [InlineData(PlayStyleTag.Forager, "forager")]
    [InlineData(PlayStyleTag.Socializer, "socializer")]
    public static void PlayStyleTag_Serializes_As_Lowercase_String(PlayStyleTag tag, string expected)
    {
        var json = JsonSerializer.Serialize(tag, ProtocolOptions);
        Assert.Equal($"\"{expected}\"", json);
    }

    [Theory]
    [InlineData("brewer", PlayStyleTag.Brewer)]
    [InlineData("farmer", PlayStyleTag.Farmer)]
    [InlineData("socializer", PlayStyleTag.Socializer)]
    public static void PlayStyleTag_Deserializes_From_Lowercase_String(string json, PlayStyleTag expected)
    {
        var tag = JsonSerializer.Deserialize<PlayStyleTag>($"\"{json}\"", ProtocolOptions);
        Assert.Equal(expected, tag);
    }

    // ───────────────────────── PlayStyle ─────────────────────────

    [Fact]
    public static void PlayStyle_Roundtrips_And_Uses_CamelCase()
    {
        var style = new PlayStyle
        {
            Tag = PlayStyleTag.Brewer,
            Confidence = 0.8,
            Evidence = "检测到 12 个酒桶"
        };

        var json = JsonSerializer.Serialize(style, ProtocolOptions);

        Assert.Contains("\"tag\":\"brewer\"", json);
        Assert.Contains("\"confidence\":0.8", json);
        Assert.Contains("\"evidence\":\"检测到 12 个酒桶\"", json);

        var roundtrip = JsonSerializer.Deserialize<PlayStyle>(json, ProtocolOptions)!;
        Assert.Equal(PlayStyleTag.Brewer, roundtrip.Tag);
        Assert.Equal(0.8, roundtrip.Confidence);
        Assert.Equal("检测到 12 个酒桶", roundtrip.Evidence);
    }

    // ───────────────────────── MilestoneType ─────────────────────────

    [Theory]
    [InlineData(MilestoneType.FishingStreak, "fishingStreak")]
    [InlineData(MilestoneType.MiningStreak, "miningStreak")]
    [InlineData(MilestoneType.FarmingStreak, "farmingStreak")]
    [InlineData(MilestoneType.SameGiftRepeated, "sameGiftRepeated")]
    [InlineData(MilestoneType.DialogueCount, "dialogueCount")]
    [InlineData(MilestoneType.MonsterKills, "monsterKills")]
    [InlineData(MilestoneType.NewArea, "newArea")]
    [InlineData(MilestoneType.FirstBundle, "firstBundle")]
    public static void MilestoneType_Serializes_As_CamelCase(MilestoneType type, string expected)
    {
        var json = JsonSerializer.Serialize(type, ProtocolOptions);
        Assert.Equal($"\"{expected}\"", json);
    }

    // ───────────────────────── Milestone ─────────────────────────

    [Fact]
    public static void Milestone_Roundtrips_And_Uses_CamelCase()
    {
        var milestone = new Milestone
        {
            Type = MilestoneType.FishingStreak,
            Description = "连续 3 天钓鱼 ≥30 分钟",
            DetectedAt = "2026-07-26T14:00:00"
        };

        var json = JsonSerializer.Serialize(milestone, ProtocolOptions);

        Assert.Contains("\"type\":\"fishingStreak\"", json);
        Assert.Contains("\"description\":\"连续 3 天钓鱼 ≥30 分钟\"", json);
        Assert.Contains("\"detectedAt\":\"2026-07-26T14:00:00\"", json);

        var roundtrip = JsonSerializer.Deserialize<Milestone>(json, ProtocolOptions)!;
        Assert.Equal(MilestoneType.FishingStreak, roundtrip.Type);
        Assert.Equal("连续 3 天钓鱼 ≥30 分钟", roundtrip.Description);
        Assert.Equal("2026-07-26T14:00:00", roundtrip.DetectedAt);
    }

    // ───────────────────────── 端到端：模拟 TS activity_report 消息 ─────────────────────────

    [Fact]
    public static void ActivityReport_Payload_Matches_Ts_Contract()
    {
        // 构造一段 C# 端实际会发送给 TS 的活动上报 payload。
        // TS 端 ActivityReportMessage 字段：dailyActivity + farmSnapshot。
        // 这里验证嵌套对象在 JSON 中的字段名与 TS 严格一致。
        var activity = new DailyActivity
        {
            Date = "2026-07-26",
            FishingMinutes = 90,
            FishCaught = 12,
            NpcsTalkedTo = new List<string> { "Willy" },
            GiftsGiven = new List<GiftRecord>
            {
                new() { To = "Willy", ItemId = "Oceanfish_6" }
            }
        };
        var farmSnapshot = new List<PlayStyle>
        {
            new() { Tag = PlayStyleTag.Brewer, Confidence = 0.8, Evidence = "酒桶 12" },
            new() { Tag = PlayStyleTag.Farmer, Confidence = 0.6, Evidence = "作物 100 格" }
        };

        var payload = new
        {
            type = "activity_report",
            requestId = "req-1",
            dailyActivity = activity,
            farmSnapshot
        };

        var json = JsonSerializer.Serialize(payload, ProtocolOptions);

        // 验证关键字段名都为 camelCase，与 TS 接口一致
        Assert.Contains("\"type\":\"activity_report\"", json);
        Assert.Contains("\"requestId\":\"req-1\"", json);
        Assert.Contains("\"dailyActivity\":", json);
        Assert.Contains("\"farmSnapshot\":", json);
        Assert.Contains("\"date\":\"2026-07-26\"", json);
        Assert.Contains("\"fishingMinutes\":90", json);
        Assert.Contains("\"tag\":\"brewer\"", json);
        Assert.Contains("\"tag\":\"farmer\"", json);
    }
}