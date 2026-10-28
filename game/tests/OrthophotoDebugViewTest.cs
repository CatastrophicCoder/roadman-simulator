using System.Collections.Generic;
using System.Threading.Tasks;
using GdUnit4;
using Godot;
using Roadman.Core.Maps;
using Roadman.Debug;
using Roadman.Scenes;
using static GdUnit4.Assertions;
using Directory = System.IO.Directory;

namespace Roadman.Tests;

/// <summary>
/// The orthophoto view on hand-made tiles and a temporary image folder, so it needs no regenerated pack.
/// </summary>
[TestSuite]
[RequireGodotRuntime]
public class OrthophotoDebugViewTest
{
    private const string WithImage = "tile_x0_y0";
    private const string WithoutImage = "tile_x1_y0";

    private static readonly MapCredit OrthoCredit = new(
        Orthophoto.CreditSource,
        "Orthophotos",
        "CC BY 4.0",
        "Test attribution for orthophotos, 10/2026",
        "2026-10-28T09:00:00Z");

    private static readonly MapCredit DemCredit = new(
        "mml_dem_2m",
        "Elevation model 2 m",
        "CC BY 4.0",
        "Test attribution for the elevation model",
        "2026-10-27T14:44:30Z");

    private string _folder = "";

    [BeforeTest]
    public void CreateImageFolder()
    {
        _folder = ProjectSettings.GlobalizePath($"user://orthophoto-test-{System.Guid.NewGuid():N}");
        Directory.CreateDirectory(_folder);
    }

    [AfterTest]
    public void DeleteImageFolder()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [TestCase]
    public void Toggle_SwapsTerrainMaterialsAndBack()
    {
        WriteImage(WithImage);
        using var runner = ISceneRunner.Load(new Node3D { Name = "Root" }, true);
        var (loader, view) = Setup(runner, [OrthoCredit]);
        var withImage = Terrain(loader, WithImage);
        var withoutImage = Terrain(loader, WithoutImage);
        var toggles = new List<bool>();
        view.Toggled += toggles.Add;

        AssertBool(view.Toggle()).IsTrue();

        AssertBool(view.Enabled).IsTrue();
        AssertThat(withImage.MaterialOverride).IsNotSame(loader.TerrainMaterial);
        var material = withImage.MaterialOverride as StandardMaterial3D;
        AssertThat(material).IsNotNull();
        AssertThat(material!.AlbedoTexture).IsNotNull();
        AssertThat(material.ShadingMode).IsEqual(BaseMaterial3D.ShadingModeEnum.Unshaded);
        AssertThat(withoutImage.MaterialOverride).IsSame(loader.TerrainMaterial);
        AssertInt(view.CachedTextureCount).IsEqual(1);

        AssertBool(view.Toggle()).IsTrue();

        AssertBool(view.Enabled).IsFalse();
        AssertThat(withImage.MaterialOverride).IsSame(loader.TerrainMaterial);
        AssertThat(withoutImage.MaterialOverride).IsSame(loader.TerrainMaterial);

        // Switching on again uses the cached material; nothing is reloaded.
        AssertBool(view.Toggle()).IsTrue();
        AssertThat(withImage.MaterialOverride).IsSame(material);
        AssertThat(toggles).IsEqual(new List<bool> { true, false, true });
    }

    [TestCase]
    public void Images_LoadLazilyOnFirstSwitchOn()
    {
        using var runner = ISceneRunner.Load(new Node3D { Name = "Root" }, true);
        var (_, view) = Setup(runner, [OrthoCredit]);
        WriteImage(WithImage);

        AssertInt(view.CachedTextureCount).IsEqual(0);
        view.SetEnabled(true);
        AssertInt(view.CachedTextureCount).IsEqual(1);
    }

    [TestCase]
    public void AttributionLabel_ShowsOrthophotoCreditsWhileOn()
    {
        WriteImage(WithImage);
        using var runner = ISceneRunner.Load(new Node3D { Name = "Root" }, true);
        var (_, view) = Setup(runner, [DemCredit, OrthoCredit]);

        AssertBool(view.AttributionPanel.Visible).IsFalse();
        view.SetEnabled(true);

        AssertBool(view.AttributionPanel.Visible).IsTrue();
        AssertThat(view.AttributionLabel.Text).IsEqual(Orthophoto.AttributionText([DemCredit, OrthoCredit]));
        AssertThat(view.AttributionLabel.Text).IsEqual("Test attribution for orthophotos, 10/2026 (CC BY 4.0)");
        AssertThat(view.AttributionLabel.LabelSettings.OutlineSize).IsGreater(0);

        view.SetEnabled(false);
        AssertBool(view.AttributionPanel.Visible).IsFalse();
    }

    [TestCase]
    public void AttributionLabel_WithoutOrthophotoCredit_SaysItIsMissing()
    {
        WriteImage(WithImage);
        using var runner = ISceneRunner.Load(new Node3D { Name = "Root" }, true);
        var (_, view) = Setup(runner, [DemCredit]);

        view.SetEnabled(true);

        AssertThat(view.AttributionLabel.Text).Contains("missing from the manifest").NotContains(DemCredit.Attribution);
    }

    [TestCase]
    public void NoImages_ShowsMessageAndStaysOff()
    {
        using var runner = ISceneRunner.Load(new Node3D { Name = "Root" }, true);
        var (loader, view) = Setup(runner, [OrthoCredit]);
        var unavailable = "";
        var toggled = false;
        view.Unavailable += message => unavailable = message;
        view.Toggled += _ => toggled = true;

        AssertBool(view.Toggle()).IsFalse();

        AssertBool(view.Enabled).IsFalse();
        AssertBool(toggled).IsFalse();
        AssertBool(view.MessagePanel.Visible).IsTrue();
        AssertThat(view.MessageLabel.Text).IsEqual("No orthophoto in this pack; run /regen-map somero");
        AssertThat(unavailable).IsEqual(view.MessageLabel.Text);
        AssertBool(view.AttributionPanel.Visible).IsFalse();
        AssertThat(Terrain(loader, WithImage).MaterialOverride).IsSame(loader.TerrainMaterial);
    }

    [TestCase]
    public void NoImageFolder_ShowsMessageAndStaysOff()
    {
        using var runner = ISceneRunner.Load(new Node3D { Name = "Root" }, true);
        var (_, view) = Setup(runner, [OrthoCredit]);
        view.TextureFolder = $"{_folder}/does-not-exist";

        AssertBool(view.SetEnabled(true)).IsFalse();
        AssertBool(view.Enabled).IsFalse();
        AssertBool(view.MessagePanel.Visible).IsTrue();
    }

    [TestCase]
    public async Task F2_TogglesTheView()
    {
        WriteImage(WithImage);
        using var runner = ISceneRunner.Load(new Node3D { Name = "Root" }, true);
        var (_, view) = Setup(runner, [OrthoCredit]);

        AssertBool(InputMap.HasAction(OrthophotoDebugView.ToggleAction)).IsTrue();
        var f2 = new InputEventKey { PhysicalKeycode = Key.F2, Pressed = true };
        AssertBool(InputMap.EventIsAction(f2, OrthophotoDebugView.ToggleAction, exactMatch: true)).IsTrue();

        runner.SimulateKeyPressed(Key.F2);
        await runner.AwaitIdleFrame();
        AssertBool(view.Enabled).IsTrue();

        runner.SimulateKeyPressed(Key.F2);
        await runner.AwaitIdleFrame();
        AssertBool(view.Enabled).IsFalse();
    }

    [TestCase]
    public void AttachOverlayTo_MovesTheOverlayIntoTheViewport()
    {
        using var runner = ISceneRunner.Load(new Node3D { Name = "Root" }, true);
        var (_, view) = Setup(runner, [OrthoCredit]);
        var viewport = new SubViewport { Name = "Capture" };
        runner.Scene()!.AddChild(viewport);

        view.AttachOverlayTo(viewport);

        AssertThat(view.Overlay.GetParent()).IsSame(viewport);
    }

    private (MapLoader Loader, OrthophotoDebugView View) Setup(ISceneRunner runner, IReadOnlyList<MapCredit> credits)
    {
        var root = runner.Scene()!;
        var loader = new MapLoader { Name = "MapLoader", MapId = "somero" };
        root.AddChild(loader);
        foreach (var id in new[] { WithImage, WithoutImage })
        {
            var tile = new Node3D { Name = id };
            tile.AddChild(new MeshInstance3D
            {
                Name = MapLoader.TerrainNodeName,
                Mesh = new PlaneMesh(),
                MaterialOverride = loader.TerrainMaterial,
            });
            loader.Tiles.AddChild(tile);
        }

        var view = new OrthophotoDebugView
        {
            Name = "OrthophotoDebugView",
            Loader = loader,
            TextureFolder = _folder,
            Credits = credits,
        };
        root.AddChild(view);
        return (loader, view);
    }

    private static MeshInstance3D Terrain(MapLoader loader, string tileId)
    {
        var terrain = MapLoader.FindTerrainMesh(loader.Tiles.GetNode(tileId));
        AssertThat(terrain).IsNotNull();
        return terrain!;
    }

    private void WriteImage(string tileId)
    {
        var image = Image.CreateEmpty(16, 16, false, Image.Format.Rgb8);
        image.Fill(new Color(0.2f, 0.6f, 0.3f));
        var error = image.SaveJpg(Orthophoto.TexturePath(_folder, tileId));
        AssertThat(error).IsEqual(Error.Ok);
    }
}
