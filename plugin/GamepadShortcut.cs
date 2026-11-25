using System;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;

namespace SephiriaBackpackOrganizer
{
    internal static class GamepadShortcut
    {
        private static readonly string[] ButtonNames = Enum.GetNames(typeof(GamepadButton));

        internal static GamepadButton[] Parse(string shortcut)
        {
            if (string.IsNullOrWhiteSpace(shortcut) ||
                string.Equals(shortcut.Trim(), "None", StringComparison.OrdinalIgnoreCase))
            {
                return Array.Empty<GamepadButton>();
            }

            string[] parts = shortcut.Split('+');
            var buttons = new GamepadButton[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                string name = parts[i].Trim();
                bool known = false;
                foreach (string buttonName in ButtonNames)
                {
                    if (string.Equals(name, buttonName, StringComparison.OrdinalIgnoreCase))
                    {
                        known = true;
                        break;
                    }
                }
                if (!known)
                {
                    throw new FormatException($"未知手柄按键 '{name}'。可用值：{string.Join(", ", ButtonNames)}");
                }

                buttons[i] = (GamepadButton)Enum.Parse(typeof(GamepadButton), name, true);
                if (Array.IndexOf(buttons, buttons[i], 0, i) >= 0)
                {
                    throw new FormatException($"手柄按键 '{name}' 重复。请用 + 连接不同按键。");
                }
            }
            return buttons;
        }

        internal static bool IsPressed(GamepadButton[] buttons)
        {
            var gamepad = Gamepad.current;
            if (gamepad == null)
            {
                return false;
            }

            bool justPressed = false;
            foreach (GamepadButton button in buttons)
            {
                var control = gamepad[button];
                if (!control.isPressed)
                {
                    return false;
                }
                justPressed |= control.wasPressedThisFrame;
            }
            return justPressed;
        }
    }
}
