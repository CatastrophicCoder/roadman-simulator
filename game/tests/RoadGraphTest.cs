using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using GdUnit4;
using Godot;
using Roadman.Core.Maps;
using Roadman.Debug;
using Roadman.Scenes;
using static GdUnit4.Assertions;
using Directory = System.IO.Directory;
using File = System.IO.File;
using Path = System.IO.Path;

namespace Roadman.Tests;

/// <summary>
/// The road graph and road collision in Godot, on the Somero pack (full or test area: <c>/regen-map somero</c>, or
/// the CI test-area build from the committed fixture). Edges and the origin bridge are found in <c>road_graph.json</c>. Criterion numbers (<c>Ac</c>) refer to
/// <c>docs/specs/roads-and-road-graph.md</c>. Like <see cref="MapLoaderTest"/>, these fail rather than skip without
/// the pack.
/// </summary>
[TestSuite]
[RequireGodotRuntime]
public class RoadGraphTest
{
    private const string WorldScenePath = "res://scenes/world/World.tscn";
    private const string MapId = "somero";

    /// <summary>Spec "Data": the origin bridge's two primary ways (a dual carriageway over the Heikintie cycleway).</summary>
    private static readonly long[] OriginBridgeWays = [88027326, 813370683];

    /// <summary>Criterion 21: a ray down at a ground drivable edge meets the road at the edge's height within 1 cm.</summary>
    private const double GroundTolerance = 0.01;

    /// <summary>Criterion 22: the deck surface is at most 0.05 m above its straight line.</summary>
    private const double DeckRise = 0.05;

    /// <summary>
    /// Criterion 22: the hit may be this far below the deck line. The spec's 0.01 mm allows float32 rounding of the
    /// mesh (under 0.008 mm at Somero's heights); the measured ray-hit error is printed by the test.
    /// </summary>
    private const double DeckBelowTolerance = 1e-5;

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

    [TestCase]
    public async Task Ac21_LoadedRoadGraph_HasTheFilesNodeAndEdgeCounts()
    {
        AssertPackPresent();
        using var runner = ISceneRunner.Load(WorldScenePath, true);
        await runner.SimulateFrames(2);
        var loader = Loader(runner);

        var path = $"res://maps/{MapId}/{MapPack.RoadGraphFile}";
        var file = JsonNode.Parse(FileAccess.GetFileAsString(path))!;
        AssertThat(loader.RoadGraph).IsNotNull();
        var graph = loader.RoadGraph!;
        AssertInt(graph.Nodes.Count).IsEqual(file["nodes"]!.AsArray().Count);
        AssertInt(graph.Edges.Count).IsEqual(file["edges"]!.AsArray().Count);
        AssertInt(graph.Edges.Count).IsGreater(0);
        AssertThat(graph.MapId).IsEqual(MapId);
    }

    /// <summary>
    /// Criterion 21 on every ground drivable edge (not bridge, not tunnel): a ray down at the polyline's midpoint by
    /// length hits the <c>road</c> body first (never terrain or <c>path</c>). Where the midpoint is a graph vertex, the
    /// hit is at that vertex's height; elsewhere at the terrain plus the map's <c>surface_offset_m</c>, because a
    /// straight graph segment isn't on the draped surface between vertices. The interior vertices of every such edge
    /// are checked too. Measured on 2026-10-29: test-area pack worst 0.0087 mm (13 interior vertices); full Somero
    /// pack 835 midpoints worst 2.22 mm and 631 interior vertices worst 0.031 mm. The midpoint error is larger because
    /// it goes through TerrainHeightAt: graph vertex minus TerrainHeightAt differs from the offset by up to 2.0 mm.
    /// </summary>
    [TestCase]
    public async Task Ac21_RayDownOnGroundDrivableEdges_HitsRoadCollisionAtTheEdgeHeight()
    {
        AssertPackPresent();
        using var runner = ISceneRunner.Load(WorldScenePath, true);
        await runner.SimulateFrames(2);
        var loader = Loader(runner);
        AssertBool(await loader.WaitForTerrainCollisionAsync()).IsTrue();
        var graph = loader.RoadGraph!;
        var offset = SurfaceOffset();

        var failures = new List<string>();
        var worstVertex = 0.0;
        var worstMidpoint = 0.0;
        var worstVertexOffset = 0.0;
        var vertices = 0;
        var midpoints = 0;
        var ground = graph.Edges.Where(e => e.Drivable && !e.Bridge && !e.Tunnel).ToList();
        AssertInt(ground.Count).IsGreater(0);
        foreach (var edge in ground)
        {
            // Interior vertices: the graph height is the surface height there.
            for (var i = 1; i < edge.Points.Count - 1; i++)
            {
                var p = edge.Points[i];
                var error = CheckRoadHit(loader, edge.Id, $"vertex {i}", p.X, p.Z, p.Y, failures);
                worstVertex = System.Math.Max(worstVertex, error);
                var terrain = loader.TerrainHeightAt((float)p.X, (float)p.Z);
                if (terrain is not null)
                {
                    // The pack's offset, measured: graph vertex minus terrain.
                    worstVertexOffset = System.Math.Max(worstVertexOffset, System.Math.Abs(p.Y - terrain.Value - offset));
                }

                vertices++;
            }

            // The midpoint by 3D length along the polyline.
            var (x, z, y, vertex) = Midpoint(edge.Points);
            double expected;
            if (vertex)
            {
                expected = y;
            }
            else
            {
                var terrain = loader.TerrainHeightAt((float)x, (float)z);
                if (terrain is null)
                {
                    failures.Add($"{edge.Id} midpoint: no terrain under it");
                    continue;
                }

                expected = terrain.Value + offset;
            }

            worstMidpoint = System.Math.Max(worstMidpoint, CheckRoadHit(loader, edge.Id, "midpoint", x, z, expected, failures));
            midpoints++;
        }

        GD.Print(
            $"RoadGraphTest: {midpoints} edge midpoints, worst {worstMidpoint * 1000:F4} mm; {vertices} interior vertices, "
            + $"worst {worstVertex * 1000:F4} mm; vertex minus terrain differs from offset {offset} m by at most "
            + $"{worstVertexOffset * 1000:F4} mm");
        AssertInt(midpoints).IsEqual(ground.Count);
        AssertFloat(worstVertexOffset).OverrideFailureMessage("surface_offset_m in map.yaml doesn't match the pack")
            .IsLessEqual(GroundTolerance);
        AssertThat(string.Join("; ", failures.Take(20))).IsEqual("");
    }

    /// <summary>
    /// Casts from 1 m above <paramref name="expected"/> to 1 m below it (so a bridge deck above a road isn't hit);
    /// records a failure unless the first non-building hit is the <c>road</c> body in <see cref="MapLoader.RoadGroup"/>
    /// within <see cref="GroundTolerance"/> of <paramref name="expected"/>. Returns the height error (0 on a miss).
    /// </summary>
    private static double CheckRoadHit(MapLoader loader, string edge, string where, double x, double z, double expected, List<string> failures)
    {
        var hit = CastDown(loader, (float)x, (float)(expected + 1), (float)z, (float)(expected - 1), out var body);
        if (hit is null)
        {
            failures.Add($"{edge} {where}: the ray hit nothing");
            return 0;
        }

        var (position, mesh) = hit.Value;
        if (mesh != MapLoader.RoadNodeName || body is null || !body.IsInGroup(MapLoader.RoadGroup))
        {
            failures.Add($"{edge} {where}: first hit is '{mesh}', not '{MapLoader.RoadNodeName}' in {MapLoader.RoadGroup}");
            return 0;
        }

        var error = System.Math.Abs(position.Y - expected);
        if (error > GroundTolerance)
        {
            failures.Add($"{edge} {where}: hit at {position.Y}, expected {expected}");
        }

        return error;
    }

    /// <summary>The point halfway along a polyline by 3D length, and whether it is one of its vertices.</summary>
    private static (double X, double Z, double Y, bool Vertex) Midpoint(IReadOnlyList<LocalPoint> points)
    {
        var lengths = new double[points.Count];
        for (var i = 1; i < points.Count; i++)
        {
            var a = points[i - 1];
            var b = points[i];
            lengths[i] = lengths[i - 1] + System.Math.Sqrt(
                System.Math.Pow(b.X - a.X, 2) + System.Math.Pow(b.Y - a.Y, 2) + System.Math.Pow(b.Z - a.Z, 2));
        }

        var half = lengths[^1] / 2;
        for (var i = 1; i < points.Count; i++)
        {
            if (lengths[i] < half)
            {
                continue;
            }

            var a = points[i - 1];
            var b = points[i];
            if (System.Math.Abs(lengths[i] - half) < 1e-6)
            {
                return (b.X, b.Z, b.Y, true);
            }

            var t = (half - lengths[i - 1]) / (lengths[i] - lengths[i - 1]);
            return (a.X + (t * (b.X - a.X)), a.Z + (t * (b.Z - a.Z)), a.Y + (t * (b.Y - a.Y)), false);
        }

        return (points[^1].X, points[^1].Z, points[^1].Y, true);
    }

    /// <summary>
    /// <c>roads.surface_offset_m</c> from the map's config, <c>maps/&lt;id&gt;/map.yaml</c> next to the game folder
    /// (the pipeline's input; the pack doesn't record it). The test also checks it against the pack's vertices.
    /// </summary>
    private static double SurfaceOffset()
    {
        var path = Path.GetFullPath(Path.Combine(ProjectSettings.GlobalizePath("res://"), "..", "maps", MapId, "map.yaml"));
        AssertBool(File.Exists(path)).OverrideFailureMessage($"no map config at {path}").IsTrue();
        var match = Regex.Match(File.ReadAllText(path), @"^\s+surface_offset_m:\s*([0-9.]+)", RegexOptions.Multiline);
        AssertBool(match.Success).OverrideFailureMessage($"no roads.surface_offset_m in {path}").IsTrue();
        return double.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
    }

    [TestCase]
    public async Task Ac22_RaysAlongTheOriginBridge_HitItsDeckOnTheStraightLine()
    {
        AssertPackPresent();
        using var runner = ISceneRunner.Load(WorldScenePath, true);
        await runner.SimulateFrames(2);
        var loader = Loader(runner);
        AssertBool(await loader.WaitForTerrainCollisionAsync()).IsTrue();
        var graph = loader.RoadGraph!;

        var lowest = double.PositiveInfinity;
        var highest = double.NegativeInfinity;
        var failures = new List<string>();
        var rays = 0;
        foreach (var way in OriginBridgeWays)
        {
            var line = CentreLine(graph, way);
            var deck = new Regex($"^{MapLoader.BridgeNodePrefix}{way}(_p\\d+)?$");
            var length = line.S[^1];
            AssertFloat(length).OverrideFailureMessage($"way {way}: span too short").IsGreater(1.0);
            for (var s = 0.5; s <= length - 0.5 + 1e-9; s += 1.0)
            {
                var (x, z, lineY) = line.At(s);
                var hit = CastDown(loader, (float)x, (float)(lineY + 5), (float)z, (float)(lineY - 30), skipBuildings: false);
                rays++;
                if (hit is null)
                {
                    failures.Add($"way {way} s={s:F1}: no hit");
                    continue;
                }

                var (position, mesh) = hit.Value;
                if (mesh is null || !deck.IsMatch(mesh))
                {
                    failures.Add($"way {way} s={s:F1}: first hit is '{mesh}', not its deck");
                    continue;
                }

                var diff = position.Y - lineY;
                lowest = System.Math.Min(lowest, diff);
                highest = System.Math.Max(highest, diff);
                if (diff < -DeckBelowTolerance || diff > DeckRise)
                {
                    failures.Add($"way {way} s={s:F1}: hit {diff * 1000:F4} mm from the line");
                }
            }
        }

        GD.Print($"RoadGraphTest: {rays} rays along the origin bridge, hit minus line {lowest * 1000:F4}..{highest * 1000:F4} mm");
        AssertInt(rays).IsGreater(0);
        AssertThat(string.Join("; ", failures)).IsEqual("");
    }

    [TestCase]
    public async Task Ac23_PackWithoutRoadGraph_LoadsTilesAndReportsTheMissingFile()
    {
        AssertPackPresent();
        var mapId = $"tmp-no-road-graph-{System.Guid.NewGuid():N}"[..30];
        var tileCount = CopyPackWithoutRoadGraph(mapId);
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
        AssertThat(loader.RoadGraph).IsNull();
        var path = $"res://maps/{mapId}/{MapPack.RoadGraphFile}";
        AssertThat(loader.RoadGraphPath).IsEqual(path);
        var errors = capture.Errors.Where(e => e.Contains(path)).ToList();
        AssertInt(errors.Count).OverrideFailureMessage($"no error names {path}; got: {string.Join(" | ", capture.Errors)}")
            .IsEqual(1);
        AssertThat(errors[0]).Contains($"/regen-map {mapId}");
    }

    [TestCase]
    public async Task Ac24_G_TogglesTheRoadGraphViewInADebugBuild()
    {
        AssertPackPresent();
        AssertBool(OS.IsDebugBuild()).IsTrue();
        using var runner = ISceneRunner.Load(WorldScenePath, true);
        await runner.SimulateFrames(2);
        var world = (World)runner.Scene()!;
        var view = world.RoadGraphView;
        AssertThat(view).IsNotNull();
        AssertThat(world.MapLoader.RoadGraph).IsNotNull();

        AssertBool(InputMap.HasAction(RoadGraphDebugView.ToggleAction)).IsTrue();
        var g = new InputEventKey { PhysicalKeycode = Key.G, Pressed = true };
        AssertBool(InputMap.EventIsAction(g, RoadGraphDebugView.ToggleAction, exactMatch: true)).IsTrue();
        var toggles = new List<bool>();
        view!.Toggled += toggles.Add;

        runner.SimulateKeyPressed(Key.G);
        await runner.AwaitIdleFrame();
        AssertBool(view.Enabled).IsTrue();
        AssertBool(view.MeshInstance.Visible).IsTrue();
        AssertThat(view.MeshInstance.Mesh).IsNotNull();
        AssertBool(view.LegendPanel.Visible).IsTrue();

        runner.SimulateKeyPressed(Key.G);
        await runner.AwaitIdleFrame();
        AssertBool(view.Enabled).IsFalse();
        AssertBool(view.MeshInstance.Visible).IsFalse();
        AssertThat(toggles).IsEqual(new List<bool> { true, false });
    }

    [TestCase]
    public async Task Ac24_WithoutRoadGraph_TheViewShowsAMessageAndStaysOff()
    {
        using var runner = ISceneRunner.Load(new Node3D { Name = "Root" }, true);
        var loader = new MapLoader { Name = "MapLoader", MapId = MapId };
        runner.Scene()!.AddChild(loader); // not loaded: RoadGraph is null
        var view = new RoadGraphDebugView { Name = "RoadGraphDebugView", Loader = loader };
        runner.Scene()!.AddChild(view);
        var unavailable = "";
        var toggled = false;
        view.Unavailable += message => unavailable = message;
        view.Toggled += _ => toggled = true;

        runner.SimulateKeyPressed(Key.G);
        await runner.AwaitIdleFrame();

        AssertBool(view.Enabled).IsFalse();
        AssertBool(toggled).IsFalse();
        AssertBool(view.MeshInstance.Visible).IsFalse();
        AssertBool(view.MessagePanel.Visible).IsTrue();
        AssertThat(view.MessageLabel.Text).IsEqual(RoadGraphDebugView.NoGraphMessage(MapId));
        AssertThat(unavailable).IsEqual(view.MessageLabel.Text);
        AssertBool(view.Toggle()).IsFalse();
    }

    [TestCase]
    public async Task PackWithEmptyRoadGraph_LoadsWithoutErrorsAndTheViewShowsAnEmptyLegend()
    {
        AssertPackPresent();
        var mapId = $"tmp-empty-road-graph-{System.Guid.NewGuid():N}"[..30];
        CopyPackWithoutRoadGraph(mapId);
        File.WriteAllText(
            Path.Combine(_tempPack, MapPack.RoadGraphFile),
            $$"""{"schema_version": 1, "map_id": "{{mapId}}", "area": "test", "nodes": [], "edges": []}""");
        using var runner = ISceneRunner.Load(new Node3D { Name = "Root" }, true);
        var loader = new MapLoader { Name = "MapLoader", MapId = mapId };
        runner.Scene()!.AddChild(loader);
        var view = new RoadGraphDebugView { Name = "RoadGraphDebugView", Loader = loader };
        runner.Scene()!.AddChild(view);
        var capture = new ErrorCapture();
        OS.AddLogger(capture);
        bool loaded;
        try
        {
            loaded = loader.Load();
            await runner.SimulateFrames(1);
            AssertBool(view.SetEnabled(true)).IsTrue();
            await runner.AwaitIdleFrame();
        }
        finally
        {
            OS.RemoveLogger(capture);
        }

        AssertBool(loaded).IsTrue();
        AssertThat(string.Join(" | ", capture.Errors.Where(e => e.Contains(mapId)))).IsEqual("");
        AssertThat(loader.RoadGraph).IsNotNull();
        AssertInt(loader.RoadGraph!.Nodes.Count).IsEqual(0);
        AssertInt(loader.RoadGraph.Edges.Count).IsEqual(0);

        AssertBool(view.Enabled).IsTrue();
        AssertThat(view.MeshInstance.Mesh).IsNull();
        AssertBool(view.LegendPanel.Visible).IsTrue();
        var texts = view.LegendPanel.GetChild(0).GetChildren()
            .SelectMany(row => row.GetChildren().OfType<Label>())
            .Select(label => label.Text)
            .ToList();
        AssertThat(texts).Contains(RoadGraphDebugView.EmptyGraphText);

        AssertBool(view.SetEnabled(false)).IsTrue();
        AssertBool(view.Enabled).IsFalse();
    }

    /// <summary>
    /// The first collision hit of a ray from (x, fromY, z) down to toY, with the name of the mesh that owns the
    /// body (the glTF node name without <c>-col</c>), or null for a body without a mesh parent (a building box).
    /// With <paramref name="skipBuildings"/>, collision-only building bodies are passed through.
    /// </summary>
    private static (Vector3 Position, string? Mesh)? CastDown(MapLoader loader, float x, float fromY, float z, float toY, bool skipBuildings = true) =>
        CastDown(loader, x, fromY, z, toY, out _, skipBuildings);

    private static (Vector3 Position, string? Mesh)? CastDown(
        MapLoader loader, float x, float fromY, float z, float toY, out Node? hitBody, bool skipBuildings = true)
    {
        hitBody = null;
        var space = loader.GetWorld3D().DirectSpaceState;
        var query = PhysicsRayQueryParameters3D.Create(new Vector3(x, fromY, z), new Vector3(x, toY, z));
        var excluded = new Godot.Collections.Array<Rid>();
        for (var i = 0; i < 16; i++)
        {
            query.Exclude = excluded;
            var hit = space.IntersectRay(query);
            if (hit.Count == 0)
            {
                return null;
            }

            var body = hit["collider"].AsGodotObject() as Node;
            var mesh = body?.GetParent() as MeshInstance3D;
            if (mesh is null && skipBuildings)
            {
                excluded.Add(hit["rid"].AsRid());
                continue;
            }

            hitBody = body;
            return (hit["position"].AsVector3(), mesh?.Name.ToString());
        }

        return null;
    }

    private sealed record Line(double[] X, double[] Z, double[] S, double Y0, double Y1)
    {
        /// <summary>Position on the centre line at distance <paramref name="s"/>, and the straight deck line's height.</summary>
        public (double X, double Z, double Y) At(double s)
        {
            var k = 0;
            while (k < S.Length - 2 && S[k + 1] < s)
            {
                k++;
            }

            var t = (s - S[k]) / (S[k + 1] - S[k]);
            var length = S[^1];
            return (X[k] + (t * (X[k + 1] - X[k])), Z[k] + (t * (Z[k + 1] - Z[k])), Y0 + ((Y1 - Y0) * s / length));
        }
    }

    /// <summary>The centre line of a bridge way's edges, chained end to end, with horizontal distances.</summary>
    private static Line CentreLine(RoadGraph graph, long way)
    {
        var edges = graph.Edges.Where(e => e.OsmWay == way && e.Bridge).ToList();
        AssertInt(edges.Count).OverrideFailureMessage($"way {way} has no bridge edges").IsGreater(0);
        var points = new List<LocalPoint>(edges[0].Points);
        edges.RemoveAt(0);
        while (edges.Count > 0)
        {
            var next = edges.FirstOrDefault(e => e.Points[0] == points[^1]);
            AssertThat(next).OverrideFailureMessage($"way {way}: bridge edges don't chain").IsNotNull();
            points.AddRange(next!.Points.Skip(1));
            edges.Remove(next);
        }

        var s = new double[points.Count];
        for (var i = 1; i < points.Count; i++)
        {
            s[i] = s[i - 1] + System.Math.Sqrt(
                System.Math.Pow(points[i].X - points[i - 1].X, 2) + System.Math.Pow(points[i].Z - points[i - 1].Z, 2));
        }

        // Graph points on a deck are on its straight line (spec "Straight deck").
        for (var i = 1; i < points.Count - 1; i++)
        {
            var expected = points[0].Y + ((points[^1].Y - points[0].Y) * s[i] / s[^1]);
            AssertFloat(System.Math.Abs(points[i].Y - expected)).IsLess(1e-5);
        }

        return new Line([.. points.Select(p => p.X)], [.. points.Select(p => p.Z)], s, points[0].Y, points[^1].Y);
    }

    /// <summary>
    /// Copies the Somero pack's manifest (with <paramref name="mapId"/>) and tile import files to a temporary pack
    /// without <c>road_graph.json</c>. The import files keep pointing at the already imported tile scenes; their
    /// UIDs are dropped so they don't clash with the originals.
    /// </summary>
    private int CopyPackWithoutRoadGraph(string mapId)
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

        AssertBool(File.Exists(Path.Combine(_tempPack, MapPack.RoadGraphFile))).IsFalse();
        return tiles.Count;
    }

    private static MapLoader Loader(ISceneRunner runner)
    {
        var loader = runner.Scene()!.GetNodeOrNull<MapLoader>("MapLoader");
        AssertThat(loader).IsNotNull();
        return loader!;
    }

    private static void AssertPackPresent()
    {
        var graph = $"res://maps/{MapId}/{MapPack.RoadGraphFile}";
        AssertBool(FileAccess.FileExists(graph))
            .OverrideFailureMessage($"no road graph at {graph}; {MapPack.RegenHint(MapId)}")
            .IsTrue();
    }
}
