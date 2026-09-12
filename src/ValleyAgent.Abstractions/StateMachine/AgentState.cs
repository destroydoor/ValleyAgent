namespace ValleyAgent.StateMachine;

/// <summary>
///     Represents the possible states an agent can be in.
/// </summary>
public enum AgentState
{
    /// <summary>Agent is idle, waiting for input or events.</summary>
    IDLE,

    /// <summary>Agent is following the player.</summary>
    FOLLOW,

    /// <summary>Agent is fighting a hostile target.</summary>
    FIGHT,

    /// <summary>Agent is performing farming tasks.</summary>
    FARM,

    /// <summary>Agent is foraging for items.</summary>
    FORAGE,

    /// <summary>Agent is mining rocks/ore.</summary>
    MINE,

    /// <summary>Agent is chopping trees for wood.</summary>
    CHOP,

    /// <summary>Agent is in conversation with the player.</summary>
    TALK,

    /// <summary>执行态（阶段 2 set_goal）：GoalExecutor 后台驱动 Goal，零 LLM 循环。</summary>
    EXECUTING_GOAL,

    /// <summary>汇报态（阶段 2）：Goal 完成且 reportBack=true，寻路回玩家后触发汇报 LLM。</summary>
    TRAVELING_TO_REPORT,
}