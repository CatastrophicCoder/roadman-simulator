using Roadman.Core.Edge;
using Roadman.Core.Maps;

namespace Roadman.Core.Tests.Edge;

/// <summary><see cref="VoidPull"/>: <c>docs/specs/world-edge-game.md</c>, acceptance criterion 4.</summary>
public class VoidPullTests
{
    private const double Tolerance = 1e-12;

    [Fact]
    public void Defaults_AreTheSpecsStartingValues()
    {
        var pull = new VoidPull();

        Assert.Equal(4.0, pull.OutwardAcceleration);
        Assert.Equal(9.8, pull.DownAcceleration);
    }

    [Fact]
    public void Acceleration_Defaults_IsOutwardTimesFourPlusDownTimesGravity() // criterion 4
    {
        AssertPoint(new LocalPoint(-4, -9.8, 0), new VoidPull().Acceleration(new LocalPoint(-1, 0, 0)));
    }

    [Fact]
    public void Acceleration_DiagonalOutward_ScalesBothComponents() // criterion 4
    {
        var pull = new VoidPull { OutwardAcceleration = 2.5, DownAcceleration = 5.0 };

        AssertPoint(new LocalPoint(1.5, -5.0, -2.0), pull.Acceleration(new LocalPoint(0.6, 0, -0.8)));
    }

    [Fact]
    public void Acceleration_UnitWithinTolerance_IsAccepted() // criterion 4
    {
        var pull = new VoidPull();

        AssertPoint(new LocalPoint(4 * (1 + 2e-7), -9.8, 0), pull.Acceleration(new LocalPoint(1 + 2e-7, 0, 0)));
    }

    [Theory]
    [InlineData(2, 0, 0)] // too long
    [InlineData(0.5, 0, 0)] // too short
    [InlineData(1.00001, 0, 0)]
    [InlineData(0, 0, 0)]
    [InlineData(0, 1, 0)] // unit but vertical
    [InlineData(0, -1, 0)]
    [InlineData(0.99999999999950, 1e-6, 0)] // unit within tolerance but not horizontal
    [InlineData(double.NaN, 0, 0)]
    [InlineData(1, double.NaN, 0)]
    [InlineData(0, 0, double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity, 0, 0)]
    public void Acceleration_NotAUnitHorizontalVector_Throws(double x, double y, double z) // criterion 4
    {
        Assert.ThrowsAny<ArgumentException>(() => new VoidPull().Acceleration(new LocalPoint(x, y, z)));
    }

    private static void AssertPoint(LocalPoint expected, LocalPoint actual)
    {
        Assert.Equal(expected.X, actual.X, Tolerance);
        Assert.Equal(expected.Y, actual.Y, Tolerance);
        Assert.Equal(expected.Z, actual.Z, Tolerance);
    }
}
