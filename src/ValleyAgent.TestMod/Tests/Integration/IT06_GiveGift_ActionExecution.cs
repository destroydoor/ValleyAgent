#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using StardewModdingAPI;
using StardewValley;
using ValleyAgent.Economy;
using ValleyAgent.Protocol;
using ValleyAgent.Services;
using Object = StardewValley.Object;

namespace ValleyAgent.TestMod.Tests.Integration;

/// <summary>
///     IT06：验证 give_gift 步骤 2 同步链路在 C# 端的物理执行（AdjustExecutor 物品批）。
///     链路（2026-08-15 账本迁移）：TS give_gift 工具同步编排 → execute_adjust 2-op 原子批
///     （NPC 扣物 + 玩家收物）→ AdjustExecutor 物理校验 + 原子执行 → adjust_result。
///     本测试驱动 C# 半环（AdjustExecutor.Execute），TS 半环由 bun 测试覆盖。
///     验证点：
///     1. 2-op 送礼批成功：NPC 背包 -1 Tulip、玩家背包 +1 Tulip
///     2. 回执 success=true（携带双方余额镜像）
/// </summary>
public class IT06_GiveGift_ActionExecution : IntegrationTestBase
{
    private const string TulipItemId = "(O)16";

    private bool _actionExecuted;
    private bool _asserted;
    private AdjustExecutor? _adjustExecutor;
    private AgentInstance? _agent;
    private int _itemCountBefore;

    public IT06_GiveGift_ActionExecution(IModHelper helper, IMonitor monitor) : base(helper, monitor)
    {
    }

    public override string TestName
    {
        get => "IT06_GiveGift_ActionExecution";
    }

    public override int TimeoutTicks
    {
        get => 600;
    }

    public override TestGroup Group
    {
        get => TestGroup.Integration;
    }

    public override void Setup()
    {
        if (!TryCommonSetup())
        {
            return;
        }

        _adjustExecutor = Container!.GetService<AdjustExecutor>();
        if (_adjustExecutor == null)
        {
            Skip("AdjustExecutor not registered in container");
            return;
        }

        var agentService = Container.GetService<AgentService>();
        if (agentService == null || !agentService.TryGetBrain(NpcName, out var agent) || agent == null)
        {
            Skip($"agent '{NpcName}' not found in AgentService");
            return;
        }

        _agent = agent;

        // 给 NPC 1 朵郁金香（送礼批第一步扣它的库存）
        var tulip = ItemRegistry.Create(TulipItemId, 1, allowNull: true);
        if (tulip == null)
        {
            Skip($"failed to create {TulipItemId}");
            return;
        }

        if (!agent.Inventory.TryAdd(tulip))
        {
            Skip("npc inventory full — cannot place test item");
            return;
        }

        _itemCountBefore = Game1.player.Items.Count(i => i != null);

        Monitor.Log($"[{TestName}] Setup complete. playerInventoryCount={_itemCountBefore}", LogLevel.Info);
    }

    public override bool Update()
    {
        if (_adjustExecutor == null || _agent == null)
        {
            return true;
        }

        // Phase 1: 送礼 2-op 原子批（模拟 TS give_gift 工具下发 execute_adjust）
        if (CurrentTick == 30 && !_actionExecuted)
        {
            _actionExecuted = true;
            try
            {
                var message = new ProtocolV2.ExecuteAdjustMessage
                {
                    RequestId = "it06-1",
                    InstructionId = "it06-gift-1",
                    NpcName = NpcName,
                    Ops = new List<ProtocolV2.AdjustOp>
                    {
                        new() { Kind = "item", Target = "npc", ItemId = TulipItemId, Quantity = -1, Reason = "give_gift" },
                        new() { Kind = "item", Target = "player", ItemId = TulipItemId, Quantity = 1, Reason = "give_gift" }
                    }
                };
                var result = _adjustExecutor.Execute(message);

                Assert("gift_batch_succeeded", result.Success,
                    result.Success ? "2-op gift batch ok" : $"failed: {result.FailureCode}");
                Assert("gift_steps_count", result.Steps.Count == 2,
                    $"steps={result.Steps.Count} (expected 2)");
            }
            catch (Exception ex)
            {
                Assert("gift_no_throw", false, $"Execute threw: {ex.Message}");
            }
        }

        // Phase 2: 验证玩家背包收到郁金香 + NPC 背包扣除
        if (CurrentTick == 120 && !_asserted)
        {
            _asserted = true;
            var itemCountAfter = Game1.player.Items.Count(i => i != null);
            var inventoryGrew = itemCountAfter > _itemCountBefore;
            Assert("inventory_received_item", inventoryGrew,
                $"before={_itemCountBefore} after={itemCountAfter}");

            var hasTulip = Game1.player.Items.Any(i => i is Object obj &&
                (obj.QualifiedItemId == TulipItemId || obj.ItemId == TulipItemId));
            Assert("has_tulip_in_inventory", hasTulip,
                hasTulip ? "tulip found in player inventory" : "tulip not in inventory");

            var npcTulip = CountNpcTulip(_agent);
            Assert("npc_item_decreased", npcTulip == 0,
                $"npc tulip count after gift = {npcTulip} (expected 0, 已送出)");
        }

        return CurrentTick >= 200;
    }

    public override void Teardown()
    {
        // 移除测试添加的郁金香（玩家侧）
        for (var i = 0; i < Game1.player.Items.Count; i++)
        {
            var item = Game1.player.Items[i];
            if (item is Object obj &&
                (obj.QualifiedItemId == TulipItemId || obj.ItemId == TulipItemId))
            {
                Game1.player.Items[i] = null;
                break;
            }
        }

        // 清空 NPC 测试物品
        if (_agent != null)
        {
            _agent.Inventory.RemoveItem(TulipItemId, 99);
        }

        CommonTeardown();
        Monitor.Log($"[{TestName}] Teardown.", LogLevel.Info);
    }

    private static int CountNpcTulip(AgentInstance agent)
    {
        var total = 0;
        foreach (var item in agent.Inventory.GetAllItems())
        {
            if (item is Object obj &&
                (obj.QualifiedItemId == TulipItemId || obj.ItemId == TulipItemId))
            {
                total += obj.Stack;
            }
        }

        return total;
    }
}
