using StardewValley;
using ValleyAgent.Services;

namespace ValleyAgent.StateMachine.States;

/// <summary>
///     Idle state - the default resting state for an agent.
/// </summary>
public class IdleState : IAgentState
{
    public int TicksInState { get; private set; }

    public string StateName
    {
        get => "IDLE";
    }

    public void Entry() => TicksInState = 0;

    public void Exit()
    {
    }

    public void Update(NPC npc, AgentInstance agent, int ticks) => TicksInState++;

    public bool CanTransitionTo(AgentState state)
    {
        return state switch
        {
            AgentState.FOLLOW => true,
            AgentState.FIGHT => true,
            AgentState.FARM => true,
            AgentState.FORAGE => true,
            AgentState.MINE => true,
            AgentState.TALK => true,
            AgentState.IDLE => false,
            _ => false
        };
    }
}