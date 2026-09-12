using StardewValley;
using ValleyAgent.Brain;
using ValleyAgent.Services;

namespace ValleyAgent.Goals;

/// <summary>
///     Goal 状态机（阶段 2 set_goal）。NotStarted → Executing → Complete / Failed / Cancelled。
///     GoalExecutor 每 tick 驱动 Executing 目标，完成后（reportBack=true）进入寻路汇报阶段。
/// </summary>
public enum GoalStatus
{
    /// <summary>已创建但尚未 Start（进入 EXECUTING_GOAL 前）。</summary>
    NotStarted,

    /// <summary>执行中：GoalExecutor 每 tick 调 Tick() 推进。</summary>
    Executing,

    /// <summary>终止条件达成（物品获得量 / 动作计数 / 击杀计数）。</summary>
    Complete,

    /// <summary>超时 / 卡死 / 资源耗尽 / NPC 死亡。</summary>
    Failed,

    /// <summary>被取消（新 set_goal 覆盖旧目标等）。</summary>
    Cancelled,
}

/// <summary>
///     五种 Goal 类型（wire 字符串 "chop_tree" | "mine" | "water_crops" | "fight" | "forage"）。
/// </summary>
public enum GoalType
{
    ChopTree,
    Mine,
    WaterCrops,
    Fight,
    Forage,
}

/// <summary>
///     Goal 契约（阶段 2，spec §3.1 / design doc §7.3）。
///
///     <para>接口定义在 Abstractions 项目，因为 <see cref="AgentBrain.PendingGoal"/>
///     需要强类型引用它，而 Abstractions 不能反向引用 ValleyAgent 主项目
///     （AgentBrain 在 Abstractions 中）。具体实现（GoalBase + 五种 Goal）在
///     ValleyAgent/Goals/。</para>
///
    ///     <para>终止判定：物品收集量模型（spec §1.1）——Goal 内部维护 collectedCount，
///     每 tick 对比 NPC 背包物品数量差（Start 时基线快照 vs 当前，按 itemId 聚合）。
///     不使用 GoalVerifier（环境差量模型，仅供 TestMod 审计）。</para>
/// </summary>
public interface IGoal
{
    /// <summary>所属 NPC 名（字典 key，大小写不敏感）。</summary>
    string NpcName { get; }

    /// <summary>Goal 类型（"chop_tree" | "mine" | "water_crops" | "fight" | "forage"）。</summary>
    GoalType Type { get; }

    /// <summary>当前状态。</summary>
    GoalStatus Status { get; }

    /// <summary>完成后是否寻路回玩家汇报。</summary>
    bool ReportBack { get; }

    /// <summary>已完成的有效动作数（chop/mine/forage = 获得物品数，water = 浇地次数，fight = 击杀数）。</summary>
    int CollectedCount { get; }

    /// <summary>自 Start 起的游戏分钟数（超时判定，ModConfig 可调）。</summary>
    int ElapsedGameMinutes { get; }

    /// <summary>失败/取消原因（供汇报与日志）。</summary>
    string Reason { get; }

    /// <summary>开始执行：取背包基线快照、记录开始时间、初始化子类状态。</summary>
    void Start(AgentInstance agent, NPC npc);

    /// <summary>每 tick 推进（委托 Handler 干活 / 自身动作循环），并做终止与超时判定。</summary>
    void Tick(AgentInstance agent, NPC npc, int currentTick);

    /// <summary>取消目标（新目标覆盖 / 玩家对话打断）。</summary>
    void Cancel(string reason);

    /// <summary>标记完成（仅执行中可转移；由 GoalExecutor 在终止判定达成时调用）。</summary>
    void Complete();

    /// <summary>标记失败（超时 / 卡死 / 资源耗尽 / NPC 死亡）。</summary>
    void Fail(string reason);

    /// <summary>进度描述，供汇报 LLM 使用（如 "chop_tree: 已获得 6/10 木头"）。</summary>
    string DescribeProgress();
}
