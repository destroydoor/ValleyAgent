using System.Collections.Generic;
using System.Linq;
using ValleyAgent.Utils;
using Xunit;

namespace ValleyAgent.UnitTests;

/// <summary>
///     <see cref="ItemNameResolver" /> 纯逻辑单元测试（阶段1 名称回落）。
///     用合成数据源注入（不依赖 Game1.objectData，单测环境游戏数据未加载）。
///     覆盖：英文内部名 "Wood" → (O)388；中文显示名 "木头" → (O)388；大小写不敏感；
///     垃圾名 → 解析失败 + 相近物品名建议列表（top 3）。
/// </summary>
public class ItemNameResolverTests
{
    /// <summary>合成物品数据：模拟 Data/Objects 关键行（id, 英文内部名, 中文显示名）。</summary>
    private static readonly IReadOnlyList<ItemNameResolver.ItemNameInfo> TestItems = new[]
    {
        new ItemNameResolver.ItemNameInfo("388", "Wood", "木头"),
        new ItemNameResolver.ItemNameInfo("390", "Stone", "石头"),
        new ItemNameResolver.ItemNameInfo("771", "Fiber", "纤维"),
        new ItemNameResolver.ItemNameInfo("16", "Wild Horseradish", "野山葵"),
        new ItemNameResolver.ItemNameInfo("60", "Emerald", "绿宝石")
    };

    // ───────────────────────── 英文内部名 ─────────────────────────

    [Fact]
    public void TryResolve_EnglishName_ResolvesToQualifiedId()
    {
        var ok = ItemNameResolver.TryResolve("Wood", out var qualifiedId, out _, TestItems);

        Assert.True(ok);
        Assert.Equal("(O)388", qualifiedId);
    }

    [Fact]
    public void TryResolve_EnglishName_CaseInsensitive()
    {
        var ok = ItemNameResolver.TryResolve("wood", out var qualifiedId, out _, TestItems);

        Assert.True(ok);
        Assert.Equal("(O)388", qualifiedId);
    }

    // ───────────────────────── 中文显示名 ─────────────────────────

    [Fact]
    public void TryResolve_ChineseDisplayName_ResolvesToQualifiedId()
    {
        var ok = ItemNameResolver.TryResolve("木头", out var qualifiedId, out _, TestItems);

        Assert.True(ok);
        Assert.Equal("(O)388", qualifiedId);
    }

    // ───────────────────────── 垃圾名 → 失败 + 建议 ─────────────────────────

    [Fact]
    public void TryResolve_GarbageName_ReturnsFalseWithSuggestions()
    {
        // "wod" 与 "Wood" 编辑距离 1 → 应进入建议列表
        var ok = ItemNameResolver.TryResolve("wod", out var qualifiedId, out var suggestions, TestItems);

        Assert.False(ok);
        Assert.Null(qualifiedId);
        Assert.NotEmpty(suggestions);
        Assert.Contains("Wood", suggestions);
        Assert.True(suggestions.Count <= ItemNameResolver.MaxSuggestions);
    }

    [Fact]
    public void TryResolve_GarbageName_SuggestionsAreClosestTop3()
    {
        // "Wod" 只与 Wood(距离1)/Stone(距离4+) 相近，应只给 Wood 一个建议
        var ok = ItemNameResolver.TryResolve("Wod", out _, out var suggestions, TestItems);

        Assert.False(ok);
        Assert.Single(suggestions);
        Assert.Equal("Wood", suggestions[0]);
    }

    [Fact]
    public void TryResolve_GarbageName_PrefixMatchGetsSuggestion()
    {
        // "野" 前缀命中 "野山葵"（中文 Ordinal 前缀）→ 建议 野山葵
        var ok = ItemNameResolver.TryResolve("野", out _, out var suggestions, TestItems);

        Assert.False(ok);
        Assert.Contains("野山葵", suggestions);
    }

    [Fact]
    public void TryResolve_EmptyInput_ReturnsFalseNoSuggestions()
    {
        var ok = ItemNameResolver.TryResolve("", out _, out var suggestions, TestItems);

        Assert.False(ok);
        Assert.Empty(suggestions);
    }

    [Fact]
    public void TryResolve_WhitespaceInput_ReturnsFalse()
    {
        var ok = ItemNameResolver.TryResolve("   ", out _, out _, TestItems);

        Assert.False(ok);
    }

    [Fact]
    public void TryResolve_NullDataSource_ReturnsFalseWithoutThrowing()
    {
        // 无数据源注入且游戏未加载（单测环境 Game1.objectData 为 null）→ 安全返回 false
        var ok = ItemNameResolver.TryResolve("Wood", out _, out _);

        Assert.False(ok);
    }

    // ───────────────────────── QualifiedItemId 直达 ─────────────────────────

    [Fact]
    public void TryResolve_QualifiedId_DoesNotMatchByAccident()
    {
        // "(O)388" 不是任何物品名 → 解析失败（真正的 ID 判定由 ItemRegistry.Create 负责）
        var ok = ItemNameResolver.TryResolve("(O)388", out var qualifiedId, out _, TestItems);

        Assert.False(ok);
        Assert.Null(qualifiedId);
    }

    // ───────────────────────── 去重与排序 ─────────────────────────

    [Fact]
    public void TryResolve_Suggestions_DeduplicatedAndOrdered()
    {
        // 大量前缀命中时（如 "E" → Emerald），建议应去重、稳定排序、不超过 MaxSuggestions
        var ok = ItemNameResolver.TryResolve("E", out _, out var suggestions, TestItems);

        Assert.False(ok);
        Assert.Equal(suggestions.Count, suggestions.Distinct().Count());
        Assert.True(suggestions.Count <= ItemNameResolver.MaxSuggestions);
    }
}
