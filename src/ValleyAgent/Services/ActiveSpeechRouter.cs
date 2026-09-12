using StardewValley;
using ValleyAgent.Utils;

namespace ValleyAgent.Services;

/// <summary>
///     T18: 按句子数 + NPC 与玩家距离路由主动说话文本（pre_speak 队列消费者）。
///     E2-3 后委托给 SpeechDisplayRouter.Route：句子数是第一键（&gt;1 句恒走分段聊天栏，不进气泡），
///     距离仅作 &lt;=1 句时的近/远兜底（近距离 → 头顶气泡；远距离/跨地图 → ChatBox 消息）。
/// </summary>
public static class ActiveSpeechRouter
{
    /// <summary>
    ///     路由主动说话文本。委托给 SpeechDisplayRouter.Route（public 面不变，
    ///     EventHandlerInitializer.ProcessPendingPreSpeakActions 调用点无需改动）。
    /// </summary>
    /// <param name="npc">说话的 NPC。</param>
    /// <param name="player">玩家 Farmer。</param>
    /// <param name="text">要说的文本。</param>
    public static void Route(NPC npc, Farmer player, string text) => SpeechDisplayRouter.Route(npc, player, text);
}