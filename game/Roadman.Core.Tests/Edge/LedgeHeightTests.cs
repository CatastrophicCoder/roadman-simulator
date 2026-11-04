using Roadman.Core.Edge;
using Roadman.Core.Maps;

namespace Roadman.Core.Tests.Edge;

/// <summary><see cref="EdgeGeometry.LedgeHeightAt"/>: the ledge's inner-edge height along the ring.</summary>
public class LedgeHeightTests
{
    private const int Precision = 9;

    /// <summary>
    /// The 100 m square (ring length 400): fence:0 s 10..100 (y 100 → 110), roadblock s 100..120 (y 110 → 104),
    /// fence:1 s 120..380 (y 104 → 90), then a gap s 380..390, and fence:2 s 390..410 over the ring's start
    /// (y 92 → 100).
    /// </summary>
    private static EdgeGeometry Geometry() =>
        new(EdgeTestData.MakeBoundary(
            EdgeTestData.Square,
            roadblocks: [Roadblock("roadblock:a", 100, 120, new(0, 110, 100), new(20, 104, 100))],
            fences:
            [
                Fence("fence:0", 10, 100, new(0, 100, 10), new(0, 110, 100)),
                Fence("fence:1", 120, 380, new(20, 104, 100), new(100, 90, 20)),
                Fence("fence:2", 390, 410, new(90, 92, 0), new(0, 100, 10)),
            ]));

    [Theory]
    [InlineData(55, 105)]
    [InlineData(110, 107)]
    [InlineData(120, 104)]
    [InlineData(250, 97)]
    [InlineData(800 + 55, 105)]
    [InlineData(-345, 105)]
    public void LinearBetweenTheEndHeightsOfTheCoveringItem(double s, double expected) =>
        Assert.Equal(expected, Geometry().LedgeHeightAt(s), Precision);

    [Theory]
    [InlineData(5, 98)]
    [InlineData(0, 96)]
    [InlineData(395, 94)]
    public void ItemOverTheRingStart_CoversSmallS(double s, double expected) =>
        // fence:2 runs s 390..410, so s 0..10 lie on it at s + 400.
        Assert.Equal(expected, Geometry().LedgeHeightAt(s), Precision);

    [Fact]
    public void InAGap_LinearFromThePreviousEndToTheNextStart()
    {
        // Gap s 380..390: fence:1 ends at y 90, fence:2 starts at y 92.
        Assert.Equal(91, Geometry().LedgeHeightAt(385), Precision);
    }

    [Fact]
    public void NoItems_Throws() =>
        Assert.Throws<InvalidOperationException>(() =>
            new EdgeGeometry(EdgeTestData.MakeBoundary(EdgeTestData.Square)).LedgeHeightAt(0));

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void NonFiniteS_Throws(double s) =>
        Assert.Throws<ArgumentException>(() => Geometry().LedgeHeightAt(s));

    private static Fence Fence(string id, double sStart, double sEnd, LocalPoint start, LocalPoint end) =>
        new(id, FenceType.Land, start, end, sStart, sEnd, new LocalPoint(1, 0, 0), null);

    private static Roadblock Roadblock(string id, double sStart, double sEnd, LocalPoint start, LocalPoint end) =>
        new(
            id,
            [new RoadblockNode("n", "e", RoadblockRole.SpawnDespawn)],
            "residential",
            true,
            true,
            4,
            RoadValueSource.Default,
            start,
            end,
            new LocalPoint((start.X + end.X) / 2, (start.Y + end.Y) / 2, (start.Z + end.Z) / 2),
            0,
            new LocalPoint(0, 0, 1),
            sStart,
            sEnd,
            false);
}
