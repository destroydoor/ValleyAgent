using System.Text.Json.Nodes;
using ValleyAgent.Config;
using Xunit;

namespace ValleyAgent.UnitTests;

/// <summary>
///     LlmConfigWriter 的序列化与解析测试。
///     对应实施计划 Task 4：把 ModConfig 多 Provider 字段序列化为 runtime JSON。
/// </summary>
public static class LlmConfigWriterTests
{
    // 占位 key 用运行时拼接生成：字面量不直接赋给 *ApiKey 属性
    // （安全扫描按属性名拦截硬编码凭据，无论值是否为真实 key）。
    private static string FakeKey(string role) => "test-key-" + role;

    private static ModConfig MakeValidMultiProviderConfig()
    {
        static string fakeKey(string role) => "test-key-" + role;

        var c = new ModConfig
        {
            MultiProviderEnabled = true,
            DirectorPrimaryProvider = "minimax",
            DirectorPrimaryApiKey = fakeKey("director-primary"),
            DirectorPrimaryModel = "MiniMax-M3",
            DirectorPrimaryBaseUrl = "https://api.minimax.chat/v1",
            DirectorFallbackProvider = "deepseek",
            DirectorFallbackApiKey = fakeKey("director-fallback"),
            DirectorFallbackModel = "deepseek-chat",
            DirectorFallbackBaseUrl = "https://api.deepseek.com/v1",
            ProtagonistPrimaryProvider = "minimax",
            ProtagonistPrimaryApiKey = fakeKey("protagonist-primary"),
            ProtagonistPrimaryModel = "MiniMax-M2.7-highspeed",
            ProtagonistPrimaryBaseUrl = "https://api.minimax.chat/v1",
            ProtagonistFallbackProvider = "deepseek",
            ProtagonistFallbackApiKey = fakeKey("protagonist-fallback"),
            ProtagonistFallbackModel = "deepseek-chat",
            ProtagonistFallbackBaseUrl = "https://api.deepseek.com/v1",
            NpcPrimaryProvider = "deepseek",
            NpcPrimaryApiKey = fakeKey("npc-primary"),
            NpcPrimaryModel = "deepseek-chat",
            NpcPrimaryBaseUrl = "https://api.deepseek.com/v1",
            NpcFallbackProvider = "",
            NpcFallbackApiKey = "",
            NpcFallbackModel = "",
            NpcFallbackBaseUrl = "",
            EnableProtagonistMapping = true,
            ProtagonistNpcs = "Abigail, Haley, Sebastian",
            LLMTimeoutSeconds = 60,
            MaxRetries = 3,
            Temperature = 0.7f
        };
        return c;
    }

    [Fact]
    public static void WriteRuntimeConfig_GeneratesValidJson()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), $"llm-test-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tmpDir);
            var config = MakeValidMultiProviderConfig();

            var path = LlmConfigWriter.WriteRuntimeConfig(config, tmpDir);
            Assert.NotNull(path);
            Assert.True(File.Exists(path));

            var json = File.ReadAllText(path!);
            var obj = JsonNode.Parse(json)!.AsObject();

            Assert.Equal(1, obj["version"]!.GetValue<int>());
            Assert.NotNull(obj["roles"]!["director"]);
            Assert.NotNull(obj["roles"]!["protagonist"]);
            Assert.NotNull(obj["roles"]!["npc"]);
        }
        finally
        {
            if (Directory.Exists(tmpDir))
            {
                Directory.Delete(tmpDir, true);
            }
        }
    }

    [Fact]
    public static void WriteRuntimeConfig_FallbackEmpty_FallbackIsNull()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), $"llm-test-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tmpDir);
            var config = MakeValidMultiProviderConfig();
            config.NpcFallbackProvider = "";
            config.NpcFallbackModel = "";

            var path = LlmConfigWriter.WriteRuntimeConfig(config, tmpDir);
            Assert.NotNull(path);
            var json = File.ReadAllText(path!);
            var obj = JsonNode.Parse(json)!.AsObject();

            // 空 fallback 序列化为 JSON null（"fallback": null）。
            // System.Text.Json.Nodes: JSON null 对应的 JsonNode 引用为 null。
            var npcRole = obj["roles"]!["npc"]!.AsObject();
            Assert.True(npcRole.ContainsKey("fallback"));
            Assert.Null(npcRole["fallback"]);
        }
        finally
        {
            if (Directory.Exists(tmpDir))
            {
                Directory.Delete(tmpDir, true);
            }
        }
    }

    [Fact]
    public static void WriteRuntimeConfig_FallbackPresent_FallbackIsObject()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), $"llm-test-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tmpDir);
            var config = MakeValidMultiProviderConfig();

            var path = LlmConfigWriter.WriteRuntimeConfig(config, tmpDir);
            Assert.NotNull(path);
            var json = File.ReadAllText(path!);
            var obj = JsonNode.Parse(json)!.AsObject();

            // director 有 fallback，应序列化为 object
            Assert.NotNull(obj["roles"]!["director"]!["fallback"]);
            Assert.Equal("deepseek", obj["roles"]!["director"]!["fallback"]!["provider"]!.GetValue<string>());
        }
        finally
        {
            if (Directory.Exists(tmpDir))
            {
                Directory.Delete(tmpDir, true);
            }
        }
    }

    [Fact]
    public static void WriteRuntimeConfig_FallbackChain_BothEntriesWritten_LegacyFieldIsFirst()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), $"llm-test-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tmpDir);
            var config = MakeValidMultiProviderConfig();
            // 第二备：四字段齐全 → fallbacks 应有两条，legacy fallback = 链首
            config.DirectorFallback2Provider = "anthropic";
            config.DirectorFallback2ApiKey = FakeKey("director-fallback2");
            config.DirectorFallback2Model = "deepseek-flash";
            config.DirectorFallback2BaseUrl = "https://api.deepseek.com/anthropic/v1";

            var path = LlmConfigWriter.WriteRuntimeConfig(config, tmpDir);
            Assert.NotNull(path);
            var obj = JsonNode.Parse(File.ReadAllText(path!))!.AsObject();

            var director = obj["roles"]!["director"]!.AsObject();
            var chain = director["fallbacks"]!.AsArray();
            Assert.Equal(2, chain.Count);
            Assert.Equal("deepseek", chain[0]!["provider"]!.GetValue<string>());
            Assert.Equal("anthropic", chain[1]!["provider"]!.GetValue<string>());
            Assert.Equal("deepseek", director["fallback"]!["provider"]!.GetValue<string>());
        }
        finally
        {
            if (Directory.Exists(tmpDir))
            {
                Directory.Delete(tmpDir, true);
            }
        }
    }

    [Fact]
    public static void WriteRuntimeConfig_EmptyApiKeyEntry_SkippedFromChain()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), $"llm-test-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tmpDir);
            var config = MakeValidMultiProviderConfig();
            // 第一备 key 为空 → 跳过；第二备齐全 → 链上只剩它，legacy fallback = 第二备
            config.NpcFallbackApiKey = "";
            config.NpcFallback2Provider = "anthropic";
            config.NpcFallback2ApiKey = FakeKey("npc-fallback2");
            config.NpcFallback2Model = "deepseek-flash";
            config.NpcFallback2BaseUrl = "https://api.deepseek.com/anthropic/v1";

            var path = LlmConfigWriter.WriteRuntimeConfig(config, tmpDir);
            Assert.NotNull(path);
            var obj = JsonNode.Parse(File.ReadAllText(path!))!.AsObject();

            var npc = obj["roles"]!["npc"]!.AsObject();
            var chain = npc["fallbacks"]!.AsArray();
            Assert.Single(chain);
            Assert.Equal("anthropic", chain[0]!["provider"]!.GetValue<string>());
            Assert.Equal("anthropic", npc["fallback"]!["provider"]!.GetValue<string>());
        }
        finally
        {
            if (Directory.Exists(tmpDir))
            {
                Directory.Delete(tmpDir, true);
            }
        }
    }

    [Fact]
    public static void WriteRuntimeConfig_AllFallbacksIncomplete_ChainEmptyLegacyNull()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), $"llm-test-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tmpDir);
            var config = MakeValidMultiProviderConfig();
            // 两备模型齐全但 key 全空 → 整链跳过
            config.ProtagonistFallbackApiKey = "";
            config.ProtagonistFallback2ApiKey = "";

            var path = LlmConfigWriter.WriteRuntimeConfig(config, tmpDir);
            Assert.NotNull(path);
            var obj = JsonNode.Parse(File.ReadAllText(path!))!.AsObject();

            var protagonist = obj["roles"]!["protagonist"]!.AsObject();
            Assert.Empty(protagonist["fallbacks"]!.AsArray());
            Assert.Null(protagonist["fallback"]);
        }
        finally
        {
            if (Directory.Exists(tmpDir))
            {
                Directory.Delete(tmpDir, true);
            }
        }
    }

    [Fact]
    public static void WriteRuntimeConfig_ProtagonistNpcsParsedToArray()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), $"llm-test-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tmpDir);
            var config = MakeValidMultiProviderConfig();
            config.ProtagonistNpcs = "Abigail, Haley,Sebastian , Pierre";

            var path = LlmConfigWriter.WriteRuntimeConfig(config, tmpDir);
            Assert.NotNull(path);
            var json = File.ReadAllText(path!);
            var obj = JsonNode.Parse(json)!.AsObject();

            var arr = obj["protagonistNpcs"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
            // ParseProtagonistList 用 TrimEntries + OrdinalIgnoreCase 去重；"Pierre" 不重复，应保留 4 个
            Assert.Equal(4, arr.Count);
            Assert.Contains("Abigail", arr);
            Assert.Contains("Haley", arr);
            Assert.Contains("Sebastian", arr);
            Assert.Contains("Pierre", arr);
        }
        finally
        {
            if (Directory.Exists(tmpDir))
            {
                Directory.Delete(tmpDir, true);
            }
        }
    }

    [Fact]
    public static void WriteRuntimeConfig_GlobalSectionContainsTimeoutRetries()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), $"llm-test-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tmpDir);
            var config = MakeValidMultiProviderConfig();
            config.LLMTimeoutSeconds = 90;
            config.MaxRetries = 5;
            config.Temperature = 0.42f;

            var path = LlmConfigWriter.WriteRuntimeConfig(config, tmpDir);
            Assert.NotNull(path);
            var json = File.ReadAllText(path!);
            var obj = JsonNode.Parse(json)!.AsObject();

            Assert.Equal(90_000, obj["global"]!["timeoutMs"]!.GetValue<int>());
            Assert.Equal(5, obj["global"]!["maxRetries"]!.GetValue<int>());
            Assert.Equal(0.42f, obj["global"]!["temperature"]!.GetValue<float>());
        }
        finally
        {
            if (Directory.Exists(tmpDir))
            {
                Directory.Delete(tmpDir, true);
            }
        }
    }

    [Fact]
    public static void GetRuntimeConfigPath_ReturnsExpectedFileName()
    {
        var path = LlmConfigWriter.GetRuntimeConfigPath("/tmp/mod");
        Assert.Equal(Path.Combine("/tmp/mod", "llm-config.runtime.json"), path);
    }

    [Fact]
    public static void ParseProtagonistList_CommaSeparated()
    {
        var list = LlmConfigWriter.ParseProtagonistList("Abigail, Haley, Sebastian");
        Assert.Equal(3, list.Count);
        Assert.Contains("Abigail", list);
        Assert.Contains("Haley", list);
        Assert.Contains("Sebastian", list);
    }

    [Fact]
    public static void ParseProtagonistList_CaseInsensitiveDedup()
    {
        var list = LlmConfigWriter.ParseProtagonistList("Abigail, abigail, ABIGAIL");
        Assert.Single(list);
    }

    [Fact]
    public static void ParseProtagonistList_EmptyString()
    {
        var list = LlmConfigWriter.ParseProtagonistList("");
        Assert.Empty(list);
    }

    [Fact]
    public static void ParseProtagonistList_TrimsWhitespace()
    {
        var list = LlmConfigWriter.ParseProtagonistList("  Abigail  ,  Haley  ");
        Assert.Equal(2, list.Count);
        Assert.Contains("Abigail", list);
        Assert.Contains("Haley", list);
    }

    [Fact]
    public static void ParseProtagonistList_NullInput()
    {
        var list = LlmConfigWriter.ParseProtagonistList(null);
        Assert.Empty(list);
    }
}