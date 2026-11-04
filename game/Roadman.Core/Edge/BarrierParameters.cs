namespace Roadman.Core.Edge;

/// <summary>
/// The tunable sizes of the barrier pieces (<c>docs/specs/world-edge-game.md</c>, "Barriers"); the barrier nodes copy
/// their exports into it. Every value is checked when it is set: heights in (0, <see cref="MaxHeight"/>], spacings and
/// <see cref="StrandCount"/> and <see cref="BoardRows"/> above 0, every other size at least 0, all finite.
/// </summary>
/// <remarks>
/// The defaults keep the rules <see cref="BarrierLayout"/> promises: the widest piece reaches
/// <see cref="StandDepth"/> / 2 = 0.25 m outward and inward of the line, well under the 1.0 m outward and 2.0 m inward
/// limits; the collision boxes reach <see cref="CollisionThickness"/> / 2 = 0.1 m.
/// </remarks>
public sealed record BarrierParameters
{
    /// <summary>The decided maximum height of any barrier above its base (ADR 0016), metres.</summary>
    public const double MaxHeight = 0.9;

    private readonly double _roadblockHeight = 0.9;
    private readonly double _landFenceHeight = 0.9;
    private readonly double _waterFenceHeight = 0.9;
    private readonly double _postSpacing = 2.5;
    private readonly double _standSpacing = 3.0;
    private readonly double _postWidth = 0.08;
    private readonly double _postDepthBelow = 0.3;
    private readonly int _strandCount = 3;
    private readonly double _strandThickness = 0.02;
    private readonly double _floatSize = 0.2;
    private readonly double _floatSpacing = 1.0;
    private readonly double _floatLineHeight = 0.1;
    private readonly double _floatLineThickness = 0.02;
    private readonly double _standWidth = 0.08;
    private readonly double _standDepth = 0.5;
    private readonly int _boardRows = 2;
    private readonly double _boardHeight = 0.2;
    private readonly double _boardGap = 0.2;
    private readonly double _boardThickness = 0.04;
    private readonly double _stripeLength = 0.5;
    private readonly double _standGroundInset = 0.05;
    private readonly double _collisionThickness = 0.2;
    private readonly double _collisionDepthBelow = 0.3;
    private readonly double _jointOverlap = 0.1;

    /// <summary>Roadblock height above the ground under its stands, metres.</summary>
    public double RoadblockHeight { get => _roadblockHeight; init => _roadblockHeight = Height(value, nameof(RoadblockHeight)); }

    /// <summary>Land fence height above its line, metres.</summary>
    public double LandFenceHeight { get => _landFenceHeight; init => _landFenceHeight = Height(value, nameof(LandFenceHeight)); }

    /// <summary>Water fence height above its line, metres.</summary>
    public double WaterFenceHeight { get => _waterFenceHeight; init => _waterFenceHeight = Height(value, nameof(WaterFenceHeight)); }

    /// <summary>Maximum distance between fence posts (land and water), metres.</summary>
    public double PostSpacing { get => _postSpacing; init => _postSpacing = Positive(value, nameof(PostSpacing)); }

    /// <summary>Maximum distance between roadblock stands, metres.</summary>
    public double StandSpacing { get => _standSpacing; init => _standSpacing = Positive(value, nameof(StandSpacing)); }

    /// <summary>Fence post width, along and across the line, metres.</summary>
    public double PostWidth { get => _postWidth; init => _postWidth = Size(value, nameof(PostWidth)); }

    /// <summary>How far a fence post reaches below the line, metres.</summary>
    public double PostDepthBelow { get => _postDepthBelow; init => _postDepthBelow = Size(value, nameof(PostDepthBelow)); }

    /// <summary>Barbed-wire strands of a land fence, evenly up to its height (the top strand's top at the height).</summary>
    public int StrandCount { get => _strandCount; init => _strandCount = PositiveCount(value, nameof(StrandCount)); }

    /// <summary>Strand thickness (height and depth), metres.</summary>
    public double StrandThickness { get => _strandThickness; init => _strandThickness = Size(value, nameof(StrandThickness)); }

    /// <summary>Edge of a water fence's float (a cube centred on the line; shorter along a short line), metres.</summary>
    public double FloatSize { get => _floatSize; init => _floatSize = Size(value, nameof(FloatSize)); }

    /// <summary>Target distance between floats, metres.</summary>
    public double FloatSpacing { get => _floatSpacing; init => _floatSpacing = Positive(value, nameof(FloatSpacing)); }

    /// <summary>Height of the float line's centre above the fence line, metres.</summary>
    public double FloatLineHeight { get => _floatLineHeight; init => _floatLineHeight = Size(value, nameof(FloatLineHeight)); }

    /// <summary>Float line thickness (height and depth), metres.</summary>
    public double FloatLineThickness { get => _floatLineThickness; init => _floatLineThickness = Size(value, nameof(FloatLineThickness)); }

    /// <summary>Roadblock stand width along the span, metres.</summary>
    public double StandWidth { get => _standWidth; init => _standWidth = Size(value, nameof(StandWidth)); }

    /// <summary>Roadblock stand depth across the span (its foot), centred on the line, metres.</summary>
    public double StandDepth { get => _standDepth; init => _standDepth = Size(value, nameof(StandDepth)); }

    /// <summary>Board rows of a roadblock, from the top down.</summary>
    public int BoardRows { get => _boardRows; init => _boardRows = PositiveCount(value, nameof(BoardRows)); }

    /// <summary>Height of one board, metres.</summary>
    public double BoardHeight { get => _boardHeight; init => _boardHeight = Size(value, nameof(BoardHeight)); }

    /// <summary>Vertical gap between board rows, metres.</summary>
    public double BoardGap { get => _boardGap; init => _boardGap = Size(value, nameof(BoardGap)); }

    /// <summary>Board thickness across the span, metres.</summary>
    public double BoardThickness { get => _boardThickness; init => _boardThickness = Size(value, nameof(BoardThickness)); }

    /// <summary>Target length of one red or white stripe of a board, metres (a bay gets a whole number of stripes).</summary>
    public double StripeLength { get => _stripeLength; init => _stripeLength = Positive(value, nameof(StripeLength)); }

    /// <summary>
    /// How far inward of the boundary a roadblock stand's ground is looked up again when nothing is found right under
    /// it, metres (<see cref="BarrierLayout.StandGround"/>). The terrain and road meshes end exactly on the boundary
    /// ring, where the stands are, so a vertical ray there can miss by rounding.
    /// </summary>
    public double StandGroundInset { get => _standGroundInset; init => _standGroundInset = Size(value, nameof(StandGroundInset)); }

    /// <summary>Collision box thickness across the line, centred on it, metres.</summary>
    public double CollisionThickness { get => _collisionThickness; init => _collisionThickness = Size(value, nameof(CollisionThickness)); }

    /// <summary>How far a collision box reaches below the line (fences) or the ground under the stands, metres.</summary>
    public double CollisionDepthBelow { get => _collisionDepthBelow; init => _collisionDepthBelow = Size(value, nameof(CollisionDepthBelow)); }

    /// <summary>How far collision boxes reach past both ends of an item, so neighbouring items overlap, metres.</summary>
    public double JointOverlap { get => _jointOverlap; init => _jointOverlap = Size(value, nameof(JointOverlap)); }

    private static double Height(double value, string name)
    {
        if (!double.IsFinite(value) || value <= 0 || value > MaxHeight)
        {
            throw new ArgumentException($"{name} must be in (0, {MaxHeight}], is {value}", name);
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

    private static int PositiveCount(int value, string name)
    {
        if (value <= 0)
        {
            throw new ArgumentException($"{name} must be above 0, is {value}", name);
        }

        return value;
    }

    private static double Size(double value, string name)
    {
        if (!double.IsFinite(value) || value < 0)
        {
            throw new ArgumentException($"{name} must be finite and at least 0, is {value}", name);
        }

        return value;
    }
}
