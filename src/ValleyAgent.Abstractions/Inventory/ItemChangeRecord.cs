namespace ValleyAgent.Inventory
{
    /// <summary>
    /// 物品/钱包变动记录（Phase 3 E3-1 经济系统使用）。
    /// E1-1 仅预留契约，不触发；Phase 3 经济系统填充并经 OnItemChanged 事件发布。
    /// </summary>
    public sealed record ItemChangeRecord(
        string ItemId,
        string NpcName,
        string GameDate,
        string ChangeType,
        int Delta,
        string? Reason);
}