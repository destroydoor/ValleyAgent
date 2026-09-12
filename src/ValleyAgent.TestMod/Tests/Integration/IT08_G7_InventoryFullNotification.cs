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
///     IT08：验证步骤 2 背包满时的原子回滚（AdjustExecutor 物品批 INVENTORY_FULL 分支）。
///     链路（2026-08-15 账本迁移）：TS give_item/give_gift 同步编排 → execute_adjust 2-op 原子批
///     （NPC 扣物 + 玩家收物）→ C# 物理校验发现玩家背包满 → 提交失败 → 回滚已提交的 NPC 扣物步
///     → adjust_result failureCode=inventoryFull（TS 账本 rolled_back，LLM 自然改口）。
///     与旧行为（掉地上 + 聊天栏通知）不同：新流程无掉落语义，失败即回滚零副作用。
///     验证点：
///     1. 玩家背包满 → 批失败（INVENTORY_FULL）
///     2. 原子回滚：NPC 背包物品未扣（rollback 生效）
///     3. 玩家背包数量未增长（物品没进背包）
/// </summary>
public class IT08_G7_InventoryFullNotification : IntegrationTestBase
{
    private const string TulipItemId = "(O)16";

    private bool _actionExecuted;
    private bool _asserted;
    private AdjustExecutor? _adjustExecutor;
    private AgentInstance? _agent;
    private int _itemCountBefore;
    private int _npcTulipBefore;

    public IT08_G7_InventoryFullNotification(IModHelper helper, IMonitor monitor) : base(helper, monitor)
    {
    }

    public override string TestName
    {
        get => "IT08_G7_InventoryFullNotification";
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

        // 给 NPC 1 朵郁金香（批第一步扣它，第二步加给玩家）
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

        // 把玩家背包填满（MaxItems 默认 36），使玩家收物步物理校验失败
        // 用直接赋值而非 addItemToInventoryBool：后者在 Items 列表大小已等于 MaxItems
        // 但存在 null 槽时仍可能因 SDV 内部逻辑返回 false，导致填不满。
        var maxItems = Game1.player.MaxItems;
        for (var i = 0; i < maxItems && i < Game1.player.Items.Count; i++)
        {
            if (Game1.player.Items[i] == null)
            {
                var stone = ItemRegistry.Create("(O)390", allowNull: true);
                if (stone != null)
                {
                    Game1.player.Items[i] = stone;
                }
            }
        }

        _itemCountBefore = Game1.player.Items.Count(i => i != null);
        _npcTulipBefore = CountNpcTulip(agent);

        Monitor.Log(
            $"[{TestName}] Setup complete. inventoryCount={_itemCountBefore}/{maxItems}, npcTulip={_npcTulipBefore}",
            LogLevel.Info);
    }

    public override bool Update()
    {
        if (_adjustExecutor == null || _agent == null)
        {
            return true;
        }

        // Phase 1: 触发送礼 2-op 原子批（背包满 → 提交失败 → 回滚）
        if (CurrentTick == 30 && !_actionExecuted)
        {
            _actionExecuted = true;
            try
            {
                var message = new ProtocolV2.ExecuteAdjustMessage
                {
                    RequestId = "it08-1",
                    InstructionId = "it08-gift-1",
                    NpcName = NpcName,
                    Ops = new List<ProtocolV2.AdjustOp>
                    {
                        new() { Kind = "item", Target = "npc", ItemId = TulipItemId, Quantity = -1, Reason = "give_item" },
                        new() { Kind = "item", Target = "player", ItemId = TulipItemId, Quantity = 1, Reason = "give_item" }
                    }
                };
                var result = _adjustExecutor.Execute(message);

                // 背包满 → 玩家收物步提交失败 → INVENTORY_FULL
                Assert("inventory_full_rejected", !result.Success && result.FailureCode == ProtocolV2.AdjustFailureCode.InventoryFull,
                    result.Success ? "batch succeeded despite full inventory" : $"rejected: {result.FailureCode}");

                // 原子回滚：第一步（NPC 扣物）已提交后被回滚
                var npcTulipAfter = CountNpcTulip(_agent);
                Assert("rollback_npc_item_restored", npcTulipAfter == _npcTulipBefore,
                    $"npc tulip after failed batch = {npcTulipAfter} (expected {_npcTulipBefore}, 回滚生效)");
            }
            catch (Exception ex)
            {
                Assert("batch_no_throw", false, $"Execute threw: {ex.Message}");
            }
        }

        // Phase 2: 玩家背包未增长（物品没进背包）
        if (CurrentTick == 120 && !_asserted)
        {
            _asserted = true;
            var itemCountAfter = Game1.player.Items.Count(i => i != null);
            Assert("inventory_unchanged", itemCountAfter == _itemCountBefore,
                $"before={_itemCountBefore} after={itemCountAfter} (背包满应保持不变)");

            var hasTulip = Game1.player.Items.Any(i => i is Object obj &&
                (obj.QualifiedItemId == TulipItemId || obj.ItemId == TulipItemId));
            Assert("no_tulip_in_inventory", !hasTulip,
                hasTulip ? "tulip unexpectedly in player inventory" : "tulip not in player inventory (正确)");
        }

        return CurrentTick >= 200;
    }

    public override void Teardown()
    {
        // 清空玩家背包里测试填入的石头，避免污染后续测试
        for (var i = 0; i < Game1.player.Items.Count; i++)
        {
            var item = Game1.player.Items[i];
            if (item != null && item.ParentSheetIndex == 390)
            {
                Game1.player.Items[i] = null;
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
