using System;
using System.Collections.Generic;
using System.Linq;
using ValleyAgent.Services;

namespace ValleyAgent.Context;

public class AgentContext
{
    public string NpcName { get; set; } = string.Empty;
    public string CurrentState { get; set; } = string.Empty;
    public string CurrentLocation { get; set; } = string.Empty;
    public string CurrentSeason { get; set; } = string.Empty;
    public int CurrentDay { get; set; }
    public int CurrentTime { get; set; }
    public string Weather { get; set; } = string.Empty;
    public string FormattedTime { get; set; } = string.Empty;
    public int FriendshipPoints { get; set; }
    public int FriendshipHearts { get; set; }
    public string FriendshipStatus { get; set; } = string.Empty;
    public Dictionary<string, string> RelationshipMemory { get; set; } = new();
    public Dictionary<string, string> GameState { get; set; } = new();
    public string NearbySummary { get; set; } = string.Empty;
    public List<string> BlockedStates { get; set; } = new();
}

public class AgentContextBuilder
{
    private readonly List<IContextProvider> _providers = new();

    public AgentContextBuilder AddProvider(IContextProvider provider)
    {
        _providers.Add(provider ?? throw new ArgumentNullException(nameof(provider)));
        return this;
    }

    public AgentContextBuilder AddProviders(IEnumerable<IContextProvider> providers)
    {
        foreach (var p in providers)
        {
            _ = AddProvider(p);
        }

        return this;
    }

    public AgentContext Build(AgentInstance agent)
    {
        var context = new AgentContext
        {
            NpcName = agent.NpcName,
            CurrentState = agent.StateMachine.CurrentStateFlag.ToString()
        };

        foreach (var provider in _providers.OrderBy(p => p.Priority))
        {
            try
            {
                provider.Contribute(context, agent);
            }
            catch (InvalidOperationException)
            {
            }
        }

        return context;
    }
}