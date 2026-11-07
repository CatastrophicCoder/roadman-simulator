namespace Roadman.Core.Maps;

/// <summary>
/// The minimap image's outer pixel edges in Godot local metres (<c>bounds_local</c> in <c>minimap.json</c>):
/// <see cref="MinX"/> is the left edge of the left (west) column, <see cref="MinZ"/> the top edge of the top (north) row.
/// </summary>
public readonly record struct MinimapBounds(double MinX, double MaxX, double MinZ, double MaxZ);

/// <summary>
/// The pack's <c>minimap.json</c> (schema version 1, <c>docs/specs/minimap.md</c>, ADR 0021): where the background
/// image <c>minimap.png</c> lies in the world. Read it with <see cref="MinimapImageReader"/>, which checks it against the
/// manifest and guarantees that the bounds equal the size times <see cref="MetresPerPixel"/>.
/// </summary>
/// <param name="SchemaVersion">Always <see cref="MinimapImageReader.SupportedSchemaVersion"/> once parsed.</param>
/// <param name="MapId">The manifest's map id.</param>
/// <param name="Area">The manifest's area, <c>full</c> or <c>test</c>.</param>
/// <param name="Image">The image file name, always <see cref="MapPack.MinimapImageFile"/>.</param>
/// <param name="MetresPerPixel">Metres per image pixel (<c>m_per_px</c>), above 0.</param>
/// <param name="Width">Image width in pixels, above 0.</param>
/// <param name="Height">Image height in pixels, above 0.</param>
/// <param name="Bounds">The image's outer pixel edges in Godot local x/z; the top row is north (min z).</param>
public sealed record MinimapImage(
    int SchemaVersion,
    string MapId,
    string Area,
    string Image,
    double MetresPerPixel,
    int Width,
    int Height,
    MinimapBounds Bounds)
{
    /// <summary>
    /// The continuous image pixel coordinates of the Godot ground point (<paramref name="x"/>, <paramref name="z"/>):
    /// (0, 0) is the top-left corner of the top-left pixel, (<see cref="Width"/>, <see cref="Height"/>) the bottom-right
    /// corner of the bottom-right pixel, so pixel (i, j)'s centre is (i + 0.5, j + 0.5).
    /// </summary>
    public (double Px, double Py) PixelOf(double x, double z) =>
        ((x - Bounds.MinX) / MetresPerPixel, (z - Bounds.MinZ) / MetresPerPixel);

    /// <summary>The inverse of <see cref="PixelOf"/>: the Godot ground point (x, z) at image pixel coordinates (px, py).</summary>
    public (double X, double Z) WorldOf(double px, double py) =>
        (Bounds.MinX + (px * MetresPerPixel), Bounds.MinZ + (py * MetresPerPixel));
}
