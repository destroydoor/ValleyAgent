namespace ValleyAgent.Friendship;

/// <summary>好感度变化上下文</summary>
public class FriendshipChangeContext
{
    /// <summary>NPC 名称</summary>
    public string NpcName { get; set; } = string.Empty;

    /// <summary>交互类型</summary>
    public InteractionType InteractionType { get; set; } = InteractionType.Conversation;

    /// <summary>当前好感度点数</summary>
    public int CurrentFriendshipPoints { get; set; }

    /// <summary>最大好感度点数</summary>
    public int MaxFriendshipPoints { get; set; } = 2500;

    /// <summary>当前游戏日期键（如 "Spring_1_Year1"）</summary>
    public string CurrentDateKey { get; set; } = string.Empty;

    /// <summary>交互内容文本</summary>
    public string InteractionContent { get; set; } = string.Empty;

    /// <summary>特殊事件修正器</summary>
    public SpecialEventModifiers SpecialEvents { get; set; } = SpecialEventModifiers.None;
}