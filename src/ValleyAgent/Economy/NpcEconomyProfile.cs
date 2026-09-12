using System.Collections.Generic;

namespace ValleyAgent.Economy;

/// <summary>
///     NPC 预算档次（E3-1 经济数据层）。
///     预算费率映射（来自 docs/design/2026-08-02-npc-economy-hire-chat-requirements.md §1）：
///     Cautious 0.3 / Normal 0.5 / Generous 0.8——"愿意花掉钱包余额的上限比例"。
///     E3-2 定价策略消费，本轮仅数据装载。
/// </summary>
public enum BudgetTier
{
    Cautious,
    Normal,
    Generous
}

/// <summary>
///     单个 NPC 的经济档案（E3-1）。不可变 record，由 NpcEconomyProfileLoader 从
///     Data/npc_economy.json 装载，装载时完成数值钳制与枚举回退。
/// </summary>
/// <param name="Name">NPC 名（查找 key，大小写不敏感）。</param>
/// <param name="InitialMoney">初始资金：首次分配 Agent 时的钱包回填值（存档恢复会覆盖）。</param>
/// <param name="InitialItems">初始物品（qualified item id 列表，E3-2+ 交易流程消费，本轮只装载）。</param>
/// <param name="Savvy">精明度 0~1：E3-2 定价时 LLM 议价意愿的输入。</param>
/// <param name="BudgetTier">预算档次：cautious / normal / generous。</param>
/// <param name="DailyWage">日薪：E3-3 雇佣定价的参考锚。</param>
/// <param name="Talkativeness">话痨度 0~1：E5 喊话频率/长度参考。</param>
/// <param name="PurchaseItems">求购偏好（qualified item id 列表，E3-5 求购生成的目标池，可为空）。</param>
public sealed record NpcEconomyProfile(
    string Name,
    int InitialMoney,
    List<string> InitialItems,
    double Savvy,
    BudgetTier BudgetTier,
    int DailyWage,
    double Talkativeness,
    List<string>? PurchaseItems = null);