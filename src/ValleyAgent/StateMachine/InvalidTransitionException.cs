using System;

namespace ValleyAgent.StateMachine;

/// <summary>
///     Thrown when an invalid state transition is attempted.
/// </summary>
public class InvalidTransitionException : Exception
{
    public InvalidTransitionException(AgentState fromState, AgentState toState)
        : base($"Invalid state transition: {fromState} -> {toState}")
    {
        FromState = fromState;
        ToState = toState;
    }

    public InvalidTransitionException(AgentState fromState, AgentState toState, string message)
        : base(message)
    {
        FromState = fromState;
        ToState = toState;
    }

    public InvalidTransitionException(AgentState fromState, AgentState toState, string message,
        Exception innerException)
        : base(message, innerException)
    {
        FromState = fromState;
        ToState = toState;
    }

    public AgentState FromState { get; }
    public AgentState ToState { get; }
}