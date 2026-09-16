using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using StardewModdingAPI;
using StardewValley;
using ValleyAgent.Config;
using ValleyAgent.Resilience;
using ValleyAgent.Services;
using ValleyAgent.StateMachine;
using ValleyAgent.WebSocket;
using SmaLogLevel = StardewModdingAPI.LogLevel;

namespace ValleyAgent.Debug;

/// <summary>
///     Handles the ValleyAgent_chat console command.
///     Allows chatting with an Agent NPC.
/// </summary>
public class ChatHandler : CommandHandlerBase
{
    private readonly DualPathAgentServerProvider? _agentServerProvider;
    private readonly CircuitBreaker? _circuitBreaker;

    public ChatHandler(
        IMonitor? monitor,
        AgentService? agentService,
        ModConfig? config,
        DualPathAgentServerProvider? agentServerProvider,
        CircuitBreaker? circuitBreaker)
        : base(monitor, agentService, config)
    {
        _agentServerProvider = agentServerProvider;
        _circuitBreaker = circuitBreaker;
    }

    public Task<CommandResult> HandleAsync(string npcName, string message)
    {
        return SafeExecuteAsync(async () =>
        {
            var serviceCheck = EnsureServiceAvailable(_agentService, "AgentService");
            if (!serviceCheck.Success)
            {
                return serviceCheck;
            }

            if (!_agentService!.TryGetAgent(npcName, out var agent) || agent == null)
            {
                return CommandResult.Fail($"'{npcName}' is not an active Agent.");
            }

            // CircuitBreaker 检查：OPEN 状态下返回降级提示，避免 LLM 雪崩
            // P0-3: 使用 TryAcquireExecution 原子获取执行权，避免 HALF_OPEN 并发失控
            var circuitBreaker = _agentService.CircuitBreaker;
            var acquired = false;
            if (circuitBreaker != null && !circuitBreaker.TryAcquireExecution())
            {
                _monitor?.Log(
                    $"Chat skipped for {npcName}: circuit breaker OPEN (failures={circuitBreaker.ConsecutiveFailures})",
                    SmaLogLevel.Warn);
                return CommandResult.Fail($"{npcName} 暂时无法回应（系统降级保护中，稍后再试）");
            }

            acquired = circuitBreaker != null;

            var sw = Stopwatch.StartNew();

            string resultText;
            string? resultAction = null;

            try
            {
                if (_agentServerProvider != null)
                {
                    var friendshipPoints = 0;
                    if (Game1.player.friendshipData.TryGetValue(agent.NpcName, out var fd))
                    {
                        friendshipPoints = fd.Points;
                    }

                    var chatNpc = Game1.getCharacterFromName(agent.NpcName);
                    var location = chatNpc?.currentLocation?.Name ?? Game1.currentLocation?.Name ?? "Unknown";
                    var weather = Game1.isRaining ? Game1.isLightning ? "Stormy" : "Rainy" :
                        Game1.isSnowing ? "Snowy" : "Sunny";

                    var worldSnapshot = new WorldSnapshot(
                        Game1.currentSeason,
                        Game1.dayOfMonth,
                        $"{Game1.timeOfDay / 100:D2}:{Game1.timeOfDay % 100:D2}",
                        weather,
                        location,
                        chatNpc != null
                            ? new TilePosition((int)chatNpc.Tile.X, (int)chatNpc.Tile.Y)
                            : new TilePosition(0, 0),
                        GetNearbyObjectsForDialogue(chatNpc),
                        friendshipPoints,
                        "IDLE",
                        Game1.player.Items.Where(i => i != null).Select(i => new InventoryItem(i.Name, i.Stack))
                            .ToList(),
                        Game1.player.Name
                    );

                    var request = new DialogueRequest(
                        "dialogue",
                        Guid.NewGuid().ToString("N"),
                        agent.NpcName,
                        message,
                        worldSnapshot,
                        Game1.player.UniqueMultiplayerID.ToString()
                    );

                    var response = await _agentServerProvider.GenerateDialogueAsync(request).ConfigureAwait(false);
                    resultText = response.Speech;
                    resultAction = response.Actions.Count > 0 ? response.Actions[0].Tool : null;
                }
                else
                {
                    throw new InvalidOperationException(
                        $"[ValleyAgent] Agent Server not initialized. Cannot generate dialogue for {agent.NpcName}.");
                }
            }
            catch (Exception ex)
            {
                // P0-3: 聊天失败时记录熔断器失败
                if (acquired)
                {
                    circuitBreaker?.RecordFailure("chat_error");
                }

                _monitor?.Log($"[Chat] {npcName}: LLM call failed — {ex}",
                    SmaLogLevel.Error);
                return CommandResult.Fail($"{npcName} 暂时无法回应（{ex.Message}）");
            }

            sw.Stop();

            // CircuitBreaker 记录成功 + 响应时间
            circuitBreaker?.RecordSuccess();
            circuitBreaker?.RecordResponseTime(sw.Elapsed);

            Game1.chatBox.addInfoMessage($"{npcName}: {resultText}");

            if (!string.IsNullOrEmpty(resultAction))
            {
                if (Enum.TryParse<AgentState>(resultAction, true, out var targetState))
                {
                    var chatNpc = Game1.getCharacterFromName(npcName);
                    if (chatNpc != null && !IsTransitionValid(chatNpc, targetState, _monitor))
                    {
                        _monitor?.Log(
                            $"[ChatAction] Guard denied {npcName}: {resultAction} invalid at '{chatNpc.currentLocation?.NameOrUniqueName ?? "?"}'",
                            SmaLogLevel.Warn);
                    }
                    else
                    {
                        agent.StateMachine.ForceTransition(targetState);
                        _monitor?.Log($"[ChatAction] {npcName} -> {resultAction}", SmaLogLevel.Info);
                    }
                }
            }

            _monitor?.Log($"[Chat] {npcName} -> {resultText} ({sw.Elapsed.TotalMilliseconds:F0}ms)", SmaLogLevel.Debug);
            return CommandResult.Ok($"{npcName}: {resultText}");
        }, "Chat");
    }
}