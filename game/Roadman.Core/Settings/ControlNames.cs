using System.Globalization;

namespace Roadman.Core.Settings;

/// <summary>
/// Plain English names of the input actions and of gamepad and mouse inputs, for the controls table and the menus
/// (ADR 0016 and ADR 0018, "Gamepad names"). Gamepad inputs have generic names only, never a console maker's button
/// names. Indices are Godot's <c>JoyButton</c>, <c>JoyAxis</c> and <c>MouseButton</c> values.
/// </summary>
public static class ControlNames
{
    private static readonly Dictionary<string, string> ActionLabels = new(StringComparer.Ordinal)
    {
        ["move_forward"] = "Move forward",
        ["move_back"] = "Move back",
        ["move_left"] = "Move left",
        ["move_right"] = "Move right",
        ["run"] = "Run (hold)",
        ["run_toggle"] = "Run toggle",
        ["jump"] = "Jump",
        ["camera_left"] = "Camera left",
        ["camera_right"] = "Camera right",
        ["camera_up"] = "Camera up",
        ["camera_down"] = "Camera down",
        ["pause"] = "Pause",
        ["inventory"] = "Inventory",
        ["debug_camera"] = "Debug camera",
        ["debug_orthophoto"] = "Orthophoto view",
        ["debug_road_graph"] = "Road graph view",
        ["debug_inspect"] = "Place inspector",
        ["debug_forward"] = "Debug camera forward",
        ["debug_back"] = "Debug camera back",
        ["debug_left"] = "Debug camera left",
        ["debug_right"] = "Debug camera right",
        ["debug_up"] = "Debug camera up",
        ["debug_down"] = "Debug camera down",
        ["debug_fast"] = "Debug camera fast",
    };

    private static readonly string[] ButtonNames =
    [
        "South face button",
        "East face button",
        "West face button",
        "North face button",
        "Back button",
        "Guide button",
        "Start button",
        "Left-stick click",
        "Right-stick click",
        "Left shoulder button",
        "Right shoulder button",
        "D-pad up",
        "D-pad down",
        "D-pad left",
        "D-pad right",
    ];

    /// <summary>The gameplay actions in the order the controls table shows them (no debug actions).</summary>
    public static IReadOnlyList<string> GameplayActionOrder { get; } =
    [
        "move_forward",
        "move_back",
        "move_left",
        "move_right",
        "run",
        "run_toggle",
        "jump",
        "camera_left",
        "camera_right",
        "camera_up",
        "camera_down",
        "pause",
        "inventory",
    ];

    /// <summary>The label of <paramref name="action"/> ("Move forward", "Run (hold)", ...); an unknown action gives its own name.</summary>
    public static string ActionLabel(string action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return ActionLabels.TryGetValue(action, out var label) ? label : action;
    }

    /// <summary>True for the debug-build actions: <c>debug_camera</c> and every <c>debug_*</c> action.</summary>
    public static bool IsDebugAction(string action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return action.StartsWith("debug_", StringComparison.Ordinal);
    }

    /// <summary>
    /// The generic name of gamepad button <paramref name="button"/> (Godot's <c>JoyButton</c> index): 0 "South face
    /// button" ... 14 "D-pad right"; any other index "Button &lt;n&gt;".
    /// </summary>
    public static string GamepadButtonName(int button) =>
        button >= 0 && button < ButtonNames.Length
            ? ButtonNames[button]
            : string.Create(CultureInfo.InvariantCulture, $"Button {button}");

    /// <summary>
    /// The generic name of gamepad axis <paramref name="axis"/> (Godot's <c>JoyAxis</c> index) pushed towards the sign
    /// of <paramref name="sign"/>: 0 and 2 "Left/Right stick left" (negative) or "... right"; 1 and 3 "Left/Right stick
    /// up" (negative) or "... down"; 4 "Left trigger", 5 "Right trigger" (any sign); any other axis "Axis &lt;n&gt; +"
    /// or "Axis &lt;n&gt; −" (a sign of 0 counts as positive).
    /// </summary>
    public static string GamepadAxisName(int axis, float sign)
    {
        var negative = sign < 0;
        return axis switch
        {
            0 => negative ? "Left stick left" : "Left stick right",
            1 => negative ? "Left stick up" : "Left stick down",
            2 => negative ? "Right stick left" : "Right stick right",
            3 => negative ? "Right stick up" : "Right stick down",
            4 => "Left trigger",
            5 => "Right trigger",
            _ => string.Create(CultureInfo.InvariantCulture, $"Axis {axis} {(negative ? "−" : "+")}"),
        };
    }

    /// <summary>
    /// The name of mouse button <paramref name="button"/> (Godot's <c>MouseButton</c> value): 1 "Left mouse button",
    /// 2 "Right mouse button", 3 "Middle mouse button", 4 "Mouse wheel up", 5 "Mouse wheel down"; any other "Mouse
    /// button &lt;n&gt;".
    /// </summary>
    public static string MouseButtonName(int button) =>
        button switch
        {
            1 => "Left mouse button",
            2 => "Right mouse button",
            3 => "Middle mouse button",
            4 => "Mouse wheel up",
            5 => "Mouse wheel down",
            _ => string.Create(CultureInfo.InvariantCulture, $"Mouse button {button}"),
        };
}
