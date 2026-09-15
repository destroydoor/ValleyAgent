#nullable enable
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using ValleyAgent.Beats;
using ValleyAgent.Commands;
using ValleyAgent.Services;

namespace ValleyAgent.TestMod.Tests.Integration;

/// <summary>
///     IT13：验证阶段 3 Director 工具集端到端（元层工具：改 NPC 状态数据，不进 NPC LLM 上下文）。
///     链路：DirectorTools.Execute(tool, args) → 各工具实现（校验 → 变更状态 → 返回）。
///     验证点：
///     1. set_npc_money：NPC 钱包按 delta 变化（+500）
///     2. set_npc_mood：L2 心情标签写入 Brain.MoodTag
///     3. set_npc_position：同图移动 NPC（tile 变化）
///     4. spawn_beat：BeatStore 创建活跃 beat（sceneDesc 可读）
///     5. set_npc_recent_events：L2 近期事件写入 Brain.TodayEvents
///     6. inject_memory：L1 长期记忆写入 Brain.ShortTermMemories
/// </summary>
public class IT13_DirectorTools : IntegrationTestBase
{
    private bool _asserted;
    private bool _done;
    private DirectorTools? _directorTools;
    private BeatStore? _beatStore;
    private AgentInstance? _agent;
    private int _moneyBefore;
    private Vector2 _posBefore;

    /// <summary>CA1861：常量数组参数提为 static readonly（重复调用不重建数组）。</summary>
    private static readonly string[] RecentEvents = { "早上在镇上散步", "帮了农场主一个小忙" };

    public IT13_DirectorTools(IModHelper helper, IMonitor monitor) : base(helper, monitor)
    {
    }

    public override string TestName
    {
        get => "IT13_DirectorTools";
    }

    public override int TimeoutTicks
    {
        get => 600;
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

        _directorTools = Container!.GetService<DirectorTools>();
        if (_directorTools == null)
        {
            Skip("DirectorTools not registered in container");
            return;
        }

        _beatStore = Container.GetService<BeatStore>();
        if (_beatStore == null)
        {
            Skip("BeatStore not registered in container");
            return;
        }

        var agentService = Container.GetService<AgentService>();
        if (agentService == null || !agentService.TryGetAgent(NpcName, out var agent) || agent == null)
        {
            Skip($"agent '{NpcName}' not found in AgentService");
            return;
        }

        _agent = agent;
        _moneyBefore = agent.Inventory.Money;
        _posBefore = Game1.getCharacterFromName(NpcName)?.Tile ?? Vector2.Zero;

        Monitor.Log($"[{TestName}] Setup complete. moneyBefore={_moneyBefore}, posBefore={_posBefore}",
            LogLevel.Info);
    }

    public override bool Update()
    {
        if (_directorTools == null || _beatStore == null || _agent == null)
        {
            return true;
        }

        if (CurrentTick == 30 && !_asserted)
        {
            _asserted = true;
            var npc = Game1.getCharacterFromName(NpcName);
            if (npc == null)
            {
                Assert("npc_available", false, "NPC not found");
                _done = true;
                return true;
            }

            // ── 1. set_npc_money ──
            var moneyResult = _directorTools.Execute("set_npc_money",
                new Dictionary<string, object> { ["npc"] = NpcName, ["delta"] = 500 });
            AssertEx("money_tool_success", moneyResult.Success,
                "DirectorTools.Execute 对 set_npc_money 返回失败（参数校验拒绝/工具路由缺项/AgentInstance 找不到）",
                $"reason={moneyResult.Reason}");
            AssertEx("money_changed", _agent.Inventory.Money == _moneyBefore + 500,
                "set_npc_money 报成功但钱包未动（执行镜像 Inventory.Money 写入断链），余额偏离 before+500",
                $"npc money {_moneyBefore} → {_agent.Inventory.Money} (expected +500)");

            // ── 2. set_npc_mood ──
            var moodResult = _directorTools.Execute("set_npc_mood",
                new Dictionary<string, object> { ["npc"] = NpcName, ["mood"] = "happy" });
            AssertEx("mood_tool_success", moodResult.Success,
                "set_npc_mood 工具调用失败（mood 参数白名单拒绝 'happy' 或工具路由缺项）",
                $"reason={moodResult.Reason}");
            AssertEx("mood_written", _agent.Brain.MoodTag == "happy",
                "set_npc_mood 报成功但 L2 心情标签未写入（Brain.MoodTag 赋值断链）",
                $"MoodTag='{_agent.Brain.MoodTag}' (expected 'happy')");

            // ── 3. set_npc_position（同图移动）──
            var targetTile = new Vector2(_posBefore.X + 2, _posBefore.Y);
            var posResult = _directorTools.Execute("set_npc_position",
                new Dictionary<string, object>
                {
                    ["npc"] = NpcName,
                    ["location"] = npc.currentLocation?.Name ?? "Farm",
                    ["tile"] = new[] { (int)targetTile.X, (int)targetTile.Y }
                });
            AssertEx("position_tool_success", posResult.Success,
                "set_npc_position 工具调用失败（tile 参数解析或落点守卫拒绝）",
                $"reason={posResult.Reason}");
            var tileNow = npc.Tile;
            AssertEx("position_moved", tileNow.X == targetTile.X && tileNow.Y == targetTile.Y,
                "set_npc_position 报成功但 NPC 没到目标格（MoveTo 异步未落位或 setTileLocation 未生效）",
                $"npc tile {_posBefore} → {tileNow} (expected {targetTile})");

            // ── 4. spawn_beat ──
            var beatResult = _directorTools.Execute("spawn_beat",
                new Dictionary<string, object>
                {
                    ["npc"] = NpcName,
                    ["sceneDesc"] = "IT13 测试剧本：和玩家聊聊天气",
                    ["durationMinutes"] = 60
                });
            AssertEx("beat_tool_success", beatResult.Success,
                "spawn_beat 工具调用失败（BeatStore 服务缺项或 sceneDesc 校验拒绝）",
                $"reason={beatResult.Reason}");
            var activeBeat = _beatStore.GetActiveBeat(NpcName);
            AssertEx("beat_active", activeBeat != null,
                "spawn_beat 报成功但 BeatStore 无活跃 beat（入 Store 后被过期清理立刻回收）",
                activeBeat == null ? "no active beat" : $"beat='{activeBeat.SceneDesc}'");

            // ── 5. set_npc_recent_events ──
            var eventsResult = _directorTools.Execute("set_npc_recent_events",
                new Dictionary<string, object>
                {
                    ["npc"] = NpcName,
                    ["events"] = RecentEvents
                });
            AssertEx("events_tool_success", eventsResult.Success,
                "set_npc_recent_events 工具调用失败（events 数组参数解析拒绝）",
                $"reason={eventsResult.Reason}");
            AssertEx("events_written", _agent.Brain.TodayEvents.Count == 2,
                "set_npc_recent_events 报成功但 L2 近期事件未写入（TodayEvents 赋值断链或被日结清理立即清空）",
                $"TodayEvents={_agent.Brain.TodayEvents.Count} (expected 2)");

            // ── 6. inject_memory ──
            var memResult = _directorTools.Execute("inject_memory",
                new Dictionary<string, object>
                {
                    ["npc"] = NpcName,
                    ["text"] = "IT13 注入的长期记忆",
                    ["importance"] = 5.0
                });
            AssertEx("memory_tool_success", memResult.Success,
                "inject_memory 工具调用失败（importance 参数解析或记忆服务缺项）",
                $"reason={memResult.Reason}");
            var hasMemory = _agent.Brain.ShortTermMemories.Exists(m => m.Text == "IT13 注入的长期记忆");
            AssertEx("memory_written", hasMemory,
                "inject_memory 报成功但 L1 记忆未出现在 ShortTermMemories（文本插值改变内容或写入断链）",
                hasMemory ? "memory found" : "memory missing");

            // ── 7. 未知工具拒绝 ──
            var unknownResult = _directorTools.Execute("unknown_tool", null);
            AssertEx("unknown_tool_rejected", !unknownResult.Success,
                "未知工具未被拒绝（工具表查找 miss 时默认放行），Execute('unknown_tool') 返回 Success",
                $"reason={unknownResult.Reason} (expected unknown_director_tool)");

            _done = true;
        }

        return _done;
    }

    public override void Teardown()
    {
        // 清理测试注入的 beat（防止污染后续测试的 L3 注入）
        _beatStore?.Clear();

        CommonTeardown();
        Monitor.Log($"[{TestName}] Teardown.", LogLevel.Info);
    }
}
