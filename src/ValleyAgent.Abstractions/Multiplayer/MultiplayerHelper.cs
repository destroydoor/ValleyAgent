namespace ValleyAgent.Multiplayer
{
    /// <summary>
    /// 联机模式工具类。提供统一的多玩家判断和消息发送接口。
    /// Phase 1: 提供守卫方法，防止 Farmhand 端崩溃。
    /// 注意：使用 StardewModdingAPI.Context 全限定名，避免与 ValleyAgent.Context 命名空间冲突。
    ///
    /// 关键设计决策：
    /// - 分屏非主玩家 (IsSplitScreenFarmhand) 在主机电脑上运行，共享主机内存。
    ///   但 SMAPI 的 UpdateTicked 会为每个分屏实例分别触发，且 Game1.player 指向不同的 Farmer。
    ///   Phase 1 策略：分屏非主玩家也跳过 Agent 逻辑，避免重复执行。
    ///   Phase 4 策略：分屏非主玩家可直接访问主机的 AgentService，无需 Mod 消息。
    /// - 远程 Farmhand (IsRemoteFarmhand) 必须通过 Mod 消息与主机通信。
    /// </summary>
    public static class MultiplayerHelper
    {
        /// <summary>
        /// 当前是否应运行完整的 Agent 逻辑（单人模式或主机端）。
        /// 所有 Agent 创建、LLM 决策、Handler 执行、WebSocket 通信都应受此守卫。
        /// 分屏非主玩家也返回 false，因为 Agent 逻辑已在主屏幕实例上运行。
        /// </summary>
        public static bool ShouldRunAgentLogic =>
            !StardewModdingAPI.Context.IsMultiplayer || StardewModdingAPI.Context.IsMainPlayer;

        /// <summary>
        /// 当前是否为 Farmhand（联机中的非主机玩家，含分屏和远程）。
        /// Farmhand 不应操作 NPC、不连接 TS Agent Server、不读写存档。
        /// </summary>
        public static bool IsFarmhand =>
            StardewModdingAPI.Context.IsMultiplayer && !StardewModdingAPI.Context.IsMainPlayer;

        /// <summary>
        /// 当前是否处于联机模式（含分屏）。
        /// </summary>
        public static bool IsMultiplayer => StardewModdingAPI.Context.IsMultiplayer;

        /// <summary>
        /// 当前是否在主机电脑上（含分屏玩家）。
        /// 分屏玩家 IsMainPlayer=false 但 IsOnHostComputer=true，
        /// 可以直接访问主机内存，无需走网络消息。
        /// </summary>
        public static bool IsOnHostComputer => StardewModdingAPI.Context.IsOnHostComputer;

        /// <summary>
        /// 分屏非主玩家：在主机电脑上但不是主玩家。
        /// Phase 1: 与远程 Farmhand 相同处理（跳过所有 Agent 逻辑）。
        /// Phase 4: 可直接调用主机的 Agent API，无需 Mod 消息。
        /// </summary>
        public static bool IsSplitScreenFarmhand =>
            StardewModdingAPI.Context.IsMultiplayer
            && !StardewModdingAPI.Context.IsMainPlayer
            && StardewModdingAPI.Context.IsOnHostComputer;

        /// <summary>
        /// 远程 Farmhand：不在主机电脑上的联机玩家。
        /// 必须通过 Mod 消息与主机通信。
        /// </summary>
        public static bool IsRemoteFarmhand =>
            StardewModdingAPI.Context.IsMultiplayer
            && !StardewModdingAPI.Context.IsMainPlayer
            && !StardewModdingAPI.Context.IsOnHostComputer;

        /// <summary>
        /// 当前是否为分屏模式。分屏下 UpdateTicked 为每个屏幕分别触发，
        /// 静态字段和共享状态可能冲突。需要使用 PerScreen&lt;T&gt; 或 ScreenId 区分。
        /// </summary>
        public static bool IsSplitScreen => StardewModdingAPI.Context.IsSplitScreen;

        /// <summary>
        /// 当前屏幕 ID。分屏模式下每个实例有唯一 ScreenId。
        /// 主屏幕始终为 0。非分屏模式下也为 0。
        /// </summary>
        public static int ScreenId => StardewModdingAPI.Context.ScreenId;
    }
}
