namespace Roadman.Core.Maps;

/// <summary>
/// The terrain height found under a point, or none. With none, the point is outside the built area.
/// </summary>
public readonly record struct HeightResolution(bool IsOutsideBuiltArea, double Height)
{
    public static HeightResolution OutsideBuiltArea => new(true, 0);

    public static HeightResolution At(double height) => new(false, height);
}

/// <summary>
/// Where a camera preset puts the camera once ground-relative heights are resolved, or
/// <see cref="IsOutsideBuiltArea"/> when there is no terrain under the camera position.
/// </summary>
public readonly record struct PresetResolution(bool IsOutsideBuiltArea, LocalPoint Position, LocalPoint LookAt)
{
    public static PresetResolution OutsideBuiltArea => new(true, default, default);
}

/// <summary>
/// Resolves ground-relative heights. The engine side supplies the terrain hit (a downward raycast); the rules
/// for what a hit or a miss means live here.
/// </summary>
public static class PresetPlacement
{
    /// <summary>
    /// The absolute height of a point <paramref name="relativeHeight"/> above the terrain, given the terrain hit
    /// <paramref name="terrainHitY"/> under it. No hit means the point is outside the built area.
    /// </summary>
    public static HeightResolution ResolveHeight(double relativeHeight, double? terrainHitY) =>
        terrainHitY is { } ground ? HeightResolution.At(ground + relativeHeight) : HeightResolution.OutsideBuiltArea;

    /// <summary>
    /// Resolves a preset's position and look-at point.
    /// An absolute preset is returned as is, without probing the terrain.
    /// For a ground-relative preset, no terrain under the position gives <see cref="PresetResolution.OutsideBuiltArea"/>.
    /// A look-at point with no terrain under it (for example beyond the map edge) is measured from the terrain
    /// under the position instead, so a camera inside the map can still look out past its edge.
    /// </summary>
    /// <param name="preset">The preset.</param>
    /// <param name="terrainHeightAt">Terrain height at Godot (x, z), or null when there is no terrain there.</param>
    public static PresetResolution Resolve(CameraPreset preset, Func<double, double, double?> terrainHeightAt)
    {
        ArgumentNullException.ThrowIfNull(preset);
        ArgumentNullException.ThrowIfNull(terrainHeightAt);
        if (!preset.GroundRelative)
        {
            return new PresetResolution(false, preset.Position, preset.LookAt);
        }

        var positionGround = terrainHeightAt(preset.Position.X, preset.Position.Z);
        var position = ResolveHeight(preset.Position.Y, positionGround);
        if (position.IsOutsideBuiltArea)
        {
            return PresetResolution.OutsideBuiltArea;
        }

        var lookAtGround = terrainHeightAt(preset.LookAt.X, preset.LookAt.Z) ?? positionGround;
        var lookAt = ResolveHeight(preset.LookAt.Y, lookAtGround);
        return new PresetResolution(
            false,
            preset.Position with { Y = position.Height },
            preset.LookAt with { Y = lookAt.Height });
    }
}
