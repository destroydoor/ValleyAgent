using StardewModdingAPI;
using StardewValley;
using ValleyAgent.Config;
using ValleyAgent.RAG;
using ValleyAgent.Services;
using SmaLogLevel = StardewModdingAPI.LogLevel;

namespace ValleyAgent.Debug;

/// <summary>
///     Handles the ValleyAgent_allocate console command.
///     Manually allocates an NPC as an AI Agent.
/// </summary>
public class AllocateAgentHandler : CommandHandlerBase
{
    private readonly ValleyTalkBioLoader? _bioLoader;

    public AllocateAgentHandler(
        IMonitor? monitor,
        AgentService? agentService,
        ModConfig? config,
        ValleyTalkBioLoader? bioLoader)
        : base(monitor, agentService, config)
    {
        _bioLoader = bioLoader;
    }

    public CommandResult Handle(string npcName)
    {
        return SafeExecute(() =>
        {
            var serviceCheck = EnsureServiceAvailable(_agentService, "AgentService");
            if (!serviceCheck.Success)
            {
                return serviceCheck;
            }

            var configCheck = EnsureServiceAvailable(_config, "ModConfig");
            if (!configCheck.Success)
            {
                return configCheck;
            }

            if (_agentService!.AllocationManager.CurrentAgentCount >= _config!.MaxAgentNpcs)
            {
                return CommandResult.Fail($"Max agents reached ({_config.MaxAgentNpcs}). Deallocate one first.");
            }

            if (_agentService.AllocationManager.CurrentAgentCount < _config!.MinAgentNpcs)
            {
                _monitor?.Log(
                    $"Below MinAgentNpcs floor ({_config.MinAgentNpcs}) — manual allocation is allowed but consider using auto-allocation.",
                    SmaLogLevel.Debug);
            }

            if (_agentService.AllocationManager.IsAllocated(npcName))
            {
                return CommandResult.Fail($"'{npcName}' is already an Agent.");
            }

            double priority = 0;
            if (Game1.player.friendshipData.TryGetValue(npcName, out var friendship))
            {
                priority = friendship.Points / 250.0;
            }

            if (_agentService.AllocationManager.TryAllocate(npcName, 0, 0, priority))
            {
                var agent = _agentService.CreateAgent(npcName);
                if (agent != null)
                {
                    _bioLoader?.InjectBio(agent.Brain);
                    return CommandResult.Ok($"Allocated '{npcName}' as Agent (priority: {priority:F2}).");
                }
            }

            return CommandResult.Fail($"Failed to allocate '{npcName}'.");
        }, "AllocateAgent");
    }
}