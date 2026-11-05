using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Roadman.Autoload;
using Roadman.Core;
using Roadman.Core.Edge;
using Roadman.Core.Maps;
using Roadman.Core.Player;
using Roadman.Core.Saves;
using Roadman.Core.Settings;
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
/// pulls back, the <see cref="DeathScreen"/> follows (any key or gamepad button press dismisses it after
/// <see cref="MinScreenTime"/>, else it closes after <see cref="ScreenTime"/>), and it respawns in the spawn area. Other bodies:
/// <see cref="IVoidFaller"/>s get <see cref="IVoidFaller.EnterVoid"/> once, other <see cref="RigidBody3D"/>s fall under
/// the pull (<see cref="VoidFall"/>) or, below the safety net, are freed. The safety net checks the player and the bodies
/// given to <see cref="TrackVoidBody"/>, including the loose pieces of every barrier item that breaks.
/// </para>
/// <para>
/// With <c>-- --perf &lt;spawn|roadblock&gt; &lt;out.json&gt; ...</c> (debug builds only) it adds a <see cref="PerfTool"/>,
/// which measures frame times; <c>--no-world-edge</c> and <c>--no-space-sky</c> switch off
/// <see cref="MapLoader.WorldEdgeEnabled"/> and <see cref="SpaceSkyEnabled"/> before the environment and the map are made.
/// </para>
/// <para>
/// A game started by <c>Main</c> (<c>docs/specs/menu-settings-save.md</c>) sets <see cref="MapId"/>,
/// <see cref="CameraSettings"/> and, to load a save, <see cref="PendingLoad"/> before the node enters the tree. With a
/// pending load the first map load places the player from it (<see cref="LoadPlayer"/>, signal
/// <see cref="SignalName.PlayerLoaded"/>) instead of the spawn draw. <see cref="SignalName.StartFailed"/> reports a map
/// load or first spawn that failed, with the error text pushed.
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

    /// <summary>
    /// The player was placed from <see cref="PendingLoad"/> at <paramref name="position"/> (its feet) after a map load.
    /// <paramref name="moved"/> is true only when the saved position failed the placement check and the spawn draw
    /// placed it instead.
    /// </summary>
    [Signal]
    public delegate void PlayerLoadedEventHandler(Vector3 position, bool moved);

    /// <summary>
    /// The map failed to load, or the player couldn't be spawned or placed after a map load; <paramref name="message"/>
    /// is the error text pushed. Not emitted for a failed respawn after a death.
    /// </summary>
    [Signal]
    public delegate void StartFailedEventHandler(string message);

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
    private const float DefaultScreenTime = 5f;
    private const float DefaultMinScreenTime = 2f;
    private const float DefaultMaxFallTime = 10f;
    private const float DefaultSafetyNetMargin = 20f;
    private const float DefaultVoidOutwardAcceleration = 4f;

    /// <summary>Toggles between the player camera and the debug camera (debug builds only).</summary>
    public const string DebugCameraAction = "debug_camera";

    /// <summary>Opens the pause menu (Esc, the gamepad start button); never dismisses the death screen.</summary>
    public const string PauseAction = "pause";

    private Random _spawnRandom = new(0);
    private int _spawnGeneration;
    private bool _screenshotRun;
    private bool _mapSignalsHooked;
    private bool _playerCameraHadMouse;
    private ulong? _resumeProcessFrame;
    private readonly Dictionary<Node3D, Action> _trackedBodies = [];
    private readonly HashSet<ulong> _voidFallers = [];
    private float? _voidDownAcceleration;
    private EdgeExportValues _validEdgeExports = new()
    {
        FallTime = DefaultFallTime,
        ScreenTime = DefaultScreenTime,
        MinScreenTime = DefaultMinScreenTime,
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

    /// <summary>
    /// Whether the sky is the space sky (<see cref="SkyShaderPath"/>: the day sky with a starfield below the horizon).
    /// False: Godot's plain <see cref="ProceduralSkyMaterial"/> day sky, for the performance measurement. Read once, when
    /// the environment is made in <see cref="_Ready"/>.
    /// </summary>
    [Export]
    public bool SpaceSkyEnabled { get; set; } = true;

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

    /// <summary>Seconds the death screen shows before the respawn when nothing dismisses it.</summary>
    [Export(PropertyHint.Range, "0,30,0.05,or_greater,suffix:s")]
    public float ScreenTime { get; set; } = DefaultScreenTime;

    /// <summary>
    /// Seconds the death screen shows before a key or gamepad button press can dismiss it (<see cref="_Input"/>); a press
    /// earlier is ignored.
    /// </summary>
    [Export(PropertyHint.Range, "0,30,0.05,or_greater,suffix:s")]
    public float MinScreenTime { get; set; } = DefaultMinScreenTime;

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

    /// <summary>
    /// The map to load, copied into <see cref="MapLoader.MapId"/> before the first load; null or empty keeps the
    /// loader's default (the project setting). A <c>--perf --map</c> argument still wins. Set it before the node enters
    /// the tree.
    /// </summary>
    public string? MapId { get; set; }

    /// <summary>
    /// A save's player part to place at the next map load instead of the spawn draw (<see cref="LoadPlayer"/>); used
    /// once, then cleared. Set it before the node enters the tree.
    /// </summary>
    public SavePlayer? PendingLoad { get; set; }

    /// <summary>
    /// The player's gameplay settings, applied to every new or respawned player camera
    /// (<see cref="ApplyCameraSettings"/>); null leaves the camera's exports as they are (direct <c>World</c> runs and
    /// tests).
    /// </summary>
    public GameplaySettings? CameraSettings { get; set; }

    /// <summary>
    /// Whether a manual save can be written now: a player exists and the death sequence is
    /// <see cref="DeathState.Alive"/>.
    /// </summary>
    public bool CanSave =>
        Death is not null && Death.State == DeathState.Alive && Player is not null && IsInstanceValid(Player) && Player.IsInsideTree();

    /// <summary>The player's facing yaw in radians; null without a player.</summary>
    public float? PlayerFacingYaw => Player is not null && IsInstanceValid(Player) ? Player.FacingYaw : null;

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

    /// <summary>The performance tool of a <c>--perf</c> run; null otherwise.</summary>
    public PerfTool? Perf { get; private set; }

    public override void _Ready()
    {
        _projectGravity = ProjectSettings.GetSetting(GravitySetting, DefaultGravity).AsSingle();

        // Read before the environment and the map loader are made: --no-space-sky and --no-world-edge change both.
        PerfArgs? perf = null;
        string? perfError = null;
        try
        {
            perf = PerfArgs.Parse(OS.GetCmdlineUserArgs());
        }
        catch (ArgumentException ex)
        {
            perfError = ex.Message;
        }

        if (perf is { SpaceSky: false })
        {
            SpaceSkyEnabled = false;
        }

        AddChild(CreateEnvironment());
        AddChild(CreateSun());

        Death = new DeathSequence(GameState.Instance?.Player ?? new PlayerState());
        Death.StateChanged += OnDeathStateChanged;
        DeathCamera = new DeathCamera { Name = "DeathCamera" };
        AddChild(DeathCamera);
        VoidFall = new VoidFall { Name = "VoidFall" };
        AddChild(VoidFall);

        MapLoader = new MapLoader { Name = "MapLoader" };
        if (!string.IsNullOrEmpty(MapId))
        {
            MapLoader.MapId = MapId;
        }

        if (perf is not null)
        {
            MapLoader.WorldEdgeEnabled = perf.WorldEdge;
            if (perf.MapId is { } perfMapId)
            {
                MapLoader.MapId = perfMapId;
            }
        }

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

        if (perfError is not null)
        {
            GD.PushError($"Perf: {perfError}");
            GetTree().Quit(PerfExitCode.Error);
            return;
        }

        if (perf is not null && !DebugBuild.IsDebug)
        {
            GD.PushError($"Perf: {PerfArgs.PerfFlag} needs a debug build");
            GetTree().Quit(PerfExitCode.Error);
            return;
        }

        _screenshotRun = screenshot is not null;
        ResetSpawnRandom(Seed ?? SeedFromCommandLine());

        if (perf is not null)
        {
            // Added before Load() so it hears MapLoaded or MapLoadFailed.
            Perf = new PerfTool { Name = "PerfTool", Request = perf, Loader = MapLoader, World = this };
            AddChild(Perf);
        }

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

    /// <summary>
    /// On the death screen, a new key press (not an echo), gamepad button press or mouse click calls
    /// <see cref="DeathSequence.Dismiss"/>; when that ends the screen (at or after <see cref="MinScreenTime"/>), the event
    /// is marked handled and the respawn runs in the next physics frame. The mouse wheel and joypad motion (sticks and
    /// analog triggers) never count, nor does a <see cref="PauseAction"/> press (it opens the pause menu in a game
    /// started by <c>Main</c>); a press before <see cref="MinScreenTime"/> is ignored, and so is one delivered in the
    /// process frame of a resume from the pause menu (<see cref="OnResumedFromPause"/>).
    /// </summary>
    public override void _Input(InputEvent @event)
    {
        if (Death?.State == DeathState.DeathScreen && IsDismissPress(@event) && !@event.IsAction(PauseAction)
            && _resumeProcessFrame != Engine.GetProcessFrames()
            && Death.Dismiss())
        {
            GetViewport().SetInputAsHandled();
        }
    }

    /// <summary>
    /// Called by <c>Main</c> when the pause menu closes, so the press that closed it never reaches the game: the player
    /// ignores <c>jump</c> and <c>run_toggle</c> until each is seen released (<see cref="Player.SuppressPressesUntilReleased"/>;
    /// a tap shorter than one physics tick would otherwise jump), and the death screen ignores dismiss presses for the
    /// rest of this process frame.
    /// </summary>
    public void OnResumedFromPause()
    {
        _resumeProcessFrame = Engine.GetProcessFrames();
        if (Player is not null && IsInstanceValid(Player))
        {
            Player.SuppressPressesUntilReleased();
        }
    }

    /// <summary>
    /// Whether <paramref name="event"/> may dismiss the death screen: a keyboard key pressed (not an echo), a gamepad
    /// button pressed, or a mouse button pressed (a click; the wheel isn't). Mouse motion, the wheel and joypad motion
    /// (including analog triggers) never do.
    /// </summary>
    public static bool IsDismissPress(InputEvent @event) => @event switch
    {
        InputEventKey { Pressed: true, Echo: false } => true,
        InputEventJoypadButton { Pressed: true } => true,
        InputEventMouseButton { Pressed: true } mouse => !IsWheel(mouse.ButtonIndex),
        _ => false,
    };

    private static bool IsWheel(MouseButton button) =>
        button is MouseButton.WheelUp or MouseButton.WheelDown or MouseButton.WheelLeft or MouseButton.WheelRight;

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
            EmitSignal(SignalName.StartFailed, FailSpawn("no player spawn", LastSpawnFailure));
            return false;
        }

        CreatePlayer(position, facing);
        GD.Print(FormattableString.Invariant(
            $"Player: spawned at ({position.X:0.###}, {position.Y:0.###}, {position.Z:0.###}), seed {Seed}, draw {LastSpawnAttempts}{(_screenshotRun ? ", hidden for the screenshot" : "")}"));
        EmitSignal(SignalName.PlayerSpawned, position);
        return true;
    }

    /// <summary>
    /// "Loading a save", step 4: frees the current player, then places a new one from <paramref name="save"/>. An empty
    /// position uses the spawn draw (not moved). Otherwise the placement check (<see cref="CheckLoadPlacement"/>) at
    /// the saved x/z: passing puts the player on that ground facing the saved yaw, with zero velocity; failing uses
    /// the spawn draw (moved). Then the player camera is current behind it, the street label targets it and
    /// <see cref="SignalName.PlayerLoaded"/> fires. Call it after the terrain collision is queryable; the map load
    /// calls it for <see cref="PendingLoad"/>.
    /// </summary>
    /// <returns>True when the player was placed; false pushes the spawn error and emits <see cref="SignalName.StartFailed"/>.</returns>
    public bool LoadPlayer(SavePlayer save)
    {
        ArgumentNullException.ThrowIfNull(save);
        RemovePlayer();
        LastSpawnAttempts = 0;
        LastSpawnFailure = null;
        var moved = false;
        Vector3 position;
        float facing;
        if (save.Position is { } saved && CheckLoadPlacement((float)saved.X, (float)saved.Z, out position))
        {
            facing = (float)(save.FacingYaw ?? 0d);
        }
        else
        {
            moved = save.Position is not null;
            if (!DrawSpawn(out position, out facing))
            {
                EmitSignal(SignalName.StartFailed, FailSpawn("no player placement for the save", LastSpawnFailure));
                return false;
            }
        }

        CreatePlayer(position, facing);
        GD.Print(FormattableString.Invariant(
            $"Player: loaded at ({position.X:0.###}, {position.Y:0.###}, {position.Z:0.###}){(moved ? ", moved by the spawn draw: the saved spot failed the placement check" : save.Position is null ? ", spawn draw for an empty position" : "")}"));
        EmitSignal(SignalName.PlayerLoaded, position, moved);
        return true;
    }

    /// <summary>
    /// The load placement check ("Loading a save", step 4): a downward ray from above (<paramref name="x"/>,
    /// <paramref name="z"/>) must hit <see cref="MapLoader.TerrainGroup"/> or <see cref="MapLoader.RoadGroup"/> first,
    /// and the player's capsule, <see cref="SpawnDropHeight"/> above that hit, must overlap nothing but terrain and
    /// roads. The player's own body is ignored. Call it from a physics frame or after one.
    /// </summary>
    /// <param name="feet">The passing point (the player's feet, <see cref="SpawnDropHeight"/> above the ground).</param>
    public bool CheckLoadPlacement(float x, float z, out Vector3 feet)
    {
        feet = Vector3.Zero;
        if (!MapLoader.IsInsideTree() || !float.IsFinite(x) || !float.IsFinite(z))
        {
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

        var ray = PhysicsRayQueryParameters3D.Create(new Vector3(x, 10_000f, z), new Vector3(x, -10_000f, z));
        ray.Exclude = excluded;
        var hit = space.IntersectRay(ray);
        if (hit.Count == 0 || hit["collider"].AsGodotObject() is not Node first || !IsGroundOrRoad(first))
        {
            return false;
        }

        var point = hit["position"].AsVector3() + (Vector3.Up * SpawnDropHeight);
        var shape = new PhysicsShapeQueryParameters3D
        {
            Shape = new CapsuleShape3D { Radius = radius, Height = height },
            CollideWithBodies = true,
            CollideWithAreas = false,
            Exclude = excluded,
            Transform = new Transform3D(Basis.Identity, point + (Vector3.Up * (height / 2f))),
        };
        foreach (var overlap in space.IntersectShape(shape, MaxSpawnShapeHits))
        {
            if (overlap["collider"].AsGodotObject() is not Node body || !IsGroundOrRoad(body))
            {
                return false;
            }
        }

        feet = point;
        return true;
    }

    private static bool IsGroundOrRoad(Node node) =>
        node.IsInGroup(MapLoader.TerrainGroup) || node.IsInGroup(MapLoader.RoadGroup);

    /// <summary>
    /// Applies <see cref="CameraSettings"/> to the player camera, when both exist. Called at every spawn, load and
    /// respawn; call it again after the settings change.
    /// </summary>
    public void ApplyCameraSettings()
    {
        if (CameraSettings is { } settings && PlayerCamera is not null && IsInstanceValid(PlayerCamera))
        {
            PlayerCamera.ApplySettings(settings);
        }
    }

    /// <summary>
    /// Adds a new player and its camera at <paramref name="position"/> facing <paramref name="facing"/>, the camera
    /// behind it with <see cref="CameraSettings"/>; outside screenshot runs the camera becomes current (capturing the
    /// mouse) and the street label follows the player.
    /// </summary>
    private void CreatePlayer(Vector3 position, float facing)
    {
        var player = new OnFoot.Player { Name = "Player", Visible = !_screenshotRun };
        var camera = new PlayerCamera { Name = "PlayerCamera", Target = player };
        player.Camera = camera;
        AddChild(player);
        AddChild(camera);
        Player = player;
        PlayerCamera = camera;
        ApplyCameraSettings();
        player.PlaceAt(position, facing);
        camera.PlaceBehind(facing);

        if (!_screenshotRun)
        {
            camera.Activate();
            Hud.Target = player;
        }
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

    /// <summary>Hands the loose pieces of a broken barrier item to the safety net (<see cref="TrackVoidBody"/>).</summary>
    private void TrackLoosePieces(Barriers barriers, string itemId)
    {
        if (!IsInstanceValid(barriers))
        {
            return;
        }

        foreach (var piece in barriers.LoosePiecesOf(itemId))
        {
            if (piece.IsInsideTree())
            {
                TrackVoidBody(piece);
            }
        }
    }

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
        ApplyCameraSettings();
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
        MinScreenTime = ValidEdgeExport(nameof(MinScreenTime), MinScreenTime, ref _validEdgeExports.MinScreenTime);
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
        Death.MinScreenTime = MinScreenTime;
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
        public float MinScreenTime;
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

        if (MapLoader.Barriers is { } barriers)
        {
            barriers.ItemBroken += (itemId, _) => TrackLoosePieces(barriers, itemId);
        }

        RemovePlayer();
        SpawnWhenCollisionReadyAsync(++_spawnGeneration);
    }

    private void OnMapLoadFailed(string message)
    {
        _spawnGeneration++;
        ResetWorldEdgeState();
        RemovePlayer();
        EmitSignal(SignalName.StartFailed, $"MapLoader: {message}");
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
            EmitSignal(SignalName.StartFailed, FailSpawn("no player spawn", LastSpawnFailure));
            return;
        }

        if (PendingLoad is { } load)
        {
            PendingLoad = null;
            LoadPlayer(load);
            return;
        }

        SpawnPlayer();
    }

    /// <summary>
    /// Pushes the spawn failure error: <paramref name="what"/>, the <paramref name="reason"/> (which names the boundary
    /// file) and the regen hint.
    /// </summary>
    /// <returns>The error text pushed.</returns>
    private string FailSpawn(string what, string? reason)
    {
        reason ??= $"no draw from the spawn area in {MapLoader.BoundaryPath} passed";
        var message = $"World: {what}: {reason}; {MapPack.RegenHint(MapLoader.ResolvedMapId)}";
        GD.PushError(message);
        return message;
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

        return TimeSeed();
    }

    /// <summary>A spawn-draw seed from the current time, used when no <c>--seed</c> is given.</summary>
    public static int TimeSeed() => unchecked((int)DateTime.UtcNow.Ticks);

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
        // keeps the former procedural ground, so ambient and reflected light are unchanged. Without the space sky:
        // ProceduralSkyMaterial's defaults, which the shader's day sky copies.
        Material skyMaterial = SpaceSkyEnabled
            ? new ShaderMaterial { Shader = GD.Load<Shader>(SkyShaderPath) }
            : new ProceduralSkyMaterial();
        var sky = new Sky { SkyMaterial = skyMaterial };
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
