namespace ValleyAgent.Economy;

/// <summary>
///     E3-2 经济规则常量（单一事实来源）。
///     数值来源：docs/design/2026-08-02-npc-economy-hire-chat-requirements.md §1.2/1.3/1.4。
///     设计文档：docs/ideas/e32-implementation-思路.md §4。
/// </summary>
public static class EconomyConstants
{
    /// <summary>谨慎型：愿意花掉钱包余额的比例上限（需求 §1.3）。</summary>
    public const double CautiousBudgetRatio = 0.3;

    /// <summary>普通型：愿意花掉钱包余额的比例上限（需求 §1.3）。</summary>
    public const double NormalBudgetRatio = 0.5;

    /// <summary>豪爽型：愿意花掉钱包余额的比例上限（需求 §1.3）。</summary>
    public const double GenerousBudgetRatio = 0.8;

    /// <summary>精明度 0 时的心理价浮动率上限（需求 §1.2：对钱没概念 ±50%）。</summary>
    public const double MaxSavvySpread = 0.5;

    /// <summary>精明度 1 时的心理价浮动率下限（需求 §1.2：精明商人 ±5%）。</summary>
    public const double MinSavvySpread = 0.05;

    /// <summary>还价最大让步轮次（需求 §1.4：最多 3 轮，第 4 轮必拒）。</summary>
    public const int MaxHaggleRounds = 3;

    /// <summary>恶意低价阈值：报价低于公道价 × 0.5 视为恶意（需求 §1.4）。</summary>
    public const double HostileOfferThreshold = 0.5;

    /// <summary>
    ///     每轮新增让步占初始差距 gap 的比例，逐轮减半（需求 §1.4 例子 100 → 50 → 25）。
    ///     累加后 3 轮共让出 87.5% 差距，留 12.5% 余量作为"第 4 轮必拒"的底线。
    /// </summary>
    public static readonly double[] MarkdownRatios = { 0.5, 0.25, 0.125 };

    /// <summary>
    ///     预算档 → 钱包支出比例映射（E3-1 注释约定的 0.3/0.5/0.8 在此落地）。
    /// </summary>
    public static double GetBudgetRatio(BudgetTier tier)
    {
        return tier switch
        {
            BudgetTier.Cautious => CautiousBudgetRatio,
            BudgetTier.Generous => GenerousBudgetRatio,
            _ => NormalBudgetRatio
        };
    }
}