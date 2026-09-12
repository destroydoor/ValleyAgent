using System;
using HarmonyLib;
using Microsoft.Xna.Framework.Input;

namespace ValleyAgent.Autopilot.Input
{
    /// <summary>
    /// 当 Autopilot 激活时，劫持 Keyboard.GetState() 和 Mouse.GetState()，
    /// 返回 VirtualInputState 中的虚拟输入，屏蔽真实玩家输入。
    /// </summary>
    public sealed class InputHijacker
    {
        private readonly VirtualInputState _virtualInput;

        public InputHijacker(VirtualInputState virtualInput)
        {
            _virtualInput = virtualInput;
            Instance = this;
        }

        public static InputHijacker? Instance { get; private set; }

        public void ApplyPatches(Harmony harmony)
        {
            ArgumentNullException.ThrowIfNull(harmony);
            harmony.Patch(
                original: AccessTools.Method(typeof(Keyboard), nameof(Keyboard.GetState), new System.Type[0]),
                prefix: new HarmonyMethod(typeof(InputHijacker), nameof(BeforeKeyboardGetState))
            );
            harmony.Patch(
                original: AccessTools.Method(typeof(Mouse), nameof(Mouse.GetState)),
                prefix: new HarmonyMethod(typeof(InputHijacker), nameof(BeforeMouseGetState))
            );
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Keyboard), nameof(Keyboard.GetState))]
        private static bool BeforeKeyboardGetState(ref KeyboardState __result)
        {
            if (Instance == null || !Instance._virtualInput.IsActive) return true;
            __result = Instance._virtualInput.GetKeyboardState();
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Mouse), nameof(Mouse.GetState))]
        private static bool BeforeMouseGetState(ref MouseState __result)
        {
            if (Instance == null || !Instance._virtualInput.IsActive) return true;
            __result = Instance._virtualInput.GetMouseState();
            return false;
        }
    }
}
