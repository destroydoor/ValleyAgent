using System;
using System.Linq;
using StardewModdingAPI;
using StardewValley;
using ValleyAgent.Config;
using ValleyAgent.RAG;
using ValleyAgent.Services;

namespace ValleyAgent.Testing;

public static class TestModHelper
{
    public static bool IsTestModPresent(IModHelper helper) => helper.ModRegistry.IsLoaded(GameConstants.TestModId);

    public static void ForceAllocateTestAgents(
        AgentService agentService,
        ValleyTalkBioLoader bioLoader,
        ModConfig config,
        IMonitor monitor,
        Action<AgentInstance> wireEvents)
    {
        // TestMod force-allocates Haley + Abigail = 2 agents. Ensure config allows it.
        config.MaxAgentNpcs = Math.Max(config.MaxAgentNpcs, 2);
        // MinAgentNpcs must not exceed MaxAgentNpcs after the bump above.
        if (config.MinAgentNpcs > config.MaxAgentNpcs)
        {
            config.MinAgentNpcs = config.MaxAgentNpcs;
        }

        var haleyNpc = Utility.getAllCharacters().FirstOrDefault(n => n.Name == "Haley" && n.IsVillager);
        if (haleyNpc != null && !agentService.AllocationManager.IsAllocated("Haley"))
        {
            _ = agentService.AllocationManager.TryAllocate("Haley", 0, 0, 999);
            var agent = agentService.CreateAgent("Haley");
            if (agent != null)
            {
                bioLoader?.InjectBio(agent.Brain);
                monitor.Log("Agent allocated: Haley (TestMod force-allocation)", LogLevel.Info);
                wireEvents(agent);
            }
        }

        var abigailNpc = Utility.getAllCharacters().FirstOrDefault(n => n.Name == "Abigail" && n.IsVillager);
        if (abigailNpc != null && !agentService.AllocationManager.IsAllocated("Abigail"))
        {
            _ = agentService.AllocationManager.TryAllocate("Abigail", 0, 0, 500);
            var abigailAgent = agentService.CreateAgent("Abigail");
            if (abigailAgent != null)
            {
                bioLoader?.InjectBio(abigailAgent.Brain);
                monitor.Log("Agent allocated: Abigail (TestMod pre-allocation for F5)", LogLevel.Info);
                wireEvents(abigailAgent);
            }
        }
    }
}