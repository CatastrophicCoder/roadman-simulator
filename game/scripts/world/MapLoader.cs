using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using Roadman.Core.Maps;

namespace Roadman.Scenes;

/// <summary>
/// Loads a generated map pack (<c>res://maps/&lt;id&gt;/</c>, ADR 0006): reads and checks <c>manifest.json</c>,
/// then instances every tile under a <c>Tiles</c> child, named by tile id, at the origin (the tile meshes are
/// already in map-local coordinates). All tiles load at once; streaming comes later.
/// After the tiles it reads <c>road_graph.json</c> into <see cref="RoadGraph"/>; a missing or invalid graph is
/// reported, and the tiles stay loaded.
/// Errors are pushed with the pack path and the <c>/regen-map</c> hint; the game keeps running without a map.
/// </summary>
public partial class MapLoader : Node3D
{
    /// <summary>Project setting that picks the map when <see cref="MapId"/> is empty.</summary>
    public const string MapIdSetting = "roadman/map/id";

    public const string DefaultMapId = "somero";

    /// <summary>Group of the terrain collision bodies (from the <c>terrain-col</c> glTF nodes).</summary>
    public const string TerrainGroup = "map_terrain";

    /// <summary>Name of the terrain mesh node in each tile; the importer strips the <c>-col</c> suffix.</summary>
    public const string TerrainNodeName = "terrain";

    /// <summary>
    /// Group of the road, path and bridge collision bodies (from the <c>road-col</c>, <c>path-col</c> and
    /// <c>bridge_osm_way_&lt;id&gt;[_p&lt;n&gt;]-col</c> glTF nodes). Not in <see cref="TerrainGroup"/>, so
    /// <see cref="TerrainHeightAt"/> keeps returning the bare terrain.
    /// </summary>
    public const string RoadGroup = "map_roads";

    /// <summary>Name of the drivable surface mesh in each tile (glTF node <c>road-col</c>).</summary>
    public const string RoadNodeName = "road";

    /// <summary>Name of the non-drivable path surface mesh in each tile (glTF node <c>path-col</c>).</summary>
    public const string PathNodeName = "path";

    /// <summary>Prefix of the bridge deck meshes, <c>bridge_osm_way_&lt;id&gt;</c> or <c>bridge_osm_way_&lt;id&gt;_p&lt;n&gt;</c>.</summary>
    public const string BridgeNodePrefix = "bridge_osm_way_";

    private const float RayHalfLength = 10_000f;
    private const int MaxRayHits = 32;

    [Signal]
    public delegate void MapLoadedEventHandler(int tileCount);

    [Signal]
    public delegate void MapLoadFailedEventHandler(string message);

    /// <summary>Prefix of the building visual mesh nodes (ADR 0006).</summary>
    public const string BuildingNodePrefix = "bld_";

    /// <summary>Folder of the placeholder map materials, until the art-direction palette replaces them.</summary>
    public const string PlaceholderMaterialsPath = "res://materials/placeholder";

    /// <summary>Map to load. Empty means the <c>roadman/map/id</c> project setting.</summary>
    [Export]
    public string MapId { get; set; } = "";

    /// <summary>Material for the terrain meshes. Null means the placeholder in <see cref="PlaceholderMaterialsPath"/>.</summary>
    [Export]
    public Material? TerrainMaterial { get; set; }

    /// <summary>Material for the building meshes. Null means the placeholder in <see cref="PlaceholderMaterialsPath"/>.</summary>
    [Export]
    public Material? BuildingMaterial { get; set; }

    /// <summary>Material for the <c>road</c> surfaces and bridge decks. Null means the placeholder in <see cref="PlaceholderMaterialsPath"/>.</summary>
    [Export]
    public Material? RoadMaterial { get; set; }

    /// <summary>Material for the <c>path</c> surfaces. Null means the placeholder in <see cref="PlaceholderMaterialsPath"/>.</summary>
    [Export]
    public Material? PathMaterial { get; set; }

    /// <summary>The parsed manifest, or null before a successful <see cref="Load"/>.</summary>
    public MapManifest? Manifest { get; private set; }

    public int LoadedTileCount { get; private set; }

    /// <summary>
    /// The pack's road graph, read after the tiles; null before a <see cref="Load"/> and when the pack's
    /// <c>road_graph.json</c> is missing or invalid (an error is pushed then).
    /// </summary>
    public RoadGraph? RoadGraph { get; private set; }

    /// <summary>Path of the road graph file in the pack.</summary>
    public string RoadGraphPath => $"{PackPath}/{MapPack.RoadGraphFile}";

    /// <summary>The map id in use: <see cref="MapId"/>, or the project setting when that is empty.</summary>
    public string ResolvedMapId =>
        string.IsNullOrEmpty(MapId) ? ProjectSettings.GetSetting(MapIdSetting, DefaultMapId).AsString() : MapId;

    public string PackPath => $"res://maps/{ResolvedMapId}";

    public Node3D Tiles { get; private set; } = null!;

    public override void _Ready()
    {
        EnsureTilesNode();
        TerrainMaterial ??= GD.Load<Material>($"{PlaceholderMaterialsPath}/terrain.tres");
        BuildingMaterial ??= GD.Load<Material>($"{PlaceholderMaterialsPath}/building.tres");
        RoadMaterial ??= GD.Load<Material>($"{PlaceholderMaterialsPath}/road.tres");
        PathMaterial ??= GD.Load<Material>($"{PlaceholderMaterialsPath}/path.tres");
    }

    /// <summary>
    /// Reads the manifest and instances every tile. Emits <see cref="SignalName.MapLoaded"/> with the number of
    /// tiles loaded (a missing tile is reported and skipped; <see cref="RoadGraph"/> is set before it), or <see cref="SignalName.MapLoadFailed"/> when the
    /// pack can't be used at all.
    /// </summary>
    /// <returns>True when the manifest was read and every tile loaded.</returns>
    public bool Load()
    {
        EnsureTilesNode();
        Unload();

        var mapId = ResolvedMapId;
        if (!MapPack.IsValidMapId(mapId))
        {
            return Fail($"map id '{mapId}' is not a lowercase slug (set {nameof(MapId)} or the {MapIdSetting} project setting)");
        }

        var manifestPath = $"{PackPath}/{MapPack.ManifestFile}";
        if (!FileAccess.FileExists(manifestPath))
        {
            return Fail($"no map pack: {manifestPath} doesn't exist; {MapPack.RegenHint(mapId)}");
        }

        MapManifest manifest;
        try
        {
            manifest = MapManifestReader.Parse(FileAccess.GetFileAsString(manifestPath), manifestPath);
        }
        catch (MapPackException ex)
        {
            return Fail($"{ex.Message}; {MapPack.RegenHint(mapId)}");
        }

        if (manifest.MapId != mapId)
        {
            return Fail($"{manifestPath}: map_id is '{manifest.MapId}', expected '{mapId}'; {MapPack.RegenHint(mapId)}");
        }

        Manifest = manifest;
        var complete = true;
        foreach (var tile in manifest.Tiles)
        {
            var tilePath = $"{PackPath}/{tile.File}";
            var scene = ResourceLoader.Exists(tilePath) ? ResourceLoader.Load<PackedScene>(tilePath) : null;
            if (scene is null)
            {
                GD.PushError($"MapLoader: tile {tile.Id} is missing or not imported: {tilePath}; {MapPack.RegenHint(mapId)}");
                complete = false;
                continue;
            }

            var instance = scene.Instantiate<Node3D>();
            instance.Name = tile.Id;
            Tiles.AddChild(instance);
            PrepareTile(instance);
            LoadedTileCount++;
        }

        GD.Print($"MapLoader: loaded {LoadedTileCount} of {manifest.Tiles.Count} tiles from {PackPath}");
        RoadGraph = LoadRoadGraph(mapId);
        EmitSignal(SignalName.MapLoaded, LoadedTileCount);
        return complete;
    }

    /// <summary>
    /// The terrain height at Godot (x, z): the highest terrain collision hit of a vertical ray, ignoring buildings.
    /// Null when there is no terrain there. Call it from a physics frame or after one (see
    /// <see cref="WaitForTerrainCollisionAsync"/>); new collision bodies aren't queryable before that.
    /// </summary>
    public float? TerrainHeightAt(float x, float z) => RaycastTerrain(x, z)?.Position.Y;

    /// <summary>
    /// The terrain hit under Godot (x, z), with the hit body; null when there is no terrain there.
    /// Non-terrain hits (building collision boxes) are excluded and the ray is cast again.
    /// </summary>
    public (Vector3 Position, StaticBody3D Body)? RaycastTerrain(float x, float z)
    {
        var space = GetWorld3D().DirectSpaceState;
        var query = PhysicsRayQueryParameters3D.Create(
            new Vector3(x, RayHalfLength, z),
            new Vector3(x, -RayHalfLength, z));
        var excluded = new Godot.Collections.Array<Rid>();
        for (var i = 0; i < MaxRayHits; i++)
        {
            query.Exclude = excluded;
            var hit = space.IntersectRay(query);
            if (hit.Count == 0)
            {
                return null;
            }

            var collider = hit["collider"].AsGodotObject();
            if (collider is StaticBody3D body && body.IsInGroup(TerrainGroup))
            {
                return (hit["position"].AsVector3(), body);
            }

            excluded.Add(hit["rid"].AsRid());
        }

        return null;
    }

    /// <summary>
    /// Waits until the terrain collision is in the physics space, probing the centre of the first loaded tile
    /// once per physics frame. Freshly added bodies can need a physics step before ray casts see them.
    /// </summary>
    /// <returns>True once a probe hits terrain; false when nothing is loaded or <paramref name="maxPhysicsFrames"/> pass without a hit.</returns>
    public async Task<bool> WaitForTerrainCollisionAsync(int maxPhysicsFrames = 10)
    {
        var probe = FirstLoadedTileCentre();
        if (probe is null)
        {
            return false;
        }

        var tree = GetTree();
        for (var frame = 0; frame < maxPhysicsFrames; frame++)
        {
            await ToSignal(tree, SceneTree.SignalName.PhysicsFrame);
            if (!IsInsideTree())
            {
                return false;
            }

            if (RaycastTerrain(probe.Value.X, probe.Value.Y) is not null)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The terrain mesh of a loaded tile (the <c>terrain</c> node under <paramref name="tile"/>, a child of
    /// <see cref="Tiles"/>), or null when the tile has none.
    /// </summary>
    public static MeshInstance3D? FindTerrainMesh(Node tile)
    {
        var pending = new Stack<Node>();
        pending.Push(tile);
        while (pending.Count > 0)
        {
            var node = pending.Pop();
            if (node is MeshInstance3D mesh && mesh.Name == TerrainNodeName)
            {
                return mesh;
            }

            foreach (var child in node.GetChildren())
            {
                pending.Push(child);
            }
        }

        return null;
    }

    private (float X, float Y)? FirstLoadedTileCentre()
    {
        if (Manifest is null)
        {
            return null;
        }

        foreach (var tile in Manifest.Tiles)
        {
            if (Tiles.HasNode(tile.Id))
            {
                var (x, z) = tile.Bounds.GodotCentre;
                return ((float)x, (float)z);
            }
        }

        return null;
    }

    private void Unload()
    {
        foreach (var child in Tiles.GetChildren())
        {
            Tiles.RemoveChild(child);
            child.QueueFree();
        }

        Manifest = null;
        RoadGraph = null;
        LoadedTileCount = 0;
    }

    /// <summary>
    /// Reads <see cref="RoadGraphPath"/>. A missing, invalid or other map's file pushes an error and gives null; the
    /// tiles stay loaded and <see cref="Load"/>'s result doesn't change.
    /// </summary>
    private RoadGraph? LoadRoadGraph(string mapId)
    {
        var path = RoadGraphPath;
        if (!FileAccess.FileExists(path))
        {
            GD.PushError($"MapLoader: no road graph: {path} doesn't exist; {MapPack.RegenHint(mapId)}");
            return null;
        }

        RoadGraph graph;
        try
        {
            graph = RoadGraphReader.Parse(FileAccess.GetFileAsString(path), path);
        }
        catch (MapPackException ex)
        {
            GD.PushError($"MapLoader: {ex.Message}; {MapPack.RegenHint(mapId)}");
            return null;
        }

        if (graph.MapId != mapId)
        {
            GD.PushError($"MapLoader: {path}: map_id is '{graph.MapId}', expected '{mapId}'; {MapPack.RegenHint(mapId)}");
            return null;
        }

        GD.Print($"MapLoader: road graph with {graph.Nodes.Count} nodes and {graph.Edges.Count} edges from {path}");
        return graph;
    }

    private bool Fail(string message)
    {
        GD.PushError($"MapLoader: {message}");
        EmitSignal(SignalName.MapLoadFailed, message);
        return false;
    }

    private void EnsureTilesNode()
    {
        if (Tiles is not null && IsInstanceValid(Tiles))
        {
            return;
        }

        Tiles = new Node3D { Name = "Tiles" };
        AddChild(Tiles);
    }

    /// <summary>
    /// Puts the terrain's collision bodies in <see cref="TerrainGroup"/> and the road, path and bridge ones in
    /// <see cref="RoadGroup"/>, and sets the map materials. The importer turns <c>terrain-col</c>, <c>road-col</c>,
    /// <c>path-col</c> and <c>bridge_osm_way_…-col</c> into meshes without the suffix, each with a
    /// <see cref="StaticBody3D"/> child; building meshes keep their <c>bld_</c> names.
    /// </summary>
    private void PrepareTile(Node tile)
    {
        var pending = new Stack<Node>();
        pending.Push(tile);
        while (pending.Count > 0)
        {
            var node = pending.Pop();
            if (node is StaticBody3D body && body.GetParent() is MeshInstance3D parent)
            {
                var parentName = parent.Name.ToString();
                if (parentName == TerrainNodeName)
                {
                    body.AddToGroup(TerrainGroup, persistent: false);
                }
                else if (IsRoadSurfaceName(parentName))
                {
                    body.AddToGroup(RoadGroup, persistent: false);
                }
            }
            else if (node is MeshInstance3D mesh)
            {
                var meshName = mesh.Name.ToString();
                if (meshName == TerrainNodeName)
                {
                    mesh.MaterialOverride = TerrainMaterial;
                }
                else if (meshName.StartsWith(BuildingNodePrefix, System.StringComparison.Ordinal))
                {
                    mesh.MaterialOverride = BuildingMaterial;
                }
                else if (meshName == RoadNodeName || meshName.StartsWith(BridgeNodePrefix, System.StringComparison.Ordinal))
                {
                    mesh.MaterialOverride = RoadMaterial;
                }
                else if (meshName == PathNodeName)
                {
                    mesh.MaterialOverride = PathMaterial;
                }
            }

            foreach (var child in node.GetChildren())
            {
                pending.Push(child);
            }
        }
    }

    /// <summary>True for the mesh names of road, path and bridge surfaces (the glTF names without <c>-col</c>).</summary>
    public static bool IsRoadSurfaceName(string name) =>
        name == RoadNodeName || name == PathNodeName || name.StartsWith(BridgeNodePrefix, System.StringComparison.Ordinal);
}
