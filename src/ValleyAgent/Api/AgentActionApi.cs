using System;
using System.Linq;
using StardewModdingAPI;
using StardewValley;
using ValleyAgent.Brain;
using ValleyAgent.Navigation;
using ValleyAgent.Services;
using ValleyAgent.StateMachine;
using ValleyAgent.Utils;
using Object = StardewValley.Object;

namespace ValleyAgent.Api;

/// <summary>
///     Handles agent actions: state transitions, speaking, emoting, healing, reviving, gifts, etc.
///     Extracted from ValleyAgentApi to follow single responsibility principle.
/// </summary>
public class AgentActionApi
{
    private readonly AgentService _agentService;
    private readonly DialogueStateManager _dialogueState;
    private readonly Func<string, bool>? _forceDecisionCallback;
    private readonly IMovementService? _movementService;

    public AgentActionApi(
        AgentService agentService,
        DialogueStateManager dialogueState,
        IMovementService? movementService = null,
        Func<string, bool>? forceDecisionCallback = null)
    {
        _agentService = agentService ?? throw new ArgumentNullException(nameof(agentService));
        _dialogueState = dialogueState ?? throw new ArgumentNullException(nameof(dialogueState));
        _movementService = movementService;
        _forceDecisionCallback = forceDecisionCallback;
    }

    private IMonitor? Monitor
    {
        get => _dialogueState.Monitor;
    }

    public bool TryForceDecision(string npcName) => _forceDecisionCallback != null && _forceDecisionCallback(npcName);

    public bool TrySetAgentState(string npcName, string stateName)
    {
        if (!_agentService.TryGetAgent(npcName, out var agent) || agent == null)
        {
            Monitor?.Log($"[Api] TrySetAgentState({npcName}, {stateName}): agent not found", LogLevel.Warn);
            return false;
        }

        if (!Enum.TryParse<AgentState>(stateName, true, out var state))
        {
            Monitor?.Log($"[Api] TrySetAgentState({npcName}, {stateName}): invalid state name", LogLevel.Warn);
            return false;
        }

        var prev = agent.StateMachine.CurrentStateFlag;

        // 幂等：目标状态已是当前状态 → 成功而非 BLOCKED 误报。
        // （对话中 NPC 同意“跟着我”时通常已在 FOLLOW，此处不应刷警告）
        if (prev == state)
        {
            return true;
        }

        try
        {
            var result = agent.StateMachine.ForceTransition(state, true);
            var after = agent.StateMachine.CurrentStateFlag;
            if (!result)
            {
                Monitor?.Log($"[Api] TrySetAgentState BLOCKED: {npcName} {prev}→{state} (after={after})",
                    LogLevel.Warn);
            }

            return result;
        }
        catch (InvalidOperationException ex)
        {
            Monitor?.Log(
                $"[Api] TrySetAgentState EXCEPTION: {npcName} {prev}→{state}: {ex.GetType().Name}: {ex.Message}",
                LogLevel.Error);
            return false;
        }
    }

    public bool TryTriggerGift(string npcName, out string itemName)
    {
        itemName = string.Empty;
        if (!_agentService.TryGetAgent(npcName, out var agent) || agent == null)
        {
            return false;
        }

        if (agent.Health.IsDead)
        {
            return false;
        }

        try
        {
            var inventory = agent.Inventory;
            var items = inventory.GetAllItems();
            var firstItem = items.FirstOrDefault(i => i != null);
            if (firstItem != null)
            {
                var id = firstItem is Object obj
                    ? obj.QualifiedItemId ?? obj.ItemId
                    : firstItem.ItemId;
                if (inventory.TryRemove(id, 1, out var removed) && removed != null)
                {
                    var leftover = Game1.player.addItemToInventory(removed);
                    if (leftover == null)
                    {
                        itemName = removed.DisplayName;
                        agent.Brain.SyncEmotion(NpcEmotion.Grateful, 0.5f, "GiftGiven");
                        return true;
                    }

                    _ = inventory.TryAdd(removed);
                    return false;
                }
            }

            var item = ItemRegistry.Create<Object>("(O)16");
            if (item.DisplayName.Contains("Error", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            item.Stack = 1;
            var leftover2 = Game1.player.addItemToInventory(item);
            if (leftover2 != null)
            {
                _ = Game1.createItemDebris(leftover2, Game1.player.Tile * 64f, Game1.player.FacingDirection,
                    Game1.currentLocation);
            }

            itemName = item.DisplayName;
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    public bool TrySpeak(string npcName, string text, int durationMs)
    {
        var npc = Game1.getCharacterFromName(npcName);
        if (npc == null)
        {
            return false;
        }

        NpcSpeechHelper.Speak(npc, text, durationMs);

        // E2-3：长文（>1 句）已由 SpeakToChatBar 按句间隔弹出，不再整段补发第二条镜像，
        // 避免一坨长文重复糊进聊天栏；短句保留原有 "[name] text" 风格镜像。
        if (!SpeechDisplayRouter.IsLongText(text))
        {
            Game1.chatBox?.addMessage($"[{npcName}] {text}", Game1.textColor);
        }

        if (_agentService.TryGetAgent(npcName, out var agent) && agent != null)
        {
            agent.Brain?.AddMemory($"I proactively said: \"{text}\"");
            if (agent.Brain != null)
            {
                agent.Brain.ActiveSpeaks++;
            }
        }

        return true;
    }

    public static bool TryEmote(string npcName, int emoteIndex)
    {
        var npc = Game1.getCharacterFromName(npcName);
        if (npc == null)
        {
            return false;
        }

        npc.doEmote(emoteIndex);
        return true;
    }

    public bool TryHeal(string npcName, int amount)
    {
        if (!_agentService.TryGetAgent(npcName, out var agent) || agent == null)
        {
            return false;
        }

        agent.Health.Heal(amount);
        return true;
    }

    public bool TryRevive(string npcName)
    {
        if (!_agentService.TryGetAgent(npcName, out var agent) || agent == null)
        {
            return false;
        }

        if (!agent.Health.IsDead)
        {
            return true;
        }

        agent.Health.Respawn();
        agent.StateMachine.ForceTransition(AgentState.IDLE);

        var npc = agent.RemovedNpcRef ?? Game1.getCharacterFromName(npcName);
        if (npc != null)
        {
            if (!Game1.currentLocation.characters.Contains(npc))
            {
                Game1.currentLocation.characters.Add(npc);
                npc.currentLocation = Game1.currentLocation;
            }

            agent.RemovedNpcRef = null;
            _movementService?.Stop(npc, "respawn");
            if (npc.currentLocation != Game1.currentLocation)
            {
                Game1.warpCharacter(npc, Game1.currentLocation, Game1.player.Tile);
            }
            else
            {
                npc.setTileLocation(Game1.player.Tile);
            }
        }

        return true;
    }

    public bool SetNpcHealth(string npcName, int health)
    {
        if (!_agentService.TryGetAgent(npcName, out var agent) || agent == null)
        {
            return false;
        }

        agent.Health.SetHealth(health);
        return true;
    }

    public static void SetFriendshipForNpc(string npcName, int points)
    {
        if (Game1.player.friendshipData.TryGetValue(npcName, out var fd))
        {
            fd.Points = points;
        }
        else
        {
            Game1.player.friendshipData[npcName] = new StardewValley.Friendship { Points = points };
        }
    }

    public bool FillNpcInventory(string npcName, string itemId, int count)
    {
        if (!_agentService.TryGetAgent(npcName, out var agent) || agent == null)
        {
            return false;
        }

        if (!string.IsNullOrEmpty(itemId) && itemId != "(O)0")
        {
            for (var i = 0; i < count; i++)
            {
                var item = ItemRegistry.Create(itemId);
                item.Stack = 1;
                if (!agent.Inventory.TryAdd(item))
                {
                    return false;
                }
            }

            return true;
        }

        string[] distinctIds =
        {
            "(O)390", "(O)388", "(O)334", "(O)335", "(O)336",
            "(O)80", "(O)82", "(O)84", "(O)86", "(O)92",
            "(O)283", "(O)372"
        };
        for (var i = 0; i < count && i < distinctIds.Length; i++)
        {
            var item = ItemRegistry.Create(distinctIds[i]);
            item.Stack = 1;
            if (!agent.Inventory.TryAdd(item))
            {
                return false;
            }
        }

        return true;
    }
}