using System.Text;
using StardewModdingAPI;
using StardewValley;
using ValleyAgent.Config;
using ValleyAgent.Services;

namespace ValleyAgent.Debug;

/// <summary>
///     Handles the ValleyAgent_agents console command.
///     Lists all active Agents with status.
/// </summary>
public class ListAgentsHandler : CommandHandlerBase
{
    public ListAgentsHandler(
        IMonitor? monitor,
        AgentService? agentService,
        ModConfig? config)
        : base(monitor, agentService, config)
    {
    }

    public CommandResult Handle()
    {
        return SafeExecute(() =>
        {
            var serviceCheck = EnsureServiceAvailable(_agentService, "AgentService");
            if (!serviceCheck.Success)
            {
                return serviceCheck;
            }

            var agents = _agentService!.GetAllAgents();
            if (agents.Count == 0)
            {
                return CommandResult.Ok("No active Agents.");
            }

            var sb = new StringBuilder();
            _ = sb.AppendLine(
                $"=== Active Agents ({agents.Count} | range [{_config?.MinAgentNpcs ?? 0}, {_config?.MaxAgentNpcs ?? 0}]) ===");
            foreach (var agent in agents)
            {
                var hearts = 0;
                if (Game1.player.friendshipData.TryGetValue(agent.NpcName, out var fs))
                {
                    hearts = fs.Points / 250;
                }

                _ = sb.AppendLine(
                    $"  [{agent.NpcName}] State: {agent.StateMachine.CurrentStateFlag} | Hearts: {hearts}/10 | Allocated: {agent.AllocatedAt:HH:mm}");
            }

            return CommandResult.Ok(sb.ToString().TrimEnd());
        }, "ListAgents");
    }
}