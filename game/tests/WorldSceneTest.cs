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

    // These load the pack in res://maps/somero, never relying on another suite having reset the override.
    [BeforeTest]
    public void UseFullPack() => TestPacks.UseFullPack();

    [TestCase]
    public void World_BuildsCameraSunAndEnvironment()
    {
        using var runner = ISceneRunner.Load(WorldScenePath, true);
        var world = runner.Scene();
        AssertThat(world).IsNotNull();

        // Right after the load, before the player spawns (it waits for the terrain collision, a physics frame or
        // more), the debug camera is current. PlayerOnFootTest checks that the player camera takes over after the
        // spawn (docs/specs/player-on-foot.md, criterion 6).
        AssertThat(((Roadman.Scenes.World)world!).Player).IsNull();
        var camera = world!.GetNodeOrNull<Camera3D>("DebugCamera");
        AssertThat(camera).IsNotNull();
        AssertThat(camera).IsInstanceOf<DebugCamera>();
        AssertThat(camera!.Current).IsTrue();

        var sun = world.GetNodeOrNull<DirectionalLight3D>("Sun");
        AssertThat(sun).IsNotNull();
        AssertThat(sun!.ShadowEnabled).IsTrue();

        var environment = world.GetNodeOrNull<WorldEnvironment>("WorldEnvironment");
        AssertThat(environment).IsNotNull();
        // The day sky with a starfield below the horizon (docs/specs/world-edge-game.md, "Space backdrop").
        AssertThat(environment!.Environment.Sky.SkyMaterial).IsInstanceOf<ShaderMaterial>();
        var shader = ((ShaderMaterial)environment.Environment.Sky.SkyMaterial).Shader;
        AssertThat(shader).IsNotNull();
        AssertThat(shader!.ResourcePath).IsEqual(Roadman.Scenes.World.SkyShaderPath);
    }

    [TestCase]
    public void World_AddsOrthophotoViewInDebugBuilds()
    {
        AssertBool(DebugBuild.IsDebug).IsTrue();
        using var runner = ISceneRunner.Load(WorldScenePath, true);
        var world = (Roadman.Scenes.World)runner.Scene()!;

        var view = world.GetNodeOrNull<OrthophotoDebugView>("OrthophotoDebugView");
        AssertThat(view).IsNotNull();
        AssertThat(world.Orthophoto).IsSame(view);
        AssertThat(view!.Loader).IsSame(world.MapLoader);
        AssertBool(view.Enabled).IsFalse();
    }
}
