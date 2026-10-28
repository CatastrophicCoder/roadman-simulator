using System;
using Godot;
using Roadman.Core.Tools;
using Roadman.Debug;

namespace Roadman.Scenes;

/// <summary>
/// Root of the game world. Builds the environment, sun, map loader and debug camera in code, loads the map, and
/// places the camera above the terrain at the origin. With <c>-- --screenshot &lt;preset&gt; &lt;out.png&gt;</c> on
/// the command line it adds a <see cref="ScreenshotTool"/> instead of placing the camera.
/// </summary>
public partial class World : Node3D
{
    [Signal]
    public delegate void CameraPlacedEventHandler(Vector3 position);

    [Export]
    public Vector3 SunRotationDegrees { get; set; } = new(-50f, -30f, 0f);

    /// <summary>Debug camera start, relative to the terrain at the map origin. The camera looks at that point.</summary>
    [Export]
    public Vector3 CameraStartOffset { get; set; } = new(0f, 30f, 40f);

    public MapLoader MapLoader { get; private set; } = null!;

    public DebugCamera Camera { get; private set; } = null!;

    public override void _Ready()
    {
        AddChild(CreateEnvironment());
        AddChild(CreateSun());

        MapLoader = new MapLoader { Name = "MapLoader" };
        AddChild(MapLoader);

        Camera = new DebugCamera { Name = "DebugCamera", Current = true };
        AddChild(Camera);
        Camera.LookAtFromPosition(CameraStartOffset, Vector3.Zero, Vector3.Up);

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

        if (screenshot is not null)
        {
            // Added before Load() so it hears MapLoaded or MapLoadFailed.
            AddChild(new ScreenshotTool { Name = "ScreenshotTool", Request = screenshot, Loader = MapLoader });
        }

        MapLoader.Load();
        if (screenshot is null && MapLoader.Manifest is not null)
        {
            PlaceCameraAboveTerrainAsync();
        }
    }

    /// <summary>Moves the camera to <see cref="CameraStartOffset"/> above the terrain at the origin once collision is ready.</summary>
    private async void PlaceCameraAboveTerrainAsync()
    {
        if (!await MapLoader.WaitForTerrainCollisionAsync() || !IsInstanceValid(Camera))
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

    private static WorldEnvironment CreateEnvironment()
    {
        var sky = new Sky { SkyMaterial = new ProceduralSkyMaterial() };
        var environment = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Sky,
            Sky = sky,
            AmbientLightSource = Godot.Environment.AmbientSource.Sky,
            ReflectedLightSource = Godot.Environment.ReflectionSource.Sky,
            TonemapMode = Godot.Environment.ToneMapper.Filmic,
        };
        return new WorldEnvironment { Name = "WorldEnvironment", Environment = environment };
    }

    private DirectionalLight3D CreateSun() =>
        new()
        {
            Name = "Sun",
            ShadowEnabled = true,
            RotationDegrees = SunRotationDegrees,
        };
}
