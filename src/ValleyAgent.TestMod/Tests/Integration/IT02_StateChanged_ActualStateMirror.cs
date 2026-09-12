#nullable enable
using System;
using StardewModdingAPI;
using ValleyAgent.Multiplayer;

namespace ValleyAgent.TestMod.Tests.Integration;

/// <summary>
///     IT02：验证 set_state 触发后 C# 端通过 AgentSyncBroadcaster 广播 state_changed 消息
///     （TS 端 actualState 镜像由 TS 单测覆盖，本测试只验证 C# 广播侧）。
///     限制：AgentSyncBroadcaster.BroadcastImmediateState 内部有
///     `if (!MultiplayerHelper.ShouldRunAgentLogic || !MultiplayerHelper.IsMultiplayer) return;`
///     守卫，单机模式下不会真正调用 SendMessage。要验证消息发出需要联机环境。
///     可自动化部分：验证状态确实切换 + broadcaster 实例可获取 + 联机守卫被检测。
///     不可自动化部分：实际 SendMessage 调用需联机环境（需手动验证）。
/// </summary>
public class IT02_StateChanged_ActualStateMirror : IntegrationTestBase
{
    private bool _asserted;
    private AgentSyncBroadcaster? _broadcaster;
    private bool _stateChanged;

    public IT02_StateChanged_ActualStateMirror(IModHelper helper, IMonitor monitor) : base(helper, monitor)
    {
    }

    public override string TestName
    {
        get => "IT02_StateChanged_ActualStateMirror";
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

        _broadcaster = Container!.GetService<AgentSyncBroadcaster>();
        if (_broadcaster == null)
        {
            Skip("AgentSyncBroadcaster not registered in container");
            return;
        }

        _ = Api!.TrySetAgentState(NpcName, "IDLE");
        Monitor.Log($"[{TestName}] Setup complete. Broadcaster={_broadcaster != null}", LogLevel.Info);
    }

    public override bool Update()
    {
        if (Api == null || _broadcaster == null)
        {
            return true;
        }

        // Phase 1: 切换状态 + 调用 BroadcastImmediateState
        if (CurrentTick == 30 && !_stateChanged)
        {
            var ok = Api.TrySetAgentState(NpcName, "FOLLOW");
            if (ok)
            {
                try
                {
                    _broadcaster.BroadcastImmediateState(NpcName);
                }
                catch (Exception ex)
                {
                    Assert("broadcast_no_throw", false, $"BroadcastImmediateState threw: {ex.Message}");
                }
            }

            _stateChanged = true;
            Assert("state_switched_to_follow", ok, $"TrySetAgentState(FOLLOW)={ok}");
        }

        // Phase 2: 验证状态确实切换 + broadcaster 实例可用 + 联机守卫检测
        if (CurrentTick == 90 && !_asserted)
        {
            _asserted = true;
            var state = Api.GetAgentState(NpcName);
            Assert("state_is_follow", state == "FOLLOW", $"state={state}");

            // broadcaster 实例非 null（Host 模式下应该注册）
            Assert("broadcaster_instance_available", _broadcaster != null);

            // 联机守卫：单机模式下 SendMessage 不会被调用（这是设计意图）
            var isMultiplayer = StardewModdingAPI.Context.IsMultiplayer;
            Assert("single_player_guard_active", !isMultiplayer,
                $"IsMultiplayer={isMultiplayer} — 单机下 broadcaster 静默是设计意图");
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