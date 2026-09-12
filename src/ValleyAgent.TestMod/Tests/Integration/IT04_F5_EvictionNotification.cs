#nullable enable
using System.Linq;
using StardewModdingAPI;
using StardewValley;
using ValleyAgent.Agents;

namespace ValleyAgent.TestMod.Tests.Integration;

/// <summary>
///     IT04：验证 F5 满员淘汰时玩家可见通知（"X 告别离开了"）。
///     触发路径：DialogueBoxInputPatch.PromoteToAgent 内部调
///     AllocationManager.ForceAllocate，若满员则触发 OnAgentDeallocated 事件，
///     PromoteToAgent 监听该事件后在 chatBox 输出 "X 告别离开了"。
///     限制：PromoteToAgent 是 private static 方法，依赖 DialogueBoxInputPatch
///     静态字段（_agentService 等）已初始化，且内部调用 _agentService.RemoveAgent
///     等会修改游戏状态。直接调用风险高且容易污染后续测试。
///     可自动化部分：验证 AllocationManager.ForceAllocate 触发 OnAgentDeallocated 事件。
///     不可自动化部分：验证 chatBox 实际出现"告别离开"消息（依赖 PromoteToAgent 完整流程）。
/// </summary>
public class IT04_F5_EvictionNotification : IntegrationTestBase
{
    private AgentAllocationManager? _allocationManager;
    private bool _asserted;
    private bool _evicted;
    private string? _evictedNpcName;
    private bool _setupComplete;

    public IT04_F5_EvictionNotification(IModHelper helper, IMonitor monitor) : base(helper, monitor)
    {
    }

    public override string TestName
    {
        get => "IT04_F5_EvictionNotification";
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

        _allocationManager = Container!.GetService<AgentAllocationManager>();
        if (_allocationManager == null)
        {
            Skip("AgentAllocationManager not registered in container");
            return;
        }

        Monitor.Log(
            $"[{TestName}] Setup complete. MaxAgents={_allocationManager.MaxAgents}, current={_allocationManager.CurrentAgentCount}",
            LogLevel.Info);
        _setupComplete = true;
    }

    public override bool Update()
    {
        if (!_setupComplete || _allocationManager == null || Api == null)
        {
            return true;
        }

        // Phase 1: 把 AllocationManager 填满到 MaxAgents
        if (CurrentTick == 30 && !_evicted)
        {
            // 先释放所有已分配的 agent（避免污染）
            foreach (var name in _allocationManager.AllocatedAgentNames.ToList())
            {
                _ = Api.TryDeallocateAgent(name);
            }

            // 拿当前 NPC 列表中前 MaxAgents 个候选
            var candidates = new[] { "Abigail", "Sebastian", "Sam", "Penny", "Maru", "Leah" };
            var allocatedCount = 0;
            foreach (var name in candidates)
            {
                if (allocatedCount >= _allocationManager.MaxAgents)
                {
                    break;
                }

                var npc = Game1.getCharacterFromName(name);
                if (npc == null)
                {
                    continue;
                }

                if (_allocationManager.ForceAllocate(name))
                {
                    allocatedCount++;
                }
            }

            Assert("filled_to_max", allocatedCount == _allocationManager.MaxAgents,
                $"allocated={allocatedCount}, max={_allocationManager.MaxAgents}");

            if (allocatedCount < _allocationManager.MaxAgents)
            {
                Skip($"无法填满 MaxAgents（候选不足，allocated={allocatedCount}）");
                return true;
            }

            // 订阅淘汰事件
            _allocationManager.OnAgentDeallocated += OnDeallocated;

            // 触发淘汰：ForceAllocate 一个新 NPC（优先级最低的非 manual 会被淘汰）
            // 找一个未分配的候选
            string? evictor = null;
            foreach (var name in candidates)
            {
                if (!_allocationManager.AllocatedAgentNames.Contains(name))
                {
                    evictor = name;
                    break;
                }
            }

            if (evictor == null)
            {
                Skip("无可用的淘汰触发者候选");
                _allocationManager.OnAgentDeallocated -= OnDeallocated;
                return true;
            }

            // 释放 manual override 让槽位可被替换
            foreach (var name in _allocationManager.AllocatedAgentNames.ToList())
            {
                _ = _allocationManager.ReleaseManualOverride(name);
            }

            var promoted = _allocationManager.ForceAllocate(evictor);
            _evicted = _evictedNpcName != null;
            Assert("eviction_triggered", _evicted,
                $"ForceAllocate({evictor})={promoted}, evictedNpc={_evictedNpcName ?? "(none)"}");

            _allocationManager.OnAgentDeallocated -= OnDeallocated;
        }

        // Phase 2: 验证淘汰事件触发
        if (CurrentTick == 90 && _evicted && !_asserted)
        {
            _asserted = true;
            Assert("evicted_npc_name_captured", !string.IsNullOrEmpty(_evictedNpcName),
                $"evictedNpc={_evictedNpcName}");

            // chatBox "告别离开" 消息由 DialogueBoxInputPatch.PromoteToAgent 内部写入，
            // 直接调 AllocationManager.ForceAllocate 不会触发该路径。
            // 本测试只验证 AllocationManager 层的 OnAgentDeallocated 事件（已在上一步断言）。
        }

        return CurrentTick >= 200;
    }

    private void OnDeallocated(object? sender, AgentAllocationEventArgs e) => _evictedNpcName = e.NpcName;

    public override void Teardown()
    {
        CommonTeardown();
        Monitor.Log($"[{TestName}] Teardown.", LogLevel.Info);
    }
}