using System;
using System.Collections.Generic;
using System.Linq;
using StardewValley;

namespace ValleyAgent.Utils;

/// <summary>
///     give_item / trade 的 item_id 名称回落解析器（阶段 1）。
///     玩家或 LLM 传的是 QualifiedItemId（如 "(O)388"）以外的物品名（英文内部名如 "Wood"，
///     或本地化显示名如中文 "木头"）时，从游戏 Data/Objects 数据中按 Name / DisplayName
///     精确匹配出 QualifiedItemId，失败时输出 top 3 相近物品名建议。
///     数据源可注入（单测传合成数据，避免依赖 Game1.objectData），默认读游戏数据并缓存。
/// </summary>
public static class ItemNameResolver
{
    /// <summary>建议列表最大条数。</summary>
    public const int MaxSuggestions = 3;

    /// <summary>物品名信息行：Id 为 Data/Objects 的 key（如 "388"），Name 为英文内部名，DisplayName 为本地化名。</summary>
    public readonly record struct ItemNameInfo(string Id, string Name, string DisplayName);

    // 游戏数据缓存：避免每次名称回落都全量遍历 objectData（数据量大，扫描代价高）。
    // 记录来源引用，语言切换 / 内容重载时 objectData 引用变化，引用不等即重建。
    private static IReadOnlyList<ItemNameInfo>? _cachedItems;
    private static object? _cachedSourceRef;

    /// <summary>
    ///     尝试把输入解析为 QualifiedItemId。
    ///     匹配顺序：英文内部名（OrdinalIgnoreCase，兼容大小写）→ 本地化显示名（Ordinal，中文按字精确）。
    ///     匹配失败时 suggestions 输出相近物品名（前缀命中优先，其次编辑距离 ≤ 2），最多 <see cref="MaxSuggestions" /> 条。
    ///     已带 "(O)" 前缀的合法 QualifiedItemId 不会被名称匹配命中，原样返回失败（由调用方 Create 判定）。
    /// </summary>
    public static bool TryResolve(
        string input,
        out string? qualifiedId,
        out IReadOnlyList<string> suggestions,
        IEnumerable<ItemNameInfo>? dataSource = null)
    {
        qualifiedId = null;
        suggestions = Array.Empty<string>();

        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var items = dataSource != null ? dataSource.ToList() : LoadGameData();
        if (items == null || items.Count == 0)
        {
            // 游戏数据未加载（如单测环境无注入数据源）→ 无法名称匹配
            return false;
        }

        foreach (var info in items)
        {
            if (string.Equals(info.Name, input, StringComparison.OrdinalIgnoreCase)
                || string.Equals(info.DisplayName, input, StringComparison.Ordinal))
            {
                qualifiedId = "(O)" + info.Id;
                return true;
            }
        }

        suggestions = BuildSuggestions(input, items);
        return false;
    }

    /// <summary>
    ///     从游戏对象数据构建名称表。Game1.objectData 在测试环境为 null（游戏未加载），
    ///     此时返回 null 表示"无数据可查"，调用方按未命中处理。
    /// </summary>
    private static IReadOnlyList<ItemNameInfo>? LoadGameData()
    {
        var source = Game1.objectData;
        if (source == null || source.Count == 0)
        {
            return null;
        }

        if (!ReferenceEquals(_cachedSourceRef, source))
        {
            _cachedItems = source
                .Select(kv => new ItemNameInfo(kv.Key, kv.Value.Name, kv.Value.DisplayName))
                .ToList();
            _cachedSourceRef = source;
        }

        // ReferenceEquals 保证缓存已重建（source 非空），null-forgiving 仅消除编译器静态警告
        return _cachedItems!;
    }

    /// <summary>
    ///     收集相近物品名：前缀命中（英文忽略大小写 / 中文按字）距离 0 优先，
    ///     其次取 Name / DisplayName 中编辑距离较小者（≤ 2 才入选），按距离升序取 top 3 去重。
    /// </summary>
    private static List<string> BuildSuggestions(string input, IReadOnlyList<ItemNameInfo> items)
    {
        var scored = new List<(int Distance, string Name)>();
        foreach (var info in items)
        {
            var nameScore = ScoreName(input, info.Name);
            var displayScore = ScoreName(input, info.DisplayName);

            // 中英同名时 Name 与 DisplayName 距离相同，优先返回英文内部名（稳定、可复述）
            var (distance, name) = nameScore <= displayScore
                ? (nameScore, info.Name)
                : (displayScore, info.DisplayName);

            if (distance <= 2)
            {
                scored.Add((distance, name));
            }
        }

        return scored
            .OrderBy(s => s.Distance)
            .ThenBy(s => s.Name, StringComparer.Ordinal)
            .Select(s => s.Name)
            .Distinct()
            .Take(MaxSuggestions)
            .ToList();
    }

    /// <summary>前缀命中返回 0；否则返回编辑距离（仅当 ≤ 2 时调用方采用）。</summary>
    private static int ScoreName(string input, string candidate)
    {
        if (string.IsNullOrEmpty(candidate))
        {
            return int.MaxValue;
        }

        if (candidate.StartsWith(input, StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith(input, StringComparison.Ordinal))
        {
            return 0;
        }

        return LevenshteinDistance(input, candidate);
    }

    /// <summary>经典 DP 编辑距离（建议列表用，物品名都很短，O(n·m) 开销可忽略）。</summary>
    private static int LevenshteinDistance(string source, string target)
    {
        if (source.Length == 0)
        {
            return target.Length;
        }

        if (target.Length == 0)
        {
            return source.Length;
        }

        var prev = new int[target.Length + 1];
        var curr = new int[target.Length + 1];
        for (var j = 0; j <= target.Length; j++)
        {
            prev[j] = j;
        }

        for (var i = 1; i <= source.Length; i++)
        {
            curr[0] = i;
            for (var j = 1; j <= target.Length; j++)
            {
                var cost = source[i - 1] == target[j - 1] ? 0 : 1;
                curr[j] = Math.Min(Math.Min(curr[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }

            (prev, curr) = (curr, prev);
        }

        return prev[target.Length];
    }
}
