using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using GdUnit4;
using Godot;
using Roadman.Core.Edge;
using Roadman.Core.Maps;
using Roadman.Core.Minimap;
using Roadman.Hud;
using Roadman.Scenes;
using static GdUnit4.Assertions;
using File = System.IO.File;
using Path = System.IO.Path;

namespace Roadman.Tests;

/// <summary>
/// The minimap on the pack in <c>res://maps/somero</c> (<c>docs/specs/minimap.md</c>; <c>Ac</c> numbers are its
/// acceptance criteria): the default corner, size and layers (10, and menu-settings-save criterion 15), the rotation
/// with the player camera (13), tunnels and bridge order (15), and no place names in its text (16). These criteria
/// name the full pack's roads, names and points of interest, so the suite uses the full pack throughout
/// (<see cref="TestPacks.UseFullPack"/> before and after each test; in CI that folder holds the test-area pack, and a
/// criterion that needs something only the full pack has is N/A there through
/// <see cref="WorldEdgeTest.AssertNotFullPack"/>). Every test starts its own <see cref="World"/>.
/// </summary>
[TestSuite]
[RequireGodotRuntime]
public class MinimapTest
{
    /// <summary>Criterion 13: how far west of a road point the player is put, so the point is due east of it, m.</summary>
    private const float DueEastDistance = 50f;

    /// <summary>Criterion 16: every n-th road graph node is a sample centre (with every point of interest).</summary>
    private const int NodeSampleStep = 8;

    /// <summary>Criterion 16: the headings sampled at each centre, degrees.</summary>
    private static readonly double[] SampleHeadings = [0, 90, 180, 270];

    [BeforeTest]
    public void SetUp() => TestPacks.UseFullPack();

    [AfterTest]
    public void CleanUp()
    {
        TestPacks.UseFullPack();
        Input.ActionRelease(Roadman.OnFoot.Player.MoveForwardAction);
    }

    // ----- Criterion 10 -----

    /// <summary>
    /// With the player at the spawn and no settings (a direct <see cref="World"/> run), the minimap is shown in the
    /// bottom-left corner: its panel is a <see cref="MinimapHud.PanelDiameter"/> square (the round panel's box),
    /// <see cref="MinimapHud.ScreenMargin"/> from the left and bottom edges of the viewport, and the frame draws the
    /// background image, at least one road segment and the player arrow (centred on the player). Also menu-settings-save
    /// criterion 15 (the minimap is in the bottom-left corner by default).
    /// </summary>
    [TestCase]
    public async Task Ac10_AtTheSpawn_ShownBottomLeft_PanelDiameterAcross_MarginFromLeftAndBottom_DrawsBackgroundRoadsAndArrow()
    {
        WorldEdgeTest.AssertPackPresent();
        using var runner = ISceneRunner.Load(WorldEdgeTest.NewWorld(WorldEdgeTest.TestSeed), true);
        var (world, minimap) = await StartWorld(runner);

        AssertThat(minimap.Corner).IsEqual(Core.Settings.MinimapCorner.BottomLeft);
        AssertFloat(minimap.PanelDiameter).IsEqual(132f);
        var viewport = minimap.GetViewport().GetVisibleRect().Size;
        var rect = minimap.PanelRect;
        var d = minimap.PanelDiameter;
        var m = minimap.ScreenMargin;
        GD.Print(WorldEdgeTest.Invariant(
            $"MinimapTest: Ac10 on the '{world.MapLoader.Manifest!.Area}' pack: viewport {viewport}, panel {rect}, {minimap.DrawnSegmentCount} segments, {minimap.DrawnLabelCount} labels, {minimap.DrawnSymbolCount} symbols"));
        AssertVector(rect.Size).OverrideFailureMessage($"panel size {rect.Size}, expected {d} × {d}").IsEqualApprox(new Vector2(d, d), new Vector2(0.01f, 0.01f));
        AssertFloat(rect.Position.X).OverrideFailureMessage($"panel left edge {rect.Position.X}, expected the margin {m}").IsEqualApprox(m, 0.01f);
        AssertFloat(viewport.Y - rect.End.Y).OverrideFailureMessage($"panel bottom edge {rect.End.Y} in a {viewport.Y} high viewport, expected the margin {m}").IsEqualApprox(m, 0.01f);

        AssertBool(minimap.IsShown).OverrideFailureMessage("the minimap isn't shown at the spawn").IsTrue();
        AssertThat(minimap.Projection).IsNotNull();
        var player = world.Player!;
        AssertFloat((float)minimap.Projection!.CentreX).IsEqualApprox(player.GlobalPosition.X, 0.01f);
        AssertFloat((float)minimap.Projection.CentreZ).IsEqualApprox(player.GlobalPosition.Z, 0.01f);
        AssertThat(world.MapLoader.MinimapTexture).OverrideFailureMessage("the pack's minimap image didn't load").IsNotNull();
        AssertBool(minimap.HasBackground).OverrideFailureMessage("no background image drawn").IsTrue();
        AssertBool(minimap.DrawsVoid).IsTrue();
        AssertInt(minimap.DrawnSegmentCount).OverrideFailureMessage("no road segment drawn at the spawn").IsGreaterEqual(1);
        AssertFloat(minimap.ArrowAngle).OverrideFailureMessage("the arrow angle isn't finite").IsBetween(-Mathf.Pi - 1e-4f, Mathf.Pi + 1e-4f);
        AssertFloat(minimap.ArrowAngle)
            .OverrideFailureMessage($"the arrow isn't the player's facing {player.FacingYaw} against the heading {minimap.Projection.HeadingYaw}")
            .IsEqualApprox((float)minimap.Projection.ArrowAngle(player.FacingYaw), 1e-4f);
    }

    // ----- Criterion 13 -----

    /// <summary>
    /// The player camera turned to look east: the heading is east, a road point due east of the player (the player put
    /// <see cref="DueEastDistance"/> west of it) projects straight above the centre, and the north indicator is on the left of the rim.
    /// Then turning only the player model changes only the arrow: the heading, the north indicator and the drawn
    /// roads stay, and the arrow turns by the model's turn (clockwise on the panel for a clockwise turn).
    /// </summary>
    [TestCase]
    public async Task Ac13_CameraLookingEast_RoadDueEastAboveCentre_NorthOnTheLeft_TurningOnlyTheModelTurnsOnlyTheArrow()
    {
        WorldEdgeTest.AssertPackPresent();
        using var runner = ISceneRunner.Load(WorldEdgeTest.NewWorld(WorldEdgeTest.TestSeed), true);
        var (world, minimap) = await StartWorld(runner);
        var player = world.Player!;
        var camera = world.PlayerCamera!;
        player.InputEnabled = false;
        camera.InputEnabled = false;

        const float east = -Mathf.Pi / 2f; // Yaw 0 faces −z (north); east is a quarter turn clockwise from above.
        // A road point exactly due east: the player is put DueEastDistance west of the road point nearest the spawn
        // (only its x/z count for the minimap; the frame is drawn at once, before physics moves it).
        var spawn = player.GlobalPosition;
        var (roadEdge, road) = minimap.Data.Roads.SelectMany(r => r.Points.Select(p => (r.EdgeId, Point: p)))
            .MinBy(e => ((e.Point.X - spawn.X) * (e.Point.X - spawn.X)) + ((e.Point.Z - spawn.Z) * (e.Point.Z - spawn.Z)));
        player.GlobalPosition = new Vector3((float)road.X - DueEastDistance, spawn.Y, (float)road.Z);
        player.FacingYaw = 0f;
        camera.PlaceBehind(east);
        minimap.UpdateFrame();
        var projection = minimap.Projection!;
        AssertFloat((float)MinimapProjection.NormaliseAngle(projection.HeadingYaw - east))
            .OverrideFailureMessage($"heading {projection.HeadingYaw} with the camera looking east").IsEqualApprox(0f, 1e-3f);
        AssertFloat((float)projection.CentreX).IsEqualApprox(player.GlobalPosition.X, 1e-3f);

        var panel = projection.ToPanel(road);
        GD.Print(WorldEdgeTest.Invariant(
            $"MinimapTest: Ac13 road point ({road.X:0.##}, {road.Z:0.##}) {DueEastDistance} m due east of the player at panel ({panel.X:0.###}, {panel.Y:0.###}); north indicator at {minimap.NorthIndicatorPosition}"));
        AssertFloat((float)panel.Y).OverrideFailureMessage($"the road point due east is at panel y {panel.Y}, not above the centre")
            .IsEqualApprox(-DueEastDistance * (float)projection.Scale, 0.01f);
        AssertFloat((float)panel.X).OverrideFailureMessage($"the road point due east is at panel x {panel.X}, not straight above the centre")
            .IsEqualApprox(0f, 0.01f);
        AssertInt(minimap.DrawnSegmentCount).OverrideFailureMessage("no road drawn with the camera looking east").IsGreater(0);
        AssertBool(minimap.DrawnRoadOrder.Contains(roadEdge))
            .OverrideFailureMessage($"the edge {roadEdge} with the road point due east isn't in the frame's draw order").IsTrue();

        var north = minimap.NorthIndicatorPosition;
        AssertFloat(north.X).OverrideFailureMessage($"north indicator at {north}, not on the left").IsLess(0f);
        AssertFloat(Math.Abs(north.Y)).OverrideFailureMessage($"north indicator at {north}, not level with the centre").IsLess(Math.Abs(north.X) * 0.01f);
        AssertFloat(north.Length()).OverrideFailureMessage($"north indicator at {north}, not near the rim").IsGreater(minimap.PanelDiameter / 2f * 0.75f);

        // Turn only the model.
        var heading = projection.HeadingYaw;
        var arrow = minimap.ArrowAngle;
        var order = minimap.DrawnRoadOrder.ToList();
        var segments = minimap.DrawnSegmentCount;
        var cameraTransform = camera.Camera.GlobalTransform;
        foreach (var turn in new[] { -Mathf.Pi / 2f, Mathf.Pi / 4f, Mathf.Pi })
        {
            player.FacingYaw = turn;
            minimap.UpdateFrame();
            AssertThat(camera.Camera.GlobalTransform).IsEqual(cameraTransform);
            AssertFloat((float)minimap.Projection!.HeadingYaw).OverrideFailureMessage("turning the model turned the map").IsEqualApprox((float)heading, 1e-6f);
            AssertVector(minimap.NorthIndicatorPosition).OverrideFailureMessage("turning the model moved the north indicator").IsEqualApprox(north, new Vector2(1e-4f, 1e-4f));
            AssertThat(minimap.DrawnRoadOrder.ToList()).OverrideFailureMessage("turning the model changed the roads drawn").IsEqual(order);
            AssertInt(minimap.DrawnSegmentCount).IsEqual(segments);

            // A model turned clockwise (seen from above: a lower yaw) of the camera turns the arrow clockwise.
            var expected = (float)MinimapProjection.NormaliseAngle(east - turn);
            AssertFloat((float)MinimapProjection.NormaliseAngle(minimap.ArrowAngle - expected))
                .OverrideFailureMessage($"facing {turn}: arrow {minimap.ArrowAngle}, expected {expected}").IsEqualApprox(0f, 1e-4f);
        }

        player.FacingYaw = 0f;
        minimap.UpdateFrame();
        AssertFloat(minimap.ArrowAngle).IsEqualApprox(arrow, 1e-5f);
        player.FacingYaw = east;
        minimap.UpdateFrame();
        AssertFloat(minimap.ArrowAngle).OverrideFailureMessage("facing the camera's way, the arrow doesn't point up").IsEqualApprox(0f, 1e-4f);
    }

    // ----- Criterion 15 -----

    /// <summary>
    /// No tunnel edge is in the minimap's data or drawn, also with the player at a tunnel edge; in the data every
    /// bridge edge comes after every non-bridge edge; and with the player at a bridge, the frame's draw order has both
    /// kinds and every bridge edge after every non-bridge one. On a pack without tunnels or bridges (the test area in
    /// CI) that part is N/A.
    /// </summary>
    [TestCase]
    public async Task Ac15_TunnelEdgesNotDrawn_BridgeEdgesDrawnAfterAllOtherEdges()
    {
        WorldEdgeTest.AssertPackPresent();
        using var runner = ISceneRunner.Load(WorldEdgeTest.NewWorld(WorldEdgeTest.TestSeed), true);
        var (world, minimap) = await StartWorld(runner);
        var graph = world.MapLoader.RoadGraph!;
        var player = world.Player!;
        player.InputEnabled = false;
        var tunnels = graph.Edges.Where(e => e.Tunnel).ToList();
        var bridges = graph.Edges.Where(e => e.Bridge && !e.Tunnel).ToList();
        var bridgeIds = bridges.Select(e => e.Id).ToHashSet();
        var tunnelIds = tunnels.Select(e => e.Id).ToHashSet();
        GD.Print($"MinimapTest: Ac15 on the '{world.MapLoader.Manifest!.Area}' pack: {tunnels.Count} tunnel edges, {bridges.Count} bridge edges, {minimap.Data.Roads.Count} roads in the data");

        var dataIds = minimap.Data.Roads.Select(r => r.EdgeId).ToList();
        AssertThat(string.Join(", ", dataIds.Where(tunnelIds.Contains))).OverrideFailureMessage("tunnel edges in the minimap data").IsEqual("");
        AssertInt(dataIds.Count).IsEqual(graph.Edges.Count - tunnels.Count);
        AssertOrder(dataIds, bridgeIds, "the data's draw order");
        AssertBool(minimap.Data.Roads.Select((r, i) => r.DrawIndex == i).All(x => x)).OverrideFailureMessage("DrawIndex isn't the position in Roads").IsTrue();
        AssertBool(minimap.Data.Roads.All(r => r.Bridge == bridgeIds.Contains(r.EdgeId))).OverrideFailureMessage("a road's Bridge flag isn't its edge's").IsTrue();

        if (tunnels.Count == 0)
        {
            WorldEdgeTest.AssertNotFullPack(world, "Ac15", "has no tunnel edge");
        }

        foreach (var tunnel in tunnels)
        {
            var mid = tunnel.Points[tunnel.Points.Count / 2];
            Frame(world, minimap, new Vector3((float)mid.X, (float)mid.Y, (float)mid.Z), 0f);
            AssertInt(minimap.DrawnSegmentCount).OverrideFailureMessage($"nothing drawn at tunnel {tunnel.Id}").IsGreater(0);
            AssertThat(string.Join(", ", minimap.DrawnRoadOrder.Where(tunnelIds.Contains)))
                .OverrideFailureMessage($"tunnel edges drawn with the player at {tunnel.Id}").IsEqual("");
        }

        if (bridges.Count == 0)
        {
            WorldEdgeTest.AssertNotFullPack(world, "Ac15", "has no bridge edge");
            return;
        }

        var checkedBridges = 0;
        foreach (var bridge in bridges)
        {
            var mid = bridge.Points[bridge.Points.Count / 2];
            Frame(world, minimap, new Vector3((float)mid.X, (float)mid.Y, (float)mid.Z), 0f);
            var drawn = minimap.DrawnRoadOrder.ToList();
            AssertBool(drawn.Contains(bridge.Id)).OverrideFailureMessage($"bridge {bridge.Id} not drawn with the player on it").IsTrue();
            AssertBool(drawn.Any(id => !bridgeIds.Contains(id))).OverrideFailureMessage($"no non-bridge edge drawn at bridge {bridge.Id}").IsTrue();
            AssertThat(string.Join(", ", drawn.Where(tunnelIds.Contains))).IsEqual("");
            AssertOrder(drawn, bridgeIds, $"the frame at bridge {bridge.Id}");
            checkedBridges++;
        }

        GD.Print($"MinimapTest: Ac15 draw order checked at {checkedBridges} bridge edges");
    }

    // ----- Criterion 16 -----

    /// <summary>
    /// Over frames centred on every point of interest and on every <see cref="NodeSampleStep"/>-th road graph node
    /// (each at the headings of <see cref="SampleHeadings"/>), and the spawn: at least one label is drawn, every drawn
    /// label text is the street label (<see cref="StreetLabel.Format"/>) of a road graph edge with a <c>name</c> or a
    /// <c>ref</c>, never a <c>name_sv</c>, and no drawn text is a point-of-interest or building <c>name</c> or
    /// <c>name_sv</c> from the pack's <c>pois.json</c> and <c>buildings.json</c>. A name that is also a street's own
    /// name in the road graph is printed (a street drawn under its own name); it is not a place name taken from the
    /// place data.
    /// </summary>
    [TestCase]
    public async Task Ac16_NoPointOfInterestOrBuildingNameInAnyMinimapText_OnlyStreetNamesAndRefs()
    {
        WorldEdgeTest.AssertPackPresent();
        using var runner = ISceneRunner.Load(WorldEdgeTest.NewWorld(WorldEdgeTest.TestSeed), true);
        var (world, minimap) = await StartWorld(runner);
        var graph = world.MapLoader.RoadGraph!;
        var player = world.Player!;
        player.InputEnabled = false;

        var placeNames = PlaceNames(TestPacks.PackPath);
        var streetTexts = graph.Edges.Where(e => e.Names.Name is not null || e.Names.Ref is not null)
            .Select(StreetLabel.Format).ToHashSet(StringComparer.Ordinal);
        var swedish = graph.Edges.Select(e => e.Names.NameSv).OfType<string>()
            .Where(sv => !graph.Edges.Any(e => e.Names.Name == sv)).ToHashSet(StringComparer.Ordinal);
        var shared = placeNames.Where(streetTexts.Contains).ToHashSet(StringComparer.Ordinal);

        var centres = new List<Vector3> { player.GlobalPosition };
        centres.AddRange(world.MapLoader.Places!.Pois.Items.Select(p => new Vector3((float)p.Position.X, (float)p.Position.Y, (float)p.Position.Z)));
        centres.AddRange(graph.Nodes.Where((_, i) => i % NodeSampleStep == 0).Select(n => new Vector3((float)n.Position.X, (float)n.Position.Y, (float)n.Position.Z)));

        var drawnTexts = new HashSet<string>(StringComparer.Ordinal);
        var frames = 0;
        var framesWithLabels = 0;
        var symbols = 0;
        foreach (var centre in centres)
        {
            foreach (var heading in SampleHeadings)
            {
                Frame(world, minimap, centre, Mathf.DegToRad((float)heading));
                frames++;
                framesWithLabels += minimap.DrawnLabelCount > 0 ? 1 : 0;
                symbols += minimap.DrawnSymbolCount;
                drawnTexts.UnionWith(minimap.LabelTexts);
            }
        }

        GD.Print(WorldEdgeTest.Invariant(
            $"MinimapTest: Ac16 on the '{world.MapLoader.Manifest!.Area}' pack: {frames} frames, {framesWithLabels} with labels, {drawnTexts.Count} distinct label texts, {symbols} symbols; {placeNames.Count} place names checked"));
        if (shared.Count > 0)
        {
            GD.Print($"MinimapTest: Ac16 place names that are also street names in the road graph: {string.Join(", ", shared.Order(StringComparer.Ordinal))}; drawn: {string.Join(", ", shared.Where(drawnTexts.Contains).Order(StringComparer.Ordinal))}");
        }

        AssertInt(drawnTexts.Count).OverrideFailureMessage("no label was drawn in any sampled frame").IsGreater(0);
        AssertThat(string.Join(" | ", drawnTexts.Where(t => !streetTexts.Contains(t)).Order(StringComparer.Ordinal)))
            .OverrideFailureMessage("drawn label texts that aren't the street label of any named road graph edge").IsEqual("");
        AssertThat(string.Join(" | ", drawnTexts.Where(swedish.Contains).Order(StringComparer.Ordinal)))
            .OverrideFailureMessage("drawn label texts that are Swedish street names").IsEqual("");
        AssertThat(string.Join(" | ", drawnTexts.Where(t => placeNames.Contains(t) && !shared.Contains(t)).Order(StringComparer.Ordinal)))
            .OverrideFailureMessage("point-of-interest or building names drawn on the minimap").IsEqual("");
        AssertBool(drawnTexts.Contains(MinimapHud.NorthText)).IsFalse();
    }

    // ----- Helpers -----

    /// <summary>The runner's world once its player has spawned, with its minimap after one drawn frame.</summary>
    internal static async Task<(World World, MinimapHud Minimap)> StartWorld(ISceneRunner runner)
    {
        var world = (World)runner.Scene()!;
        AssertBool(await WorldEdgeTest.WaitForSpawn(world)).OverrideFailureMessage($"the player didn't spawn ({world.LastSpawnFailure})").IsTrue();
        AssertThat(world.Death.State).IsEqual(DeathState.Alive);
        AssertThat(world.Minimap).OverrideFailureMessage("World has no minimap").IsNotNull();
        var minimap = world.Minimap!;
        await world.ToSignal(world.GetTree(), SceneTree.SignalName.ProcessFrame);
        await world.ToSignal(world.GetTree(), SceneTree.SignalName.ProcessFrame);
        return (world, minimap);
    }

    /// <summary>Puts the player at <paramref name="at"/> with the camera looking <paramref name="yaw"/>, then draws one frame.</summary>
    private static void Frame(World world, MinimapHud minimap, Vector3 at, float yaw)
    {
        world.Player!.GlobalPosition = at + (Vector3.Up * 0.1f);
        world.Player.FacingYaw = yaw;
        world.PlayerCamera!.PlaceBehind(yaw);
        minimap.UpdateFrame();
        AssertBool(minimap.IsShown).OverrideFailureMessage($"the minimap isn't shown with the player at {at}").IsTrue();
    }

    /// <summary>Every bridge edge in <paramref name="order"/> comes after every non-bridge edge.</summary>
    private static void AssertOrder(IReadOnlyList<string> order, HashSet<string> bridgeIds, string what)
    {
        var lastOther = -1;
        var firstBridge = int.MaxValue;
        for (var i = 0; i < order.Count; i++)
        {
            if (bridgeIds.Contains(order[i]))
            {
                firstBridge = Math.Min(firstBridge, i);
            }
            else
            {
                lastOther = i;
            }
        }

        AssertBool(lastOther < firstBridge)
            .OverrideFailureMessage($"{what}: a non-bridge edge ({(lastOther >= 0 ? order[lastOther] : "")}, index {lastOther}) after a bridge edge ({(firstBridge < order.Count ? order[firstBridge] : "")}, index {firstBridge})")
            .IsTrue();
    }

    /// <summary>Every <c>name</c> and <c>name_sv</c> of the points of interest and buildings of the pack in <paramref name="packPath"/>.</summary>
    private static HashSet<string> PlaceNames(string packPath)
    {
        var folder = ProjectSettings.GlobalizePath(packPath);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (file, list) in new[] { (MapPack.PoisFile, "pois"), (MapPack.BuildingsFile, "buildings") })
        {
            var root = JsonNode.Parse(File.ReadAllText(Path.Combine(folder, file)))!;
            foreach (var item in root[list]!.AsArray())
            {
                foreach (var key in new[] { "name", "name_sv" })
                {
                    if (item![key] is JsonValue value && value.TryGetValue<string>(out var name) && name.Trim() != "")
                    {
                        names.Add(name);
                    }
                }
            }
        }

        AssertInt(names.Count).OverrideFailureMessage($"no names in {packPath}'s pois.json and buildings.json").IsGreater(0);
        return names;
    }
}
