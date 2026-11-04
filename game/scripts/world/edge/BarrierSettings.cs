using Godot;
using Roadman.Core.Edge;

namespace Roadman.Scenes.Edge;

/// <summary>
/// The tuned sizes of the world-edge barriers (<c>docs/specs/world-edge-game.md</c>, "Barriers", "Exports"), grouped
/// per barrier type in the inspector: <c>Roadblock</c>, <c>Land Fence</c> (its post values are also the water
/// fences'), <c>Water Fence</c>, <c>Collision</c> (every type) and <c>Breaking</c> (when an item breaks, and its loose
/// pieces). The properties carry the group's name as a prefix
/// (<see cref="LandFencePostSpacing"/> shows as Land Fence → Post Spacing).
/// <para>
/// <see cref="MapLoader.BarrierSettings"/> holds one, which every <see cref="Barriers"/> it creates uses, so a value set
/// there (in code, or a saved <c>.tres</c> assigned to it) lasts across map reloads. A change takes effect at the next
/// build (a map load), except the <c>Breaking</c> values, which <see cref="Barriers"/> reads each physics frame while a
/// breaker exists. <see cref="ToParameters"/> checks the layout values (<see cref="BarrierParameters"/>),
/// <see cref="ToBreakParameters"/> the breaking ones (<see cref="BreakParameters"/>).
/// </para>
/// </summary>
[GlobalClass]
public partial class BarrierSettings : Resource
{
    // ----- Roadblock -----

    /// <summary>Height of the boards' top above the ground under each stand, m (at most 0.9, ADR 0016).</summary>
    [ExportGroup("Roadblock", "Roadblock")]
    [Export(PropertyHint.Range, "0.05,0.9,0.01,suffix:m")]
    public float RoadblockHeight { get; set; } = 0.9f;

    /// <summary>Largest distance between neighbouring stands, m.</summary>
    [Export(PropertyHint.Range, "0.1,20,0.05,or_greater,suffix:m")]
    public float RoadblockStandSpacing { get; set; } = 3.0f;

    /// <summary>Stand width along the span, m.</summary>
    [Export(PropertyHint.Range, "0,1,0.005,or_greater,suffix:m")]
    public float RoadblockStandWidth { get; set; } = 0.08f;

    /// <summary>Stand depth across the span (its foot), m.</summary>
    [Export(PropertyHint.Range, "0,2,0.01,or_greater,suffix:m")]
    public float RoadblockStandDepth { get; set; } = 0.5f;

    /// <summary>
    /// How far inward of the boundary a stand's ground is looked up again when the ray right under the stand misses
    /// (the terrain and road meshes end exactly on the ring), m (<see cref="BarrierLayout.StandGround"/>).
    /// </summary>
    [Export(PropertyHint.Range, "0,0.5,0.005,suffix:m")]
    public float RoadblockStandGroundInset { get; set; } = 0.05f;

    /// <summary>Board rows, from the top down.</summary>
    [Export(PropertyHint.Range, "1,5,1,or_greater")]
    public int RoadblockBoardRows { get; set; } = 2;

    /// <summary>Height of one board, m.</summary>
    [Export(PropertyHint.Range, "0,0.9,0.01,suffix:m")]
    public float RoadblockBoardHeight { get; set; } = 0.2f;

    /// <summary>Vertical gap between board rows, m.</summary>
    [Export(PropertyHint.Range, "0,0.9,0.01,suffix:m")]
    public float RoadblockBoardGap { get; set; } = 0.2f;

    /// <summary>Board thickness across the span, m.</summary>
    [Export(PropertyHint.Range, "0,0.5,0.005,or_greater,suffix:m")]
    public float RoadblockBoardThickness { get; set; } = 0.04f;

    /// <summary>Target length of one red or white stripe, m.</summary>
    [Export(PropertyHint.Range, "0.05,5,0.05,or_greater,suffix:m")]
    public float RoadblockStripeLength { get; set; } = 0.5f;

    // ----- Land fence (posts also for water fences) -----

    /// <summary>Height above the fence line, m (at most 0.9, ADR 0016).</summary>
    [ExportGroup("Land Fence", "LandFence")]
    [Export(PropertyHint.Range, "0.05,0.9,0.01,suffix:m")]
    public float LandFenceHeight { get; set; } = 0.9f;

    /// <summary>Largest distance between fence posts, m; land and water fences.</summary>
    [Export(PropertyHint.Range, "0.1,20,0.05,or_greater,suffix:m")]
    public float LandFencePostSpacing { get; set; } = 2.5f;

    /// <summary>Post width along and across the line, m; land and water fences.</summary>
    [Export(PropertyHint.Range, "0,1,0.005,or_greater,suffix:m")]
    public float LandFencePostWidth { get; set; } = 0.08f;

    /// <summary>How far a post reaches below the line, m; land and water fences.</summary>
    [Export(PropertyHint.Range, "0,2,0.01,or_greater,suffix:m")]
    public float LandFencePostDepthBelow { get; set; } = 0.3f;

    /// <summary>Barbed-wire strands, evenly up to the height.</summary>
    [Export(PropertyHint.Range, "1,10,1,or_greater")]
    public int LandFenceStrandCount { get; set; } = 3;

    /// <summary>Strand thickness, m.</summary>
    [Export(PropertyHint.Range, "0,0.2,0.005,or_greater,suffix:m")]
    public float LandFenceStrandThickness { get; set; } = 0.02f;

    // ----- Water fence -----

    /// <summary>Height above the fence line, m (at most 0.9, ADR 0016).</summary>
    [ExportGroup("Water Fence", "WaterFence")]
    [Export(PropertyHint.Range, "0.05,0.9,0.01,suffix:m")]
    public float WaterFenceHeight { get; set; } = 0.9f;

    /// <summary>Edge of a float, m.</summary>
    [Export(PropertyHint.Range, "0,1,0.01,or_greater,suffix:m")]
    public float WaterFenceFloatSize { get; set; } = 0.2f;

    /// <summary>Target distance between floats, m.</summary>
    [Export(PropertyHint.Range, "0.1,10,0.05,or_greater,suffix:m")]
    public float WaterFenceFloatSpacing { get; set; } = 1.0f;

    /// <summary>Height of the float line's centre above the fence line, m.</summary>
    [Export(PropertyHint.Range, "0,0.9,0.01,suffix:m")]
    public float WaterFenceFloatLineHeight { get; set; } = 0.1f;

    /// <summary>Float line thickness, m.</summary>
    [Export(PropertyHint.Range, "0,0.2,0.005,or_greater,suffix:m")]
    public float WaterFenceFloatLineThickness { get; set; } = 0.02f;

    // ----- Collision (every type) -----

    /// <summary>Collision box thickness across the line, centred on it, m.</summary>
    [ExportGroup("Collision")]
    [Export(PropertyHint.Range, "0,1,0.01,or_greater,suffix:m")]
    public float CollisionThickness { get; set; } = 0.2f;

    /// <summary>How far a collision box reaches below the line or the ground under the stands, m.</summary>
    [Export(PropertyHint.Range, "0,2,0.01,or_greater,suffix:m")]
    public float CollisionDepthBelow { get; set; } = 0.3f;

    /// <summary>How far collision boxes reach past both ends of an item, so neighbouring items overlap, m.</summary>
    [Export(PropertyHint.Range, "0,1,0.01,or_greater,suffix:m")]
    public float JointOverlap { get; set; } = 0.1f;

    // ----- Breaking -----

    /// <summary>A breaker moving at least this fast breaks an item it touches, m/s.</summary>
    [ExportGroup("Breaking", "Breaking")]
    [Export(PropertyHint.Range, "0,50,0.1,or_greater,suffix:m/s")]
    public float BreakingSpeed { get; set; } = 3.0f;

    /// <summary>Density of a loose piece, kg/m³ (its mass is its volume times this).</summary>
    [Export(PropertyHint.Range, "1,10000,1,or_greater,suffix:kg/m³")]
    public float BreakingPieceDensity { get; set; } = 300f;

    /// <summary>The lightest a loose piece gets, kg.</summary>
    [Export(PropertyHint.Range, "0.01,100,0.01,or_greater,suffix:kg")]
    public float BreakingMinPieceMass { get; set; } = 0.5f;

    /// <summary>Margin of the breakers' sweep queries against the unbroken items, m.</summary>
    [Export(PropertyHint.Range, "0,0.5,0.005,or_greater,suffix:m")]
    public float BreakingSweepMargin { get; set; } = 0.04f;

    /// <summary>The breaking parameters from these values.</summary>
    /// <exception cref="System.ArgumentException">A value is out of its range (<see cref="BreakParameters"/>).</exception>
    public BreakParameters ToBreakParameters() =>
        new()
        {
            BreakSpeed = BreakingSpeed,
            PieceDensity = BreakingPieceDensity,
            MinPieceMass = BreakingMinPieceMass,
            SweepMargin = BreakingSweepMargin,
        };

    /// <summary>The layout parameters from these values.</summary>
    /// <exception cref="System.ArgumentException">A value is out of its range (<see cref="BarrierParameters"/>).</exception>
    public BarrierParameters ToParameters() =>
        new()
        {
            RoadblockHeight = RoadblockHeight,
            LandFenceHeight = LandFenceHeight,
            WaterFenceHeight = WaterFenceHeight,
            PostSpacing = LandFencePostSpacing,
            StandSpacing = RoadblockStandSpacing,
            PostWidth = LandFencePostWidth,
            PostDepthBelow = LandFencePostDepthBelow,
            StrandCount = LandFenceStrandCount,
            StrandThickness = LandFenceStrandThickness,
            FloatSize = WaterFenceFloatSize,
            FloatSpacing = WaterFenceFloatSpacing,
            FloatLineHeight = WaterFenceFloatLineHeight,
            FloatLineThickness = WaterFenceFloatLineThickness,
            StandWidth = RoadblockStandWidth,
            StandDepth = RoadblockStandDepth,
            StandGroundInset = RoadblockStandGroundInset,
            BoardRows = RoadblockBoardRows,
            BoardHeight = RoadblockBoardHeight,
            BoardGap = RoadblockBoardGap,
            BoardThickness = RoadblockBoardThickness,
            StripeLength = RoadblockStripeLength,
            CollisionThickness = CollisionThickness,
            CollisionDepthBelow = CollisionDepthBelow,
            JointOverlap = JointOverlap,
        };
}
