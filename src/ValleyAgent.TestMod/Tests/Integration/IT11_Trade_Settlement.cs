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
///     IT11：验证步骤 2 交易链路端到端结算（NPC 是买家，玩家卖物品）。
///     链路（2026-08-15 账本迁移）：TS 账本业务校验 → execute_adjust 原子批 →
///     C# AdjustExecutor 物理校验 + 原子执行（玩家收钱扣物、NPC 付钱收物）→ adjust_result。
///     本测试驱动 C# 半环（AdjustExecutor.Execute，含幂等缓存），TS 半环由 bun 测试覆盖。
///     验证点：
///     1. 4-op 交易批成功：玩家金币 +price×qty、玩家物品 -qty、NPC 钱包 -price×qty、NPC 背包 +qty
///     2. 幂等：同 instructionId 重发 → 返回缓存回执，不重复入账
///     3. 反例：NPC 钱包不足 → 零副作用预校验拒绝（INSUFFICIENT_FUNDS），资产不动
/// </summary>
public class IT11_Trade_Settlement : IntegrationTestBase
{
    private const string WoodItemId = "(O)388";
    private const int TradeQuantity = 5;
    private const int TradePrice = 10;
    private const int TradeTotal = TradePrice * TradeQuantity;

    private bool _tradeExecuted;
    private bool _idempotentChecked;
    private bool _insufficientChecked;
    private bool _asserted;
    private AdjustExecutor? _adjustExecutor;
    private AgentInstance? _agent;
    private int _playerMoneyBefore;
    private int _npcMoneyBefore;
    private int _playerWoodBefore;
    private int _npcWoodBefore;

    public IT11_Trade_Settlement(IModHelper helper, IMonitor monitor) : base(helper, monitor)
    {
    }

    public override string TestName
    {
        get => "IT11_Trade_Settlement";
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

        // 拿 AgentInstance（钱包/背包）
        var agentService = Container.GetService<AgentService>();
        if (agentService == null || !agentService.TryGetBrain(NpcName, out var agent) || agent == null)
        {
            Skip($"agent '{NpcName}' not found in AgentService");
            return;
        }

        _agent = agent;

        // NPC 钱包：直接赋值（静默路径，测试初始化用），保证有足够余额成交
        agent.Inventory.Money = 1000;
        _npcMoneyBefore = agent.Inventory.Money;

        // 玩家背包：加 10 Wood（足够卖 5 个）
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
            $"[{TestName}] Setup complete. playerMoney={_playerMoneyBefore}, npcMoney={_npcMoneyBefore}, " +
            $"playerWood={_playerWoodBefore}, npcWood={_npcWoodBefore}",
            LogLevel.Info);
    }

    /// <summary>构造 TS trade 工具同款 4-op 交易批（玩家收钱扣物、NPC 付钱收物）。</summary>
    private static ProtocolV2.ExecuteAdjustMessage BuildTradeBatch(string instructionId)
        => new()
        {
            RequestId = $"it11-{instructionId}",
            InstructionId = instructionId,
            NpcName = "IT11_Npc",
            Ops = new List<ProtocolV2.AdjustOp>
            {
                new() { Kind = "money", Target = "player", Amount = TradeTotal, Reason = "trade" },
                new() { Kind = "item", Target = "player", ItemId = WoodItemId, Quantity = -TradeQuantity, Reason = "trade" },
                new() { Kind = "money", Target = "npc", Amount = -TradeTotal, Reason = "trade" },
                new() { Kind = "item", Target = "npc", ItemId = WoodItemId, Quantity = TradeQuantity, Reason = "trade" }
            }
        };

    public override bool Update()
    {
        if (_adjustExecutor == null || _agent == null)
        {
            return true;
        }

        // Phase 1: 交易原子批成功（模拟 TS trade 工具下发 execute_adjust）
        if (CurrentTick == 30 && !_tradeExecuted)
        {
            _tradeExecuted = true;
            try
            {
                var message = BuildTradeBatch("it11-trade-1");
                message.NpcName = NpcName;
                var result = _adjustExecutor.Execute(message);

                AssertEx("trade_batch_succeeded", result.Success,
                    "AdjustExecutor 拒绝了 TS trade 同款 4-op 批（物理校验误判 INSUFFICIENT/INVENTORY_FULL/PLAYER_NOT_FOUND 或幂等缓存误命中）",
                    result.Success ? "4-op trade batch ok" : $"failed: {result.FailureCode}");
                if (!result.Success)
                {
                    return true;
                }

                AssertEx("trade_steps_count", result.Steps.Count == 4,
                    "原子批步数不足 4（某个 op 被静默跳过或合并），回执 Steps 列表缺步",
                    $"steps={result.Steps.Count} (expected 4)");

                var playerMoney = Game1.player.Money;
                AssertEx("player_money_increased", playerMoney == _playerMoneyBefore + TradeTotal,
                    "玩家收款步未入账或金额错（money op 的 Amount 映射丢失），玩家金币不等于 before+50",
                    $"player money {_playerMoneyBefore} → {playerMoney} (expected +{TradeTotal})");

                var npcMoney = _agent.Inventory.Money;
                AssertEx("npc_money_decreased", npcMoney == _npcMoneyBefore - TradeTotal,
                    "NPC 付款步未扣款（npc money op 漏执行），NPC 钱包未减 50",
                    $"npc money {_npcMoneyBefore} → {npcMoney} (expected -{TradeTotal})");

                var playerWood = CountPlayerWood();
                AssertEx("player_item_decreased", playerWood == _playerWoodBefore - TradeQuantity,
                    "玩家扣物步未执行或扣错数量（item op 的负数量语义被当成正数），玩家 wood 未减 5",
                    $"player wood {_playerWoodBefore} → {playerWood} (expected -{TradeQuantity})");

                var npcWood = CountNpcWood(_agent);
                AssertEx("npc_item_increased", npcWood == _npcWoodBefore + TradeQuantity,
                    "NPC 收物步未执行（TryAdd 失败被吞或 item op 漏执行），NPC 背包 wood 未增 5",
                    $"npc wood {_npcWoodBefore} → {npcWood} (expected +{TradeQuantity})");

                // 回执携带双方新余额
                AssertEx("receipt_npc_money", result.NpcMoney == _npcMoneyBefore - TradeTotal,
                    "回执快照与实际钱包脱钩（NpcMoney 未从执行后状态回填），adjust_result 的新余额字段错误",
                    $"receipt npcMoney={result.NpcMoney} (expected {_npcMoneyBefore - TradeTotal})");
            }
            catch (Exception ex)
            {
                Assert("trade_no_throw", false, $"Execute threw: {ex.Message}");
            }
        }

        // Phase 2: 幂等——同 instructionId 重发返回缓存回执，不重复入账
        if (CurrentTick == 90 && !_idempotentChecked)
        {
            _idempotentChecked = true;
            try
            {
                var message = BuildTradeBatch("it11-trade-1");
                message.NpcName = NpcName;
                var replay = _adjustExecutor.Execute(message);

                AssertEx("idempotent_replay_cached", replay.Success && replay.InstructionId == "it11-trade-1",
                    "幂等缓存未命中：同 instructionId 重发被当新指令执行失败或 InstructionId 回填丢失（指令去重表断链）",
                    replay.Success ? "replay returned cached receipt" : $"replay failed: {replay.FailureCode}");

                var playerWood = CountPlayerWood();
                AssertEx("idempotent_no_double_execution", playerWood == _playerWoodBefore - TradeQuantity,
                    "重发导致二次入账：玩家 wood 比 Phase 1 结束时又减 5，幂等去重失效",
                    $"player wood after replay = {playerWood} (expected {_playerWoodBefore - TradeQuantity}, 未重复入账)");

                var npcMoney = _agent.Inventory.Money;
                AssertEx("idempotent_npc_money_stable", npcMoney == _npcMoneyBefore - TradeTotal,
                    "重发再次扣了 NPC 钱包（money op 重复执行），NPC 余额比 Phase 1 结束时又少 50",
                    $"npc money after replay = {npcMoney} (expected {_npcMoneyBefore - TradeTotal})");
            }
            catch (Exception ex)
            {
                Assert("idempotent_no_throw", false, $"replay threw: {ex.Message}");
            }
        }

        // Phase 3: 反例——NPC 钱包不足时预校验拒绝（资产不动，零副作用）
        if (CurrentTick == 150 && !_insufficientChecked)
        {
            _insufficientChecked = true;
            try
            {
                _agent.Inventory.Money = 10; // 不够 50

                var message = BuildTradeBatch("it11-trade-2");
                message.NpcName = NpcName;
                var result = _adjustExecutor.Execute(message);

                AssertEx("insufficient_rejected",
                    !result.Success && result.FailureCode == ProtocolV2.AdjustFailureCode.InsufficientFunds,
                    "预校验漏判 NPC 余额不足：钱包只有 10 却放行了 50 的付款批（INSUFFICIENT_FUNDS 分支未触发）",
                    result.Success ? "settled despite insufficient wallet" : $"rejected: {result.FailureCode}");

                var npcMoney = _agent.Inventory.Money;
                AssertEx("insufficient_npc_money_untouched", npcMoney == 10,
                    "拒绝批次仍动了 NPC 钱包（校验与提交顺序错位），余额偏离 Setup 设置的 10",
                    $"npc money after failed batch = {npcMoney} (expected 10, 零副作用)");

                var playerWood = CountPlayerWood();
                AssertEx("insufficient_player_item_untouched", playerWood == _playerWoodBefore - TradeQuantity,
                    "拒绝批次扣了玩家 wood（零副作用承诺被破坏）",
                    $"player wood after failed batch = {playerWood} (expected {_playerWoodBefore - TradeQuantity}, 未扣)");

                var playerMoney = Game1.player.Money;
                AssertEx("insufficient_player_money_untouched", playerMoney == _playerMoneyBefore + TradeTotal,
                    "拒绝批次给玩家入了账（收款 op 在预校验失败后仍执行），玩家金币多出 50",
                    $"player money after failed batch = {playerMoney} (expected {_playerMoneyBefore + TradeTotal}, 未入账)");
            }
            catch (Exception ex)
            {
                Assert("insufficient_no_throw", false, $"Execute threw: {ex.Message}");
            }
        }

        if (CurrentTick == 210 && !_asserted)
        {
            _asserted = true;
            var state = Api!.GetAgentState(NpcName);
            AssertEx("state_still_readable", !string.IsNullOrEmpty(state),
                "交易批执行后 AgentBrain 被拆除或名字路由失效，GetAgentState 返回空串");
        }

        return CurrentTick >= 240;
    }

    public override void Teardown()
    {
        CommonTeardown();
        Monitor.Log($"[{TestName}] Teardown.", LogLevel.Info);
    }

    private static int CountPlayerWood()
    {
        var total = 0;
        foreach (var item in Game1.player.Items)
        {
            if (item is Object obj &&
                (obj.QualifiedItemId == WoodItemId || obj.ItemId == WoodItemId))
            {
                total += obj.Stack;
            }
        }

        return total;
    }

    private static int CountNpcWood(AgentInstance agent)
    {
        var total = 0;
        foreach (var item in agent.Inventory.GetAllItems())
        {
            if (item is Object obj &&
                (obj.QualifiedItemId == WoodItemId || obj.ItemId == WoodItemId))
            {
                total += obj.Stack;
            }
        }

        return total;
    }
}
