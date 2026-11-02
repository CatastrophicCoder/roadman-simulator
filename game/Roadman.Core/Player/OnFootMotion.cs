namespace Roadman.Core.Player;

/// <summary>The result of one <see cref="OnFootMotion.Step"/>: the new horizontal velocity and facing.</summary>
/// <param name="VelocityX">Velocity along +X (east), m/s.</param>
/// <param name="VelocityZ">Velocity along +Z (south), m/s.</param>
/// <param name="FacingYaw">The body's yaw in radians, in (−π, π] (see <see cref="OnFootMotion"/> for the convention).</param>
public readonly record struct OnFootStep(double VelocityX, double VelocityZ, double FacingYaw)
{
    /// <summary>The horizontal speed, m/s.</summary>
    public double Speed => Math.Sqrt((VelocityX * VelocityX) + (VelocityZ * VelocityZ));
}

/// <summary>
/// The on-foot movement maths of <c>docs/specs/player-on-foot.md</c>, with no engine types: walking and running
/// relative to the camera, acceleration limits, air control and turning. The Godot <c>Player</c> passes its exports
/// as the properties and applies gravity and jumps itself.
/// </summary>
/// <remarks>
/// <para>
/// Conventions (Godot's): +X east, +Y up, −Z north. A yaw θ is a rotation about +Y; a node with yaw θ looks along its
/// local −Z, which is the world direction (−sin θ, −cos θ) in (x, z). So yaw 0 looks north (−Z) and yaw π/2 looks
/// west (−X).
/// </para>
/// <para>
/// The move vector is <c>Input.GetVector("left", "right", "forward", "back")</c>: x is right (+1) or left (−1), y is
/// back (+1) or forward (−1). It is rotated by the camera yaw, so for a camera with yaw θ the world direction is
/// (x cos θ + y sin θ, −x sin θ + y cos θ): forward (y = −1) moves along (−sin θ, −cos θ), away from the camera, and
/// right (x = +1) along (cos θ, −sin θ).
/// </para>
/// <para>
/// The facing yaw uses the same convention: it turns toward <c>atan2(−dx, −dz)</c> for a move direction (dx, dz),
/// so the body's −Z (its front) points where it walks.
/// </para>
/// </remarks>
public sealed class OnFootMotion
{
    private double _walkSpeed = 1.4;
    private double _runSpeed = 5.0;
    private double _acceleration = 20;
    private double _deceleration = 25;
    private double _airControl = 0.3;
    private double _turnSpeed = 720;

    /// <summary>Uses the spec's starting values (walk 1.4 m/s, run 5.0 m/s, 20 and 25 m/s², air control 0.3, 720 °/s).</summary>
    public OnFootMotion()
    {
    }

    /// <exception cref="ArgumentOutOfRangeException">A value that its property rejects.</exception>
    public OnFootMotion(
        double walkSpeed,
        double runSpeed,
        double acceleration,
        double deceleration,
        double airControl,
        double turnSpeed)
    {
        WalkSpeed = walkSpeed;
        RunSpeed = runSpeed;
        Acceleration = acceleration;
        Deceleration = deceleration;
        AirControl = airControl;
        TurnSpeed = turnSpeed;
    }

    /// <summary>Speed at full input while walking, m/s; finite and at least 0.</summary>
    public double WalkSpeed
    {
        get => _walkSpeed;
        set => _walkSpeed = NonNegative(value);
    }

    /// <summary>Speed at full input while running, m/s; finite and at least 0.</summary>
    public double RunSpeed
    {
        get => _runSpeed;
        set => _runSpeed = NonNegative(value);
    }

    /// <summary>Largest change of the horizontal velocity per second with input, m/s²; finite and at least 0.</summary>
    public double Acceleration
    {
        get => _acceleration;
        set => _acceleration = NonNegative(value);
    }

    /// <summary>Largest change of the horizontal velocity per second without input, m/s²; finite and at least 0.</summary>
    public double Deceleration
    {
        get => _deceleration;
        set => _deceleration = NonNegative(value);
    }

    /// <summary>Factor (0 to 1) on <see cref="Acceleration"/> and <see cref="Deceleration"/> while not on the floor.</summary>
    public double AirControl
    {
        get => _airControl;
        set => _airControl = double.IsFinite(value) && value is >= 0 and <= 1
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value, "must be between 0 and 1");
    }

    /// <summary>Largest facing change per second, in <b>degrees</b> per second; finite and at least 0.</summary>
    public double TurnSpeed
    {
        get => _turnSpeed;
        set => _turnSpeed = NonNegative(value);
    }

    /// <summary>The take-off speed that reaches <paramref name="height"/> metres under <paramref name="gravity"/> m/s²: √(2 · gravity · height).</summary>
    /// <exception cref="ArgumentOutOfRangeException">A negative or non-finite argument.</exception>
    public static double JumpVelocity(double height, double gravity) =>
        Math.Sqrt(2 * NonNegative(gravity, nameof(gravity)) * NonNegative(height, nameof(height)));

    /// <summary>
    /// Advances the horizontal velocity and the facing by <paramref name="delta"/> seconds.
    /// </summary>
    /// <param name="moveX">Move input x: +1 right, −1 left.</param>
    /// <param name="moveY">Move input y: −1 forward (away from the camera), +1 back. The vector's length is clamped to 1.</param>
    /// <param name="cameraYaw">The camera's yaw in radians (see the class remarks).</param>
    /// <param name="run">Whether the run action is held: the target speed is <see cref="RunSpeed"/> × |move|, else <see cref="WalkSpeed"/> × |move|.</param>
    /// <param name="onFloor">False in the air: the rate is scaled by <see cref="AirControl"/>.</param>
    /// <param name="velocityX">Current velocity along +X, m/s.</param>
    /// <param name="velocityZ">Current velocity along +Z, m/s.</param>
    /// <param name="facingYaw">Current facing yaw in radians.</param>
    /// <param name="delta">Time step in seconds, at least 0.</param>
    /// <returns>
    /// The velocity moved toward the target by at most <see cref="Acceleration"/> × delta (with input) or
    /// <see cref="Deceleration"/> × delta (without), times <see cref="AirControl"/> in the air; and the facing turned
    /// toward the move direction by at most <see cref="TurnSpeed"/> × delta, or unchanged without input (wrapped to
    /// (−π, π]).
    /// </returns>
    /// <exception cref="ArgumentException">A non-finite argument or a negative <paramref name="delta"/>.</exception>
    public OnFootStep Step(
        double moveX,
        double moveY,
        double cameraYaw,
        bool run,
        bool onFloor,
        double velocityX,
        double velocityZ,
        double facingYaw,
        double delta)
    {
        RequireFinite(moveX, nameof(moveX));
        RequireFinite(moveY, nameof(moveY));
        RequireFinite(cameraYaw, nameof(cameraYaw));
        RequireFinite(velocityX, nameof(velocityX));
        RequireFinite(velocityZ, nameof(velocityZ));
        RequireFinite(facingYaw, nameof(facingYaw));
        RequireFinite(delta, nameof(delta));
        if (delta < 0)
        {
            throw new ArgumentException($"must not be negative, is {delta}", nameof(delta));
        }

        var magnitude = Math.Sqrt((moveX * moveX) + (moveY * moveY));
        if (magnitude > 1)
        {
            moveX /= magnitude;
            moveY /= magnitude;
            magnitude = 1;
        }

        var hasInput = magnitude > 0;
        var sin = Math.Sin(cameraYaw);
        var cos = Math.Cos(cameraYaw);

        // (moveX, 0, moveY) rotated by the camera yaw about +Y.
        var directionX = (moveX * cos) + (moveY * sin);
        var directionZ = (-moveX * sin) + (moveY * cos);

        var speed = run ? RunSpeed : WalkSpeed;
        var targetX = directionX * speed;
        var targetZ = directionZ * speed;

        var rate = hasInput ? Acceleration : Deceleration;
        if (!onFloor)
        {
            rate *= AirControl;
        }

        var (newX, newZ) = MoveToward(velocityX, velocityZ, targetX, targetZ, rate * delta);

        var facing = WrapAngle(facingYaw);
        if (hasInput)
        {
            var desired = Math.Atan2(-directionX, -directionZ);
            var difference = WrapAngle(desired - facing);
            var maxTurn = TurnSpeed * Math.PI / 180 * delta;
            facing = WrapAngle(facing + Math.Clamp(difference, -maxTurn, maxTurn));
        }

        return new OnFootStep(newX, newZ, facing);
    }

    /// <summary>Wraps <paramref name="angle"/> (radians) into (−π, π].</summary>
    public static double WrapAngle(double angle)
    {
        var wrapped = Math.IEEERemainder(angle, 2 * Math.PI);
        return wrapped <= -Math.PI ? wrapped + (2 * Math.PI) : wrapped;
    }

    private static (double X, double Z) MoveToward(double x, double z, double targetX, double targetZ, double maxDelta)
    {
        var dx = targetX - x;
        var dz = targetZ - z;
        var distance = Math.Sqrt((dx * dx) + (dz * dz));
        if (distance <= maxDelta || distance == 0)
        {
            return (targetX, targetZ);
        }

        var scale = maxDelta / distance;
        return (x + (dx * scale), z + (dz * scale));
    }

    private static double NonNegative(double value) => NonNegative(value, nameof(value));

    private static double NonNegative(double value, string name) =>
        double.IsFinite(value) && value >= 0
            ? value
            : throw new ArgumentOutOfRangeException(name, value, "must be finite and at least 0");

    private static void RequireFinite(double value, string name)
    {
        if (!double.IsFinite(value))
        {
            throw new ArgumentException($"must be finite, is {value}", name);
        }
    }
}
