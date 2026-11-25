using System;
using UnityEngine.InputSystem.LowLevel;

// Minimal input-state model for the native shortcut tests.
namespace UnityEngine.InputSystem
{
    internal sealed class ButtonControl
    {
        public bool isPressed;
        public bool wasPressedThisFrame;

        public void SetPressed(bool pressed)
        {
            wasPressedThisFrame = pressed && !isPressed;
            isPressed = pressed;
        }
    }

    internal sealed class Gamepad
    {
        public static Gamepad current;
        private readonly ButtonControl[] buttons = new ButtonControl[34];

        public Gamepad()
        {
            for (int i = 0; i < buttons.Length; i++)
            {
                buttons[i] = new ButtonControl();
            }
        }

        public ButtonControl this[GamepadButton button] => buttons[(int)button];

        public void SetPressed(params GamepadButton[] held)
        {
            for (int i = 0; i < buttons.Length; i++)
            {
                buttons[i].SetPressed(Array.IndexOf(held, (GamepadButton)i) >= 0);
            }
        }
    }
}

namespace UnityEngine.InputSystem.LowLevel
{
    internal enum GamepadButton
    {
        DpadUp = 0, DpadDown = 1, DpadLeft = 2, DpadRight = 3,
        North = 4, East = 5, South = 6, West = 7,
        LeftStick = 8, RightStick = 9, LeftShoulder = 10, RightShoulder = 11,
        Start = 12, Select = 13, LeftTrigger = 32, RightTrigger = 33,
        X = West, Y = North, A = South, B = East,
        Cross = South, Square = West, Triangle = North, Circle = East
    }
}
