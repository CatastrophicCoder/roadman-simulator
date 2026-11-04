using Roadman.Core.Maps;

namespace Roadman.Core.Edge;

/// <summary>The nearest point on the boundary ring to a query point.</summary>
/// <param name="Point">The point on the ring.</param>
/// <param name="S">Its ring distance, in [0, <see cref="BoundaryRing.LengthM"/>).</param>
/// <param name="Distance">Its horizontal distance from the query point.</param>
public readonly record struct RingPoint(PlanarPoint Point, double S, double Distance);

/// <summary>
/// Queries on the world edge of <c>boundary.json</c> (ADR 0015, <c>docs/specs/world-edge-game.md</c>): the boundary
/// ring, the void trigger's prisms and the safety net. Everything is horizontal (x, z) except the void's y range and the
/// safety net.
/// </summary>
public sealed class EdgeGeometry
{
    /// <summary>Distances below this (metres) count as "on the ring", and nearest-point ties within it go to the lower s.</summary>
    public const double Epsilon = 1e-9;

    /// <summary>Side of a void-grid cell in metres.</summary>
    public const double VoidCellSize = 25.0;

    private readonly PlanarPoint[] _ring;
    private readonly double[] _segmentStartS;
    private readonly PlanarPoint[] _segmentOutward;
    private readonly PlanarPoint[][] _prisms;
    private readonly Dictionary<(long X, long Z), List<int>> _voidCells = [];
    private readonly (double SStart, double SEnd, double StartY, double EndY)[] _items;

    /// <exception cref="ArgumentNullException"><paramref name="boundary"/> is null.</exception>
    public EdgeGeometry(Boundary boundary)
    {
        ArgumentNullException.ThrowIfNull(boundary);
        Boundary = boundary;

        _ring = [.. boundary.Ring.Points];
        var count = _ring.Length;
        _segmentStartS = new double[count];
        _segmentOutward = new PlanarPoint[count];

        // Signed area in (x, z) (shoelace): positive means the interior lies left of each segment's direction in the
        // x-z plane, so the outward normal is the right-hand one (dz, -dx); negative, the left-hand one (-dz, dx).
        // boundary.json's ring is counter-clockwise seen from above (z south), which is a negative area here.
        var area2 = 0.0;
        for (var i = 0; i < count; i++)
        {
            var a = _ring[i];
            var b = _ring[(i + 1) % count];
            area2 += (a.X * b.Z) - (b.X * a.Z);
        }

        var sign = area2 >= 0 ? 1.0 : -1.0;
        var s = 0.0;
        for (var i = 0; i < count; i++)
        {
            var a = _ring[i];
            var b = _ring[(i + 1) % count];
            _segmentStartS[i] = s;
            var dx = b.X - a.X;
            var dz = b.Z - a.Z;
            var length = Math.Sqrt((dx * dx) + (dz * dz));
            _segmentOutward[i] = length > 0
                ? new PlanarPoint(sign * dz / length, -sign * dx / length)
                : new PlanarPoint(0, 0);
            s += length;
        }

        _items =
        [
            .. boundary.Roadblocks.Select(item => (item.SStart, item.SEnd, item.Start.Y, item.End.Y))
                .Concat(boundary.Fences.Select(item => (item.SStart, item.SEnd, item.Start.Y, item.End.Y)))
                .OrderBy(item => item.SStart),
        ];

        _prisms = [.. boundary.VoidTrigger.Prisms.Select(prism => prism.Footprint.ToArray())];
        for (var p = 0; p < _prisms.Length; p++)
        {
            var footprint = _prisms[p];
            if (footprint.Length == 0)
            {
                continue;
            }

            var minX = footprint.Min(point => point.X);
            var maxX = footprint.Max(point => point.X);
            var minZ = footprint.Min(point => point.Z);
            var maxZ = footprint.Max(point => point.Z);
            for (var cx = Cell(minX); cx <= Cell(maxX); cx++)
            {
                for (var cz = Cell(minZ); cz <= Cell(maxZ); cz++)
                {
                    if (!_voidCells.TryGetValue((cx, cz), out var list))
                    {
                        list = [];
                        _voidCells[(cx, cz)] = list;
                    }

                    list.Add(p);
                }
            }
        }
    }

    /// <summary>The boundary these queries answer for.</summary>
    public Boundary Boundary { get; }

    /// <summary>
    /// Whether (<paramref name="x"/>, <paramref name="z"/>) lies inside the boundary ring (even-odd rule). Points on
    /// the ring (within <see cref="Epsilon"/>) count as inside.
    /// </summary>
    public bool IsInsideRing(double x, double z)
    {
        if (_ring.Length == 0)
        {
            return false;
        }

        if (NearestRingPoint(x, z).Distance < Epsilon)
        {
            return true;
        }

        var inside = false;
        for (int i = 0, j = _ring.Length - 1; i < _ring.Length; j = i++)
        {
            var a = _ring[i];
            var b = _ring[j];
            if ((a.Z > z) != (b.Z > z) && x < a.X + ((z - a.Z) * (b.X - a.X) / (b.Z - a.Z)))
            {
                inside = !inside;
            }
        }

        return inside;
    }

    /// <summary>
    /// Whether <paramref name="point"/> lies inside any void prism: inside its footprint (a convex quadrilateral of
    /// either winding, edges included) and <see cref="VoidTrigger.BottomY"/> &lt;= y &lt;= <see cref="VoidTrigger.TopY"/>.
    /// The prisms are looked up in a uniform grid of <see cref="VoidCellSize"/> cells over their bounding boxes.
    /// </summary>
    public bool IsInVoid(LocalPoint point)
    {
        var trigger = Boundary.VoidTrigger;
        if (!(point.Y >= trigger.BottomY && point.Y <= trigger.TopY))
        {
            return false;
        }

        if (!_voidCells.TryGetValue((Cell(point.X), Cell(point.Z)), out var candidates))
        {
            return false;
        }

        foreach (var index in candidates)
        {
            if (ConvexContains(_prisms[index], point.X, point.Z))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The nearest point on the closed ring (segments i to i + 1, and the last point to the first). Ties within
    /// <see cref="Epsilon"/> go to the lower s: the first segment in ring order wins, so ring point 0 has s 0, never
    /// <see cref="BoundaryRing.LengthM"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The ring has no points.</exception>
    public RingPoint NearestRingPoint(double x, double z)
    {
        var (point, s, distance, _) = Nearest(x, z);
        return new RingPoint(point, s, distance);
    }

    /// <summary>
    /// The unit horizontal vector (x, z) pointing out of the playable area at (<paramref name="x"/>,
    /// <paramref name="z"/>): from the nearest ring point to the point when the point is outside the ring, the
    /// opposite way when it is inside, and the outward normal of the segment holding the nearest point (lower-s rule)
    /// when it lies on the ring (closer than <see cref="Epsilon"/>).
    /// </summary>
    public PlanarPoint OutwardAt(double x, double z)
    {
        var (point, _, distance, segment) = Nearest(x, z);
        if (distance < Epsilon)
        {
            return _segmentOutward[segment];
        }

        var dx = (x - point.X) / distance;
        var dz = (z - point.Z) / distance;
        return IsInsideRing(x, z) ? new PlanarPoint(-dx, -dz) : new PlanarPoint(dx, dz);
    }

    /// <summary>
    /// The height of the ledge's inner edge at ring distance <paramref name="s"/> (taken modulo
    /// <see cref="BoundaryRing.LengthM"/>): linear between the start and end heights of the roadblock or fence that
    /// covers <paramref name="s"/> (ADR 0015: the ledge's inner edge runs straight between its item's end heights).
    /// Where two items overlap, the one that starts first wins. In a gap between items it is linear from the previous
    /// item's end height to the next item's start height.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="s"/> isn't finite.</exception>
    /// <exception cref="InvalidOperationException">The boundary has no roadblocks and no fences.</exception>
    public double LedgeHeightAt(double s)
    {
        if (!double.IsFinite(s))
        {
            throw new ArgumentException($"s must be finite, is {s}", nameof(s));
        }

        if (_items.Length == 0)
        {
            throw new InvalidOperationException("The boundary has no roadblocks and no fences.");
        }

        var length = Boundary.Ring.LengthM;
        if (length > 0)
        {
            s %= length;
            if (s < 0)
            {
                s += length;
            }
        }

        // An item spanning the ring's first point has SEnd above LengthM, so try s and s + LengthM.
        foreach (var candidate in (ReadOnlySpan<double>)[s, s + length])
        {
            foreach (var item in _items)
            {
                if (candidate >= item.SStart && candidate <= item.SEnd)
                {
                    return Interpolate(item.StartY, item.EndY, item.SStart, item.SEnd, candidate);
                }
            }
        }

        // In a gap: from the end of the last item ending before s to the start of the first item starting after it,
        // cyclically.
        var previous = _items[^1];
        var previousEnd = previous.SEnd - length;
        foreach (var item in _items)
        {
            if (item.SEnd <= s && item.SEnd >= previousEnd)
            {
                previous = item;
                previousEnd = item.SEnd;
            }
        }

        var next = _items[0];
        var nextStart = next.SStart + length;
        foreach (var item in _items)
        {
            if (item.SStart >= s && item.SStart < nextStart)
            {
                next = item;
                nextStart = item.SStart;
            }
        }

        return Interpolate(previous.EndY, next.StartY, previousEnd, nextStart, s);
    }

    /// <summary>
    /// True when <paramref name="y"/> is below <see cref="Cliff.BottomY"/> − <paramref name="margin"/> (false at
    /// exactly that height).
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="margin"/> isn't finite.</exception>
    public bool IsBelowSafetyNet(double y, double margin)
    {
        if (!double.IsFinite(margin))
        {
            throw new ArgumentException($"margin must be finite, is {margin}", nameof(margin));
        }

        return y < Boundary.Cliff.BottomY - margin;
    }

    private (PlanarPoint Point, double S, double Distance, int Segment) Nearest(double x, double z)
    {
        if (_ring.Length == 0)
        {
            throw new InvalidOperationException("The boundary ring has no points.");
        }

        var bestPoint = _ring[0];
        var bestS = 0.0;
        var bestDistance = double.PositiveInfinity;
        var bestSegment = 0;
        for (var i = 0; i < _ring.Length; i++)
        {
            var a = _ring[i];
            var b = _ring[(i + 1) % _ring.Length];
            var dx = b.X - a.X;
            var dz = b.Z - a.Z;
            var lengthSq = (dx * dx) + (dz * dz);
            var t = lengthSq > 0 ? Math.Clamp((((x - a.X) * dx) + ((z - a.Z) * dz)) / lengthSq, 0, 1) : 0;
            var px = a.X + (t * dx);
            var pz = a.Z + (t * dz);
            var distance = Math.Sqrt(((x - px) * (x - px)) + ((z - pz) * (z - pz)));
            if (distance < bestDistance - Epsilon)
            {
                bestPoint = new PlanarPoint(px, pz);
                bestS = _segmentStartS[i] + (t * Math.Sqrt(lengthSq));
                bestDistance = distance;
                bestSegment = lengthSq > 0 ? i : bestSegment;
            }
        }

        // The ring's own length (summed here) can differ from LengthM in the last digits; keep s below LengthM.
        var length = Boundary.Ring.LengthM;
        if (bestS >= length)
        {
            bestS = Math.Max(0, bestS - length);
        }

        return (bestPoint, bestS, bestDistance, bestSegment);
    }

    private static bool ConvexContains(PlanarPoint[] polygon, double x, double z)
    {
        var positive = false;
        var negative = false;
        for (var i = 0; i < polygon.Length; i++)
        {
            var a = polygon[i];
            var b = polygon[(i + 1) % polygon.Length];
            var ex = b.X - a.X;
            var ez = b.Z - a.Z;
            var length = Math.Sqrt((ex * ex) + (ez * ez));
            if (length == 0)
            {
                continue;
            }

            // Signed distance of the point from the edge's line; within Epsilon counts as on the edge.
            var side = ((ex * (z - a.Z)) - (ez * (x - a.X))) / length;
            if (side > Epsilon)
            {
                positive = true;
            }
            else if (side < -Epsilon)
            {
                negative = true;
            }

            if (positive && negative)
            {
                return false;
            }
        }

        return true;
    }

    private static double Interpolate(double fromY, double toY, double fromS, double toS, double s) =>
        toS - fromS > 0 ? fromY + ((toY - fromY) * Math.Clamp((s - fromS) / (toS - fromS), 0, 1)) : fromY;

    private static long Cell(double value) => (long)Math.Floor(value / VoidCellSize);
}
