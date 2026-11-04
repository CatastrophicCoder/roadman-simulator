using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Roadman.Autoload;
using Roadman.Core;
using Roadman.Core.Edge;
using Roadman.Core.Maps;
using Roadman.Core.Player;
using Roadman.Core.Tools;
using Roadman.Debug;
using Roadman.Hud;
using Roadman.OnFoot;
using Roadman.Scenes.Edge;

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
/// the mouse capture it had. Without a player, or outside <see cref="DeathState.Alive"/>, the toggle does nothing.
/// </para>
/// <para>
/// The world edge (<c>docs/specs/world-edge-game.md</c>): the player entering the <see cref="VoidTrigger"/>, or falling
/// below the safety net (<see cref="SafetyNetMargin"/> under <c>cliff.bottom_y</c>), starts the death sequence
/// (<see cref="Death"/>): it falls out under <see cref="VoidPull"/> with no collision while the <see cref="DeathCamera"/>
/// pulls back, the <see cref="DeathScreen"/> follows, and it respawns in the spawn area. Other bodies:
/// <see cref="IVoidFaller"/>s get <see cref="IVoidFaller.EnterVoid"/> once, other <see cref="RigidBody3D"/>s fall under
/// the pull (<see cref="VoidFall"/>) or, below the safety net, are freed. The safety net checks the player and the bodies
/// given to <see cref="TrackVoidBody"/>.
/// </para>
/// </summary>
public partial class World : Node3D
{
    [Signal]
    public delegate void CameraPlacedEventHandler(Vector3 position);

    /// <summary>The player was placed at <paramref name="position"/> (its feet) after a map load.</summary>
    [Signal]
    public delegate void PlayerSpawnedEventHandler(Vector3 position);

    /// <summary>The player respawned at <paramref name="position"/> (its feet) after a death in the void.</summary>
    [Signal]
    public delegate void PlayerRespawnedEventHandler(Vector3 position);

    private const int MaxSpawnShapeHits = 32;

    /// <summary>The sky shader: the day sky above the horizon, a starfield below it.</summary>
    public const string SkyShaderPath = "res://materials/sky/space_sky.gdshader";

    /// <summary>The project setting with the default gravity, m/s².</summary>
    public const string GravitySetting = "physics/3d/default_gravity";

    /// <summary>Gravity before <see cref="_Ready"/> reads <see cref="GravitySetting"/>, m/s².</summary>
    private const float DefaultGravity = 9.8f;

    // Starting values of the world-edge exports (docs/specs/world-edge-game.md, "Exports"), also their first "last
    // valid value" for ApplyEdgeExports.
    private const float DefaultFallTime = 3f;
    private const float DefaultScreenTime = 3f;
    private const float DefaultMaxFallTime = 10f;
    private const float DefaultSafetyNetMargin = 20f;
    private const float DefaultVoidOutwardAcceleration = 4f;

    /// <summary>Toggles between the player camera and the debug camera (debug builds only).</summary>
    public const string DebugCameraAction = "debug_camera";

    private Random _spawnRandom = new(0);
    private int _spawnGeneration;
    private bool _screenshotRun;
    private bool _mapSignalsHooked;
    private bool _playerCameraHadMouse;
    private readonly Dictionary<Node3D, Action> _trackedBodies = [];
    private readonly HashSet<ulong> _voidFallers = [];
    private float? _voidDownAcceleration;
    private EdgeExportValues _validEdgeExports = new()
    {
        FallTime = DefaultFallTime,
        ScreenTime = DefaultScreenTime,
        MaxFallTime = DefaultMaxFallTime,
        SafetyNetMargin = DefaultSafetyNetMargin,
        VoidOutwardAcceleration = DefaultVoidOutwardAcceleration,
    };
    private float _projectGravity = DefaultGravity;

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
    /// The safety net's depth below the slab's bottom (<c>cliff.bottom_y</c>), m: a player or tracked body below it
    /// that isn't falling yet is handled as if it entered the void.
    /// </summary>
    [Export(PropertyHint.Range, "0,200,0.1,or_greater,suffix:m")]
    public float SafetyNetMargin { get; set; } = DefaultSafetyNetMargin;

    /// <summary>Seconds the player falls before the death screen.</summary>
    [Export(PropertyHint.Range, "0,30,0.05,or_greater,suffix:s")]
    public float FallTime { get; set; } = DefaultFallTime;

    /// <summary>Seconds the death screen shows before the respawn.</summary>
    [Export(PropertyHint.Range, "0,30,0.05,or_greater,suffix:s")]
    public float ScreenTime { get; set; } = DefaultScreenTime;

    /// <summary>Seconds after which a falling rigid body (not the player) is freed whatever its height.</summary>
    [Export(PropertyHint.Range, "0,120,0.1,or_greater,suffix:s")]
    public float MaxFallTime { get; set; } = DefaultMaxFallTime;

    /// <summary>The void pull's outward acceleration, m/s².</summary>
    [Export(PropertyHint.Range, "0,50,0.1,or_greater,suffix:m/s²")]
    public float VoidOutwardAcceleration { get; set; } = DefaultVoidOutwardAcceleration;

    /// <summary>
    /// The void pull's downward acceleration, m/s². Until it is set, the project's default gravity
    /// (<c>physics/3d/default_gravity</c>, read in <see cref="_Ready"/>), as the player falls under.
    /// </summary>
    [Export(PropertyHint.Range, "0,100,0.01,or_greater,suffix:m/s²")]
    public float VoidDownAcceleration
    {
        get => _voidDownAcceleration ?? _projectGravity;
        set => _voidDownAcceleration = value;
    }

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

    /// <summary>The death and respawn state machine, over <c>GameState</c>'s <see cref="PlayerState"/>.</summary>
    public DeathSequence Death { get; private set; } = null!;

    /// <summary>The pull that replaces gravity in the void; its values come from the exports each physics frame.</summary>
    public VoidPull VoidPull { get; } = new();

    /// <summary>The death sequence's camera; in every build, current only during a death.</summary>
    public DeathCamera DeathCamera { get; private set; } = null!;

    /// <summary>The death screen; in every build, hidden outside a death.</summary>
    public DeathScreen DeathScreen { get; private set; } = null!;

    /// <summary>The falls of plain rigid bodies into the void.</summary>
    public VoidFall VoidFall { get; private set; } = null!;

    /// <summary>The bodies besides the player that the safety net checks (<see cref="TrackVoidBody"/>).</summary>
    public IReadOnlyCollection<Node3D> TrackedVoidBodies => _trackedBodies.Keys;

    public override void _Ready()
    {
        _projectGravity = ProjectSettings.GetSetting(GravitySetting, DefaultGravity).AsSingle();
        AddChild(CreateEnvironment());
        AddChild(CreateSun());

        Death = new DeathSequence(GameState.Instance?.Player ?? new PlayerState());
        Death.StateChanged += OnDeathStateChanged;
        DeathCamera = new DeathCamera { Name = "DeathCamera" };
        AddChild(DeathCamera);
        VoidFall = new VoidFall { Name = "VoidFall" };
        AddChild(VoidFall);

        MapLoader = new MapLoader { Name = "MapLoader" };
        AddChild(MapLoader);
        HookMapSignals();

        Hud = new StreetLabelHud { Name = "StreetLabelHud", Loader = MapLoader };
        AddChild(Hud);
        DeathScreen = new DeathScreen { Name = "DeathScreen" };
        AddChild(DeathScreen);

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
        ApplyEdgeExports();
        if (Death.State is DeathState.Falling or DeathState.DeathScreen)
        {
            Death.Advance(delta);
        }

        // The respawn runs here, after Advance has returned, not in the StateChanged handler: completing it there would
        // raise Respawning->Alive while DeathScreen->Respawning is still being announced, so handlers subscribed after
        // World's would hear the two in the wrong order.
        if (Death.State == DeathState.Respawning)
        {
            RespawnAfterDeath();
        }

        CheckPlayerInVoid();
        CheckSafetyNet();
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
    /// <returns>
    /// False when it did nothing: not a debug build, no debug camera, no player, a screenshot run, or the death sequence
    /// running (<see cref="Death"/> not <see cref="DeathState.Alive"/>).
    /// </returns>
    public bool ToggleDebugCamera()
    {
        if (!DebugBuild.IsDebug
            || Death.State != DeathState.Alive
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
    /// Adds <paramref name="body"/> to the bodies the safety net checks each physics frame besides the player (loose
    /// barrier pieces, later vehicles and characters). Tracking an already tracked body does nothing.
    /// <para>
    /// It is dropped (<see cref="UntrackVoidBody"/>, which also disconnects its <see cref="Node.TreeExiting"/> handler)
    /// when it leaves the tree for any reason: freed, or only removed, for example to be reparented. A body that comes
    /// back into the tree isn't tracked again by itself; track it after it is added (Part B's loose pieces: once they
    /// are under <c>Barriers/LoosePieces</c>). Every body is also dropped on a map load or failed load.
    /// </para>
    /// </summary>
    public void TrackVoidBody(Node3D body)
    {
        if (!IsInstanceValid(body) || _trackedBodies.ContainsKey(body))
        {
            return;
        }

        void OnTreeExiting() => UntrackVoidBody(body);
        _trackedBodies[body] = OnTreeExiting;
        body.TreeExiting += OnTreeExiting;
    }

    /// <summary>
    /// Removes <paramref name="body"/> from the safety net's bodies and disconnects its <see cref="Node.TreeExiting"/>
    /// handler. Does nothing for a body that isn't tracked.
    /// </summary>
    public void UntrackVoidBody(Node3D body)
    {
        if (!_trackedBodies.Remove(body, out var handler))
        {
            return;
        }

        if (IsInstanceValid(body))
        {
            body.TreeExiting -= handler;
        }
    }

    /// <summary>Drops every tracked body, disconnecting their handlers.</summary>
    private void UntrackAllVoidBodies()
    {
        foreach (var body in _trackedBodies.Keys.ToList())
        {
            UntrackVoidBody(body);
        }
    }

    /// <summary>
    /// Handles <paramref name="body"/> entering the void (from the <see cref="VoidTrigger"/> or the safety net) with the
    /// unit horizontal <paramref name="outward"/>: the player starts the death sequence (only in
    /// <see cref="DeathState.Alive"/>), an <see cref="IVoidFaller"/> gets <see cref="IVoidFaller.EnterVoid"/> once, and
    /// any other <see cref="RigidBody3D"/> falls under the pull (<see cref="VoidFall"/>) or, when
    /// <paramref name="freeRigidBodies"/> (the safety net), is freed. Other bodies and bodies already falling are
    /// ignored.
    /// </summary>
    /// <returns>True when it did something.</returns>
    public bool HandleVoidEntry(Node3D body, Vector3 outward, bool freeRigidBodies = false)
    {
        if (!IsInstanceValid(body) || body.IsQueuedForDeletion())
        {
            return false;
        }

        if (body == Player)
        {
            return StartDeath(outward);
        }

        if (body is IVoidFaller faller)
        {
            if (!_voidFallers.Add(body.GetInstanceId()))
            {
                return false;
            }

            faller.EnterVoid(outward);
            return true;
        }

        if (body is RigidBody3D rigid && !VoidFall.IsFalling(rigid))
        {
            if (freeRigidBodies)
            {
                UntrackVoidBody(rigid);
                rigid.QueueFree();
                return true;
            }

            return VoidFall.Track(rigid, outward);
        }

        return false;
    }

    /// <summary>
    /// Step 1 of the death sequence (<c>docs/specs/world-edge-game.md</c>, "Death sequence"): records the losses and
    /// calls <c>Die()</c> (<see cref="DeathSequence.EnterVoid"/>), turns the player's and its camera's input off, clears
    /// the player's collision mask, replaces its gravity with <see cref="VoidPull"/> along <paramref name="outward"/>,
    /// and starts the <see cref="DeathCamera"/> from the player camera. Does nothing without a player or outside
    /// <see cref="DeathState.Alive"/>.
    /// </summary>
    /// <returns>True when the sequence started.</returns>
    public bool StartDeath(Vector3 outward)
    {
        if (Player is null || !IsInstanceValid(Player) || PlayerCamera is null || !IsInstanceValid(PlayerCamera)
            || Death.State != DeathState.Alive)
        {
            return false;
        }

        outward.Y = 0f;
        outward = outward.LengthSquared() > 0f ? outward.Normalized() : Vector3.Right;
        var position = Player.GlobalPosition;
        ApplyEdgeExports();
        if (!Death.EnterVoid(new VoidEntry(
            new LocalPoint(position.X, position.Y, position.Z), new PlanarPoint(outward.X, outward.Z))))
        {
            return false;
        }

        Player.InputEnabled = false;
        PlayerCamera.InputEnabled = false;
        Player.CollisionMask = 0;
        var pull = VoidPull.Acceleration(new LocalPoint(outward.X, 0, outward.Z));
        Player.GravityOverride = new Vector3((float)pull.X, (float)pull.Y, (float)pull.Z);

        var (target, lookAt) = DeathCameraTarget(position, outward);
        DeathCamera.Begin(PlayerCamera.Camera.GlobalTransform, PlayerCamera.Camera.Fov, target, lookAt);
        DebugCameraActive = false;
        GD.Print(FormattableString.Invariant(
            $"Player: entered the void at ({position.X:0.###}, {position.Y:0.###}, {position.Z:0.###}), outward ({outward.X:0.###}, {outward.Z:0.###})"));
        return true;
    }

    /// <summary>
    /// The death camera's end point and look-at for an entry at <paramref name="position"/>: the nearest ring point at
    /// the ledge's height (<see cref="EdgeGeometry.LedgeHeightAt"/>), + <paramref name="outward"/> ×
    /// <see cref="DeathCamera.PullBackDistance"/> + up × <see cref="DeathCamera.PullBackHeight"/>; looking at the ring
    /// point <see cref="DeathCamera.LookAtDrop"/> below the ledge. Without edge geometry (or items) the entry's own
    /// position stands in for the ring point.
    /// </summary>
    public (Vector3 Target, Vector3 LookAt) DeathCameraTarget(Vector3 position, Vector3 outward)
    {
        var ring = position;
        if (MapLoader.EdgeGeometry is { } geometry)
        {
            var nearest = geometry.NearestRingPoint(position.X, position.Z);
            var ledge = (double)position.Y;
            if (geometry.Boundary.Roadblocks.Count + geometry.Boundary.Fences.Count > 0)
            {
                ledge = geometry.LedgeHeightAt(nearest.S);
            }

            ring = new Vector3((float)nearest.Point.X, (float)ledge, (float)nearest.Point.Z);
        }

        var target = ring + (outward * DeathCamera.PullBackDistance) + (Vector3.Up * DeathCamera.PullBackHeight);
        var lookAt = ring + (Vector3.Down * DeathCamera.LookAtDrop);
        return (target, lookAt);
    }

    private void OnBodyEnteredVoid(Node3D body, Vector3 outward) => HandleVoidEntry(body, outward);

    /// <summary>
    /// The safety net: the player and every tracked body below <see cref="SafetyNetMargin"/> under the slab that isn't
    /// falling yet are handled as void entries (rigid bodies freed).
    /// </summary>
    private void CheckSafetyNet()
    {
        if (MapLoader?.EdgeGeometry is not { } geometry)
        {
            return;
        }

        if (Player is not null && IsInstanceValid(Player) && Death.State == DeathState.Alive
            && geometry.IsBelowSafetyNet(Player.GlobalPosition.Y, SafetyNetMargin))
        {
            var position = Player.GlobalPosition;
            StartDeath(OutwardAt(geometry, position));
        }

        if (_trackedBodies.Count == 0)
        {
            return;
        }

        foreach (var body in _trackedBodies.Keys.ToList())
        {
            if (!IsInstanceValid(body) || body.IsQueuedForDeletion())
            {
                UntrackVoidBody(body);
                continue;
            }

            if (body == Player || !body.IsInsideTree() || !geometry.IsBelowSafetyNet(body.GlobalPosition.Y, SafetyNetMargin))
            {
                continue;
            }

            HandleVoidEntry(body, OutwardAt(geometry, body.GlobalPosition), freeRigidBodies: true);
        }
    }

    /// <summary>
    /// The player's own void test, each physics frame: the <see cref="VoidTrigger"/> reports a
    /// <see cref="CharacterBody3D"/> only a frame after it overlaps a prism (Jolt), so the outermost points of its
    /// capsule (at the centres of its two end spheres, moved one radius outward) are also tested against
    /// <see cref="EdgeGeometry.IsInVoid"/>. The first of the two to see the entry starts the death sequence.
    /// </summary>
    private void CheckPlayerInVoid()
    {
        if (MapLoader?.EdgeGeometry is not { } geometry
            || Player is null || !IsInstanceValid(Player) || !Player.IsInsideTree()
            || Death.State != DeathState.Alive)
        {
            return;
        }

        var feet = Player.GlobalPosition;
        var outward = OutwardAt(geometry, feet);
        var radius = Player.CapsuleRadius;
        var reach = outward * radius;
        foreach (var height in new[] { radius, Mathf.Max(radius, Player.CapsuleHeight - radius) })
        {
            var point = feet + reach + (Vector3.Up * height);
            if (geometry.IsInVoid(new LocalPoint(point.X, point.Y, point.Z)))
            {
                HandleVoidEntry(Player, outward);
                return;
            }
        }
    }

    private static Vector3 OutwardAt(EdgeGeometry geometry, Vector3 position)
    {
        var outward = geometry.OutwardAt(position.X, position.Z);
        return new Vector3((float)outward.X, 0f, (float)outward.Z);
    }

    /// <summary>
    /// Shows the death screen on <see cref="DeathState.DeathScreen"/>. <see cref="DeathState.Respawning"/> is handled in
    /// <see cref="_PhysicsProcess"/> once <see cref="DeathSequence.Advance"/> has returned, so every subscriber hears
    /// the transitions in order.
    /// </summary>
    private void OnDeathStateChanged(DeathState from, DeathState to)
    {
        if (to == DeathState.DeathScreen)
        {
            ShowDeathScreen();
        }
    }

    /// <summary>Step 2: the death screen fades in; the player is hidden and stopped; the street label shows nothing.</summary>
    private void ShowDeathScreen()
    {
        if (Death.Losses is { } losses)
        {
            DeathScreen.ShowLosses(DeathScreenText.Format(losses));
        }

        if (Player is not null && IsInstanceValid(Player))
        {
            Player.Visible = false;
            Player.Velocity = Vector3.Zero;
            Player.SetPhysicsProcess(false);
        }

        Hud.Target = null;
        Hud.Suppressed = true;
    }

    /// <summary>
    /// Steps 3 and 4: restores the barriers and draws a spawn point; puts the player back there with everything on
    /// again, or, when every draw fails, pushes the spawn error, frees the player and ends in
    /// <see cref="DeathState.Failed"/>.
    /// </summary>
    private void RespawnAfterDeath()
    {
        MapLoader.Barriers?.RestoreAll();
        if (Player is null || !IsInstanceValid(Player) || PlayerCamera is null || !IsInstanceValid(PlayerCamera)
            || !DrawSpawn(out var position, out var facing))
        {
            FailSpawn("no player respawn", LastSpawnFailure ?? "there was no player to respawn");
            DeathCamera.Stop();
            RemovePlayer();
            DeathScreen.HideNow();
            Death.CompleteRespawn(false);
            return;
        }

        var player = Player;
        var camera = PlayerCamera;
        player.GravityOverride = null;
        player.CollisionMask = CollisionLayers.PlayerMask;
        player.SetPhysicsProcess(true);
        player.PlaceAt(position, facing);
        player.Visible = !_screenshotRun;
        player.InputEnabled = true;
        camera.InputEnabled = true;
        camera.PlaceBehind(facing);
        if (!_screenshotRun)
        {
            camera.Activate();
            Hud.Target = player;
        }

        DeathCamera.Stop();
        DeathScreen.FadeOutAndHide();
        Hud.Suppressed = false;
        DebugCameraActive = false;
        Death.CompleteRespawn(true);
        GD.Print(FormattableString.Invariant(
            $"Player: respawned at ({position.X:0.###}, {position.Y:0.###}, {position.Z:0.###}), draw {LastSpawnAttempts}"));
        EmitSignal(SignalName.PlayerRespawned, position);
    }

    /// <summary>
    /// A map load or failed load: the death sequence back to <see cref="DeathState.Alive"/>, the death camera and
    /// screen off, falling bodies freed and the tracked bodies forgotten.
    /// </summary>
    private void ResetWorldEdgeState()
    {
        Death.Reset();
        DeathCamera.Stop();
        DeathScreen.HideNow();
        VoidFall.Clear();
        UntrackAllVoidBodies();
        _voidFallers.Clear();
        Hud.Suppressed = false;
        ApplyEdgeExports();
    }

    /// <summary>
    /// Copies the world-edge exports into <see cref="Death"/>, <see cref="VoidPull"/> and <see cref="VoidFall"/>, each
    /// physics frame. Each must be finite and at least 0: an export set out of range pushes one error and is set back
    /// to its last valid value, which is used.
    /// </summary>
    private void ApplyEdgeExports()
    {
        FallTime = ValidEdgeExport(nameof(FallTime), FallTime, ref _validEdgeExports.FallTime);
        ScreenTime = ValidEdgeExport(nameof(ScreenTime), ScreenTime, ref _validEdgeExports.ScreenTime);
        MaxFallTime = ValidEdgeExport(nameof(MaxFallTime), MaxFallTime, ref _validEdgeExports.MaxFallTime);
        SafetyNetMargin = ValidEdgeExport(nameof(SafetyNetMargin), SafetyNetMargin, ref _validEdgeExports.SafetyNetMargin);
        VoidOutwardAcceleration = ValidEdgeExport(nameof(VoidOutwardAcceleration), VoidOutwardAcceleration, ref _validEdgeExports.VoidOutwardAcceleration);
        if (!IsValidEdgeExport(VoidDownAcceleration))
        {
            // Back to the last valid setting, which may be "unset" (the project gravity).
            PushInvalidEdgeExport(nameof(VoidDownAcceleration), VoidDownAcceleration, _validEdgeExports.VoidDownAcceleration ?? _projectGravity);
            _voidDownAcceleration = _validEdgeExports.VoidDownAcceleration;
        }
        else
        {
            _validEdgeExports.VoidDownAcceleration = _voidDownAcceleration;
        }

        Death.FallTime = FallTime;
        Death.ScreenTime = ScreenTime;
        VoidPull.OutwardAcceleration = VoidOutwardAcceleration;
        VoidPull.DownAcceleration = VoidDownAcceleration;
        VoidFall.Pull = VoidPull;
        VoidFall.Geometry = MapLoader?.EdgeGeometry;
        VoidFall.MaxFallTime = MaxFallTime;
        VoidFall.SafetyNetMargin = SafetyNetMargin;
    }

    private static bool IsValidEdgeExport(float value) => float.IsFinite(value) && value >= 0f;

    /// <summary>
    /// <paramref name="value"/> when it is valid (it becomes <paramref name="lastValid"/>); otherwise pushes one error
    /// and returns <paramref name="lastValid"/>.
    /// </summary>
    private static float ValidEdgeExport(string name, float value, ref float lastValid)
    {
        if (IsValidEdgeExport(value))
        {
            lastValid = value;
            return value;
        }

        PushInvalidEdgeExport(name, value, lastValid);
        return lastValid;
    }

    private static void PushInvalidEdgeExport(string name, float value, float kept) =>
        GD.PushError(FormattableString.Invariant($"World: {name} must be finite and at least 0, is {value}; keeping {kept}"));

    /// <summary>The last valid value of each world-edge export (<see cref="ApplyEdgeExports"/>).</summary>
    private struct EdgeExportValues
    {
        public float FallTime;
        public float ScreenTime;
        public float MaxFallTime;
        public float SafetyNetMargin;
        public float VoidOutwardAcceleration;

        /// <summary>Null: not set, so the project gravity.</summary>
        public float? VoidDownAcceleration;
    }

    private void OnMapLoaded(int tileCount)
    {
        ResetWorldEdgeState();
        if (MapLoader.VoidTrigger is { } trigger)
        {
            trigger.BodyEnteredVoid += OnBodyEnteredVoid;
        }

        RemovePlayer();
        SpawnWhenCollisionReadyAsync(++_spawnGeneration);
    }

    private void OnMapLoadFailed(string message)
    {
        _spawnGeneration++;
        ResetWorldEdgeState();
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
        // The day sky above the horizon and a starfield below it (materials/sky/space_sky.gdshader); its radiance pass
        // keeps the former procedural ground, so ambient and reflected light are unchanged.
        var sky = new Sky { SkyMaterial = new ShaderMaterial { Shader = GD.Load<Shader>(SkyShaderPath) } };
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
