using ValleyAgent.Services;

namespace ValleyAgent.Context.Providers;

public class BrainContextProvider : IContextProvider
{
    public string ProviderName
    {
        get => "Brain";
    }

    public int Priority
    {
        get => 40;
    }

    public void Contribute(AgentContext context, AgentInstance agent)
    {
        // 情绪
        var emotionDesc = agent.Brain.GetEmotionDescription();
        if (!string.IsNullOrEmpty(emotionDesc))
        {
            context.RelationshipMemory["BrainEmotion"] = emotionDesc;
        }

        // 短期记忆
        var memorySummary = agent.Brain.GetMemorySummary();
        if (!string.IsNullOrEmpty(memorySummary))
        {
            context.RelationshipMemory["BrainRecentEvents"] = memorySummary;
        }

        // Bio 数据
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
    }
}