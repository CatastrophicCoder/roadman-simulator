using Godot;
using Roadman.Scenes;

namespace Roadman.OnFoot;

/// <summary>
/// The third-person camera of <c>docs/specs/player-on-foot.md</c>: this node is a pivot that follows
/// <see cref="Target"/> at <see cref="PivotHeight"/> above its feet, turned by <see cref="Yaw"/> and
/// <see cref="Pitch"/>. It holds a <see cref="SpringArm3D"/> (a sphere of <see cref="CameraCollisionRadius"/>, map
/// layer only, so it ignores the player) with the <see cref="Camera3D"/> at its end, behind the pivot (+Z).
/// <para>
/// The arm shortens at once when blocked (the spring arm's own cast) and extends back at
/// <see cref="ArmReturnSpeed"/>: each physics frame <see cref="SpringArm3D.SpringLength"/> is set to the last hit
/// length plus <see cref="ArmReturnSpeed"/> × delta, at most <see cref="ArmLength"/>. The pivot runs after the player
/// and the arm after the pivot (<see cref="Node.ProcessPhysicsPriority"/>), so the arm casts from this frame's pivot.
/// </para>
/// <para>
/// Mouse orbit only while the mouse is captured, this camera is current and <see cref="InputEnabled"/>; right-stick
/// orbit (<c>camera_left</c>, <c>camera_right</c>, <c>camera_up</c>, <c>camera_down</c>, through
/// <see cref="OrbitStick"/>) each physics frame while this camera is current and <see cref="InputEnabled"/>. The mouse is
/// captured while this camera is current and the window has focus; <c>ui_cancel</c> releases it, a left click
/// captures it again, losing focus releases it (and regaining it captures again unless <c>ui_cancel</c> released it).
/// Whether this camera holds the capture (<see cref="MouseCaptured"/>) follows <see cref="Input.MouseMode"/>, so a
/// release by anything else can't leave it believing it still holds the mouse.
/// There is no automatic swing-back: the yaw changes only with input or <see cref="PlaceBehind"/>.
/// </para>
/// </summary>
public partial class PlayerCamera : Node3D
{
    /// <summary>Physics priority of the pivot; after the player (0), so it follows this frame's position.</summary>
    public const int PivotPhysicsPriority = 10;

    /// <summary>Physics priority of the spring arm; after the pivot, so it casts from this frame's pivot.</summary>
    public const int ArmPhysicsPriority = 11;

    public const string CameraLeftAction = "camera_left";
    public const string CameraRightAction = "camera_right";
    public const string CameraUpAction = "camera_up";
    public const string CameraDownAction = "camera_down";

    private float _yaw;
    private float _pitch;
    private bool _ownsCapture;
    private bool _captureWanted;
    private bool _armReset = true;
    private float _cameraCollisionRadius = 0.2f;
    private float _fov = 70f;

    /// <summary>Pivot height above the target's origin (its feet), m.</summary>
    [Export]
    public float PivotHeight { get; set; } = 1.6f;

    /// <summary>Full arm length, m.</summary>
    [Export]
    public float ArmLength { get; set; } = 4.0f;

    /// <summary>Radius of the arm's sphere cast, m; wider than the near plane's half-extent. Applied at once when ready.</summary>
    [Export]
    public float CameraCollisionRadius
    {
        get => _cameraCollisionRadius;
        set
        {
            _cameraCollisionRadius = value;
            if (SpringArm is not null && IsInstanceValid(SpringArm))
            {
                SpringArm.Shape = new SphereShape3D { Radius = value };
            }
        }
    }

    /// <summary>How fast a shortened arm extends back, m/s.</summary>
    [Export]
    public float ArmReturnSpeed { get; set; } = 8f;

    [Export]
    public float MinPitchDegrees { get; set; } = -70f;

    [Export]
    public float MaxPitchDegrees { get; set; } = 30f;

    /// <summary>Pitch given by <see cref="PlaceBehind"/>, degrees (negative looks down at the player).</summary>
    [Export]
    public float StartPitchDegrees { get; set; } = -15f;

    /// <summary>Radians per pixel of mouse motion.</summary>
    [Export]
    public float MouseSensitivity { get; set; } = 0.003f;

    /// <summary>Radians per second at full right-stick tilt.</summary>
    [Export]
    public float StickSensitivity { get; set; } = 3f;

    /// <summary>Mouse up pitches down when true. Default false (not inverted); the settings view will expose it.</summary>
    [Export]
    public bool InvertMouseY { get; set; }

    /// <summary>Stick up pitches down when true. Default false (not inverted); the settings view will expose it.</summary>
    [Export]
    public bool InvertStickY { get; set; }

    /// <summary>
    /// Time constant of the pivot following the target, s; 0 is rigid. With a value above 0 the pivot closes about
    /// 63 % of the gap in that time.
    /// </summary>
    [Export]
    public float FollowSmoothing { get; set; }

    /// <summary>Vertical field of view, degrees. Applied at once when ready.</summary>
    [Export]
    public float Fov
    {
        get => _fov;
        set
        {
            _fov = value;
            if (Camera is not null && IsInstanceValid(Camera))
            {
                Camera.Fov = value;
            }
        }
    }

    /// <summary>The node followed (the player); null leaves the pivot where it is.</summary>
    public Node3D? Target { get; set; }

    /// <summary>False while something else owns the controls (the debug camera): no mouse or stick orbit, no mouse capture.</summary>
    public bool InputEnabled { get; set; } = true;

    public SpringArm3D SpringArm { get; private set; } = null!;

    public Camera3D Camera { get; private set; } = null!;

    /// <summary>Whether this camera is the viewport's current one.</summary>
    public bool IsCurrent => Camera is not null && Camera.Current;

    /// <summary>
    /// Whether this camera holds the mouse capture: it captured the mouse and <see cref="Input.MouseMode"/> is still
    /// captured. Derived from the engine's mouse mode, so anything else releasing the mouse ends it at once.
    /// </summary>
    public bool MouseCaptured => _ownsCapture && Input.MouseMode == Input.MouseModeEnum.Captured;

    /// <summary>Yaw in radians (Godot's convention: yaw 0 looks along −Z), wrapped to [−π, π).</summary>
    public float Yaw
    {
        get => _yaw;
        set
        {
            _yaw = Mathf.Wrap(value, -Mathf.Pi, Mathf.Pi);
            ApplyRotation();
        }
    }

    /// <summary>Pitch in radians, clamped to [<see cref="MinPitchDegrees"/>, <see cref="MaxPitchDegrees"/>]; positive looks up.</summary>
    public float Pitch
    {
        get => _pitch;
        set
        {
            _pitch = Mathf.Clamp(value, Mathf.DegToRad(MinPitchDegrees), Mathf.DegToRad(MaxPitchDegrees));
            ApplyRotation();
        }
    }

    /// <summary>The pivot point the camera orbits: <see cref="Target"/>'s position plus <see cref="PivotHeight"/>.</summary>
    public Vector3? DesiredPivot =>
        Target is not null && IsInstanceValid(Target) && Target.IsInsideTree()
            ? Target.GlobalPosition + (Vector3.Up * PivotHeight)
            : null;

    public override void _Ready()
    {
        // Yaw and pitch turn this node, so the arm (along +Z) is behind the view direction (−Z).
        ProcessPhysicsPriority = PivotPhysicsPriority;
        SpringArm = new SpringArm3D
        {
            Name = "SpringArm",
            Shape = new SphereShape3D { Radius = CameraCollisionRadius },
            CollisionMask = CollisionLayers.Map,
            SpringLength = ArmLength,
            ProcessPhysicsPriority = ArmPhysicsPriority,
        };
        AddChild(SpringArm);
        Camera = new Camera3D { Name = "Camera", Fov = Fov, Current = false };
        SpringArm.AddChild(Camera);
        Pitch = Mathf.DegToRad(StartPitchDegrees);
    }

    public override void _PhysicsProcess(double delta)
    {
        // Before the pivot and arm update, so this frame's arm casts along the turned direction.
        if (IsCurrent && InputEnabled)
        {
            var stick = Input.GetVector(CameraLeftAction, CameraRightAction, CameraUpAction, CameraDownAction);
            if (stick != Vector2.Zero)
            {
                OrbitStick(stick, delta);
            }
        }

        if (DesiredPivot is { } pivot)
        {
            GlobalPosition = FollowSmoothing <= 0f
                ? pivot
                : GlobalPosition.Lerp(pivot, 1f - Mathf.Exp(-(float)delta / FollowSmoothing));
        }

        // Shortening is the arm's own cast this frame; extending is limited here. After a (re)spawn the arm starts at
        // full length, so the camera doesn't zoom out from the pivot.
        if (_armReset)
        {
            _armReset = false;
            SpringArm.SpringLength = ArmLength;
            return;
        }

        var hitLength = SpringArm.GetHitLength();
        SpringArm.SpringLength = Mathf.Min(ArmLength, hitLength + (ArmReturnSpeed * (float)delta));
    }

    public override void _Process(double delta)
    {
        // Resync with the engine: something else released the mouse, so this camera no longer holds it (a left
        // click captures it again).
        if (_ownsCapture && Input.MouseMode != Input.MouseModeEnum.Captured)
        {
            _ownsCapture = false;
        }

        if (!IsCurrent && _ownsCapture)
        {
            ReleaseMouse(keepWanted: false);
        }
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (!IsCurrent || !InputEnabled)
        {
            return;
        }

        if (@event.IsActionPressed("ui_cancel"))
        {
            if (MouseCaptured)
            {
                ReleaseMouse(keepWanted: false);
                GetViewport().SetInputAsHandled();
            }

            return;
        }

        switch (@event)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } when !MouseCaptured:
                _captureWanted = true;
                CaptureMouse();
                GetViewport().SetInputAsHandled();
                break;
            case InputEventMouseMotion motion when MouseCaptured:
                Orbit(motion.Relative);
                GetViewport().SetInputAsHandled();
                break;
        }
    }

    public override void _Notification(int what)
    {
        if (what == NotificationApplicationFocusOut)
        {
            ReleaseMouse(keepWanted: true);
        }
        else if (what == NotificationApplicationFocusIn && _captureWanted && IsCurrent && InputEnabled)
        {
            CaptureMouse();
        }
    }

    public override void _ExitTree()
    {
        ReleaseMouse(keepWanted: false);
    }

    /// <summary>
    /// Applies a mouse motion of <paramref name="relative"/> pixels: right turns right, up pitches up (down with
    /// <see cref="InvertMouseY"/>).
    /// </summary>
    public void Orbit(Vector2 relative)
    {
        var pitchSign = InvertMouseY ? -1f : 1f;
        _yaw = Mathf.Wrap(_yaw - (relative.X * MouseSensitivity), -Mathf.Pi, Mathf.Pi);
        Pitch = _pitch - (relative.Y * MouseSensitivity * pitchSign);
    }

    /// <summary>
    /// Applies a right-stick tilt of <paramref name="stick"/> (x right, y down, as <see cref="Input.GetVector"/> gives it)
    /// for <paramref name="delta"/> seconds at <see cref="StickSensitivity"/>: right turns right, up pitches up (down
    /// with <see cref="InvertStickY"/>). Pitch stays within its limits.
    /// </summary>
    public void OrbitStick(Vector2 stick, double delta)
    {
        var pitchSign = InvertStickY ? -1f : 1f;
        var step = StickSensitivity * (float)delta;
        _yaw = Mathf.Wrap(_yaw - (stick.X * step), -Mathf.Pi, Mathf.Pi);
        Pitch = _pitch - (stick.Y * step * pitchSign);
    }

    /// <summary>Turns the camera to look the way <paramref name="facingYaw"/> faces, at <see cref="StartPitchDegrees"/>, and snaps it to the target.</summary>
    public void PlaceBehind(float facingYaw)
    {
        _yaw = Mathf.Wrap(facingYaw, -Mathf.Pi, Mathf.Pi);
        Pitch = Mathf.DegToRad(StartPitchDegrees);
        SnapToTarget();
    }

    /// <summary>
    /// Moves the pivot onto the target at once (after a spawn or respawn), whatever <see cref="FollowSmoothing"/> is,
    /// and lets the arm start the next physics frame at full length (the cast still shortens it when blocked).
    /// </summary>
    public void SnapToTarget()
    {
        _armReset = true;
        if (DesiredPivot is { } pivot)
        {
            GlobalPosition = pivot;
        }
    }

    /// <summary>
    /// Makes this camera current. With <paramref name="captureMouse"/> it also captures the mouse when the window has
    /// focus (and again when focus returns); without it the mouse stays as it is until a left click captures it.
    /// </summary>
    public void Activate(bool captureMouse = true)
    {
        Camera.MakeCurrent();
        if (!captureMouse)
        {
            return;
        }

        _captureWanted = true;
        if (InputEnabled && GetWindow().HasFocus())
        {
            CaptureMouse();
        }
    }

    private void CaptureMouse()
    {
        _ownsCapture = true;
        Input.MouseMode = Input.MouseModeEnum.Captured;
    }

    /// <summary>Releases the mouse only when this camera holds the capture, so it never frees another node's.</summary>
    private void ReleaseMouse(bool keepWanted)
    {
        _captureWanted &= keepWanted;
        if (!_ownsCapture)
        {
            return;
        }

        _ownsCapture = false;
        if (Input.MouseMode == Input.MouseModeEnum.Captured)
        {
            Input.MouseMode = Input.MouseModeEnum.Visible;
        }
    }

    private void ApplyRotation() => Rotation = new Vector3(_pitch, _yaw, 0f);
}
