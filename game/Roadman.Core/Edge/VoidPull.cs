using Roadman.Core.Maps;

namespace Roadman.Core.Edge;

/// <summary>
/// The acceleration that replaces gravity for a body falling into the void (<c>docs/specs/world-edge-game.md</c>,
/// "Death sequence"): a constant pull outward, away from the slab, plus a constant pull down.
/// </summary>
public sealed class VoidPull
{
    /// <summary>Tolerance on the length of the outward vector (it must be 1 within this).</summary>
    public const double UnitTolerance = 1e-6;

    /// <summary>Tolerance on the outward vector's y (it must be horizontal within this).</summary>
    public const double HorizontalTolerance = 1e-9;

    /// <summary>Outward acceleration in m/s².</summary>
    public double OutwardAcceleration { get; set; } = 4.0;

    /// <summary>Downward acceleration in m/s²; the engine sets the project gravity.</summary>
    public double DownAcceleration { get; set; } = 9.8;

    /// <summary>
    /// <paramref name="outward"/> × <see cref="OutwardAcceleration"/> + (0, −1, 0) × <see cref="DownAcceleration"/>.
    /// </summary>
    /// <param name="outward">A unit horizontal vector pointing out of the playable area.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="outward"/> isn't finite, its length differs from 1 by more than <see cref="UnitTolerance"/>, or
    /// |y| is above <see cref="HorizontalTolerance"/>.
    /// </exception>
    public LocalPoint Acceleration(LocalPoint outward)
    {
        if (!double.IsFinite(outward.X) || !double.IsFinite(outward.Y) || !double.IsFinite(outward.Z))
        {
            throw new ArgumentException($"outward must be finite, is {outward}", nameof(outward));
        }

        var length = Math.Sqrt((outward.X * outward.X) + (outward.Y * outward.Y) + (outward.Z * outward.Z));
        if (Math.Abs(length - 1) > UnitTolerance)
        {
            throw new ArgumentException($"outward must be a unit vector, has length {length}", nameof(outward));
        }

        if (Math.Abs(outward.Y) > HorizontalTolerance)
        {
            throw new ArgumentException($"outward must be horizontal, has y {outward.Y}", nameof(outward));
        }

        return new LocalPoint(
            outward.X * OutwardAcceleration,
            (outward.Y * OutwardAcceleration) - DownAcceleration,
            outward.Z * OutwardAcceleration);
    }
}
