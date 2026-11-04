using Godot;

namespace Roadman.Scenes.Edge;

/// <summary>
/// A land fence segment of the world edge (<c>docs/specs/world-edge-game.md</c>, "Barriers"): posts with barbed-wire
/// strands along the fence line. One <see cref="StaticBody3D"/> per <c>boundary.json</c> land fence; its pieces are
/// drawn by <see cref="Barriers"/>' batches. Its tuned sizes are the <c>Land Fence</c> group of
/// <see cref="BarrierSettings"/>, whose post values (<see cref="BarrierSettings.LandFencePostSpacing"/> and the others)
/// water fences use too.
/// </summary>
public partial class LandFence : BarrierItem
{
}
