namespace Roadman.Scenes;

/// <summary>
/// Physics layer bits. Every map collision body (terrain, roads, buildings, the world-edge cliff) stays on Godot's
/// default layer 1; the player has its own layer, so rays and shapes that look for the map pass through it.
/// </summary>
public static class CollisionLayers
{
    /// <summary>Layer 1: the map pack's collision bodies (the default layer of imported bodies).</summary>
    public const uint Map = 1u << 0;

    /// <summary>Layer 2: the player's <c>CharacterBody3D</c>.</summary>
    public const uint Player = 1u << 1;
}
