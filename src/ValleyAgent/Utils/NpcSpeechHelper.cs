using StardewValley;

namespace ValleyAgent.Utils;

/// <summary>
///     Helper that broadcasts NPC speech both as a floating text bubble above the NPC
///     and as a message in the game's chat box (if available).
/// </summary>
public static class NpcSpeechHelper
{
    /// <summary>
    ///     Shows text above the NPC's head and mirrors it to the chat box.
    ///     Use this for actual dialogue / things the NPC "says".
    ///     E2-3 长文本规则：≤1 句才可气泡（受 ChatBubbleEnabled 主开关门控）+ 聊天栏镜像；
    ///     >1 句不进气泡，全文走聊天栏、按句间隔弹出（SpeechDisplayRouter.SpeakToChatBar）。
    /// </summary>
    public static void Speak(NPC npc, string text, int durationMs = 3000)
    {
        if (npc == null || string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var color = SpeechDisplayRouter.GetNpcColor(npc.Name);

        if (SpeechDisplayRouter.CanUseBubble(text))
        {
            npc.showTextAboveHead(text, duration: durationMs);

            if (Game1.chatBox != null)
            {
                Game1.chatBox.addMessage($"{npc.Name}: {text}", color);
            }
        }
        else
        {
            // 长文（>1 句）或气泡主开关关闭 → 只走聊天栏（长文自动分句间隔弹出）
            SpeechDisplayRouter.SpeakToChatBar(npc.Name, text, color);
        }
    }

    /// <summary>
    ///     Shows a status indicator above the NPC's head without sending it to the chat box.
    ///     Use this for HP numbers, thinking dots, emote text, etc.
    ///     E2-3 不拦截：状态指示（非说话文本）仍走气泡。
    /// </summary>
    public static void ShowStatus(NPC npc, string text, int durationMs = 3000)
    {
        if (npc == null || string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        npc.showTextAboveHead(text, duration: durationMs);
    }
}