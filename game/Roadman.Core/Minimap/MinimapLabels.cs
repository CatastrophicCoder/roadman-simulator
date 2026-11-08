using Roadman.Core.Maps;

namespace Roadman.Core.Minimap;

/// <summary>
/// An oriented rectangle on the panel: <see cref="Centre"/>, half extents along its own axes, and
/// <see cref="Angle"/>, the Godot 2D rotation (radians, clockwise on screen) of its width axis from panel +x.
/// </summary>
public readonly record struct MinimapRect(PanelPoint Centre, double HalfWidth, double HalfHeight, double Angle)
{
    /// <summary>An axis-aligned rectangle from its centre and full size (for the arrow and the north indicator).</summary>
    public static MinimapRect AxisAligned(PanelPoint centre, double width, double height) =>
        new(centre, width / 2, height / 2, 0);

    /// <summary>The four corners, in order round the rectangle.</summary>
    public PanelPoint[] Corners()
    {
        var (sin, cos) = Math.SinCos(Angle);
        var ax = new PanelPoint(cos * HalfWidth, sin * HalfWidth);
        var ay = new PanelPoint(-sin * HalfHeight, cos * HalfHeight);
        return [Centre - ax - ay, Centre + ax - ay, Centre + ax + ay, Centre - ax + ay];
    }

    /// <summary>The same rectangle grown by <paramref name="margin"/> on every side.</summary>
    public MinimapRect Inflate(double margin) => this with { HalfWidth = HalfWidth + margin, HalfHeight = HalfHeight + margin };

    /// <summary>
    /// True when the two rectangles share an area (separating axis test on the four edge directions); rectangles that
    /// only touch don't overlap.
    /// </summary>
    public bool Overlaps(MinimapRect other)
    {
        var a = Corners();
        var b = other.Corners();
        return !Separated(a, b, Angle) && !Separated(a, b, other.Angle);
    }

    /// <summary>True when every corner lies at most <paramref name="radius"/> from the panel centre.</summary>
    /// <remarks>Allocates nothing (the minimap calls it for every stored label each frame).</remarks>
    public bool IsInsideCircle(double radius)
    {
        if (!(radius >= 0))
        {
            return false;
        }

        var (sin, cos) = Math.SinCos(Angle);
        var axX = cos * HalfWidth;
        var axY = sin * HalfWidth;
        var ayX = -sin * HalfHeight;
        var ayY = cos * HalfHeight;
        var limit = radius * radius;
        return Within(Centre.X - axX - ayX, Centre.Y - axY - ayY, limit)
            && Within(Centre.X + axX - ayX, Centre.Y + axY - ayY, limit)
            && Within(Centre.X + axX + ayX, Centre.Y + axY + ayY, limit)
            && Within(Centre.X - axX + ayX, Centre.Y - axY + ayY, limit);
    }

    private static bool Within(double x, double y, double limitSquared) => (x * x) + (y * y) <= limitSquared;

    private static bool Separated(PanelPoint[] a, PanelPoint[] b, double angle)
    {
        var (sin, cos) = Math.SinCos(angle);
        PanelPoint[] axes = [new(cos, sin), new(-sin, cos)];
        foreach (var axis in axes)
        {
            var (aMin, aMax) = Project(a, axis);
            var (bMin, bMax) = Project(b, axis);
            if (aMax <= bMin || bMax <= aMin)
            {
                return true;
            }
        }

        return false;
    }

    private static (double Min, double Max) Project(PanelPoint[] corners, PanelPoint axis)
    {
        var min = double.PositiveInfinity;
        var max = double.NegativeInfinity;
        foreach (var corner in corners)
        {
            var value = (corner.X * axis.X) + (corner.Y * axis.Y);
            min = Math.Min(min, value);
            max = Math.Max(max, value);
        }

        return (min, max);
    }
}

/// <summary>One visible, clipped piece of a labelled road on the panel.</summary>
/// <param name="Text">The label text (<see cref="MinimapRoad.LabelText"/>).</param>
/// <param name="Group">The road's class group.</param>
/// <param name="Points">The piece in panel coordinates, at least 2 points.</param>
public sealed record MinimapLabelSource(string Text, RoadGroup Group, IReadOnlyList<PanelPoint> Points);

/// <summary>Tuning of <see cref="MinimapLabels.Place"/>, in panel pixels (base units).</summary>
/// <param name="Padding">A stretch must be at least the text width plus this long.</param>
/// <param name="StraightTolerance">
/// How far, as a fraction of the text height, a point of a stretch may lie from the stretch's chord for it to count as
/// straight enough.
/// </param>
/// <param name="RimMargin">Labels stay this far inside the rim.</param>
/// <param name="Gap">The least space between a label and another label, the arrow or the north indicator.</param>
public sealed record MinimapLabelOptions(
    double Padding = 8,
    double StraightTolerance = 0.3,
    double RimMargin = 2,
    double Gap = 2)
{
    public static MinimapLabelOptions Default { get; } = new();
}

/// <summary>A placed label on the panel.</summary>
/// <param name="Text">The label text.</param>
/// <param name="Group">The best class group among the label's roads.</param>
/// <param name="Anchor">The centre of the text box, on the middle of <paramref name="Stretch"/>'s chord.</param>
/// <param name="Angle">
/// The Godot 2D rotation of the text (radians, clockwise on screen; 0 is horizontal, left to right), in [−π/2, π/2]:
/// the text's up vector never points into the lower half of the panel.
/// </param>
/// <param name="Width">The measured text width.</param>
/// <param name="Height">The measured text height.</param>
/// <param name="Stretch">The section of the road polyline the label runs along.</param>
public sealed record MinimapLabelPlacement(
    string Text,
    RoadGroup Group,
    PanelPoint Anchor,
    double Angle,
    double Width,
    double Height,
    IReadOnlyList<PanelPoint> Stretch)
{
    /// <summary>The text box on the panel.</summary>
    public MinimapRect Rect => new(Anchor, Width / 2, Height / 2, Angle);

    /// <summary>The label at its ground position, to re-project each frame until the next placement.</summary>
    public MinimapWorldLabel ToWorld(MinimapProjection projection)
    {
        ArgumentNullException.ThrowIfNull(projection);
        return new MinimapWorldLabel(
            Text, Group, projection.ToWorld(Anchor), MinimapProjection.NormaliseAngle(Angle - projection.NorthBearing), Width, Height);
    }
}

/// <summary>
/// A label stored at its ground position (<c>docs/specs/minimap.md</c>, "Labels": between recomputes labels are drawn
/// at their stored world positions, re-projected each frame).
/// </summary>
/// <param name="Text">The label text.</param>
/// <param name="Group">Its class group.</param>
/// <param name="Anchor">The ground point at the text centre.</param>
/// <param name="MapAngle">The text's rotation on a north-up map, radians.</param>
/// <param name="Width">The measured text width.</param>
/// <param name="Height">The measured text height.</param>
public readonly record struct MinimapWorldLabel(
    string Text,
    RoadGroup Group,
    PlanarPoint Anchor,
    double MapAngle,
    double Width,
    double Height)
{
    /// <summary>The text centre on the panel of <paramref name="projection"/>.</summary>
    public PanelPoint AnchorIn(MinimapProjection projection)
    {
        ArgumentNullException.ThrowIfNull(projection);
        return projection.ToPanel(Anchor);
    }

    /// <summary>The text's rotation on the panel of <paramref name="projection"/>, kept upright.</summary>
    public double AngleIn(MinimapProjection projection)
    {
        ArgumentNullException.ThrowIfNull(projection);
        return MinimapLabels.Upright(MapAngle + projection.NorthBearing);
    }
}

/// <summary>
/// Street label placement on the minimap (<c>docs/specs/minimap.md</c>, "Labels"). Pieces are grouped by text and
/// joined where they meet; each text gets at most one label, along the longest straight-enough stretch of its
/// visible roads that is at least the text width plus <see cref="MinimapLabelOptions.Padding"/> long and whose text
/// box, centred on the stretch, lies inside the circle (<see cref="MinimapLabelOptions.RimMargin"/> from the rim).
/// Candidates are placed in order of class group (main roads first), then visible length (longest first), then text
/// (ordinal); one that would overlap the avoided rectangles or an earlier label (with
/// <see cref="MinimapLabelOptions.Gap"/>) is dropped. Deterministic for the same input.
/// </summary>
public static class MinimapLabels
{
    private const double JoinTolerance = 1e-6;

    /// <summary>
    /// Whether the labels must be placed again for the frame of <paramref name="current"/>: always without a placement
    /// (<paramref name="placedWith"/> null), else when the centre has moved more than <paramref name="distanceM"/> metres
    /// or the heading has turned more than <paramref name="degrees"/> (either way round, across ±180°) since the
    /// placement made with <paramref name="placedWith"/>.
    /// </summary>
    public static bool NeedsPlacement(MinimapProjection? placedWith, MinimapProjection current, double distanceM, double degrees)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (placedWith is null)
        {
            return true;
        }

        var dx = current.CentreX - placedWith.CentreX;
        var dz = current.CentreZ - placedWith.CentreZ;
        var turn = Math.Abs(MinimapProjection.NormaliseAngle(current.HeadingYaw - placedWith.HeadingYaw));
        return (dx * dx) + (dz * dz) > distanceM * distanceM
            || turn > degrees * Math.PI / 180;
    }

    /// <summary>
    /// The label sources of <paramref name="roads"/> on the panel of <paramref name="projection"/>: every road with a
    /// label text, projected and clipped to the circle, one source per visible piece, in the order of
    /// <paramref name="roads"/>.
    /// </summary>
    public static List<MinimapLabelSource> Collect(IEnumerable<MinimapRoad> roads, MinimapProjection projection)
    {
        ArgumentNullException.ThrowIfNull(roads);
        ArgumentNullException.ThrowIfNull(projection);
        var sources = new List<MinimapLabelSource>();
        var projected = new List<PanelPoint>();
        var pieces = new List<IReadOnlyList<PanelPoint>>();
        foreach (var road in roads)
        {
            if (string.IsNullOrWhiteSpace(road.LabelText))
            {
                continue;
            }

            projected.Clear();
            foreach (var point in road.Points)
            {
                projected.Add(projection.ToPanel(point));
            }

            pieces.Clear();
            MinimapClip.Clip(projected, projection.RadiusPx, pieces);
            foreach (var piece in pieces)
            {
                sources.Add(new MinimapLabelSource(road.LabelText, road.Group, piece));
            }
        }

        return sources;
    }

    /// <summary>Places the labels; see the class summary for the rules.</summary>
    /// <param name="sources">The visible, clipped pieces with their texts (blank texts are skipped).</param>
    /// <param name="measure">The text's width and height in panel pixels.</param>
    /// <param name="radius">The panel radius.</param>
    /// <param name="avoid">Rectangles labels must not overlap: the player arrow and the north indicator.</param>
    /// <param name="options">Tuning; <see cref="MinimapLabelOptions.Default"/> when null.</param>
    /// <returns>The placed labels, in placement order.</returns>
    public static List<MinimapLabelPlacement> Place(
        IReadOnlyList<MinimapLabelSource> sources,
        Func<string, (double Width, double Height)> measure,
        double radius,
        IReadOnlyList<MinimapRect> avoid,
        MinimapLabelOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(measure);
        ArgumentNullException.ThrowIfNull(avoid);
        if (!(radius > 0))
        {
            throw new ArgumentOutOfRangeException(nameof(radius), radius, "must be above 0");
        }

        options ??= MinimapLabelOptions.Default;
        var gapHalf = Math.Max(options.Gap, 0) / 2;

        var groups = new Dictionary<string, TextGroup>(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            if (string.IsNullOrWhiteSpace(source.Text) || source.Points.Count < 2)
            {
                continue;
            }

            if (!groups.TryGetValue(source.Text, out var group))
            {
                group = new TextGroup(source.Text, source.Group);
                groups.Add(source.Text, group);
            }

            group.Add(source);
        }

        var ordered = groups.Values
            .OrderBy(group => group.Group)
            .ThenByDescending(group => group.VisibleLength)
            .ThenBy(group => group.Text, StringComparer.Ordinal);

        var placed = new List<MinimapLabelPlacement>();
        var taken = avoid.Select(rect => rect.Inflate(gapHalf)).ToList();
        foreach (var group in ordered)
        {
            var (width, height) = measure(group.Text);
            if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0)
            {
                continue;
            }

            var candidate = Candidate(group, width, height, radius, options);
            if (candidate is null)
            {
                continue;
            }

            var padded = candidate.Rect.Inflate(gapHalf);
            if (taken.Any(rect => rect.Overlaps(padded)))
            {
                continue;
            }

            taken.Add(padded);
            placed.Add(candidate);
        }

        return placed;
    }

    /// <summary>
    /// <paramref name="angle"/> (a Godot 2D rotation of text) turned by π when the text would read upside down, so its
    /// up vector never points into the lower half of the panel; a vertical text reads from bottom to top. In [−π/2, π/2].
    /// </summary>
    public static double Upright(double angle)
    {
        var (sin, cos) = Math.SinCos(angle);
        var flip = cos < -1e-12 || (Math.Abs(cos) <= 1e-12 && sin > 0);
        return MinimapProjection.NormaliseAngle(flip ? angle + Math.PI : angle);
    }

    private static MinimapLabelPlacement? Candidate(TextGroup group, double width, double height, double radius, MinimapLabelOptions options)
    {
        var needed = width + options.Padding;
        var tolerance = Math.Max(options.StraightTolerance, 0) * height;
        var stretches = new List<(int Chain, int Start, int End, double Length)>();
        for (var c = 0; c < group.Chains.Count; c++)
        {
            var chain = group.Chains[c];
            for (var i = 0; i + 1 < chain.Count; i++)
            {
                var best = -1;
                for (var j = i + 1; j < chain.Count; j++)
                {
                    if (!IsStraight(chain, i, j, tolerance))
                    {
                        break;
                    }

                    best = j;
                }

                if (best > i)
                {
                    var length = PanelPoint.Distance(chain[i], chain[best]);
                    if (length >= needed)
                    {
                        stretches.Add((c, i, best, length));
                    }
                }
            }
        }

        foreach (var (c, i, j, _) in stretches
            .OrderByDescending(s => s.Length)
            .ThenBy(s => s.Chain)
            .ThenBy(s => s.Start))
        {
            var chain = group.Chains[c];
            var a = chain[i];
            var b = chain[j];
            var anchor = new PanelPoint((a.X + b.X) / 2, (a.Y + b.Y) / 2);
            var angle = Upright(Math.Atan2(b.Y - a.Y, b.X - a.X));
            var placement = new MinimapLabelPlacement(
                group.Text, group.Group, anchor, angle, width, height, chain.Skip(i).Take(j - i + 1).ToList());
            if (placement.Rect.IsInsideCircle(radius - Math.Max(options.RimMargin, 0)))
            {
                return placement;
            }
        }

        return null;
    }

    /// <summary>True when every point between <paramref name="i"/> and <paramref name="j"/> lies near the chord and runs along it.</summary>
    private static bool IsStraight(List<PanelPoint> chain, int i, int j, double tolerance)
    {
        var a = chain[i];
        var chord = chain[j] - a;
        var length = chord.Length;
        if (length == 0)
        {
            return false;
        }

        var ux = chord.X / length;
        var uy = chord.Y / length;
        var along = 0.0;
        for (var k = i + 1; k <= j; k++)
        {
            var d = chain[k] - a;
            var t = (d.X * ux) + (d.Y * uy);
            var offset = Math.Abs((d.X * uy) - (d.Y * ux));
            if (offset > tolerance || t < along - tolerance)
            {
                return false;
            }

            along = Math.Max(along, t);
        }

        return true;
    }

    /// <summary>Every piece of one label text, joined into chains where pieces meet end to end.</summary>
    private sealed class TextGroup(string text, RoadGroup group)
    {
        public string Text { get; } = text;

        public RoadGroup Group { get; private set; } = group;

        public double VisibleLength { get; private set; }

        public List<List<PanelPoint>> Chains { get; } = [];

        public void Add(MinimapLabelSource source)
        {
            if (source.Group < Group)
            {
                Group = source.Group;
            }

            for (var k = 0; k + 1 < source.Points.Count; k++)
            {
                VisibleLength += PanelPoint.Distance(source.Points[k], source.Points[k + 1]);
            }

            var piece = source.Points.ToList();
            while (true)
            {
                var joined = false;
                for (var c = 0; c < Chains.Count && !joined; c++)
                {
                    var chain = Chains[c];
                    if (Same(chain[^1], piece[0]))
                    {
                        piece = [.. chain, .. piece.Skip(1)];
                    }
                    else if (Same(chain[^1], piece[^1]))
                    {
                        piece = [.. chain, .. Enumerable.Reverse(piece).Skip(1)];
                    }
                    else if (Same(chain[0], piece[^1]))
                    {
                        piece = [.. piece, .. chain.Skip(1)];
                    }
                    else if (Same(chain[0], piece[0]))
                    {
                        piece = [.. Enumerable.Reverse(piece), .. chain.Skip(1)];
                    }
                    else
                    {
                        continue;
                    }

                    Chains.RemoveAt(c);
                    joined = true;
                }

                if (!joined)
                {
                    break;
                }
            }

            Chains.Add(piece);
        }

        private static bool Same(PanelPoint a, PanelPoint b) =>
            Math.Abs(a.X - b.X) <= JoinTolerance && Math.Abs(a.Y - b.Y) <= JoinTolerance;
    }
}
