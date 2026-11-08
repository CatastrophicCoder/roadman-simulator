using Roadman.Core.Minimap;

namespace Roadman.Core.Tests.Minimap;

/// <summary>
/// <see cref="MinimapLabels.NeedsPlacement"/>: the minimap places its labels again only after the centre moves or the
/// heading turns by a threshold (<c>docs/specs/minimap.md</c>, "Labels").
/// </summary>
public class MinimapLabelRefreshTests
{
    private const double Distance = 10;
    private const double Degrees = 12;

    [Fact]
    public void NoPlacementYet_Places()
    {
        Assert.True(MinimapLabels.NeedsPlacement(null, At(0, 0, 0), Distance, Degrees));
    }

    [Fact]
    public void SameFrame_DoesNotPlace()
    {
        Assert.False(MinimapLabels.NeedsPlacement(At(5, 5, 0.3), At(5, 5, 0.3), Distance, Degrees));
    }

    [Fact]
    public void CentreMove_PlacesFromTheThreshold()
    {
        var placed = At(100, 200, 0);

        Assert.False(MinimapLabels.NeedsPlacement(placed, At(106, 207.9, 0), Distance, Degrees));
        Assert.False(MinimapLabels.NeedsPlacement(placed, At(110, 200, 0), Distance, Degrees)); // exactly the threshold
        Assert.True(MinimapLabels.NeedsPlacement(placed, At(110.01, 200, 0), Distance, Degrees));
        Assert.True(MinimapLabels.NeedsPlacement(placed, At(100, 189, 0), Distance, Degrees));
    }

    [Fact]
    public void HeadingTurn_PlacesFromTheThreshold_EitherWay()
    {
        var placed = At(0, 0, Radians(40));

        Assert.False(MinimapLabels.NeedsPlacement(placed, At(0, 0, Radians(51.9)), Distance, Degrees));
        Assert.False(MinimapLabels.NeedsPlacement(placed, At(0, 0, Radians(28.1)), Distance, Degrees));
        Assert.True(MinimapLabels.NeedsPlacement(placed, At(0, 0, Radians(52.1)), Distance, Degrees));
        Assert.True(MinimapLabels.NeedsPlacement(placed, At(0, 0, Radians(27.9)), Distance, Degrees));
    }

    [Fact]
    public void HeadingTurn_AcrossHalfTurn_UsesTheShortWay()
    {
        // 175° to −175° is a 10° turn, not 350°.
        Assert.False(MinimapLabels.NeedsPlacement(At(0, 0, Radians(175)), At(0, 0, Radians(-175)), Distance, Degrees));
        Assert.True(MinimapLabels.NeedsPlacement(At(0, 0, Radians(175)), At(0, 0, Radians(-172)), Distance, Degrees));

        // A full turn more is no turn.
        Assert.False(MinimapLabels.NeedsPlacement(At(0, 0, Radians(10)), At(0, 0, Radians(370)), Distance, Degrees));
    }

    [Fact]
    public void ZeroThresholds_PlaceOnAnyChange()
    {
        Assert.False(MinimapLabels.NeedsPlacement(At(1, 1, 0), At(1, 1, 0), 0, 0));
        Assert.True(MinimapLabels.NeedsPlacement(At(1, 1, 0), At(1.001, 1, 0), 0, 0));
        Assert.True(MinimapLabels.NeedsPlacement(At(1, 1, 0), At(1, 1, 0.001), 0, 0));
    }

    private static MinimapProjection At(double x, double z, double heading) => new(x, z, heading, 150, 66);

    private static double Radians(double degrees) => degrees * Math.PI / 180;
}
