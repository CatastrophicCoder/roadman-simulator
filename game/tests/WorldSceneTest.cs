using GdUnit4;
using Godot;
using Roadman.Debug;
using static GdUnit4.Assertions;

namespace Roadman.Tests;

[TestSuite]
[RequireGodotRuntime]
public class WorldSceneTest
{
    private const string WorldScenePath = "res://scenes/world/World.tscn";

    [TestCase]
    public void World_BuildsCameraSunAndEnvironment()
    {
        using var runner = ISceneRunner.Load(WorldScenePath, true);
        var world = runner.Scene();
        AssertThat(world).IsNotNull();

        var camera = world!.GetNodeOrNull<Camera3D>("DebugCamera");
        AssertThat(camera).IsNotNull();
        AssertThat(camera).IsInstanceOf<DebugCamera>();
        AssertThat(camera!.Current).IsTrue();

        var sun = world.GetNodeOrNull<DirectionalLight3D>("Sun");
        AssertThat(sun).IsNotNull();
        AssertThat(sun!.ShadowEnabled).IsTrue();

        var environment = world.GetNodeOrNull<WorldEnvironment>("WorldEnvironment");
        AssertThat(environment).IsNotNull();
        AssertThat(environment!.Environment.Sky.SkyMaterial).IsInstanceOf<ProceduralSkyMaterial>();
    }
}
