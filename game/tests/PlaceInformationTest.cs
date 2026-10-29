using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using GdUnit4;
using Godot;
using Roadman.Core.Maps;
using Roadman.Debug;
using Roadman.Hud;
using Roadman.Scenes;
using static GdUnit4.Assertions;
using Directory = System.IO.Directory;
using File = System.IO.File;
using Path = System.IO.Path;

namespace Roadman.Tests;

/// <summary>
/// Place information in Godot (<c>docs/specs/place-information.md</c>, criteria 15 to 17; <c>Ac</c> numbers refer to
/// it): <see cref="MapLoader.Places"/> and the building bodies, the HUD street label and the debug place inspector.
/// They run on whichever Somero pack is built (full, or the CI test area from the committed fixture), so every
/// position comes from the loaded data: edges from <see cref="MapLoader.RoadGraph"/>, buildings and POIs from
/// <see cref="MapLoader.Places"/>, heights from <see cref="MapLoader.TerrainHeightAt"/> or ray hits. Like
/// <see cref="MapLoaderTest"/>, these fail rather than skip without the pack.
/// </summary>
[TestSuite]
[RequireGodotRuntime]
public class PlaceInformationTest
{
    private const string WorldScenePath = "res://scenes/world/World.tscn";
    private const string MapId = "somero";

    /// <summary>Spec "Test area": the one POI of the test area, a <c>shop=convenience</c> node, and its building.</summary>
    private const string ShopPoi = "osm:node/9523461057";
    private const string ShopBuilding = "osm:way/464653177";

    /// <summary>How high above the ground the camera is put for the HUD's downward ray.</summary>
    private const float CameraHeight = 25f;

    /// <summary>Grid step in metres when searching for a ground point far from every edge.</summary>
    private const float FarSearchStep = 10f;

    private string _tempPack = "";

    [AfterTest]
    public void DeleteTemporaryPack()
    {
        if (_tempPack != "" && Directory.Exists(_tempPack))
        {
            Directory.Delete(_tempPack, recursive: true);
        }

        _tempPack = "";
    }

    // ----- Criterion 15: Places and building bodies -----

    [TestCase]
    public async Task Ac15_LoadedPlaces_HaveTheFilesBuildingAndPoiCounts()
    {
        AssertPackPresent();
        using var runner = ISceneRunner.Load(WorldScenePath, true);
        await runner.SimulateFrames(2);
        var loader = ((World)runner.Scene()!).MapLoader;

        var buildings = JsonNode.Parse(FileAccess.GetFileAsString(loader.BuildingsPath))!;
        var pois = JsonNode.Parse(FileAccess.GetFileAsString(loader.PoisPath))!;
        AssertThat(loader.Places).OverrideFailureMessage("MapLoader.Places is null after loading the pack").IsNotNull();
        var places = loader.Places!;
        AssertInt(places.Buildings.Items.Count).IsEqual(buildings["buildings"]!.AsArray().Count);
        AssertInt(places.Pois.Items.Count).IsEqual(pois["pois"]!.AsArray().Count);
        AssertInt(places.Buildings.Items.Count).IsGreater(0);
        AssertInt(places.Pois.Items.Count).IsGreater(0);
        AssertThat(places.Buildings.MapId).IsEqual(MapId);
    }

    /// <summary>
    /// Every collision-only body under a tile (a <see cref="StaticBody3D"/> without a mesh parent) is in
    /// <see cref="MapLoader.BuildingGroup"/> and resolves to an existing building, each body under the name of one of
    /// its building's <c>collision_nodes</c>; every building has at least one body, and every listed collision node has
    /// exactly one.
    /// </summary>
    [TestCase]
    public async Task Ac15_EveryBuildingBody_IsInMapBuildingsAndResolvesToItsBuilding()
    {
        AssertPackPresent();
        using var runner = ISceneRunner.Load(WorldScenePath, true);
        await runner.SimulateFrames(2);
        var loader = ((World)runner.Scene()!).MapLoader;
        AssertThat(loader.Places).IsNotNull();
        var places = loader.Places!;

        var failures = new List<string>();
        var collisionOnly = 0;
        foreach (var tile in loader.Tiles.GetChildren())
        {
            foreach (var body in tile.GetChildren().OfType<StaticBody3D>())
            {
                collisionOnly++;
                if (!body.IsInGroup(MapLoader.BuildingGroup))
                {
                    failures.Add($"{tile.Name}/{body.Name} is not in {MapLoader.BuildingGroup}");
                }
            }
        }

        var grouped = loader.GetTree().GetNodesInGroup(MapLoader.BuildingGroup).ToList();
        AssertInt(grouped.Count).OverrideFailureMessage($"no body is in {MapLoader.BuildingGroup}").IsGreater(0);
        var bodiesPerBuilding = new Dictionary<string, int>();
        var bodyNames = new HashSet<string>();
        foreach (var node in grouped)
        {
            var name = node.Name.ToString();
            var id = loader.BuildingIdOf(node);
            if (id is null || !places.TryGetBuilding(id, out var building))
            {
                failures.Add($"{node.GetParent()?.Name}/{name} resolves to no building (got '{id}')");
                continue;
            }

            if (!building.CollisionNodes.Contains(name))
            {
                failures.Add($"{name} resolves to {id}, whose collision_nodes are [{string.Join(", ", building.CollisionNodes)}]");
            }

            if (node.GetParent()?.Name.ToString() != building.Tile)
            {
                failures.Add($"{name} is under tile {node.GetParent()?.Name}, but {id} is in {building.Tile}");
            }

            if (!bodyNames.Add(name))
            {
                failures.Add($"two bodies are named {name}");
            }

            bodiesPerBuilding[id] = bodiesPerBuilding.GetValueOrDefault(id) + 1;
        }

        foreach (var building in places.Buildings.Items)
        {
            if (bodiesPerBuilding.GetValueOrDefault(building.Id) == 0)
            {
                failures.Add($"{building.Id} has no body in {MapLoader.BuildingGroup}");
            }

            foreach (var node in building.CollisionNodes.Where(n => !bodyNames.Contains(n)))
            {
                failures.Add($"{building.Id}: no body named {node}");
            }
        }

        GD.Print(
            $"PlaceInformationTest: {grouped.Count} bodies in {MapLoader.BuildingGroup}, {collisionOnly} collision-only "
            + $"bodies under tiles, {places.Buildings.Items.Count} buildings");
        AssertInt(grouped.Count).IsEqual(collisionOnly);
        AssertThat(string.Join("; ", failures.Take(20))).IsEqual("");
    }

    [TestCase]
    public async Task Ac15_PackWithoutPoisJson_LoadsTilesAndRoadGraphAndReportsTheMissingFile()
    {
        AssertPackPresent();
        var mapId = $"tmp-no-pois-{System.Guid.NewGuid():N}"[..30];
        var tileCount = CopyPack(mapId, MapPack.RoadGraphFile, MapPack.BuildingsFile);
        AssertBool(File.Exists(Path.Combine(_tempPack, MapPack.PoisFile))).IsFalse();

        using var runner = ISceneRunner.Load(new Node3D { Name = "Root" }, true);
        var loader = new MapLoader { Name = "MapLoader", MapId = mapId };
        runner.Scene()!.AddChild(loader);
        var capture = new ErrorCapture();
        OS.AddLogger(capture);
        bool loaded;
        try
        {
            loaded = loader.Load();
            await runner.SimulateFrames(1);
        }
        finally
        {
            OS.RemoveLogger(capture);
        }

        AssertBool(loaded).IsTrue();
        AssertInt(loader.LoadedTileCount).IsEqual(tileCount);
        AssertThat(loader.RoadGraph).OverrideFailureMessage("the road graph didn't load from the pack copy").IsNotNull();
        AssertInt(loader.RoadGraph!.Edges.Count).IsGreater(0);
        AssertThat(loader.Places).IsNull();

        var path = $"res://maps/{mapId}/{MapPack.PoisFile}";
        AssertThat(loader.PoisPath).IsEqual(path);
        var errors = capture.Errors.Where(e => e.Contains(path)).ToList();
        AssertInt(errors.Count).OverrideFailureMessage($"no single error names {path}; got: {string.Join(" | ", capture.Errors)}")
            .IsEqual(1);
        AssertThat(errors[0]).Contains($"/regen-map {mapId}");

        // Without Places, building bodies resolve to nothing and the inspector says the pack has no place data.
        var body = loader.GetTree().GetNodesInGroup(MapLoader.BuildingGroup).FirstOrDefault();
        AssertThat(body).OverrideFailureMessage($"the pack copy has no body in {MapLoader.BuildingGroup}").IsNotNull();
        AssertThat(loader.BuildingIdOf(body)).IsNull();
        var inspector = new PlaceInspector { Name = "PlaceInspector", Loader = loader };
        runner.Scene()!.AddChild(inspector);
        AssertThat(inspector.InspectHit(body, Vector3.Zero)).IsEqual(PlaceText.NoPlacesMessage(mapId));
        AssertThat(inspector.SelectionId).IsNull();
    }

    // ----- Criterion 16: HUD street label -----

    /// <summary>
    /// The camera above the middle of a named ground edge: the HUD's own downward ray finds the reference point, and
    /// the label shows that edge's <see cref="StreetLabel.Format"/> text. A second update at the same place doesn't
    /// emit <c>StreetChanged</c> again.
    /// </summary>
    [TestCase]
    public async Task Ac16_CameraAboveANamedEdge_ShowsItsStreetLabelText()
    {
        AssertPackPresent();
        using var runner = ISceneRunner.Load(WorldScenePath, true);
        var world = await LoadWorld(runner);
        var graph = world.MapLoader.RoadGraph!;
        var hud = world.Hud;
        AssertFloat(hud.StreetMaxDistance).IsEqual(40f);
        var changes = new List<string>();
        hud.StreetChanged += changes.Add;

        var named = graph.Edges.Where(e => e.Names.Name is not null && !e.Bridge && !e.Tunnel).ToList();
        AssertInt(named.Count).OverrideFailureMessage("the road graph has no named ground edge").IsGreater(0);
        var (edge, reference) = FindEdgeUnderCamera(world, named);
        AssertThat(edge).OverrideFailureMessage(
            $"no named ground edge of {named.Count} is the nearest edge to the ground below a camera above its middle").IsNotNull();

        var expected = StreetLabel.Format(edge!);
        AssertThat(expected).StartsWith(edge!.Names.Name!);
        AssertThat(hud.UpdateStreet()).IsEqual(expected);
        AssertThat(hud.Text).IsEqual(expected);
        AssertThat(hud.Label.Text).IsEqual(expected);
        AssertThat(hud.LastReferencePoint).IsEqual(reference);
        GD.Print($"PlaceInformationTest: camera above {edge.Id} ({edge.Class}): '{expected}'");

        var emitted = changes.Count;
        AssertThat(changes.LastOrDefault()).IsEqual(expected);
        AssertThat(hud.UpdateStreet()).IsEqual(expected);
        AssertInt(changes.Count).OverrideFailureMessage("StreetChanged fired although the text didn't change").IsEqual(emitted);

        // The HUD's own physics frames keep the same text while the camera stays put.
        await runner.SimulateFrames(3);
        AssertThat(hud.Text).IsEqual(expected);
    }

    /// <summary>
    /// The camera above the middle of an unnamed service edge without ref, chosen so that the nearest edge to the
    /// reference point (the HUD's own downward ray) is that edge: the label shows "Service road".
    /// </summary>
    [TestCase]
    public async Task Ac16_CameraAboveAnUnnamedServiceEdge_ShowsServiceRoad()
    {
        AssertPackPresent();
        using var runner = ISceneRunner.Load(WorldScenePath, true);
        var world = await LoadWorld(runner);
        var graph = world.MapLoader.RoadGraph!;
        var hud = world.Hud;

        var service = graph.Edges
            .Where(e => e.Class == "service" && e.Names.Name is null && e.Names.Ref is null && !e.Bridge && !e.Tunnel)
            .ToList();
        AssertInt(service.Count).OverrideFailureMessage("the road graph has no unnamed ground service edge").IsGreater(0);
        var (edge, reference) = FindEdgeUnderCamera(world, service);
        AssertThat(edge).OverrideFailureMessage(
            $"no unnamed service edge of {service.Count} is the nearest edge to the ground below a camera above its middle").IsNotNull();
        AssertThat(graph.NearestEdge(ToLocal(reference!.Value), hud.StreetMaxDistance)!.Value.Edge.Id).IsEqual(edge!.Id);

        AssertThat(hud.UpdateStreet()).IsEqual("Service road");
        AssertThat(hud.Text).IsEqual("Service road");

        // The same point given directly.
        AssertThat(hud.UpdateStreetAt(reference)).IsEqual("Service road");
        GD.Print($"PlaceInformationTest: camera above service edge {edge.Id}");
    }

    /// <summary>
    /// A ground point more than <see cref="StreetLabelHud.StreetMaxDistance"/> from every edge, found on a grid over
    /// the tiles: with the camera above it the label is empty, after showing a street first.
    /// </summary>
    [TestCase]
    public async Task Ac16_CameraMoreThan40MetresFromEveryEdge_ShowsNothing()
    {
        AssertPackPresent();
        using var runner = ISceneRunner.Load(WorldScenePath, true);
        var world = await LoadWorld(runner);
        var loader = world.MapLoader;
        var graph = loader.RoadGraph!;
        var hud = world.Hud;

        // A street first, so the label has to change back to empty.
        var named = graph.Edges.Where(e => e.Names.Name is not null && !e.Bridge && !e.Tunnel).ToList();
        var (edge, _) = FindEdgeUnderCamera(world, named);
        AssertThat(edge).IsNotNull();
        AssertThat(hud.UpdateStreet()).IsEqual(StreetLabel.Format(edge!));

        Vector3? far = null;
        foreach (var tile in loader.Manifest!.Tiles)
        {
            var bounds = tile.Bounds;
            for (var x = bounds.MinX + (FarSearchStep / 2); x < bounds.MaxX && far is null; x += FarSearchStep)
            {
                // Bounds are east/north; Godot z is -north.
                for (var north = bounds.MinY + (FarSearchStep / 2); north < bounds.MaxY && far is null; north += FarSearchStep)
                {
                    var z = -north;
                    if (loader.TerrainHeightAt((float)x, (float)z) is not { } ground)
                    {
                        continue;
                    }

                    var point = new Vector3((float)x, ground, (float)z);
                    if (graph.NearestEdge(ToLocal(point), hud.StreetMaxDistance) is not null)
                    {
                        continue;
                    }

                    PlaceCameraAbove(world, point);
                    if (hud.ReferencePoint() is { } reference
                        && graph.NearestEdge(ToLocal(reference), hud.StreetMaxDistance) is null)
                    {
                        far = reference;
                    }
                }
            }
        }

        AssertThat(far).OverrideFailureMessage("no ground point of the pack is more than 40 m from every edge").IsNotNull();
        AssertThat(hud.UpdateStreet()).IsEqual("");
        AssertThat(hud.Text).IsEqual("");
        AssertThat(hud.LastReferencePoint).IsEqual(far);
        AssertThat(graph.NearestEdge(ToLocal(far!.Value), double.MaxValue)!.Value.Distance).IsGreater(hud.StreetMaxDistance);
        GD.Print($"PlaceInformationTest: no edge within 40 m of {far}");
    }

    // ----- Criterion 17: debug place inspector -----

    [TestCase]
    public async Task Ac17_I_TogglesTheInspectorInADebugBuild()
    {
        AssertPackPresent();
        AssertBool(OS.IsDebugBuild()).IsTrue();
        using var runner = ISceneRunner.Load(WorldScenePath, true);
        await runner.SimulateFrames(2);
        var world = (World)runner.Scene()!;
        var inspector = world.Inspector;
        AssertThat(inspector).OverrideFailureMessage("World added no PlaceInspector in a debug build").IsNotNull();

        AssertBool(InputMap.HasAction(PlaceInspector.ToggleAction)).IsTrue();
        var i = new InputEventKey { PhysicalKeycode = Key.I, Pressed = true };
        AssertBool(InputMap.EventIsAction(i, PlaceInspector.ToggleAction, exactMatch: true)).IsTrue();
        var toggles = new List<bool>();
        inspector!.Toggled += toggles.Add;
        AssertBool(inspector.Enabled).IsFalse();

        runner.SimulateKeyPressed(Key.I);
        await runner.AwaitIdleFrame();
        AssertBool(inspector.Enabled).IsTrue();
        AssertBool(inspector.Panel.Visible).IsTrue();
        AssertBool(inspector.Crosshair.Visible).IsTrue();

        runner.SimulateKeyPressed(Key.I);
        await runner.AwaitIdleFrame();
        AssertBool(inspector.Enabled).IsFalse();
        AssertBool(inspector.Panel.Visible).IsFalse();
        AssertBool(inspector.Crosshair.Visible).IsFalse();

        // The action itself, as any other key bound to it would send it.
        runner.SimulateActionPressed(PlaceInspector.ToggleAction);
        await runner.AwaitIdleFrame();
        AssertBool(inspector.Enabled).IsTrue();
        AssertThat(toggles).IsEqual(new List<bool> { true, false, true });
    }

    /// <summary>
    /// A ray straight down at the roof of a building with an address and a category source shows its id, category,
    /// <c>category_source</c> and address. The first such building whose roof the ray reaches is used.
    /// </summary>
    [TestCase]
    public async Task Ac17_RayAtABuilding_ShowsItsIdCategorySourceAndAddress()
    {
        AssertPackPresent();
        using var runner = ISceneRunner.Load(WorldScenePath, true);
        var world = await LoadWorld(runner);
        var loader = world.MapLoader;
        var inspector = world.Inspector!;
        var places = loader.Places!;

        var candidates = places.Buildings.Items
            .Where(b => b.Address.Street is not null && b.Address.HouseNumber is not null && b.CategorySource is not null)
            .ToList();
        AssertInt(candidates.Count).OverrideFailureMessage("no building has a street, a house number and a category source").IsGreater(0);

        Building? building = null;
        (Vector3 From, Vector3 To)? ray = null;
        foreach (var candidate in candidates)
        {
            ray = RoofRay(loader, candidate);
            if (ray is not null)
            {
                building = candidate;
                break;
            }
        }

        AssertThat(building).OverrideFailureMessage("no candidate building's roof is the first hit of a ray from above").IsNotNull();
        AssertBool(inspector.SetEnabled(true)).IsTrue();
        var text = inspector.InspectRay(ray!.Value.From, ray.Value.To);

        AssertThat(inspector.SelectedBuilding).IsSame(building);
        AssertThat(inspector.SelectedPoi).IsNull();
        AssertThat(inspector.SelectionId).IsEqual(building!.Id);
        AssertThat(inspector.PanelText).IsEqual(text);
        AssertThat(text)
            .Contains(building.Id)
            .Contains($"category: {PlaceText.WireName(building.Category)}")
            .Contains($"category_source: {PlaceText.WireName(building.CategorySource!.Value)}")
            .Contains(building.Address.Street!)
            .Contains(building.Address.HouseNumber!);
        AssertThat(text).IsEqual(PlaceText.Describe(building, places));
        GD.Print($"PlaceInformationTest: inspected {building.Id} ({PlaceText.WireName(building.Category)})");
    }

    /// <summary>
    /// The real per-frame path: the camera above a building, looking straight down, so the ray through the screen
    /// centre (<see cref="PlaceInspector.InspectCentre"/>) reaches its roof.
    /// </summary>
    [TestCase]
    public async Task Ac17_CameraAimedAtABuilding_InspectCentreShowsIt()
    {
        AssertPackPresent();
        using var runner = ISceneRunner.Load(WorldScenePath, true);
        var world = await LoadWorld(runner);
        var loader = world.MapLoader;
        var inspector = world.Inspector!;

        Building? building = null;
        (Vector3 From, Vector3 To)? ray = null;
        foreach (var candidate in loader.Places!.Buildings.Items)
        {
            ray = RoofRay(loader, candidate);
            if (ray is not null)
            {
                building = candidate;
                break;
            }
        }

        AssertThat(building).IsNotNull();
        var (from, to) = ray!.Value;
        world.Camera.LookAtFromPosition(from, to, Vector3.Forward);
        AssertBool(inspector.SetEnabled(true)).IsTrue();
        await runner.SimulateFrames(2);

        AssertThat(inspector.ResolvedCamera()).IsSame(world.Camera);
        AssertThat(inspector.SelectionId).IsEqual(building!.Id);
        AssertThat(inspector.PanelText).Contains($"Building {building.Id}");
    }

    [TestCase]
    public async Task Ac17_RayAtTheConvenienceShopsBuilding_ListsTheShop()
    {
        AssertPackPresent();
        using var runner = ISceneRunner.Load(WorldScenePath, true);
        var world = await LoadWorld(runner);
        var loader = world.MapLoader;
        var inspector = world.Inspector!;
        var places = loader.Places!;

        AssertBool(places.TryGetPoi(ShopPoi, out var shop)).OverrideFailureMessage($"{ShopPoi} is not in the pack").IsTrue();
        AssertThat(shop.BuildingId).IsEqual(ShopBuilding);
        var building = places.Building(ShopBuilding);
        AssertThat(places.PoisOnOrIn(building).Select(p => p.Id).ToList()).Contains(ShopPoi);

        var ray = RoofRay(loader, building);
        AssertThat(ray).OverrideFailureMessage($"a ray from above doesn't reach the roof of {ShopBuilding}").IsNotNull();
        var text = inspector.InspectRay(ray!.Value.From, ray.Value.To);

        AssertThat(inspector.SelectionId).IsEqual(ShopBuilding);
        AssertThat(text).Contains($"Building {ShopBuilding}").Contains("POIs on or in it:").Contains(PlaceText.Summary(shop));
        AssertThat(text).Contains(ShopPoi);
    }

    /// <summary>
    /// A ray that hits the ground (road or terrain) within <see cref="PlaceInspector.PoiPickRadius"/> of a POI point
    /// selects that POI. POI points outside buildings are tried first, with a ray from high above; a POI inside a
    /// building (the test area's only point POI is the shop) is reached with a short ray that starts below the roof,
    /// 1.5 m above the ground. POIs with another POI point within twice the radius are skipped, so the pick is
    /// unambiguous.
    /// </summary>
    [TestCase]
    public async Task Ac17_RayHittingTheGroundWithin3MetresOfAPoiPoint_ShowsThatPoi()
    {
        AssertPackPresent();
        using var runner = ISceneRunner.Load(WorldScenePath, true);
        var world = await LoadWorld(runner);
        var loader = world.MapLoader;
        var inspector = world.Inspector!;
        var places = loader.Places!;
        var radius = inspector.PoiPickRadius;
        AssertFloat(radius).IsEqual(3f);

        var points = places.Pois.Items.Where(p => p.Geometry == PoiGeometry.Point).ToList();
        AssertInt(points.Count).OverrideFailureMessage("the pack has no POI point").IsGreater(0);
        var candidates = points
            .Where(p => !points.Any(o => o.Id != p.Id && Distance(o.Position, p.Position) <= 2 * radius))
            .OrderBy(p => p.BuildingId is null ? 0 : 1)
            .ThenBy(p => p.Id, System.StringComparer.Ordinal)
            .ToList();

        Poi? poi = null;
        (Vector3 From, Vector3 To, Vector3 Hit)? ray = null;
        foreach (var candidate in candidates)
        {
            ray = GroundRayNear(loader, candidate, radius);
            if (ray is not null)
            {
                poi = candidate;
                break;
            }
        }

        AssertThat(poi).OverrideFailureMessage(
            $"no ray hits the ground within {radius} m of any of {candidates.Count} POI points").IsNotNull();
        var text = inspector.InspectRay(ray!.Value.From, ray.Value.To);

        AssertThat(inspector.SelectedBuilding).IsNull();
        AssertThat(inspector.SelectedPoi).IsSame(poi);
        AssertThat(inspector.SelectionId).IsEqual(poi!.Id);
        AssertThat(text).StartsWith($"POI {poi.Id}");
        AssertThat(text).IsEqual(PlaceText.Describe(poi));
        GD.Print(
            $"PlaceInformationTest: ground hit {ray.Value.Hit} is {Distance(poi.Position, ToLocal(ray.Value.Hit)).ToString("F2", System.Globalization.CultureInfo.InvariantCulture)} m from {poi.Id} "
            + $"(building {poi.BuildingId ?? "none"})");
    }

    /// <summary>
    /// The shop node is inside its building (the test area's only POI point), so a ray from above hits the roof. A
    /// short ray starting below the roof hits the ground within the pick radius of the node and selects the shop,
    /// not its building. This runs on both packs; the general test above may choose a free-standing POI instead.
    /// </summary>
    [TestCase]
    public async Task Ac17_GroundRayBelowTheShopsRoof_ShowsTheShop()
    {
        AssertPackPresent();
        using var runner = ISceneRunner.Load(WorldScenePath, true);
        var world = await LoadWorld(runner);
        var loader = world.MapLoader;
        var inspector = world.Inspector!;
        AssertBool(loader.Places!.TryGetPoi(ShopPoi, out var shop)).IsTrue();

        var ray = GroundRayNear(loader, shop, inspector.PoiPickRadius);
        AssertThat(ray).OverrideFailureMessage($"no ray hits the ground within {inspector.PoiPickRadius} m of {ShopPoi}").IsNotNull();
        var text = inspector.InspectRay(ray!.Value.From, ray.Value.To);

        AssertThat(inspector.SelectedBuilding).IsNull();
        AssertThat(inspector.SelectionId).IsEqual(ShopPoi);
        AssertThat(text).IsEqual(PlaceText.Describe(shop));
        AssertThat(text).Contains($"building_id: \"{ShopBuilding}\"");
    }

    // ----- Helpers -----

    /// <summary>Loads the World scene and waits for terrain collision and the camera's start placement.</summary>
    private static async Task<World> LoadWorld(ISceneRunner runner)
    {
        await runner.SimulateFrames(5);
        var world = (World)runner.Scene()!;
        var loader = world.MapLoader;
        AssertBool(await loader.WaitForTerrainCollisionAsync()).IsTrue();
        // World moves the camera above the origin once collision is ready; let that happen before tests move it.
        await runner.SimulateFrames(2);
        AssertThat(loader.RoadGraph).IsNotNull();
        AssertThat(loader.Places).IsNotNull();
        return world;
    }

    /// <summary>
    /// The first edge of <paramref name="edges"/> for which a camera above its middle gives a HUD reference point
    /// whose nearest edge within <see cref="StreetLabelHud.StreetMaxDistance"/> is that edge. The camera stays there.
    /// </summary>
    private static (RoadEdge? Edge, Vector3? Reference) FindEdgeUnderCamera(World world, IEnumerable<RoadEdge> edges)
    {
        var hud = world.Hud;
        var graph = world.MapLoader.RoadGraph!;
        foreach (var edge in edges)
        {
            var middle = Middle(edge.Points);
            PlaceCameraAbove(world, new Vector3((float)middle.X, (float)middle.Y, (float)middle.Z));
            if (hud.ReferencePoint() is not { } reference)
            {
                continue;
            }

            if (graph.NearestEdge(ToLocal(reference), hud.StreetMaxDistance) is { } nearest && nearest.Edge.Id == edge.Id)
            {
                return (edge, reference);
            }
        }

        return (null, null);
    }

    private static void PlaceCameraAbove(World world, Vector3 point) =>
        world.Camera.LookAtFromPosition(point + (Vector3.Up * CameraHeight), point, Vector3.Forward);

    /// <summary>The point halfway along a polyline by horizontal length.</summary>
    private static LocalPoint Middle(IReadOnlyList<LocalPoint> points)
    {
        var lengths = new double[points.Count];
        for (var k = 1; k < points.Count; k++)
        {
            lengths[k] = lengths[k - 1] + System.Math.Sqrt(
                System.Math.Pow(points[k].X - points[k - 1].X, 2) + System.Math.Pow(points[k].Z - points[k - 1].Z, 2));
        }

        var half = lengths[^1] / 2;
        for (var k = 1; k < points.Count; k++)
        {
            if (lengths[k] >= half && lengths[k] > lengths[k - 1])
            {
                var a = points[k - 1];
                var b = points[k];
                var t = (half - lengths[k - 1]) / (lengths[k] - lengths[k - 1]);
                return new LocalPoint(a.X + (t * (b.X - a.X)), a.Y + (t * (b.Y - a.Y)), a.Z + (t * (b.Z - a.Z)));
            }
        }

        return points[0];
    }

    /// <summary>
    /// A vertical ray at the top of <paramref name="building"/>'s collision shape whose first hit is one of the
    /// building's bodies, or null. The aim point is the centroid of the highest up-facing triangle of the shape.
    /// </summary>
    private static (Vector3 From, Vector3 To)? RoofRay(MapLoader loader, Building building)
    {
        var tile = loader.Tiles.GetNodeOrNull(building.Tile);
        if (tile is null)
        {
            return null;
        }

        foreach (var nodeName in building.CollisionNodes)
        {
            if (tile.GetNodeOrNull<StaticBody3D>(nodeName) is not { } body)
            {
                continue;
            }

            foreach (var shape in body.GetChildren().OfType<CollisionShape3D>())
            {
                if (shape.Shape?.GetDebugMesh() is not { } mesh)
                {
                    continue;
                }

                var faces = mesh.GetFaces();
                Vector3? best = null;
                for (var k = 0; k + 2 < faces.Length; k += 3)
                {
                    var a = shape.GlobalTransform * faces[k];
                    var b = shape.GlobalTransform * faces[k + 1];
                    var c = shape.GlobalTransform * faces[k + 2];
                    var normal = (b - a).Cross(c - a);
                    if (normal.Length() < 1e-4f || Mathf.Abs(normal.Normalized().Y) < 0.7f)
                    {
                        continue;
                    }

                    var centroid = (a + b + c) / 3f;
                    if (best is null || centroid.Y > best.Value.Y)
                    {
                        best = centroid;
                    }
                }

                if (best is not { } top)
                {
                    continue;
                }

                var from = top + (Vector3.Up * 50f);
                var to = top + Vector3.Down;
                var hit = loader.GetWorld3D().DirectSpaceState.IntersectRay(PhysicsRayQueryParameters3D.Create(from, to));
                if (hit.Count > 0 && loader.BuildingIdOf(hit["collider"].AsGodotObject() as Node) == building.Id)
                {
                    return (from, to);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// A vertical ray near <paramref name="poi"/> (at its x/z, or 1 m or 2 m away in one of eight directions) whose
    /// first hit is a road or terrain body within <paramref name="radius"/> of the POI's position in 3D, or null.
    /// The ray starts 50 m above the ground; if a building is hit first there, it starts 1.5 m above the ground instead
    /// (inside the building, which the ray then leaves through no face before the ground).
    /// </summary>
    private static (Vector3 From, Vector3 To, Vector3 Hit)? GroundRayNear(MapLoader loader, Poi poi, float radius)
    {
        var space = loader.GetWorld3D().DirectSpaceState;
        var offsets = new List<Vector2> { Vector2.Zero };
        foreach (var distance in new[] { 1f, 2f })
        {
            for (var k = 0; k < 8; k++)
            {
                offsets.Add(Vector2.FromAngle(k * Mathf.Pi / 4f) * distance);
            }
        }

        foreach (var offset in offsets)
        {
            var x = (float)poi.Position.X + offset.X;
            var z = (float)poi.Position.Z + offset.Y;
            if (loader.TerrainHeightAt(x, z) is not { } ground)
            {
                continue;
            }

            foreach (var height in new[] { 50f, 1.5f })
            {
                var from = new Vector3(x, ground + height, z);
                var to = new Vector3(x, ground - 1.5f, z);
                var hit = space.IntersectRay(PhysicsRayQueryParameters3D.Create(from, to));
                if (hit.Count == 0 || hit["collider"].AsGodotObject() is not Node body)
                {
                    break;
                }

                if (body.IsInGroup(MapLoader.BuildingGroup))
                {
                    continue; // try from below the roof
                }

                if (!body.IsInGroup(MapLoader.TerrainGroup) && !body.IsInGroup(MapLoader.RoadGroup))
                {
                    break;
                }

                var position = hit["position"].AsVector3();
                if (Distance(poi.Position, ToLocal(position)) <= radius)
                {
                    return (from, to, position);
                }

                break;
            }
        }

        return null;
    }

    private static LocalPoint ToLocal(Vector3 point) => new(point.X, point.Y, point.Z);

    private static double Distance(LocalPoint a, LocalPoint b) =>
        System.Math.Sqrt(System.Math.Pow(a.X - b.X, 2) + System.Math.Pow(a.Y - b.Y, 2) + System.Math.Pow(a.Z - b.Z, 2));

    /// <summary>
    /// Copies the Somero pack's manifest and tile import files to a temporary pack <paramref name="mapId"/>, plus the
    /// listed JSON files with their <c>map_id</c> set to <paramref name="mapId"/>. The import files keep pointing at
    /// the already imported tile scenes; their UIDs are dropped so they don't clash with the originals.
    /// </summary>
    private int CopyPack(string mapId, params string[] jsonFiles)
    {
        var source = ProjectSettings.GlobalizePath($"res://maps/{MapId}");
        _tempPack = ProjectSettings.GlobalizePath($"res://maps/{mapId}");
        Directory.CreateDirectory(Path.Combine(_tempPack, "tiles"));
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(source, MapPack.ManifestFile)))!;
        manifest["map_id"] = mapId;
        File.WriteAllText(
            Path.Combine(_tempPack, MapPack.ManifestFile),
            manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        var tiles = manifest["tiles"]!.AsArray();
        foreach (var tile in tiles)
        {
            var file = tile!["file"]!.GetValue<string>();
            var import = File.ReadAllLines(Path.Combine(source, file + ".import")).Where(l => !l.StartsWith("uid="));
            File.WriteAllLines(Path.Combine(_tempPack, file + ".import"), import);
        }

        foreach (var file in jsonFiles)
        {
            var json = JsonNode.Parse(File.ReadAllText(Path.Combine(source, file)))!;
            json["map_id"] = mapId;
            File.WriteAllText(Path.Combine(_tempPack, file), json.ToJsonString());
        }

        return tiles.Count;
    }

    private static void AssertPackPresent()
    {
        foreach (var file in new[] { MapPack.ManifestFile, MapPack.RoadGraphFile, MapPack.BuildingsFile, MapPack.PoisFile })
        {
            var path = $"res://maps/{MapId}/{file}";
            AssertBool(FileAccess.FileExists(path))
                .OverrideFailureMessage($"no {file} at {path}; {MapPack.RegenHint(MapId)}")
                .IsTrue();
        }
    }
}
