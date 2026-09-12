using System.Threading.Tasks;
using StardewModdingAPI;
using ValleyAgent.Config;
using ValleyAgent.Services;

namespace ValleyAgent.Debug;

/// <summary>
///     Handles the ValleyAgent_test_llm console command.
///     Tests LLM connection (currently disabled — routes through TS Agent Server).
/// </summary>
public class TestLlmHandler : CommandHandlerBase
{
    public TestLlmHandler(
        IMonitor? monitor,
        AgentService? agentService,
        ModConfig? config)
        : base(monitor, agentService, config)
    {
    }

    public Task<CommandResult> HandleAsync()
    {
        return Task.FromResult(CommandResult.Fail(
            "TestLLM disabled: all LLM calls route through TS Agent Server (valley-ai-server.exe). " +
            "Check the server's cmd window for LLM connectivity errors; the server logs VercelAIProvider retries and LLMBillingError/LLMUnavailableError."));
    }
}