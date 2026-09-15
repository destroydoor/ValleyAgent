#nullable enable
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.TerrainFeatures;
using ValleyAgent.Services;
using ValleyAgent.StateMachine;
using ValleyAgent.WebSocket;
using Object = StardewValley.Object;

namespace ValleyAgent.TestMod.Tests.Integration;

/// <summary>
///     IT12：验证阶段 2 set_goal 端到端（chop_tree，reportBack=false）。
///     链路：ExecuteAction(set_goal) → GoalExecutor.CreateGoal → EXECUTING_GOAL 状态
///     → AgentTickLoop 每 tick 驱动 ChopTreeGoal.TickCore 砍树（Wood 进 NPC 背包）
///     → 背包 Wood 差量 ≥ quantity → 完成 → FinalizeSuccess（不汇报，直接回 IDLE）。
///     验证点：
///     1. set_goal 创建后 PendingGoal 非空 + 状态 EXECUTING_GOAL
///     2. 砍树后 NPC 背包 Wood 增加 ≥ quantity
///     3. 完成后 PendingGoal 清空 + 状态回 IDLE（reportBack=false 直接收尾）
/// </summary>
public class IT12_SetGoal_ChopTree : IntegrationTestBase
{
    private const string WoodItemId = "(O)388";
    private const int GoalQuantity = 5;

    private bool _goalCreated;
    private bool _goalCompleted;
    private bool _asserted;
    private CommandExecutor? _executor;
    private AgentInstance? _agent;
    private Vector2 _treeTile;
    private int _woodAtGoalStart;

    public IT12_SetGoal_ChopTree(IModHelper helper, IMonitor monitor) : base(helper, monitor)
    {
    }

    public override string TestName
    {
        get => "IT12_SetGoal_ChopTree";
    }

    public override int TimeoutTicks
    {
        get => 900;
    }

    public override TestGroup Group
    {
        get => TestGroup.Integration;
    }

    public override void Setup()
    {
        if (!TryCommonSetup())
        {
            return;
        }

        _executor = Container!.GetService<CommandExecutor>();
        if (_executor == null)
        {
            Skip("CommandExecutor not registered in container");
            return;
        }

        _executor.OnSendActionResult = (_, _, _, _) => Task.FromResult(true);

        var agentService = Container.GetService<AgentService>();
        if (agentService == null || !agentService.TryGetAgent(NpcName, out var agent) || agent == null)
        {
            Skip($"agent '{NpcName}' not found in AgentService");
            return;
        }

        _agent = agent;
        _woodAtGoalStart = CountNpcWood(agent);

        var npc = Game1.getCharacterFromName(NpcName);
        if (npc == null || npc.currentLocation == null)
        {
            Skip("NPC or currentLocation is null");
            return;
        }

        var npcTile = npc.Tile;
        _treeTile = new Vector2((int)npcTile.X + 1, (int)npcTile.Y);

        // 移除 NPC 周围 3 格内所有现有 Tree（避免干扰最近树选择）
        var loc = npc.currentLocation;
        var tilesToClean = new List<Vector2>();
        foreach (var entry in loc.terrainFeatures.Pairs)
        {
            if (entry.Value is Tree && Vector2.Distance(entry.Key, npcTile) <= 3f)
            {
                tilesToClean.Add(entry.Key);
            }
        }

        foreach (var tile in tilesToClean)
        {
            _ = loc.terrainFeatures.Remove(tile);
        }

        if (loc.terrainFeatures.ContainsKey(_treeTile))
        {
            _ = loc.terrainFeatures.Remove(_treeTile);
        }

        try
        {
            // 成熟橡树（growthStage=5）：砍一次得 5 Wood → 恰好达成 quantity=5
            var tree = new Tree("1", 5);
            loc.terrainFeatures[_treeTile] = tree;
            Monitor.Log($"[{TestName}] Placed mature Tree at {_treeTile} in {loc.Name}", LogLevel.Info);
        }
        catch (Exception ex)
        {
            Skip($"failed to place Tree: {ex.Message}");
            return;
        }

        Monitor.Log($"[{TestName}] Setup complete. treeTile={_treeTile}, woodAtStart={_woodAtGoalStart}",
            LogLevel.Info);
    }

    public override bool Update()
    {
        if (_executor == null || _agent == null)
        {
            return true;
        }

        // Phase 1: 下发 set_goal（chop_tree ×5，不汇报）
        if (CurrentTick == 30 && !_goalCreated)
        {
            _goalCreated = true;
            var args = new Dictionary<string, object>
            {
                ["type"] = "chop_tree",
                ["quantity"] = GoalQuantity,
                ["reportBack"] = false
            };
            var action = new ToolAction("set_goal", args, "it12-call-1");
            try
            {
                _executor.ExecuteAction(action, NpcName);
            }
            catch (Exception ex)
            {
                Assert("set_goal_execute_no_throw", false, $"ExecuteAction(set_goal) threw: {ex.Message}");
                return true;
            }

            var goalActive = _agent.Brain.PendingGoal != null;
            AssertEx("goal_created", goalActive,
                "set_goal 工具调用后 PendingGoal 仍为 null（GoalExecutor.CreateGoal 未接线或参数解析失败）",
                goalActive ? $"PendingGoal={_agent.Brain.PendingGoal?.Type}" : "PendingGoal null");

            var state = _agent.StateMachine.CurrentStateFlag;
            AssertEx("state_executing_goal", state == AgentState.EXECUTING_GOAL,
                "set_goal 创建目标后状态机未进入 EXECUTING_GOAL（ForceTransition 缺失或被守卫拦截）",
                $"state={state} (expected EXECUTING_GOAL)");
        }

        // Phase 2: 等待执行完成（砍树 + 收尾回 IDLE）
        if (CurrentTick == 240 && !_goalCompleted)
        {
            _goalCompleted = true;
            var pending = _agent.Brain.PendingGoal;
            var state = _agent.StateMachine.CurrentStateFlag;
            var woodNow = CountNpcWood(_agent);
            var woodGained = woodNow - _woodAtGoalStart;

            Assert("wood_harvested", woodGained >= GoalQuantity,
                $"npc wood {_woodAtGoalStart} → {woodNow} (gained {woodGained}, expected ≥ {GoalQuantity})");

            // reportBack=false → 完成后直接收尾：目标清空 + 离开执行态
            AssertEx("goal_cleared_after_complete", pending == null,
                "目标完成后 PendingGoal 未清空（FinalizeSuccess 漏掉清目标步骤），残留目标会污染后续 set_goal",
                pending == null ? "PendingGoal cleared" : $"PendingGoal still {pending?.Type}/{pending?.Status}");

            // 状态断言放宽：完成后 ForceIdle(IDLE)，但玩家紧邻 NPC 时可能随后被触发 TALK
            //（测试场景玩家固定在 NPC 旁 1 格）。核心断言是"已离开 EXECUTING_GOAL 执行态"，
            // 强制 IDLE 会因 TALK 竞态产生误导性失败。
            AssertEx("state_left_executing", state != AgentState.EXECUTING_GOAL,
                "目标完成后状态机滞留 EXECUTING_GOAL（收尾 ForceTransition 未执行或被 STATE_DURATIONS 拦住）",
                $"state={state} (expected to leave EXECUTING_GOAL after reportBack=false)");
        }

        if (CurrentTick == 300 && !_asserted)
        {
            _asserted = true;
            var apiState = Api!.GetAgentState(NpcName);
            AssertEx("state_still_readable", !string.IsNullOrEmpty(apiState),
                "set_goal 全流程跑完后 AgentBrain 被拆除（OnAgentDeallocated 误回收），GetAgentState 返回空串",
                $"state={apiState}");
        }

        return CurrentTick >= 330;
    }

    public override void Teardown()
    {
        // 清理可能残留的树
        var npc = Game1.getCharacterFromName(NpcName);
        var loc = npc?.currentLocation;
        if (loc != null && loc.terrainFeatures.ContainsKey(_treeTile))
        {
            _ = loc.terrainFeatures.Remove(_treeTile);
        }

        // 清理残留目标（防止污染后续测试）
        if (_agent?.Brain.PendingGoal != null)
        {
            _agent.Brain.PendingGoal = null;
        }

        if (_executor != null)
        {
            _executor.OnSendActionResult = null;
        }

        CommonTeardown();
        Monitor.Log($"[{TestName}] Teardown.", LogLevel.Info);
    }

    private static int CountNpcWood(AgentInstance agent)
    {
        var total = 0;
        foreach (var item in agent.Inventory.GetAllItems())
        {
            if (item is Object obj &&
                (obj.QualifiedItemId == WoodItemId || obj.ItemId == WoodItemId))
            {
                total += obj.Stack;
            }
        }

        return total;
    }
}
