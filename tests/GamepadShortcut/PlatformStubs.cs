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
        public readonly ButtonControl rightTrigger = new ButtonControl();
        public readonly ButtonControl leftTrigger = new ButtonControl();
    }
}
