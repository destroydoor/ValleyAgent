using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using ValleyAgent.i18n;
using ValleyAgent.Navigation;
using ValleyAgent.Services;
using ValleyAgent.StateMachine;
using ValleyAgent.Utils;

namespace ValleyAgent.Handlers;

/// <summary>
///     Handles NPC talking behavior: approach the player and initiate dialogue.
/// </summary>
public class TalkHandler : HandlerBase
{
    private const int DebugLogCooldownTicks = 60;
    private const int ExitDistance = 8;
    private const int MaxTalkDurationTicks = 1800;
    private const int TalkCooldownTicks = 3600;
    private readonly Dictionary<string, int> _lastDebugLogTick = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _talkEntryTick = new(StringComparer.OrdinalIgnoreCase);
    private readonly ITranslationProvider? _translation;

    public TalkHandler(IMonitor? monitor, IMovementService movementService, ITranslationProvider? translation = null,
        object? _ = null) : base(monitor, movementService)
    {
        _translation = translation;
    }

    protected override int DefaultActionCooldownTicks
    {
        get => TalkCooldownTicks;
    }

    /// <summary>
    ///     Gets a random greeting line for the NPC to speak in TALK state.
    ///     Picks from i18n greeting pool (DIALOG_Talk_0..6).
    /// </summary>
    private string GetRandomGreeting()
    {
        if (_translation != null)
        {
            var i = RandomNumberGenerator.GetInt32(7);
            var key = $"DIALOG_Talk_{i}";
            var value = _translation.GetString(key);

            if (!string.IsNullOrEmpty(value)
                && !value.StartsWith("DIALOG_", StringComparison.OrdinalIgnoreCase)
                && !value.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }

        string[] lines = { "..." };
        return lines[RandomNumberGenerator.GetInt32(lines.Length)];
    }

    public void Update(NPC npc, AgentInstance agent, int currentTick)
    {
        if (npc == null)
        {
            return;
        }

        // 防御性清理：如果 NPC 已不在 TALK 状态，清理计时器
        if (agent.StateMachine.CurrentStateFlag != AgentState.TALK)
        {
            _ = _talkEntryTick.Remove(npc.Name);
            return;
        }

        // 低血量时退出 TALK，避免 NPC 在聊天中被怪物打死
        if (agent.Health != null && agent.Health.HealthPercent < 0.3f)
        {
            _movementService.Stop(npc, "talk-low-health");
            _ = _talkEntryTick.Remove(npc.Name);
            agent.StateMachine.ForceTransition(AgentState.IDLE);
            _monitor?.Log($"[Talk] {npc.Name}: low health ({agent.Health.HealthPercent:P0}), exiting TALK",
                LogLevel.Debug);
            return;
        }

        var player = Game1.player;
        if (player == null)
        {
            return;
        }

        // ── Auto-return to IDLE after max TALK duration ──
        if (!_talkEntryTick.TryGetValue(npc.Name, out var entryTick))
        {
            _talkEntryTick[npc.Name] = currentTick;
        }
        else if (currentTick - entryTick > MaxTalkDurationTicks)
        {
            npc.doEmote(20); // wave goodbye
            _movementService.Stop(npc, "talk-timeout");
            if (npc.isMoving())
            {
                npc.Halt();
            }

            _ = _talkEntryTick.Remove(npc.Name);
            agent.StateMachine.ForceTransition(AgentState.IDLE);
            _monitor?.Log($"[Talk] {npc.Name}: TALK duration exceeded {MaxTalkDurationTicks / 60}s. Returning to IDLE.",
                LogLevel.Debug);
            return;
        }

        // ── Exit condition: player moved too far away ──
        var dist = Vector2.Distance(
            new Vector2(npc.TilePoint.X, npc.TilePoint.Y),
            new Vector2(player.TilePoint.X, player.TilePoint.Y));

        if (dist > ExitDistance)
        {
            // Closing ritual
            npc.doEmote(20); // wave

            // Clean up movement
            _movementService.Stop(npc, "talk-player-too-far");
            if (npc.isMoving())
            {
                npc.Halt();
            }

            _ = _talkEntryTick.Remove(npc.Name);
            _monitor?.Log($"[Talk] {npc.Name}: player too far ({dist:F1} > {ExitDistance} tiles). Exiting TALK.",
                LogLevel.Debug);
            agent.StateMachine.ForceTransition(AgentState.IDLE);
            return;
        }

        // Log only once per second to avoid spam
        var shouldLog = !_lastDebugLogTick.TryGetValue(npc.Name, out var lastLog)
                        || currentTick - lastLog >= DebugLogCooldownTicks;
        if (shouldLog)
        {
            _monitor?.Log(
                $"[Talk] {npc.Name}: approaching player dist={dist:F1} posNpc={npc.TilePoint} posPlayer={player.TilePoint}",
                LogLevel.Debug);
            _lastDebugLogTick[npc.Name] = currentTick;
        }

        // Pathfind to player if not adjacent
        if (!IsAdjacent(npc.TilePoint, player.TilePoint))
        {
            var moveResult = _movementService.MoveTo(npc, player.TilePoint, MovementMode.ShortRange, currentTick);
            if (moveResult == MoveResult.Frozen)
            {
                // NPC 被冻结（如状态冲突），等待 3 秒后自动退出 TALK
                if (_talkEntryTick.TryGetValue(npc.Name, out var frozenEntryTick)
                    && currentTick - frozenEntryTick > 180)
                {
                    _monitor?.Log($"[Talk] {npc.Name}: frozen too long, exiting TALK", LogLevel.Debug);
                    _ = _talkEntryTick.Remove(npc.Name);
                    agent.StateMachine.ForceTransition(AgentState.IDLE);
                }
            }

            return;
        }

        // Adjacent - stop and face player
        _movementService.Stop(npc, "talk-arrived");
        if (npc.isMoving())
        {
            npc.Halt();
        }

        FacePosition(npc, player.getStandingPosition());

        // Talk if cooldown ready
        if (CanAct(npc.Name, currentTick))
        {
            TalkToPlayer(npc);
            RecordAction(npc.Name, currentTick);
        }
    }

    private void TalkToPlayer(NPC npc)
    {
        var line = GetRandomGreeting();
        NpcSpeechHelper.Speak(npc, line);
    }
}