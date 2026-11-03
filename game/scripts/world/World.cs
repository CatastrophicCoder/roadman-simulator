using System;
using Godot;
using Roadman.Core.Maps;
using Roadman.Core.Player;
using Roadman.Core.Tools;
using Roadman.Debug;
using Roadman.Hud;
using Roadman.OnFoot;

namespace Roadman.Scenes;

/// <summary>
/// Root of the game world. Builds the environment, sun and map loader in code, loads the map, and places the debug
/// camera above the terrain at the origin. With <c>-- --screenshot &lt;preset&gt; &lt;out.png&gt;</c> on the command
/// line it adds a <see cref="ScreenshotTool"/> instead of placing the camera. Always adds the
/// <see cref="StreetLabelHud"/>. Debug builds (<see cref="DebugBuild.IsDebug"/>) also get the <see cref="DebugCamera"/>,
/// the <see cref="OrthophotoDebugView"/> (F2), the <see cref="RoadGraphDebugView"/> (G) and the
/// <see cref="PlaceInspector"/> (I); release builds have only the player camera.
/// <para>
/// Each time the map loads, the old <see cref="Player"/> is freed and, once the terrain collision is queryable, a new
/// one spawns at a random point of the pack's spawn area (<see cref="DrawSpawn"/>), with its
/// <see cref="PlayerCamera"/> current behind it and the street label following it. The draws are seeded from
/// <c>--seed &lt;n&gt;</c> or the time. In a screenshot run the player spawns hidden, its camera never becomes current
/// and the label keeps measuring from the capture camera. A failed spawn pushes an error and leaves the debug camera.
/// </para>
/// <para>
/// In debug builds <c>debug_camera</c> (the physical key left of 1) switches between the player camera and the debug
/// camera (<see cref="ToggleDebugCamera"/>). The debug camera starts at the player camera's transform; while it is
/// current the player gets no input and the player camera doesn't orbit. Switching back restores the player camera and
/// the mouse capture it had. Without a player the toggle does nothing.
/// </para>
/// </summary>
public partial class World : Node3D
{
    [Signal]
    public delegate void CameraPlacedEventHandler(Vector3 position);

    /// <summary>The player was placed at <paramref name="position"/> (its feet) after a map load.</summary>
    [Signal]
    public delegate void PlayerSpawnedEventHandler(Vector3 position);

    /// <summary>The temporary fall respawn put the player back at <paramref name="position"/>.</summary>
    [Signal]
    public delegate void PlayerRespawnedEventHandler(Vector3 position);

    private const int MaxSpawnShapeHits = 32;

    /// <summary>Toggles between the player camera and the debug camera (debug builds only).</summary>
    public const string DebugCameraAction = "debug_camera";

    private Random _spawnRandom = new(0);
    private int _spawnGeneration;
    private bool _screenshotRun;
    private bool _mapSignalsHooked;
    private bool _playerCameraHadMouse;

    [Export]
    public Vector3 SunRotationDegrees { get; set; } = new(-50f, -30f, 0f);

    [Export]
    public float SunEnergy { get; set; } = 0.9f;

    /// <summary>How far from the camera the sun casts shadows, in metres; large enough for the overview preset.</summary>
    [Export]
    public float SunShadowMaxDistance { get; set; } = 1500f;

    /// <summary>Strength of the sky's ambient light. Lower keeps flat surfaces from washing out.</summary>
    [Export]
    public float AmbientEnergy { get; set; } = 0.6f;

    [Export]
    public float TonemapExposure { get; set; } = 1f;

    /// <summary>Debug camera start, relative to the terrain at the map origin. The camera looks at that point.</summary>
    [Export]
    public Vector3 CameraStartOffset { get; set; } = new(0f, 30f, 40f);

    /// <summary>Height above the ground hit at which the player is put, m; it then drops onto the ground.</summary>
    [Export]
    public float SpawnDropHeight { get; set; } = 0.5f;

    /// <summary>Spawn-area draws before a spawn (or respawn) fails.</summary>
    [Export]
    public int SpawnMaxAttempts { get; set; } = 50;

    /// <summary>
    /// Temporary fall respawn: the player respawns when more than this many metres below the slab's bottom
    /// (<c>cliff.bottom_y</c>).
    /// </summary>
    [Export]
    public float FallRespawnDepth { get; set; } = 20f;

    /// <summary>Physics frames to wait for the terrain collision before the spawn gives up.</summary>
    [Export]
    public int SpawnPhysicsWaitFrames { get; set; } = 30;

    /// <summary>
    /// Seed of the spawn draws. Set it before the node enters the tree to override <c>--seed</c> and the time; after
    /// that it is the seed in use (call <see cref="ResetSpawnRandom"/> to change it).
    /// </summary>
    public int? Seed { get; set; }

    public MapLoader MapLoader { get; private set; } = null!;

    /// <summary>The player; null before the spawn, after a failed one and between map loads.</summary>
    public OnFoot.Player? Player { get; private set; }

    /// <summary>The player's third-person camera; null whenever <see cref="Player"/> is.</summary>
    public PlayerCamera? PlayerCamera { get; private set; }

    /// <summary>
    /// Spawn-area draws the last spawn or respawn made: on success the draw that passed (1 = the first), on failure
    /// the draws made before it gave up (0 when it couldn't draw at all).
    /// </summary>
    public int LastSpawnAttempts { get; private set; }

    /// <summary>
    /// Why the last <see cref="DrawSpawn"/> failed (it names <see cref="MapLoader.BoundaryPath"/>); null after a
    /// passing draw.
    /// </summary>
    public string? LastSpawnFailure { get; private set; }

    /// <summary>The free-flying debug camera; null outside debug builds.</summary>
    public DebugCamera? Camera { get; private set; }

    /// <summary>
    /// Whether <c>debug_camera</c> switched to the debug camera (player and player camera input off). False after every
    /// spawn and without a player.
    /// </summary>
    public bool DebugCameraActive { get; private set; }

    /// <summary>The orthophoto debug view (F2); null outside debug builds.</summary>
    public OrthophotoDebugView? Orthophoto { get; private set; }

    /// <summary>The road graph debug view (G); null outside debug builds.</summary>
    public RoadGraphDebugView? RoadGraphView { get; private set; }

    /// <summary>The HUD street label; in every build.</summary>
    public StreetLabelHud Hud { get; private set; } = null!;

    /// <summary>The place inspector (I); null outside debug builds.</summary>
    public PlaceInspector? Inspector { get; private set; }

    public override void _Ready()
    {
        AddChild(CreateEnvironment());
        AddChild(CreateSun());

        MapLoader = new MapLoader { Name = "MapLoader" };
        AddChild(MapLoader);
        HookMapSignals();

        Hud = new StreetLabelHud { Name = "StreetLabelHud", Loader = MapLoader };
        AddChild(Hud);

        if (DebugBuild.IsDebug)
        {
            Orthophoto = new OrthophotoDebugView { Name = "OrthophotoDebugView", Loader = MapLoader };
            AddChild(Orthophoto);
            RoadGraphView = new RoadGraphDebugView { Name = "RoadGraphDebugView", Loader = MapLoader };
            AddChild(RoadGraphView);
            Inspector = new PlaceInspector { Name = "PlaceInspector", Loader = MapLoader };
            AddChild(Inspector);
            Camera = new DebugCamera { Name = "DebugCamera", Current = true };
            AddChild(Camera);
            Camera.LookAtFromPosition(CameraStartOffset, Vector3.Zero, Vector3.Up);
        }

        ScreenshotArgs? screenshot;
        try
        {
            screenshot = ScreenshotArgs.Parse(OS.GetCmdlineUserArgs());
        }
        catch (ArgumentException ex)
        {
            GD.PushError($"Screenshot: {ex.Message}");
            GetTree().Quit(ScreenshotExitCode.Error);
            return;
        }

        _screenshotRun = screenshot is not null;
        ResetSpawnRandom(Seed ?? SeedFromCommandLine());

        if (screenshot is not null)
        {
            // Added before Load() so it hears MapLoaded or MapLoadFailed.
            AddChild(new ScreenshotTool
            {
                Name = "ScreenshotTool",
                Request = screenshot,
                Loader = MapLoader,
                OrthophotoView = Orthophoto,
                RoadGraphView = RoadGraphView,
                Hud = Hud,
                Inspector = Inspector,
                World = this,
            });
        }

        MapLoader.Load();
        if (screenshot is null && MapLoader.Manifest is not null && Camera is not null)
        {
            PlaceCameraAboveTerrainAsync();
        }
    }

    public override void _EnterTree()
    {
        // _Ready runs once; a World removed and added again hooks its loader here, so a later reload spawns again.
        if (MapLoader is not null && IsInstanceValid(MapLoader))
        {
            HookMapSignals();
        }
    }

    public override void _ExitTree()
    {
        // A spawn still waiting for the terrain collision belongs to the old tree; drop it.
        _spawnGeneration++;
        if (_mapSignalsHooked && MapLoader is not null && IsInstanceValid(MapLoader))
        {
            MapLoader.MapLoaded -= OnMapLoaded;
            MapLoader.MapLoadFailed -= OnMapLoadFailed;
        }

        _mapSignalsHooked = false;
    }

    public override void _PhysicsProcess(double delta)
    {
        // TEMPORARY fall respawn (docs/specs/player-on-foot.md, "Temporary fall respawn"): the world-edge death spec
        // replaces this with the void trigger and the death sequence; remove it then.
        if (Player is not null
            && IsInstanceValid(Player)
            && MapLoader.Boundary is { } boundary
            && FallReset.IsBelow(Player.GlobalPosition.Y, boundary.Cliff.BottomY, FallRespawnDepth))
        {
            RespawnPlayer();
        }
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event.IsActionPressed(DebugCameraAction) && !@event.IsEcho() && ToggleDebugCamera())
        {
            GetViewport().SetInputAsHandled();
        }
    }

    /// <summary>
    /// Switches between the player camera and the debug camera (<c>debug_camera</c>). To the debug camera: it takes the
    /// player camera's global transform and field of view and becomes current, and the player and player camera stop
    /// reading input; whether the player camera held the mouse is remembered. Back: their input is on again and the
    /// player camera becomes current, capturing the mouse only if it held it before.
    /// </summary>
    /// <returns>False when it did nothing: not a debug build, no debug camera, no player, or a screenshot run.</returns>
    public bool ToggleDebugCamera()
    {
        if (!DebugBuild.IsDebug
            || _screenshotRun
            || Camera is null || !IsInstanceValid(Camera)
            || Player is null || !IsInstanceValid(Player)
            || PlayerCamera is null || !IsInstanceValid(PlayerCamera))
        {
            return false;
        }

        if (!DebugCameraActive)
        {
            _playerCameraHadMouse = PlayerCamera.MouseCaptured;
            Camera.GlobalTransform = PlayerCamera.Camera.GlobalTransform;
            Camera.Fov = PlayerCamera.Camera.Fov;
            Camera.MakeCurrent();
            Player.InputEnabled = false;
            PlayerCamera.InputEnabled = false;
            DebugCameraActive = true;
            return true;
        }

        Player.InputEnabled = true;
        PlayerCamera.InputEnabled = true;
        PlayerCamera.Activate(captureMouse: _playerCameraHadMouse);
        DebugCameraActive = false;
        return true;
    }

    /// <summary>Starts the spawn draws over with <paramref name="seed"/>; <see cref="Seed"/> becomes it.</summary>
    public void ResetSpawnRandom(int seed)
    {
        Seed = seed;
        _spawnRandom = new Random(seed);
    }

    /// <summary>
    /// The spawn draw procedure of the spec ("Spawn"): up to <see cref="SpawnMaxAttempts"/> points from the spawn
    /// area's open ground, each passing when (1) a downward ray there hits <see cref="MapLoader.TerrainGroup"/> first,
    /// of any collision body, and (2) the player's capsule, <see cref="SpawnDropHeight"/> above that hit, overlaps no
    /// collision body but terrain. The player's own body is ignored. Call it from a physics frame or after one.
    /// </summary>
    /// <param name="position">The passing point (the player's feet, <see cref="SpawnDropHeight"/> above the ground).</param>
    /// <param name="facingYaw">A random facing in radians, from the same random source, drawn after a passing point.</param>
    /// <returns>
    /// False with no boundary, when the spawn area's open ground can't be drawn from (<see cref="SpawnPicker"/> throws)
    /// or when every draw fails; <see cref="LastSpawnFailure"/> then says which.
    /// </returns>
    public bool DrawSpawn(out Vector3 position, out float facingYaw)
    {
        position = Vector3.Zero;
        facingYaw = 0f;
        LastSpawnAttempts = 0;
        LastSpawnFailure = null;
        var boundaryPath = MapLoader.BoundaryPath;
        if (MapLoader.Boundary is not { } boundary)
        {
            LastSpawnFailure = $"{boundaryPath} is missing or invalid";
            return false;
        }

        if (!MapLoader.IsInsideTree())
        {
            LastSpawnFailure = $"the map loader isn't in the scene tree, so no draw from the spawn area in {boundaryPath} was made";
            return false;
        }

        SpawnPicker picker;
        try
        {
            picker = new SpawnPicker(boundary.SpawnArea, _spawnRandom);
        }
        catch (ArgumentException ex)
        {
            LastSpawnFailure = $"the spawn area's open ground in {boundaryPath} can't be drawn from ({ex.Message})";
            return false;
        }

        var (radius, height) = Player is not null && IsInstanceValid(Player)
            ? (Player.CapsuleRadius, Player.CapsuleHeight)
            : CapsuleOfNewPlayer();
        var space = MapLoader.GetWorld3D().DirectSpaceState;
        var excluded = new Godot.Collections.Array<Rid>();
        if (Player is not null && IsInstanceValid(Player))
        {
            excluded.Add(Player.GetRid());
        }

        var shape = new PhysicsShapeQueryParameters3D
        {
            Shape = new CapsuleShape3D { Radius = radius, Height = height },
            CollideWithBodies = true,
            CollideWithAreas = false,
            Exclude = excluded,
        };

        for (var attempt = 1; attempt <= SpawnMaxAttempts; attempt++)
        {
            PlanarPoint point;
            try
            {
                point = picker.Next();
            }
            catch (InvalidOperationException ex)
            {
                LastSpawnAttempts = attempt - 1;
                LastSpawnFailure = FormattableString.Invariant(
                    $"draw {attempt} from the spawn area's open ground in {boundaryPath} found no point ({ex.Message})");
                return false;
            }

            var x = (float)point.X;
            var z = (float)point.Z;
            var ray = PhysicsRayQueryParameters3D.Create(new Vector3(x, 10_000f, z), new Vector3(x, -10_000f, z));
            ray.Exclude = excluded;
            var hit = space.IntersectRay(ray);
            if (hit.Count == 0 || hit["collider"].AsGodotObject() is not Node first || !first.IsInGroup(MapLoader.TerrainGroup))
            {
                continue;
            }

            var feet = hit["position"].AsVector3() + (Vector3.Up * SpawnDropHeight);
            shape.Transform = new Transform3D(Basis.Identity, feet + (Vector3.Up * (height / 2f)));
            var blocked = false;
            foreach (var overlap in space.IntersectShape(shape, MaxSpawnShapeHits))
            {
                if (overlap["collider"].AsGodotObject() is not Node body || !body.IsInGroup(MapLoader.TerrainGroup))
                {
                    blocked = true;
                    break;
                }
            }

            if (blocked)
            {
                continue;
            }

            position = feet;
            facingYaw = (float)((_spawnRandom.NextDouble() * 2 * Math.PI) - Math.PI);
            LastSpawnAttempts = attempt;
            return true;
        }

        LastSpawnAttempts = SpawnMaxAttempts;
        LastSpawnFailure = FormattableString.Invariant(
            $"none of {SpawnMaxAttempts} draws from the spawn area in {boundaryPath} had open terrain clear of collision bodies");
        return false;
    }

    /// <summary>
    /// Frees the current player, then creates one at a passing spawn draw: emits <see cref="SignalName.PlayerSpawned"/>,
    /// makes its camera current behind it and sets the street label's target (outside screenshot runs; there it stays
    /// hidden). On failure pushes an error naming <c>boundary.json</c> and <c>/regen-map</c>, and the debug camera stays.
    /// Call it after the terrain collision is queryable.
    /// </summary>
    /// <returns>True when the player spawned.</returns>
    public bool SpawnPlayer()
    {
        RemovePlayer();
        if (!DrawSpawn(out var position, out var facing))
        {
            FailSpawn("no player spawn", LastSpawnFailure);
            return false;
        }

        var player = new OnFoot.Player { Name = "Player", Visible = !_screenshotRun };
        var camera = new PlayerCamera { Name = "PlayerCamera", Target = player };
        player.Camera = camera;
        AddChild(player);
        AddChild(camera);
        Player = player;
        PlayerCamera = camera;
        player.PlaceAt(position, facing);
        camera.PlaceBehind(facing);

        if (!_screenshotRun)
        {
            camera.Activate();
            Hud.Target = player;
        }

        GD.Print(FormattableString.Invariant(
            $"Player: spawned at ({position.X:0.###}, {position.Y:0.###}, {position.Z:0.###}), seed {Seed}, draw {LastSpawnAttempts}{(_screenshotRun ? ", hidden for the screenshot" : "")}"));
        EmitSignal(SignalName.PlayerSpawned, position);
        return true;
    }

    /// <summary>
    /// The temporary fall respawn: runs the draw procedure again for the existing player and puts it there with zero
    /// velocity, emitting <see cref="SignalName.PlayerRespawned"/>. No death: money and gear stay. On failure pushes
    /// the spawn error and frees the player.
    /// </summary>
    /// <returns>True when the player was put back; false without a player or when every draw failed.</returns>
    public bool RespawnPlayer()
    {
        if (Player is null || !IsInstanceValid(Player))
        {
            return false;
        }

        if (!DrawSpawn(out var position, out var facing))
        {
            FailSpawn("no player respawn", LastSpawnFailure);
            RemovePlayer();
            return false;
        }

        Player.PlaceAt(position, facing);
        PlayerCamera?.SnapToTarget();
        GD.Print(FormattableString.Invariant(
            $"Player: respawned at ({position.X:0.###}, {position.Y:0.###}, {position.Z:0.###}), draw {LastSpawnAttempts}"));
        EmitSignal(SignalName.PlayerRespawned, position);
        return true;
    }

    private void OnMapLoaded(int tileCount)
    {
        RemovePlayer();
        SpawnWhenCollisionReadyAsync(++_spawnGeneration);
    }

    private void OnMapLoadFailed(string message)
    {
        _spawnGeneration++;
        RemovePlayer();
    }

    private async void SpawnWhenCollisionReadyAsync(int generation)
    {
        var ready = await MapLoader.WaitForTerrainCollisionAsync(SpawnPhysicsWaitFrames);
        if (generation != _spawnGeneration || !IsInsideTree())
        {
            return;
        }

        if (!ready)
        {
            LastSpawnAttempts = 0;
            LastSpawnFailure = FormattableString.Invariant(
                $"no terrain collision was queryable after {SpawnPhysicsWaitFrames} physics frames, so no draw from the spawn area in {MapLoader.BoundaryPath} was made");
            FailSpawn("no player spawn", LastSpawnFailure);
            return;
        }

        SpawnPlayer();
    }

    /// <summary>
    /// Pushes the spawn failure error: <paramref name="what"/>, the <paramref name="reason"/> (which names the boundary
    /// file) and the regen hint.
    /// </summary>
    private void FailSpawn(string what, string? reason)
    {
        reason ??= $"no draw from the spawn area in {MapLoader.BoundaryPath} passed";
        GD.PushError($"World: {what}: {reason}; {MapPack.RegenHint(MapLoader.ResolvedMapId)}");
    }

    /// <summary>Connects <see cref="MapLoader"/>'s load signals once per stay in the tree.</summary>
    private void HookMapSignals()
    {
        if (_mapSignalsHooked)
        {
            return;
        }

        MapLoader.MapLoaded += OnMapLoaded;
        MapLoader.MapLoadFailed += OnMapLoadFailed;
        _mapSignalsHooked = true;
    }

    /// <summary>Frees the player and its camera; the street label measures from the camera again and the debug camera is current.</summary>
    private void RemovePlayer()
    {
        var hadCamera = PlayerCamera is not null;
        foreach (Node? node in new Node?[] { Player, PlayerCamera })
        {
            if (node is not null && IsInstanceValid(node))
            {
                node.GetParent()?.RemoveChild(node);
                node.QueueFree();
            }
        }

        Player = null;
        PlayerCamera = null;
        DebugCameraActive = false;
        _playerCameraHadMouse = false;
        if (Hud is not null)
        {
            Hud.Target = null;
        }

        if (hadCamera && !_screenshotRun && Camera is not null && IsInstanceValid(Camera))
        {
            Camera.MakeCurrent();
        }
    }

    /// <summary>Capsule size of a player with its export defaults, for draws before a player exists.</summary>
    private static (float Radius, float Height) CapsuleOfNewPlayer()
    {
        var template = new OnFoot.Player();
        var size = (template.CapsuleRadius, template.CapsuleHeight);
        template.Free();
        return size;
    }

    /// <summary><c>--seed &lt;n&gt;</c> from the user command line, else a seed from the time.</summary>
    private static int SeedFromCommandLine()
    {
        try
        {
            if (SeedArg.Parse(OS.GetCmdlineUserArgs()) is { } seed)
            {
                return seed;
            }
        }
        catch (ArgumentException ex)
        {
            GD.PushError($"World: {ex.Message}; the spawn draws are seeded from the time");
        }

        return unchecked((int)DateTime.UtcNow.Ticks);
    }

    /// <summary>Moves the camera to <see cref="CameraStartOffset"/> above the terrain at the origin once collision is ready.</summary>
    private async void PlaceCameraAboveTerrainAsync()
    {
        if (!await MapLoader.WaitForTerrainCollisionAsync() || Camera is null || !IsInstanceValid(Camera))
        {
            return;
        }

        if (MapLoader.TerrainHeightAt(0f, 0f) is not { } ground)
        {
            GD.PushWarning("World: no terrain under the map origin; the debug camera stays at its default position");
            return;
        }

        var target = new Vector3(0f, ground, 0f);
        Camera.LookAtFromPosition(target + CameraStartOffset, target, Vector3.Up);
        EmitSignal(SignalName.CameraPlaced, Camera.GlobalPosition);
    }

    private WorldEnvironment CreateEnvironment()
    {
        var sky = new Sky { SkyMaterial = new ProceduralSkyMaterial() };
        var environment = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Sky,
            Sky = sky,
            AmbientLightSource = Godot.Environment.AmbientSource.Sky,
            ReflectedLightSource = Godot.Environment.ReflectionSource.Sky,
            AmbientLightEnergy = AmbientEnergy,
            // AgX compresses highlights more gently than Filmic, so light surfaces keep their colour.
            TonemapMode = Godot.Environment.ToneMapper.Agx,
            TonemapExposure = TonemapExposure,
        };
        return new WorldEnvironment { Name = "WorldEnvironment", Environment = environment };
    }

    private DirectionalLight3D CreateSun() =>
        new()
        {
            Name = "Sun",
            ShadowEnabled = true,
            RotationDegrees = SunRotationDegrees,
            LightEnergy = SunEnergy,
            DirectionalShadowMaxDistance = SunShadowMaxDistance,
        };
}
