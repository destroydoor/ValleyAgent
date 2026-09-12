using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Monsters;
using ValleyAgent.Brain;
using ValleyAgent.Services;
using ValleyAgent.StateMachine;
using xTile.Dimensions;

namespace ValleyAgent.Combat;

/// <summary>
///     怪物攻击管理器。强制附近怪物追踪和攻击 Agent NPC。
///     SDV 怪物默认只攻击玩家，此模块手动覆盖其行为。
///     从 ModEntry 提取。
/// </summary>
public class MonsterAggroManager
{
    private const int MonsterAttackCooldown = 90;
    private const float AggroRange = 10f;
    private const int MonsterDamage = 8;
    private const int CleanupInterval = 1000;
    private readonly IMonitor? _monitor;
    private readonly Dictionary<string, int> _monsterAttackTick = new();

    public MonsterAggroManager(IMonitor? monitor)
    {
        _monitor = monitor;
    }

    public void ProcessMonsterAttacks(IReadOnlyList<AgentInstance> agents, int currentTick)
    {
        foreach (var agent in agents)
        {
            if (agent.Health.IsDead)
            {
                continue;
            }

            var npc = Game1.getCharacterFromName(agent.NpcName);
            if (npc?.currentLocation == null)
            {
                continue;
            }

            var location = npc.currentLocation;
            // 快照遍历：怪物死亡时 SDV 会从 location.characters 移除，导致 InvalidOperationException
            foreach (var character in location.characters.ToList())
            {
                if (character is not Monster monster || monster.Health <= 0)
                {
                    continue;
                }

                var dist = Vector2.Distance(npc.Tile, monster.Tile);
                if (dist > AggroRange)
                {
                    continue;
                }

                // 让怪物面朝 NPC
                var dir = npc.Tile - monster.Tile;
                if (Math.Abs(dir.X) >= Math.Abs(dir.Y))
                {
                    monster.faceDirection(dir.X > 0 ? 1 : 3);
                }
                else
                {
                    monster.faceDirection(dir.Y > 0 ? 2 : 0);
                }

                // 怪物向 NPC 移动
                if (dist > 1.2f)
                {
                    var speed = 1.5f;
                    if (dir.Length() > 0)
                    {
                        dir.Normalize();
                        var nextPos = monster.Position + dir * speed;
                        var nextTile = nextPos / 64f;
                        var tileLoc = new Location((int)nextTile.X, (int)nextTile.Y);
                        if (location.isTilePassable(tileLoc, Game1.viewport))
                        {
                            monster.Position = nextPos;
                        }
                    }
                }

                // 近距离攻击
                if (dist <= 1.5f)
                {
                    var key = $"{npc.Name}_{monster.GetHashCode()}";
                    var last = _monsterAttackTick.GetValueOrDefault(key);
                    if (currentTick - last >= MonsterAttackCooldown)
                    {
                        _monsterAttackTick[key] = currentTick;

                        // NPC 在非 FIGHT 状态被攻击时，自动切换到 FIGHT 状态反击
                        if (agent.StateMachine.CurrentStateFlag != AgentState.FIGHT)
                        {
                            agent.StateMachine.ForceTransition(AgentState.FIGHT, true);
                            _monitor?.Log($"[Combat] {npc.Name}: attacked while in non-FIGHT state, switching to FIGHT",
                                LogLevel.Info);
                        }

                        var died = agent.Health.TakeDamage(MonsterDamage);
                        agent.Brain.SyncEmotion(NpcEmotion.Tired, 0.7f, "HurtInBattle");
                        npc.shake(300);
                        _monitor?.Log(
                            $"[Combat] {monster.Name} hit {npc.Name} for {MonsterDamage} HP (remaining {agent.Health.Health}/{agent.Health.MaxHealth})",
                            LogLevel.Debug);

                        if (died)
                        {
                            npc.doEmote(15);
                            _monitor?.Log($"{npc.Name} has been knocked out! Respawning tomorrow.", LogLevel.Warn);
                        }
                    }
                }

                // 定期清理过期的攻击冷却条目
                if (currentTick % CleanupInterval == 0)
                {
                    var staleKeys = _monsterAttackTick.Keys
                        .Where(k => currentTick - _monsterAttackTick[k] > MonsterAttackCooldown * 10)
                        .ToList();
                    foreach (var key in staleKeys)
                    {
                        _ = _monsterAttackTick.Remove(key);
                    }
                }
            }
        }
    }
}