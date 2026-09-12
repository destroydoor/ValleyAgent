using System;
using System.Globalization;
using System.Linq;
using ValleyAgent.Agents;

namespace ValleyAgent.Protocol;

/// <summary>
///     处理 TS 导演下发的 allocate_agent 消息（设计文档 §4.2.2）。
///     ForceAllocate 目标 NPC 并写入 KeepUntil 豁免互动空闲淘汰。
///     返回 (success, reason) 用于回 action_result 给 TS。
/// </summary>
public class AllocateAgentHandler
{
    private readonly Func<string, DateTime?> _keepUntilParser;
    private readonly AgentAllocationManager _manager;

    /// <param name="manager">分配管理器。</param>
    /// <param name="keepUntilParser">ISO 8601 → DateTime 解析器（测试可注入，fail-open 返回 null）。</param>
    public AllocateAgentHandler(
        AgentAllocationManager manager,
        Func<string, DateTime?>? keepUntilParser = null)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _keepUntilParser = keepUntilParser ?? DefaultParseKeepUntil;
    }

    /// <summary>
    ///     处理 allocate_agent 消息：ForceAllocate + 写入 KeepUntil。
    /// </summary>
    /// <returns>(success, reason)：成功=Allocated；满员无可替槽=MaxCapacityReached；参数非法=InvalidState。</returns>
    public (bool Success, ProtocolV2.ActionResultReason Reason) Handle(ProtocolV2.AllocateAgentMessage msg)
    {
        if (msg == null)
        {
            return (false, ProtocolV2.ActionResultReason.InvalidState);
        }

        if (string.IsNullOrWhiteSpace(msg.NpcName))
        {
            return (false, ProtocolV2.ActionResultReason.InvalidState);
        }

        // 解析 keepUntilIso，失败 fail-open（无豁免）
        DateTime? keepUntil = null;
        if (!string.IsNullOrWhiteSpace(msg.KeepUntilIso))
        {
            keepUntil = _keepUntilParser(msg.KeepUntilIso);
        }

        bool allocated;
        if (_manager.IsAllocated(msg.NpcName))
        {
            // 已分配 → 视为成功，仅更新 keepUntil
            allocated = true;
        }
        else
        {
            // ForceAllocate：满员时替换最低优先级 non-manual Agent；无可替槽返回 false
            allocated = _manager.ForceAllocate(msg.NpcName);
        }

        if (!allocated)
        {
            return (false, ProtocolV2.ActionResultReason.MaxCapacityReached);
        }

        // 写入 KeepUntil 豁免
        var info = _manager.GetAllAllocatedAgents()
            .FirstOrDefault(a => string.Equals(a.NpcName, msg.NpcName, StringComparison.OrdinalIgnoreCase));
        if (info != null)
        {
            info.KeepUntil = keepUntil;
        }

        return (true, ProtocolV2.ActionResultReason.Allocated);
    }

    /// <summary>
    ///     默认 ISO 8601 解析器：兼容多种格式，解析失败 fail-open 返回 null。
    ///     AssumeUniversal + AdjustToUniversal 保证返回值为 UTC。
    /// </summary>
    private static DateTime? DefaultParseKeepUntil(string iso)
    {
        if (DateTime.TryParse(
                iso,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var dt))
        {
            return dt;
        }

        return null;
    }
}