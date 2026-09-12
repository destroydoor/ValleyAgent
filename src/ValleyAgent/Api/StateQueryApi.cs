using System;
using System.Collections.Generic;
using System.Linq;
using StardewValley;
using ValleyAgent.Patches;
using ValleyAgent.Services;

namespace ValleyAgent.Api;

/// <summary>
///     Handles read-only state queries: agent state, health, emotion, inventory, memories, etc.
///     Extracted from ValleyAgentApi to follow single responsibility principle.
/// </summary>
public class StateQueryApi
{
    private readonly AgentService _agentService;

    public StateQueryApi(AgentService agentService)
    {
        _agentService = agentService ?? throw new ArgumentNullException(nameof(agentService));
    }

    public string GetAgentState(string npcName)
    {
        return !_agentService.TryGetAgent(npcName, out var agent) || agent == null
            ? string.Empty
            : agent.StateMachine.CurrentStateFlag.ToString();
    }

    public string[] GetEnabledFeatures()
    {
        var features = new List<string>();
        var config = _agentService.Config;
        if (config.EnableCombatAssist)
        {
            features.Add("Combat");
        }

        if (config.EnableFarmingAssist)
        {
            features.Add("Farming");
        }

        if (config.EnableMiningAssist)
        {
            features.Add("Mining");
        }

        if (config.EnableForagingAssist)
        {
            features.Add("Foraging");
        }

        if (config.EnableGifts)
        {
            features.Add("Gifts");
        }

        if (config.EnableFriendshipChanges)
        {
            features.Add("Friendship");
        }

        return features.ToArray();
    }

    public static int GetDialogueInterceptCount() => NPCDialoguePatch.InterceptCount;

    public static int GetNpcFriendshipPoints(string npcName) =>
        Game1.player.friendshipData.TryGetValue(npcName, out var fd) ? fd.Points : 0;

    public int GetNpcHealth(string npcName) => !_agentService.TryGetAgent(npcName, out var agent) || agent == null
        ? -1
        : agent.Health?.Health ?? -1;

    public int GetNpcMaxHealth(string npcName) => !_agentService.TryGetAgent(npcName, out var agent) || agent == null
        ? -1
        : agent.Health?.MaxHealth ?? -1;

    public string GetNpcEmotion(string npcName)
    {
        return !_agentService.TryGetAgent(npcName, out var agent) || agent == null
            ? string.Empty
            : agent.Brain?.Emotion.ToString() ?? string.Empty;
    }

    public string[] GetNpcInventory(string npcName)
    {
        return !_agentService.TryGetAgent(npcName, out var agent) || agent == null
            ? Array.Empty<string>()
            : agent.Inventory.GetAllItems()
                .Where(i => i != null)
                .Select(i => $"{i!.QualifiedItemId ?? i.ItemId}x{i.Stack}")
                .ToArray();
    }

    public string[] GetNpcMemories(string npcName)
    {
        return !_agentService.TryGetAgent(npcName, out var agent) || agent == null
            ? Array.Empty<string>()
            : agent.Brain?.ShortTermMemories.Select(m => m.Text).ToArray() ?? Array.Empty<string>();
    }

    public AgentInstance? GetAgentInstance(string npcName) =>
        !_agentService.TryGetAgent(npcName, out var agent) ? null : agent;

    public string[] GetRegisteredStates(string npcName)
    {
        if (!_agentService.TryGetAgent(npcName, out var agent) || agent == null)
        {
            return Array.Empty<string>();
        }

        return agent.StateMachine.GetRegisteredStates().Select(s => s.ToString()).ToArray();
    }
}