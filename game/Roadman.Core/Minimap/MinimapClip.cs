using Roadman.Core.Maps;

namespace Roadman.Core.Minimap;

/// <summary>
/// Clipping to the round minimap panel (<c>docs/specs/minimap.md</c>, "Roadman.Core"): the circle is centred on the
/// panel origin. Points exactly on the rim count as inside.
/// </summary>
public static class MinimapClip
{
    /// <summary>True when <paramref name="point"/> lies in the circle of <paramref name="radius"/>, rim included.</summary>
    public static bool IsInside(PanelPoint point, double radius) =>
        (point.X * point.X) + (point.Y * point.Y) <= radius * radius;

    /// <summary>
    /// The parts of the polyline <paramref name="points"/> inside the circle of <paramref name="radius"/>: zero or more
    /// pieces, each of at least 2 points, in polyline order. Vertices inside are kept as they are; a piece that enters
    /// or leaves the circle starts or ends on the rim. A polyline fully inside comes back as one piece with the same
    /// points; one fully outside gives no piece.
    /// </summary>
    public static List<IReadOnlyList<PanelPoint>> Clip(IReadOnlyList<PanelPoint> points, double radius)
    {
        var result = new List<IReadOnlyList<PanelPoint>>();
        Clip(points, radius, result);
        return result;
    }

    /// <summary>Like <see cref="Clip(IReadOnlyList{PanelPoint}, double)"/>, appending the pieces to <paramref name="into"/>.</summary>
    /// <returns>The number of pieces added.</returns>
    public static int Clip(IReadOnlyList<PanelPoint> points, double radius, List<IReadOnlyList<PanelPoint>> into)
    {
        ArgumentNullException.ThrowIfNull(into);
        var flat = new List<PanelPoint>();
        var lengths = new List<int>();
        var added = Clip(points, radius, flat, lengths);
        var offset = 0;
        foreach (var length in lengths)
        {
            into.Add(flat.GetRange(offset, length));
            offset += length;
        }

        return added;
    }

    /// <summary>
    /// Like <see cref="Clip(IReadOnlyList{PanelPoint}, double)"/>, but flat: appends the points of every piece, one
    /// piece after the other, to <paramref name="pointsInto"/> and each piece's point count to
    /// <paramref name="pieceLengths"/>. Allocates nothing once the two lists have grown (the minimap's per-frame use).
    /// </summary>
    /// <returns>The number of pieces added.</returns>
    public static int Clip(IReadOnlyList<PanelPoint> points, double radius, List<PanelPoint> pointsInto, List<int> pieceLengths)
    {
        ArgumentNullException.ThrowIfNull(points);
        ArgumentNullException.ThrowIfNull(pointsInto);
        ArgumentNullException.ThrowIfNull(pieceLengths);
        if (!(radius > 0))
        {
            throw new ArgumentOutOfRangeException(nameof(radius), radius, "must be above 0");
        }

        var added = 0;
        var pieceStart = -1;
        for (var i = 0; i + 1 < points.Count; i++)
        {
            var a = points[i];
            var b = points[i + 1];
            if (!SegmentInterval(a, b, radius, out var t0, out var t1))
            {
                Close(ref pieceStart, pointsInto, pieceLengths, ref added);
                continue;
            }

            var start = t0 <= 0 ? a : OnRim(Lerp(a, b, t0), radius);
            var end = t1 >= 1 ? b : OnRim(Lerp(a, b, t1), radius);
            if (pieceStart < 0 || t0 > 0)
            {
                Close(ref pieceStart, pointsInto, pieceLengths, ref added);
                pieceStart = pointsInto.Count;
                pointsInto.Add(start);
            }

            pointsInto.Add(end);
            if (t1 < 1)
            {
                Close(ref pieceStart, pointsInto, pieceLengths, ref added);
            }
        }

        Close(ref pieceStart, pointsInto, pieceLengths, ref added);
        return added;
    }

    /// <summary>
    /// <paramref name="point"/> itself when it lies in the circle; otherwise the rim point on the ray from the centre
    /// through it. The centre stays the centre.
    /// </summary>
    public static PanelPoint ClampToRim(PanelPoint point, double radius)
    {
        if (!(radius > 0))
        {
            throw new ArgumentOutOfRangeException(nameof(radius), radius, "must be above 0");
        }

        return IsInside(point, radius) ? point : OnRim(point, radius);
    }

    /// <summary>
    /// Replaces the contents of <paramref name="into"/> with a regular polygon of <paramref name="segments"/> corners
    /// on the circle of <paramref name="radius"/> round (<paramref name="centreX"/>, <paramref name="centreZ"/>),
    /// counter-clockwise from +x. Its edges lie inside the circle by at most radius × (1 − cos(π / segments)).
    /// </summary>
    public static void CirclePolygon(double centreX, double centreZ, double radius, int segments, List<PlanarPoint> into)
    {
        ArgumentNullException.ThrowIfNull(into);
        if (segments < 3)
        {
            throw new ArgumentOutOfRangeException(nameof(segments), segments, "must be at least 3");
        }

        into.Clear();
        for (var i = 0; i < segments; i++)
        {
            var (sin, cos) = Math.SinCos(2 * Math.PI * i / segments);
            into.Add(new PlanarPoint(centreX + (radius * cos), centreZ + (radius * sin)));
        }
    }

    /// <summary>
    /// Clips the convex polygon in <paramref name="polygon"/> to the axis-aligned rectangle <paramref name="rect"/> in
    /// place (Sutherland–Hodgman), using <paramref name="scratch"/> as working space. The result is convex too; when
    /// fewer than 3 corners remain (no overlap) the list is emptied. Allocates nothing once the lists have grown.
    /// </summary>
    /// <returns>The number of corners left.</returns>
    public static int ClipConvexToRect(List<PlanarPoint> polygon, MinimapBounds rect, List<PlanarPoint> scratch)
    {
        ArgumentNullException.ThrowIfNull(polygon);
        ArgumentNullException.ThrowIfNull(scratch);
        ClipEdge(polygon, scratch, xAxis: true, rect.MinX, keepAbove: true);
        ClipEdge(polygon, scratch, xAxis: true, rect.MaxX, keepAbove: false);
        ClipEdge(polygon, scratch, xAxis: false, rect.MinZ, keepAbove: true);
        ClipEdge(polygon, scratch, xAxis: false, rect.MaxZ, keepAbove: false);
        if (polygon.Count < 3)
        {
            polygon.Clear();
        }

        return polygon.Count;
    }

    /// <summary>
    /// One Sutherland–Hodgman pass against the line x = <paramref name="limit"/> (or z with <paramref name="xAxis"/>
    /// false), keeping the side at or above it (<paramref name="keepAbove"/>) or at or below it.
    /// </summary>
    private static void ClipEdge(List<PlanarPoint> polygon, List<PlanarPoint> scratch, bool xAxis, double limit, bool keepAbove)
    {
        scratch.Clear();
        var count = polygon.Count;
        for (var i = 0; i < count; i++)
        {
            var current = polygon[i];
            var previous = polygon[(i + count - 1) % count];
            var dc = Signed(current, xAxis, limit, keepAbove);
            var dp = Signed(previous, xAxis, limit, keepAbove);
            if (dc >= 0)
            {
                if (dp < 0)
                {
                    scratch.Add(Crossing(previous, current, dp, dc));
                }

                scratch.Add(current);
            }
            else if (dp >= 0)
            {
                scratch.Add(Crossing(previous, current, dp, dc));
            }
        }

        polygon.Clear();
        polygon.AddRange(scratch);
    }

    private static double Signed(PlanarPoint point, bool xAxis, double limit, bool keepAbove)
    {
        var value = (xAxis ? point.X : point.Z) - limit;
        return keepAbove ? value : -value;
    }

    /// <summary>The point of a→b where the signed distance goes from <paramref name="da"/> to 0.</summary>
    private static PlanarPoint Crossing(PlanarPoint a, PlanarPoint b, double da, double db)
    {
        var t = da / (da - db);
        return new PlanarPoint(a.X + ((b.X - a.X) * t), a.Z + ((b.Z - a.Z) * t));
    }

    /// <summary>
    /// The parameter range [t0, t1] ⊆ [0, 1] of the segment a→b inside the circle; false when no part of it is inside.
    /// </summary>
    private static bool SegmentInterval(PanelPoint a, PanelPoint b, double radius, out double t0, out double t1)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var qa = (dx * dx) + (dy * dy);
        var qb = 2 * ((a.X * dx) + (a.Y * dy));
        var qc = (a.X * a.X) + (a.Y * a.Y) - (radius * radius);
        t0 = 0;
        t1 = 1;
        if (qa == 0)
        {
            return qc <= 0;
        }

        var aInside = qc <= 0;
        var bInside = IsInside(b, radius);
        if (aInside && bInside)
        {
            return true;
        }

        var discriminant = (qb * qb) - (4 * qa * qc);
        if (discriminant <= 0)
        {
            return false;
        }

        var root = Math.Sqrt(discriminant);

        // The numerically stable pair of roots.
        var q = -0.5 * (qb + (Math.Sign(qb) == 0 ? root : Math.Sign(qb) * root));
        var r1 = q / qa;
        var r2 = q != 0 ? qc / q : -r1;
        var enter = Math.Min(r1, r2);
        var leave = Math.Max(r1, r2);
        t0 = aInside ? 0 : enter;
        t1 = bInside ? 1 : leave;
        if (t0 >= 1 || t1 <= 0 || t1 <= t0)
        {
            return false;
        }

        t0 = Math.Max(t0, 0);
        t1 = Math.Min(t1, 1);
        return true;
    }

    private static PanelPoint Lerp(PanelPoint a, PanelPoint b, double t) =>
        new(a.X + ((b.X - a.X) * t), a.Y + ((b.Y - a.Y) * t));

    private static PanelPoint OnRim(PanelPoint point, double radius)
    {
        var length = point.Length;
        return length == 0 ? point : point * (radius / length);
    }

    /// <summary>Ends the open piece (if any): records its length, or drops it when it has fewer than 2 points.</summary>
    private static void Close(ref int pieceStart, List<PanelPoint> pointsInto, List<int> pieceLengths, ref int added)
    {
        if (pieceStart >= 0)
        {
            var length = pointsInto.Count - pieceStart;
            if (length >= 2)
            {
                pieceLengths.Add(length);
                added++;
            }
            else
            {
                pointsInto.RemoveRange(pieceStart, length);
            }
        }

        pieceStart = -1;
    }
}
