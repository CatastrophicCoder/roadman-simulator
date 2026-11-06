using System;
using System.Linq;
using System.Threading.Tasks;
using GdUnit4;
using Godot;
using Roadman.Autoload;
using Roadman.Core.Maps;
using Roadman.Core.Saves;
using Roadman.Core.Settings;
using Roadman.Core.Player;
using Roadman.Scenes;
using Roadman.Ui;
using static GdUnit4.Assertions;
using static Roadman.Tests.MainTestSupport;
using File = System.IO.File;

namespace Roadman.Tests;

/// <summary>
/// <c>Main</c> at startup and on its own screens, Part A of <c>docs/specs/menu-settings-save.md</c> (<c>Ac</c>
/// numbers are its acceptance criteria): the main menu (8), a new game and a failed start (10), focus loss on the menu
/// (13), Quit and the close request on the menu (18), keyboard-only and gamepad-only use of the main menu, its slot
/// list, the dialog and the failed-start screen (22), and the user folder and version (23). Saves and settings live in a temporary folder (<see cref="MainTestSupport"/>).
/// </summary>
[TestSuite]
[RequireGodotRuntime]
public class MainMenuTest
{
    /// <summary>Criterion 10: a fixed seed for the seeded new game.</summary>
    private const int TestSeed = 5;

    /// <summary>Physics frames for the main menu to settle (its first frames) before checks.</summary>
    private const int MenuFrames = 3;

    private string _folder = "";
    private Main? _main;
    private Variant? _savedMapIdSetting;

    [BeforeTest]
    public void SetUp() => _folder = BeginTest("main-menu");

    [AfterTest]
    public void CleanUp()
    {
        TearDown(_folder, _main);
        _main = null;
        _folder = "";
        if (_savedMapIdSetting is { } saved)
        {
            ProjectSettings.SetSetting(MapLoader.MapIdSetting, saved);
            _savedMapIdSetting = null;
        }
    }

    // ----- Criterion 8: startup -----

    [TestCase]
    public async Task Ac8_StartupWithNoArgs_ShowsTheMainMenuWithFiveButtonsInOrderAndTheVersion_NoWorld_NoErrors()
    {
        var capture = new ErrorCapture();
        OS.AddLogger(capture);
        try
        {
            using var runner = ISceneRunner.Load(_main = NewMain(_folder), true);
            await WorldEdgeTest.PhysicsFrames(_main, MenuFrames);
            var menu = _main.MainMenu;

            AssertThat(_main.State).IsEqual(MainState.MainMenu);
            AssertBool(menu.IsVisibleInTree()).IsTrue();
            AssertBool(_main.Background.Visible).IsTrue();
            AssertThat(menu.Title.Text).IsEqual("Roadman Simulator: Somero-City");
            AssertThat(string.Join("|", menu.Buttons.Select(b => b.Text))).IsEqual("New game|Continue|Load|Settings|Quit");
            var box = menu.NewGameButton.GetParent();
            AssertThat(string.Join("|", menu.Buttons.Select(b => b.GetIndex())))
                .OverrideFailureMessage("the buttons aren't in that order in the menu")
                .IsEqual(string.Join("|", menu.Buttons.Select(b => b.GetIndex()).Order()));
            AssertBool(menu.Buttons.All(b => b.GetParent() == box)).IsTrue();
            AssertThat(menu.VersionLabel.Text).IsEqual("Version dev");
            AssertBool(menu.VersionLabel.IsVisibleInTree()).IsTrue();
            AssertThat(_main.World).IsNull();
            AssertInt(WorldEdgeTest.Descendants(_main.GetTree().Root).OfType<World>().Count()).IsEqual(0);
            AssertThat(Input.MouseMode).IsEqual(Input.MouseModeEnum.Visible);
            AssertBool(_main.GetTree().Paused).IsFalse();

            // An empty saves folder: Continue and Load disabled, and not focusable; Settings enabled.
            AssertBool(menu.SettingsButton.Disabled).IsFalse();
            foreach (var button in new[] { menu.ContinueButton, menu.LoadButton })
            {
                AssertBool(button.Disabled).OverrideFailureMessage($"{button.Text} is enabled").IsTrue();
                AssertThat(button.FocusMode).OverrideFailureMessage($"{button.Text} is focusable").IsEqual(Control.FocusModeEnum.None);
            }

            AssertBool(menu.NewGameButton.Disabled).IsFalse();
            AssertBool(menu.QuitButton.Disabled).IsFalse();
            AssertThat(FocusOwner(_main)).IsSame(menu.NewGameButton);
        }
        finally
        {
            OS.RemoveLogger(capture);
        }

        GD.Print($"MainMenuTest: Ac8 warnings and errors at startup: {string.Join(" | ", capture.Errors)}");
        AssertThat(string.Join(" | ", capture.ErrorsOnly)).IsEqual("");
    }

    [TestCase]
    public async Task Ac8_OneValidSave_EnablesContinueAndLoad_AndADamagedOneAloneDoesNot()
    {
        System.IO.Directory.CreateDirectory(SavesOf(_folder));
        File.WriteAllText(SlotPath(_folder, SlotId.Manual(3)), "{ not json");
        using (var runner = ISceneRunner.Load(_main = NewMain(_folder), true))
        {
            await WorldEdgeTest.PhysicsFrames(_main, MenuFrames);
            AssertBool(_main.MainMenu.ContinueButton.Disabled).OverrideFailureMessage("Continue enabled with only a damaged save").IsTrue();
            AssertBool(_main.MainMenu.LoadButton.Disabled).OverrideFailureMessage("Load enabled with only a damaged save").IsTrue();
        }

        _main = null;
        WriteSave(_folder, SlotId.Manual(2), SampleSave(position: new LocalPoint(1, 2, 3), facingYaw: 0.5));
        using var second = ISceneRunner.Load(_main = NewMain(_folder), true);
        await WorldEdgeTest.PhysicsFrames(_main, MenuFrames);
        var menu = _main.MainMenu;
        AssertBool(menu.ContinueButton.Disabled).IsFalse();
        AssertBool(menu.LoadButton.Disabled).IsFalse();
        AssertThat(menu.ContinueButton.FocusMode).IsEqual(Control.FocusModeEnum.All);
        AssertThat(menu.LoadButton.FocusMode).IsEqual(Control.FocusModeEnum.All);
        AssertBool(menu.SettingsButton.Disabled).IsFalse();
        AssertThat(menu.SettingsButton.FocusMode).IsEqual(Control.FocusModeEnum.All);
        AssertThat(FocusOwner(_main)).IsSame(menu.NewGameButton);
    }

    // ----- Criterion 10: new game -----

    /// <summary>
    /// New game from the menu button: the loading screen shows "Loading &lt;display name&gt;…" first, then the player
    /// spawns on the spawn area's open ground with <c>PlayerState</c> reset (the previous money, gear and wanted level
    /// gone), play time 0 at the start, the player camera current, the loading screen gone and <c>GameStarted</c> fired
    /// once. No save is written.
    /// </summary>
    [TestCase]
    public async Task Ac10_NewGame_ShowsTheLoadingScreen_ThenSpawnsWithStateResetPlayTimeZeroAndThePlayerCameraCurrent()
    {
        WorldEdgeTest.AssertPackPresent();
        var state = WorldEdgeTest.PlayerStateOfGame();
        state.AddMoney(777);
        state.AddGear("baseball_bat");
        state.SetWantedLevel(4);
        using var runner = ISceneRunner.Load(_main = NewMain(_folder), true);
        await WorldEdgeTest.PhysicsFrames(_main, MenuFrames);
        var started = 0;
        double? playTimeAtStart = null;
        _main.GameStarted += () =>
        {
            started++;
            playTimeAtStart = _main.PlayTime.Seconds;
        };
        Vector3? spawned = null;
        var disconnect = OnEachWorld(_main, world => world.PlayerSpawned += p => spawned = p);
        try
        {
            Click(_main.MainMenu.NewGameButton);
            AssertThat(_main.State).IsEqual(MainState.Loading);
            AssertBool(_main.LoadingScreen.IsVisibleInTree()).IsTrue();
            AssertBool(_main.LoadingScreen.Failed).IsFalse();
            var displayName = _main.SlotList.MapDisplayName("somero");
            AssertThat(_main.LoadingScreen.StatusText).IsEqual($"Loading {displayName}…");
            AssertBool(_main.MainMenu.IsVisibleInTree()).IsFalse();
            AssertInt(state.Money).IsEqual(0);
            AssertInt(state.Gear.Count).IsEqual(0);
            AssertInt(state.WantedLevel).IsEqual(0);

            var world = await WaitForGame(_main);
            AssertInt(started).IsEqual(1);
            AssertThat(playTimeAtStart).IsEqual(0d);
            AssertBool(_main.LoadingScreen.Visible).IsFalse();
            AssertThat(spawned).OverrideFailureMessage("PlayerSpawned didn't fire").IsNotNull();
            var openGround = world.MapLoader.Boundary!.SpawnArea.OpenGround;
            AssertBool(SpawnPicker.Contains(openGround, spawned!.Value.X, spawned.Value.Z))
                .OverrideFailureMessage($"spawn {spawned} is outside spawn_area.open_ground").IsTrue();
            AssertBool(world.PlayerCamera!.IsCurrent).IsTrue();
            AssertThat(world.GetViewport().GetCamera3D()).IsSame(world.PlayerCamera.Camera);
            AssertInt(state.Money).IsEqual(0);
            AssertInt(state.Gear.Count).IsEqual(0);
            AssertInt(state.WantedLevel).IsEqual(0);
            AssertBool(System.IO.Directory.Exists(SavesOf(_folder)) && System.IO.Directory.EnumerateFileSystemEntries(SavesOf(_folder)).Any())
                .OverrideFailureMessage("a new game wrote a save").IsFalse();

            await WorldEdgeTest.PhysicsFrames(_main, 30);
            AssertFloat(_main.PlayTime.Seconds).IsGreater(0d);
        }
        finally
        {
            disconnect();
        }
    }

    /// <summary>
    /// <c>--new-game --seed 5</c> spawns where a <c>World</c> instanced directly with seed 5 spawns. <c>Main</c> reads
    /// <c>--seed</c> from <see cref="Main.UserArgs"/> in tests, like <c>--new-game</c>.
    /// </summary>
    [TestCase]
    public async Task Ac10_NewGameWithSeed_SpawnsWhereADirectWorldRunWithTheSameSeedSpawns()
    {
        WorldEdgeTest.AssertPackPresent();
        Vector3? direct = null;
        using (var worldRunner = ISceneRunner.Load(WorldEdgeTest.NewWorld(TestSeed), true))
        {
            var world = (World)worldRunner.Scene()!;
            world.PlayerSpawned += p => direct = p;
            AssertBool(await WorldEdgeTest.WaitForSpawn(world)).IsTrue();
        }

        Vector3? viaMain = null;
        int? worldSeed = null;
        _main = NewMain(_folder, "--new-game", "--seed", TestSeed.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var disconnect = OnEachWorld(_main, world => world.PlayerSpawned += p =>
        {
            viaMain = p;
            worldSeed = world.Seed;
        });
        try
        {
            using var runner = ISceneRunner.Load(_main, true);
            await WaitForGame(_main);
        }
        finally
        {
            disconnect();
        }

        GD.Print(Invariant($"MainMenuTest: Ac10 seed {TestSeed}: direct World {direct}, through Main {viaMain} (World.Seed {worldSeed})"));
        AssertThat(direct).IsNotNull();
        AssertThat(viaMain).IsNotNull();
        AssertThat(worldSeed).OverrideFailureMessage($"Main's World ran with seed {worldSeed}, not the --seed {TestSeed} of Main.UserArgs").IsEqual(TestSeed);
        AssertThat(viaMain).IsEqual(direct);
    }

    /// <summary>
    /// A new game on a map whose pack fails to load (a map id with no pack, set in the project setting the new game
    /// reads): the loading screen shows the error text <c>MapLoader</c> pushes and "Back to main menu" (focused), and
    /// no <c>World</c> is left; Back returns to the main menu.
    /// </summary>
    [TestCase]
    public async Task Ac10_NewGameOnAPackThatFailsToLoad_ShowsTheErrorAndBackToMainMenu_AndNoWorldIsLeft()
    {
        const string missing = "no-such-pack-test";
        _savedMapIdSetting = ProjectSettings.GetSetting(MapLoader.MapIdSetting, MapLoader.DefaultMapId);
        ProjectSettings.SetSetting(MapLoader.MapIdSetting, missing);
        var capture = new ErrorCapture();
        OS.AddLogger(capture);
        try
        {
            using var runner = ISceneRunner.Load(_main = NewMain(_folder), true);
            await WorldEdgeTest.PhysicsFrames(_main, MenuFrames);
            Click(_main.MainMenu.NewGameButton);
            AssertBool(await WorldEdgeTest.WaitUntil(_main, () => _main.State == MainState.LoadFailed, GameStartFrames))
                .OverrideFailureMessage($"the start didn't fail (state {_main.State})").IsTrue();
            await WorldEdgeTest.PhysicsFrames(_main, 2);

            var screen = _main.LoadingScreen;
            AssertBool(screen.IsVisibleInTree()).IsTrue();
            AssertBool(screen.Failed).IsTrue();
            AssertBool(screen.BackButton.IsVisibleInTree()).IsTrue();
            AssertThat(screen.BackButton.Text).IsEqual("Back to main menu");
            AssertThat(FocusOwner(_main)).IsSame(screen.BackButton);
            AssertThat(_main.World).IsNull();
            AssertInt(WorldEdgeTest.Descendants(_main.GetTree().Root).OfType<World>().Count(w => !WorldEdgeTest.IsFreed(w)))
                .OverrideFailureMessage("a World is left after the failed start").IsEqual(0);
            var pushed = capture.ErrorsOnly.Where(e => e.Contains("MapLoader", StringComparison.Ordinal)).ToList();
            GD.Print($"MainMenuTest: Ac10 failed start: '{screen.ErrorText}'; pushed: {string.Join(" | ", capture.ErrorsOnly)}");
            AssertBool(pushed.Count > 0).OverrideFailureMessage("MapLoader pushed no error").IsTrue();
            AssertBool(pushed.Any(e => e.Contains(screen.ErrorText, StringComparison.Ordinal)))
                .OverrideFailureMessage($"the screen's text '{screen.ErrorText}' isn't the pushed error ({string.Join(" | ", pushed)})").IsTrue();
            AssertBool(screen.ErrorText.Contains(missing, StringComparison.Ordinal)).IsTrue();

            Click(screen.BackButton);
            AssertThat(_main.State).IsEqual(MainState.MainMenu);
            AssertBool(_main.MainMenu.IsVisibleInTree()).IsTrue();
            AssertBool(screen.Visible).IsFalse();
        }
        finally
        {
            OS.RemoveLogger(capture);
        }
    }

    // ----- Criterion 13: focus loss on the menu -----

    [TestCase]
    public async Task Ac13_WindowFocusLossOnTheMainMenu_DoesNothing()
    {
        using var runner = ISceneRunner.Load(_main = NewMain(_folder), true);
        await WorldEdgeTest.PhysicsFrames(_main, MenuFrames);
        _main.GetTree().Root.PropagateNotification((int)Node.NotificationWMWindowFocusOut);
        await WorldEdgeTest.PhysicsFrames(_main, 2);
        AssertThat(_main.State).IsEqual(MainState.MainMenu);
        AssertBool(_main.MainMenu.IsVisibleInTree()).IsTrue();
        AssertBool(_main.PauseMenu.Visible).IsFalse();
        AssertBool(_main.Dialog.IsOpen).IsFalse();
        AssertBool(_main.GetTree().Paused).IsFalse();
        _main.GetTree().Root.PropagateNotification((int)Node.NotificationWMWindowFocusIn);
    }

    // ----- Criterion 18: quitting from the menu -----

    [TestCase]
    public async Task Ac18_QuitAndACloseRequestOnTheMainMenu_AskQuitTheGameFirst_CancelStays_QuitExitsWithCode0()
    {
        using var runner = ISceneRunner.Load(_main = NewMain(_folder), true);
        await WorldEdgeTest.PhysicsFrames(_main, MenuFrames);
        var quits = new System.Collections.Generic.List<int>();
        _main.QuitRequested += code => quits.Add(code);

        // The close request asks first; Cancel keeps the menu.
        _main.GetTree().Root.PropagateNotification((int)Node.NotificationWMCloseRequest);
        AssertBool(_main.Dialog.IsOpen).OverrideFailureMessage("the close request didn't ask").IsTrue();
        AssertThat(_main.Dialog.MessageText).IsEqual("Quit the game?");
        AssertThat(_main.Dialog.DetailText).IsEqual("");
        AssertThat(string.Join("|", _main.Dialog.Buttons.Select(b => b.Text))).IsEqual("Quit|Cancel");
        AssertThat(_main.ExitCode).IsNull();
        ClickDialog(_main, "Cancel");
        AssertBool(_main.Dialog.IsOpen).IsFalse();
        AssertThat(_main.State).IsEqual(MainState.MainMenu);
        AssertThat(_main.ExitCode).IsNull();
        AssertInt(quits.Count).IsEqual(0);

        // The Quit button asks the same; Quit exits with code 0 and writes no autosave (no game).
        Click(_main.MainMenu.QuitButton);
        AssertBool(_main.Dialog.IsOpen).IsTrue();
        AssertThat(_main.Dialog.MessageText).IsEqual("Quit the game?");
        AssertThat(_main.ExitCode).IsNull();
        ClickDialog(_main, "Quit");
        AssertThat(_main.ExitCode).IsEqual(0);
        AssertThat(string.Join(",", quits)).IsEqual("0");
        AssertBool(File.Exists(SlotPath(_folder, SlotId.Autosave))).IsFalse();

        // And the close request's own Quit exits with 0 too.
        _main.GetTree().Root.PropagateNotification((int)Node.NotificationWMCloseRequest);
        AssertBool(_main.Dialog.IsOpen).IsTrue();
        ClickDialog(_main, "Quit");
        AssertThat(string.Join(",", quits)).IsEqual("0,0");
    }

    /// <summary>
    /// Amendment 3 (implementation of part A): the close request while the loading screen shows asks "Quit the game?"
    /// without the autosave line, and back closes it there. Asked again and left open until the player is placed, the
    /// game opens paused under it: the tree paused, the pause menu under the dialog, the mouse released while the
    /// camera still wants it. Cancel then returns to the pause menu (focus on Resume).
    /// </summary>
    [TestCase]
    public async Task Ac18_CloseRequestWhileLoading_AsksWithoutAutosave_LoadingFinishesPausedUnderIt_CancelGoesToThePauseMenu()
    {
        WorldEdgeTest.AssertPackPresent();
        using var runner = ISceneRunner.Load(_main = NewMain(_folder, "--new-game"), true);
        var world = await QuitAskedWhileLoadingThenPlaced(_main);
        ClickDialog(_main!, "Cancel");
        AssertBool(_main!.Dialog.IsOpen).IsFalse();
        AssertThat(_main.State).IsEqual(MainState.Paused);
        AssertBool(_main.GetTree().Paused).IsTrue();
        AssertBool(_main.PauseMenu.IsVisibleInTree()).IsTrue();
        AssertThat(FocusOwner(_main)).IsSame(_main.PauseMenu.ResumeButton);
        AssertThat(_main.ExitCode).IsNull();

        Click(_main.PauseMenu.ResumeButton);
        AssertThat(_main.State).IsEqual(MainState.Playing);
        AssertBool(world.PlayerCamera!.WantsCapture).IsTrue();
        AssertBool(File.Exists(SlotPath(_folder, SlotId.Autosave))).IsFalse();
    }

    /// <summary>
    /// As above, but Quit on the question that stayed open through the end of the load: exit code 0 and no autosave,
    /// as the question (without the autosave line) promised.
    /// </summary>
    [TestCase]
    public async Task Ac18_CloseRequestWhileLoading_QuitAfterTheLoadFinished_ExitsWith0AndWritesNoAutosave()
    {
        WorldEdgeTest.AssertPackPresent();
        using var runner = ISceneRunner.Load(_main = NewMain(_folder, "--new-game"), true);
        await QuitAskedWhileLoadingThenPlaced(_main);
        var quits = new System.Collections.Generic.List<int>();
        _main!.QuitRequested += code => quits.Add(code);
        ClickDialog(_main, "Quit");
        AssertThat(_main.ExitCode).IsEqual(0);
        AssertThat(string.Join(",", quits)).IsEqual("0");
        AssertBool(File.Exists(SlotPath(_folder, SlotId.Autosave)))
            .OverrideFailureMessage("quitting from the question asked while loading wrote an autosave").IsFalse();
        AssertBool(System.IO.Directory.Exists(SavesOf(_folder)) && System.IO.Directory.EnumerateFileSystemEntries(SavesOf(_folder)).Any())
            .OverrideFailureMessage("quitting from the question asked while loading wrote a file in the saves folder").IsFalse();
    }

    /// <summary>
    /// With <paramref name="main"/> just started with <c>--new-game</c>: sends the close request on the loading screen
    /// (checks the question, closes it with Esc, sends it again) and waits until the player is placed; checks the
    /// paused game under the open question.
    /// </summary>
    private static async Task<World> QuitAskedWhileLoadingThenPlaced(Main main)
    {
        AssertThat(main.State).IsEqual(MainState.Loading);

        main.GetTree().Root.PropagateNotification((int)Node.NotificationWMCloseRequest);
        AssertBool(main.Dialog.IsOpen).OverrideFailureMessage("the close request on the loading screen didn't ask").IsTrue();
        AssertThat(main.Dialog.MessageText).IsEqual("Quit the game?");
        AssertThat(main.Dialog.DetailText).OverrideFailureMessage("the question on the loading screen promises an autosave").IsEqual("");
        AssertThat(string.Join("|", main.Dialog.Buttons.Select(b => b.Text))).IsEqual("Quit|Cancel");

        // Back closes it on the loading screen too.
        TapKey(Key.Escape);
        AssertBool(main.Dialog.IsOpen).OverrideFailureMessage("Esc didn't close the question on the loading screen").IsFalse();
        AssertThat(main.State).IsEqual(MainState.Loading);

        main.GetTree().Root.PropagateNotification((int)Node.NotificationWMCloseRequest);
        AssertBool(main.Dialog.IsOpen).IsTrue();
        AssertThat(main.State).IsEqual(MainState.Loading);

        var placed = await WorldEdgeTest.WaitUntil(main, () => main.World?.Player is not null && main.State != MainState.Loading, GameStartFrames);
        AssertBool(placed).OverrideFailureMessage($"no player placed (state {main.State}, loading error '{main.LoadingScreen.ErrorText}')").IsTrue();
        await WorldEdgeTest.PhysicsFrames(main, 3);
        var world = main.World!;
        var camera = world.PlayerCamera!;
        AssertThat(main.State).OverrideFailureMessage("the game didn't open paused under the open question").IsEqual(MainState.Paused);
        AssertBool(main.GetTree().Paused).IsTrue();
        AssertBool(main.LoadingScreen.Visible).IsFalse();
        AssertBool(main.PauseMenu.IsVisibleInTree()).IsTrue();
        AssertBool(main.Dialog.IsOpen).OverrideFailureMessage("the question closed when the load finished").IsTrue();
        AssertThat(main.Dialog.MessageText).IsEqual("Quit the game?");
        AssertThat(main.Dialog.DetailText).OverrideFailureMessage("the question changed to promise an autosave").IsEqual("");
        AssertThat(FocusOwner(main)).IsSame(main.Dialog.ButtonByText("Quit"));
        AssertThat(Input.MouseMode).IsNotEqual(Input.MouseModeEnum.Captured);
        AssertBool(camera.MouseCaptured).IsFalse();
        AssertBool(camera.WantsCapture).OverrideFailureMessage("the camera forgot it wanted the mouse").IsTrue();
        AssertThat(main.ExitCode).IsNull();
        return world;
    }

    // ----- Startup: direct start -----

    /// <summary>
    /// "Startup and scenes": with <c>--screenshot</c> or <c>--perf</c> in the user args <c>Main</c> only adds
    /// <c>World</c>: state <see cref="MainState.DirectStart"/>, no menu UI, <see cref="SceneTree.AutoAcceptQuit"/> as
    /// it was, the settings not read (a sentinel <see cref="GameState.Settings"/> stays, though a settings file
    /// exists) and no saves store. This checks <c>Main</c>'s branch only: <c>World</c> reads the process's own
    /// command line, not <see cref="Main.UserArgs"/>, so here it runs as a plain world (no screenshot or perf tool,
    /// nothing quits or writes a file); the tools themselves are covered by the screenshot and perf runs.
    /// </summary>
    [TestCase]
    public async Task Startup_ScreenshotOrPerfArgs_DirectStart_NoMenus_AutoAcceptQuitUnchanged_SettingsNotRead()
    {
        WorldEdgeTest.AssertPackPresent();
        var gameState = GameState.Instance;
        var tree = (SceneTree)Engine.GetMainLoop();
        var originalSettings = gameState.Settings;
        var originalAutoAccept = tree.AutoAcceptQuit;
        try
        {
            foreach (var args in new[] { new[] { "--screenshot", "overview", "x.png" }, new[] { "--perf", "spawn", "x.json" } })
            {
                var what = string.Join(" ", args);
                tree.AutoAcceptQuit = true;
                var sentinel = GameSettings.Default with { };
                gameState.Settings = sentinel;
                File.WriteAllText(System.IO.Path.Combine(_folder, "settings.json"), "{ not json");

                using (var runner = ISceneRunner.Load(_main = NewMain(_folder, args), true))
                {
                    AssertThat(_main.State).OverrideFailureMessage($"{what}: state").IsEqual(MainState.DirectStart);
                    AssertBool(_main.DirectStart).IsTrue();
                    var world = _main.GetChildren().OfType<World>().SingleOrDefault();
                    AssertThat(world).OverrideFailureMessage($"{what}: no World child").IsNotNull();
                    AssertThat(_main.World).OverrideFailureMessage($"{what}: a direct start isn't a game").IsNull();
                    AssertBool(await WorldEdgeTest.WaitUntil(_main, () => world!.Player is not null, GameStartFrames))
                        .OverrideFailureMessage($"{what}: the world didn't spawn a player").IsTrue();
                    await WorldEdgeTest.PhysicsFrames(_main, 3);

                    AssertThat(_main.State).IsEqual(MainState.DirectStart);
                    AssertThat(_main.ProcessMode).IsEqual(Node.ProcessModeEnum.Inherit);
                    AssertThat(_main.GetNodeOrNull("Ui")).OverrideFailureMessage($"{what}: the menu UI was built").IsNull();
                    var menus = WorldEdgeTest.Descendants(tree.Root)
                        .Where(n => n is MainMenu or PauseMenu or SlotList or LoadingScreen or ConfirmDialog)
                        .Select(n => n.Name.ToString())
                        .ToList();
                    AssertThat(string.Join(", ", menus)).OverrideFailureMessage($"{what}: menus in the tree: {string.Join(", ", menus)}").IsEqual("");
                    AssertBool(tree.AutoAcceptQuit).OverrideFailureMessage($"{what}: AutoAcceptQuit changed").IsTrue();
                    AssertBool(tree.Paused).IsFalse();
                    AssertThat(gameState.Settings).OverrideFailureMessage($"{what}: the settings were read").IsSame(sentinel);
                    AssertBool(System.IO.Directory.Exists(SavesOf(_folder))).OverrideFailureMessage($"{what}: a saves folder was made").IsFalse();

                    // A close request does nothing in Main (no question; the window closes as before).
                    tree.Root.PropagateNotification((int)Node.NotificationWMCloseRequest);
                    AssertThat(_main.ExitCode).IsNull();
                    AssertThat(_main.State).IsEqual(MainState.DirectStart);
                }

                _main = null;
            }
        }
        finally
        {
            gameState.Settings = originalSettings;
            tree.AutoAcceptQuit = originalAutoAccept;
        }
    }

    // ----- Criterion 22: keyboard and gamepad alone -----

    [TestCase]
    public async Task Ac22_MainMenuWithoutSaves_Keyboard_FocusStartsOnNewGameAndReachesOnlyTheEnabledButtons() =>
        await AssertMainMenuWithoutSaves(Keyboard);

    [TestCase]
    public async Task Ac22_MainMenuWithoutSaves_GamepadDpad_FocusStartsOnNewGameAndReachesOnlyTheEnabledButtons() =>
        await AssertMainMenuWithoutSaves(PadDpad);

    private async Task AssertMainMenuWithoutSaves(Nav nav)
    {
        using var runner = ISceneRunner.Load(_main = NewMain(_folder), true);
        await WorldEdgeTest.PhysicsFrames(_main, MenuFrames);
        var menu = _main.MainMenu;
        AssertFocusAndReach(_main, nav, "main menu (no saves)", menu.NewGameButton, menu.Buttons);
    }

    /// <summary>
    /// With a valid save, a damaged slot and the autosave: on the main menu every enabled button is reachable; Load
    /// opens the slot list with accept; there the focus starts on the first enabled row and every enabled row, every
    /// Delete button and Back are reachable; a Delete opens the dialog, whose buttons are reachable and whose Cancel
    /// is back; back leaves the slot list; Quit's dialog the same. The keyboard alone (arrows, Enter, Esc).
    /// </summary>
    [TestCase]
    public async Task Ac22_MainMenuSlotListAndDialog_KeyboardAlone_FocusFirstEnabledReachEveryEnabledControlAcceptAndBack() =>
        await AssertMainMenuSlotListAndDialog(Keyboard);

    /// <summary>As the keyboard case, with the d-pad, the south face button (accept) and the east face button (back).</summary>
    [TestCase]
    public async Task Ac22_MainMenuSlotListAndDialog_GamepadAlone_FocusFirstEnabledReachEveryEnabledControlAcceptAndBack() =>
        await AssertMainMenuSlotListAndDialog(PadDpad);

    private async Task AssertMainMenuSlotListAndDialog(Nav nav)
    {
        var now = DateTimeOffset.UtcNow;
        WriteSave(_folder, SlotId.Autosave, SampleSave(position: new LocalPoint(1, 2, 3), savedAt: now.AddMinutes(-5)));
        WriteSave(_folder, SlotId.Manual(2), SampleSave(position: new LocalPoint(4, 5, 6), savedAt: now));
        File.WriteAllText(SlotPath(_folder, SlotId.Manual(4)), "{ damaged");
        using var runner = ISceneRunner.Load(_main = NewMain(_folder), true);
        await WorldEdgeTest.PhysicsFrames(_main, MenuFrames);
        var menu = _main.MainMenu;
        var list = _main.SlotList;

        AssertThat(_main.State).IsEqual(MainState.MainMenu);
        menu.FocusFirst();
        AssertFocusAndReach(_main, nav, "main menu (saves)", menu.NewGameButton, menu.Buttons);
        AssertBool(menu.SettingsButton.Disabled).IsFalse();

        // Load with accept.
        menu.NewGameButton.GrabFocus();
        nav.Down();
        nav.Down();
        AssertThat(FocusOwner(_main)).OverrideFailureMessage($"{nav}: two downs from New game reach {NameOf(FocusOwner(_main))}").IsSame(menu.LoadButton);
        nav.Accept();
        AssertBool(list.IsVisibleInTree()).OverrideFailureMessage($"{nav}: accept on Load didn't open the slot list").IsTrue();
        AssertThat(list.Mode).IsEqual(SlotListMode.Load);
        var firstEnabled = list.Rows.First(r => !r.Button.Disabled).Button;
        var controls = list.Rows.SelectMany(r => new Control[] { r.Button, r.Delete }).Append(list.BackButton).ToList();
        AssertFocusAndReach(_main, nav, "slot list (load)", firstEnabled, controls);
        AssertBool(list.RowOf(SlotId.Manual(4)).Delete.IsVisibleInTree() && !list.RowOf(SlotId.Manual(4)).Delete.Disabled)
            .OverrideFailureMessage("the damaged row has no enabled Delete").IsTrue();

        // A Delete opens the dialog; its buttons are reachable; back is Cancel and keeps the file.
        list.RowOf(SlotId.Manual(4)).Delete.GrabFocus();
        nav.Accept();
        AssertBool(_main.Dialog.IsOpen).OverrideFailureMessage($"{nav}: accept on Delete didn't ask").IsTrue();
        AssertThat(_main.Dialog.MessageText).IsEqual("Delete slot 4? This can't be undone.");
        AssertFocusAndReach(_main, nav, "delete dialog", _main.Dialog.Buttons[0], _main.Dialog.Buttons);
        nav.Cancel();
        AssertBool(_main.Dialog.IsOpen).IsFalse();
        AssertBool(File.Exists(SlotPath(_folder, SlotId.Manual(4)))).IsTrue();
        AssertBool(list.IsVisibleInTree()).OverrideFailureMessage($"{nav}: back in the dialog closed the slot list too").IsTrue();

        // Back leaves the slot list for the menu.
        nav.Cancel();
        AssertBool(list.Visible).IsFalse();
        AssertBool(menu.IsVisibleInTree()).IsTrue();
        AssertThat(FocusOwner(_main)).IsSame(menu.NewGameButton);

        // Quit's dialog with accept; back cancels; back on the top level does nothing.
        menu.QuitButton.GrabFocus();
        nav.Accept();
        AssertBool(_main.Dialog.IsOpen).IsTrue();
        AssertFocusAndReach(_main, nav, "quit dialog", _main.Dialog.ButtonByText("Quit")!, _main.Dialog.Buttons);
        nav.Cancel();
        AssertBool(_main.Dialog.IsOpen).IsFalse();
        AssertThat(_main.ExitCode).IsNull();
        nav.Cancel();
        AssertThat(_main.State).IsEqual(MainState.MainMenu);
        AssertBool(menu.IsVisibleInTree()).IsTrue();
        AssertBool(_main.Dialog.IsOpen).IsFalse();
    }

    /// <summary>
    /// The failed-start screen (a save naming a map with no pack, loaded directly): the focus starts on "Back to main
    /// menu", the only control; accept, and back, go to the main menu. The keyboard alone, then the gamepad alone
    /// (d-pad, south face button, east face button).
    /// </summary>
    [TestCase]
    public async Task Ac22_LoadFailedScreen_KeyboardAlone_FocusStartsOnBack_AcceptAndBackReturnToTheMenu() =>
        await AssertLoadFailedScreen(Keyboard);

    [TestCase]
    public async Task Ac22_LoadFailedScreen_GamepadAlone_FocusStartsOnBack_AcceptAndBackReturnToTheMenu() =>
        await AssertLoadFailedScreen(PadDpad);

    private async Task AssertLoadFailedScreen(Nav nav)
    {
        using var runner = ISceneRunner.Load(_main = NewMain(_folder), true);
        await WorldEdgeTest.PhysicsFrames(_main, MenuFrames);
        AssertBool(_main.LoadSave(SampleSave(mapId: "not-installed-map", position: new LocalPoint(0, 0, 0)))).IsFalse();
        AssertThat(_main.State).IsEqual(MainState.LoadFailed);
        AssertBool(_main.LoadingScreen.Failed).IsTrue();
        AssertBool(_main.LoadingScreen.ErrorText.Contains("Map not-installed-map not installed", StringComparison.Ordinal)).IsTrue();
        AssertThat(_main.World).IsNull();
        AssertFocusAndReach(_main, nav, "failed start", _main.LoadingScreen.BackButton, [_main.LoadingScreen.BackButton]);
        nav.Accept();
        AssertThat(_main.State).OverrideFailureMessage($"{nav}: accept on Back didn't return to the menu").IsEqual(MainState.MainMenu);
        AssertBool(_main.MainMenu.IsVisibleInTree()).IsTrue();

        // Back on the failed screen returns to the menu too.
        _main.LoadSave(SampleSave(mapId: "not-installed-map", position: new LocalPoint(0, 0, 0)));
        AssertThat(_main.State).IsEqual(MainState.LoadFailed);
        nav.Cancel();
        AssertThat(_main.State).OverrideFailureMessage($"{nav}: back on the failed screen didn't return to the menu").IsEqual(MainState.MainMenu);
    }

    // ----- Criterion 23: user folder and version -----

    [TestCase]
    public async Task Ac23_UserDataDirEndsInRoadmanSimulatorNotUnderAppUserdata_AndTheMenuShowsVersionDev()
    {
        var dir = OS.GetUserDataDir().TrimEnd('/', '\\');
        GD.Print($"MainMenuTest: Ac23 user data dir {dir}");
        AssertBool(dir.EndsWith("RoadmanSimulator", StringComparison.Ordinal)).OverrideFailureMessage($"user dir {dir}").IsTrue();
        AssertBool(dir.Contains("app_userdata", StringComparison.Ordinal)).OverrideFailureMessage($"user dir {dir} is under app_userdata").IsFalse();
        AssertThat(ProjectSettings.GetSetting(Main.VersionSetting).AsString()).IsEqual("dev");
        AssertThat(Main.GameVersion).IsEqual("dev");

        using var runner = ISceneRunner.Load(_main = NewMain(_folder), true);
        await WorldEdgeTest.PhysicsFrames(_main, MenuFrames);
        AssertThat(_main.MainMenu.VersionLabel.Text).IsEqual("Version dev");

        // The tests never touch the real user folder: the settings and saves are in the temporary one.
        AssertBool(GameState.Instance.ResolvedSettingsPath.StartsWith(_folder, StringComparison.Ordinal)).IsTrue();
    }
}
