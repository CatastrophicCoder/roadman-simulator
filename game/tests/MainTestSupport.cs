using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using GdUnit4;
using Godot;
using Roadman.Autoload;
using Roadman.Core.Maps;
using Roadman.Core.Saves;
using Roadman.Core.Settings;
using Roadman.Debug;
using Roadman.OnFoot;
using Roadman.Saves;
using Roadman.Scenes;
using Roadman.Settings;
using static GdUnit4.Assertions;

namespace Roadman.Tests;

/// <summary>
/// Shared helpers for the <c>Main</c> suites of <c>docs/specs/menu-settings-save.md</c> (<see cref="MainMenuTest"/>,
/// <see cref="PauseTest"/>, <see cref="SaveLoadTest"/>, <see cref="SettingsViewTest"/>): a <see cref="Main"/> on a temporary saves folder and settings
/// file (never the real user folder), simulated keyboard and gamepad input, focus navigation, and sample saves.
/// <para>
/// <c>Main</c> starts games on the map in the project setting <c>roadman/map/id</c> (<c>somero</c>), the same pack
/// every other suite runs on (the full pack, or the CI test area from the committed fixture), so positions are
/// derived from the loaded pack as in <see cref="WorldEdgeTest"/>.
/// </para>
/// <para>
/// Every suite calls <see cref="BeginTest"/> before each test and <see cref="TearDown"/> after it. <c>BeginTest</c>
/// makes the temporary folder and records the engine state <c>Main</c> changes when it applies the settings
/// (<see cref="EngineState"/>); <c>TearDown</c> unpauses the tree, frees a <c>Main</c> that is still alive, puts that
/// state back, resets <see cref="GameState.SettingsPath"/> and <see cref="DebugBuild.Override"/>, returns every
/// injected input to rest and deletes the temporary folder (making it writable first), so a failing test can't stall
/// or change the suites after it.
/// </para>
/// </summary>
internal static class MainTestSupport
{
    public const string MainScenePath = "res://scenes/main/Main.tscn";

    /// <summary>The joypad device the injected gamepad events come from; the actions listen to every device (−1).</summary>
    public const int PadDevice = 0;

    /// <summary>
    /// Physics frames allowed for a game to start (loading screen frames, the map load, the terrain-collision wait and
    /// the spawn or placement). A full-pack load takes one long frame, so this bounds only a failing start.
    /// </summary>
    public const int GameStartFrames = 900;

    /// <summary>Physics frames allowed for the player to stand still on the floor after a spawn or placement.</summary>
    public const int SettleFrames = 180;

    /// <summary>The keys the tests inject; <see cref="ReleaseInputs"/> releases each.</summary>
    private static readonly Key[] InjectedKeys =
    [
        Key.Escape, Key.Enter, Key.Space, Key.Up, Key.Down, Key.Left, Key.Right, Key.Quoteleft, Key.F2, Key.G, Key.I, Key.W,
    ];

    private static readonly JoyButton[] InjectedButtons =
    [
        GamepadButtons.South, GamepadButtons.East, GamepadButtons.Start,
        JoyButton.DpadUp, JoyButton.DpadDown, JoyButton.DpadLeft, JoyButton.DpadRight,
    ];

    private static readonly JoyAxis[] InjectedAxes = [JoyAxis.LeftX, JoyAxis.LeftY];

    /// <summary>The audio buses <see cref="SettingsApplier.ApplyAudio"/> sets.</summary>
    private static readonly string[] Buses = [SettingsApplier.MasterBus, SettingsApplier.MusicBus, SettingsApplier.EffectsBus];

    /// <summary>The state recorded by <see cref="BeginTest"/>, put back by <see cref="TearDown"/>.</summary>
    private static EngineState? _savedState;


    private static readonly string[] PressedActions = [Player.MoveForwardAction, Player.JumpAction, World.PauseAction, "ui_cancel", "ui_accept"];

    /// <summary>One way of driving the menus: four directions, accept and back.</summary>
    internal sealed record Nav(string Name, Action Up, Action Down, Action Left, Action Right, Action Accept, Action Cancel)
    {
        public IEnumerable<Action> Directions => [Up, Down, Left, Right];

        public override string ToString() => Name;
    }

    /// <summary>The keyboard alone: arrow keys, Enter, Esc.</summary>
    public static readonly Nav Keyboard = new(
        "keyboard",
        () => TapKey(Key.Up),
        () => TapKey(Key.Down),
        () => TapKey(Key.Left),
        () => TapKey(Key.Right),
        () => TapKey(Key.Enter),
        () => TapKey(Key.Escape));

    /// <summary>The gamepad's d-pad, the south face button and the east face button.</summary>
    public static readonly Nav PadDpad = new(
        "gamepad d-pad",
        () => TapPad(JoyButton.DpadUp),
        () => TapPad(JoyButton.DpadDown),
        () => TapPad(JoyButton.DpadLeft),
        () => TapPad(JoyButton.DpadRight),
        () => TapPad(GamepadButtons.South),
        () => TapPad(GamepadButtons.East));

    /// <remarks>
    /// The left stick (also in <c>ui_up</c>/<c>ui_down</c>/<c>ui_left</c>/<c>ui_right</c>) isn't driven here: inside the
    /// gdUnit4 test process an injected <see cref="InputEventJoypadMotion"/> sets the <c>ui_*</c> action but never moves
    /// GUI focus, not even between two plain Godot buttons, while the same events do move it in a standalone headless
    /// <c>SceneTree</c> script. The d-pad goes through the same <c>ui_*</c> focus navigation.
    /// </remarks>
    public static readonly Nav[] AllNavs = [Keyboard, PadDpad];

    // ===== Main and folders =====

    /// <summary>
    /// The global engine state <c>Main._Ready</c> changes through <see cref="SettingsApplier.ApplyAll"/> and the
    /// settings view through its changes: the root viewport's MSAA, screen-space anti-aliasing and 3D scale,
    /// <see cref="Engine.MaxFps"/>, the three audio buses' volume and mute, and <see cref="GameState.Settings"/>. The
    /// directional shadow atlas and soft shadow filter have no getters; <see cref="Restore"/> sets the project's own.
    /// </summary>
    internal sealed record EngineState(
        int MaxFps,
        Viewport.Msaa Msaa3D,
        Viewport.ScreenSpaceAAEnum ScreenSpaceAA,
        float Scaling3DScale,
        (float Db, bool Mute)[] Buses,
        GameSettings Settings)
    {
        public static EngineState Take()
        {
            var root = ((SceneTree)Engine.GetMainLoop()).Root;
            return new EngineState(
                Engine.MaxFps,
                root.Msaa3D,
                root.ScreenSpaceAA,
                root.Scaling3DScale,
                [.. MainTestSupport.Buses.Select(name =>
                {
                    var index = AudioServer.GetBusIndex(name);
                    return index < 0 ? (0f, false) : (AudioServer.GetBusVolumeDb(index), AudioServer.IsBusMute(index));
                })],
                GameState.Instance?.Settings ?? GameSettings.Default);
        }

        public void Restore()
        {
            Engine.MaxFps = MaxFps;
            if (Engine.GetMainLoop() is SceneTree tree)
            {
                tree.Root.Msaa3D = Msaa3D;
                tree.Root.ScreenSpaceAA = ScreenSpaceAA;
                tree.Root.Scaling3DScale = Scaling3DScale;
            }

            for (var i = 0; i < MainTestSupport.Buses.Length && i < Buses.Length; i++)
            {
                var index = AudioServer.GetBusIndex(MainTestSupport.Buses[i]);
                if (index >= 0)
                {
                    AudioServer.SetBusVolumeDb(index, Buses[i].Db);
                    AudioServer.SetBusMute(index, Buses[i].Mute);
                }
            }

            // The project's own shadow atlas and soft shadow filter (a High or Low preset changed them).
            RenderingServer.DirectionalShadowAtlasSetSize(
                ProjectSettings.GetSetting("rendering/lights_and_shadows/directional_shadow/size", 4096).AsInt32(),
                ProjectSettings.GetSetting(SettingsApplier.DirectionalShadow16BitsSetting, true).AsBool());
            RenderingServer.DirectionalSoftShadowFilterSetQuality(SettingsApplier.SoftShadowQuality(QualityPreset.Medium));
            if (GameState.Instance is { } state)
            {
                state.Settings = Settings;
            }
        }
    }

    /// <summary>
    /// Before each test: records the <see cref="EngineState"/> for <see cref="TearDown"/> and returns a new empty
    /// temporary folder named after <paramref name="what"/>.
    /// </summary>
    public static string BeginTest(string what)
    {
        _savedState = EngineState.Take();
        return NewTempFolder(what);
    }

    /// <summary>A new empty temporary folder; the caller deletes it with <see cref="DeleteFolder"/>.</summary>
    public static string NewTempFolder(string what)
    {
        var folder = Path.Combine(Path.GetTempPath(), $"roadman-{what}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>The saves folder inside a test's temporary folder (created by the first write, as the game does).</summary>
    public static string SavesOf(string folder) => Path.Combine(folder, "saves");

    /// <summary>
    /// A <c>Main</c> with <paramref name="args"/> as its user args, its saves in <paramref name="folder"/>/saves, its
    /// settings at <paramref name="folder"/>/settings.json, quitting only by <see cref="Main.ExitCode"/>.
    /// </summary>
    public static Main NewMain(string folder, params string[] args)
    {
        AssertThat(GameState.Instance).OverrideFailureMessage("no GameState autoload").IsNotNull();
        GameState.Instance.SettingsPath = Path.Combine(folder, "settings.json");
        var main = GD.Load<PackedScene>(MainScenePath).Instantiate<Main>();
        main.UserArgs = args;
        main.SavesFolder = SavesOf(folder);
        main.QuitOnConfirm = false;
        return main;
    }

    /// <summary>See the class summary.</summary>
    public static void TearDown(string folder, params Node?[] nodes)
    {
        ReleaseInputs();
        if (Engine.GetMainLoop() is SceneTree tree)
        {
            tree.Paused = false;
        }

        foreach (var node in nodes)
        {
            if (node is not null && GodotObject.IsInstanceValid(node))
            {
                if (node.GetParent() is { } parent)
                {
                    parent.RemoveChild(node);
                }

                node.Free();
            }
        }

        if (Engine.GetMainLoop() is SceneTree after)
        {
            after.Paused = false;
        }

        _savedState?.Restore();
        _savedState = null;
        DebugBuild.Override = null;
        if (GameState.Instance is { } state)
        {
            state.SettingsPath = null;
        }

        DeleteFolder(folder);
    }

    /// <summary>
    /// Makes <paramref name="folder"/> read-only (no new files can be created in it) or writable again, with Unix file
    /// modes. On Windows a folder's read-only attribute doesn't stop new files, so the read-only cases can't run there:
    /// the test fails with a message saying so (the suites run on macOS and the Linux CI).
    /// </summary>
    public static void SetWritable(string folder, bool writable)
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("a read-only folder needs Unix file modes; this test runs on macOS and Linux only");
        }

        var mode = UnixFileMode.UserRead | UnixFileMode.UserExecute;
        File.SetUnixFileMode(folder, writable ? mode | UnixFileMode.UserWrite : mode);
    }

    /// <summary>Makes every folder under <paramref name="folder"/> writable again, then deletes it.</summary>
    public static void DeleteFolder(string folder)
    {
        if (folder == "" || !Directory.Exists(folder))
        {
            return;
        }

        if (!OperatingSystem.IsWindows())
        {
            foreach (var dir in Directory.EnumerateDirectories(folder, "*", SearchOption.AllDirectories).Prepend(folder))
            {
                SetWritable(dir, writable: true);
            }
        }

        Directory.Delete(folder, recursive: true);
    }

    /// <summary>Waits until <paramref name="main"/> runs a game with a player (state <see cref="MainState.Playing"/>).</summary>
    public static async Task<World> WaitForGame(Main main)
    {
        var started = await WorldEdgeTest.WaitUntil(
            main,
            () => main.State == MainState.Playing && main.World?.Player is not null,
            GameStartFrames);
        AssertBool(started)
            .OverrideFailureMessage($"no game started (state {main.State}, loading error '{main.LoadingScreen.ErrorText}')")
            .IsTrue();
        return main.World!;
    }

    /// <summary>
    /// Waits until the play time has started counting. Right after a map load (one long frame) Godot runs physics
    /// steps in catch-up bursts of several per process frame, and the play time counts process frames, so a test that
    /// waits only physics frames can still see 0 s.
    /// </summary>
    public static async Task WaitForPlayTime(Main main) =>
        AssertBool(await WorldEdgeTest.WaitUntil(main, () => main.PlayTime.Seconds > 0d, 120))
            .OverrideFailureMessage("the play time didn't start").IsTrue();

    /// <summary>Waits until the world's barriers are built (after the spawn).</summary>
    public static async Task WaitForEdge(World world) =>
        AssertBool(await world.MapLoader.WaitForWorldEdgeAsync(WorldEdgeTest.EdgeWaitFrames))
            .OverrideFailureMessage("the barriers weren't built").IsTrue();

    /// <summary>Waits until the player stands still on the floor for five physics frames.</summary>
    public static async Task Settle(World world)
    {
        var player = world.Player!;
        var still = 0;
        await WorldEdgeTest.Drive(world, _ =>
        {
            still = player.IsOnFloor() && player.Velocity.Length() < 1e-3f ? still + 1 : 0;
            return still >= 5;
        }, SettleFrames);
        AssertBool(player.IsOnFloor()).OverrideFailureMessage($"the player didn't settle at {player.GlobalPosition}").IsTrue();
    }

    /// <summary>
    /// Calls <paramref name="onWorld"/> with each <see cref="World"/> <paramref name="main"/> adds, when it enters the
    /// tree (before its <c>_Ready</c>), so a test can connect to its signals before they fire. Returns the
    /// disconnecting action.
    /// </summary>
    public static Action OnEachWorld(Main main, Action<World> onWorld)
    {
        void Handler(Node child)
        {
            if (child is World world)
            {
                onWorld(world);
            }
        }

        main.ChildEnteredTree += Handler;
        return () =>
        {
            if (GodotObject.IsInstanceValid(main))
            {
                main.ChildEnteredTree -= Handler;
            }
        };
    }

    // ===== Input =====

    public static void Inject(InputEvent @event)
    {
        Input.ParseInputEvent(@event);
        Input.FlushBufferedEvents();
    }

    /// <summary>A key event with both the keycode and the physical keycode (the built-in <c>ui_*</c> actions match the keycode, the game's the physical one).</summary>
    public static void KeyEvent(Key key, bool pressed) =>
        Inject(new InputEventKey { Keycode = key, PhysicalKeycode = key, Pressed = pressed });

    public static void TapKey(Key key)
    {
        KeyEvent(key, pressed: true);
        KeyEvent(key, pressed: false);
    }

    public static void PadButton(JoyButton button, bool pressed) =>
        Inject(new InputEventJoypadButton { Device = PadDevice, ButtonIndex = button, Pressed = pressed });

    public static void TapPad(JoyButton button)
    {
        PadButton(button, pressed: true);
        PadButton(button, pressed: false);
    }

    public static void Stick(JoyAxis axis, float value) =>
        Inject(new InputEventJoypadMotion { Device = PadDevice, Axis = axis, AxisValue = value });

    public static void FlickStick(JoyAxis axis, float value)
    {
        Stick(axis, value);
        Stick(axis, 0f);
    }

    /// <summary>Returns every injected key, button, axis and pressed action to rest.</summary>
    public static void ReleaseInputs()
    {
        foreach (var key in InjectedKeys)
        {
            KeyEvent(key, pressed: false);
        }

        foreach (var button in InjectedButtons)
        {
            PadButton(button, pressed: false);
        }

        foreach (var axis in InjectedAxes)
        {
            Stick(axis, 0f);
        }

        foreach (var action in PressedActions)
        {
            Input.ActionRelease(action);
        }
    }

    /// <summary>
    /// Presses at one process frame and releases at the next, as real input arrives: Godot flushes device events
    /// between main-loop iterations, before the physics steps. (An event injected inside a
    /// <see cref="SceneTree.PhysicsFrame"/> handler counts as just pressed in the next physics frame, and that
    /// just-press survives a release in its handler; see <see cref="PlayerOnFootTest"/>. Real input never arrives
    /// there.)
    /// </summary>
    /// <param name="holdPhysicsFrames">
    /// Physics frames the press is held before the release (0: released at the next process frame, which headless,
    /// where process frames outrun physics steps, is usually before the next physics step: a tap shorter than one
    /// physics tick).
    /// </param>
    public static async Task PressThenRelease(Node node, Action press, Action release, int holdPhysicsFrames = 0)
    {
        var tree = node.GetTree();
        await node.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
        press();
        for (var i = 0; i < holdPhysicsFrames; i++)
        {
            await node.ToSignal(tree, SceneTree.SignalName.PhysicsFrame);
        }

        await node.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
        release();
    }

    // ===== Focus =====

    public static Control? FocusOwner(Node node) => node.GetViewport().GuiGetFocusOwner();

    public static string NameOf(Control? control) => control is null ? "(none)" : $"{control.GetParent()?.Name}/{control.Name} '{(control as Button)?.Text}'";

    /// <summary>
    /// Every control reachable from the current focus owner with <paramref name="nav"/>'s four directions alone: a
    /// breadth-first search that, from each control reached so far, presses each direction once (the focus is put
    /// back on that control with <see cref="Control.GrabFocus"/> between presses, which only revisits a control the
    /// events already reached). The focus ends where it started.
    /// </summary>
    public static HashSet<Control> Reachable(Node node, Nav nav)
    {
        var start = FocusOwner(node);
        AssertThat(start).OverrideFailureMessage($"{nav}: nothing has the focus").IsNotNull();
        var seen = new HashSet<Control> { start! };
        var queue = new Queue<Control>(seen);
        while (queue.Count > 0)
        {
            var from = queue.Dequeue();
            foreach (var direction in nav.Directions)
            {
                from.GrabFocus();
                direction();
                if (FocusOwner(node) is { } to && seen.Add(to))
                {
                    queue.Enqueue(to);
                }
            }
        }

        start!.GrabFocus();
        return seen;
    }

    /// <summary>
    /// Asserts that the focus starts on <paramref name="first"/>, and that with <paramref name="nav"/> alone exactly
    /// the enabled controls of <paramref name="controls"/> are reachable (nothing disabled, nothing else).
    /// </summary>
    public static void AssertFocusAndReach(Node node, Nav nav, string view, Control first, IEnumerable<Control> controls)
    {
        var owner = FocusOwner(node);
        AssertThat(owner).OverrideFailureMessage($"{view}, {nav}: the focus starts on {NameOf(owner)}, not {NameOf(first)}").IsSame(first);
        var enabled = controls.Where(c => c.IsVisibleInTree() && !(c is BaseButton { Disabled: true })).ToHashSet();
        var reached = Reachable(node, nav);
        var missing = enabled.Except(reached).Select(NameOf).ToList();
        var extra = reached.Except(enabled).Select(NameOf).ToList();
        GD.Print($"MainTests: {view}, {nav}: reached {reached.Count} of {enabled.Count} enabled controls");
        AssertThat(string.Join(", ", missing)).OverrideFailureMessage($"{view}, {nav}: not reachable: {string.Join(", ", missing)}").IsEqual("");
        AssertThat(string.Join(", ", extra)).OverrideFailureMessage($"{view}, {nav}: reachable but not an enabled control of the view: {string.Join(", ", extra)}").IsEqual("");
    }

    // ===== Saves =====

    /// <summary>A valid save of <paramref name="mapId"/> with the given player part.</summary>
    public static SaveGame SampleSave(
        string mapId = "somero",
        LocalPoint? position = null,
        double? facingYaw = null,
        int money = 10,
        IEnumerable<string>? gear = null,
        int wantedLevel = 0,
        DateTimeOffset? savedAt = null,
        double playSeconds = 600) =>
        new(
            SaveFormat.CurrentVersion,
            "dev",
            savedAt ?? DateTimeOffset.UtcNow,
            playSeconds,
            mapId,
            "2026-01-01T00:00:00Z",
            new SavePlayer(position, position is null ? null : facingYaw ?? 0d, money, gear ?? [], wantedLevel),
            SaveQuests.Empty);

    /// <summary>Writes <paramref name="save"/> to <paramref name="slot"/> in the test's saves folder, as the game does.</summary>
    public static void WriteSave(string folder, SlotId slot, SaveGame save) =>
        new SaveSlots(new FileSaveStore(SavesOf(folder))).Write(slot, save);

    /// <summary>The slot as the game reads it.</summary>
    public static SlotInfo ReadSlot(string folder, SlotId slot) =>
        new SaveSlots(new FileSaveStore(SavesOf(folder))).Get(slot);

    public static string SlotPath(string folder, SlotId slot) => Path.Combine(SavesOf(folder), slot.FileName);

    /// <summary>Presses <paramref name="button"/> as a click does (its <c>pressed</c> signal).</summary>
    public static void Click(BaseButton button)
    {
        AssertBool(button.Disabled).OverrideFailureMessage($"{NameOf(button)} is disabled").IsFalse();
        button.EmitSignal(BaseButton.SignalName.Pressed);
    }

    /// <summary>Presses the open dialog's button with <paramref name="text"/>.</summary>
    public static void ClickDialog(Main main, string text)
    {
        AssertBool(main.Dialog.IsOpen).OverrideFailureMessage($"no dialog is open to press '{text}'").IsTrue();
        var button = main.Dialog.ButtonByText(text);
        AssertThat(button)
            .OverrideFailureMessage($"the dialog '{main.Dialog.MessageText}' has no '{text}' button (has {string.Join(", ", main.Dialog.Buttons.Select(b => b.Text))})")
            .IsNotNull();
        Click(button!);
    }

    public static string Invariant(FormattableString text) => FormattableString.Invariant(text);
}
