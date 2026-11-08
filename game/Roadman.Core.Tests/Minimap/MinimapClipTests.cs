using Roadman.Core.Minimap;

namespace Roadman.Core.Tests.Minimap;

/// <summary><see cref="MinimapClip"/>: <c>docs/specs/minimap.md</c>, acceptance criterion 6.</summary>
public class MinimapClipTests
{
    private const double Radius = 66;
    private const double RimPrecision = 1e-6;

    [Fact]
    public void Ac6_PolylineFullyInside_IsUnchanged()
    {
        PanelPoint[] line = [new(-10, -10), new(20, 5), new(30, 40), new(-50, 0)];

        var piece = Assert.Single(MinimapClip.Clip(line, Radius));

        Assert.Equal(line, piece);
    }

    [Fact]
    public void Ac6_PolylineFullyOutside_IsDropped()
    {
        PanelPoint[] line = [new(100, 0), new(100, 100), new(-100, 100), new(-100, 80)];

        Assert.Empty(MinimapClip.Clip(line, Radius));
    }

    [Fact]
    public void Ac6_SegmentPassingByOutside_IsDropped()
    {
        // Both ends outside, the line misses the circle.
        Assert.Empty(MinimapClip.Clip([new PanelPoint(-100, 70), new PanelPoint(100, 70)], Radius));

        // Both ends outside on the same side, the infinite line would cross the circle.
        Assert.Empty(MinimapClip.Clip([new PanelPoint(100, 0), new PanelPoint(200, 0)], Radius));
    }

    [Fact]
    public void Ac6_SegmentCrossingTheCircle_EndsOnTheRim()
    {
        var piece = Assert.Single(MinimapClip.Clip([new PanelPoint(-100, 10), new PanelPoint(100, 10)], Radius));

        Assert.Equal(2, piece.Count);
        AssertOnRim(piece[0]);
        AssertOnRim(piece[1]);
        Assert.True(piece[0].X < 0 && piece[1].X > 0, "the piece keeps the segment's direction");
        Assert.Equal(10, piece[0].Y, RimPrecision);
        Assert.Equal(10, piece[1].Y, RimPrecision);
    }

    [Fact]
    public void Ac6_SegmentLeavingTheCircle_KeepsItsInsideEndAndEndsOnTheRim()
    {
        var piece = Assert.Single(MinimapClip.Clip([new PanelPoint(3, 4), new PanelPoint(200, 150)], Radius));

        Assert.Equal(new PanelPoint(3, 4), piece[0]);
        AssertOnRim(piece[1]);
    }

    [Fact]
    public void Ac6_PolylineLeavingAndReentering_GivesTwoPieces()
    {
        PanelPoint[] line = [new(-30, 0), new(0, -120), new(30, 0), new(40, 10)];

        var pieces = MinimapClip.Clip(line, Radius);

        Assert.Equal(2, pieces.Count);
        Assert.Equal(new PanelPoint(-30, 0), pieces[0][0]);
        AssertOnRim(pieces[0][^1]);
        AssertOnRim(pieces[1][0]);
        Assert.Equal([new PanelPoint(30, 0), new PanelPoint(40, 10)], pieces[1].Skip(1));
    }

    [Fact]
    public void Ac6_RandomSegments_CrossingOnesEndOnTheRimWithin1e6()
    {
        var random = new Random(66);
        for (var i = 0; i < 5000; i++)
        {
            var a = new PanelPoint(-200 + (random.NextDouble() * 400), -200 + (random.NextDouble() * 400));
            var b = new PanelPoint(-200 + (random.NextDouble() * 400), -200 + (random.NextDouble() * 400));
            var aInside = MinimapClip.IsInside(a, Radius);
            var bInside = MinimapClip.IsInside(b, Radius);

            var pieces = MinimapClip.Clip([a, b], Radius);

            Assert.True(pieces.Count <= 1);
            if (pieces.Count == 0)
            {
                Assert.False(aInside || bInside);
                continue;
            }

            var piece = pieces[0];
            Assert.Equal(2, piece.Count);
            if (aInside)
            {
                Assert.Equal(a, piece[0]);
            }
            else
            {
                AssertOnRim(piece[0]);
            }

            if (bInside)
            {
                Assert.Equal(b, piece[1]);
            }
            else
            {
                AssertOnRim(piece[1]);
            }

            // The piece lies on the segment.
            AssertOnSegment(a, b, piece[0]);
            AssertOnSegment(a, b, piece[1]);
        }
    }

    [Fact]
    public void Ac6_ClampToRim_OfAnOutsidePoint_IsOnTheRimOnTheRayFromTheCentre()
    {
        var random = new Random(9);
        for (var i = 0; i < 1000; i++)
        {
            var angle = random.NextDouble() * 2 * Math.PI;
            var distance = Radius + 0.001 + (random.NextDouble() * 1000);
            var point = new PanelPoint(Math.Cos(angle) * distance, Math.Sin(angle) * distance);

            var clamped = MinimapClip.ClampToRim(point, Radius);

            AssertOnRim(clamped);
            Assert.Equal(0, (clamped.X * point.Y) - (clamped.Y * point.X), 1e-6); // collinear with the centre
            Assert.True((clamped.X * point.X) + (clamped.Y * point.Y) > 0, "same side of the centre");
        }
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(10, -20)]
    [InlineData(66, 0)]
    [InlineData(-46.6, 46.6)]
    public void Ac6_ClampToRim_OfAnInsidePoint_IsThePointItself(double x, double y)
    {
        var point = new PanelPoint(x, y);

        Assert.Equal(point, MinimapClip.ClampToRim(point, Radius));
    }

    [Fact]
    public void Clip_ShortOrEmptyPolyline_GivesNoPiece()
    {
        Assert.Empty(MinimapClip.Clip([], Radius));
        Assert.Empty(MinimapClip.Clip([new PanelPoint(0, 0)], Radius));
    }

    [Fact]
    public void Clip_AppendsToTheGivenList()
    {
        var into = new List<IReadOnlyList<PanelPoint>> { new[] { new PanelPoint(1, 1), new PanelPoint(2, 2) } };

        var added = MinimapClip.Clip([new PanelPoint(0, 0), new PanelPoint(10, 0)], Radius, into);

        Assert.Equal(1, added);
        Assert.Equal(2, into.Count);
    }

    [Fact]
    public void Clip_Flat_GivesTheSamePiecesAsTheListOverload()
    {
        var random = new Random(7);
        var flat = new List<PanelPoint>();
        var lengths = new List<int>();
        for (var i = 0; i < 500; i++)
        {
            var line = new PanelPoint[2 + random.Next(8)];
            for (var k = 0; k < line.Length; k++)
            {
                line[k] = new PanelPoint(-150 + (random.NextDouble() * 300), -150 + (random.NextDouble() * 300));
            }

            var expected = MinimapClip.Clip(line, Radius);
            flat.Clear();
            lengths.Clear();

            var added = MinimapClip.Clip(line, Radius, flat, lengths);

            Assert.Equal(expected.Count, added);
            Assert.Equal(expected.Select(piece => piece.Count), lengths);
            Assert.Equal(expected.SelectMany(piece => piece), flat);
        }
    }

    [Fact]
    public void Clip_Flat_AppendsAfterExistingContents()
    {
        var flat = new List<PanelPoint> { new(1, 1), new(2, 2) };
        var lengths = new List<int> { 2 };

        var added = MinimapClip.Clip([new PanelPoint(-100, 0), new PanelPoint(0, 0), new PanelPoint(0, 100)], Radius, flat, lengths);

        Assert.Equal(1, added);
        Assert.Equal([2, 3], lengths);
        Assert.Equal(new PanelPoint(1, 1), flat[0]);
        AssertOnRim(flat[2]);
        Assert.Equal(new PanelPoint(0, 0), flat[3]);
        AssertOnRim(flat[4]);
    }

    private static void AssertOnRim(PanelPoint point) => Assert.Equal(Radius, point.Length, RimPrecision);

    private static void AssertOnSegment(PanelPoint a, PanelPoint b, PanelPoint p)
    {
        var ab = b - a;
        var ap = p - a;
        var length = ab.Length;
        if (length == 0)
        {
            return;
        }

        Assert.Equal(0, ((ab.X * ap.Y) - (ab.Y * ap.X)) / length, 1e-6);
        var t = ((ab.X * ap.X) + (ab.Y * ap.Y)) / (length * length);
        Assert.InRange(t, -1e-9, 1 + 1e-9);
    }
}
