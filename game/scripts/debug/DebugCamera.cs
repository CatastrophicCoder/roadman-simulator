using Godot;

namespace Roadman.Debug;

/// <summary>
/// Free-flying camera for looking around the map.
/// WASD moves, Q/E move down/up, holding the right mouse button looks around,
/// Shift moves faster and the mouse wheel changes the base speed.
/// Input is read only while this camera is <see cref="Camera3D.Current"/>, so the player's WASD and Shift don't move
/// it while the player camera is current. It changes the mouse mode only while current: when another camera becomes
/// current it stops looking and leaves the mouse to that camera.
/// </summary>
public partial class DebugCamera : Camera3D
{
    private const float MaxPitchRadians = 1.55f;

    [Export]
    public float MoveSpeed { get; set; } = 20f;

    [Export]
    public float FastMultiplier { get; set; } = 4f;

    [Export]
    public float MouseSensitivity { get; set; } = 0.003f;

    [Export]
    public float WheelSpeedFactor { get; set; } = 1.2f;

    [Export]
    public float MinMoveSpeed { get; set; } = 1f;

    [Export]
    public float MaxMoveSpeed { get; set; } = 500f;

    private bool _looking;

    public override void _UnhandledInput(InputEvent @event)
    {
        if (!Current)
        {
            return;
        }

        switch (@event)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.Right } button:
                SetLooking(button.Pressed);
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.WheelUp, Pressed: true }:
                MoveSpeed = Mathf.Min(MoveSpeed * WheelSpeedFactor, MaxMoveSpeed);
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.WheelDown, Pressed: true }:
                MoveSpeed = Mathf.Max(MoveSpeed / WheelSpeedFactor, MinMoveSpeed);
                break;
            case InputEventMouseMotion motion when _looking:
                Look(motion.Relative);
                break;
        }
    }

    public override void _Process(double delta)
    {
        if (_looking && !Current)
        {
            // Another camera became current (the player camera, which may have captured the mouse itself): stop
            // looking without touching the mouse mode.
            _looking = false;
        }

        if (!Current)
        {
            return;
        }

        // The button release can miss _UnhandledInput (eaten by GUI, window lost focus); never stay captured.
        if (_looking && !Input.IsMouseButtonPressed(MouseButton.Right))
        {
            SetLooking(false);
        }
        else if (_looking && Input.MouseMode != Input.MouseModeEnum.Captured)
        {
            // Something else released the mouse while the button is still held: take it back.
            Input.MouseMode = Input.MouseModeEnum.Captured;
        }

        var input = Input.GetVector("debug_left", "debug_right", "debug_forward", "debug_back");
        var vertical = Input.GetAxis("debug_down", "debug_up");
        var basis = GlobalBasis;
        var direction = (basis.X * input.X) + (basis.Z * input.Y) + (Vector3.Up * vertical);
        if (direction == Vector3.Zero)
        {
            return;
        }

        var speed = MoveSpeed * (Input.IsActionPressed("debug_fast") ? FastMultiplier : 1f);
        GlobalPosition += direction.Normalized() * speed * (float)delta;
    }

    public override void _Notification(int what)
    {
        if (what == NotificationApplicationFocusOut)
        {
            SetLooking(false);
        }
    }

    public override void _ExitTree()
    {
        SetLooking(false);
    }

    /// <summary>
    /// Starts or stops looking. The mouse mode changes only while this camera is current, so it never releases a capture
    /// another camera holds.
    /// </summary>
    private void SetLooking(bool looking)
    {
        if (_looking == looking)
        {
            return;
        }

        _looking = looking;
        if (Current)
        {
            Input.MouseMode = looking ? Input.MouseModeEnum.Captured : Input.MouseModeEnum.Visible;
        }
    }

    private void Look(Vector2 relative)
    {
        var rotation = Rotation;
        rotation.Y -= relative.X * MouseSensitivity;
        rotation.X = Mathf.Clamp(rotation.X - (relative.Y * MouseSensitivity), -MaxPitchRadians, MaxPitchRadians);
        rotation.Z = 0f;
        Rotation = rotation;
    }
}
