using System.Linq;
using StardewValley;
using ValleyAgent.Services;

namespace ValleyAgent.Context.Providers;

public class RelationshipContextProvider : IContextProvider
{
    public string ProviderName
    {
        get => "Relationship";
    }

    public int Priority
    {
        get => 30;
    }

    public void Contribute(AgentContext context, AgentInstance agent)
    {
        if (Game1.player.friendshipData.TryGetValue(agent.NpcName, out var fsData))
        {
            context.FriendshipPoints = fsData.Points;
            context.FriendshipHearts = fsData.Points / 250;
            context.FriendshipStatus = fsData.Status.ToString();
        }

        if (Game1.player.friendshipData.TryGetValue(agent.NpcName, out var fs))
        {
            var hearts = fs.Points / 250;
            context.RelationshipMemory["Friendship"] = $"{fs.Points}/2500 ({hearts}/10 hearts)";
            context.RelationshipMemory["Status"] = fs.Status.ToString();
        }

        if (!string.IsNullOrEmpty(agent.LastDecisionState))
        {
            context.RelationshipMemory["LastDecision"] = $"{agent.LastDecisionState}: {agent.LastDecisionReason}";
        }

        if (agent.Brain != null)
        {
            var recentMemories = agent.Brain.ShortTermMemories.TakeLast(5).ToList();
            if (recentMemories.Count > 0)
            {
                var lines = recentMemories.Select(m => m.Text);
                context.RelationshipMemory["RecentEvents"] = string.Join(" | ", lines);
            }

            context.RelationshipMemory["Emotion"] = agent.Brain.Emotion.ToString();
            context.RelationshipMemory["FriendshipPoints"] =
                Game1.player.friendshipData.TryGetValue(agent.NpcName, out var fp) ? fp.Points.ToString() : "0";
        }
    }
}