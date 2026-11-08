using Roadman.Core.Maps;
using Roadman.Core.Minimap;

namespace Roadman.Core.Tests.Minimap;

/// <summary>
/// <see cref="MinimapClip.CirclePolygon"/> and <see cref="MinimapClip.ClipConvexToRect"/>: the part of the background
/// image the minimap draws (<c>docs/specs/minimap.md</c>, layer 2), the visible circle cut to the image's bounds.
/// </summary>
public class MinimapImageAreaTests
{
    private const double Precision = 1e-9;

    private static readonly MinimapBounds Image = new(-100, 100, -50, 50);

    [Fact]
    public void CirclePolygon_HasItsCornersOnTheCircle()
    {
        var polygon = new List<PlanarPoint>();

        MinimapClip.CirclePolygon(10, -20, 30, 48, polygon);

        Assert.Equal(48, polygon.Count);
        Assert.All(polygon, p => Assert.Equal(30, Math.Sqrt(((p.X - 10) * (p.X - 10)) + ((p.Z + 20) * (p.Z + 20))), Precision));
        Assert.Equal(new PlanarPoint(40, -20), polygon[0]);
    }

    [Fact]
    public void CirclePolygon_RejectsFewerThanThreeCorners()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => MinimapClip.CirclePolygon(0, 0, 1, 2, []));
    }

    [Fact]
    public void ClipConvexToRect_PolygonInside_IsUnchanged()
    {
        var polygon = new List<PlanarPoint>();
        MinimapClip.CirclePolygon(0, 0, 20, 32, polygon);
        var before = polygon.ToList();

        var count = MinimapClip.ClipConvexToRect(polygon, Image, []);

        Assert.Equal(32, count);
        Assert.Equal(before, polygon);
    }

    [Fact]
    public void ClipConvexToRect_PolygonOutside_IsEmptied()
    {
        var polygon = new List<PlanarPoint>();
        MinimapClip.CirclePolygon(500, 0, 20, 32, polygon);

        Assert.Equal(0, MinimapClip.ClipConvexToRect(polygon, Image, []));
        Assert.Empty(polygon);
    }

    [Fact]
    public void ClipConvexToRect_PolygonOverAnEdge_StaysInsideTheRectangle()
    {
        var polygon = new List<PlanarPoint>();
        MinimapClip.CirclePolygon(90, 40, 30, 64, polygon);

        var count = MinimapClip.ClipConvexToRect(polygon, Image, []);

        Assert.True(count >= 3);
        Assert.All(polygon, p =>
        {
            Assert.InRange(p.X, Image.MinX - Precision, Image.MaxX + Precision);
            Assert.InRange(p.Z, Image.MinZ - Precision, Image.MaxZ + Precision);
        });

        // The rectangle's corner (100, 50) is inside the circle, so it is a corner of the result.
        Assert.Contains(polygon, p => Math.Abs(p.X - 100) < Precision && Math.Abs(p.Z - 50) < Precision);
    }

    [Fact]
    public void ClipConvexToRect_RectangleInsideThePolygon_GivesTheRectangle()
    {
        var polygon = new List<PlanarPoint>();
        MinimapClip.CirclePolygon(0, 0, 1000, 64, polygon);

        MinimapClip.ClipConvexToRect(polygon, Image, []);

        Assert.Equal(4, polygon.Count);
        var area = 0.0;
        for (var i = 0; i < polygon.Count; i++)
        {
            var a = polygon[i];
            var b = polygon[(i + 1) % polygon.Count];
            area += (a.X * b.Z) - (b.X * a.Z);
        }

        Assert.Equal(200 * 100, Math.Abs(area / 2), 1e-6);
    }
}
