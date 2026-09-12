using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using StardewModdingAPI;

namespace ValleyAgent.Config;

/// <summary>
///     单个 NPC 的人设配置（阶段 3，3.2.1）。由 NpcConfigLoader 从 npc-configs/{npc_name}.json 装载。
///     供 Director 人设约束 / DirectorContextBuilder 拼装 NPC 摘要 / 未来 spawn 初始化使用。
/// </summary>
public sealed record NpcConfig(
    string Name,
    string Personality,
    string SpeechStyle,
    string Birthday,
    bool IsRomanceable,
    IReadOnlyList<string> LovedGifts,
    IReadOnlyList<string> LikedGifts,
    IReadOnlyList<string> DislikedGifts,
    IReadOnlyList<string> HatedGifts,
    int InitialMoney,
    IReadOnlyList<string> InitialInventory,
    bool IsProtagonist,
    string DefaultMood,
    IReadOnlyDictionary<string, int> Relationships);

/// <summary>
///     NPC 人设配置装载器（阶段 3，3.2.1）。镜像 NpcEconomyProfileLoader 的容错契约：
///     - 从 mod 目录读 JSON（每 NPC 一个文件：npc-configs/{npc_name}.json），
///       IOException/JsonException → 单文件跳过不抛（配置损坏不能拖垮 mod 启动）；
///     - 按 NPC 名缓存（StringComparer.OrdinalIgnoreCase），未知 NPC 返回 null；
///     - 数值钳制（InitialMoney ≥ 0）。
///     设计文档：docs/ideas/phase3-director-l2-default-思路.md §2.4。
/// </summary>
public sealed class NpcConfigLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly Dictionary<string, NpcConfig> _configs =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>已装载的配置数（未知 NPC 时 GetProfile 返回 null）。</summary>
    public int Count
    {
        get => _configs.Count;
    }

    /// <summary>清空全部配置（换档/重载前调用）。</summary>
    public void Clear() => _configs.Clear();

    /// <summary>
    ///     从 mod 目录装载全部 NPC 配置（relativeDir 相对 mod 目录，如 "npc-configs"）。
    ///     目录不存在或为空目录 → 空表；单文件损坏 → 跳过该文件，其余正常装载。
    /// </summary>
    public void LoadFromDirectory(IModHelper helper, string relativeDir)
    {
        ArgumentNullException.ThrowIfNull(helper);
        if (string.IsNullOrWhiteSpace(relativeDir))
        {
            _configs.Clear();
            return;
        }

        LoadFromDirectoryInternal(Path.Combine(helper.DirectoryPath, relativeDir));
    }

    /// <summary>
    ///     从绝对目录装载全部 NPC 配置（单测直接喂临时目录）。目录不存在或为空目录 → 空表。
    /// </summary>
    internal void LoadFromDirectoryInternal(string absoluteDir)
    {
        if (string.IsNullOrWhiteSpace(absoluteDir) || !Directory.Exists(absoluteDir))
        {
            _configs.Clear();
            return;
        }

        foreach (var file in Directory.EnumerateFiles(absoluteDir, "*.json"))
        {
            LoadFromFile(file);
        }
    }

    /// <summary>
    ///     从指定路径装载单个 NPC 配置（单元测试直接喂临时文件）。失败静默跳过该文件。
    /// </summary>
    public void LoadFromFile(string jsonPath)
    {
        try
        {
            if (!File.Exists(jsonPath))
            {
                return;
            }

            var json = File.ReadAllText(jsonPath);
            var entry = JsonSerializer.Deserialize<NpcConfigData>(json, JsonOptions);
            if (entry == null || string.IsNullOrWhiteSpace(entry.Name))
            {
                return;
            }

            _configs[entry.Name] = Map(entry);
        }
        catch (IOException)
        {
            // 单文件读写失败 → 跳过该 NPC，不影响其余配置
        }
        catch (JsonException)
        {
            // 单文件 JSON 非法 → 跳过该 NPC，不影响其余配置
        }
    }

    /// <summary>
    ///     按 NPC 名查配置，大小写不敏感；未知 NPC 返回 null（调用方按"无人设配置"处理）。
    /// </summary>
    public NpcConfig? GetProfile(string npcName)
    {
        if (string.IsNullOrWhiteSpace(npcName))
        {
            return null;
        }

        return _configs.TryGetValue(npcName, out var config) ? config : null;
    }

    /// <summary>全部已装载的配置名（供 DirectorContextBuilder 人设注入）。</summary>
    public IReadOnlyList<string> GetLoadedNames() => _configs.Keys.ToList().AsReadOnly();

    private static NpcConfig Map(NpcConfigData entry)
    {
        var relationships = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (entry.Relationships != null)
        {
            foreach (var kvp in entry.Relationships)
            {
                if (!string.IsNullOrWhiteSpace(kvp.Key))
                {
                    relationships[kvp.Key] = Math.Max(0, kvp.Value);
                }
            }
        }

        return new NpcConfig(
            entry.Name,
            entry.Personality ?? "",
            entry.SpeechStyle ?? "",
            entry.Birthday ?? "",
            entry.IsRomanceable,
            entry.LovedGifts ?? new List<string>(),
            entry.LikedGifts ?? new List<string>(),
            entry.DislikedGifts ?? new List<string>(),
            entry.HatedGifts ?? new List<string>(),
            Math.Max(0, entry.InitialMoney),
            entry.InitialInventory ?? new List<string>(),
            entry.IsProtagonist,
            entry.DefaultMood ?? "",
            relationships);
    }

    /// <summary>
    ///     JSON 反序列化中间模型。注意属性必须可写（get; set;）——
    ///     System.Text.Json 对 get-only 属性静默忽略（NpcEconomyProfileLoader 的已知坑）。
    /// </summary>
    private sealed class NpcConfigData
    {
        public string Name { get; set; } = "";
        public string Personality { get; set; } = "";
        public string SpeechStyle { get; set; } = "";
        public string Birthday { get; set; } = "";
        public bool IsRomanceable { get; set; }
        public List<string>? LovedGifts { get; set; }
        public List<string>? LikedGifts { get; set; }
        public List<string>? DislikedGifts { get; set; }
        public List<string>? HatedGifts { get; set; }
        public int InitialMoney { get; set; }
        public List<string>? InitialInventory { get; set; }
        public bool IsProtagonist { get; set; }
        public string DefaultMood { get; set; } = "";
        public Dictionary<string, int>? Relationships { get; set; }
    }
}
