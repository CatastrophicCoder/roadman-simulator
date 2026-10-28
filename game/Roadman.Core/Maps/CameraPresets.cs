namespace Roadman.Core.Maps;

/// <summary>A point in Godot local metres: +X east, +Y up, −Z north.</summary>
public readonly record struct LocalPoint(double X, double Y, double Z);

/// <summary>
/// A named camera position from the pack's <c>camera_presets.json</c>, used by the screenshot tool.
/// </summary>
/// <param name="Name">Preset name; also the screenshot file name in <c>/verify</c>.</param>
/// <param name="Position">Camera position.</param>
/// <param name="LookAt">The point the camera looks at.</param>
/// <param name="Fov">Vertical field of view in degrees.</param>
/// <param name="GroundRelative">
/// When true, <see cref="Position"/>.Y and <see cref="LookAt"/>.Y are heights above the terrain at that
/// point's x/z instead of absolute heights. Resolve them with <see cref="PresetPlacement"/>.
/// </param>
public sealed record CameraPreset(string Name, LocalPoint Position, LocalPoint LookAt, double Fov, bool GroundRelative);

/// <summary>The contents of a pack's <c>camera_presets.json</c>. Read it with <see cref="CameraPresetsReader"/>.</summary>
public sealed record CameraPresets(int SchemaVersion, IReadOnlyDictionary<string, CameraPreset> Presets);
