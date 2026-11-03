using System;
using Directory = System.IO.Directory;
using IOException = System.IO.IOException;
using Path = System.IO.Path;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Roadman.Core.Maps;
using Roadman.Core.Tools;
using Roadman.Hud;
using Roadman.Scenes;

namespace Roadman.Debug;

/// <summary>
/// Renders the world from a named camera preset to a PNG and quits:
/// <c>godot --path game --resolution 1600x900 -- --screenshot &lt;preset&gt; &lt;out.png&gt; [--presets &lt;file&gt;] [--map &lt;map-id&gt;] [--orthophoto] [--road-graph] [--inspect] [--player] [--seed &lt;n&gt;]</c>.
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

        if (Request!.Player)
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
        var maxSpawnFrames = world.SpawnPhysicsWaitFrames + PlayerSpawnMarginFrames;
        var tree = GetTree();
        for (var frame = 0; !HasPlayer(world) && world.LastSpawnFailure is null && frame < maxSpawnFrames; frame++)
        {
            await ToSignal(tree, SceneTree.SignalName.PhysicsFrame);
            if (_finished || !IsInsideTree())
            {
                return null;
            }
        }

        if (!HasPlayer(world))
        {
            var reason = world.LastSpawnFailure
                ?? FormattableString.Invariant($"no spawn within {maxSpawnFrames} physics frames");
            Finish(ScreenshotExitCode.Error, $"--player: the player didn't spawn ({reason}); {MapPack.RegenHint(loader.ResolvedMapId)}");
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
        if (error is not null)
        {
            GD.PushError($"Screenshot: {error}");
        }

        if (QuitOnFinish)
        {
            GetTree().Quit(exitCode);
        }
    }

    private static Vector3 ToVector(LocalPoint point) => new((float)point.X, (float)point.Y, (float)point.Z);
}
