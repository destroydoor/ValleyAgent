using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using StardewValley;
using ValleyAgent.Services;

namespace ValleyAgent.Commands;

public interface IAgentCommand
{
    public string CommandName { get; }
    public IReadOnlyList<string> Aliases { get; }

    public void Execute(NPC npc, AgentInstance agent, Dictionary<string, object> parameters,
        Func<string, string, bool, Dictionary<string, object>, Task> sendResult);
}