using Roadman.Core.Maps;

namespace Roadman.Core.Player;

/// <summary>
/// Draws spawn points uniformly by area over open ground (<see cref="SpawnArea.OpenGround"/>), holes excluded, so a
/// larger polygon is picked more often. The sequence depends only on the polygons and the injected
/// <see cref="Random"/>: the same seed always gives the same points. Every point lies inside the open ground; checks
/// that need the engine (collision under and around the point) retry by calling <see cref="Next"/> again.
/// </summary>
/// <remarks>
/// Method: pick a polygon with probability proportional to its area, then draw uniform points in its exterior's
/// bounding box until one lies inside the polygon (inside the exterior, outside every hole). Each accepted point is
/// uniform over that polygon, so the result is uniform over the whole open ground.
/// </remarks>
public sealed class SpawnPicker
{
    /// <summary>
    /// Bounding-box draws per <see cref="Next"/> before it gives up. A valid polygon with positive area accepts a draw
    /// with probability area / box area, so this is only reached for a degenerate sliver.
    /// </summary>
    public const int MaxDrawsPerPoint = 1_000_000;

    private readonly PlanarPolygon[] _polygons;
    private readonly double[] _cumulativeArea;
    private readonly (double MinX, double MinZ, double MaxX, double MaxZ)[] _boxes;
    private readonly Random _random;

    /// <param name="openGround">
    /// The open-ground polygons: each exterior and hole ring closed (last point repeats the first), with at least 4
    /// points and finite coordinates.
    /// </param>
    /// <param name="random">The random source; seed it for a repeatable sequence.</param>
    /// <exception cref="ArgumentException">
    /// No polygons, a ring that isn't closed or has fewer than 4 points, a non-finite coordinate, a polygon whose holes
    /// are larger than its exterior, or a total area of 0.
    /// </exception>
    public SpawnPicker(IReadOnlyList<PlanarPolygon> openGround, Random random)
    {
        ArgumentNullException.ThrowIfNull(openGround);
        ArgumentNullException.ThrowIfNull(random);
        if (openGround.Count == 0)
        {
            throw new ArgumentException("open ground has no polygons", nameof(openGround));
        }

        _random = random;
        _polygons = new PlanarPolygon[openGround.Count];
        _cumulativeArea = new double[openGround.Count];
        _boxes = new (double, double, double, double)[openGround.Count];
        var total = 0.0;
        for (var i = 0; i < openGround.Count; i++)
        {
            var polygon = openGround[i] ?? throw new ArgumentException($"open ground polygon {i} is null", nameof(openGround));
            CheckRing(polygon.Exterior, $"polygon {i} exterior");
            for (var h = 0; h < polygon.Holes.Count; h++)
            {
                CheckRing(polygon.Holes[h], $"polygon {i} hole {h}");
            }

            var area = polygon.Area;
            if (area < 0)
            {
                throw new ArgumentException($"open ground polygon {i} has holes larger than its exterior (area {area})", nameof(openGround));
            }

            total += area;
            _polygons[i] = polygon;
            _cumulativeArea[i] = total;
            _boxes[i] = (
                polygon.Exterior.Min(p => p.X),
                polygon.Exterior.Min(p => p.Z),
                polygon.Exterior.Max(p => p.X),
                polygon.Exterior.Max(p => p.Z));
        }

        if (!(total > 0))
        {
            throw new ArgumentException("open ground has zero area", nameof(openGround));
        }

        TotalArea = total;
    }

    /// <summary>Draws from <paramref name="spawnArea"/>'s open ground.</summary>
    public SpawnPicker(SpawnArea spawnArea, Random random)
        : this((spawnArea ?? throw new ArgumentNullException(nameof(spawnArea))).OpenGround, random)
    {
    }

    /// <summary>The open ground's area in square metres (exteriors minus holes), above 0.</summary>
    public double TotalArea { get; }

    /// <summary>The next point, uniform by area over the open ground.</summary>
    /// <exception cref="InvalidOperationException">
    /// <see cref="MaxDrawsPerPoint"/> box draws all missed the picked polygon (a degenerate sliver).
    /// </exception>
    public PlanarPoint Next()
    {
        var index = PickPolygon();
        var polygon = _polygons[index];
        var (minX, minZ, maxX, maxZ) = _boxes[index];
        for (var draw = 0; draw < MaxDrawsPerPoint; draw++)
        {
            var x = minX + (_random.NextDouble() * (maxX - minX));
            var z = minZ + (_random.NextDouble() * (maxZ - minZ));
            if (polygon.Contains(x, z))
            {
                return new PlanarPoint(x, z);
            }
        }

        throw new InvalidOperationException(
            $"no point found in open ground polygon {index} after {MaxDrawsPerPoint} draws; it is too thin for its bounding box");
    }

    /// <summary>
    /// True when (<paramref name="x"/>, <paramref name="z"/>) is in any of <paramref name="openGround"/>'s polygons
    /// (inside an exterior, outside its holes).
    /// </summary>
    public static bool Contains(IReadOnlyList<PlanarPolygon> openGround, double x, double z)
    {
        ArgumentNullException.ThrowIfNull(openGround);
        return openGround.Any(polygon => polygon.Contains(x, z));
    }

    private int PickPolygon()
    {
        // Zero-area polygons have the same cumulative value as the one before, so they are never picked.
        var target = _random.NextDouble() * TotalArea;
        for (var i = 0; i < _cumulativeArea.Length; i++)
        {
            if (target < _cumulativeArea[i])
            {
                return i;
            }
        }

        // Rounding: fall back on the last polygon with area.
        for (var i = _cumulativeArea.Length - 1; i > 0; i--)
        {
            if (_cumulativeArea[i] > _cumulativeArea[i - 1])
            {
                return i;
            }
        }

        return 0;
    }

    private static void CheckRing(IReadOnlyList<PlanarPoint>? ring, string what)
    {
        if (ring is null || ring.Count < 4)
        {
            throw new ArgumentException($"open ground {what} needs at least 4 points", "openGround");
        }

        if (ring[0] != ring[^1])
        {
            throw new ArgumentException($"open ground {what} is not closed (the last point must repeat the first)", "openGround");
        }

        foreach (var point in ring)
        {
            if (!double.IsFinite(point.X) || !double.IsFinite(point.Z))
            {
                throw new ArgumentException($"open ground {what} has a non-finite coordinate", "openGround");
            }
        }
    }
}
