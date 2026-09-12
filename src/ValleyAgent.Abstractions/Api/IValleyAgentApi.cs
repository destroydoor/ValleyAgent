using ValleyAgent.Brain;
using ValleyAgent.Navigation;
using ValleyAgent.Services;

namespace ValleyAgent.Api
{
    /// <summary>
    /// Public API surface exposed by ValleyAgent for other mods (e.g. TestMod) to query and control agents.
    /// </summary>
    public interface IValleyAgentApi
    {
        /// <summary>Names of all currently allocated agents.</summary>
        public string[] GetActiveAgentNames();

        /// <summary>Get the current state name for an agent, or empty string if not allocated.</summary>
        public string GetAgentState(string npcName);

        /// <summary>Try to allocate an NPC as an agent. Returns true on success.</summary>
        public bool TryAllocateAgent(string npcName);

        /// <summary>Try to deallocate an agent. Returns true on success.</summary>
        public bool TryDeallocateAgent(string npcName);

        /// <summary>Force an immediate LLM decision for the given agent.</summary>
        public bool TryForceDecision(string npcName);

        /// <summary>Forcefully set an agent's state (bypasses minimum duration).</summary>
        public bool TrySetAgentState(string npcName, string stateName);

        /// <summary>Trigger an NPC-to-player gift via the gift system. Returns true and the item name if successful.</summary>
        public bool TryTriggerGift(string npcName, out string itemName);

        /// <summary>Get the last LLM decision for an agent. Returns true if a decision has been recorded.</summary>
        public bool TryGetLastDecision(string npcName, out string state, out string reason);

        /// <summary>Trigger an AI dialogue generation for an NPC. Returns true if the request was queued.</summary>
        public bool TryGenerateDialogue(string npcName, string playerInput);

        /// <summary>Get the last AI-generated dialogue for an NPC. Returns true if a response is available.</summary>
        public bool TryGetLastDialogue(string npcName, out string response);

        /// <summary>任务5.1：获取最近对话响应的来源（LLM/Fallback/Error）。返回 false 表示尚未产生对话或 agent 不存在。</summary>
        public bool TryGetLastDialogueSource(string npcName, out DialogueResponseSource source);

        /// <summary>清除指定 NPC 的对话冷却和挂起请求，供测试使用。</summary>
        public void ClearDialogueState(string npcName);

        /// <summary>仅清除指定 NPC 的对话冷却时间戳，允许下一次对话立即触发。供测试使用。</summary>
        public void ClearDialogueCooldown(string npcName);

        /// <summary>获取或设置对话冷却时间（毫秒）。设为 0 可禁用冷却用于测试。默认 30000ms。</summary>
        public int DialogueCooldownMs { get; set; }

        /// <summary>获取移动服务实例，用于控制 NPC 导航。若 ValleyAgent 未初始化完成则返回 null。</summary>
        public IMovementService? GetMovementService();

        /// <summary>Returns the list of currently enabled feature names (Combat, Farming, Mining, Foraging, Gifts, Friendship).</summary>
        public string[] GetEnabledFeatures();

        /// <summary>Returns how many times the NPC dialogue patch has intercepted a player interaction.</summary>
        public int GetDialogueInterceptCount();

        /// <summary>Returns the current friendship points for an NPC, or -1 if not found.</summary>
        public int GetNpcFriendshipPoints(string npcName);

        /// <summary>Returns the current health of an NPC agent, or -1 if not found.</summary>
        public int GetNpcHealth(string npcName);

        /// <summary>Returns the maximum health of an NPC agent, or -1 if not found.</summary>
        public int GetNpcMaxHealth(string npcName);

        /// <summary>Make an NPC speak text above their head.</summary>
        public bool TrySpeak(string npcName, string text, int durationMs);

        /// <summary>Make an NPC perform an emote.</summary>
        public bool TryEmote(string npcName, int emoteIndex);

        /// <summary>Get all items in an NPC's backpack. Returns empty array if not found.</summary>
        public string[] GetNpcInventory(string npcName);

        /// <summary>Heal an NPC by the given amount. Returns true if successful.</summary>
        public bool TryHeal(string npcName, int amount);

        /// <summary>Revive a dead NPC, restoring full health and warping them to the player. Returns true if successful.</summary>
        public bool TryRevive(string npcName);

        /// <summary>Returns the current emotion name of an NPC agent (e.g. "Happy", "Neutral"), or empty string if not found.</summary>
        public string GetNpcEmotion(string npcName);

        /// <summary>Fills the NPC's backpack with the given number of items for testing. Returns true if successful.</summary>
        public bool FillNpcInventory(string npcName, string itemId, int count);

        /// <summary>Returns the NPC's recent short-term memory entries. Returns empty array if not found.</summary>
        public string[] GetNpcMemories(string npcName);

        /// <summary>Sets the friendship points for an NPC. Used by narrative tests to pre-configure relationships.</summary>
        public void SetFriendshipForNpc(string npcName, int points);

        /// <summary>Sets an NPC's health directly. Used by narrative tests to simulate death/injury.</summary>
        public bool SetNpcHealth(string npcName, int health);

        /// <summary>Get the AgentInstance for an NPC, or null if not allocated. Exposes internal state for narrative test validation.</summary>
        public AgentInstance? GetAgentInstance(string npcName);

        /// <summary>返回状态机已注册的状态列表，用于诊断状态转换失败。</summary>
        public string[] GetRegisteredStates(string npcName);
    }
}
