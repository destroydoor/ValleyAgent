#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;

namespace ValleyAgent.TestMod.Tests.Integration;

/// <summary>
///     IT03：验证 F3 旅行失败恢复机制。
///     旅行失败恢复包含两个独立职责：
///     (a) OnTravelFailed 回调 → ForceTransition(IDLE, reason="travel_failed")
///     (b) CancelTravel → 清除 IsInvisible（NPC 重新可见），保险兜底 warpCharacter 回出发位置
///     本测试通过反射设置 AgentNavigator 内部状态（_travelStates + _hiddenNpcs），
///     模拟旅行中 HideNpc 状态，然后分别触发 OnTravelFailed 和 CancelTravel，
///     真实验证状态转换和恢复。
/// </summary>
public class IT03_F3_TravelFailureRecovery : IntegrationTestBase
{
    private bool _asserted;
    private bool _cancelled;
    private string _departureLocation = "";
    private Point _departureTile;
    private bool _triggered;

    public IT03_F3_TravelFailureRecovery(IModHelper helper, IMonitor monitor) : base(helper, monitor)
    {
    }

    public override string TestName
    {
        get => "IT03_F3_TravelFailureRecovery";
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

        // 起点 FOLLOW，确保 OnTravelFailed 触发后转 IDLE 是真实转换
        _ = Api!.TrySetAgentState(NpcName, "FOLLOW");

        var npc = Game1.getCharacterFromName(NpcName);
        if (npc == null)
        {
            Skip($"{NpcName} not found");
            return;
        }

        // 记录出发位置（TryCommonSetup 已把 NPC 放在 Farm 54,16）
        _departureTile = npc.TilePoint;
        _departureLocation = npc.currentLocation.NameOrUniqueName;

        // 通过反射设置 AgentNavigator 内部状态，模拟旅行中 HideNpc 状态。
        // CancelTravel 只在 _travelStates 有条目且 _hiddenNpcs 包含 NPC 时才恢复位置。
        // 不设置这些内部状态的话，CancelTravel 只调 ShowNpc，不会 warpCharacter。
        if (!TrySetupTravelState(npc))
        {
            Skip("Failed to setup travel state via reflection");
            return;
        }

        // 模拟 HideNpc 隐藏 NPC（IsInvisible 隐藏精灵，不再挪到 (-1000,-1000)）
        npc.IsInvisible = true;

        Monitor.Log(
            $"[{TestName}] Setup complete. departure={_departureLocation}({_departureTile.X},{_departureTile.Y}), " +
            $"OnTravelFailed bound={Navigator?.OnTravelFailed != null}",
            LogLevel.Info);
    }

    /// <summary>
    ///     通过反射在 AgentNavigator._travelStates 中创建 TravelState 条目，
    ///     并把 NPC 名加入 _hiddenNpcs，模拟旅行中 HideNpc 状态。
    /// </summary>
    private bool TrySetupTravelState(NPC npc)
    {
        if (Navigator == null)
        {
            return false;
        }

        var navigatorType = Navigator.GetType();

        // 获取 _travelStates 字段（Dictionary<string, TravelState>）
        var travelStatesField = navigatorType.GetField("_travelStates",
            BindingFlags.Instance | BindingFlags.NonPublic);
        if (travelStatesField == null)
        {
            Monitor.Log($"[{TestName}] _travelStates field not found", LogLevel.Error);
            return false;
        }

        // 获取 _hiddenNpcs 字段（HashSet<string>）
        var hiddenNpcsField = navigatorType.GetField("_hiddenNpcs",
            BindingFlags.Instance | BindingFlags.NonPublic);
        if (hiddenNpcsField == null)
        {
            Monitor.Log($"[{TestName}] _hiddenNpcs field not found", LogLevel.Error);
            return false;
        }

        // TravelState 是私有嵌套类
        var travelStateType = navigatorType.GetNestedType("TravelState", BindingFlags.NonPublic);
        if (travelStateType == null)
        {
            Monitor.Log($"[{TestName}] TravelState nested type not found", LogLevel.Error);
            return false;
        }

        // 创建 TravelState 实例并设置属性
        var travelState = Activator.CreateInstance(travelStateType);
        travelStateType.GetProperty("IsTravelling")?.SetValue(travelState, true);
        travelStateType.GetProperty("DepartureLocation")?.SetValue(travelState, _departureLocation);
        travelStateType.GetProperty("DepartureNpcTile")?.SetValue(travelState, _departureTile);
        travelStateType.GetProperty("TargetLocation")?.SetValue(travelState, "Forest");
        travelStateType.GetProperty("Phase")?.SetValue(travelState, 1); // TravelPhase.Travelling = 1

        // 添加到 _travelStates
        if (travelStatesField.GetValue(Navigator) is IDictionary travelStatesDict)
        {
            travelStatesDict[NpcName] = travelState;
        }
        else
        {
            Monitor.Log($"[{TestName}] _travelStates is not IDictionary", LogLevel.Error);
            return false;
        }

        // 添加到 _hiddenNpcs
        if (hiddenNpcsField.GetValue(Navigator) is HashSet<string> hiddenNpcsSet)
        {
            _ = hiddenNpcsSet.Add(NpcName);
        }
        else
        {
            Monitor.Log($"[{TestName}] _hiddenNpcs is not HashSet<string>", LogLevel.Error);
            return false;
        }

        return true;
    }

    public override bool Update()
    {
        if (Api == null || Navigator == null)
        {
            return true;
        }

        // Phase 1: 触发 OnTravelFailed 回调（验证 ForceTransition 契约）
        if (CurrentTick == 30 && !_triggered)
        {
            _triggered = true;

            var callbackBound = Navigator.OnTravelFailed != null;
            Assert("on_travel_failed_callback_bound", callbackBound,
                callbackBound
                    ? "callback injected by ServiceInitializer"
                    : "callback is null — ServiceInitializer did not wire OnTravelFailed");

            if (callbackBound)
            {
                try
                {
                    Navigator.OnTravelFailed?.Invoke(NpcName);
                }
                catch (Exception ex)
                {
                    Assert("on_travel_failed_no_throw", false, $"OnTravelFailed threw: {ex.Message}");
                }
            }
        }

        // Phase 2: 调用 CancelTravel（验证位置恢复逻辑）
        if (CurrentTick == 60 && !_cancelled)
        {
            _cancelled = true;
            try
            {
                Navigator.CancelTravel(NpcName);
                Monitor.Log($"[{TestName}] CancelTravel called", LogLevel.Info);
            }
            catch (Exception ex)
            {
                Assert("cancel_travel_no_throw", false, $"CancelTravel threw: {ex.Message}");
            }
        }

        // Phase 3: 验证状态转 IDLE + 位置恢复
        if (CurrentTick == 90 && !_asserted)
        {
            _asserted = true;

            var state = Api.GetAgentState(NpcName);
            Assert("state_transitions_to_idle", state == "IDLE",
                $"state={state} (expected IDLE after travel_failed)");

            // 真实验证恢复：CancelTravel 应清除 IsInvisible（NPC 重新可见、仍可访问），
            // 且 NPC 一直留在出发位置（HideNpc 不再挪动 Position）。
            var npc = Game1.getCharacterFromName(NpcName);
            var restored = npc != null && !npc.IsInvisible;
            Assert("npc_restored_after_cancel", restored,
                restored
                    ? $"IsInvisible cleared after CancelTravel (pos=({npc?.Position.X ?? 0f},{npc?.Position.Y ?? 0f}))"
                    : npc == null
                        ? "NPC not accessible after CancelTravel"
                        : "NPC still invisible — CancelTravel did not clear IsInvisible");

            // NPC 实例仍可访问（未因旅行失败被销毁）
            Assert("npc_still_accessible", npc != null);
        }

        return CurrentTick >= 150;
    }

    public override void Teardown()
    {
        Api?.TrySetAgentState(NpcName, "IDLE");
        CommonTeardown();
        Monitor.Log($"[{TestName}] Teardown.", LogLevel.Info);
    }
}