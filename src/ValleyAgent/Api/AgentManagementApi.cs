using System;
using System.Linq;
using ValleyAgent.Agents;
using ValleyAgent.Services;

namespace ValleyAgent.Api;

/// <summary>
///     Handles agent lifecycle management: allocation, deallocation, and agent enumeration.
///     Extracted from ValleyAgentApi to follow single responsibility principle.
/// </summary>
public class AgentManagementApi
{
    private readonly AgentService _agentService;
    private readonly AgentAllocationManager _allocationManager;

    public AgentManagementApi(AgentService agentService, AgentAllocationManager allocationManager)
    {
        _agentService = agentService ?? throw new ArgumentNullException(nameof(agentService));
        _allocationManager = allocationManager ?? throw new ArgumentNullException(nameof(allocationManager));
    }

    public string[] GetActiveAgentNames()
    {
        return _agentService.GetAllAgents()
            .Select(a => a.NpcName)
            .ToArray();
    }

    public bool TryAllocateAgent(string npcName)
    {
        try
        {
            if (_allocationManager.IsAllocated(npcName))
            {
                if (!_agentService.HasAgent(npcName))
                {
                    _ = _agentService.CreateAgent(npcName);
                }

                return true;
            }

            var result = _allocationManager.TryAllocate(npcName, 0, 0, 0);
            if (result)
            {
                _ = _agentService.CreateAgent(npcName);
            }

            return result;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    public bool TryDeallocateAgent(string npcName)
    {
        try
        {
            if (!_allocationManager.IsAllocated(npcName))
            {
                return true;
            }

            _ = _agentService.RemoveAgent(npcName);
            _ = _allocationManager.Deallocate(npcName);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}