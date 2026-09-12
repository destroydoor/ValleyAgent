using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using ValleyAgent.StateMachine;

namespace ValleyAgent.Controllers;

/// <summary>
///     Represents a potential combat target in the game world.
///     Game-agnostic data container used by <see cref="FightController" />.
/// </summary>
public class CombatTarget
{
    /// <summary>
    ///     Unique identifier for this target.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    ///     X-coordinate of the target's position in the game world.
    /// </summary>
    public int PositionX { get; set; }

    /// <summary>
    ///     Y-coordinate of the target's position in the game world.
    /// </summary>
    public int PositionY { get; set; }

    /// <summary>
    ///     Whether this target is hostile to the agent.
    /// </summary>
    public bool IsHostile { get; set; }

    /// <summary>
    ///     Current health percentage of the target (0-100).
    /// </summary>
    public float HealthPercent { get; set; } = 100f;
}

/// <summary>
///     Interface for game-specific combat environment queries and actions.
///     The game layer implements this to bridge the game-agnostic controller to the actual game world.
/// </summary>
public interface ICombatEnvironment
{
    /// <summary>
    ///     Gets all targets near the agent.
    /// </summary>
    public IEnumerable<CombatTarget> GetNearbyTargets();

    /// <summary>
    ///     Gets the agent's current position in the game world.
    /// </summary>
    public (int X, int Y) GetAgentPosition();

    /// <summary>
    ///     Gets the agent's current health as a percentage (0-100).
    /// </summary>
    public float GetAgentHealthPercent();

    /// <summary>
    ///     Moves the agent toward the specified position.
    /// </summary>
    public void MoveToward((int X, int Y) targetPosition);

    /// <summary>
    ///     Moves the agent away from the specified position.
    /// </summary>
    public void MoveAwayFrom((int X, int Y) targetPosition);

    /// <summary>
    ///     Executes an attack against the specified target.
    ///     Actual damage and hit resolution is handled by the game layer.
    /// </summary>
    public void PerformAttack(CombatTarget target);

    /// <summary>
    ///     Stops the agent's current movement.
    /// </summary>
    public void StopMovement();

    /// <summary>
    ///     Checks whether the player is still in the current area.
    /// </summary>
    public bool IsPlayerInArea();
}

/// <summary>
///     NPC combat behavior controller for the <see cref="AgentState.FIGHT" /> state.
///     Implements game-agnostic combat logic: target acquisition, attack cooldowns,
///     retreat on low health, and graceful failure when no targets are available.
/// </summary>
public class FightController : IAgentController
{
    private static readonly TimeSpan NoTargetTimeout = TimeSpan.FromSeconds(5);
    private readonly Stopwatch _attackCooldownTimer;
    private readonly Stopwatch _combatDurationTimer;
    private readonly ICombatEnvironment _environment;
    private readonly Stopwatch _noTargetTimer;
    private CombatTarget? _currentTarget;
    private bool _isRetreating;

    /// <summary>
    ///     Creates a new <see cref="FightController" />.
    /// </summary>
    /// <param name="environment">The combat environment interface that bridges to the game world.</param>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="environment" /> is null.</exception>
    public FightController(ICombatEnvironment environment)
    {
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _attackCooldownTimer = new Stopwatch();
        _noTargetTimer = new Stopwatch();
        _combatDurationTimer = new Stopwatch();
    }

    /// <summary>
    ///     Maximum distance (in tiles) to detect hostile targets.
    ///     Default: 10 tiles.
    /// </summary>
    public float DetectionRange { get; set; } = 10f;

    /// <summary>
    ///     Maximum distance (in tiles) at which the agent can attack.
    ///     Default: 1 tile.
    /// </summary>
    public float AttackRange { get; set; } = 1f;

    /// <summary>
    ///     Health percentage threshold below which the agent will retreat.
    ///     Expressed as a fraction (0.0 - 1.0). Default: 0.30 (30%).
    /// </summary>
    public float RetreatThreshold { get; set; } = 0.30f;

    /// <summary>
    ///     Minimum time between attacks.
    ///     Default: 1 second.
    /// </summary>
    public TimeSpan AttackCooldown { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    ///     Damage dealt per simulated attack. Used for tracking total damage output.
    ///     Default: 10.
    /// </summary>
    public float DamagePerAttack { get; set; } = 10f;

    /// <summary>
    ///     Number of attacks performed during the current combat session.
    /// </summary>
    public int AttackCount { get; private set; }

    /// <summary>
    ///     Total simulated damage dealt during the current combat session.
    /// </summary>
    public float TotalDamageDealt
    {
        get => AttackCount * DamagePerAttack;
    }

    /// <inheritdoc />
    public string ControllerName
    {
        get => "FIGHT";
    }

    /// <inheritdoc />
    public bool IsActive { get; private set; }

    /// <inheritdoc />
    public event EventHandler<string>? OnActionStarted;

    /// <inheritdoc />
    public event EventHandler<string>? OnActionCompleted;

    /// <inheritdoc />
    public event EventHandler<string>? OnActionFailed;

    /// <inheritdoc />
    public bool CanHandle(AgentState state) => state == AgentState.FIGHT;

    /// <inheritdoc />
    /// <summary>
    ///     Initializes combat by finding the nearest hostile target.
    ///     If no target is found, begins a grace period before failing.
    /// </summary>
    public void Start()
    {
        if (IsActive)
        {
            return;
        }

        IsActive = true;
        _currentTarget = null;
        AttackCount = 0;
        _isRetreating = false;
        _attackCooldownTimer.Reset();
        _noTargetTimer.Reset();
        _combatDurationTimer.Restart();

        _currentTarget = FindNearestHostileTarget();

        if (_currentTarget != null)
        {
            OnActionStarted?.Invoke(this, $"Combat started against {_currentTarget.Id}");
        }
        else
        {
            _noTargetTimer.Start();
            OnActionStarted?.Invoke(this, "Combat started - searching for targets");
        }
    }

    /// <inheritdoc />
    /// <summary>
    ///     Clears the current target and stops movement.
    /// </summary>
    public void Stop()
    {
        if (!IsActive)
        {
            return;
        }

        IsActive = false;
        _currentTarget = null;
        _attackCooldownTimer.Stop();
        _noTargetTimer.Stop();
        _combatDurationTimer.Stop();
        _isRetreating = false;

        _environment.StopMovement();
    }

    /// <inheritdoc />
    /// <summary>
    ///     Processes combat logic for the current tick.
    /// </summary>
    /// <param name="ticks">Number of ticks since the game started.</param>
    public void Update(int ticks)
    {
        if (!IsActive)
        {
            return;
        }

        // If player leaves the area, stop fighting gracefully
        if (!_environment.IsPlayerInArea())
        {
            OnActionCompleted?.Invoke(this, "Player left");
            Stop();
            return;
        }

        // Validate existing target (may have died or become non-hostile)
        if (_currentTarget != null)
        {
            _currentTarget = ValidateTarget(_currentTarget);
        }

        // Attempt to acquire a new target if none is held
        _currentTarget ??= FindNearestHostileTarget();

        // No valid target available
        if (_currentTarget == null)
        {
            if (!_noTargetTimer.IsRunning)
            {
                _noTargetTimer.Start();
            }
            else if (_noTargetTimer.Elapsed > NoTargetTimeout)
            {
                OnActionFailed?.Invoke(this, "No hostile targets");
                Stop();
            }

            return;
        }

        // Reset the no-target timer since we have a valid target
        if (_noTargetTimer.IsRunning)
        {
            _noTargetTimer.Stop();
            _noTargetTimer.Reset();
        }

        var agentPos = _environment.GetAgentPosition();
        var agentHealth = _environment.GetAgentHealthPercent();
        var targetPos = (_currentTarget.PositionX, _currentTarget.PositionY);
        var distance = CalculateDistance(agentPos, targetPos);

        // Retreat if health is below threshold
        if (agentHealth < RetreatThreshold * 100f)
        {
            if (!_isRetreating)
            {
                _isRetreating = true;
                OnActionStarted?.Invoke(this, $"Retreating from {_currentTarget.Id} - health low ({agentHealth:F1}%)");
            }

            _environment.MoveAwayFrom(targetPos);
            return;
        }

        if (_isRetreating)
        {
            _isRetreating = false;
        }

        // Attack if in range, otherwise move toward target
        if (distance <= AttackRange)
        {
            if (!_attackCooldownTimer.IsRunning || _attackCooldownTimer.Elapsed >= AttackCooldown)
            {
                _environment.PerformAttack(_currentTarget);
                AttackCount++;
                _attackCooldownTimer.Restart();
            }
        }
        else
        {
            _environment.MoveToward(targetPos);
        }
    }

    /// <summary>
    ///     Finds the nearest hostile target within detection range.
    /// </summary>
    /// <returns>The nearest hostile target, or null if none are found.</returns>
    private CombatTarget? FindNearestHostileTarget()
    {
        var agentPos = _environment.GetAgentPosition();

        return _environment.GetNearbyTargets()
            .Where(t => t.IsHostile)
            .Select(t => new
            {
                Target = t,
                Distance = CalculateDistance(agentPos, (t.PositionX, t.PositionY))
            })
            .Where(x => x.Distance <= DetectionRange)
            .OrderBy(x => x.Distance)
            .Select(x => x.Target)
            .FirstOrDefault();
    }

    /// <summary>
    ///     Validates that the given target still exists in the nearby targets list and is still hostile.
    /// </summary>
    /// <param name="target">The target to validate.</param>
    /// <returns>The updated target if valid; otherwise, null.</returns>
    private CombatTarget? ValidateTarget(CombatTarget target)
    {
        return _environment.GetNearbyTargets()
            .FirstOrDefault(t => t.Id == target.Id && t.IsHostile);
    }

    /// <summary>
    ///     Calculates Euclidean distance between two points.
    /// </summary>
    private static float CalculateDistance((int X, int Y) a, (int X, int Y) b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return MathF.Sqrt(dx * dx + dy * dy);
    }
}