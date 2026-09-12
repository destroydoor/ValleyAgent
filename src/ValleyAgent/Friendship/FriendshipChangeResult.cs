using System;
using System.Collections.Generic;

namespace ValleyAgent.Friendship;

/// <summary>好感度变化结果</summary>
public class FriendshipChangeResult
{
    /// <summary>操作是否成功</summary>
    public bool Success { get; set; }

    /// <summary>错误信息</summary>
    public string ErrorMessage { get; set; } = string.Empty;

    /// <summary>变化前的点数</summary>
    public int PreviousPoints { get; set; }

    /// <summary>变化后的点数</summary>
    public int NewPoints { get; set; }

    /// <summary>实际应用的点数变化</summary>
    public int AppliedChange { get; set; }

    /// <summary>变化原因</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>应用的修正器描述</summary>
    public List<string> AppliedModifiers { get; set; } = new();

    /// <summary>是否应用了边际递减</summary>
    public bool DiminishingReturnsApplied { get; set; }
}

/// <summary>好感度变化记录</summary>
public class FriendshipChangeRecord
{
    /// <summary>NPC 名称</summary>
    public string NpcName { get; set; } = string.Empty;

    /// <summary>变化量</summary>
    public int ChangeAmount { get; set; }

    /// <summary>变化原因</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>交互类型</summary>
    public InteractionType InteractionType { get; set; }

    /// <summary>日期键</summary>
    public string DateKey { get; set; } = string.Empty;

    /// <summary>时间戳</summary>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}