using UnityEngine.InputSystem;

namespace SephiriaBackpackOrganizer
{
    internal static class GamepadShortcut
    {
        internal static bool IsPressed()
        {
            var gamepad = Gamepad.current;
            return gamepad != null && gamepad.leftTrigger.isPressed && gamepad.rightTrigger.isPressed &&
                (gamepad.leftTrigger.wasPressedThisFrame || gamepad.rightTrigger.wasPressedThisFrame);
        }
    }
}
