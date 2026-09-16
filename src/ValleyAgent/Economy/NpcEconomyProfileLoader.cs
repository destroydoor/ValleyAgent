using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using StardewModdingAPI;
using ValleyAgent.Infrastructure;

namespace ValleyAgent.Economy;

/// <summary>
///     NPC 经济档案装载器（E3-1）。镜像 MemoryRuleLoader 的容错契约：
///     - 从 mod 目录读 JSON（默认 Data/npc_economy.json），IOException/JsonException → 空表不抛
///       （JSON 损坏时坏文件隔离改名 .corrupt-&lt;ts&gt; + Error 留痕，issue #26 批③）；
///     - 按 NPC 名缓存（StringComparer.OrdinalIgnoreCase），未知 NPC 返回 null；
///     - 装载时钳制数值（Savvy/Talkativeness ∈ [0,1]，InitialMoney/DailyWage ≥ 0），
///     BudgetTier 枚举解析失败回退 Normal。
///     设计文档：docs/ideas/e31-implementation-思路.md §2.3。
/// </summary>
public sealed class NpcEconomyProfileLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly Dictionary<string, NpcEconomyProfile> _profiles =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly IMonitor? _monitor;

    public NpcEconomyProfileLoader(IMonitor? monitor = null)
    {
        _monitor = monitor;
    }

    static NpcEconomyProfileLoader()
    {
        // BudgetTier 用字符串序列化（"cautious"/"normal"/"generous"），
        // 自定义转换器：大小写不敏感解析，未知值回退 Normal（单条配置错误不拖垮整表）。
        JsonOptions.Converters.Add(new BudgetTierJsonConverter());
    }

    /// <summary>已装载的档案数（未知 NPC 时 GetProfile 返回 null）。</summary>
    public int Count
    {
        get => _profiles.Count;
    }

    /// <summary>
    ///     从 mod 目录装载档案。relativePath 相对 mod 目录（如 "Data/npc_economy.json"）。
    ///     文件缺失 / JSON 非法 → 空表，绝不抛异常（配置损坏不能拖垮 mod 启动）。
    /// </summary>
    public void Load(IModHelper helper, string relativePath)
    {
        ArgumentNullException.ThrowIfNull(helper);
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            _profiles.Clear();
            return;
        }

        LoadFromFile(Path.Combine(helper.DirectoryPath, relativePath));
    }

    /// <summary>
    ///     从指定路径装载档案（单元测试直接喂临时文件）。失败降级空表并留痕；JSON 损坏时隔离改名。
    /// </summary>
    public void LoadFromFile(string jsonPath)
    {
        _profiles.Clear();

        try
        {
            if (!File.Exists(jsonPath))
            {
                return;
            }

            var json = File.ReadAllText(jsonPath);
            var entries = JsonSerializer.Deserialize<List<NpcEconomyProfileData>>(json, JsonOptions);
            if (entries == null)
            {
                return;
            }

            foreach (var entry in entries)
            {
                if (entry == null || string.IsNullOrWhiteSpace(entry.Name))
                {
                    continue;
                }

                _profiles[entry.Name] = new NpcEconomyProfile(
                    entry.Name,
                    Math.Max(0, entry.InitialMoney),
                    entry.InitialItems ?? new List<string>(),
                    Math.Clamp(entry.Savvy, 0.0, 1.0),
                    entry.BudgetTier,
                    Math.Max(0, entry.DailyWage),
                    Math.Clamp(entry.Talkativeness, 0.0, 1.0),
                    entry.PurchaseItems ?? new List<string>());
            }
        }
        catch (IOException ex)
        {
            // 读失败（文件锁/权限）≠ 已损坏：不隔离，Error 留痕 + 本次降级空表
            _monitor?.Log($"[NpcEconomyProfileLoader] economy profile unreadable — using empty table: {jsonPath}: {ex}", LogLevel.Error);
        }
        catch (JsonException ex)
        {
            // 解析失败 = 文件已损坏：隔离改名（可取回原始字节），不再静默清空整张经济表
            var quarantined = CorruptFileQuarantine.TryQuarantine(jsonPath);
            _monitor?.Log(
                $"[NpcEconomyProfileLoader] economy profile JSON corrupt — quarantined: '{jsonPath}' → '{quarantined ?? "(rename failed, file left in place)"}'; using empty table: {ex}",
                LogLevel.Error);
        }
    }

    /// <summary>
    ///     按 NPC 名查档案，大小写不敏感；未知 NPC 返回 null（调用方按"无档案"处理）。
    /// </summary>
    public NpcEconomyProfile? GetProfile(string npcName)
    {
        if (string.IsNullOrWhiteSpace(npcName))
        {
            return null;
        }

        return _profiles.TryGetValue(npcName, out var profile) ? profile : null;
    }

    /// <summary>
    ///     JSON 反序列化中间模型。
    ///     Name/BudgetTier 必须有 setter：System.Text.Json 反序列化无法写只读属性，
    ///     否则 Name 恒空 → 整表跳过（2026-08-09 单测发现：经济档案从未成功加载，NPC 钱包全 0）。
    /// </summary>
    private sealed class NpcEconomyProfileData
    {
        public string Name { get; set; } = "";
        public int InitialMoney { get; set; }
        public List<string>? InitialItems { get; set; }
        public double Savvy { get; set; }
        public BudgetTier BudgetTier { get; set; } = BudgetTier.Normal;
        public int DailyWage { get; set; }
        public double Talkativeness { get; set; }
        public List<string>? PurchaseItems { get; set; }
    }

    /// <summary>
    ///     BudgetTier 字符串 ↔ 枚举转换器。解析大小写不敏感（"cautious"→Cautious）；
    ///     未知字符串回退 Normal，避免单个 NPC 配置笔误导致整份档案加载失败。
    /// </summary>
    private sealed class BudgetTierJsonConverter : JsonConverter<BudgetTier>
    {
        public override BudgetTier Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String
                && Enum.TryParse<BudgetTier>(reader.GetString(), true, out var result))
            {
                return result;
            }

            return BudgetTier.Normal;
        }

        public override void Write(Utf8JsonWriter writer, BudgetTier value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString().ToLowerInvariant());
    }
}