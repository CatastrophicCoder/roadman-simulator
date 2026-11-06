using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using GdUnit4;
using Godot;
using Roadman.Autoload;
using Roadman.Core.Saves;
using Roadman.Core.Settings;
using Roadman.Debug;
using Roadman.OnFoot;
using Roadman.Scenes;
using Roadman.Settings;
using Roadman.Ui;
using static GdUnit4.Assertions;
using static Roadman.Tests.MainTestSupport;
using SettingsWindowMode = Roadman.Core.Settings.WindowMode;

namespace Roadman.Tests;

/// <summary>
/// The settings view, Part B of <c>docs/specs/menu-settings-save.md</c> (<c>Ac</c> numbers are its acceptance
/// criteria): each value applied at once and the settings file written on close and read back by a new <c>Main</c>
/// (19), the display confirm-or-revert dialog (20), the controls table (21), and keyboard-only and gamepad-only use of
/// the four tabs and their dialogs (22). <c>Main</c> gets a <see cref="RecordingDisplayBackend"/> before it enters the
/// tree, since the headless display server ignores window modes and sizes. Saves and settings live in a temporary
/// folder (<see cref="MainTestSupport"/>), whose <see cref="MainTestSupport.TearDown"/> puts back every global the
/// view changes (<see cref="MainTestSupport.EngineState"/>, <see cref="DebugBuild.Override"/>) after each test.
/// </summary>
[TestSuite]
[RequireGodotRuntime]
public class SettingsViewTest
{
    /// <summary>Physics frames for the main menu to settle (its first frames) before checks.</summary>
    private const int MenuFrames = 3;

    /// <summary>Criterion 20: the countdown left when the test hands over to the view's own real-time count, s.</summary>
    private const double CountdownTail = 0.5;

    /// <summary>Criterion 20: physics frames allowed for <see cref="CountdownTail"/> to run out (about 2 s at 60 Hz).</summary>
    private const int CountdownTailFrames = 120;

    private static readonly string[] Buses = [SettingsApplier.MasterBus, SettingsApplier.MusicBus, SettingsApplier.EffectsBus];

    /// <summary>Criterion 21: console makers' names (copied from <c>Roadman.Core.Tests/Settings/ControlNamesTests.cs</c>).</summary>
    private static readonly string[] ConsoleWords =
    [
        "A", "B", "X", "Y",
        "Cross", "Circle", "Square", "Triangle",
        "L1", "R1", "L2", "R2", "L3", "R3",
        "LB", "RB", "LT", "RT", "LS", "RS",
        "ZL", "ZR", "SL", "SR",
        "Xbox", "PlayStation", "PS", "PS4", "PS5", "DualShock", "DualSense",
        "Nintendo", "Switch", "Joy-Con", "Joycon", "Wii",
        "Options", "Share", "Create", "Select", "View", "Menu", "Home", "Capture", "Touchpad",
        "Minus", "Plus", "Steam", "Sony", "Microsoft",
    ];

    private static readonly Regex ConsoleWordPattern = new(
        @"(?<![\p{L}\p{N}])(" + string.Join("|", ConsoleWords.Select(Regex.Escape)) + @")(?![\p{L}\p{N}])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex ActionLine = new(@"^([A-Za-z0-9_]+)\s*=\s*\{", RegexOptions.CultureInvariant);

    private string _folder = "";
    private Main? _main;
    private ISceneRunner? _runner;
    private RecordingDisplayBackend _backend = new();

    [BeforeTest]
    public void SetUp()
    {
        _folder = BeginTest("settings-view");
        _backend = new RecordingDisplayBackend();
    }

    [AfterTest]
    public void CleanUp()
    {
        _runner?.Dispose();
        _runner = null;
        TearDown(_folder, _main);
        _main = null;
        _folder = "";
    }

    // ----- Criterion 19: every value applies at once -----

    /// <summary>
    /// On the main menu: a window size change calls <c>SetWindowSize</c>, a window mode change <c>SetWindowMode</c>
    /// (back to a window also re-sends the size); the render scale and quality set the root viewport's
    /// <c>Scaling3DScale</c> (preset factor × render scale in fullscreen, the factor alone in a window), <c>Msaa3D</c> and
    /// <c>ScreenSpaceAA</c> per <see cref="QualityPreset"/>; vsync calls <c>SetVSync</c>; the frame cap sets
    /// <see cref="Engine.MaxFps"/>. Every change is visible right after the control's signal, with no frame between.
    /// </summary>
    [TestCase]
    public async Task Ac19_DisplayValues_ApplyAtOnce_WindowThroughTheBackend_RenderScaleAndQualityOnTheViewport_VSyncAndFrameCap()
    {
        var view = await OpenSettingsOnMainMenu();
        var viewport = _main!.GetViewport();
        var big = new WindowSize(1920, 1080);
        AssertThat(view.WindowSizeItems.Select(s => s.ToString()).ToArray())
            .OverrideFailureMessage("the size list isn't WindowSizes.Fitting of the backend's usable screen")
            .IsEqual(WindowSizes.Fitting(_backend.UsableSize!.Value).Select(s => s.ToString()).ToArray());

        _backend.Calls.Clear();
        Pick(view.WindowSizeOption, view.WindowSizeItems.ToList().IndexOf(big));
        AssertBool(_backend.Calls.Contains("SetWindowSize(1920×1080)")).OverrideFailureMessage($"calls: {Calls()}").IsTrue();
        AssertBool(_backend.Calls.Any(c => c.StartsWith("SetWindowMode", StringComparison.Ordinal))).OverrideFailureMessage($"calls: {Calls()}").IsFalse();
        AssertThat(_backend.Size).IsEqual(big);
        ClickDialog(_main, "Keep");

        _backend.Calls.Clear();
        Pick(view.WindowModeOption, (int)SettingsWindowMode.BorderlessFullscreen);
        AssertBool(_backend.Calls.Contains("SetWindowMode(BorderlessFullscreen)")).OverrideFailureMessage($"calls: {Calls()}").IsTrue();
        AssertBool(_backend.Calls.Any(c => c.StartsWith("SetWindowSize", StringComparison.Ordinal)))
            .OverrideFailureMessage($"a size was set in fullscreen: {Calls()}").IsFalse();
        AssertThat(_backend.Mode).IsEqual(SettingsWindowMode.BorderlessFullscreen);
        AssertScale(viewport, 1.0, "Medium, fullscreen, 100 %");
        ClickDialog(_main, "Keep");

        AssertBool(view.RenderScaleSlider.Editable).OverrideFailureMessage("render scale disabled in fullscreen").IsTrue();
        view.RenderScaleSlider.Value = 70;
        AssertInt(view.Settings.Display.RenderScale).IsEqual(70);
        AssertScale(viewport, 0.7, "Medium, fullscreen, 70 %");
        ClickDialog(_main, "Keep");

        Pick(view.QualityOption, (int)QualityLevel.Low);
        AssertBool(_main.Dialog.IsOpen).OverrideFailureMessage("a quality change asked to keep").IsFalse();
        AssertThat(viewport.Msaa3D).IsEqual(Viewport.Msaa.Disabled);
        AssertThat(viewport.ScreenSpaceAA).IsEqual(Viewport.ScreenSpaceAAEnum.Fxaa);
        AssertScale(viewport, 0.75 * 0.7, "Low, fullscreen, 70 %");

        Pick(view.QualityOption, (int)QualityLevel.High);
        AssertThat(viewport.Msaa3D).IsEqual(Viewport.Msaa.Msaa4X);
        AssertThat(viewport.ScreenSpaceAA).IsEqual(Viewport.ScreenSpaceAAEnum.Disabled);
        AssertScale(viewport, 0.7, "High, fullscreen, 70 %");

        Pick(view.QualityOption, (int)QualityLevel.Medium);
        AssertThat(viewport.Msaa3D).IsEqual(Viewport.Msaa.Msaa2X);
        AssertThat(viewport.ScreenSpaceAA).IsEqual(Viewport.ScreenSpaceAAEnum.Disabled);

        Pick(view.QualityOption, (int)QualityLevel.Low);
        _backend.Calls.Clear();
        Pick(view.WindowModeOption, (int)SettingsWindowMode.Windowed);
        AssertBool(_backend.Calls.Contains("SetWindowMode(Windowed)")).OverrideFailureMessage($"calls: {Calls()}").IsTrue();
        AssertBool(_backend.Calls.Contains("SetWindowSize(1920×1080)"))
            .OverrideFailureMessage($"back to a window didn't set the window size: {Calls()}").IsTrue();
        AssertScale(viewport, 0.75, "Low, windowed: the factor alone, render scale 70 % ignored");
        AssertBool(view.RenderScaleSlider.Editable).OverrideFailureMessage("render scale editable in a window").IsFalse();
        ClickDialog(_main, "Keep");

        _backend.Calls.Clear();
        view.VSyncCheck.ButtonPressed = false;
        AssertThat(Calls()).IsEqual("SetVSync(False)");
        AssertThat(_backend.VSync).IsEqual(false);
        view.VSyncCheck.ButtonPressed = true;
        AssertThat(_backend.VSync).IsEqual(true);
        AssertBool(_backend.Calls.Any(c => c.StartsWith("SetWindowSize", StringComparison.Ordinal)))
            .OverrideFailureMessage($"a vsync change moved the window: {Calls()}").IsFalse();

        Pick(view.FrameCapOption, SettingsRules.FrameCaps.ToList().IndexOf(60));
        AssertInt(Engine.MaxFps).IsEqual(60);
        Pick(view.FrameCapOption, SettingsRules.FrameCaps.ToList().IndexOf(30));
        AssertInt(Engine.MaxFps).IsEqual(30);
        Pick(view.FrameCapOption, SettingsRules.FrameCaps.ToList().IndexOf(0));
        AssertInt(Engine.MaxFps).IsEqual(0);
        AssertBool(_main.Dialog.IsOpen).IsFalse();
        AssertThat(GameState.Instance.Settings).IsEqual(view.Settings);
    }

    /// <summary>
    /// Each volume slider sets its <c>AudioServer</c> bus at once: <see cref="VolumeMath.ToDb"/> of the percentage, not
    /// muted; 0 mutes the bus; raising it again unmutes it. The other buses keep their values.
    /// </summary>
    [TestCase]
    public async Task Ac19_EachVolume_SetsItsBusAtOnce_ZeroMutes_RaisingUnmutes()
    {
        var view = await OpenSettingsOnMainMenu();
        view.SelectTab(SettingsView.AudioTab);
        var sliders = new[] { view.MasterSlider, view.MusicSlider, view.EffectsSlider };
        for (var i = 0; i < Buses.Length; i++)
        {
            var bus = AudioServer.GetBusIndex(Buses[i]);
            AssertInt(bus).OverrideFailureMessage($"no bus {Buses[i]} in default_bus_layout.tres").IsGreaterEqual(0);
            var others = Enumerable.Range(0, Buses.Length).Where(j => j != i)
                .Select(j => (AudioServer.GetBusVolumeDb(AudioServer.GetBusIndex(Buses[j])), AudioServer.IsBusMute(AudioServer.GetBusIndex(Buses[j]))))
                .ToList();

            sliders[i].Value = 50;
            AssertFloat(AudioServer.GetBusVolumeDb(bus)).OverrideFailureMessage($"{Buses[i]} at 50 %").IsEqualApprox((float)VolumeMath.ToDb(50), 0.01f);
            AssertBool(AudioServer.IsBusMute(bus)).IsFalse();

            sliders[i].Value = 0;
            AssertBool(AudioServer.IsBusMute(bus)).OverrideFailureMessage($"{Buses[i]} at 0 % isn't muted").IsTrue();

            sliders[i].Value = 35;
            AssertBool(AudioServer.IsBusMute(bus)).OverrideFailureMessage($"{Buses[i]} at 35 % is still muted").IsFalse();
            AssertFloat(AudioServer.GetBusVolumeDb(bus)).IsEqualApprox((float)VolumeMath.ToDb(35), 0.01f);

            sliders[i].Value = 100;
            AssertFloat(AudioServer.GetBusVolumeDb(bus)).IsEqualApprox(0f, 0.01f);

            var othersAfter = Enumerable.Range(0, Buses.Length).Where(j => j != i)
                .Select(j => (AudioServer.GetBusVolumeDb(AudioServer.GetBusIndex(Buses[j])), AudioServer.IsBusMute(AudioServer.GetBusIndex(Buses[j]))))
                .ToList();
            AssertBool(others.SequenceEqual(othersAfter)).OverrideFailureMessage($"changing {Buses[i]} changed another bus").IsTrue();
        }

        AssertThat(view.Settings.Audio).IsEqual(new AudioSettings(100, 100, 100));
        AssertThat(view.MasterValue.Text).IsEqual("100 %");
    }

    /// <summary>
    /// In a running game (opened from the pause menu): quality sets <c>World.Sun</c>'s shadows (Medium at the start:
    /// two splits, 800 m; Low: orthogonal, 300 m; High: four splits, 1500 m) and the root viewport at once; the
    /// keep-or-revert countdown also runs while the tree is paused.
    /// </summary>
    [TestCase]
    public async Task Ac19_QualityInARunningGame_SetsTheSunsShadowsAtOnce_AndAc20_TheCountdownRunsWhilePaused()
    {
        WorldEdgeTest.AssertPackPresent();
        var view = await OpenSettingsInGame();
        var world = _main!.World!;
        var sun = world.Sun!;
        AssertThat(world.QualitySettings).IsEqual(QualityPreset.Medium);
        AssertSun(sun, QualityPreset.Medium);

        Pick(view.QualityOption, (int)QualityLevel.Low);
        AssertSun(sun, QualityPreset.Low);
        AssertThat(sun.DirectionalShadowMode).IsEqual(DirectionalLight3D.ShadowMode.Orthogonal);
        AssertThat(_main.GetViewport().ScreenSpaceAA).IsEqual(Viewport.ScreenSpaceAAEnum.Fxaa);

        Pick(view.QualityOption, (int)QualityLevel.High);
        AssertSun(sun, QualityPreset.High);
        AssertThat(sun.DirectionalShadowMode).IsEqual(DirectionalLight3D.ShadowMode.Parallel4Splits);
        AssertThat(_main.GetViewport().Msaa3D).IsEqual(Viewport.Msaa.Msaa4X);
        AssertBool(_main.Dialog.IsOpen).IsFalse();

        // Criterion 20 while paused: the countdown is real time, so it runs out with the tree paused.
        AssertBool(_main.GetTree().Paused).IsTrue();
        Pick(view.WindowModeOption, (int)SettingsWindowMode.BorderlessFullscreen);
        AssertBool(view.RevertPending).IsTrue();
        view.Revert.Tick(DisplayRevert.TimeoutSeconds - CountdownTail);
        AssertBool(await WorldEdgeTest.WaitUntil(_main, () => !view.RevertPending, CountdownTailFrames))
            .OverrideFailureMessage("the countdown didn't run out while paused").IsTrue();
        AssertThat(view.Settings.Display.WindowMode).IsEqual(SettingsWindowMode.Windowed);
        AssertThat(_backend.Mode).IsEqual(SettingsWindowMode.Windowed);
    }

    /// <summary>
    /// Invert and sensitivity values apply to the player camera at once (sensitivity = the camera's default × the
    /// multiplier), and to the camera of the next new game and of the next load (Continue on the autosave Quit to menu
    /// wrote).
    /// </summary>
    [TestCase]
    public async Task Ac19_GameplayValues_ApplyToThePlayerCameraAtOnce_AndAfterTheNextNewGameAndLoad()
    {
        WorldEdgeTest.AssertPackPresent();
        var view = await OpenSettingsInGame();
        view.SelectTab(SettingsView.GameplayTab);
        var camera = _main!.World!.PlayerCamera!;
        AssertFloat(camera.MouseSensitivity).IsEqualApprox(PlayerCamera.DefaultMouseSensitivity, 1e-7f);
        AssertFloat(camera.StickSensitivity).IsEqualApprox(PlayerCamera.DefaultStickSensitivity, 1e-6f);

        view.InvertMouseCheck.ButtonPressed = true;
        AssertBool(camera.InvertMouseY).OverrideFailureMessage("invert mouse Y didn't reach the camera at once").IsTrue();
        AssertBool(camera.InvertStickY).IsFalse();
        view.InvertStickCheck.ButtonPressed = true;
        AssertBool(camera.InvertStickY).IsTrue();
        view.MouseSensitivitySlider.Value = 2.0;
        AssertFloat(camera.MouseSensitivity).IsEqualApprox(PlayerCamera.DefaultMouseSensitivity * 2f, 1e-7f);
        view.StickSensitivitySlider.Value = 0.5;
        AssertFloat(camera.StickSensitivity).IsEqualApprox(PlayerCamera.DefaultStickSensitivity * 0.5f, 1e-6f);
        AssertCameraMatches(camera, "at once");

        // Close the view (writes the file), quit to the menu (autosave), start a new game.
        _main.Back();
        AssertBool(view.Visible).IsFalse();
        Click(_main.PauseMenu.QuitToMenuButton);
        ClickDialog(_main, "Quit");
        AssertThat(_main.State).IsEqual(MainState.MainMenu);
        AssertBool(File.Exists(SlotPath(_folder, SlotId.Autosave))).IsTrue();
        Click(_main.MainMenu.NewGameButton);
        var world = await WaitForGame(_main);
        AssertCameraMatches(world.PlayerCamera!, "after the next new game");

        // And after a load.
        _main.OpenPause();
        Click(_main.PauseMenu.QuitToMenuButton);
        ClickDialog(_main, "Quit");
        AssertThat(_main.State).IsEqual(MainState.MainMenu);
        AssertBool(_main.Continue()).OverrideFailureMessage("Continue didn't load the autosave").IsTrue();
        world = await WaitForGame(_main);
        AssertCameraMatches(world.PlayerCamera!, "after the next load");
    }

    /// <summary>
    /// Closing the view writes <c>settings.json</c> with every changed value; a new <c>Main</c> (new recording
    /// backend) reads it back, and the window size, vsync, frame cap and bus volumes are applied before its menu UI
    /// exists (checked when the <c>Ui</c> layer enters the tree), with no warning.
    /// </summary>
    [TestCase]
    public async Task Ac19_ClosingWritesTheFile_ANewMainReadsItBackAndAppliesItBeforeTheMenuShows()
    {
        var view = await OpenSettingsOnMainMenu();
        var settingsPath = Path.Combine(_folder, "settings.json");
        AssertBool(File.Exists(settingsPath)).OverrideFailureMessage("the file was written before the view closed").IsFalse();
        Pick(view.WindowSizeOption, view.WindowSizeItems.ToList().IndexOf(new WindowSize(1280, 720)));
        ClickDialog(_main!, "Keep");
        Pick(view.QualityOption, (int)QualityLevel.High);
        view.VSyncCheck.ButtonPressed = false;
        Pick(view.FrameCapOption, SettingsRules.FrameCaps.ToList().IndexOf(30));
        view.SelectTab(SettingsView.AudioTab);
        view.MasterSlider.Value = 40;
        view.MusicSlider.Value = 0;
        view.EffectsSlider.Value = 55;
        view.SelectTab(SettingsView.GameplayTab);
        view.InvertMouseCheck.ButtonPressed = true;
        view.StickSensitivitySlider.Value = 1.5;
        Pick(view.MinimapCornerOption, (int)MinimapCorner.TopRight);
        var expected = view.Settings;

        Click(view.BackButton);
        AssertBool(view.Visible).IsFalse();
        AssertThat(view.LastSaveError).IsNull();
        AssertBool(File.Exists(settingsPath)).OverrideFailureMessage("closing the view didn't write settings.json").IsTrue();
        var written = SettingsReader.Read(File.ReadAllText(settingsPath));
        AssertThat(string.Join(" | ", written.Warnings)).IsEqual("");
        AssertThat(written.Settings).IsEqual(expected);

        // A fresh start: other values in the engine first, then a new Main.
        TearDownMainOnly();
        Engine.MaxFps = 0;
        foreach (var name in Buses)
        {
            AudioServer.SetBusMute(AudioServer.GetBusIndex(name), false);
            AudioServer.SetBusVolumeDb(AudioServer.GetBusIndex(name), 0f);
        }

        GameState.Instance.Settings = GameSettings.Default;
        _backend = new RecordingDisplayBackend();
        _main = NewMain(_folder);
        _main.DisplayBackend = _backend;
        string? callsAtUi = null;
        int? maxFpsAtUi = null;
        (float, bool)[]? busesAtUi = null;
        GameSettings? settingsAtUi = null;
        _main.ChildEnteredTree += child =>
        {
            if (child.Name == "Ui" && callsAtUi is null)
            {
                callsAtUi = Calls();
                maxFpsAtUi = Engine.MaxFps;
                busesAtUi = [.. Buses.Select(n => (AudioServer.GetBusVolumeDb(AudioServer.GetBusIndex(n)), AudioServer.IsBusMute(AudioServer.GetBusIndex(n))))];
                settingsAtUi = GameState.Instance.Settings;
            }
        };
        _runner = ISceneRunner.Load(_main, true);
        await WorldEdgeTest.PhysicsFrames(_main, MenuFrames);

        AssertThat(_main.State).IsEqual(MainState.MainMenu);
        AssertThat(GameState.Instance.Settings).IsEqual(expected);
        AssertThat(string.Join(" | ", GameState.Instance.SettingsWarnings)).IsEqual("");
        AssertThat(settingsAtUi).OverrideFailureMessage("the settings weren't read before the menu UI was built").IsEqual(expected);
        AssertThat(callsAtUi).OverrideFailureMessage("the window size and vsync weren't applied before the menu").IsEqual("SetWindowSize(1280×720), SetVSync(False)");
        AssertThat(maxFpsAtUi).IsEqual(30);
        AssertFloat(busesAtUi![0].Item1).IsEqualApprox((float)VolumeMath.ToDb(40), 0.01f);
        AssertBool(busesAtUi[0].Item2).IsFalse();
        AssertBool(busesAtUi[1].Item2).OverrideFailureMessage("Music at 0 % wasn't muted at startup").IsTrue();
        AssertFloat(busesAtUi[2].Item1).IsEqualApprox((float)VolumeMath.ToDb(55), 0.01f);
        AssertThat(_main.GetViewport().Msaa3D).IsEqual(Viewport.Msaa.Msaa4X);
    }

    /// <summary>
    /// A settings file with one bad value (an unknown quality) starts with that value's default (Medium), keeps the
    /// others (vsync off, master 40 %, invert mouse on) and gives exactly one warning, naming the key.
    /// </summary>
    [TestCase]
    public async Task Ac19_SettingsFileWithOneBadValue_StartsWithItsDefault_KeepsTheOthers_OneWarning()
    {
        var good = GameSettings.Default with
        {
            Display = GameSettings.Default.Display with { Quality = QualityLevel.High, VSync = false },
            Audio = GameSettings.Default.Audio with { MasterVolume = 40 },
            Gameplay = GameSettings.Default.Gameplay with { InvertMouseY = true },
        };
        var json = SettingsWriter.Write(good);
        AssertBool(json.Contains("\"high\"", StringComparison.Ordinal)).IsTrue();
        File.WriteAllText(Path.Combine(_folder, "settings.json"), json.Replace("\"high\"", "\"ultra\"", StringComparison.Ordinal));

        _main = NewMain(_folder);
        _main.DisplayBackend = _backend;
        _runner = ISceneRunner.Load(_main, true);
        await WorldEdgeTest.PhysicsFrames(_main, MenuFrames);

        var settings = GameState.Instance.Settings;
        AssertThat(settings).IsEqual(good with { Display = good.Display with { Quality = QualityLevel.Medium } });
        AssertInt(GameState.Instance.SettingsWarnings.Count).OverrideFailureMessage($"warnings: {string.Join(" | ", GameState.Instance.SettingsWarnings)}").IsEqual(1);
        AssertBool(GameState.Instance.SettingsWarnings[0].Contains("quality", StringComparison.Ordinal)).IsTrue();
        AssertThat(_backend.VSync).IsEqual(false);
        AssertThat(_main.GetViewport().Msaa3D).IsEqual(Viewport.Msaa.Msaa2X);
    }

    // ----- Criterion 20: confirm or revert -----

    /// <summary>
    /// A window mode change shows "Keep these display settings?" with a 10-second countdown and Keep and Revert; the
    /// display controls are disabled and a further display change is ignored while it shows; Keep keeps the change and
    /// enables the controls again, and nothing reverts later.
    /// </summary>
    [TestCase]
    public async Task Ac20_AWindowModeChange_AsksWithA10SecondCountdown_BlocksDisplayChanges_KeepKeepsIt()
    {
        var view = await OpenSettingsOnMainMenu();
        Pick(view.WindowModeOption, (int)SettingsWindowMode.BorderlessFullscreen);
        AssertKeepQuestion(view);
        AssertThat(view.Revert.SecondsLeft).IsEqual(DisplayRevert.TimeoutSeconds);
        AssertThat(_main!.Dialog.DetailText).IsEqual("Reverting in 10 s");
        AssertDisplayControlsDisabled(view);

        var pending = view.Settings.Display;
        view.ChangeDisplay(pending with { Quality = QualityLevel.Low, RenderScale = 50 });
        AssertThat(view.Settings.Display).OverrideFailureMessage("a display change went through while the question showed").IsEqual(pending);

        ClickDialog(_main, "Keep");
        AssertBool(_main.Dialog.IsOpen).IsFalse();
        AssertBool(view.RevertPending).IsFalse();
        AssertThat(view.Settings.Display.WindowMode).IsEqual(SettingsWindowMode.BorderlessFullscreen);
        AssertThat(_backend.Mode).IsEqual(SettingsWindowMode.BorderlessFullscreen);
        foreach (var control in new BaseButton[] { view.WindowModeOption, view.QualityOption, view.VSyncCheck, view.FrameCapOption })
        {
            AssertBool(control.Disabled).OverrideFailureMessage($"{control.Name} stayed disabled after Keep").IsFalse();
        }

        AssertBool(view.RenderScaleSlider.Editable).IsTrue();
        await WorldEdgeTest.PhysicsFrames(_main, 10);
        AssertThat(view.Settings.Display.WindowMode).IsEqual(SettingsWindowMode.BorderlessFullscreen);
        AssertBool(view.Visible).IsTrue();
    }

    /// <summary>
    /// For each of the three changes (window mode, window size, render scale) and each way out other than Keep
    /// (Revert, <c>Main.Back()</c>, Esc, the east face button, the countdown running out): the three values from before
    /// the change come back, in the view, in <see cref="GameState.Settings"/>, on the backend and on the viewport; the
    /// dialog closes and the settings view stays open.
    /// </summary>
    [TestCase]
    public async Task Ac20_RevertBackEscEastButtonAndTimeout_EachRestoreModeSizeAndRenderScale()
    {
        var view = await OpenSettingsOnMainMenu();
        var main = _main!;
        var viewport = main.GetViewport();
        var windowed = GameSettings.Default.Display;
        var fullscreen = windowed with { WindowMode = SettingsWindowMode.BorderlessFullscreen };
        var changes = new (string Name, DisplaySettings Baseline, Action Change)[]
        {
            ("window mode", windowed, () => Pick(view.WindowModeOption, (int)SettingsWindowMode.BorderlessFullscreen)),
            ("window size", windowed, () => Pick(view.WindowSizeOption, view.WindowSizeItems.ToList().IndexOf(new WindowSize(1280, 720)))),
            ("render scale", fullscreen, () => view.RenderScaleSlider.Value = 60),
        };
        var triggers = new (string Name, Func<Task> Run)[]
        {
            ("Revert", () => { ClickDialog(main, "Revert"); return Task.CompletedTask; }),
            ("Main.Back()", () => { main.Back(); return Task.CompletedTask; }),
            ("Esc", () => { TapKey(Key.Escape); return Task.CompletedTask; }),
            ("east face button", () => { TapPad(GamepadButtons.East); return Task.CompletedTask; }),
            ("countdown", async () =>
            {
                view.Revert.Tick(DisplayRevert.TimeoutSeconds - CountdownTail);
                await main.ToSignal(main.GetTree(), SceneTree.SignalName.ProcessFrame);
                await main.ToSignal(main.GetTree(), SceneTree.SignalName.ProcessFrame);
                AssertThat(main.Dialog.DetailText).OverrideFailureMessage("the countdown text didn't follow the count").IsEqual("Reverting in 1 s");
                AssertBool(await WorldEdgeTest.WaitUntil(main, () => !view.RevertPending, CountdownTailFrames))
                    .OverrideFailureMessage("the countdown didn't run out").IsTrue();
            }),
        };

        foreach (var (changeName, baseline, change) in changes)
        {
            foreach (var (triggerName, run) in triggers)
            {
                var what = $"{changeName} then {triggerName}";
                view.ChangeDisplay(baseline);
                view.KeepDisplay();
                var before = view.Settings.Display;
                AssertThat(before).OverrideFailureMessage($"{what}: baseline not set").IsEqual(baseline);
                _backend.Calls.Clear();

                change();
                AssertThat(view.Settings.Display).OverrideFailureMessage($"{what}: the change didn't apply").IsNotEqual(before);
                AssertKeepQuestion(view, what);
                AssertDisplayControlsDisabled(view, what);

                await run();
                AssertBool(view.RevertPending).OverrideFailureMessage($"{what}: still pending").IsFalse();
                AssertBool(main.Dialog.IsOpen).OverrideFailureMessage($"{what}: the dialog is still open").IsFalse();
                AssertBool(view.IsVisibleInTree()).OverrideFailureMessage($"{what}: the settings view closed").IsTrue();
                AssertThat(view.Settings.Display).OverrideFailureMessage($"{what}: not restored").IsEqual(before);
                AssertThat(GameState.Instance.Settings.Display).IsEqual(before);
                AssertThat(_backend.Mode).OverrideFailureMessage($"{what}: backend mode; calls {Calls()}").IsEqual(before.WindowMode);
                if (before.WindowMode == SettingsWindowMode.Windowed)
                {
                    AssertThat(_backend.Calls.LastOrDefault(c => c.StartsWith("SetWindowSize", StringComparison.Ordinal)))
                        .OverrideFailureMessage($"{what}: the size wasn't set back; calls {Calls()}")
                        .IsEqual($"SetWindowSize({before.WindowSize})");
                }

                AssertScale(viewport, QualityPreset.EffectiveRenderScale(before.Quality, before.RenderScale, before.WindowMode == SettingsWindowMode.BorderlessFullscreen), what);
                AssertBool(view.WindowModeOption.Disabled).OverrideFailureMessage($"{what}: controls still disabled").IsFalse();
                ReleaseInputs();
            }
        }
    }

    /// <summary>Quality, vsync and the frame cap apply with no dialog and nothing pending.</summary>
    [TestCase]
    public async Task Ac20_QualityVSyncAndFrameCap_ApplyWithNoDialog()
    {
        var view = await OpenSettingsOnMainMenu();
        Pick(view.QualityOption, (int)QualityLevel.High);
        AssertBool(_main!.Dialog.IsOpen).OverrideFailureMessage("quality asked").IsFalse();
        AssertBool(view.RevertPending).IsFalse();
        view.VSyncCheck.ButtonPressed = false;
        AssertBool(_main.Dialog.IsOpen).OverrideFailureMessage("vsync asked").IsFalse();
        AssertBool(view.RevertPending).IsFalse();
        Pick(view.FrameCapOption, SettingsRules.FrameCaps.ToList().IndexOf(120));
        AssertBool(_main.Dialog.IsOpen).OverrideFailureMessage("frame cap asked").IsFalse();
        AssertBool(view.RevertPending).IsFalse();
        AssertThat(view.Settings.Display).IsEqual(GameSettings.Default.Display with { Quality = QualityLevel.High, VSync = false, FrameCap = 120 });
    }

    // ----- Criterion 21: the controls table -----

    /// <summary>
    /// With <see cref="DebugBuild.Override"/> false: the rows are exactly the non-debug, non-<c>ui_*</c> actions of
    /// <c>project.godot</c>'s <c>[input]</c> section (read from the file), each with a label; <c>pause</c> shows Escape
    /// and "Start button", <c>jump</c> Space and "South face button"; mouse look shows "Mouse"; no gamepad cell
    /// contains a console maker's name; no debug rows, and the "Debug" table is hidden.
    /// </summary>
    [TestCase]
    public async Task Ac21_ControlsTab_ListsEveryNonDebugActionWithKeyboardAndGamepadBindings_NoConsoleNames_NoDebugRowsInRelease()
    {
        DebugBuild.Override = false;
        var view = await OpenSettingsOnMainMenu();
        view.SelectTab(SettingsView.ControlsTab);
        var rows = view.ControlRows;
        var project = ProjectActions();
        var expected = project.Where(a => !a.StartsWith("ui_", StringComparison.Ordinal) && !a.StartsWith("debug_", StringComparison.Ordinal)).Order().ToList();
        GD.Print($"SettingsViewTest: Ac21 rows: {string.Join(" | ", rows.Select(r => $"{r.Label}: {r.Keys} / {r.Gamepad}"))}");

        AssertThat(string.Join(",", rows.Select(r => r.Action).Order())).IsEqual(string.Join(",", expected));
        AssertBool(rows.Any(r => r.IsDebug)).OverrideFailureMessage("debug rows with DebugBuild.Override false").IsFalse();
        AssertBool(((Control)view.FindChild("Debug", true, false)).Visible).OverrideFailureMessage("the Debug table shows in a release build").IsFalse();
        foreach (var row in rows)
        {
            AssertThat(row.Label).OverrideFailureMessage($"{row.Action} has no plain label").IsNotEqual(row.Action);
            AssertBool(row.Keys != SettingsView.NoBinding || row.Gamepad != SettingsView.NoBinding)
                .OverrideFailureMessage($"{row.Action} shows no binding").IsTrue();
            var match = ConsoleWordPattern.Match(row.Gamepad);
            AssertBool(match.Success).OverrideFailureMessage($"{row.Action}'s gamepad text '{row.Gamepad}' contains '{match.Value}'").IsFalse();
        }

        var byAction = rows.ToDictionary(r => r.Action);
        var pause = byAction["pause"];
        AssertThat(pause.Label).IsEqual("Pause");
        AssertBool(pause.Keys.Split(", ").Contains("Escape")).OverrideFailureMessage($"pause keys '{pause.Keys}'").IsTrue();
        AssertBool(pause.Gamepad.Split(", ").Contains("Start button")).OverrideFailureMessage($"pause gamepad '{pause.Gamepad}'").IsTrue();
        var jump = byAction["jump"];
        AssertBool(jump.Keys.Split(", ").Contains("Space")).OverrideFailureMessage($"jump keys '{jump.Keys}'").IsTrue();
        AssertBool(jump.Gamepad.Split(", ").Contains("South face button")).OverrideFailureMessage($"jump gamepad '{jump.Gamepad}'").IsTrue();
        AssertBool(byAction["move_forward"].Keys.Split(", ").Contains("W")).IsTrue();
        AssertBool(byAction["move_forward"].Gamepad.Split(", ").Contains("Left stick up")).IsTrue();
        AssertThat(byAction["run"].Label).IsEqual("Run (hold)");
        AssertThat(byAction["run_toggle"].Gamepad).IsEqual("Left-stick click");
        AssertBool(byAction["camera_left"].Keys.Split(", ").Contains(SettingsView.MouseLook)).IsTrue();
        AssertBool(byAction["camera_left"].Gamepad.Split(", ").Contains("Right stick left")).IsTrue();

        // Every gamepad binding in InputMap is in its row, by its generic name.
        foreach (var row in rows)
        {
            foreach (var pad in InputMap.ActionGetEvents(row.Action).Select(SettingsView.GamepadText).OfType<string>())
            {
                AssertBool(row.Gamepad.Split(", ").Contains(pad)).OverrideFailureMessage($"{row.Action}: '{pad}' missing from '{row.Gamepad}'").IsTrue();
            }
        }
    }

    /// <summary>With <see cref="DebugBuild.Override"/> true the debug actions of <c>project.godot</c> follow as a "Debug" group, shown.</summary>
    [TestCase]
    public async Task Ac21_ControlsTab_DebugRowsOnlyInADebugBuild()
    {
        DebugBuild.Override = true;
        var view = await OpenSettingsOnMainMenu();
        view.SelectTab(SettingsView.ControlsTab);
        var debug = ProjectActions().Where(a => a.StartsWith("debug_", StringComparison.Ordinal)).Order().ToList();
        AssertBool(debug.Contains("debug_camera")).IsTrue();
        AssertThat(string.Join(",", view.ControlRows.Where(r => r.IsDebug).Select(r => r.Action).Order())).IsEqual(string.Join(",", debug));
        AssertBool(view.ControlRows.SkipWhile(r => !r.IsDebug).All(r => r.IsDebug))
            .OverrideFailureMessage("the debug rows aren't a separate group after the gameplay rows").IsTrue();
        AssertBool(((Control)view.FindChild("Debug", true, false)).Visible).IsTrue();
        foreach (var row in view.ControlRows.Where(r => r.IsDebug))
        {
            AssertBool(ConsoleWordPattern.IsMatch(row.Gamepad)).OverrideFailureMessage($"{row.Action}: '{row.Gamepad}'").IsFalse();
        }

        // Back to a release build: reopening drops them.
        DebugBuild.Override = false;
        Click(view.BackButton);
        AssertBool(_main!.OpenSettings()).IsTrue();
        AssertBool(view.ControlRows.Any(r => r.IsDebug)).IsFalse();

        // The rebuilt table queued its old labels for deletion; let that frame end, so they aren't counted as orphans.
        await WorldEdgeTest.PhysicsFrames(_main, 1);
    }

    /// <summary>
    /// <c>docs/specs/inventory.md</c> criterion 23: the controls tab lists <c>inventory</c> as "Inventory" with Tab
    /// (the key's label from <c>InputMap</c>) and "Back button", not as a debug row, and no gamepad text names a
    /// console maker's button.
    /// </summary>
    [TestCase]
    public async Task InventoryAc23_ControlsTab_ListsInventoryWithTabAndBackButton_NoConsoleNames()
    {
        DebugBuild.Override = false;
        var view = await OpenSettingsOnMainMenu();
        view.SelectTab(SettingsView.ControlsTab);
        var rows = view.ControlRows.Where(r => r.Action == Main.InventoryAction).ToList();
        AssertInt(rows.Count).OverrideFailureMessage("no single inventory row on the controls tab").IsEqual(1);
        var row = rows[0];
        GD.Print($"SettingsViewTest: inventory Ac23 row: {row.Label}: {row.Keys} / {row.Gamepad}");
        AssertThat(row.Label).IsEqual("Inventory");
        AssertBool(row.IsDebug).IsFalse();
        AssertThat(row.Keys).IsEqual("Tab");
        AssertThat(row.Gamepad).IsEqual("Back button");
        foreach (var r in view.ControlRows)
        {
            var match = ConsoleWordPattern.Match(r.Gamepad);
            AssertBool(match.Success).OverrideFailureMessage($"{r.Action}'s gamepad text '{r.Gamepad}' contains '{match.Value}'").IsFalse();
        }
    }

    // ----- Criterion 22: keyboard alone and gamepad alone -----

    [TestCase]
    public async Task Ac22_SettingsTabsAndDialogs_KeyboardAlone_FocusStartsFirst_EveryEnabledControlReachable() =>
        await AssertTabsAndDialogs(Keyboard);

    [TestCase]
    public async Task Ac22_SettingsTabsAndDialogs_GamepadDpadAlone_FocusStartsFirst_EveryEnabledControlReachable() =>
        await AssertTabsAndDialogs(PadDpad);

    /// <summary>
    /// From the main menu with <paramref name="nav"/> alone: accept on Settings opens the view with the focus on the
    /// Display tab button; on each tab (reached by moving right along the tab buttons and accepting) every enabled
    /// control of the tab, the four tab buttons, Reset (not on the controls tab) and Back are reachable and nothing
    /// else; left on a volume slider lowers it by 5; Reset's dialog and the keep-or-revert dialog (opened by accepting
    /// on the window mode option and choosing in its list) have reachable buttons, and back cancels them (the window
    /// mode reverts); back closes the view with the focus on the menu's Settings button.
    /// </summary>
    private async Task AssertTabsAndDialogs(Nav nav)
    {
        _main = NewMain(_folder);
        _main.DisplayBackend = _backend;
        _runner = ISceneRunner.Load(_main, true);
        await WorldEdgeTest.PhysicsFrames(_main, MenuFrames);
        var view = _main.SettingsView;

        _main.MainMenu.SettingsButton.GrabFocus();
        nav.Accept();
        AssertBool(view.IsVisibleInTree()).OverrideFailureMessage($"{nav}: accept on Settings didn't open the view").IsTrue();

        for (var tab = 0; tab < SettingsView.TabNames.Length; tab++)
        {
            if (tab > 0)
            {
                view.TabButtons[tab - 1].GrabFocus();
                nav.Right();
                AssertThat(FocusOwner(_main)).OverrideFailureMessage($"{nav}: right from tab {tab - 1} reaches {NameOf(FocusOwner(_main))}").IsSame(view.TabButtons[tab]);
                nav.Accept();
                AssertInt(view.CurrentTab).OverrideFailureMessage($"{nav}: accept on the {SettingsView.TabNames[tab]} tab button").IsEqual(tab);
            }

            var tabControls = tab switch
            {
                SettingsView.DisplayTab => view.DisplayControls,
                SettingsView.AudioTab => view.AudioControls,
                SettingsView.GameplayTab => view.GameplayControls,
                _ => [],
            };
            var expected = view.TabButtons.Cast<Control>().Concat(tabControls).Append(view.ResetButton).Append(view.BackButton)
                .Where(UiFocus.IsFocusable)
                .ToList();
            AssertBool(expected.Contains(view.BackButton)).IsTrue();
            AssertBool(expected.Contains(view.ResetButton)).OverrideFailureMessage($"Reset on the {SettingsView.TabNames[tab]} tab").IsEqual(tab != SettingsView.ControlsTab);
            AssertFocusAndReach(_main, nav, $"settings, {SettingsView.TabNames[tab]} tab", view.TabButtons[tab], expected);
        }

        // Audio: a slider is operated with left. With the keyboard this is asserted. With the d-pad it isn't: in the
        // gdUnit4 test process (Godot 4.7.2, no joypad connected) an injected InputEventJoypadButton for d-pad left
        // moves the focus between controls but never steps an HSlider, not even held for 2 s with ui_left reported
        // pressed, while an InputEventAction ui_left does. Whether a real gamepad steps the sliders is left to the
        // playtest (criterion 27); the result is printed.
        view.TabButtons[SettingsView.AudioTab].GrabFocus();
        nav.Accept();
        view.MusicSlider.GrabFocus();
        var music = view.Settings.Audio.MusicVolume;
        nav.Left();
        var afterLeft = view.Settings.Audio.MusicVolume;
        GD.Print($"SettingsViewTest: Ac22 {nav}: left on the music slider {music} -> {afterLeft}");
        if (nav == Keyboard)
        {
            AssertInt(afterLeft).OverrideFailureMessage($"{nav}: left on the music slider").IsEqual(music - 5);
        }

        AssertThat(FocusOwner(_main)).IsSame(view.MusicSlider);

        // Reset's dialog: reachable buttons, back cancels.
        view.ResetButton.GrabFocus();
        nav.Accept();
        AssertBool(_main.Dialog.IsOpen).OverrideFailureMessage($"{nav}: accept on Reset didn't ask").IsTrue();
        AssertThat(_main.Dialog.MessageText).IsEqual("Reset audio settings to defaults?");
        AssertFocusAndReach(_main, nav, "reset dialog", _main.Dialog.Buttons[0], _main.Dialog.Buttons);
        nav.Cancel();
        AssertBool(_main.Dialog.IsOpen).IsFalse();
        AssertBool(view.IsVisibleInTree()).OverrideFailureMessage($"{nav}: back in the reset dialog closed the view").IsTrue();
        AssertInt(view.Settings.Audio.MusicVolume).OverrideFailureMessage("Cancel reset the volume").IsEqual(afterLeft);

        // The window mode option's list opens with accept; back closes only the list.
        view.TabButtons[SettingsView.DisplayTab].GrabFocus();
        nav.Accept();
        view.WindowModeOption.GrabFocus();
        nav.Accept();
        var popup = view.WindowModeOption.GetPopup();
        AssertBool(popup.Visible).OverrideFailureMessage($"{nav}: accept on the window mode option didn't open its list").IsTrue();
        await WorldEdgeTest.PhysicsFrames(_main, 1);
        nav.Cancel();
        await WorldEdgeTest.PhysicsFrames(_main, 1);
        AssertBool(popup.Visible).OverrideFailureMessage($"{nav}: back didn't close the window mode list").IsFalse();
        AssertBool(view.IsVisibleInTree()).OverrideFailureMessage($"{nav}: back in the window mode list closed the settings view").IsTrue();
        AssertThat(view.Settings.Display.WindowMode).IsEqual(SettingsWindowMode.Windowed);
        AssertBool(_main.Dialog.IsOpen).IsFalse();

        // The keep-or-revert dialog. The keyboard picks fullscreen in the list (down, accept). With the d-pad the list
        // can't be driven in the test process (Godot 4.7.2, no joypad connected): an injected d-pad down leaves the
        // list's focused item where it was and the south face button then picks that item, so the change is made with
        // ChangeDisplay and only the dialog is driven with the d-pad. A real gamepad in the list is for the playtest.
        if (nav == Keyboard)
        {
            view.WindowModeOption.GrabFocus();
            nav.Accept();
            AssertBool(popup.Visible).IsTrue();
            await WorldEdgeTest.PhysicsFrames(_main, 1);
            nav.Down();
            nav.Accept();
            await WorldEdgeTest.PhysicsFrames(_main, 1);
            AssertThat(view.Settings.Display.WindowMode)
                .OverrideFailureMessage($"{nav}: down and accept in the window mode list didn't choose fullscreen (list visible {popup.Visible})")
                .IsEqual(SettingsWindowMode.BorderlessFullscreen);
        }
        else
        {
            view.WindowModeOption.GrabFocus();
            view.ChangeDisplay(view.Settings.Display with { WindowMode = SettingsWindowMode.BorderlessFullscreen });
        }

        AssertKeepQuestion(view, nav.ToString());
        AssertFocusAndReach(_main, nav, "keep-or-revert dialog", _main.Dialog.ButtonByText("Keep")!, _main.Dialog.Buttons);
        nav.Cancel();
        AssertBool(view.RevertPending).IsFalse();
        AssertThat(view.Settings.Display.WindowMode).OverrideFailureMessage($"{nav}: back didn't revert").IsEqual(SettingsWindowMode.Windowed);
        AssertBool(view.IsVisibleInTree()).IsTrue();

        // Back closes the view.
        nav.Cancel();
        AssertBool(view.Visible).OverrideFailureMessage($"{nav}: back didn't close the view").IsFalse();
        AssertBool(_main.MainMenu.IsVisibleInTree()).IsTrue();
        AssertThat(FocusOwner(_main)).IsSame(_main.MainMenu.SettingsButton);
        AssertBool(File.Exists(Path.Combine(_folder, "settings.json"))).IsTrue();
    }

    // ----- Quitting with the view open -----

    /// <summary>
    /// A window close request while the settings view is open on the main menu, confirmed with Quit, writes
    /// <c>settings.json</c> with the changed volume; a window size change still waiting for keep or revert (its
    /// question replaced by the quit question) is written reverted.
    /// </summary>
    [TestCase]
    public async Task QuitConfirmedWithTheViewOpenOnTheMainMenu_WritesTheSettings_PendingDisplayChangeReverted()
    {
        var view = await OpenSettingsOnMainMenu();
        var settingsPath = Path.Combine(_folder, "settings.json");
        var sizeBefore = view.Settings.Display.WindowSize;
        var otherSize = view.WindowSizeItems.First(size => size != sizeBefore);
        Pick(view.WindowSizeOption, view.WindowSizeItems.ToList().IndexOf(otherSize));
        AssertBool(view.RevertPending).OverrideFailureMessage("a window size change didn't ask to keep").IsTrue();
        view.SelectTab(SettingsView.AudioTab);
        view.MasterSlider.Value = 35;

        _main!.GetTree().Root.PropagateNotification((int)Node.NotificationWMCloseRequest);
        ClickDialog(_main, "Quit");

        AssertThat(_main.ExitCode).IsEqual(0);
        AssertBool(view.Visible).OverrideFailureMessage("the settings view is still open after the quit").IsFalse();
        AssertBool(File.Exists(settingsPath)).OverrideFailureMessage("quitting with the view open didn't write settings.json").IsTrue();
        var written = SettingsReader.Read(File.ReadAllText(settingsPath));
        AssertThat(string.Join(" | ", written.Warnings)).IsEqual("");
        AssertInt(written.Settings.Audio.MasterVolume).IsEqual(35);
        AssertThat(written.Settings.Display.WindowSize)
            .OverrideFailureMessage($"the pending window size {otherSize} was written instead of the reverted {sizeBefore}")
            .IsEqual(sizeBefore);
    }

    /// <summary>
    /// A window close request while the settings view is open from the pause menu, confirmed with Quit (which
    /// autosaves), writes <c>settings.json</c> with the changed volume.
    /// </summary>
    [TestCase]
    public async Task QuitConfirmedWithTheViewOpenFromThePauseMenu_WritesTheSettings()
    {
        var view = await OpenSettingsInGame();
        var settingsPath = Path.Combine(_folder, "settings.json");
        view.SelectTab(SettingsView.AudioTab);
        view.EffectsSlider.Value = 45;

        _main!.GetTree().Root.PropagateNotification((int)Node.NotificationWMCloseRequest);
        ClickDialog(_main, "Quit");

        AssertThat(_main.ExitCode).IsEqual(0);
        AssertBool(File.Exists(SlotPath(_folder, SlotId.Autosave))).OverrideFailureMessage("the quit didn't autosave").IsTrue();
        AssertBool(view.Visible).OverrideFailureMessage("the settings view is still open after the quit").IsFalse();
        AssertBool(File.Exists(settingsPath)).OverrideFailureMessage("quitting with the view open didn't write settings.json").IsTrue();
        var written = SettingsReader.Read(File.ReadAllText(settingsPath));
        AssertThat(string.Join(" | ", written.Warnings)).IsEqual("");
        AssertInt(written.Settings.Audio.EffectsVolume).IsEqual(45);
    }

    // ----- Helpers -----

    private async Task<SettingsView> OpenSettingsOnMainMenu()
    {
        _main = NewMain(_folder);
        _main.DisplayBackend = _backend;
        _runner = ISceneRunner.Load(_main, true);
        await WorldEdgeTest.PhysicsFrames(_main, MenuFrames);
        AssertThat(_main.State).IsEqual(MainState.MainMenu);
        AssertBool(_main.OpenSettings()).OverrideFailureMessage("OpenSettings on the main menu").IsTrue();
        var view = _main.SettingsView;
        AssertBool(view.IsVisibleInTree()).IsTrue();
        AssertThat(FocusOwner(_main)).IsSame(view.TabButtons[SettingsView.DisplayTab]);
        return view;
    }

    private async Task<SettingsView> OpenSettingsInGame()
    {
        _main = NewMain(_folder, "--new-game");
        _main.DisplayBackend = _backend;
        _runner = ISceneRunner.Load(_main, true);
        await WaitForGame(_main);
        AssertBool(_main.OpenPause()).IsTrue();
        AssertBool(_main.OpenSettings()).OverrideFailureMessage("OpenSettings from the pause menu").IsTrue();
        return _main.SettingsView;
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
        AssertInt(index).OverrideFailureMessage($"{option.Name}: no such item").IsGreaterEqual(0);
        AssertBool(option.Disabled).OverrideFailureMessage($"{option.Name} is disabled").IsFalse();
        option.Select(index);
        option.EmitSignal(OptionButton.SignalName.ItemSelected, index);
    }

    private string Calls() => string.Join(", ", _backend.Calls);

    private static void AssertScale(Viewport viewport, double expected, string what) =>
        AssertFloat(viewport.Scaling3DScale).OverrideFailureMessage($"Scaling3DScale {viewport.Scaling3DScale}, expected {expected} ({what})")
            .IsEqualApprox((float)expected, 1e-4f);

    private static void AssertSun(DirectionalLight3D sun, QualityPreset preset)
    {
        AssertBool(sun.ShadowEnabled).IsEqual(preset.DirectionalShadows);
        var mode = preset.ShadowSplits switch
        {
            1 => DirectionalLight3D.ShadowMode.Orthogonal,
            2 => DirectionalLight3D.ShadowMode.Parallel2Splits,
            _ => DirectionalLight3D.ShadowMode.Parallel4Splits,
        };
        AssertThat(sun.DirectionalShadowMode).OverrideFailureMessage($"{preset.Level}: shadow mode {sun.DirectionalShadowMode}").IsEqual(mode);
        AssertFloat(sun.DirectionalShadowMaxDistance).OverrideFailureMessage($"{preset.Level}: shadow distance").IsEqualApprox((float)preset.ShadowMaxDistance, 1e-3f);
    }

    private static void AssertCameraMatches(PlayerCamera camera, string when)
    {
        AssertBool(camera.InvertMouseY).OverrideFailureMessage($"invert mouse Y {when}").IsTrue();
        AssertBool(camera.InvertStickY).OverrideFailureMessage($"invert stick Y {when}").IsTrue();
        AssertFloat(camera.MouseSensitivity).OverrideFailureMessage($"mouse sensitivity {when}").IsEqualApprox(PlayerCamera.DefaultMouseSensitivity * 2f, 1e-7f);
        AssertFloat(camera.StickSensitivity).OverrideFailureMessage($"stick sensitivity {when}").IsEqualApprox(PlayerCamera.DefaultStickSensitivity * 0.5f, 1e-6f);
    }

    private void AssertKeepQuestion(SettingsView view, string what = "")
    {
        AssertBool(_main!.Dialog.IsOpen).OverrideFailureMessage($"{what}: no keep-or-revert question").IsTrue();
        AssertThat(_main.Dialog.MessageText).IsEqual(SettingsView.KeepQuestion);
        AssertThat(_main.Dialog.MessageText).IsEqual("Keep these display settings?");
        AssertThat(string.Join("|", _main.Dialog.Buttons.Select(b => b.Text))).IsEqual("Keep|Revert");
        AssertBool(view.RevertPending).IsTrue();
        AssertBool(_main.Dialog.DetailText.StartsWith("Reverting in ", StringComparison.Ordinal)).OverrideFailureMessage($"{what}: countdown '{_main.Dialog.DetailText}'").IsTrue();
    }

    private static void AssertDisplayControlsDisabled(SettingsView view, string what = "")
    {
        foreach (var control in new BaseButton[] { view.WindowModeOption, view.WindowSizeOption, view.QualityOption, view.VSyncCheck, view.FrameCapOption })
        {
            AssertBool(control.Disabled).OverrideFailureMessage($"{what}: {control.Name} enabled while the question shows").IsTrue();
        }

        AssertBool(view.RenderScaleSlider.Editable).OverrideFailureMessage($"{what}: render scale editable while the question shows").IsFalse();
    }

    /// <summary>The action names in the <c>[input]</c> section of <c>project.godot</c>, read from the file.</summary>
    private static List<string> ProjectActions()
    {
        var path = ProjectSettings.GlobalizePath("res://project.godot");
        var actions = new List<string>();
        var inInput = false;
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                inInput = line == "[input]";
                continue;
            }

            if (inInput && ActionLine.Match(line) is { Success: true } match)
            {
                actions.Add(match.Groups[1].Value);
            }
        }

        AssertBool(actions.Count > 0).OverrideFailureMessage($"no actions in {path}").IsTrue();
        return actions;
    }
}
