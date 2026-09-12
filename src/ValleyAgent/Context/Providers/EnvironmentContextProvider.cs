using System.Collections.Generic;
using StardewValley;
using ValleyAgent.Handlers;
using ValleyAgent.Services;

namespace ValleyAgent.Context.Providers;

public class EnvironmentContextProvider : IContextProvider
{
    public string ProviderName
    {
        get => "Environment";
    }

    public int Priority
    {
        get => 50;
    }

    public void Contribute(AgentContext context, AgentInstance agent)
    {
        var npc = Game1.getCharacterFromName(agent.NpcName);
        if (npc == null)
        {
            return;
        }

        var scans = new List<string>();
        if (FightHandler.ScanEnvironment(npc) is string fightScan)
        {
            scans.Add(fightScan);
        }

        if (FarmHandler.ScanEnvironment(npc) is string farmScan)
        {
            scans.Add(farmScan);
        }

        if (MineHandler.ScanEnvironment(npc) is string mineScan)
        {
            scans.Add(mineScan);
        }

        if (ForageHandler.ScanEnvironment(npc) is string forageScan)
        {
            scans.Add(forageScan);
        }

        if (scans.Count > 0)
        {
            var scanSummary = string.Join("; ", scans);
            context.GameState["nearby_objects"] = scanSummary;
            context.NearbySummary = scanSummary;
        }
    }
}