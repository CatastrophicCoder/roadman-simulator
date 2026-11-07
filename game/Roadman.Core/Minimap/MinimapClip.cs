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
        ArgumentNullException.ThrowIfNull(points);
        ArgumentNullException.ThrowIfNull(into);
        if (!(radius > 0))
        {
            throw new ArgumentOutOfRangeException(nameof(radius), radius, "must be above 0");
        }

        var added = 0;
        List<PanelPoint>? piece = null;
        for (var i = 0; i + 1 < points.Count; i++)
        {
            var a = points[i];
            var b = points[i + 1];
            if (!SegmentInterval(a, b, radius, out var t0, out var t1))
            {
                Close(ref piece, into, ref added);
                continue;
            }

            var start = t0 <= 0 ? a : OnRim(Lerp(a, b, t0), radius);
            var end = t1 >= 1 ? b : OnRim(Lerp(a, b, t1), radius);
            if (piece is null || t0 > 0)
            {
                Close(ref piece, into, ref added);
                piece = [start];
            }

            piece.Add(end);
            if (t1 < 1)
            {
                Close(ref piece, into, ref added);
            }
        }

        Close(ref piece, into, ref added);
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

    private static void Close(ref List<PanelPoint>? piece, List<IReadOnlyList<PanelPoint>> into, ref int added)
    {
        if (piece is { Count: >= 2 })
        {
            into.Add(piece);
            added++;
        }

        piece = null;
    }
}
