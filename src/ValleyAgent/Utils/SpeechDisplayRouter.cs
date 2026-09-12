using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using StardewValley;

namespace ValleyAgent.Utils;

/// <summary>
///     E2-3 长文本规则的显示模式：&lt;=1 句可气泡；&gt;1 句恒进聊天栏按句间隔弹出。
/// </summary>
public enum SpeechDisplayMode
{
    /// <summary>&lt;=1 句且近距离：走头顶气泡（受 ChatBubbleEnabled 主开关门控）。</summary>
    Bubble,

    /// <summary>&lt;=1 句但远距/跨图：聊天栏单条。</summary>
    ChatBar,

    /// <summary>&gt;1 句：不进气泡，全文进聊天栏、按句间隔弹出。</summary>
    ChatBarStaged
}

/// <summary>
///     E2-3 长文本规则的统一显示路由（静态，E2-1 MovementConstants 同款配置模式）。
///     4 个说话扇出点（NpcSpeechHelper.Speak / SpeakCommand / ShowDialogueCommand / ActiveSpeechRouter.Route）
///     全部收敛到本类：以"句子数"为第一键（&gt;1 句绝不上气泡），距离仅作 &lt;=1 句时的近/远兜底。
///     运行时配置：ServiceInitializer 启动时 + GMCM 热更（OnConfigChanged）调用 ApplyConfig。
///     测试接缝：SetClock / SetChatSink / SetBubbleSink，可在无游戏环境验证决策与调度时序。
///     设计依据：docs/ideas/2026-08-03-e23-long-text-rules.md。
/// </summary>
public static class SpeechDisplayRouter
{
    /// <summary>长文逐句弹入聊天栏的默认间隔（毫秒）。</summary>
    public const int DefaultLongTextIntervalMs = 2000;

    // 运行时配置（默认即常量；由 ApplyConfig 从 ModConfig 覆盖）
    private static bool _chatBubbleEnabled = true;
    private static int _longTextIntervalMs = DefaultLongTextIntervalMs;

    // 运行时/测试接缝：时钟与两个显示出口
    private static Func<long> _nowMs = () => Environment.TickCount;
    private static Action<string, Color>? _chatSink; // (message, color) → Game1.chatBox.addMessage
    private static Action<NPC, string, int>? _bubbleSink; // (npc, text, durationMs) → npc.showTextAboveHead

    private static readonly List<PendingChatMessage> _pendingMessages = new();

    /// <summary>
    ///     从 ModConfig 应用显示规则配置（启动 + GMCM 热更时调用）。
    /// </summary>
    /// <param name="chatBubbleEnabled">气泡主开关：false 时任何文本不进气泡，只走聊天栏。</param>
    /// <param name="longTextIntervalMs">长文按句弹入聊天栏的间隔（毫秒）。</param>
    public static void ApplyConfig(bool chatBubbleEnabled, int longTextIntervalMs)
    {
        _chatBubbleEnabled = chatBubbleEnabled;
        _longTextIntervalMs = Math.Max(0, longTextIntervalMs);
    }

    /// <summary>覆盖时钟（单测用）。null 恢复默认 Environment.TickCount。</summary>
    public static void SetClock(Func<long>? nowMs) => _nowMs = nowMs ?? (() => Environment.TickCount);

    /// <summary>覆盖聊天栏出口（单测用）。null 时消息被静默丢弃。</summary>
    public static void SetChatSink(Action<string, Color>? sink) => _chatSink = sink;

    /// <summary>覆盖气泡出口（单测用）。null 时气泡静默跳过。</summary>
    public static void SetBubbleSink(Action<NPC, string, int>? sink) => _bubbleSink = sink;

    // ─────────────────────────── 纯决策 ───────────────────────────

    /// <summary>是否为长文（&gt;1 句）。长文绝不上气泡。</summary>
    public static bool IsLongText(string text) => SentenceSplitter.CountSentences(text) > 1;

    /// <summary>
    ///     句子数 + 地图/距离上下文 → 显示模式（纯决策，可单测）。
    ///     句子数是第一键：长文无视距离恒走分段聊天栏。
    /// </summary>
    public static SpeechDisplayMode DecideDisplay(string text, bool sameMap, float distance,
        int bubbleDistanceThreshold)
    {
        if (IsLongText(text))
        {
            return SpeechDisplayMode.ChatBarStaged;
        }

        if (!sameMap || distance > bubbleDistanceThreshold)
        {
            return SpeechDisplayMode.ChatBar;
        }

        return SpeechDisplayMode.Bubble;
    }

    /// <summary>
    ///     该文本是否允许进气泡（&lt;=1 句 且 气泡主开关开启）。
    ///     调用方仍需自行判断距离/位置（如 SpeakCommand 的 ≤3 格、ActiveSpeechRouter 的近距）。
    /// </summary>
    public static bool CanUseBubble(string text) => _chatBubbleEnabled && !IsLongText(text);

    // ─────────────────────────── 执行 ───────────────────────────

    /// <summary>
    ///     把 NPC 说话文本送进聊天栏：短句立即单条；长文首句立即、后续按间隔逐条弹出。
    ///     每条保留 "{名字}: " 前缀（与既有聊天栏路径一致）。
    /// </summary>
    public static void SpeakToChatBar(string npcName, string text, Color color)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var sentences = SentenceSplitter.Split(text);
        if (sentences.Count <= 1)
        {
            _chatSink?.Invoke($"{npcName}: {text}", color);
            return;
        }

        // 长文：第 i 句在 i × LongTextIntervalMs 后弹出（第 0 句立即）
        for (var i = 0; i < sentences.Count; i++)
        {
            var message = $"{npcName}: {sentences[i]}";
            var delayMs = (long)i * _longTextIntervalMs;
            var dueMs = _nowMs() + delayMs;
            _pendingMessages.Add(new PendingChatMessage { DueMs = dueMs, Message = message, Color = color });
        }
    }

    /// <summary>
    ///     完整路由（ActiveSpeechRouter.Route 委托给本方法，public 面不变）。
    ///     句子数优先：&gt;1 句恒走分段聊天栏（无视距离/跨图）；&lt;=1 句按距离兜底
    ///     （同图近距 → 气泡；跨图或超距 → 聊天栏）。
    /// </summary>
    /// <param name="npc">说话的 NPC。</param>
    /// <param name="player">玩家 Farmer。</param>
    /// <param name="text">要说的文本。</param>
    /// <param name="bubbleDurationMs">气泡停留时长（毫秒）。</param>
    /// <param name="bubbleDistanceThreshold">气泡距离阈值（格，曼哈顿距离）。</param>
    public static void Route(
        NPC npc,
        Farmer player,
        string text,
        int bubbleDurationMs = 3000,
        int bubbleDistanceThreshold = 8)
    {
        if (npc == null || player == null || string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var color = GetNpcColor(npc.Name);
        var sameMap = npc.currentLocation == player.currentLocation;
        var distance = Math.Abs(npc.Tile.X - player.Tile.X) + Math.Abs(npc.Tile.Y - player.Tile.Y);

        switch (DecideDisplay(text, sameMap, distance, bubbleDistanceThreshold))
        {
            case SpeechDisplayMode.Bubble:
                if (_chatBubbleEnabled)
                {
                    _bubbleSink?.Invoke(npc, text, bubbleDurationMs);
                }
                else
                {
                    // 气泡主开关关闭 → 降级到聊天栏
                    SpeakToChatBar(npc.Name, text, color);
                }

                break;

            case SpeechDisplayMode.ChatBar:
            case SpeechDisplayMode.ChatBarStaged:
                SpeakToChatBar(npc.Name, text, color);
                break;
        }
    }

    /// <summary>
    ///     每帧排空到期聊天消息（EventHandlerInitializer.OnUpdateTicked 调用）。
    ///     正向遍历保证同一批次按入队顺序（句子 0..n）弹出。
    /// </summary>
    public static void Tick()
    {
        if (_pendingMessages.Count == 0 || _chatSink == null)
        {
            return;
        }

        var now = _nowMs();
        for (var i = 0; i < _pendingMessages.Count;)
        {
            var item = _pendingMessages[i];
            if (item.DueMs <= now)
            {
                _pendingMessages.RemoveAt(i);
                _chatSink(item.Message, item.Color);
            }
            else
            {
                i++;
            }
        }
    }

    /// <summary>
    ///     按 NPC 名字生成一致的柔和配色（与 NpcSpeechHelper 同算法，集中到路由供各出口复用）。
    /// </summary>
    public static Color GetNpcColor(string name)
    {
        var hash = name.GetHashCode();
        var r = 140 + (hash & 0x7F);
        var g = 180 + ((hash >> 8) & 0x7F);
        var b = 200 + ((hash >> 16) & 0x7F);
        return new Color(
            Math.Min(255, r),
            Math.Min(255, g),
            Math.Min(255, b));
    }

    /// <summary>待弹出的聊天消息（按到期时间升序处理）。</summary>
    private sealed class PendingChatMessage
    {
        public Color Color;
        public long DueMs;
        public string Message = "";
    }
}