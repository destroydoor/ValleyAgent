using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ValleyAgent.Agents;
using ValleyAgent.Config;
using ValleyAgent.Infrastructure;
using ValleyAgent.Resilience;

namespace ValleyAgent.Debug;

/// <summary>
///     Result of executing a console command.
/// </summary>
public class CommandResult
{
    /// <summary>Whether the command executed successfully.</summary>
    public bool Success { get; set; }

    /// <summary>Output message to display to the user.</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>Optional additional data for programmatic consumers.</summary>
    public Dictionary<string, object>? Data { get; set; }

    public static CommandResult Ok(string message, Dictionary<string, object>? data = null) =>
        new() { Success = true, Message = message, Data = data };

    public static CommandResult Fail(string message) => new() { Success = false, Message = message };
}

/// <summary>
///     Game-agnostic console command system for ValleyAgent.
///     Design:
///     - Exposes commands as a string→action map
///     - ModEntry wires command invocations to SMAPI's helper.ConsoleCommands
///     - No SMAPI types in core logic
///     - Graceful handling of invalid commands and missing arguments
///     - Help text built-in for all commands
///     Commands:
///     - ValleyAgent_status           �?Show all Agent states and info
///     - ValleyAgent_force_decision   �?Force LLM decision for an NPC
///     - ValleyAgent_reset_circuit    �?Reset the CircuitBreaker
///     - ValleyAgent_token_usage      �?Show session token stats
///     - ValleyAgent_debug [on|off]   �?Toggle debug mode
///     - ValleyAgent_reload_config    �?Reload config from disk
/// </summary>
public class ConsoleCommands
{
    private readonly AgentAllocationManager? _agentManager;
    private readonly CircuitBreaker? _circuitBreaker;

    private readonly Dictionary<string, Func<string[], CommandResult>> _commands;
    private readonly ModConfig? _config;
    private readonly DebugLogger? _debugLogger;
    private readonly Dictionary<string, string> _helpText;

    public ConsoleCommands(
        AgentAllocationManager? agentManager = null,
        CircuitBreaker? circuitBreaker = null,
        DebugLogger? debugLogger = null,
        ModConfig? config = null)
    {
        _agentManager = agentManager;
        _circuitBreaker = circuitBreaker;
        _debugLogger = debugLogger;
        _config = config;

        _commands = new Dictionary<string, Func<string[], CommandResult>>(StringComparer.OrdinalIgnoreCase);
        _helpText = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        RegisterCommands();
    }

    /// <summary>
    ///     Callback to force an LLM decision for a specific NPC.
    ///     Called by ValleyAgent_force_decision.
    /// </summary>
    public Func<string, Task<CommandResult>>? ForceDecisionCallback { get; set; }

    /// <summary>
    ///     Callback to reload configuration from disk.
    ///     Called by ValleyAgent_reload_config.
    /// </summary>
    public Func<CommandResult>? ReloadConfigCallback { get; set; }

    /// <summary>
    ///     Callback to allocate an NPC as an Agent.
    ///     Called by ValleyAgent_allocate.
    /// </summary>
    public Func<string, CommandResult>? AllocateAgentCallback { get; set; }

    /// <summary>
    ///     Callback to deallocate an NPC from being an Agent.
    ///     Called by ValleyAgent_deallocate.
    /// </summary>
    public Func<string, CommandResult>? DeallocateAgentCallback { get; set; }

    /// <summary>
    ///     Callback to chat with an Agent NPC.
    ///     Called by ValleyAgent_chat.
    /// </summary>
    public Func<string, string, Task<CommandResult>>? ChatCallback { get; set; }

    /// <summary>
    ///     Callback to list all agents with detailed info.
    ///     Called by ValleyAgent_agents.
    /// </summary>
    public Func<CommandResult>? ListAgentsCallback { get; set; }

    /// <summary>
    ///     Callback to show decision context for an NPC.
    ///     Called by ValleyAgent_context.
    /// </summary>
    public Func<string, CommandResult>? ShowContextCallback { get; set; }

    /// <summary>
    ///     Callback to test LLM connection.
    ///     Called by ValleyAgent_test_llm.
    /// </summary>
    public Func<Task<CommandResult>>? TestLlmCallback { get; set; }

    /// <summary>
    ///     Callback to build the one-screen diagnostics report (issue #27 ④).
    ///     具体数据采集由宿主（EventHandlerInitializer）注入——WS 状态/TS 进程/日志尾部/
    ///     熔断器/队列深度分散在宿主侧服务里，保持本类 game-agnostic。
    ///     实现方约定：单项取不到标 "&lt;unavailable&gt;"，报告器自身不抛异常。
    /// </summary>
    public Func<string>? DiagReportCallback { get; set; }

    /// <summary>
    ///     Maps command names to their handler functions.
    /// </summary>
    public IReadOnlyDictionary<string, Func<string[], CommandResult>> CommandMap
    {
        get => _commands;
    }

    /// <summary>
    ///     Executes a command by name with the given arguments.
    /// </summary>
    /// <param name="commandName">The command name (e.g., "ValleyAgent_status").</param>
    /// <param name="args">Arguments split by space.</param>
    /// <returns>CommandResult with success status and output message.</returns>
    public CommandResult Execute(string commandName, string[] args)
    {
        if (string.IsNullOrWhiteSpace(commandName))
        {
            return CommandResult.Fail("No command specified. Type 'ValleyAgent_help' for available commands.");
        }

        if (_commands.TryGetValue(commandName, out var handler))
        {
            try
            {
                return handler(args);
            }
            catch (Exception ex)
            {
                // issue #27 ④：兜底捕获——此前只捕 InvalidOperationException/ArgumentException，
                // NRE/JsonException/TaskCanceledException 等直接逃逸到 SMAPI 命令执行器
                // （用户侧零反馈）。{ex} 全文留痕 + 一行错误反馈。
                _debugLogger?.Log($"Console command '{commandName}' failed: {ex}", LogLevel.Error);
                return CommandResult.Fail(
                    $"Command '{commandName}' failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        return CommandResult.Fail($"Unknown command: '{commandName}'. Type 'ValleyAgent_help' for available commands.");
    }

    /// <summary>
    ///     Parses a raw command string into command name and arguments.
    ///     Splits by space, respecting basic quoting (not implemented - simple split).
    /// </summary>
    /// <param name="input">Raw command input.</param>
    /// <returns>Tuple of (commandName, args array).</returns>
    public static (string commandName, string[] args) ParseCommand(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return (string.Empty, Array.Empty<string>());
        }

        var parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return (string.Empty, Array.Empty<string>());
        }

        var commandName = parts[0];
        var args = parts.Length > 1 ? parts[1..] : Array.Empty<string>();
        return (commandName, args);
    }

    /// <summary>
    ///     Gets help text for all commands or a specific command.
    /// </summary>
    public string GetHelpText(string? commandName = null)
    {
        if (!string.IsNullOrWhiteSpace(commandName))
        {
            return _helpText.TryGetValue(commandName, out var help) ? help : $"No help available for '{commandName}'.";
        }

        var sb = new StringBuilder();
        _ = sb.AppendLine("=== ValleyAgent Console Commands ===");
        foreach (var kvp in _helpText.OrderBy(h => h.Key, StringComparer.OrdinalIgnoreCase))
        {
            _ = sb.AppendLine(kvp.Value);
        }

        return sb.ToString();
    }

    private void RegisterCommands()
    {
        // ValleyAgent_status
        _commands["ValleyAgent_status"] = HandleStatus;
        _helpText["ValleyAgent_status"] = "ValleyAgent_status - Show all allocated Agent states and info";

        // ValleyAgent_force_decision
        _commands["ValleyAgent_force_decision"] = HandleForceDecision;
        _helpText["ValleyAgent_force_decision"] =
            "ValleyAgent_force_decision [npc] - Force an LLM decision for the specified NPC";

        // ValleyAgent_reset_circuit
        _commands["ValleyAgent_reset_circuit"] = HandleResetCircuit;
        _helpText["ValleyAgent_reset_circuit"] = "ValleyAgent_reset_circuit - Reset the CircuitBreaker to CLOSED state";

        // ValleyAgent_token_usage
        _commands["ValleyAgent_token_usage"] = HandleTokenUsage;
        _helpText["ValleyAgent_token_usage"] = "ValleyAgent_token_usage - Show session LLM token usage statistics";

        // ValleyAgent_debug
        _commands["ValleyAgent_debug"] = HandleDebug;
        _helpText["ValleyAgent_debug"] = "ValleyAgent_debug [on|off] - Toggle debug mode (or show current state)";

        // ValleyAgent_allocate
        _commands["ValleyAgent_allocate"] = HandleAllocate;
        _helpText["ValleyAgent_allocate"] = "ValleyAgent_allocate [npc] - Manually allocate an NPC as an AI Agent";

        // ValleyAgent_deallocate
        _commands["ValleyAgent_deallocate"] = HandleDeallocate;
        _helpText["ValleyAgent_deallocate"] = "ValleyAgent_deallocate [npc] - Remove an NPC's Agent allocation";

        // ValleyAgent_chat
        _commands["ValleyAgent_chat"] = HandleChat;
        _helpText["ValleyAgent_chat"] = "ValleyAgent_chat [npc] [message] - Chat with an Agent NPC";

        // ValleyAgent_agents
        _commands["ValleyAgent_agents"] = HandleAgents;
        _helpText["ValleyAgent_agents"] = "ValleyAgent_agents - List all active Agents with status";

        // ValleyAgent_context
        _commands["ValleyAgent_context"] = HandleContext;
        _helpText["ValleyAgent_context"] = "ValleyAgent_context [npc] - Show decision context for an Agent";

        // ValleyAgent_test_llm
        _commands["ValleyAgent_test_llm"] = HandleTestLlm;
        _helpText["ValleyAgent_test_llm"] = "ValleyAgent_test_llm - Test LLM connection with a simple prompt";

        // ValleyAgent_diag（issue #27 ④）
        _commands["ValleyAgent_diag"] = HandleDiag;
        _helpText["ValleyAgent_diag"] =
            "ValleyAgent_diag - One-screen diagnostics: WS status / TS process / server.log tail / "
            + "circuit breaker / queue depths / pending adjusts / last failure";

        // ValleyAgent_reload_config
        _commands["ValleyAgent_reload_config"] = HandleReloadConfig;
        _helpText["ValleyAgent_reload_config"] = "ValleyAgent_reload_config - Reload configuration from disk";

        // ValleyAgent_help
        _commands["ValleyAgent_help"] = HandleHelp;
        _helpText["ValleyAgent_help"] = "ValleyAgent_help [command] - Show help for all commands or a specific command";
    }

    private CommandResult HandleStatus(string[] args)
    {
        var sb = new StringBuilder();

        if (_agentManager == null)
        {
            _ = sb.AppendLine("AgentAllocationManager not available.");
        }
        else
        {
            var agents = _agentManager.GetAllAllocatedAgents();
            if (agents.Count == 0)
            {
                _ = sb.AppendLine("No Agents currently allocated.");
            }
            else
            {
                _ = sb.AppendLine(
                    $"=== Allocated Agents ({agents.Count} | range [{_agentManager.MinAgents}, {_agentManager.MaxAgents}]) ===");

                foreach (var agent in agents)
                {
                    _ = sb.AppendLine($"  [{agent.NpcName}]");
                    _ = sb.AppendLine($"    State: {agent.CurrentState}");
                    _ = sb.AppendLine($"    Manual: {agent.IsManuallyOverridden}");
                    _ = sb.AppendLine($"    Priority: {agent.PriorityScore:F2}");
                    _ = sb.AppendLine($"    Friendship: {agent.FriendshipLevel:F0}");
                    _ = sb.AppendLine($"    Last Updated: {agent.LastUpdated:HH:mm:ss UTC}");
                }
            }
        }

        // issue #25：降级项在 status 一屏可见（初始化分段失败 / Harmony 补丁回退 / 整体降级）。
        _ = sb.AppendLine($"=== Degraded features: {(DegradedFeatures.Any ? "" : "none")} ===");
        if (DegradedFeatures.Any)
        {
            _ = sb.AppendLine(DegradedFeatures.Describe());
        }

        return CommandResult.Ok(sb.ToString().TrimEnd());
    }

    private CommandResult HandleForceDecision(string[] args)
    {
        if (args.Length == 0)
        {
            return CommandResult.Fail("Usage: ValleyAgent_force_decision [npc_name]");
        }

        var npcName = args[0];

        if (_agentManager != null && !_agentManager.IsAllocated(npcName))
        {
            return CommandResult.Fail($"NPC '{npcName}' is not currently allocated as an Agent.");
        }

        if (ForceDecisionCallback == null)
        {
            return CommandResult.Fail("Force decision handler not configured.");
        }

        // Note: ForceDecisionCallback is async, but ConsoleCommands returns sync CommandResult.
        // The caller (ModEntry) should handle the async invocation if needed.
        // For now, we return an informational message and the caller can trigger the async flow.
        return CommandResult.Ok($"Requesting forced LLM decision for '{npcName}'... (async callback invoked)");
    }

    private CommandResult HandleResetCircuit(string[] args)
    {
        if (_circuitBreaker == null)
        {
            return CommandResult.Fail("CircuitBreaker not available.");
        }

        var previousState = _circuitBreaker.CurrentState;
        _circuitBreaker.Reset();
        return CommandResult.Ok($"CircuitBreaker reset: {previousState} -> CLOSED");
    }

    private CommandResult HandleTokenUsage(string[] args)
    {
        if (_debugLogger == null)
        {
            return CommandResult.Fail("DebugLogger not available.");
        }

        var summary = _debugLogger.GetTokenUsageSummary();
        return CommandResult.Ok(summary);
    }

    private CommandResult HandleDebug(string[] args)
    {
        if (_config == null)
        {
            return CommandResult.Fail("ModConfig not available.");
        }

        if (args.Length == 0)
        {
            return CommandResult.Ok($"Debug mode is currently: {(_config.DebugMode ? "ON" : "OFF")}");
        }

        var toggle = args[0].ToLowerInvariant();
        switch (toggle)
        {
            case "on":
            case "true":
            case "1":
                _config.DebugMode = true;
                if (_debugLogger != null)
                {
                    _debugLogger.MinimumLevel = LogLevel.Debug;
                }

                return CommandResult.Ok("Debug mode enabled. Log level set to Debug.");

            case "off":
            case "false":
            case "0":
                _config.DebugMode = false;
                if (_debugLogger != null)
                {
                    _debugLogger.MinimumLevel = LogLevel.Info;
                }

                return CommandResult.Ok("Debug mode disabled. Log level set to Info.");

            default:
                return CommandResult.Fail($"Invalid argument '{args[0]}'. Use 'on' or 'off'.");
        }
    }

    private CommandResult HandleAllocate(string[] args)
    {
        if (args.Length == 0)
        {
            return CommandResult.Fail("Usage: ValleyAgent_allocate [npc_name]");
        }

        if (AllocateAgentCallback == null)
        {
            return CommandResult.Fail("Allocate handler not configured.");
        }

        var npcName = args[0];
        return AllocateAgentCallback(npcName);
    }

    private CommandResult HandleDeallocate(string[] args)
    {
        if (args.Length == 0)
        {
            return CommandResult.Fail("Usage: ValleyAgent_deallocate [npc_name]");
        }

        if (DeallocateAgentCallback == null)
        {
            return CommandResult.Fail("Deallocate handler not configured.");
        }

        var npcName = args[0];
        return DeallocateAgentCallback(npcName);
    }

    private CommandResult HandleChat(string[] args)
    {
        if (args.Length < 2)
        {
            return CommandResult.Fail("Usage: ValleyAgent_chat [npc_name] [message]");
        }

        if (ChatCallback == null)
        {
            return CommandResult.Fail("Chat handler not configured.");
        }

        var npcName = args[0];
        var message = string.Join(" ", args[1..]);

        // Fire-and-forget async; return immediately with status
        _ = Task.Run(async () => await ChatCallback(npcName, message).ConfigureAwait(false));

        return CommandResult.Ok($"Sending message to {npcName}...");
    }

    private CommandResult HandleAgents(string[] args) => ListAgentsCallback == null
        ? CommandResult.Fail("List agents handler not configured.")
        : ListAgentsCallback();

    private CommandResult HandleContext(string[] args)
    {
        return args.Length == 0
            ? CommandResult.Fail("Usage: ValleyAgent_context [npc_name]")
            : ShowContextCallback == null
                ? CommandResult.Fail("Context handler not configured.")
                : ShowContextCallback(args[0]);
    }

    private CommandResult HandleTestLlm(string[] args)
    {
        if (TestLlmCallback == null)
        {
            return CommandResult.Fail("Test LLM handler not configured.");
        }

        _ = Task.Run(async () =>
        {
            var result = await TestLlmCallback().ConfigureAwait(false);
            // Result will be logged by the callback itself
        });

        return CommandResult.Ok("Testing LLM connection... check console for results.");
    }

    private CommandResult HandleReloadConfig(string[] args) => ReloadConfigCallback == null
        ? CommandResult.Fail("Config reload handler not configured.")
        : ReloadConfigCallback();

    private CommandResult HandleDiag(string[] args)
    {
        _ = args;
        return DiagReportCallback == null
            ? CommandResult.Fail("Diag report handler not configured.")
            : CommandResult.Ok(DiagReportCallback());
    }

    private CommandResult HandleHelp(string[] args) =>
        args.Length > 0 ? CommandResult.Ok(GetHelpText(args[0])) : CommandResult.Ok(GetHelpText());
}