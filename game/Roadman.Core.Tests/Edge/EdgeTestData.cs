using Roadman.Core.Maps;
using Roadman.Core.Tests.Maps;

namespace Roadman.Core.Tests.Edge;

/// <summary>
/// Synthetic boundaries for the world-edge tests (<c>docs/specs/world-edge-game.md</c>, Part A), the generated packs'
/// boundaries, and a small geometry oracle (point in ring, distance to ring) that doesn't use the code under test.
/// </summary>
internal static class EdgeTestData
{
    /// <summary>
    /// A 100 m square in the packs' orientation (from the lowest-x, lowest-z vertex towards +z; negative signed area
    /// in (x, z), as Somero's ring). Segments: 0 west (x 0), 1 south (z 100), 2 east (x 100), 3 north (z 0).
    /// </summary>
    internal static readonly IReadOnlyList<PlanarPoint> Square =
        [new(0, 0), new(0, 100), new(100, 100), new(100, 0)];

    /// <summary>The same square in the opposite orientation (positive signed area in (x, z)).</summary>
    internal static readonly IReadOnlyList<PlanarPoint> SquareReversed =
        [new(0, 0), new(100, 0), new(100, 100), new(0, 100)];

    /// <summary>
    /// An L shape in the packs' orientation: the square without its x 50..100, z 0..50 quarter, so (50, 50) is a
    /// concave corner and (0, 0) a convex one. Segments and s: 0 (0,0)→(0,100) s 0..100; 1 (0,100)→(100,100)
    /// s 100..200; 2 (100,100)→(100,50) s 200..250; 3 (100,50)→(50,50) s 250..300; 4 (50,50)→(50,0) s 300..350;
    /// 5 (50,0)→(0,0) s 350..400.
    /// </summary>
    internal static readonly IReadOnlyList<PlanarPoint> LShape =
        [new(0, 0), new(0, 100), new(100, 100), new(100, 50), new(50, 50), new(50, 0)];

    internal const double CliffBottomY = 51.75;
    internal const double VoidBottomY = 31.75;
    internal const double VoidTopY = 160.5;

    /// <summary>A boundary record around <paramref name="ring"/> with the given items and prisms.</summary>
    internal static Boundary MakeBoundary(
        IReadOnlyList<PlanarPoint> ring,
        IReadOnlyList<Roadblock>? roadblocks = null,
        IReadOnlyList<Fence>? fences = null,
        IReadOnlyList<VoidPrism>? prisms = null)
    {
        var closed = ring.Append(ring[0]).ToList();
        return new Boundary(
            1,
            "somero",
            "test",
            new BoundaryRing(ring, RingLength(ring), BoundarySource.TestArea),
            roadblocks ?? [],
            fences ?? [],
            new Cliff(2.0, CliffBottomY),
            new VoidTrigger(
                VoidBottomY,
                VoidTopY,
                prisms ?? [new VoidPrism([new(-21, -21), new(-1, -21), new(-1, -1), new(-21, -1)])]),
            new SpawnArea(ring, [new PlanarPolygon(closed, [])], 1.0),
            []);
    }

    /// <summary>Every generated pack's boundary (none when no pack has been built).</summary>
    internal static IReadOnlyList<(string MapId, Boundary Boundary)> RealPacks() =>
        GeneratedPacks.WithBoundary()
            .Select(pack =>
            {
                var manifest = MapManifestReader.Parse(File.ReadAllText(pack.Manifest), pack.Manifest);
                return (pack.MapId, BoundaryReader.Parse(File.ReadAllText(pack.Boundary), manifest, pack.Boundary));
            })
            .ToList();

    internal static double RingLength(IReadOnlyList<PlanarPoint> ring)
    {
        var sum = 0.0;
        for (var i = 0; i < ring.Count; i++)
        {
            var a = ring[i];
            var b = ring[(i + 1) % ring.Count];
            sum += Math.Sqrt(((b.X - a.X) * (b.X - a.X)) + ((b.Z - a.Z) * (b.Z - a.Z)));
        }

        return sum;
    }

    /// <summary>Even-odd point-in-ring test (oracle; the ring is not closed).</summary>
    internal static bool Inside(IReadOnlyList<PlanarPoint> ring, double x, double z)
    {
        var inside = false;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
        {
            var a = ring[i];
            var b = ring[j];
            if ((a.Z > z) != (b.Z > z) && x < ((b.X - a.X) * (z - a.Z) / (b.Z - a.Z)) + a.X)
            {
                inside = !inside;
            }
        }

        return inside;
    }

    /// <summary>Distance from (x, z) to the closed ring (oracle).</summary>
    internal static double DistanceToRing(IReadOnlyList<PlanarPoint> ring, double x, double z)
    {
        var best = double.PositiveInfinity;
        for (var i = 0; i < ring.Count; i++)
        {
            var a = ring[i];
            var b = ring[(i + 1) % ring.Count];
            var dx = b.X - a.X;
            var dz = b.Z - a.Z;
            var len2 = (dx * dx) + (dz * dz);
            var t = len2 == 0 ? 0 : Math.Clamp((((x - a.X) * dx) + ((z - a.Z) * dz)) / len2, 0, 1);
            var px = a.X + (t * dx) - x;
            var pz = a.Z + (t * dz) - z;
            best = Math.Min(best, (px * px) + (pz * pz));
        }

        return Math.Sqrt(best);
    }
}
