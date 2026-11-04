using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using GdUnit4;
using Godot;
using Roadman.Core.Maps;
using Roadman.Core.Player;
using Roadman.Core.Tools;
using Roadman.Debug;
using Roadman.OnFoot;
using Roadman.Scenes;
using static GdUnit4.Assertions;
using Directory = System.IO.Directory;
using File = System.IO.File;
using Path = System.IO.Path;

namespace Roadman.Tests;

/// <summary>
/// The player on foot in Godot, <c>docs/specs/player-on-foot.md</c> (<c>Ac</c> numbers are its acceptance criteria):
/// spawn, spawn failure, movement, jump, collision, the third-person camera with the mouse and the gamepad, the debug
/// camera toggle and the release layout, and the street label (criterion 20, the temporary fall respawn, is replaced by
/// docs/specs/world-edge-game.md). They run on whichever Somero pack is built (full, or the CI test area from the
/// committed fixture), so every position is derived from the loaded pack: the spawn area's open ground from
/// <see cref="MapLoader.Boundary"/>, roads, bridges and <c>span_end</c> nodes from <see cref="MapLoader.RoadGraph"/>,
/// walls from the building collision bodies, heights from ray hits. Motion is scripted per physics frame through
/// <see cref="SceneTree.PhysicsFrame"/>, which Godot emits before any node's <c>_PhysicsProcess</c>, so the state read
/// there is the previous frame's result. An action pressed there (with <see cref="Input.ActionPress(StringName, float)"/>
/// or an injected event) is held at once, so the player reads held input in that same frame, but Godot 4.7 reports it
/// as "just pressed" only from the next physics frame (measured: in the handler of the press frame
/// <c>IsActionJustPressed</c> is false, in the next one true), and that next frame's just-press survives a release
/// in its handler. So a jump or run-toggle press acts one frame after the handler that pressed it. Like <see cref="MapLoaderTest"/>, these fail rather than skip without the pack.
/// </summary>
[TestSuite]
[RequireGodotRuntime]
public class PlayerOnFootTest
{
    private const string WorldScenePath = "res://scenes/world/World.tscn";
    private const string MapId = "somero";

    /// <summary>A fixed seed, so the spawn and every derived placement are the same on each run.</summary>
    private const int TestSeed = 20261102;

    /// <summary>Criterion 6: physics frames after the spawn before the player must be on the floor.</summary>
    private const int SpawnSettleFrames = 120;

    /// <summary>Criteria 6 and 10: the spec's distance and speed tolerances.</summary>
    private const float SpecDistanceTolerance = 0.1f;

    private const float SpecSpeedTolerance = 0.05f;

    /// <summary>Criterion 11: the apex tolerance (± 5 % of <see cref="Player.JumpHeight"/>).</summary>
    private const float JumpTolerance = 0.05f;

    /// <summary>Criterion 12: the 0.9 m barrier plus the 0.25 m fence terrain deviation, and the box depth.</summary>
    private const float BoxHeight = 1.15f;

    private const float BoxDepth = 0.2f;

    /// <summary>Criterion 13: the route length along the road.</summary>
    private const float RoadRouteLength = 20f;

    /// <summary>Criterion 14: how long the player runs into a wall or railing.</summary>
    private const float WallRunSeconds = 3f;

    /// <summary>Criterion 15: the spec's road-side ramp length ("Context"); the route goes twice that far onto the road.</summary>
    private const float RampLength = 5f;

    /// <summary>Criterion 16: the player's distance from the wall, and the camera yaw step.</summary>
    private const float WallDistance = 1f;

    private const float YawStepDegrees = 15f;

    /// <summary>
    /// Criteria 10 and 17: physics frames allowed after the ideal stop before a stop counts as failed. The ideal stop
    /// takes ⌈speed / (<see cref="Player.Deceleration"/> · dt)⌉ frames, assuming every frame is on the floor; a frame
    /// off the floor decelerates at only <see cref="Player.AirControl"/> of that, so a few such frames (a seam between
    /// collision faces) make the stop a little longer. Half a second (30 frames at the default 60 ticks per second) is
    /// far more than that. The loops end at the first stopped frame, so the margin only bounds a failing run and never
    /// changes a measured distance or speed.
    /// </summary>
    private static int StopMarginFrames => Engine.PhysicsTicksPerSecond / 2;

    /// <summary>
    /// Criteria 10 and 17: horizontal speed below which the player counts as stopped, m/s. <see cref="OnFootMotion"/>
    /// takes the horizontal velocity to exactly 0 (a move-toward step), but <c>MoveAndSlide</c> on a floor whose normal
    /// is slightly off vertical can leave a float rounding residue in x and z. At 1e-4 m/s the player would move
    /// 1e-4 m/s × 0.5 s (<see cref="StopMarginFrames"/>) = 0.05 mm, more than three orders of magnitude below the spec's
    /// 0.1 m stop tolerance (<see cref="SpecDistanceTolerance"/>), so counting it as stopped can't hide a failure.
    /// </summary>
    private const float StoppedSpeed = 1e-4f;

    /// <summary>Spacing of the samples along test routes and lines, m (a search resolution, not a tolerance).</summary>
    private const float SampleStep = 0.5f;

    /// <summary>The ray start height of the downward casts, as <see cref="World.DrawSpawn"/>.</summary>
    private const float RayTop = 10_000f;

    /// <summary>The joypad device the injected gamepad events come from; the actions listen to every device (−1).</summary>
    private const int PadDevice = 0;

    /// <summary>The debug camera's forward action (<see cref="DebugCamera"/> reads it by name).</summary>
    private const string DebugForwardAction = "debug_forward";

    private static readonly string[] PlayerActions =
    [
        Player.MoveForwardAction, Player.MoveBackAction, Player.MoveLeftAction, Player.MoveRightAction,
        Player.RunAction, Player.RunToggleAction, Player.JumpAction,
        PlayerCamera.CameraLeftAction, PlayerCamera.CameraRightAction, PlayerCamera.CameraUpAction, PlayerCamera.CameraDownAction,
        World.DebugCameraAction, DebugForwardAction,
    ];

    /// <summary>The stick axes and buttons the tests inject; <see cref="ReleaseAll"/> returns every one to rest.</summary>
    private static readonly JoyAxis[] StickAxes = [JoyAxis.LeftX, JoyAxis.LeftY, JoyAxis.RightX, JoyAxis.RightY];

    private static readonly JoyButton[] PadButtons = [GamepadButtons.South, GamepadButtons.LeftStickClick];

    /// <summary>The physical keys the tests inject as key events (the two <c>debug_camera</c> keys and W).</summary>
    private static readonly Key[] InjectedKeys = [Key.Quoteleft, Key.Section, Key.W];

    private string _tempPack = "";
    private Variant? _savedMapIdSetting;

    [AfterTest]
    public void CleanUp()
    {
        ReleaseAll();
        DebugBuild.Override = null;
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

    // ----- Criterion 6: spawn -----

    [TestCase]
    public async Task Ac6_World_SpawnsThePlayerOnOpenGroundAndItSettlesOnTheFloorWithoutErrors()
    {
        AssertPackPresent();
        var capture = new ErrorCapture();
        OS.AddLogger(capture);
        World world;
        Vector3? spawned = null;
        var spawnSignals = 0;
        try
        {
            using var runner = ISceneRunner.Load(NewWorld(TestSeed), true);
            world = (World)runner.Scene()!;
            world.PlayerSpawned += position =>
            {
                spawned = position;
                spawnSignals++;
            };
            AssertBool(await WaitForSpawn(world)).OverrideFailureMessage("the player didn't spawn").IsTrue();
            var player = world.Player!;
            var boundary = world.MapLoader.Boundary!;

            AssertInt(spawnSignals).IsEqual(1);
            AssertThat(spawned).IsEqual(player.GlobalPosition);
            AssertInt(world.LastSpawnAttempts).IsBetween(1, world.SpawnMaxAttempts);
            AssertBool(SpawnPicker.Contains(boundary.SpawnArea.OpenGround, spawned!.Value.X, spawned.Value.Z))
                .OverrideFailureMessage($"spawn {spawned} is outside spawn_area.open_ground")
                .IsTrue();
            AssertThat(world.GetNodeOrNull<Player>("Player")).IsSame(player);

            await PhysicsFrames(world, SpawnSettleFrames);
            var feet = player.GlobalPosition;
            var terrain = world.MapLoader.TerrainHeightAt(feet.X, feet.Z);
            AssertBool(terrain is not null).OverrideFailureMessage($"no terrain under the player at {feet}").IsTrue();
            GD.Print(Invariant(
                $"PlayerOnFootTest: Ac6 spawn {spawned} after {world.LastSpawnAttempts} draws; after {SpawnSettleFrames} frames feet {feet}, terrain {terrain}, gap {feet.Y - terrain!.Value:0.####} m"));
            AssertBool(player.IsOnFloor()).OverrideFailureMessage($"not on the floor after {SpawnSettleFrames} physics frames").IsTrue();
            AssertFloat(Mathf.Abs(feet.Y - terrain.Value)).IsLessEqual(SpecDistanceTolerance);
            AssertBool(world.PlayerCamera!.IsCurrent).OverrideFailureMessage("the player camera isn't current").IsTrue();
            AssertThat(world.GetViewport().GetCamera3D()).IsSame(world.PlayerCamera.Camera);
            AssertBool(world.Camera!.Current).IsFalse();
        }
        finally
        {
            OS.RemoveLogger(capture);
        }

        AssertThat(string.Join(" | ", capture.ErrorsOnly)).IsEqual("");
    }

    // ----- Criterion 7: seeded spawn -----

    [TestCase]
    public async Task Ac7_SameSeed_SpawnsAtTheSamePositionInTwoRuns()
    {
        AssertPackPresent();
        var first = await SpawnPositionOfARun(TestSeed);
        var second = await SpawnPositionOfARun(TestSeed);
        GD.Print(Invariant($"PlayerOnFootTest: Ac7 seed {TestSeed}: {first} and {second}"));
        AssertThat(second).IsEqual(first);
    }

    /// <summary>
    /// Ten seeds through <see cref="World.ResetSpawnRandom"/> and <see cref="World.SpawnPlayer"/> on one loaded world
    /// (the same procedure as the first spawn): the ten spawn points are not all equal, and every one is on open
    /// ground. Seeding the first of them again repeats its point.
    /// </summary>
    [TestCase]
    public async Task Ac7_TenDifferentSeeds_GiveSpawnPointsThatAreNotAllEqual()
    {
        AssertPackPresent();
        using var runner = ISceneRunner.Load(NewWorld(TestSeed), true);
        var world = (World)runner.Scene()!;
        AssertBool(await WaitForSpawn(world)).IsTrue();
        var openGround = world.MapLoader.Boundary!.SpawnArea.OpenGround;
        var spawned = new List<Vector3>();
        world.PlayerSpawned += spawned.Add;

        var seeds = Enumerable.Range(1, 10).ToList();
        foreach (var seed in seeds)
        {
            world.ResetSpawnRandom(seed);
            AssertBool(world.SpawnPlayer()).OverrideFailureMessage($"seed {seed}: no spawn").IsTrue();
            await PhysicsFrames(world, 1);
        }

        world.ResetSpawnRandom(seeds[0]);
        var replacedPlayer = world.Player!;
        var replacedCamera = world.PlayerCamera!;
        AssertBool(world.SpawnPlayer()).IsTrue();

        // SpawnPlayer detaches the replaced player and camera and queues them for freeing; they go at the end of this
        // frame. Without this wait the test ends first and gdUnit counts their 11 nodes as orphans.
        await runner.AwaitIdleFrame();
        AssertBool(GodotObject.IsInstanceValid(replacedPlayer)).OverrideFailureMessage("the replaced player wasn't freed").IsFalse();
        AssertBool(GodotObject.IsInstanceValid(replacedCamera)).OverrideFailureMessage("the replaced player camera wasn't freed").IsFalse();

        GD.Print($"PlayerOnFootTest: Ac7 ten seeds: {string.Join(", ", spawned.Take(seeds.Count).Select(p => Invariant($"{p}")))}");
        AssertInt(spawned.Count).IsEqual(seeds.Count + 1);
        AssertInt(spawned.Take(seeds.Count).Distinct().Count()).IsGreater(1);
        AssertThat(spawned[^1]).IsEqual(spawned[0]);
        foreach (var point in spawned)
        {
            AssertBool(SpawnPicker.Contains(openGround, point.X, point.Z)).OverrideFailureMessage($"{point} is outside open ground").IsTrue();
        }
    }

    // ----- Criterion 8: a blocked draw is redrawn -----

    /// <summary>
    /// A first run with the test seed gives the seeded first passing point. A second run with the same seed has a
    /// thin post next to that point, inside the capsule's radius but clear of the downward ray at the point, so only
    /// the capsule query (spawn step 2) can reject the draw. The player spawns elsewhere on open ground after more
    /// draws, and settles on the floor.
    /// </summary>
    [TestCase]
    public async Task Ac8_DrawWhoseCapsuleOverlapsABody_IsRedrawnAndThePlayerSpawnsElsewhere()
    {
        AssertPackPresent();
        int firstAttempts;
        Vector3 first;
        float radius;
        {
            using var runner = ISceneRunner.Load(NewWorld(TestSeed), true);
            var world = (World)runner.Scene()!;
            AssertBool(await WaitForSpawn(world)).IsTrue();
            first = world.Player!.GlobalPosition;
            firstAttempts = world.LastSpawnAttempts;
            radius = world.Player.CapsuleRadius;
        }

        using var blockedRunner = ISceneRunner.Load(NewWorld(TestSeed), true);
        var blocked = (World)blockedRunner.Scene()!;
        // Half the radius off the point: the capsule overlaps the post, the vertical ray at the point misses it.
        var post = AddPost(blocked, new Vector3(first.X + (radius / 2f), first.Y, first.Z), radius / 4f, first.Y - 5f, first.Y + 5f);
        AssertBool(await WaitForSpawn(blocked)).IsTrue();
        var player = blocked.Player!;
        var spawn = player.GlobalPosition;
        GD.Print(Invariant(
            $"PlayerOnFootTest: Ac8 first point {first} (draw {firstAttempts}); with the post: {spawn} (draw {blocked.LastSpawnAttempts})"));

        AssertBool(IsInsideTree(post)).IsTrue();
        AssertInt(blocked.LastSpawnAttempts).IsGreater(firstAttempts);
        AssertFloat(HorizontalDistance(spawn, first)).IsGreater(radius);
        AssertBool(SpawnPicker.Contains(blocked.MapLoader.Boundary!.SpawnArea.OpenGround, spawn.X, spawn.Z)).IsTrue();
        await PhysicsFrames(blocked, SpawnSettleFrames);
        AssertBool(player.IsOnFloor()).IsTrue();
    }

    // ----- Criterion 9: spawn failure -----

    [TestCase]
    public async Task Ac9_PackWithoutBoundaryJson_ReportsItAndSpawnsNoPlayer()
    {
        AssertPackPresent();
        var mapId = $"tmp-no-boundary-{Guid.NewGuid():N}"[..30];
        var tiles = CopyPack(mapId, boundary: null);
        await AssertSpawnFailure(mapId, tiles, expectBoundary: false, "is missing or invalid");
    }

    [TestCase]
    public async Task Ac9_PackWithInvalidBoundaryJson_ReportsItAndSpawnsNoPlayer()
    {
        AssertPackPresent();
        var mapId = $"tmp-bad-boundary-{Guid.NewGuid():N}"[..30];
        var tiles = CopyPack(mapId, boundary => boundary["schema_version"] = 99);
        await AssertSpawnFailure(mapId, tiles, expectBoundary: false, "is missing or invalid");
    }

    /// <summary>
    /// The real pack with <see cref="World.SpawnMaxAttempts"/> lowered, and a post next to each of the seeded draws
    /// (predicted with <see cref="SpawnPicker"/> and the same seed): every draw fails the capsule check, so the spawn
    /// fails like a pack without a boundary.
    /// </summary>
    [TestCase]
    public async Task Ac9_EveryDrawBlocked_ReportsItAndSpawnsNoPlayer()
    {
        AssertPackPresent();
        const int attempts = 5;
        var boundary = ReadPackBoundary();
        var template = new Player();
        var radius = template.CapsuleRadius;
        var height = template.CapsuleHeight;
        template.Free();
        var world = NewWorld(TestSeed);
        world.SpawnMaxAttempts = attempts;

        // Every capsule a draw can test lies in [bottom, top]. Bottom: the slab's bottom, below every terrain hit.
        // Top: the spawn ray starts at RayTop (as World.DrawSpawn), so its hit is at most there, and the capsule it
        // tests spans SpawnDropHeight + CapsuleHeight above the hit. The post is beside the draw point (half a radius
        // off, a quarter radius wide), so however tall it is, the vertical ray at the point misses it.
        var bottom = (float)boundary.Cliff.BottomY;
        var top = RayTop + world.SpawnDropHeight + height;
        var picker = new SpawnPicker(boundary.SpawnArea, new Random(TestSeed));
        var draws = Enumerable.Range(0, attempts).Select(_ => picker.Next()).ToList();
        foreach (var draw in draws)
        {
            AddPost(world, new Vector3((float)draw.X + (radius / 2f), 0f, (float)draw.Z), radius / 4f, bottom, top);
        }

        var capture = new ErrorCapture();
        OS.AddLogger(capture);
        try
        {
            using var runner = ISceneRunner.Load(world, true);
            await WaitUntil(world, () => capture.ErrorsOnly.Any(e => e.Contains("no player spawn")), world.SpawnPhysicsWaitFrames + 30);
            await PhysicsFrames(world, 5);

            AssertThat(world.MapLoader.Boundary).IsNotNull();
            AssertInt(world.LastSpawnAttempts).IsEqual(attempts);
            AssertThat(world.LastSpawnFailure).IsNotNull();
            AssertThat(world.LastSpawnFailure!).Contains($"none of {attempts} draws").Contains(world.MapLoader.BoundaryPath);
            AssertNoPlayer(world);
            await AssertDebugCameraKeyChangesNothing(world);
        }
        finally
        {
            OS.RemoveLogger(capture);
        }

        var path = $"res://maps/{MapId}/{MapPack.BoundaryFile}";
        var errors = capture.ErrorsOnly.Where(e => e.Contains("no player spawn")).ToList();
        AssertInt(errors.Count).OverrideFailureMessage($"no single spawn error; got: {string.Join(" | ", capture.ErrorsOnly)}").IsEqual(1);
        AssertThat(errors[0]).Contains(path).Contains($"/regen-map {MapId}").Contains($"{attempts} draws");
    }

    /// <summary>
    /// The terrain collision isn't queryable before the wait runs out: the spawn fails without a single draw, says so
    /// in <see cref="World.LastSpawnFailure"/> and the error, and leaves no player. The world first spawns normally
    /// (so <see cref="World.LastSpawnAttempts"/> is at least 1 and the failure is null), then reloads the map with
    /// <see cref="World.SpawnPhysicsWaitFrames"/> 0: the wait gives up before any physics frame, the same branch a
    /// pack whose tiles never get terrain collision takes after its frames, without needing such a pack.
    /// </summary>
    [TestCase]
    public async Task Ac9_TerrainCollisionNotQueryableInTheWait_ReportsNoTerrainCollisionAndMakesNoDraw()
    {
        AssertPackPresent();
        using var runner = ISceneRunner.Load(NewWorld(TestSeed), true);
        var world = (World)runner.Scene()!;
        AssertBool(await WaitForSpawn(world)).IsTrue();
        AssertInt(world.LastSpawnAttempts).IsGreaterEqual(1);
        AssertThat(world.LastSpawnFailure).IsNull();

        world.SpawnPhysicsWaitFrames = 0;
        var capture = new ErrorCapture();
        OS.AddLogger(capture);
        try
        {
            world.MapLoader.Load();
            await WaitUntil(world, () => capture.ErrorsOnly.Any(e => e.Contains("no player spawn")), 30);
            await runner.AwaitIdleFrame();

            AssertThat(world.MapLoader.Boundary).IsNotNull();
            AssertInt(world.LastSpawnAttempts).IsEqual(0);
            AssertThat(world.LastSpawnFailure).IsNotNull();
            AssertThat(world.LastSpawnFailure!).Contains("no terrain collision").Contains(world.MapLoader.BoundaryPath);
            AssertNoPlayer(world);
            await AssertDebugCameraKeyChangesNothing(world);
        }
        finally
        {
            OS.RemoveLogger(capture);
        }

        var errors = capture.ErrorsOnly.Where(e => e.Contains("no player spawn")).ToList();
        AssertInt(errors.Count).OverrideFailureMessage($"no single spawn error; got: {string.Join(" | ", capture.ErrorsOnly)}").IsEqual(1);
        AssertThat(errors[0]).Contains("no terrain collision").Contains($"res://maps/{MapId}/{MapPack.BoundaryFile}").Contains($"/regen-map {MapId}");
    }

    /// <summary>
    /// A valid <c>boundary.json</c> whose only open ground is a sliver: a triangle from (0, 0) to (1000, 1000) and
    /// (1000, 1000 + 10⁻⁶), area 5 × 10⁻⁴ m² (above 0, so the reader accepts it) in a bounding box of about
    /// 10⁶ m². A box draw lands in it with probability 5 × 10⁻¹⁰, so the picker's
    /// <see cref="SpawnPicker.MaxDrawsPerPoint"/> (10⁶) box draws expect 5 × 10⁻⁴ hits and the first
    /// <see cref="SpawnPicker.Next"/> gives up (seeded, so the same on every run). The spawn fails at draw 1 with the
    /// picker's reason, no draw counted, and no player.
    /// </summary>
    [TestCase]
    public async Task Ac9_OpenGroundSliverThePickerCannotDrawFrom_ReportsThePickerFailureAndSpawnsNoPlayer()
    {
        AssertPackPresent();
        var mapId = $"tmp-sliver-boundary-{Guid.NewGuid():N}"[..30];
        var tiles = CopyPack(mapId, boundary =>
            boundary["spawn_area"]!["open_ground"] = JsonNode.Parse("[[[[0, 0], [1000, 1000], [1000, 1000.000001], [0, 0]]]]"));
        await AssertSpawnFailure(mapId, tiles, expectBoundary: true, "draw 1 from the spawn area's open ground", "found no point");
    }

    // ----- Criterion 10: walk, run and stop -----

    [TestCase]
    public async Task Ac10_HoldingMoveForward_WalksAwayFromTheCameraAtWalkSpeedAndStopsWithinTheDecelerationDistance()
    {
        AssertPackPresent();
        using var runner = ISceneRunner.Load(NewWorld(TestSeed), true);
        var world = (World)runner.Scene()!;
        AssertBool(await WaitForSpawn(world)).IsTrue();
        var player = world.Player!;
        await AssertStraightRun(world, player, FindFlatLine(world, player), run: false);
    }

    [TestCase]
    public async Task Ac10_HoldingMoveForwardAndRun_RunsAtRunSpeedAndStopsWithinTheDecelerationDistance()
    {
        AssertPackPresent();
        using var runner = ISceneRunner.Load(NewWorld(TestSeed), true);
        var world = (World)runner.Scene()!;
        AssertBool(await WaitForSpawn(world)).IsTrue();
        var player = world.Player!;
        await AssertStraightRun(world, player, FindFlatLine(world, player), run: true);
    }

    // ----- Criterion 11: jump -----

    [TestCase]
    public async Task Ac11_Jump_RaisesTheFeetByJumpHeightAndLands_ASecondPressInTheAirDoesNothing()
    {
        AssertPackPresent();
        using var runner = ISceneRunner.Load(NewWorld(TestSeed), true);
        var world = (World)runner.Scene()!;
        AssertBool(await WaitForSpawn(world)).IsTrue();
        var player = world.Player!;
        var line = FindFlatLine(world, player);
        await PlaceAndSettle(world, player, line.Start, line.Yaw);
        var takeOff = player.GlobalPosition.Y;
        AssertBool(player.IsOnFloor()).IsTrue();

        var airFrames = Mathf.CeilToInt(2f * player.JumpSpeed / player.Gravity * Engine.PhysicsTicksPerSecond);
        var apex = takeOff;
        var leftFloor = false;
        var landed = false;
        int? secondPressFrame = null;
        var velocityBeforeSecond = 0f;
        var velocityAfterSecond = 0f;
        await Drive(world, frame =>
        {
            var y = player.GlobalPosition.Y;
            apex = Mathf.Max(apex, y);
            var onFloor = player.IsOnFloor();
            leftFloor |= !onFloor && frame > 0;
            if (frame == 0)
            {
                Input.ActionPress(Player.JumpAction);
                return false;
            }

            if (frame == 1)
            {
                Input.ActionRelease(Player.JumpAction);
            }

            // In the air, rising at under half the take-off speed: press again.
            if (secondPressFrame is null && leftFloor && !onFloor && player.Velocity.Y > 0f && player.Velocity.Y < player.JumpSpeed / 2f)
            {
                secondPressFrame = frame;
                velocityBeforeSecond = player.Velocity.Y;
                Input.ActionPress(Player.JumpAction);
                return false;
            }

            if (secondPressFrame is { } pressed && frame == pressed + 1)
            {
                velocityAfterSecond = player.Velocity.Y;
                Input.ActionRelease(Player.JumpAction);
            }

            landed = leftFloor && onFloor;
            return landed;
        }, 2 * airFrames);

        var rise = apex - takeOff;
        GD.Print(Invariant(
            $"PlayerOnFootTest: Ac11 take-off {takeOff:0.###}, apex +{rise:0.####} m (JumpHeight {player.JumpHeight}), second press at vy {velocityBeforeSecond:0.###} -> {velocityAfterSecond:0.###}"));
        AssertBool(leftFloor).OverrideFailureMessage("the jump didn't leave the floor").IsTrue();
        AssertFloat(rise).IsBetween(player.JumpHeight * (1f - JumpTolerance), player.JumpHeight * (1f + JumpTolerance));
        AssertBool(landed).OverrideFailureMessage($"not on the floor again within {2 * airFrames} physics frames").IsTrue();
        AssertThat(secondPressFrame).OverrideFailureMessage("the second press never happened in the air").IsNotNull();
        AssertFloat(velocityAfterSecond).OverrideFailureMessage("the second press in the air changed the vertical speed upward")
            .IsLess(velocityBeforeSecond);
    }

    // ----- Criterion 12: jumping a barrier -----

    /// <summary>
    /// On the flattest open-ground line, a 1.15 m tall, 0.2 m deep box across the path. The player runs up at
    /// <see cref="Player.RunSpeed"/> and presses jump where the apex (<c>RunSpeed × JumpSpeed / Gravity</c> ahead)
    /// falls on the box; it clears it and lands on the far side.
    /// </summary>
    [TestCase]
    public async Task Ac12_RunUpJump_ClearsA115MetreBoxAndLandsOnTheFarSide()
    {
        AssertPackPresent();
        using var runner = ISceneRunner.Load(NewWorld(TestSeed), true);
        var world = (World)runner.Scene()!;
        AssertBool(await WaitForSpawn(world)).IsTrue();
        var player = world.Player!;
        var line = FindFlatLine(world, player);

        var apexDistance = player.RunSpeed * player.JumpSpeed / player.Gravity;
        var runUp = RunUpDistance(player.RunSpeed, player.Acceleration);
        var boxAt = runUp + apexDistance;
        var boxCentre = line.At(boxAt);
        var ground = world.MapLoader.TerrainHeightAt(boxCentre.X, boxCentre.Z)!.Value;
        // Buried 0.5 m, so no gap under it on uneven ground; 1.15 m above the ground at its centre.
        const float buried = 0.5f;
        var box = new StaticBody3D { Name = "TestBarrier" };
        box.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(2f, BoxHeight + buried, BoxDepth) } });
        world.AddChild(box);
        box.GlobalTransform = new Transform3D(
            Basis.LookingAt(line.Direction, Vector3.Up),
            new Vector3(boxCentre.X, ground + ((BoxHeight - buried) / 2f), boxCentre.Z));

        await PlaceAndSettle(world, player, line.Start, line.Yaw);
        var takeOff = 0f;
        var takeOffSpeed = 0f;
        var jumped = false;
        var landedAt = (float?)null;
        var leftFloor = false;
        var maxFrames = Mathf.CeilToInt((line.Length / player.RunSpeed + 3f) * Engine.PhysicsTicksPerSecond);
        await Drive(world, frame =>
        {
            var along = line.Along(player.GlobalPosition);
            var onFloor = player.IsOnFloor();
            if (frame == 0)
            {
                Input.ActionPress(Player.MoveForwardAction);
                Input.ActionPress(Player.RunAction);
                return false;
            }

            if (!jumped && along >= boxAt - apexDistance)
            {
                jumped = true;
                takeOff = along;
                takeOffSpeed = Horizontal(player.Velocity).Length();
                Input.ActionPress(Player.JumpAction);
                return false;
            }

            if (jumped)
            {
                Input.ActionRelease(Player.JumpAction);
                leftFloor |= !onFloor;
                if (leftFloor && onFloor)
                {
                    landedAt = along;
                    ReleaseAll();
                    return true;
                }
            }

            return false;
        }, maxFrames);

        var farSide = boxAt + (BoxDepth / 2f) + player.CapsuleRadius;
        GD.Print(Invariant(
            $"PlayerOnFootTest: Ac12 box at {boxAt:0.##} m along the line, take-off at {takeOff:0.##} m at {takeOffSpeed:0.####} m/s (RunSpeed {player.RunSpeed}), landed at {landedAt:0.##} m (far side from {farSide:0.##} m)"));
        AssertBool(jumped).OverrideFailureMessage("never reached the take-off point").IsTrue();
        AssertFloat(takeOffSpeed).OverrideFailureMessage("the run-up didn't reach RunSpeed before the take-off")
            .IsGreaterEqual(player.RunSpeed * (1f - SpecSpeedTolerance));
        AssertThat(landedAt).OverrideFailureMessage("didn't land").IsNotNull();
        AssertFloat(landedAt!.Value).IsGreater(farSide);
        await PhysicsFrames(world, 30);
        AssertFloat(line.Along(player.GlobalPosition)).IsGreater(farSide);
        AssertBool(player.IsOnFloor()).IsTrue();
    }

    // ----- Criterion 13: road edges -----

    /// <summary>
    /// From the terrain beside a road near the spawn area, onto the road's centre 10 m on, and off onto the terrain
    /// again 20 m from the start, then back the same way: the 0.05 m road edge is crossed both ways, twice. The road
    /// is the nearest ground drivable edge of the road graph to the spawn area where such a route has only road and
    /// terrain under it.
    /// </summary>
    [TestCase]
    public async Task Ac13_WalkingOntoARoadAndOffItAgain_NeverAirborneOverOneFrameNeverStopsNeverBelowTheSurface()
    {
        AssertPackPresent();
        using var runner = ISceneRunner.Load(NewWorld(TestSeed), true);
        var world = (World)runner.Scene()!;
        AssertBool(await WaitForSpawn(world)).IsTrue();
        var player = world.Player!;
        var route = FindRoadRoute(world, player);
        AssertThat(route).OverrideFailureMessage("no road near the spawn area has a terrain-road-terrain route clear of other bodies").IsNotNull();
        GD.Print(Invariant($"PlayerOnFootTest: Ac13 route beside {route!.Value.EdgeId}: {string.Join(" -> ", route.Value.Points)}"));

        foreach (var points in new[] { route.Value.Points, route.Value.Points.AsEnumerable().Reverse().ToList() })
        {
            var result = await FollowRoute(world, player, points, run: false);
            var minimum = MinimumCruiseSpeed(player.WalkSpeed, points, result.MaxSlopeRadians);
            GD.Print(Invariant(
                $"PlayerOnFootTest: Ac13 {points[0]} -> {points[^1]}: longest airborne run {result.LongestAirborne} frames, min speed {result.MinCruiseSpeed:0.###} (allowed {minimum:0.###}), worst depth below the surface {result.WorstBelowSurface * 1000:0.###} mm, road/terrain switches {result.SurfaceSwitches}"));
            AssertBool(result.Finished).OverrideFailureMessage("the route wasn't finished").IsTrue();
            AssertInt(result.SurfaceSwitches).OverrideFailureMessage("the route didn't cross the road edge both ways").IsGreaterEqual(2);
            AssertInt(result.LongestAirborne).IsLessEqual(1);
            AssertFloat(result.MinCruiseSpeed).IsGreaterEqual(minimum);
            AssertFloat(result.WorstBelowSurface).IsLessEqual(PenetrationSlop());
        }
    }

    // ----- Criterion 14: walls and railings -----

    [TestCase]
    public async Task Ac14_RunningIntoABuildingWall_StopsAtTheWallAndDoesNotPassItIn3Seconds()
    {
        AssertPackPresent();
        using var runner = ISceneRunner.Load(NewWorld(TestSeed), true);
        var world = (World)runner.Scene()!;
        AssertBool(await WaitForSpawn(world)).IsTrue();
        var player = world.Player!;
        // The capsule's centre stops a radius from the wall, so the run-up to RunSpeed fits in front of it.
        var approach = RunUpDistance(player.RunSpeed, player.Acceleration) + player.CapsuleRadius;
        var wall = FindWall(world, player, approach);
        AssertThat(wall).OverrideFailureMessage("no building near the spawn area has a wall reachable over open ground").IsNotNull();
        var (building, point, normal, start) = wall!.Value;

        await PlaceAndSettle(world, player, start, YawOf(-normal));
        var minDistance = float.MaxValue;
        var lastDistance = 0f;
        var lastSpeed = 0f;
        var topSpeed = 0f;
        var frames = Mathf.CeilToInt(WallRunSeconds * Engine.PhysicsTicksPerSecond);
        await Drive(world, frame =>
        {
            var distance = (player.GlobalPosition - point).Dot(normal);
            minDistance = Mathf.Min(minDistance, distance);
            lastDistance = distance;
            lastSpeed = Horizontal(player.Velocity).Length();
            topSpeed = Mathf.Max(topSpeed, lastSpeed);
            if (frame == 0)
            {
                Input.ActionPress(Player.MoveForwardAction);
                Input.ActionPress(Player.RunAction);
            }

            return false;
        }, frames);
        ReleaseAll();

        GD.Print(Invariant(
            $"PlayerOnFootTest: Ac14 wall of {building} at {point}, normal {normal}: start {approach:0.##} m out, top speed {topSpeed:0.####} m/s, closest {minDistance:0.####} m, end {lastDistance:0.####} m, end speed {lastSpeed:0.###} (capsule radius {player.CapsuleRadius})"));
        AssertFloat(topSpeed).OverrideFailureMessage("the player didn't reach RunSpeed before the wall")
            .IsGreaterEqual(player.RunSpeed * (1f - SpecSpeedTolerance));
        AssertFloat(minDistance).OverrideFailureMessage("the capsule went into the wall").IsGreaterEqual(player.CapsuleRadius - player.SafeMargin);
        AssertFloat(lastDistance).OverrideFailureMessage("the player didn't reach the wall").IsLessEqual(player.CapsuleRadius + SpecDistanceTolerance);
    }

    [TestCase]
    public async Task Ac14_RunningAcrossABridgeDeckIntoItsRailing_DoesNotPassIt()
    {
        AssertPackPresent();
        using var runner = ISceneRunner.Load(NewWorld(TestSeed), true);
        var world = (World)runner.Scene()!;
        AssertBool(await WaitForSpawn(world)).IsTrue();
        var player = world.Player!;

        // N/A only for a pack without a single bridge edge (the test-area pack may have none). With bridges, failing to
        // find a deck with a railing is a failure: the spec allows no N/A for criterion 14.
        var bridgeEdges = world.MapLoader.RoadGraph!.Edges.Count(e => e.Bridge);
        if (bridgeEdges == 0)
        {
            GD.Print($"PlayerOnFootTest: Ac14 bridge railing N/A: the '{world.MapLoader.Manifest!.Area}' pack's road graph has no bridge edges, so there is no railing to run into (the wall half of criterion 14 still runs)");
            return;
        }

        var deck = FindBridgeDeck(world);
        AssertThat(deck)
            .OverrideFailureMessage($"the road graph has {bridgeEdges} bridge edges, but none has a deck under its middle with a railing beside it")
            .IsNotNull();
        var (way, centre, side, railing) = deck!.Value;
        await PlaceAndSettle(world, player, centre, YawOf(side));
        var deckY = player.GlobalPosition.Y;
        var maxOffset = float.MinValue;
        var lowest = float.MaxValue;
        var frames = Mathf.CeilToInt(WallRunSeconds * Engine.PhysicsTicksPerSecond);
        await Drive(world, frame =>
        {
            maxOffset = Mathf.Max(maxOffset, (player.GlobalPosition - centre).Dot(side));
            lowest = Mathf.Min(lowest, player.GlobalPosition.Y);
            if (frame == 0)
            {
                Input.ActionPress(Player.MoveForwardAction);
                Input.ActionPress(Player.RunAction);
            }

            return false;
        }, frames);
        ReleaseAll();

        GD.Print(Invariant(
            $"PlayerOnFootTest: Ac14 bridge {way}: railing {railing:0.###} m from the centre line, player reached {maxOffset:0.###} m, lowest feet {lowest - deckY:0.####} m from the deck"));
        AssertFloat(maxOffset).OverrideFailureMessage("the player passed the railing").IsLessEqual(railing - player.CapsuleRadius + player.SafeMargin);
        AssertFloat(lowest).OverrideFailureMessage("the player fell off the deck").IsGreaterEqual(deckY - SpecDistanceTolerance);
        AssertBool(player.IsOnFloor()).IsTrue();
    }

    // ----- Criterion 15: bridge ramps -----

    /// <summary>
    /// From <see cref="RampLength"/> × 2 out on the road, up the ramp at a <c>span_end</c> node, along the chained
    /// bridge edges across the deck and down onto the road beyond it, then back: on the floor in every frame. At
    /// <see cref="Player.RunSpeed"/>, the speed most likely to leave the floor at a ramp's crest (and a walk over a
    /// 60 m route would take most of a minute).
    /// </summary>
    [TestCase]
    public async Task Ac15_GoingUpABridgeRampAcrossTheDeckAndDown_StaysOnTheFloor()
    {
        AssertPackPresent();
        using var runner = ISceneRunner.Load(NewWorld(TestSeed), true);
        var world = (World)runner.Scene()!;
        AssertBool(await WaitForSpawn(world)).IsTrue();
        var player = world.Player!;
        var route = FindRampRoute(world);
        if (route is null)
        {
            GD.Print($"PlayerOnFootTest: Ac15 N/A: the '{world.MapLoader.Manifest!.Area}' pack has no span_end node with a bridge route (allowed by the spec on the test-area pack only)");
            AssertThat(world.MapLoader.Manifest!.Area).OverrideFailureMessage("the full pack must have a bridge route for criterion 15").IsEqual("test");
            return;
        }

        var (spanEnd, points, deckFrom, deckTo, deckWidth) = route.Value;

        // "Crosses the deck": at each end of the deck the whole capsule was over it, so the player's centre came
        // within half the deck's width (the narrowest bridge edge's width_m) less the capsule radius of the end node.
        var onDeck = (deckWidth / 2f) - player.CapsuleRadius;
        AssertFloat(onDeck).OverrideFailureMessage(Invariant($"the deck ({deckWidth} m) is narrower than the capsule")).IsGreater(0f);
        GD.Print(Invariant($"PlayerOnFootTest: Ac15 route over {spanEnd}: {points.Count} points, deck from {deckFrom} to {deckTo}, {deckWidth} m wide"));
        foreach (var path in new[] { points, points.AsEnumerable().Reverse().ToList() })
        {
            var result = await FollowRoute(world, player, path, run: true);
            GD.Print(Invariant(
                $"PlayerOnFootTest: Ac15 {path[0]} -> {path[^1]}: airborne frames {result.AirborneFrames}, closest to the deck ends {result.ClosestTo(deckFrom):0.###} / {result.ClosestTo(deckTo):0.###} m, worst depth below the surface {result.WorstBelowSurface * 1000:0.###} mm"));
            AssertBool(result.Finished).OverrideFailureMessage("the route wasn't finished").IsTrue();
            AssertInt(result.AirborneFrames).OverrideFailureMessage("left the floor on the ramp or deck").IsEqual(0);
            AssertFloat(result.ClosestTo(deckFrom)).IsLessEqual(onDeck);
            AssertFloat(result.ClosestTo(deckTo)).IsLessEqual(onDeck);
            AssertFloat(result.WorstBelowSurface).IsLessEqual(PenetrationSlop());
        }
    }

    // ----- Criterion 16: camera clipping -----

    [TestCase]
    public async Task Ac16_CameraTurnedRoundThePlayerBesideAWall_NeverHasACollisionBodyBetweenPivotAndCamera()
    {
        AssertPackPresent();
        using var runner = ISceneRunner.Load(NewWorld(TestSeed), true);
        var world = (World)runner.Scene()!;
        AssertBool(await WaitForSpawn(world)).IsTrue();
        var player = world.Player!;
        var camera = world.PlayerCamera!;
        var wall = FindWall(world, player, WallDistance);
        AssertThat(wall).OverrideFailureMessage("no building near the spawn area has a wall reachable over open ground").IsNotNull();
        var (building, point, normal, start) = wall!.Value;
        await PlaceAndSettle(world, player, start, YawOf(-normal));

        var steps = Mathf.RoundToInt(360f / YawStepDegrees);
        var settleFrames = Mathf.CeilToInt(camera.ArmLength / camera.ArmReturnSpeed * Engine.PhysicsTicksPerSecond) + 2;
        var hits = new List<string>();
        var shortest = float.MaxValue;
        var checks = 0;
        var baseYaw = YawOf(-normal);
        var space = world.GetWorld3D().DirectSpaceState;
        for (var step = 0; step < steps; step++)
        {
            camera.Yaw = baseYaw + Mathf.DegToRad(step * YawStepDegrees);
            var yawDegrees = step * YawStepDegrees;
            var previous = float.NaN;
            var stable = 0;
            await Drive(world, frame =>
            {
                var from = camera.GlobalPosition;
                var to = camera.Camera.GlobalPosition;
                shortest = Mathf.Min(shortest, from.DistanceTo(to));
                checks++;
                var hit = space.IntersectRay(PhysicsRayQueryParameters3D.Create(from, to, CollisionLayers.Map));
                if (hit.Count > 0)
                {
                    hits.Add(Invariant($"yaw +{yawDegrees}° frame {frame}: {(hit["collider"].AsGodotObject() as Node)?.Name} at {hit["position"].AsVector3()}"));
                }

                var length = camera.SpringArm.SpringLength;
                stable = length == previous ? stable + 1 : 0;
                previous = length;
                return frame > 1 && stable >= 2;
            }, settleFrames * 2);
        }

        GD.Print(Invariant(
            $"PlayerOnFootTest: Ac16 wall of {building}, player {WallDistance} m from it: {checks} pivot-camera rays over {steps} yaws, shortest arm {shortest:0.###} m"));
        AssertFloat(shortest).OverrideFailureMessage("the wall never shortened the arm, so the test didn't test clipping").IsLess(camera.ArmLength);
        AssertThat(string.Join("; ", hits.Take(10))).IsEqual("");
    }

    // ----- Criterion 17: gamepad (injected joypad events) and mouse -----

    /// <summary>Criterion 17 with criterion 10's measurement: the left stick at full forward tilt (LeftY −1) walks like <c>move_forward</c>.</summary>
    [TestCase]
    public async Task Ac17_LeftStickFullForward_WalksAwayFromTheCameraAtWalkSpeedAndStops()
    {
        AssertPackPresent();
        using var runner = ISceneRunner.Load(NewWorld(TestSeed), true);
        var world = (World)runner.Scene()!;
        AssertBool(await WaitForSpawn(world)).IsTrue();
        var player = world.Player!;
        await AssertStraightRun(world, player, FindFlatLine(world, player), run: false, () => Stick(JoyAxis.LeftY, -1f), 1f, "Ac17 full stick");
    }

    /// <summary>
    /// "Half tilt" (spec amendment, part B plan 2): the raw stick value halfway between the action's deadzone and full
    /// tilt. <see cref="Input.GetVector"/> remaps the length radially from [deadzone, 1] to [0, 1], with the mean of its
    /// four actions' deadzones, so that raw value gives strength 0.5 and half <see cref="Player.WalkSpeed"/> (± 5 %).
    /// </summary>
    [TestCase]
    public async Task Ac17_LeftStickHalfTilt_WalksAtHalfWalkSpeed()
    {
        AssertPackPresent();
        using var runner = ISceneRunner.Load(NewWorld(TestSeed), true);
        var world = (World)runner.Scene()!;
        AssertBool(await WaitForSpawn(world)).IsTrue();
        var player = world.Player!;
        var deadzone = MoveDeadzone();
        var halfTilt = (deadzone + 1f) / 2f;
        var strength = Mathf.InverseLerp(deadzone, 1f, halfTilt);
        AssertFloat(strength).IsEqualApprox(0.5f, 1e-6f);

        // The remap as Input reads it: injected on its own, the move vector has that length.
        Stick(JoyAxis.LeftY, -halfTilt);
        var vector = Input.GetVector(Player.MoveLeftAction, Player.MoveRightAction, Player.MoveForwardAction, Player.MoveBackAction);
        ReleaseAll();
        GD.Print(Invariant($"PlayerOnFootTest: Ac17 half tilt: deadzone {deadzone}, raw {halfTilt}, move vector {vector}"));
        AssertFloat(vector.Length()).IsEqualApprox(strength, 1e-6f);
        AssertFloat(vector.Y).IsLess(0f);

        await AssertStraightRun(world, player, FindFlatLine(world, player), run: false, () => Stick(JoyAxis.LeftY, -halfTilt), strength, "Ac17 half tilt");
    }

    /// <summary>
    /// With the stick at full forward tilt, a left-stick click (press, then release a frame later) turns
    /// <see cref="Player.RunToggle"/> on and the player runs at <see cref="Player.RunSpeed"/> (± 5 % after
    /// acceleration). The stick back inside its deadzone (half the deadzone, not zero) turns the toggle off in that
    /// physics frame; after the stop, full tilt again walks at <see cref="Player.WalkSpeed"/>. Each speed is measured
    /// over <see cref="ToggleHoldSeconds"/> after the ramp to it, along the flat line, and the whole sequence stays on
    /// that line (checked).
    /// </summary>
    [TestCase]
    public async Task Ac17_LeftStickClick_TogglesRunningOn_StickInsideTheDeadzoneTurnsItOff_TheNextMovementWalks()
    {
        AssertPackPresent();
        using var runner = ISceneRunner.Load(NewWorld(TestSeed), true);
        var world = (World)runner.Scene()!;
        AssertBool(await WaitForSpawn(world)).IsTrue();
        var player = world.Player!;
        var line = FindFlatLine(world, player);
        await PlaceAndSettle(world, player, line.Start, line.Yaw);
        AssertBool(player.RunToggle.On).IsFalse();
        var deadzone = MoveDeadzone();
        var dt = 1f / Engine.PhysicsTicksPerSecond;
        var hold = Mathf.RoundToInt(ToggleHoldSeconds * Engine.PhysicsTicksPerSecond);

        // Frame 0 tilts, frame 1 clicks, frame 2 releases the click. The click is just pressed for the player in frame
        // 2 (class summary), so from frame 2 the target is RunSpeed; it is reached within ⌈RunSpeed / (Acceleration·dt)⌉
        // frames (from rest, the slowest case), so the run is measured from then.
        const int clickFrame = 1;
        const int clickActs = clickFrame + 1;
        var runFrom = clickActs + Mathf.CeilToInt(player.RunSpeed / player.Acceleration / dt) + 1;
        var runTo = runFrom + hold;
        var stopFrames = Mathf.CeilToInt(player.RunSpeed / player.Deceleration / dt) + StopMarginFrames;
        int? stoppedAt = null;
        int? walkFrom = null;
        int? walkTo = null;
        var onAfterClick = false;
        var onBeforeRelease = false;
        var onAfterRelease = true;
        var onWhileWalking = true;
        var alongRunFrom = 0f;
        var alongRunTo = 0f;
        var alongWalkFrom = 0f;
        var alongWalkTo = 0f;
        var farthest = 0f;
        await Drive(world, frame =>
        {
            var along = line.Along(player.GlobalPosition);
            farthest = Mathf.Max(farthest, along);
            switch (frame)
            {
                case 0:
                    Stick(JoyAxis.LeftY, -1f);
                    return false;
                case clickFrame:
                    PadButton(GamepadButtons.LeftStickClick, pressed: true);
                    return false;
                case clickActs:
                    PadButton(GamepadButtons.LeftStickClick, pressed: false);
                    return false;
                case clickActs + 1:
                    // The state read here is after the physics of the frame that saw the click.
                    onAfterClick = player.RunToggle.On;
                    return false;
            }

            if (frame == runFrom)
            {
                alongRunFrom = along;
            }
            else if (frame == runTo)
            {
                alongRunTo = along;
                onBeforeRelease = player.RunToggle.On;
                Stick(JoyAxis.LeftY, -deadzone / 2f);
            }
            else if (frame == runTo + 1)
            {
                onAfterRelease = player.RunToggle.On;
            }
            else if (frame > runTo + 1 && stoppedAt is null)
            {
                if (Horizontal(player.Velocity).Length() < StoppedSpeed)
                {
                    stoppedAt = frame;
                    Stick(JoyAxis.LeftY, -1f);
                    walkFrom = frame + Mathf.CeilToInt(player.WalkSpeed / player.Acceleration / dt) + 1;
                    walkTo = walkFrom + hold;
                }
                else if (frame > runTo + stopFrames)
                {
                    return true;
                }
            }
            else if (frame == walkFrom)
            {
                alongWalkFrom = along;
            }
            else if (frame == walkTo)
            {
                alongWalkTo = along;
                onWhileWalking = player.RunToggle.On;
                ReleaseAll();
                return true;
            }

            return false;
        }, runTo + stopFrames + (3 * hold));
        ReleaseAll();

        var seconds = hold * dt;
        var runSpeed = (alongRunTo - alongRunFrom) / seconds;
        var walkSpeed = (alongWalkTo - alongWalkFrom) / seconds;
        GD.Print(Invariant(
            $"PlayerOnFootTest: Ac17 run toggle: on after the click {onAfterClick}, run {runSpeed:0.####} m/s (RunSpeed {player.RunSpeed}), off after the stick returned {!onAfterRelease}, stopped at frame {stoppedAt}, walk {walkSpeed:0.####} m/s (WalkSpeed {player.WalkSpeed}), farthest {farthest:0.##} of {line.Length:0.##} m"));
        AssertBool(onAfterClick).OverrideFailureMessage("the left-stick click didn't turn the run toggle on").IsTrue();
        AssertBool(onBeforeRelease).OverrideFailureMessage("the run toggle turned off while the stick was held").IsTrue();
        AssertFloat(runSpeed).IsBetween(player.RunSpeed * (1f - SpecSpeedTolerance), player.RunSpeed * (1f + SpecSpeedTolerance));
        AssertBool(onAfterRelease).OverrideFailureMessage("the stick back inside its deadzone didn't turn the run toggle off").IsFalse();
        AssertThat(stoppedAt).OverrideFailureMessage("the player didn't stop with the stick inside its deadzone").IsNotNull();
        AssertThat(walkTo).IsNotNull();
        AssertFloat(walkSpeed).IsBetween(player.WalkSpeed * (1f - SpecSpeedTolerance), player.WalkSpeed * (1f + SpecSpeedTolerance));
        AssertBool(onWhileWalking).OverrideFailureMessage("the next movement ran").IsFalse();
        AssertFloat(farthest).OverrideFailureMessage("the sequence left the checked flat line").IsLessEqual(line.Length);
    }

    /// <summary>The south face button (button 0) jumps like <c>jump</c> (criterion 11's apex tolerance) and the player lands.</summary>
    [TestCase]
    public async Task Ac17_SouthButton_JumpsToJumpHeightAndLands()
    {
        AssertPackPresent();
        using var runner = ISceneRunner.Load(NewWorld(TestSeed), true);
        var world = (World)runner.Scene()!;
        AssertBool(await WaitForSpawn(world)).IsTrue();
        var player = world.Player!;
        var line = FindFlatLine(world, player);
        await PlaceAndSettle(world, player, line.Start, line.Yaw);
        var takeOff = player.GlobalPosition.Y;
        var airFrames = Mathf.CeilToInt(2f * player.JumpSpeed / player.Gravity * Engine.PhysicsTicksPerSecond);
        var apex = takeOff;
        var leftFloor = false;
        var landed = false;
        await Drive(world, frame =>
        {
            apex = Mathf.Max(apex, player.GlobalPosition.Y);
            var onFloor = player.IsOnFloor();
            if (frame == 0)
            {
                PadButton(GamepadButtons.South, pressed: true);
                return false;
            }

            if (frame == 1)
            {
                PadButton(GamepadButtons.South, pressed: false);
            }

            leftFloor |= !onFloor;
            landed = leftFloor && onFloor;
            return landed;
        }, 2 * airFrames);

        var rise = apex - takeOff;
        GD.Print(Invariant($"PlayerOnFootTest: Ac17 south button: apex +{rise:0.####} m (JumpHeight {player.JumpHeight})"));
        AssertBool(leftFloor).OverrideFailureMessage("the south button didn't leave the floor").IsTrue();
        AssertFloat(rise).IsBetween(player.JumpHeight * (1f - JumpTolerance), player.JumpHeight * (1f + JumpTolerance));
        AssertBool(landed).OverrideFailureMessage($"not on the floor again within {2 * airFrames} physics frames").IsTrue();
    }

    /// <summary>
    /// In the world, with the player camera current: right stick right (RightX +1) turns the camera right (yaw falls by
    /// <see cref="PlayerCamera.StickSensitivity"/> × dt per frame at full tilt), and stick up (RightY −1) pitches it up
    /// by the same rate (not inverted). Held up, then down, for longer than the whole pitch range takes, the pitch
    /// stops at <see cref="PlayerCamera.MaxPitchDegrees"/>, then <see cref="PlayerCamera.MinPitchDegrees"/>, and is
    /// within them in every frame.
    /// </summary>
    [TestCase]
    public async Task Ac17_RightStick_TurnsTheCameraRightAndPitchesUpNotInverted_PitchStaysWithinItsLimits()
    {
        AssertPackPresent();
        using var runner = ISceneRunner.Load(NewWorld(TestSeed), true);
        var world = (World)runner.Scene()!;
        AssertBool(await WaitForSpawn(world)).IsTrue();
        var camera = world.PlayerCamera!;
        AssertBool(camera.IsCurrent).IsTrue();
        AssertBool(camera.InvertStickY).IsFalse();
        var min = Mathf.DegToRad(camera.MinPitchDegrees);
        var max = Mathf.DegToRad(camera.MaxPitchDegrees);
        var step = camera.StickSensitivity / Engine.PhysicsTicksPerSecond;

        // Yaw: a quarter turn's worth of frames, so the change stays inside (−π, π) and needs no unwrapping beyond one.
        var yawFrames = Mathf.FloorToInt(Mathf.Pi / 2f / step);
        var yawBefore = camera.Yaw;
        await HoldStick(world, JoyAxis.RightX, 1f, yawFrames, null);
        var yawChange = Mathf.AngleDifference(yawBefore, camera.Yaw);

        // Pitch up for fewer frames than the way from the start pitch to the limit.
        var pitchBefore = camera.Pitch;
        var upFrames = Mathf.FloorToInt((max - pitchBefore) / step) - 1;
        await HoldStick(world, JoyAxis.RightY, -1f, upFrames, null);
        var pitchChange = camera.Pitch - pitchBefore;

        // Held through the whole range and beyond, both ways.
        var rangeFrames = Mathf.CeilToInt((max - min) / step) + 2;
        var pitches = new List<float>();
        await HoldStick(world, JoyAxis.RightY, -1f, rangeFrames, pitches);
        var atTop = camera.Pitch;
        await HoldStick(world, JoyAxis.RightY, 1f, rangeFrames, pitches);
        var atBottom = camera.Pitch;

        // Float rounding only: each frame adds one rounded step and wraps or clamps, so the error grows by at most a
        // couple of float32 ulps of the angle (|angle| ≤ π) per frame.
        var ulp = MathF.BitIncrement(Mathf.Pi) - Mathf.Pi;
        GD.Print(Invariant(
            $"PlayerOnFootTest: Ac17 right stick: yaw {Mathf.RadToDeg(yawChange):0.###}° in {yawFrames} frames, pitch +{Mathf.RadToDeg(pitchChange):0.###}° in {upFrames} frames, held: top {Mathf.RadToDeg(atTop):0.###}°, bottom {Mathf.RadToDeg(atBottom):0.###}°"));
        AssertFloat(yawChange).IsEqualApprox(-yawFrames * step, 2f * yawFrames * ulp);
        AssertFloat(pitchChange).IsEqualApprox(upFrames * step, 2f * upFrames * ulp);
        AssertFloat(atTop).IsEqualApprox(max, 1e-6f);
        AssertFloat(atBottom).IsEqualApprox(min, 1e-6f);
        AssertFloat(pitches.Max()).IsLessEqual(max);
        AssertFloat(pitches.Min()).IsGreaterEqual(min);
    }

    /// <summary>
    /// <see cref="PlayerCamera.OrbitStick"/> directly (x right, y down, as <see cref="Input.GetVector"/> gives it): up
    /// pitches up and down back by <see cref="PlayerCamera.StickSensitivity"/> × delta, right turns right, a tilt held
    /// for twice the time the pitch range takes stops at each limit, and <see cref="PlayerCamera.InvertStickY"/>
    /// reverses only the pitch.
    /// </summary>
    [TestCase]
    public async Task Ac17_OrbitStick_UpPitchesUpRightTurnsRight_PitchClampedToItsLimits()
    {
        using var runner = ISceneRunner.Load(new Node3D { Name = "Root" }, true);
        var camera = new PlayerCamera { Name = "PlayerCamera" };
        runner.Scene()!.AddChild(camera);
        await runner.AwaitIdleFrame();
        var min = Mathf.DegToRad(camera.MinPitchDegrees);
        var max = Mathf.DegToRad(camera.MaxPitchDegrees);
        var dt = 1.0 / Engine.PhysicsTicksPerSecond;
        var step = camera.StickSensitivity * (float)dt;
        var wholeRange = 2.0 * (max - min) / camera.StickSensitivity;

        var start = camera.Pitch;
        camera.OrbitStick(new Vector2(0f, -1f), dt);
        AssertFloat(camera.Pitch).IsEqualApprox(start + step, 1e-6f);
        camera.OrbitStick(new Vector2(0f, 1f), dt);
        AssertFloat(camera.Pitch).IsEqualApprox(start, 1e-6f);

        camera.OrbitStick(new Vector2(0f, -1f), wholeRange);
        AssertFloat(camera.Pitch).IsEqualApprox(max, 1e-6f);
        camera.OrbitStick(new Vector2(0f, 1f), wholeRange);
        AssertFloat(camera.Pitch).IsEqualApprox(min, 1e-6f);

        var yaw = camera.Yaw;
        camera.OrbitStick(new Vector2(1f, 0f), dt);
        AssertFloat(camera.Yaw).IsEqualApprox(yaw - step, 1e-6f);
        AssertFloat(camera.Rotation.X).IsEqualApprox(camera.Pitch, 1e-6f);
        AssertFloat(camera.Rotation.Y).IsEqualApprox(camera.Yaw, 1e-6f);

        camera.InvertStickY = true;
        camera.OrbitStick(new Vector2(0f, 1f), dt);
        AssertFloat(camera.Pitch).IsEqualApprox(min + step, 1e-6f);
        yaw = camera.Yaw;
        camera.OrbitStick(new Vector2(1f, 0f), dt);
        AssertFloat(camera.Yaw).IsEqualApprox(yaw - step, 1e-6f);
    }


    [TestCase]
    public async Task Ac17_MouseUp_PitchesUp_AndPitchStaysWithinItsLimits()
    {
        using var runner = ISceneRunner.Load(new Node3D { Name = "Root" }, true);
        var camera = new PlayerCamera { Name = "PlayerCamera" };
        runner.Scene()!.AddChild(camera);
        await runner.AwaitIdleFrame();
        var min = Mathf.DegToRad(camera.MinPitchDegrees);
        var max = Mathf.DegToRad(camera.MaxPitchDegrees);
        AssertBool(camera.InvertMouseY).IsFalse();

        var start = camera.Pitch;
        camera.Orbit(new Vector2(0f, -10f));
        AssertFloat(camera.Pitch).IsEqualApprox(start + (10f * camera.MouseSensitivity), 1e-6f);
        camera.Orbit(new Vector2(0f, 10f));
        AssertFloat(camera.Pitch).IsEqualApprox(start, 1e-6f);

        camera.Orbit(new Vector2(0f, -1e6f));
        AssertFloat(camera.Pitch).IsEqualApprox(max, 1e-6f);
        camera.Orbit(new Vector2(0f, -10f));
        AssertFloat(camera.Pitch).IsEqualApprox(max, 1e-6f);
        camera.Orbit(new Vector2(0f, 1e6f));
        AssertFloat(camera.Pitch).IsEqualApprox(min, 1e-6f);
        camera.Orbit(new Vector2(0f, 10f));
        AssertFloat(camera.Pitch).IsEqualApprox(min, 1e-6f);

        // Mouse right turns right (yaw decreases in Godot's convention), and the rotation follows yaw and pitch.
        var yaw = camera.Yaw;
        camera.Orbit(new Vector2(10f, 0f));
        AssertFloat(camera.Yaw).IsEqualApprox(yaw - (10f * camera.MouseSensitivity), 1e-6f);
        AssertFloat(camera.Rotation.X).IsEqualApprox(camera.Pitch, 1e-6f);
        AssertFloat(camera.Rotation.Y).IsEqualApprox(camera.Yaw, 1e-6f);
    }

    // ----- Criterion 18: no automatic swing-back -----

    /// <summary>
    /// One full turn of the input direction in <see cref="CircleSeconds"/> with no camera input. The input at frame k
    /// of N is the angle θ = 2πk/N, pressed as <c>move_right</c> sin θ and <c>move_back</c> cos θ
    /// (<see cref="PressAxis"/>: a positive value presses the positive action), so the move direction, relative to the
    /// camera's forward F and right R, is d(θ) = sin θ·R − cos θ·F: back, right, forward, left. At
    /// <see cref="Player.WalkSpeed"/> W and ω = 2π/T the ideal path is p(t) = c + r·(−cos θ·R − sin θ·F), a circle of
    /// radius r = W·T/2π (2.8 m/s, 3 s: ≈ 1.34 m) centred one radius to the right of the start, c = p₀ + r·R. The walk
    /// starts there, on the flattest disc of open ground that holds that circle with the capsule (<see cref="FindOpenDisc"/>),
    /// and every frame the player's centre stays within <see cref="CirclePathBound"/> of c and on open ground. The camera
    /// yaw and pitch never change.
    /// </summary>
    [TestCase]
    public async Task Ac18_WalkingInACircleWithoutCameraInput_LeavesTheCameraYawUnchanged()
    {
        AssertPackPresent();
        using var runner = ISceneRunner.Load(NewWorld(TestSeed), true);
        var world = (World)runner.Scene()!;
        AssertBool(await WaitForSpawn(world)).IsTrue();
        var player = world.Player!;
        var camera = world.PlayerCamera!;
        var openGround = world.MapLoader.Boundary!.SpawnArea.OpenGround;
        var r = player.WalkSpeed * CircleSeconds / Mathf.Tau;

        // The disc must hold the path bound plus the capsule. The bound's slope term depends on the disc found, so the
        // search radius adds a sample step instead (the rings are sampled that far apart, a margin for an obstacle
        // edge between two samples), and the slope term and the centre's settle offset must fit in it (checked below;
        // at a few degrees the slope term is millimetres).
        var noSlopeBound = CirclePathBound(player, r, 0f);
        var disc = FindOpenDisc(world, player, noSlopeBound + player.CapsuleRadius + SampleStep);
        var bound = CirclePathBound(player, r, disc.MaxSlope);

        // Camera yaw 0: F = −Z and R = F × up = +X (OnFootMotion: right input moves along (cos yaw, −sin yaw)).
        const float yaw = 0f;
        var right = new Vector3(Mathf.Cos(yaw), 0f, -Mathf.Sin(yaw));
        var startXz = disc.Centre - (right * r);
        var ground = FirstHit(world, startXz.X, startXz.Z, RayTop, -RayTop)!.Value.Position;
        await PlaceAndSettle(world, player, ground, yaw);
        camera.Yaw = yaw;
        var cameraYaw = camera.Yaw;
        var pitch = camera.Pitch;
        var centre = Horizontal(player.GlobalPosition + (right * r));
        var centreOffset = HorizontalDistance(centre, disc.Centre);
        AssertFloat(centreOffset + bound + player.CapsuleRadius)
            .OverrideFailureMessage(Invariant($"the circle (bound {bound:0.###} m, centre {centreOffset:0.####} m off) doesn't fit the {disc.Radius:0.###} m disc with the capsule"))
            .IsLessEqual(disc.Radius);

        var frames = Mathf.CeilToInt(CircleSeconds * Engine.PhysicsTicksPerSecond);
        var yaws = new List<float>();
        var pitches = new List<float>();
        var facing = new List<float>();
        var path = 0f;
        var last = player.GlobalPosition;
        var farthest = 0f;
        var offGround = new List<Vector3>();
        await Drive(world, frame =>
        {
            var position = player.GlobalPosition;
            yaws.Add(camera.Yaw);
            pitches.Add(camera.Pitch);
            facing.Add(player.FacingYaw);
            path += Horizontal(position - last).Length();
            last = position;
            farthest = Mathf.Max(farthest, HorizontalDistance(position, centre));
            if (!SpawnPicker.Contains(openGround, position.X, position.Z))
            {
                offGround.Add(position);
            }

            var angle = Mathf.Tau * frame / frames;
            PressAxis(Player.MoveLeftAction, Player.MoveRightAction, Mathf.Sin(angle));
            PressAxis(Player.MoveForwardAction, Player.MoveBackAction, Mathf.Cos(angle));
            return false;
        }, frames);
        ReleaseAll();

        var turned = 0f;
        for (var i = 1; i < facing.Count; i++)
        {
            turned += Mathf.Abs(Mathf.AngleDifference(facing[i - 1], facing[i]));
        }

        GD.Print(Invariant(
            $"PlayerOnFootTest: Ac18 circle r {r:0.###} m on a {disc.Radius:0.###} m disc at {disc.Centre} (max slope {Mathf.RadToDeg(disc.MaxSlope):0.##}°): walked {path:0.##} m, farthest {farthest:0.###} m from the centre (bound {bound:0.###}), facing turned {Mathf.RadToDeg(turned):0.#}°, camera yaw stayed {Mathf.RadToDeg(cameraYaw):0.###}°"));
        AssertFloat(path).OverrideFailureMessage("the player didn't walk").IsGreater(player.WalkSpeed * CircleSeconds / 2f);
        AssertFloat(turned).OverrideFailureMessage("the player didn't walk round").IsGreater(Mathf.Pi * 1.5f);
        AssertFloat(farthest).OverrideFailureMessage("the walk left the circle's bound").IsLessEqual(bound);
        AssertThat(string.Join("; ", offGround.Take(5).Select(p => Invariant($"{p}")))).OverrideFailureMessage("the walk left the open ground").IsEqual("");
        AssertThat(yaws.Distinct().ToList()).IsEqual(new List<float> { cameraYaw });
        AssertThat(pitches.Distinct().ToList()).IsEqual(new List<float> { pitch });
    }

    // ----- Criterion 19: the debug camera toggle (debug builds) and the release layout -----

    [TestCase]
    public void Ac19_DebugCameraAction_IsBoundToExactlyThePhysicalKeysQuoteleftAndSection()
    {
        AssertBool(InputMap.HasAction(World.DebugCameraAction)).IsTrue();
        var events = InputMap.ActionGetEvents(World.DebugCameraAction);
        var keys = events.OfType<InputEventKey>().ToList();
        AssertInt(events.Count).OverrideFailureMessage("debug_camera has events other than the two keys").IsEqual(2);
        AssertInt(keys.Count).IsEqual(2);
        AssertThat(keys.Select(k => k.PhysicalKeycode).OrderBy(k => k).ToList())
            .IsEqual(new[] { Key.Quoteleft, Key.Section }.OrderBy(k => k).ToList());

        // Physical keycodes only (the key left of 1 on any layout), never a layout keycode or label.
        foreach (var key in keys)
        {
            AssertThat(key.Keycode).IsEqual(Key.None);
            AssertThat(key.KeyLabel).IsEqual(Key.None);
        }
    }

    /// <summary>
    /// A press of the physical key left of 1 (<c>Key.Quoteleft</c>), injected through <see cref="Input.ParseInputEvent"/>,
    /// makes the <see cref="DebugCamera"/> current at the player camera's global transform and field of view, and
    /// turns the player's and the player camera's input off.
    /// </summary>
    [TestCase]
    public async Task Ac19_DebugCameraKey_SwitchesToTheDebugCameraAtThePlayerCameraTransform()
    {
        AssertPackPresent();
        AssertBool(DebugBuild.IsDebug).IsTrue();
        using var runner = ISceneRunner.Load(NewWorld(TestSeed), true);
        var world = (World)runner.Scene()!;
        AssertBool(await WaitForSpawn(world)).IsTrue();
        var player = world.Player!;
        var playerCamera = world.PlayerCamera!;
        var debugCamera = world.Camera!;
        var line = FindFlatLine(world, player);
        await PlaceAndSettle(world, player, line.Start, line.Yaw);
        AssertBool(playerCamera.IsCurrent).IsTrue();
        AssertBool(world.DebugCameraActive).IsFalse();
        var transform = playerCamera.Camera.GlobalTransform;
        AssertThat(debugCamera.GlobalTransform.IsEqualApprox(transform)).OverrideFailureMessage("the debug camera already was where the player camera is").IsFalse();

        TapKey(Key.Quoteleft);

        GD.Print(Invariant($"PlayerOnFootTest: Ac19 player camera at {transform.Origin}, debug camera now at {debugCamera.GlobalPosition}"));
        AssertBool(world.DebugCameraActive).OverrideFailureMessage("the debug_camera key didn't toggle").IsTrue();
        AssertBool(debugCamera.Current).IsTrue();
        AssertThat(world.GetViewport().GetCamera3D()).IsSame(debugCamera);
        AssertBool(playerCamera.IsCurrent).IsFalse();
        AssertBool(debugCamera.GlobalTransform.IsEqualApprox(transform))
            .OverrideFailureMessage(Invariant($"debug camera at {debugCamera.GlobalTransform}, player camera at {transform}"))
            .IsTrue();
        AssertFloat(debugCamera.Fov).IsEqual(playerCamera.Camera.Fov);
        AssertBool(player.InputEnabled).IsFalse();
        AssertBool(playerCamera.InputEnabled).IsFalse();
    }

    /// <summary>
    /// With the debug camera on: <c>move_forward</c> held (and the right stick at full tilt) for
    /// <see cref="DebugHoldSeconds"/> leaves the player and the player camera where they were, while
    /// <c>debug_forward</c> moves the debug camera along its view direction by <c>MoveSpeed</c> × the process time it
    /// was held (the debug camera moves in <c>_Process</c>).
    /// </summary>
    [TestCase]
    public async Task Ac19_WhileTheDebugCameraIsOn_MoveForwardDoesNotMoveThePlayer_DebugForwardMovesTheDebugCamera()
    {
        AssertPackPresent();
        using var runner = ISceneRunner.Load(NewWorld(TestSeed), true);
        var world = (World)runner.Scene()!;
        AssertBool(await WaitForSpawn(world)).IsTrue();
        var player = world.Player!;
        var playerCamera = world.PlayerCamera!;
        var debugCamera = world.Camera!;
        var line = FindFlatLine(world, player);
        await PlaceAndSettle(world, player, line.Start, line.Yaw);
        TapKey(Key.Quoteleft);
        AssertBool(world.DebugCameraActive).IsTrue();

        var feet = player.GlobalPosition;
        var yaw = playerCamera.Yaw;
        var pitch = playerCamera.Pitch;
        var cameraStart = debugCamera.GlobalPosition;
        var forward = -debugCamera.GlobalBasis.Z.Normalized();
        var tree = world.GetTree();
        var heldTime = 0.0;
        var processFrames = 0;
        void OnProcess()
        {
            heldTime += world.GetProcessDeltaTime();
            processFrames++;
        }

        var frames = Mathf.RoundToInt(DebugHoldSeconds * Engine.PhysicsTicksPerSecond);
        var maxDrift = 0f;
        var jumped = false;
        try
        {
            await Drive(world, frame =>
            {
                maxDrift = Mathf.Max(maxDrift, HorizontalDistance(player.GlobalPosition, feet));
                jumped |= !player.IsOnFloor();
                if (frame == 0)
                {
                    // Counted from here: the process frames after this physics frame read the pressed action.
                    tree.ProcessFrame += OnProcess;
                    Input.ActionPress(Player.MoveForwardAction);
                    Input.ActionPress(Player.JumpAction);
                    Input.ActionPress(DebugForwardAction);
                    Stick(JoyAxis.RightX, 1f);
                    Stick(JoyAxis.RightY, -1f);
                    return false;
                }

                if (frame == frames)
                {
                    tree.ProcessFrame -= OnProcess;
                    ReleaseAll();
                    return true;
                }

                return false;
            }, frames + 1);
        }
        finally
        {
            tree.ProcessFrame -= OnProcess;
            ReleaseAll();
        }

        var moved = debugCamera.GlobalPosition - cameraStart;
        var along = moved.Dot(forward);
        var across = (moved - (forward * along)).Length();
        var expected = debugCamera.MoveSpeed * (float)heldTime;

        // Float rounding only: each process frame adds one rounded step to coordinates of magnitude up to |p|, so the
        // error grows by at most a float32 ulp of |p| per component and frame.
        var magnitude = Mathf.Max(MaxAbs(cameraStart), MaxAbs(debugCamera.GlobalPosition));
        var rounding = 2f * Mathf.Sqrt(3f) * processFrames * (MathF.BitIncrement(magnitude) - magnitude);

        // The player stands with zero velocity and no input: MoveAndSlide's depenetration recovery, bounded by the
        // body's safe margin, is the only horizontal motion it may make.
        GD.Print(Invariant(
            $"PlayerOnFootTest: Ac19 debug camera on: player drift {maxDrift:0.######} m (safe margin {player.SafeMargin}), debug camera {along:0.####} m forward (expected {expected:0.####} over {processFrames} process frames, {heldTime:0.####} s), {across:0.######} m across"));
        AssertFloat(maxDrift).OverrideFailureMessage("move_forward moved the player while the debug camera was on").IsLessEqual(player.SafeMargin);
        AssertBool(jumped).OverrideFailureMessage("jump worked while the debug camera was on").IsFalse();
        AssertFloat(playerCamera.Yaw).IsEqual(yaw);
        AssertFloat(playerCamera.Pitch).IsEqual(pitch);
        AssertInt(processFrames).OverrideFailureMessage("no process frame ran while debug_forward was held").IsGreater(0);
        AssertFloat(along).OverrideFailureMessage("debug_forward didn't move the debug camera forward").IsGreater(0f);
        AssertFloat(along).IsEqualApprox(expected, rounding);
        AssertFloat(across).IsLessEqual(rounding);
        AssertBool(debugCamera.Current).IsTrue();
    }

    /// <summary>
    /// After switching on with <c>Key.Quoteleft</c>, a press of the other binding (<c>Key.Section</c>, left of 1 on ISO
    /// Mac keyboards) switches back: the player camera is current again, as it was (same yaw and pitch), the player and
    /// the player camera read input again, and <c>move_forward</c> then moves the player.
    /// </summary>
    [TestCase]
    public async Task Ac19_SectionKey_SwitchesBackToThePlayerCameraWithInputOn()
    {
        AssertPackPresent();
        using var runner = ISceneRunner.Load(NewWorld(TestSeed), true);
        var world = (World)runner.Scene()!;
        AssertBool(await WaitForSpawn(world)).IsTrue();
        var player = world.Player!;
        var playerCamera = world.PlayerCamera!;
        var line = FindFlatLine(world, player);
        await PlaceAndSettle(world, player, line.Start, line.Yaw);
        var yaw = playerCamera.Yaw;
        var pitch = playerCamera.Pitch;
        TapKey(Key.Quoteleft);
        AssertBool(world.DebugCameraActive).IsTrue();
        await PhysicsFrames(world, 2);

        TapKey(Key.Section);

        AssertBool(world.DebugCameraActive).OverrideFailureMessage("the Section key didn't toggle back").IsFalse();
        AssertBool(playerCamera.IsCurrent).IsTrue();
        AssertThat(world.GetViewport().GetCamera3D()).IsSame(playerCamera.Camera);
        AssertBool(world.Camera!.Current).IsFalse();
        AssertBool(player.InputEnabled).IsTrue();
        AssertBool(playerCamera.InputEnabled).IsTrue();
        AssertFloat(playerCamera.Yaw).IsEqual(yaw);
        AssertFloat(playerCamera.Pitch).IsEqual(pitch);

        // Input really is on again: one frame of move_forward accelerates the player.
        var speed = 0f;
        await Drive(world, frame =>
        {
            if (frame == 0)
            {
                Input.ActionPress(Player.MoveForwardAction);
                return false;
            }

            speed = Horizontal(player.Velocity).Length();
            ReleaseAll();
            return true;
        }, 2);
        AssertFloat(speed).OverrideFailureMessage("move_forward didn't move the player after switching back").IsGreater(0f);
    }

    /// <summary>
    /// Criterion 19's release check (spec amendment 5, ADR 0016): with <see cref="DebugBuild.Override"/> false before the
    /// world enters the tree, the world builds the release layout. It has no <see cref="DebugCamera"/> (nor any debug
    /// view) anywhere in its tree, the player spawns with its camera current, and neither
    /// <see cref="World.ToggleDebugCamera"/> nor the <c>debug_camera</c> key changes anything.
    /// </summary>
    [TestCase]
    public async Task Ac19_ReleaseLayout_HasNoDebugCameraAndThePlayerCameraStaysCurrent()
    {
        AssertPackPresent();
        DebugBuild.Override = false;
        AssertBool(DebugBuild.IsDebug).IsFalse();
        using var runner = ISceneRunner.Load(NewWorld(TestSeed), true);
        var world = (World)runner.Scene()!;
        AssertBool(await WaitForSpawn(world)).OverrideFailureMessage("the player didn't spawn in the release layout").IsTrue();
        await PhysicsFrames(world, 2);
        var player = world.Player!;
        var playerCamera = world.PlayerCamera!;

        AssertInt(Descendants(world).OfType<DebugCamera>().Count()).IsEqual(0);
        AssertInt(Descendants(world).OfType<OrthophotoDebugView>().Count()).IsEqual(0);
        AssertInt(Descendants(world).OfType<RoadGraphDebugView>().Count()).IsEqual(0);
        AssertInt(Descendants(world).OfType<PlaceInspector>().Count()).IsEqual(0);
        AssertThat(world.Camera).IsNull();
        AssertThat(world.Orthophoto).IsNull();
        AssertThat(world.RoadGraphView).IsNull();
        AssertThat(world.Inspector).IsNull();
        AssertBool(playerCamera.IsCurrent).IsTrue();
        AssertThat(world.GetViewport().GetCamera3D()).IsSame(playerCamera.Camera);

        AssertBool(world.ToggleDebugCamera()).IsFalse();
        TapKey(Key.Quoteleft);
        TapKey(Key.Section);
        await PhysicsFrames(world, 2);
        AssertBool(world.DebugCameraActive).IsFalse();
        AssertBool(playerCamera.IsCurrent).IsTrue();
        AssertThat(world.GetViewport().GetCamera3D()).IsSame(playerCamera.Camera);
        AssertBool(player.InputEnabled).IsTrue();
        AssertBool(playerCamera.InputEnabled).IsTrue();
    }

    /// <summary>
    /// Criterion 19, the mouse capture restore ("Switching back restores the player camera as it was, and the mouse
    /// capture"): <see cref="PlayerCamera.Activate"/> with <c>captureMouse: false</c> makes the camera current and
    /// doesn't capture the mouse, and a <see cref="World.ToggleDebugCamera"/> round trip that starts with the mouse not
    /// captured ends with the player camera current and the mouse still not captured.
    /// <para>
    /// What this can and can't show: measured on 2026-11-04 under the gdUnit4 headless run, the window reports focus
    /// (<c>HasFocus()</c> true), but the headless display server doesn't support mouse capture, so
    /// <see cref="Input.MouseMode"/> stays <c>Visible</c> after any capture request and
    /// <see cref="PlayerCamera.MouseCaptured"/> is always false here (the spawn's <c>Activate()</c> asks for the capture
    /// and doesn't get it). The test therefore shows that neither path changes <see cref="Input.MouseMode"/> or leaves the
    /// debug camera current, and that the camera never reports a capture; it can't tell "not captured because not
    /// asked" from "not captured because the display can't", and it can't show that a round trip starting with the
    /// mouse captured captures it again. That half needs a real display (the playtest, criterion 25). The printed line
    /// records the display, focus and mouse mode of each run.
    /// </para>
    /// </summary>
    [TestCase]
    public async Task Ac19_SwitchingBackWithTheMouseFree_MakesThePlayerCameraCurrentAndLeavesTheMouseFree()
    {
        AssertPackPresent();
        using var runner = ISceneRunner.Load(NewWorld(TestSeed), true);
        var world = (World)runner.Scene()!;
        AssertBool(await WaitForSpawn(world)).IsTrue();
        await PhysicsFrames(world, 2);
        var playerCamera = world.PlayerCamera!;
        var modeBefore = Input.MouseMode;
        GD.Print($"PlayerOnFootTest: Ac19 mouse restore: display {DisplayServer.GetName()}, focus {playerCamera.GetWindow().HasFocus()}, mouse mode {modeBefore}");
        AssertBool(playerCamera.MouseCaptured).OverrideFailureMessage("precondition: the mouse is captured after the spawn").IsFalse();

        // Activate without capture, with the debug camera current first so MakeCurrent has something to change.
        world.Camera!.MakeCurrent();
        AssertBool(playerCamera.IsCurrent).IsFalse();
        playerCamera.Activate(captureMouse: false);
        AssertBool(playerCamera.IsCurrent).IsTrue();
        AssertThat(world.GetViewport().GetCamera3D()).IsSame(playerCamera.Camera);
        AssertBool(playerCamera.MouseCaptured).IsFalse();
        AssertThat(Input.MouseMode).IsEqual(modeBefore);
        await PhysicsFrames(world, 2);
        AssertBool(playerCamera.MouseCaptured).IsFalse();
        AssertThat(Input.MouseMode).IsEqual(modeBefore);

        // The round trip through the debug camera.
        AssertBool(world.ToggleDebugCamera()).IsTrue();
        AssertBool(world.DebugCameraActive).IsTrue();
        AssertBool(playerCamera.IsCurrent).IsFalse();
        await PhysicsFrames(world, 2);
        AssertBool(world.ToggleDebugCamera()).IsTrue();
        AssertBool(world.DebugCameraActive).IsFalse();
        AssertBool(playerCamera.IsCurrent).IsTrue();
        AssertThat(world.GetViewport().GetCamera3D()).IsSame(playerCamera.Camera);
        AssertBool(playerCamera.InputEnabled).IsTrue();
        AssertBool(playerCamera.MouseCaptured).IsFalse();
        AssertThat(Input.MouseMode).IsEqual(modeBefore);
        await PhysicsFrames(world, 2);
        AssertBool(playerCamera.MouseCaptured).IsFalse();
        AssertThat(Input.MouseMode).IsEqual(modeBefore);
    }

    // ----- Criterion 23: --player and the debug-only screenshot flags outside a debug build -----

    /// <summary>
    /// Criterion 23's release half: with <see cref="DebugBuild.Override"/> false, a <see cref="ScreenshotTool"/> asked for
    /// <c>--player</c> finishes with <see cref="ScreenshotExitCode.Error"/> (1) when it is added and pushes
    /// "--player needs a debug build". The check comes before the headless refusal, so it runs here without a display.
    /// </summary>
    [TestCase]
    public void Ac23_PlayerFlagOutsideADebugBuild_ExitsWithErrorAndSaysItNeedsADebugBuild()
    {
        DebugBuild.Override = false;
        var (exitCode, errors) = RunScreenshotToolReady(ToolArgs() with { Player = true });
        AssertThat(exitCode).IsEqual(ScreenshotExitCode.Error);
        AssertBool(errors.Any(e => e.Contains("--player needs a debug build"))).OverrideFailureMessage(string.Join("; ", errors)).IsTrue();
        AssertBool(errors.Any(e => e.Contains("--headless"))).OverrideFailureMessage("the headless refusal ran before the build check").IsFalse();
    }

    /// <summary>
    /// The contrast for <see cref="Ac23_PlayerFlagOutsideADebugBuild_ExitsWithErrorAndSaysItNeedsADebugBuild"/>: in a
    /// debug build the same request passes the build check and stops at the headless refusal (still exit 1 here), so
    /// the message above comes from the build check and not from something else refusing.
    /// </summary>
    [TestCase]
    public void Ac23_PlayerFlagInADebugBuild_PassesTheBuildCheck()
    {
        AssertThat(DisplayServer.GetName()).OverrideFailureMessage("this contrast needs the headless run of .runsettings").IsEqual("headless");
        DebugBuild.Override = true;
        var (exitCode, errors) = RunScreenshotToolReady(ToolArgs() with { Player = true });
        AssertThat(exitCode).IsEqual(ScreenshotExitCode.Error);
        AssertBool(errors.Any(e => e.Contains("--player needs a debug build"))).OverrideFailureMessage(string.Join("; ", errors)).IsFalse();
        AssertBool(errors.Any(e => e.Contains("rendering is disabled with --headless"))).OverrideFailureMessage(string.Join("; ", errors)).IsTrue();
    }

    /// <summary>
    /// World-edge spec criterion 25's release half: with <see cref="DebugBuild.Override"/> false, a
    /// <see cref="ScreenshotTool"/> asked for <c>--death fall</c> or <c>--death screen</c> finishes with
    /// <see cref="ScreenshotExitCode.Error"/> (1) when it is added and pushes "--death needs a debug build", before the
    /// headless refusal. In a debug build the same request passes the build check and stops at the headless refusal.
    /// </summary>
    [TestCase]
    public void WorldEdgeAc25_DeathFlagOutsideADebugBuild_ExitsWithErrorAndSaysItNeedsADebugBuild()
    {
        foreach (var death in new[] { DeathShot.Fall, DeathShot.Screen })
        {
            DebugBuild.Override = false;
            var (exitCode, errors) = RunScreenshotToolReady(ToolArgs() with { Death = death });
            AssertThat(exitCode).OverrideFailureMessage($"--death {death}").IsEqual(ScreenshotExitCode.Error);
            AssertBool(errors.Any(e => e.Contains("--death needs a debug build"))).OverrideFailureMessage($"--death {death}: {string.Join("; ", errors)}").IsTrue();
            AssertBool(errors.Any(e => e.Contains("--headless"))).OverrideFailureMessage("the headless refusal ran before the build check").IsFalse();

            AssertThat(DisplayServer.GetName()).OverrideFailureMessage("the contrast needs the headless run of .runsettings").IsEqual("headless");
            DebugBuild.Override = true;
            (exitCode, errors) = RunScreenshotToolReady(ToolArgs() with { Death = death });
            AssertThat(exitCode).IsEqual(ScreenshotExitCode.Error);
            AssertBool(errors.Any(e => e.Contains("--death needs a debug build"))).OverrideFailureMessage(string.Join("; ", errors)).IsFalse();
            AssertBool(errors.Any(e => e.Contains("rendering is disabled with --headless"))).OverrideFailureMessage(string.Join("; ", errors)).IsTrue();
        }
    }

    /// <summary>
    /// Outside a debug build the world has no road graph view (criterion 19's release layout), so a
    /// <see cref="ScreenshotTool"/> asked for <c>--road-graph</c> without it exits 1 and says why (game/CLAUDE.md,
    /// "Screenshots": "Exit 1 in a non-debug build").
    /// </summary>
    [TestCase]
    public void RoadGraphFlagOutsideADebugBuild_ExitsWithErrorAndSaysItNeedsTheDebugView()
    {
        DebugBuild.Override = false;
        var (exitCode, errors) = RunScreenshotToolReady(ToolArgs() with { RoadGraph = true });
        AssertThat(exitCode).IsEqual(ScreenshotExitCode.Error);
        AssertBool(errors.Any(e => e.Contains("--road-graph needs the road graph debug view, which only debug builds have")))
            .OverrideFailureMessage(string.Join("; ", errors)).IsTrue();
    }

    /// <summary>
    /// Outside a debug build the world has no place inspector, so a <see cref="ScreenshotTool"/> asked for
    /// <c>--inspect</c> without it exits 1 and says why (game/CLAUDE.md, "Screenshots": "Exit 1 in a non-debug build").
    /// </summary>
    [TestCase]
    public void InspectFlagOutsideADebugBuild_ExitsWithErrorAndSaysItNeedsTheInspector()
    {
        DebugBuild.Override = false;
        var (exitCode, errors) = RunScreenshotToolReady(ToolArgs() with { Inspect = true });
        AssertThat(exitCode).IsEqual(ScreenshotExitCode.Error);
        AssertBool(errors.Any(e => e.Contains("--inspect needs the place inspector, which only debug builds have")))
            .OverrideFailureMessage(string.Join("; ", errors)).IsTrue();
    }

    // ----- Criterion 21: street label follows the player -----

    [TestCase]
    public async Task Ac21_StreetLabel_FollowsThePlayerBesideANamedEdgeWithTheDebugCameraFarAway()
    {
        AssertPackPresent();
        using var runner = ISceneRunner.Load(NewWorld(TestSeed), true);
        var world = (World)runner.Scene()!;
        AssertBool(await WaitForSpawn(world)).IsTrue();
        var player = world.Player!;
        var hud = world.Hud;
        var graph = world.MapLoader.RoadGraph!;
        AssertThat(hud.Target).IsSame(player);

        // The first named ground edge whose middle, on the road surface, is nearest to that edge. The road surface is
        // the first body from the top there (no height window to guess): with a bridge or anything else above the
        // middle, the next candidate is used.
        RoadEdge? edge = null;
        Vector3 feet = default;
        foreach (var candidate in graph.Edges.Where(e => e.Names.Name is not null && !e.Bridge && !e.Tunnel))
        {
            var middle = PolylineAt(candidate.Points, PolylineLength(candidate.Points) / 2);
            if (FirstHit(world, middle.X, middle.Z, RayTop, -RayTop) is not { } hit || !hit.Body.IsInGroup(MapLoader.RoadGroup) || IsBridgeBody(hit.Body))
            {
                continue;
            }

            if (graph.NearestEdge(ToLocal(hit.Position), hud.StreetMaxDistance) is { } nearest && nearest.Edge.Id == candidate.Id)
            {
                edge = candidate;
                feet = hit.Position;
                break;
            }
        }

        AssertThat(edge).OverrideFailureMessage("no named ground edge is the nearest edge to its own middle").IsNotNull();
        // The debug camera current, far beyond the map: its own downward ray would hit nothing.
        world.Camera!.MakeCurrent();
        world.Camera!.GlobalPosition = feet + new Vector3(100_000f, 1000f, 100_000f);
        player.PlaceAt(feet, 0f);
        await PhysicsFrames(world, 3);

        var nearestNow = graph.NearestEdge(ToLocal(player.GlobalPosition), hud.StreetMaxDistance);
        AssertThat(nearestNow?.Edge.Id).IsEqual(edge!.Id);
        var expected = StreetLabel.Format(edge);
        GD.Print($"PlayerOnFootTest: Ac21 player on {edge.Id}: '{hud.Text}'");
        AssertThat(hud.Target).IsSame(player);
        AssertThat(hud.Text).IsEqual(expected);
        AssertThat(hud.LastReferencePoint).IsEqual(player.GlobalPosition);
        // Without the target the far camera gives nothing, so the text above came from the player.
        AssertThat(hud.UpdateStreetAt(null)).IsEqual("");
    }

    // ----- Spec "Spawn": a map reload spawns again, also after the world left the tree and came back -----

    /// <summary>
    /// <see cref="World"/> hooks its map loader's signals once per stay in the tree (<c>_Ready</c> runs only once):
    /// after the world is removed and added again, <see cref="MapLoader.Load"/> frees the old player and spawns a new
    /// one, exactly once, with its camera current and the street label following it.
    /// </summary>
    [TestCase]
    public async Task Spawn_WorldRemovedFromTheTreeAndAddedAgain_SpawnsThePlayerAgainOnTheNextMapLoad()
    {
        AssertPackPresent();
        using var runner = ISceneRunner.Load(NewWorld(TestSeed), true);
        var world = (World)runner.Scene()!;
        AssertBool(await WaitForSpawn(world)).IsTrue();
        var firstPlayer = world.Player!;
        var firstCamera = world.PlayerCamera!;
        var spawns = new List<Vector3>();
        world.PlayerSpawned += spawns.Add;

        var parent = world.GetParent();
        parent.RemoveChild(world);
        await runner.AwaitIdleFrame();
        AssertBool(world.IsInsideTree()).IsFalse();
        parent.AddChild(world);
        await runner.AwaitIdleFrame();

        world.MapLoader.Load();
        AssertBool(await WaitForSpawn(world)).OverrideFailureMessage("no spawn after the world re-entered the tree and the map reloaded").IsTrue();
        await PhysicsFrames(world, world.SpawnPhysicsWaitFrames + 2);
        await runner.AwaitIdleFrame();

        var player = world.Player!;
        AssertInt(spawns.Count).OverrideFailureMessage($"expected one spawn, got {spawns.Count}").IsEqual(1);
        AssertThat(player).IsNotSame(firstPlayer);
        AssertBool(GodotObject.IsInstanceValid(firstPlayer)).OverrideFailureMessage("the old player wasn't freed").IsFalse();
        AssertBool(GodotObject.IsInstanceValid(firstCamera)).OverrideFailureMessage("the old player camera wasn't freed").IsFalse();
        AssertInt(Descendants(world).OfType<Player>().Count()).IsEqual(1);
        AssertInt(Descendants(world).OfType<PlayerCamera>().Count()).IsEqual(1);
        AssertBool(SpawnPicker.Contains(world.MapLoader.Boundary!.SpawnArea.OpenGround, spawns[0].X, spawns[0].Z)).IsTrue();
        AssertBool(world.PlayerCamera!.IsCurrent).IsTrue();
        AssertThat(world.Hud.Target).IsSame(player);
        AssertThat(world.LastSpawnFailure).IsNull();
    }

    // ----- Exports applied live (spec "Exports"; the playtest, criterion 25, tunes them in the running game) -----

    [TestCase]
    public async Task Exports_CapsuleHeightAndRadiusChangedOnAReadyPlayer_RebuildTheCollisionShapeAndModel()
    {
        using var runner = ISceneRunner.Load(new Node3D { Name = "Root" }, true);
        var player = new Player { Name = "Player" };
        runner.Scene()!.AddChild(player);
        await runner.AwaitIdleFrame();
        var startRadius = player.CapsuleRadius;
        var startShape = player.CollisionShape.Shape;
        var startModel = player.Model;

        // Values other than the defaults, read from the player so the test never relies on the defaults' numbers.
        var height = player.CapsuleHeight * 1.25f;
        var radius = player.CapsuleRadius * 1.5f;

        player.CapsuleHeight = height;
        var capsule = player.CollisionShape.Shape as CapsuleShape3D;
        AssertThat(capsule).IsNotNull();
        AssertThat(capsule).IsNotSame(startShape);
        AssertFloat(capsule!.Height).IsEqual(height);
        AssertFloat(capsule.Radius).IsEqual(startRadius);
        AssertThat(player.CollisionShape.Position).IsEqual(new Vector3(0f, height / 2f, 0f));
        AssertThat(player.Model).IsNotSame(startModel);
        AssertFloat(TopOfModel(player.Model)).IsGreater(TopOfModel(startModel));

        var modelBeforeRadius = player.Model;
        player.CapsuleRadius = radius;
        capsule = (CapsuleShape3D)player.CollisionShape.Shape;
        AssertFloat(capsule.Radius).IsEqual(radius);
        AssertFloat(capsule.Height).IsEqual(height);
        AssertThat(player.CollisionShape.Position).IsEqual(new Vector3(0f, height / 2f, 0f));
        AssertThat(player.Model).IsNotSame(modelBeforeRadius);
        var body = player.Model.GetNode<MeshInstance3D>("Body").Mesh as CapsuleMesh;
        AssertFloat(body!.Radius).IsGreater(((CapsuleMesh)startModel.GetNode<MeshInstance3D>("Body").Mesh).Radius);

        // The replaced models are freed at the end of the frame; one model stays.
        await runner.AwaitIdleFrame();
        AssertBool(GodotObject.IsInstanceValid(startModel)).IsFalse();
        AssertBool(GodotObject.IsInstanceValid(modelBeforeRadius)).IsFalse();
        AssertInt(player.GetChildren().Count(c => c.Name == (StringName)"Model")).IsEqual(1);
        AssertInt(player.GetChildren().OfType<CollisionShape3D>().Count()).IsEqual(1);
    }

    [TestCase]
    public async Task Exports_CapsuleSizeSetBeforeReady_IsUsedWhenThePlayerIsBuilt()
    {
        using var runner = ISceneRunner.Load(new Node3D { Name = "Root" }, true);
        var player = new Player { Name = "Player" };
        var height = player.CapsuleHeight * 0.8f;
        var radius = player.CapsuleRadius * 0.8f;
        player.CapsuleHeight = height;
        player.CapsuleRadius = radius;
        runner.Scene()!.AddChild(player);
        await runner.AwaitIdleFrame();

        var capsule = (CapsuleShape3D)player.CollisionShape.Shape;
        AssertFloat(capsule.Height).IsEqual(height);
        AssertFloat(capsule.Radius).IsEqual(radius);
        AssertThat(player.CollisionShape.Position).IsEqual(new Vector3(0f, height / 2f, 0f));
        AssertInt(player.GetChildren().Count(c => c.Name == (StringName)"Model")).IsEqual(1);
    }

    [TestCase]
    public async Task Exports_FovAndCameraCollisionRadiusChangedOnAReadyCamera_ApplyToTheCameraAndSpringArm()
    {
        using var runner = ISceneRunner.Load(new Node3D { Name = "Root" }, true);
        var camera = new PlayerCamera { Name = "PlayerCamera" };
        runner.Scene()!.AddChild(camera);
        await runner.AwaitIdleFrame();
        AssertFloat(camera.Camera.Fov).IsEqual(camera.Fov);
        AssertFloat(((SphereShape3D)camera.SpringArm.Shape).Radius).IsEqual(camera.CameraCollisionRadius);

        var fov = camera.Fov + 15f;
        var radius = camera.CameraCollisionRadius * 2f;
        camera.Fov = fov;
        camera.CameraCollisionRadius = radius;

        AssertFloat(camera.Fov).IsEqual(fov);
        AssertFloat(camera.Camera.Fov).IsEqual(fov);
        AssertFloat(camera.CameraCollisionRadius).IsEqual(radius);
        var sphere = camera.SpringArm.Shape as SphereShape3D;
        AssertThat(sphere).OverrideFailureMessage("the spring arm's shape isn't a sphere").IsNotNull();
        AssertFloat(sphere!.Radius).IsEqual(radius);
    }

    [TestCase]
    public async Task Exports_FovAndCameraCollisionRadiusSetBeforeReady_AreUsedWhenTheCameraIsBuilt()
    {
        using var runner = ISceneRunner.Load(new Node3D { Name = "Root" }, true);
        var camera = new PlayerCamera { Name = "PlayerCamera", Fov = 55f, CameraCollisionRadius = 0.35f };
        runner.Scene()!.AddChild(camera);
        await runner.AwaitIdleFrame();

        AssertFloat(camera.Camera.Fov).IsEqual(55f);
        AssertFloat(((SphereShape3D)camera.SpringArm.Shape).Radius).IsEqual(0.35f);
    }

    // ===== Helpers: screenshot tool =====

    /// <summary>A preset request with no flags; the output path is never written (every run here exits before saving).</summary>
    private static ScreenshotArgs ToolArgs() =>
        new("centre-street-level", Path.Combine(Path.GetTempPath(), "player-on-foot-test-unused.png"), null, MapId);

    /// <summary>
    /// Adds a <see cref="ScreenshotTool"/> for <paramref name="args"/> with the release layout's null views and no world
    /// (as <see cref="World"/> builds it outside a debug build) and <see cref="ScreenshotTool.QuitOnFinish"/> false, so
    /// its <c>_Ready</c> checks run without quitting the test runner. The loader is never added to the tree, so no map
    /// loads. Returns <see cref="ScreenshotTool.ExitCode"/> and the errors pushed meanwhile.
    /// </summary>
    private static (int? ExitCode, IReadOnlyList<string> Errors) RunScreenshotToolReady(ScreenshotArgs args)
    {
        var loader = new MapLoader { Name = "MapLoader" };
        var tool = new ScreenshotTool { Name = "ScreenshotTool", Request = args, Loader = loader, QuitOnFinish = false };
        var capture = new ErrorCapture();
        OS.AddLogger(capture);
        try
        {
            using var runner = ISceneRunner.Load(new Node { Name = "Root" }, true);
            runner.Scene()!.AddChild(tool);
            return (tool.ExitCode, capture.ErrorsOnly);
        }
        finally
        {
            OS.RemoveLogger(capture);
            loader.Free();
        }
    }

    // ===== Helpers: world and frames =====

    private static World NewWorld(int? seed)
    {
        var world = GD.Load<PackedScene>(WorldScenePath).Instantiate<World>();
        world.Seed = seed;
        return world;
    }

    /// <summary>Waits until the world's player exists (the spawn runs after the terrain-collision wait).</summary>
    private static Task<bool> WaitForSpawn(World world) =>
        WaitUntil(world, () => world.Player is not null, world.SpawnPhysicsWaitFrames + 30);

    private static async Task<Vector3> SpawnPositionOfARun(int seed)
    {
        using var runner = ISceneRunner.Load(NewWorld(seed), true);
        var world = (World)runner.Scene()!;
        Vector3? spawned = null;
        world.PlayerSpawned += position => spawned = position;
        AssertBool(await WaitForSpawn(world)).IsTrue();
        AssertThat(spawned).IsNotNull();
        return spawned!.Value;
    }

    private static async Task PhysicsFrames(Node node, int count)
    {
        var tree = node.GetTree();
        for (var i = 0; i < count; i++)
        {
            await node.ToSignal(tree, SceneTree.SignalName.PhysicsFrame);
        }
    }

    private static async Task<bool> WaitUntil(Node node, Func<bool> condition, int maxPhysicsFrames)
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
    /// it: an exception thrown in a signal handler doesn't reach the test.
    /// </summary>
    private static async Task Drive(Node node, Func<int, bool> step, int maxFrames)
    {
        var tree = node.GetTree();
        var frame = 0;
        var done = false;
        void OnFrame()
        {
            if (done)
            {
                return;
            }

            done = step(frame) || ++frame >= maxFrames;
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
    }

    /// <summary>
    /// Returns every injected input to rest: the stick axes to 0 and the pad buttons and injected keys released (as
    /// events, because an action's state per device stays until its device sends the release), then every action the
    /// tests press with <see cref="Input.ActionPress(StringName, float)"/>.
    /// </summary>
    private static void ReleaseAll()
    {
        foreach (var axis in StickAxes)
        {
            Stick(axis, 0f);
        }

        foreach (var button in PadButtons)
        {
            PadButton(button, pressed: false);
        }

        foreach (var key in InjectedKeys)
        {
            KeyEvent(key, pressed: false);
        }

        foreach (var action in PlayerActions)
        {
            Input.ActionRelease(action);
        }
    }

    /// <summary>
    /// Feeds <paramref name="event"/> to <see cref="Input.ParseInputEvent"/> and flushes it at once, so the action
    /// state changes and the event reaches the nodes' input callbacks now. In a <see cref="SceneTree.PhysicsFrame"/>
    /// handler it acts like <see cref="Input.ActionPress(StringName, float)"/> (see the class summary for when a press
    /// counts as just pressed).
    /// </summary>
    private static void Inject(InputEvent @event)
    {
        Input.ParseInputEvent(@event);
        Input.FlushBufferedEvents();
    }

    private static void Stick(JoyAxis axis, float value) =>
        Inject(new InputEventJoypadMotion { Device = PadDevice, Axis = axis, AxisValue = value });

    private static void PadButton(JoyButton button, bool pressed) =>
        Inject(new InputEventJoypadButton { Device = PadDevice, ButtonIndex = button, Pressed = pressed });

    private static void KeyEvent(Key physical, bool pressed) =>
        Inject(new InputEventKey { PhysicalKeycode = physical, Pressed = pressed });

    /// <summary>
    /// The deadzone <see cref="Input.GetVector"/> uses for the move actions when none is passed: the mean of the four
    /// actions' deadzones (Godot's documented default).
    /// </summary>
    private static float MoveDeadzone() =>
        new[] { Player.MoveLeftAction, Player.MoveRightAction, Player.MoveForwardAction, Player.MoveBackAction }
            .Average(a => InputMap.ActionGetDeadzone(a));

    /// <summary>
    /// Tilts <paramref name="axis"/> to <paramref name="value"/> at the start of a physics frame and back to 0 at the
    /// start of the frame <paramref name="frames"/> later, so exactly <paramref name="frames"/> physics frames read the
    /// tilt. Adds the camera pitch after each of them to <paramref name="pitches"/> (when given).
    /// </summary>
    private static async Task HoldStick(World world, JoyAxis axis, float value, int frames, List<float>? pitches)
    {
        var camera = world.PlayerCamera!;
        await Drive(world, frame =>
        {
            if (frame > 0)
            {
                pitches?.Add(camera.Pitch);
            }

            if (frame == 0)
            {
                Stick(axis, value);
                return false;
            }

            if (frame == frames)
            {
                Stick(axis, 0f);
                return true;
            }

            return false;
        }, frames + 1);
    }

    /// <summary>
    /// Criterion 17's run toggle: how long each of the run and the walk is measured after its ramp. Both, with their
    /// ramps and the stop between, fit on the flat line criterion 10's single 2 s run is sized for (the test checks it).
    /// </summary>
    private const float ToggleHoldSeconds = 1f;

    /// <summary>Criterion 19: how long <c>move_forward</c> and <c>debug_forward</c> are held with the debug camera on (criterion 10's hold, 2 s).</summary>
    private const float DebugHoldSeconds = 2f;

    /// <summary>A key press and its release, as one tap of a physical key.</summary>
    private static void TapKey(Key physical)
    {
        KeyEvent(physical, pressed: true);
        KeyEvent(physical, pressed: false);
    }

    /// <summary>Presses <paramref name="negative"/> or <paramref name="positive"/> with the strength of <paramref name="value"/>.</summary>
    private static void PressAxis(string negative, string positive, float value)
    {
        Input.ActionRelease(value > 0f ? negative : positive);
        if (Mathf.Abs(value) > 0f)
        {
            Input.ActionPress(value > 0f ? positive : negative, Mathf.Abs(value));
        }
        else
        {
            Input.ActionRelease(positive);
        }
    }

    /// <summary>
    /// Puts the player's feet on <paramref name="feet"/> (raised a little, so it doesn't start in the ground), the
    /// camera behind it at <paramref name="yaw"/>, and waits until it stands still on the floor.
    /// </summary>
    private static async Task PlaceAndSettle(World world, Player player, Vector3 feet, float yaw)
    {
        ReleaseAll();
        player.PlaceAt(feet + (Vector3.Up * 0.05f), yaw);
        world.PlayerCamera!.PlaceBehind(yaw);
        var still = 0;
        await Drive(world, _ =>
        {
            still = player.IsOnFloor() && player.Velocity.Length() < 1e-3f ? still + 1 : 0;
            return still >= 5;
        }, SpawnSettleFrames);
        AssertBool(player.IsOnFloor()).OverrideFailureMessage(Invariant($"the player didn't settle at {feet}")).IsTrue();
    }

    // ===== Helpers: criterion 10 =====

    /// <summary>
    /// Criterion 10's measurement: from rest at the line's start with the camera behind, the input held for 2 s moves
    /// the player away from the camera at the target speed (± 5 % after acceleration), with little sideways drift, and
    /// after the release it stops within the distance <see cref="Player.Deceleration"/> gives (+ 0.1 m). By default the
    /// input is <c>move_forward</c> (and <c>run</c> with <paramref name="run"/>); <paramref name="press"/> replaces it
    /// (a stick tilt, criterion 17), and <paramref name="strength"/> is the move vector's length that input gives, so
    /// the target is <paramref name="strength"/> × the walk or run speed (spec: speed = WalkSpeed × |move|).
    /// </summary>
    private static async Task AssertStraightRun(World world, Player player, FlatLine line, bool run, Action? press = null, float strength = 1f, string label = "")
    {
        await PlaceAndSettle(world, player, line.Start, line.Yaw);
        var camera = world.PlayerCamera!;
        var speed = (run ? player.RunSpeed : player.WalkSpeed) * strength;
        var holdFrames = Mathf.RoundToInt(2f * Engine.PhysicsTicksPerSecond);
        var dt = 1f / Engine.PhysicsTicksPerSecond;
        var steadyFrom = Mathf.CeilToInt(speed / player.Acceleration / dt) + 1;
        Vector3? steadyStart = null;
        var steadyEnd = Vector3.Zero;
        var releasePosition = Vector3.Zero;
        var stopPosition = Vector3.Zero;
        var stopped = false;
        var stopFrames = Mathf.CeilToInt(speed / player.Deceleration / dt) + StopMarginFrames;
        Vector3 cameraAtStart = default;
        await Drive(world, frame =>
        {
            var position = player.GlobalPosition;
            if (frame == 0)
            {
                cameraAtStart = camera.Camera.GlobalPosition;
                if (press is not null)
                {
                    press();
                }
                else
                {
                    Input.ActionPress(Player.MoveForwardAction);
                    if (run)
                    {
                        Input.ActionPress(Player.RunAction);
                    }
                }
            }
            else if (frame == steadyFrom)
            {
                steadyStart = position;
            }
            else if (frame == holdFrames)
            {
                steadyEnd = position;
                releasePosition = position;
                ReleaseAll();
            }
            else if (frame > holdFrames)
            {
                stopPosition = position;
                if (Horizontal(player.Velocity).Length() < StoppedSpeed)
                {
                    stopped = true;
                    return true;
                }
            }

            return false;
        }, holdFrames + stopFrames);

        var seconds = (holdFrames - steadyFrom) * dt;
        var moved = Horizontal(steadyEnd - steadyStart!.Value);
        var awayFromCamera = Horizontal(line.Start - cameraAtStart).Normalized();
        var along = moved.Dot(awayFromCamera) / seconds;
        var across = Mathf.Abs(moved.Dot(awayFromCamera.Cross(Vector3.Up))) / seconds;
        var stopDistance = HorizontalDistance(stopPosition, releasePosition);
        var allowedStop = (speed * speed / (2f * player.Deceleration)) + SpecDistanceTolerance;
        GD.Print(Invariant(
            $"PlayerOnFootTest: {(label == "" ? "Ac10" : label)} {(run ? "run" : "walk")} on a line with max slope {Mathf.RadToDeg(line.MaxSlope):0.##}°: {along:0.####} m/s away from the camera (target {speed}), {across:0.####} m/s across; stopped in {stopDistance:0.####} m (allowed {allowedStop:0.####})"));
        AssertFloat(along).IsBetween(speed * (1f - SpecSpeedTolerance), speed * (1f + SpecSpeedTolerance));
        AssertFloat(across).IsLessEqual(speed * SpecSpeedTolerance);
        AssertBool(stopped).OverrideFailureMessage("the player didn't stop after the release").IsTrue();
        AssertFloat(stopDistance).IsLessEqual(allowedStop);
    }

    // ===== Helpers: derived places =====

    /// <summary>A straight line on open ground: its start (on the terrain), horizontal direction and steepest slope.</summary>
    private sealed record FlatLine(Vector3 Start, Vector3 Direction, float Length, float MaxSlope)
    {
        public float Yaw => YawOf(Direction);

        public Vector3 At(float distance) => Start + (Direction * distance);

        public float Along(Vector3 point) => Horizontal(point - Start).Dot(Direction);
    }

    /// <summary>
    /// The flattest straight line on the spawn area's open ground long enough for criteria 10 to 12 and 18: from
    /// starts on a grid over the open ground, in 8 directions, every sample of the line and of two parallel lines
    /// <see cref="LateralClearance"/> to each side is on open ground, and the first body under each centre sample is
    /// terrain. The flattest is the one whose steepest 1 m step is smallest.
    /// </summary>
    private static FlatLine FindFlatLine(World world, Player player)
    {
        var openGround = world.MapLoader.Boundary!.SpawnArea.OpenGround;
        // The longest of the straight runs on it, plus a capsule radius so the body's front stays over the line:
        // criterion 10 holds input 2 s from rest (under 2 s × RunSpeed) and then stops; criterion 12 runs up, jumps
        // (2 × RunSpeed × JumpSpeed / g on flat ground: rise and fall take the same time) and then stops. A stop from
        // v under the per-frame limit covers less than the continuous v²/(2·Deceleration), since each frame moves
        // at the already lowered speed.
        var stop = StopDistance(player.RunSpeed, player.Deceleration);
        var length = Mathf.Max(
            (2f * player.RunSpeed) + stop,
            RunUpDistance(player.RunSpeed, player.Acceleration) + (2f * player.RunSpeed * player.JumpSpeed / player.Gravity) + stop)
            + player.CapsuleRadius;
        var minX = openGround.Min(p => p.Exterior.Min(q => q.X));
        var maxX = openGround.Max(p => p.Exterior.Max(q => q.X));
        var minZ = openGround.Min(p => p.Exterior.Min(q => q.Z));
        var maxZ = openGround.Max(p => p.Exterior.Max(q => q.Z));
        const float grid = 3f;
        const int directions = 8;
        const float heightStep = 1f;
        FlatLine? best = null;
        var lines = 0;
        for (var x = minX; x <= maxX; x += grid)
        {
            for (var z = minZ; z <= maxZ; z += grid)
            {
                for (var k = 0; k < directions; k++)
                {
                    var direction = new Vector3(Mathf.Cos(k * Mathf.Tau / directions), 0f, Mathf.Sin(k * Mathf.Tau / directions));
                    var side = direction.Cross(Vector3.Up);
                    var start = new Vector3((float)x, 0f, (float)z);
                    var open = true;
                    for (var s = 0f; s <= length && open; s += SampleStep)
                    {
                        foreach (var offset in new[] { -LateralClearance, 0f, LateralClearance })
                        {
                            var p = start + (direction * s) + (side * offset);
                            if (!SpawnPicker.Contains(openGround, p.X, p.Z))
                            {
                                open = false;
                                break;
                            }
                        }
                    }

                    if (!open)
                    {
                        continue;
                    }

                    lines++;
                    var maxSlope = 0f;
                    float? previous = null;
                    Vector3 first = default;
                    for (var s = 0f; s <= length + 1e-3f; s += heightStep)
                    {
                        var p = start + (direction * s);
                        if (FirstHit(world, p.X, p.Z, RayTop, -RayTop) is not { } hit || !hit.Body.IsInGroup(MapLoader.TerrainGroup))
                        {
                            maxSlope = float.MaxValue;
                            break;
                        }

                        if (previous is { } h)
                        {
                            maxSlope = Mathf.Max(maxSlope, Mathf.Atan(Mathf.Abs(hit.Position.Y - h) / heightStep));
                        }
                        else
                        {
                            first = hit.Position;
                        }

                        previous = hit.Position.Y;
                        if (best is not null && maxSlope >= best.MaxSlope)
                        {
                            break;
                        }
                    }

                    if (best is null || maxSlope < best.MaxSlope)
                    {
                        best = new FlatLine(first, direction, length, maxSlope);
                    }
                }
            }
        }

        AssertBool(best is not null).OverrideFailureMessage(Invariant($"no straight {length:0.#} m line with {LateralClearance} m clearance on the open ground")).IsTrue();
        GD.Print(Invariant(
            $"PlayerOnFootTest: flattest of {lines} open-ground lines of {length:0.##} m starts at {best!.Start}, direction {best.Direction}, max slope {Mathf.RadToDeg(best.MaxSlope):0.##}°"));
        AssertFloat(best.MaxSlope).IsLess(player.FloorMaxAngle);
        return best;
    }

    /// <summary>Open ground kept on each side of a flat line: room beside it for the capsule (criteria 10 to 12).</summary>
    private const float LateralClearance = 1.5f;

    /// <summary>Criterion 18: the time of one full turn of the input direction.</summary>
    private const float CircleSeconds = 3f;

    /// <summary>
    /// Criterion 18: how far from the ideal centre c the player's centre may get on the circle of radius
    /// <paramref name="r"/>. The motion moves the velocity toward the target W·d(θ) by at most Acceleration·dt per
    /// frame; the target turns by W·ω·dt per frame (2.8 m/s × 2π/3 s / 60 ≈ 0.1 m/s), less than that limit (20 m/s² /
    /// 60 ≈ 0.33 m/s), so once the velocity has caught up it equals the target every frame. Up to then it falls short
    /// by a deficit δ = ∫(target − velocity) dt, at most the ramp from rest,
    /// <see cref="RunUpDistance"/>(W, Acceleration) = W²/2a + W·dt, so afterwards the path is the ideal circle moved by
    /// δ, within r + |δ| of c (not inside r: the far side lies |δ| beyond). Each frame moving at the frame's start
    /// direction instead of turning continuously moves the polygon from the circle by at most W·dt more. On a slope θ
    /// the body slides along the floor, which can shorten a frame's horizontal step to cos²θ of it, so over the
    /// W·T path the centre shifts by at most W·T·sin²θ.
    /// </summary>
    private static float CirclePathBound(Player player, float r, float maxSlope)
    {
        var dt = 1f / Engine.PhysicsTicksPerSecond;
        var w = player.WalkSpeed;
        var sin = Mathf.Sin(maxSlope);
        return r + RunUpDistance(w, player.Acceleration) + (w * dt) + (w * CircleSeconds * sin * sin);
    }

    /// <summary>A disc of open ground: its centre (y 0), radius and the steepest slope between its samples.</summary>
    private sealed record OpenDisc(Vector3 Centre, float Radius, float MaxSlope);

    /// <summary>
    /// The flattest disc of radius <paramref name="radius"/> on the spawn area's open ground: centres on a grid of
    /// <see cref="SampleStep"/> over the open ground's bounds; samples at the centre and on rings
    /// <see cref="SampleStep"/> or less apart out to <paramref name="radius"/> (the outermost exactly at it), each with
    /// spokes at most <see cref="SampleStep"/> apart on the outer ring. Every sample must be inside
    /// <c>spawn_area.open_ground</c> (<see cref="SpawnPicker.Contains"/>), the first body under it must be terrain, and
    /// the steepest slope between neighbouring samples (along a ring and between rings) is the disc's; it must be under
    /// <see cref="Player.FloorMaxAngle"/>.
    /// </summary>
    private static OpenDisc FindOpenDisc(World world, Player player, float radius)
    {
        var openGround = world.MapLoader.Boundary!.SpawnArea.OpenGround;
        var rings = Mathf.CeilToInt(radius / SampleStep);
        var spokes = Mathf.CeilToInt(Mathf.Tau * radius / SampleStep);
        var offsets = new Vector2[rings + 1, spokes];
        for (var k = 0; k <= rings; k++)
        {
            for (var j = 0; j < spokes; j++)
            {
                var angle = Mathf.Tau * j / spokes;
                offsets[k, j] = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * (radius * k / rings);
            }
        }

        var minX = openGround.Min(p => p.Exterior.Min(q => q.X));
        var maxX = openGround.Max(p => p.Exterior.Max(q => q.X));
        var minZ = openGround.Min(p => p.Exterior.Min(q => q.Z));
        var maxZ = openGround.Max(p => p.Exterior.Max(q => q.Z));
        OpenDisc? best = null;
        var discs = 0;
        var heights = new float[rings + 1, spokes];
        for (var x = minX + radius; x <= maxX - radius; x += SampleStep)
        {
            for (var z = minZ + radius; z <= maxZ - radius; z += SampleStep)
            {
                var centre = new Vector2((float)x, (float)z);
                var open = true;
                // Outermost ring first: it fails soonest.
                for (var k = rings; k >= 0 && open; k--)
                {
                    for (var j = 0; j < (k == 0 ? 1 : spokes) && open; j++)
                    {
                        var p = centre + offsets[k, j];
                        open = SpawnPicker.Contains(openGround, p.X, p.Y);
                    }
                }

                if (!open)
                {
                    continue;
                }

                discs++;
                var maxSlope = 0f;
                for (var k = 0; k <= rings && maxSlope < (best?.MaxSlope ?? float.MaxValue); k++)
                {
                    for (var j = 0; j < spokes; j++)
                    {
                        var p = centre + offsets[k, j];
                        if (k == 0 && j > 0)
                        {
                            heights[0, j] = heights[0, 0];
                            continue;
                        }

                        if (FirstHit(world, p.X, p.Y, RayTop, -RayTop) is not { } hit || !hit.Body.IsInGroup(MapLoader.TerrainGroup))
                        {
                            maxSlope = float.MaxValue;
                            break;
                        }

                        heights[k, j] = hit.Position.Y;
                        if (k > 0)
                        {
                            maxSlope = Mathf.Max(maxSlope, Slope(heights[k, j], heights[k - 1, j], (offsets[k, j] - offsets[k - 1, j]).Length()));
                            if (j > 0)
                            {
                                maxSlope = Mathf.Max(maxSlope, Slope(heights[k, j], heights[k, j - 1], (offsets[k, j] - offsets[k, j - 1]).Length()));
                            }

                            if (j == spokes - 1)
                            {
                                maxSlope = Mathf.Max(maxSlope, Slope(heights[k, j], heights[k, 0], (offsets[k, j] - offsets[k, 0]).Length()));
                            }
                        }
                    }
                }

                if (best is null || maxSlope < best.MaxSlope)
                {
                    best = new OpenDisc(new Vector3(centre.X, 0f, centre.Y), radius, maxSlope);
                }
            }
        }

        AssertBool(best is not null).OverrideFailureMessage(Invariant($"no {radius:0.###} m disc of the open ground has terrain under every sample")).IsTrue();
        GD.Print(Invariant(
            $"PlayerOnFootTest: flattest of {discs} open-ground discs of {radius:0.###} m is at {best!.Centre}, max slope {Mathf.RadToDeg(best.MaxSlope):0.##}°"));
        AssertFloat(best.MaxSlope).IsLess(player.FloorMaxAngle);
        return best;
    }

    private static float Slope(float heightA, float heightB, float horizontal) => Mathf.Atan(Mathf.Abs(heightA - heightB) / horizontal);

    /// <summary>
    /// The terrain-road-terrain route of criterion 13 beside the ground drivable edge nearest the spawn area: A on the
    /// terrain beside the road (half the width, a capsule radius and the distance to cruise speed out), B on the centre line 10 m on,
    /// C on the terrain again 20 m on, on the same side. Every sample along A-B-C and of the two capsule-edge lines
    /// beside it has only road or terrain as its first body, A and C have terrain and B has road.
    /// </summary>
    private static (string EdgeId, List<Vector3> Points)? FindRoadRoute(World world, Player player)
    {
        var graph = world.MapLoader.RoadGraph!;
        var centre = SpawnCentre(world);
        var edges = graph.Edges
            .Where(e => e.Drivable && !e.Bridge && !e.Tunnel && PolylineLength(e.Points) >= RoadRouteLength)
            .OrderBy(e => DistanceToPolyline(centre, e.Points))
            .Take(30);
        foreach (var edge in edges)
        {
            var total = PolylineLength(edge.Points);
            // The capsule at A (and C) stands wholly off the road (half the width plus its radius from the centre
            // line), and the player reaches its cruise speed before the capsule can touch the road: FollowRoute
            // measures the speed ("never stops") from frame cruiseFrom on, by which the player has covered at most
            // the ramp to WalkSpeed plus one frame (RunUpDistance + W·dt). That margin is taken straight across
            // although the walk to B is diagonal, so along the walk it is longer. RoadEdgeClearOfCapsule checks the
            // road-side edge of the capsule against the actual mesh along that stretch.
            var cruise = RunUpDistance(player.WalkSpeed, player.Acceleration) + (player.WalkSpeed / Engine.PhysicsTicksPerSecond);
            var outward = ((float)edge.WidthM / 2f) + player.CapsuleRadius + cruise;
            for (var s0 = 0.0; s0 + RoadRouteLength <= total; s0 += 2.0)
            {
                foreach (var sideSign in new[] { 1f, -1f })
                {
                    var a = Offset(edge.Points, s0, sideSign * outward);
                    var b = Offset(edge.Points, s0 + (RoadRouteLength / 2f), 0f);
                    var c = Offset(edge.Points, s0 + RoadRouteLength, sideSign * outward);
                    var points = new List<Vector3>();
                    var ok = true;
                    foreach (var (p, group) in new[] { (a, MapLoader.TerrainGroup), (b, MapLoader.RoadGroup), (c, MapLoader.TerrainGroup) })
                    {
                        if (FirstHit(world, p.X, p.Z, RayTop, -RayTop) is not { } hit || !hit.Body.IsInGroup(group) || hit.Body.GetParent()?.Name == (StringName)MapLoader.PathNodeName)
                        {
                            ok = false;
                            break;
                        }

                        points.Add(hit.Position);
                    }

                    if (!ok
                        || !RoadEdgeClearOfCapsule(world, points[0], points[1], Offset(edge.Points, s0, 0f), player.CapsuleRadius, cruise)
                        || !RoadEdgeClearOfCapsule(world, points[2], points[1], Offset(edge.Points, s0 + RoadRouteLength, 0f), player.CapsuleRadius, cruise)
                        || !RouteOnRoadOrTerrain(world, points, player.CapsuleRadius + 0.1f)
                        || MaxRouteSlope(world, points) >= player.FloorMaxAngle)
                    {
                        continue;
                    }

                    return (edge.Id, points);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Whether, over the first <paramref name="distance"/> of the walk from <paramref name="start"/> toward
    /// <paramref name="toward"/>, the capsule's edge nearest the road (its centre moved <paramref name="radius"/> toward
    /// <paramref name="roadCentre"/>, the centre line point across from the start) has terrain as its first body.
    /// </summary>
    private static bool RoadEdgeClearOfCapsule(World world, Vector3 start, Vector3 toward, Vector3 roadCentre, float radius, float distance)
    {
        var direction = Horizontal(toward - start).Normalized();
        var inward = Horizontal(roadCentre - start).Normalized();
        for (var s = 0f; ; s = Mathf.Min(s + (SampleStep / 2f), distance))
        {
            var p = start + (direction * s) + (inward * radius);
            if (FirstHit(world, p.X, p.Z, RayTop, -RayTop) is not { } hit || !hit.Body.IsInGroup(MapLoader.TerrainGroup))
            {
                return false;
            }

            if (s >= distance)
            {
                return true;
            }
        }
    }

    /// <summary>Whether every sample along the route, and of the lines <paramref name="halfWidth"/> to its sides, has road or terrain as its first body.</summary>
    private static bool RouteOnRoadOrTerrain(World world, List<Vector3> points, float halfWidth)
    {
        for (var i = 1; i < points.Count; i++)
        {
            var a = points[i - 1];
            var b = points[i];
            var direction = Horizontal(b - a).Normalized();
            var side = direction.Cross(Vector3.Up);
            var length = HorizontalDistance(a, b);
            for (var s = 0f; s <= length; s += SampleStep / 2f)
            {
                foreach (var offset in new[] { -halfWidth, 0f, halfWidth })
                {
                    var p = a + (direction * s) + (side * offset);
                    if (FirstHit(world, p.X, p.Z, RayTop, -RayTop) is not { } hit
                        || !(hit.Body.IsInGroup(MapLoader.RoadGroup) || hit.Body.IsInGroup(MapLoader.TerrainGroup))
                        || hit.Body.GetParent()?.Name.ToString().StartsWith(MapLoader.BridgeNodePrefix) == true)
                    {
                        return false;
                    }
                }
            }
        }

        return true;
    }

    /// <summary>The steepest slope (radians) between surface samples <see cref="SampleStep"/> apart along the route.</summary>
    private static float MaxRouteSlope(World world, List<Vector3> points)
    {
        var steepest = 0f;
        for (var i = 1; i < points.Count; i++)
        {
            var a = points[i - 1];
            var b = points[i];
            var direction = Horizontal(b - a).Normalized();
            var length = HorizontalDistance(a, b);
            float? previous = null;
            for (var s = 0f; s <= length; s += SampleStep)
            {
                // The first body from the top, as RouteOnRoadOrTerrain checked it is road or terrain: no window.
                var p = a + (direction * s);
                if (FirstHit(world, p.X, p.Z, RayTop, -RayTop) is not { } hit)
                {
                    return float.MaxValue;
                }

                if (previous is { } h)
                {
                    steepest = Mathf.Max(steepest, Mathf.Atan(Mathf.Abs(hit.Position.Y - h) / SampleStep));
                }

                previous = hit.Position.Y;
            }
        }

        return steepest;
    }

    /// <summary>
    /// A wall of the building nearest the spawn area that the player can reach over road or terrain: the largest
    /// vertical face of one of its collision shapes, its outward normal, and a start point <paramref name="distance"/>
    /// out from it on the ground. Horizontal rays from the start toward the wall, at the capsule's middle and at both
    /// sides of the capsule, hit this building first.
    /// </summary>
    private static (string Building, Vector3 Point, Vector3 Normal, Vector3 Start)? FindWall(World world, Player player, float distance)
    {
        var loader = world.MapLoader;
        var centre = SpawnCentre(world);
        var space = world.GetWorld3D().DirectSpaceState;
        var buildings = loader.Places!.Buildings.Items
            .Select(b => (Building: b, Centre: BuildingCentre(loader, b)))
            .Where(b => b.Centre is not null)
            .OrderBy(b => HorizontalDistance(b.Centre!.Value, centre))
            .Select(b => b.Building)
            .Take(40);
        foreach (var building in buildings)
        {
            var tile = loader.Tiles.GetNodeOrNull(building.Tile);
            foreach (var nodeName in building.CollisionNodes)
            {
                if (tile?.GetNodeOrNull<StaticBody3D>(nodeName) is not { } body)
                {
                    continue;
                }

                foreach (var shape in body.GetChildren().OfType<CollisionShape3D>())
                {
                    foreach (var (point, normal) in WallFaces(shape))
                    {
                        var startXz = point + (normal * distance);
                        if (FirstHit(world, startXz.X, startXz.Z, RayTop, -RayTop) is not { } ground
                            || !(ground.Body.IsInGroup(MapLoader.TerrainGroup) || ground.Body.IsInGroup(MapLoader.RoadGroup)))
                        {
                            continue;
                        }

                        var start = ground.Position;
                        var side = normal.Cross(Vector3.Up);
                        var reaches = true;
                        foreach (var height in new[] { 0.3f, player.CapsuleHeight / 2f, player.CapsuleHeight - 0.1f })
                        {
                            foreach (var offset in new[] { -player.CapsuleRadius, 0f, player.CapsuleRadius })
                            {
                                var from = start + (Vector3.Up * height) + (side * offset);
                                // The face's plane is exactly `distance` away along the ray (the offsets are along
                                // the face, the face is vertical). The ray ends a safe margin past it, enough for
                                // float rounding, so it hits only this face, not one set further back.
                                var to = from - (normal * (distance + player.SafeMargin));
                                var hit = space.IntersectRay(PhysicsRayQueryParameters3D.Create(from, to, CollisionLayers.Map));
                                if (hit.Count == 0 || hit["collider"].AsGodotObject() != body)
                                {
                                    reaches = false;
                                    break;
                                }
                            }

                            if (!reaches)
                            {
                                break;
                            }
                        }

                        if (reaches)
                        {
                            return (building.Id, new Vector3(point.X, start.Y, point.Z), normal, start);
                        }
                    }
                }
            }
        }

        return null;
    }

    /// <summary>The vertical triangles of a collision shape, largest first: centroid and outward horizontal normal.</summary>
    private static IEnumerable<(Vector3 Point, Vector3 Normal)> WallFaces(CollisionShape3D shape)
    {
        if (shape.Shape?.GetDebugMesh() is not { } mesh)
        {
            yield break;
        }

        var faces = mesh.GetFaces();
        var vertices = faces.Select(v => shape.GlobalTransform * v).ToList();
        if (vertices.Count == 0)
        {
            yield break;
        }

        var middle = vertices.Aggregate(Vector3.Zero, (sum, v) => sum + v) / vertices.Count;
        var walls = new List<(float Area, Vector3 Point, Vector3 Normal)>();
        for (var k = 0; k + 2 < vertices.Count; k += 3)
        {
            var (a, b, c) = (vertices[k], vertices[k + 1], vertices[k + 2]);
            var cross = (b - a).Cross(c - a);
            if (cross.Length() < 1e-4f || Mathf.Abs(cross.Normalized().Y) > 0.01f)
            {
                continue;
            }

            var normal = Horizontal(cross).Normalized();
            var centroid = (a + b + c) / 3f;
            if (normal.Dot(centroid - middle) < 0f)
            {
                normal = -normal;
            }

            walls.Add((cross.Length() / 2f, centroid, normal));
        }

        foreach (var wall in walls.OrderByDescending(w => w.Area))
        {
            yield return (wall.Point, wall.Normal);
        }
    }

    /// <summary>
    /// The centre of the bounding box of the building's collision shapes in map coordinates (the shapes' vertices
    /// are in map coordinates, so the nodes' own positions don't tell), or null without a resolved body.
    /// </summary>
    private static Vector3? BuildingCentre(MapLoader loader, Building building)
    {
        var tile = loader.Tiles.GetNodeOrNull(building.Tile);
        Aabb? box = null;
        foreach (var body in building.CollisionNodes.Select(n => tile?.GetNodeOrNull<StaticBody3D>(n)).OfType<StaticBody3D>())
        {
            foreach (var shape in body.GetChildren().OfType<CollisionShape3D>())
            {
                if (shape.Shape?.GetDebugMesh() is { } mesh)
                {
                    var aabb = shape.GlobalTransform * mesh.GetAabb();
                    box = box is { } b ? b.Merge(aabb) : aabb;
                }
            }
        }

        return box?.GetCenter();
    }

    /// <summary>
    /// A bridge deck with a railing, nearest the spawn area first: the middle of a bridge edge on its deck surface,
    /// the horizontal direction across it, and the distance from the centre line to the railing (the first hit of a
    /// horizontal ray 0.5 m above the deck, on the same bridge body).
    /// </summary>
    private static (long Way, Vector3 Centre, Vector3 Side, float Railing)? FindBridgeDeck(World world)
    {
        var graph = world.MapLoader.RoadGraph!;
        var centre = SpawnCentre(world);
        var space = world.GetWorld3D().DirectSpaceState;
        foreach (var edge in graph.Edges.Where(e => e.Bridge).OrderBy(e => DistanceToPolyline(centre, e.Points)))
        {
            var length = PolylineLength(edge.Points);
            var middle = PolylineAt(edge.Points, length / 2);
            // The deck is the first body from the top at the edge's middle; anything above it means the next edge.
            if (FirstHit(world, middle.X, middle.Z, RayTop, -RayTop) is not { } deck || !IsBridgeBody(deck.Body))
            {
                continue;
            }

            var tangent = Horizontal(PolylineAt(edge.Points, (length / 2) + 0.5) - PolylineAt(edge.Points, (length / 2) - 0.5)).Normalized();
            foreach (var side in new[] { tangent.Cross(Vector3.Up), Vector3.Up.Cross(tangent) })
            {
                var from = deck.Position + (Vector3.Up * 0.5f);
                var hit = space.IntersectRay(PhysicsRayQueryParameters3D.Create(from, from + (side * ((float)edge.WidthM + 5f)), CollisionLayers.Map));
                if (hit.Count > 0 && hit["collider"].AsGodotObject() == deck.Body)
                {
                    return (edge.OsmWay, deck.Position, side, Horizontal(hit["position"].AsVector3() - deck.Position).Length());
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The criterion 15 route over the bridge nearest the spawn area: out along the road edge at a <c>span_end</c>
    /// node (up to twice the ramp length), the node, the chained bridge edges' points to where the bridge ends, and on
    /// along a road edge there. Each sample of it has a road or bridge surface as its first body from the top
    /// (<see cref="SurfacePoints"/>). Points are on the surface.
    /// </summary>
    private static (string SpanEnd, List<Vector3> Points, Vector3 DeckFrom, Vector3 DeckTo, float DeckWidth)? FindRampRoute(World world)
    {
        var graph = world.MapLoader.RoadGraph!;
        var centre = SpawnCentre(world);
        var spanEnds = graph.Nodes.Where(n => n.Kind == RoadNodeKind.SpanEnd)
            .OrderBy(n => Horizontal(ToVector(n.Position)).DistanceTo(centre));
        foreach (var node in spanEnds)
        {
            var edges = graph.EdgesAt(node.Id);
            var road = edges.FirstOrDefault(e => !e.Bridge && !e.Tunnel);
            var bridge = edges.FirstOrDefault(e => e.Bridge);
            if (road is null || bridge is null)
            {
                continue;
            }

            var points = new List<LocalPoint>();
            points.AddRange(PointsFrom(road, road.OtherEnd(node.Id), node.Id, 2 * RampLength));
            var current = node.Id;
            var edge = bridge;
            var used = new HashSet<string>();
            while (edge is not null && used.Add(edge.Id))
            {
                var forward = edge.From == current;
                var chain = forward ? edge.Points : edge.Points.Reverse().ToList();
                points.AddRange(points.Count > 0 && chain[0] == points[^1] ? chain.Skip(1) : chain);
                current = edge.OtherEnd(current);
                edge = graph.EdgesAt(current).FirstOrDefault(e => e.Bridge && !used.Contains(e.Id));
            }

            var deckFrom = ToVector(node.Position);
            var deckTo = ToVector(graph.Node(current).Position);
            var beyond = graph.EdgesAt(current).FirstOrDefault(e => !e.Bridge && !e.Tunnel && !used.Contains(e.Id));
            if (beyond is not null)
            {
                var onward = PointsFrom(beyond, current, beyond.OtherEnd(current), 2 * RampLength, fromStart: true);
                points.AddRange(onward.Skip(1));
            }

            var surface = SurfacePoints(world, points);
            if (surface is not null)
            {
                var deckWidth = (float)graph.Edges.Where(e => used.Contains(e.Id)).Min(e => e.WidthM);
                return (node.Id, surface, deckFrom, deckTo, deckWidth);
            }
        }

        return null;
    }

    /// <summary>
    /// The points of <paramref name="edge"/> from <paramref name="fromNode"/> to <paramref name="toNode"/>, cut to the
    /// last <paramref name="maxLength"/> metres before <paramref name="toNode"/>, or with <paramref name="fromStart"/>
    /// the first <paramref name="maxLength"/> metres after <paramref name="fromNode"/>.
    /// </summary>
    private static List<LocalPoint> PointsFrom(RoadEdge edge, string fromNode, string toNode, double maxLength, bool fromStart = false)
    {
        var points = edge.From == fromNode ? edge.Points.ToList() : edge.Points.Reverse().ToList();
        if (!fromStart)
        {
            points.Reverse();
        }

        var cut = new List<LocalPoint> { points[0] };
        var travelled = 0.0;
        for (var i = 1; i < points.Count && travelled < maxLength; i++)
        {
            var step = HorizontalDistance(ToVector(points[i - 1]), ToVector(points[i]));
            if (travelled + step > maxLength)
            {
                var t = (maxLength - travelled) / step;
                cut.Add(Lerp(points[i - 1], points[i], t));
                break;
            }

            travelled += step;
            cut.Add(points[i]);
        }

        if (!fromStart)
        {
            cut.Reverse();
        }

        return cut;
    }

    /// <summary>
    /// Each point moved onto the road or bridge surface at its x/z, or null when the first body from the top at a
    /// sample along the route isn't a road, path or deck (<see cref="MapLoader.RoadGroup"/>). Taking the first body
    /// from the top needs no height window around the graph point.
    /// </summary>
    private static List<Vector3>? SurfacePoints(World world, List<LocalPoint> points)
    {
        for (var i = 1; i < points.Count; i++)
        {
            var a = ToVector(points[i - 1]);
            var b = ToVector(points[i]);
            var length = HorizontalDistance(a, b);
            for (var s = 0f; s <= length; s += SampleStep)
            {
                var p = a.Lerp(b, length > 0f ? s / length : 0f);
                if (FirstHit(world, p.X, p.Z, RayTop, -RayTop) is not { } hit || !hit.Body.IsInGroup(MapLoader.RoadGroup))
                {
                    return null;
                }
            }
        }

        var surface = new List<Vector3>();
        foreach (var point in points)
        {
            var p = ToVector(point);
            surface.Add(FirstHit(world, p.X, p.Z, RayTop, -RayTop)!.Value.Position);
        }

        return surface;
    }

    // ===== Helpers: route following =====

    private sealed class RouteResult
    {
        public bool Finished { get; set; }

        public int LongestAirborne { get; set; }

        public int AirborneFrames { get; set; }

        public float MinCruiseSpeed { get; set; } = float.MaxValue;

        public float WorstBelowSurface { get; set; } = float.MinValue;

        public float MaxSlopeRadians { get; set; }

        public int SurfaceSwitches { get; set; }

        public List<Vector3> Positions { get; } = [];

        public float ClosestTo(Vector3 point) => Positions.Count == 0 ? float.MaxValue : Positions.Min(p => HorizontalDistance(p, point));
    }

    /// <summary>
    /// Places the player on the first point and walks (or runs) it through the rest, steering by setting the camera
    /// yaw toward the next point each physics frame with <c>move_forward</c> held. A point counts as reached when the
    /// player is past it along the segment. Records, after the first frames of acceleration: consecutive frames off
    /// the floor, the horizontal speed per frame, how far the feet are below the first road or terrain surface under
    /// them, and how often that surface switches between road and terrain.
    /// </summary>
    private static async Task<RouteResult> FollowRoute(World world, Player player, List<Vector3> points, bool run)
    {
        var camera = world.PlayerCamera!;
        var first = Horizontal(points[1] - points[0]);
        await PlaceAndSettle(world, player, points[0], YawOf(first));
        var result = new RouteResult();
        var speed = run ? player.RunSpeed : player.WalkSpeed;
        var dt = 1f / Engine.PhysicsTicksPerSecond;
        var cruiseFrom = Mathf.CeilToInt(speed / player.Acceleration / dt) + 1;
        var total = 0f;
        for (var i = 1; i < points.Count; i++)
        {
            total += HorizontalDistance(points[i - 1], points[i]);
        }

        var maxFrames = Mathf.CeilToInt((total / speed * 1.5f) + 2f) * Engine.PhysicsTicksPerSecond;
        var target = 1;
        var airborne = 0;
        var last = player.GlobalPosition;
        bool? onRoad = null;
        var space = world.GetWorld3D().DirectSpaceState;
        await Drive(world, frame =>
        {
            var position = player.GlobalPosition;
            result.Positions.Add(position);
            if (frame > 0)
            {
                airborne = player.IsOnFloor() ? 0 : airborne + 1;
                result.LongestAirborne = Math.Max(result.LongestAirborne, airborne);
                result.AirborneFrames += player.IsOnFloor() ? 0 : 1;
                var surface = space.IntersectRay(PhysicsRayQueryParameters3D.Create(position + Vector3.Up, position + (Vector3.Down * 3f), CollisionLayers.Map));
                if (surface.Count > 0 && surface["collider"].AsGodotObject() is Node body)
                {
                    result.WorstBelowSurface = Mathf.Max(result.WorstBelowSurface, surface["position"].AsVector3().Y - position.Y);
                    var road = body.IsInGroup(MapLoader.RoadGroup);
                    if (onRoad is { } was && was != road)
                    {
                        result.SurfaceSwitches++;
                    }

                    onRoad = road;
                }
            }

            if (frame >= cruiseFrom)
            {
                result.MinCruiseSpeed = Mathf.Min(result.MinCruiseSpeed, HorizontalDistance(position, last) / dt);
            }

            last = position;
            while (target < points.Count && Horizontal(position - points[target]).Dot(Horizontal(points[target] - points[target - 1])) >= 0f)
            {
                target++;
            }

            if (target >= points.Count)
            {
                result.Finished = true;
                ReleaseAll();
                return true;
            }

            camera.Yaw = YawOf(Horizontal(points[target] - position));
            Input.ActionPress(Player.MoveForwardAction);
            if (run)
            {
                Input.ActionPress(Player.RunAction);
            }

            return false;
        }, maxFrames);
        ReleaseAll();
        result.MaxSlopeRadians = MaxRouteSlope(world, points);
        return result;
    }

    /// <summary>
    /// The lowest horizontal speed allowed while following <paramref name="points"/> at <paramref name="speed"/>:
    /// a turn of φ at a point takes the acceleration-limited velocity through v·cos(φ/2), and a slope θ cuts the
    /// horizontal speed to v·cos θ; 5 % below that (the spec's speed tolerance) counts as stopping.
    /// </summary>
    private static float MinimumCruiseSpeed(float speed, List<Vector3> points, float maxSlope)
    {
        var maxTurn = 0f;
        for (var i = 1; i < points.Count - 1; i++)
        {
            maxTurn = Mathf.Max(maxTurn, Horizontal(points[i] - points[i - 1]).AngleTo(Horizontal(points[i + 1] - points[i])));
        }

        return speed * Mathf.Cos(maxTurn / 2f) * Mathf.Cos(maxSlope) * (1f - SpecSpeedTolerance);
    }

    // ===== Helpers: spawn failure =====

    /// <summary>
    /// Loads the temporary pack <paramref name="mapId"/> and checks the spawn failure: the pack's other files loaded,
    /// no player, one spawn error naming the boundary path and <c>/regen-map</c>, and
    /// <see cref="World.LastSpawnFailure"/> naming the path and containing every one of <paramref name="reason"/>.
    /// Without a boundary (<paramref name="expectBoundary"/> false) the loader's own error names the path too, the
    /// spawn made no draw, and its reason never speaks of draws.
    /// </summary>
    private async Task AssertSpawnFailure(string mapId, int tiles, bool expectBoundary, params string[] reason)
    {
        _savedMapIdSetting = ProjectSettings.GetSetting(MapLoader.MapIdSetting, MapLoader.DefaultMapId);
        ProjectSettings.SetSetting(MapLoader.MapIdSetting, mapId);
        var capture = new ErrorCapture();
        OS.AddLogger(capture);
        World world;
        try
        {
            using var runner = ISceneRunner.Load(NewWorld(TestSeed), true);
            world = (World)runner.Scene()!;
            AssertThat(world.MapLoader.ResolvedMapId).IsEqual(mapId);
            await WaitUntil(world, () => capture.ErrorsOnly.Any(e => e.Contains("no player spawn")), world.SpawnPhysicsWaitFrames + 30);
            await PhysicsFrames(world, 5);

            var loader = world.MapLoader;
            AssertInt(loader.LoadedTileCount).IsEqual(tiles);
            AssertThat(loader.RoadGraph).OverrideFailureMessage("the road graph didn't load from the pack copy").IsNotNull();
            AssertThat(loader.Places).OverrideFailureMessage("the places didn't load from the pack copy").IsNotNull();
            AssertBool(loader.Boundary is not null).IsEqual(expectBoundary);
            AssertThat(world.LastSpawnFailure).IsNotNull();
            AssertThat(world.LastSpawnFailure!).Contains(loader.BoundaryPath);
            foreach (var part in reason)
            {
                AssertThat(world.LastSpawnFailure!).Contains(part);
            }

            if (!expectBoundary)
            {
                AssertInt(world.LastSpawnAttempts).IsEqual(0);
                AssertThat(world.LastSpawnFailure!).NotContains("draws");
            }

            AssertNoPlayer(world);
            await AssertDebugCameraKeyChangesNothing(world);
        }
        finally
        {
            OS.RemoveLogger(capture);
        }

        var path = $"res://maps/{mapId}/{MapPack.BoundaryFile}";
        var regen = $"/regen-map {mapId}";
        var loaderErrors = capture.ErrorsOnly.Where(e => e.Contains("MapLoader") && e.Contains(path)).ToList();
        AssertInt(loaderErrors.Count)
            .OverrideFailureMessage($"expected {(expectBoundary ? "no" : "one")} MapLoader error naming {path}; got: {string.Join(" | ", capture.ErrorsOnly)}")
            .IsEqual(expectBoundary ? 0 : 1);
        if (!expectBoundary)
        {
            AssertThat(loaderErrors[0]).Contains(regen);
        }

        var spawnErrors = capture.ErrorsOnly.Where(e => e.Contains("no player spawn")).ToList();
        AssertInt(spawnErrors.Count).OverrideFailureMessage($"no single spawn error; got: {string.Join(" | ", capture.ErrorsOnly)}").IsEqual(1);
        AssertThat(spawnErrors[0]).Contains(path).Contains(regen);
        foreach (var part in reason)
        {
            AssertThat(spawnErrors[0]).Contains(part);
        }
    }

    private static void AssertNoPlayer(World world)
    {
        AssertThat(world.Player).IsNull();
        AssertThat(world.PlayerCamera).IsNull();
        AssertInt(Descendants(world).OfType<Player>().Count()).IsEqual(0);
        AssertInt(Descendants(world).OfType<PlayerCamera>().Count()).IsEqual(0);
        AssertThat(world.Hud.Target).IsNull();
        AssertBool(DebugBuild.IsDebug).IsTrue();
        AssertBool(world.Camera!.Current).IsTrue();
        AssertThat(world.GetViewport().GetCamera3D()).IsSame(world.Camera);
    }

    /// <summary>
    /// Criterion 9: without a player, a press of each <c>debug_camera</c> key (injected as in the criterion 19 tests,
    /// where the same injection does toggle) and <see cref="World.ToggleDebugCamera"/> leave the debug camera current.
    /// </summary>
    private static async Task AssertDebugCameraKeyChangesNothing(World world)
    {
        var camera = world.Camera!;
        TapKey(Key.Quoteleft);
        await PhysicsFrames(world, 2);
        TapKey(Key.Section);
        await PhysicsFrames(world, 2);
        AssertBool(world.ToggleDebugCamera()).IsFalse();
        AssertBool(world.DebugCameraActive).IsFalse();
        AssertBool(camera.Current).OverrideFailureMessage("debug_camera changed the current camera without a player").IsTrue();
        AssertThat(world.GetViewport().GetCamera3D()).IsSame(camera);
        AssertThat(world.Player).IsNull();
    }

    /// <summary>
    /// Copies the Somero pack's manifest and tile import files to a temporary pack <paramref name="mapId"/>, with
    /// <c>road_graph.json</c>, <c>buildings.json</c> and <c>pois.json</c> (their <c>map_id</c> set to it), and
    /// <c>boundary.json</c> changed by <paramref name="boundary"/>, or left out when it is null. The import files keep
    /// pointing at the already imported tile scenes; their UIDs are dropped so they don't clash with the originals.
    /// </summary>
    private int CopyPack(string mapId, Action<JsonNode>? boundary)
    {
        var source = ProjectSettings.GlobalizePath($"res://maps/{MapId}");
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

    private static Boundary ReadPackBoundary()
    {
        var manifestPath = $"res://maps/{MapId}/{MapPack.ManifestFile}";
        var manifest = MapManifestReader.Parse(FileAccess.GetFileAsString(manifestPath), manifestPath);
        var path = $"res://maps/{MapId}/{MapPack.BoundaryFile}";
        return BoundaryReader.Parse(FileAccess.GetFileAsString(path), manifest, path);
    }

    /// <summary>
    /// How far a body may sink into another in Jolt (<c>physics/jolt_physics_3d/simulation/penetration_slop</c>):
    /// the feet that far under the surface are resting on it, not below it.
    /// </summary>
    private static float PenetrationSlop()
    {
        const string setting = "physics/jolt_physics_3d/simulation/penetration_slop";
        AssertBool(ProjectSettings.HasSetting(setting)).OverrideFailureMessage($"no project setting {setting}").IsTrue();
        return ProjectSettings.GetSetting(setting).AsSingle();
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

    // ===== Helpers: geometry =====

    private static (Vector3 Position, Node Body)? FirstHit(World world, float x, float z, float fromY, float toY)
    {
        var hit = world.GetWorld3D().DirectSpaceState.IntersectRay(
            PhysicsRayQueryParameters3D.Create(new Vector3(x, fromY, z), new Vector3(x, toY, z), CollisionLayers.Map));
        return hit.Count > 0 && hit["collider"].AsGodotObject() is Node body ? (hit["position"].AsVector3(), body) : null;
    }

    private static bool IsBridgeBody(Node body) =>
        body.GetParent() is MeshInstance3D mesh && mesh.Name.ToString().StartsWith(MapLoader.BridgeNodePrefix);

    /// <summary>The middle of the spawn area's polygon (the mean of its points), at y 0.</summary>
    private static Vector3 SpawnCentre(World world)
    {
        var polygon = world.MapLoader.Boundary!.SpawnArea.Polygon;
        return new Vector3((float)polygon.Average(p => p.X), 0f, (float)polygon.Average(p => p.Z));
    }

    private static float RampUpDistance(float speed, float acceleration) => speed * speed / (2f * acceleration);

    /// <summary>
    /// Distance from rest after which the player moves at <paramref name="speed"/> under full input on the floor:
    /// <see cref="RampUpDistance"/> plus one physics frame's travel at that speed. Derivation: each frame the motion
    /// raises the speed by a·dt (<see cref="OnFootMotion.Step"/>, acceleration-limited) and then
    /// <c>MoveAndSlide</c> moves the body at the new speed, so the speed first equals v on frame
    /// N = ⌈v / (a·dt)⌉ after Σ_{k=1}^{N−1} k·a·dt² = a·dt²·N(N−1)/2 of travel. Since N − 1 &lt; v / (a·dt), that is
    /// under v²/(2a) + v·dt/2, so v·dt more covers the difference between the stepped and the continuous ramp. The
    /// input is pressed in <see cref="SceneTree.PhysicsFrame"/>, before the player's <c>_PhysicsProcess</c> in the
    /// same frame, so there is no frame of input delay to add.
    /// </summary>
    private static float RunUpDistance(float speed, float acceleration) =>
        RampUpDistance(speed, acceleration) + (speed / Engine.PhysicsTicksPerSecond);

    private static float StopDistance(float speed, float deceleration) => speed * speed / (2f * deceleration);

    /// <summary>Godot's yaw of a horizontal direction: yaw 0 looks along −Z.</summary>
    private static float YawOf(Vector3 direction) => Mathf.Atan2(-direction.X, -direction.Z);

    private static Vector3 Horizontal(Vector3 v) => new(v.X, 0f, v.Z);

    private static float MaxAbs(Vector3 v) => Mathf.Max(Mathf.Abs(v.X), Mathf.Max(Mathf.Abs(v.Y), Mathf.Abs(v.Z)));

    private static float HorizontalDistance(Vector3 a, Vector3 b) => Horizontal(a - b).Length();

    private static Vector3 ToVector(LocalPoint p) => new((float)p.X, (float)p.Y, (float)p.Z);

    private static LocalPoint ToLocal(Vector3 p) => new(p.X, p.Y, p.Z);

    private static LocalPoint Lerp(LocalPoint a, LocalPoint b, double t) =>
        new(a.X + ((b.X - a.X) * t), a.Y + ((b.Y - a.Y) * t), a.Z + ((b.Z - a.Z) * t));

    private static double PolylineLength(IReadOnlyList<LocalPoint> points)
    {
        var length = 0.0;
        for (var i = 1; i < points.Count; i++)
        {
            length += HorizontalDistance(ToVector(points[i - 1]), ToVector(points[i]));
        }

        return length;
    }

    /// <summary>The point at horizontal distance <paramref name="s"/> along a polyline.</summary>
    private static Vector3 PolylineAt(IReadOnlyList<LocalPoint> points, double s)
    {
        var travelled = 0.0;
        for (var i = 1; i < points.Count; i++)
        {
            var step = HorizontalDistance(ToVector(points[i - 1]), ToVector(points[i]));
            if (travelled + step >= s && step > 0)
            {
                return ToVector(Lerp(points[i - 1], points[i], (s - travelled) / step));
            }

            travelled += step;
        }

        return ToVector(points[^1]);
    }

    /// <summary>The point at distance <paramref name="s"/> along the polyline, moved <paramref name="offset"/> to its right.</summary>
    private static Vector3 Offset(IReadOnlyList<LocalPoint> points, double s, float offset)
    {
        var at = PolylineAt(points, s);
        var tangent = Horizontal(PolylineAt(points, s + 0.5) - PolylineAt(points, Math.Max(0, s - 0.5))).Normalized();
        return at + (tangent.Cross(Vector3.Up) * offset);
    }

    private static float DistanceToPolyline(Vector3 point, IReadOnlyList<LocalPoint> points)
    {
        var best = float.MaxValue;
        var p = new Vector2(point.X, point.Z);
        for (var i = 1; i < points.Count; i++)
        {
            var a = new Vector2((float)points[i - 1].X, (float)points[i - 1].Z);
            var b = new Vector2((float)points[i].X, (float)points[i].Z);
            var ab = b - a;
            var t = ab.LengthSquared() > 0f ? Mathf.Clamp((p - a).Dot(ab) / ab.LengthSquared(), 0f, 1f) : 0f;
            best = Mathf.Min(best, p.DistanceTo(a + (ab * t)));
        }

        return best;
    }

    private static bool IsInsideTree(Node node) => GodotObject.IsInstanceValid(node) && node.IsInsideTree();

    /// <summary>Height of the placeholder model's cap above the feet; it scales with <see cref="Player.CapsuleHeight"/>.</summary>
    private static float TopOfModel(Node model) => model.GetNode<Node3D>("Cap").Position.Y;

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

    private static string Invariant(FormattableString text) => FormattableString.Invariant(text);

    private static void AssertPackPresent()
    {
        foreach (var file in new[] { MapPack.ManifestFile, MapPack.RoadGraphFile, MapPack.BuildingsFile, MapPack.PoisFile, MapPack.BoundaryFile })
        {
            var path = $"res://maps/{MapId}/{file}";
            AssertBool(FileAccess.FileExists(path)).OverrideFailureMessage($"no {file} at {path}; {MapPack.RegenHint(MapId)}").IsTrue();
        }
    }
}
