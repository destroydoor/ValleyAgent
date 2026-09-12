#nullable enable
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using ValleyAgent.TestMod.Infrastructure;

namespace ValleyAgent.TestMod.Tests.Experience;

/// <summary>
///     NPC 对话气泡可见。通过 TrySpeak 让 NPC 说出 "今天阳光真好！"，
///     验证说话文本正确，气泡和聊天框消息在视觉上可见。
/// </summary>
[RegisteredTest(TestGroup.Experience, "NPC 对话气泡可见", "experience")]
public class EXP008_NpcSpeechBubbleVisible : V3TestBase
{
    private const string SpeechText = "今天阳光真好！";
    private IValleyAgentApi? _api;
    private NPC? _npc;
    private bool _speakResult;

    public EXP008_NpcSpeechBubbleVisible(IModHelper helper, IMonitor monitor) : base(helper, monitor)
    {
    }

    public override string TestName
    {
        get => "EXP008_NpcSpeechBubbleVisible";
    }

    public override int TimeoutTicks
    {
        get => 4000;
    }

    public override TestGroup Group
    {
        get => TestGroup.Experience;
    }

    public override void Setup()
    {
        // 同 PIPE005：ModEntry.API 是 GameLaunched 时的 fallback（子 API 为 null），走惰性完整实例。
        _api = ValleyAgent.ModEntry.Instance?.API;
        if (_api == null)
        {
            Skip("API not available");
            return;
        }

        SafeWarp.Farmer(Monitor, "Farm", 54, 15);
        _ = _api.TryAllocateAgent("Haley");
        _npc = Game1.getCharacterFromName("Haley");
        if (_npc == null)
        {
            Skip("Haley not found");
            return;
        }

        _npc.setTileLocation(new Vector2(54, 16));
        _npc.Halt();
        _npc.controller = null;

        Monitor.Log($"[EXP008] Setup complete. NPC at {_npc.Tile}.", LogLevel.Info);
    }

    public override bool Update()
    {
        if (_npc == null || _api == null)
        {
            return true;
        }

        // tick 120: 截图（说话前）
        if (CurrentTick == 120)
        {
            CaptureScreenshot("before_speech");
        }

        // tick 120-240: 触发说话
        if (CurrentTick >= 120 && CurrentTick <= 240 && !_speakResult)
        {
            // durationMs = 3000（3 秒显示时间）
            _speakResult = _api.TrySpeak("Haley", SpeechText, 3000);
            if (_speakResult)
            {
                Monitor.Log($"[EXP008] TrySpeak succeeded at tick {CurrentTick}.", LogLevel.Info);
            }
        }

        // tick 240: 截图（说话后）+ 断言文本正确 + 视觉断言
        if (CurrentTick == 240)
        {
            CaptureScreenshot("after_speech");

            // TrySpeak 成功即代表文本已设置；视觉断言验证实际显示
            Assert("speech_text_correct", _speakResult,
                $"TrySpeak={_speakResult} expected='{Truncate(SpeechText)}'");

            AssertVisual("speech_bubble_visible", "",
                "从玩家视角观察：NPC 头顶的对话气泡是否在屏幕上可见且内容含 '今天阳光真好！'？描述任何可能影响气泡显示的问题（如气泡缺失、内容错误、显示位置异常等）");
            AssertVisual("chatbox_message_visible", "",
                "从玩家视角观察：聊天框中的消息文本是否在屏幕上可见且内容含 '今天阳光真好！'？描述任何可能影响消息显示的问题（如消息缺失、文本错误、显示延迟等）");
        }

        return CurrentTick >= 360;
    }

    public override void Teardown()
    {
        TestScenes.ClearAll(Helper, Monitor);
        Monitor.Log("[EXP008] Teardown.", LogLevel.Info);
    }
}