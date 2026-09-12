#nullable enable
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using ValleyAgent.TestMod.Infrastructure;

namespace ValleyAgent.TestMod.Tests.Experience;

/// <summary>
///     对话文本输入与发送。输入 "你好啊"，按 Enter 发送，
///     等待 NPC 回复，验证玩家消息已发送且 NPC 回复可见、未脱戏。
/// </summary>
[RegisteredTest(TestGroup.Experience, "对话文本输入与发送", "experience")]
public class EXP003_DialogueTextInputAndSend : V3TestBase
{
    private const string PlayerText = "你好啊";
    private IValleyAgentApi? _api;
    private bool _messageSent;
    private NPC? _npc;
    private bool _replyChecked; // 2026-08-20 Phase 5：轮询等待标志（真实 LLM 响应 5-15s）

    public EXP003_DialogueTextInputAndSend(IModHelper helper, IMonitor monitor) : base(helper, monitor)
    {
    }

    public override string TestName
    {
        get => "EXP003_DialogueTextInputAndSend";
    }

    public override int TimeoutTicks
    {
        get => 5000;
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

        Monitor.Log($"[EXP003] Setup complete. NPC at {_npc.Tile}.", LogLevel.Info);
    }

    public override bool Update()
    {
        if (_npc == null || _api == null)
        {
            return true;
        }

        // tick 60: 打开对话
        if (CurrentTick == 60)
        {
            var tx = (int)_npc.Tile.X;
            var ty = (int)_npc.Tile.Y;
            if (Input != null)
            {
                Input.SimulateRightClick(tx, ty);
            }
            else
            {
                _ = _api.TryGenerateDialogue("Haley", "");
            }
        }

        // tick 120: 输入文本
        if (CurrentTick == 120)
        {
            if (Input != null)
            {
                Input.SimulateTextInput(PlayerText);
                Monitor.Log($"[EXP003] Text input '{PlayerText}' queued.", LogLevel.Info);
            }

            CaptureScreenshot("player_text_input");
            AssertVisual("player_text_visible", "",
                $"从玩家视角观察：玩家输入的文本 '{PlayerText}' 是否在屏幕上可见？描述任何可能影响输入反馈的问题（如文本未显示、显示延迟、显示错误等）");
        }

        // tick 180: 按 Enter 发送
        if (CurrentTick == 180)
        {
            if (Input != null)
            {
                Input.SimulateKeyPress(SButton.Enter);
            }

            // 同时通过 API 显式触发对话生成（确保请求被排队）
            _messageSent = _api.TryGenerateDialogue("Haley", PlayerText);
            Assert("player_message_sent", _messageSent,
                $"TryGenerateDialogue={_messageSent}");
        }

        // tick 360 起轮询等待 NPC 回复（2026-08-20 Phase 5：真实 LLM 响应 5-15s，
        // 固定 tick 断言必然失败——改为每 60 tick 轮询，超时窗口 1200 tick ≈ 20s）。
        // 超时仍未收到 → 断言失败（超时=失败语义由 runner 层兜底，这里显式断言）。
        if (CurrentTick >= 360 && !_replyChecked && CurrentTick % 60 == 0)
        {
            var found = _api.TryGetLastDialogue("Haley", out var reply);
            var elapsedTicks = CurrentTick - 180;
            if (found && !string.IsNullOrEmpty(reply) || CurrentTick >= 1200)
            {
                _replyChecked = true;
                CaptureScreenshot("npc_reply");
                Assert("npc_reply_received", found && !string.IsNullOrEmpty(reply),
                    $"found={found} len={reply?.Length ?? 0}");

                // 回复必须在发送后 5s 内到达（真实 LLM 下为轮询窗口内的首次命中）
                Assert("npc_reply_within_5s", found && elapsedTicks <= 300,
                    $"elapsedTicks={elapsedTicks} threshold=300 found={found}");

                // 中文回复检查
                var hasChinese = !string.IsNullOrEmpty(reply) && ContainsChinese(reply);
                Assert("npc_reply_contains_chinese", hasChinese,
                    $"reply=\"{Truncate(reply ?? "")}\" hasChinese={hasChinese}");

                AssertVisual("npc_reply_visible", "",
                    "从玩家视角观察：NPC 的回复文本是否在屏幕上可见？描述任何可能影响对话体验的问题（如回复未显示、显示延迟、文本截断等）");
                AssertVisual("npc_reply_not_ooc", "",
                    "从玩家视角观察：NPC 回复是否保持了角色设定，没有出戏（OOC）内容？描述任何可能影响沉浸感的问题（如角色性格不符、出戏表述、设定崩坏等）");
            }
        }

        return CurrentTick >= 480;
    }

    public override void Teardown()
    {
        if (_api != null)
        {
            _api.DialogueCooldownMs = 30000;
        }

        TestScenes.ClearAll(Helper, Monitor);
        Monitor.Log("[EXP003] Teardown.", LogLevel.Info);
    }

    private static bool ContainsChinese(string s)
    {
        foreach (var c in s)
        {
            if (c >= '\u4e00' && c <= '\u9fff')
            {
                return true;
            }
        }

        return false;
    }
}