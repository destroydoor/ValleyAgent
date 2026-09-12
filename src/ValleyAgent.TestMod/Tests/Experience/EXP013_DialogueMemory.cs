#nullable enable
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using ValleyAgent.TestMod.Infrastructure;

namespace ValleyAgent.TestMod.Tests.Experience;

/// <summary>
///     多轮对话记忆验证。Round 1 玩家说 "我叫张三"，NPC 应当通过 remember 工具或
///     short-term memory 记下。Round 2 玩家问 "我叫什么名字"，NPC 回答应包含 "张三"。
///     验证 AgentMemory.conversationHistory 跨轮次保留，且 LLM 能基于历史回答。
/// </summary>
[RegisteredTest(TestGroup.Experience, "多轮对话记忆", "experience")]
public class EXP013_DialogueMemory : V3TestBase
{
    private const string PlayerName = "张三";
    private const string PlayerIntro = "我叫张三";
    private const string PlayerQuestion = "我叫什么名字";

    private IValleyAgentApi? _api;
    private NPC? _npc;
    private string? _round1Reply;
    private bool _round1Sent;
    private string? _round2Reply;
    private bool _round2Sent;

    public EXP013_DialogueMemory(IModHelper helper, IMonitor monitor) : base(helper, monitor)
    {
    }

    public override string TestName
    {
        get => "EXP013_DialogueMemory";
    }

    public override int TimeoutTicks
    {
        get => 12000; // 2 rounds × ~5s LLM latency + buffer
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

        _api.DialogueCooldownMs = 0;
        ClearDialogueCooldown(_api, "Haley");

        Monitor.Log("[EXP013] Setup complete. Two-round memory test starting.", LogLevel.Info);
    }

    public override bool Update()
    {
        if (_npc == null || _api == null)
        {
            return true;
        }

        // Phase 1 (tick 60): Round 1 — introduce player name.
        // TryGenerateDialogue queues the request asynchronously (returns true if queued);
        // the reply is fetched later via TryGetLastDialogue once the LLM has finished.
        if (CurrentTick == 60 && !_round1Sent)
        {
            _round1Sent = _api.TryGenerateDialogue("Haley", PlayerIntro);
            Assert("round1_sent", _round1Sent,
                $"intro=\"{PlayerIntro}\" queued={_round1Sent}");
            Monitor.Log($"[EXP013] Round 1: player=\"{PlayerIntro}\" queued={_round1Sent}",
                LogLevel.Info);
        }

        // Phase 2 (tick 360, ~6s later): fetch Round 1 reply, then send Round 2.
        // 6s gives the LLM/server enough time to finish Round 1 + persist to AgentMemory.
        if (CurrentTick == 360 && _round1Sent && !_round2Sent)
        {
            _ = _api.TryGetLastDialogue("Haley", out _round1Reply);
            Monitor.Log($"[EXP013] Round 1 reply fetched: \"{_round1Reply ?? "(null)"}\"",
                LogLevel.Info);

            _round2Sent = _api.TryGenerateDialogue("Haley", PlayerQuestion);
            Assert("round2_sent", _round2Sent,
                $"question=\"{PlayerQuestion}\" queued={_round2Sent}");
            Monitor.Log($"[EXP013] Round 2: player=\"{PlayerQuestion}\" queued={_round2Sent}",
                LogLevel.Info);
        }

        // Phase 3 (tick 660, ~5s after Round 2 sent): fetch Round 2 reply + assert remembers name.
        if (CurrentTick == 660 && _round2Sent)
        {
            _ = _api.TryGetLastDialogue("Haley", out _round2Reply);

            // Core assertion: Round 2 reply must contain "张三".
            var replyContainsName = _round2Reply?.Contains(PlayerName) ?? false;
            Assert("round2_remembers_name", replyContainsName,
                $"expected \"{PlayerName}\" in reply, got=\"{_round2Reply ?? "(null)"}\"");

            Monitor.Log($"[EXP013] Round 2 reply fetched: npcReply=\"{_round2Reply ?? "(null)"}\" " +
                        $"containsName={replyContainsName}", LogLevel.Info);
        }

        return CurrentTick >= 780;
    }

    public override void Teardown()
    {
        Monitor.Log($"[EXP013] Teardown. round1Sent={_round1Sent} round2Sent={_round2Sent} " +
                    $"round2Remembered={_round2Reply?.Contains(PlayerName) ?? false}", LogLevel.Info);
    }
}