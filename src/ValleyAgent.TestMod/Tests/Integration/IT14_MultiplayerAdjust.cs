#nullable enable
using System;
using System.Collections.Generic;
using StardewModdingAPI;
using StardewValley;
using ValleyAgent.Economy;
using ValleyAgent.Protocol;
using ValleyAgent.Services;
using Object = StardewValley.Object;

namespace ValleyAgent.TestMod.Tests.Integration;

/// <summary>
///     IT14：验证 execute_adjust 按 playerId 解析目标 Farmer（2026-08-16 联机适配）。
///     单机环境无法伪造 FarmHand（otherFarmers 是 net 同步字典），三分支覆盖全部解析语义：
///     1. playerId = 本机玩家 ID → 命中 MasterPlayer 分支，交易成功且落在本机玩家身上
///     2. playerId = 不存在的 ID → PlayerNotFound 整批拒绝，双方资产零变动
///     3. playerId 缺省（null）→ 回落 Game1.player（单机/旧客户端向后兼容），交易成功
///     真·多 Farmer 语义由 E2E harness C4（双实例真联机）覆盖。
/// </summary>
public class IT14_MultiplayerAdjust : IntegrationTestBase
{
    private const string WoodItemId = "(O)388";
    private const int TradeQuantity = 2;
    private const int TradePrice = 5;
    private const int TradeTotal = TradePrice * TradeQuantity;

    private bool _phase1Checked;
    private bool _phase2Checked;
    private bool _phase3Checked;
    private AdjustExecutor? _adjustExecutor;
    private AgentInstance? _agent;
    private int _playerMoneyBefore;
    private int _npcMoneyBefore;
    private int _playerWoodBefore;
    private int _npcWoodBefore;

    public IT14_MultiplayerAdjust(IModHelper helper, IMonitor monitor) : base(helper, monitor)
    {
    }

    public override string TestName => "IT14_MultiplayerAdjust";

    public override int TimeoutTicks => 400;

    public override TestGroup Group => TestGroup.Integration;

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

        // NPC 钱包：直接赋值保证足够余额成交
        agent.Inventory.Money = 1000;
        _npcMoneyBefore = agent.Inventory.Money;

        // 玩家背包：加 10 Wood（足够卖 2 个 × 2 次交易）
        var wood = ItemRegistry.Create(WoodItemId, 10, allowNull: true);
        if (wood == null)
        {
            Skip($"failed to create {WoodItemId}");
            return;
        }

        var added = Game1.player.addItemToInventoryBool(wood);
        if (!added)
        {
            Skip("player inventory full — cannot place test item");
            return;
        }

        _playerMoneyBefore = Game1.player.Money;
        _playerWoodBefore = CountPlayerWood();
        _npcWoodBefore = CountNpcWood(agent);

        Monitor.Log(
            $"[{TestName}] Setup complete. playerId={Game1.player.UniqueMultiplayerID}, " +
            $"playerMoney={_playerMoneyBefore}, npcMoney={_npcMoneyBefore}, playerWood={_playerWoodBefore}, npcWood={_npcWoodBefore}",
            LogLevel.Info);
    }

    /// <summary>构造 TS trade 工具同款 4-op 交易批（可指定 playerId；null 模拟旧客户端缺省）。</summary>
    private static ProtocolV2.ExecuteAdjustMessage BuildTradeBatch(string instructionId, string? playerId)
        => new()
        {
            RequestId = $"it14-{instructionId}",
            InstructionId = instructionId,
            NpcName = "IT14_Npc",
            PlayerId = playerId,
            Ops = new List<ProtocolV2.AdjustOp>
            {
                new() { Kind = "money", Target = "player", Amount = TradeTotal, Reason = "trade" },
                new() { Kind = "item", Target = "player", ItemId = WoodItemId, Quantity = -TradeQuantity, Reason = "trade" },
                new() { Kind = "money", Target = "npc", Amount = -TradeTotal, Reason = "trade" },
                new() { Kind = "item", Target = "npc", ItemId = WoodItemId, Quantity = TradeQuantity, Reason = "trade" }
            }
        };

    /// <summary>断言一次成功交易的双方资产变化（自 _playerMoneyBefore/_npcMoneyBefore 基线累计）。</summary>
    private bool AssertTradeApplied(ProtocolV2.AdjustResultMessage result, string prefix,
        int expectedPlayerMoney, int expectedNpcMoney, int expectedPlayerWood, int expectedNpcWood)
    {
        if (!result.Success)
        {
            Assert($"{prefix}_succeeded", false, $"failed: {result.FailureCode}");
            return false;
        }

        Assert($"{prefix}_player_money", Game1.player.Money == expectedPlayerMoney,
            $"player money = {Game1.player.Money} (expected {expectedPlayerMoney})");
        Assert($"{prefix}_npc_money", _agent!.Inventory.Money == expectedNpcMoney,
            $"npc money = {_agent.Inventory.Money} (expected {expectedNpcMoney})");
        Assert($"{prefix}_player_wood", CountPlayerWood() == expectedPlayerWood,
            $"player wood = {CountPlayerWood()} (expected {expectedPlayerWood})");
        Assert($"{prefix}_npc_wood", CountNpcWood(_agent) == expectedNpcWood,
            $"npc wood = {CountNpcWood(_agent)} (expected {expectedNpcWood})");
        return true;
    }

    public override bool Update()
    {
        if (_adjustExecutor == null || _agent == null)
        {
            return true;
        }

        // Phase 1: playerId = 本机玩家 ID → 命中 MasterPlayer，交易成功
        if (CurrentTick == 30 && !_phase1Checked)
        {
            _phase1Checked = true;
            try
            {
                var message = BuildTradeBatch("it14-own-id", Game1.player.UniqueMultiplayerID.ToString());
                message.NpcName = NpcName;
                var result = _adjustExecutor.Execute(message);

                Assert("own_id_resolved", result.Success, result.Success ? "ok" : $"failed: {result.FailureCode}");
                if (result.Success)
                {
                    Assert("own_id_receipt_player_money", result.PlayerMoney == _playerMoneyBefore + TradeTotal,
                        $"receipt playerMoney={result.PlayerMoney} (expected {_playerMoneyBefore + TradeTotal})");
                    AssertTradeApplied(result, "own_id",
                        _playerMoneyBefore + TradeTotal, _npcMoneyBefore - TradeTotal,
                        _playerWoodBefore - TradeQuantity, _npcWoodBefore + TradeQuantity);
                }
            }
            catch (Exception ex)
            {
                Assert("own_id_no_throw", false, $"Execute threw: {ex.Message}");
            }
        }

        // Phase 2: playerId = 不存在的玩家 → PlayerNotFound 整批拒绝，双方资产零变动
        if (CurrentTick == 90 && !_phase2Checked)
        {
            _phase2Checked = true;
            try
            {
                var message = BuildTradeBatch("it14-ghost-id", "999999999999");
                message.NpcName = NpcName;
                var result = _adjustExecutor.Execute(message);

                Assert("ghost_id_rejected",
                    !result.Success && result.FailureCode == ProtocolV2.AdjustFailureCode.PlayerNotFound,
                    result.Success ? "settled despite unknown player" : $"rejected: {result.FailureCode}");

                Assert("ghost_id_player_money_untouched", Game1.player.Money == _playerMoneyBefore + TradeTotal,
                    $"player money = {Game1.player.Money} (expected {_playerMoneyBefore + TradeTotal}, 未变动)");
                Assert("ghost_id_npc_money_untouched", _agent.Inventory.Money == _npcMoneyBefore - TradeTotal,
                    $"npc money = {_agent.Inventory.Money} (expected {_npcMoneyBefore - TradeTotal}, 未变动)");
                Assert("ghost_id_player_wood_untouched", CountPlayerWood() == _playerWoodBefore - TradeQuantity,
                    $"player wood = {CountPlayerWood()} (expected {_playerWoodBefore - TradeQuantity}, 未扣)");
                Assert("ghost_id_npc_wood_untouched", CountNpcWood(_agent) == _npcWoodBefore + TradeQuantity,
                    $"npc wood = {CountNpcWood(_agent)} (expected {_npcWoodBefore + TradeQuantity}, 未加)");
            }
            catch (Exception ex)
            {
                Assert("ghost_id_no_throw", false, $"Execute threw: {ex.Message}");
            }
        }

        // Phase 3: playerId 缺省（null）→ 回落 Game1.player（旧客户端向后兼容），交易成功
        if (CurrentTick == 150 && !_phase3Checked)
        {
            _phase3Checked = true;
            try
            {
                var message = BuildTradeBatch("it14-default", null);
                message.NpcName = NpcName;
                var result = _adjustExecutor.Execute(message);

                Assert("default_resolved", result.Success, result.Success ? "ok" : $"failed: {result.FailureCode}");
                if (result.Success)
                {
                    AssertTradeApplied(result, "default",
                        _playerMoneyBefore + TradeTotal * 2, _npcMoneyBefore - TradeTotal * 2,
                        _playerWoodBefore - TradeQuantity * 2, _npcWoodBefore + TradeQuantity * 2);
                }
            }
            catch (Exception ex)
            {
                Assert("default_no_throw", false, $"Execute threw: {ex.Message}");
            }
        }

        return CurrentTick >= 200;
    }

    public override void Teardown()
    {
        try
        {
            // 测试卫生（审计 P2）：恢复玩家/NPC 资产基线，避免污染后续测试。
            if (_agent != null)
            {
                // NPC 钱包恢复 + 移除交易所得 Wood
                _agent.Inventory.Money = _npcMoneyBefore;
                var npcWoodToRemove = CountNpcWood(_agent) - _npcWoodBefore;
                if (npcWoodToRemove > 0)
                {
                    _ = _agent.Inventory.TryRemove(WoodItemId, npcWoodToRemove, out _);
                }
            }

            // 玩家：钱包恢复 + 移除 Setup 加的 Wood 中未消耗部分
            var playerMoneyDelta = Game1.player.Money - _playerMoneyBefore;
            if (playerMoneyDelta != 0)
            {
                Game1.player.Money -= playerMoneyDelta;
            }

            var playerWoodToRemove = CountPlayerWood() - _playerWoodBefore;
            if (playerWoodToRemove > 0)
            {
                RemovePlayerWood(playerWoodToRemove);
            }
        }
        catch (Exception ex)
        {
            Monitor.Log($"[{TestName}] Teardown restore failed: {ex.Message}", LogLevel.Warn);
        }

        CommonTeardown();
        Monitor.Log($"[{TestName}] Teardown.", LogLevel.Info);
    }

    private static int CountPlayerWood()
    {
        var count = 0;
        foreach (var item in Game1.player.Items)
        {
            if (item is Object obj && (obj.QualifiedItemId == WoodItemId || obj.ItemId == WoodItemId))
            {
                count += obj.Stack;
            }
        }

        return count;
    }

    private static void RemovePlayerWood(int quantity)
    {
        var remaining = quantity;
        var items = Game1.player.Items;
        for (var i = 0; i < items.Count && remaining > 0; i++)
        {
            if (items[i] is not Object obj)
            {
                continue;
            }

            var objId = obj.QualifiedItemId ?? obj.ItemId ?? "";
            if (!objId.Equals(WoodItemId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var take = Math.Min(remaining, obj.Stack);
            remaining -= take;
            obj.Stack -= take;
            if (obj.Stack <= 0)
            {
                items[i] = null;
            }
        }
    }

    private static int CountNpcWood(AgentInstance agent)
    {
        var count = 0;
        foreach (var slot in agent.Inventory.GetAllItems())
        {
            if (slot is Object obj && (obj.QualifiedItemId == WoodItemId || obj.ItemId == WoodItemId))
            {
                count += obj.Stack;
            }
        }

        return count;
    }
}
