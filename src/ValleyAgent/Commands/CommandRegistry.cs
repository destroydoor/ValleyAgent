using System;
using System.Collections.Generic;
using StardewModdingAPI;

namespace ValleyAgent.Commands;

public class CommandRegistry
{
    private readonly Dictionary<string, IAgentCommand> _commands = new(StringComparer.OrdinalIgnoreCase);
    private readonly IMonitor _monitor;

    public CommandRegistry(IMonitor monitor)
    {
        _monitor = monitor;
    }

    public void Register(IAgentCommand command)
    {
        _commands[command.CommandName] = command;
        foreach (var alias in command.Aliases)
        {
            _commands[alias] = command;
        }

        _monitor.Log($"[CommandRegistry] Registered command: {command.CommandName}" +
                     (command.Aliases.Count > 0 ? $" (aliases: {string.Join(", ", command.Aliases)})" : ""),
            LogLevel.Debug);
    }

    public IAgentCommand? GetCommand(string actionName) =>
        _commands.TryGetValue(actionName, out var command) ? command : null;

    public bool HasCommand(string actionName) => _commands.ContainsKey(actionName);
}