using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using StardewModdingAPI;
using StardewValley;
using ValleyAgent.Inventory;
using ValleyAgent.Protocol;
using ValleyAgent.Services;
using ValleyAgent.Utils;
using ValleyAgent.WebSocket;
using AdjustFailureCode = ValleyAgent.Protocol.ProtocolV2.AdjustFailureCode;
using AdjustOp = ValleyAgent.Protocol.ProtocolV2.AdjustOp;
using AdjustResultMessage = ValleyAgent.Protocol.ProtocolV2.AdjustResultMessage;
// 实例化点用全名（ProtocolV2.AdjustResultMessage）：check:protocol 的 C# 发送证据
// 只识别 `new ProtocolV2.XxxMessage` 形态（模式 E），别名实例化不会被登记为 adjust_result 发送。
using AdjustStepResult = ValleyAgent.Protocol.ProtocolV2.AdjustStepResult;
using ExecuteAdjustMessage = ValleyAgent.Protocol.ProtocolV2.ExecuteAdjustMessage;
using SObject = StardewValley.Object;

namespace ValleyAgent.Economy;

/// <summary>
///     execute_adjust 原子批经济指令执行器（2026-08-15 账本迁移设计 §4.1/§6，步骤 1）。
///     权威账本在 TS；本执行器只做两件事：
///     1. 物理校验 —— 对 Game1.player 实时校验（余额/背包/物品存在性），不信 worldSnapshot；
///     2. 原语执行 —— money/item 增减，NPC 侧走 AgentInventory（保留 OnWalletChanged → TranscriptSink 留痕）。
///     原子批纪律（收编 TradeSettlement 回滚纪律）：全量零副作用预校验 → 按序提交 → 任一步失败回滚已提交项。
///     幂等：instructionId 为幂等键，指令结果环形日志（默认 256 条），重复指令直接返回缓存回执，
///     断线重连后 TS 凭 instructionId 查询执行结果（步骤 4 对账），缓存命中即可闭环。
///     非 sealed：物品解析（<see cref="ResolveItem" />）为 internal virtual，供单测注入合成物品
///     （游戏数据未加载的环境 ItemRegistry.Create 恒返回 null，无法走真实物品路径）。
/// </summary>
public class AdjustExecutor
{
    private readonly IMonitor? _monitor;
    private readonly AgentService? _agentService;
    private readonly IAgentServerProvider? _agentServerProvider;
    private readonly int _resultLogCapacity;

    /// <summary>指令结果环形日志：instructionId → 已执行回执（幂等缓存，最近 <see cref="_resultLogCapacity" /> 条）。</summary>
    private readonly Dictionary<string, AdjustResultMessage> _resultLog = new(StringComparer.Ordinal);

    /// <summary>环形淘汰顺序队列（FIFO，与 <see cref="_resultLog" /> 同锁维护）。</summary>
    private readonly Queue<string> _resultOrder = new();

    /// <summary>
    ///     当前 ExecuteCore 调用是否已进入阶段四（提交期）——自此任何 op 都可能已实际生效。
    ///     issue #27 守卫 ①：Execute 捕获异常后按此决定是否写幂等缓存（提交期异常不缓存，
    ///     避免把"可能部分已应用"钉死为"失败已处理"而阻断 TS reconcile 补偿）。
    ///     本执行器只允许主线程串行调用（见 <see cref="Execute" /> 注释），字段无并发竞争。
    /// </summary>
    private bool _commitPhaseEntered;

    private readonly object _gate = new();

    /// <param name="monitor">日志（可空：单测环境传 stub 或 null）。</param>
    /// <param name="agentService">NPC Agent 查找（可空：单测环境传 null 时 npc 目标直接 AgentMissing）。</param>
    /// <param name="agentServerProvider">WebSocket 回执通道（可空：单测不发回执）。</param>
    /// <param name="resultLogCapacity">指令结果日志容量（环形，默认 256，可配置）。</param>
    public AdjustExecutor(
        IMonitor? monitor,
        AgentService? agentService,
        IAgentServerProvider? agentServerProvider = null,
        int resultLogCapacity = 256)
    {
        _monitor = monitor;
        _agentService = agentService;
        _agentServerProvider = agentServerProvider;
        _resultLogCapacity = Math.Max(1, resultLogCapacity);
    }

    /// <summary>
    ///     执行原子批指令并返回回执。
    ///     幂等键 instructionId 已缓存时直接返回缓存回执（不重复执行、不重发）；
    ///     未缓存则执行后入环形日志。
    ///     外层守卫（issue #27 ①）：ExecuteCore 抛出的异常不再逃逸——转为 internalError
    ///     回执返回（携带异常全文）。此前异常路径既不写幂等缓存也不回执，TS 侧 10s 超时后
    ///     scheduleReconcile 无上限重发（每 ~40s 一轮直到重启）。回执由调用方
    ///     （EventHandlerInitializer.HandleExecuteAdjust）经 SendAdjustResultAsync 发出。
    ///     幂等缓存按异常阶段分流：pre-commit（零副作用）异常 → 缓存安全（重发必然同结果，
    ///     缓存命中即闭环）；commit 阶段异常 → 可能部分已应用，不缓存（内部已尽力回滚，
    ///     回滚失败 Error 留痕）——保留 TS reconcile 凭 instructionId 补偿的通路。
    ///     并发约定：幂等检查（锁内）与执行（锁外）之间存在竞态窗口，并发调用同一
    ///     instructionId 会重复执行——因此本方法**只允许在游戏主线程调用**（生产路径由
    ///     EventHandlerInitializer 的主线程指令队列保证；Execute 是 public 是给 TestMod/
    ///     单测直调，调用方同样须保证主线程串行）。
    /// </summary>
    public AdjustResultMessage Execute(ExecuteAdjustMessage message)
    {
        if (message == null)
        {
            throw new ArgumentNullException(nameof(message));
        }

        if (string.IsNullOrEmpty(message.InstructionId))
        {
            // 无幂等键的指令无法防重放，拒绝执行（校验失败结果不入缓存）。
            return Fail(message, AdjustFailureCode.InvalidOp, 0,
                new AdjustStepResult { Index = 0, Kind = "batch", Target = "", Success = false, FailureCode = AdjustFailureCode.InvalidOp, Detail = "missing instructionId" });
        }

        lock (_gate)
        {
            if (_resultLog.TryGetValue(message.InstructionId, out var cached))
            {
                _monitor?.Log($"[AdjustExecutor] idempotent hit: {message.InstructionId} (replay, no re-execute)",
                    LogLevel.Debug);
                return cached;
            }
        }

        ProtocolV2.AdjustResultMessage result;
        var cacheable = true;
        try
        {
            result = ExecuteCore(message);
        }
        catch (Exception ex)
        {
            // 守卫（issue #27 ①）：异常必回执。日志带异常全文（{ex} 含堆栈）——排障不猜。
            _monitor?.Log(
                $"[AdjustExecutor] {message.InstructionId}: ExecuteCore threw {Environment.NewLine}{ex}",
                LogLevel.Error);
            result = InternalErrorReceipt(message, ex, _commitPhaseEntered);
            cacheable = !_commitPhaseEntered;
        }

        if (cacheable)
        {
            Cache(message.InstructionId, result);
        }

        return result;
    }

    /// <summary>
    ///     查询已缓存指令结果（步骤 4 断线对账用：TS 凭 instructionId 查 C# 执行日志闭环）。
    ///     未执行过 / 已被环形淘汰返回 null。
    /// </summary>
    public AdjustResultMessage? FindCachedResult(string instructionId)
    {
        if (string.IsNullOrEmpty(instructionId))
        {
            return null;
        }

        lock (_gate)
        {
            return _resultLog.TryGetValue(instructionId, out var result) ? result : null;
        }
    }

    /// <summary>当前幂等缓存条数（ValleyAgent_diag 只读采集用，issue #27 ④）。</summary>
    public int CachedResultCount
    {
        get
        {
            lock (_gate)
            {
                return _resultLog.Count;
            }
        }
    }

    /// <summary>
    ///     fire-and-forget 回发 adjust_result（镜像 CommandExecutor.SendToolActionResultAsync 形状）。
    ///     发送失败只降级日志（回执丢失由 TS 超时重发 + instructionId 幂等兜底，见设计 §6）。
    /// </summary>
    public async Task SendAdjustResultAsync(ProtocolV2.AdjustResultMessage result)
    {
        if (result == null || _agentServerProvider == null)
        {
            _monitor?.Log("[AdjustExecutor] Cannot send adjust_result: no server provider", LogLevel.Debug);
            return;
        }

        try
        {
            var json = MessageProtocol.Serialize(result);
            await _agentServerProvider.SendMessageAsync(json).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            _monitor?.Log($"[AdjustExecutor] Failed to send adjust_result via WS: {ex}", LogLevel.Warn);
        }
    }

    // ── 执行核心 ─────────────────────────────────────────

    /// <summary>把执行结果写入环形日志（幂等缓存，超出容量淘汰最旧）。</summary>
    private void Cache(string instructionId, ProtocolV2.AdjustResultMessage result)
    {
        lock (_gate)
        {
            // 防重复入队：同一 instructionId 已缓存时只覆盖值，不重复占淘汰位——
            // 并发重复入队会让队列与字典脱节，淘汰时误删仍在缓存内的键。
            if (!_resultLog.ContainsKey(instructionId))
            {
                _resultOrder.Enqueue(instructionId);
            }

            _resultLog[instructionId] = result;
            while (_resultOrder.Count > _resultLogCapacity)
            {
                var oldest = _resultOrder.Dequeue();
                _resultLog.Remove(oldest);
            }
        }
    }

    private ProtocolV2.AdjustResultMessage ExecuteCore(ExecuteAdjustMessage message)
    {
        _commitPhaseEntered = false; // 只允许主线程串行调用（见 Execute 注释），字段无并发竞争
        var npcName = message.NpcName ?? "";

        if (message.Ops == null || message.Ops.Count == 0)
        {
            _monitor?.Log($"[AdjustExecutor] {message.InstructionId}: empty ops rejected", LogLevel.Warn);
            return Fail(message, AdjustFailureCode.InvalidOp, 0,
                new AdjustStepResult { Index = 0, Kind = "batch", Target = "", Success = false, FailureCode = AdjustFailureCode.InvalidOp, Detail = "empty ops" });
        }

        var steps = new List<AdjustStepResult>(message.Ops.Count);

        // 阶段一：静态参数校验（kind/target/amount/quantity，不触碰任何状态）。
        // 参数错误优先于状态错误——指令本身非法时不依赖 agent 是否存在。
        for (var i = 0; i < message.Ops.Count; i++)
        {
            var op = message.Ops[i];

            if (!TryValidateStatic(op, out var staticCode, out var staticDetail))
            {
                return Fail(message, staticCode, i, Step(op, staticCode, staticDetail));
            }
        }

        // 阶段二：NPC agent 解析（任一 op 目标是 npc 时）。
        // 用 TryGetBrain（活跃 + 休眠）而非 TryGetAgent（仅活跃）：钱包/背包挂在 AgentInventory 上，
        // 阶段 3 全员默认创建 Brain 后每个 NPC 都有 Inventory——adjust 原语不依赖状态机激活，
        // 与 DirectorTools/WorldSnapshotBuilder 的 L2 读取口径一致。仅在服务缺失/无 Brain 时 AgentMissing。
        AgentInstance? agent = null;
        var needsAgent = false;
        foreach (var op in message.Ops)
        {
            if (op.Target.Equals("npc", StringComparison.OrdinalIgnoreCase))
            {
                needsAgent = true;
                break;
            }
        }

        if (needsAgent)
        {
            if (_agentService == null || !_agentService.TryGetBrain(npcName, out agent) || agent == null)
            {
                var detail = $"agent '{npcName}' not found";
                _monitor?.Log($"[AdjustExecutor] {message.InstructionId}: {detail}", LogLevel.Warn);
                return Fail(message, AdjustFailureCode.AgentMissing, 0, Step(message.Ops[0], AdjustFailureCode.AgentMissing, detail));
            }
        }

        // 阶段 2.5：玩家解析（任一 op 目标是 player 时）。2026-08-16 联机：
        // 按 playerId 解析目标 Farmer（房客交易扣房客的钱物，而非硬编码主机 Game1.player）；
        // 缺省/null → Game1.player（单机/旧客户端向后兼容）；解析不到 → PlayerNotFound 整批拒绝（零副作用）。
        Farmer? player = null;
        var needsPlayer = false;
        foreach (var op in message.Ops)
        {
            if (op.Target.Equals("player", StringComparison.OrdinalIgnoreCase))
            {
                needsPlayer = true;
                break;
            }
        }

        if (needsPlayer)
        {
            player = ResolvePlayer(message.PlayerId);
            if (player == null)
            {
                // 缺省 playerId（回落 Game1.player 但尚无玩家：启动早期/单测）→ InternalError；
                // 指定了 playerId 但解析不到（玩家已退出/不存在）→ PlayerNotFound（2026-08-16 联机）。
                var isDefault = string.IsNullOrWhiteSpace(message.PlayerId);
                var detail = isDefault ? "player not available" : $"player '{message.PlayerId}' not found";
                _monitor?.Log($"[AdjustExecutor] {message.InstructionId}: {detail}", LogLevel.Warn);
                return Fail(message,
                    isDefault ? AdjustFailureCode.InternalError : AdjustFailureCode.PlayerNotFound, 0,
                    Step(message.Ops[0], isDefault ? AdjustFailureCode.InternalError : AdjustFailureCode.PlayerNotFound, detail),
                    player: null);
            }
        }

        // 阶段三：全量零副作用物理预校验。任一步不通过 → 整体失败（前面步骤尚未执行，无需回滚）。
        for (var i = 0; i < message.Ops.Count; i++)
        {
            var op = message.Ops[i];

            if (!TryPreValidate(op, agent, player, out var preCode, out var preDetail))
            {
                return Fail(message, preCode, i, Step(op, preCode, preDetail), player: player);
            }
        }

        // 阶段四：按序提交。任一步失败 → 回滚已提交项（收编 TradeSettlement 纪律），整体失败。
        // 自此任何 op 都可能已实际生效——异常路径按 _commitPhaseEntered 决定是否写幂等缓存。
        _commitPhaseEntered = true;
        try
        {
            for (var i = 0; i < message.Ops.Count; i++)
            {
                var op = message.Ops[i];
                if (TryCommit(op, agent, player, out var commitCode, out var commitDetail, out var resolvedItemId))
                {
                    steps.Add(new AdjustStepResult
                    {
                        Index = i, Kind = op.Kind, Target = op.Target, Success = true,
                        FailureCode = AdjustFailureCode.None, Detail = commitDetail, ItemId = resolvedItemId
                    });
                    continue;
                }

                _monitor?.Log($"[AdjustExecutor] {message.InstructionId}: op#{i} commit failed ({commitCode}: {commitDetail}); rolling back {steps.Count} committed step(s)", LogLevel.Warn);
                RollbackCommitted(message.Ops, steps, agent, player);

                return Fail(message, commitCode, i, Step(op, commitCode, commitDetail, resolvedItemId), steps, player);
            }
        }
        catch (Exception ex)
        {
            // 提交期异常同样触发回滚纪律（issue #27 ①）：此前异常直接逃逸出 ExecuteCore，
            // 已提交步悬空——C# 执行镜像与 TS 账本漂移。回滚失败由 RollbackCommitted Error 留痕，
            // 异常继续上抛给 Execute 的外层守卫转 internalError 回执。{ex} 全文留痕。
            _monitor?.Log(
                $"[AdjustExecutor] {message.InstructionId}: exception in commit phase; rolling back {steps.Count} committed step(s){Environment.NewLine}{ex}",
                LogLevel.Error);
            RollbackCommitted(message.Ops, steps, agent, player);
            throw;
        }

        // 整体成功：携带双方变更后余额（TS 推进 pending → committed 的依据）。
        return new ProtocolV2.AdjustResultMessage
        {
            RequestId = message.RequestId,
            InstructionId = message.InstructionId,
            NpcName = npcName,
            Success = true,
            Steps = steps,
            FailureCode = null,
            PlayerMoney = player?.Money,
            NpcMoney = agent?.Inventory.Money,
            PlayerId = message.PlayerId
        };
    }

    /// <summary>静态参数校验（不触碰游戏状态）：kind/target 合法、金额/数量非零。</summary>
    private static bool TryValidateStatic(AdjustOp op, out AdjustFailureCode code, out string detail)
    {
        var kindOk = op.Kind.Equals("money", StringComparison.OrdinalIgnoreCase)
                     || op.Kind.Equals("item", StringComparison.OrdinalIgnoreCase);
        if (!kindOk)
        {
            code = AdjustFailureCode.InvalidOp;
            detail = $"invalid kind '{op.Kind}' (expected money|item)";
            return false;
        }

        var targetOk = op.Target.Equals("player", StringComparison.OrdinalIgnoreCase)
                       || op.Target.Equals("npc", StringComparison.OrdinalIgnoreCase);
        if (!targetOk)
        {
            code = AdjustFailureCode.InvalidOp;
            detail = $"invalid target '{op.Target}' (expected player|npc)";
            return false;
        }

        if (op.Kind.Equals("money", StringComparison.OrdinalIgnoreCase))
        {
            if (op.Amount is not int amount || amount == 0)
            {
                code = AdjustFailureCode.InvalidOp;
                detail = $"invalid amount '{op.Amount}' (must be non-zero int)";
                return false;
            }
        }
        else
        {
            if (string.IsNullOrEmpty(op.ItemId))
            {
                code = AdjustFailureCode.InvalidOp;
                detail = "missing itemId";
                return false;
            }

            if (op.Quantity is not int quantity || quantity == 0)
            {
                code = AdjustFailureCode.InvalidOp;
                detail = $"invalid quantity '{op.Quantity}' (must be non-zero int)";
                return false;
            }
        }

        code = AdjustFailureCode.None;
        detail = "";
        return true;
    }

    /// <summary>
    ///     零副作用预校验：物品解析 + 物理约束（余额/背包满/物品存在性）。
    ///     通过不代表提交必然成功（物品加入仍可能因竞态失败），提交失败走回滚。
    /// </summary>
    private bool TryPreValidate(AdjustOp op, AgentInstance? agent, Farmer? player, out AdjustFailureCode code, out string detail)
    {
        // 玩家侧物理约束：目标 Farmer 必须可用（needsPlayer 时已解析，此处防御单测/启动早期）。
        if (op.Target.Equals("player", StringComparison.OrdinalIgnoreCase) && player == null)
        {
            code = AdjustFailureCode.InternalError;
            detail = "player not available";
            return false;
        }

        if (op.Kind.Equals("money", StringComparison.OrdinalIgnoreCase))
        {
            var amount = op.Amount!.Value;
            if (amount < 0 && TargetBalance(op.Target, agent, player) < -amount)
            {
                code = AdjustFailureCode.InsufficientFunds;
                detail = $"insufficient funds ({op.Target} wallet {TargetBalance(op.Target, agent, player)}g, need {-amount}g)";
                return false;
            }

            code = AdjustFailureCode.None;
            detail = "";
            return true;
        }

        // item 操作：先解析物品（与 give_item/adjust_inventory 一致的 ID → 名称回落），失败即 ItemNotFound。
        var item = ResolveItem(op.ItemId!, out var suggestionsText);
        if (item == null)
        {
            code = AdjustFailureCode.ItemNotFound;
            detail = $"unknown item_id '{op.ItemId}'{suggestionsText}";
            return false;
        }

        var qualifiedId = item.QualifiedItemId ?? op.ItemId!;
        var quantity = op.Quantity!.Value;

        if (quantity < 0)
        {
            // 扣除：目标必须有足量物品（零副作用：只查不扣）。
            var have = op.Target.Equals("player", StringComparison.OrdinalIgnoreCase)
                ? PlayerHasItem(player!, qualifiedId, -quantity)
                : CountItem(agent!.Inventory, qualifiedId) >= -quantity;
            if (!have)
            {
                code = AdjustFailureCode.ItemNotFound;
                detail = $"{op.Target} doesn't have {quantity} x {qualifiedId}";
                return false;
            }
        }
        else if (op.Target.Equals("player", StringComparison.OrdinalIgnoreCase) && player!.isInventoryFull())
        {
            // 玩家加物：背包满直接拒绝（提交时 addItemToInventoryBool 仍为最终裁决，失败走回滚）。
            code = AdjustFailureCode.InventoryFull;
            detail = "player inventory full";
            return false;
        }

        code = AdjustFailureCode.None;
        detail = "";
        return true;
    }

    /// <summary>
    ///     按序提交单个 op。成功返回 true 与人类可读详情；
    ///     失败返回失败码与原因（调用方负责回滚已提交项）。
    ///     resolvedItemId：item 步骤解析出的权威 QualifiedItemId（2026-08-17 键归一，回执携带），
    ///     money 步骤或解析失败为 null。
    ///     internal virtual：单测注入"提交期抛异常"（issue #27 ① 守卫的 commit 阶段分支）
    ///     ——覆写首调 base 成功、次调抛出，验证异常回执 + 不写幂等缓存。
    /// </summary>
    internal virtual bool TryCommit(AdjustOp op, AgentInstance? agent, Farmer? player, out AdjustFailureCode code, out string detail, out string? resolvedItemId)
    {
        var reason = op.Reason ?? "execute_adjust";
        var qualifiedId = "";
        resolvedItemId = null;

        if (op.Kind.Equals("money", StringComparison.OrdinalIgnoreCase))
        {
            var amount = op.Amount!.Value;
            if (op.Target.Equals("player", StringComparison.OrdinalIgnoreCase))
            {
                // 2026-08-16 联机：Farmer.Money setter 禁止改其他玩家（抛异常），
                // 统一走 team.AddIndividualMoney（vanilla 权威路径，单机等价 Money += amount）。
                Game1.player.team.AddIndividualMoney(player!, amount);
                code = AdjustFailureCode.None;
                detail = $"player wallet {amount:+#;-#} → {player!.Money}g";
                return true;
            }

            // NPC 侧走 AgentInventory 事件链（AddMoney/TrySpend → OnWalletChanged → TranscriptSink 留痕）。
            if (amount > 0)
            {
                var balance = agent!.Inventory.AddMoney(amount, reason);
                code = AdjustFailureCode.None;
                detail = $"{agent.NpcName} wallet +{amount} → {balance}g";
                return true;
            }

            if (!agent!.Inventory.TrySpend(-amount, reason))
            {
                code = AdjustFailureCode.InsufficientFunds;
                detail = $"{agent.NpcName} wallet_insufficient (balance {agent.Inventory.Money}g, need {-amount}g)";
                return false;
            }

            code = AdjustFailureCode.None;
            detail = $"{agent.NpcName} wallet {amount} → {agent.Inventory.Money}g";
            return true;
        }

        // item 操作（预校验已解析成功，这里重新解析一次以拿到物品实例——解析是纯查表，无副作用）。
        var item = ResolveItem(op.ItemId!, out _)!;
        qualifiedId = item.QualifiedItemId ?? op.ItemId!;
        resolvedItemId = qualifiedId;
        var quantity = op.Quantity!.Value;

        if (quantity > 0)
        {
            item.Stack = quantity;
            var added = op.Target.Equals("player", StringComparison.OrdinalIgnoreCase)
                ? player!.addItemToInventoryBool(item)
                : agent!.Inventory.TryAdd(item);
            if (!added)
            {
                code = AdjustFailureCode.InventoryFull;
                detail = $"{op.Target} inventory full, {qualifiedId} x{quantity} not added";
                return false;
            }

            code = AdjustFailureCode.None;
            detail = $"{op.Target} +{quantity} x {qualifiedId}";
            return true;
        }

        // 扣除。
        var removed = op.Target.Equals("player", StringComparison.OrdinalIgnoreCase)
            ? PlayerRemoveItem(player!, qualifiedId, -quantity)
            : RemoveItemQuantity(agent!.Inventory, qualifiedId, -quantity);
        if (!removed)
        {
            code = AdjustFailureCode.ItemNotFound;
            detail = $"{op.Target} doesn't have {qualifiedId} x{-quantity}";
            return false;
        }

        code = AdjustFailureCode.None;
        detail = $"{op.Target} -{quantity} x {qualifiedId}";
        return true;
    }

    /// <summary>
    ///     失败回滚：按逆序撤销已提交步骤（TradeSettlement 纪律）。
    ///     回滚失败只能降级日志（绝不静默）——此时 C# 执行镜像与 TS 账本可能漂移，
    ///     留痕供断线对账（步骤 4）发现。
    /// </summary>
    private void RollbackCommitted(List<AdjustOp> ops, List<AdjustStepResult> committedSteps, AgentInstance? agent, Farmer? player)
    {
        for (var s = committedSteps.Count - 1; s >= 0; s--)
        {
            var op = ops[committedSteps[s].Index];
            if (TryRollback(op, agent, player, out var rbDetail))
            {
                _monitor?.Log($"[AdjustExecutor] rollback op#{committedSteps[s].Index} ({op.Kind}/{op.Target}): {rbDetail}", LogLevel.Info);
            }
            else
            {
                _monitor?.Log($"[AdjustExecutor] ROLLBACK FAILED op#{committedSteps[s].Index} ({op.Kind}/{op.Target}): {rbDetail} — C# 镜像与 TS 账本可能漂移，待对账", LogLevel.Error);
            }
        }
    }

    /// <summary>撤销单个已提交 op（应用逆操作）。</summary>
    private bool TryRollback(AdjustOp op, AgentInstance? agent, Farmer? player, out string detail)
    {
        var reason = op.Reason is { Length: > 0 } ? $"{op.Reason} (adjust rollback)" : "adjust rollback";
        var isPlayer = op.Target.Equals("player", StringComparison.OrdinalIgnoreCase);

        if (op.Kind.Equals("money", StringComparison.OrdinalIgnoreCase))
        {
            var amount = op.Amount!.Value;
            if (isPlayer)
            {
                Game1.player.team.AddIndividualMoney(player!, -amount);
                detail = $"player wallet {amount:+#;-#} (undo)";
                return true;
            }

            if (amount > 0)
            {
                // 撤销收入 = 支出（需要余额充足）。
                if (agent!.Inventory.TrySpend(amount, reason))
                {
                    detail = $"{agent.NpcName} wallet {amount} (undo)";
                    return true;
                }
            }
            else
            {
                // 撤销支出 = 收入。AddMoney 恒成功，无需余额检查——刚花掉的这笔钱
                // 已从钱包扣除，撤销只是把它加回来（余额必然低于支出额，检查反而误判）。
                agent!.Inventory.AddMoney(-amount, reason);
                detail = $"{agent.NpcName} wallet +{-amount} (undo)";
                return true;
            }

            detail = $"{agent.NpcName} undo {amount}g failed";
            return false;
        }

        // item 逆操作：加过则扣回，扣过则补回。
        var item = ResolveItem(op.ItemId!, out _);
        if (item == null)
        {
            detail = $"cannot rollback: item '{op.ItemId}' unresolvable";
            return false;
        }

        var qualifiedId = item.QualifiedItemId ?? op.ItemId!;
        var quantity = op.Quantity!.Value;

        if (quantity > 0)
        {
            // 撤销加物 = 扣回同量。
            var removed = isPlayer
                ? PlayerRemoveItem(player!, qualifiedId, quantity)
                : RemoveItemQuantity(agent!.Inventory, qualifiedId, quantity);
            detail = removed ? $"{op.Target} -{quantity} x {qualifiedId} (undo)" : $"{op.Target} undo remove {qualifiedId} failed";
            return removed;
        }

        // 撤销扣物 = 补回同量。
        item.Stack = -quantity;
        var added = isPlayer
            ? player!.addItemToInventoryBool(item)
            : agent!.Inventory.TryAdd(item);
        detail = added ? $"{op.Target} +{-quantity} x {qualifiedId} (undo)" : $"{op.Target} undo add {qualifiedId} failed";
        return added;
    }

    // ── 工具方法 ─────────────────────────────────────────

    /// <summary>
    ///     解析物品（ID 直建 + 名称回落，镜像 CommandExecutor.ExecuteAdjustInventory）。
    ///     internal virtual：单测环境游戏数据未加载（ItemRegistry.Create 恒返回 null），
    ///     测试子类覆写此方法注入合成物品以覆盖 item 操作与回滚路径。
    /// </summary>
    internal virtual Item? ResolveItem(string itemId, out string suggestionsText)
    {
        suggestionsText = "";
        var item = ItemRegistry.Create(itemId, allowNull: true);
        if (item != null)
        {
            return item;
        }

        IReadOnlyList<string> suggestions = Array.Empty<string>();
        if (ItemNameResolver.TryResolve(itemId, out var resolvedId, out suggestions))
        {
            item = ItemRegistry.Create(resolvedId!, allowNull: true);
        }

        if (item == null && suggestions.Count > 0)
        {
            suggestionsText = $"; did you mean: {string.Join(", ", suggestions)}";
        }

        return item;
    }

    /// <summary>目标钱包余额（player = 目标 Farmer.Money，npc = AgentInventory.Money）。预校验专用，调用前已保证目标可用。</summary>
    private static int TargetBalance(string target, AgentInstance? agent, Farmer? player)
        => target.Equals("player", StringComparison.OrdinalIgnoreCase) ? player!.Money : agent!.Inventory.Money;

    /// <summary>统计 AgentInventory 中匹配物品的总堆叠数（跨槽位累加）。</summary>
    private static int CountItem(AgentInventory inventory, string qualifiedId)
    {
        var total = 0;
        foreach (var slot in inventory.GetAllItems())
        {
            if (slot is SObject obj &&
                (obj.QualifiedItemId == qualifiedId || obj.ItemId == qualifiedId))
            {
                total += obj.Stack;
            }
        }

        return total;
    }


    /// <summary>
    ///     玩家背包是否持有足量物品（PlayerTradeActor.HasItem 内联——2026-08-15 步骤 2
    ///     并入执行器：玩家侧物理校验不再依赖 ITradeActor 适配层）。
    ///     按 QualifiedItemId/ItemId 遍历背包累加堆叠数。
    /// </summary>
    private static bool PlayerHasItem(Farmer farmer, string itemId, int quantity)
    {
        var count = 0;
        foreach (var item in farmer.Items)
        {
            if (item is SObject obj && (obj.QualifiedItemId == itemId || obj.ItemId == itemId))
            {
                count += obj.Stack;
                if (count >= quantity)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    ///     从玩家背包按数量精确扣除（PlayerTradeActor.TryRemove 内联）。
    ///     SDV 1.6 Farmer 无按 qualified id 字符串移除的 API，手动遍历背包扣除。
    ///     原子：先确认足量（跨槽位累加）再扣，数量不足返回 false 且零副作用。
    /// </summary>
    private static bool PlayerRemoveItem(Farmer farmer, string itemId, int quantity)
    {
        if (quantity <= 0)
        {
            return false;
        }

        if (!PlayerHasItem(farmer, itemId, quantity))
        {
            return false;
        }

        var remaining = quantity;
        var items = farmer.Items;
        for (var i = 0; i < items.Count && remaining > 0; i++)
        {
            if (items[i] is not SObject obj)
            {
                continue;
            }

            var objId = obj.QualifiedItemId ?? obj.ItemId ?? "";
            if (!objId.Equals(itemId, StringComparison.OrdinalIgnoreCase))
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

        return remaining == 0;
    }

    /// <summary>
    ///     从 AgentInventory 按数量精确扣除（AgentInventory.TryRemove 只扣首个匹配槽位的
    ///     min(count, stack)，不足量时仍返回 true——这里是修正语义：跨槽位累扣到足量）。
    /// </summary>
    private static bool RemoveItemQuantity(AgentInventory inventory, string qualifiedId, int quantity)
    {
        if (quantity <= 0 || CountItem(inventory, qualifiedId) < quantity)
        {
            return false;
        }

        var remaining = quantity;
        while (remaining > 0)
        {
            if (!inventory.TryRemove(qualifiedId, remaining, out var removed))
            {
                return false;
            }

            remaining -= removed?.Stack ?? 0;
        }

        return true;
    }

    private static AdjustStepResult Step(AdjustOp op, AdjustFailureCode code, string detail, string? itemId = null)
        => new()
        {
            Index = 0, Kind = op.Kind, Target = op.Target, Success = false, FailureCode = code, Detail = detail,
            ItemId = itemId
        };

    /// <summary>
    ///     按 playerId 解析目标 Farmer（2026-08-16 联机适配）。
    ///     playerId 缺省/空 → Game1.player（单机/旧客户端向后兼容）；
    ///     playerId 非数字或玩家不存在 → null（调用方回 PlayerNotFound，整批零副作用拒绝）。
    ///     Game1.GetPlayer 覆盖 MasterPlayer + 在线 farmhand + 离线 farmhand（netWorldState.farmhandData）。
    ///     Game1 未初始化（启动早期/单测环境）时 GetPlayer 内部访问 netWorldState 抛 NRE——
    ///     按"解析不到"处理，不把异常冒泡成无回执（TS 收不到回执只能超时重发，比失败回执更糟）。
    /// </summary>
    private static Farmer? ResolvePlayer(string? playerId)
    {
        if (string.IsNullOrWhiteSpace(playerId))
        {
            return Game1.player;
        }

        if (!long.TryParse(playerId, out var id))
        {
            return null;
        }

        try
        {
            return Game1.GetPlayer(id);
        }
        catch (NullReferenceException)
        {
            return null;
        }
    }

    /// <summary>
    ///     ExecuteCore 异常路径的兜底回执（issue #27 守卫 ①）。
    ///     遵守 AGENTS §2 第 10 条"不猜"：Detail 携带异常全文（ex.ToString()，含类型/消息/堆栈），
    ///     不做任何归因润色。commit 阶段的异常额外声明"可能部分已应用"——提示消费方
    ///     （TS handleAdjustResult）该笔账目不能当作干净失败直接回滚了事，须待 reconcile 对账。
    /// </summary>
    private static ProtocolV2.AdjustResultMessage InternalErrorReceipt(
        ExecuteAdjustMessage message, Exception ex, bool commitPhaseEntered)
    {
        var phase = commitPhaseEntered
            ? "commit phase (rollback attempted; possible partial application — do not treat as clean failure)"
            : "pre-commit phase (zero side effects)";
        return new ProtocolV2.AdjustResultMessage
        {
            RequestId = message.RequestId,
            InstructionId = message.InstructionId,
            NpcName = message.NpcName ?? "",
            Success = false,
            Steps = new List<AdjustStepResult>(1)
            {
                new()
                {
                    Index = 0, Kind = "batch", Target = "", Success = false,
                    FailureCode = AdjustFailureCode.InternalError,
                    Detail = $"internal error during {phase}: {ex}"
                }
            },
            FailureCode = AdjustFailureCode.InternalError,
            PlayerMoney = null,
            NpcMoney = null,
            PlayerId = message.PlayerId
        };
    }

    private static ProtocolV2.AdjustResultMessage Fail(
        ExecuteAdjustMessage message,
        AdjustFailureCode code,
        int index,
        AdjustStepResult failedStep,
        List<AdjustStepResult>? executedSteps = null,
        Farmer? player = null)
    {
        var steps = executedSteps ?? new List<AdjustStepResult>(1);
        failedStep.Index = index;
        steps.Add(failedStep);

        return new ProtocolV2.AdjustResultMessage
        {
            RequestId = message.RequestId,
            InstructionId = message.InstructionId,
            NpcName = message.NpcName ?? "",
            Success = false,
            Steps = steps,
            FailureCode = code,
            PlayerMoney = player?.Money,
            NpcMoney = null, // 失败时批次已回滚/未执行完，余额由 TS 保持 pending 状态自行处理
            PlayerId = message.PlayerId
        };
    }
}
