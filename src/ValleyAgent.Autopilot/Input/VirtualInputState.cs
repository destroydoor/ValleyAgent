using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework.Input;

namespace ValleyAgent.Autopilot.Input
{
    /// <summary>
    /// 管理 AI 注入的虚拟输入状态。当 Autopilot 激活时，
    /// InputHijacker 从这里读取状态替代真实输入。
    /// </summary>
    public sealed class VirtualInputState
    {
        private readonly HashSet<Keys> _heldKeys = new();
        private int _mouseX;
        private int _mouseY;
        private bool _mouseLeftPressed;
        private bool _mouseRightPressed;

        public bool IsActive { get; set; }

        public void SetKey(string keyName, bool pressed)
        {
            if (Enum.TryParse<Keys>(keyName, ignoreCase: true, out var key))
            {
                if (pressed)
                    _heldKeys.Add(key);
                else
                    _heldKeys.Remove(key);
            }
        }

        public void SetMouse(int x, int y, bool leftButton, bool rightButton)
        {
            _mouseX = x;
            _mouseY = y;
            _mouseLeftPressed = leftButton;
            _mouseRightPressed = rightButton;
        }

        public void ReleaseAll()
        {
            _heldKeys.Clear();
            _mouseLeftPressed = false;
            _mouseRightPressed = false;
        }

        public KeyboardState GetKeyboardState()
        {
            return _heldKeys.Count > 0 ? new KeyboardState(_heldKeys.ToArray()) : new KeyboardState();
        }

        public MouseState GetMouseState()
        {
            // 注意：MonoGame 的 MouseState 构造参数顺序是 (x, y, scroll, left, MIDDLE, RIGHT, x1, x2)，
            // 与旧 XNA 的 (left, right, middle) 不同。按 XNA 顺序传参会把右键写进 MiddleButton，
            // 导致右键永远不会被游戏检测到（本 bug 曾让所有虚拟右键点击落空）。
            return new MouseState(
                _mouseX, _mouseY, 0,
                _mouseLeftPressed ? ButtonState.Pressed : ButtonState.Released,
                ButtonState.Released,
                _mouseRightPressed ? ButtonState.Pressed : ButtonState.Released,
                ButtonState.Released, ButtonState.Released, 0
            );
        }
    }
}
