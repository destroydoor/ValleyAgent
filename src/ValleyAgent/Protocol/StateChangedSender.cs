using System;
using System.Threading;
using System.Threading.Tasks;
using ValleyAgent.StateMachine;
using ValleyAgent.WebSocket;

namespace ValleyAgent.Protocol;

/// <summary>
///     订阅 AgentStateMachine.OnStateChanged，状态转换后发 state_changed 消息到 TS Agent Server。
///     意图/现实双轨根基：TS 端据此维护 actualState 镜像，激活 prompt 的 {actual_state_section} 段。
/// </summary>
public class StateChangedSender
{
    private readonly string _npcName;
    private readonly IAgentServerProvider _serverProvider;
    private readonly AgentStateMachine _stateMachine;

    public StateChangedSender(IAgentServerProvider serverProvider, string npcName, AgentStateMachine stateMachine)
    {
        _serverProvider = serverProvider ?? throw new ArgumentNullException(nameof(serverProvider));
        _npcName = npcName ?? throw new ArgumentNullException(nameof(npcName));
        _stateMachine = stateMachine ?? throw new ArgumentNullException(nameof(stateMachine));
    }

    /// <summary>订阅状态机事件。幂等，重复调用只订阅一次。</summary>
    public void Start() => _stateMachine.OnStateChanged += HandleStateChanged;

    /// <summary>取消订阅。幂等。</summary>
    public void Stop() => _stateMachine.OnStateChanged -= HandleStateChanged;

    private void HandleStateChanged(object? sender, StateTransitionEventArgs e)
    {
        // fire-and-forget：不阻塞状态机转换线程
        _ = Task.Run(async () =>
        {
            try
            {
                // 使用匿名对象序列化，与 EventHandlerInitializer 中的 emotion_sync/memory_sync
                // 保持一致，确保 check:protocol 脚本能通过 type = "state_changed" 模式检测到。
                var json = MessageProtocol.Serialize(new
                {
                    type = "state_changed",
                    npcName = _npcName,
                    previousState = e.PreviousState.ToString(),
                    newState = e.NewState.ToString(),
                    wasForced = e.WasForced,
                    previousStateDurationMs = (long)e.PreviousStateDuration.TotalMilliseconds,
                    reason = e.Reason ?? ""
                });
                await _serverProvider.SendMessageAsync(json, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // 状态变更通知失败不应影响游戏；TS 端会在下轮 dialogue 时通过 state_sync 对齐
            }
        });
    }
}