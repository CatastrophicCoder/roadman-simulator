using Godot;
using Roadman.Debug;

namespace Roadman.Scenes;

/// <summary>
/// Root of the game world. Builds the environment, sun and debug camera in code.
/// </summary>
public partial class World : Node3D
{
    [Export]
    public Vector3 SunRotationDegrees { get; set; } = new(-50f, -30f, 0f);

    [Export]
    public Vector3 CameraStartPosition { get; set; } = new(0f, 30f, 40f);

    public override void _Ready()
    {
        AddChild(CreateEnvironment());
        AddChild(CreateSun());

        var camera = new DebugCamera { Name = "DebugCamera", Current = true };
        AddChild(camera);
        camera.LookAtFromPosition(CameraStartPosition, Vector3.Zero, Vector3.Up);
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
