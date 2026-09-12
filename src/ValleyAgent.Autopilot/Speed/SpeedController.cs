using System;
using HarmonyLib;
using StardewValley;

namespace ValleyAgent.Autopilot.Speed
{
    /// <summary>
    /// 通过 Harmony 补丁修改 Game1.Update 的 ElapsedGameTime，
    /// 实现全局慢动作效果。
    /// </summary>
    public sealed class SpeedController
    {
        private float _speedFactor = 1.0f;

        public float SpeedFactor
        {
            get => _speedFactor;
            set => _speedFactor = Math.Clamp(value, 0.1f, 1.0f);
        }

        public static SpeedController? Instance { get; private set; }

        public SpeedController()
        {
            Instance = this;
        }

        public void ApplyPatches(Harmony harmony)
        {
            ArgumentNullException.ThrowIfNull(harmony);
            harmony.Patch(
                original: AccessTools.Method(typeof(Game1), "Update"),
                prefix: new HarmonyMethod(typeof(SpeedController), nameof(BeforeUpdate))
            );
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Game1), "Update")]
        private static void BeforeUpdate(ref Microsoft.Xna.Framework.GameTime gameTime)
        {
            if (Instance == null) return;
            var factor = Instance._speedFactor;
            if (factor < 1.0f)
            {
                var original = gameTime.ElapsedGameTime;
                gameTime.ElapsedGameTime = TimeSpan.FromTicks((long)(original.Ticks * factor));
            }
        }
    }
}
