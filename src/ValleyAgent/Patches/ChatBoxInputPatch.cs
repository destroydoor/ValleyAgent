using System;
using HarmonyLib;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;
using ValleyAgent.Chat;
using ValleyAgent.Infrastructure;

namespace ValleyAgent.Patches;

/// <summary>
///     E2-2: 聊天栏输入拦截。玩家在聊天栏按 Enter 提交的普通消息
///     （chatKind==0 且 sourceFarmer==本地玩家）交给 ChatBarRouter 做四层路由消歧。
///     拦截点说明（已反编译游戏确认）：本地提交经 textBoxEnter → receiveChatMessage 到达本方法；
///     远程玩家的消息也经同一方法在主机显示，但 sourceFarmer 是远程 ID → 被守卫跳过，
///     不会重复触发路由。Prefix 返回 void（放行原版），玩家自己的消息照常进聊天栏并广播。
/// </summary>
[HarmonyPatch(typeof(ChatBox))]
public static class ChatBoxInputPatch
{
    private static IMonitor? _monitor;

    public static void Initialize(IMonitor monitor) => _monitor = monitor;

    [HarmonyPrefix]
    [HarmonyPatch(nameof(ChatBox.receiveChatMessage))]
    public static void ReceiveChatMessagePrefix(long sourceFarmer, int chatKind, LocalizedContentManager.LanguageCode language, string message)
    {
        // issue #24：try 必须是首条语句——本前缀注入原版聊天提交调用栈，SMAPI 事件兜底覆盖不到。
        try
        {
            _ = language; // 语言仅影响原版显示，路由不关心

            // 只路由本地玩家自己提交的普通聊天：远程消息（sourceFarmer != 本地）
            // 与私聊/系统信息（chatKind != 0）都不属于"玩家对在场 NPC 说话"。
            if (Game1.player == null
                || sourceFarmer != Game1.player.UniqueMultiplayerID
                || chatKind != 0
                || string.IsNullOrEmpty(message))
            {
                return;
            }

            ChatBarRouter.HandlePlayerChatMessage(message);
        }
        catch (Exception ex)
        {
            // 回落语义（Prefix 返回 void）：路由失败即本条消息不做 AI 路由，
            // 原版聊天显示/广播不受影响（原版逻辑在原方法里，Prefix 异常不再外抛）。
            if (QueueTelemetry.ShouldWarn("harmony:ChatBoxInputPatch.ReceiveChatMessagePrefix"))
            {
                _monitor?.Log($"[ChatBox] Chat routing failed — skip AI routing, vanilla chat unaffected: {ex}",
                    LogLevel.Error);
                ModErrorLog.LogError("Harmony", "ChatBoxInputPatch.ReceiveChatMessagePrefix failed", ex);
            }
        }
    }
}