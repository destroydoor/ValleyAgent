using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using ValleyAgent.Navigation;
using ValleyAgent.Services;
using ValleyAgent.StateMachine;

namespace ValleyAgent.Commands;

public class MoveToCommand : AgentCommandBase
{
    private readonly IMovementService _movementService;
    private readonly AgentNavigator? _navigator;

    public MoveToCommand(IMonitor monitor, IMovementService movementService, AgentNavigator? navigator = null) :
        base(monitor)
    {
        _movementService = movementService;
        _navigator = navigator;
    }

    public override string CommandName
    {
        get => "move_to";
    }

    public override void Execute(NPC npc, AgentInstance agent, Dictionary<string, object> parameters,
        Func<string, string, bool, Dictionary<string, object>, Task> sendResult)
    {
#pragma warning disable IDE0007
        if (TryGetCoordinate(parameters, out var fx, out float fy))
#pragma warning restore IDE0007
        {
            _ = Convert.ToInt32(GetParamAny(parameters, "speed", "speed", 2));
            var modeStr = (GetParamAny(parameters, "mode", "mode", "short") as string)?.ToLowerInvariant() ?? "short";
            var mapName = GetParamAny(parameters, "map_name", "mapName") as string ?? "";

            if (!string.IsNullOrEmpty(mapName) && npc.currentLocation?.NameOrUniqueName != mapName)
            {
                _navigator?.NavigateToTaskLocation(npc, new[] { mapName }, Game1.ticks, out _, false);
            }

            Monitor.Log($"[CommandExecutor] Moving {npc.Name} to ({fx}, {fy})", LogLevel.Debug);

            var mode = modeStr switch
            {
                "long" => MovementMode.LongRange,
                "immediate" => MovementMode.Immediate,
                _ => MovementMode.ShortRange
            };

            var targetPoint = new Point((int)fx, (int)fy);
            var result = _movementService.MoveTo(npc, targetPoint, mode, Game1.ticks);

            _ = sendResult(npc.Name, "move_to", result is not MoveResult.InvalidNpc and not MoveResult.Frozen,
                new Dictionary<string, object>
                {
                    ["x"] = fx,
                    ["y"] = fy,
                    ["result"] = result.ToString()
                });
        }
        else
        {
            _ = sendResult(npc.Name, "move_to", false,
                new Dictionary<string, object> { ["error"] = "Missing x,y coordinates" });
        }
    }
}

public class FollowCommand : AgentCommandBase
{
    public FollowCommand(IMonitor monitor) : base(monitor)
    {
    }

    public override string CommandName
    {
        get => "follow_player";
    }

    public override IReadOnlyList<string> Aliases
    {
        get => new[] { "follow" };
    }

    public override void Execute(NPC npc, AgentInstance agent, Dictionary<string, object> parameters,
        Func<string, string, bool, Dictionary<string, object>, Task> sendResult)
    {
        var rawDist = GetParamAny(parameters, "distance", "dist");
        var distance = rawDist != null ? Convert.ToInt32(rawDist) : 2;
        Monitor.Log($"[CommandExecutor] {npc.Name} following player (distance: {distance})", LogLevel.Debug);
        agent.StateMachine.ForceTransition(AgentState.FOLLOW);
        _ = sendResult(npc.Name, "follow_player", true,
            new Dictionary<string, object> { ["message"] = "Following player", ["distance"] = distance });
    }
}

public class StopCommand : AgentCommandBase
{
    private readonly ValleyAgent.Core.AgentTickLoop? _agentTickLoop;
    private readonly IMovementService _movementService;

    public StopCommand(IMonitor monitor, IMovementService movementService,
        ValleyAgent.Core.AgentTickLoop? agentTickLoop = null) : base(monitor)
    {
        _movementService = movementService;
        _agentTickLoop = agentTickLoop;
    }

    public override string CommandName
    {
        get => "stop";
    }

    public override void Execute(NPC npc, AgentInstance agent, Dictionary<string, object> parameters,
        Func<string, string, bool, Dictionary<string, object>, Task> sendResult)
    {
        Monitor.Log($"[CommandExecutor] Stopping {npc.Name}", LogLevel.Debug);
        _movementService.Stop(npc, "command-stop");
        // 不手动设 followSchedule——走 vanilla-release 流程：
        // ForceTransition(IDLE) 成功后调 ReleaseToVanillaNow，
        // 下一 tick 由 AgentTickLoop released 分支接管（BeginWalkBack → FinalizeVanillaRelease），
        // 避免与 IdleWanderHandler 抢 controller。
        var transitioned = agent.StateMachine.ForceTransition(AgentState.IDLE);
        if (transitioned)
        {
            _agentTickLoop?.ReleaseToVanillaNow(npc.Name);
        }

        _ = sendResult(npc.Name, "stop", true,
            new Dictionary<string, object> { ["message"] = "Stopped" });
    }
}

public class WaitCommand : AgentCommandBase
{
    private readonly IMovementService _movementService;

    public WaitCommand(IMonitor monitor, IMovementService movementService) : base(monitor)
    {
        _movementService = movementService;
    }

    public override string CommandName
    {
        get => "wait";
    }

    public override void Execute(NPC npc, AgentInstance agent, Dictionary<string, object> parameters,
        Func<string, string, bool, Dictionary<string, object>, Task> sendResult)
    {
        var durationMs = Convert.ToInt32(GetParamAny(parameters, "duration_ms", "durationMs", 1000));
        Monitor.Log($"[CommandExecutor] {npc.Name} waiting for {durationMs}ms", LogLevel.Debug);
        _movementService.Stop(npc, "command-wait");
        _ = sendResult(npc.Name, "wait", true,
            new Dictionary<string, object> { ["duration_ms"] = durationMs });
    }
}