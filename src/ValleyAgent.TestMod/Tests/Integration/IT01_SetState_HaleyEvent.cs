#nullable enable
using System;
using System.Collections.Generic;
using StardewModdingAPI;
using ValleyAgent.WebSocket;

namespace ValleyAgent.TestMod.Tests.Integration;

/// <summary>
///     IT01：验证 LLM 返回 set_state FOLLOW action 时，CommandExecutor 正确驱动
///     状态机切换到 FOLLOW（非桩）。覆盖 P0 set_state action 接线。
///     验证点：
///     1. ExecuteAction(set_state, FOLLOW) 后 GetAgentState == "FOLLOW"
///     2. 状态机真正切换（非 IDLE），证明走的是 ForceTransition 而非 no-op
///     3. 转换后状态稳定（再读一次仍为 FOLLOW）
///     注意：本测试直接调 CommandExecutor.ExecuteAction，绕过 WS dialogue 管道。
///     dialogue → action 的完整 WS 链路需 ValleyAgent 连到 mock server（环境依赖），
///     由 PIPE003/PIPE005 覆盖。
/// </summary>
public class IT01_SetState_HaleyEvent : IntegrationTestBase
{
    private bool _actionExecuted;
    private CommandExecutor? _executor;
    private bool _stateAsserted;

    public IT01_SetState_HaleyEvent(IModHelper helper, IMonitor monitor) : base(helper, monitor)
    {
    }

    public override string TestName
    {
        get => "IT01_SetState_HaleyEvent";
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

        _executor = Container!.GetService<CommandExecutor>();
        if (_executor == null)
        {
            Skip("CommandExecutor not registered in container");
            return;
        }

        // 起点 IDLE，确保后续切换到 FOLLOW 是真实转换
        _ = Api!.TrySetAgentState(NpcName, "IDLE");
        Monitor.Log($"[{TestName}] Setup complete. Executor={_executor != null}", LogLevel.Info);
    }

    public override bool Update()
    {
        if (_executor == null || Api == null)
        {
            return true;
        }

        // Phase 1: 触发 set_state FOLLOW action
        if (CurrentTick == 30 && !_actionExecuted)
        {
            var args = new Dictionary<string, object>
            {
                ["state"] = "FOLLOW"
            };
            var action = new ToolAction("set_state", args, "it01-call-1");
            try
            {
                _executor.ExecuteAction(action, NpcName);
                _actionExecuted = true;
                Monitor.Log($"[{TestName}] ExecuteAction(set_state, FOLLOW) returned", LogLevel.Info);
            }
            catch (Exception ex)
            {
                Assert("action_executed_no_throw", false, $"ExecuteAction threw: {ex.Message}");
                return true;
            }
        }

        // Phase 2: 验证状态切换
        if (CurrentTick == 90 && _actionExecuted && !_stateAsserted)
        {
            _stateAsserted = true;
            var state = Api.GetAgentState(NpcName);
            Assert("state_is_FOLLOW", state == "FOLLOW",
                $"state={state} (expected FOLLOW)");

            // 二次读取确认稳定
            var state2 = Api.GetAgentState(NpcName);
            Assert("state_stable", state2 == "FOLLOW",
                $"second read state={state2}");

            // 确认不是 IDLE（避免 no-op 路径误判通过）
            AssertEx("state_not_IDLE", state != "IDLE",
                "set_state 动作是 no-op：CommandExecutor 未把 FOLLOW 写进状态机，GetAgentState 仍返回 IDLE",
                "state was IDLE — action was a no-op");
        }

        return CurrentTick >= 150;
    }

    public override void Teardown()
    {
        // 恢复 IDLE，避免影响后续测试
        Api?.TrySetAgentState(NpcName, "IDLE");
        CommonTeardown();
        Monitor.Log($"[{TestName}] Teardown.", LogLevel.Info);
    }
}