using Roadman.Core.Player;

namespace Roadman.Core.Tests.Player;

/// <summary><see cref="OnFootMotion"/>: <c>docs/specs/player-on-foot.md</c>, acceptance criterion 5.</summary>
public class OnFootMotionTests
{
    private const double Dt = 1.0 / 60;

    // Forward input is moveY = −1 (Input.GetVector's "forward" is negative y).
    private const double Forward = -1;

    [Fact]
    public void Defaults_AreTheSpecsStartingValues()
    {
        var motion = new OnFootMotion();

        Assert.Equal(2.8, motion.WalkSpeed);
        Assert.Equal(7.5, motion.RunSpeed);
        Assert.Equal(20, motion.Acceleration);
        Assert.Equal(25, motion.Deceleration);
        Assert.Equal(0.3, motion.AirControl);
        Assert.Equal(720, motion.TurnSpeed);
    }

    [Theory]
    [InlineData(false, 1.0)]
    [InlineData(true, 1.0)]
    [InlineData(false, 0.5)]
    [InlineData(true, 0.5)]
    public void Step_FromRestUnderConstantInput_ReachesSpeedTimesInput(bool run, double input)
    {
        var motion = new OnFootMotion();
        var expected = (run ? motion.RunSpeed : motion.WalkSpeed) * input;

        // 2 s is far more than RunSpeed / Acceleration = 0.375 s.
        var step = Run(motion, 0, Forward * input, cameraYaw: 0, run, onFloor: true, steps: 120);

        Assert.InRange(step.Speed, expected * 0.99, expected * 1.01);
    }

    [Fact]
    public void Step_HalfInput_GivesHalfTheFullInputSpeed()
    {
        var motion = new OnFootMotion();
        foreach (var run in new[] { false, true })
        {
            var full = Run(motion, 0, Forward, 0, run, onFloor: true, steps: 120).Speed;
            var half = Run(motion, 0, Forward * 0.5, 0, run, onFloor: true, steps: 120).Speed;

            Assert.InRange(half, full * 0.5 * 0.99, full * 0.5 * 1.01);
        }
    }

    [Fact]
    public void Step_InputLongerThanOne_IsClampedToFullSpeed()
    {
        var motion = new OnFootMotion();

        var step = Run(motion, 1, Forward, 0, run: true, onFloor: true, steps: 120);

        Assert.InRange(step.Speed, motion.RunSpeed * 0.99, motion.RunSpeed * 1.01);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void Step_ForwardInput_MovesAwayFromTheCamera(double cameraYawDegrees)
    {
        // A camera with yaw θ looks along (−sin θ, −cos θ); "away from the camera" is that direction.
        var motion = new OnFootMotion();
        var theta = cameraYawDegrees * Math.PI / 180;
        var lookX = -Math.Sin(theta);
        var lookZ = -Math.Cos(theta);

        var step = Run(motion, 0, Forward, theta, run: false, onFloor: true, steps: 120);

        var alongLook = ((step.VelocityX * lookX) + (step.VelocityZ * lookZ)) / step.Speed;
        Assert.InRange(alongLook, 1 - 1e-9, 1 + 1e-9);

        // The body ends up facing that way too (its −Z along the look direction, so its yaw is the camera's).
        Assert.InRange(Math.Abs(OnFootMotion.WrapAngle(step.FacingYaw - theta)), 0, 1e-9);
    }

    [Fact]
    public void Step_VelocityChangePerStep_NeverExceedsTheAccelerationLimit()
    {
        var motion = new OnFootMotion();
        var random = new Random(5);
        var vx = 0.0;
        var vz = 0.0;
        var facing = 0.0;
        var changes = 0;

        // Random input, camera, run flag and gaps without input, including reversals at full run speed.
        for (var i = 0; i < 5000; i++)
        {
            var hasInput = random.NextDouble() > 0.3;
            var moveX = hasInput ? (random.NextDouble() * 2) - 1 : 0;
            var moveY = hasInput ? (random.NextDouble() * 2) - 1 : 0;
            var yaw = random.NextDouble() * 2 * Math.PI;
            var step = motion.Step(moveX, moveY, yaw, random.NextDouble() > 0.5, onFloor: true, vx, vz, facing, Dt);

            var change = Math.Sqrt(Math.Pow(step.VelocityX - vx, 2) + Math.Pow(step.VelocityZ - vz, 2));
            var limit = (moveX == 0 && moveY == 0 ? motion.Deceleration : motion.Acceleration) * Dt;
            Assert.True(change <= limit + 1e-12, $"step {i}: change {change} exceeds {limit}");
            if (change > 0)
            {
                changes++;
            }

            (vx, vz, facing) = (step.VelocityX, step.VelocityZ, step.FacingYaw);
        }

        Assert.True(changes > 1000);
    }

    [Fact]
    public void Step_FromRest_UsesTheFullAccelerationLimit()
    {
        var motion = new OnFootMotion();

        var step = motion.Step(0, Forward, 0, run: true, onFloor: true, 0, 0, 0, Dt);

        Assert.Equal(motion.Acceleration * Dt, step.Speed, 12);
    }

    [Fact]
    public void Step_NoInput_DeceleratesToZeroAndKeepsTheFacing()
    {
        var motion = new OnFootMotion();
        var facing = 1.234;
        var vx = 3.0;
        var vz = -4.0; // 5 m/s
        var previousSpeed = 5.0;

        // 5 m/s at 25 m/s² stops in 0.2 s = 12 steps.
        var steps = (int)Math.Ceiling(5.0 / (motion.Deceleration * Dt));
        for (var i = 0; i < steps; i++)
        {
            var step = motion.Step(0, 0, 0.5, run: false, onFloor: true, vx, vz, facing, Dt);

            Assert.Equal(facing, step.FacingYaw);
            Assert.True(step.Speed < previousSpeed);
            Assert.InRange(previousSpeed - step.Speed, 0, (motion.Deceleration * Dt) + 1e-12);
            (vx, vz, previousSpeed) = (step.VelocityX, step.VelocityZ, step.Speed);
        }

        Assert.Equal(0, vx);
        Assert.Equal(0, vz);

        // At rest it stays at rest, facing unchanged.
        var rest = motion.Step(0, 0, 2.0, run: true, onFloor: true, 0, 0, facing, Dt);
        Assert.Equal(new OnFootStep(0, 0, facing), rest);
    }

    [Theory]
    [InlineData(0.3)]
    [InlineData(0.0)]
    [InlineData(1.0)]
    public void Step_InAir_AirControlScalesTheChange(double airControl)
    {
        var motion = new OnFootMotion { AirControl = airControl };

        // With input: accelerating from rest toward RunSpeed (the limit binds on the floor and in the air).
        AssertAirChangeScaled(motion, airControl, moveY: Forward, vx: 0, vz: 0);

        // With input: turning a forward run into a sideways one.
        AssertAirChangeScaled(motion, airControl, moveY: 0, vx: 0, vz: -5, moveX: 1);

        // Without input: decelerating from RunSpeed.
        AssertAirChangeScaled(motion, airControl, moveY: 0, vx: 0, vz: -5);
    }

    [Fact]
    public void Step_FacingTurnsAtTurnSpeed()
    {
        var motion = new OnFootMotion();

        // Facing north (yaw 0), input to the right with the camera at yaw 0: target facing −π/2 (east).
        var step = motion.Step(1, 0, 0, run: false, onFloor: true, 0, 0, 0, Dt);

        Assert.Equal(-motion.TurnSpeed * Math.PI / 180 * Dt, step.FacingYaw, 12);
    }

    [Fact]
    public void JumpVelocity_IsSqrtOfTwoGH()
    {
        Assert.Equal(Math.Sqrt(2 * 9.8 * 1.3), OnFootMotion.JumpVelocity(1.3, 9.8), 12);
        Assert.Equal(Math.Sqrt(2 * 9.80665 * 0.9), OnFootMotion.JumpVelocity(0.9, 9.80665), 12);
        Assert.Equal(0, OnFootMotion.JumpVelocity(0, 9.8));

        // The spec's figure: about 5.05 m/s at 9.8 m/s² for 1.3 m.
        Assert.Equal(5.05, OnFootMotion.JumpVelocity(1.3, 9.8), 2);
    }

    private static void AssertAirChangeScaled(
        OnFootMotion motion,
        double airControl,
        double moveY,
        double vx,
        double vz,
        double moveX = 0)
    {
        var floor = motion.Step(moveX, moveY, 0, run: true, onFloor: true, vx, vz, 0, Dt);
        var air = motion.Step(moveX, moveY, 0, run: true, onFloor: false, vx, vz, 0, Dt);

        var floorChange = Math.Sqrt(Math.Pow(floor.VelocityX - vx, 2) + Math.Pow(floor.VelocityZ - vz, 2));
        var airChange = Math.Sqrt(Math.Pow(air.VelocityX - vx, 2) + Math.Pow(air.VelocityZ - vz, 2));

        Assert.True(floorChange > 0);
        Assert.Equal(airControl * floorChange, airChange, 12);
    }

    private static OnFootStep Run(
        OnFootMotion motion,
        double moveX,
        double moveY,
        double cameraYaw,
        bool run,
        bool onFloor,
        int steps)
    {
        var step = new OnFootStep(0, 0, 0);
        for (var i = 0; i < steps; i++)
        {
            step = motion.Step(moveX, moveY, cameraYaw, run, onFloor, step.VelocityX, step.VelocityZ, step.FacingYaw, Dt);
        }

        return step;
    }
}
