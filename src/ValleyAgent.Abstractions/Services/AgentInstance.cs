using System;
using StardewValley;
using ValleyAgent.Brain;
using ValleyAgent.Health;
using ValleyAgent.Inventory;
using ValleyAgent.StateMachine;

namespace ValleyAgent.Services
{
    /// <summary>
    /// 单个 NPC 的代理实例。持有状态机、Brain、背包、健康。
    /// </summary>
    public class AgentInstance
    {
        public string NpcName { get; }
        public AgentStateMachine StateMachine { get; }
        public DateTime AllocatedAt { get; }
        public AgentBrain Brain { get; }

        public string LastDecisionState
        {
            get => Brain.LastDecisionState;
            set => Brain.LastDecisionState = value;
        }

        public string LastDecisionReason
        {
            get => Brain.LastDecisionReason;
            set => Brain.LastDecisionReason = value;
        }

        public string LastDialogueResponse
        {
            get => Brain.LastDialogueResponse;
            set => Brain.LastDialogueResponse = value;
        }

        /// <summary>任务5.1：最近对话响应来源的转发属性，供测试系统断言。</summary>
        public DialogueResponseSource LastDialogueSource
        {
            get => Brain.LastDialogueSource;
            set => Brain.LastDialogueSource = value;
        }

        public AgentInventory Inventory { get; }
        public AgentHealth Health { get; }

        // 死亡后 NPC 从地图移除，暂存引用供复活时重新添加
        internal NPC? RemovedNpcRef { get; set; }

        public AgentInstance(string npcName, AgentStateMachine stateMachine, int maxHealth = 100)
        {
            NpcName = npcName ?? throw new ArgumentNullException(nameof(npcName));
            StateMachine = stateMachine ?? throw new ArgumentNullException(nameof(stateMachine));
            Inventory = new AgentInventory();
            Health = new AgentHealth(maxHealth);
            Brain = new AgentBrain(npcName);
            AllocatedAt = DateTime.UtcNow;
        }
    }
}
