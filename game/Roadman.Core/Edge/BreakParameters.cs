namespace Roadman.Core.Edge;

/// <summary>
/// When a barrier item breaks and how heavy its loose pieces are (<c>docs/specs/world-edge-game.md</c>, "Breaking");
/// the <c>Breaking</c> group of the barrier settings is copied into it. Every value is checked when it is set:
/// <see cref="BreakSpeed"/> and <see cref="SweepMargin"/> finite and at least 0, <see cref="PieceDensity"/> and
/// <see cref="MinPieceMass"/> finite and above 0.
/// </summary>
public sealed record BreakParameters
{
    private readonly double _breakSpeed = 3.0;
    private readonly double _pieceDensity = 300.0;
    private readonly double _minPieceMass = 0.5;
    private readonly double _sweepMargin = 0.04;

    /// <summary>The breaker speed at or above which an item it touches breaks, m/s.</summary>
    public double BreakSpeed { get => _breakSpeed; init => _breakSpeed = AtLeastZero(value, nameof(BreakSpeed)); }

    /// <summary>Density of a loose piece, kg/m³.</summary>
    public double PieceDensity { get => _pieceDensity; init => _pieceDensity = Positive(value, nameof(PieceDensity)); }

    /// <summary>The lightest a loose piece gets, kg (thin strands would otherwise weigh grams).</summary>
    public double MinPieceMass { get => _minPieceMass; init => _minPieceMass = Positive(value, nameof(MinPieceMass)); }

    /// <summary>Margin of a breaker's sweep queries against the unbroken items, m.</summary>
    public double SweepMargin { get => _sweepMargin; init => _sweepMargin = AtLeastZero(value, nameof(SweepMargin)); }

    /// <summary>
    /// Whether a breaker moving at <paramref name="speed"/> (m/s; the item is static, so this is the relative speed)
    /// breaks an item it touches: at least <see cref="BreakSpeed"/>. False for a speed that isn't finite.
    /// </summary>
    public bool Breaks(double speed) => double.IsFinite(speed) && speed >= BreakSpeed;

    /// <summary>
    /// The volume of a loose piece of kind <paramref name="kind"/> whose box has the edges <paramref name="sizeX"/>
    /// (along), <paramref name="sizeY"/> (up) and <paramref name="sizeZ"/> (across), m³: a cylinder for posts (diameter
    /// the larger of x and z), an ellipsoid for floats, a box for every other kind. Negative or non-finite edges count
    /// as 0.
    /// </summary>
    public static double PieceVolume(PieceKind kind, double sizeX, double sizeY, double sizeZ)
    {
        var x = Edge(sizeX);
        var y = Edge(sizeY);
        var z = Edge(sizeZ);
        return kind switch
        {
            PieceKind.Post => Math.PI * Math.Pow(Math.Max(x, z) / 2, 2) * y,
            PieceKind.Float => 4.0 / 3.0 * Math.PI * (x / 2) * (y / 2) * (z / 2),
            _ => x * y * z,
        };
    }

    /// <summary>
    /// The mass of a loose piece: <see cref="PieceVolume"/> × <see cref="PieceDensity"/>, at least
    /// <see cref="MinPieceMass"/>, kg.
    /// </summary>
    public double PieceMass(PieceKind kind, double sizeX, double sizeY, double sizeZ) =>
        Math.Max(MinPieceMass, PieceVolume(kind, sizeX, sizeY, sizeZ) * PieceDensity);

    private static double Edge(double value) => double.IsFinite(value) && value > 0 ? value : 0;

    private static double AtLeastZero(double value, string name)
    {
        if (!double.IsFinite(value) || value < 0)
        {
            throw new ArgumentException($"{name} must be finite and at least 0, is {value}", name);
        }

        return value;
    }

    private static double Positive(double value, string name)
    {
        if (!double.IsFinite(value) || value <= 0)
        {
            throw new ArgumentException($"{name} must be finite and above 0, is {value}", name);
        }

        return value;
    }
}
