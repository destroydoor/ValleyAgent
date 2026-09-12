using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using ValleyAgent.Friendship;
using ValleyAgent.Services;
using ValleyAgent.Utils;

namespace ValleyAgent.Commands;

public class SpeakCommand : AgentCommandBase
{
    public SpeakCommand(IMonitor monitor) : base(monitor)
    {
    }

    public override string CommandName
    {
        get => "speak";
    }

    public override void Execute(NPC npc, AgentInstance agent, Dictionary<string, object> parameters,
        Func<string, string, bool, Dictionary<string, object>, Task> sendResult)
    {
        if (parameters.TryGetValue("text", out var textObj) && textObj is string text)
        {
            Monitor.Log($"[CommandExecutor] {npc.Name} says: {text}", LogLevel.Debug);

            var durationMs = GetParamAny(parameters, "duration_ms", "durationMs") is int ms ? ms :
                GetParamAny(parameters, "duration_ms", "durationMs") is string msStr &&
                int.TryParse(msStr, out var parsed) ? parsed : 0;

            // E2-3 长文本规则：聊天栏由路由统一处理（短句立即单条；>1 句按句间隔弹出）
            SpeechDisplayRouter.SpeakToChatBar(npc.Name, text, Color.Gold);

            // 气泡仅限 ≤1 句（且气泡主开关开启）+ 玩家近距离
            if (SpeechDisplayRouter.CanUseBubble(text) &&
                npc.currentLocation == Game1.currentLocation &&
                Vector2.Distance(npc.Tile, Game1.player.Tile) <= 3f)
            {
                if (durationMs > 0)
                {
                    npc.showTextAboveHead(text, duration: durationMs);
                }
                else
                {
                    npc.showTextAboveHead(text);
                }
            }

            _ = sendResult(npc.Name, "speak", true,
                new Dictionary<string, object> { ["text"] = text });
        }
        else
        {
            _ = sendResult(npc.Name, "speak", false,
                new Dictionary<string, object> { ["error"] = "Missing 'text' parameter" });
        }
    }
}

public class EmoteCommand : AgentCommandBase
{
    private static readonly Dictionary<string, int> EmoteMap = new(StringComparer.OrdinalIgnoreCase)
    {
        { "happy", 20 }, { "hooray", 20 }, { "exclamation", 20 }, { "wave", 20 },
        { "heart", 8 }, { "love", 8 },
        { "music", 12 }, { "note", 12 },
        { "star", 60 },
        { "stretch", 24 }, { "yawn", 24 },
        { "question", 4 }, { "confused", 4 },
        { "elipses", 16 }, { "thinking", 16 }, { "annoyed", 16 },
        { "sad", 32 }, { "sweat", 32 }, { "worried", 32 },
        { "angry", 28 }, { "frustrated", 28 }, { "vex", 28 },
        { "skull", 52 }, { "dead", 52 },
        { "blocked", 56 }, { "x", 56 }, { "cancel", 56 },
        { "sleep", 40 }, { "zzz", 40 },
        { "fish", 36 },
        { "gift", 32 }, { "present", 32 },
        { "bomb", 68 }
    };

    public EmoteCommand(IMonitor monitor) : base(monitor)
    {
    }

    public override string CommandName
    {
        get => "emote";
    }

    public override void Execute(NPC npc, AgentInstance agent, Dictionary<string, object> parameters,
        Func<string, string, bool, Dictionary<string, object>, Task> sendResult)
    {
        var emoteId = -1;

        if (GetParamAny(parameters, "emote_id", "emote", "emoteId") is string emoteStr)
        {
            if (EmoteMap.TryGetValue(emoteStr, out emoteId))
            {
                Monitor.Log($"[CommandExecutor] {npc.Name} emote '{emoteStr}' -> ID {emoteId}", LogLevel.Debug);
            }
            else
            {
                if (int.TryParse(emoteStr, out emoteId))
                {
                    Monitor.Log($"[CommandExecutor] {npc.Name} emote ID {emoteId} (from string)", LogLevel.Debug);
                }
                else
                {
                    Monitor.Log($"[CommandExecutor] Unknown emote '{emoteStr}' for {npc.Name}", LogLevel.Warn);
                    _ = sendResult(npc.Name, "emote", false,
                        new Dictionary<string, object> { ["error"] = $"Unknown emote: {emoteStr}" });
                    return;
                }
            }
        }
        else
        {
            var rawId = GetParamAny(parameters, "emoteId", "emote_id", "emote");
            if (rawId != null)
            {
                emoteId = Convert.ToInt32(rawId);
                Monitor.Log($"[CommandExecutor] {npc.Name} emote ID {emoteId}", LogLevel.Debug);
            }
        }

        if (emoteId >= 0)
        {
            npc.doEmote(emoteId);

            var durationMs = Convert.ToInt32(GetParamAny(parameters, "duration_ms", "durationMs", 2000));
            var bubbleText = GetParamAny(parameters, "text", "bubble_text") as string;
            if (!string.IsNullOrWhiteSpace(bubbleText))
            {
                npc.showTextAboveHead(bubbleText, duration: durationMs);
            }

            _ = sendResult(npc.Name, "emote", true,
                new Dictionary<string, object> { ["emoteId"] = emoteId });
        }
        else
        {
            _ = sendResult(npc.Name, "emote", false,
                new Dictionary<string, object> { ["error"] = "Missing emote_id/emote/emoteId parameter" });
        }
    }
}

public class ShowDialogueCommand : AgentCommandBase
{
    public ShowDialogueCommand(IMonitor monitor) : base(monitor)
    {
    }

    public override string CommandName
    {
        get => "show_dialogue";
    }

    public override void Execute(NPC npc, AgentInstance agent, Dictionary<string, object> parameters,
        Func<string, string, bool, Dictionary<string, object>, Task> sendResult)
    {
        var text = GetParamAny(parameters, "text", "message") as string;
        var style = (GetParamAny(parameters, "style", "type") as string)?.ToLowerInvariant() ?? "bubble";
        var durationMs = Convert.ToInt32(GetParamAny(parameters, "duration_ms", "durationMs", 3000));

        if (string.IsNullOrWhiteSpace(text))
        {
            _ = sendResult(npc.Name, "show_dialogue", false,
                new Dictionary<string, object> { ["error"] = "Missing 'text' parameter" });
            return;
        }

        Monitor.Log($"[CommandExecutor] {npc.Name} showing dialogue (style={style}): {text}", LogLevel.Debug);

        switch (style)
        {
            case "dialogue_box":
            case "dialoguebox":
                // E2-3 不拦截：LLM 显式请求的交互式对话框，玩家按回车翻页、节奏自控
                var dialogue = new StardewValley.Dialogue(npc, null, text);
                npc.setNewDialogue(dialogue);
                Game1.drawDialogue(npc);
                break;

            case "chat":
                // E2-3：聊天栏由路由统一处理（短句立即；>1 句按句间隔弹出）
                SpeechDisplayRouter.SpeakToChatBar(npc.Name, text, Color.Gold);
                break;

            case "bubble":
            default:
                // E2-3 长文本规则：≤1 句才可气泡；>1 句绝不上气泡、降级走聊天栏分句
                if (SpeechDisplayRouter.CanUseBubble(text))
                {
                    npc.showTextAboveHead(text, duration: durationMs);
                }
                else
                {
                    SpeechDisplayRouter.SpeakToChatBar(npc.Name, text, Color.Gold);
                }

                break;
        }

        _ = sendResult(npc.Name, "show_dialogue", true,
            new Dictionary<string, object> { ["text"] = text, ["style"] = style });
    }
}

public class SetFriendshipCommand : AgentCommandBase
{
    public SetFriendshipCommand(IMonitor monitor) : base(monitor)
    {
    }

    public override string CommandName
    {
        get => "set_friendship";
    }

    public override IReadOnlyList<string> Aliases
    {
        get => new[] { "update_friendship" };
    }

    public FriendshipSystem? FriendshipSystem { get; set; }

    public override void Execute(NPC npc, AgentInstance agent, Dictionary<string, object> parameters,
        Func<string, string, bool, Dictionary<string, object>, Task> sendResult)
    {
        var rawFriendship = GetParamAny(parameters, "friendship", "friendship_points");
        var rawDelta = GetParamAny(parameters, "delta", "change");

        if (Game1.player.friendshipData.TryGetValue(npc.Name, out var fsData))
        {
            if (rawFriendship != null)
            {
                var targetPoints = Convert.ToInt32(rawFriendship);
                var oldPoints = fsData.Points;
                if (FriendshipSystem != null)
                {
                    var fsContext = new FriendshipChangeContext
                    {
                        NpcName = npc.Name,
                        InteractionType = InteractionType.Conversation,
                        CurrentFriendshipPoints = oldPoints,
                        MaxFriendshipPoints = 2500,
                        CurrentDateKey = $"{Game1.currentSeason}_{Game1.dayOfMonth}_Year{Game1.year}"
                    };
                    var delta = targetPoints - oldPoints;
                    var fsResult = FriendshipSystem.ApplyDirectChange(fsContext, delta, "SetFriendshipCommand");
                    fsData.Points = Math.Clamp(fsResult.NewPoints, 0, 2500);
                }
                else
                {
                    fsData.Points = Math.Clamp(targetPoints, 0, 2500);
                }

                Monitor.Log($"[CommandExecutor] {npc.Name} friendship set to {fsData.Points} (was {oldPoints})",
                    LogLevel.Debug);
            }
            else if (rawDelta != null)
            {
                var delta = Convert.ToInt32(rawDelta);
                var oldPoints = fsData.Points;
                if (FriendshipSystem != null)
                {
                    var fsContext = new FriendshipChangeContext
                    {
                        NpcName = npc.Name,
                        InteractionType = InteractionType.Conversation,
                        CurrentFriendshipPoints = oldPoints,
                        MaxFriendshipPoints = 2500,
                        CurrentDateKey = $"{Game1.currentSeason}_{Game1.dayOfMonth}_Year{Game1.year}"
                    };
                    var fsResult = FriendshipSystem.ApplyDirectChange(fsContext, delta, "SetFriendshipCommand:delta");
                    fsData.Points = Math.Clamp(fsResult.NewPoints, 0, 2500);
                }
                else
                {
                    fsData.Points = Math.Clamp(oldPoints + delta, 0, 2500);
                }

                Monitor.Log($"[CommandExecutor] {npc.Name} friendship {oldPoints} + {delta} = {fsData.Points}",
                    LogLevel.Debug);
            }

            _ = sendResult(npc.Name, "set_friendship", true,
                new Dictionary<string, object> { ["friendship"] = fsData.Points });
        }
        else
        {
            Monitor.Log($"[CommandExecutor] No friendship data for {npc.Name}", LogLevel.Warn);
            _ = sendResult(npc.Name, "set_friendship", false,
                new Dictionary<string, object> { ["error"] = $"No friendship data for {npc.Name}" });
        }
    }
}