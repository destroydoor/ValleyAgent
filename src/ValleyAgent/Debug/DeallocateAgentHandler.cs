using StardewModdingAPI;
using ValleyAgent.Config;
using ValleyAgent.Services;

namespace ValleyAgent.Debug;

/// <summary>
///     Handles the ValleyAgent_deallocate console command.
///     Removes an NPC's Agent allocation.
/// </summary>
public class DeallocateAgentHandler : CommandHandlerBase
{
    public DeallocateAgentHandler(
        IMonitor? monitor,
        AgentService? agentService,
        ModConfig? config)
        : base(monitor, agentService, config)
    {
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

            if (!_agentService!.AllocationManager.IsAllocated(npcName))
            {
                return CommandResult.Fail($"'{npcName}' is not an Agent.");
            }

            _ = _agentService.AllocationManager.Deallocate(npcName);
            _ = _agentService.RemoveAgent(npcName);
            return CommandResult.Ok($"Deallocated '{npcName}' from Agent status.");
        }, "DeallocateAgent");
    }
}