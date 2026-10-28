using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using GdUnit4;
using Godot;
using Roadman.Core.Maps;
using Roadman.Scenes;
using static GdUnit4.Assertions;

namespace Roadman.Tests;

/// <summary>
/// Loads the real map pack. These tests fail, rather than skip, when the pack is missing, so a fresh clone
/// can't pass them by accident; build the pack with <c>/regen-map somero</c>.
/// </summary>
[TestSuite]
[RequireGodotRuntime]
public class MapLoaderTest
{
    private const string WorldScenePath = "res://scenes/world/World.tscn";
    private const string MapId = "somero";

    /// <summary>Allowed distance between the ray hit and the terrain mesh's vertical extent (float rounding).</summary>
    private const float HeightTolerance = 0.05f;

    [TestCase]
    public async Task World_LoadsEveryManifestTile()
    {
        AssertPackPresent();
        using var runner = ISceneRunner.Load(WorldScenePath, true);
        await runner.SimulateFrames(2);
        var loader = Loader(runner);

        AssertThat(loader.Manifest).IsNotNull();
        var manifest = loader.Manifest!;
        AssertThat(manifest.Tiles.Count).IsGreater(0);
        AssertThat(loader.LoadedTileCount).IsEqual(manifest.Tiles.Count);
        AssertThat(loader.Tiles.GetChildCount()).IsEqual(manifest.Tiles.Count);
        foreach (var tile in manifest.Tiles)
        {
            AssertBool(loader.Tiles.HasNode(tile.Id)).OverrideFailureMessage($"tile {tile.Id} is not under Tiles").IsTrue();
        }
    }

    [TestCase]
    public async Task RayDownAtOrigin_HitsTerrainWithinItsMeshHeightRange()
    {
        AssertPackPresent();
        using var runner = ISceneRunner.Load(WorldScenePath, true);
        await runner.SimulateFrames(5);
        var loader = Loader(runner);

        var hit = loader.RaycastTerrain(0f, 0f);
        AssertBool(hit is not null).OverrideFailureMessage("a ray down at the origin hit no terrain").IsTrue();
        var (position, body) = hit!.Value;

        // The importer turns the terrain-col node into a "terrain" mesh with the collision body as its child.
        var terrain = body.GetParent() as MeshInstance3D;
        AssertThat(terrain).IsNotNull();
        AssertThat(terrain!.Name.ToString()).IsEqual(MapLoader.TerrainNodeName);
        AssertThat(terrain.GetParent()?.GetParent()).IsSame(loader.Tiles);

        var aabb = terrain.GlobalTransform * terrain.Mesh.GetAabb();
        var minY = aabb.Position.Y;
        var maxY = aabb.End.Y;
        AssertFloat(aabb.Size.Y).OverrideFailureMessage($"terrain mesh of {terrain.GetParent()!.Name} is flat").IsGreater(0.1);
        AssertFloat(Mathf.Max(Mathf.Abs(minY), Mathf.Abs(maxY)))
            .OverrideFailureMessage($"terrain range {minY}..{maxY} is around y = 0; elevation data missing?")
            .IsGreater(1.0);
        AssertFloat(position.Y).IsBetween(minY - HeightTolerance, maxY + HeightTolerance);
    }

    [TestCase]
    public async Task World_PlacesDebugCameraAboveTerrainAtOrigin()
    {
        AssertPackPresent();
        using var runner = ISceneRunner.Load(WorldScenePath, true);
        await runner.SimulateFrames(5);
        var world = (World)runner.Scene()!;

        var ground = world.MapLoader.TerrainHeightAt(0f, 0f);
        AssertBool(ground is not null).IsTrue();
        AssertFloat(world.Camera.GlobalPosition.Y - ground!.Value).IsEqualApprox(world.CameraStartOffset.Y, 0.01);
    }

    [TestCase]
    public async Task GroundRelativePresetFarOutsideTheMap_IsOutsideBuiltArea()
    {
        AssertPackPresent();
        using var runner = ISceneRunner.Load(WorldScenePath, true);
        await runner.SimulateFrames(5);
        var loader = Loader(runner);
        var manifest = loader.Manifest!;

        var farX = 0.0;
        foreach (var tile in manifest.Tiles)
        {
            farX = System.Math.Max(farX, tile.Bounds.MaxX);
        }

        farX += 50_000;
        AssertBool(manifest.ContainsGodotPoint(farX, 0)).IsFalse();
        var preset = new CameraPreset("far-away", new LocalPoint(farX, 600, 0), new LocalPoint(0, 0, 0), 70, true);

        var result = PresetPlacement.Resolve(preset, (x, z) => loader.TerrainHeightAt((float)x, (float)z));

        AssertBool(result.IsOutsideBuiltArea).IsTrue();
    }

    /// <summary>
    /// The importer turns each <c>bld_*-colonly</c> glTF node into a standalone <see cref="StaticBody3D"/> (directly
    /// under the tile, with a generated name) holding a <see cref="CollisionShape3D"/>, and makes no mesh for it.
    /// </summary>
    [TestCase]
    public async Task Tiles_HaveCollisionOnlyBuildingBodiesWithoutMeshes()
    {
        AssertPackPresent();
        using var runner = ISceneRunner.Load(WorldScenePath, true);
        await runner.SimulateFrames(2);
        var loader = Loader(runner);

        var buildingBodies = 0;
        foreach (var tile in loader.Tiles.GetChildren())
        {
            foreach (var body in tile.GetChildren().OfType<StaticBody3D>())
            {
                AssertBool(body.IsInGroup(MapLoader.TerrainGroup))
                    .OverrideFailureMessage($"{tile.Name}/{body.Name}: a body directly under the tile is in {MapLoader.TerrainGroup}")
                    .IsFalse();

                var shapes = body.GetChildren().OfType<CollisionShape3D>().ToList();
                AssertInt(shapes.Count).OverrideFailureMessage($"{tile.Name}/{body.Name} has no CollisionShape3D").IsGreater(0);
                foreach (var shape in shapes)
                {
                    AssertThat(shape.Shape).OverrideFailureMessage($"{tile.Name}/{body.Name}/{shape.Name} has no shape").IsNotNull();
                }

                AssertInt(Descendants(body).OfType<MeshInstance3D>().Count())
                    .OverrideFailureMessage($"{tile.Name}/{body.Name}: a collision-only body has a mesh")
                    .IsEqual(0);
                buildingBodies++;
            }

            foreach (var mesh in Descendants(tile).OfType<MeshInstance3D>())
            {
                AssertBool(mesh.Name.ToString().Contains("colonly", System.StringComparison.OrdinalIgnoreCase))
                    .OverrideFailureMessage($"{tile.Name}/{mesh.Name}: a -colonly node was imported as a mesh")
                    .IsFalse();
            }
        }

        AssertInt(buildingBodies).OverrideFailureMessage("no tile has a collision-only building body").IsGreater(0);
    }

    /// <summary>
    /// Every tile is imported with the <c>[importer_defaults]</c> from <c>project.godot</c>. The <c>.glb.import</c>
    /// files are rewritten on each import, so this checks the settings actually used.
    /// </summary>
    [TestCase]
    public void TileImportSettings_DisableCompressionAndLods()
    {
        AssertPackPresent();
        var manifestPath = $"res://maps/{MapId}/{MapPack.ManifestFile}";
        var manifest = MapManifestReader.Parse(FileAccess.GetFileAsString(manifestPath), manifestPath);
        AssertInt(manifest.Tiles.Count).IsGreater(0);

        foreach (var tile in manifest.Tiles)
        {
            var importPath = $"res://maps/{MapId}/{tile.File}.import";
            var config = new ConfigFile();
            var error = config.Load(importPath);
            AssertThat(error).OverrideFailureMessage($"can't read {importPath}: {error}").IsEqual(Error.Ok);
            AssertThat(config.GetValue("remap", "importer", "").AsString()).IsEqual("scene");

            AssertBool(config.HasSectionKey("params", "meshes/force_disable_compression"))
                .OverrideFailureMessage($"{importPath} has no meshes/force_disable_compression").IsTrue();
            AssertBool(config.GetValue("params", "meshes/force_disable_compression").AsBool())
                .OverrideFailureMessage($"{importPath}: meshes/force_disable_compression is not true").IsTrue();

            AssertBool(config.HasSectionKey("params", "meshes/generate_lods"))
                .OverrideFailureMessage($"{importPath} has no meshes/generate_lods").IsTrue();
            AssertBool(config.GetValue("params", "meshes/generate_lods").AsBool())
                .OverrideFailureMessage($"{importPath}: meshes/generate_lods is not false").IsFalse();
        }
    }

    [TestCase]
    public void MissingPack_FailsWithRegenHint()
    {
        using var runner = ISceneRunner.Load(new Node3D { Name = "Root" }, true);
        var loader = new MapLoader { MapId = "no-such-map" };
        var failure = "";
        loader.MapLoadFailed += message => failure = message;
        runner.Scene()!.AddChild(loader);

        var loaded = loader.Load();

        AssertBool(loaded).IsFalse();
        AssertThat(loader.Manifest).IsNull();
        AssertThat(loader.LoadedTileCount).IsEqual(0);
        AssertThat(failure).Contains("res://maps/no-such-map/manifest.json").Contains("/regen-map no-such-map");
    }

    [TestCase]
    public async Task Tiles_UseTheMapMaterials()
    {
        AssertPackPresent();
        using var runner = ISceneRunner.Load(WorldScenePath, true);
        await runner.SimulateFrames(2);
        var loader = Loader(runner);
        AssertThat(loader.TerrainMaterial).IsNotNull();
        AssertThat(loader.BuildingMaterial).IsNotNull();

        var meshes = Descendants(loader.Tiles).OfType<MeshInstance3D>().ToList();
        var terrain = meshes.Where(m => m.Name == MapLoader.TerrainNodeName).ToList();
        var buildings = meshes.Where(m => m.Name.ToString().StartsWith(MapLoader.BuildingNodePrefix)).ToList();
        AssertThat(terrain.Count).IsEqual(loader.LoadedTileCount);
        AssertThat(buildings.Count).IsGreater(0);
        AssertThat(terrain.All(m => m.MaterialOverride == loader.TerrainMaterial)).IsTrue();
        AssertThat(buildings.All(m => m.MaterialOverride == loader.BuildingMaterial)).IsTrue();
    }

    private static MapLoader Loader(ISceneRunner runner)
    {
        var loader = runner.Scene()!.GetNodeOrNull<MapLoader>("MapLoader");
        AssertThat(loader).IsNotNull();
        return loader!;
    }

    private static IEnumerable<Node> Descendants(Node root)
    {
        foreach (var child in root.GetChildren())
        {
            yield return child;
            foreach (var descendant in Descendants(child))
            {
                yield return descendant;
            }
        }
    }

    private static void AssertPackPresent()
    {
        var manifest = $"res://maps/{MapId}/{MapPack.ManifestFile}";
        AssertBool(FileAccess.FileExists(manifest))
            .OverrideFailureMessage($"no map pack at {manifest}; {MapPack.RegenHint(MapId)}")
            .IsTrue();
    }
}
