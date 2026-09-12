using StardewValley;
using StardewValley.Menus;

namespace ValleyAgent.Utils
{
    /// <summary>
    /// D3 修复：旅行语音反馈辅助。
    /// 玩家通常已切换到新地图，NPC 头顶气泡在旧地图不可见。
    /// Speak 同时发送头顶气泡 + chatBox 消息，保证玩家在新地图也能看到反馈。
    ///
    /// 放在 Abstractions 项目（与 GameEventGuard 同目录）以供 AgentNavigator 引用，
    /// 因为 NpcSpeechHelper 在主 mod（ValleyAgent\Utils），Abstractions 不能反向引用主 mod。
    /// </summary>
    public static class TravelSpeechHelper
    {
        /// <summary>
        /// 同时显示头顶气泡（仅同地图可见）和 chat 消息（全图可见）。
        /// </summary>
        public static void Speak(NPC npc, string text, int durationMs = 3000)
        {
            if (npc == null || string.IsNullOrEmpty(text))
            {
                return;
            }

            // 头顶气泡：仅在 NPC 当前地图可见（玩家若已切图则看不到）
            npc.showTextAboveHead(text, duration: durationMs);

            // chat 消息：全局可见，弥补跨图气泡丢失
            try
            {
                Game1.chatBox?.addMessage($"{npc.displayName}: {text}", Microsoft.Xna.Framework.Color.White);
            }
            catch
            {
                // chatBox 可能在某些时机为 null（如加载早期），静默忽略
            }
        }

        /// <summary>
        /// 发送灰色系统消息到聊天栏，与 NPC 彩色对话明确区分。
        /// 仅用于 player-acts-on-engine 系统事件（vanilla-release / cross-map follow-lost）。
        /// NPC 主动人设发言（Speak）不得降级走此函数 — 那走 AI 主动路径（SpeakCommand Color.Gold / NpcSpeechHelper）。
        /// </summary>
        public static void SystemSpeak(string npcName, string message)
        {
            if (string.IsNullOrEmpty(npcName) || string.IsNullOrEmpty(message))
            {
                return;
            }

            try
            {
                Game1.chatBox?.addMessage($"*{npcName}{message}*", Microsoft.Xna.Framework.Color.LightGray);
            }
            catch
            {
                // chatBox 可能在切换画面时为 null
            }
        }
    }
}
