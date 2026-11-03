using Godot;

namespace Roadman.OnFoot;

/// <summary>
/// Generic names of the gamepad buttons the input map uses (ADR 0016, "Generic gamepad names"). Code and docs name
/// buttons only through these, never by a console maker's button names. Axes need no aliases: Godot's
/// <see cref="JoyAxis"/> names (<c>LeftX</c>, <c>LeftY</c>, <c>RightX</c>, <c>RightY</c>) are already generic.
/// </summary>
public static class GamepadButtons
{
    /// <summary>The south face button (<c>jump</c>). Godot's enum name for the south face button is <c>A</c>.</summary>
    public const JoyButton South = JoyButton.A;

    /// <summary>The left-stick click (<c>run_toggle</c>).</summary>
    public const JoyButton LeftStickClick = JoyButton.LeftStick;
}
