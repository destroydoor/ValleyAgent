#nullable enable
using System;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using ValleyAgent.TestMod.Infrastructure;

namespace ValleyAgent.TestMod.Tests.Experience;

/// <summary>
///     NPC 送礼给玩家。触发 TryTriggerGift 后，验证玩家收到礼物，
///     且礼物通知与 NPC 表情在视觉上可见。
/// </summary>
[RegisteredTest(TestGroup.Experience, "NPC 送礼给玩家", "experience")]
public class EXP007_NpcGiftToPlayer : V3TestBase
{
    private IValleyAgentApi? _api;
    private string _giftItemName = "";
    private bool _giftTriggered;
    private NPC? _npc;

    public EXP007_NpcGiftToPlayer(IModHelper helper, IMonitor monitor) : base(helper, monitor)
    {
    }

    public override string TestName
    {
        get => "EXP007_NpcGiftToPlayer";
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

        Monitor.Log($"[EXP007] Setup complete. NPC at {_npc.Tile}.", LogLevel.Info);
    }

    public override bool Update()
    {
        if (_npc == null || _api == null)
        {
            return true;
        }

        // tick 120-240: 触发礼物（持续尝试直至成功或窗口结束）
        if (CurrentTick >= 120 && CurrentTick <= 240 && !_giftTriggered)
        {
            _giftTriggered = _api.TryTriggerGift("Haley", out var itemName);
            if (_giftTriggered)
            {
                _giftItemName = itemName ?? "";
                Monitor.Log($"[EXP007] Gift triggered: '{_giftItemName}' at tick {CurrentTick}.",
                    LogLevel.Info);
            }
        }

        // tick 240: 断言玩家收到礼物 + 截图 + 视觉断言
        if (CurrentTick == 240)
        {
            var received = !string.IsNullOrEmpty(_giftItemName) && PlayerHasItem(_giftItemName);
            Assert("player_received_gift", received,
                $"item='{_giftItemName}' triggered={_giftTriggered}");

            CaptureScreenshot("gift_received");
            AssertVisual("gift_notification_visible", "",
                "从玩家视角观察：收到礼物的通知是否在屏幕上可见？描述任何可能影响反馈感的问题（如通知缺失、显示延迟、提示不明显等）");
            AssertVisual("npc_emote_visible", "",
                "从玩家视角观察：NPC 的表情动画（如爱心/感谢）是否在屏幕上可见？描述任何可能影响情感反馈的问题（如表情缺失、动画不流畅、显示异常等）");
        }

        return CurrentTick >= 360;
    }

    public override void Teardown()
    {
        TestScenes.ClearAll(Helper, Monitor);
        Monitor.Log("[EXP007] Teardown.", LogLevel.Info);
    }

    private static bool PlayerHasItem(string itemName)
    {
        foreach (var item in Game1.player.Items)
        {
            if (item == null)
            {
                continue;
            }

            if (item.Name.Equals(itemName, StringComparison.OrdinalIgnoreCase) ||
                item.DisplayName.Equals(itemName, StringComparison.OrdinalIgnoreCase) ||
                item.ItemId.Equals(itemName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}