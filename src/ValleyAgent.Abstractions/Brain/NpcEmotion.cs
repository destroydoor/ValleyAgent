namespace ValleyAgent.Brain
{
    /// <summary>
    /// NPC 的情绪状态，驱动决策权重和对话语气。
    /// 从 AGENTS.md 3.4 定义。
    /// </summary>
    public enum NpcEmotion
    {
        Neutral,    // 平静
        Happy,      // 开心
        Sad,        // 难过
        Angry,      // 生气
        Worried,    // 担心
        Excited,    // 兴奋
        Tired,      // 疲惫
        Grateful,   // 感激
    }

    /// <summary>
    /// 触发情绪变化的事件类型。
    /// </summary>
    public enum NpcEmotionEvent
    {
        None,
        GiftLoved,
        GiftLiked,
        GiftHated,
        GiftDisliked,
        FoughtMonsters,
        HurtInBattle,
        PlayerInDanger,
        Complimented,
        Insulted,
        TaskCompleted,
        IdleTooLong,
        GiftGiven,   // NPC gave a gift to the player
    }

    /// <summary>
    /// NpcEmotion 扩展方法：将情绪映射为游戏内 emote ID。
    /// 用于情绪变化时的视觉反馈。
    /// </summary>
    public static class NpcEmotionExtensions
    {
        /// <summary>
        /// 将情绪映射为 SDV emote ID。返回 null 表示无对应 emote。
        /// </summary>
        public static int? GetEmoteId(this NpcEmotion emotion) => emotion switch
        {
            NpcEmotion.Happy    => 20,  // 心形
            NpcEmotion.Sad      => 28,  // 云朵
            NpcEmotion.Angry    => 12,  // 怒气
            NpcEmotion.Worried  => 8,   // 感叹号
            NpcEmotion.Excited  => 32,  // 星星
            NpcEmotion.Tired    => 24,  // 睡觉
            NpcEmotion.Grateful => 20,  // 心形
            _ => null
        };
    }
}
