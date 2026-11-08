using Roadman.Core.Minimap;

namespace Roadman.Core.Tests.Minimap;

/// <summary>
/// <see cref="MinimapProjection.HeadingFromCameraRight"/>: the minimap's heading is the player camera's yaw, read from
/// its right vector so the pitch doesn't matter (<c>docs/specs/minimap.md</c>).
/// </summary>
public class MinimapHeadingTests
{
    private const double Precision = 1e-12;

    [Theory]
    [InlineData(0)]
    [InlineData(30)]
    [InlineData(90)]
    [InlineData(-90)]
    [InlineData(179)]
    [InlineData(-179)]
    public void HeadingFromCameraRight_LevelCamera_IsItsYaw(double yawDegrees)
    {
        var yaw = yawDegrees * Math.PI / 180;
        var (x, _, z) = CameraRight(yaw, 0);

        Assert.Equal(yaw, MinimapProjection.HeadingFromCameraRight(x, z), Precision);
    }

    [Theory]
    [InlineData(-89)]
    [InlineData(-45)]
    [InlineData(20)]
    [InlineData(89)]
    public void HeadingFromCameraRight_PitchedCamera_IsStillItsYaw(double pitchDegrees)
    {
        var yaw = 1.1;
        var (x, _, z) = CameraRight(yaw, pitchDegrees * Math.PI / 180);

        Assert.Equal(yaw, MinimapProjection.HeadingFromCameraRight(x, z), Precision);
    }

    [Fact]
    public void HeadingFromCameraRight_Godot180_IsPi()
    {
        // Yaw π: the right vector is (−1, 0, −0); either sign of zero must give a half turn, not 0.
        Assert.Equal(Math.PI, Math.Abs(MinimapProjection.HeadingFromCameraRight(-1, 0)), Precision);
    }

    /// <summary>
    /// The right vector of a camera looking along yaw <paramref name="yaw"/> and pitch <paramref name="pitch"/>, as
    /// <c>LookAt</c> builds it: forward × up, with forward (−sin y cos p, sin p, −cos y cos p) (Godot cameras look
    /// along −z) and up (0, 1, 0). Not normalised: the length is cos p.
    /// </summary>
    private static (double X, double Y, double Z) CameraRight(double yaw, double pitch)
    {
        var (sy, cy) = Math.SinCos(yaw);
        var (sp, cp) = Math.SinCos(pitch);
        var forward = (X: -sy * cp, Y: sp, Z: -cy * cp);

        // forward × (0, 1, 0) = (−forward.z, 0, forward.x).
        return (-forward.Z, 0, forward.X);
    }
}
