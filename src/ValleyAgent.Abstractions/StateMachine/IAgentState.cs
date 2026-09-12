using StardewValley;
using ValleyAgent.Services;

namespace ValleyAgent.StateMachine
{
    /// <summary>
    /// Agent 状态接口。Handler 直接实现此接口，消除 Controller 空转层。
    /// </summary>
    public interface IAgentState
    {
        /// <summary>状态名称（日志/调试用）。</summary>
        public string StateName { get; }

        /// <summary>进入状态时调用。</summary>
        public void Entry();

        /// <summary>退出状态时调用。</summary>
        public void Exit();

        /// <summary>每 tick 调用，驱动状态行为。</summary>
        /// <param name="npc">当前 NPC 实例。</param>
        /// <param name="agent">Agent 实例（含 Brain/Inventory/Health）。</param>
        /// <param name="ticks">当前游戏 tick 计数。</param>
        public void Update(NPC npc, AgentInstance agent, int ticks);

        /// <summary>判断从此状态转换到目标状态是否允许。</summary>
        public bool CanTransitionTo(AgentState state);
    }
}