using Roadman.Core.Maps;

namespace Roadman.Core.Edge;

/// <summary>The three barrier types along the world edge.</summary>
public enum BarrierKind
{
    /// <summary>Red-and-white boards on stands where a road leaves the playable area.</summary>
    Roadblock,

    /// <summary>Posts with barbed-wire strands.</summary>
    LandFence,

    /// <summary>Posts with a line on floats.</summary>
    WaterFence,
}

/// <summary>The meshes a barrier is built from.</summary>
public enum PieceKind
{
    /// <summary>A fence post (land and water fences).</summary>
    Post,

    /// <summary>A barbed-wire strand of a land fence.</summary>
    Strand,

    /// <summary>A float of a water fence.</summary>
    Float,

    /// <summary>The line on the floats of a water fence.</summary>
    FloatLine,

    /// <summary>A roadblock stand.</summary>
    Stand,

    /// <summary>A red stripe of a roadblock board.</summary>
    BoardRed,

    /// <summary>A white stripe of a roadblock board.</summary>
    BoardWhite,
}

/// <summary>
/// A parallelepiped: the unit cube [−0.5, 0.5]³ mapped by x′ = <see cref="Origin"/> + u·<see cref="AxisX"/> +
/// v·<see cref="AxisY"/> + w·<see cref="AxisZ"/>. <see cref="AxisY"/> is always vertical (0, h, 0);
/// <see cref="AxisX"/> may slope (a sheared box), so a box's top follows a sloping line exactly.
/// <see cref="AxisZ"/> is horizontal.
/// </summary>
/// <param name="Origin">The box's centre.</param>
/// <param name="AxisX">The full edge along the barrier (may slope).</param>
/// <param name="AxisY">The full vertical edge.</param>
/// <param name="AxisZ">The full horizontal edge across the barrier.</param>
public readonly record struct BoxTransform(LocalPoint Origin, LocalPoint AxisX, LocalPoint AxisY, LocalPoint AxisZ)
{
    /// <summary>The 8 corners, for u, then v, then w in {−0.5, 0.5} (w changing fastest).</summary>
    public IReadOnlyList<LocalPoint> Corners()
    {
        var corners = new LocalPoint[8];
        var index = 0;
        foreach (var u in (ReadOnlySpan<double>)[-0.5, 0.5])
        {
            foreach (var v in (ReadOnlySpan<double>)[-0.5, 0.5])
            {
                foreach (var w in (ReadOnlySpan<double>)[-0.5, 0.5])
                {
                    corners[index++] = new LocalPoint(
                        Origin.X + (u * AxisX.X) + (v * AxisY.X) + (w * AxisZ.X),
                        Origin.Y + (u * AxisX.Y) + (v * AxisY.Y) + (w * AxisZ.Y),
                        Origin.Z + (u * AxisX.Z) + (v * AxisY.Z) + (w * AxisZ.Z));
                }
            }
        }

        return corners;
    }
}

/// <summary>One visual piece of a barrier item.</summary>
/// <param name="Kind">Which mesh it is.</param>
/// <param name="Box">Where the unit-cube mesh goes.</param>
/// <param name="BaseA">
/// Start of the reference line under the piece for the height rule: the fence line (fences) or the ground under the
/// stands (roadblocks). Equal to <paramref name="BaseB"/> for posts and stands.
/// </param>
/// <param name="BaseB">End of the reference line.</param>
public sealed record BarrierPiece(PieceKind Kind, BoxTransform Box, LocalPoint BaseA, LocalPoint BaseB);

/// <summary>The layout of one roadblock or fence of <c>boundary.json</c>.</summary>
/// <param name="Id">The roadblock's or fence's id.</param>
/// <param name="Kind">Its barrier type.</param>
/// <param name="SStart">Ring distance of its start.</param>
/// <param name="SEnd">Ring distance of its end.</param>
/// <param name="Start">Its start on the boundary.</param>
/// <param name="End">Its end on the boundary.</param>
/// <param name="Outward">Unit (x, z) pointing out of the playable area.</param>
/// <param name="Pieces">The visual pieces.</param>
/// <param name="Collision">The collision boxes.</param>
/// <param name="PostBases">Fences: the base of each post the item owns, on the line. Roadblocks: each stand's base.</param>
public sealed record BarrierItemLayout(
    string Id,
    BarrierKind Kind,
    double SStart,
    double SEnd,
    LocalPoint Start,
    LocalPoint End,
    PlanarPoint Outward,
    IReadOnlyList<BarrierPiece> Pieces,
    IReadOnlyList<BoxTransform> Collision,
    IReadOnlyList<LocalPoint> PostBases);

/// <summary>
/// Turns the roadblocks and fences of a <see cref="Boundary"/> into piece and collision boxes, with no engine types
/// (<c>docs/specs/world-edge-game.md</c>, "Barriers").
/// </summary>
/// <remarks>
/// <para>Rules every item keeps (with <see cref="BarrierParameters"/> that pass validation):</para>
/// <list type="bullet">
/// <item>No piece corner is more than the type's height above its base (<see cref="BarrierPiece.BaseA"/> to
/// <see cref="BarrierPiece.BaseB"/> at the corner's horizontal projection).</item>
/// <item>Pieces and collision boxes are centred on the item's line; the widest reaches half its depth outward and
/// inward (0.25 m with the defaults).</item>
/// <item>Fences: one collision box along the whole line, roadblocks: one per bay between neighbouring stands. Boxes
/// reach <see cref="BarrierParameters.JointOverlap"/> past both ends of the item, from
/// <see cref="BarrierParameters.CollisionDepthBelow"/> below the line up to the height (in the overlap, a box's sloping
/// top is lowered by the slope times the overlap, so it stays under the height above the item's end).</item>
/// <item>Fence posts stand at both ends and at most <see cref="BarrierParameters.PostSpacing"/> apart, evenly spaced.
/// An item owns the post at its end, and the one at its start only when the previous item in ring order isn't a
/// fence, so a fence-fence joint has exactly one post.</item>
/// <item>Roadblock stands stand at both ends and at most <see cref="BarrierParameters.StandSpacing"/> apart, evenly
/// spaced, each on <c>groundAt</c> (<see cref="StandGround"/>); board stripes alternate red and white along each row.</item>
/// </list>
/// </remarks>
public static class BarrierLayout
{
    private const double CountTolerance = 1e-9;

    /// <summary>One item per roadblock and fence of <paramref name="boundary"/>, sorted by s start (then id).</summary>
    /// <param name="boundary">The world edge.</param>
    /// <param name="parameters">Piece sizes.</param>
    /// <param name="groundAt">
    /// The ground or road height at (x, z) under a roadblock stand, looked up by <see cref="StandGround"/>; null at
    /// both of its points means the straight line between the roadblock's start and end heights at that point.
    /// </param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static IReadOnlyList<BarrierItemLayout> Build(
        Boundary boundary, BarrierParameters parameters, Func<double, double, double?> groundAt)
    {
        ArgumentNullException.ThrowIfNull(boundary);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(groundAt);

        var sources = new List<Source>(boundary.Roadblocks.Count + boundary.Fences.Count);
        sources.AddRange(boundary.Roadblocks.Select(roadblock => new Source(
            roadblock.Id, BarrierKind.Roadblock, roadblock.SStart, roadblock.SEnd, roadblock.Start, roadblock.End,
            new PlanarPoint(roadblock.Outward.X, roadblock.Outward.Z))));
        sources.AddRange(boundary.Fences.Select(fence => new Source(
            fence.Id, fence.Type == FenceType.Water ? BarrierKind.WaterFence : BarrierKind.LandFence, fence.SStart,
            fence.SEnd, fence.Start, fence.End, new PlanarPoint(fence.Outward.X, fence.Outward.Z))));
        sources.Sort((a, b) =>
        {
            var bySStart = a.SStart.CompareTo(b.SStart);
            return bySStart != 0 ? bySStart : StringComparer.Ordinal.Compare(a.Id, b.Id);
        });

        var items = new List<BarrierItemLayout>(sources.Count);
        for (var i = 0; i < sources.Count; i++)
        {
            var source = sources[i];
            if (source.Kind == BarrierKind.Roadblock)
            {
                items.Add(BuildRoadblock(source, parameters, groundAt));
            }
            else
            {
                var previous = sources[(i - 1 + sources.Count) % sources.Count];
                var ownsStartPost = previous.Kind == BarrierKind.Roadblock;
                items.Add(BuildFence(source, parameters, ownsStartPost));
            }
        }

        return items;
    }

    private static BarrierItemLayout BuildFence(Source item, BarrierParameters parameters, bool ownsStartPost)
    {
        var frame = Frame.Of(item);
        var height = item.Kind == BarrierKind.WaterFence ? parameters.WaterFenceHeight : parameters.LandFenceHeight;
        var pieces = new List<BarrierPiece>();
        var postBases = new List<LocalPoint>();

        var bays = BayCount(frame.Length, parameters.PostSpacing);
        for (var k = ownsStartPost ? 0 : 1; k <= bays; k++)
        {
            var post = Lerp(item.Start, item.End, (double)k / bays);
            postBases.Add(post);
            pieces.Add(new BarrierPiece(
                PieceKind.Post,
                VerticalBox(post, -parameters.PostDepthBelow, height, parameters.PostWidth, parameters.PostWidth, frame),
                post,
                post));
        }

        if (item.Kind == BarrierKind.LandFence)
        {
            var thickness = Math.Min(parameters.StrandThickness, height);
            for (var k = 1; k <= parameters.StrandCount; k++)
            {
                var centre = (height - (thickness / 2)) * k / parameters.StrandCount;
                pieces.Add(new BarrierPiece(
                    PieceKind.Strand,
                    SegmentBox(item.Start, item.End, centre - (thickness / 2), centre + (thickness / 2), thickness, frame),
                    item.Start,
                    item.End));
            }
        }
        else
        {
            var lineThickness = parameters.FloatLineThickness;
            var lineCentre = Math.Min(parameters.FloatLineHeight, height - (lineThickness / 2));
            pieces.Add(new BarrierPiece(
                PieceKind.FloatLine,
                SegmentBox(
                    item.Start, item.End, lineCentre - (lineThickness / 2), lineCentre + (lineThickness / 2),
                    lineThickness, frame),
                item.Start,
                item.End));

            if (frame.Length > 0)
            {
                var count = Math.Max(1, (int)Math.Round(frame.Length / parameters.FloatSpacing));
                var size = Math.Min(parameters.FloatSize, 2 * height);
                var halfAlong = Math.Min(size, frame.Length / count) / 2 / frame.Length;
                for (var j = 0; j < count; j++)
                {
                    var f = (j + 0.5) / count;
                    pieces.Add(new BarrierPiece(
                        PieceKind.Float,
                        SegmentBox(
                            Lerp(item.Start, item.End, f - halfAlong), Lerp(item.Start, item.End, f + halfAlong),
                            -size / 2, size / 2, size, frame),
                        item.Start,
                        item.End));
                }
            }
        }

        var collision = new[]
        {
            CollisionBox(item.Start, item.End, frame, height, parameters, extendStart: true, extendEnd: true),
        };
        return new BarrierItemLayout(
            item.Id, item.Kind, item.SStart, item.SEnd, item.Start, item.End, item.Outward, pieces, collision,
            postBases);
    }

    /// <summary>
    /// The ground height for a roadblock stand at (<paramref name="x"/>, <paramref name="z"/>) on the boundary:
    /// <paramref name="groundAt"/> there, or, when that is null or not finite, <paramref name="groundAt"/> at the point
    /// <paramref name="inset"/> inward (against <paramref name="outward"/>). The terrain and road meshes end exactly on
    /// the ring, so a lookup right on it can miss by rounding; the inset one lands inside them. Null when both miss.
    /// </summary>
    /// <param name="groundAt">The ground or road height at (x, z), or null.</param>
    /// <param name="x">The stand's x.</param>
    /// <param name="z">The stand's z.</param>
    /// <param name="outward">The item's unit outward direction.</param>
    /// <param name="inset">How far inward the second lookup is, metres (<see cref="BarrierParameters.StandGroundInset"/>).</param>
    /// <exception cref="ArgumentNullException"><paramref name="groundAt"/> is null.</exception>
    public static double? StandGround(Func<double, double, double?> groundAt, double x, double z, PlanarPoint outward, double inset)
    {
        ArgumentNullException.ThrowIfNull(groundAt);
        if (groundAt(x, z) is { } y && double.IsFinite(y))
        {
            return y;
        }

        if (inset > 0 && groundAt(x - (outward.X * inset), z - (outward.Z * inset)) is { } inward && double.IsFinite(inward))
        {
            return inward;
        }

        return null;
    }

    private static BarrierItemLayout BuildRoadblock(
        Source item, BarrierParameters parameters, Func<double, double, double?> groundAt)
    {
        var frame = Frame.Of(item);
        var height = parameters.RoadblockHeight;
        var bays = BayCount(frame.Length, parameters.StandSpacing);

        var stands = new LocalPoint[bays + 1];
        for (var k = 0; k <= bays; k++)
        {
            var onLine = Lerp(item.Start, item.End, (double)k / bays);
            var ground = StandGround(groundAt, onLine.X, onLine.Z, item.Outward, parameters.StandGroundInset);
            stands[k] = ground is { } y ? onLine with { Y = y } : onLine;
        }

        var pieces = new List<BarrierPiece>();
        foreach (var stand in stands)
        {
            pieces.Add(new BarrierPiece(
                PieceKind.Stand,
                VerticalBox(stand, 0, height, parameters.StandWidth, parameters.StandDepth, frame),
                stand,
                stand));
        }

        var collision = new List<BoxTransform>(bays);
        for (var k = 0; k < bays; k++)
        {
            var a = stands[k];
            var b = stands[k + 1];
            var stripes = Math.Max(1, (int)Math.Round(frame.Length / bays / parameters.StripeLength));
            for (var row = 0; row < parameters.BoardRows; row++)
            {
                var top = height - (row * (parameters.BoardHeight + parameters.BoardGap));
                var bottom = top - parameters.BoardHeight;
                if (bottom < 0)
                {
                    break;
                }

                for (var j = 0; j < stripes; j++)
                {
                    var red = (k * stripes + j + row) % 2 == 0;
                    pieces.Add(new BarrierPiece(
                        red ? PieceKind.BoardRed : PieceKind.BoardWhite,
                        SegmentBox(
                            Lerp(a, b, (double)j / stripes), Lerp(a, b, (double)(j + 1) / stripes), bottom, top,
                            parameters.BoardThickness, frame),
                        a,
                        b));
                }
            }

            collision.Add(CollisionBox(a, b, frame, height, parameters, extendStart: k == 0, extendEnd: k == bays - 1));
        }

        return new BarrierItemLayout(
            item.Id, item.Kind, item.SStart, item.SEnd, item.Start, item.End, item.Outward, pieces, collision, stands);
    }

    /// <summary>
    /// A collision box from <paramref name="a"/> to <paramref name="b"/>, optionally extended by the joint overlap
    /// along the line (and its slope) past either end, from below the line up to the height. Where it is extended, its
    /// top is lowered by |slope| × overlap so that no corner rises above the height over the nearer end.
    /// </summary>
    private static BoxTransform CollisionBox(
        LocalPoint a, LocalPoint b, Frame frame, double height, BarrierParameters parameters, bool extendStart,
        bool extendEnd)
    {
        var overlap = parameters.JointOverlap;
        var horizontal = Math.Sqrt(((b.X - a.X) * (b.X - a.X)) + ((b.Z - a.Z) * (b.Z - a.Z)));
        var slope = horizontal > 0 ? (b.Y - a.Y) / horizontal : 0;
        var step = new LocalPoint(frame.Along.X * overlap, slope * overlap, frame.Along.Z * overlap);
        var start = extendStart ? Sub(a, step) : a;
        var end = extendEnd ? Add(b, step) : b;
        var lowered = extendStart || extendEnd ? Math.Abs(slope) * overlap : 0;
        var top = Math.Max(height - lowered, height / 2);
        return SegmentBox(start, end, -parameters.CollisionDepthBelow, top, parameters.CollisionThickness, frame);
    }

    /// <summary>A box along the 3D segment a–b, from <paramref name="bottom"/> to <paramref name="top"/> above it.</summary>
    private static BoxTransform SegmentBox(
        LocalPoint a, LocalPoint b, double bottom, double top, double thickness, Frame frame) =>
        new(
            new LocalPoint((a.X + b.X) / 2, ((a.Y + b.Y) / 2) + ((bottom + top) / 2), (a.Z + b.Z) / 2),
            Sub(b, a),
            new LocalPoint(0, top - bottom, 0),
            new LocalPoint(frame.Across.X * thickness, 0, frame.Across.Z * thickness));

    /// <summary>An upright box centred on <paramref name="point"/> horizontally, from bottom to top above it.</summary>
    private static BoxTransform VerticalBox(
        LocalPoint point, double bottom, double top, double width, double depth, Frame frame) =>
        new(
            new LocalPoint(point.X, point.Y + ((bottom + top) / 2), point.Z),
            new LocalPoint(frame.Along.X * width, 0, frame.Along.Z * width),
            new LocalPoint(0, top - bottom, 0),
            new LocalPoint(frame.Across.X * depth, 0, frame.Across.Z * depth));

    /// <summary>Bays of at most <paramref name="spacing"/> over <paramref name="length"/>; at least 1.</summary>
    private static int BayCount(double length, double spacing) =>
        Math.Max(1, (int)Math.Ceiling((length / spacing) - CountTolerance));

    private static LocalPoint Lerp(LocalPoint a, LocalPoint b, double t) =>
        new(a.X + ((b.X - a.X) * t), a.Y + ((b.Y - a.Y) * t), a.Z + ((b.Z - a.Z) * t));

    private static LocalPoint Add(LocalPoint a, LocalPoint b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

    private static LocalPoint Sub(LocalPoint a, LocalPoint b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    private sealed record Source(
        string Id, BarrierKind Kind, double SStart, double SEnd, LocalPoint Start, LocalPoint End, PlanarPoint Outward);

    /// <summary>
    /// An item's horizontal frame: the unit direction from start to end, the unit horizontal normal on the outward
    /// side, and the horizontal length.
    /// </summary>
    private readonly record struct Frame(PlanarPoint Along, PlanarPoint Across, double Length)
    {
        public static Frame Of(Source item)
        {
            var dx = item.End.X - item.Start.X;
            var dz = item.End.Z - item.Start.Z;
            var length = Math.Sqrt((dx * dx) + (dz * dz));
            var outwardLength = Math.Sqrt((item.Outward.X * item.Outward.X) + (item.Outward.Z * item.Outward.Z));
            var outward = outwardLength > 0
                ? new PlanarPoint(item.Outward.X / outwardLength, item.Outward.Z / outwardLength)
                : new PlanarPoint(1, 0);
            if (length == 0)
            {
                // A zero-length item: run along the boundary, at right angles to the outward direction.
                return new Frame(new PlanarPoint(-outward.Z, outward.X), outward, 0);
            }

            var along = new PlanarPoint(dx / length, dz / length);
            var across = new PlanarPoint(along.Z, -along.X);
            if ((across.X * outward.X) + (across.Z * outward.Z) < 0)
            {
                across = new PlanarPoint(-across.X, -across.Z);
            }

            return new Frame(along, across, length);
        }
    }
}
