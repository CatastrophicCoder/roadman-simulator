using Godot;

namespace Roadman.Scenes.Edge;

/// <summary>
/// A roadblock of the world edge (<c>docs/specs/world-edge-game.md</c>, "Barriers"): light red-and-white boards on
/// stands across a road where it leaves the playable area. One <see cref="StaticBody3D"/> per <c>boundary.json</c>
/// roadblock; its pieces are drawn by <see cref="Barriers"/>' batches. Its tuned sizes are the <c>Roadblock</c> group of
/// <see cref="BarrierSettings"/>.
/// </summary>
public partial class Roadblock : BarrierItem
{
}
