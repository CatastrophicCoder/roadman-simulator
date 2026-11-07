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

    /// <summary>The suite runs on the full pack; only the override test sets <see cref="MapLoader.PackFolderOverride"/>.</summary>
    // The suite checks the full pack (its full-pack-only branches would skip on the test area), so it never relies on
    // another suite having reset the override.
    [BeforeTest]
    public void UseFullPack() => TestPacks.UseFullPack();

    [AfterTest]
    public void ResetPackOverride() => TestPacks.UseFullPack();

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

    /// <summary>
    /// Test-performance criterion 9: with <see cref="MapLoader.PackFolderOverride"/> set to the test-area pack, a
    /// <c>World</c> loads every tile of that pack's manifest (area <c>test</c>, map id unchanged) and reads its road
    /// graph, places and boundary from that folder. With the override null again, <see cref="MapLoader.PackPath"/> is
    /// <c>res://maps/somero</c>.
    /// </summary>
    [TestCase]
    public async Task PackFolderOverride_LoadsTheWholeTestAreaPackFromItsFolder_AndNullGivesTheMapsFolderAgain()
    {
        TestPacks.UseTestArea();
        var manifestPath = $"{TestPacks.TestAreaPath}/{MapPack.ManifestFile}";
        var fileManifest = MapManifestReader.Parse(FileAccess.GetFileAsString(manifestPath), manifestPath);
        using (var runner = ISceneRunner.Load(WorldScenePath, true))
        {
            await runner.SimulateFrames(2);
            var loader = Loader(runner);

            AssertThat(loader.ResolvedMapId).IsEqual(MapId);
            AssertThat(loader.PackPath).IsEqual(TestPacks.TestAreaPath);
            AssertThat(loader.Manifest).IsNotNull();
            var manifest = loader.Manifest!;
            AssertThat(manifest.MapId).IsEqual(MapId);
            AssertThat(manifest.Area).IsEqual("test");
            AssertInt(manifest.Tiles.Count).IsEqual(fileManifest.Tiles.Count);
            AssertInt(manifest.Tiles.Count).IsGreater(0);
            AssertInt(loader.LoadedTileCount).IsEqual(manifest.Tiles.Count);
            AssertInt(loader.Tiles.GetChildCount()).IsEqual(manifest.Tiles.Count);
            AssertThat(loader.RoadGraph).IsNotNull();
            AssertThat(loader.Places).IsNotNull();
            AssertThat(loader.Boundary).IsNotNull();
            AssertThat(loader.Boundary!.Area).IsEqual("test");
            foreach (var path in new[] { loader.RoadGraphPath, loader.BuildingsPath, loader.PoisPath, loader.BoundaryPath })
            {
                AssertBool(path.StartsWith($"{TestPacks.TestAreaPath}/", System.StringComparison.Ordinal))
                    .OverrideFailureMessage($"{path} is not in {TestPacks.TestAreaPath}").IsTrue();
            }
        }

        TestPacks.UseFullPack();
        var fresh = new MapLoader { MapId = MapId };
        try
        {
            AssertThat(MapLoader.PackFolderOverride).IsNull();
            AssertThat(fresh.PackPath).IsEqual(TestPacks.FullPath);
            AssertThat(fresh.PackPath).IsEqual($"res://maps/{MapId}");
        }
        finally
        {
            fresh.Free();
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
        AssertFloat(world.Camera!.GlobalPosition.Y - ground!.Value).IsEqualApprox(world.CameraStartOffset.Y, 0.01);
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
    /// Every <c>edge_cliff</c> collision body (the <see cref="StaticBody3D"/> child of the <c>edge_cliff</c> mesh
    /// imported from <c>edge_cliff-col</c>) is in <see cref="MapLoader.EdgeGroup"/> and not in
    /// <see cref="MapLoader.TerrainGroup"/>. At the middle of the ledge beyond the road 52 roadblock (derived from the
    /// pack's <c>boundary.json</c>) <see cref="MapLoader.GroundOrEdgeHitAt"/> hits the cliff, and
    /// <see cref="MapLoader.TerrainHeightAt"/> doesn't return that hit.
    /// </summary>
    [TestCase]
    public async Task EdgeCliffBodies_AreInEdgeGroupAndIgnoredByTerrainHeight()
    {
        AssertPackPresent();
        using var runner = ISceneRunner.Load(WorldScenePath, true);
        await runner.SimulateFrames(5);
        var loader = Loader(runner);

        var cliffBodies = Descendants(loader.Tiles)
            .OfType<StaticBody3D>()
            .Where(body => body.GetParent() is MeshInstance3D { Name: var name } && name == MapLoader.EdgeCliffNodeName)
            .ToList();
        foreach (var body in cliffBodies)
        {
            var path = $"{body.GetParent()!.GetParent()!.Name}/{body.GetParent()!.Name}/{body.Name}";
            AssertBool(body.IsInGroup(MapLoader.EdgeGroup)).OverrideFailureMessage($"{path} is not in {MapLoader.EdgeGroup}").IsTrue();
            AssertBool(body.IsInGroup(MapLoader.TerrainGroup)).OverrideFailureMessage($"{path} is in {MapLoader.TerrainGroup}").IsFalse();
            AssertBool(body.IsInGroup(MapLoader.RoadGroup)).OverrideFailureMessage($"{path} is in {MapLoader.RoadGroup}").IsFalse();
        }

        AssertInt(loader.GetTree().GetNodesInGroup(MapLoader.EdgeGroup).Count).IsEqual(cliffBodies.Count);
        if (!IsFullPack(loader, "the road 52 ledge"))
        {
            return;
        }

        AssertInt(cliffBodies.Count).OverrideFailureMessage("the full pack has no edge_cliff collision body").IsGreater(0);
        var ledge = Road52LedgeMiddle();
        var hit = loader.GroundOrEdgeHitAt((float)ledge.X, (float)ledge.Z);
        AssertBool(hit is not null).OverrideFailureMessage(System.FormattableString.Invariant($"no ground or edge hit at the ledge middle {ledge}")).IsTrue();
        AssertBool(hit!.Value.Body.IsInGroup(MapLoader.EdgeGroup))
            .OverrideFailureMessage($"the hit at the ledge middle {ledge} is {hit.Value.Body.Name}, not the cliff")
            .IsTrue();
        var terrain = loader.RaycastTerrain((float)ledge.X, (float)ledge.Z);
        AssertBool(terrain is null || terrain.Value.Body.IsInGroup(MapLoader.TerrainGroup) && terrain.Value.Body != hit.Value.Body)
            .OverrideFailureMessage($"TerrainHeightAt at the ledge middle {ledge} returned the cliff")
            .IsTrue();
    }

    /// <summary>
    /// A look-at point over Kirkkojärvi, 25 m past B's west edge (x −1103 there), is N/A: a downward ray there hits
    /// neither terrain nor cliff, although the point is inside a tile's <c>bounds_local</c>. The z is the middle of the
    /// lake's water fence on that edge, and the point is checked to be outside the boundary ring, both from the pack's
    /// <c>boundary.json</c>.
    /// </summary>
    [TestCase]
    public async Task LookAtOverLakePastWestNotch_IsOutsideBuiltArea()
    {
        AssertPackPresent();
        using var runner = ISceneRunner.Load(WorldScenePath, true);
        await runner.SimulateFrames(5);
        var loader = Loader(runner);
        if (!IsFullPack(loader, "the lake past B's west notch"))
        {
            return;
        }

        using var boundary = ReadBoundary();
        var root = boundary.RootElement;
        // The west water fences: type "water", outward −x.
        var westWater = root.GetProperty("fences").EnumerateArray()
            .Where(f => f.GetProperty("type").GetString() == "water" && f.GetProperty("outward")[0].GetDouble() < -0.9)
            .ToList();
        AssertInt(westWater.Count).OverrideFailureMessage("boundary.json has no west water fence").IsGreater(0);
        var edgeX = westWater.Select(f => f.GetProperty("start")[0].GetDouble()).Average();
        var minZ = westWater.Min(f => System.Math.Min(f.GetProperty("start")[2].GetDouble(), f.GetProperty("end")[2].GetDouble()));
        var maxZ = westWater.Max(f => System.Math.Max(f.GetProperty("start")[2].GetDouble(), f.GetProperty("end")[2].GetDouble()));
        const double lookAtX = -1130;
        var lookAtZ = (minZ + maxZ) / 2;
        var ledgeM = root.GetProperty("cliff").GetProperty("ledge_m").GetDouble();

        AssertFloat(lookAtX).OverrideFailureMessage($"x {lookAtX} isn't past the west edge x {edgeX} and its ledge").IsLess(edgeX - ledgeM);
        AssertBool(BoundaryRing(root).Contains(lookAtX, lookAtZ))
            .OverrideFailureMessage($"({lookAtX}, {lookAtZ}) is inside the boundary ring")
            .IsFalse();
        AssertBool(loader.Manifest!.ContainsGodotPoint(lookAtX, lookAtZ))
            .OverrideFailureMessage($"({lookAtX}, {lookAtZ}) is outside every tile's bounds_local")
            .IsTrue();

        var preset = new CameraPreset("lake", new LocalPoint(-1000, 120, lookAtZ), new LocalPoint(lookAtX, 82, lookAtZ), 70, false);
        AssertBool(PresetPlacement.LooksAtMap(preset, (x, z) => loader.GroundOrEdgeHitAt((float)x, (float)z)?.Position.Y))
            .OverrideFailureMessage($"a downward ray at ({lookAtX}, {lookAtZ:0.###}) hit {loader.GroundOrEdgeHitAt((float)lookAtX, (float)lookAtZ)?.Body.GetPath()}")
            .IsFalse();
    }

    /// <summary>
    /// The <c>world-edge</c> preset looks at the cliff ledge, so it isn't N/A on the full pack although its camera is
    /// over nothing; on a test-area pack it is N/A (ADR 0008). It is read from the repo's
    /// <c>maps/&lt;id&gt;/camera_presets.json</c>, the pipeline input, so the test checks the current preset even before
    /// the pack is regenerated. A look-at in the playable area (the pack's <c>overview</c> preset) is inside on any pack.
    /// </summary>
    [TestCase]
    public async Task PackPresets_LookAtTheMap()
    {
        AssertPackPresent();
        using var runner = ISceneRunner.Load(WorldScenePath, true);
        await runner.SimulateFrames(5);
        var loader = Loader(runner);
        var presetsPath = $"res://maps/{MapId}/{MapPack.CameraPresetsFile}";
        var presets = CameraPresetsReader.Parse(FileAccess.GetFileAsString(presetsPath), presetsPath).Presets;
        bool LooksAtMap(CameraPreset preset) =>
            PresetPlacement.LooksAtMap(preset, (x, z) => loader.GroundOrEdgeHitAt((float)x, (float)z)?.Position.Y);

        AssertBool(presets.ContainsKey("overview")).OverrideFailureMessage($"{presetsPath} has no overview preset").IsTrue();
        AssertBool(LooksAtMap(presets["overview"])).OverrideFailureMessage("overview is N/A").IsTrue();

        var sourcePath = System.IO.Path.GetFullPath(
            System.IO.Path.Combine(ProjectSettings.GlobalizePath("res://"), "..", "maps", MapId, MapPack.CameraPresetsFile));
        AssertBool(FileAccess.FileExists(sourcePath)).OverrideFailureMessage($"no presets file at {sourcePath}").IsTrue();
        var sourcePresets = CameraPresetsReader.Parse(FileAccess.GetFileAsString(sourcePath), sourcePath).Presets;
        AssertBool(sourcePresets.TryGetValue("world-edge", out var worldEdge))
            .OverrideFailureMessage($"{sourcePath} has no world-edge preset")
            .IsTrue();

        var lookAt = worldEdge!.LookAt;
        var where = System.FormattableString.Invariant($"x {lookAt.X:0.###}, z {lookAt.Z:0.###}");
        var hit = loader.GroundOrEdgeHitAt((float)lookAt.X, (float)lookAt.Z);
        if (loader.Manifest!.Area != "full")
        {
            AssertBool(hit is null)
                .OverrideFailureMessage($"world-edge isn't N/A on the '{loader.Manifest.Area}' pack (hit {hit?.Body.GetPath()})")
                .IsTrue();
            return;
        }

        AssertBool(worldEdge.GroundRelative).IsFalse();
        AssertBool(hit is not null).OverrideFailureMessage($"world-edge is N/A: nothing under its look-at {where} ({sourcePath})").IsTrue();
        AssertBool(hit!.Value.Body.IsInGroup(MapLoader.EdgeGroup))
            .OverrideFailureMessage($"world-edge looks at {hit.Value.Body.GetPath()}, not the cliff")
            .IsTrue();
        AssertBool(LooksAtMap(worldEdge)).IsTrue();
    }

    /// <summary>
    /// The importer turns each <c>bld_*-colonly</c> glTF node into a standalone <see cref="StaticBody3D"/> (directly
    /// under the tile) holding a <see cref="CollisionShape3D"/>, and makes no mesh for it.
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
    /// In the imported tile scenes (before <see cref="MapLoader"/> touches them), each building body keeps its glTF
    /// node name without the <c>-colonly</c> suffix, <c>bld_&lt;id&gt;_c&lt;k&gt;</c>: no body is renamed by Godot, and
    /// re-appending the suffix gives exactly the tile's <c>collision_nodes</c> in <c>buildings.json</c>.
    /// </summary>
    [TestCase]
    public void ImportedTiles_NameEachBuildingBodyAfterItsCollisionNodeWithoutTheSuffix()
    {
        AssertPackPresent();
        var manifestPath = $"res://maps/{MapId}/{MapPack.ManifestFile}";
        var manifest = MapManifestReader.Parse(FileAccess.GetFileAsString(manifestPath), manifestPath);
        var buildingsPath = $"res://maps/{MapId}/{MapPack.BuildingsFile}";
        var buildings = BuildingsReader.Parse(FileAccess.GetFileAsString(buildingsPath), buildingsPath);
        var nodesByTile = BuildingBodies.CollisionNodesByTile(buildings);

        var failures = new List<string>();
        var bodyCount = 0;
        foreach (var tile in manifest.Tiles)
        {
            var scene = GD.Load<PackedScene>($"res://maps/{MapId}/{tile.File}").Instantiate();
            try
            {
                var names = scene.GetChildren().OfType<StaticBody3D>().Select(body => body.Name.ToString()).ToList();
                bodyCount += names.Count;
                failures.AddRange(names
                    .Where(name => !System.Text.RegularExpressions.Regex.IsMatch(name, "^bld_.+_c[0-9]+$"))
                    .Select(name => $"{tile.Id}/{name}"));

                var expected = nodesByTile.TryGetValue(tile.Id, out var nodes) ? nodes : [];
                var resolved = names.Select(name => name + MapLoader.CollisionOnlySuffix).Order(System.StringComparer.Ordinal);
                if (!resolved.SequenceEqual(expected.Order(System.StringComparer.Ordinal)))
                {
                    failures.Add($"{tile.Id}: body names + {MapLoader.CollisionOnlySuffix} aren't its collision_nodes");
                }
            }
            finally
            {
                scene.Free();
            }
        }

        AssertInt(bodyCount).IsGreater(0);
        AssertThat(string.Join("; ", failures.Take(20))).IsEqual("");
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

    private static bool IsFullPack(MapLoader loader, string what)
    {
        var area = loader.Manifest!.Area;
        if (area == "full")
        {
            return true;
        }

        GD.Print($"skipped {what}: the '{MapId}' pack's area is '{area}', and boundary B is only in the full pack");
        return false;
    }

    private static System.Text.Json.JsonDocument ReadBoundary()
    {
        var path = $"res://maps/{MapId}/boundary.json";
        AssertBool(FileAccess.FileExists(path)).OverrideFailureMessage($"no {path}; {MapPack.RegenHint(MapId)}").IsTrue();
        return System.Text.Json.JsonDocument.Parse(FileAccess.GetFileAsString(path));
    }

    /// <summary>The boundary ring of <c>boundary.json</c> (Godot x, z), closed, as a polygon without holes.</summary>
    private static PoiPolygon BoundaryRing(System.Text.Json.JsonElement root)
    {
        var ring = root.GetProperty("boundary").GetProperty("ring").EnumerateArray()
            .Select(p => new PlanarPoint(p[0].GetDouble(), p[1].GetDouble()))
            .ToList();
        if (ring[0] != ring[^1])
        {
            ring.Add(ring[0]);
        }

        return new PoiPolygon(ring, []);
    }

    /// <summary>
    /// The middle of the cliff ledge beyond the road 52 roadblock on B's north edge: the roadblock nearest the
    /// <c>world-edge</c> camera (ADR 0015) moved half of <c>cliff.ledge_m</c> along its <c>outward</c>.
    /// </summary>
    private static (double X, double Z) Road52LedgeMiddle()
    {
        using var boundary = ReadBoundary();
        var root = boundary.RootElement;
        var ledgeM = root.GetProperty("cliff").GetProperty("ledge_m").GetDouble();
        // The world-edge camera is 40 m along the edge and 30 m outward from the roadblock (ADR 0015).
        var presetsPath = $"res://maps/{MapId}/{MapPack.CameraPresetsFile}";
        var presets = CameraPresetsReader.Parse(FileAccess.GetFileAsString(presetsPath), presetsPath).Presets;
        AssertBool(presets.ContainsKey("world-edge")).OverrideFailureMessage($"{presetsPath} has no world-edge preset").IsTrue();
        var (cameraX, cameraZ) = (presets["world-edge"].Position.X, presets["world-edge"].Position.Z);
        var roadblock = root.GetProperty("roadblocks").EnumerateArray()
            .MinBy(r => System.Math.Pow(r.GetProperty("position")[0].GetDouble() - cameraX, 2)
                + System.Math.Pow(r.GetProperty("position")[2].GetDouble() - cameraZ, 2));
        var position = roadblock.GetProperty("position");
        var outward = roadblock.GetProperty("outward");
        return (
            position[0].GetDouble() + (outward[0].GetDouble() * ledgeM / 2),
            position[2].GetDouble() + (outward[2].GetDouble() * ledgeM / 2));
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
