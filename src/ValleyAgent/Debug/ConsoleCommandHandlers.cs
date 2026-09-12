using System;
using StardewModdingAPI;
using ValleyAgent.AI;
using ValleyAgent.Config;
using ValleyAgent.RAG;
using ValleyAgent.Services;
using ValleyAgent.StateMachine;
using ValleyAgent.WebSocket;

namespace ValleyAgent.Debug;

/// <summary>
///     Coordinates all console command handlers.
///     Provides a single point for registering command callbacks with <see cref="ConsoleCommands" />.
/// </summary>
public class ConsoleCommandHandlers
{
    private readonly AllocateAgentHandler _allocateAgentHandler;
    private readonly ChatHandler _chatHandler;
    private readonly DeallocateAgentHandler _deallocateAgentHandler;
    private readonly ForceDecisionHandler _forceDecisionHandler;
    private readonly ListAgentsHandler _listAgentsHandler;
    private readonly ReloadConfigHandler _reloadConfigHandler;
    private readonly ShowContextHandler _showContextHandler;
    private readonly TestLlmHandler _testLlmHandler;

    public ConsoleCommandHandlers(
        IMonitor? monitor,
        AgentService? agentService,
        ModConfig? config,
        DecisionContextBuilder? decisionContextBuilder,
        DualPathAgentServerProvider? agentServerProvider,
        DebugLogger? debugLogger,
        ValleyTalkBioLoader? bioLoader,
        IModHelper? helper,
        Action<AgentInstance, AgentState, string, string> enqueueDecision)
    {
        _forceDecisionHandler = new ForceDecisionHandler(
            monitor, agentService, config, debugLogger, enqueueDecision);

        _chatHandler = new ChatHandler(
            monitor, agentService, config, agentServerProvider, agentService?.CircuitBreaker);

        _allocateAgentHandler = new AllocateAgentHandler(
            monitor, agentService, config, bioLoader);

        _deallocateAgentHandler = new DeallocateAgentHandler(
            monitor, agentService, config);

        _reloadConfigHandler = new ReloadConfigHandler(
            monitor, agentService, config, helper);

        _listAgentsHandler = new ListAgentsHandler(
            monitor, agentService, config);

        _showContextHandler = new ShowContextHandler(
            monitor, agentService, config, decisionContextBuilder);

        _testLlmHandler = new TestLlmHandler(
            monitor, agentService, config);
    }

    /// <summary>
    ///     Registers all command callbacks with the <see cref="ConsoleCommands" /> instance.
    /// </summary>
    public void RegisterCallbacks(ConsoleCommands consoleCommands)
    {
        consoleCommands.ForceDecisionCallback = _forceDecisionHandler.HandleAsync;
        consoleCommands.ChatCallback = _chatHandler.HandleAsync;
        consoleCommands.AllocateAgentCallback = _allocateAgentHandler.Handle;
        consoleCommands.DeallocateAgentCallback = _deallocateAgentHandler.Handle;
        consoleCommands.ReloadConfigCallback = _reloadConfigHandler.Handle;
        consoleCommands.ListAgentsCallback = _listAgentsHandler.Handle;
        consoleCommands.ShowContextCallback = _showContextHandler.Handle;
        consoleCommands.TestLlmCallback = _testLlmHandler.HandleAsync;
    }
}