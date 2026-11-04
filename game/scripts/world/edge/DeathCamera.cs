using Godot;

namespace Roadman.Scenes.Edge;

/// <summary>
/// The camera of the death sequence (<c>docs/specs/world-edge-game.md</c>, "Death sequence", step 1), added by
/// <c>World</c> in every build and not current until a fall. <see cref="Begin"/> starts it at the player camera's
/// transform and field of view and makes it current; over <see cref="PullBackTime"/> it moves (smoothstep) to
/// <see cref="TargetPosition"/> while its orientation turns (slerp, the same smoothstep) to look at
/// <see cref="LookAtPoint"/>, then holds still. It has no collision. <see cref="Stop"/> ends it.
/// </summary>
public partial class DeathCamera : Camera3D
{
    /// <summary>Seconds the pull-back takes.</summary>
    [Export]
    public float PullBackTime { get; set; } = 2.5f;

    /// <summary>How far outward of the ring point the camera ends, m.</summary>
    [Export]
    public float PullBackDistance { get; set; } = 60f;

    /// <summary>The camera's end height relative to the ledge, m (negative: below it).</summary>
    [Export]
    public float PullBackHeight { get; set; } = -10f;

    /// <summary>How far below the ledge the camera looks at the ring point, m.</summary>
    [Export]
    public float LookAtDrop { get; set; } = 15f;

    /// <summary>Whether a pull-back is running or holding (between <see cref="Begin"/> and <see cref="Stop"/>).</summary>
    public bool IsActive { get; private set; }

    /// <summary>Seconds since <see cref="Begin"/>.</summary>
    public double Elapsed { get; private set; }

    /// <summary>The transform the pull-back started from.</summary>
    public Transform3D StartTransform { get; private set; } = Transform3D.Identity;

    /// <summary>The field of view the pull-back started with (it keeps it).</summary>
    public float StartFov { get; private set; }

    /// <summary>Where the camera ends.</summary>
    public Vector3 TargetPosition { get; private set; }

    /// <summary>What the camera looks at in the end.</summary>
    public Vector3 LookAtPoint { get; private set; }

    /// <summary>The final orientation (looking from <see cref="TargetPosition"/> at <see cref="LookAtPoint"/>).</summary>
    public Basis TargetBasis { get; private set; } = Basis.Identity;

    /// <summary>Whether the pull-back has reached its target.</summary>
    public bool IsFinished => IsActive && Elapsed >= PullBackTime;

    public DeathCamera()
    {
        Current = false;
    }

    /// <summary>
    /// Starts at <paramref name="start"/> with <paramref name="fov"/>, becomes current and pulls back to
    /// <paramref name="target"/>, looking at <paramref name="lookAt"/>.
    /// </summary>
    public void Begin(Transform3D start, float fov, Vector3 target, Vector3 lookAt)
    {
        StartTransform = start.Orthonormalized();
        StartFov = fov;
        TargetPosition = target;
        LookAtPoint = lookAt;
        var direction = lookAt - target;
        TargetBasis = direction.LengthSquared() > 1e-8f && Mathf.Abs(direction.Normalized().Dot(Vector3.Up)) < 0.999f
            ? Basis.LookingAt(direction, Vector3.Up)
            : StartTransform.Basis;
        Elapsed = 0;
        IsActive = true;
        Fov = fov;
        GlobalTransform = StartTransform;
        MakeCurrent();
    }

    /// <summary>Ends the sequence's camera: no longer current or active.</summary>
    public void Stop()
    {
        IsActive = false;
        if (Current)
        {
            ClearCurrent(false);
        }
    }

    /// <summary>Moves the camera <paramref name="delta"/> seconds on (called every frame while active).</summary>
    public void Advance(double delta)
    {
        if (!IsActive)
        {
            return;
        }

        Elapsed += delta;
        GlobalTransform = TransformAt(Elapsed);
    }

    /// <summary>The camera's transform <paramref name="time"/> seconds after <see cref="Begin"/>.</summary>
    public Transform3D TransformAt(double time)
    {
        var t = PullBackTime > 0f ? Mathf.Clamp((float)(time / PullBackTime), 0f, 1f) : 1f;
        var eased = Mathf.SmoothStep(0f, 1f, t);
        var startRotation = StartTransform.Basis.GetRotationQuaternion();
        var targetRotation = TargetBasis.GetRotationQuaternion();
        var basis = new Basis(startRotation.Slerp(targetRotation, eased));
        return new Transform3D(basis, StartTransform.Origin.Lerp(TargetPosition, eased));
    }

    public override void _Process(double delta) => Advance(delta);
}
