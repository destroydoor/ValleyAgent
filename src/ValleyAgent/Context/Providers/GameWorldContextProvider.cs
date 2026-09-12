using StardewValley;
using ValleyAgent.RAG;
using ValleyAgent.Services;

namespace ValleyAgent.Context.Providers;

public class GameWorldContextProvider : IContextProvider
{
    private readonly GameSummaryLoader? _gameSummaryLoader;

    public GameWorldContextProvider(GameSummaryLoader? gameSummaryLoader)
    {
        _gameSummaryLoader = gameSummaryLoader;
    }

    public string ProviderName
    {
        get => "GameWorld";
    }

    public int Priority
    {
        get => 10;
    }

    public void Contribute(AgentContext context, AgentInstance agent)
    {
        var npc = Game1.getCharacterFromName(agent.NpcName);
        var weather = Game1.isRaining ? "Rainy" : Game1.isSnowing ? "Snowy" : "Sunny";

        context.CurrentLocation = npc?.currentLocation?.Name ?? Game1.currentLocation?.Name ?? "Unknown";
        context.CurrentSeason = Game1.currentSeason ?? "Spring";
        context.CurrentDay = Game1.dayOfMonth;
        context.CurrentTime = Game1.timeOfDay;
        context.Weather = weather;
        context.FormattedTime = FormatDecisionTime(Game1.timeOfDay);

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
    }

    private static string FormatDecisionTime(int timeOfDay)
    {
        var hours = timeOfDay / 100;
        var minutes = timeOfDay % 100;
        return $"{hours:D2}:{minutes:D2}";
    }
}