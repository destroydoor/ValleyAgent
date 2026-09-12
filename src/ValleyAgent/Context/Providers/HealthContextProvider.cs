using ValleyAgent.Services;

namespace ValleyAgent.Context.Providers;

public class HealthContextProvider : IContextProvider
{
    public string ProviderName
    {
        get => "Health";
    }

    public int Priority
    {
        get => 20;
    }

    public void Contribute(AgentContext context, AgentInstance agent)
    {
        // 始终报告血量信息（修复二元化丢失问题）
        var healthPercent = agent.Health.HealthPercent;
        if (healthPercent < 0.3f)
        {
            context.RelationshipMemory["Health"] = $"Critical ({agent.Health.Health}/{agent.Health.MaxHealth})";
        }
        else if (healthPercent < 0.5f)
        {
            context.RelationshipMemory["Health"] = $"Low ({agent.Health.Health}/{agent.Health.MaxHealth})";
        }
        else
        {
            context.RelationshipMemory["Health"] =
                $"{agent.Health.Health}/{agent.Health.MaxHealth} ({healthPercent:P0})";
        }
    }
}