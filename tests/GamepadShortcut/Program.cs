using System;
using SephiriaBackpackOrganizer;
using UnityEngine.InputSystem;

internal static class Program
{
    private static void Expect(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void Main()
    {
        CheckGamepadShortcut();
        Console.WriteLine("PASS: LT + RT shortcut");
    }

    private static void CheckGamepadShortcut()
    {
        Gamepad.current = null;
        Expect(!GamepadShortcut.IsPressed(), "A disconnected controller must not trigger sorting.");
        var pad = Gamepad.current = new Gamepad();
        Frame(pad, false, false, false, "Neutral controller");
        Frame(pad, true, false, false, "LT alone");
        Frame(pad, true, true, true, "Press RT while holding LT");
        Frame(pad, true, true, false, "Holding the chord must not repeat");
        Frame(pad, true, false, false, "Release RT");
        Frame(pad, true, true, true, "Press RT again while holding LT");
        Frame(pad, false, false, false, "Release both");
        Frame(pad, false, true, false, "RT alone");
        Frame(pad, true, true, true, "Press LT while holding RT");
        Frame(pad, true, true, false, "Holding the reverse-order chord must not repeat");
        Frame(pad, false, true, false, "Release LT");
        Frame(pad, true, true, true, "Press LT again while holding RT");
        Frame(pad, false, false, false, "Release before simultaneous press");
        Frame(pad, true, true, true, "Press both triggers simultaneously");
        Frame(pad, true, true, false, "Holding both triggers must not repeat");
        Gamepad.current = null;
        Expect(!GamepadShortcut.IsPressed(), "Disconnecting during a chord must not trigger sorting.");
    }

    private static void Frame(Gamepad pad, bool lt, bool rt, bool expected, string scenario)
    {
        pad.leftTrigger.SetPressed(lt);
        pad.rightTrigger.SetPressed(rt);
        Expect(GamepadShortcut.IsPressed() == expected, scenario);
    }
}
