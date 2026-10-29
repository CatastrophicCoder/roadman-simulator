using System;
using Directory = System.IO.Directory;
using IOException = System.IO.IOException;
using Path = System.IO.Path;
using System.Linq;
using Godot;
using Roadman.Core.Maps;
using Roadman.Core.Tools;
using Roadman.Scenes;

namespace Roadman.Debug;

/// <summary>
/// Renders the world from a named camera preset to a PNG and quits:
/// <c>godot --path game --resolution 1600x900 -- --screenshot &lt;preset&gt; &lt;out.png&gt; [--presets &lt;file&gt;] [--map &lt;map-id&gt;] [--orthophoto] [--road-graph]</c>.
/// <para>
/// The image comes from its own <see cref="SubViewport"/> of <see cref="ImageSize"/>, so it doesn't depend on the
/// window size, display scaling or where the window is. Rendering needs a real display server: the tool refuses to
/// run under <c>--headless</c>. Exit codes are in <see cref="ScreenshotExitCode"/>.
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

    private async void CaptureAsync()
    {
        var loader = Loader!;
        var preset = _preset!;
        var terrainReady = await loader.WaitForTerrainCollisionAsync(MaxPhysicsWaitFrames);
        if (_finished || !IsInsideTree())
        {
            return;
        }

        if (!terrainReady && preset.GroundRelative)
        {
            Finish(
                ScreenshotExitCode.Error,
                $"no terrain collision in {loader.PackPath} after {MaxPhysicsWaitFrames} physics frames; {MapPack.RegenHint(loader.ResolvedMapId)}");
            return;
        }

        var placement = PresetPlacement.Resolve(preset, (x, z) => loader.TerrainHeightAt((float)x, (float)z));
        if (placement.IsOutsideBuiltArea)
        {
            GD.Print($"N/A: outside built area (preset '{preset.Name}' has no terrain under {preset.Position.X}, {preset.Position.Z})");
            Finish(ScreenshotExitCode.OutsideBuiltArea, null);
            return;
        }

        PlaceCamera(ToVector(placement.Position), ToVector(placement.LookAt), (float)preset.Fov);
        GD.Print($"Screenshot: preset '{preset.Name}' camera at {_camera!.GlobalPosition}, looking at {ToVector(placement.LookAt)}");

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

    /// <summary>Quits with <paramref name="exitCode"/>; an error message is pushed as an error so logs show it.</summary>
    private void Finish(int exitCode, string? error)
    {
        if (_finished)
        {
            return;
        }

        _finished = true;
        if (error is not null)
        {
            GD.PushError($"Screenshot: {error}");
        }

        GetTree().Quit(exitCode);
    }

    private static Vector3 ToVector(LocalPoint point) => new((float)point.X, (float)point.Y, (float)point.Z);
}
