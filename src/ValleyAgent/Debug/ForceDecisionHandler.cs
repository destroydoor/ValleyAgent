using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using ValleyAgent.AI;
using ValleyAgent.Config;
using ValleyAgent.Services;
using ValleyAgent.StateMachine;

namespace ValleyAgent.Debug;

/// <summary>
///     Handles the ValleyAgent_force_decision console command.
///     decision 管道（LLM）已删除，命令改为强制规则引擎决策，仍可用于调试 NPC 状态切换。
/// </summary>
public class ForceDecisionHandler : CommandHandlerBase
{
    private readonly DebugLogger? _debugLogger;
    private readonly Action<AgentInstance, AgentState, string, string> _enqueueDecision;

    public ForceDecisionHandler(
        IMonitor? monitor,
        AgentService? agentService,
        ModConfig? config,
        DebugLogger? debugLogger,
        Action<AgentInstance, AgentState, string, string> enqueueDecision)
        : base(monitor, agentService, config)
    {
        _debugLogger = debugLogger;
        _enqueueDecision = enqueueDecision ?? throw new ArgumentNullException(nameof(enqueueDecision));
    }

    public Task<CommandResult> HandleAsync(string npcName)
    {
        return SafeExecuteAsync(async () =>
        {
            var serviceCheck = EnsureServiceAvailable(_agentService, "AgentService");
            if (!serviceCheck.Success)
            {
                return serviceCheck;
            }

            if (!_agentService!.TryGetAgent(npcName, out var agent) || agent == null)
            {
                return CommandResult.Fail($"Agent '{npcName}' not found.");
            }

            var npc = Game1.getCharacterFromName(npcName);
            var location = npc?.currentLocation?.NameOrUniqueName ?? Game1.currentLocation?.NameOrUniqueName ?? "Town";
            var playerDistance = npc != null
                ? Vector2.Distance(npc.Tile, Game1.player.Tile)
                : float.MaxValue;

            var friendship = GetCurrentFriendshipPoints(agent.NpcName);

            var ruleContext = new RuleDecisionContext
            {
                Location = location,
                Health = agent.Health.Health,
                MaxHealth = agent.Health.MaxHealth,
                Friendship = friendship,
                PlayerDistance = playerDistance,
                IsRaining = Game1.isRaining || Game1.isSnowing,
                NearbyObjects = new List<string>(),
                Emotion = agent.Brain.CurrentEmotionState,
                ConsecutiveIdleCount = 0,
                CurrentState = agent.StateMachine.CurrentStateFlag
            };

            var ruleResult = RuleBasedDecisionEngine.Decide(ruleContext);
            _enqueueDecision(agent, ruleResult.TargetState, ruleResult.Reason, ruleResult.Thought);

            _agentService.RecordDecision(npcName);
            _debugLogger?.LogDecision(npcName, agent.StateMachine.CurrentStateFlag, ruleResult.TargetState,
                $"[FORCED] {ruleResult.Reason}");

            await Task.CompletedTask.ConfigureAwait(false);
            return CommandResult.Ok(
                $"Forced rule decision for {npcName}: {ruleResult.TargetState} | {ruleResult.Reason}");
        }, "ForceDecision");
    }
}