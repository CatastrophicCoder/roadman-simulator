using System;
using System.Linq;
using System.Threading.Tasks;
using GdUnit4;
using Godot;
using Roadman.Core.Edge;
using Roadman.OnFoot;
using Roadman.Scenes;
using Roadman.Ui;
using static GdUnit4.Assertions;
using static Roadman.Tests.MainTestSupport;

namespace Roadman.Tests;

/// <summary>
/// The pause menu over a running game, Part A of <c>docs/specs/menu-settings-save.md</c> (<c>Ac</c> numbers are its
/// acceptance criteria): pausing and what stops (11), resuming and the closing press (11), the east face button (11),
/// pausing during the death sequence (12), window focus loss in a game (13), and keyboard-only and gamepad-only use of
/// the pause menu and its dialogs (22). Each test starts a game with <c>--new-game</c> on the pack every suite uses and
/// puts the player in the void as <see cref="WorldEdgeTest"/> does. Frames are counted with
/// <see cref="SceneTree.PhysicsFrame"/>, which Godot emits while the tree is paused too.
/// </summary>
[TestSuite]
[RequireGodotRuntime]
public class PauseTest
{
    /// <summary>Criterion 11: the frames over which nothing may change while paused.</summary>
    private const int PausedFrames = 60;

    /// <summary>Criterion 11: frames watched after a resume for a jump or a camera move.</summary>
    private const int AfterResumeFrames = 30;

    /// <summary>Criterion 11: physics frames a closing press is held before its release (a short, ordinary press).</summary>
    private const int HoldFrames = 4;

    /// <summary>Criterion 11: the largest rise of a standing player's feet that isn't a jump, m (floor snapping noise).</summary>
    private const float StandingRise = 0.01f;

    /// <summary>Criterion 12: a shortened fall, so the test reaches the death screen sooner (the screen keeps its defaults).</summary>
    private const float ShortFallTime = 1f;

    private string _folder = "";
    private Main? _main;
    private Node? _testBody;

    [BeforeTest]
    public void SetUp() => _folder = NewTempFolder("pause");

    [AfterTest]
    public void CleanUp()
    {
        TearDown(_folder, _testBody, _main);
        _main = null;
        _testBody = null;
        _folder = "";
    }

    // ----- Criterion 11: pausing stops the world -----

    /// <summary>
    /// Esc (the <c>pause</c> action) in a game pauses the tree, shows the pause menu with its five buttons (Resume
    /// focused, Save enabled while alive, Settings disabled until Part B) and releases the mouse while the player
    /// camera keeps wanting the capture. Over 60 paused frames, with W held and the debug keys tapped, the player's
    /// position, a falling rigid body and the play time don't change (the death sequence's time in state, which
    /// <c>World</c> doesn't advance while alive, is checked while paused in <c>Falling</c> and on the death screen by
    /// criterion 12's test), and no
    /// debug view or the debug camera switches on. Esc again resumes, the camera wants the capture, and the body and
    /// play time move on.
    /// </summary>
    [TestCase]
    public async Task Ac11_PauseKey_PausesShowsFiveButtonsReleasesTheMouse_AndFor60FramesNothingInTheWorldChangesAndDebugKeysDoNothing()
    {
        WorldEdgeTest.AssertPackPresent();
        using var runner = ISceneRunner.Load(_main = NewMain(_folder, "--new-game"), true);
        var world = await WaitForGame(_main);
        await WaitForEdge(world);
        await Settle(world);
        var player = world.Player!;
        var camera = world.PlayerCamera!;
        AssertBool(camera.WantsCapture).OverrideFailureMessage("the player camera doesn't want the capture after the spawn").IsTrue();

        var body = new RigidBody3D { Name = "PauseTestBody", CollisionLayer = CollisionLayers.Barrier, CollisionMask = CollisionLayers.Map };
        body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(0.4f, 0.4f, 0.4f) } });
        world.AddChild(body);
        _testBody = body;
        body.GlobalPosition = player.GlobalPosition + new Vector3(0f, 40f, 0f);
        await WorldEdgeTest.PhysicsFrames(world, 5);
        await WaitForPlayTime(_main);
        AssertFloat(body.LinearVelocity.Y).OverrideFailureMessage("the test body isn't falling").IsLess(0f);

        TapKey(Key.Escape);
        AssertThat(_main.State).IsEqual(MainState.Paused);
        AssertBool(_main.GetTree().Paused).IsTrue();
        var menu = _main.PauseMenu;
        AssertBool(menu.IsVisibleInTree()).IsTrue();
        AssertThat(string.Join("|", menu.Buttons.Select(b => b.Text))).IsEqual("Resume|Save|Load|Settings|Quit to menu");
        AssertBool(menu.SaveButton.Disabled).OverrideFailureMessage("Save is disabled while alive").IsFalse();
        AssertBool(menu.SaveHint.Visible).IsFalse();
        AssertBool(menu.SettingsButton.Disabled).IsTrue();
        AssertThat(menu.SettingsButton.FocusMode).IsEqual(Control.FocusModeEnum.None);
        AssertThat(FocusOwner(_main)).IsSame(menu.ResumeButton);
        AssertThat(Input.MouseMode).IsEqual(Input.MouseModeEnum.Visible);
        AssertBool(camera.MouseCaptured).IsFalse();
        AssertBool(camera.WantsCapture).OverrideFailureMessage("pausing forgot that the camera wanted the capture").IsTrue();

        var playerAt = player.GlobalPosition;
        var bodyAt = body.GlobalPosition;
        var timeInState = world.Death.TimeInState;
        var playTime = _main.PlayTime.Seconds;
        var yaw = camera.Yaw;
        var maxPlayerMove = 0f;
        var maxBodyMove = 0f;
        var maxTimeChange = 0d;
        var maxPlayChange = 0d;
        var debugOn = "";
        Input.ActionPress(Player.MoveForwardAction);
        try
        {
            await WorldEdgeTest.Drive(_main, frame =>
            {
                if (frame % 10 == 0)
                {
                    TapKey(Key.Quoteleft);
                    TapKey(Key.F2);
                    TapKey(Key.G);
                    TapKey(Key.I);
                }

                maxPlayerMove = Mathf.Max(maxPlayerMove, (player.GlobalPosition - playerAt).Length());
                maxBodyMove = Mathf.Max(maxBodyMove, (body.GlobalPosition - bodyAt).Length());
                maxTimeChange = Math.Max(maxTimeChange, Math.Abs(world.Death.TimeInState - timeInState));
                maxPlayChange = Math.Max(maxPlayChange, Math.Abs(_main.PlayTime.Seconds - playTime));
                if (world.DebugCameraActive || world.Orthophoto?.Enabled == true || world.RoadGraphView?.Enabled == true || world.Inspector?.Enabled == true)
                {
                    debugOn = $"frame {frame}: debug camera {world.DebugCameraActive}, orthophoto {world.Orthophoto?.Enabled}, road graph {world.RoadGraphView?.Enabled}, inspector {world.Inspector?.Enabled}";
                }

                return false;
            }, PausedFrames);
        }
        finally
        {
            Input.ActionRelease(Player.MoveForwardAction);
        }

        GD.Print(Invariant($"PauseTest: Ac11 over {PausedFrames} paused frames: player {maxPlayerMove} m, body {maxBodyMove} m, time in state {maxTimeChange} s, play time {maxPlayChange} s"));
        AssertFloat(maxPlayerMove).IsEqual(0f);
        AssertFloat(maxBodyMove).IsEqual(0f);
        AssertFloat(maxTimeChange).IsEqual(0d);
        AssertFloat(maxPlayChange).IsEqual(0d);
        AssertFloat(camera.Yaw).IsEqual(yaw);
        AssertThat(debugOn).OverrideFailureMessage($"a debug key acted while paused: {debugOn}").IsEqual("");
        AssertThat(_main.State).IsEqual(MainState.Paused);

        TapKey(Key.Escape);
        AssertThat(_main.State).IsEqual(MainState.Playing);
        AssertBool(_main.GetTree().Paused).IsFalse();
        AssertBool(menu.Visible).IsFalse();
        AssertBool(camera.WantsCapture).IsTrue();
        await WorldEdgeTest.PhysicsFrames(world, 10);
        AssertFloat((body.GlobalPosition - bodyAt).Length()).OverrideFailureMessage("the body didn't move on after the resume").IsGreater(0f);
        AssertFloat(_main.PlayTime.Seconds).IsGreater(playTime);
        AssertBool(world.DebugCameraActive).IsFalse();
    }

    // ----- Criterion 11: resuming, the closing press, the east face button; criterion 13 in a game -----

    /// <summary>
    /// Each way of resuming, each an ordinary press held over a few paused physics frames: the Resume button with Space
    /// (also <c>jump</c>); the <c>pause</c> action (the Start button); and last back (the east face button). (The Resume
    /// button with the gamepad's south face button is in the criterion 22 gamepad test; a Space tap shorter than one
    /// physics tick in <see cref="Ac11_SpaceTappedOnResumeWithinOnePhysicsTick_DoesNotJumpThePlayer"/>.) After each
    /// the tree runs, the camera wants the capture again, and over 30 frames the standing player doesn't jump (vertical
    /// speed, feet height) and the camera's yaw and pitch don't move. The Start button pauses too; the east face button in a game neither pauses nor releases the mouse.
    /// Window focus loss in the game opens the pause menu, regaining focus doesn't resume, and a second focus loss
    /// while paused changes nothing (criterion 13).
    /// </summary>
    [TestCase]
    public async Task Ac11_Ac13_ResumeByButtonPauseAndBack_TheClosingPressNeitherJumpsNorMovesTheCamera_EastButtonDoesNotPause_FocusLossPauses()
    {
        WorldEdgeTest.AssertPackPresent();
        using var runner = ISceneRunner.Load(_main = NewMain(_folder, "--new-game"), true);
        var world = await WaitForGame(_main);
        await Settle(world);
        var camera = world.PlayerCamera!;
        AssertBool(camera.WantsCapture).IsTrue();

        // The east face button in the game: no pause, the capture still wanted.
        TapPad(GamepadButtons.East);
        await WorldEdgeTest.PhysicsFrames(world, 2);
        AssertThat(_main.State).IsEqual(MainState.Playing);
        AssertBool(_main.GetTree().Paused).IsFalse();
        AssertBool(camera.WantsCapture).OverrideFailureMessage("the east face button released the capture").IsTrue();

        // Resume button + Space.
        TapPad(GamepadButtons.Start);
        AssertThat(_main.State).OverrideFailureMessage("the Start button didn't pause").IsEqual(MainState.Paused);
        await AssertResumeByHeldPress(world, "Space on Resume", () => KeyEvent(Key.Space, true), () => KeyEvent(Key.Space, false));

        // The pause action (the Start button) closes the menu.
        TapKey(Key.Escape);
        AssertThat(_main.State).IsEqual(MainState.Paused);
        await AssertResumeByHeldPress(world, "Start button", () => PadButton(GamepadButtons.Start, true), () => PadButton(GamepadButtons.Start, false));

        // Criterion 13: focus loss opens the pause menu; focus back doesn't resume; a second loss changes nothing.
        _main.GetTree().Root.PropagateNotification((int)Node.NotificationWMWindowFocusOut);
        AssertThat(_main.State).OverrideFailureMessage("focus loss didn't pause").IsEqual(MainState.Paused);
        AssertBool(_main.GetTree().Paused).IsTrue();
        AssertBool(_main.PauseMenu.IsVisibleInTree()).IsTrue();
        _main.GetTree().Root.PropagateNotification((int)Node.NotificationWMWindowFocusIn);
        await WorldEdgeTest.PhysicsFrames(world, 3);
        AssertThat(_main.State).OverrideFailureMessage("regaining focus resumed").IsEqual(MainState.Paused);
        AssertBool(_main.GetTree().Paused).IsTrue();
        _main.GetTree().Root.PropagateNotification((int)Node.NotificationWMWindowFocusOut);
        AssertThat(_main.State).IsEqual(MainState.Paused);
        AssertBool(_main.PauseMenu.IsVisibleInTree()).IsTrue();
        _main.GetTree().Root.PropagateNotification((int)Node.NotificationWMWindowFocusIn);
        Click(_main.PauseMenu.ResumeButton);
        AssertThat(_main.State).IsEqual(MainState.Playing);
        AssertBool(camera.WantsCapture).IsTrue();

        // Last, back (the east face button) closes the menu.
        await WorldEdgeTest.PhysicsFrames(world, 2);
        TapPad(GamepadButtons.Start);
        AssertThat(_main.State).IsEqual(MainState.Paused);
        await AssertResumeByHeldPress(world, "east face button", () => PadButton(GamepadButtons.East, true), () => PadButton(GamepadButtons.East, false));
    }

    /// <summary>
    /// Space (<c>ui_accept</c> and <c>jump</c>) tapped on the focused Resume button, pressed and released between the
    /// same two physics steps (a tap shorter than one physics tick; headless, process frames outrun physics steps, so
    /// a press at one process frame and a release at the next usually are): the closing press must not jump the
    /// standing player.
    /// </summary>
    [TestCase]
    public async Task Ac11_SpaceTappedOnResumeWithinOnePhysicsTick_DoesNotJumpThePlayer()
    {
        WorldEdgeTest.AssertPackPresent();
        using var runner = ISceneRunner.Load(_main = NewMain(_folder, "--new-game"), true);
        var world = await WaitForGame(_main);
        await Settle(world);
        TapKey(Key.Escape);
        AssertThat(_main.State).IsEqual(MainState.Paused);
        await AssertResumeByHeldPress(world, "Space tapped on Resume", () => KeyEvent(Key.Space, true), () => KeyEvent(Key.Space, false), holdPhysicsFrames: 0);
    }

    /// <summary>
    /// Presses between two frames, holds for <paramref name="holdPhysicsFrames"/> paused physics frames and releases
    /// (the press or the release resumes, through the menu's back or the Resume button), then checks the resume and
    /// watches the player and camera for <see cref="AfterResumeFrames"/> frames.
    /// </summary>
    private async Task AssertResumeByHeldPress(World world, string what, Action press, Action release, int holdPhysicsFrames = HoldFrames)
    {
        var main = _main!;
        var player = world.Player!;
        var camera = world.PlayerCamera!;
        AssertThat(FocusOwner(main)).IsSame(main.PauseMenu.ResumeButton);
        var feetY = player.GlobalPosition.Y;
        var yaw = camera.Yaw;
        var pitch = camera.Pitch;
        var physicsAtPress = Engine.GetPhysicsFrames();
        await PressThenRelease(main, press, release, holdPhysicsFrames);
        GD.Print($"PauseTest: {what}: held over {Engine.GetPhysicsFrames() - physicsAtPress} physics step(s)");
        AssertThat(main.State).OverrideFailureMessage($"{what}: didn't resume").IsEqual(MainState.Playing);
        AssertBool(main.GetTree().Paused).IsFalse();
        AssertBool(main.PauseMenu.Visible).IsFalse();
        // Nothing keeps the GUI focus in the game, so ui_accept (Space, the south face button) can't press a hidden button.
        AssertThat(FocusOwner(main)).OverrideFailureMessage($"{what}: a control kept the focus after the resume").IsNull();
        AssertBool(camera.WantsCapture).OverrideFailureMessage($"{what}: the camera doesn't want the capture after the resume").IsTrue();

        var maxUp = 0f;
        var maxRise = 0f;
        await WorldEdgeTest.Drive(world, _ =>
        {
            maxUp = Mathf.Max(maxUp, player.Velocity.Y);
            maxRise = Mathf.Max(maxRise, player.GlobalPosition.Y - feetY);
            return false;
        }, AfterResumeFrames);
        GD.Print(Invariant($"PauseTest: Ac11 resume by {what}: max vertical speed {maxUp} m/s, max rise {maxRise} m"));
        AssertFloat(maxUp).OverrideFailureMessage($"{what}: the player jumped (vertical speed {maxUp} m/s)").IsLess(0.5f);
        AssertFloat(maxRise).OverrideFailureMessage($"{what}: the player rose {maxRise} m").IsLessEqual(StandingRise);
        AssertFloat(camera.Yaw).OverrideFailureMessage($"{what}: the camera turned").IsEqual(yaw);
        AssertFloat(camera.Pitch).OverrideFailureMessage($"{what}: the camera pitched").IsEqual(pitch);
    }

    // ----- Criterion 12: pausing during the death sequence -----

    /// <summary>
    /// <c>pause</c> during <c>Falling</c>, on the death screen before <c>MinScreenTime</c> and after it: each opens the
    /// pause menu without dismissing the screen, Save is disabled with its hint, the sequence's time in state doesn't
    /// change while paused and goes on from there after Resume; the closing press (Esc, Start, and Enter on Resume)
    /// doesn't dismiss the screen. Then another key after <c>MinScreenTime</c> still dismisses it.
    /// </summary>
    [TestCase]
    public async Task Ac12_PauseDuringFallingAndOnTheDeathScreen_DoesNotDismiss_SaveDisabledWithHint_SequenceContinuesAfterResume()
    {
        WorldEdgeTest.AssertPackPresent();
        using var runner = ISceneRunner.Load(_main = NewMain(_folder, "--new-game"), true);
        var world = await WaitForGame(_main);
        await WaitForEdge(world);
        world.FallTime = ShortFallTime;
        var entry = WorldEdgeTest.FindLedgeEntry(world);
        WorldEdgeTest.AssertEntered(await WorldEdgeTest.PlaceOnLedgeAndWaitForEntry(world, entry));
        AssertThat(world.Death.State).IsEqual(DeathState.Falling);
        await WorldEdgeTest.PhysicsFrames(world, 10);

        // Falling.
        await AssertPausedFrozen(world, DeathState.Falling, "Falling", pause: () => TapKey(Key.Escape));
        TapKey(Key.Escape);
        await AssertContinues(world, DeathState.Falling, "Falling");

        // The death screen, before MinScreenTime.
        AssertBool(await WorldEdgeTest.WaitUntil(world, () => world.Death.State == DeathState.DeathScreen, WorldEdgeTest.FramesFor(ShortFallTime, Engine.PhysicsTicksPerSecond) + 10))
            .OverrideFailureMessage("the death screen didn't come").IsTrue();
        await WorldEdgeTest.PhysicsFrames(world, 5);
        AssertFloat(world.Death.TimeInState).IsLess(world.MinScreenTime);
        await AssertPausedFrozen(world, DeathState.DeathScreen, "the death screen before MinScreenTime", pause: () => TapKey(Key.Escape));
        TapKey(Key.Escape);
        await AssertContinues(world, DeathState.DeathScreen, "the death screen before MinScreenTime");

        // The death screen, after MinScreenTime: pause with Start, resume with Enter on Resume (a key that dismisses
        // the screen when the game runs), pressed in one paused frame and released in the next.
        AssertBool(await WorldEdgeTest.WaitUntil(world, () => world.Death.TimeInState >= world.MinScreenTime + 0.1, WorldEdgeTest.FramesFor(world.MinScreenTime + 0.2, Engine.PhysicsTicksPerSecond)))
            .IsTrue();
        AssertThat(world.Death.State).IsEqual(DeathState.DeathScreen);
        await AssertPausedFrozen(world, DeathState.DeathScreen, "the death screen after MinScreenTime", pause: () => TapPad(GamepadButtons.Start));
        AssertThat(FocusOwner(_main)).IsSame(_main.PauseMenu.ResumeButton);
        await PressThenRelease(_main, () => KeyEvent(Key.Enter, true), () => KeyEvent(Key.Enter, false));
        await AssertContinues(world, DeathState.DeathScreen, "the death screen after MinScreenTime (Enter on Resume)");

        // And Esc after MinScreenTime: pauses and resumes without dismissing.
        TapKey(Key.Escape);
        AssertThat(_main.State).IsEqual(MainState.Paused);
        AssertThat(world.Death.State).IsEqual(DeathState.DeathScreen);
        _pausedTime = world.Death.TimeInState;
        await WorldEdgeTest.PhysicsFrames(_main, 5);
        AssertFloat(world.Death.TimeInState).IsEqual(_pausedTime);
        await PressThenRelease(_main, () => KeyEvent(Key.Escape, true), () => KeyEvent(Key.Escape, false));
        await AssertContinues(world, DeathState.DeathScreen, "the death screen after MinScreenTime (Esc)");

        // Another key still dismisses it.
        TapKey(Key.Enter);
        AssertBool(await WorldEdgeTest.WaitUntil(world, () => world.Death.State == DeathState.Alive, 10))
            .OverrideFailureMessage($"another key didn't dismiss the death screen (state {world.Death.State})").IsTrue();
        AssertThat(_main.State).IsEqual(MainState.Playing);
    }

    /// <summary>
    /// Pauses with <paramref name="pause"/> in <paramref name="state"/> and checks: paused, the state kept, Save
    /// disabled with its hint, and the time in state, the player and the death camera unchanged over 30 frames.
    /// </summary>
    private async Task AssertPausedFrozen(World world, DeathState state, string when, Action pause)
    {
        var main = _main!;
        pause();
        AssertThat(main.State).OverrideFailureMessage($"{when}: pause didn't open the pause menu").IsEqual(MainState.Paused);
        AssertBool(main.PauseMenu.IsVisibleInTree()).IsTrue();
        AssertThat(world.Death.State).OverrideFailureMessage($"{when}: the pause press changed the death state").IsEqual(state);
        AssertBool(main.PauseMenu.SaveButton.Disabled).OverrideFailureMessage($"{when}: Save is enabled").IsTrue();
        AssertThat(main.PauseMenu.SaveButton.FocusMode).IsEqual(Control.FocusModeEnum.None);
        AssertBool(main.PauseMenu.SaveHint.IsVisibleInTree()).OverrideFailureMessage($"{when}: no save hint").IsTrue();
        AssertThat(main.PauseMenu.SaveHint.Text).IsEqual("You can save when you're not falling off the world");
        AssertBool(world.CanSave).IsFalse();
        AssertThat(main.MakeSave(autosave: false)).IsNull();
        var time = world.Death.TimeInState;
        var at = world.Player!.GlobalPosition;
        var cameraAt = world.DeathCamera.GlobalTransform;
        var opacity = world.DeathScreen.Opacity;
        await WorldEdgeTest.PhysicsFrames(main, 30);
        AssertThat(world.Death.State).IsEqual(state);
        AssertFloat(world.Death.TimeInState).OverrideFailureMessage($"{when}: the sequence ran while paused").IsEqual(time);
        AssertThat(world.Player!.GlobalPosition).IsEqual(at);
        AssertThat(world.DeathCamera.GlobalTransform).IsEqual(cameraAt);
        AssertFloat(world.DeathScreen.Opacity).IsEqual(opacity);
        _pausedTime = time;
    }

    private double _pausedTime;

    /// <summary>After a resume: the tree runs, the state is still <paramref name="state"/> and its time goes on from where it paused.</summary>
    private async Task AssertContinues(World world, DeathState state, string when)
    {
        var main = _main!;
        AssertThat(main.State).OverrideFailureMessage($"{when}: didn't resume").IsEqual(MainState.Playing);
        AssertBool(main.GetTree().Paused).IsFalse();
        await WorldEdgeTest.PhysicsFrames(world, 3);
        AssertThat(world.Death.State).OverrideFailureMessage($"{when}: the closing press changed the death state").IsEqual(state);
        var dt = 1d / Engine.PhysicsTicksPerSecond;
        AssertFloat(world.Death.TimeInState).OverrideFailureMessage($"{when}: the time in state didn't go on from {_pausedTime}")
            .IsBetween(_pausedTime + dt, _pausedTime + (4 * dt) + 1e-6);
    }

    // ----- Criterion 22: the pause menu with the keyboard or gamepad alone -----

    /// <summary>
    /// The pause menu (alive, with one valid save so Load is enabled): focus on Resume, every enabled button reachable
    /// (Settings never); Save opens the slot list in save mode, where every manual row, Back and no autosave row are
    /// reachable; Quit to menu's dialog with its Quit and Cancel reachable, back cancels; then back resumes. The
    /// keyboard alone (Esc, arrows, Enter).
    /// </summary>
    [TestCase]
    public async Task Ac22_PauseMenuSlotListAndDialog_KeyboardAlone_FocusFirstEnabledReachEveryEnabledControlAcceptAndBack() =>
        await AssertPauseMenuSlotListAndDialog(Keyboard);

    /// <summary>As the keyboard case, with the Start button, the d-pad, the south face button and the east face button.</summary>
    [TestCase]
    public async Task Ac22_PauseMenuSlotListAndDialog_GamepadAlone_FocusFirstEnabledReachEveryEnabledControlAcceptAndBack() =>
        await AssertPauseMenuSlotListAndDialog(PadDpad);

    private async Task AssertPauseMenuSlotListAndDialog(Nav nav)
    {
        WorldEdgeTest.AssertPackPresent();
        WriteSave(_folder, Core.Saves.SlotId.Manual(5), SampleSave(position: new Core.Maps.LocalPoint(1, 2, 3)));
        using var runner = ISceneRunner.Load(_main = NewMain(_folder, "--new-game"), true);
        var world = await WaitForGame(_main);
        await Settle(world);
        var menu = _main.PauseMenu;
        var list = _main.SlotList;
        for (var round = 0; round < 2; round++)
        {
            // Twice, so the second round starts from a resumed game. Pause with the same device: Esc on the keyboard, the Start button on the gamepad.
            if (nav == Keyboard)
            {
                TapKey(Key.Escape);
            }
            else
            {
                TapPad(GamepadButtons.Start);
            }

            AssertThat(_main.State).IsEqual(MainState.Paused);
            AssertBool(menu.LoadButton.Disabled).IsFalse();
            AssertFocusAndReach(_main, nav, "pause menu", menu.ResumeButton, menu.Buttons);

            // Save → slot list (save mode).
            menu.ResumeButton.GrabFocus();
            nav.Down();
            AssertThat(FocusOwner(_main)).IsSame(menu.SaveButton);
            nav.Accept();
            AssertBool(list.IsVisibleInTree()).OverrideFailureMessage($"{nav}: accept on Save didn't open the slot list").IsTrue();
            AssertThat(list.Mode).IsEqual(SlotListMode.Save);
            var autosave = list.RowOf(Core.Saves.SlotId.Autosave);
            AssertBool(autosave.Button.Disabled).IsTrue();
            var controls = list.Rows.SelectMany(r => new Control[] { r.Button, r.Delete }).Append(list.BackButton).ToList();
            AssertFocusAndReach(_main, nav, "slot list (save)", list.RowOf(Core.Saves.SlotId.Manual(1)).Button, controls);
            nav.Cancel();
            AssertBool(list.Visible).IsFalse();
            AssertBool(menu.IsVisibleInTree()).IsTrue();
            AssertThat(_main.State).IsEqual(MainState.Paused);

            // Quit to menu → dialog; back cancels to the pause menu.
            menu.QuitToMenuButton.GrabFocus();
            nav.Accept();
            AssertBool(_main.Dialog.IsOpen).IsTrue();
            AssertThat(_main.Dialog.MessageText).IsEqual("Quit to the main menu?");
            AssertFocusAndReach(_main, nav, "quit-to-menu dialog", _main.Dialog.ButtonByText("Quit")!, _main.Dialog.Buttons);
            nav.Cancel();
            AssertBool(_main.Dialog.IsOpen).IsFalse();
            AssertThat(_main.State).IsEqual(MainState.Paused);
            AssertBool(menu.IsVisibleInTree()).IsTrue();

            // Back resumes.
            nav.Cancel();
            AssertThat(_main.State).OverrideFailureMessage($"{nav}: back on the pause menu didn't resume").IsEqual(MainState.Playing);
            await WorldEdgeTest.PhysicsFrames(world, 2);
        }
    }
}
