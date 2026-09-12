using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using StardewValley;
using ValleyAgent.Services;

namespace ValleyAgent.StateMachine
{
    /// <summary>
    /// Generic state machine for agent behavior, inspired by NPC Adventures'
    /// CompanionStateMachine pattern but rewritten for .NET 6+ and game-agnostic design.
    ///
    /// Manages state transitions with validation, duration tracking, and debug logging.
    /// All transitions follow defined rules; fromDecision only bypasses minimum state duration
    /// for TS Agent Server decision results, but still respects AllowedTransitions.
    /// </summary>
    public class AgentStateMachine
    {
        private readonly Dictionary<AgentState, IAgentState> _states;
        private readonly Stopwatch _stateStopwatch;
        private readonly List<string> _transitionLog;

        /// <summary>
        /// Allowed transitions map: from state -> set of allowed target states.
        /// </summary>
        private static readonly Dictionary<AgentState, HashSet<AgentState>> AllowedTransitions = new()
        {
            [AgentState.IDLE] = new HashSet<AgentState>
            {
                AgentState.FOLLOW,
                AgentState.FIGHT,
                AgentState.FARM,
                AgentState.FORAGE,
                AgentState.MINE,
                AgentState.TALK,
                AgentState.EXECUTING_GOAL,
            },
            [AgentState.FOLLOW] = new HashSet<AgentState>
            {
                AgentState.IDLE,
                AgentState.FIGHT,
                AgentState.FARM,
                AgentState.FORAGE,
                AgentState.MINE,
                AgentState.TALK,
                AgentState.EXECUTING_GOAL,
            },
            [AgentState.FIGHT] = new HashSet<AgentState>
            {
                AgentState.IDLE,
                AgentState.FOLLOW,
                AgentState.EXECUTING_GOAL,
            },
            [AgentState.FARM] = new HashSet<AgentState>
            {
                AgentState.IDLE,
                AgentState.FOLLOW,
                AgentState.FIGHT,
                AgentState.TALK,
                AgentState.EXECUTING_GOAL,
            },
            [AgentState.FORAGE] = new HashSet<AgentState>
            {
                AgentState.IDLE,
                AgentState.FOLLOW,
                AgentState.FIGHT,
                AgentState.TALK,
                AgentState.EXECUTING_GOAL,
            },
            [AgentState.MINE] = new HashSet<AgentState>
            {
                AgentState.IDLE,
                AgentState.FOLLOW,
                AgentState.FIGHT,
                AgentState.TALK,
                AgentState.EXECUTING_GOAL,
            },
            [AgentState.TALK] = new HashSet<AgentState>
            {
                AgentState.IDLE,
                AgentState.FOLLOW,
                AgentState.FIGHT,
                AgentState.EXECUTING_GOAL,
            },
            [AgentState.CHOP] = new HashSet<AgentState>
            {
                AgentState.IDLE,
                AgentState.FOLLOW,
                AgentState.FIGHT,
                AgentState.EXECUTING_GOAL,
            },
            // 执行态：任何状态可被 set_goal 接管（spec §1.3 边集）。
            // EXECUTING_GOAL → FOLLOW 供 FightHandler 低血量逃生等紧急路径；
            // → TALK 供玩家对话打断（对话结束后由 GoalExecutor 恢复执行）。
            [AgentState.EXECUTING_GOAL] = new HashSet<AgentState>
            {
                AgentState.IDLE,
                AgentState.FOLLOW,
                AgentState.FIGHT,
                AgentState.TALK,
                AgentState.TRAVELING_TO_REPORT,
            },
            // 汇报态：汇报完成 / 超时 / NPC 死亡 → 收尾回 IDLE。
            [AgentState.TRAVELING_TO_REPORT] = new HashSet<AgentState>
            {
                AgentState.IDLE,
            },
        };

        /// <summary>
        /// Fired when a state transition occurs.
        /// </summary>
        public event EventHandler<StateTransitionEventArgs>? OnStateChanged;

        /// <summary>
        /// The current state implementation.
        /// </summary>
        public IAgentState? CurrentState { get; private set; }

        /// <summary>
        /// The current state flag.
        /// </summary>
        public AgentState CurrentStateFlag { get; private set; }

        /// <summary>
        /// How long the agent has been in the current state.
        /// </summary>
        public TimeSpan StateDuration => _stateStopwatch.IsRunning
            ? _stateStopwatch.Elapsed
            : TimeSpan.Zero;

        /// <summary>
        /// Read-only access to the transition log for debugging.
        /// </summary>
        public IReadOnlyList<string> TransitionLog => _transitionLog.AsReadOnly();

        /// <summary>
        /// Creates a new AgentStateMachine.
        /// </summary>
        public AgentStateMachine()
        {
            _states = new Dictionary<AgentState, IAgentState>();
            CurrentState = null;
            CurrentStateFlag = AgentState.IDLE;
            _stateStopwatch = new Stopwatch();
            _transitionLog = new List<string>();
        }

        /// <summary>
        /// Registers a state implementation for a given state flag.
        /// Must be called before the state machine is used.
        /// </summary>
        /// <param name="stateFlag">The state flag this implementation handles.</param>
        /// <param name="state">The state implementation.</param>
        public void RegisterState(AgentState stateFlag, IAgentState state)
        {
            if (_states.ContainsKey(stateFlag))
            {
                throw new InvalidOperationException($"State {stateFlag} is already registered.");
            }

            _states[stateFlag] = state;
        }

        /// <summary>
        /// Sets up the state machine with all registered states and transitions to IDLE.
        /// Must be called after registering all states.
        /// </summary>
        public void Initialize()
        {
            if (_states.Count == 0)
            {
                throw new InvalidOperationException("No states registered. Call RegisterState() first.");
            }

            if (!_states.ContainsKey(AgentState.IDLE))
            {
                throw new InvalidOperationException("IDLE state must be registered before initialization.");
            }

            TransitionTo(AgentState.IDLE, forced: false);
        }

        /// <summary>
        /// Attempts to transition to a new state following transition rules.
        /// Returns the result of the transition attempt.
        /// </summary>
        /// <param name="newState">The target state to transition to.</param>
        /// <param name="reason">自由字符串原因（默认 "llm_decision"），透传到 StateChangedSender。</param>
        /// <returns>Success if transition occurred, Failed if current state rejected it, InvalidTransition if not allowed.</returns>
        public StateTransitionResult TryTransition(AgentState newState, string reason = "llm_decision")
        {
            if (CurrentState == null)
            {
                LogTransition(AgentState.IDLE, newState, "Failed: state machine not initialized");
                return StateTransitionResult.Failed;
            }

            if (!IsTransitionAllowed(CurrentStateFlag, newState))
            {
                LogTransition(CurrentStateFlag, newState, "InvalidTransition: not in allowed transitions");
                return StateTransitionResult.InvalidTransition;
            }

            if (!CurrentState.CanTransitionTo(newState))
            {
                LogTransition(CurrentStateFlag, newState, "Failed: current state rejected transition");
                return StateTransitionResult.Failed;
            }

            return PerformTransition(newState, forced: false, reason: reason)
                ? StateTransitionResult.Success
                : StateTransitionResult.Failed;
        }

        /// <summary>
        /// Forces a state transition with restricted bypass rules.
        /// Allowed bypasses:
        ///   1. Any state → IDLE (handler exit / timeout)
        ///   2. Emergency escape (low health → FOLLOW)
        ///   3. AllowedTransitions whitelist
        /// fromDecision only bypasses MinStateDuration guard (decision engine already considers timing).
        /// All transitions must respect AllowedTransitions regardless of source.
        /// </summary>
        /// <param name="newState">The target state to transition to.</param>
        /// <param name="skipDurationGuard">When true, bypasses the minimum state duration check. Intended for test/debug API only.</param>
        /// <param name="fromDecision">When true, bypasses minimum state duration only (TS Agent Server decision result).</param>
        /// <param name="reason">自由字符串原因（如 "travel_failed"/"evicted"/"task_completed"/"manual"），透传到 StateChangedSender 供 TS 端 prompt 渲染。</param>
        public bool ForceTransition(AgentState newState, bool skipDurationGuard = false, bool fromDecision = false, string reason = "")
        {
            if (CurrentState == null)
            {
                LogTransition(AgentState.IDLE, newState, "ForceTransition: state machine not initialized, initializing");
                TransitionTo(newState, forced: true, reason: reason);
                return true;
            }

            if (newState == CurrentStateFlag)
            {
                LogTransition(CurrentStateFlag, newState, "ForceTransition BLOCKED: self-transition (no-op)");
                return false;
            }

            var isEmergencyEscape = newState == AgentState.FOLLOW && _emergencyEscapeAllowed;
            var isLegalTransition = IsTransitionAllowed(CurrentStateFlag, newState);
            var bypassAllowed = newState == AgentState.IDLE
                || isEmergencyEscape
                || isLegalTransition;

            if (!bypassAllowed)
            {
                LogTransition(CurrentStateFlag, newState, "ForceTransition BLOCKED: not in allowed transitions");
                return false;
            }

            if (!skipDurationGuard
                && newState != AgentState.IDLE
                && !fromDecision
                && !isEmergencyEscape)
            {
                var minDuration = GetMinimumStateDuration(CurrentStateFlag);
                if (minDuration > TimeSpan.Zero && StateDuration < minDuration)
                {
                    LogTransition(CurrentStateFlag, newState,
                        $"ForceTransition BLOCKED: minimum duration not met ({StateDuration.TotalSeconds:F1}s < {minDuration.TotalSeconds:F1}s)");
                    return false;
                }
            }

            return PerformTransition(newState, forced: true, reason: reason);
        }

        /// <summary>
        /// Called every game tick to update the current state.
        /// </summary>
        /// <param name="npc">The NPC instance for this agent.</param>
        /// <param name="agent">The agent instance.</param>
        /// <param name="ticks">Number of ticks since the game started.</param>
        public void Update(NPC npc, AgentInstance agent, int ticks) => CurrentState?.Update(npc, agent, ticks);

        /// <summary>
        /// 启用/禁用紧急逃生模式（低血量时允许从任何状态转到FOLLOW）
        /// </summary>
        public void SetEmergencyEscape(bool enabled) => _emergencyEscapeAllowed = enabled;

        /// <summary>
        /// Resets the state machine back to IDLE state.
        /// </summary>
        public void Reset()
        {
            if (CurrentState != null)
            {
                var previousState = CurrentStateFlag;
                _ = _stateStopwatch.Elapsed;
                CurrentState.Exit();
                _stateStopwatch.Stop();
                LogTransition(previousState, AgentState.IDLE, "Reset");
            }

            TransitionTo(AgentState.IDLE, forced: false);
        }

        /// <summary>
        /// Checks if a transition from one state to another is allowed by the transition rules.
        /// </summary>
        public static bool IsTransitionAllowed(AgentState from, AgentState to) => AllowedTransitions.TryGetValue(from, out var allowed) && allowed.Contains(to);

        /// <summary>
        /// 获取指定状态的最短持续时间。状态机自身需要此守卫，防止 ForceTransition 绕过时间约束。
        /// </summary>
        public static TimeSpan GetMinimumStateDuration(AgentState state) => state switch
        {
            AgentState.FIGHT => TimeSpan.FromSeconds(15.0),
            AgentState.FARM => TimeSpan.FromSeconds(8.0),
            AgentState.MINE => TimeSpan.FromSeconds(10.0),
            AgentState.FORAGE => TimeSpan.FromSeconds(8.0),
            AgentState.FOLLOW => TimeSpan.FromSeconds(3.0),
            AgentState.IDLE => TimeSpan.Zero,
            AgentState.TALK => TimeSpan.Zero,
            _ => TimeSpan.Zero,
        };

        /// <summary>低血量时允许从任何状态强制转到 FOLLOW。</summary>
        private bool _emergencyEscapeAllowed;

        /// <summary>
        /// Gets the state implementation for a given state flag, or null if not registered.
        /// </summary>
        public IAgentState? GetState(AgentState stateFlag) => _states.TryGetValue(stateFlag, out var state) ? state : null;

        /// <summary>
        /// 返回已注册的状态列表，用于诊断。
        /// </summary>
        public AgentState[] GetRegisteredStates() => _states.Keys.ToArray();

        /// <summary>
        /// Returns the set of states that are valid transition targets from the given state.
        /// </summary>
        public static HashSet<AgentState> GetAllowedTransitions(AgentState fromState)
        {
            if (!AllowedTransitions.TryGetValue(fromState, out var allowed))
            {
                return new HashSet<AgentState>();
            }

            return new HashSet<AgentState>(allowed);
        }

        private bool PerformTransition(AgentState newState, bool forced, string reason = "")
        {
            if (!_states.TryGetValue(newState, out var newStateImpl))
            {
                LogTransition(CurrentStateFlag, newState, $"PerformTransition BLOCKED: state {newState} not registered (registered: {string.Join(",", _states.Keys)})");
                return false;
            }

            var previousStateFlag = CurrentStateFlag;
            var previousDuration = _stateStopwatch.Elapsed;

            CurrentState?.Exit();
            _stateStopwatch.Stop();

            CurrentState = newStateImpl;
            CurrentStateFlag = newState;
            CurrentState.Entry();
            _stateStopwatch.Restart();

            LogTransition(previousStateFlag, newState, forced ? "Forced" : "Normal");

            OnStateChanged?.Invoke(this, new StateTransitionEventArgs(
                previousStateFlag, newState, previousDuration, forced, reason));
            return true;
        }

        private void TransitionTo(AgentState newState, bool forced, string reason = "")
        {
            if (!_states.TryGetValue(newState, out var newStateImpl))
            {
                LogTransition(CurrentStateFlag, newState, $"TransitionTo BLOCKED: state {newState} not registered (registered: {string.Join(",", _states.Keys)})");
                return;
            }

            var previousStateFlag = CurrentStateFlag;
            var previousDuration = _stateStopwatch.IsRunning ? _stateStopwatch.Elapsed : TimeSpan.Zero;

            CurrentState?.Exit();
            _stateStopwatch.Stop();

            CurrentState = newStateImpl;
            CurrentStateFlag = newState;
            CurrentState.Entry();
            _stateStopwatch.Restart();

            LogTransition(previousStateFlag, newState, forced ? "Forced" : "Normal");

            OnStateChanged?.Invoke(this, new StateTransitionEventArgs(
                previousStateFlag, newState, previousDuration, forced, reason));
        }

        private void LogTransition(AgentState from, AgentState to, string reason)
        {
            var timestamp = DateTime.UtcNow.ToString("O");
            var entry = $"[{timestamp}] {from} -> {to} ({reason})";
            _transitionLog.Add(entry);
        }
    }
}
