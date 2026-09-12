using System;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using ValleyAgent.Multiplayer.Transports;

namespace ValleyAgent.Multiplayer;

/// <summary>
///     联机事件路由器。订阅 SMAPI Multiplayer 事件，按消息类型分发到
///     Host 端 HostRequestHandlers 或 ThinClient 端 AgentRemoteRenderer。
///     Host/ThinClient 模式互斥；单机模式不注册。
///     不做 ProtocolVersion 校验（由 ModEntry 三模式初始化阶段通过 hostMod.Version.MajorVersion 比较处理）。
/// </summary>
public class MultiplayerEventRouter
{
    private const string ModId = "dandm1.ValleyAgent";

    private readonly IModHelper _helper;
    private readonly IMonitor _monitor;

    private AgentSyncBroadcaster? _broadcaster;

    // Task 9: ThinClient 模式下额外的 Transport 路由目标。
    // 使用具体类型而非接口，因为 HandleResponse 是 Transport 特有方法（不在 IDialogueTransport/IGiftTransport 中）。
    private FarmhandDialogueTransport? _dialogueTransport;
    private FarmhandGiftTransport? _giftTransport;
    private AgentRemoteRenderer? _renderer;
    private HostRequestHandlers? _requestHandlers;

    /// <summary>构造函数。仅注入静态依赖，模式相关依赖通过 InitializeHost/InitializeThinClient 设置。</summary>
    public MultiplayerEventRouter(IModHelper helper, IMonitor monitor)
    {
        _helper = helper ?? throw new ArgumentNullException(nameof(helper));
        _monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
    }

    /// <summary>初始化主机模式：注入 Broadcaster 和可选的 Request Handlers。ThinClient 状态会被清空。</summary>
    public void InitializeHost(AgentSyncBroadcaster broadcaster, HostRequestHandlers? handlers = null)
    {
        _broadcaster = broadcaster ?? throw new ArgumentNullException(nameof(broadcaster));
        _requestHandlers = handlers;
        _renderer = null;
    }

    /// <summary>
    ///     初始化瘦客户端模式：注入 Remote Renderer 与可选的 Farmhand Transport。
    ///     Host 状态会被清空。Transport 用于在收到 DialogueResponse/GiftResponse 时
    ///     同时通知 FarmhandDialogueTransport/FarmhandGiftTransport 完成 pending 请求。
    /// </summary>
    public void InitializeThinClient(
        AgentRemoteRenderer renderer,
        FarmhandDialogueTransport? dialogueTransport = null,
        FarmhandGiftTransport? giftTransport = null)
    {
        _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
        _dialogueTransport = dialogueTransport;
        _giftTransport = giftTransport;
        _broadcaster = null;
        _requestHandlers = null;
    }

    /// <summary>订阅 SMAPI Multiplayer 事件。由 ModEntry 在模式判定后调用，避免单机模式无意义订阅。</summary>
    public void Register()
    {
        _helper.Events.Multiplayer.ModMessageReceived += OnModMessageReceived;
        _helper.Events.Multiplayer.PeerConnected += OnPeerConnected;
        _helper.Events.Multiplayer.PeerDisconnected += OnPeerDisconnected;
    }

    /// <summary>反注册所有 SMAPI Multiplayer 事件订阅。对称于 Register，重复调用安全。</summary>
    public void Unregister()
    {
        _helper.Events.Multiplayer.ModMessageReceived -= OnModMessageReceived;
        _helper.Events.Multiplayer.PeerConnected -= OnPeerConnected;
        _helper.Events.Multiplayer.PeerDisconnected -= OnPeerDisconnected;
    }

    /// <summary>
    ///     Mod 消息接收回调。按 e.Type 路由到当前模式对应的处理器。
    ///     整个分发逻辑包 try/catch，失败时记 Error 不抛，避免污染 SMAPI 事件管线。
    /// </summary>
    private void OnModMessageReceived(object? sender, ModMessageReceivedEventArgs e)
    {
        if (e.FromModID != ModId)
        {
            return;
        }

        _monitor.Log(
            $"[MultiplayerEventRouter] Received {e.Type} from player {e.FromPlayerID} (broadcaster={_broadcaster != null}, renderer={_renderer != null})");

        try
        {
            if (_broadcaster != null)
            {
                switch (e.Type)
                {
                    case MessageTypes.DialogueRequest:
                        _requestHandlers?.HandleDialogueRequest(e.ReadAs<DialogueRequestMessage>());
                        break;
                    case MessageTypes.GiftRequest:
                        _requestHandlers?.HandleGiftRequest(e.ReadAs<GiftRequestMessage>());
                        break;
                    case MessageTypes.InteractionRequest:
                        _requestHandlers?.HandleInteractionRequest(e.ReadAs<InteractionRequestMessage>());
                        break;
                    default:
                        _monitor.Log($"[MultiplayerEventRouter] Unknown message type: {e.Type}");
                        break;
                }
            }
            else if (_renderer != null)
            {
                switch (e.Type)
                {
                    case MessageTypes.AgentState:
                        _renderer.HandleAgentStateMessage(e.ReadAs<AgentStateMessage>());
                        break;
                    case MessageTypes.FullSync:
                        _renderer.HandleFullSyncMessage(e.ReadAs<FullSyncMessage>());
                        break;
                    case MessageTypes.DialogueResponse:
                    {
                        // e.ReadAs<T>() 只能消费一次，先读出消息再分别传给 renderer 与 transport。
                        var msg = e.ReadAs<DialogueResponseMessage>();
                        _renderer.HandleDialogueResponse(msg);
                        _dialogueTransport?.HandleResponse(msg);
                        break;
                    }
                    case MessageTypes.GiftResponse:
                    {
                        var msg = e.ReadAs<GiftResponseMessage>();
                        _renderer.HandleGiftResponse(msg);
                        _giftTransport?.HandleResponse(msg);
                        break;
                    }
                    case MessageTypes.NpcAction:
                        _renderer.HandleNpcAction(e.ReadAs<NpcActionMessage>());
                        break;
                    default:
                        _monitor.Log($"[MultiplayerEventRouter] Unknown message type: {e.Type}");
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            _monitor.Log($"[MultiplayerEventRouter] Failed to handle {e.Type}: {ex}", LogLevel.Error);
        }
    }

    /// <summary>
    ///     Peer 连接回调。Host 模式触发全量状态同步给新加入的 Farmhand；
    ///     ThinClient 模式无操作。异常防护避免单次同步失败影响后续事件。
    /// </summary>
    private void OnPeerConnected(object? sender, PeerConnectedEventArgs e)
    {
        try
        {
            _monitor.Log(
                $"[MultiplayerEventRouter] Peer connected: {e.Peer.PlayerID}, broadcaster={_broadcaster != null}, renderer={_renderer != null}",
                LogLevel.Debug);
            if (_broadcaster != null)
            {
                _broadcaster.SendFullSync(e.Peer.PlayerID);
            }
            // ThinClient 模式：无操作
        }
        catch (Exception ex)
        {
            _monitor.Log($"[MultiplayerEventRouter] OnPeerConnected failed: {ex}", LogLevel.Error);
        }
    }

    /// <summary>
    ///     Peer 断开回调。当前无 per-peer 状态需清理，预留方法体仅打 Trace log，
    ///     后续如需清理远程 Farmhand 缓存可在此扩展。
    /// </summary>
    private void OnPeerDisconnected(object? sender, PeerDisconnectedEventArgs e) =>
        _monitor.Log($"[MultiplayerEventRouter] Peer disconnected: {e.Peer.PlayerID}");
}