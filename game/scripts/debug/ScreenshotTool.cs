using System;
using Directory = System.IO.Directory;
using IOException = System.IO.IOException;
using Path = System.IO.Path;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Roadman.Autoload;
using Roadman.Core.Edge;
using Roadman.Core.Items;
using Roadman.Core.Maps;
using Roadman.Core.Saves;
using Roadman.Core.Tools;
using Roadman.Hud;
using Roadman.Scenes;

namespace Roadman.Debug;

/// <summary>
/// Renders the world from a named camera preset to a PNG and quits:
/// <c>godot --path game --resolution 1600x900 -- --screenshot &lt;preset&gt; &lt;out.png&gt; [--presets &lt;file&gt;] [--map &lt;map-id&gt;] [--orthophoto] [--road-graph] [--inspect] [--player] [--death &lt;fall|screen&gt;] [--menu &lt;main|pause|settings|slots|inventory&gt;] [--give &lt;id,...&gt;] [--seed &lt;n&gt;]</c>.
/// <para>
/// The image comes from its own <see cref="SubViewport"/> of <see cref="ImageSize"/>, so it doesn't depend on the
/// window size, display scaling or where the window is. Rendering needs a real display server: the tool refuses to
/// run under <c>--headless</c>. Exit codes are in <see cref="ScreenshotExitCode"/>.
/// </para>
/// <para>
/// With <c>--player</c> (debug builds only), after the usual checks the spawned (hidden) player is placed on the ground
/// at the preset's look-at x/z (<see cref="MapLoader.GroundOrRoadHitAt"/>), facing away from the preset's camera, and
/// shown; the street label follows it. Once it is on the floor, the capture camera copies the player camera's transform
/// and field of view every frame for <see cref="SettleFrames"/> frames, so the image is the third-person view. The
/// player camera itself never becomes current: the capture camera renders in its own viewport.
/// </para>
/// <para>
/// With <c>--death &lt;fall|screen&gt;</c> (debug builds only; <c>docs/specs/world-edge-game.md</c>, "Screenshots"), after
/// the usual checks the spawned player is put at the ring point nearest the preset's look-at x/z,
/// <see cref="EdgeGeometry.VoidInnerOffset"/> + <see cref="DeathOutwardMargin"/> outward, at the ledge's height
/// (<see cref="EdgeGeometry.LedgeHeightAt"/>), facing outward, which starts the death sequence. <c>fall</c> holds the
/// sequence in <see cref="DeathState.Falling"/> (<see cref="Scenes.World.FallTime"/> set to <see cref="DeathHoldTime"/>)
/// and captures the <see cref="Scenes.Edge.DeathCamera"/>'s frame once its pull-back has ended; <c>screen</c> holds it on
/// the death screen (<see cref="Scenes.World.ScreenTime"/> set to <see cref="DeathHoldTime"/>), moves the
/// <see cref="Hud.DeathScreen"/> into the capture viewport and captures it once it has faded in. The capture camera copies
/// the death camera's transform and field of view every settle frame; the death camera is current only in the window's
/// viewport, whose 3D view isn't rendered.
/// </para>
/// <para>
/// With <c>--menu &lt;main|pause|settings|slots|inventory&gt;</c> (debug builds only; <c>docs/specs/menu-settings-save.md</c>,
/// "Screenshots"), after the usual checks the tool writes sample saves to a new temporary folder
/// (<see cref="WriteSampleSaves"/>), points <c>Main</c>'s slots at it, moves <c>Main.Ui</c> into the capture viewport
/// (with the window's 2D scale, <see cref="MenuCanvasSize"/>, so the menu is laid out as in the game) and asks
/// <c>Main</c> to show the view (<see cref="Main.ShowMenuForScreenshot"/>; all but <c>main</c> pause the tree, so the
/// tool and its viewport then run with <see cref="Node.ProcessModeEnum.Always"/>). The folder is deleted when the run
/// finishes. <c>Main</c> must be the world's parent (the main scene), else exit 1. <c>--menu inventory</c>
/// (<c>docs/specs/inventory.md</c>, "Screenshots") first waits for the spawned player, which got the <c>--give</c> items
/// (<see cref="Scenes.World.GivenItems"/>), equips the first weapon and the first clothing item given, and shows the
/// inventory screen as opened from the game; without <c>--give</c> it is the empty screen.
/// </para>
/// </summary>
public partial class ScreenshotTool : Node
{
    /// <summary>Size of the saved image in pixels.</summary>
    [Export]
    public Vector2I ImageSize { get; set; } = new(1600, 900);

    /// <summary>Rendered frames to wait after placing the camera, so shadows and exposure settle.</summary>
    [Export]
    public int SettleFrames { get; set; } = 30;

    /// <summary>Physics frames to wait for the terrain collision before giving up.</summary>
    [Export]
    public int MaxPhysicsWaitFrames { get; set; } = 30;

    /// <summary>Largest per-channel difference that still counts as one flat colour.</summary>
    [Export]
    public int FlatColourTolerance { get; set; } = 2;

    [Export]
    public float CameraFar { get; set; } = 8000f;

    /// <summary>
    /// <c>--player</c>: physics frames to wait for the player beyond <see cref="Scenes.World.SpawnPhysicsWaitFrames"/>
    /// (the world's own wait for the terrain collision before it draws a spawn point).
    /// <para>
    /// Why 10: the world starts its wait on <see cref="MapLoader.MapLoaded"/> and spawns synchronously in the frame the
    /// wait succeeds, so it has a player at most <see cref="Scenes.World.SpawnPhysicsWaitFrames"/> physics frames after
    /// the load. The tool starts counting later (after its own terrain-collision wait, which also starts on the load),
    /// so its count is never ahead of the world's; the one frame the two awaits on the same physics frame signal can
    /// resume apart in is all the margin needs. 10 is that frame with a tenfold allowance, and costs nothing when the
    /// spawn succeeds (the loop ends as soon as the player exists or <see cref="Scenes.World.LastSpawnFailure"/> is set).
    /// </para>
    /// </summary>
    [Export]
    public int PlayerSpawnMarginFrames { get; set; } = 10;

    /// <summary>
    /// <c>--player</c>: physics frames to wait for the placed player to be on the floor.
    /// <para>
    /// Why 60 (1 s at the default 60 physics ticks per second): the tool puts the player's feet on the ground hit itself
    /// (<see cref="MapLoader.GroundOrRoadHitAt"/>), so it is on the floor after the first <c>MoveAndSlide</c>. The
    /// slowest landing the player otherwise makes is the spawn drop of <see cref="Scenes.World.SpawnDropHeight"/>
    /// (0.5 m) under the project gravity (9.8 m/s²): √(2 · 0.5 / 9.8) ≈ 0.32 s ≈ 20 frames. 60 frames is three times
    /// that, so a placement a little above the surface still lands in time; the loop ends at the first frame on the
    /// floor, so the wait costs nothing when it succeeds.
    /// </para>
    /// </summary>
    [Export]
    public int PlayerFloorWaitFrames { get; set; } = 60;

    /// <summary>
    /// <c>--death</c>: how far beyond the void trigger's inner edge (<see cref="EdgeGeometry.VoidInnerOffset"/>) the player
    /// is put, m (the spec's <c>void.inner_offset_m</c> + 1 m).
    /// </summary>
    [Export]
    public float DeathOutwardMargin { get; set; } = 1f;

    /// <summary>
    /// <c>--death</c>: what <see cref="Scenes.World.FallTime"/> (<c>fall</c>) or <see cref="Scenes.World.ScreenTime"/>
    /// (<c>screen</c>) is set to, s, so the sequence holds in the captured stage while the image settles; also
    /// <see cref="Scenes.World.MinScreenTime"/> (both), so no key press in the window dismisses the death screen.
    /// </summary>
    [Export]
    public float DeathHoldTime { get; set; } = 3600f;

    /// <summary>
    /// <c>--death</c>: physics frames to wait for the death sequence to start after the player is placed. The world tests
    /// the player against the void prisms each physics frame, so it starts in the first; 10 is a tenfold allowance.
    /// </summary>
    [Export]
    public int DeathStartWaitFrames { get; set; } = 10;

    /// <summary>
    /// <c>--death</c>: seconds beyond the stage's own length (the pull-back, or the fall plus the fade) to wait for it.
    /// </summary>
    [Export]
    public float DeathStageMarginSeconds { get; set; } = 5f;

    /// <summary>
    /// Whether <see cref="Finish"/> quits the scene tree with the exit code (the command-line run). Tests set it false
    /// and read <see cref="ExitCode"/> instead, so the test runner keeps running.
    /// </summary>
    [Export]
    public bool QuitOnFinish { get; set; } = true;

    /// <summary>The exit code the run finished with (<see cref="ScreenshotExitCode"/>); null while it is still running.</summary>
    public int? ExitCode { get; private set; }

    /// <summary>The parsed command line; set before adding the node.</summary>
    public ScreenshotArgs? Request { get; set; }

    /// <summary>
    /// The loader whose map is photographed; set before adding the node and before it loads. With <c>--map</c>, the
    /// tool sets the loader's <see cref="MapLoader.MapId"/> when it is added.
    /// </summary>
    public MapLoader? Loader { get; set; }

    /// <summary>
    /// The orthophoto view switched on for <c>--orthophoto</c>; its overlay is moved into the capture viewport so the
    /// attribution is in the image. Null outside debug builds, where <c>--orthophoto</c> is an error.
    /// </summary>
    public OrthophotoDebugView? OrthophotoView { get; set; }

    /// <summary>
    /// The road graph view switched on for <c>--road-graph</c>; its legend is moved into the capture viewport. Null
    /// outside debug builds, where <c>--road-graph</c> is an error.
    /// </summary>
    public RoadGraphDebugView? RoadGraphView { get; set; }

    /// <summary>
    /// The HUD street label; moved into the capture viewport so the label is in every image, measured from the
    /// capture camera. Null leaves the image without it.
    /// </summary>
    public StreetLabelHud? Hud { get; set; }

    /// <summary>
    /// The place inspector switched on for <c>--inspect</c>; its panel and crosshair are moved into the capture
    /// viewport, and its ray starts from the capture camera, through the preset's look-at point. Null outside debug
    /// builds, where <c>--inspect</c> is an error.
    /// </summary>
    public PlaceInspector? Inspector { get; set; }

    /// <summary>
    /// The world whose <see cref="Scenes.World.Player"/> and <see cref="Scenes.World.PlayerCamera"/> <c>--player</c>
    /// photographs (and whose <see cref="Scenes.World.LastSpawnFailure"/> it reports). Set before adding the node;
    /// <c>--player</c> without it is an error.
    /// </summary>
    public World? World { get; set; }

    /// <summary>
    /// <c>--menu</c>: when the samples were saved (<see cref="WriteSampleSaves"/>): the autosave, then slot 1. Shown in
    /// UTC.
    /// </summary>
    public static readonly DateTimeOffset[] SampleSavedAt =
    [
        new(2026, 11, 5, 14, 30, 0, TimeSpan.Zero),
        new(2026, 11, 4, 20, 15, 0, TimeSpan.Zero),
    ];

    private string? _menuSavesFolder;
    private CameraPreset? _preset;
    private string _outputPath = "";
    private SubViewport? _viewport;
    private Camera3D? _camera;
    private bool _finished;

    public override void _Ready()
    {
        if (Request is null || Loader is null)
        {
            Finish(ScreenshotExitCode.Error, "ScreenshotTool needs Request and Loader set before it is added");
            return;
        }

        // Before anything reads Loader.PackPath: the presets file and the tiles both come from this pack.
        if (Request.MapId is { } mapId)
        {
            Loader.MapId = mapId;
        }

        // The build checks need neither rendering nor the pack, so they come before the headless refusal: the release
        // case is testable headless, and the exit code (Error) is the same either way.
        if (Request.Orthophoto && OrthophotoView is null)
        {
            Finish(ScreenshotExitCode.Error, "--orthophoto needs the orthophoto debug view, which only debug builds have");
            return;
        }

        if (Request.RoadGraph && RoadGraphView is null)
        {
            Finish(ScreenshotExitCode.Error, "--road-graph needs the road graph debug view, which only debug builds have");
            return;
        }

        if (Request.Inspect && Inspector is null)
        {
            Finish(ScreenshotExitCode.Error, "--inspect needs the place inspector, which only debug builds have");
            return;
        }

        if (Request.Player && !DebugBuild.IsDebug)
        {
            Finish(ScreenshotExitCode.Error, "--player needs a debug build");
            return;
        }

        if (Request.Death is not null && !DebugBuild.IsDebug)
        {
            Finish(ScreenshotExitCode.Error, $"{ScreenshotArgs.DeathFlag} needs a debug build");
            return;
        }

        if (Request.Menu is not null && !DebugBuild.IsDebug)
        {
            Finish(ScreenshotExitCode.Error, $"{ScreenshotArgs.MenuFlag} needs a debug build");
            return;
        }

        if (DisplayServer.GetName() == "headless")
        {
            Finish(
                ScreenshotExitCode.Error,
                "rendering is disabled with --headless; run without it (see game/CLAUDE.md, \"Screenshots\")");
            return;
        }

        var launchDirectory = OS.GetEnvironment("PWD");
        try
        {
            _outputPath = OutputPath.Resolve(Request.OutputPath, launchDirectory);
            _preset = LoadPreset(Request, Loader, launchDirectory);
        }
        catch (Exception ex) when (ex is ArgumentException or MapPackException)
        {
            Finish(ScreenshotExitCode.Error, ex.Message);
            return;
        }

        if (_preset is null)
        {
            return;
        }

        if (Request.Player && World is null)
        {
            Finish(ScreenshotExitCode.Error, "--player needs the World set on the ScreenshotTool before it is added");
            return;
        }

        if (Request.Death is not null && World is null)
        {
            Finish(ScreenshotExitCode.Error, $"{ScreenshotArgs.DeathFlag} needs the World set on the ScreenshotTool before it is added");
            return;
        }

        CreateViewport();
        Loader.MapLoaded += OnMapLoaded;
        Loader.MapLoadFailed += OnMapLoadFailed;
    }

    public override void _ExitTree()
    {
        if (Loader is not null && IsInstanceValid(Loader))
        {
            Loader.MapLoaded -= OnMapLoaded;
            Loader.MapLoadFailed -= OnMapLoadFailed;
        }

        DeleteMenuSaves();
    }

    private CameraPreset? LoadPreset(ScreenshotArgs request, MapLoader loader, string launchDirectory)
    {
        var path = request.PresetsPath is { } custom
            ? OutputPath.Resolve(custom, launchDirectory)
            : $"{loader.PackPath}/{MapPack.CameraPresetsFile}";
        if (!FileAccess.FileExists(path))
        {
            var hint = request.PresetsPath is null ? $"; {MapPack.RegenHint(loader.ResolvedMapId)}" : "";
            Finish(ScreenshotExitCode.Error, $"camera presets file {path} doesn't exist{hint}");
            return null;
        }

        var presets = CameraPresetsReader.Parse(FileAccess.GetFileAsString(path), path);
        if (!presets.Presets.TryGetValue(request.Preset, out var preset))
        {
            var known = presets.Presets.Count == 0 ? "none" : string.Join(", ", presets.Presets.Keys.Order());
            Finish(ScreenshotExitCode.Error, $"unknown camera preset '{request.Preset}' in {path} (presets: {known})");
            return null;
        }

        return preset;
    }

    private void CreateViewport()
    {
        // Shares the main viewport's World3D (its world_3d is left empty), so it sees the map, sun and environment.
        _viewport = new SubViewport
        {
            Name = "Capture",
            Size = ImageSize,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
            TransparentBg = false,
        };
        _camera = new Camera3D { Name = "Camera", Far = CameraFar, Current = true };
        _viewport.AddChild(_camera);
        AddChild(_viewport);

        // A CanvasLayer draws only in its own viewport, and the HUD measures from its viewport's camera.
        if (Hud is not null)
        {
            Hud.GetParent()?.RemoveChild(Hud);
            _viewport.AddChild(Hud);
        }

        // The window's own 3D view isn't saved; skip rendering it.
        GetViewport().Disable3D = true;
    }

    private void OnMapLoadFailed(string message) => Finish(ScreenshotExitCode.Error, $"map didn't load: {message}");

    private void OnMapLoaded(int tileCount)
    {
        var manifestTiles = Loader!.Manifest?.Tiles.Count;
        if (ScreenshotExitCode.AfterMapLoad(tileCount, manifestTiles) is { } exitCode)
        {
            // An incomplete map must not end as "outside built area": the missing terrain may be a missing tile.
            Finish(
                exitCode,
                $"map didn't load completely: {tileCount} of {manifestTiles?.ToString() ?? "?"} tiles from {Loader.PackPath}; "
                + MapPack.RegenHint(Loader.ResolvedMapId));
            return;
        }

        if (Request!.Orthophoto && !EnableOrthophoto())
        {
            return;
        }

        if (Request.RoadGraph && !EnableRoadGraph())
        {
            return;
        }

        if (Request.Inspect && !EnableInspector())
        {
            return;
        }

        CaptureAsync();
    }

    /// <summary>Switches the orthophoto view on, with its overlay drawn in the capture viewport.</summary>
    private bool EnableOrthophoto()
    {
        var view = OrthophotoView!;
        // A CanvasLayer draws only in its own viewport; in the window's it would be missing from the image.
        view.AttachOverlayTo(_viewport!);
        if (view.SetEnabled(true))
        {
            return true;
        }

        Finish(
            ScreenshotExitCode.Error,
            $"--orthophoto: no orthophoto images in {view.ResolvedTextureFolder}; {MapPack.RegenHint(Loader!.ResolvedMapId)}");
        return false;
    }

    /// <summary>Switches the road graph view on, with its legend drawn in the capture viewport.</summary>
    private bool EnableRoadGraph()
    {
        var view = RoadGraphView!;
        view.AttachOverlayTo(_viewport!);
        if (view.SetEnabled(true))
        {
            return true;
        }

        Finish(
            ScreenshotExitCode.Error,
            $"--road-graph: no readable road graph at {Loader!.RoadGraphPath}; {MapPack.RegenHint(Loader.ResolvedMapId)}");
        return false;
    }

    /// <summary>
    /// Switches the place inspector on, with its panel and crosshair drawn in the capture viewport. Its ray then
    /// starts from the capture camera, through the screen centre, which the camera aims at the preset's look-at point.
    /// </summary>
    private bool EnableInspector()
    {
        var inspector = Inspector!;
        if (Loader!.Places is null)
        {
            Finish(
                ScreenshotExitCode.Error,
                $"--inspect: no readable place data at {Loader.BuildingsPath} and {Loader.PoisPath}; {MapPack.RegenHint(Loader.ResolvedMapId)}");
            return false;
        }

        inspector.Camera = _camera;
        inspector.AttachOverlayTo(_viewport!);
        return inspector.SetEnabled(true);
    }

    private async void CaptureAsync()
    {
        var loader = Loader!;
        var preset = _preset!;
        var terrainReady = await loader.WaitForTerrainCollisionAsync(MaxPhysicsWaitFrames);
        if (_finished || !IsInsideTree())
        {
            return;
        }

        // A collision that never became queryable means the map or its collision didn't load: an error for every
        // preset, never N/A. ADR 0008: with a test-area pack, a preset outside the area is N/A. Every tile loaded
        // (checked in OnMapLoaded) and the collision is queryable, so a downward ray at the look-at x/z that hits
        // neither the terrain nor the world-edge cliff looks at no map: the image would show empty sky or the void.
        var lookAtCheck = PresetPlacement.CheckLookAt(
            preset, terrainReady, (x, z) => loader.GroundOrEdgeHitAt((float)x, (float)z)?.Position.Y);
        if (lookAtCheck == LookAtCheck.CollisionNotReady)
        {
            Finish(
                ScreenshotExitCode.Error,
                $"no terrain collision in {loader.PackPath} after {MaxPhysicsWaitFrames} physics frames; {MapPack.RegenHint(loader.ResolvedMapId)}");
            return;
        }

        if (lookAtCheck == LookAtCheck.OutsideBuiltArea)
        {
            var lookAt = FormattableString.Invariant($"x {preset.LookAt.X:0.###}, z {preset.LookAt.Z:0.###}");
            GD.Print(
                $"N/A: outside built area (preset '{preset.Name}' looks at {lookAt}: a downward ray there hits neither "
                + $"the terrain ({MapLoader.TerrainGroup}) nor the world-edge cliff ({MapLoader.EdgeGroup}))");
            Finish(ScreenshotExitCode.OutsideBuiltArea, null);
            return;
        }

        var placement = PresetPlacement.Resolve(preset, (x, z) => loader.TerrainHeightAt((float)x, (float)z));
        if (placement.IsOutsideBuiltArea)
        {
            var position = FormattableString.Invariant($"x {preset.Position.X:0.###}, z {preset.Position.Z:0.###}");
            GD.Print($"N/A: outside built area (preset '{preset.Name}' has no terrain under its camera at {position})");
            Finish(ScreenshotExitCode.OutsideBuiltArea, null);
            return;
        }

        PlaceCamera(ToVector(placement.Position), ToVector(placement.LookAt), (float)preset.Fov);
        var camera = _camera!.GlobalPosition;
        var target = ToVector(placement.LookAt);
        GD.Print(FormattableString.Invariant(
            $"Screenshot: preset '{preset.Name}' camera at ({camera.X:0.###}, {camera.Y:0.###}, {camera.Z:0.###}), looking at ({target.X:0.###}, {target.Y:0.###}, {target.Z:0.###})"));

        if (Request!.Death is { } death)
        {
            await CaptureDeathAsync(death, placement);
            return;
        }

        if (Request.Menu is { } menu)
        {
            await CaptureMenuAsync(menu, placement);
            return;
        }

        if (Request.Player)
        {
            if (await PlacePlayerAsync(placement) is not { } playerCamera)
            {
                return;
            }

            // Copy before each frame, so the last frame drawn before Save() uses the latest third-person transform.
            for (var i = 0; i < SettleFrames; i++)
            {
                _camera!.GlobalTransform = playerCamera.Camera.GlobalTransform;
                _camera.Fov = playerCamera.Camera.Fov;
                await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                if (_finished || !IsInsideTree())
                {
                    return;
                }
            }

            Save();
            return;
        }

        for (var i = 0; i < SettleFrames; i++)
        {
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            if (_finished || !IsInsideTree())
            {
                return;
            }
        }

        // Spec criterion 18: the --inspect image must show the panel on a building. The inspector casts its ray each
        // physics frame, so after the settle frames its selection is the building at the screen centre, if any. With
        // the whole map loaded, a centre ray that hits nothing has left the built area: N/A, like a preset outside it.
        if (Request.Inspect && Inspector!.SelectedBuilding is null && !Inspector.LastRayHit)
        {
            GD.Print($"N/A: outside built area (the centre ray of preset '{preset.Name}' leaves the built area without hitting a building)");
            Finish(ScreenshotExitCode.OutsideBuiltArea, null);
            return;
        }

        if (Request.Inspect && Inspector!.SelectedBuilding is null)
        {
            Finish(
                ScreenshotExitCode.Error,
                $"--inspect: the centre ray of preset '{preset.Name}' selects no building "
                + $"({(Inspector.SelectionId is { } id ? $"selected {id}" : $"panel '{Inspector.PanelText.Split('\n')[0]}'")}); "
                + "aim the preset's look_at at a building");
            return;
        }

        Save();
    }

    /// <summary>
    /// <c>--player</c>: waits for the world's player, puts it on the ground (the higher of the terrain and road hits) at
    /// the look-at x/z facing away from the preset camera, shows it, turns its camera behind it, points the street label
    /// at it and waits until it is on the floor.
    /// </summary>
    /// <returns>The player camera to copy, or null after finishing with an error.</returns>
    private async Task<OnFoot.PlayerCamera?> PlacePlayerAsync(PresetResolution placement)
    {
        var world = World!;
        var loader = Loader!;
        var tree = GetTree();
        if (!await WaitForPlayerAsync(ScreenshotArgs.PlayerFlag))
        {
            return null;
        }

        var player = world.Player!;
        var playerCamera = world.PlayerCamera!;
        var lookAt = ToVector(placement.LookAt);
        if (loader.GroundOrRoadHitAt(lookAt.X, lookAt.Z) is not { } ground)
        {
            Finish(
                ScreenshotExitCode.Error,
                FormattableString.Invariant(
                    $"--player: no terrain or road under the look-at point x {lookAt.X:0.###}, z {lookAt.Z:0.###} ({MapLoader.TerrainGroup}, {MapLoader.RoadGroup})"));
            return null;
        }

        // Away from the camera: Godot's yaw 0 looks along −Z, forward = (−sin yaw, 0, −cos yaw).
        var away = lookAt - ToVector(placement.Position);
        var facing = new Vector2(away.X, away.Z).LengthSquared() > 1e-6f ? Mathf.Atan2(-away.X, -away.Z) : playerCamera.Yaw;

        player.InputEnabled = false;
        playerCamera.InputEnabled = false;
        player.PlaceAt(ground.Position, facing);
        player.Visible = true;
        playerCamera.PlaceBehind(facing);
        if (Hud is not null)
        {
            Hud.Target = player;
        }

        var feet = ground.Position;
        GD.Print(FormattableString.Invariant(
            $"Screenshot: --player at ({feet.X:0.###}, {feet.Y:0.###}, {feet.Z:0.###}), facing {Mathf.RadToDeg(facing):0.###}°"));

        for (var frame = 0; frame < PlayerFloorWaitFrames; frame++)
        {
            await ToSignal(tree, SceneTree.SignalName.PhysicsFrame);
            if (_finished || !IsInsideTree())
            {
                return null;
            }

            if (!IsInstanceValid(player) || !IsInstanceValid(playerCamera))
            {
                Finish(ScreenshotExitCode.Error, "--player: the player was freed before the capture");
                return null;
            }

            if (player.IsOnFloor())
            {
                return playerCamera;
            }
        }

        Finish(
            ScreenshotExitCode.Error,
            FormattableString.Invariant(
                $"--player: the player isn't on the floor after {PlayerFloorWaitFrames} physics frames at ({feet.X:0.###}, {feet.Y:0.###}, {feet.Z:0.###})"));
        return null;
    }

    /// <summary>
    /// Waits for the world's spawned player (up to <see cref="Scenes.World.SpawnPhysicsWaitFrames"/> +
    /// <see cref="PlayerSpawnMarginFrames"/> physics frames, or until the spawn failed); finishes with an error naming
    /// <paramref name="flag"/> and <see cref="Scenes.World.LastSpawnFailure"/> when there is none.
    /// </summary>
    private async Task<bool> WaitForPlayerAsync(string flag)
    {
        var world = World!;
        var maxSpawnFrames = world.SpawnPhysicsWaitFrames + PlayerSpawnMarginFrames;
        var tree = GetTree();
        for (var frame = 0; !HasPlayer(world) && world.LastSpawnFailure is null && frame < maxSpawnFrames; frame++)
        {
            await ToSignal(tree, SceneTree.SignalName.PhysicsFrame);
            if (_finished || !IsInsideTree())
            {
                return false;
            }
        }

        if (HasPlayer(world))
        {
            return true;
        }

        var reason = world.LastSpawnFailure
            ?? FormattableString.Invariant($"no spawn within {maxSpawnFrames} physics frames");
        Finish(ScreenshotExitCode.Error, $"{flag}: the player didn't spawn ({reason}); {MapPack.RegenHint(Loader!.ResolvedMapId)}");
        return false;
    }

    /// <summary>
    /// <c>--death</c>: holds the stage to capture, puts the player in the void beside the ring point nearest the preset's
    /// look-at, waits for the stage (the pull-back's end, or the death screen faded in), then copies the death camera into
    /// the capture camera for <see cref="SettleFrames"/> frames and saves.
    /// </summary>
    private async Task CaptureDeathAsync(DeathShot death, PresetResolution placement)
    {
        var world = World!;
        var loader = Loader!;
        var flag = $"{ScreenshotArgs.DeathFlag} {(death == DeathShot.Fall ? ScreenshotArgs.DeathFallValue : ScreenshotArgs.DeathScreenValue)}";
        if (loader.EdgeGeometry is not { } geometry)
        {
            Finish(
                ScreenshotExitCode.Error,
                $"{flag}: no world edge (no readable {loader.BoundaryPath}, or MapLoader.WorldEdgeEnabled is false); {MapPack.RegenHint(loader.ResolvedMapId)}");
            return;
        }

        if (geometry.Boundary.Roadblocks.Count + geometry.Boundary.Fences.Count == 0)
        {
            Finish(ScreenshotExitCode.Error, $"{flag}: {loader.BoundaryPath} has no roadblocks or fences, so there is no ledge height");
            return;
        }

        if (!await WaitForPlayerAsync(flag))
        {
            return;
        }

        // A key pressed in the open window must not dismiss the death screen while the image settles.
        world.MinScreenTime = DeathHoldTime;
        if (death == DeathShot.Fall)
        {
            world.FallTime = DeathHoldTime;
        }
        else
        {
            world.ScreenTime = DeathHoldTime;
            var screen = world.DeathScreen;
            screen.GetParent()?.RemoveChild(screen);
            _viewport!.AddChild(screen);
        }

        var lookAt = ToVector(placement.LookAt);
        var ring = geometry.NearestRingPoint(lookAt.X, lookAt.Z);
        var ringOutward = geometry.OutwardAt(ring.Point.X, ring.Point.Z);
        var outward = new Vector3((float)ringOutward.X, 0f, (float)ringOutward.Z);
        var distance = (float)geometry.VoidInnerOffset + DeathOutwardMargin;
        var position = new Vector3((float)ring.Point.X, (float)geometry.LedgeHeightAt(ring.S), (float)ring.Point.Z) + (outward * distance);
        var facing = Mathf.Atan2(-outward.X, -outward.Z);

        var player = world.Player!;
        var playerCamera = world.PlayerCamera!;
        player.InputEnabled = false;
        playerCamera.InputEnabled = false;
        player.PlaceAt(position, facing);
        player.Visible = true;
        playerCamera.PlaceBehind(facing);
        GD.Print(FormattableString.Invariant(
            $"Screenshot: {flag} at ({position.X:0.###}, {position.Y:0.###}, {position.Z:0.###})"));

        var tree = GetTree();
        for (var frame = 0; world.Death.State == DeathState.Alive && frame < DeathStartWaitFrames; frame++)
        {
            await ToSignal(tree, SceneTree.SignalName.PhysicsFrame);
            if (_finished || !IsInsideTree())
            {
                return;
            }
        }

        if (world.Death.State == DeathState.Alive)
        {
            Finish(
                ScreenshotExitCode.Error,
                FormattableString.Invariant(
                    $"{flag}: the death sequence didn't start within {DeathStartWaitFrames} physics frames of placing the player at ({position.X:0.###}, {position.Y:0.###}, {position.Z:0.###})"));
            return;
        }

        var deathCamera = world.DeathCamera;
        var stageSeconds = death == DeathShot.Fall
            ? deathCamera.PullBackTime
            : world.FallTime + world.DeathScreen.FadeTime;
        var maxFrames = Mathf.CeilToInt((stageSeconds + DeathStageMarginSeconds) * Engine.PhysicsTicksPerSecond);
        bool Reached() => death == DeathShot.Fall
            ? world.Death.State == DeathState.Falling && deathCamera.IsFinished
            : world.Death.State == DeathState.DeathScreen && world.DeathScreen.Opacity >= 1f;
        for (var frame = 0; !Reached() && frame < maxFrames; frame++)
        {
            await ToSignal(tree, SceneTree.SignalName.PhysicsFrame);
            if (_finished || !IsInsideTree())
            {
                return;
            }
        }

        if (!Reached())
        {
            Finish(
                ScreenshotExitCode.Error,
                $"{flag}: the stage wasn't reached within {maxFrames} physics frames (death state {world.Death.State}, pull-back {(deathCamera.IsFinished ? "finished" : "running")}, death screen opacity {world.DeathScreen.Opacity.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)})");
            return;
        }

        for (var i = 0; i < SettleFrames; i++)
        {
            _camera!.GlobalTransform = deathCamera.GlobalTransform;
            _camera.Fov = deathCamera.Fov;
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            if (_finished || !IsInsideTree())
            {
                return;
            }
        }

        if (!Reached())
        {
            Finish(ScreenshotExitCode.Error, $"{flag}: the death sequence left the stage while the image settled (death state {world.Death.State})");
            return;
        }

        Save();
    }

    /// <summary>
    /// <c>--menu</c>: sample saves in a temporary folder, <c>Main</c>'s UI in the capture viewport, the view shown, then
    /// <see cref="SettleFrames"/> frames and the save.
    /// </summary>
    private async Task CaptureMenuAsync(MenuShot menu, PresetResolution placement)
    {
        var flag = $"{ScreenshotArgs.MenuFlag} {ScreenshotArgs.MenuValue(menu)}";
        if (World?.GetParent() is not Main main || main.Ui is null || main.SettingsView is null)
        {
            Finish(ScreenshotExitCode.Error, $"{flag}: needs Main (the main scene) as the world's parent, with its UI built");
            return;
        }

        var loader = Loader!;
        _menuSavesFolder = Path.Combine(Path.GetTempPath(), $"roadman-menu-shot-{Guid.NewGuid():N}");
        try
        {
            WriteSampleSaves(
                _menuSavesFolder,
                loader.Manifest?.MapId ?? loader.ResolvedMapId,
                loader.Manifest?.GeneratedAt,
                ToVector(placement.LookAt));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Finish(ScreenshotExitCode.Error, $"{flag}: can't write the sample saves to {_menuSavesFolder}: {ex.Message}");
            return;
        }

        main.UseSavesFolder(_menuSavesFolder);

        // The menus are laid out in the window's 2D space (the project's base size, stretched); give the capture the
        // same space, so the image shows them as the game does.
        _viewport!.Size2DOverride = MenuCanvasSize(ImageSize);
        _viewport.Size2DOverrideStretch = true;
        var ui = main.Ui;
        ui.GetParent()?.RemoveChild(ui);
        _viewport.AddChild(ui);

        if (menu == MenuShot.Inventory && !await GiveAndEquipForInventoryAsync(flag))
        {
            return;
        }

        // Pausing stops the world; the tool's waits and its viewport must go on.
        ProcessMode = ProcessModeEnum.Always;
        _viewport.ProcessMode = ProcessModeEnum.Always;
        if (!main.ShowMenuForScreenshot(menu))
        {
            Finish(ScreenshotExitCode.Error, $"{flag}: Main didn't show the menu (not a --menu direct start)");
            return;
        }

        GD.Print($"Screenshot: {flag}");
        for (var i = 0; i < SettleFrames; i++)
        {
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            if (_finished || !IsInsideTree())
            {
                return;
            }
        }

        Save();
    }

    /// <summary>
    /// <c>--menu inventory</c>: waits for the spawned player (the world applies <c>--give</c> at its placement), then
    /// equips the first weapon and the first clothing item of <see cref="Scenes.World.GivenItems"/>.
    /// </summary>
    private async Task<bool> GiveAndEquipForInventoryAsync(string flag)
    {
        if (!await WaitForPlayerAsync(flag))
        {
            return false;
        }

        if (GameState.Instance?.Player is not { } state)
        {
            Finish(ScreenshotExitCode.Error, $"{flag}: no GameState autoload");
            return false;
        }

        foreach (var category in new[] { ItemCategory.Weapon, ItemCategory.Clothing })
        {
            var id = World!.GivenItems.FirstOrDefault(
                id => state.Catalog.TryGet(id, out var item) && item.Category == category);
            if (id is not null)
            {
                state.Equip(id);
            }
        }

        var equipped = string.Join(", ", ItemCatalog.Slots.Select(slot => state.EquippedIn(slot) ?? "-"));
        GD.Print($"Screenshot: {flag} gear {string.Join(", ", state.Gear.Order(StringComparer.Ordinal))}; equipped {equipped}");
        return true;
    }

    /// <summary>
    /// The 2D size the menus are laid out in for an image of <paramref name="imageSize"/>: the project's base size
    /// (<c>display/window/size/viewport_width</c> and <c>_height</c>) widened or heightened to the image's aspect ratio,
    /// as the <c>canvas_items</c> stretch mode with the <c>expand</c> aspect does for the window
    /// (<see cref="CanvasStretch.ExpandedSize"/>).
    /// </summary>
    public static Vector2I MenuCanvasSize(Vector2I imageSize)
    {
        var baseWidth = ProjectSettings.GetSetting("display/window/size/viewport_width", 1152).AsInt32();
        var baseHeight = ProjectSettings.GetSetting("display/window/size/viewport_height", 648).AsInt32();
        var (width, height) = CanvasStretch.ExpandedSize(baseWidth, baseHeight, imageSize.X, imageSize.Y);
        return new Vector2I(width, height);
    }

    /// <summary>
    /// <c>--menu</c>: writes the sample saves into <paramref name="folder"/> (created): the autosave and slot 1 valid
    /// (<paramref name="mapId"/>, <see cref="SampleSavedAt"/>, money 120 and 2450, play time 1:23 and 12:05, some
    /// gear, at <paramref name="position"/>), slot 2 damaged (not JSON), the rest empty.
    /// </summary>
    public static void WriteSampleSaves(string folder, string mapId, string? generatedAt, Vector3 position)
    {
        var slots = new SaveSlots(new Saves.FileSaveStore(folder));
        var stamp = string.IsNullOrWhiteSpace(generatedAt) ? SaveFactory.UnknownGeneratedAt : generatedAt;
        var point = new LocalPoint(position.X, position.Y, position.Z);
        SaveGame Sample(DateTimeOffset savedAt, double playSeconds, int money, string[] gear) =>
            new(
                SaveFormat.CurrentVersion,
                Main.GameVersion,
                savedAt,
                playSeconds,
                mapId,
                stamp,
                new SavePlayer(point, 0d, money, gear, SaveEquipped.Empty, 0),
                SaveQuests.Empty);

        slots.Write(SlotId.Autosave, Sample(SampleSavedAt[0], 4_980, 120, ["phone"]));
        slots.Write(SlotId.Manual(1), Sample(SampleSavedAt[1], 43_500, 2_450, ["crowbar", "phone"]));
        new Saves.FileSaveStore(folder).WriteText(SlotId.Manual(2).FileName, "{ not json");
    }

    private static bool HasPlayer(World world) =>
        world.Player is { } player && IsInstanceValid(player)
        && world.PlayerCamera is { } camera && IsInstanceValid(camera);

    private void PlaceCamera(Vector3 position, Vector3 lookAt, float fov)
    {
        var direction = (lookAt - position).Normalized();
        // Looking straight down or up: Vector3.Up can't be the up vector, so put north (−Z) at the top of the image.
        var up = Mathf.Abs(direction.Dot(Vector3.Up)) > 0.999f ? Vector3.Forward : Vector3.Up;
        _camera!.Fov = fov;
        _camera.LookAtFromPosition(position, lookAt, up);
    }

    private void Save()
    {
        var image = _viewport!.GetTexture().GetImage();
        if (image is null || image.IsEmpty())
        {
            Finish(ScreenshotExitCode.Error, "the capture viewport returned no image");
            return;
        }

        image.Convert(Image.Format.Rgba8);
        if (ImageCheck.IsFlatColour(image.GetData(), 4, FlatColourTolerance))
        {
            Finish(ScreenshotExitCode.Error, $"the image is one flat colour ({image.GetPixel(0, 0)}); nothing was rendered");
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_outputPath)!);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Finish(ScreenshotExitCode.Error, $"can't create the folder for {_outputPath}: {ex.Message}");
            return;
        }

        var error = image.SavePng(_outputPath);
        if (error != Error.Ok)
        {
            Finish(ScreenshotExitCode.Error, $"saving {_outputPath} failed: {error}");
            return;
        }

        var window = GetWindow();
        GD.Print(
            $"Screenshot: saved {_outputPath} ({image.GetWidth()}x{image.GetHeight()}; "
            + $"window {window.Size} at {window.Position}, screen {DisplayServer.WindowGetCurrentScreen()})");
        Finish(ScreenshotExitCode.Saved, null);
    }

    /// <summary>
    /// Records <paramref name="exitCode"/> in <see cref="ExitCode"/> and, with <see cref="QuitOnFinish"/>, quits with it; an
    /// error message is pushed as an error so logs show it.
    /// </summary>
    private void Finish(int exitCode, string? error)
    {
        if (_finished)
        {
            return;
        }

        _finished = true;
        ExitCode = exitCode;
        DeleteMenuSaves();
        if (error is not null)
        {
            GD.PushError($"Screenshot: {error}");
        }

        if (QuitOnFinish)
        {
            GetTree().Quit(exitCode);
        }
    }

    private void DeleteMenuSaves()
    {
        if (_menuSavesFolder is not { } folder)
        {
            return;
        }

        _menuSavesFolder = null;
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            GD.PushWarning($"Screenshot: can't delete the sample saves folder {folder}: {ex.Message}");
        }
    }

    private static Vector3 ToVector(LocalPoint point) => new((float)point.X, (float)point.Y, (float)point.Z);
}
