using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using StardewModdingAPI;
using StardewValley;
using ValleyAgent.Services;
using ValleyAgent.StateMachine;

namespace ValleyAgent.Commands;

public class SetStateCommand : AgentCommandBase
{
    public SetStateCommand(IMonitor monitor) : base(monitor)
    {
    }

    public override string CommandName
    {
        get => "set_state";
    }

    public override void Execute(NPC npc, AgentInstance agent, Dictionary<string, object> parameters,
        Func<string, string, bool, Dictionary<string, object>, Task> sendResult)
    {
        if (parameters.TryGetValue("state", out var stateObj) && stateObj is string stateStr)
        {
            if (Enum.TryParse<AgentState>(stateStr, true, out var state))
            {
                Monitor.Log($"[CommandExecutor] Setting {npc.Name} state to {state}", LogLevel.Debug);
                // 不使用 fromDecision: true，让 LLM 工具调用也遵守最小状态持续时间约束
                // fromDecision 仅用于 TS Agent Server 决策引擎的输出，不应被 LLM 工具调用绕过
                var success = agent.StateMachine.ForceTransition(state, fromDecision: false);
                if (success)
                {
                    _ = sendResult(npc.Name, "set_state", true,
                        new Dictionary<string, object> { ["state"] = state.ToString() });
                }
                else
                {
                    _ = sendResult(npc.Name, "set_state", false,
                        new Dictionary<string, object>
                        {
                            ["error"] = $"State transition to {state} blocked (minimum duration not met or not allowed)"
                        });
                }
            }
            else
            {
                Monitor.Log($"[CommandExecutor] Invalid state '{stateStr}' for {npc.Name}", LogLevel.Warn);
                _ = sendResult(npc.Name, "set_state", false,
                    new Dictionary<string, object> { ["error"] = $"Invalid state: {stateStr}" });
            }
        }
        else
        {
            _ = sendResult(npc.Name, "set_state", false,
                new Dictionary<string, object> { ["error"] = "Missing 'state' parameter" });
        }
    }
}