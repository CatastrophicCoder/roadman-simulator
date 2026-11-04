using Godot;

namespace Roadman.Scenes.Edge;

/// <summary>
/// A water fence segment of the world edge (<c>docs/specs/world-edge-game.md</c>, "Barriers"): posts with a line on
/// floats, at the segment's heights (the terrain, until water surfaces exist; ADR 0015). One
/// <see cref="StaticBody3D"/> per <c>boundary.json</c> water fence; its pieces are drawn by <see cref="Barriers"/>'
/// batches. Its tuned sizes are the <c>Water Fence</c> group of <see cref="BarrierSettings"/>; its posts use the
/// <c>Land Fence</c> group's post values.
/// </summary>
public partial class WaterFence : BarrierItem
{
}
