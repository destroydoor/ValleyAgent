using System;

namespace ValleyAgent.StateMachine
{
    /// <summary>
    /// Event arguments for state change notifications.
    /// </summary>
    public class StateTransitionEventArgs : EventArgs
    {
        /// <summary>
        /// The state being transitioned from.
        /// </summary>
        public AgentState PreviousState { get; }

        /// <summary>
        /// The state being transitioned to.
        /// </summary>
        public AgentState NewState { get; }

        /// <summary>
        /// How long the agent spent in the previous state.
        /// </summary>
        public TimeSpan PreviousStateDuration { get; }

        /// <summary>
        /// Whether this was a forced transition (e.g., LLM decision or timeout).
        /// </summary>
        public bool WasForced { get; }

        /// <summary>
        /// 自由字符串描述转换原因（如 "travel_failed"/"evicted"/"task_completed"/"llm_decision"/"manual"）。
        /// 用于 FOLLOW 生命周期等场景区分不同退出原因，TS 端可据此渲染 prompt。
        /// </summary>
        public string Reason { get; }

        public StateTransitionEventArgs(AgentState previousState, AgentState newState, TimeSpan previousStateDuration, bool wasForced = false, string reason = "")
        {
            PreviousState = previousState;
            NewState = newState;
            PreviousStateDuration = previousStateDuration;
            WasForced = wasForced;
            Reason = reason ?? "";
        }
    }
}