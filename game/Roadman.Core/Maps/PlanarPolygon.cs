namespace Roadman.Core.Maps;

/// <summary>
/// A polygon on the ground plane (Godot local metres, x east, z south): an exterior ring and its holes, each closed
/// (the last point repeats the first). Used for the open ground of <c>boundary.json</c>'s spawn area.
/// </summary>
/// <param name="Exterior">The outer ring, closed.</param>
/// <param name="Holes">Rings cut out of it, each closed.</param>
public sealed record PlanarPolygon(IReadOnlyList<PlanarPoint> Exterior, IReadOnlyList<IReadOnlyList<PlanarPoint>> Holes)
{
    /// <summary>Area in square metres: the exterior's minus the holes'.</summary>
    public double Area => Math.Abs(SignedArea(Exterior)) - Holes.Sum(hole => Math.Abs(SignedArea(hole)));

    /// <summary>
    /// True when (<paramref name="x"/>, <paramref name="z"/>) is inside the exterior and outside every hole
    /// (even-odd rule; a point exactly on a ring may go either way).
    /// </summary>
    public bool Contains(double x, double z) =>
        RingContains(Exterior, x, z) && !Holes.Any(hole => RingContains(hole, x, z));

    /// <summary>Signed area of a closed ring (shoelace formula); positive when counter-clockwise in (x, z).</summary>
    internal static double SignedArea(IReadOnlyList<PlanarPoint> ring)
    {
        var sum = 0.0;
        for (var i = 0; i < ring.Count - 1; i++)
        {
            sum += (ring[i].X * ring[i + 1].Z) - (ring[i + 1].X * ring[i].Z);
        }

        return sum / 2;
    }

    /// <summary>Even-odd test of a point against a closed ring.</summary>
    internal static bool RingContains(IReadOnlyList<PlanarPoint> ring, double x, double z)
    {
        var inside = false;
        for (var i = 0; i < ring.Count - 1; i++)
        {
            var a = ring[i];
            var b = ring[i + 1];
            if ((a.Z > z) != (b.Z > z) && x < a.X + ((z - a.Z) * (b.X - a.X) / (b.Z - a.Z)))
            {
                inside = !inside;
            }
        }

        return inside;
    }
}
