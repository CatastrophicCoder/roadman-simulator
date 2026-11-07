using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using GdUnit4;
using Godot;
using Roadman.Autoload;
using Roadman.Core;
using Roadman.Core.Edge;
using Roadman.Core.Maps;
using Roadman.Core.Player;
using Roadman.Debug;
using Roadman.Hud;
using Roadman.OnFoot;
using Roadman.Scenes;
using Roadman.Scenes.Edge;
using static GdUnit4.Assertions;
using Directory = System.IO.Directory;
using File = System.IO.File;
using Path = System.IO.Path;
using RoadblockBarrier = Roadman.Scenes.Edge.Roadblock;
using VoidTriggerArea = Roadman.Scenes.Edge.VoidTrigger;

namespace Roadman.Tests;

/// <summary>
/// The world edge in Godot, Part A of <c>docs/specs/world-edge-game.md</c> (<c>Ac</c> numbers are its acceptance
/// criteria): the barriers and the void trigger the map loader builds (7, 8), the player against the barriers (9, 10),
/// the spring arm through them (11), the death sequence (13 to 16), the seeded respawn (18), a failed respawn (19), the
/// debug camera toggle during the sequence (20), a map reload during it (21), the safety net (22) and other bodies in
/// the void (23). Part B's criteria 12 and 17, the loose pieces' safety net and the death screen's dismiss press are in
/// <see cref="WorldEdgeBreakingTest"/>, which reuses the <c>internal</c> helpers here; criterion 23's "loose piece" here is a plain
/// <see cref="RigidBody3D"/> on <see cref="CollisionLayers.Barrier"/> given to <see cref="World.TrackVoidBody"/>, and its
/// <see cref="IVoidFaller"/> is <see cref="TestVoidFaller"/>.
/// <para>
/// Most run on the test-area pack (<see cref="TestPacks"/>; ADR 0020), and the full-pack checks (Ac8, Ac9) on whichever
/// Somero pack is built (the full pack, or the CI test area from the committed fixture), so
/// every position is derived from the loaded pack: barrier items from <see cref="Barriers.Layouts"/> (built from
/// <see cref="MapLoader.Boundary"/>), ring points, outward directions and ledge heights from
/// <see cref="MapLoader.EdgeGeometry"/>, ground heights from ray hits. A pack without an item type the criterion needs
/// prints an N/A line, which is allowed only on the test-area pack. Motion and timing are scripted per physics frame
/// through <see cref="SceneTree.PhysicsFrame"/> (emitted before every node's <c>_PhysicsProcess</c>, so the state read
/// there is the previous frame's result), never wall time. Criteria that don't measure the sequence's durations
/// shorten <see cref="World.FallTime"/> and <see cref="World.ScreenTime"/>; 14, 15 and 16 use the defaults.
/// </para>
/// </summary>
[TestSuite]
[RequireGodotRuntime]
public class WorldEdgeTest
{
    private const string WorldScenePath = "res://scenes/world/World.tscn";
    private const string MapId = "somero";

    /// <summary>A fixed seed, so the spawn and every derived placement are the same on each run.</summary>
    internal const int TestSeed = 20261104;

    /// <summary>Criterion 19: the seed the respawn draws are reset to, so the test can predict them.</summary>
    private const int RespawnSeed = 7;

    /// <summary>Physics frames allowed for the barriers to be built after the spawn.</summary>
    internal const int EdgeWaitFrames = 60;

    /// <summary>Criterion 16: physics frames after the respawn before the player must be on the floor.</summary>
    internal const int RespawnSettleFrames = 120;

    /// <summary>
    /// Fall and screen time for criteria that don't measure them (13 checks only the entry, 18 to 22 the states and
    /// the respawn), so a death takes 1 s instead of 6 s.
    /// </summary>
    internal const float ShortTime = 0.5f;

    /// <summary>Criterion 13: how far outward of the boundary the player is placed on the ledge.</summary>
    private const float LedgeEntryDistance = 1.5f;

    /// <summary>Street label test: how far within the label's reach the street at the ledge entry must be, m.</summary>
    private const float StreetReachMargin = 10f;

    /// <summary>Void trigger path: how far outward of the ledge entry the player is put above the prisms, m.</summary>
    private const float TriggerEntryOutward = 3f;

    /// <summary>Void trigger path: how far the capsule's bottom dips into the prisms below <c>top_y</c>, m.</summary>
    private const float TriggerDip = 0.1f;

    /// <summary>
    /// Criterion 14: how far past the fall's horizontal travel the ring must stay straight outward of the ledge entry, m
    /// (a search margin, not a tolerance).
    /// </summary>
    private const float FallCorridorMargin = 2f;

    /// <summary>The height above the ledge or ground a placed body starts at, so it doesn't start in the surface.</summary>
    internal const float PlaceLift = 0.05f;

    /// <summary>Shortest item the player runs into or stands at: the capsule (0.6 m wide) hits its middle.</summary>
    internal const float MinItemLength = 2f;

    /// <summary>Criterion 9: how far inside the barrier the run starts ("a few metres").</summary>
    internal const float RunStartDistance = 4f;

    /// <summary>Criterion 9: the spec's time limit for the stop.</summary>
    internal const float StopWithinSeconds = 3f;

    /// <summary>Criteria 9 and 10: the run-up's speed must reach <see cref="Player.RunSpeed"/> within this fraction.</summary>
    private const float SpeedTolerance = 0.05f;

    /// <summary>Criterion 9: largest change of the player's distance from the barrier line over the last half second that still counts as stopped, m.</summary>
    internal const float StoppedDrift = 0.01f;

    /// <summary>Criteria 14 and 23: the spec's tolerance on the velocity change per physics step (± 1 %).</summary>
    private const float PullTolerance = 0.01f;

    /// <summary>Criterion 15: the spec's position tolerance for the death camera's target.</summary>
    private const float CameraTargetTolerance = 0.01f;

    /// <summary>Criterion 15: largest angle between the camera's view and the direction to its look-at point, degrees.</summary>
    private const float LookAtToleranceDegrees = 0.1f;

    /// <summary>Criterion 22: "just" below or above the safety net, m.</summary>
    internal const float NetOffset = 0.01f;

    /// <summary>Criteria 9 to 11: steepest ground along a run-up or under a standing point, degrees.</summary>
    internal const float MaxRouteSlopeDegrees = 15f;

    /// <summary>Spacing of the samples along test routes, m (a search resolution, not a tolerance).</summary>
    internal const float SampleStep = 0.25f;

    /// <summary>Criterion 11: distances inward of a post at which the player is tried, m.</summary>
    private static readonly float[] ArmStandDistances = [3.8f, 4.4f, 3.2f];

    /// <summary>Criterion 11: height above a post's base the arm is aimed at, m (its collision box spans −0.3 to 0.9).</summary>
    private const float ArmAimHeight = 0.5f;

    /// <summary>Criteria 22 and 23: the edge of the test bodies' box, m.</summary>
    private const float TestBodySize = 0.4f;

    /// <summary>Criterion 23: the launch velocity's outward and upward parts, m/s.</summary>
    private const float LaunchOutward = 4f;

    private const float LaunchUp = 3f;

    /// <summary>Criterion 23: the short <see cref="World.MaxFallTime"/> of the timeout case, s.</summary>
    private const float ShortMaxFallTime = 0.5f;

    private static readonly string[] PlayerActions =
    [
        Player.MoveForwardAction, Player.MoveBackAction, Player.MoveLeftAction, Player.MoveRightAction,
        Player.RunAction, Player.RunToggleAction, Player.JumpAction, World.DebugCameraAction,
    ];

    /// <summary>The physical keys the tests inject (the two <c>debug_camera</c> keys).</summary>
    private static readonly Key[] InjectedKeys = [Key.Quoteleft, Key.Section];

    private string _tempPack = "";
    private Variant? _savedMapIdSetting;

    /// <summary>The suite runs on the test-area pack; Ac8 and Ac9, which reach <see cref="AssertNotFullPack"/>, use the full pack.</summary>
    [BeforeTest]
    public void UseTestPack() => TestPacks.UseTestArea();

    [AfterTest]
    public void CleanUp()
    {
        ReleaseAll();
        DebugBuild.Override = null;
        TestPacks.UseFullPack();
        if (_tempPack != "" && Directory.Exists(_tempPack))
        {
            Directory.Delete(_tempPack, recursive: true);
        }

        _tempPack = "";
        if (_savedMapIdSetting is { } saved)
        {
            ProjectSettings.SetSetting(MapLoader.MapIdSetting, saved);
            _savedMapIdSetting = null;
        }
    }

    // ----- Criterion 7: barriers and trigger placed -----

    /// <summary>
    /// One barrier item per roadblock and fence of <c>boundary.json</c>, of the right type, named after its id, under
    /// <c>MapLoader/Edge/Barriers</c>, each a <see cref="StaticBody3D"/> on <see cref="CollisionLayers.BarrierStatic"/> with
    /// its collision boxes and no visual children; the visuals in one <see cref="MultiMeshInstance3D"/> per type and
    /// piece kind; no <see cref="RigidBody3D"/> anywhere; a <see cref="VoidTriggerArea"/> with one shape per prism; no
    /// errors. A map reload rebuilds both, with the same counts.
    /// </summary>
    [TestCase]
    public async Task Ac7_World_PlacesOneBarrierItemPerRoadblockAndFenceAndAVoidTriggerShapePerPrism_AndAReloadRebuildsThem()
    {
        AssertPackPresent();
        var capture = new ErrorCapture();
        OS.AddLogger(capture);
        try
        {
            using var runner = ISceneRunner.Load(NewWorld(TestSeed), true);
            var world = await StartWorld(runner);
            var loader = world.MapLoader;
            var boundary = loader.Boundary!;
            GD.Print(Invariant(
                $"WorldEdgeTest: Ac7 on the '{boundary.Area}' pack: {boundary.Roadblocks.Count} roadblocks, {boundary.Fences.Count} fences ({boundary.Fences.Count(f => f.Type == FenceType.Water)} water), {boundary.VoidTrigger.Prisms.Count} prisms"));
            var first = AssertEdgeBuilt(world);

            loader.Load();
            AssertBool(await WaitForSpawn(world)).OverrideFailureMessage("the player didn't spawn after the reload").IsTrue();
            AssertBool(await loader.WaitForWorldEdgeAsync(EdgeWaitFrames)).OverrideFailureMessage("the barriers weren't rebuilt").IsTrue();
            await PhysicsFrames(world, 2);
            var second = AssertEdgeBuilt(world);

            AssertBool(GodotObject.IsInstanceValid(first.Barriers) && first.Barriers.IsInsideTree())
                .OverrideFailureMessage("the old barriers are still in the tree after the reload").IsFalse();
            AssertBool(GodotObject.IsInstanceValid(first.Trigger) && first.Trigger.IsInsideTree())
                .OverrideFailureMessage("the old void trigger is still in the tree after the reload").IsFalse();
            AssertThat(second.Barriers).IsNotSame(first.Barriers);
            AssertThat(second.Trigger).IsNotSame(first.Trigger);
            AssertInt(second.Items).IsEqual(first.Items);
            AssertInt(second.Pieces).IsEqual(first.Pieces);
            AssertInt(second.Batches).IsEqual(first.Batches);
            AssertInt(second.Shapes).IsEqual(first.Shapes);
        }
        finally
        {
            OS.RemoveLogger(capture);
        }

        AssertThat(string.Join(" | ", capture.ErrorsOnly)).IsEqual("");
    }

    /// <summary>
    /// A pack copy without <c>boundary.json</c>: no barriers, no trigger, nothing under <c>MapLoader/Edge</c>. The
    /// errors are the ones a pack without a boundary already gave before the world edge (player-on-foot criterion 9):
    /// the loader's error naming the file, and the spawn error that follows from it. Nothing else.
    /// </summary>
    [TestCase]
    public async Task Ac7_PackWithoutBoundaryJson_BuildsNoBarriersAndNoTrigger_AndPushesOnlyTheExistingErrors()
    {
        AssertPackPresent();
        var mapId = $"tmp-edge-no-boundary-{Guid.NewGuid():N}"[..30];
        CopyPack(mapId, boundary: null);
        _savedMapIdSetting = ProjectSettings.GetSetting(MapLoader.MapIdSetting, MapLoader.DefaultMapId);
        ProjectSettings.SetSetting(MapLoader.MapIdSetting, mapId);
        var capture = new ErrorCapture();
        OS.AddLogger(capture);
        try
        {
            using var runner = ISceneRunner.Load(NewWorld(TestSeed), true);
            var world = (World)runner.Scene()!;
            AssertThat(world.MapLoader.ResolvedMapId).IsEqual(mapId);
            await WaitUntil(world, () => capture.ErrorsOnly.Any(e => e.Contains("no player spawn")), world.SpawnPhysicsWaitFrames + 30);
            await PhysicsFrames(world, EdgeWaitFrames);

            var loader = world.MapLoader;
            AssertThat(loader.Manifest).IsNotNull();
            AssertThat(loader.Boundary).IsNull();
            AssertThat(loader.EdgeGeometry).IsNull();
            AssertThat(loader.VoidTrigger).IsNull();
            AssertThat(loader.Barriers).IsNull();
            AssertInt(loader.Edge.GetChildCount()).IsEqual(0);
            AssertInt(Descendants(world).OfType<BarrierItem>().Count()).IsEqual(0);
            AssertInt(Descendants(world).OfType<VoidTriggerArea>().Count()).IsEqual(0);
            AssertInt(Descendants(world).OfType<Barriers>().Count()).IsEqual(0);
        }
        finally
        {
            OS.RemoveLogger(capture);
        }

        var path = $"res://maps/{mapId}/{MapPack.BoundaryFile}";
        var errors = capture.ErrorsOnly;
        var loaderErrors = errors.Where(e => e.Contains("MapLoader") && e.Contains(path)).ToList();
        var spawnErrors = errors.Where(e => e.Contains("no player spawn")).ToList();
        GD.Print($"WorldEdgeTest: Ac7 errors without boundary.json: {string.Join(" | ", errors)}");
        AssertInt(loaderErrors.Count).OverrideFailureMessage($"expected one MapLoader error naming {path}; got: {string.Join(" | ", errors)}").IsEqual(1);
        AssertInt(spawnErrors.Count).OverrideFailureMessage($"expected one spawn error; got: {string.Join(" | ", errors)}").IsEqual(1);
        AssertInt(errors.Count).OverrideFailureMessage($"errors besides the boundary and spawn errors: {string.Join(" | ", errors)}").IsEqual(2);
    }

    // ----- Criterion 8: placement -----

    /// <summary>
    /// No barrier item's collision overlaps a <c>map_buildings</c> body (a shape query with each item shape) or a void
    /// prism (a shape query with each prism shape against the barrier layer, and no collision or piece corner in
    /// <see cref="EdgeGeometry.IsInVoid"/>); every roadblock stand's base is within 0.05 m of the ground or road hit
    /// under it.
    /// </summary>
    [TestCase]
    public async Task Ac8_NoBarrierOverlapsABuildingOrAVoidPrism_AndEveryRoadblockStandStandsOnTheGroundOrRoad()
    {
        TestPacks.UseFullPack(); // Full pack: the test has an N/A branch for the test area (AssertNotFullPack).
        AssertPackPresent();
        using var runner = ISceneRunner.Load(NewWorld(TestSeed), true);
        var world = await StartWorld(runner);
        var loader = world.MapLoader;
        var barriers = loader.Barriers!;
        var geometry = loader.EdgeGeometry!;
        var space = world.GetWorld3D().DirectSpaceState;
        await PhysicsFrames(world, 2);

        var buildingHits = new List<string>();
        var shapeCount = 0;
        foreach (var item in barriers.Items)
        {
            foreach (var shape in item.GetChildren().OfType<CollisionShape3D>())
            {
                shapeCount++;
                var query = new PhysicsShapeQueryParameters3D
                {
                    Shape = shape.Shape,
                    Transform = shape.GlobalTransform,
                    CollisionMask = CollisionLayers.Map,
                    CollideWithBodies = true,
                    CollideWithAreas = false,
                };
                var seen = new HashSet<ulong>();
                foreach (var hit in space.IntersectShape(query, 32))
                {
                    if (hit["collider"].AsGodotObject() is Node3D body && body.IsInGroup(MapLoader.BuildingGroup) && seen.Add(body.GetInstanceId()))
                    {
                        buildingHits.Add($"{item.ItemId}/{shape.Name} overlaps {body.Name} ({BuildingReach(body, FrameOf(item.Layout!))})");
                    }
                }
            }
        }

        // The building bodies are hollow trimeshes, so the shape query above sees only boxes that cross a wall, roof or
        // floor. A box inside a building's footprint is found with rays: down from above (the roof) and up from below
        // (the floor) at points spread over each collision box's footprint, overlapping the box's height range.
        var insideBuildings = new List<string>();
        var rayPoints = 0;
        foreach (var layout in barriers.Layouts)
        {
            foreach (var box in layout.Collision)
            {
                var axisX = ToVector(box.AxisX);
                var steps = Math.Max(1, Mathf.CeilToInt(Horizontal(axisX).Length() / SampleStep));
                for (var i = 0; i <= steps; i++)
                {
                    var u = ((float)i / steps) - 0.5f;
                    foreach (var w in new[] { -0.5f, 0f, 0.5f })
                    {
                        rayPoints++;
                        var centre = ToVector(box.Origin) + (axisX * u) + (ToVector(box.AxisZ) * w);
                        var bottom = centre.Y - ((float)box.AxisY.Y / 2f);
                        var top = centre.Y + ((float)box.AxisY.Y / 2f);
                        if (BuildingHit(space, new Vector3(centre.X, 10_000f, centre.Z), new Vector3(centre.X, -10_000f, centre.Z)) is not { } roof
                            || BuildingHit(space, new Vector3(centre.X, -10_000f, centre.Z), new Vector3(centre.X, 10_000f, centre.Z)) is not { } floor
                            || roof.Y < bottom || floor.Y > top)
                        {
                            continue;
                        }

                        insideBuildings.Add(Invariant(
                            $"{layout.Id} at ({centre.X:0.##}, {centre.Z:0.##}), y {bottom:0.##}..{top:0.##}, is inside {roof.Body.Name} (floor {floor.Y:0.##}, roof {roof.Y:0.##})"));
                    }
                }
            }
        }

        var prismHits = new List<string>();
        foreach (var prism in loader.VoidTrigger!.GetChildren().OfType<CollisionShape3D>())
        {
            var query = new PhysicsShapeQueryParameters3D
            {
                Shape = prism.Shape,
                Transform = prism.GlobalTransform,
                CollisionMask = CollisionLayers.BarrierStatic,
                CollideWithBodies = true,
                CollideWithAreas = false,
            };
            foreach (var hit in space.IntersectShape(query, 32))
            {
                if (hit["collider"].AsGodotObject() is BarrierItem item)
                {
                    prismHits.Add($"{prism.Name} overlaps {item.ItemId}");
                }
            }
        }

        var cornersInVoid = new List<string>();
        foreach (var layout in barriers.Layouts)
        {
            var boxes = layout.Collision.Select(b => ("collision", b)).Concat(layout.Pieces.Select(p => (p.Kind.ToString(), p.Box)));
            foreach (var (what, box) in boxes)
            {
                foreach (var corner in box.Corners().Where(geometry.IsInVoid).Take(1))
                {
                    cornersInVoid.Add(Invariant($"{layout.Id} {what} corner ({corner.X:0.###}, {corner.Y:0.###}, {corner.Z:0.###})"));
                }
            }
        }

        // The stands stand on the ring, where the terrain and road meshes end, so the ground under a stand is looked up
        // by the layout's own rule (BarrierLayout.StandGround): the ray right under it, else the one StandGroundInset
        // inward of it.
        var standErrors = new List<string>();
        var stands = 0;
        var insetStands = 0;
        var worstStand = 0f;
        var inset = barriers.Parameters().StandGroundInset;
        double? GroundOrRoad(double x, double z) => loader.GroundOrRoadHitAt((float)x, (float)z)?.Position.Y;
        foreach (var layout in barriers.Layouts.Where(l => l.Kind == BarrierKind.Roadblock))
        {
            foreach (var piece in layout.Pieces.Where(p => p.Kind == PieceKind.Stand))
            {
                stands++;
                var box = piece.Box;
                var baseY = (float)(box.Origin.Y - (box.AxisY.Y / 2));
                if (GroundOrRoad(box.Origin.X, box.Origin.Z) is null)
                {
                    insetStands++;
                }

                if (BarrierLayout.StandGround(GroundOrRoad, box.Origin.X, box.Origin.Z, layout.Outward, inset) is not { } ground)
                {
                    standErrors.Add(Invariant($"{layout.Id}: no ground or road under the stand at ({box.Origin.X:0.###}, {box.Origin.Z:0.###}) or {inset:0.###} m inward of it"));
                    continue;
                }

                var gap = Mathf.Abs(baseY - (float)ground);
                worstStand = Mathf.Max(worstStand, gap);
                if (gap > 0.05f)
                {
                    standErrors.Add(Invariant($"{layout.Id}: stand base y {baseY:0.###}, ground or road {ground:0.###}"));
                }
            }
        }

        GD.Print(Invariant(
            $"WorldEdgeTest: Ac8 {barriers.ItemCount} items, {shapeCount} collision shapes against buildings ({buildingHits.Count} overlaps), {rayPoints} footprint points against building interiors ({insideBuildings.Count} inside), {loader.VoidTrigger.ShapeCount} prisms against the barriers, {stands} roadblock stands ({insetStands} with the ground {inset:0.###} m inward, worst base gap {worstStand:0.####} m)"));
        foreach (var group in insideBuildings.GroupBy(s => s[..s.IndexOf(" at (", StringComparison.Ordinal)] + " in " + s[(s.IndexOf("inside ", StringComparison.Ordinal) + 7)..s.IndexOf(" (floor", StringComparison.Ordinal)]))
        {
            GD.Print($"WorldEdgeTest: Ac8 inside a building: {group.Key}, {group.Count()} points, first: {group.First()}");
        }

        AssertInt(shapeCount).IsGreater(0);
        AssertThat(string.Join("; ", buildingHits.Take(10))).IsEqual("");
        AssertThat(string.Join("; ", insideBuildings.Take(10))).IsEqual("");
        AssertThat(string.Join("; ", prismHits.Take(10))).IsEqual("");
        AssertThat(string.Join("; ", cornersInVoid.Take(10))).IsEqual("");
        if (stands == 0)
        {
            AssertNotFullPack(world, "Ac8", "has no roadblock stands");
        }

        AssertThat(string.Join("; ", standErrors.Take(10))).IsEqual("");
    }

    // ----- Criterion 9: barriers stop the player -----

    [TestCase]
    public async Task Ac9_RunningIntoALandFence_StopsThePlayerInsideTheBoundaryWithin3sAndBreaksNothing()
    {
        TestPacks.UseFullPack(); // Full pack: the test has an N/A branch for the test area (AssertNotFullPack).
        await AssertRunIntoBarrierStops(BarrierKind.LandFence);
    }

    [TestCase]
    public async Task Ac9_RunningIntoAWaterFence_StopsThePlayerInsideTheBoundaryWithin3sAndBreaksNothing()
    {
        TestPacks.UseFullPack(); // Full pack: the test has an N/A branch for the test area (AssertNotFullPack).
        await AssertRunIntoBarrierStops(BarrierKind.WaterFence);
    }

    [TestCase]
    public async Task Ac9_RunningIntoARoadblock_StopsThePlayerInsideTheBoundaryWithin3sAndBreaksNothing()
    {
        TestPacks.UseFullPack(); // Full pack: the test has an N/A branch for the test area (AssertNotFullPack).
        await AssertRunIntoBarrierStops(BarrierKind.Roadblock);
    }

    // ----- Criterion 10: jumping a fence -----

    /// <summary>
    /// The land fence where the terrain under its midpoint is lowest relative to its line (among those at least
    /// <see cref="MinItemLength"/> long with a walkable run-up): from a run-up at <see cref="Player.RunSpeed"/>, the
    /// player jumps where the apex falls on the fence and comes down beyond it (on the ledge, or in the void).
    /// </summary>
    [TestCase]
    public async Task Ac10_RunUpJump_ClearsTheLandFenceWithTheLowestTerrainUnderItsLine_AndLandsBeyondIt()
    {
        AssertPackPresent();
        using var runner = ISceneRunner.Load(NewWorld(TestSeed), true);
        var world = await StartWorld(runner);
        var loader = world.MapLoader;
        var player = world.Player!;
        var barriers = loader.Barriers!;
        var thickness = barriers.Settings.CollisionThickness;

        var apexDistance = player.RunSpeed * player.JumpSpeed / player.Gravity;
        var runUp = RunUpDistance(player.RunSpeed, player.Acceleration) + apexDistance + 1.5f;
        var candidates = barriers.Layouts
            .Where(l => l.Kind == BarrierKind.LandFence)
            .Select(FrameOf)
            .Where(f => f.Length >= MinItemLength)
            .Select(f =>
            {
                var mid = f.At(0.5f);
                var terrain = loader.TerrainHeightAt(mid.X, mid.Z);
                return (Item: f, Mid: mid, Gap: terrain is { } t ? mid.Y - t : float.NaN);
            })
            .Where(c => float.IsFinite(c.Gap))
            .OrderByDescending(c => c.Gap)
            .ToList();
        AssertInt(candidates.Count).OverrideFailureMessage("no land fence long enough to jump in its middle").IsGreater(0);

        (ItemFrame Item, Vector3 Mid, float Gap, Vector3 Start)? chosen = null;
        var rejected = 0;
        foreach (var candidate in candidates)
        {
            var start = candidate.Mid - (candidate.Item.Across * runUp);
            var stop = candidate.Mid - (candidate.Item.Across * ((thickness / 2f) + player.CapsuleRadius + 0.05f));
            if (WalkableRoute(world, player, start, stop) is { } feet)
            {
                chosen = (candidate.Item, candidate.Mid, candidate.Gap, feet);
                break;
            }

            rejected++;
        }

        AssertBool(chosen is not null).OverrideFailureMessage("no land fence has a walkable run-up").IsTrue();
        var (item, midPoint, gap, startFeet) = chosen!.Value;
        GD.Print(Invariant(
            $"WorldEdgeTest: Ac10 lowest terrain under a fence line: {candidates[0].Gap:0.###} m below {candidates[0].Item.Layout.Id}; jumped {item.Layout.Id} ({gap:0.###} m below its line, {rejected} fences with a lower terrain had no walkable run-up)"));

        var yaw = YawOf(item.Across);
        await PlaceAndSettle(world, player, startFeet, yaw);
        var fenceAt = Horizontal(midPoint - startFeet).Dot(item.Across);
        var beyond = (thickness / 2f) + player.CapsuleRadius;
        var jumped = false;
        var takeOffSpeed = 0f;
        var takeOff = 0f;
        var crossed = false;
        var leftFloor = false;
        var outcome = "";
        var maxBeyond = float.MinValue;
        var maxFrames = Mathf.CeilToInt(((fenceAt / player.RunSpeed) + 3f) * Engine.PhysicsTicksPerSecond);
        await Drive(world, frame =>
        {
            var p = player.GlobalPosition;
            var along = Horizontal(p - startFeet).Dot(item.Across);
            var past = Horizontal(p - midPoint).Dot(item.Across);
            maxBeyond = Mathf.Max(maxBeyond, past);
            if (frame == 0)
            {
                Input.ActionPress(Player.MoveForwardAction);
                Input.ActionPress(Player.RunAction);
                return false;
            }

            if (!jumped && along >= fenceAt - apexDistance)
            {
                jumped = true;
                takeOff = along;
                takeOffSpeed = Horizontal(player.Velocity).Length();
                Input.ActionPress(Player.JumpAction);
                return false;
            }

            if (!jumped)
            {
                return false;
            }

            Input.ActionRelease(Player.JumpAction);
            leftFloor |= !player.IsOnFloor();
            crossed |= past > beyond;
            if (world.Death.State != DeathState.Alive)
            {
                outcome = Invariant($"entered the void {past:0.###} m beyond the line");
                ReleaseAll();
                return true;
            }

            if (leftFloor && player.IsOnFloor())
            {
                outcome = Invariant($"landed {past:0.###} m beyond the line");
                ReleaseAll();
                return true;
            }

            return false;
        }, maxFrames);
        ReleaseAll();

        GD.Print(Invariant(
            $"WorldEdgeTest: Ac10 take-off {fenceAt - takeOff:0.##} m before the line at {takeOffSpeed:0.###} m/s (RunSpeed {player.RunSpeed}); {(outcome == "" ? "never came down" : outcome)}; furthest {maxBeyond:0.###} m beyond (fully beyond from {beyond:0.###} m)"));
        AssertBool(jumped).OverrideFailureMessage("never reached the take-off point").IsTrue();
        AssertFloat(takeOffSpeed).OverrideFailureMessage("the run-up didn't reach RunSpeed before the take-off")
            .IsGreaterEqual(player.RunSpeed * (1f - SpeedTolerance));
        AssertBool(crossed).OverrideFailureMessage(Invariant($"the player never got beyond the fence line (furthest {maxBeyond:0.###} m, needs {beyond:0.###} m)")).IsTrue();
        AssertThat(outcome).OverrideFailureMessage("the player neither landed nor entered the void").IsNotEqual("");
        AssertInt(barriers.BrokenItemIds.Count).IsEqual(0);
    }

    // ----- Criterion 11: the spring arm ignores barriers -----

    /// <summary>
    /// The player stands inside a land fence post, facing it, and the camera is turned to look back at the player from
    /// outside: the arm's line from the pivot to the camera passes through the post (a ray on the barrier layer hits
    /// the item every frame) and nothing on the map layer, and the spring arm keeps its full length.
    /// </summary>
    [TestCase]
    public async Task Ac11_CameraTurnedSoAFencePostIsBetweenPivotAndCamera_SpringArmKeepsItsFullLength()
    {
        AssertPackPresent();
        using var runner = ISceneRunner.Load(NewWorld(TestSeed), true);
        var world = await StartWorld(runner);
        var player = world.Player!;
        var camera = world.PlayerCamera!;
        var barriers = world.MapLoader.Barriers!;
        var space = world.GetWorld3D().DirectSpaceState;
        var arm = new SphereShape3D { Radius = camera.CameraCollisionRadius };
        var tried = 0;

        foreach (var layout in barriers.Layouts.Where(l => l.Kind == BarrierKind.LandFence))
        {
            var item = FrameOf(layout);
            if (item.Length < MinItemLength)
            {
                continue;
            }

            foreach (var postBase in layout.PostBases.Select(ToVector))
            {
                foreach (var distance in ArmStandDistances)
                {
                    var stand = postBase - (item.Across * distance);
                    if (StandingPoint(world, player, stand) is not { } feet)
                    {
                        continue;
                    }

                    tried++;
                    if (tried > 40)
                    {
                        break;
                    }

                    var pivotGuess = feet + (Vector3.Up * camera.PivotHeight);
                    var aim = postBase + (Vector3.Up * ArmAimHeight);
                    if (!ArmClearOfMapButThroughBarrier(space, arm, pivotGuess, aim, camera, out _))
                    {
                        continue;
                    }

                    await PlaceAndSettle(world, player, feet, YawOf(item.Across));
                    var pivot = player.GlobalPosition + (Vector3.Up * camera.PivotHeight);
                    if (!ArmClearOfMapButThroughBarrier(space, arm, pivot, aim, camera, out var pitch))
                    {
                        continue;
                    }

                    camera.Yaw = YawOf(-item.Across);
                    camera.Pitch = pitch;
                    var frames = Mathf.CeilToInt(camera.ArmLength / camera.ArmReturnSpeed * Engine.PhysicsTicksPerSecond) + 2;
                    var shortest = float.MaxValue;
                    var barrierHits = 0;
                    var mapHits = new List<string>();
                    await Drive(world, frame =>
                    {
                        if (frame == 0)
                        {
                            return false;
                        }

                        shortest = Mathf.Min(shortest, camera.SpringArm.SpringLength);
                        var from = camera.GlobalPosition;
                        var to = camera.Camera.GlobalPosition;
                        var barrierHit = space.IntersectRay(PhysicsRayQueryParameters3D.Create(from, to, CollisionLayers.BarrierStatic));
                        if (barrierHit.Count > 0 && barrierHit["collider"].AsGodotObject() is BarrierItem hit && hit.ItemId == layout.Id)
                        {
                            barrierHits++;
                        }

                        var mapHit = space.IntersectRay(PhysicsRayQueryParameters3D.Create(from, to, CollisionLayers.Map));
                        if (mapHit.Count > 0)
                        {
                            mapHits.Add($"frame {frame}: {(mapHit["collider"].AsGodotObject() as Node)?.Name}");
                        }

                        return false;
                    }, frames + 1);

                    GD.Print(Invariant(
                        $"WorldEdgeTest: Ac11 player {distance} m inside a post of {layout.Id}, pitch {Mathf.RadToDeg(pitch):0.##}°: shortest arm {shortest:0.####} m of {camera.ArmLength}, the post's item between pivot and camera in {barrierHits} of {frames} frames"));
                    AssertThat(string.Join("; ", mapHits.Take(5))).OverrideFailureMessage("a map body is between pivot and camera, so the test can't tell").IsEqual("");
                    AssertInt(barrierHits).OverrideFailureMessage("the fence wasn't between the pivot and the camera in every frame").IsEqual(frames);
                    AssertFloat(shortest).OverrideFailureMessage("the barrier shortened the spring arm").IsGreaterEqual(camera.ArmLength - 1e-4f);
                    return;
                }
            }
        }

        AssertBool(false).OverrideFailureMessage($"no land fence post with a standing point where the arm passes through it clear of the map ({tried} tried)").IsTrue();
    }

    // ----- Criterion 13: entering the void -----

    /// <summary>
    /// With money, two gear items and a wanted level, the player placed 1.5 m outward of a boundary point on the ledge
    /// enters the void one physics frame later: <c>Died</c> fires once, money, gear and wanted level are cleared, the
    /// losses hold what it had, its and its camera's input are off, its collision mask is 0 (the layer stays), and the
    /// death camera is current.
    /// </summary>
    [TestCase]
    public async Task Ac13_PlayerPlacedOnTheLedge_EntersTheVoidWithinOnePhysicsFrame_DiesOnceAndLosesEverything()
    {
        AssertPackPresent();
        var state = PlayerStateOfGame();
        state.Die();
        var died = 0;
        var diedSignals = 0;
        void OnDied() => died++;
        void OnPlayerDied() => diedSignals++;
        state.Died += OnDied;
        Events.Instance.PlayerDied += OnPlayerDied;
        try
        {
            using var runner = ISceneRunner.Load(NewWorld(TestSeed, ShortTime, ShortTime), true);
            var world = await StartWorld(runner);
            var entry = FindLedgeEntry(world);
            state.AddMoney(1234);
            state.AddGear("baseball_bat");
            state.AddGear("crowbar");
            state.SetWantedLevel(2);

            var result = await PlaceOnLedgeAndWaitForEntry(world, entry);
            var player = world.Player!;
            GD.Print(Invariant(
                $"WorldEdgeTest: Ac13 placed at {entry.Feet} ({LedgeEntryDistance} m outward of {entry.Item.Layout.Id}); state {world.Death.State} after {result.Frames} physics frame(s)"));
            AssertInt(result.Frames).OverrideFailureMessage("the player didn't enter the void within one physics frame").IsEqual(1);
            AssertThat(world.Death.State).IsEqual(DeathState.Falling);
            AssertInt(died).IsEqual(1);
            AssertInt(diedSignals).IsEqual(1);
            AssertInt(state.Money).IsEqual(0);
            AssertInt(state.Gear.Count).IsEqual(0);
            AssertInt(state.WantedLevel).IsEqual(0);
            AssertThat(world.Death.Losses).IsNotNull();
            AssertInt(world.Death.Losses!.Money).IsEqual(1234);
            AssertThat(string.Join(",", world.Death.Losses.Gear)).IsEqual("baseball_bat,crowbar");
            AssertBool(player.InputEnabled).IsFalse();
            AssertBool(world.PlayerCamera!.InputEnabled).IsFalse();
            AssertInt((int)player.CollisionMask).IsEqual(0);
            AssertInt((int)player.CollisionLayer).IsEqual((int)CollisionLayers.Player);
            AssertBool(world.DeathCamera.Current).IsTrue();
            AssertThat(world.GetViewport().GetCamera3D()).IsSame(world.DeathCamera);

            await PhysicsFrames(world, 5);
            AssertInt(died).OverrideFailureMessage("Died fired again while falling").IsEqual(1);
        }
        finally
        {
            state.Died -= OnDied;
            Events.Instance.PlayerDied -= OnPlayerDied;
        }
    }

    // ----- Criterion 14: the fall -----

    /// <summary>
    /// Over the whole of <see cref="World.FallTime"/>: every physics frame the player's distance outside the ring grows
    /// and its y falls, and its velocity changes by the pull (outward × <see cref="World.VoidOutwardAcceleration"/> +
    /// down × <see cref="World.VoidDownAcceleration"/>) × dt within 1 %, not by gravity alone.
    /// </summary>
    [TestCase]
    public async Task Ac14_DuringFalling_ThePlayerMovesOutwardAndDownEveryFrame_AndItsVelocityChangesByVoidPullPerStep()
    {
        AssertPackPresent();
        using var runner = ISceneRunner.Load(NewWorld(TestSeed), true);
        var world = await StartWorld(runner);
        var geometry = world.MapLoader.EdgeGeometry!;
        var entry = FindLedgeEntry(world);
        var result = await PlaceOnLedgeAndWaitForEntry(world, entry);
        AssertEntered(result);
        var player = world.Player!;
        var outward = ToVector(world.Death.Entry!.Outward);
        var expected = (outward * world.VoidOutwardAcceleration) + (Vector3.Down * world.VoidDownAcceleration);

        var samples = new List<(Vector3 Position, Vector3 Velocity, double Distance, DeathState State)>();
        await Drive(world, _ =>
        {
            var p = player.GlobalPosition;
            var distance = geometry.NearestRingPoint(p.X, p.Z).Distance * (geometry.IsInsideRing(p.X, p.Z) ? -1 : 1);
            samples.Add((p, player.Velocity, distance, world.Death.State));
            return world.Death.State != DeathState.Falling;
        }, Mathf.CeilToInt(world.FallTime * Engine.PhysicsTicksPerSecond) + 10);

        var dt = 1f / Engine.PhysicsTicksPerSecond;
        var step = expected * dt;
        var errors = new List<string>();
        var worst = 0f;
        var steps = 0;
        for (var i = 1; i < samples.Count; i++)
        {
            if (samples[i].State != DeathState.Falling)
            {
                break;
            }

            steps++;
            var (p0, v0, d0, _) = samples[i - 1];
            var (p1, v1, d1, _) = samples[i];
            var change = v1 - v0;
            var error = (change - step).Length() / step.Length();
            worst = Mathf.Max(worst, error);
            if (error > PullTolerance)
            {
                errors.Add(Invariant($"step {i}: dv {change} vs pull·dt {step} ({error * 100:0.##} %)"));
            }

            var outwardStep = Horizontal(p1 - p0).Dot(outward);
            if (!(outwardStep > 0f))
            {
                errors.Add(Invariant($"step {i}: moved {outwardStep:0.######} m along outward"));
            }

            if (!(d1 > d0))
            {
                errors.Add(Invariant($"step {i}: distance outside the ring {d0:0.#####} -> {d1:0.#####}"));
            }

            if (!(p1.Y < p0.Y))
            {
                errors.Add(Invariant($"step {i}: y {p0.Y:0.#####} -> {p1.Y:0.#####}"));
            }
        }

        var gravityOnly = Vector3.Down * player.Gravity * dt;
        GD.Print(Invariant(
            $"WorldEdgeTest: Ac14 {steps} falling steps, outward {outward}, pull {expected} m/s²; worst velocity-change error {worst * 100:0.###} %, gravity alone would be {(gravityOnly - step).Length() / step.Length() * 100:0.#} % off; fell {samples[0].Position.Y - samples[steps].Position.Y:0.##} m, {samples[steps].Distance - samples[0].Distance:0.##} m outward"));
        AssertInt(steps).OverrideFailureMessage("too few falling steps measured").IsGreaterEqual(Mathf.FloorToInt(world.FallTime * Engine.PhysicsTicksPerSecond) - 2);
        AssertFloat((gravityOnly - step).Length() / step.Length()).OverrideFailureMessage("the pull equals gravity, so the test can't tell").IsGreater(PullTolerance * 10f);
        AssertThat(string.Join("; ", errors.Take(10))).IsEqual("");
    }

    // ----- Criterion 15: the death camera -----

    /// <summary>
    /// The death camera starts at the player camera's global transform and field of view, reaches its target (the
    /// entry's nearest ring point at ledge height + outward × <see cref="DeathCamera.PullBackDistance"/> + up ×
    /// <see cref="DeathCamera.PullBackHeight"/>) within 0.01 m at <see cref="DeathCamera.PullBackTime"/> and holds it
    /// until the death screen, looking at the ring point <see cref="DeathCamera.LookAtDrop"/> below the ledge.
    /// </summary>
    [TestCase]
    public async Task Ac15_DeathCamera_StartsAtThePlayerCamera_ReachesItsTargetAtPullBackTimeAndHolds_LookingBelowTheLedge()
    {
        AssertPackPresent();
        using var runner = ISceneRunner.Load(NewWorld(TestSeed), true);
        var world = await StartWorld(runner);
        var geometry = world.MapLoader.EdgeGeometry!;
        var death = world.DeathCamera;
        var entry = FindLedgeEntry(world);
        var result = await PlaceOnLedgeAndWaitForEntry(world, entry);
        AssertEntered(result);

        AssertFloat(death.Fov).IsEqual(result.PlayerCameraFov);
        AssertFloat(death.StartFov).IsEqual(result.PlayerCameraFov);
        AssertFloat(death.StartTransform.Origin.DistanceTo(result.PlayerCameraTransform.Origin)).IsLessEqual(1e-4f);
        AssertFloat(BasisDifference(death.StartTransform.Basis, result.PlayerCameraTransform.Basis.Orthonormalized())).IsLessEqual(1e-4f);

        var entryPoint = world.Death.Entry!;
        var outward = ToVector(entryPoint.Outward);
        var nearest = geometry.NearestRingPoint(entryPoint.Position.X, entryPoint.Position.Z);
        var ledge = (float)geometry.LedgeHeightAt(nearest.S);
        var ring = new Vector3((float)nearest.Point.X, ledge, (float)nearest.Point.Z);
        var target = ring + (outward * death.PullBackDistance) + (Vector3.Up * death.PullBackHeight);
        var lookAt = ring + (Vector3.Down * death.LookAtDrop);
        var lineHeight = entry.Item.At(Mathf.Clamp(Horizontal(ring - entry.Item.Start).Dot(entry.Item.Along) / entry.Item.Length, 0f, 1f)).Y;
        GD.Print(Invariant(
            $"WorldEdgeTest: Ac15 ring point {ring} (fence line there {lineHeight:0.###}), target {target}, look-at {lookAt}; camera reports target {death.TargetPosition}, look-at {death.LookAtPoint}"));
        AssertFloat(Mathf.Abs(ledge - lineHeight)).OverrideFailureMessage("the ledge height isn't the fence line's").IsLessEqual(0.01f);
        AssertFloat(death.TargetPosition.DistanceTo(target)).IsLessEqual(CameraTargetTolerance);
        AssertFloat(death.LookAtPoint.DistanceTo(lookAt)).IsLessEqual(CameraTargetTolerance);

        var midway = new List<float>();
        var reached = new List<(double Elapsed, float Distance, float Angle)>();
        var falling = 0;
        await Drive(world, _ =>
        {
            if (world.Death.State != DeathState.Falling)
            {
                return true;
            }

            falling++;
            var elapsed = death.Elapsed;
            var distance = death.GlobalPosition.DistanceTo(target);
            if (elapsed <= death.PullBackTime / 2f)
            {
                midway.Add(distance);
            }
            else if (elapsed >= death.PullBackTime)
            {
                var forward = -death.GlobalTransform.Basis.Z;
                var angle = Mathf.RadToDeg(forward.AngleTo(lookAt - death.GlobalPosition));
                reached.Add((elapsed, distance, angle));
            }

            return false;
        }, Mathf.CeilToInt(world.FallTime * Engine.PhysicsTicksPerSecond) + 10);

        GD.Print(Invariant(
            $"WorldEdgeTest: Ac15 {falling} falling frames; {midway.Count} in the first half of the pull-back (closest {(midway.Count > 0 ? midway.Min() : float.NaN):0.###} m), {reached.Count} after PullBackTime {death.PullBackTime} s (worst {(reached.Count > 0 ? reached.Max(r => r.Distance) : float.NaN):0.#####} m, {(reached.Count > 0 ? reached.Max(r => r.Angle) : float.NaN):0.####}° off the look-at)"));
        AssertInt(midway.Count).OverrideFailureMessage("no frame in the first half of the pull-back").IsGreater(0);
        AssertFloat(midway.Min()).OverrideFailureMessage("the camera was at its target before half the pull-back").IsGreater(CameraTargetTolerance);
        AssertInt(reached.Count).OverrideFailureMessage("no frame after PullBackTime before the death screen").IsGreater(0);
        AssertFloat(reached.Max(r => r.Distance)).IsLessEqual(CameraTargetTolerance);
        AssertFloat(reached.Max(r => r.Angle)).IsLessEqual(LookAtToleranceDegrees);
        AssertBool(death.Current).IsTrue();
    }

    // ----- Criterion 16: death screen and respawn -----

    /// <summary>
    /// With the default times: the death screen shows at <see cref="World.FallTime"/> (to the physics frame) with its
    /// three lines and the player hidden; the respawn comes at <see cref="World.FallTime"/> +
    /// <see cref="World.ScreenTime"/> with zero velocity inside <c>spawn_area.open_ground</c>, and within 120 physics
    /// frames the player is on the floor with input on, its collision mask back, normal gravity, its camera current
    /// and behind it, the death screen hidden, and <see cref="World.PlayerRespawned"/> fired once.
    /// </summary>
    [TestCase]
    public async Task Ac16_DeathScreenAtFallTime_RespawnAtFallPlusScreenTime_OnOpenGroundWithEverythingBack()
    {
        AssertPackPresent();
        var state = PlayerStateOfGame();
        state.Die();
        using var runner = ISceneRunner.Load(NewWorld(TestSeed), true);
        var world = await StartWorld(runner);
        var boundary = world.MapLoader.Boundary!;
        var player = world.Player!;
        var entry = FindLedgeEntry(world);
        state.AddMoney(250);
        state.AddGear("crowbar");

        var frame = 0;
        var changes = new List<(DeathState From, DeathState To, int Frame)>();
        void OnChanged(DeathState from, DeathState to) => changes.Add((from, to, frame));
        world.Death.StateChanged += OnChanged;
        var respawns = new List<(Vector3 Position, Vector3 Velocity, float Yaw, float CameraYaw)>();
        world.PlayerRespawned += position => respawns.Add((position, player.Velocity, player.FacingYaw, world.PlayerCamera!.Yaw));
        try
        {
            void Count() => frame++;
            world.GetTree().PhysicsFrame += Count;
            try
            {
                var result = await PlaceOnLedgeAndWaitForEntry(world, entry);
                AssertEntered(result);

                (bool Shown, string Title, string Subtitle, string Losses, bool PlayerVisible)? atScreen = null;
                var shownWhileFalling = false;
                await Drive(world, _ =>
                {
                    if (world.Death.State == DeathState.Falling)
                    {
                        shownWhileFalling |= world.DeathScreen.IsShown;
                    }

                    if (world.Death.State == DeathState.DeathScreen && atScreen is null)
                    {
                        var screen = world.DeathScreen;
                        atScreen = (screen.IsShown, screen.TitleText, screen.SubtitleText, screen.LossesText, player.Visible);
                    }

                    return world.Death.State is DeathState.Alive or DeathState.Failed;
                }, Mathf.CeilToInt((world.FallTime + world.ScreenTime) * Engine.PhysicsTicksPerSecond) + 20);

                var ticks = Engine.PhysicsTicksPerSecond;
                var fallFrames = FramesFor(world.FallTime, ticks);
                var screenFrames = FramesFor(world.ScreenTime, ticks);
                var falling = changes.FirstOrDefault(c => c.To == DeathState.Falling);
                var screenAt = changes.FirstOrDefault(c => c.To == DeathState.DeathScreen);
                var respawnAt = changes.FirstOrDefault(c => c.To == DeathState.Respawning);
                GD.Print(Invariant(
                    $"WorldEdgeTest: Ac16 state changes as a late subscriber hears them: {string.Join(", ", changes.Select(c => $"{c.From}->{c.To}@{c.Frame}"))}; expected the screen {fallFrames} and the respawn {fallFrames + screenFrames} frames after the entry"));

                // In the order this late subscriber heard them: World completes the respawn after Advance returns, so
                // DeathScreen->Respawning is heard before Respawning->Alive.
                AssertThat(string.Join(",", changes.Select(c => $"{c.From}->{c.To}"))).IsEqual(
                    "Alive->Falling,Falling->DeathScreen,DeathScreen->Respawning,Respawning->Alive");
                AssertThat(world.Death.State).IsEqual(DeathState.Alive);
                AssertInt(changes.Single(c => c.To == DeathState.Alive).Frame).IsEqual(respawnAt.Frame);
                AssertInt(screenAt.Frame - falling.Frame).IsEqual(fallFrames);
                AssertInt(respawnAt.Frame - falling.Frame).IsEqual(fallFrames + screenFrames);
                AssertBool(shownWhileFalling).OverrideFailureMessage("the death screen showed while falling").IsFalse();

                AssertThat(atScreen).OverrideFailureMessage("no frame in DeathScreen").IsNotNull();
                AssertBool(atScreen!.Value.Shown).IsTrue();
                AssertThat(atScreen.Value.Title).IsEqual("The Earth is flat");
                AssertThat(atScreen.Value.Subtitle).IsEqual("You fell off the edge of the world");
                AssertThat(atScreen.Value.Losses).IsEqual("Money lost: 250 · Gear lost: Crowbar");
                AssertBool(atScreen.Value.PlayerVisible).OverrideFailureMessage("the player is visible on the death screen").IsFalse();

                AssertInt(respawns.Count).IsEqual(1);
                var (position, velocity, yaw, cameraYaw) = respawns[0];
                AssertThat(world.Player).IsSame(player);
                AssertThat(velocity).IsEqual(Vector3.Zero);
                AssertBool(SpawnPicker.Contains(boundary.SpawnArea.OpenGround, position.X, position.Z))
                    .OverrideFailureMessage($"respawn {position} is outside spawn_area.open_ground").IsTrue();
                AssertFloat(Mathf.Abs(Mathf.AngleDifference(yaw, cameraYaw))).OverrideFailureMessage("the camera isn't behind the player at the respawn").IsLessEqual(1e-4f);

                var onFloor = await WaitUntil(world, () => player.IsOnFloor() && !world.DeathScreen.IsShown, RespawnSettleFrames);
                GD.Print(Invariant($"WorldEdgeTest: Ac16 respawned at {position}, now {player.GlobalPosition}, on the floor {player.IsOnFloor()}, death screen shown {world.DeathScreen.IsShown}"));
                AssertBool(onFloor).OverrideFailureMessage($"not on the floor with the death screen hidden within {RespawnSettleFrames} physics frames").IsTrue();
                AssertBool(player.Visible).IsTrue();
                AssertBool(player.InputEnabled).IsTrue();
                AssertBool(world.PlayerCamera!.InputEnabled).IsTrue();
                AssertInt((int)player.CollisionMask).IsEqual((int)CollisionLayers.PlayerMask);
                AssertThat(player.GravityOverride).IsNull();
                AssertBool(world.PlayerCamera.IsCurrent).IsTrue();
                AssertThat(world.GetViewport().GetCamera3D()).IsSame(world.PlayerCamera.Camera);
                AssertBool(world.DeathCamera.Current).IsFalse();
                AssertBool(world.DeathScreen.IsShown).IsFalse();
                AssertThat(world.Hud.Target).IsSame(player);
                AssertInt(respawns.Count).OverrideFailureMessage("PlayerRespawned fired more than once").IsEqual(1);
            }
            finally
            {
                world.GetTree().PhysicsFrame -= Count;
            }
        }
        finally
        {
            world.Death.StateChanged -= OnChanged;
        }
    }

    /// <summary>
    /// The street label (spec, "Edge cases"): it keeps following the player while it falls, shows nothing from the death
    /// screen until the respawn (although a label still following the hidden player would name a street), and follows
    /// the player again after the respawn. The entry is the first ledge entry with a street well within the label's
    /// reach, so the label has something to show there.
    /// </summary>
    [TestCase]
    public async Task Ac16_StreetLabel_FollowsThePlayerWhileFalling_EmptyFromTheDeathScreenUntilTheRespawn()
    {
        AssertPackPresent();
        using var runner = ISceneRunner.Load(NewWorld(TestSeed, ShortTime, ShortTime), true);
        var world = await StartWorld(runner);
        var hud = world.Hud;
        var graph = world.MapLoader.RoadGraph;
        AssertThat(graph).OverrideFailureMessage("the pack has no road graph").IsNotNull();
        string StreetAt(Vector3 point, float reach) => StreetLabelHud.StreetTextAt(graph, point, reach);

        // A street within the label's reach less a margin, so the short fall (well under a metre outward) can't take the
        // player out of reach.
        var reach = hud.StreetMaxDistance - StreetReachMargin;
        var entry = FindLedgeEntry(world, feet => StreetAt(feet, reach) != "", Invariant($"a street within {reach} m"));
        var player = world.Player!;
        var result = await PlaceOnLedgeAndWaitForEntry(world, entry);
        AssertEntered(result);

        var falling = new List<(string Text, bool Suppressed, bool Following)>();
        var hidden = new List<(string Text, bool Suppressed, bool TargetNull, string Unsuppressed)>();
        await Drive(world, _ =>
        {
            switch (world.Death.State)
            {
                case DeathState.Falling:
                    falling.Add((hud.Text, hud.Suppressed, hud.Target == player));
                    return false;
                case DeathState.DeathScreen:
                    hidden.Add((hud.Text, hud.Suppressed, hud.Target is null, StreetAt(player.GlobalPosition, hud.StreetMaxDistance)));
                    return false;
                default:
                    return true;
            }
        }, Mathf.CeilToInt((world.FallTime + world.ScreenTime) * Engine.PhysicsTicksPerSecond) + 20);

        // The frame of the respawn has run; one more so the label has measured from the respawned player.
        await PhysicsFrames(world, 1);
        var afterText = hud.Text;
        var afterReference = hud.LastReferencePoint;
        GD.Print(Invariant(
            $"WorldEdgeTest: Ac16 street label: '{(falling.Count > 0 ? falling[0].Text : "")}' at the entry, {falling.Count} falling frames, {hidden.Count} death-screen frames (texts {string.Join("|", hidden.Select(h => h.Text).Distinct())}; a label following the hidden player would show '{(hidden.Count > 0 ? hidden[0].Unsuppressed : "")}'); after the respawn at {player.GlobalPosition}: '{afterText}'"));

        AssertThat(world.Death.State).IsEqual(DeathState.Alive);
        AssertInt(falling.Count).OverrideFailureMessage("no falling frame").IsGreater(0);
        AssertBool(falling.All(f => f.Following && !f.Suppressed && f.Text != ""))
            .OverrideFailureMessage("the street label didn't follow the falling player").IsTrue();
        AssertInt(hidden.Count).OverrideFailureMessage("no death-screen frame").IsGreater(0);
        AssertBool(hidden.All(h => h.Unsuppressed != ""))
            .OverrideFailureMessage("the test can't tell: no street near the hidden player").IsTrue();
        AssertBool(hidden.All(h => h.Text == "" && h.Suppressed && h.TargetNull))
            .OverrideFailureMessage("the street label wasn't empty on the death screen").IsTrue();
        AssertBool(hud.Suppressed).IsFalse();
        AssertThat(hud.Target).IsSame(player);
        AssertThat(afterReference).IsNotNull();
        AssertFloat(afterReference!.Value.DistanceTo(player.GlobalPosition)).IsLessEqual(1f);
        AssertThat(afterText).IsEqual(StreetAt(afterReference.Value, hud.StreetMaxDistance));
    }

    // ----- The void trigger's own path for the player -----

    /// <summary>
    /// Only the <see cref="VoidTriggerArea"/> catches the player: its capsule's bottom dips just into a prism from above
    /// <c>void_trigger.top_y</c>, outward of a ledge entry, where <see cref="World"/>'s own per-frame test points (the
    /// capsule's end-sphere centres moved one radius outward) are still above the prism and the safety net is far
    /// below. The trigger reports the player, the death sequence starts from that report (the entry's recorded
    /// position has both test points outside the void), with input off, mask 0, the void pull and the death camera,
    /// and no error is pushed from the area's callback or the frames after it.
    /// </summary>
    [TestCase]
    public async Task Ac13_PlayerCaughtOnlyByTheVoidTrigger_StartsTheDeathSequenceWithoutErrors()
    {
        AssertPackPresent();
        var capture = new ErrorCapture();
        OS.AddLogger(capture);
        try
        {
            using var runner = ISceneRunner.Load(NewWorld(TestSeed, ShortTime, ShortTime), true);
            var world = await StartWorld(runner);
            var geometry = world.MapLoader.EdgeGeometry!;
            var trigger = world.MapLoader.VoidTrigger!;
            var top = (float)world.MapLoader.Boundary!.VoidTrigger.TopY;
            var entry = FindLedgeEntry(world);
            var player = world.Player!;
            var radius = player.CapsuleRadius;
            var outward = entry.Item.Across;

            // Well inside the prisms' footprint (they start 1 m outward); the capsule's lowest TriggerDip m in the prism.
            var feet = entry.Feet + (outward * TriggerEntryOutward);
            feet.Y = top - TriggerDip;
            AssertBool(geometry.IsInVoid(new LocalPoint(feet.X, feet.Y + (TriggerDip / 2f), feet.Z)))
                .OverrideFailureMessage(Invariant($"the capsule's bottom at {feet} isn't in a prism")).IsTrue();
            AssertBool(PlayerTestPointsInVoid(geometry, player, feet))
                .OverrideFailureMessage(Invariant($"World's own test points at {feet} are already in the void")).IsFalse();

            var reported = 0;
            void OnEntered(Node3D body, Vector3 _)
            {
                if (body == player)
                {
                    reported++;
                }
            }

            trigger.BodyEnteredVoid += OnEntered;
            var frames = -1;
            try
            {
                await Drive(world, frame =>
                {
                    if (frame == 0)
                    {
                        player.PlaceAt(feet, YawOf(outward));
                        world.PlayerCamera!.SnapToTarget();
                        return false;
                    }

                    if (world.Death.State != DeathState.Alive)
                    {
                        frames = frame;
                        return true;
                    }

                    return false;
                }, 10);

                // Frames after the start: the mask, gravity and camera changes made from the area's callback settle.
                await PhysicsFrames(world, 10);
            }
            finally
            {
                trigger.BodyEnteredVoid -= OnEntered;
            }

            var at = world.Death.Entry?.Position;
            var atVector = at is { } p ? new Vector3((float)p.X, (float)p.Y, (float)p.Z) : Vector3.Zero;
            GD.Print(Invariant(
                $"WorldEdgeTest: void trigger path: placed at {feet} (top_y {top}), the trigger reported the player {reported} time(s), the death started after {frames} frame(s) at {atVector}; errors: {capture.ErrorsOnly.Count}"));
            AssertInt(frames).OverrideFailureMessage("the death sequence didn't start").IsGreater(0);
            AssertInt(reported).OverrideFailureMessage("the void trigger didn't report the player").IsGreater(0);
            AssertThat(at).IsNotNull();
            AssertBool(PlayerTestPointsInVoid(geometry, player, atVector))
                .OverrideFailureMessage("World's own test caught the player, not the trigger").IsFalse();
            AssertBool(geometry.IsBelowSafetyNet(atVector.Y, world.SafetyNetMargin)).IsFalse();
            AssertThat(world.Death.State).IsEqual(DeathState.Falling);
            AssertBool(player.InputEnabled).IsFalse();
            AssertInt((int)player.CollisionMask).IsEqual(0);
            AssertThat(player.GravityOverride).IsNotNull();
            AssertBool(world.DeathCamera.Current).IsTrue();
        }
        finally
        {
            OS.RemoveLogger(capture);
        }

        AssertThat(string.Join(" | ", capture.ErrorsOnly)).IsEqual("");
    }

    // ----- World-edge exports -----

    /// <summary>
    /// <see cref="World.VoidDownAcceleration"/> is the project gravity until it is set. A world-edge export set out of
    /// range (negative or not finite) pushes one error, however many physics frames pass, and is set back to its last
    /// valid value; the sequence keeps using that value.
    /// </summary>
    [TestCase]
    public async Task WorldEdgeExports_InvalidValue_PushesOneErrorAndKeepsTheLastValidValue()
    {
        AssertPackPresent();
        using var runner = ISceneRunner.Load(NewWorld(TestSeed, ShortTime, ShortTime), true);
        var world = await StartWorld(runner);
        var gravity = ProjectSettings.GetSetting(World.GravitySetting).AsSingle();
        AssertFloat(world.VoidDownAcceleration).IsEqual(gravity);
        AssertFloat(world.VoidPull.DownAcceleration).IsEqualApprox(gravity, 1e-6f);

        var capture = new ErrorCapture();
        OS.AddLogger(capture);
        float fallTime, maxFallTime, down, downAfterSet;
        double sequenceFallTime;
        try
        {
            world.FallTime = -1f;
            world.MaxFallTime = float.NaN;
            world.VoidDownAcceleration = float.PositiveInfinity;
            await PhysicsFrames(world, 5);
            fallTime = world.FallTime;
            maxFallTime = world.MaxFallTime;
            down = world.VoidDownAcceleration;
            sequenceFallTime = world.Death.FallTime;

            world.VoidDownAcceleration = 5f;
            await PhysicsFrames(world, 2);
            world.VoidDownAcceleration = -2f;
            await PhysicsFrames(world, 2);
            downAfterSet = world.VoidDownAcceleration;
        }
        finally
        {
            OS.RemoveLogger(capture);
        }

        var errors = capture.ErrorsOnly;
        GD.Print($"WorldEdgeTest: exports: {errors.Count} errors: {string.Join(" | ", errors)}");
        AssertFloat(fallTime).IsEqual(ShortTime);
        AssertFloat((float)sequenceFallTime).IsEqual(ShortTime);
        AssertFloat(maxFallTime).IsEqual(10f);
        AssertFloat(down).IsEqual(gravity);
        AssertFloat(downAfterSet).IsEqual(5f);
        AssertInt(errors.Count).IsEqual(4);
        AssertInt(errors.Count(e => e.Contains("FallTime must be", StringComparison.Ordinal) && !e.Contains("MaxFallTime", StringComparison.Ordinal))).IsEqual(1);
        AssertInt(errors.Count(e => e.Contains("MaxFallTime must be", StringComparison.Ordinal))).IsEqual(1);
        AssertInt(errors.Count(e => e.Contains("VoidDownAcceleration must be", StringComparison.Ordinal))).IsEqual(2);
    }

    // ----- Criterion 18: seeded respawn -----

    [TestCase]
    public async Task Ac18_SameSeed_TwoRunsOfTheSameDeath_RespawnAtTheSamePoint()
    {
        AssertPackPresent();
        var first = await RespawnPositionOfARun(TestSeed);
        var second = await RespawnPositionOfARun(TestSeed);
        GD.Print(Invariant($"WorldEdgeTest: Ac18 respawns with seed {TestSeed}: {first} and {second}"));
        AssertThat(second).IsEqual(first);
    }

    // ----- Criterion 19: failed respawn -----

    /// <summary>
    /// <see cref="World.SpawnMaxAttempts"/> lowered and, after the entry, the draws reset to a known seed with a post
    /// beside each of them (predicted with <see cref="SpawnPicker"/>, as player-on-foot criterion 9 does for the first
    /// spawn): the respawn pushes one spawn error naming the boundary file, the reason and <c>/regen-map</c>, frees the
    /// player, hides the death screen and ends in <see cref="DeathState.Failed"/> on the debug camera.
    /// </summary>
    [TestCase]
    public async Task Ac19_EveryRespawnDrawBlocked_PushesTheSpawnErrorFreesThePlayerHidesTheScreenAndEndsFailedOnTheDebugCamera()
    {
        AssertPackPresent();
        const int attempts = 5;
        using var runner = ISceneRunner.Load(NewWorld(TestSeed, ShortTime, ShortTime), true);
        var world = await StartWorld(runner);
        var boundary = world.MapLoader.Boundary!;
        var player = world.Player!;
        var entry = FindLedgeEntry(world);
        world.SpawnMaxAttempts = attempts;

        var bottom = (float)boundary.Cliff.BottomY;
        var top = 10_000f + world.SpawnDropHeight + player.CapsuleHeight;
        var picker = new SpawnPicker(boundary.SpawnArea, new Random(RespawnSeed));
        foreach (var draw in Enumerable.Range(0, attempts).Select(_ => picker.Next()))
        {
            AddPost(world, new Vector3((float)draw.X + (player.CapsuleRadius / 2f), 0f, (float)draw.Z), player.CapsuleRadius / 4f, bottom, top);
        }

        var capture = new ErrorCapture();
        OS.AddLogger(capture);
        try
        {
            var result = await PlaceOnLedgeAndWaitForEntry(world, entry);
            AssertEntered(result);
            world.ResetSpawnRandom(RespawnSeed);
            await WaitUntil(world, () => world.Death.State is DeathState.Alive or DeathState.Failed,
                Mathf.CeilToInt((world.FallTime + world.ScreenTime) * Engine.PhysicsTicksPerSecond) + 20);
            await PhysicsFrames(world, 2);

            AssertThat(world.Death.State).IsEqual(DeathState.Failed);
            AssertInt(world.LastSpawnAttempts).IsEqual(attempts);
            AssertThat(world.LastSpawnFailure).IsNotNull();
            AssertThat(world.LastSpawnFailure!).Contains($"none of {attempts} draws").Contains(world.MapLoader.BoundaryPath);
            AssertThat(world.Player).IsNull();
            AssertThat(world.PlayerCamera).IsNull();
            AssertBool(GodotObject.IsInstanceValid(player) && player.IsInsideTree()).OverrideFailureMessage("the player is still in the tree").IsFalse();
            AssertInt(Descendants(world).OfType<Player>().Count()).IsEqual(0);
            AssertInt(Descendants(world).OfType<PlayerCamera>().Count()).IsEqual(0);
            AssertBool(world.DeathScreen.IsShown).IsFalse();
            AssertBool(world.DeathCamera.Current).IsFalse();
            AssertBool(DebugBuild.IsDebug).IsTrue();
            AssertBool(world.Camera!.Current).IsTrue();
            AssertThat(world.GetViewport().GetCamera3D()).IsSame(world.Camera);
            AssertBool(world.ToggleDebugCamera()).IsFalse();
        }
        finally
        {
            OS.RemoveLogger(capture);
        }

        var path = $"{TestPacks.PackPath}/{MapPack.BoundaryFile}";
        var errors = capture.ErrorsOnly.Where(e => e.Contains("no player respawn")).ToList();
        AssertInt(errors.Count).OverrideFailureMessage($"no single respawn error; got: {string.Join(" | ", capture.ErrorsOnly)}").IsEqual(1);
        AssertThat(errors[0]).Contains(path).Contains($"/regen-map {MapId}").Contains($"none of {attempts} draws");
        AssertInt(capture.ErrorsOnly.Count).OverrideFailureMessage($"other errors: {string.Join(" | ", capture.ErrorsOnly)}").IsEqual(1);
    }

    // ----- Criterion 20: debug camera during the sequence -----

    /// <summary>
    /// During <see cref="DeathState.Falling"/> and <see cref="DeathState.DeathScreen"/> both <c>debug_camera</c> keys
    /// and <see cref="World.ToggleDebugCamera"/> change nothing: the death camera stays current.
    /// </summary>
    [TestCase]
    public async Task Ac20_DebugCameraToggle_DoesNothingWhileFallingOrOnTheDeathScreen()
    {
        AssertPackPresent();
        using var runner = ISceneRunner.Load(NewWorld(TestSeed, 1f, 1f), true);
        var world = await StartWorld(runner);
        var entry = FindLedgeEntry(world);
        var result = await PlaceOnLedgeAndWaitForEntry(world, entry);
        AssertEntered(result);

        foreach (var phase in new[] { DeathState.Falling, DeathState.DeathScreen })
        {
            AssertBool(await WaitUntil(world, () => world.Death.State == phase, Engine.PhysicsTicksPerSecond * 2)).IsTrue();
            TapKey(Key.Quoteleft);
            await PhysicsFrames(world, 2);
            TapKey(Key.Section);
            await PhysicsFrames(world, 2);
            AssertThat(world.Death.State).IsEqual(phase);
            AssertBool(world.ToggleDebugCamera()).IsFalse();
            AssertBool(world.DebugCameraActive).IsFalse();
            AssertBool(world.Camera!.Current).OverrideFailureMessage($"the debug camera became current in {phase}").IsFalse();
            AssertBool(world.DeathCamera.Current).OverrideFailureMessage($"the death camera isn't current in {phase}").IsTrue();
            AssertThat(world.GetViewport().GetCamera3D()).IsSame(world.DeathCamera);
        }
    }

    /// <summary>
    /// The debug camera current (the toggle on) when the player enters the void: the death camera still takes over,
    /// from the player camera's transform, and the respawn ends on the player camera with input on.
    /// </summary>
    [TestCase]
    public async Task Ac20_EnteringTheVoidWithTheDebugCameraOn_RunsTheSameSequenceAndComesBackOnThePlayerCamera()
    {
        AssertPackPresent();
        using var runner = ISceneRunner.Load(NewWorld(TestSeed, ShortTime, ShortTime), true);
        var world = await StartWorld(runner);
        var player = world.Player!;
        var entry = FindLedgeEntry(world);
        AssertBool(world.ToggleDebugCamera()).IsTrue();
        AssertBool(world.DebugCameraActive).IsTrue();
        AssertBool(world.Camera!.Current).IsTrue();

        var result = await PlaceOnLedgeAndWaitForEntry(world, entry);
        AssertEntered(result);
        AssertThat(world.Death.State).IsEqual(DeathState.Falling);
        AssertBool(world.DeathCamera.Current).IsTrue();
        AssertBool(world.Camera.Current).IsFalse();
        AssertFloat(world.DeathCamera.StartTransform.Origin.DistanceTo(result.PlayerCameraTransform.Origin)).IsLessEqual(1e-4f);
        AssertFloat(BasisDifference(world.DeathCamera.StartTransform.Basis, result.PlayerCameraTransform.Basis.Orthonormalized())).IsLessEqual(1e-4f);

        var respawned = 0;
        world.PlayerRespawned += _ => respawned++;
        AssertBool(await WaitUntil(world, () => world.Death.State is DeathState.Alive or DeathState.Failed,
            Mathf.CeilToInt((world.FallTime + world.ScreenTime) * Engine.PhysicsTicksPerSecond) + 20)).IsTrue();
        await PhysicsFrames(world, 2);
        AssertThat(world.Death.State).IsEqual(DeathState.Alive);
        AssertInt(respawned).IsEqual(1);
        AssertThat(world.Player).IsSame(player);
        AssertBool(world.DebugCameraActive).IsFalse();
        AssertBool(world.PlayerCamera!.IsCurrent).IsTrue();
        AssertThat(world.GetViewport().GetCamera3D()).IsSame(world.PlayerCamera.Camera);
        AssertBool(world.Camera.Current).IsFalse();
        AssertBool(player.InputEnabled).IsTrue();
        AssertBool(world.PlayerCamera.InputEnabled).IsTrue();
    }

    // ----- Criterion 21: map reload during the sequence -----

    [TestCase]
    public async Task Ac21_MapReloadWhileFalling_RemovesDeathCameraAndScreenResetsRebuildsAndSpawnsWithoutErrors() =>
        await AssertReloadDuring(DeathState.Falling);

    [TestCase]
    public async Task Ac21_MapReloadOnTheDeathScreen_RemovesDeathCameraAndScreenResetsRebuildsAndSpawnsWithoutErrors() =>
        await AssertReloadDuring(DeathState.DeathScreen);

    // ----- Criterion 22: the safety net -----

    /// <summary>
    /// The player teleported inside the boundary (not in the trigger) to just above <c>cliff.bottom_y</c> −
    /// <see cref="World.SafetyNetMargin"/>: after one physics frame nothing has happened; to just below: after one
    /// physics frame the death sequence runs.
    /// </summary>
    [TestCase]
    public async Task Ac22_SafetyNet_PlayerJustBelowStartsTheDeathWithinOnePhysicsFrame_JustAboveDoesNothing()
    {
        AssertPackPresent();
        using var runner = ISceneRunner.Load(NewWorld(TestSeed, ShortTime, ShortTime), true);
        var world = await StartWorld(runner);
        var (net, x, z) = SafetyNetPoint(world);
        var player = world.Player!;
        var stateAbove = DeathState.Failed;
        var stateBelow = DeathState.Alive;
        await Drive(world, frame =>
        {
            switch (frame)
            {
                case 0:
                    player.PlaceAt(new Vector3(x, net + NetOffset, z), 0f);
                    return false;
                case 1:
                    stateAbove = world.Death.State;
                    player.PlaceAt(new Vector3(x, net - NetOffset, z), 0f);
                    return false;
                default:
                    stateBelow = world.Death.State;
                    return true;
            }
        }, 3);

        GD.Print(Invariant($"WorldEdgeTest: Ac22 net at y {net:0.###} under ({x:0.##}, {z:0.##}): {NetOffset} m above -> {stateAbove}, {NetOffset} m below -> {stateBelow}"));
        AssertThat(stateAbove).IsEqual(DeathState.Alive);
        AssertThat(stateBelow).IsEqual(DeathState.Falling);
        AssertInt((int)player.CollisionMask).IsEqual(0);
        AssertBool(world.DeathCamera.Current).IsTrue();
    }

    /// <summary>
    /// A tracked plain rigid body (a "loose piece" on the barrier layer) and a tracked <see cref="TestVoidFaller"/>
    /// teleported to just above the safety net are left alone for a physics frame; just below, the piece is freed and
    /// the faller gets <see cref="IVoidFaller.EnterVoid"/> once (also after more frames down there).
    /// </summary>
    [TestCase]
    public async Task Ac22_SafetyNet_LoosePieceJustBelowIsFreed_VoidFallerGetsEnterVoidOnce_JustAboveNeither()
    {
        AssertPackPresent();
        using var runner = ISceneRunner.Load(NewWorld(TestSeed, ShortTime, ShortTime), true);
        var world = await StartWorld(runner);
        var (net, x, z) = SafetyNetPoint(world);
        var pieceAt = new Vector3(x, 0f, z);
        var fallerAt = new Vector3(x + 2f, 0f, z);

        // Added at the "above" spot: this code runs inside a physics frame's signal, before World's safety-net check of
        // that frame, so a body left at the origin (below the net) would be handled at once.
        var piece = NewTestBody(new RigidBody3D { Name = "TestLoosePiece", Position = pieceAt with { Y = net + NetOffset } });
        var faller = NewTestBody(new TestVoidFaller { Name = "TestVoidFaller", Position = fallerAt with { Y = net + NetOffset } });
        world.AddChild(piece);
        world.AddChild(faller);
        world.TrackVoidBody(piece);
        world.TrackVoidBody(faller);

        var pieceFreedAbove = true;
        var fallerAbove = -1;
        var pieceFreedBelow = false;
        var fallerBelow = -1;
        await Drive(world, frame =>
        {
            switch (frame)
            {
                case 0:
                    piece.GlobalPosition = pieceAt with { Y = net + NetOffset };
                    faller.GlobalPosition = fallerAt with { Y = net + NetOffset };
                    return false;
                case 1:
                    pieceFreedAbove = IsFreed(piece);
                    fallerAbove = faller.Entries.Count;
                    piece.GlobalPosition = pieceAt with { Y = net - NetOffset };
                    faller.GlobalPosition = fallerAt with { Y = net - NetOffset };
                    return false;
                default:
                    pieceFreedBelow = IsFreed(piece);
                    fallerBelow = faller.Entries.Count;
                    return true;
            }
        }, 3);
        await PhysicsFrames(world, 10);

        GD.Print(Invariant(
            $"WorldEdgeTest: Ac22 net at y {net:0.###}: above -> piece freed {pieceFreedAbove}, faller entries {fallerAbove}; below -> piece freed {pieceFreedBelow}, faller entries {fallerBelow}, after 10 more frames {faller.Entries.Count}"));
        AssertBool(pieceFreedAbove).IsFalse();
        AssertInt(fallerAbove).IsEqual(0);
        AssertBool(pieceFreedBelow).OverrideFailureMessage("the loose piece wasn't freed within one physics frame").IsTrue();
        AssertInt(fallerBelow).OverrideFailureMessage("the faller didn't get EnterVoid within one physics frame").IsEqual(1);
        AssertInt(faller.Entries.Count).OverrideFailureMessage("the faller got EnterVoid more than once").IsEqual(1);
        AssertFloat(faller.Entries[0].Y).IsEqual(0f);
        AssertFloat(Mathf.Abs(faller.Entries[0].Length() - 1f)).IsLessEqual(1e-5f);
        AssertBool(GodotObject.IsInstanceValid(faller) && !faller.IsQueuedForDeletion()).OverrideFailureMessage("the faller was freed").IsTrue();
        AssertThat(world.Death.State).IsEqual(DeathState.Alive);
    }

    // ----- Criterion 23: other bodies in the void -----

    /// <summary>
    /// A tracked rigid body on the barrier layer launched outward off the ledge enters the trigger, gets gravity scale
    /// 0, and while past the ledge its velocity changes by the pull × dt per step (± 1 %; the body has no damping); it
    /// is freed within a physics frame of dropping below <c>void_trigger.bottom_y</c>, not before.
    /// </summary>
    [TestCase]
    public async Task Ac23_LooseBodyLaunchedOverTheEdge_FallsUnderVoidPull_AndIsFreedBelowTheTriggerBottom()
    {
        AssertPackPresent();
        using var runner = ISceneRunner.Load(NewWorld(TestSeed), true);
        var world = await StartWorld(runner);
        var trace = await LaunchOverTheEdge(world, maxFallTime: null);
        var bottom = (float)world.MapLoader.Boundary!.VoidTrigger.BottomY;

        var firstBelow = trace.Samples.FindIndex(s => s.Position.Y < bottom);
        GD.Print(Invariant(
            $"WorldEdgeTest: Ac23 launched from {trace.Samples[0].Position}: falling from sample {trace.FallingFrom}, {trace.PullSteps} pull steps past the ledge (worst {trace.WorstPullError * 100:0.###} %), first below y {bottom:0.###} at sample {firstBelow}, freed at sample {trace.FreedAt}"));
        AssertInt(trace.FallingFrom).OverrideFailureMessage("the body never started falling (never entered the trigger)").IsGreaterEqual(0);
        AssertFloat(trace.GravityScaleWhileFalling).IsEqual(0f);
        AssertInt(trace.PullSteps).OverrideFailureMessage("no pull step measured past the ledge").IsGreater(10);
        AssertThat(string.Join("; ", trace.PullErrors.Take(10))).IsEqual("");
        AssertInt(firstBelow).OverrideFailureMessage("the body never got below the trigger's bottom").IsGreaterEqual(0);
        AssertInt(trace.FreedAt).OverrideFailureMessage("the body wasn't freed").IsGreaterEqual(0);
        AssertInt(trace.FreedAt).OverrideFailureMessage("freed before it was below the trigger's bottom").IsGreater(firstBelow);
        AssertInt(trace.FreedAt - firstBelow).OverrideFailureMessage("not freed within a physics frame of dropping below the trigger's bottom").IsLessEqual(1);
    }

    /// <summary>
    /// The same launch with <see cref="World.MaxFallTime"/> 0.5 s: the body is freed that long after it started
    /// falling (to the physics frame), still above <c>void_trigger.bottom_y</c>.
    /// </summary>
    [TestCase]
    public async Task Ac23_LooseBodyLaunchedOverTheEdge_IsFreedAfterMaxFallTime()
    {
        AssertPackPresent();
        using var runner = ISceneRunner.Load(NewWorld(TestSeed), true);
        var world = await StartWorld(runner);
        var trace = await LaunchOverTheEdge(world, maxFallTime: ShortMaxFallTime);
        var bottom = (float)world.MapLoader.Boundary!.VoidTrigger.BottomY;
        var expected = FramesFor(ShortMaxFallTime, Engine.PhysicsTicksPerSecond);
        GD.Print(Invariant(
            $"WorldEdgeTest: Ac23 MaxFallTime {ShortMaxFallTime} s: falling from sample {trace.FallingFrom}, freed at sample {trace.FreedAt} (expected about {expected} frames later), last y {trace.Samples[^1].Position.Y:0.###}"));
        AssertInt(trace.FallingFrom).IsGreaterEqual(0);
        AssertInt(trace.FreedAt).OverrideFailureMessage("the body wasn't freed").IsGreaterEqual(0);
        AssertInt(trace.FreedAt - trace.FallingFrom).IsBetween(expected, expected + 1);
        AssertFloat(trace.Samples[^1].Position.Y).IsGreater(bottom);
    }

    /// <summary>
    /// A <see cref="TestVoidFaller"/> put on the ledge inside the trigger gets <see cref="IVoidFaller.EnterVoid"/> once,
    /// with the unit horizontal outward vector there; moved out of the trigger and back in, it gets no second call.
    /// </summary>
    [TestCase]
    public async Task Ac23_VoidFallerEnteringTheTrigger_GetsEnterVoidOnceWithTheOutwardVector()
    {
        AssertPackPresent();
        using var runner = ISceneRunner.Load(NewWorld(TestSeed), true);
        var world = await StartWorld(runner);
        var entry = FindLedgeEntry(world);
        var inTrigger = entry.Feet + (Vector3.Up * ((TestBodySize / 2f) + PlaceLift));
        var outOfTrigger = entry.Item.At(0.5f) + (entry.Item.Across * 0.4f) + (Vector3.Up * ((TestBodySize / 2f) + PlaceLift));
        var faller = NewTestBody(new TestVoidFaller { Name = "TestVoidFaller", Position = outOfTrigger });
        world.AddChild(faller);

        var firstCall = -1;
        await Drive(world, frame =>
        {
            if (frame == 0)
            {
                faller.GlobalPosition = inTrigger;
                return false;
            }

            if (faller.Entries.Count > 0)
            {
                firstCall = frame;
                return true;
            }

            return false;
        }, 10);
        await PhysicsFrames(world, 10);
        var afterStay = faller.Entries.Count;

        faller.GlobalPosition = outOfTrigger;
        faller.LinearVelocity = Vector3.Zero;
        await PhysicsFrames(world, 5);
        faller.GlobalPosition = inTrigger;
        faller.LinearVelocity = Vector3.Zero;
        await PhysicsFrames(world, 5);

        var expected = world.MapLoader.EdgeGeometry!.OutwardAt(inTrigger.X, inTrigger.Z);
        GD.Print(Invariant(
            $"WorldEdgeTest: Ac23 faller at {inTrigger} ({LedgeEntryDistance} m outward of {entry.Item.Layout.Id}): first EnterVoid after {firstCall} frame(s), {afterStay} call(s) after staying, {faller.Entries.Count} after leaving and re-entering; outward {(faller.Entries.Count > 0 ? faller.Entries[0] : Vector3.Zero)}, expected ({expected.X:0.####}, {expected.Z:0.####})"));
        AssertInt(firstCall).OverrideFailureMessage("EnterVoid wasn't called").IsGreater(0);
        AssertInt(afterStay).IsEqual(1);
        AssertInt(faller.Entries.Count).OverrideFailureMessage("EnterVoid was called again on re-entering").IsEqual(1);
        var outward = faller.Entries[0];
        AssertFloat(outward.Y).IsEqual(0f);
        AssertFloat(Mathf.Abs(outward.Length() - 1f)).IsLessEqual(1e-5f);
        AssertFloat(outward.Dot(ToVector(expected))).IsGreaterEqual(0.999f);
        AssertFloat(outward.Dot(entry.Item.Across)).IsGreaterEqual(0.99f);
        AssertBool(GodotObject.IsInstanceValid(faller) && !faller.IsQueuedForDeletion()).OverrideFailureMessage("the faller was freed").IsTrue();
    }

    /// <summary>
    /// <see cref="World.TrackVoidBody"/> connects one <see cref="Node.TreeExiting"/> handler (tracking twice adds no
    /// second); a body removed from the tree is dropped and its handler disconnected, and isn't tracked again when it
    /// comes back until it is tracked again; <see cref="World.UntrackVoidBody"/> disconnects it too.
    /// </summary>
    [TestCase]
    public async Task TrackVoidBody_LeavingTheTree_DropsTheBodyAndDisconnectsItsHandler()
    {
        AssertPackPresent();
        using var runner = ISceneRunner.Load(NewWorld(TestSeed, ShortTime, ShortTime), true);
        var world = await StartWorld(runner);
        var (net, x, z) = SafetyNetPoint(world);
        var body = NewTestBody(new RigidBody3D { Name = "TestTrackedBody", Position = new Vector3(x, net + 50f, z), Freeze = true });
        world.AddChild(body);
        int Handlers() => body.GetSignalConnectionList(Node.SignalName.TreeExiting).Count;
        var before = Handlers();

        world.TrackVoidBody(body);
        world.TrackVoidBody(body);
        var tracked = world.TrackedVoidBodies.Contains(body);
        var whileTracked = Handlers();

        world.RemoveChild(body);
        var droppedOnExit = !world.TrackedVoidBodies.Contains(body);
        var afterExit = Handlers();
        world.AddChild(body);
        var trackedAfterReentry = world.TrackedVoidBodies.Contains(body);

        world.TrackVoidBody(body);
        var trackedAgain = world.TrackedVoidBodies.Contains(body);
        world.UntrackVoidBody(body);
        var afterUntrack = Handlers();
        var droppedOnUntrack = !world.TrackedVoidBodies.Contains(body);
        body.QueueFree();
        await PhysicsFrames(world, 1);

        GD.Print($"WorldEdgeTest: TrackVoidBody handlers: {before} before, {whileTracked} tracked, {afterExit} after leaving the tree, {afterUntrack} after untracking");
        AssertBool(tracked).IsTrue();
        AssertInt(whileTracked).IsEqual(before + 1);
        AssertBool(droppedOnExit).IsTrue();
        AssertInt(afterExit).IsEqual(before);
        AssertBool(trackedAfterReentry).IsFalse();
        AssertBool(trackedAgain).IsTrue();
        AssertBool(droppedOnUntrack).IsTrue();
        AssertInt(afterUntrack).IsEqual(before);
    }

    // ===== Helpers: criterion 7 =====

    private readonly record struct EdgeCounts(Barriers Barriers, VoidTriggerArea Trigger, int Items, int Pieces, int Batches, int Shapes);

    /// <summary>Criterion 7's checks on the loaded world; returns the nodes and counts for the reload comparison.</summary>
    private static EdgeCounts AssertEdgeBuilt(World world)
    {
        var loader = world.MapLoader;
        var boundary = loader.Boundary!;
        var barriers = loader.Barriers!;
        AssertThat(loader.GetNodeOrNull("Edge/Barriers")).IsSame(barriers);
        AssertBool(barriers.IsBuilt).IsTrue();
        AssertInt(barriers.ItemCount).IsEqual(boundary.Roadblocks.Count + boundary.Fences.Count);

        var expected = boundary.Roadblocks.Select(r => (r.Id, Kind: BarrierKind.Roadblock))
            .Concat(boundary.Fences.Select(f => (f.Id, Kind: f.Type == FenceType.Water ? BarrierKind.WaterFence : BarrierKind.LandFence)))
            .ToList();
        var problems = new List<string>();
        foreach (var (id, kind) in expected)
        {
            var item = barriers.ItemById(id);
            if (item is null)
            {
                problems.Add($"{id}: no item");
                continue;
            }

            var typeOk = kind switch
            {
                BarrierKind.Roadblock => item is RoadblockBarrier,
                BarrierKind.WaterFence => item is WaterFence,
                _ => item is LandFence,
            };
            if (!typeOk || item.Kind != kind)
            {
                problems.Add($"{id}: {item.GetType().Name} ({item.Kind}), expected {kind}");
            }

            if (item.Name != id.ValidateNodeName() || item.ItemId != id)
            {
                problems.Add($"{id}: named {item.Name}");
            }

            if (item.GetParent() != barriers || !item.IsInsideTree())
            {
                problems.Add($"{id}: not a child of Edge/Barriers");
            }

            if (item.CollisionLayer != CollisionLayers.BarrierStatic)
            {
                problems.Add($"{id}: layer {item.CollisionLayer}");
            }

            var shapes = item.GetChildren().OfType<CollisionShape3D>().Count();
            if (shapes == 0 || shapes != item.Layout!.Collision.Count)
            {
                problems.Add($"{id}: {shapes} collision shapes for {item.Layout!.Collision.Count} boxes");
            }

            if (Descendants(item).OfType<VisualInstance3D>().Any())
            {
                problems.Add($"{id}: has visual children");
            }

            if (barriers.InstancesOf(id).Count != item.Layout.Pieces.Count)
            {
                problems.Add($"{id}: {barriers.InstancesOf(id).Count} batch instances for {item.Layout.Pieces.Count} pieces");
            }
        }

        AssertThat(string.Join("; ", problems.Take(10))).IsEqual("");

        var visuals = Descendants(barriers).OfType<VisualInstance3D>().ToList();
        var batches = visuals.OfType<MultiMeshInstance3D>().ToList();
        var kinds = barriers.Layouts.SelectMany(l => l.Pieces.Select(p => (l.Kind, p.Kind))).Distinct().Count();
        AssertInt(visuals.Count).OverrideFailureMessage("a barrier visual isn't a MultiMeshInstance3D").IsEqual(batches.Count);
        AssertInt(batches.Count).IsEqual(barriers.Batches.Count);
        AssertInt(batches.Count).OverrideFailureMessage("not one batch per barrier type and piece kind").IsEqual(kinds);
        AssertBool(batches.All(b => b.GetParent() == barriers.BatchRoot)).IsTrue();
        var instances = batches.Sum(b => b.Multimesh.InstanceCount);
        AssertInt(instances).IsEqual(barriers.PieceCount);
        AssertInt(instances).IsEqual(barriers.Layouts.Sum(l => l.Pieces.Count));
        AssertInt(Descendants(world.GetTree().Root).OfType<RigidBody3D>().Count()).OverrideFailureMessage("a RigidBody3D is in the scene").IsEqual(0);
        AssertInt(barriers.LoosePieces.GetChildCount()).IsEqual(0);

        var trigger = loader.VoidTrigger!;
        AssertThat(loader.GetNodeOrNull("Edge/VoidTrigger")).IsSame(trigger);
        var prismShapes = trigger.GetChildren().OfType<CollisionShape3D>().ToList();
        AssertInt(prismShapes.Count).IsEqual(boundary.VoidTrigger.Prisms.Count);
        AssertInt(trigger.ShapeCount).IsEqual(boundary.VoidTrigger.Prisms.Count);
        AssertBool(prismShapes.All(s => s.Shape is ConvexPolygonShape3D { Points.Length: 8 })).IsTrue();
        AssertInt((int)trigger.CollisionMask & (int)CollisionLayers.Map).IsEqual(0);
        AssertInt((int)trigger.CollisionMask & (int)CollisionLayers.Player).IsEqual((int)CollisionLayers.Player);
        AssertInt((int)trigger.CollisionMask & (int)CollisionLayers.Barrier).IsEqual((int)CollisionLayers.Barrier);
        AssertInt((int)trigger.CollisionMask & (int)CollisionLayers.BarrierStatic).IsEqual(0);

        GD.Print(Invariant(
            $"WorldEdgeTest: Ac7 {barriers.ItemCount} items, {barriers.PieceCount} pieces in {batches.Count} batches, {prismShapes.Count} trigger shapes"));
        return new EdgeCounts(barriers, trigger, barriers.ItemCount, barriers.PieceCount, batches.Count, prismShapes.Count);
    }

    /// <summary>
    /// Copies the Somero pack's manifest and tile import files to a temporary pack <paramref name="mapId"/>, with
    /// <c>road_graph.json</c>, <c>buildings.json</c> and <c>pois.json</c> (their <c>map_id</c> set to it), and
    /// <c>boundary.json</c> changed by <paramref name="boundary"/>, or left out when it is null (as
    /// <see cref="PlayerOnFootTest"/> does). The source is <see cref="TestPacks.PackPath"/>; the override is cleared, so
    /// the temporary map id loads from <c>res://maps/&lt;mapId&gt;</c>.
    /// </summary>
    private int CopyPack(string mapId, Action<JsonNode>? boundary)
    {
        var source = ProjectSettings.GlobalizePath(TestPacks.PackPath);
        TestPacks.UseFullPack(); // The override would load the source pack instead of the temporary one.
        _tempPack = ProjectSettings.GlobalizePath($"res://maps/{mapId}");
        Directory.CreateDirectory(Path.Combine(_tempPack, "tiles"));
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(source, MapPack.ManifestFile)))!;
        manifest["map_id"] = mapId;
        File.WriteAllText(Path.Combine(_tempPack, MapPack.ManifestFile), manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        var tiles = manifest["tiles"]!.AsArray();
        foreach (var tile in tiles)
        {
            var file = tile!["file"]!.GetValue<string>();
            var import = File.ReadAllLines(Path.Combine(source, file + ".import")).Where(l => !l.StartsWith("uid="));
            File.WriteAllLines(Path.Combine(_tempPack, file + ".import"), import);
        }

        var files = new List<string> { MapPack.RoadGraphFile, MapPack.BuildingsFile, MapPack.PoisFile };
        if (boundary is not null)
        {
            files.Add(MapPack.BoundaryFile);
        }

        foreach (var file in files)
        {
            var json = JsonNode.Parse(File.ReadAllText(Path.Combine(source, file)))!;
            json["map_id"] = mapId;
            if (file == MapPack.BoundaryFile)
            {
                boundary!(json);
            }

            File.WriteAllText(Path.Combine(_tempPack, file), json.ToJsonString());
        }

        AssertBool(File.Exists(Path.Combine(_tempPack, MapPack.BoundaryFile))).IsEqual(boundary is not null);
        return tiles.Count;
    }

    // ===== Helpers: criteria 9 to 11 =====

    /// <summary>A barrier item's line in Godot terms: start, end, unit horizontal along, unit horizontal outward normal.</summary>
    internal readonly record struct ItemFrame(BarrierItemLayout Layout, Vector3 Start, Vector3 End, Vector3 Along, Vector3 Across, float Length)
    {
        public Vector3 At(float t) => Start.Lerp(End, t);
    }

    /// <summary>
    /// Criterion 8: the first hit on a <c>map_buildings</c> body along the ray from <paramref name="from"/> to
    /// <paramref name="to"/> (back faces included, other bodies skipped), with its y; null when there is none.
    /// </summary>
    private static (float Y, Node Body)? BuildingHit(PhysicsDirectSpaceState3D space, Vector3 from, Vector3 to)
    {
        var query = PhysicsRayQueryParameters3D.Create(from, to, CollisionLayers.Map);
        query.HitBackFaces = true;
        var excluded = new Godot.Collections.Array<Rid>();
        for (var i = 0; i < 16; i++)
        {
            query.Exclude = excluded;
            var hit = space.IntersectRay(query);
            if (hit.Count == 0)
            {
                return null;
            }

            if (hit["collider"].AsGodotObject() is Node body && body.IsInGroup(MapLoader.BuildingGroup))
            {
                return (hit["position"].AsVector3().Y, body);
            }

            excluded.Add(hit["rid"].AsRid());
        }

        return null;
    }

    /// <summary>
    /// Criterion 8's failure detail: the building body's collision shapes and their distinct vertices in the item's
    /// frame (along the item from its start, across its line with outward positive, and y), so a report can say where
    /// the overlap is.
    /// </summary>
    private static string BuildingReach(Node3D body, ItemFrame item)
    {
        var parts = new List<string>();
        foreach (var shape in body.GetChildren().OfType<CollisionShape3D>())
        {
            var points = shape.Shape switch
            {
                ConvexPolygonShape3D convex => convex.Points,
                ConcavePolygonShape3D concave => concave.GetFaces(),
                _ => [],
            };
            var vertices = points
                .Select(local => shape.GlobalTransform * local)
                .Select(p => (Along: Horizontal(p - item.Start).Dot(item.Along), Across: Horizontal(p - item.Start).Dot(item.Across), p.Y))
                .Select(v => Invariant($"({v.Along:0.##}, {v.Across:0.##}, y {v.Y:0.##})"))
                .Distinct()
                .ToList();
            parts.Add($"{shape.Shape?.GetType().Name} with {vertices.Count} vertices (along, across, y): {string.Join(" ", vertices.Take(24))}");
        }

        return Invariant($"item {item.Length:0.##} m long; ") + string.Join("; ", parts);
    }

    internal static ItemFrame FrameOf(BarrierItemLayout layout)
    {
        var start = ToVector(layout.Start);
        var end = ToVector(layout.End);
        var along = Horizontal(end - start);
        var length = along.Length();
        var outward = new Vector3((float)layout.Outward.X, 0f, (float)layout.Outward.Z).Normalized();
        if (length < 1e-6f)
        {
            return new ItemFrame(layout, start, end, new Vector3(-outward.Z, 0f, outward.X), outward, 0f);
        }

        along /= length;
        var across = new Vector3(along.Z, 0f, -along.X);
        if (across.Dot(outward) < 0f)
        {
            across = -across;
        }

        return new ItemFrame(layout, start, end, along, across, length);
    }

    /// <summary>
    /// Criterion 9: on the first item of <paramref name="kind"/> (longest first) with a walkable straight route from
    /// <see cref="RunStartDistance"/> inside its midpoint up to it, the player runs at <see cref="Player.RunSpeed"/>
    /// straight outward into it for <see cref="StopWithinSeconds"/>: it reaches the run speed, touches the item, never
    /// leaves the ring or enters the void, has stopped (its distance from the line steady over the last half second)
    /// within the time, and no item breaks or loses its body.
    /// </summary>
    private static async Task AssertRunIntoBarrierStops(BarrierKind kind)
    {
        AssertPackPresent();
        using var runner = ISceneRunner.Load(NewWorld(TestSeed), true);
        var world = await StartWorld(runner);
        var loader = world.MapLoader;
        var geometry = loader.EdgeGeometry!;
        var barriers = loader.Barriers!;
        var player = world.Player!;
        var thickness = barriers.Settings.CollisionThickness;
        var contact = (thickness / 2f) + player.CapsuleRadius;

        var items = barriers.Layouts.Where(l => l.Kind == kind).Select(FrameOf).Where(f => f.Length >= MinItemLength).OrderByDescending(f => f.Length).ToList();
        if (items.Count == 0)
        {
            AssertNotFullPack(world, "Ac9", $"has no {kind} at least {MinItemLength} m long");
            return;
        }

        (ItemFrame Item, Vector3 Mid, Vector3 Start)? chosen = null;
        foreach (var candidate in items)
        {
            var mid = candidate.At(0.5f);
            if (kind == BarrierKind.Roadblock && loader.GroundOrRoadHitAt(mid.X, mid.Z) is { } ground)
            {
                mid.Y = ground.Position.Y;
            }

            var start = mid - (candidate.Across * RunStartDistance);
            var stop = mid - (candidate.Across * (contact + 0.05f));
            if (WalkableRoute(world, player, start, stop) is { } feet)
            {
                chosen = (candidate, mid, feet);
                break;
            }
        }

        if (chosen is null && kind == BarrierKind.WaterFence)
        {
            AssertNotFullPack(world, "Ac9", "has no water fence with a walkable run-up");
            return;
        }

        AssertBool(chosen is not null).OverrideFailureMessage($"no {kind} has a walkable run-up of {RunStartDistance} m").IsTrue();
        var (item, midPoint, startFeet) = chosen!.Value;
        var itemCount = barriers.ItemCount;
        var bodies = barriers.Items.ToList();
        await PlaceAndSettle(world, player, startFeet, YawOf(item.Across));

        var frames = Mathf.RoundToInt(StopWithinSeconds * Engine.PhysicsTicksPerSecond);
        var distances = new List<float>();
        var maxSpeed = 0f;
        var leftRing = new List<string>();
        var states = new HashSet<DeathState>();
        await Drive(world, frame =>
        {
            if (frame == 0)
            {
                Input.ActionPress(Player.MoveForwardAction);
                Input.ActionPress(Player.RunAction);
            }

            var p = player.GlobalPosition;
            distances.Add(Horizontal(p - midPoint).Dot(item.Across));
            maxSpeed = Mathf.Max(maxSpeed, Horizontal(player.Velocity).Length());
            if (!geometry.IsInsideRing(p.X, p.Z))
            {
                leftRing.Add(Invariant($"frame {frame}: {p}"));
            }

            states.Add(world.Death.State);
            return false;
        }, frames + 1);
        ReleaseAll();

        var last = distances[^1];
        var tail = distances.Skip(distances.Count - (Engine.PhysicsTicksPerSecond / 2)).ToList();
        var drift = tail.Max() - tail.Min();
        var closest = distances.Max();
        GD.Print(Invariant(
            $"WorldEdgeTest: Ac9 {kind} {item.Layout.Id} ({item.Length:0.##} m), run from {RunStartDistance} m inside: top speed {maxSpeed:0.###} m/s, closest {-closest:0.###} m inside the line (contact at {contact:0.###} m), drift over the last 0.5 s {drift:0.#####} m"));
        AssertFloat(maxSpeed).OverrideFailureMessage("never reached RunSpeed").IsGreaterEqual(player.RunSpeed * (1f - SpeedTolerance));
        AssertFloat(closest).OverrideFailureMessage("never got to the barrier").IsGreaterEqual(-(contact + 0.05f));
        AssertFloat(closest).OverrideFailureMessage("the capsule got past the barrier line").IsLessEqual(-player.CapsuleRadius);
        AssertThat(string.Join("; ", leftRing.Take(5))).IsEqual("");
        AssertThat(string.Join(",", states)).IsEqual("Alive");
        AssertFloat(drift).OverrideFailureMessage("still moving at the end of the 3 s").IsLessEqual(StoppedDrift);
        AssertFloat(last).IsLess(0f);
        AssertInt(barriers.BrokenItemIds.Count).IsEqual(0);
        AssertInt(barriers.ItemCount).IsEqual(itemCount);
        AssertBool(bodies.All(b => GodotObject.IsInstanceValid(b) && b.IsInsideTree() && b.GetParent() == barriers)).OverrideFailureMessage("a barrier body left the tree").IsTrue();
        AssertInt(barriers.LoosePieces.GetChildCount()).IsEqual(0);
    }

    /// <summary>
    /// The ground feet at <paramref name="from"/> when the straight route to <paramref name="to"/> is walkable: every
    /// <see cref="SampleStep"/> the first map hit from above is terrain or road, the slope between samples is at most
    /// <see cref="MaxRouteSlopeDegrees"/>, and the capsule there overlaps nothing but terrain and roads. Null otherwise.
    /// </summary>
    private static Vector3? WalkableRoute(World world, Player player, Vector3 from, Vector3 to)
    {
        var horizontal = Horizontal(to - from);
        var length = horizontal.Length();
        var steps = Math.Max(1, Mathf.CeilToInt(length / SampleStep));
        Vector3? start = null;
        Vector3? previous = null;
        for (var i = 0; i <= steps; i++)
        {
            var point = from + (horizontal * ((float)i / steps));
            if (StandingPoint(world, player, point) is not { } feet)
            {
                return null;
            }

            if (previous is { } p && Slope(p, feet) > MaxRouteSlopeDegrees)
            {
                return null;
            }

            start ??= feet;
            previous = feet;
        }

        return start;
    }

    /// <summary>
    /// The feet on the ground or road at x/z of <paramref name="point"/> when the first map hit from above there is
    /// terrain or road and the player's capsule (lifted by <see cref="PlaceLift"/>) overlaps nothing on the map or
    /// barrier layers but terrain and roads. Null otherwise.
    /// </summary>
    private static Vector3? StandingPoint(World world, Player player, Vector3 point)
    {
        var space = world.GetWorld3D().DirectSpaceState;
        var ray = PhysicsRayQueryParameters3D.Create(new Vector3(point.X, 10_000f, point.Z), new Vector3(point.X, -10_000f, point.Z), CollisionLayers.Map);
        var hit = space.IntersectRay(ray);
        if (hit.Count == 0 || hit["collider"].AsGodotObject() is not Node first || !IsGround(first))
        {
            return null;
        }

        var feet = hit["position"].AsVector3();
        var query = new PhysicsShapeQueryParameters3D
        {
            Shape = new CapsuleShape3D { Radius = player.CapsuleRadius, Height = player.CapsuleHeight },
            Transform = new Transform3D(Basis.Identity, feet + (Vector3.Up * (PlaceLift + (player.CapsuleHeight / 2f)))),
            CollisionMask = CollisionLayers.PlayerMask,
            CollideWithBodies = true,
            CollideWithAreas = false,
            Exclude = [player.GetRid()],
        };
        foreach (var overlap in space.IntersectShape(query, 32))
        {
            if (overlap["collider"].AsGodotObject() is not Node body || !IsGround(body))
            {
                return null;
            }
        }

        return feet;
    }

    internal static bool IsGround(Node body) => body.IsInGroup(MapLoader.TerrainGroup) || body.IsInGroup(MapLoader.RoadGroup);

    /// <summary>
    /// Criterion 11: whether the spring arm from <paramref name="pivot"/> aimed through <paramref name="aim"/> (at the
    /// camera's arm length, within its pitch limits) hits nothing on the map layer but passes through a barrier item.
    /// <paramref name="pitch"/> is the camera pitch that points the arm there.
    /// </summary>
    private static bool ArmClearOfMapButThroughBarrier(PhysicsDirectSpaceState3D space, SphereShape3D arm, Vector3 pivot, Vector3 aim, PlayerCamera camera, out float pitch)
    {
        var direction = (aim - pivot).Normalized();
        pitch = Mathf.Asin(-direction.Y);
        if (pitch < Mathf.DegToRad(camera.MinPitchDegrees) || pitch > Mathf.DegToRad(camera.MaxPitchDegrees))
        {
            return false;
        }

        var end = pivot + (direction * camera.ArmLength);
        var mapCast = new PhysicsShapeQueryParameters3D
        {
            Shape = arm,
            Transform = new Transform3D(Basis.Identity, pivot),
            Motion = end - pivot,
            CollisionMask = CollisionLayers.Map,
            CollideWithBodies = true,
        };
        var map = space.CastMotion(mapCast);
        if (map.Length < 2 || map[0] < 1f || map[1] < 1f)
        {
            return false;
        }

        var through = space.IntersectRay(PhysicsRayQueryParameters3D.Create(pivot, end, CollisionLayers.BarrierStatic));
        return through.Count > 0 && through["collider"].AsGodotObject() is BarrierItem;
    }

    // ===== Helpers: the death sequence =====

    /// <summary>A ledge point 1.5 m outward of a land fence's midpoint, its fence, and the outward direction there.</summary>
    internal readonly record struct LedgeEntry(Vector3 Feet, ItemFrame Item);

    /// <summary>
    /// The first land fence in ring order at least <see cref="MinItemLength"/> long whose midpoint has the ledge
    /// (<c>map_edge</c>) <see cref="LedgeEntryDistance"/> m outward, at the ledge height there, and where the ring is
    /// straight for the whole fall: along the outward line from that point, over the fall's horizontal travel
    /// (<see cref="FallTravel"/>) + <see cref="FallCorridorMargin"/>, the nearest ring point stays the entry's and
    /// nothing else of the ring is nearer, so the distance from the ring grows by exactly the outward travel. Near a
    /// ring corner the nearest ring point would switch during the fall and the distance could shrink while the player
    /// still moves outward (criterion 14). <paramref name="accept"/>, when given, must also accept the entry's feet
    /// (<paramref name="acceptWhat"/> says what it asks for in the failure message).
    /// </summary>
    internal static LedgeEntry FindLedgeEntry(World world, Func<Vector3, bool>? accept = null, string? acceptWhat = null)
    {
        var loader = world.MapLoader;
        var geometry = loader.EdgeGeometry!;
        var corridor = FallTravel(world) + FallCorridorMargin;
        var cornered = 0;
        foreach (var layout in loader.Barriers!.Layouts.Where(l => l.Kind == BarrierKind.LandFence))
        {
            var item = FrameOf(layout);
            if (item.Length < MinItemLength)
            {
                continue;
            }

            var point = item.At(0.5f) + (item.Across * LedgeEntryDistance);
            var nearest = geometry.NearestRingPoint(point.X, point.Z);
            if (geometry.IsInsideRing(point.X, point.Z) || Mathf.Abs((float)nearest.Distance - LedgeEntryDistance) > 0.05f)
            {
                continue;
            }

            var outward = geometry.OutwardAt(point.X, point.Z);
            if ((outward.X * item.Across.X) + (outward.Z * item.Across.Z) < 1 - 1e-4)
            {
                continue;
            }

            if (!StraightRingCorridor(geometry, point, item.Across, nearest, corridor))
            {
                cornered++;
                continue;
            }

            var ledge = (float)geometry.LedgeHeightAt(nearest.S);
            if (loader.GroundOrEdgeHitAt(point.X, point.Z) is not { } hit || !hit.Body.IsInGroup(MapLoader.EdgeGroup)
                || Mathf.Abs(hit.Position.Y - ledge) > 0.3f)
            {
                continue;
            }

            if (accept is not null && !accept(hit.Position))
            {
                continue;
            }

            GD.Print(Invariant(
                $"WorldEdgeTest: ledge entry at {hit.Position} beside {layout.Id}, a straight ring for {corridor:0.##} m outward ({cornered} fence midpoint(s) before it skipped near a ring corner)"));
            return new LedgeEntry(hit.Position, item);
        }

        AssertBool(false).OverrideFailureMessage(Invariant(
            $"no land fence has the ledge 1.5 m outward of its midpoint with a straight ring for {corridor:0.##} m outward{(acceptWhat is null ? "" : $" and {acceptWhat}")} ({cornered} skipped near a ring corner)")).IsTrue();
        throw new InvalidOperationException("unreachable");
    }

    /// <summary>
    /// Whether either of <see cref="World"/>'s own per-frame void test points for a player with its feet at
    /// <paramref name="feet"/> is in the void: the centres of the capsule's two end spheres, moved one radius outward.
    /// </summary>
    private static bool PlayerTestPointsInVoid(EdgeGeometry geometry, Player player, Vector3 feet)
    {
        var outward = geometry.OutwardAt(feet.X, feet.Z);
        var radius = player.CapsuleRadius;
        var reach = new Vector3((float)outward.X, 0f, (float)outward.Z) * radius;
        foreach (var height in new[] { radius, Mathf.Max(radius, player.CapsuleHeight - radius) })
        {
            var point = feet + reach + (Vector3.Up * height);
            if (geometry.IsInVoid(new LocalPoint(point.X, point.Y, point.Z)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The player's horizontal travel during <see cref="World.FallTime"/> from rest under the void pull's outward part
    /// (½ a t², a = <see cref="World.VoidOutwardAcceleration"/>), m.
    /// </summary>
    private static float FallTravel(World world) => 0.5f * world.VoidOutwardAcceleration * world.FallTime * world.FallTime;

    /// <summary>
    /// Whether, at every <see cref="SampleStep"/> along <paramref name="outward"/> from <paramref name="from"/> up to
    /// <paramref name="length"/>, the nearest ring point is still <paramref name="entry"/>'s (within 1 cm) and the point
    /// is outside the ring at the entry's distance plus the travel (within 1 cm).
    /// </summary>
    private static bool StraightRingCorridor(EdgeGeometry geometry, Vector3 from, Vector3 outward, RingPoint entry, float length)
    {
        var steps = Mathf.CeilToInt(length / SampleStep);
        for (var i = 1; i <= steps; i++)
        {
            var t = Mathf.Min(length, i * SampleStep);
            var q = from + (outward * t);
            var nearest = geometry.NearestRingPoint(q.X, q.Z);
            var moved = Math.Sqrt(Math.Pow(nearest.Point.X - entry.Point.X, 2) + Math.Pow(nearest.Point.Z - entry.Point.Z, 2));
            if (geometry.IsInsideRing(q.X, q.Z) || moved > 0.01 || Math.Abs(nearest.Distance - (entry.Distance + t)) > 0.01)
            {
                return false;
            }
        }

        return true;
    }

    internal readonly record struct EntryResult(int Frames, Transform3D PlayerCameraTransform, float PlayerCameraFov);

    /// <summary>
    /// Places the player on <paramref name="entry"/> at the start of a physics frame (facing outward) and returns how
    /// many physics frames later the death sequence had started, with the player camera's transform and field of view
    /// from the last frame handler before the entry (the world's checks run after it, so as the death camera saw them).
    /// </summary>
    internal static async Task<EntryResult> PlaceOnLedgeAndWaitForEntry(World world, LedgeEntry entry)
    {
        var player = world.Player!;
        var camera = world.PlayerCamera!;
        var frames = -1;
        var transform = Transform3D.Identity;
        var fov = 0f;
        await Drive(world, frame =>
        {
            if (frame == 0)
            {
                // A teleport: the camera snaps with it, as it would have followed a player walking there.
                player.PlaceAt(entry.Feet + (Vector3.Up * PlaceLift), YawOf(entry.Item.Across));
                camera.SnapToTarget();
            }
            else if (world.Death.State != DeathState.Alive)
            {
                frames = frame;
                return true;
            }

            // The world's void checks run after this handler in the same frame, so this is what the death camera sees.
            transform = camera.Camera.GlobalTransform;
            fov = camera.Camera.Fov;
            return false;
        }, 10);
        return new EntryResult(frames, transform, fov);
    }

    /// <summary>
    /// For the criteria after 13: the placement started the death sequence within the 10 frames
    /// <see cref="PlaceOnLedgeAndWaitForEntry"/> waits. How fast is criterion 13's check alone, so a slow entry fails
    /// there and not in every criterion that only needs a death.
    /// </summary>
    internal static void AssertEntered(EntryResult result) =>
        AssertInt(result.Frames).OverrideFailureMessage("placing the player on the ledge didn't start the death sequence").IsGreater(0);

    private static async Task<Vector3> RespawnPositionOfARun(int seed)
    {
        using var runner = ISceneRunner.Load(NewWorld(seed, ShortTime, ShortTime), true);
        var world = await StartWorld(runner);
        var entry = FindLedgeEntry(world);
        Vector3? respawn = null;
        world.PlayerRespawned += position => respawn = position;
        var result = await PlaceOnLedgeAndWaitForEntry(world, entry);
        AssertEntered(result);
        AssertBool(await WaitUntil(world, () => respawn is not null,
            Mathf.CeilToInt((world.FallTime + world.ScreenTime) * Engine.PhysicsTicksPerSecond) + 20)).OverrideFailureMessage("no respawn").IsTrue();
        return respawn!.Value;
    }

    /// <summary>
    /// Criterion 21: a map reload while the sequence is in <paramref name="phase"/>: the death camera stops and isn't
    /// current, the death screen is hidden, the sequence is <see cref="DeathState.Alive"/>, new barriers are built with
    /// the same counts, a new player spawns (and no respawn follows), and no error is pushed.
    /// </summary>
    private static async Task AssertReloadDuring(DeathState phase)
    {
        AssertPackPresent();
        var capture = new ErrorCapture();
        OS.AddLogger(capture);
        try
        {
            using var runner = ISceneRunner.Load(NewWorld(TestSeed, 1f, 1f), true);
            var world = await StartWorld(runner);
            var loader = world.MapLoader;
            var oldBarriers = loader.Barriers!;
            var items = oldBarriers.ItemCount;
            var pieces = oldBarriers.PieceCount;
            var oldPlayer = world.Player!;
            var entry = FindLedgeEntry(world);
            var spawned = 0;
            var respawned = 0;
            world.PlayerSpawned += _ => spawned++;
            world.PlayerRespawned += _ => respawned++;

            var result = await PlaceOnLedgeAndWaitForEntry(world, entry);
            AssertEntered(result);
            AssertBool(await WaitUntil(world, () => world.Death.State == phase, Engine.PhysicsTicksPerSecond * 2)).IsTrue();
            await PhysicsFrames(world, 5);
            AssertThat(world.Death.State).IsEqual(phase);
            if (phase == DeathState.DeathScreen)
            {
                AssertBool(world.DeathScreen.IsShown).IsTrue();
            }

            loader.Load();
            AssertThat(world.Death.State).IsEqual(DeathState.Alive);
            AssertBool(world.DeathCamera.Current).IsFalse();
            AssertBool(world.DeathCamera.IsActive).IsFalse();
            AssertBool(world.DeathScreen.IsShown).IsFalse();

            AssertBool(await WaitUntil(world, () => spawned > 0, world.SpawnPhysicsWaitFrames + 30)).OverrideFailureMessage("no spawn after the reload").IsTrue();
            AssertBool(await loader.WaitForWorldEdgeAsync(EdgeWaitFrames)).OverrideFailureMessage("the barriers weren't rebuilt").IsTrue();
            await PhysicsFrames(world, Mathf.CeilToInt((world.FallTime + world.ScreenTime) * Engine.PhysicsTicksPerSecond) + 10);

            GD.Print(Invariant($"WorldEdgeTest: Ac21 reload in {phase}: state {world.Death.State}, spawns {spawned}, respawns {respawned}, barriers {loader.Barriers?.ItemCount}"));
            AssertInt(spawned).IsEqual(1);
            AssertInt(respawned).IsEqual(0);
            AssertThat(world.Death.State).IsEqual(DeathState.Alive);
            AssertThat(world.Player).IsNotNull();
            AssertThat(world.Player).IsNotSame(oldPlayer);
            AssertBool(GodotObject.IsInstanceValid(oldPlayer) && oldPlayer.IsInsideTree()).IsFalse();
            AssertBool(world.Player!.InputEnabled).IsTrue();
            AssertInt((int)world.Player.CollisionMask).IsEqual((int)CollisionLayers.PlayerMask);
            AssertThat(world.Player.GravityOverride).IsNull();
            AssertBool(world.PlayerCamera!.IsCurrent).IsTrue();
            AssertBool(world.DeathCamera.Current).IsFalse();
            AssertBool(world.DeathScreen.IsShown).IsFalse();
            AssertThat(loader.Barriers).IsNotSame(oldBarriers);
            AssertBool(GodotObject.IsInstanceValid(oldBarriers) && oldBarriers.IsInsideTree()).IsFalse();
            AssertInt(loader.Barriers!.ItemCount).IsEqual(items);
            AssertInt(loader.Barriers.PieceCount).IsEqual(pieces);
        }
        finally
        {
            OS.RemoveLogger(capture);
        }

        AssertThat(string.Join(" | ", capture.ErrorsOnly)).IsEqual("");
    }

    // ===== Helpers: criteria 22 and 23 =====

    /// <summary>The safety net's height and an x/z inside the ring, outside every void prism (the player's spawn).</summary>
    internal static (float Net, float X, float Z) SafetyNetPoint(World world)
    {
        var boundary = world.MapLoader.Boundary!;
        var geometry = world.MapLoader.EdgeGeometry!;
        var net = (float)(boundary.Cliff.BottomY - world.SafetyNetMargin);
        var spawn = world.Player!.GlobalPosition;
        AssertBool(geometry.IsInsideRing(spawn.X, spawn.Z)).IsTrue();
        AssertBool(geometry.IsInVoid(new LocalPoint(spawn.X, net, spawn.Z))).IsFalse();
        AssertBool(geometry.IsBelowSafetyNet(net - NetOffset, world.SafetyNetMargin)).IsTrue();
        AssertBool(geometry.IsBelowSafetyNet(net + NetOffset, world.SafetyNetMargin)).IsFalse();
        return (net, spawn.X, spawn.Z);
    }

    /// <summary>A test body on the barrier layer, colliding with the map, with a box shape and no damping.</summary>
    private static T NewTestBody<T>(T body)
        where T : RigidBody3D
    {
        body.CollisionLayer = CollisionLayers.Barrier;
        body.CollisionMask = CollisionLayers.Map;
        body.LinearDampMode = RigidBody3D.DampMode.Replace;
        body.LinearDamp = 0f;
        body.AngularDampMode = RigidBody3D.DampMode.Replace;
        body.AngularDamp = 0f;
        body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = Vector3.One * TestBodySize } });
        return body;
    }

    internal static bool IsFreed(Node node) => !GodotObject.IsInstanceValid(node) || node.IsQueuedForDeletion();

    private sealed record LaunchTrace(
        List<(Vector3 Position, Vector3 Velocity, bool Falling, double Distance)> Samples,
        int FallingFrom,
        int FreedAt,
        float GravityScaleWhileFalling,
        int PullSteps,
        float WorstPullError,
        List<string> PullErrors);

    /// <summary>
    /// Criterion 23: a tracked test rigid body put on the ledge 0.5 m outward of a land fence's midpoint and launched
    /// outward and up; samples it every physics frame until it is freed (or 15 s). Pull steps are the frame pairs where
    /// it was falling at both ends and more than the ledge's width plus 0.5 m outside the ring, so nothing can touch it.
    /// </summary>
    private static async Task<LaunchTrace> LaunchOverTheEdge(World world, float? maxFallTime)
    {
        if (maxFallTime is { } time)
        {
            world.MaxFallTime = time;
        }

        var geometry = world.MapLoader.EdgeGeometry!;
        var ledgeWidth = (float)world.MapLoader.Boundary!.Cliff.LedgeM;
        var entry = FindLedgeEntry(world);
        var start = entry.Item.At(0.5f) + (entry.Item.Across * 0.5f);
        start.Y = entry.Feet.Y + (TestBodySize / 2f) + 0.3f;

        // Added at the start, not the origin: this runs inside a physics frame's signal, before the safety net's check.
        var body = NewTestBody(new RigidBody3D { Name = "TestLoosePiece", Position = start });
        world.AddChild(body);
        world.TrackVoidBody(body);

        var samples = new List<(Vector3 Position, Vector3 Velocity, bool Falling, double Distance)>();
        var fallingFrom = -1;
        var freedAt = -1;
        var gravityScale = float.NaN;
        await Drive(world, frame =>
        {
            if (frame == 0)
            {
                body.GlobalPosition = start;
                body.LinearVelocity = (entry.Item.Across * LaunchOutward) + (Vector3.Up * LaunchUp);
                return false;
            }

            if (IsFreed(body))
            {
                freedAt = samples.Count;
                return true;
            }

            var p = body.GlobalPosition;
            var falling = world.VoidFall.IsFalling(body);
            if (falling && fallingFrom < 0)
            {
                fallingFrom = samples.Count;
                gravityScale = body.GravityScale;
            }

            var distance = geometry.NearestRingPoint(p.X, p.Z).Distance * (geometry.IsInsideRing(p.X, p.Z) ? -1 : 1);
            samples.Add((p, body.LinearVelocity, falling, distance));
            return false;
        }, 15 * Engine.PhysicsTicksPerSecond);

        var dt = 1f / Engine.PhysicsTicksPerSecond;
        var errors = new List<string>();
        var worst = 0f;
        var steps = 0;
        for (var i = 1; i < samples.Count; i++)
        {
            var (_, v0, f0, d0) = samples[i - 1];
            var (_, v1, f1, _) = samples[i];
            if (!f0 || !f1 || d0 < ledgeWidth + 0.5)
            {
                continue;
            }

            steps++;
            var change = v1 - v0;
            var horizontal = Horizontal(change);
            var expectedHorizontal = world.VoidOutwardAcceleration * dt;
            var expectedVertical = -world.VoidDownAcceleration * dt;
            var expected = new Vector2(expectedHorizontal, expectedVertical).Length();
            var error = new Vector2(horizontal.Length() - expectedHorizontal, change.Y - expectedVertical).Length() / expected;
            var direction = horizontal.Length() > 0f ? horizontal.Normalized().Dot(entry.Item.Across) : 0f;
            worst = Mathf.Max(worst, error);
            if (error > PullTolerance || direction < 0.99f)
            {
                errors.Add(Invariant($"sample {i}: dv {change} vs {expectedHorizontal:0.#####} outward and {expectedVertical:0.#####} down ({error * 100:0.##} %, direction {direction:0.####})"));
            }
        }

        return new LaunchTrace(samples, fallingFrom, freedAt, gravityScale, steps, worst, errors);
    }

    // ===== Helpers: world and frames =====

    internal static World NewWorld(int? seed, float? fallTime = null, float? screenTime = null)
    {
        var world = GD.Load<PackedScene>(WorldScenePath).Instantiate<World>();
        world.Seed = seed;
        if (fallTime is { } fall)
        {
            world.FallTime = fall;
        }

        if (screenTime is { } screen)
        {
            world.ScreenTime = screen;
        }

        return world;
    }

    /// <summary>The runner's world once its player has spawned and its barriers are built.</summary>
    internal static async Task<World> StartWorld(ISceneRunner runner)
    {
        var world = (World)runner.Scene()!;
        AssertBool(await WaitForSpawn(world)).OverrideFailureMessage("the player didn't spawn").IsTrue();
        AssertBool(await world.MapLoader.WaitForWorldEdgeAsync(EdgeWaitFrames)).OverrideFailureMessage("the barriers weren't built").IsTrue();
        AssertThat(world.MapLoader.EdgeGeometry).IsNotNull();
        AssertThat(world.Death.State).IsEqual(DeathState.Alive);
        return world;
    }

    internal static Task<bool> WaitForSpawn(World world) =>
        WaitUntil(world, () => world.Player is not null, world.SpawnPhysicsWaitFrames + 30);

    internal static PlayerState PlayerStateOfGame()
    {
        AssertThat(GameState.Instance).OverrideFailureMessage("no GameState autoload").IsNotNull();
        return GameState.Instance.Player;
    }

    /// <summary>Physics steps of 1/<paramref name="ticks"/> s until <paramref name="seconds"/> is reached (as <see cref="DeathSequence"/> counts).</summary>
    internal static int FramesFor(double seconds, int ticks)
    {
        var frames = 0;
        var time = 0.0;
        var dt = 1.0 / ticks;
        while (time < seconds - DeathSequence.BoundaryTolerance)
        {
            time += dt;
            frames++;
        }

        return frames;
    }

    internal static async Task PhysicsFrames(Node node, int count)
    {
        var tree = node.GetTree();
        for (var i = 0; i < count; i++)
        {
            await node.ToSignal(tree, SceneTree.SignalName.PhysicsFrame);
        }
    }

    internal static async Task<bool> WaitUntil(Node node, Func<bool> condition, int maxPhysicsFrames)
    {
        for (var i = 0; i < maxPhysicsFrames && !condition(); i++)
        {
            await PhysicsFrames(node, 1);
        }

        return condition();
    }

    /// <summary>
    /// Calls <paramref name="step"/> at the start of each physics frame (before every node's <c>_PhysicsProcess</c>)
    /// with the frame index, until it returns true or after <paramref name="maxFrames"/> frames. Don't assert inside
    /// it: an exception thrown in a signal handler doesn't reach the test. An exception thrown by
    /// <paramref name="step"/> ends the drive and is rethrown here, so a broken step can't keep the frames going.
    /// </summary>
    internal static async Task Drive(Node node, Func<int, bool> step, int maxFrames)
    {
        var tree = node.GetTree();
        var frame = 0;
        var done = false;
        Exception? failure = null;
        void OnFrame()
        {
            if (done)
            {
                return;
            }

            try
            {
                done = step(frame) || ++frame >= maxFrames;
            }
            catch (Exception ex)
            {
                failure = ex;
                done = true;
            }
        }

        tree.PhysicsFrame += OnFrame;
        try
        {
            while (!done)
            {
                await node.ToSignal(tree, SceneTree.SignalName.PhysicsFrame);
            }
        }
        finally
        {
            tree.PhysicsFrame -= OnFrame;
        }

        if (failure is not null)
        {
            throw new InvalidOperationException($"a scripted physics frame threw: {failure.Message}", failure);
        }
    }

    /// <summary>
    /// Puts the player's feet on <paramref name="feet"/> (raised a little), the camera behind it at
    /// <paramref name="yaw"/>, and waits until it stands still on the floor.
    /// </summary>
    private static async Task PlaceAndSettle(World world, Player player, Vector3 feet, float yaw)
    {
        ReleaseAll();
        player.PlaceAt(feet + (Vector3.Up * PlaceLift), yaw);
        world.PlayerCamera!.PlaceBehind(yaw);
        var still = 0;
        await Drive(world, _ =>
        {
            still = player.IsOnFloor() && player.Velocity.Length() < 1e-3f ? still + 1 : 0;
            return still >= 5;
        }, RespawnSettleFrames);
        AssertBool(player.IsOnFloor()).OverrideFailureMessage(Invariant($"the player didn't settle at {feet}")).IsTrue();
    }

    private static void ReleaseAll()
    {
        foreach (var key in InjectedKeys)
        {
            KeyEvent(key, pressed: false);
        }

        foreach (var action in PlayerActions)
        {
            Input.ActionRelease(action);
        }
    }

    private static void KeyEvent(Key physical, bool pressed)
    {
        Input.ParseInputEvent(new InputEventKey { PhysicalKeycode = physical, Pressed = pressed });
        Input.FlushBufferedEvents();
    }

    private static void TapKey(Key physical)
    {
        KeyEvent(physical, pressed: true);
        KeyEvent(physical, pressed: false);
    }

    /// <summary>A thin vertical post (a box on the map layer) centred at x/z of <paramref name="at"/>, from <paramref name="bottom"/> to <paramref name="top"/>.</summary>
    private static StaticBody3D AddPost(Node parent, Vector3 at, float halfWidth, float bottom, float top)
    {
        var post = new StaticBody3D { Name = "TestPost", CollisionLayer = CollisionLayers.Map };
        post.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(halfWidth * 2f, top - bottom, halfWidth * 2f) } });
        post.Position = new Vector3(at.X, (bottom + top) / 2f, at.Z);
        parent.AddChild(post);
        return post;
    }

    /// <summary>An item type or feature the criterion needs is missing: allowed (N/A) only on the test-area pack.</summary>
    internal static void AssertNotFullPack(World world, string criterion, string what)
    {
        var area = world.MapLoader.Manifest!.Area;
        GD.Print($"WorldEdgeTest: {criterion} N/A: the '{area}' pack {what} (allowed by the spec on the test-area pack only)");
        AssertThat(area).OverrideFailureMessage($"the full pack {what}").IsEqual("test");
    }

    // ===== Helpers: geometry =====

    /// <summary>Criterion 9 and 10's run-up: the distance to reach <paramref name="speed"/> at <paramref name="acceleration"/>.</summary>
    private static float RunUpDistance(float speed, float acceleration) => speed * speed / (2f * acceleration);

    internal static float Slope(Vector3 a, Vector3 b) =>
        Mathf.RadToDeg(Mathf.Atan2(Mathf.Abs(a.Y - b.Y), Mathf.Max(Horizontal(a - b).Length(), 1e-6f)));

    /// <summary>Largest difference of the two bases' columns.</summary>
    private static float BasisDifference(Basis a, Basis b) =>
        Mathf.Max((a.Column0 - b.Column0).Length(), Mathf.Max((a.Column1 - b.Column1).Length(), (a.Column2 - b.Column2).Length()));

    internal static float YawOf(Vector3 direction) => Mathf.Atan2(-direction.X, -direction.Z);

    internal static Vector3 Horizontal(Vector3 v) => new(v.X, 0f, v.Z);

    internal static Vector3 ToVector(LocalPoint p) => new((float)p.X, (float)p.Y, (float)p.Z);

    internal static Vector3 ToVector(PlanarPoint p) => new((float)p.X, 0f, (float)p.Z);

    internal static IEnumerable<Node> Descendants(Node root)
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

    internal static string Invariant(FormattableString text) => FormattableString.Invariant(text);

    internal static void AssertPackPresent()
    {
        foreach (var file in new[] { MapPack.ManifestFile, MapPack.RoadGraphFile, MapPack.BuildingsFile, MapPack.PoisFile, MapPack.BoundaryFile })
        {
            var path = $"{TestPacks.PackPath}/{file}";
            AssertBool(FileAccess.FileExists(path)).OverrideFailureMessage($"no {file} at {path}; {TestPacks.MissingPackHint(MapId)}").IsTrue();
        }
    }
}
