using ValleyAgent.Services;

namespace ValleyAgent.Context;

public interface IContextProvider
{
    public string ProviderName { get; }
    public int Priority { get; }
    public void Contribute(AgentContext context, AgentInstance agent);
}