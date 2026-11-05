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

    /// <summary>
    /// The east face button: back in the menus. Godot's built-in <c>ui_cancel</c> doesn't hold it; the override of
    /// <c>ui_cancel</c> in <c>project.godot</c> adds it. Godot's enum name for it is <c>B</c>.
    /// </summary>
    public const JoyButton East = JoyButton.B;

    /// <summary>The left-stick click (<c>run_toggle</c>).</summary>
    public const JoyButton LeftStickClick = JoyButton.LeftStick;

    /// <summary>The start button (<c>pause</c>); shown as "Start button".</summary>
    public const JoyButton Start = JoyButton.Start;
}
