namespace Roadman.Scenes;

/// <summary>
/// Physics layer bits. Every map collision body (terrain, roads, buildings, the world-edge cliff) stays on Godot's
/// default layer 1; the player and the world-edge barriers have their own layers, so rays and shapes that look for the
/// map (map rays, the camera's spring arm) pass through them (ADR 0016). The unbroken barrier items
/// (<see cref="BarrierStatic"/>) and the loose pieces of broken ones (<see cref="Barrier"/>) are on separate layers, so
/// the void trigger monitors only the pieces (ADR 0017).
/// </summary>
public static class CollisionLayers
{
    /// <summary>Layer 1: the map pack's collision bodies (the default layer of imported bodies).</summary>
    public const uint Map = 1u << 0;

    /// <summary>Layer 2: the player's <c>CharacterBody3D</c>.</summary>
    public const uint Player = 1u << 1;

    /// <summary>
    /// Layer 3: the loose pieces of broken world-edge barrier items (rigid bodies). The void trigger monitors it.
    /// </summary>
    public const uint Barrier = 1u << 2;

    /// <summary>Layer 4: vehicles (reserved; the vehicles spec uses it, and the void trigger already monitors it).</summary>
    public const uint Vehicle = 1u << 3;

    /// <summary>
    /// Layer 5: the unbroken world-edge barrier items (roadblocks, land and water fences; static bodies). Not monitored by
    /// the void trigger: nothing on it ever moves, and an area that monitors hundreds of static bodies costs physics time
    /// each step (the spec's 1 ms budget).
    /// </summary>
    public const uint BarrierStatic = 1u << 4;

    /// <summary>Both barrier layers: unbroken items and loose pieces.</summary>
    public const uint Barriers = Barrier | BarrierStatic;

    /// <summary>What the player collides with: the map and both barrier layers.</summary>
    public const uint PlayerMask = Map | Barriers;
}
