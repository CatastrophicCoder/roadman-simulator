namespace Roadman.Scenes;

/// <summary>
/// Physics layer bits. Every map collision body (terrain, roads, buildings, the world-edge cliff) stays on Godot's
/// default layer 1; the player and the world-edge barriers have their own layers, so rays and shapes that look for the
/// map (map rays, the camera's spring arm) pass through them (ADR 0016).
/// </summary>
public static class CollisionLayers
{
    /// <summary>Layer 1: the map pack's collision bodies (the default layer of imported bodies).</summary>
    public const uint Map = 1u << 0;

    /// <summary>Layer 2: the player's <c>CharacterBody3D</c>.</summary>
    public const uint Player = 1u << 1;

    /// <summary>Layer 3: the world-edge barrier items (roadblocks, land and water fences) and their loose pieces.</summary>
    public const uint Barrier = 1u << 2;

    /// <summary>Layer 4: vehicles (reserved; the vehicles spec uses it, and the void trigger already monitors it).</summary>
    public const uint Vehicle = 1u << 3;

    /// <summary>What the player collides with: the map and the barriers.</summary>
    public const uint PlayerMask = Map | Barrier;
}
