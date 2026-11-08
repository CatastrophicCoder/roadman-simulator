using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using GdUnit4;
using Godot;
using Roadman.Autoload;
using Roadman.Core.Edge;
using Roadman.Core.Settings;
using Roadman.Hud;
using Roadman.Scenes;
using Roadman.Ui;
using static GdUnit4.Assertions;
using static Roadman.Tests.MainTestSupport;

namespace Roadman.Tests;

/// <summary>
/// The minimap in a game started by <c>Main</c> on the test-area pack (<c>docs/specs/minimap.md</c>; <c>Ac</c> numbers
/// are its acceptance criteria): the corner from the settings view, at once, persisted and used by the next game (11),
/// and when it is shown or hidden (12). Saves and settings live in a temporary folder (<see cref="MainTestSupport"/>),
/// whose <see cref="MainTestSupport.TearDown"/> puts back every global the game changes after each test. One test
/// (the failed spawn) runs a direct <see cref="World"/>, since <c>Main</c> frees a world whose spawn failed.
/// </summary>
[TestSuite]
[RequireGodotRuntime]
public class MinimapMainTest
{
    /// <summary>Criterion 12: the death fall's length in the death test, s.</summary>
    private const float ShortFallTime = 1f;

    /// <summary>Process frames to let the minimap's own <c>_Process</c> run after a change.</summary>
    private const int ProcessFramesAfterChange = 2;

    /// <summary>Criterion 11: the order the corners are picked in (the default first would change nothing); ends off the default.</summary>
    private static readonly MinimapCorner[] PickOrder =
        [MinimapCorner.BottomRight, MinimapCorner.TopLeft, MinimapCorner.BottomLeft, MinimapCorner.TopRight];

    private string _folder = "";
    private Main? _main;
    private World? _world;
    private ISceneRunner? _runner;

    [BeforeTest]
    public void SetUp()
    {
        _folder = BeginTest("minimap");
        TestPacks.UseTestArea();
    }

    [AfterTest]
    public void CleanUp()
    {
        TestPacks.UseFullPack();
        _runner?.Dispose();
        _runner = null;
        TearDown(_folder, _world, _main);
        _main = null;
        _world = null;
        _folder = "";
    }

    // ----- Criterion 11 -----

    /// <summary>
    /// In a game, from the pause menu's settings view: each of the four "Minimap corner" values moves the panel to that
    /// corner (<see cref="MinimapHud.ScreenMargin"/> from both of its edges, <see cref="MinimapHud.PanelDiameter"/>
    /// across) within one process frame, with no map reload (no <c>MapLoaded</c>, the same data and world). The row has
    /// no note. Closing the view writes the corner to <c>settings.json</c>; after Resume the minimap shows there; the
    /// next new game in the same <c>Main</c>, and a new game in a new <c>Main</c> that reads the file, use it.
    /// </summary>
    [TestCase]
    public async Task Ac11_EachCornerThroughTheSettingsView_MovesThePanelWithinOneProcessFrame_NoReload_PersistsAndTheNextGameUsesIt_NoNote()
    {
        WorldEdgeTest.AssertPackPresent();
        _runner = ISceneRunner.Load(_main = NewMain(_folder, "--new-game"), true);
        var world = await WaitForGame(_main);
        var minimap = await ShownMinimap(world, "at the start");
        AssertCorner(minimap, MinimapCorner.BottomLeft, "by default");

        var loads = 0;
        world.MapLoader.MapLoaded += _ => loads++;
        var builds = minimap.DataBuildCount;
        var data = minimap.Data;
        AssertBool(_main.OpenPause()).IsTrue();
        AssertBool(_main.OpenSettings()).OverrideFailureMessage("OpenSettings from the pause menu").IsTrue();
        var view = _main.SettingsView;
        view.SelectTab(SettingsView.GameplayTab);
        var option = view.MinimapCornerOption;
        AssertInt(option.Selected).IsEqual((int)MinimapCorner.BottomLeft);
        AssertThat(option.GetParent().GetNodeOrNull<Label>("MinimapCornerValue"))
            .OverrideFailureMessage("no note label beside \"Minimap corner\"").IsNotNull();
        AssertThat(option.GetParent().GetNode<Label>("MinimapCornerValue").Text)
            .OverrideFailureMessage("\"Minimap corner\" shows a note").IsEqual("");
        AssertThat(option.GetParent().GetNode<Label>("MinimapCornerLabel").Text).IsEqual("Minimap corner");

        foreach (var corner in PickOrder)
        {
            Pick(option, (int)corner);
            await _main.ToSignal(_main.GetTree(), SceneTree.SignalName.ProcessFrame);
            AssertThat(minimap.Corner).OverrideFailureMessage($"picking {corner} didn't reach the minimap").IsEqual(corner);
            AssertCorner(minimap, corner, $"one process frame after picking {corner}");
            AssertBool(minimap.IsShown).OverrideFailureMessage("the minimap shows over the settings view").IsFalse();
        }

        AssertInt(loads).OverrideFailureMessage("a corner change reloaded the map").IsEqual(0);
        AssertInt(minimap.DataBuildCount).OverrideFailureMessage("a corner change rebuilt the minimap's data").IsEqual(builds);
        AssertThat(minimap.Data).IsSame(data);
        AssertThat(_main.World).IsSame(world);

        // Close: the file has the corner.
        _main.Back();
        AssertBool(view.Visible).IsFalse();
        AssertThat(view.LastSaveError).IsNull();
        var settingsPath = Path.Combine(_folder, "settings.json");
        AssertBool(File.Exists(settingsPath)).OverrideFailureMessage("closing the view didn't write settings.json").IsTrue();
        var written = SettingsReader.Read(File.ReadAllText(settingsPath));
        AssertThat(string.Join(" | ", written.Warnings)).IsEqual("");
        AssertThat(written.Settings.Gameplay.MinimapCorner).IsEqual(MinimapCorner.TopRight);

        // Resume: shown in the new corner.
        AssertBool(_main.Resume()).IsTrue();
        await ShownMinimap(world, "after Resume");
        AssertCorner(minimap, MinimapCorner.TopRight, "after Resume");

        // The next new game in this Main.
        AssertBool(_main.OpenPause()).IsTrue();
        Click(_main.PauseMenu.QuitToMenuButton);
        ClickDialog(_main, "Quit");
        AssertThat(_main.State).IsEqual(MainState.MainMenu);
        Click(_main.MainMenu.NewGameButton);
        var next = await WaitForGame(_main);
        AssertThat(next).IsNotSame(world);
        var nextMinimap = await ShownMinimap(next, "in the next game");
        AssertCorner(nextMinimap, MinimapCorner.TopRight, "in the next game");

        // A new Main reading the file.
        TearDownMainOnly();
        GameState.Instance.Settings = GameSettings.Default;
        _runner = ISceneRunner.Load(_main = NewMain(_folder, "--new-game"), true);
        var fresh = await WaitForGame(_main);
        AssertThat(GameState.Instance.Settings.Gameplay.MinimapCorner).IsEqual(MinimapCorner.TopRight);
        var freshMinimap = await ShownMinimap(fresh, "in a new Main's game");
        AssertCorner(freshMinimap, MinimapCorner.TopRight, "in a new Main's game");
    }

    // ----- Criterion 12 -----

    /// <summary>
    /// Hidden while the pause menu, the settings view, the slot list or the inventory screen (from the game and from the
    /// pause menu) shows; shown again after each Resume.
    /// </summary>
    [TestCase]
    public async Task Ac12_HiddenWhilePauseMenuSettingsViewSlotListAndInventoryShow_ShownAfterResume()
    {
        WorldEdgeTest.AssertPackPresent();
        _runner = ISceneRunner.Load(_main = NewMain(_folder, "--new-game"), true);
        var world = await WaitForGame(_main);
        var minimap = await ShownMinimap(world, "at the start");

        AssertBool(_main.OpenPause()).IsTrue();
        await AssertHidden(minimap, "with the pause menu open");

        AssertBool(_main.OpenSettings()).IsTrue();
        await AssertHidden(minimap, "with the settings view open");
        _main.Back();
        AssertBool(_main.PauseMenu.Visible).IsTrue();
        await AssertHidden(minimap, "back on the pause menu from the settings view");

        AssertBool(_main.OpenSlotList(SlotListMode.Load)).IsTrue();
        await AssertHidden(minimap, "with the slot list (load) open");
        _main.Back();
        AssertBool(_main.OpenSlotList(SlotListMode.Save)).IsTrue();
        await AssertHidden(minimap, "with the slot list (save) open");
        _main.Back();

        AssertBool(_main.OpenInventory()).OverrideFailureMessage("the inventory didn't open from the pause menu").IsTrue();
        await AssertHidden(minimap, "with the inventory screen open from the pause menu");
        AssertBool(_main.CloseInventory()).IsTrue();
        await AssertHidden(minimap, "back on the pause menu from the inventory screen");

        AssertBool(_main.Resume()).IsTrue();
        await ShownMinimap(world, "after Resume from the pause menu");

        AssertBool(_main.OpenInventory()).OverrideFailureMessage("the inventory didn't open from the game").IsTrue();
        AssertThat(_main.State).IsEqual(MainState.Inventory);
        await AssertHidden(minimap, "with the inventory screen open from the game");
        AssertBool(_main.CloseInventory()).IsTrue();
        AssertThat(_main.State).IsEqual(MainState.Playing);
        await ShownMinimap(world, "after closing the inventory screen opened from the game");
    }

    /// <summary>
    /// Hidden during the death fall and on the death screen (also checked through the frame's projection, which is
    /// null while hidden), and shown again after the respawn, centred on the respawned player.
    /// </summary>
    [TestCase]
    public async Task Ac12_HiddenDuringTheDeathFallAndTheDeathScreen_VisibleAgainAfterTheRespawn()
    {
        WorldEdgeTest.AssertPackPresent();
        _runner = ISceneRunner.Load(_main = NewMain(_folder, "--new-game"), true);
        var world = await WaitForGame(_main);
        await WaitForEdge(world);
        var minimap = await ShownMinimap(world, "at the start");
        world.FallTime = ShortFallTime;

        WorldEdgeTest.AssertEntered(await WorldEdgeTest.PlaceOnLedgeAndWaitForEntry(world, WorldEdgeTest.FindLedgeEntry(world)));
        AssertThat(world.Death.State).IsEqual(DeathState.Falling);
        await AssertHidden(minimap, "during the death fall");
        AssertThat(minimap.Projection).IsNull();
        await WorldEdgeTest.PhysicsFrames(world, 10);
        await AssertHidden(minimap, "later in the death fall");

        AssertBool(await WorldEdgeTest.WaitUntil(world, () => world.Death.State == DeathState.DeathScreen, WorldEdgeTest.FramesFor(ShortFallTime, Engine.PhysicsTicksPerSecond) + 10))
            .OverrideFailureMessage("the death screen didn't come").IsTrue();
        await AssertHidden(minimap, "on the death screen");

        AssertBool(await WorldEdgeTest.WaitUntil(world, () => world.Death.TimeInState >= world.MinScreenTime + 0.1, WorldEdgeTest.FramesFor(world.MinScreenTime + 0.2, Engine.PhysicsTicksPerSecond)))
            .IsTrue();
        await AssertHidden(minimap, "on the death screen after MinScreenTime");
        await PressThenRelease(_main, () => KeyEvent(Key.Enter, true), () => KeyEvent(Key.Enter, false));
        AssertBool(await WorldEdgeTest.WaitUntil(world, () => world.Death.State == DeathState.Alive && world.Player is not null, 30))
            .OverrideFailureMessage($"no respawn after the dismiss (state {world.Death.State})").IsTrue();
        await ShownMinimap(world, "after the respawn");
        AssertCentredOnPlayer(world, minimap, "after the respawn");
    }

    /// <summary>
    /// With the debug camera on (debug build), the minimap still shows, centred on the player, and moving and turning
    /// the debug camera changes neither the centre nor the heading (the player camera's yaw). Back on the player camera
    /// it still shows.
    /// </summary>
    [TestCase]
    public async Task Ac12_ShownWhileTheDebugCameraIsOn_CentredOnThePlayer_HeadingUnchangedByMovingTheDebugCamera()
    {
        WorldEdgeTest.AssertPackPresent();
        AssertBool(Roadman.Debug.DebugBuild.IsDebug).OverrideFailureMessage("the tests run in a debug build").IsTrue();
        _runner = ISceneRunner.Load(_main = NewMain(_folder, "--new-game"), true);
        var world = await WaitForGame(_main);
        await Settle(world);
        var minimap = await ShownMinimap(world, "at the start");
        var heading = minimap.Projection!.HeadingYaw;

        AssertBool(world.ToggleDebugCamera()).OverrideFailureMessage("the debug camera didn't switch on").IsTrue();
        AssertBool(world.DebugCameraActive).IsTrue();
        var debugCamera = world.Camera!;
        AssertBool(debugCamera.Current).IsTrue();
        await ShownMinimap(world, "with the debug camera on");
        AssertCentredOnPlayer(world, minimap, "with the debug camera on");
        AssertFloat((float)minimap.Projection!.HeadingYaw).IsEqualApprox((float)heading, 1e-5f);

        foreach (var (offset, yaw) in new[] { (new Vector3(40f, 15f, -25f), 1.2f), (new Vector3(-60f, 30f, 70f), -2.5f) })
        {
            var start = debugCamera.GlobalTransform;
            debugCamera.GlobalTransform = new Transform3D(new Basis(Vector3.Up, yaw), start.Origin + offset);
            await WorldEdgeTest.PhysicsFrames(world, 3);
            await ShownMinimap(world, $"after moving the debug camera by {offset}, yaw {yaw}");
            AssertCentredOnPlayer(world, minimap, $"after moving the debug camera by {offset}");
            AssertFloat((float)MinimapProjection(minimap).HeadingYaw)
                .OverrideFailureMessage($"moving the debug camera turned the minimap from {heading} to {minimap.Projection!.HeadingYaw}")
                .IsEqualApprox((float)heading, 1e-5f);
        }

        AssertBool(world.ToggleDebugCamera()).IsTrue();
        AssertBool(world.DebugCameraActive).IsFalse();
        await ShownMinimap(world, "back on the player camera");
    }

    /// <summary>
    /// Hidden before the spawn: in every process frame of a new game's loading in which the world has no player yet.
    /// </summary>
    [TestCase]
    public async Task Ac12_HiddenBeforeTheSpawn()
    {
        WorldEdgeTest.AssertPackPresent();
        _runner = ISceneRunner.Load(_main = NewMain(_folder, "--new-game"), true);
        var main = _main;
        var framesWithoutPlayer = 0;
        var shownWithoutPlayer = 0;
        for (var i = 0; i < GameStartFrames && !(main.State == MainState.Playing && main.World?.Player is not null); i++)
        {
            await main.ToSignal(main.GetTree(), SceneTree.SignalName.ProcessFrame);
            if (main.World is { } w && GodotObject.IsInstanceValid(w) && w.Player is null && w.Minimap is { } m)
            {
                framesWithoutPlayer++;
                shownWithoutPlayer += m.IsShown ? 1 : 0;
                m.UpdateFrame();
                shownWithoutPlayer += m.IsShown || m.Projection is not null ? 1 : 0;
            }
        }

        GD.Print($"MinimapMainTest: Ac12 {framesWithoutPlayer} process frames with a world and no player");
        AssertInt(framesWithoutPlayer).OverrideFailureMessage("no frame with a world before the spawn was seen").IsGreater(0);
        AssertInt(shownWithoutPlayer).OverrideFailureMessage("the minimap showed before the spawn").IsEqual(0);
        var world = await WaitForGame(main);
        await ShownMinimap(world, "after the spawn");
    }

    /// <summary>
    /// Hidden after a failed spawn (a direct <see cref="World"/> whose spawn has no draws, so it fails with the spawn
    /// error and no player): never shown before or after the failure.
    /// </summary>
    [TestCase]
    public async Task Ac12_HiddenAfterAFailedSpawn()
    {
        WorldEdgeTest.AssertPackPresent();
        var world = WorldEdgeTest.NewWorld(WorldEdgeTest.TestSeed);
        world.SpawnMaxAttempts = 0;
        var failed = "";
        world.StartFailed += message => failed = message;
        var capture = new ErrorCapture();
        OS.AddLogger(capture);
        try
        {
            _runner = ISceneRunner.Load(_world = world, true);
            var shown = 0;
            for (var i = 0; i < world.SpawnPhysicsWaitFrames + 30 && failed == ""; i++)
            {
                await world.ToSignal(world.GetTree(), SceneTree.SignalName.ProcessFrame);
                shown += world.Minimap?.IsShown == true ? 1 : 0;
            }

            AssertThat(failed).OverrideFailureMessage("the spawn didn't fail").IsNotEqual("");
            AssertThat(world.Player).IsNull();
            var minimap = world.Minimap!;
            for (var i = 0; i < 5; i++)
            {
                await world.ToSignal(world.GetTree(), SceneTree.SignalName.ProcessFrame);
                shown += minimap.IsShown ? 1 : 0;
            }

            minimap.UpdateFrame();
            AssertInt(shown).OverrideFailureMessage("the minimap showed around the failed spawn").IsEqual(0);
            AssertBool(minimap.IsShown).IsFalse();
            AssertThat(minimap.Projection).IsNull();
            AssertBool(minimap.Panel.Visible).IsFalse();
        }
        finally
        {
            OS.RemoveLogger(capture);
        }

        var errors = capture.ErrorsOnly;
        AssertThat(string.Join(" | ", errors.Where(e => !e.Contains("no player spawn"))))
            .OverrideFailureMessage("errors other than the spawn error").IsEqual("");
    }

    // ----- Helpers -----

    /// <summary>Waits for the minimap's own frames, then asserts it shows; returns it.</summary>
    private static async Task<MinimapHud> ShownMinimap(World world, string when)
    {
        AssertThat(world.Minimap).OverrideFailureMessage($"{when}: World has no minimap").IsNotNull();
        var minimap = world.Minimap!;
        for (var i = 0; i < ProcessFramesAfterChange; i++)
        {
            await world.ToSignal(world.GetTree(), SceneTree.SignalName.ProcessFrame);
        }

        AssertBool(minimap.IsShown).OverrideFailureMessage($"{when}: the minimap isn't shown").IsTrue();
        AssertThat(minimap.Projection).OverrideFailureMessage($"{when}: no projection").IsNotNull();
        return minimap;
    }

    private static async Task AssertHidden(MinimapHud minimap, string when)
    {
        for (var i = 0; i < ProcessFramesAfterChange; i++)
        {
            await minimap.ToSignal(minimap.GetTree(), SceneTree.SignalName.ProcessFrame);
        }

        AssertBool(minimap.IsShown).OverrideFailureMessage($"{when}: the minimap is shown").IsFalse();
    }

    private static Core.Minimap.MinimapProjection MinimapProjection(MinimapHud minimap)
    {
        AssertThat(minimap.Projection).IsNotNull();
        return minimap.Projection!;
    }

    private static void AssertCentredOnPlayer(World world, MinimapHud minimap, string when)
    {
        var player = world.Player!.GlobalPosition;
        var projection = MinimapProjection(minimap);
        AssertFloat((float)projection.CentreX).OverrideFailureMessage($"{when}: centre x {projection.CentreX}, player at {player}").IsEqualApprox(player.X, 0.05f);
        AssertFloat((float)projection.CentreZ).OverrideFailureMessage($"{when}: centre z {projection.CentreZ}, player at {player}").IsEqualApprox(player.Z, 0.05f);
    }

    /// <summary>The panel's rectangle is <paramref name="corner"/>'s: the margin from both of its edges, the diameter across.</summary>
    internal static void AssertCorner(MinimapHud minimap, MinimapCorner corner, string when)
    {
        var viewport = minimap.GetViewport().GetVisibleRect().Size;
        var d = minimap.PanelDiameter;
        var m = minimap.ScreenMargin;
        var right = corner is MinimapCorner.TopRight or MinimapCorner.BottomRight;
        var bottom = corner is MinimapCorner.BottomLeft or MinimapCorner.BottomRight;
        var expected = new Rect2(right ? viewport.X - m - d : m, bottom ? viewport.Y - m - d : m, d, d);
        var rect = minimap.PanelRect;
        AssertVector(rect.Position)
            .OverrideFailureMessage($"{when}: panel at {rect} in a {viewport} viewport, expected {expected} ({corner})")
            .IsEqualApprox(expected.Position, new Vector2(0.01f, 0.01f));
        AssertVector(rect.Size)
            .OverrideFailureMessage($"{when}: panel size {rect.Size}, expected {d} × {d}")
            .IsEqualApprox(expected.Size, new Vector2(0.01f, 0.01f));
    }

    private void TearDownMainOnly()
    {
        _runner?.Dispose();
        _runner = null;
        if (_main is not null && GodotObject.IsInstanceValid(_main))
        {
            _main.GetParent()?.RemoveChild(_main);
            _main.Free();
        }

        _main = null;
    }

    /// <summary>Chooses item <paramref name="index"/> as the user does: the option shows it and emits <c>item_selected</c>.</summary>
    private static void Pick(OptionButton option, int index)
    {
        AssertBool(option.Disabled).OverrideFailureMessage($"{option.Name} is disabled").IsFalse();
        option.Select(index);
        option.EmitSignal(OptionButton.SignalName.ItemSelected, index);
    }
}
