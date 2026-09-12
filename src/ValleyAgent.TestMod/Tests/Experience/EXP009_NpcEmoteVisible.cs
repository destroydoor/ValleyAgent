#nullable enable
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using ValleyAgent.TestMod.Infrastructure;

namespace ValleyAgent.TestMod.Tests.Experience;

/// <summary>
///     NPC 表情动画可见。通过 TryEmote 触发表情（emote 索引 20），
///     录像验证表情动画在视觉上可见。
/// </summary>
[RegisteredTest(TestGroup.Experience, "NPC 表情可见", "experience")]
public class EXP009_NpcEmoteVisible : V3TestBase
{
    private const int EmoteIndex = 20;
    private IValleyAgentApi? _api;
    private bool _emoteTriggered;
    private NPC? _npc;

    public EXP009_NpcEmoteVisible(IModHelper helper, IMonitor monitor) : base(helper, monitor)
    {
    }

    public override string TestName
    {
        get => "EXP009_NpcEmoteVisible";
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

        Monitor.Log($"[EXP009] Setup complete. NPC at {_npc.Tile}.", LogLevel.Info);
    }

    public override bool Update()
    {
        if (_npc == null || _api == null)
        {
            return true;
        }

        // tick 120: 开始录像
        if (CurrentTick == 120)
        {
            Screenshot?.StartRecording(TestName, "emote_action", 2, 300);
            Monitor.Log("[EXP009] Recording started.", LogLevel.Info);
        }

        // tick 120-240: 触发表情
        if (CurrentTick >= 120 && CurrentTick <= 240 && !_emoteTriggered)
        {
            _emoteTriggered = _api.TryEmote("Haley", EmoteIndex);
            if (_emoteTriggered)
            {
                Monitor.Log($"[EXP009] TryEmote({EmoteIndex}) succeeded at tick {CurrentTick}.",
                    LogLevel.Info);
            }
        }

        // tick 360: 停止录像 + 视觉断言
        if (CurrentTick == 360)
        {
            var recordingPath = Screenshot?.StopRecording();
            Monitor.Log($"[EXP009] Recording stopped. path={recordingPath ?? "(null)"}",
                LogLevel.Info);

            // Scenario 5 extension: assert emote was triggered.
            // Note: API only returns bool; it does not expose the actual emote ID
            // emitted by the NPC. The requested EmoteIndex is the only verifiable
            // signal at this layer — deeper ID match verification requires game-state
            // inspection (out of scope for this minimal extension).
            Assert("emote_triggered", _emoteTriggered,
                $"emoteIndex={EmoteIndex} triggered={_emoteTriggered}");

            AssertVisual("emote_animation_visible", recordingPath ?? "",
                $"从玩家视角观察：NPC 表情动画（emote 索引 {EmoteIndex}）是否在录像中自然可见？描述任何可能影响观感的问题（如动画缺失、显示不流畅、表情错位等）");
        }

        return CurrentTick >= 480;
    }

    public override void Teardown()
    {
        TestScenes.ClearAll(Helper, Monitor);
        Monitor.Log("[EXP009] Teardown.", LogLevel.Info);
    }
}