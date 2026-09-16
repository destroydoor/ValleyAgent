using StardewModdingAPI;
using StardewValley;
using ValleyAgent.Agents;
using ValleyAgent.Config;
using ValleyAgent.Economy;
using ValleyAgent.Performance;
using ValleyAgent.Protocol;
using ValleyAgent.Resilience;
using ValleyAgent.Services;
using ValleyAgent.WebSocket;
using Xunit;
using AdjustFailureCode = ValleyAgent.Protocol.ProtocolV2.AdjustFailureCode;
using AdjustOp = ValleyAgent.Protocol.ProtocolV2.AdjustOp;
using AdjustResultMessage = ValleyAgent.Protocol.ProtocolV2.AdjustResultMessage;
using AdjustStepResult = ValleyAgent.Protocol.ProtocolV2.AdjustStepResult;
using ExecuteAdjustMessage = ValleyAgent.Protocol.ProtocolV2.ExecuteAdjustMessage;
using SObject = StardewValley.Object;

namespace ValleyAgent.UnitTests;

/// <summary>
///     <see cref="AdjustExecutor" /> 单元测试（2026-08-15 账本迁移设计 §4.1/§6，步骤 1）。
///     验证：原子批（预校验零副作用 + 提交失败回滚已提交项）、instructionId 幂等防重放、
///     指令结果环形日志淘汰、各失败码（INVALID_OP/AGENT_MISSING/INSUFFICIENT_FUNDS/ITEM_NOT_FOUND/
///     INVENTORY_FULL/INTERNAL_ERROR）、adjust_result 序列化契约（camelCase 失败码）。
///     约定：单测环境游戏数据未加载（ItemRegistry.Create 返回 null）——真实物品路径用
///     <see cref="ItemTestExecutor" /> 注入合成物品；玩家侧（Game1.player）物理校验需游戏运行验证，
///     单测只覆盖玩家不可用（InternalError）路径。
/// </summary>
public class AdjustExecutorTests
{
    // ── 辅助 ─────────────────────────────────────────

    private static AdjustExecutor CreateExecutor(AgentService? service, int capacity = 256)
        => new(new StubMonitor(), service, null, capacity);

    private static AgentService CreateAgentService()
        => new(
            new ModConfig(),
            new AgentAllocationManager(),
            new TokenBudgetManager(1000),
            new PerformanceMonitor(),
            new CacheManager(),
            new CircuitBreaker());

    private static ExecuteAdjustMessage Msg(string instructionId, string npcName, params AdjustOp[] ops)
        => new()
        {
            RequestId = Guid.NewGuid().ToString("N"),
            InstructionId = instructionId,
            NpcName = npcName,
            Ops = ops.ToList()
        };

    private static ExecuteAdjustMessage Msg(string instructionId, string npcName, string? playerId, params AdjustOp[] ops)
        => new()
        {
            RequestId = Guid.NewGuid().ToString("N"),
            InstructionId = instructionId,
            NpcName = npcName,
            PlayerId = playerId,
            Ops = ops.ToList()
        };

    private static AdjustOp Money(string target, int amount)
        => new() { Kind = "money", Target = target, Amount = amount, Reason = "test" };

    private static AdjustOp Item(string target, string itemId, int quantity)
        => new() { Kind = "item", Target = target, ItemId = itemId, Quantity = quantity };

    // ── 参数校验（INVALID_OP，不触碰游戏状态）──────────

    [Fact]
    public void Execute_MissingInstructionId_ReturnsInvalidOpAndNotCached()
    {
        using var service = CreateAgentService();
        var executor = CreateExecutor(service);

        var result = executor.Execute(Msg("", "Abigail", Money("npc", 100)));

        Assert.False(result.Success);
        Assert.Equal(AdjustFailureCode.InvalidOp, result.FailureCode);
        Assert.Null(executor.FindCachedResult(""));
    }

    [Fact]
    public void Execute_EmptyOps_ReturnsInvalidOp()
    {
        using var service = CreateAgentService();
        var executor = CreateExecutor(service);

        var result = executor.Execute(Msg("e1", "Abigail"));

        Assert.False(result.Success);
        Assert.Equal(AdjustFailureCode.InvalidOp, result.FailureCode);
    }

    [Fact]
    public void Execute_InvalidKind_ReturnsInvalidOp()
    {
        using var service = CreateAgentService();
        var executor = CreateExecutor(service);

        var op = new AdjustOp { Kind = "gold", Target = "npc", Amount = 100 };
        var result = executor.Execute(Msg("k1", "Abigail", op));

        Assert.False(result.Success);
        Assert.Equal(AdjustFailureCode.InvalidOp, result.FailureCode);
        Assert.Equal("invalid kind 'gold' (expected money|item)", result.Steps[0].Detail);
    }

    [Fact]
    public void Execute_InvalidTarget_ReturnsInvalidOp()
    {
        using var service = CreateAgentService();
        var executor = CreateExecutor(service);

        var result = executor.Execute(Msg("t1", "Abigail", Money("shop", 100)));

        Assert.False(result.Success);
        Assert.Equal(AdjustFailureCode.InvalidOp, result.FailureCode);
    }

    [Fact]
    public void Execute_ZeroAmount_ReturnsInvalidOp()
    {
        using var service = CreateAgentService();
        var executor = CreateExecutor(service);

        var result = executor.Execute(Msg("a1", "Abigail", Money("npc", 0)));

        Assert.False(result.Success);
        Assert.Equal(AdjustFailureCode.InvalidOp, result.FailureCode);
    }

    [Fact]
    public void Execute_ZeroQuantity_ReturnsInvalidOp()
    {
        using var service = CreateAgentService();
        var executor = CreateExecutor(service);

        var result = executor.Execute(Msg("q1", "Abigail", Item("npc", "(O)388", 0)));

        Assert.False(result.Success);
        Assert.Equal(AdjustFailureCode.InvalidOp, result.FailureCode);
    }

    // ── AGENT_MISSING ────────────────────────────────

    [Fact]
    public void Execute_NullAgentService_NpcTarget_ReturnsAgentMissing()
    {
        var executor = CreateExecutor(null); // AgentService 为 null：npc 目标必须先失败（AgentMissing）

        var result = executor.Execute(Msg("m1", "Abigail", Money("npc", 100)));

        Assert.False(result.Success);
        Assert.Equal(AdjustFailureCode.AgentMissing, result.FailureCode);
    }

    [Fact]
    public void Execute_UnknownNpc_ReturnsAgentMissing()
    {
        using var service = CreateAgentService(); // 未 CreateAgent("Abigail")
        var executor = CreateExecutor(service);

        var result = executor.Execute(Msg("m2", "Abigail", Money("npc", 100)));

        Assert.False(result.Success);
        Assert.Equal(AdjustFailureCode.AgentMissing, result.FailureCode);
    }

    // ── NPC 钱包操作 ──────────────────────────────────

    [Fact]
    public void Execute_NpcMoneyAdd_Success_ReturnsNewBalance()
    {
        using var service = CreateAgentService();
        var agent = service.EnsureBrain("Abigail")!;
        agent.Inventory.Money = 100; // 静默路径回填（单测无钱包事件订阅者）
        var executor = CreateExecutor(service);

        var result = executor.Execute(Msg("n1", "Abigail", Money("npc", 50)));

        Assert.True(result.Success);
        Assert.Null(result.FailureCode);
        Assert.Equal(150, agent.Inventory.Money);
        Assert.Equal(150, result.NpcMoney);
        Assert.Single(result.Steps);
        Assert.True(result.Steps[0].Success);
    }

    [Fact]
    public void Execute_NpcMoneySpend_Success()
    {
        using var service = CreateAgentService();
        var agent = service.EnsureBrain("Abigail")!;
        agent.Inventory.Money = 100;
        var executor = CreateExecutor(service);

        var result = executor.Execute(Msg("n2", "Abigail", Money("npc", -40)));

        Assert.True(result.Success);
        Assert.Equal(60, agent.Inventory.Money);
        Assert.Equal(60, result.NpcMoney);
    }

    [Fact]
    public void Execute_NpcMoneySpend_InsufficientFunds_NoSideEffect()
    {
        using var service = CreateAgentService();
        var agent = service.EnsureBrain("Abigail")!;
        agent.Inventory.Money = 100;
        var executor = CreateExecutor(service);

        var result = executor.Execute(Msg("n3", "Abigail", Money("npc", -200)));

        Assert.False(result.Success);
        Assert.Equal(AdjustFailureCode.InsufficientFunds, result.FailureCode);
        Assert.Equal(100, agent.Inventory.Money); // 余额未动（零副作用预校验）
    }

    // ── 原子批 ────────────────────────────────────────

    [Fact]
    public void Execute_Batch_PreValidationFailure_CommitsNothing()
    {
        using var service = CreateAgentService();
        var agent = service.EnsureBrain("Abigail")!;
        agent.Inventory.Money = 100;
        var executor = CreateExecutor(service);

        // 第二步预校验失败（余额不足）→ 第一步虽可通过校验，但整批未提交任何变更。
        var result = executor.Execute(Msg("b1", "Abigail",
            Money("npc", -60),
            Money("npc", -99999)));

        Assert.False(result.Success);
        Assert.Equal(AdjustFailureCode.InsufficientFunds, result.FailureCode);
        Assert.Equal(100, agent.Inventory.Money); // 零副作用
        Assert.Single(result.Steps); // 只列出失败步（未执行的步骤不出现）
    }

    [Fact]
    public void Execute_Batch_MixedSuccess_AppliesAllInOrder()
    {
        using var service = CreateAgentService();
        var agent = service.EnsureBrain("Abigail")!;
        agent.Inventory.Money = 500;
        var executor = CreateExecutor(service);

        var result = executor.Execute(Msg("b2", "Abigail",
            Money("npc", -100),
            Money("npc", 250)));

        Assert.True(result.Success);
        Assert.Equal(650, agent.Inventory.Money);
        Assert.Equal(2, result.Steps.Count);
        Assert.All(result.Steps, s => Assert.True(s.Success));
    }

    [Fact]
    public void Execute_Batch_CommitFailure_RollsBackCommittedSteps()
    {
        using var service = CreateAgentService();
        var agent = service.EnsureBrain("Abigail")!;
        agent.Inventory.Money = 100;
        // 塞满 12 槽（合成物品），第 13 个不同物品 TryAdd 必然失败——物品加入无零副作用预校验，
        // 只能在提交期暴露 INVENTORY_FULL，触发回滚已提交的钱包步。
        for (var i = 0; i < 12; i++)
        {
            Assert.True(agent.Inventory.TryAdd(MakeFakeItem($"fill{i}")));
        }

        var executor = new ItemTestExecutor(service);

        var result = executor.Execute(Msg("b3", "Abigail",
            Money("npc", -60), // 提交成功
            Item("npc", "overflow", 1))); // 提交失败（背包满）→ 回滚第一步

        Assert.False(result.Success);
        Assert.Equal(AdjustFailureCode.InventoryFull, result.FailureCode);
        Assert.Equal(100, agent.Inventory.Money); // 已提交的钱包扣款被回滚
        Assert.True(result.Steps[0].Success); // 步骤明细保留：第 0 步曾成功（后回滚）
        Assert.False(result.Steps[1].Success);
    }

    // ── NPC 物品操作（合成物品注入）────────────────────

    [Fact]
    public void Execute_NpcItemAdd_Success()
    {
        using var service = CreateAgentService();
        var agent = service.EnsureBrain("Abigail")!;
        var executor = new ItemTestExecutor(service);

        var result = executor.Execute(Msg("i1", "Abigail", Item("npc", "strawberry", 5)));

        Assert.True(result.Success);
        var obj = Assert.Single(NonNull(agent.Inventory.GetAllItems())) as SObject;
        Assert.Equal(5, obj!.Stack);
        Assert.Equal("(O)strawberry", obj.QualifiedItemId);
    }

    [Fact]
    public void Execute_NpcItemRemove_Success()
    {
        using var service = CreateAgentService();
        var agent = service.EnsureBrain("Abigail")!;
        var executor = new ItemTestExecutor(service);
        Assert.True(agent.Inventory.TryAdd(MakeFakeItem("strawberry", 5)));

        var result = executor.Execute(Msg("i2", "Abigail", Item("npc", "strawberry", -2)));

        Assert.True(result.Success);
        var obj = Assert.Single(NonNull(agent.Inventory.GetAllItems())) as SObject;
        Assert.Equal(3, obj!.Stack);
    }

    [Fact]
    public void Execute_NpcItemRemove_InsufficientQuantity_ItemNotFound_NoSideEffect()
    {
        using var service = CreateAgentService();
        var agent = service.EnsureBrain("Abigail")!;
        var executor = new ItemTestExecutor(service);
        Assert.True(agent.Inventory.TryAdd(MakeFakeItem("strawberry", 1)));

        var result = executor.Execute(Msg("i3", "Abigail", Item("npc", "strawberry", -3)));

        Assert.False(result.Success);
        Assert.Equal(AdjustFailureCode.ItemNotFound, result.FailureCode);
        var obj = Assert.Single(NonNull(agent.Inventory.GetAllItems())) as SObject;
        Assert.Equal(1, obj!.Stack); // 未扣除（零副作用预校验）
    }

    [Fact]
    public void Execute_UnknownItemId_ReturnsItemNotFound()
    {
        using var service = CreateAgentService();
        service.EnsureBrain("Abigail");
        var executor = CreateExecutor(service); // 真实解析路径：游戏数据未加载 → 恒 ItemNotFound

        var result = executor.Execute(Msg("i4", "Abigail", Item("npc", "(O)388", 1)));

        Assert.False(result.Success);
        Assert.Equal(AdjustFailureCode.ItemNotFound, result.FailureCode);
    }

    // ── 玩家侧（单测环境无 Game1.player）───────────────

    [Fact]
    public void Execute_PlayerTarget_NoPlayer_ReturnsInternalError()
    {
        using var service = CreateAgentService();
        var executor = CreateExecutor(service);

        var result = executor.Execute(Msg("p1", "Abigail", Money("player", 100)));

        // Game1.player 在单测环境为 null → 物理校验拒绝执行（玩家余额/背包校验需游戏运行验证）。
        Assert.False(result.Success);
        Assert.Equal(AdjustFailureCode.InternalError, result.FailureCode);
    }

    [Fact]
    public void Execute_PlayerTarget_UnknownPlayerId_ReturnsPlayerNotFound()
    {
        using var service = CreateAgentService();
        var executor = CreateExecutor(service);

        // 指定了 playerId 但解析不到对应 Farmer（2026-08-16 联机：玩家已退出/不存在）→
        // PlayerNotFound 整批拒绝（零副作用）。单测环境 Game1.GetPlayer 若抛异常，
        // 该分支回退由 TestMod IT14 phase 2（ghost-id）游戏内覆盖。
        var result = executor.Execute(Msg("p2", "Abigail", "999999999999", Money("player", 100)));

        Assert.False(result.Success);
        Assert.Equal(AdjustFailureCode.PlayerNotFound, result.FailureCode);
    }

    // ── 幂等 ──────────────────────────────────────────

    [Fact]
    public void Execute_Idempotent_SameInstructionReplaysCachedResult()
    {
        using var service = CreateAgentService();
        var agent = service.EnsureBrain("Abigail")!;
        agent.Inventory.Money = 100;
        var executor = CreateExecutor(service);

        var first = executor.Execute(Msg("dup", "Abigail", Money("npc", 100)));
        var second = executor.Execute(Msg("dup", "Abigail", Money("npc", 100)));

        Assert.True(first.Success);
        Assert.Same(first, second); // 缓存回执原样返回
        Assert.Equal(200, agent.Inventory.Money); // 只执行一次，未重复入账
    }

    [Fact]
    public void Execute_RingEviction_EvictsOldestBeyondCapacity()
    {
        using var service = CreateAgentService();
        var executor = CreateExecutor(service, capacity: 2);

        executor.Execute(Msg("r1", "Abigail", Money("npc", 10)));
        executor.Execute(Msg("r2", "Abigail", Money("npc", 10)));
        executor.Execute(Msg("r3", "Abigail", Money("npc", 10)));

        Assert.Null(executor.FindCachedResult("r1")); // 最旧被淘汰
        Assert.NotNull(executor.FindCachedResult("r2"));
        Assert.NotNull(executor.FindCachedResult("r3"));
    }

    // ── ExecuteCore 异常守卫（issue #27 ①：异常必回执 + 幂等缓存按阶段分流）──

    [Fact]
    public void Execute_PreCommitException_ReturnsInternalErrorReceiptAndCaches()
    {
        using var service = CreateAgentService();
        service.EnsureBrain("Abigail");
        var executor = new ThrowingResolveExecutor(service);

        var result = executor.Execute(Msg("ex1", "Abigail", Item("npc", "strawberry", 1)));

        // 异常必回执：internalError + 异常全文（类型 + 消息；{ex} 全文含堆栈）。
        Assert.False(result.Success);
        Assert.Equal(AdjustFailureCode.InternalError, result.FailureCode);
        Assert.Contains("InvalidOperationException", result.Steps[0].Detail);
        Assert.Contains("resolve exploded: strawberry", result.Steps[0].Detail);
        Assert.Contains("during pre-commit phase", result.Steps[0].Detail);

        // pre-commit（零副作用）异常 → 幂等缓存写入：同 instructionId 重发被缓存挡掉，
        // 不重复执行 ExecuteCore（TS reconcile 重发即闭环）。
        Assert.NotNull(executor.FindCachedResult("ex1"));
        Assert.Same(result, executor.Execute(Msg("ex1", "Abigail", Item("npc", "strawberry", 1))));
        Assert.Equal(1, executor.CallCount);
    }

    [Fact]
    public void Execute_CommitPhaseException_ReturnsInternalErrorReceiptWithoutCaching()
    {
        using var service = CreateAgentService();
        var agent = service.EnsureBrain("Abigail")!;
        agent.Inventory.Money = 500;
        var executor = new ExplodingCommitExecutor(service);

        var result = executor.Execute(Msg("ex2", "Abigail", Money("npc", -60), Money("npc", -40)));

        // 异常必回执：internalError + 异常全文 + "可能部分已应用"声明。
        Assert.False(result.Success);
        Assert.Equal(AdjustFailureCode.InternalError, result.FailureCode);
        Assert.Contains("during commit phase", result.Steps[0].Detail);
        Assert.Contains("possible partial application", result.Steps[0].Detail);
        Assert.Contains("InvalidOperationException", result.Steps[0].Detail);
        Assert.Contains("commit exploded", result.Steps[0].Detail);

        // 提交期异常不写幂等缓存（issue #27 决策）：缓存会把"可能部分已应用"钉死为
        // "失败已处理"，阻断 TS reconcile 凭 instructionId 的补偿重发。
        Assert.Null(executor.FindCachedResult("ex2"));

        // 回滚已尽力：首步 -60 已提交后被回滚，异常步未提交 → 余额回到 500。
        Assert.Equal(500, agent.Inventory.Money);

        // 重发不被缓存挡掉：再次执行重新走 ExecuteCore（TryCommit 第 3、4 次调用，第 4 次再次抛）。
        executor.Execute(Msg("ex2", "Abigail", Money("npc", -60), Money("npc", -40)));
        Assert.Equal(4, executor.CallCount);
    }

    // ── 序列化契约（camelCase wire 对齐，types.ts:131-133）─

    [Fact]
    public void Serialize_AdjustResult_EmitsCamelCaseFailureCode()
    {
        var result = new AdjustResultMessage
        {
            RequestId = "req-1",
            InstructionId = "i1",
            NpcName = "Abigail",
            Success = false,
            FailureCode = AdjustFailureCode.InsufficientFunds,
            Steps = new List<AdjustStepResult>
            {
                new()
                {
                    Index = 0, Kind = "money", Target = "npc", Success = false,
                    FailureCode = AdjustFailureCode.InsufficientFunds, Detail = "insufficient funds"
                }
            }
        };

        var json = MessageProtocol.Serialize(result);

        Assert.Contains("\"type\":\"adjust_result\"", json);
        Assert.Contains("\"instructionId\":\"i1\"", json);
        Assert.Contains("\"failureCode\":\"insufficientFunds\"", json);
    }

    // ── 测试物品工厂 ──────────────────────────────────

    /// <summary>GetAllItems 返回 12 槽位数组（含 null），过滤出非空项供断言。</summary>
    private static IEnumerable<Item> NonNull(Item?[] slots) => slots.Where(i => i != null)!;

    /// <summary>
    ///     合成物品：参数化 ctor 依赖游戏数据（ItemRegistry.RequireTypeDefinition 会抛
    ///     KeyNotFoundException），故走参数化 ctor 构造并覆写 getOne()（AgentInventory.TryRemove
    ///     会调用 getOne() 产出 removed 实例，基类实现会重新走 Object ctor 爆炸）。
    /// </summary>
    private sealed class FakeObject : SObject
    {
        public FakeObject(string itemId, int stack)
        {
            ItemId = itemId;
            Stack = stack;
        }

        protected override Item GetOneNew() => new FakeObject(ItemId, Stack);
    }

    private static FakeObject MakeFakeItem(string itemId, int stack = 1) => new(itemId, stack);

    /// <summary>
    ///     覆写 <see cref="AdjustExecutor.ResolveItem" /> 注入合成物品的执行器
    ///     （单测环境 ItemRegistry.Create 恒返回 null，无法走真实物品路径）。
    /// </summary>
    private sealed class ItemTestExecutor : AdjustExecutor
    {
        public ItemTestExecutor(AgentService? service)
            : base(new StubMonitor(), service, null)
        {
        }

        internal override Item? ResolveItem(string itemId, out string suggestionsText)
        {
            suggestionsText = "";
            return new FakeObject(itemId, 1); // Stack 由提交阶段按 quantity 覆写
        }
    }

    /// <summary>
    ///     ResolveItem 恒抛异常的执行器（issue #27 ①）：注入 pre-commit 阶段异常
    ///     （阶段三零副作用预校验中物品解析爆炸），验证守卫回执 + 幂等缓存写入。
    /// </summary>
    private sealed class ThrowingResolveExecutor : AdjustExecutor
    {
        public ThrowingResolveExecutor(AgentService? service)
            : base(new StubMonitor(), service, null)
        {
        }

        /// <summary>ResolveItem 被调用次数（断言缓存命中后未重新执行）。</summary>
        public int CallCount { get; private set; }

        internal override Item? ResolveItem(string itemId, out string suggestionsText)
        {
            CallCount++;
            suggestionsText = "";
            throw new InvalidOperationException($"resolve exploded: {itemId}");
        }
    }

    /// <summary>
    ///     TryCommit 首调走 base（真实提交 npc money）、次调抛异常的执行器（issue #27 ①）：
    ///     注入 commit 阶段异常，验证守卫回执 + 不写幂等缓存 + 已提交步被回滚。
    /// </summary>
    private sealed class ExplodingCommitExecutor : AdjustExecutor
    {
        public ExplodingCommitExecutor(AgentService? service)
            : base(new StubMonitor(), service, null)
        {
        }

        /// <summary>TryCommit 被调用次数（断言重发未被缓存挡掉、重新走了 ExecuteCore）。</summary>
        public int CallCount { get; private set; }

        internal override bool TryCommit(AdjustOp op, AgentInstance? agent, Farmer? player,
            out AdjustFailureCode code, out string detail, out string? resolvedItemId)
        {
            CallCount++;
            if (CallCount % 2 == 1)
            {
                // 奇数次调用走真实提交（每批第 1 步成功），偶数次抛（第 2 步爆炸）——
                // 每次完整执行都是"首步已提交 + 次步异常"，可重复验证回滚 + 不缓存。
                return base.TryCommit(op, agent, player, out code, out detail, out resolvedItemId);
            }

            throw new InvalidOperationException("commit exploded");
        }
    }
}
