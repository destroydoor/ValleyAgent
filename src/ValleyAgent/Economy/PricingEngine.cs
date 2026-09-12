using System;

namespace ValleyAgent.Economy;

/// <summary>
///     E3-2 定价引擎（纯函数，不碰游戏状态，便于单测）。
///     公道价 = 游戏卖价（调用方从 Object.salePrice() 取，负数钳 0）。
///     精明度越高 → 心理价区间越窄；出价上限 = min(心理价上限, 钱包 × 预算比例)。
///     设计文档：docs/ideas/e32-implementation-思路.md §2。
/// </summary>
public static class PricingEngine
{
    /// <summary>
    ///     公道价：负数钳 0 后原样返回。心理价区间/出价上限/还价底线的单一语义锚点。
    /// </summary>
    public static int CalculateFairPrice(int salePrice) => Math.Max(0, salePrice);

    /// <summary>
    ///     精明度 → 心理价浮动率：savvy 越高浮动越窄。
    ///     spread = Max − (Max − Min) × clamp01(savvy)，默认 0.5 − 0.45 × savvy。
    /// </summary>
    public static double CalculateSavvySpread(double savvy)
    {
        var clamped = Math.Clamp(savvy, 0.0, 1.0);
        return EconomyConstants.MaxSavvySpread
               - (EconomyConstants.MaxSavvySpread - EconomyConstants.MinSavvySpread) * clamped;
    }

    /// <summary>心理价区间下限 low = round(公道价 × (1 − spread))（NPC 买入时还价向它让步）。</summary>
    public static int CalculatePsychologicalLow(int fairPrice, double savvy) =>
        RoundToInt(fairPrice * (1.0 - CalculateSavvySpread(savvy)));

    /// <summary>心理价区间上限 high = round(公道价 × (1 + spread))（NPC 买入出价上限 / 卖出开价）。</summary>
    public static int CalculatePsychologicalHigh(int fairPrice, double savvy) =>
        RoundToInt(fairPrice * (1.0 + CalculateSavvySpread(savvy)));

    /// <summary>
    ///     购买力上限 = 钱包余额 × 预算比例（向下取整，绝不超支）。
    /// </summary>
    public static int CalculateBudgetCap(NpcEconomyProfile profile, int wallet)
    {
        var budgetRatio = EconomyConstants.GetBudgetRatio(profile.BudgetTier);
        return (int)Math.Floor(Math.Max(0, wallet) * budgetRatio);
    }

    /// <summary>
    ///     NPC 买入玩家的物品时开出的最高价 = min(心理价上限, 购买力上限)。
    ///     价格认知与支付能力是两件事，两个都要满足（需求 §1.3）。
    /// </summary>
    public static int CalculateNpcOfferPrice(int salePrice, NpcEconomyProfile profile, int wallet)
    {
        var fair = CalculateFairPrice(salePrice);
        var psychologicalHigh = CalculatePsychologicalHigh(fair, profile.Savvy);
        var budgetCap = CalculateBudgetCap(profile, wallet);
        return Math.Min(psychologicalHigh, budgetCap);
    }

    private static int RoundToInt(double value)
    {
        // AwayFromZero 避免 banker's rounding 的半数歧义（如 63.5 → 64）
        return (int)Math.Round(value, MidpointRounding.AwayFromZero);
    }
}