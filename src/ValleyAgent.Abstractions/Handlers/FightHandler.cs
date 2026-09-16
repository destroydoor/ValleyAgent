using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Monsters;
using StardewValley.Tools;
using ValleyAgent.StateMachine;
using ValleyAgent.Services;
using ValleyAgent.Utils;
using ValleyAgent.Navigation;

namespace ValleyAgent.Handlers
{
    /// <summary>
    /// Handles NPC combat behavior: find nearest monster, path to it, and attack.
    /// Adapted from The Stardew Squad's attacking task implementation.
    /// </summary>
    public class FightHandler : HandlerBase
    {
        private readonly Dictionary<string, int> _attackCooldown = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, MeleeWeapon> _npcWeapons = new(StringComparer.OrdinalIgnoreCase);
        // 强制攻击目标 ID（由 CommandExecutor 设置，优先于最近怪物选择）
        private const int SwingThreshold = 36; // Effective swing frames for animation
        private const int SearchRadius = 12;
        private const int DebugLogCooldownTicks = 60; // 1s at 60fps - throttle handler debug logs
        private readonly Dictionary<string, int> _lastDebugLogTick = new(StringComparer.OrdinalIgnoreCase);

        public FightHandler(IMonitor? monitor, IMovementService movementService) : base(monitor, movementService) { }

        /// <summary>
        /// Updates combat logic for the given NPC. Should be called every tick while in FIGHT state.
        /// </summary>
        public void Update(NPC npc, AgentInstance agent, int currentTick)
        {
            if (npc == null || agent == null)
            {
                return;
            }

            // Check death state
            if (agent.Health?.IsDead == true)
            {
                _movementService.Stop(npc, "fight-dead");
                if (npc.isMoving())
                {
                    npc.Halt();
                }

                // 死亡后状态机应回到 IDLE，避免残留 FIGHT
                if (agent.StateMachine.CurrentStateFlag != AgentState.IDLE)
                {
                    agent.StateMachine.ForceTransition(AgentState.IDLE);
                }

                return;
            }

            // Decrement attack cooldown for animation tracking
            if (_attackCooldown.TryGetValue(npc.Name, out var cd) && cd > 0)
            {
                _attackCooldown[npc.Name] = cd - 1;
            }

            // 优先使用强制目标 ID，否则找最近怪物
            var monster = FindTargetMonster(npc);
            if (monster == null)
            {
                // 无怪物 — 执行收尾仪式（AGENTS.md §4.6：收剑 emote + 战后感叹）
                _movementService.Stop(npc, "fight-no-monsters");
                if (npc.isMoving())
                {
                    npc.Halt();
                }

                npc.doEmote(28); // 收剑 emote（剑图标）
                _monitor?.Log($"[Fight] {npc.Name}: no monsters in range (searchRadius={SearchRadius}), loc={npc.currentLocation.NameOrUniqueName}. Exiting FIGHT.", LogLevel.Debug);
                agent.StateMachine.ForceTransition(AgentState.IDLE);
                return;
            }

            var monsterDist = Vector2.Distance(npc.Tile, monster.Tile);
            // Log only once per second to avoid spam
            var shouldLog = !_lastDebugLogTick.TryGetValue(npc.Name, out var lastLog)
                || currentTick - lastLog >= DebugLogCooldownTicks;
            if (shouldLog)
            {
                _lastDebugLogTick[npc.Name] = currentTick;
                _monitor?.Log($"[Fight] {npc.Name}: nearest monster={monster.Name} at {monster.Tile} dist={monsterDist:F1} npc={npc.Tile} hp={agent.Health?.Health}/{agent.Health?.MaxHealth}", LogLevel.Debug);
            }

            // Check if adjacent (within 1 tile, including diagonals)
            if (IsAdjacentToMonster(npc, monster))
            {
                // Stop pathfinding when adjacent
                _movementService.Stop(npc, "fight-adjacent");

                // Attack if cooldown is ready
                if (CanAct(npc.Name, currentTick))
                {
                    var hpBefore = monster.Health;
                    // 快照怪物掉落列表：damageMonster 在怪物死亡时会消费 objectsToDrop，
                    // 导致 EnsureLootDrops 拿到空列表。攻击前先备份。
                    var lootSnapshot = monster.objectsToDrop?.Count > 0
                        ? monster.objectsToDrop.ToList()
                        : null;
                    ExecuteAttack(npc, monster);
                    RecordAction(npc.Name, currentTick);
                    _attackCooldown[npc.Name] = DefaultActionCooldownTicks;

                    if (hpBefore > 0 && monster.Health <= 0)
                    {
                        EnsureLootDrops(npc, monster, lootSnapshot);
                    }

                    TryMonsterCounterAttack(npc, agent, monster);
                }
                return;
            }

            // Not adjacent - pathfind to a walkable tile near the monster
            var approachTile = PathfindingUtility.GetApproachTile(
                monster.TilePoint,
                (x, y) => TileWalkability.IsTileWalkable(npc.currentLocation, x, y));
            var moveResult = _movementService.MoveTo(npc, approachTile, MovementMode.Immediate, currentTick);
            if (moveResult == MoveResult.Frozen)
            {
                _movementService.Stop(npc, "fight-frozen");
                agent.StateMachine.ForceTransition(AgentState.IDLE);
            }
        }

        // ─── Monster Detection ─────────────────────────────────────────────

        /// <summary>设置强制攻击目标 ID（由 CommandExecutor 调用）。</summary>
        public void SetForcedTargetById(string npcName, string targetId) => SetForcedTarget(npcName, Point.Zero, targetId: targetId);

        /// <summary>查找目标怪物：优先使用强制目标 ID，否则找最近怪物。</summary>
        public Monster? FindTargetMonster(NPC npc)
        {
            // 如果有强制目标 ID，优先匹配
            if (TryGetForcedTargetInfo(npc.Name, out var forcedInfo) && !string.IsNullOrEmpty(forcedInfo.TargetId))
            {
                var forcedId = forcedInfo.TargetId;
                ClearForcedTarget(npc.Name); // 一次性消费
                var location = npc.currentLocation;
                if (location != null)
                {
                    var forced = location.characters
                        .OfType<Monster>()
                        .FirstOrDefault(m => IsMonsterTargetable(m, location)
                            && (m.Name?.Equals(forcedId, StringComparison.OrdinalIgnoreCase) == true
                                || m.GetHashCode().ToString() == forcedId)); // GetHashCode: 同进程内稳定，跨进程不稳定
                    if (forced != null)
                    {
                        return forced;
                    }
                }
            }
            return FindNearestMonster(npc);
        }

        /// <summary>Finds the nearest targetable monster to the NPC.</summary>
        public static Monster? FindNearestMonster(NPC npc)
        {
            var location = npc.currentLocation;
            if (location == null) return null;
            var npcTile = npc.TilePoint;
            return location.characters
                .OfType<Monster>()
                .Where(m => IsMonsterTargetable(m, location)
                    && Math.Abs(npcTile.X - m.TilePoint.X) + Math.Abs(npcTile.Y - m.TilePoint.Y) <= SearchRadius)
                .OrderBy(m => Vector2.DistanceSquared(npc.Tile, m.Tile))
                .FirstOrDefault();
        }

        private static bool IsMonsterTargetable(Monster monster, GameLocation location)
        {
            // Monster removed from location
            if (!location.characters.Contains(monster))
            {
                return false;
            }

            // Mummy reviving
            if (monster is Mummy mummy && mummy.reviveTimer.Value > 0)
            {
                return false;
            }

            // Duggy hiding underground
            if (monster is Duggy duggy && duggy.DamageToFarmer == 0)
            {
                return false;
            }

            // RockCrab hiding in shell
            return (monster is not RockCrab crab || !crab.waiter) && monster.Health > 0;
        }

        // ─── Monster Counter-Attack ────────────────────────────────────────

        private const float MonsterCounterAttackChance = 0.25f;
        private const float LowHealthThreshold = 0.3f;
        private const int MonsterBaseDamage = 5;

        private void TryMonsterCounterAttack(NPC npc, AgentInstance agent, Monster monster)
        {
            // 怪物已死时不反击
            if (monster.Health <= 0) return;

            // 概率反击
            if ((RandomNumberGenerator.GetInt32(0, 1_000_000) / 1_000_000.0) >= MonsterCounterAttackChance) return;

            // 根据怪物类型计算伤害
            var damage = Math.Max(MonsterBaseDamage, monster.DamageToFarmer / 2);

            agent.Health.TakeDamage(damage);
            npc.shake(200);
            _monitor?.Log($"[Fight] {npc.Name}: monster {monster.Name} counter-attacked for {damage} damage. HP: {agent.Health.Health}/{agent.Health.MaxHealth}", LogLevel.Debug);

            // 血量低于 30% 时自动撤退到 FOLLOW 状态（逃离战斗）
            if (!agent.Health.IsDead && agent.Health.Health < agent.Health.MaxHealth * LowHealthThreshold)
            {
                _monitor?.Log($"[Fight] {npc.Name}: health below {LowHealthThreshold*100:F0}% — retreating to FOLLOW.", LogLevel.Info);
                _movementService.Stop(npc, "fight-retreat");
                agent.StateMachine.ForceTransition(AgentState.FOLLOW);
            }
        }

        // ─── Attack Execution ──────────────────────────────────────────────

        private void ExecuteAttack(NPC npc, Monster monster)
        {
            var (minDamage, maxDamage) = CalculateAttackDamage();
            var critChance = CalculateCritChance();
            var critMultiplier = CalculateCritMultiplier();

            // 设计决策：使用 Game1.player 作为攻击来源
            // 原因：damageMonster 需要 Farmer 参数来计算伤害，NPC 没有等效接口
            // 副作用：战斗经验归于玩家、玩家职业加成被应用、掉落物品归玩家
            // TODO: 未来应独立计算 NPC 伤害，避免借用玩家属性
            _ = npc.currentLocation.damageMonster(
        monster.GetBoundingBox(),
        minDamage,
        maxDamage,
        isBomb: false,
        knockBackModifier: 1.0f,
        addedPrecision: 0,
        critChance: critChance,
        critMultiplier: critMultiplier,
        triggerMonsterInvincibleTimer: true,
        who: Game1.player,
        isProjectile: true
    );

            ToolAnimationHelper.PlayAttackAnimation(npc);
            npc.currentLocation.playSound("swordswipe");
            FacePosition(npc, monster.getStandingPosition());
            AnimateSwordSwing(npc);
            npc.shake(400);

            _monitor?.Log($"{npc.Name} attacked {monster.Name} for {minDamage}-{maxDamage} damage", LogLevel.Trace);

            // Random combat dialogue
            if (RandomNumberGenerator.GetInt32(10) == 0)
            {
                // Combat lines should come from AIDialogueSystem (contextual).
                // TODO: integrate with AIDialogueSystem for contextual combat lines
                // For now, no hardcoded lines — emoji-only feedback.
                npc.doEmote(28); // angry emote
            }
        }

        private static (int minDamage, int maxDamage) CalculateAttackDamage()
        {
            var player = Game1.player;
            var minDamage = Math.Max(1, player.CombatLevel * 5);
            var maxDamage = 10 + (player.CombatLevel * 11);

            if (player.professions.Contains(Farmer.fighter))
            {
                minDamage = (int)(minDamage * 1.1f);
                maxDamage = (int)(maxDamage * 1.1f);
            }
            if (player.professions.Contains(Farmer.brute))
            {
                minDamage = (int)(minDamage * 1.15f);
                maxDamage = (int)(maxDamage * 1.15f);
            }
            return (minDamage, maxDamage);
        }

        private static float CalculateCritChance()
        {
            var baseChance = 0.02f;
            var effective = baseChance * (1f + Game1.player.buffs.CriticalChanceMultiplier);
            if (Game1.player.professions.Contains(Farmer.scout))
            {
                effective *= 1.5f;
            }

            return effective;
        }

        private static float CalculateCritMultiplier()
        {
            var baseMult = 3.0f;
            var effective = baseMult * (1f + Game1.player.buffs.CriticalPowerMultiplier);
            if (Game1.player.professions.Contains(Farmer.desperado))
            {
                effective *= 2f;
            }

            return effective;
        }

        // ─── Animation & Helpers ───────────────────────────────────────────

        /// <summary>Returns environment summary with counts, or null if none.</summary>
        public static string? ScanEnvironment(NPC npc)
        {
            var location = npc.currentLocation;
            if (location == null)
            {
                return null;
            }

            var npcTile = npc.TilePoint;
            var monsterCount = location.characters
                .OfType<Monster>()
                .Count(m => IsMonsterTargetable(m, location)
                    && Math.Abs(npcTile.X - m.TilePoint.X) + Math.Abs(npcTile.Y - m.TilePoint.Y) <= SearchRadius);
            return monsterCount > 0 ? $"{monsterCount} monsters nearby" : null;
        }

        /// <summary>
        /// 确保怪物被击杀后掉落物出现在地面。
        /// SDV 的 damageMonster 归属玩家击杀，满背包时掉落物可能丢失。
        /// 此方法使用攻击前快照的 lootSnapshot，对比背包差异，将未入包的物品掉落到地面。
        /// </summary>
        private void EnsureLootDrops(NPC npc, Monster monster, List<string>? lootSnapshot)
        {
            try
            {
                var location = npc.currentLocation;
                if (location == null) return;

                var dropList = lootSnapshot ?? monster.objectsToDrop?.ToList();
                if (dropList == null || dropList.Count == 0) return;

                var debrisBefore = location.debris.Count;

                foreach (var item in dropList)
                {
                    var obj = new StardewValley.Object(item, 1);
                    var added = Game1.player.addItemToInventoryBool(obj);
                    if (!added)
                    {
                        _ = Game1.createItemDebris(obj, monster.Position, RandomNumberGenerator.GetInt32(4), location);
                    }
                }
                monster.objectsToDrop?.Clear();

                var debrisAfter = location.debris.Count;
                if (debrisAfter > debrisBefore)
                {
                    _monitor?.Log($"[Fight] EnsureLootDrops: {debrisAfter - debrisBefore} items dropped on ground (inventory full)", LogLevel.Debug);
                }
            }
            catch (InvalidOperationException ex)
            {
                _monitor?.Log($"[Fight] EnsureLootDrops failed for {monster.Name}: {ex}", LogLevel.Warn);
            }
        }

        private static void AnimateSwordSwing(NPC npc)
        {
            var location = npc.currentLocation;
            var sourceRect = new Rectangle(0, 0, 16, 16); // Rusty Sword texture
            var layerDepth = (npc.GetBoundingBox().Bottom + 2) / 10000f;
            var layerDepthBehind = (npc.GetBoundingBox().Bottom - 32) / 10000f;

            switch (npc.FacingDirection)
            {
                case 1: // Right
                    AddSwordSprite(location, sourceRect, npc.Position + new Vector2(40f, -64f + 8f), layerDepthBehind, false, -(float)Math.PI / 4f, 50);
                    AddSwordSprite(location, sourceRect, npc.Position + new Vector2(64f - 4f, -16f), layerDepth, false, (float)Math.PI / 4f, 100);
                    AddSwordSprite(location, sourceRect, npc.Position + new Vector2(64f - 28f, 4f), layerDepth, false, (float)Math.PI * 5f / 8f, 150);
                    AddSwordSprite(location, sourceRect, npc.Position + new Vector2(64f - 48f, 4f), layerDepth, false, (float)Math.PI * 3f / 4f, 200);
                    break;
                case 3: // Left
                    AddSwordSprite(location, sourceRect, npc.Position + new Vector2(-40f, -64f + 8f), layerDepthBehind, true, (float)Math.PI / 4f, 50);
                    AddSwordSprite(location, sourceRect, npc.Position + new Vector2(-64f + 4f, -16f), layerDepth, true, -(float)Math.PI / 4f, 100);
                    AddSwordSprite(location, sourceRect, npc.Position + new Vector2(-64f + 28f, 4f), layerDepth, true, -(float)Math.PI * 5f / 8f, 150);
                    AddSwordSprite(location, sourceRect, npc.Position + new Vector2(-64f + 48f, 4f), layerDepth, true, -(float)Math.PI * 3f / 4f, 200);
                    break;
                case 0: // Up
                    AddSwordSprite(location, sourceRect, npc.Position + new Vector2(32f, -32f), layerDepthBehind, false, (float)Math.PI * -3f / 4f, 50);
                    AddSwordSprite(location, sourceRect, npc.Position + new Vector2(48f, -52f), layerDepthBehind, false, (float)Math.PI * -3f / 8f, 100);
                    AddSwordSprite(location, sourceRect, npc.Position + new Vector2(64f - 8f, -40f), layerDepthBehind, false, 0f, 150);
                    AddSwordSprite(location, sourceRect, npc.Position + new Vector2(64f, -40f), layerDepthBehind, false, (float)Math.PI / 8f, 200);
                    break;
                case 2: // Down
                    AddSwordSprite(location, sourceRect, npc.Position + new Vector2(56f, -16f), layerDepth, false, (float)Math.PI / 8f, 50);
                    AddSwordSprite(location, sourceRect, npc.Position + new Vector2(40f, 0f), layerDepth, false, (float)Math.PI / 2f, 100);
                    AddSwordSprite(location, sourceRect, npc.Position + new Vector2(8f, 8f), layerDepth, false, (float)Math.PI, 150);
                    AddSwordSprite(location, sourceRect, npc.Position + new Vector2(12f, 0f), layerDepth, false, 3.5342917f, 200);
                    break;
                default:
                    break;
            }
        }

        private static void AddSwordSprite(GameLocation location, Rectangle sourceRect, Vector2 position, float layerDepth, bool flipped, float rotation, int delay)
        {
            location.temporarySprites.Add(new TemporaryAnimatedSprite(
                textureName: Tool.weaponsTextureName,
                sourceRect,
                animationInterval: 50f,
                animationLength: 1,
                numberOfLoops: 0,
                position: position,
                flicker: false,
                flipped: flipped,
                layerDepth: layerDepth,
                alphaFade: 0f,
                color: Color.White,
                scale: 4f,
                scaleChange: 0f,
                rotation: rotation,
                rotationChange: 0f
            )
            { delayBeforeAnimationStart = delay });
        }

        // ─── Weapon Drawing ────────────────────────────────────────────────

        /// <summary>Returns true if the NPC is currently in the attack swing animation.</summary>
        public bool IsAttacking(NPC npc) => _attackCooldown.TryGetValue(npc.Name, out var cd) && cd > SwingThreshold;

        /// <summary>Draws the NPC's weapon during the attack swing. Call from RenderedWorld.</summary>
        public void DrawWeapon(SpriteBatch b, NPC npc)
        {
            if (!IsAttacking(npc))
            {
                return;
            }

            var weapon = GetOrCreateWeapon(npc);
            var cd = _attackCooldown[npc.Name];
            var frames = weapon.type.Value == 3 ? 4 : 7;
            var duration = DefaultActionCooldownTicks - SwingThreshold;
            var tick = DefaultActionCooldownTicks - cd;
            var currentFrame = tick % Math.Max(1, duration) / Math.Max(1, duration / frames);

            WeaponDrawHelper.DrawDuringUse(
                currentFrame,
                npc.FacingDirection,
                b,
                npc.getLocalPosition(Game1.viewport),
                npc,
                ItemRegistry.GetDataOrErrorItem(weapon.QualifiedItemId).GetSourceRect(),
                weapon.type.Value,
                weapon.isOnSpecial);
        }

        private MeleeWeapon GetOrCreateWeapon(NPC npc)
        {
            if (!_npcWeapons.TryGetValue(npc.Name, out var weapon))
            {
                weapon = new MeleeWeapon("0"); // Rusty Sword
                _npcWeapons[npc.Name] = weapon;
            }
            return weapon;
        }
    }
}
