using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.TerrainFeatures;
using ValleyAgent.Commands;
using ValleyAgent.Economy;
using ValleyAgent.Multiplayer;
using ValleyAgent.Navigation;
using ValleyAgent.Patches;
using ValleyAgent.Protocol;
using ValleyAgent.Services;
using ValleyAgent.StateMachine;
using ValleyAgent.Utils;
using ValleyAgent.WebSocket;
using ActionResultReason = ValleyAgent.Protocol.ProtocolV2.ActionResultReason;

namespace ValleyAgent;

public class CommandExecutor
{
    private static readonly HashSet<string> HighRiskActions = new(StringComparer.OrdinalIgnoreCase)
    {
        "set_state", "drop_item", "set_friendship", "update_friendship"
    };

    private static readonly HashSet<string> ProtectedItems = new(StringComparer.OrdinalIgnoreCase)
    {
        "sword", "pickaxe", "axe", "hoe", "watering_can", "fishing_rod"
    };

    /// <summary>
    ///     在 C# 侧为 no-op 的工具：记忆/信息类由 TS 已执行，speak/show_dialogue 由 SubmitInput 渲染。
    ///     这些工具不下发 action_result，避免污染下次对话 prompt（"remember：成功" 类噪声）。
    /// </summary>
    private static readonly HashSet<string> NoOpTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "show_dialogue", "speak", "remember", "get_info", "forget"
    };

    private readonly IAgentServerProvider? _agentServerProvider;
    private readonly AgentService _agentService;
    private readonly AgentSyncBroadcaster? _broadcaster;
    private readonly IMonitor _monitor;
    private readonly CommandRegistry _registry;
    private readonly Goals.GoalExecutor? _goalExecutor;
    private readonly IMovementService? _movementService;

    /// <summary>
    ///     AgentTickLoop：FOLLOW→IDLE 立即释放到原版日程流程（vanilla-release）。
    /// </summary>
    private readonly Core.AgentTickLoop? _agentTickLoop;

    /// <summary>
    ///     阶段 3 Director 工具执行器（director_command 路由目标）。
    ///     Director 工具是元层工具（改 NPC 状态数据），与 NPC 角色扮演工具（switch 分发）完全分离。
    /// </summary>
    private readonly Commands.DirectorTools? _directorTools;

    public CommandExecutor(
        IMonitor monitor,
        AgentService agentService,
        CommandRegistry registry,
        IAgentServerProvider? agentServerProvider = null,
        AgentSyncBroadcaster? broadcaster = null,
        Goals.GoalExecutor? goalExecutor = null,
        Commands.DirectorTools? directorTools = null,
        IMovementService? movementService = null,
        Core.AgentTickLoop? agentTickLoop = null)
    {
        _monitor = monitor;
        _agentService = agentService;
        _registry = registry;
        _agentServerProvider = agentServerProvider;
        _broadcaster = broadcaster;
        _goalExecutor = goalExecutor;
        _directorTools = directorTools;
        _movementService = movementService;
        _agentTickLoop = agentTickLoop;
    }

    public Func<string, string, bool, Dictionary<string, object>, Task>? OnSendActionResult { get; set; }

    /// <summary>
    ///     Execute a single ToolAction (from TS dialogue response) by dispatching to the
    ///     appropriate CommandAction via the CommandRegistry.
    ///     Spec 4.2: P0 immediate tools (emote/give_item/give_gift/set_state/show_dialogue).
    ///     C3: 执行后向 TS 服务器回发 action_result（仅对 LLM 可见工具且 CallId 非空时），
    ///     用于 LLM 下次对话感知上次行动成败。NoOpTools 跳过回发（避免噪声）。
    /// </summary>
    public void ExecuteAction(ToolAction action, string npcName)
    {
        if (action == null)
        {
            throw new ArgumentNullException(nameof(action));
        }

        if (string.IsNullOrEmpty(action.Tool))
        {
            _monitor.Log("ExecuteAction: action.Tool is empty", LogLevel.Warn);
            return;
        }

        var tool = action.Tool;
        var args = action.Args ?? new Dictionary<string, object>();

        // No-op 工具：TS 侧已执行或 SubmitInput 已渲染，C# 无需动作也无需回发。
        if (NoOpTools.Contains(tool))
        {
            return;
        }

        bool success;
        ActionResultReason reason;
        string resultMessage;
        try
        {
            switch (tool)
            {
                case "emote":
                    (success, reason) = ExecuteEmote(args, npcName);
                    resultMessage = success ? "" : $"emote failed: {reason}";
                    break;
                // 2026-08-15 步骤 2：trade/give_item/give_gift/receive_payment 已改由
                // TS 同步编排（账本校验→execute_adjust 原子批→回执），不再作为 action 到达 C#。
                case "set_goal":
                    // 阶段2：意图式工具——TS 只声明意图（type/params/reportBack），
                    // C# GoalExecutor 建 Goal 进 EXECUTING_GOAL 后台执行（零 LLM 循环）。
                    (success, reason) = ExecuteSetGoal(args, npcName, action.CallId);
                    resultMessage = success ? "" : $"set_goal failed: {reason}";
                    break;
                case "set_state":
                    (success, reason) = ExecuteSetState(args, npcName);
                    resultMessage = success ? "" : $"set_state failed: {reason}";
                    break;
                case "chop_tree":
                    (success, reason) = ExecuteChopTree(args, npcName);
                    resultMessage = success ? "" : $"chop_tree failed: {reason}";
                    break;
                default:
                    _monitor.Log($"ExecuteAction: unknown tool '{tool}'", LogLevel.Warn);
                    success = false;
                    reason = ActionResultReason.InternalError;
                    resultMessage = $"unknown tool: {tool}";
                    break;
            }
        }
        catch (InvalidOperationException ex)
        {
            _monitor.Log($"ExecuteAction: {tool} failed: {ex.Message}", LogLevel.Warn);
            success = false;
            reason = ActionResultReason.InternalError;
            resultMessage = ex.Message;
        }

        // C3 反馈环：向 TS 服务器回发 action_result。CallId 为空（旧 TS 客户端）则跳过。
        if (!string.IsNullOrEmpty(action.CallId))
        {
            _ = SendToolActionResultAsync(npcName, action.CallId, tool, success, reason, resultMessage);
        }
    }

    /// <summary>
    ///     阶段 3：Director 工具路由（director_command 消息入口）。
    ///     与 ExecuteAction 的 NPC 工具 switch 完全分离——Director 工具是元层（改 NPC 状态数据），
    ///     不是 NPC 角色扮演工具，不进 dialogue 的 ToolAction 通道。
    /// </summary>
    /// <param name="tool">Director 工具名。</param>
    /// <param name="args">工具参数。</param>
    /// <returns>(成功?, 机器可读原因, 人类可读消息)。</returns>
    public (bool Success, ActionResultReason Reason, string Message) ExecuteDirectorCommand(
        string tool, Dictionary<string, object>? args)
    {
        if (_directorTools == null)
        {
            _monitor.Log($"[CommandExecutor] director_command '{tool}' skipped: DirectorTools not wired", LogLevel.Warn);
            return (false, ActionResultReason.InternalError, "director_tools_not_wired");
        }

        return _directorTools.Execute(tool, args);
    }

    /// <summary>
    ///     C3: 向 TS 服务器发送 action_result 消息，携带 callId/tool/success/reason/result。
    ///     TS ProtocolAdapter.routeToolResult 将其入队 per-NPC 反馈队列，下次对话注入 prompt。
    /// </summary>
    public async Task SendToolActionResultAsync(
        string npcName, string callId, string tool, bool success, ActionResultReason reason, string resultMessage)
    {
        if (_agentServerProvider == null)
        {
            _monitor?.Log("[CommandExecutor] Cannot send action_result: no server provider", LogLevel.Debug);
            return;
        }

        try
        {
            var msg = new ProtocolV2.ActionResultMessage
            {
                NpcName = npcName,
                CallId = callId,
                Tool = tool,
                Action = tool, // 镜像字段，兼容旧消费者
                Success = success,
                Reason = reason,
                Result = resultMessage.Length > 0 ? resultMessage : null,
                RequestId = Guid.NewGuid().ToString("N")
            };
            var json = MessageProtocol.Serialize(msg);
            await _agentServerProvider.SendMessageAsync(json).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            _monitor.Log($"[CommandExecutor] Failed to send action_result via WS: {ex.Message}", LogLevel.Warn);
        }
    }

    internal (bool Success, ActionResultReason Reason) ExecuteEmote(Dictionary<string, object> args, string npcName)
    {
        if (!args.TryGetValue("emote_id", out var emoteObj))
        {
            return (false, ActionResultReason.InvalidState);
        }

        NPC? npc;
        try
        {
            npc = Game1.getCharacterFromName<NPC>(npcName);
        }
        catch (NullReferenceException ex)
        {
            _monitor.Log($"ExecuteEmote: game state not initialized for NPC '{npcName}': {ex.Message}", LogLevel.Warn);
            return (false, ActionResultReason.AgentMissing);
        }

        if (npc == null)
        {
            return (false, ActionResultReason.AgentMissing);
        }

        var emoteId = emoteObj?.ToString() ?? "";
        var emoteInt = MapEmoteStringToInt(emoteId);
        if (emoteInt > 0)
        {
            npc.doEmote(emoteInt);
            _broadcaster?.BroadcastNpcAction(npcName, "emote", emoteInt);
        }

        return (true, ActionResultReason.None);
    }

    internal (bool Success, ActionResultReason Reason) ExecuteSetState(Dictionary<string, object> args, string npcName)
    {
        if (!args.TryGetValue("state", out var stateObj))
        {
            return (false, ActionResultReason.InvalidState);
        }

        var stateStr = stateObj?.ToString() ?? "IDLE";

        // agent 未分配或 AgentService 不可用（单测环境传 null）
        if (_agentService == null || !_agentService.TryGetAgent(npcName, out var agent) || agent == null)
        {
            _monitor.Log($"ExecuteSetState: {npcName}: agent not found", LogLevel.Warn);
            return (false, ActionResultReason.AgentMissing);
        }

        if (!Enum.TryParse<AgentState>(stateStr, true, out var state))
        {
            _monitor.Log($"ExecuteSetState: {npcName}: invalid state '{stateStr}'", LogLevel.Warn);
            return (false, ActionResultReason.InvalidState);
        }

        var prev = agent.StateMachine.CurrentStateFlag;

        // 幂等：目标状态已是当前状态 → 成功（对话中 NPC 同意"跟着我"时通常已在 FOLLOW）
        if (prev == state)
        {
            NPCDialoguePatch.UpdatePreDialogueState(npcName, state);
            return (true, ActionResultReason.None);
        }

        // 从 FOLLOW/TALK 切到 IDLE 时清理残留 controller（两种来源都要停移动）。
        // 不直接设 followSchedule——由 AgentTickLoop vanilla-release 流程统一处理
        // （BeginWalkBack → FinalizeVanillaRelease），避免与 IdleWanderHandler 抢 controller。
        if (state == AgentState.IDLE && (prev == AgentState.FOLLOW || prev == AgentState.TALK))
        {
            try
            {
                var npc = Game1.getCharacterFromName<NPC>(npcName);
                if (npc != null)
                {
                    _movementService?.Stop(npc, "set-state-cleanup");
                    _monitor.Log(
                        $"[execute-set-state] npc={npcName} from={prev} to={state} cleanup: stopMovement=true",
                        LogLevel.Info);
                }
            }
            catch (NullReferenceException ex)
            {
                _monitor.Log($"[execute-set-state] cleanup failed for {npcName}: {ex.Message}", LogLevel.Warn);
            }
        }

        try
        {
            var result = agent.StateMachine.ForceTransition(state, true);
            if (!result)
            {
                _monitor.Log($"ExecuteSetState BLOCKED: {npcName} {prev}→{state}", LogLevel.Warn);
                return (false, ActionResultReason.TransitionBlocked);
            }

            // FOLLOW→IDLE：立即释放到 vanilla-release 流程。
            // ForceTransition 成功后才调（released 分支看到非 IDLE 会 ReEngage，顺序错了会被弹回来）。
            // TALK→IDLE 不做立即释放——对话结束后维持原延迟释放行为（只清 controller）。
            if (prev == AgentState.FOLLOW && state == AgentState.IDLE)
            {
                _agentTickLoop?.ReleaseToVanillaNow(npcName);
                _monitor.Log(
                    $"[execute-set-state] npc={npcName} from=FOLLOW to=IDLE → vanilla release",
                    LogLevel.Info);
            }

            // 状态机转换成功：同步更新 preState，对话结束时恢复的是 LLM 决策的新状态
            NPCDialoguePatch.UpdatePreDialogueState(npcName, state);
            _monitor.Log($"ExecuteSetState: {npcName} {prev}→{state} OK", LogLevel.Info);
            return (true, ActionResultReason.None);
        }
        catch (InvalidOperationException ex)
        {
            _monitor.Log($"ExecuteSetState EXCEPTION: {npcName} {prev}→{state}: {ex.Message}", LogLevel.Error);
            return (false, ActionResultReason.InternalError);
        }
    }

    internal (bool Success, ActionResultReason Reason) ExecuteSetGoal(
        Dictionary<string, object> args, string npcName, string callId)
    {
        if (!args.TryGetValue("type", out var typeObj) || string.IsNullOrWhiteSpace(typeObj?.ToString()))
        {
            return (false, ActionResultReason.InvalidState);
        }

        var typeStr = typeObj.ToString()!;
        var reportBack = true;
        if (args.TryGetValue("reportBack", out var reportObj) && bool.TryParse(reportObj?.ToString(), out var parsedReport))
        {
            reportBack = parsedReport;
        }

        // agent 未分配或 AgentService 不可用（单测环境传 null）
        if (_agentService == null || !_agentService.TryGetAgent(npcName, out var agent) || agent == null)
        {
            _monitor.Log($"ExecuteSetGoal: {npcName}: agent not found", LogLevel.Warn);
            return (false, ActionResultReason.AgentMissing);
        }

        if (_goalExecutor == null)
        {
            _monitor.Log($"ExecuteSetGoal: {npcName}: GoalExecutor not wired", LogLevel.Warn);
            return (false, ActionResultReason.InternalError);
        }

        NPC? npc;
        try
        {
            npc = Game1.getCharacterFromName<NPC>(npcName);
        }
        catch (NullReferenceException ex)
        {
            _monitor.Log($"ExecuteSetGoal: game state not initialized for NPC '{npcName}': {ex.Message}", LogLevel.Warn);
            return (false, ActionResultReason.AgentMissing);
        }

        if (npc == null)
        {
            return (false, ActionResultReason.AgentMissing);
        }

        if (!_goalExecutor.CreateGoal(agent, npc, typeStr, args, reportBack, callId, out var error))
        {
            _monitor.Log($"ExecuteSetGoal: {npcName}: {error}", LogLevel.Warn);
            return (false, ActionResultReason.InvalidState);
        }

        return (true, ActionResultReason.None);
    }

    internal (bool Success, ActionResultReason Reason) ExecuteChopTree(Dictionary<string, object> args, string npcName)
    {
        // tree_id is optional; SDV trees are identified by tile position, not string ID.
        // Finds the nearest non-stump Tree within MaxRadius tiles of the NPC.
        NPC? npc;
        try
        {
            npc = Game1.getCharacterFromName<NPC>(npcName);
        }
        catch (NullReferenceException ex)
        {
            _monitor.Log($"ExecuteChopTree: game state not initialized for NPC '{npcName}': {ex.Message}",
                LogLevel.Warn);
            return (false, ActionResultReason.AgentMissing);
        }

        if (npc == null)
        {
            return (false, ActionResultReason.AgentMissing);
        }

        var location = npc.currentLocation;
        if (location == null)
        {
            return (false, ActionResultReason.LocationInvalid);
        }

        var npcTile = npc.Tile;
        Tree? nearestTree = null;
        var nearestTile = Vector2.Zero;
        var nearestDist = float.MaxValue;
        const float MaxRadius = 3f;

        foreach (var tileV in location.terrainFeatures.Keys)
        {
            if (location.terrainFeatures[tileV] is Tree tree && !tree.stump.Value)
            {
                var dist = Vector2.Distance(npcTile, tileV);
                if (dist < nearestDist && dist <= MaxRadius)
                {
                    nearestDist = dist;
                    nearestTree = tree;
                    nearestTile = tileV;
                }
            }
        }

        if (nearestTree == null)
        {
            _monitor.Log($"ExecuteChopTree: no tree found near {npcName} at {location.Name}", LogLevel.Debug);
            return (false, ActionResultReason.TargetUnreachable);
        }

        try
        {
            var woodQty = nearestTree.growthStage.Value >= 5 ? 5 : 1;
            location.terrainFeatures.Remove(nearestTile);

            var wood = ItemRegistry.Create("(O)388", woodQty, allowNull: true);
            if (wood != null)
            {
                Game1.createItemDebris(wood, nearestTile * 64f, 0, location);
            }

            _monitor.Log($"ExecuteChopTree: {npcName} chopped tree at {nearestTile}, dropped {woodQty} wood",
                LogLevel.Debug);
            return (true, ActionResultReason.None);
        }
        catch (InvalidOperationException ex)
        {
            _monitor.Log($"ExecuteChopTree failed for {npcName}: {ex.Message}", LogLevel.Warn);
            return (false, ActionResultReason.InternalError);
        }
    }

    private static int MapEmoteStringToInt(string emoteId)
    {
        return emoteId switch
        {
            "happy" => 8,
            "sad" => 28,
            "angry" => 12,
            "surprised" => 16,
            "heart" => 20,
            "question" => 18,
            "exclamation" => 18,
            "sleep" => 24,
            "music" => 56,
            "love" => 20,
            "wave" => 28,
            "confused" => 18,
            "thinking" => 18,
            "annoyed" => 12,
            "worried" => 28,
            "frustrated" => 12,
            "sweat" => 43,
            "fish" => 44,
            "gift" => 45,
            "bomb" => 46,
            "stretch" => 39,
            "star" => 40,
            "note" => 41,
            "hooray" => 42,
            _ => 0
        };
    }

    public void ExecuteCommands(string npcName, List<ProtocolV2.CommandAction> commands)
    {
        var npc = Game1.getCharacterFromName(npcName);
        if (npc == null)
        {
            _monitor.Log($"[CommandExecutor] NPC '{npcName}' not found", LogLevel.Warn);
            return;
        }

        if (!_agentService.TryGetAgent(npcName, out var agent) || agent == null)
        {
            _monitor.Log($"[CommandExecutor] No agent found for NPC '{npcName}'", LogLevel.Warn);
            return;
        }

        // 命令去重：相同动作只执行最后一次（speak 除外，允许多次说话）
        // 避免LLM在一次对话中重复调用相同工具导致状态抖动
        var deduplicatedCommands = DeduplicateCommands(commands);

        foreach (var command in deduplicatedCommands)
        {
            try
            {
                ExecuteSingleCommand(npc, agent, command);
            }
            catch (InvalidOperationException ex)
            {
                _monitor.Log($"[CommandExecutor] Failed to execute {command.Action}: {ex.Message}", LogLevel.Error);
                _ = SendActionResultAsync(npcName, command.Action, false,
                    new Dictionary<string, object> { ["error"] = ex.Message });
            }
        }
    }

    /// <summary>
    ///     命令去重：相同动作只保留最后一次，speak 动作保留全部（允许多轮对话）。
    /// </summary>
    private static List<ProtocolV2.CommandAction> DeduplicateCommands(List<ProtocolV2.CommandAction> commands)
    {
        if (commands.Count <= 1)
        {
            return commands;
        }

        var result = new List<ProtocolV2.CommandAction>();
        var seenActions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 从后往前遍历，保留每个动作的最后一次出现（speak 除外）
        for (var i = commands.Count - 1; i >= 0; i--)
        {
            var action = commands[i].Action ?? "";
            if (action.Equals("speak", StringComparison.OrdinalIgnoreCase))
            {
                // speak 允许多次，直接保留（但插入到结果前面以保持顺序）
                result.Insert(0, commands[i]);
                continue;
            }

            if (seenActions.Add(action))
            {
                // 第一次见到该动作（从后往前），保留
                result.Insert(0, commands[i]);
            }
        }

        return result;
    }

    public void ExecuteToolCalls(string npcName, List<ProtocolV2.ToolCall> toolCalls)
    {
        var commands = new List<ProtocolV2.CommandAction>(toolCalls.Count);
        foreach (var tc in toolCalls)
        {
            commands.Add(new ProtocolV2.CommandAction
            {
                Action = tc.Action,
                Parameters = tc.Parameters
            });
        }

        ExecuteCommands(npcName, commands);
    }

    private void ExecuteSingleCommand(NPC npc, AgentInstance agent, ProtocolV2.CommandAction command)
    {
        // 高风险动作权限检查
        if (HighRiskActions.Contains(command.Action))
        {
            // set_friendship / update_friendship 只能由 friendship_eval 事件驱动，
            // 不允许 LLM 通过工具调用直接修改好感度（防止作弊）
            if (command.Action.Equals("set_friendship", StringComparison.OrdinalIgnoreCase)
                || command.Action.Equals("update_friendship", StringComparison.OrdinalIgnoreCase))
            {
                _monitor.Log(
                    $"[CommandExecutor] Blocked {command.Action} from tool call: {npc.Name} (friendship must be driven by friendship_eval)",
                    LogLevel.Warn);
                _ = SendActionResultAsync(npc.Name, command.Action, false,
                    new Dictionary<string, object>
                    {
                        ["error"] =
                            $"{command.Action} is not allowed from tool calls; friendship changes must come from friendship_eval events"
                    });
                return;
            }

            if (command.Action.Equals("drop_item", StringComparison.OrdinalIgnoreCase))
            {
                var itemId = AgentCommandBase.GetParamAny(command.Parameters, "item_id", "itemId", "")?.ToString();
                if (!string.IsNullOrEmpty(itemId) && ProtectedItems.Contains(itemId))
                {
                    _monitor.Log($"[CommandExecutor] Blocked dropping protected item: {itemId}", LogLevel.Warn);
                    _ = SendActionResultAsync(npc.Name, command.Action, false,
                        new Dictionary<string, object> { ["error"] = $"Cannot drop protected item: {itemId}" });
                    return;
                }
            }
        }

        var commandInstance = _registry.GetCommand(command.Action);
        if (commandInstance == null)
        {
            _monitor.Log($"[CommandExecutor] Unknown command: {command.Action}", LogLevel.Warn);
            _ = SendActionResultAsync(npc.Name, command.Action, false,
                new Dictionary<string, object> { ["error"] = $"Unknown command: {command.Action}" });
            return;
        }

        // 命令执行超时监控：记录耗时超过阈值的命令（不阻塞，仅警告）
        var startTime = Environment.TickCount;
        try
        {
            commandInstance.Execute(npc, agent, command.Parameters, SendActionResultAsync);
        }
        finally
        {
            var elapsed = Environment.TickCount - startTime;
            if (elapsed > 1000)
            {
                _monitor.Log($"[CommandExecutor] {command.Action} took {elapsed}ms (slow, threshold=1000ms)",
                    LogLevel.Warn);
            }
        }
    }

    private async Task SendActionResultAsync(string npcName, string action, bool success,
        Dictionary<string, object> result)
    {
        if (OnSendActionResult != null)
        {
            try
            {
                await OnSendActionResult(npcName, action, success, result).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                _monitor.Log($"[CommandExecutor] Failed to send action_result via callback: {ex.Message}",
                    LogLevel.Warn);
            }

            return;
        }

        if (_agentServerProvider != null)
        {
            try
            {
                var msg = new ProtocolV2.ActionResultMessage
                {
                    NpcName = npcName,
                    Action = action,
                    Success = success,
                    Result = result,
                    RequestId = Guid.NewGuid().ToString("N")
                };
                var json = MessageProtocol.Serialize(msg);
                await _agentServerProvider.SendMessageAsync(json).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                _monitor.Log($"[CommandExecutor] Failed to send action_result via WS: {ex.Message}", LogLevel.Warn);
            }
        }
    }
}