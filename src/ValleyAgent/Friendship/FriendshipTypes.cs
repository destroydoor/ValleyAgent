using System;

namespace ValleyAgent.Friendship;

/// <summary>交互类型</summary>
public enum InteractionType
{
    /// <summary>普通对话</summary>
    Conversation = 0,

    /// <summary>送礼</summary>
    Gift = 1,

    /// <summary>共同战斗</summary>
    Combat = 2,

    /// <summary>共同劳作</summary>
    Farming = 3,

    /// <summary>共同采矿</summary>
    Mining = 4,

    /// <summary>节日互动</summary>
    Festival = 5
}

/// <summary>特殊事件修正器</summary>
[Flags]
public enum SpecialEventModifiers
{
    None = 0,

    /// <summary>生日 — 3x 倍率</summary>
    Birthday = 1,

    /// <summary>节日 — 2x 倍率</summary>
    Festival = 2,

    /// <summary>雨天 — 0.5x 倍率</summary>
    RainyDay = 4,

    /// <summary>首次见面 — 2x 倍率</summary>
    FirstMeeting = 8
}