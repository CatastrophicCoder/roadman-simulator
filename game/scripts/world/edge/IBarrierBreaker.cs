using Godot;

namespace Roadman.Scenes.Edge;

/// <summary>
/// A body that breaks world-edge barriers (<c>docs/specs/world-edge-game.md</c>, "Breaking"): vehicles later, a
/// car-sized test body now. A breaker is a <see cref="PhysicsBody3D"/> that implements this interface and is in the
/// group <see cref="Barriers.BreakerGroup"/>. Each physics frame, before the step, <see cref="Barriers"/> sweeps its
/// enabled <see cref="CollisionShape3D"/> children along <see cref="BreakerVelocity"/> over the step; an item hit at
/// <see cref="Roadman.Core.Edge.BreakParameters.BreakSpeed"/> or faster breaks. The player on foot is never a breaker.
/// </summary>
public interface IBarrierBreaker
{
    /// <summary>The body's velocity, m/s (barrier items are static, so its length is the relative speed).</summary>
    Vector3 BreakerVelocity { get; }
}
