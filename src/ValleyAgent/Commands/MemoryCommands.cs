using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using StardewModdingAPI;
using StardewValley;
using ValleyAgent.Brain;
using ValleyAgent.Services;

namespace ValleyAgent.Commands;

public class RememberCommand : AgentCommandBase
{
    public RememberCommand(IMonitor monitor) : base(monitor)
    {
    }

    public override string CommandName
    {
        get => "remember";
    }

    public override void Execute(NPC npc, AgentInstance agent, Dictionary<string, object> parameters,
        Func<string, string, bool, Dictionary<string, object>, Task> sendResult)
    {
        var text = GetParamAny(parameters, "text") as string;
        var category = (GetParamAny(parameters, "category") as string)?.ToLowerInvariant() ?? "life_event";
        var emotionalWeight = (GetParamAny(parameters, "emotional_weight") as string)?.ToLowerInvariant() ?? "joy";

        if (string.IsNullOrWhiteSpace(text))
        {
            _ = sendResult(npc.Name, "remember", false,
                new Dictionary<string, object> { ["error"] = "Missing 'text' parameter" });
            return;
        }

        Monitor.Log(
            $"[CommandExecutor] {npc.Name} remembering: {(text.Length > 80 ? text[..80] : text)} [{category}/{emotionalWeight}]",
            LogLevel.Debug);

        var importance = emotionalWeight switch
        {
            "love" or "pride" => 8.0,
            "joy" or "sorrow" => 6.0,
            "anger" or "fear" => 5.0,
            "surprise" => 4.0,
            _ => 5.0
        };

        var entryType = category switch
        {
            "relationship" => MemoryEntryType.Conversation,
            "life_event" => MemoryEntryType.Event,
            "trauma" => MemoryEntryType.Emotion,
            "achievement" => MemoryEntryType.Task,
            _ => MemoryEntryType.Generic
        };

        var tags = new List<string> { category, emotionalWeight, "significant" };
        if (importance >= 4.0)
        {
            agent.Brain.AddSignificantMemory(text, importance, entryType.ToString());
        }
        else
        {
            agent.Brain.AddMemory(text, importance, entryType, npc.currentLocation?.NameOrUniqueName ?? "", tags);
        }

        _ = sendResult(npc.Name, "remember", true,
            new Dictionary<string, object>
                { ["text"] = text, ["category"] = category, ["emotional_weight"] = emotionalWeight });
    }
}

public class ForgetCommand : AgentCommandBase
{
    public ForgetCommand(IMonitor monitor) : base(monitor)
    {
    }

    public override string CommandName
    {
        get => "forget";
    }

    public override void Execute(NPC npc, AgentInstance agent, Dictionary<string, object> parameters,
        Func<string, string, bool, Dictionary<string, object>, Task> sendResult)
    {
        var memoryText = GetParamAny(parameters, "memory_text") as string;
        var reason = GetParamAny(parameters, "reason") as string;

        if (string.IsNullOrWhiteSpace(memoryText))
        {
            _ = sendResult(npc.Name, "forget", false,
                new Dictionary<string, object> { ["error"] = "Missing 'memory_text' parameter" });
            return;
        }

        Monitor.Log(
            $"[CommandExecutor] {npc.Name} forgetting: {(memoryText.Length > 80 ? memoryText[..80] : memoryText)} (reason: {reason})",
            LogLevel.Debug);

        var removed = 0;
        var toRemove = agent.Brain.ShortTermMemories
            .Where(m => m.Text.Contains(memoryText, StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var m in toRemove)
        {
            _ = agent.Brain.ShortTermMemories.Remove(m);
            removed++;
        }

        toRemove = agent.Brain.LongTermMemories
            .Where(m => m.Text.Contains(memoryText, StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var m in toRemove)
        {
            _ = agent.Brain.LongTermMemories.Remove(m);
            removed++;
        }

        _ = sendResult(npc.Name, "forget", true,
            new Dictionary<string, object>
                { ["memory_text"] = memoryText, ["removed_count"] = removed, ["reason"] = reason ?? "" });
    }
}