#nullable enable
using System;
using System.Collections;
using StardewModdingAPI;
using StardewValley;

namespace ValleyAgent.TestMod.Tests.Integration;

/// <summary>
///     IT05：验证 B4 旅行连续失败熔断机制。
///     当同一 NPC 连续旅行失败达 TravelFailureThreshold（=2）次后，
///     _travelBlockedUntilDay 被设置为当前 dayOfMonth，本游戏日内不再自动发起旅行。
///     验证点：
///     1. 反射设置 _consecutiveTravelFailures[NpcName] = 2 后，
///     NavigateToTaskLocation 返回 null（熔断阻止）
///     2. startedTravel out 参数为 false
///     3. NPC 仍可访问，状态未崩溃
///     注意：直接反射设置 private 字段模拟连续失败，避免依赖真实 warp 失败。
/// </summary>
public class IT05_TravelCircuitBreaker : IntegrationTestBase
{
    // CA1861: 避免将常量数组作为参数传递，改用 static readonly 字段。
    private static readonly string[] s_candidateLocations = { "Forest" };
    private bool _asserted;
    private bool _breakerTested;

    private bool _setupComplete;

    public IT05_TravelCircuitBreaker(IModHelper helper, IMonitor monitor) : base(helper, monitor)
    {
    }

    public override string TestName
    {
        get => "IT05_TravelCircuitBreaker";
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

        // 起点 FOLLOW，旅行逻辑只在 FOLLOW/任务态触发
        _ = Api!.TrySetAgentState(NpcName, "FOLLOW");

        // 反射设置 _consecutiveTravelFailures[NpcName] = 2，触发熔断阈值
        var failuresDict = ReadField<IDictionary>(Navigator!, "_consecutiveTravelFailures");
        if (failuresDict == null)
        {
            Skip("cannot access _consecutiveTravelFailures via reflection");
            return;
        }

        try
        {
            failuresDict[NpcName] = 2;
        }
        catch (Exception ex)
        {
            Skip($"failed to set _consecutiveTravelFailures: {ex.Message}");
            return;
        }

        // 同时设置 _travelBlockedUntilDay[NpcName] = Game1.dayOfMonth
        var blockedDict = ReadField<IDictionary>(Navigator!, "_travelBlockedUntilDay");
        if (blockedDict != null)
        {
            try
            {
                blockedDict[NpcName] = Game1.dayOfMonth;
            }
            catch (Exception)
            {
                // 非致命：熔断检查同时看 _travelBlockedUntilDay 和 _consecutiveTravelFailures
            }
        }

        Monitor.Log($"[{TestName}] Setup complete. dayOfMonth={Game1.dayOfMonth}", LogLevel.Info);
        _setupComplete = true;
    }

    public override bool Update()
    {
        if (!_setupComplete || Navigator == null || Api == null)
        {
            return true;
        }

        // Phase 1: 尝试 NavigateToTaskLocation，应被熔断阻止
        if (CurrentTick == 30 && !_breakerTested)
        {
            _breakerTested = true;
            var npc = Game1.getCharacterFromName(NpcName);
            if (npc == null)
            {
                Assert("npc_available", false, $"{NpcName} not found");
                return true;
            }

            var startedTravel = false;
            string? result;
            try
            {
                result = Navigator.NavigateToTaskLocation(npc, s_candidateLocations, CurrentTick, out startedTravel);
            }
            catch (Exception ex)
            {
                Assert("navigate_no_throw", false, $"NavigateToTaskLocation threw: {ex.Message}");
                return true;
            }

            // 熔断开启时：startedTravel=false 且 result=null
            var blockedByBreaker = !startedTravel && result == null;
            Assert("travel_blocked_by_circuit_breaker", blockedByBreaker,
                $"startedTravel={startedTravel}, result={result ?? "(null)"}");

            // 事件/节日也会阻止旅行 — 排除该干扰
            if (!blockedByBreaker)
            {
                Assert("no_event_interference", !Game1.eventUp,
                    $"eventUp={Game1.eventUp} — 旅行阻止可能由事件而非熔断");
            }
        }

        // Phase 2: 验证 NPC 未崩溃 + 状态仍可读
        if (CurrentTick == 90 && !_asserted)
        {
            _asserted = true;
            var npc = Game1.getCharacterFromName(NpcName);
            Assert("npc_still_accessible_after_block", npc != null);

            var state = Api.GetAgentState(NpcName);
            Assert("state_still_readable", !string.IsNullOrEmpty(state), $"state={state}");
        }

        return CurrentTick >= 150;
    }

    public override void Teardown()
    {
        // 清理熔断状态，避免影响后续测试
        var failuresDict = Navigator != null
            ? ReadField<IDictionary>(Navigator, "_consecutiveTravelFailures")
            : null;
        if (failuresDict != null)
        {
            try
            {
                failuresDict.Remove(NpcName);
            }
            catch (Exception)
            {
                /* ignore */
            }
        }

        var blockedDict = Navigator != null
            ? ReadField<IDictionary>(Navigator, "_travelBlockedUntilDay")
            : null;
        if (blockedDict != null)
        {
            try
            {
                blockedDict.Remove(NpcName);
            }
            catch (Exception)
            {
                /* ignore */
            }
        }

        Api?.TrySetAgentState(NpcName, "IDLE");
        CommonTeardown();
        Monitor.Log($"[{TestName}] Teardown.", LogLevel.Info);
    }
}