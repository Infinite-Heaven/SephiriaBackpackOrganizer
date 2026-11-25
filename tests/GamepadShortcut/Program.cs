using System;
using SephiriaBackpackOrganizer;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;

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
        CheckDefaultShortcut();
        CheckCustomShortcuts();
        CheckConfigurationValidation();
        Console.WriteLine("PASS: configurable gamepad shortcuts (default LT + RT)");
    }

    private static void CheckDefaultShortcut()
    {
        var chord = GamepadShortcut.Parse("LeftTrigger + RightTrigger");
        Gamepad.current = null;
        Expect(!GamepadShortcut.IsPressed(chord), "A disconnected controller must not trigger sorting.");
        var pad = Gamepad.current = new Gamepad();
        Frame(pad, chord, false, "Neutral controller");
        Frame(pad, chord, false, "LT alone", GamepadButton.LeftTrigger);
        Frame(pad, chord, true, "RT after LT", GamepadButton.LeftTrigger, GamepadButton.RightTrigger);
        Frame(pad, chord, false, "Holding the chord", GamepadButton.LeftTrigger, GamepadButton.RightTrigger);
        Frame(pad, chord, false, "Release RT", GamepadButton.LeftTrigger);
        Frame(pad, chord, true, "Press RT again", GamepadButton.LeftTrigger, GamepadButton.RightTrigger);
        Frame(pad, chord, false, "Release both");
        Frame(pad, chord, false, "RT alone", GamepadButton.RightTrigger);
        Frame(pad, chord, true, "LT after RT", GamepadButton.LeftTrigger, GamepadButton.RightTrigger);
        Frame(pad, chord, false, "Holding the reverse-order chord", GamepadButton.LeftTrigger, GamepadButton.RightTrigger);
        Frame(pad, chord, false, "Release LT", GamepadButton.RightTrigger);
        Frame(pad, chord, true, "Press LT again", GamepadButton.LeftTrigger, GamepadButton.RightTrigger);
        Frame(pad, chord, false, "Release before simultaneous press");
        Frame(pad, chord, true, "Press both simultaneously", GamepadButton.LeftTrigger, GamepadButton.RightTrigger);
        Frame(pad, chord, false, "Hold both", GamepadButton.LeftTrigger, GamepadButton.RightTrigger);
        Gamepad.current = null;
        Expect(!GamepadShortcut.IsPressed(chord), "Disconnecting during a chord must not trigger sorting.");
    }

    private static void CheckCustomShortcuts()
    {
        var pad = Gamepad.current = new Gamepad();
        var chord = GamepadShortcut.Parse(" rightshoulder + DPADdown ");
        Frame(pad, chord, false, "Custom binding must replace LT + RT", GamepadButton.LeftTrigger, GamepadButton.RightTrigger);
        Frame(pad, chord, false, "Custom modifier alone", GamepadButton.RightShoulder);
        Frame(pad, chord, true, "Custom shoulder and D-pad", GamepadButton.RightShoulder, GamepadButton.DpadDown);
        Frame(pad, chord, false, "Custom chord hold", GamepadButton.RightShoulder, GamepadButton.DpadDown);

        chord = GamepadShortcut.Parse("South");
        Frame(pad, chord, false, "Release before single button");
        Frame(pad, chord, true, "Single button binding", GamepadButton.South);
        Frame(pad, chord, false, "Single button hold", GamepadButton.South);

        chord = GamepadShortcut.Parse("LeftShoulder + RightShoulder + North");
        Frame(pad, chord, false, "Partial chord", GamepadButton.LeftShoulder, GamepadButton.RightShoulder);
        Frame(pad, chord, true, "All configured buttons", GamepadButton.LeftShoulder, GamepadButton.RightShoulder, GamepadButton.North);

        chord = GamepadShortcut.Parse("A");
        Frame(pad, chord, true, "Named face-button alias", GamepadButton.South);
    }

    private static void CheckConfigurationValidation()
    {
        var pad = Gamepad.current = new Gamepad();
        foreach (string value in new[] { null, "", "  ", "None", " none " })
        {
            Frame(pad, GamepadShortcut.Parse(value), false, "Disabled shortcut", GamepadButton.LeftTrigger, GamepadButton.RightTrigger);
        }
        foreach (string value in new[] {
            "UnknownButton", "32", "-1", "LeftTrigger, RightTrigger", "LeftTrigger +",
            "+ RightTrigger", "LeftTrigger ++ RightTrigger", "LeftTrigger + lefttrigger",
            "South + A", "None + RightTrigger" })
        {
            try
            {
                GamepadShortcut.Parse(value);
            }
            catch (FormatException)
            {
                continue;
            }
            throw new InvalidOperationException("Invalid shortcut accepted: " + value);
        }
    }

    private static void Frame(Gamepad pad, GamepadButton[] chord, bool expected,
        string scenario, params GamepadButton[] held)
    {
        pad.SetPressed(held);
        Expect(GamepadShortcut.IsPressed(chord) == expected, scenario);
    }
}
