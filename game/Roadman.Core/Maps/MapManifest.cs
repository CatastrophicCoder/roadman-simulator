namespace Roadman.Core.Maps;

/// <summary>
/// The parts of a map pack's <c>manifest.json</c> (schema version 1, ADR 0006) that the game uses.
/// Read it with <see cref="MapManifestReader"/>.
/// </summary>
/// <param name="SchemaVersion">Always <see cref="MapManifestReader.SupportedSchemaVersion"/> once parsed.</param>
/// <param name="MapId">The map id; equals the pack folder name.</param>
/// <param name="DisplayName">The name shown in the game.</param>
/// <param name="Area"><c>full</c> or <c>test</c>.</param>
/// <param name="Origin">The local origin in the map CRS.</param>
/// <param name="TileSizeM">Tile edge in metres.</param>
/// <param name="Tiles">Every tile in the pack.</param>
/// <param name="Credits">The data credits of the pack, one per source dataset the build read.</param>
public sealed record MapManifest(
    int SchemaVersion,
    string MapId,
    string DisplayName,
    string Area,
    MapOrigin Origin,
    int TileSizeM,
    IReadOnlyList<MapTile> Tiles,
    IReadOnlyList<MapCredit> Credits)
{
    /// <summary>True when the Godot point (<paramref name="x"/>, <paramref name="z"/>) lies on any tile.</summary>
    public bool ContainsGodotPoint(double x, double z) => Tiles.Any(tile => tile.Bounds.ContainsGodotPoint(x, z));
}

/// <summary>The local origin in map CRS metres (<c>origin</c> in the manifest).</summary>
public readonly record struct MapOrigin(double X, double Y);

/// <summary>
/// One entry of the manifest's <c>credits</c>: the licence and attribution of a dataset the build read, taken from
/// the download manifest (ADR 0006).
/// </summary>
/// <param name="Source">Source adapter name, e.g. <c>mml_orthophoto</c>.</param>
/// <param name="Dataset">Dataset name.</param>
/// <param name="Licence">Licence name, e.g. <c>CC BY 4.0</c>.</param>
/// <param name="Attribution">The attribution text the licence requires.</param>
/// <param name="RetrievedAt">When the data was downloaded, as written by the pipeline (UTC, ISO 8601).</param>
public sealed record MapCredit(string Source, string Dataset, string Licence, string Attribution, string RetrievedAt);

/// <summary>One tile of the pack.</summary>
/// <param name="Id">Tile id, e.g. <c>tile_x0_y-1</c>; also used as the node name in the game.</param>
/// <param name="File">Path of the tile's <c>.glb</c>, relative to the pack folder.</param>
/// <param name="Bounds">The tile's area in local map metres.</param>
/// <param name="BuildingCount">Number of buildings in the tile.</param>
public sealed record MapTile(string Id, string File, TileBounds Bounds, int BuildingCount);

/// <summary>
/// A tile's <c>bounds_local</c> <c>[min_x, min_y, max_x, max_y]</c> in local map metres, where x is east and
/// y is north. In Godot, north is −Z, so the tile covers z from −<see cref="MaxY"/> to −<see cref="MinY"/>.
/// </summary>
public readonly record struct TileBounds(double MinX, double MinY, double MaxX, double MaxY)
{
    /// <summary>The tile's centre as Godot (x, z).</summary>
    public (double X, double Z) GodotCentre => ((MinX + MaxX) / 2, -(MinY + MaxY) / 2);

    /// <summary>True when the Godot point (<paramref name="x"/>, <paramref name="z"/>) lies on the tile, edges included.</summary>
    public bool ContainsGodotPoint(double x, double z)
    {
        var north = -z;
        return x >= MinX && x <= MaxX && north >= MinY && north <= MaxY;
    }
}
