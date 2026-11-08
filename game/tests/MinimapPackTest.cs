using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using GdUnit4;
using Godot;
using Roadman.Core.Edge;
using Roadman.Core.Maps;
using Roadman.Core.Minimap;
using Roadman.Core.Saves;
using Roadman.Hud;
using Roadman.Scenes;
using static GdUnit4.Assertions;

namespace Roadman.Tests;

/// <summary>
/// The minimap's data across map loads and with pack files missing (<c>docs/specs/minimap.md</c>, criterion 14), on a
/// direct <see cref="World"/>. The reload test uses the test-area pack; the others copy it to a temporary pack
/// <c>res://maps/tmp-*</c> with one file left out or changed, clearing <see cref="MapLoader.PackFolderOverride"/> first
/// (it would load the source pack instead), and delete it after the test.
/// </summary>
[TestSuite]
[RequireGodotRuntime]
public class MinimapPackTest
{
    /// <summary>Process frames for the minimap to draw after the player is placed.</summary>
    private const int DrawFrames = 2;

    private string _tempPack = "";
    private ISceneRunner? _runner;

    [BeforeTest]
    public void UseTestPack() => TestPacks.UseTestArea();

    [AfterTest]
    public void CleanUp()
    {
        _runner?.Dispose();
        _runner = null;
        TestPacks.UseFullPack();
        if (_tempPack != "" && Directory.Exists(_tempPack))
        {
            Directory.Delete(_tempPack, recursive: true);
        }

        _tempPack = "";
    }

    // ----- Criterion 14 -----

    /// <summary>
    /// A map reload rebuilds the minimap's data (one more <see cref="MinimapHud.DataBuildCount"/>, a new
    /// <see cref="MinimapHud.Data"/> with the same content) and clears its markers; the minimap shows again once the
    /// player has respawned, with no error.
    /// </summary>
    [TestCase]
    public async Task Ac14_AMapReload_RebuildsTheMinimapData_AndClearsItsMarkers()
    {
        WorldEdgeTest.AssertPackPresent();
        var capture = new ErrorCapture();
        OS.AddLogger(capture);
        try
        {
            using var runner = ISceneRunner.Load(WorldEdgeTest.NewWorld(WorldEdgeTest.TestSeed), true);
            var (world, minimap) = await MinimapTest.StartWorld(runner);
            var builds = minimap.DataBuildCount;
            var data = minimap.Data;
            AssertInt(builds).OverrideFailureMessage("the data wasn't built at the first load").IsGreaterEqual(1);
            AssertInt(data.Roads.Count).IsGreater(0);
            var player = world.Player!.GlobalPosition;
            minimap.Markers.Add(new MinimapMarker("test:near", player.X + 10, player.Z));
            minimap.Markers.Add(new MinimapMarker("test:far", player.X + 5000, player.Z, clampToRim: true));
            minimap.UpdateFrame();
            AssertInt(minimap.DrawnMarkerCount).OverrideFailureMessage("the markers weren't drawn before the reload").IsEqual(2);

            AssertBool(world.MapLoader.Load()).IsTrue();
            AssertInt(minimap.DataBuildCount).OverrideFailureMessage("the reload didn't rebuild the minimap's data").IsEqual(builds + 1);
            AssertThat(minimap.Data).OverrideFailureMessage("the reload kept the old data").IsNotSame(data);
            AssertInt(minimap.Markers.Count).OverrideFailureMessage("the reload kept the markers").IsEqual(0);
            AssertThat(minimap.Data.Roads.Select(r => r.EdgeId).ToList()).IsEqual(data.Roads.Select(r => r.EdgeId).ToList());
            AssertInt(minimap.Data.Pois.Count).IsEqual(data.Pois.Count);

            AssertBool(await WorldEdgeTest.WaitForSpawn(world)).OverrideFailureMessage("the player didn't spawn after the reload").IsTrue();
            await ProcessFrames(world, DrawFrames);
            AssertBool(minimap.IsShown).OverrideFailureMessage("the minimap isn't shown after the reload").IsTrue();
            AssertInt(minimap.DrawnMarkerCount).IsEqual(0);
            AssertBool(minimap.HasBackground).IsTrue();
        }
        finally
        {
            OS.RemoveLogger(capture);
        }

        AssertThat(string.Join(" | ", capture.ErrorsOnly)).IsEqual("");
    }

    /// <summary>
    /// A pack without <c>minimap.json</c>: one error naming <c>res://maps/&lt;id&gt;/minimap.json</c> and
    /// <c>/regen-map &lt;id&gt;</c>, and no other; the tiles, road graph, places and boundary load; the minimap shows
    /// with no background image, the void outside and the ring filled with the ground colour, and the roads.
    /// </summary>
    [TestCase]
    public async Task Ac14_PackWithoutMinimapJson_OneErrorNamingThePathAndRegenHint_StillLoads_RingFilledWithGround()
    {
        WorldEdgeTest.AssertPackPresent();
        var mapId = NewMapId("tmp-mm-no-json");
        CopyPack(mapId, leaveOut: MapPack.MinimapFile);
        var (world, minimap, errors) = await LoadTempPack(mapId);
        var path = $"res://maps/{mapId}/{MapPack.MinimapFile}";
        AssertSingleError(errors, path, mapId);

        var loader = world.MapLoader;
        AssertThat(loader.MinimapImage).IsNull();
        AssertThat(loader.MinimapTexture).IsNull();
        AssertThat(loader.RoadGraph).IsNotNull();
        AssertThat(loader.Places).IsNotNull();
        AssertThat(loader.Boundary).IsNotNull();
        AssertGroundFilledRing(minimap);
        AssertInt(minimap.DrawnSegmentCount).IsGreater(0);
    }

    /// <summary>
    /// A pack whose <c>minimap.json</c> gives another size than the PNG's (one more column, with the bounds widened by
    /// one pixel so the JSON itself is valid): one error naming the image path and <c>/regen-map &lt;id&gt;</c>, and
    /// no other; everything else loads; the minimap draws the ground-filled ring with no background.
    /// </summary>
    [TestCase]
    public async Task Ac14_PackWithMismatchedImageSize_OneErrorNamingThePathAndRegenHint_StillLoads_RingFilledWithGround()
    {
        WorldEdgeTest.AssertPackPresent();
        var mapId = NewMapId("tmp-mm-size");
        CopyPack(mapId, changeMinimap: json =>
        {
            var metres = json["m_per_px"]!.GetValue<double>();
            json["width"] = json["width"]!.GetValue<int>() + 1;
            json["bounds_local"]!["max_x"] = json["bounds_local"]!["max_x"]!.GetValue<double>() + metres;
        });
        var (world, minimap, errors) = await LoadTempPack(mapId);
        var path = $"res://maps/{mapId}/{MapPack.MinimapImageFile}";
        AssertSingleError(errors, path, mapId);
        AssertThat(errors[0]).Contains($"res://maps/{mapId}/{MapPack.MinimapFile}");

        var loader = world.MapLoader;
        AssertThat(loader.MinimapImage).IsNull();
        AssertThat(loader.MinimapTexture).IsNull();
        AssertThat(loader.RoadGraph).IsNotNull();
        AssertThat(loader.Places).IsNotNull();
        AssertThat(loader.Boundary).IsNotNull();
        AssertGroundFilledRing(minimap);
        AssertInt(minimap.DrawnSegmentCount).IsGreater(0);
    }

    /// <summary>
    /// A pack without <c>road_graph.json</c>: the only error is <c>MapLoader</c>'s naming that file; the minimap shows
    /// with the background, the void and ring, and the point-of-interest data, and no roads or labels.
    /// </summary>
    [TestCase]
    public async Task Ac14_PackWithoutRoadGraph_MinimapShowsTheOtherLayers_NoErrorBeyondMapLoaders()
    {
        WorldEdgeTest.AssertPackPresent();
        var mapId = NewMapId("tmp-mm-no-roads");
        CopyPack(mapId, leaveOut: MapPack.RoadGraphFile);
        var (world, minimap, errors) = await LoadTempPack(mapId);
        AssertOnlyErrorsNaming(errors, $"res://maps/{mapId}/{MapPack.RoadGraphFile}", expectAtLeast: 1);

        AssertThat(world.MapLoader.RoadGraph).IsNull();
        AssertBool(minimap.IsShown).IsTrue();
        AssertInt(minimap.Data.Roads.Count).IsEqual(0);
        AssertInt(minimap.DrawnSegmentCount).IsEqual(0);
        AssertInt(minimap.DrawnLabelCount).IsEqual(0);
        AssertBool(minimap.HasBackground).OverrideFailureMessage("no background without the road graph").IsTrue();
        AssertBool(minimap.DrawsVoid).IsTrue();
        AssertThat(minimap.Data.Ring).IsNotNull();
        AssertInt(minimap.Data.Pois.Count).IsEqual(world.MapLoader.Places!.Pois.Items.Count);
    }

    /// <summary>
    /// A pack without <c>pois.json</c>: the only errors are <c>MapLoader</c>'s naming that file; the minimap shows with
    /// the background, the void and ring, and the roads, and no symbols.
    /// </summary>
    [TestCase]
    public async Task Ac14_PackWithoutPoisJson_MinimapShowsTheOtherLayers_NoErrorBeyondMapLoaders()
    {
        WorldEdgeTest.AssertPackPresent();
        var mapId = NewMapId("tmp-mm-no-pois");
        CopyPack(mapId, leaveOut: MapPack.PoisFile);
        var (world, minimap, errors) = await LoadTempPack(mapId);
        AssertOnlyErrorsNaming(errors, $"res://maps/{mapId}/{MapPack.PoisFile}", expectAtLeast: 1);

        AssertThat(world.MapLoader.Places).IsNull();
        AssertBool(minimap.IsShown).IsTrue();
        AssertInt(minimap.Data.Pois.Count).IsEqual(0);
        AssertInt(minimap.DrawnSymbolCount).IsEqual(0);
        AssertInt(minimap.DrawnSegmentCount).IsGreater(0);
        AssertBool(minimap.HasBackground).IsTrue();
        AssertBool(minimap.DrawsVoid).IsTrue();
        AssertThat(minimap.Data.Ring).IsNotNull();
    }

    /// <summary>
    /// A pack without <c>boundary.json</c>: the errors are <c>MapLoader</c>'s naming that file and the spawn error
    /// that follows from it (it names the file too), nothing else. The spawn draw needs the boundary, so the test
    /// places the player as a load does (<see cref="World.LoadPlayer"/> at a road node that passes
    /// <see cref="World.CheckLoadPlacement"/>); the minimap then shows with the background and the roads, the ground
    /// colour over the whole circle (no void) and no ring or roadblocks.
    /// </summary>
    [TestCase]
    public async Task Ac14_PackWithoutBoundaryJson_MinimapShowsTheOtherLayers_NoErrorBeyondMapLoaders()
    {
        WorldEdgeTest.AssertPackPresent();
        var mapId = NewMapId("tmp-mm-no-boundary");
        CopyPack(mapId, leaveOut: MapPack.BoundaryFile);
        var (world, minimap, errors) = await LoadTempPack(mapId, expectSpawn: false);
        var path = $"res://maps/{mapId}/{MapPack.BoundaryFile}";
        AssertOnlyErrorsNaming(errors, path, expectAtLeast: 1);
        AssertBool(errors.Any(e => e.StartsWith("MapLoader", StringComparison.Ordinal) || e.Contains("MapLoader:"))).OverrideFailureMessage($"no MapLoader error: {string.Join(" | ", errors)}").IsTrue();

        AssertThat(world.MapLoader.Boundary).IsNull();
        AssertThat(world.Player).IsNull();
        AssertBool(minimap.IsShown).OverrideFailureMessage("shown without a player").IsFalse();

        var graph = world.MapLoader.RoadGraph!;
        Vector3? feet = null;
        foreach (var node in graph.Nodes)
        {
            if (world.CheckLoadPlacement((float)node.Position.X, (float)node.Position.Z, out var at))
            {
                feet = at;
                break;
            }
        }

        AssertThat(feet).OverrideFailureMessage("no road node passes the load placement check").IsNotNull();
        var save = new SavePlayer(new LocalPoint(feet!.Value.X, feet.Value.Y, feet.Value.Z), 0d, 0, [], SaveEquipped.Empty, 0);
        AssertBool(world.LoadPlayer(save)).IsTrue();
        await ProcessFrames(world, DrawFrames);

        AssertBool(minimap.IsShown).OverrideFailureMessage("the minimap isn't shown without a boundary").IsTrue();
        AssertThat(minimap.Data.Ring).IsNull();
        AssertInt(minimap.Data.Roadblocks.Count).IsEqual(0);
        AssertBool(minimap.DrawsVoid).OverrideFailureMessage("the void is drawn without a boundary").IsFalse();
        AssertBool(minimap.HasBackground).IsTrue();
        AssertInt(minimap.DrawnSegmentCount).IsGreater(0);
        AssertInt(minimap.DrawnGroundPolygonCount).IsEqual(0);
    }

    // ----- Helpers -----

    private static string NewMapId(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..30];

    /// <summary>
    /// A direct <see cref="World"/> on <paramref name="mapId"/> with an error capture over the whole load; waits for the
    /// spawn (or, without <paramref name="expectSpawn"/>, for the spawn error) and the minimap's frames.
    /// </summary>
    private async Task<(World World, MinimapHud Minimap, IReadOnlyList<string> Errors)> LoadTempPack(string mapId, bool expectSpawn = true)
    {
        var capture = new ErrorCapture();
        OS.AddLogger(capture);
        var world = WorldEdgeTest.NewWorld(WorldEdgeTest.TestSeed);
        world.MapId = mapId;
        _runner = ISceneRunner.Load(world, true);
        try
        {
            AssertThat(world.MapLoader.ResolvedMapId).IsEqual(mapId);
            if (expectSpawn)
            {
                AssertBool(await WorldEdgeTest.WaitForSpawn(world)).OverrideFailureMessage($"the player didn't spawn ({world.LastSpawnFailure})").IsTrue();
            }
            else
            {
                await WorldEdgeTest.WaitUntil(world, () => capture.ErrorsOnly.Any(e => e.Contains("no player spawn")), world.SpawnPhysicsWaitFrames + 30);
            }

            await ProcessFrames(world, DrawFrames);
        }
        finally
        {
            OS.RemoveLogger(capture);
        }

        AssertThat(world.MapLoader.Manifest).OverrideFailureMessage("the temporary pack didn't load").IsNotNull();
        AssertInt(world.MapLoader.LoadedTileCount).IsEqual(world.MapLoader.Manifest!.Tiles.Count);
        AssertThat(world.Minimap).IsNotNull();
        GD.Print($"MinimapPackTest: {mapId}: errors: {string.Join(" | ", capture.ErrorsOnly)}");
        return (world, world.Minimap!, capture.ErrorsOnly);
    }

    private static async Task ProcessFrames(Node node, int count)
    {
        for (var i = 0; i < count; i++)
        {
            await node.ToSignal(node.GetTree(), SceneTree.SignalName.ProcessFrame);
        }
    }

    /// <summary>Exactly one error, naming <paramref name="path"/> and <c>/regen-map &lt;mapId&gt;</c>.</summary>
    private static void AssertSingleError(IReadOnlyList<string> errors, string path, string mapId)
    {
        AssertInt(errors.Count).OverrideFailureMessage($"expected one error naming {path}; got: {string.Join(" | ", errors)}").IsEqual(1);
        AssertThat(errors[0]).Contains(path);
        AssertThat(errors[0]).Contains($"/regen-map {mapId}");
    }

    /// <summary>Every error names <paramref name="path"/> (from <c>MapLoader</c>, or the spawn error that follows from it); none is the minimap's own.</summary>
    private static void AssertOnlyErrorsNaming(IReadOnlyList<string> errors, string path, int expectAtLeast)
    {
        AssertInt(errors.Count).OverrideFailureMessage($"no error naming {path}").IsGreaterEqual(expectAtLeast);
        AssertThat(string.Join(" | ", errors.Where(e => !e.Contains(path))))
            .OverrideFailureMessage($"errors that don't name {path}").IsEqual("");
        AssertThat(string.Join(" | ", errors.Where(e => e.Contains("minimap", StringComparison.OrdinalIgnoreCase))))
            .OverrideFailureMessage("minimap errors").IsEqual("");
    }

    /// <summary>Shown, no background, the void drawn, and the ring polygon cut to the circle filled with the ground colour.</summary>
    private static void AssertGroundFilledRing(MinimapHud minimap)
    {
        AssertBool(minimap.IsShown).OverrideFailureMessage("the minimap isn't shown without its image").IsTrue();
        AssertBool(minimap.HasBackground).OverrideFailureMessage("a background drawn without an image").IsFalse();
        AssertBool(minimap.DrawsVoid).IsTrue();
        AssertThat(minimap.Data.Ring).IsNotNull();
        AssertInt(minimap.DrawnGroundPolygonCount).OverrideFailureMessage("the ring isn't filled with the ground colour").IsGreater(0);
    }

    /// <summary>
    /// Copies the test pack (<see cref="TestPacks.PackPath"/>) to <c>res://maps/&lt;mapId&gt;</c>: the manifest and the
    /// tile import files, <c>road_graph.json</c>, <c>buildings.json</c>, <c>pois.json</c> and <c>boundary.json</c>
    /// (their <c>map_id</c> set) and the minimap files, without <paramref name="leaveOut"/>, and with
    /// <paramref name="changeMinimap"/> applied to <c>minimap.json</c>. Clears the override afterwards.
    /// </summary>
    private void CopyPack(string mapId, string? leaveOut = null, Action<JsonNode>? changeMinimap = null)
    {
        var source = ProjectSettings.GlobalizePath(TestPacks.PackPath);
        TestPacks.UseFullPack(); // The override would load the source pack instead of the temporary one.
        _tempPack = ProjectSettings.GlobalizePath($"res://maps/{mapId}");
        Directory.CreateDirectory(Path.Combine(_tempPack, "tiles"));
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(source, MapPack.ManifestFile)))!;
        manifest["map_id"] = mapId;
        File.WriteAllText(Path.Combine(_tempPack, MapPack.ManifestFile), manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        foreach (var tile in manifest["tiles"]!.AsArray())
        {
            var file = tile!["file"]!.GetValue<string>();
            var import = File.ReadAllLines(Path.Combine(source, file + ".import")).Where(l => !l.StartsWith("uid=", StringComparison.Ordinal));
            File.WriteAllLines(Path.Combine(_tempPack, file + ".import"), import);
        }

        foreach (var file in new[] { MapPack.RoadGraphFile, MapPack.BuildingsFile, MapPack.PoisFile, MapPack.BoundaryFile })
        {
            if (file == leaveOut)
            {
                continue;
            }

            var json = JsonNode.Parse(File.ReadAllText(Path.Combine(source, file)))!;
            json["map_id"] = mapId;
            File.WriteAllText(Path.Combine(_tempPack, file), json.ToJsonString());
        }

        TestPacks.CopyMinimap(source, _tempPack, mapId);
        var minimapJson = Path.Combine(_tempPack, MapPack.MinimapFile);
        if (changeMinimap is not null)
        {
            var json = JsonNode.Parse(File.ReadAllText(minimapJson))!;
            changeMinimap(json);
            File.WriteAllText(minimapJson, json.ToJsonString());
        }

        if (leaveOut is not null)
        {
            File.Delete(Path.Combine(_tempPack, leaveOut));
            AssertBool(File.Exists(Path.Combine(_tempPack, leaveOut))).IsFalse();
        }
    }
}
