using System;
using System.Security.Cryptography;
using ValleyAgent.StateMachine;
using ValleyAgent.Utils;

namespace ValleyAgent.Controllers;

/// <summary>
///     Controller for NPC idle/thinking behavior. Handles the IDLE agent state.
///     Provides natural idle activities (wandering, looking around, emoting, staying still)
///     with configurable weights and boundaries. Supports an override thinking state.
/// </summary>
public class IdleController : IAgentController
{
    private int _activityDurationTicks;
    private int _activityStartTick;
    private int _currentX;
    private int _currentY;
    private int _nextActivityTick;

    private int _startX;

    private int _startY;

    // Configuration
    public int IdleRadiusTiles { get; set; } = 5;
    public int MinActivityIntervalSeconds { get; set; } = 3;
    public int MaxActivityIntervalSeconds { get; set; } = 5;
    public double WanderWeight { get; set; } = 0.40;
    public double LookAroundWeight { get; set; } = 0.20;
    public double EmoteWeight { get; set; } = 0.20;
    public double StayStillWeight { get; set; } = 0.20;

    /// <summary>
    ///     Gets the current idle activity for inspection.
    /// </summary>
    public IdleActivity CurrentActivity { get; private set; }

    /// <summary>
    ///     Gets the current facing direction (up, down, left, right).
    /// </summary>
    public string CurrentFacing { get; private set; } = "down";

    /// <summary>
    ///     Gets the current emote string, if any.
    /// </summary>
    public string CurrentEmote { get; private set; } = "";

    /// <summary>
    ///     Gets whether the agent is currently in thinking mode.
    /// </summary>
    public bool IsThinking { get; private set; }

    // IAgentController
    public string ControllerName
    {
        get => "Idle";
    }

    public bool IsActive { get; private set; }

    public event EventHandler<string>? OnActionStarted;
    public event EventHandler<string>? OnActionCompleted;
    public event EventHandler<string>? OnActionFailed;

    public bool CanHandle(AgentState state) => state == AgentState.IDLE;

    public void Start()
    {
        if (IsActive)
        {
            return;
        }

        IsActive = true;
        _startX = _currentX;
        _startY = _currentY;
        _activityStartTick = 0;

        if (IsThinking)
        {
            CurrentActivity = IdleActivity.Thinking;
            CurrentEmote = "thinking";
            OnActionStarted?.Invoke(this, "Thinking");
        }
        else
        {
            PickNewActivity(0);
        }
    }

    public void Stop()
    {
        if (!IsActive)
        {
            return;
        }

        if (CurrentActivity != IdleActivity.None)
        {
            OnActionCompleted?.Invoke(this, $"Idle activity '{CurrentActivity}' stopped");
        }

        IsActive = false;
        CurrentActivity = IdleActivity.None;
        CurrentEmote = "";
    }

    public void Update(int ticks)
    {
        if (!IsActive)
        {
            return;
        }

        // Thinking state overrides all normal idle behavior
        if (IsThinking)
        {
            CurrentActivity = IdleActivity.Thinking;
            CurrentEmote = "thinking";
            return;
        }

        // Check if it's time to pick a new activity
        if (ticks >= _nextActivityTick)
        {
            // Complete previous activity if any
            if (CurrentActivity != IdleActivity.None)
            {
                OnActionCompleted?.Invoke(this, $"Completed {CurrentActivity}");
            }

            // If out of bounds, force return toward center
            if (IsOutOfBounds())
            {
                StartReturnToCenter();
            }
            else
            {
                PickNewActivity(ticks);
            }
        }
        else
        {
            // Continue current activity
            ExecuteCurrentActivity();
        }
    }

    /// <summary>
    ///     Sets or clears the thinking state. When thinking, normal idle behavior
    ///     is overridden with a continuous thinking emote.
    /// </summary>
    public void SetThinking(bool isThinking)
    {
        if (IsThinking == isThinking)
        {
            return;
        }

        IsThinking = isThinking;

        if (IsActive)
        {
            if (IsThinking)
            {
                CurrentActivity = IdleActivity.Thinking;
                CurrentEmote = "thinking";
                OnActionStarted?.Invoke(this, "Thinking");
            }
            else
            {
                OnActionCompleted?.Invoke(this, "Thinking ended");
                PickNewActivity(0);
            }
        }
    }

    /// <summary>
    ///     Sets the agent's current tile position. Should be called by the owning system
    ///     before Start() and whenever the agent moves.
    /// </summary>
    public void SetPosition(int x, int y)
    {
        _currentX = x;
        _currentY = y;
    }

    private void PickNewActivity(int ticks)
    {
        // 使用加密安全的随机数生成器生成 [0, 1) 范围内的 double
        var roll = RandomNumberGenerator.GetInt32(0, 1_000_000) / 1_000_000.0;
        var cumulative = 0.0;

        IdleActivity chosen;
        cumulative += WanderWeight;
        if (roll < cumulative)
        {
            chosen = IdleActivity.Wander;
        }
        else
        {
            cumulative += LookAroundWeight;
            if (roll < cumulative)
            {
                chosen = IdleActivity.LookAround;
            }
            else
            {
                cumulative += EmoteWeight;
                chosen = roll < cumulative ? IdleActivity.Emote : IdleActivity.StayStill;
            }
        }

        StartActivity(chosen, ticks);
    }

    private void StartActivity(IdleActivity activity, int ticks)
    {
        CurrentActivity = activity;
        _activityStartTick = ticks;
        _activityDurationTicks = GetActivityDuration(activity);
        _nextActivityTick = ticks + _activityDurationTicks;

        switch (activity)
        {
            case IdleActivity.Wander:
                var distance = RandomNumberGenerator.GetInt32(1, 4); // 1-3 tiles
                var dir = RandomNumberGenerator.GetInt32(4);
                int dx = 0, dy = 0;
                switch (dir)
                {
                    case 0:
                        dy = -distance;
                        CurrentFacing = "up";
                        break;
                    case 1:
                        dy = distance;
                        CurrentFacing = "down";
                        break;
                    case 2:
                        dx = -distance;
                        CurrentFacing = "left";
                        break;
                    case 3:
                        dx = distance;
                        CurrentFacing = "right";
                        break;
                }

                // Check boundary before committing
                var targetX = _currentX + dx;
                var targetY = _currentY + dy;
                if (!IsWithinRadius(targetX, targetY))
                {
                    // Clamp to boundary
                    targetX = ClampToRadius(targetX, _startX);
                    targetY = ClampToRadius(targetY, _startY);
                    dx = targetX - _currentX;
                    dy = targetY - _currentY;
                }

                if (dx == 0 && dy == 0)
                {
                    OnActionFailed?.Invoke(this, "Wander blocked: already at boundary edge");
                    // Fallback to staying still so we never get stuck
                    CurrentActivity = IdleActivity.StayStill;
                    CurrentEmote = "";
                    OnActionStarted?.Invoke(this, "Staying still (wander fallback)");
                    break;
                }

                _currentX = targetX;
                _currentY = targetY;
                OnActionStarted?.Invoke(this, $"Wandering {CurrentFacing} {Math.Abs(dx) + Math.Abs(dy)} tiles");
                break;

            case IdleActivity.LookAround:
                string[] directions = { "up", "down", "left", "right" };
                CurrentFacing = directions[RandomNumberGenerator.GetInt32(directions.Length)];
                OnActionStarted?.Invoke(this, $"Looking {CurrentFacing}");
                break;

            case IdleActivity.Emote:
                string[] emotes = { "happy", "sad", "thinking", "question", "exclamation" };
                CurrentEmote = emotes[RandomNumberGenerator.GetInt32(emotes.Length)];
                OnActionStarted?.Invoke(this, $"Emote: {CurrentEmote}");
                break;

            case IdleActivity.StayStill:
                CurrentEmote = "";
                OnActionStarted?.Invoke(this, "Staying still");
                break;

            case IdleActivity.ReturnToCenter:
                OnActionStarted?.Invoke(this, "Returning to idle center");
                break;

            // 未使用的枚举值，无需特殊处理
            case IdleActivity.None:
            case IdleActivity.Thinking:
            default:
                break;
        }
    }

    private static void ExecuteCurrentActivity()
    {
        // Stub for per-tick activity execution.
        // Actual sprite/animation updates would be handled by the rendering system.
        // Movement is instantaneous in this controller; pathing would be added here.
    }

    private void StartReturnToCenter()
    {
        CurrentActivity = IdleActivity.ReturnToCenter;

        // Calculate one step toward center
        var dx = Math.Sign(_startX - _currentX);
        var dy = Math.Sign(_startY - _currentY);

        if (dx != 0)
        {
            _currentX += dx;
            CurrentFacing = dx > 0 ? "right" : "left";
        }
        else if (dy != 0)
        {
            _currentY += dy;
            CurrentFacing = dy > 0 ? "down" : "up";
        }

        _activityStartTick = 0;
        _activityDurationTicks = 60; // ~1 second at 60 ticks/sec
        _nextActivityTick = _activityStartTick + _activityDurationTicks;

        OnActionStarted?.Invoke(this, "Returning toward idle center");
    }

    private bool IsOutOfBounds() => !IsWithinRadius(_currentX, _currentY);

    private bool IsWithinRadius(int x, int y) =>
        Math.Abs(x - _startX) <= IdleRadiusTiles && Math.Abs(y - _startY) <= IdleRadiusTiles;

    private int ClampToRadius(int value, int center)
    {
        return value > center + IdleRadiusTiles
            ? center + IdleRadiusTiles
            : value < center - IdleRadiusTiles
                ? center - IdleRadiusTiles
                : value;
    }

    private int GetActivityDuration(IdleActivity activity)
    {
        // Assume ~60 ticks per second
        var seconds = activity switch
        {
            IdleActivity.Wander => RandomNumberGenerator.GetInt32(2, 4),
            IdleActivity.LookAround => RandomNumberGenerator.GetInt32(1, 3),
            IdleActivity.Emote => RandomNumberGenerator.GetInt32(2, 4),
            IdleActivity.StayStill => RandomNumberGenerator.GetInt32(2, 5),
            IdleActivity.ReturnToCenter => 1,
            IdleActivity.Thinking => 1,
            IdleActivity.None => RandomNumberGenerator.GetInt32(MinActivityIntervalSeconds,
                MaxActivityIntervalSeconds + 1),
            _ => RandomNumberGenerator.GetInt32(MinActivityIntervalSeconds, MaxActivityIntervalSeconds + 1)
        };

        return seconds * 60 / DebugFlags.GameSpeedMultiplier;
    }
}

/// <summary>
///     Possible idle activities an agent can perform.
/// </summary>
public enum IdleActivity
{
    /// <summary>No active activity.</summary>
    None,

    /// <summary>Move a short distance in a random direction.</summary>
    Wander,

    /// <summary>Change facing direction.</summary>
    LookAround,

    /// <summary>Display a random emote.</summary>
    Emote,

    /// <summary>Do nothing for a few seconds.</summary>
    StayStill,

    /// <summary>Return toward the idle center because boundaries were exceeded.</summary>
    ReturnToCenter,

    /// <summary>Override state for thinking mode.</summary>
    Thinking
}