using Roadman.Core.Maps;
using Roadman.Core.Minimap;

namespace Roadman.Core.Tests.Minimap;

/// <summary><see cref="MinimapProjection"/>: <c>docs/specs/minimap.md</c>, acceptance criterion 5.</summary>
public class MinimapProjectionTests
{
    // Godot yaws (Node3D.Rotation.Y): 0 faces −z (north), −90° faces +x (east), 180° faces +z (south).
    private const double North = 0;
    private static readonly double East = -Math.PI / 2;
    private static readonly double South = Math.PI;
    private static readonly double West = Math.PI / 2;

    private const double Precision = 1e-9;

    [Theory]
    [InlineData(0, 0)]
    [InlineData(123.5, -456.25)]
    public void Ac5_FacingNorth_NorthIsUpAndEastIsRight(double cx, double cz)
    {
        var projection = new MinimapProjection(cx, cz, North, 150, 66);
        var s = 66.0 / 150;

        AssertPanel(new PanelPoint(0, -10 * s), projection.ToPanel(cx, cz - 10)); // 10 m north
        AssertPanel(new PanelPoint(10 * s, 0), projection.ToPanel(cx + 10, cz)); // 10 m east
        Assert.Equal(s, projection.Scale);
    }

    [Fact]
    public void Ac5_FacingEast_EastIsUp()
    {
        var projection = new MinimapProjection(5, 7, East, 150, 66);
        var s = projection.Scale;

        AssertPanel(new PanelPoint(0, -10 * s), projection.ToPanel(15, 7)); // 10 m east
        AssertPanel(new PanelPoint(-10 * s, 0), projection.ToPanel(5, -3)); // 10 m north: left
        AssertPanel(new PanelPoint(10 * s, 0), projection.ToPanel(5, 17)); // 10 m south: right
    }

    [Fact]
    public void Ac5_FacingSouth_NorthIsDown()
    {
        var projection = new MinimapProjection(0, 0, South, 150, 66);
        var s = projection.Scale;

        AssertPanel(new PanelPoint(0, 10 * s), projection.ToPanel(0, -10)); // 10 m north
        AssertPanel(new PanelPoint(-10 * s, 0), projection.ToPanel(10, 0)); // 10 m east: left
    }

    [Fact]
    public void Ac5_FacingWest_WestIsUp()
    {
        var projection = new MinimapProjection(0, 0, West, 150, 66);
        var s = projection.Scale;

        AssertPanel(new PanelPoint(0, -10 * s), projection.ToPanel(-10, 0));
        AssertPanel(new PanelPoint(10 * s, 0), projection.ToPanel(0, -10)); // north: right
    }

    [Fact]
    public void Ac5_TheRangeIsTheRadius()
    {
        var projection = new MinimapProjection(0, 0, 0.7, 150, 110);

        Assert.Equal(110, projection.ToPanel(150 * Math.Sin(0.3), 150 * Math.Cos(0.3)).Length, Precision);
    }

    [Fact]
    public void Ac5_ToWorldOfToPanel_RoundTripsForSeededPoints()
    {
        var random = new Random(20261108);
        for (var i = 0; i < 2000; i++)
        {
            var cx = -1200 + (random.NextDouble() * 2400);
            var cz = -1200 + (random.NextDouble() * 2400);
            var yaw = -10 + (random.NextDouble() * 20); // beyond ±2π too
            var range = 10 + (random.NextDouble() * 500);
            var radius = 20 + (random.NextDouble() * 200);
            var projection = new MinimapProjection(cx, cz, yaw, range, radius);
            var x = cx - 300 + (random.NextDouble() * 600);
            var z = cz - 300 + (random.NextDouble() * 600);

            var back = projection.ToWorld(projection.ToPanel(x, z));

            Assert.Equal(x, back.X, Precision);
            Assert.Equal(z, back.Z, Precision);
        }
    }

    [Fact]
    public void Ac5_NorthBearing_MatchesThePanelDirectionOfAPointDueNorth()
    {
        var random = new Random(5);
        for (var i = 0; i < 500; i++)
        {
            var yaw = -7 + (random.NextDouble() * 14);
            var projection = new MinimapProjection(10, 20, yaw, 150, 66);
            var north = projection.ToPanel(10, 20 - 50);

            AssertAngle(MinimapProjection.AngleOf(north), projection.NorthBearing);
            AssertPanel(MinimapProjection.DirectionOf(projection.NorthBearing) * north.Length, north);
        }
    }

    [Fact]
    public void Ac5_NorthBearing_FacingEastIsLeft()
    {
        // Criterion 13: with the camera looking east, the north indicator is on the left of the rim.
        AssertAngle(-Math.PI / 2, new MinimapProjection(0, 0, East, 150, 66).NorthBearing);
        AssertAngle(0, new MinimapProjection(0, 0, North, 150, 66).NorthBearing);
        AssertAngle(Math.PI, new MinimapProjection(0, 0, South, 150, 66).NorthBearing);
    }

    [Fact]
    public void Ac5_ArrowAngle_IsUpWhenFacingTheHeading()
    {
        foreach (var yaw in new[] { North, East, South, West, 0.3, -2.9 })
        {
            AssertAngle(0, new MinimapProjection(0, 0, yaw, 150, 66).ArrowAngle(yaw));
        }
    }

    [Fact]
    public void Ac5_ArrowAngle_IsRightWhenFacing90DegreesClockwiseOfTheHeading()
    {
        // Clockwise seen from above is a negative yaw step: camera north (0), player east (−90°).
        AssertAngle(Math.PI / 2, new MinimapProjection(0, 0, North, 150, 66).ArrowAngle(East));

        // Camera east, player south (180°, which is −90° from east).
        AssertAngle(Math.PI / 2, new MinimapProjection(0, 0, East, 150, 66).ArrowAngle(South));

        // Facing the other way: left.
        AssertAngle(-Math.PI / 2, new MinimapProjection(0, 0, North, 150, 66).ArrowAngle(West));
    }

    [Fact]
    public void Ac5_ArrowAngle_PointsAtThePanelPositionOfAPointAhead()
    {
        var random = new Random(77);
        for (var i = 0; i < 500; i++)
        {
            var heading = -4 + (random.NextDouble() * 8);
            var facing = -4 + (random.NextDouble() * 8);
            var projection = new MinimapProjection(0, 0, heading, 150, 66);

            // Forward of a Godot yaw: (−sin yaw, −cos yaw) in x/z.
            var ahead = projection.ToPanel(-Math.Sin(facing) * 20, -Math.Cos(facing) * 20);

            AssertAngle(MinimapProjection.AngleOf(ahead), projection.ArrowAngle(facing));
        }
    }

    [Fact]
    public void ImageTransform_PlacesImagePixelsOnTheirGroundPoints()
    {
        var image = new MinimapImage(1, "somero", "test", "minimap.png", 0.5, 600, 400, new MinimapBounds(-150, 150, -100, 100));
        var projection = new MinimapProjection(12, -34, 0.8, 150, 66);
        var transform = projection.ImageTransform(image);

        foreach (var (px, py) in new[] { (0.0, 0.0), (600.0, 0.0), (17.5, 333.25), (600.0, 400.0) })
        {
            // Godot's draw transform: rotate, scale, then translate.
            var (sin, cos) = Math.SinCos(transform.Rotation);
            var drawn = new PanelPoint(
                transform.Origin.X + (transform.Scale * ((px * cos) - (py * sin))),
                transform.Origin.Y + (transform.Scale * ((px * sin) + (py * cos))));
            var (x, z) = image.WorldOf(px, py);

            AssertPanel(projection.ToPanel(x, z), drawn);
        }
    }

    [Theory]
    [InlineData(0, 150)]
    [InlineData(-1, 150)]
    [InlineData(66, 0)]
    [InlineData(66, double.NaN)]
    public void Constructor_RejectsNonPositiveRadiusOrRange(double radius, double range)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MinimapProjection(0, 0, 0, range, radius));
    }

    [Fact]
    public void Constructor_RejectsNonFiniteCentreOrYaw()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MinimapProjection(double.NaN, 0, 0, 150, 66));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MinimapProjection(0, double.PositiveInfinity, 0, 150, 66));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MinimapProjection(0, 0, double.NaN, 150, 66));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(Math.PI, Math.PI)]
    [InlineData(-Math.PI, Math.PI)]
    [InlineData(3 * Math.PI, Math.PI)]
    [InlineData(1.5 * Math.PI, -0.5 * Math.PI)]
    [InlineData(-0.25, -0.25)]
    public void NormaliseAngle_IsInMinusPiToPi(double angle, double expected)
    {
        Assert.Equal(expected, MinimapProjection.NormaliseAngle(angle), Precision);
    }

    private static void AssertPanel(PanelPoint expected, PanelPoint actual)
    {
        Assert.Equal(expected.X, actual.X, Precision);
        Assert.Equal(expected.Y, actual.Y, Precision);
    }

    private static void AssertAngle(double expected, double actual) =>
        Assert.Equal(0, MinimapProjection.NormaliseAngle(expected - actual), Precision);
}
