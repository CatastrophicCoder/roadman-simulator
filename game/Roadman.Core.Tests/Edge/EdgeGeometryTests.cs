using Roadman.Core.Edge;
using Roadman.Core.Maps;
using Roadman.Core.Tests.Maps;
using static Roadman.Core.Tests.Edge.EdgeTestData;

namespace Roadman.Core.Tests.Edge;

/// <summary><see cref="EdgeGeometry"/>: <c>docs/specs/world-edge-game.md</c>, acceptance criterion 1.</summary>
public class EdgeGeometryTests
{
    private const double Tolerance = 1e-9;

    private const int Samples = 10_000;

    // Sampled band points keep this far from the band's inner (1 m) and outer (21 m) limits, and sampled inside
    // points this far from the ring: boundary.json rounds every coordinate to 1 µm, so a point within a few µm of a
    // limit may fall either side of a prism edge. 1 mm is a thousand times that rounding.
    private const double RoundingMargin = 1e-3;

    // Near convex ring corners the prisms' edges lie up to 5 cm outside the 1 m circle (ADR 0015, amendment of
    // 2026-11-04: ARC_EXCESS_M), so the band the trigger guarantees starts at 1.05 m there; sampled band points start
    // there everywhere (spec criterion 1, amended).
    private const double CornerSlack = 0.05;

    [Fact]
    public void Constructor_NullBoundary_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new EdgeGeometry(null!));
    }

    [Fact]
    public void Boundary_IsTheOneGiven()
    {
        var boundary = MakeBoundary(Square);
        Assert.Same(boundary, new EdgeGeometry(boundary).Boundary);
    }

    [Theory]
    [InlineData(50, 50, true)]
    [InlineData(-5, 50, false)]
    [InlineData(105, 50, false)]
    [InlineData(0, 50, true)] // on the ring counts as inside
    [InlineData(0, 0, true)]
    public void IsInsideRing_Square(double x, double z, bool expected)
    {
        Assert.Equal(expected, new EdgeGeometry(MakeBoundary(Square)).IsInsideRing(x, z));
    }

    [Theory]
    [InlineData(25, 25, true)]
    [InlineData(75, 25, false)] // the cut-out quarter
    [InlineData(75, 75, true)]
    public void IsInsideRing_LShape(double x, double z, bool expected)
    {
        Assert.Equal(expected, new EdgeGeometry(MakeBoundary(LShape)).IsInsideRing(x, z));
    }

    [Fact]
    public void NearestRingPoint_OutsidePoint_GivesPointSAndDistance()
    {
        var nearest = new EdgeGeometry(MakeBoundary(Square)).NearestRingPoint(-5, 30);

        AssertPoint(new PlanarPoint(0, 30), nearest.Point);
        Assert.Equal(30, nearest.S, Tolerance);
        Assert.Equal(5, nearest.Distance, Tolerance);
    }

    [Fact]
    public void NearestRingPoint_RingPointZero_HasSZeroNotLength() // criterion 1, ties to the lower s
    {
        var nearest = new EdgeGeometry(MakeBoundary(Square)).NearestRingPoint(0, 0);

        Assert.Equal(0, nearest.S);
        Assert.Equal(0, nearest.Distance, Tolerance);
    }

    [Fact]
    public void NearestRingPoint_SInLastSegment_IsBelowLength()
    {
        var nearest = new EdgeGeometry(MakeBoundary(Square)).NearestRingPoint(10, -3);

        // Segment 3 runs (100, 0) → (0, 0), s 300..400: x 10 is 90 m along it.
        Assert.Equal(390, nearest.S, Tolerance);
        Assert.InRange(nearest.S, 0, 400 - Tolerance);
    }

    [Fact]
    public void NearestRingPoint_TieBetweenTwoSegments_GoesToLowerS() // criterion 1
    {
        // (60, 40) in the L's cut-out is 10 m from segment 3 at (60, 50), s 290, and 10 m from segment 4 at
        // (50, 40), s 310.
        var nearest = new EdgeGeometry(MakeBoundary(LShape)).NearestRingPoint(60, 40);

        AssertPoint(new PlanarPoint(60, 50), nearest.Point);
        Assert.Equal(290, nearest.S, Tolerance);
        Assert.Equal(10, nearest.Distance, Tolerance);
    }

    [Fact]
    public void NearestRingPoint_CentreOfSquare_GoesToFirstSegment() // criterion 1
    {
        var nearest = new EdgeGeometry(MakeBoundary(Square)).NearestRingPoint(50, 50);

        AssertPoint(new PlanarPoint(0, 50), nearest.Point);
        Assert.Equal(50, nearest.S, Tolerance);
    }

    [Theory]
    [InlineData(-5, 50, -1, 0)] // outside, west
    [InlineData(5, 50, -1, 0)] // inside, west: still outward
    [InlineData(0, 50, -1, 0)] // on the west segment
    [InlineData(50, 103, 0, 1)] // outside, south (z 100)
    [InlineData(50, 97, 0, 1)] // inside, south
    [InlineData(100, 30, 1, 0)] // on the east segment
    [InlineData(50, 0, 0, -1)] // on the north segment
    public void OutwardAt_Square_PointsOut(double x, double z, double ox, double oz) // criterion 1
    {
        AssertUnit(new PlanarPoint(ox, oz), new EdgeGeometry(MakeBoundary(Square)).OutwardAt(x, z));
    }

    [Theory]
    [InlineData(-5, 50, -1, 0)]
    [InlineData(5, 50, -1, 0)]
    [InlineData(0, 50, -1, 0)]
    [InlineData(100, 30, 1, 0)]
    [InlineData(50, 0, 0, -1)]
    [InlineData(50, 100, 0, 1)]
    public void OutwardAt_ReversedOrientation_StillPointsOut(double x, double z, double ox, double oz) // criterion 1
    {
        AssertUnit(new PlanarPoint(ox, oz), new EdgeGeometry(MakeBoundary(SquareReversed)).OutwardAt(x, z));
    }

    [Fact]
    public void OutwardAt_OutsideConvexCorner_PointsAwayFromCorner() // criterion 1
    {
        AssertUnit(new PlanarPoint(-0.6, -0.8), new EdgeGeometry(MakeBoundary(LShape)).OutwardAt(-3, -4));
    }

    [Fact]
    public void OutwardAt_InsideConvexCorner_PointsToNearestSegmentWithLowerS() // criterion 1
    {
        // (5, 5) is 5 m from segment 0 at (0, 5), s 5, and 5 m from segment 5 at (5, 0), s 395.
        AssertUnit(new PlanarPoint(-1, 0), new EdgeGeometry(MakeBoundary(LShape)).OutwardAt(5, 5));
    }

    [Fact]
    public void OutwardAt_OnConvexCornerRingPointZero_UsesFirstSegment() // criterion 1
    {
        // (0, 0) ends segment 5 (s 400) and starts segment 0 (s 0): s 0 wins, segment 0's normal is −x.
        AssertUnit(new PlanarPoint(-1, 0), new EdgeGeometry(MakeBoundary(LShape)).OutwardAt(0, 0));
    }

    [Fact]
    public void OutwardAt_OnConvexCorner_UsesSegmentEndingThere() // criterion 1
    {
        // (0, 100) ends segment 0 and starts segment 1, both at s 100: the first segment in ring order wins.
        AssertUnit(new PlanarPoint(-1, 0), new EdgeGeometry(MakeBoundary(LShape)).OutwardAt(0, 100));
    }

    [Fact]
    public void OutwardAt_OnConcaveCorner_UsesSegmentEndingThere() // criterion 1
    {
        // (50, 50) ends segment 3 ((100, 50) → (50, 50)) and starts segment 4: segment 3's normal is −z, into the
        // cut-out.
        AssertUnit(new PlanarPoint(0, -1), new EdgeGeometry(MakeBoundary(LShape)).OutwardAt(50, 50));
    }

    [Fact]
    public void OutwardAt_InsideConcaveCorner_PointsThroughCorner() // criterion 1
    {
        // Nearest ring point of (45, 55) is the concave corner itself; outward = −(p − corner).
        var r = 1 / Math.Sqrt(2);
        AssertUnit(new PlanarPoint(r, -r), new EdgeGeometry(MakeBoundary(LShape)).OutwardAt(45, 55));
    }

    [Fact]
    public void OutwardAt_OutsideConcaveCornerTie_GoesToLowerS() // criterion 1
    {
        // (60, 40): segment 3 (s 290) gives −z, segment 4 (s 310) would give +x.
        AssertUnit(new PlanarPoint(0, -1), new EdgeGeometry(MakeBoundary(LShape)).OutwardAt(60, 40));
    }

    [Fact]
    public void OutwardAt_OutsideConcaveCorner_PointsOut() // criterion 1
    {
        AssertUnit(new PlanarPoint(0, -1), new EdgeGeometry(MakeBoundary(LShape)).OutwardAt(70, 45));
        AssertUnit(new PlanarPoint(1, 0), new EdgeGeometry(MakeBoundary(LShape)).OutwardAt(55, 20));
    }

    [Fact]
    public void OutwardAt_RandomPointsAroundLShape_AreUnitAndPointOut() // criterion 1
    {
        var geometry = new EdgeGeometry(MakeBoundary(LShape));
        var random = new Random(20261104);
        const double step = 1e-3;

        for (var i = 0; i < 2000; i++)
        {
            var x = (random.NextDouble() * 140) - 20;
            var z = (random.NextDouble() * 140) - 20;
            var distance = DistanceToRing(LShape, x, z);
            if (distance < 0.1)
            {
                continue;
            }

            var outward = geometry.OutwardAt(x, z);
            Assert.Equal(1, Math.Sqrt((outward.X * outward.X) + (outward.Z * outward.Z)), 1e-9);

            var nx = x + (step * outward.X);
            var nz = z + (step * outward.Z);
            var after = DistanceToRing(LShape, nx, nz);
            if (Inside(LShape, x, z))
            {
                Assert.True(after < distance, $"inside ({x}, {z}): outward {outward} doesn't approach the ring");
            }
            else
            {
                Assert.False(Inside(LShape, nx, nz), $"outside ({x}, {z}): outward {outward} goes inside");
                Assert.True(after > distance, $"outside ({x}, {z}): outward {outward} doesn't leave the ring");
            }
        }
    }

    [Theory]
    [InlineData(-10, 50, 40, true)]
    [InlineData(-10, 50, VoidBottomY, true)] // bottom inclusive
    [InlineData(-10, 50, VoidTopY, true)] // top inclusive
    [InlineData(-1, 50, 40, true)] // footprint edge inclusive
    [InlineData(-21, 0, 40, true)] // footprint corner inclusive
    [InlineData(-0.5, 50, 40, false)] // between the ring and the prism
    [InlineData(-10, 101, 40, false)] // past the footprint's end
    [InlineData(50, 50, 40, false)] // inside the ring
    public void IsInVoid_SyntheticPrism(double x, double z, double y, bool expected)
    {
        // One west band prism, x −21..−1, z 0..100, given in both windings.
        VoidPrism[] clockwise = [new VoidPrism([new(-21, 0), new(-21, 100), new(-1, 100), new(-1, 0)])];
        VoidPrism[] counter = [new VoidPrism([new(-21, 0), new(-1, 0), new(-1, 100), new(-21, 100)])];

        Assert.Equal(expected, new EdgeGeometry(MakeBoundary(Square, prisms: clockwise)).IsInVoid(new LocalPoint(x, y, z)));
        Assert.Equal(expected, new EdgeGeometry(MakeBoundary(Square, prisms: counter)).IsInVoid(new LocalPoint(x, y, z)));
    }

    [Fact]
    public void IsInVoid_JustOutsidePrismHeights_IsFalse()
    {
        VoidPrism[] prisms = [new VoidPrism([new(-21, 0), new(-21, 100), new(-1, 100), new(-1, 0)])];
        var geometry = new EdgeGeometry(MakeBoundary(Square, prisms: prisms));

        Assert.False(geometry.IsInVoid(new LocalPoint(-10, Math.BitDecrement(VoidBottomY), 50)));
        Assert.False(geometry.IsInVoid(new LocalPoint(-10, Math.BitIncrement(VoidTopY), 50)));
    }

    [PackFact]
    public void IsInVoid_GeneratedPacks_TenThousandBandPointsAreInVoid() // criterion 1
    {
        // Band points are drawn uniformly from the ring's bounding box grown by 21 m and kept when the oracle says
        // they are outside the ring and 1.05 m (1 m plus CornerSlack) to 21 m from it (less RoundingMargin at both
        // limits), so corners and notches are covered by area. y is uniform in bottom_y..top_y.
        foreach (var (mapId, boundary) in RealPacks())
        {
            var ring = boundary.Ring.Points;
            var geometry = new EdgeGeometry(boundary);
            var random = new Random(20261104);
            var (minX, maxX, minZ, maxZ) = Bounds(ring, 21);
            var failures = new List<string>();
            var accepted = 0;

            for (var attempt = 0; accepted < Samples; attempt++)
            {
                Assert.True(attempt < 20_000_000, $"{mapId}: too few band samples ({accepted})");
                var x = minX + (random.NextDouble() * (maxX - minX));
                var z = minZ + (random.NextDouble() * (maxZ - minZ));
                if (Inside(ring, x, z))
                {
                    continue;
                }

                var distance = DistanceToRing(ring, x, z);
                if (distance < 1 + CornerSlack + RoundingMargin || distance > 21 - RoundingMargin)
                {
                    continue;
                }

                accepted++;
                var y = boundary.VoidTrigger.BottomY
                    + (random.NextDouble() * (boundary.VoidTrigger.TopY - boundary.VoidTrigger.BottomY));
                if (!geometry.IsInVoid(new LocalPoint(x, y, z)) && failures.Count < 10)
                {
                    failures.Add($"({x:0.###}, {y:0.###}, {z:0.###}) {distance:0.###} m out");
                }
            }

            Assert.True(failures.Count == 0, $"{mapId}: band points not in the void: {string.Join("; ", failures)}");
        }
    }

    [PackFact]
    public void IsInVoid_GeneratedPacks_TenThousandInsidePointsAreNot() // criterion 1
    {
        foreach (var (mapId, boundary) in RealPacks())
        {
            var ring = boundary.Ring.Points;
            var geometry = new EdgeGeometry(boundary);
            var random = new Random(4);
            var (minX, maxX, minZ, maxZ) = Bounds(ring, 0);
            var failures = new List<string>();
            var accepted = 0;

            for (var attempt = 0; accepted < Samples; attempt++)
            {
                Assert.True(attempt < 10_000_000, $"{mapId}: too few inside samples ({accepted})");
                var x = minX + (random.NextDouble() * (maxX - minX));
                var z = minZ + (random.NextDouble() * (maxZ - minZ));
                if (!Inside(ring, x, z) || DistanceToRing(ring, x, z) < RoundingMargin)
                {
                    continue;
                }

                accepted++;
                var y = boundary.VoidTrigger.BottomY
                    + (random.NextDouble() * (boundary.VoidTrigger.TopY - boundary.VoidTrigger.BottomY));
                Assert.True(geometry.IsInsideRing(x, z), $"{mapId}: ({x}, {z}) is inside the ring");
                if (geometry.IsInVoid(new LocalPoint(x, y, z)) && failures.Count < 10)
                {
                    failures.Add($"({x:0.###}, {y:0.###}, {z:0.###})");
                }
            }

            Assert.True(failures.Count == 0, $"{mapId}: inside points in the void: {string.Join("; ", failures)}");
        }
    }

    [PackFact]
    public void OutwardAt_GeneratedPacks_MatchesEveryItemsOutward() // criterion 1: orientation against the pack
    {
        foreach (var (mapId, boundary) in RealPacks())
        {
            var geometry = new EdgeGeometry(boundary);
            var items = boundary.Roadblocks.Select(r => (r.Id, r.Start, r.End, r.Outward))
                .Concat(boundary.Fences.Select(f => (f.Id, f.Start, f.End, f.Outward)));

            var probes = 0;
            foreach (var (id, start, end, outward) in items)
            {
                var length = Math.Sqrt(((end.X - start.X) * (end.X - start.X)) + ((end.Z - start.Z) * (end.Z - start.Z)));
                var mx = (start.X + end.X) / 2;
                var mz = (start.Z + end.Z) / 2;

                foreach (var sign in new[] { 1.0, -1.0 })
                {
                    // The probe sits `offset` from the item's midpoint along ±outward. The ring has thin spikes (on
                    // Somero, fence:2 doubles back 0.8 m from fence:1), so take the largest offset at which no other
                    // part of the ring is nearer than the item itself; skip the side if even 5 mm is too far.
                    var offset = new[] { 0.5, 0.1, 0.02, 0.005 }
                        .Where(o => o <= length / 4)
                        .FirstOrDefault(o => DistanceToRing(
                            boundary.Ring.Points, mx + (sign * o * outward.X), mz + (sign * o * outward.Z)) > o - 1e-6);
                    if (offset == 0)
                    {
                        continue;
                    }

                    probes++;
                    var tolerance = Math.Max(1e-4, 4e-6 / offset); // boundary.json rounds to 1 µm
                    var actual = geometry.OutwardAt(mx + (sign * offset * outward.X), mz + (sign * offset * outward.Z));
                    Assert.True(
                        Math.Abs(actual.X - outward.X) < tolerance && Math.Abs(actual.Z - outward.Z) < tolerance,
                        $"{mapId} {id} ({(sign > 0 ? "outside" : "inside")}, {offset} m): OutwardAt {actual}, item outward {outward}");
                }
            }

            // Nearly every item gets both probes; a few sides of very short items in spikes may be skipped.
            Assert.True(probes >= 2 * (boundary.Roadblocks.Count + boundary.Fences.Count) * 0.95, $"{mapId}: only {probes} probes");
        }
    }

    [Fact]
    public void IsBelowSafetyNet_ExactlyAtNet_IsFalseAndJustBelowIsTrue() // criterion 1
    {
        var geometry = new EdgeGeometry(MakeBoundary(Square));
        var net = CliffBottomY - 20.0;

        Assert.False(geometry.IsBelowSafetyNet(net, 20.0));
        Assert.True(geometry.IsBelowSafetyNet(Math.BitDecrement(net), 20.0));
        Assert.False(geometry.IsBelowSafetyNet(Math.BitIncrement(net), 20.0));
    }

    [PackFact]
    public void IsBelowSafetyNet_GeneratedPacks_ExactlyAtNet_IsFalseAndJustBelowIsTrue() // criterion 1
    {
        foreach (var (_, boundary) in RealPacks())
        {
            var geometry = new EdgeGeometry(boundary);
            var net = boundary.Cliff.BottomY - 20.0;

            Assert.False(geometry.IsBelowSafetyNet(net, 20.0));
            Assert.True(geometry.IsBelowSafetyNet(Math.BitDecrement(net), 20.0));
        }
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void IsBelowSafetyNet_NonFiniteMargin_Throws(double margin)
    {
        var geometry = new EdgeGeometry(MakeBoundary(Square));
        Assert.ThrowsAny<ArgumentException>(() => geometry.IsBelowSafetyNet(0, margin));
    }

    private static (double MinX, double MaxX, double MinZ, double MaxZ) Bounds(IReadOnlyList<PlanarPoint> ring, double grow) =>
        (ring.Min(p => p.X) - grow, ring.Max(p => p.X) + grow, ring.Min(p => p.Z) - grow, ring.Max(p => p.Z) + grow);

    private static void AssertPoint(PlanarPoint expected, PlanarPoint actual)
    {
        Assert.Equal(expected.X, actual.X, Tolerance);
        Assert.Equal(expected.Z, actual.Z, Tolerance);
    }

    private static void AssertUnit(PlanarPoint expected, PlanarPoint actual)
    {
        Assert.True(
            Math.Abs(expected.X - actual.X) < 1e-9 && Math.Abs(expected.Z - actual.Z) < 1e-9,
            $"expected {expected}, got {actual}");
    }
}
