using System.Text;
using StardewModdingAPI;
using ValleyAgent.AI;
using ValleyAgent.Config;
using ValleyAgent.Services;

namespace ValleyAgent.Debug;

/// <summary>
///     Handles the ValleyAgent_context console command.
///     Shows decision context for an Agent.
/// </summary>
public class ShowContextHandler : CommandHandlerBase
{
    private readonly DecisionContextBuilder? _decisionContextBuilder;

    public ShowContextHandler(
        IMonitor? monitor,
        AgentService? agentService,
        ModConfig? config,
        DecisionContextBuilder? decisionContextBuilder)
        : base(monitor, agentService, config)
    {
        _decisionContextBuilder = decisionContextBuilder;
    }

    public CommandResult Handle(string npcName)
    {
        return SafeExecute(() =>
        {
            var serviceCheck = EnsureServiceAvailable(_agentService, "AgentService");
            if (!serviceCheck.Success)
            {
                return serviceCheck;
            }

            if (!_agentService!.TryGetAgent(npcName, out var agent) || agent == null)
            {
                return CommandResult.Fail($"'{npcName}' is not an active Agent.");
            }

            var contextCheck = EnsureServiceAvailable(_decisionContextBuilder, "DecisionContextBuilder");
            if (!contextCheck.Success)
            {
                return contextCheck;
            }

            var ctx = _decisionContextBuilder!.Build(agent);
            var sb = new StringBuilder();
            _ = sb.AppendLine($"=== Context for {npcName} ===");
            _ = sb.AppendLine($"State: {ctx.CurrentState}");
            _ = sb.AppendLine($"Location: {ctx.CurrentLocation}");
            _ = sb.AppendLine($"Time: {ctx.CurrentTime} | Season: {ctx.CurrentSeason} | Day: {ctx.CurrentDay}");
            return CommandResult.Ok(sb.ToString().TrimEnd());
        }, "ShowContext");
    }
}