#nullable enable
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using ValleyAgent.TestMod.Infrastructure;

namespace ValleyAgent.TestMod.Tests.Experience;

/// <summary>
///     点击 NPC 打开对话窗口。验证右键 NPC 后能生成对话文本，
///     且对话窗口、输入区、关闭按钮、文本在视觉上可见。
/// </summary>
[RegisteredTest(TestGroup.Experience, "点击 NPC 打开对话窗口", "experience")]
public class EXP001_ClickNpcOpensDialogue : V3TestBase
{
    private IValleyAgentApi? _api;
    private NPC? _npc;
    private bool _dialogueChecked; // 2026-08-20 Phase 5：轮询等待标志（真实 LLM 5-15s）

    public EXP001_ClickNpcOpensDialogue(IModHelper helper, IMonitor monitor) : base(helper, monitor)
    {
    }

    public override string TestName
    {
        get => "EXP001_ClickNpcOpensDialogue";
    }

    public override int TimeoutTicks
    {
        get => 3000;
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

        // 将 NPC 放到玩家附近
        _npc.setTileLocation(new Vector2(54, 16));
        _npc.Halt();
        _npc.controller = null;

        // 清除对话冷却，确保后续交互可以立即触发
        _api.DialogueCooldownMs = 0;
        ClearDialogueCooldown(_api, "Haley");

        Monitor.Log($"[EXP001] Setup complete. NPC at {_npc.Tile}, player at {Game1.player.Tile}.",
            LogLevel.Info);
    }

    public override bool Update()
    {
        if (_npc == null || _api == null)
        {
            return true;
        }

        // tick 60: 右键点击 NPC
        if (CurrentTick == 60)
        {
            var tx = (int)_npc.Tile.X;
            var ty = (int)_npc.Tile.Y;
            if (Input != null)
            {
                Input.SimulateRightClick(tx, ty);
                Monitor.Log($"[EXP001] Right-clicked NPC at tile ({tx},{ty}).", LogLevel.Info);
            }
            else
            {
                Monitor.Log("[EXP001] Input simulator not available; calling TryGenerateDialogue directly.",
                    LogLevel.Warn);
                _ = _api.TryGenerateDialogue("Haley", "你好");
            }
        }

        // tick 120 起轮询等待对话生成（真实 LLM 响应 5-15s，固定 tick 必然失败）
        if (CurrentTick >= 120 && !_dialogueChecked && CurrentTick % 60 == 0)
        {
            var found = _api.TryGetLastDialogue("Haley", out var response);
            if (found && !string.IsNullOrEmpty(response) || CurrentTick >= 1200)
            {
                _dialogueChecked = true;
                CaptureScreenshot("dialogue_opened");

                Assert("dialogue_generated", found, $"TryGetLastDialogue={found}");
                Assert("dialogue_text_nonempty", found && !string.IsNullOrEmpty(response),
                    $"len={response?.Length ?? 0}");

            AssertVisual("dialogue_window_visible", "",
                "从玩家视角观察：对话窗口是否在屏幕上可见？描述任何可能影响对话体验的问题（如窗口缺失、位置异常、遮挡等）");
            AssertVisual("dialogue_input_area_visible", "",
                "从玩家视角观察：对话输入区域是否在屏幕上可见且可输入？描述任何可能影响输入体验的问题（如输入框缺失、无法聚焦、输入无响应等）");
            AssertVisual("dialogue_close_button_visible", "",
                "从玩家视角观察：对话窗口的关闭按钮是否在屏幕上可见？描述任何可能影响关闭操作的问题（如按钮缺失、位置隐蔽、点击无反应等）");
            AssertVisual("dialogue_text_complete", "",
                "从玩家视角观察：对话文本是否完整渲染在窗口中？描述任何可能影响阅读体验的问题（如文本截断、溢出、渲染不完整等）");
            }
        }

        // 轮询完成或兜底超时（tick 1200 与断言窗口一致）才结束——否则断言永远不执行
        return CurrentTick >= 240 && (_dialogueChecked || CurrentTick >= 1200);
    }

    public override void Teardown()
    {
        if (_api != null)
        {
            _api.DialogueCooldownMs = 30000;
        }

        TestScenes.ClearAll(Helper, Monitor);
        Monitor.Log("[EXP001] Teardown.", LogLevel.Info);
    }
}