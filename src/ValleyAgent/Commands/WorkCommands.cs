using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using ValleyAgent.Handlers;
using ValleyAgent.Navigation;
using ValleyAgent.Services;
using ValleyAgent.StateMachine;

namespace ValleyAgent.Commands;

public class HarvestCommand : AgentCommandBase
{
    private readonly FarmHandler? _farmHandler;
    private readonly IMovementService _movementService;

    public HarvestCommand(IMonitor monitor, IMovementService movementService, FarmHandler? farmHandler) : base(monitor)
    {
        _movementService = movementService;
        _farmHandler = farmHandler;
    }

    public override string CommandName
    {
        get => "harvest";
    }

    public override void Execute(NPC npc, AgentInstance agent, Dictionary<string, object> parameters,
        Func<string, string, bool, Dictionary<string, object>, Task> sendResult)
    {
#pragma warning disable IDE0007
        if (TryGetCoordinate(parameters, out var x, out int y))
#pragma warning restore IDE0007
        {
            Monitor.Log($"[CommandExecutor] {npc.Name} harvesting at ({x}, {y})", LogLevel.Debug);
            var targetTile = new Point(x, y);

            if (_farmHandler != null)
            {
                _farmHandler.SetForcedTarget(npc.Name, targetTile, "harvest");
                agent.StateMachine.ForceTransition(AgentState.FARM);

                var approachTile = GetApproachTile(npc, targetTile);
                _ = _movementService.MoveTo(npc, approachTile, MovementMode.ShortRange, Game1.ticks);
            }

            _ = sendResult(npc.Name, "harvest", true,
                new Dictionary<string, object> { ["x"] = x, ["y"] = y, ["mode"] = "targeted" });
        }
        else
        {
            Monitor.Log($"[CommandExecutor] {npc.Name} harvesting (scan mode)", LogLevel.Debug);
            agent.StateMachine.ForceTransition(AgentState.FARM);
            _ = sendResult(npc.Name, "harvest", true,
                new Dictionary<string, object> { ["mode"] = "scan" });
        }
    }
}

public class MineCommand : AgentCommandBase
{
    private readonly MineHandler? _mineHandler;
    private readonly IMovementService _movementService;

    public MineCommand(IMonitor monitor, IMovementService movementService, MineHandler? mineHandler) : base(monitor)
    {
        _movementService = movementService;
        _mineHandler = mineHandler;
    }

    public override string CommandName
    {
        get => "mine";
    }

    public override void Execute(NPC npc, AgentInstance agent, Dictionary<string, object> parameters,
        Func<string, string, bool, Dictionary<string, object>, Task> sendResult)
    {
#pragma warning disable IDE0007
        if (TryGetCoordinate(parameters, out var x, out int y))
#pragma warning restore IDE0007
        {
            Monitor.Log($"[CommandExecutor] {npc.Name} mining at ({x}, {y})", LogLevel.Debug);
            var targetTile = new Point(x, y);

            if (_mineHandler != null)
            {
                _mineHandler.SetForcedTarget(npc.Name, targetTile);
                agent.StateMachine.ForceTransition(AgentState.MINE);

                var approachTile = GetApproachTile(npc, targetTile);
                _ = _movementService.MoveTo(npc, approachTile, MovementMode.ShortRange, Game1.ticks);
            }

            _ = sendResult(npc.Name, "mine", true,
                new Dictionary<string, object> { ["x"] = x, ["y"] = y, ["mode"] = "targeted" });
        }
        else
        {
            Monitor.Log($"[CommandExecutor] {npc.Name} mining (scan mode)", LogLevel.Debug);
            agent.StateMachine.ForceTransition(AgentState.MINE);
            _ = sendResult(npc.Name, "mine", true,
                new Dictionary<string, object> { ["mode"] = "scan" });
        }
    }
}

public class ForageCommand : AgentCommandBase
{
    private readonly ForageHandler? _forageHandler;
    private readonly IMovementService _movementService;

    public ForageCommand(IMonitor monitor, IMovementService movementService, ForageHandler? forageHandler) :
        base(monitor)
    {
        _movementService = movementService;
        _forageHandler = forageHandler;
    }

    public override string CommandName
    {
        get => "forage";
    }

    public override void Execute(NPC npc, AgentInstance agent, Dictionary<string, object> parameters,
        Func<string, string, bool, Dictionary<string, object>, Task> sendResult)
    {
#pragma warning disable IDE0007
        if (TryGetCoordinate(parameters, out var x, out int y))
#pragma warning restore IDE0007
        {
            Monitor.Log($"[CommandExecutor] {npc.Name} foraging at ({x}, {y})", LogLevel.Debug);
            var targetTile = new Point(x, y);

            if (_forageHandler != null)
            {
                _forageHandler.SetForcedTarget(npc.Name, targetTile);
                agent.StateMachine.ForceTransition(AgentState.FORAGE);

                var approachTile = GetApproachTile(npc, targetTile);
                _ = _movementService.MoveTo(npc, approachTile, MovementMode.ShortRange, Game1.ticks);
            }

            _ = sendResult(npc.Name, "forage", true,
                new Dictionary<string, object> { ["x"] = x, ["y"] = y, ["mode"] = "targeted" });
        }
        else
        {
            Monitor.Log($"[CommandExecutor] {npc.Name} foraging (scan mode)", LogLevel.Debug);
            agent.StateMachine.ForceTransition(AgentState.FORAGE);
            _ = sendResult(npc.Name, "forage", true,
                new Dictionary<string, object> { ["mode"] = "scan" });
        }
    }
}

public class WaterCommand : AgentCommandBase
{
    private readonly FarmHandler? _farmHandler;
    private readonly IMovementService _movementService;

    public WaterCommand(IMonitor monitor, IMovementService movementService, FarmHandler? farmHandler) : base(monitor)
    {
        _movementService = movementService;
        _farmHandler = farmHandler;
    }

    public override string CommandName
    {
        get => "water";
    }

    public override void Execute(NPC npc, AgentInstance agent, Dictionary<string, object> parameters,
        Func<string, string, bool, Dictionary<string, object>, Task> sendResult)
    {
#pragma warning disable IDE0007
        if (!TryGetCoordinate(parameters, out var x, out int y))
#pragma warning restore IDE0007
        {
            Monitor.Log($"[CommandExecutor] Water requires x,y coordinates for {npc.Name}", LogLevel.Warn);
            _ = sendResult(npc.Name, "water", false,
                new Dictionary<string, object> { ["error"] = "Missing x,y coordinates for water command" });
            return;
        }

        Monitor.Log($"[CommandExecutor] {npc.Name} watering at ({x}, {y})", LogLevel.Debug);
        var targetTile = new Point(x, y);

        if (_farmHandler != null)
        {
            _farmHandler.SetForcedTarget(npc.Name, targetTile, "water");
            agent.StateMachine.ForceTransition(AgentState.FARM);

            var approachTile = GetApproachTile(npc, targetTile);
            _ = _movementService.MoveTo(npc, approachTile, MovementMode.ShortRange, Game1.ticks);
        }

        _ = sendResult(npc.Name, "water", true,
            new Dictionary<string, object> { ["x"] = x, ["y"] = y });
    }
}

public class AttackCommand : AgentCommandBase
{
    private readonly FightHandler? _fightHandler;

    public AttackCommand(IMonitor monitor, FightHandler? fightHandler) : base(monitor)
    {
        _fightHandler = fightHandler;
    }

    public override string CommandName
    {
        get => "attack";
    }

    public override void Execute(NPC npc, AgentInstance agent, Dictionary<string, object> parameters,
        Func<string, string, bool, Dictionary<string, object>, Task> sendResult)
    {
        var targetId = GetParamAny(parameters, "target_id", "targetId") as string;
        if (!string.IsNullOrWhiteSpace(targetId) && _fightHandler != null)
        {
            _fightHandler.SetForcedTargetById(npc.Name, targetId);
        }

        Monitor.Log($"[CommandExecutor] {npc.Name} attacking (target: {targetId ?? "auto"})", LogLevel.Debug);
        agent.StateMachine.ForceTransition(AgentState.FIGHT);
        _ = sendResult(npc.Name, "attack", true,
            new Dictionary<string, object> { ["message"] = "Entering FIGHT state", ["target_id"] = targetId ?? "" });
    }
}