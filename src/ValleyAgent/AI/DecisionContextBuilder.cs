using System.Collections.Generic;
using StardewValley;
using ValleyAgent.Handlers;
using ValleyAgent.RAG;
using ValleyAgent.Services;

namespace ValleyAgent.AI;

public class DecisionContextBuilder
{
    private readonly GameSummaryLoader? _gameSummaryLoader;

    public DecisionContextBuilder(GameSummaryLoader? gameSummaryLoader)
    {
        _gameSummaryLoader = gameSummaryLoader;
    }

    public DecisionContext Build(AgentInstance agent)
    {
        var npc = Game1.getCharacterFromName(agent.NpcName);
        var weather = Game1.isRaining ? "Rainy" : Game1.isSnowing ? "Snowy" : "Sunny";
        var context = new DecisionContext
        {
            NpcName = agent.NpcName,
            CurrentState = agent.StateMachine.CurrentStateFlag,
            CurrentLocation = npc?.currentLocation?.Name ?? Game1.currentLocation?.Name ?? "Unknown",
            CurrentSeason = Game1.currentSeason ?? "Spring",
            CurrentDay = Game1.dayOfMonth,
            CurrentTime = Game1.timeOfDay,
            Weather = weather,
            FormattedTime = FormatDecisionTime(Game1.timeOfDay),
            FarmerNickname = agent.Brain.FarmerNickname ?? "新来的农夫"
        };

        // 填充 NPC 人设字段（TS Agent Server prompt 需要 npc_biography / npc_traits / npc_relationships）
        if (agent.Brain.Bio != null)
        {
            context.NpcBiography = agent.Brain.Bio.PromptSummary ?? string.Empty;
            context.NpcTraits = agent.Brain.Bio.TraitNames ?? string.Empty;
            context.NpcRelationships = agent.Brain.Bio.RelationshipSummary ?? string.Empty;
        }

        if (Game1.player.friendshipData.TryGetValue(agent.NpcName, out var fsData))
        {
            context.FriendshipPoints = fsData.Points;
            context.FriendshipHearts = fsData.Points / 250;
            context.FriendshipStatus = fsData.Status.ToString();
        }

        var emotionDesc = agent.Brain.GetEmotionDescription();
        if (!string.IsNullOrEmpty(emotionDesc))
        {
            context.RelationshipMemory["Emotion"] = emotionDesc;
        }

        var memorySummary = agent.Brain.GetMemorySummary();
        if (!string.IsNullOrEmpty(memorySummary))
        {
            context.RelationshipMemory["RecentEvents"] = memorySummary;
        }

        if (agent.Brain.Bio != null)
        {
            if (!string.IsNullOrEmpty(agent.Brain.Bio.TraitNames))
            {
                context.RelationshipMemory["Traits"] = agent.Brain.Bio.TraitNames;
            }

            if (!string.IsNullOrEmpty(agent.Brain.Bio.RelationshipSummary))
            {
                context.RelationshipMemory["Relationships"] = agent.Brain.Bio.RelationshipSummary;
            }
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

        if (agent.Health.HealthPercent < 0.5f)
        {
            context.RelationshipMemory["Health"] = $"Low ({agent.Health.Health}/{agent.Health.MaxHealth})";
        }

        context.GameState["Weather"] = weather;
        context.GameState["Season"] = Game1.currentSeason ?? "Spring";
        context.GameState["Day"] = $"{Game1.dayOfMonth}, Year {Game1.year}";

        if (_gameSummaryLoader is { IsLoaded: true })
        {
            var worldKnowledge = _gameSummaryLoader.BuildWorldKnowledgeSummary(
                Game1.currentSeason ?? "Spring",
                Game1.currentLocation?.Name ?? "Unknown",
                Game1.dayOfMonth);
            if (!string.IsNullOrWhiteSpace(worldKnowledge))
            {
                context.GameState["world_knowledge"] = worldKnowledge;
            }
        }

        if (npc != null)
        {
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

        return context;
    }

    public static string FormatDecisionTime(int timeOfDay)
    {
        var hours = timeOfDay / 100;
        var minutes = timeOfDay % 100;
        return $"{hours:D2}:{minutes:D2}";
    }
}