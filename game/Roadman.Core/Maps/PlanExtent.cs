namespace Roadman.Core.Maps;

/// <summary>
/// A rectangle in Godot x/z (plan view), edges included: for example the x/z extent of the loaded tiles' meshes.
/// </summary>
public readonly record struct PlanExtent(double MinX, double MinZ, double MaxX, double MaxZ)
{
    /// <summary>True when the Godot point (<paramref name="x"/>, <paramref name="z"/>) lies inside, edges included.</summary>
    public bool Contains(double x, double z) => x >= MinX && x <= MaxX && z >= MinZ && z <= MaxZ;

    /// <summary>The smallest extent that holds both this one and <paramref name="other"/>.</summary>
    public PlanExtent Union(PlanExtent other) => new(
        Math.Min(MinX, other.MinX),
        Math.Min(MinZ, other.MinZ),
        Math.Max(MaxX, other.MaxX),
        Math.Max(MaxZ, other.MaxZ));

    /// <summary>
    /// The union of <paramref name="extents"/>, or null when there are none.
    /// </summary>
    public static PlanExtent? UnionOf(IEnumerable<PlanExtent> extents)
    {
        ArgumentNullException.ThrowIfNull(extents);
        PlanExtent? result = null;
        foreach (var extent in extents)
        {
            result = result is { } sofar ? sofar.Union(extent) : extent;
        }

        return result;
    }
}
