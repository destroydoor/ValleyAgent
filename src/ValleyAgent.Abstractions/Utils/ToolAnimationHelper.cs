using System.Collections.Generic;
using StardewValley;

namespace ValleyAgent.Utils
{
    /// <summary>
    /// Provides NPC body animations for tools and combat.
    /// Adapted from The Stardew Squad (NpcAdventures) animation system.
    /// 
    /// Simplified implementation: only NPC sprite animation via setCurrentAnimation.
    /// Weapon drawing (MeleeWeapon.drawDuringUse hook) is not included to avoid
    /// complex Harmony patches; TemporaryAnimatedSprite effects are used instead.
    /// </summary>
    public static class ToolAnimationHelper
    {
        // ── Attack animations ──────────────
        // Uses standard NPC walking sprite frames to simulate a swing motion.
        private static readonly List<FarmerSprite.AnimationFrame>[] AttackAnimations = new[]
        {
            new List<FarmerSprite.AnimationFrame> { new(8,  100), new(9,  250) }, // Up
            new List<FarmerSprite.AnimationFrame> { new(6,  100), new(7,  250) }, // Right
            new List<FarmerSprite.AnimationFrame> { new(0,  100), new(1,  250) }, // Down
            new List<FarmerSprite.AnimationFrame> { new(14, 100), new(15, 250) }  // Left
        };

        // ── Tool swing (pickaxe / hoe) ─────────────────────────────────────
        private static readonly List<FarmerSprite.AnimationFrame>[] ToolSwingAnimations = new[]
        {
            new List<FarmerSprite.AnimationFrame> { new(8,  120), new(9,  200) }, // Up
            new List<FarmerSprite.AnimationFrame> { new(6,  120), new(7,  200) }, // Right
            new List<FarmerSprite.AnimationFrame> { new(0,  120), new(1,  200) }, // Down
            new List<FarmerSprite.AnimationFrame> { new(14, 120), new(15, 200) }  // Left
        };

        // ── Watering ───────────────────────────────────────────────────────
        private static readonly List<FarmerSprite.AnimationFrame>[] WateringAnimations = new[]
        {
            new List<FarmerSprite.AnimationFrame> { new(8,  150), new(9,  300) }, // Up
            new List<FarmerSprite.AnimationFrame> { new(6,  150), new(7,  300) }, // Right
            new List<FarmerSprite.AnimationFrame> { new(0,  150), new(1,  300) }, // Down
            new List<FarmerSprite.AnimationFrame> { new(14, 150), new(15, 300) }  // Left
        };

        // ── Pickup (forage) ────────────────────────────────────────────────
        // Quick bend-and-stand motion.  Reuses the facing-direction walk frames
        // so the NPC doesn't snap to a different orientation.
        private static readonly List<FarmerSprite.AnimationFrame>[] PickupAnimations = new[]
        {
            new List<FarmerSprite.AnimationFrame> { new(8,  80), new(9,  120), new(8, 80) }, // Up
            new List<FarmerSprite.AnimationFrame> { new(6,  80), new(7,  120), new(6, 80) }, // Right
            new List<FarmerSprite.AnimationFrame> { new(0,  80), new(1,  120), new(0, 80) }, // Down
            new List<FarmerSprite.AnimationFrame> { new(14, 80), new(15, 120), new(14, 80) } // Left
        };

        // ═══ Public API ═════════════════════════════════════════════════════

        /// <summary>Plays a sword/weapon attack swing animation.</summary>
        public static void PlayAttackAnimation(NPC npc)
        {
            if (npc?.Sprite == null)
            {
                return;
            }

            npc.Sprite.StopAnimation();
            npc.Sprite.setCurrentAnimation(AttackAnimations[npc.FacingDirection]);
        }

        /// <summary>Plays a pickaxe / hammer swing animation.</summary>
        public static void PlayToolSwingAnimation(NPC npc)
        {
            if (npc?.Sprite == null)
            {
                return;
            }

            npc.Sprite.StopAnimation();
            npc.Sprite.setCurrentAnimation(ToolSwingAnimations[npc.FacingDirection]);
        }

        /// <summary>Plays a watering-can motion animation.</summary>
        public static void PlayWateringAnimation(NPC npc)
        {
            if (npc?.Sprite == null)
            {
                return;
            }

            npc.Sprite.StopAnimation();
            npc.Sprite.setCurrentAnimation(WateringAnimations[npc.FacingDirection]);
        }

        /// <summary>Plays a quick bend-down pickup animation.</summary>
        public static void PlayPickupAnimation(NPC npc)
        {
            if (npc?.Sprite == null)
            {
                return;
            }

            npc.Sprite.StopAnimation();
            npc.Sprite.setCurrentAnimation(PickupAnimations[npc.FacingDirection]);
        }

        /// <summary>Stops any active animation and returns to idle.</summary>
        public static void StopAnimation(NPC npc) => npc?.Sprite?.StopAnimation();
    }
}
