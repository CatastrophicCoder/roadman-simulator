using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Roadman.Autoload;
using Roadman.Core.Edge;
using Roadman.Core.Items;
using Roadman.Core.Maps;
using Roadman.Core.Saves;
using Roadman.Core.Settings;
using Roadman.Core.Tools;
using Roadman.Saves;
using Roadman.Settings;
using Roadman.Ui;

namespace Roadman.Scenes;

/// <summary>What <see cref="Main"/> shows.</summary>
public enum MainState
{
    /// <summary>Before <see cref="Main._Ready"/>.</summary>
    Starting,

    /// <summary>A <c>--screenshot</c> or <c>--perf</c> run: <c>World</c> only, no menus.</summary>
    DirectStart,

    /// <summary>The main menu (or its slot list or a dialog over it).</summary>
    MainMenu,

    /// <summary>The loading screen while <c>World</c> loads.</summary>
    Loading,

    /// <summary>The loading screen with the error and "Back to main menu".</summary>
    LoadFailed,

    /// <summary>A game is running.</summary>
    Playing,

    /// <summary>The pause menu (or its slot list or a dialog) over the paused game.</summary>
    Paused,

    /// <summary>
    /// The inventory screen opened from the game (<c>docs/specs/inventory.md</c>), over the paused game. Opened from
    /// the pause menu, the state stays <see cref="Paused"/>.
    /// </summary>
    Inventory,
}

/// <summary>
/// The main scene (<c>docs/specs/menu-settings-save.md</c>, "Startup and scenes"): owns the menus, the loading screen
/// and the saves, and instances <c>World.tscn</c> as a child while a game runs. The node tree is built in code.
/// <para>
/// Startup: with <c>--screenshot</c> or <c>--perf</c> in the user args it only adds <c>World</c>, which behaves as
/// before (no menus, no settings, no pause, the window closes as before); with <c>--menu</c> as well it builds its UI
/// hidden, for the screenshot tool to show (<see cref="ShowMenuForScreenshot"/>), and still handles no input. Otherwise
/// it reads the settings (<see cref="GameState.LoadSettings"/>), applies them all (<see cref="SettingsApplier.ApplyAll"/>
/// through <see cref="DisplayBackend"/>) before the menu shows, and shows the main menu, or with
/// <c>--new-game</c> starts a new game at once. <c>--seed &lt;n&gt;</c> in the same user args (<see cref="UserArgs"/> or
/// the command line's) is parsed here and set as <see cref="World.Seed"/> on every <c>World</c> it creates, so a new game
/// and a loaded save that needs the spawn draw (an empty position, or a blocked one) draw as a direct <c>World</c> run
/// with that seed does; without it each game is seeded from the time.
/// </para>
/// <para>
/// Input: <c>World</c> is a child, so its <c>_Input</c> runs before <see cref="_Input"/>; it ignores <c>pause</c>.
/// Here <c>pause</c> opens the pause menu in a game; <c>pause</c> or <c>ui_cancel</c> closes a dialog, goes back from
/// the slot list, or resumes from the pause menu's top level, and is marked handled. Pausing sets
/// <see cref="SceneTree.Paused"/>; <c>Main</c> and its UI run always, <c>World</c> is pausable. Window focus loss in a
/// game opens the pause menu, and a window close request asks first (<see cref="RequestQuitGame"/>).
/// <c>inventory</c> (Tab, the Back button) in a game opens the inventory screen (<see cref="OpenInventory"/>); while it
/// shows, <c>inventory</c>, <c>pause</c> and <c>ui_cancel</c> are back (<see cref="CloseInventory"/>, or a dialog over it
/// first), and the event is marked handled, so Tab never moves the focus there. Elsewhere Tab keeps
/// <c>ui_focus_next</c>.
/// </para>
/// <para>
/// <c>--give &lt;id,...&gt;</c>: in a game started here (not a direct start) this node is the one owner of the argument.
/// It parses it once (<see cref="World.ResolveGiveArg"/>) and hands the ids to the first <c>World</c> it creates
/// (<see cref="World.GiveItems"/>, which applies them after its first placement, never on a respawn). The list is
/// cleared only once a world places its player (<see cref="OnPlayerPlaced"/>), so a start that fails
/// (<c>StartFailed</c>) passes it on to the next world; worlds after the first placed player get an empty list. In a
/// direct start the world reads its own command line instead.
/// </para>
/// <para>
/// Loading a save cleans its player part first (<see cref="SaveItemCheck.Clean"/>): unknown items are dropped and
/// items in a slot that doesn't fit them unequipped, each change pushed as a warning naming the slot file and the ids,
/// and once the player is placed <see cref="Notice"/> shows <see cref="ItemsRemovedNotice"/> or
/// <see cref="ItemsUnequippedNotice"/>. When the player was also moved (the map changed) it shows one notice instead,
/// <see cref="CombinedNotice"/> (item text first, then <see cref="MapChangedNotice"/>; <see cref="PlacementNotice"/>).
/// </para>
/// <para>
/// Tests set <see cref="UserArgs"/>, <see cref="SavesFolder"/> and <see cref="DisplayBackend"/> before the node enters the
/// tree, point
/// <see cref="GameState.SettingsPath"/> at a temporary file, and set <see cref="QuitOnConfirm"/> false so quitting only
/// sets <see cref="ExitCode"/> and emits <see cref="SignalName.QuitRequested"/>.
/// </para>
/// </summary>
public partial class Main : Node
{
    /// <summary>A quit was confirmed with <paramref name="exitCode"/> (also when <see cref="QuitOnConfirm"/> is false).</summary>
    [Signal]
    public delegate void QuitRequestedEventHandler(int exitCode);

    /// <summary>A new or loaded game finished loading: the player is placed and the loading screen is gone.</summary>
    [Signal]
    public delegate void GameStartedEventHandler();

    public const string WorldScenePath = "res://scenes/world/World.tscn";

    /// <summary>The project setting with the game version (<c>dev</c> locally; <c>release.yml</c> writes the tag).</summary>
    public const string VersionSetting = "application/config/version";

    public const string CancelAction = "ui_cancel";

    public const string MapChangedNotice = "The map has changed since this save, so you start somewhere else.";

    public const string AutosaveLine = "Your game will be saved to the autosave slot.";

    /// <summary>The input action that opens and closes the inventory screen (Tab, the gamepad's Back button).</summary>
    public const string InventoryAction = World.InventoryAction;

    /// <summary>The notice after loading a save whose unknown items were dropped.</summary>
    public const string ItemsRemovedNotice = "Some items in this save no longer exist and were removed.";

    /// <summary>The notice after loading a save whose only change was emptying slots.</summary>
    public const string ItemsUnequippedNotice = "Some items in this save were unequipped.";

    /// <summary>
    /// The one notice after a load that both cleaned items and moved the player (the map changed):
    /// <paramref name="itemNotice"/> (<see cref="ItemsRemovedNotice"/> or <see cref="ItemsUnequippedNotice"/>), a space,
    /// then <see cref="MapChangedNotice"/>.
    /// </summary>
    public static string CombinedNotice(string itemNotice) => $"{itemNotice} {MapChangedNotice}";

    /// <summary>
    /// The notice to show once a loaded or new player is placed: <see cref="CombinedNotice"/> when both apply,
    /// else <see cref="MapChangedNotice"/> when <paramref name="moved"/>, else <paramref name="itemNotice"/>; null for none.
    /// </summary>
    public static string? PlacementNotice(bool moved, string? itemNotice) => (moved, itemNotice) switch
    {
        (true, { } items) => CombinedNotice(items),
        (true, null) => MapChangedNotice,
        (false, var items) => items,
    };

    /// <summary>
    /// The press actions <see cref="Resume"/> releases when pressed in the resume frame: the game's presses and the
    /// debug toggles. Held actions (movement, <c>run</c>, camera) aren't in it.
    /// </summary>
    public static readonly string[] ResumeReleasedActions =
    [
        OnFoot.Player.JumpAction, OnFoot.Player.RunToggleAction, World.PauseAction, InventoryAction, "ui_accept", CancelAction,
        World.DebugCameraAction, Debug.OrthophotoDebugView.ToggleAction, Debug.RoadGraphDebugView.ToggleAction,
        Debug.PlaceInspector.ToggleAction,
    ];

    /// <summary>Canvas layer of the menus, above the death screen (100).</summary>
    public const int UiLayer = 110;

    private int _gameGeneration;
    private int? _seedArg;
    private bool _focusLostWhileLoading;
    private bool _quitAskedWhileLoading;
    private bool _changeAutoAcceptQuit;
    private World? _directWorld;
    private FileSaveStore? _savesStore;
    private IReadOnlyList<string> _giveItems = [];
    private string? _pendingItemNotice;

    /// <summary>
    /// Whether a confirmed quit calls <see cref="SceneTree.Quit"/>. Tests set it false: the quit then only sets
    /// <see cref="ExitCode"/> and emits <see cref="SignalName.QuitRequested"/>.
    /// </summary>
    [Export]
    public bool QuitOnConfirm { get; set; } = true;

    /// <summary>Frames the loading screen shows before <c>World</c> is added, so it is drawn first.</summary>
    [Export]
    public int LoadingScreenFrames { get; set; } = 2;

    /// <summary>The user args to read instead of the command line's; set before the node enters the tree (tests).</summary>
    public string[]? UserArgs { get; set; }

    /// <summary>
    /// The saves folder instead of <c>user://saves/</c> (an absolute path); set before the node enters the tree
    /// (tests).
    /// </summary>
    public string? SavesFolder { get; set; }

    /// <summary>
    /// The window operations the display settings use; set before the node enters the tree (tests set a recording
    /// one, since the headless display server ignores window modes and sizes).
    /// </summary>
    public IDisplayBackend DisplayBackend { get; set; } = new GodotDisplayBackend();

    public MainState State { get; private set; } = MainState.Starting;

    /// <summary>Whether this is a <c>--screenshot</c> or <c>--perf</c> run (no menus).</summary>
    public bool DirectStart => State == MainState.DirectStart;

    /// <summary>The running world; null without a game (and in the menus).</summary>
    public World? World { get; private set; }

    public SaveSlots Slots { get; private set; } = null!;

    /// <summary>The play time of the running game.</summary>
    public PlayTime PlayTime { get; private set; } = new();

    /// <summary>The exit code of a confirmed quit; null until then.</summary>
    public int? ExitCode { get; private set; }

    /// <summary>The reason the last autosave failed; null after one that worked.</summary>
    public string? LastAutosaveError { get; private set; }

    public CanvasLayer Ui { get; private set; } = null!;

    /// <summary>The flat background of the main menu.</summary>
    public ColorRect Background { get; private set; } = null!;

    public MainMenu MainMenu { get; private set; } = null!;

    public PauseMenu PauseMenu { get; private set; } = null!;

    public SlotList SlotList { get; private set; } = null!;

    public LoadingScreen LoadingScreen { get; private set; } = null!;

    public NoticeLabel Notice { get; private set; } = null!;

    public ConfirmDialog Dialog { get; private set; } = null!;

    /// <summary>The inventory screen, opened from the game or from the pause menu (<see cref="OpenInventory"/>).</summary>
    public InventoryScreen InventoryScreen { get; private set; } = null!;

    /// <summary>
    /// The usable screen size the <c>--menu settings</c> screenshot gives the settings view: 2560×1440, so every size of
    /// <see cref="WindowSizes.All"/> is listed.
    /// </summary>
    public static readonly WindowSize ScreenshotUsableScreen = new(2560, 1440);

    /// <summary>The settings view, opened from the main menu or the pause menu (<see cref="OpenSettings"/>).</summary>
    public SettingsView SettingsView { get; private set; } = null!;

    /// <summary>The game version: <see cref="VersionSetting"/>, or <c>dev</c> when it is empty (<see cref="SaveFactory.GameVersionOrDev"/>).</summary>
    public static string GameVersion =>
        SaveFactory.GameVersionOrDev(ProjectSettings.GetSetting(VersionSetting, "").AsString());

    public override void _Ready()
    {
        var args = UserArgs ?? OS.GetCmdlineUserArgs();
        if (args.Contains(ScreenshotArgs.ScreenshotFlag) || args.Contains(PerfArgs.PerfFlag))
        {
            State = MainState.DirectStart;
            if (args.Contains(ScreenshotArgs.MenuFlag))
            {
                // Built before World, so the screenshot tool finds it; hidden, with no saves until the tool points
                // them at its sample folder (UseSavesFolder), and no settings read or applied.
                BuildUi();
            }

            _directWorld = GD.Load<PackedScene>(WorldScenePath).Instantiate<World>();
            AddChild(_directWorld);
            return;
        }

        ProcessMode = ProcessModeEnum.Always;
        GetTree().AutoAcceptQuit = false;
        _changeAutoAcceptQuit = true;
        if (GameState.Instance is { } gameState)
        {
            gameState.LoadSettings();
            SettingsApplier.ApplyAll(gameState.Settings, DisplayBackend, GetViewport());
        }

        _savesStore = NewSaveStore(SavesFolder);
        Slots = new SaveSlots(_savesStore);
        BuildUi();

        try
        {
            _seedArg = SeedArg.Parse(args);
        }
        catch (ArgumentException ex)
        {
            GD.PushError($"Main: {ex.Message}; the spawn draws are seeded from the time");
        }

        _giveItems = World.ResolveGiveArg(args, GameState.Instance?.Player.Catalog ?? ItemCatalog.Default, "Main");

        var newGame = false;
        try
        {
            newGame = NewGameArg.Parse(args);
        }
        catch (ArgumentException ex)
        {
            GD.PushError($"Main: {ex.Message}; showing the main menu");
        }

        if (newGame)
        {
            StartNewGame();
        }
        else
        {
            ShowMainMenu();
        }
    }

    public override void _ExitTree()
    {
        _gameGeneration++;
        if (_changeAutoAcceptQuit && IsInsideTree())
        {
            GetTree().AutoAcceptQuit = true;
            GetTree().Paused = false;
        }
    }

    public override void _Process(double delta)
    {
        if (State == MainState.Playing && !GetTree().Paused)
        {
            PlayTime.Advance(delta);
        }
    }

    /// <summary>
    /// <c>pause</c> in a game opens the pause menu; <c>pause</c> or <c>ui_cancel</c> goes back one level in the menus
    /// (<see cref="Back"/>). Handled events are marked so. <c>ui_cancel</c> alone in a running game passes on.
    /// </summary>
    public override void _Input(InputEvent @event)
    {
        if (DirectStart || State == MainState.Starting)
        {
            return;
        }

        var pause = @event.IsActionPressed(World.PauseAction);
        var cancel = @event.IsActionPressed(CancelAction);
        var inventory = InputMap.HasAction(InventoryAction) && @event.IsActionPressed(InventoryAction);
        if (!pause && !cancel && !inventory)
        {
            return;
        }

        if (State == MainState.Playing && !Dialog.IsOpen)
        {
            if ((pause && OpenPause()) || (!pause && inventory && OpenInventory()))
            {
                GetViewport().SetInputAsHandled();
            }

            return;
        }

        if (State == MainState.Loading && !Dialog.IsOpen)
        {
            return;
        }

        if (!pause && !cancel && !InventoryScreen.Visible)
        {
            // Tab outside the inventory screen keeps ui_focus_next; the Back button does nothing there.
            return;
        }

        Back();
        GetViewport().SetInputAsHandled();
    }

    public override void _Notification(int what)
    {
        if (DirectStart || State == MainState.Starting)
        {
            return;
        }

        switch ((long)what)
        {
            case NotificationWMCloseRequest:
                RequestQuitGame();
                break;
            case NotificationApplicationFocusOut:
            case NotificationWMWindowFocusOut:
                if (State == MainState.Loading)
                {
                    _focusLostWhileLoading = true;
                }

                OpenPause();
                break;
            case NotificationApplicationFocusIn:
            case NotificationWMWindowFocusIn:
                _focusLostWhileLoading = false;
                break;
        }
    }

    /// <summary>
    /// Back one level: a dialog cancels; the inventory screen closes (to the game or the pause menu); the settings view
    /// closes (writing the settings) and the slot list returns to its menu; the pause menu resumes; on the main menu's
    /// top level nothing happens; after a failed start it returns to the main menu.
    /// </summary>
    public void Back()
    {
        if (Dialog.IsOpen)
        {
            Dialog.Cancel();
            return;
        }

        if (InventoryScreen.Visible)
        {
            CloseInventory();
            return;
        }

        if (SettingsView.Visible)
        {
            SettingsView.Close();
            return;
        }

        if (SlotList.Visible)
        {
            SlotList.Back();
            return;
        }

        switch (State)
        {
            case MainState.Paused:
                Resume();
                break;
            case MainState.LoadFailed:
                ShowMainMenu();
                break;
        }
    }

    /// <summary>Frees any game and shows the main menu (Continue and Load enabled by the saves).</summary>
    public void ShowMainMenu()
    {
        EndWorld();
        HideViews();
        State = MainState.MainMenu;
        Input.MouseMode = Input.MouseModeEnum.Visible;
        Background.Visible = true;
        MainMenu.Visible = true;
        RefreshMainMenu();
    }

    /// <summary>New game: <c>PlayerState.Reset()</c>, play time 0, the loading screen, then the spawn draw.</summary>
    public void StartNewGame() => BeginGame(null);

    /// <summary>Continue: loads <see cref="SaveSlots.MostRecent"/>; false when there is none.</summary>
    public bool Continue() => Slots.MostRecent() is { Save: { } save } info && LoadSave(save, SaveFileOf(info.Slot));

    /// <summary>Loads <paramref name="slot"/> without asking; false when it isn't a valid save.</summary>
    public bool LoadSlot(SlotId slot) =>
        Slots.Get(slot) is { Status: SlotStatus.Valid, Save: { } save } && LoadSave(save, SaveFileOf(slot));

    /// <summary>
    /// Loads <paramref name="save"/> without asking ("Loading a save"): frees a running game, restores the player
    /// state and play time, and loads the save's map with the save's player part pending. A map with no pack shows the
    /// failure screen and returns false. The player part is cleaned of unknown and misplaced items first
    /// (<see cref="SaveItemCheck.Clean"/>); <paramref name="source"/> (the slot file) names the save in the warnings.
    /// </summary>
    public bool LoadSave(SaveGame save, string? source = null)
    {
        ArgumentNullException.ThrowIfNull(save);
        if (!SlotList.MapInstalled(save.MapId))
        {
            EndWorld();
            HideViews();
            State = MainState.LoadFailed;
            LoadingScreen.Visible = true;
            LoadingScreen.ShowError($"Map {save.MapId} not installed: {MapLoader.PackPathFor(save.MapId)}/{MapPack.ManifestFile} doesn't exist");
            return false;
        }

        BeginGame(save, source);
        return true;
    }

    /// <summary>
    /// Opens the pause menu over a running game: pauses the tree, releases the mouse (the player camera remembers it
    /// wanted it). False when no game is running or it is open already.
    /// </summary>
    public bool OpenPause()
    {
        if (State != MainState.Playing || World is null)
        {
            return false;
        }

        if (World.PlayerCamera is { } camera && IsInstanceValid(camera))
        {
            camera.ReleaseForPause();
        }

        Input.MouseMode = Input.MouseModeEnum.Visible;
        GetTree().Paused = true;
        State = MainState.Paused;
        SetHudVisible(World, false);
        PauseMenu.Visible = true;
        RefreshPauseMenu();
        return true;
    }

    /// <summary>
    /// Opens the inventory screen (<c>docs/specs/inventory.md</c>, "Opening") when <see cref="World.CanOpenInventory"/>:
    /// from a running game it pauses as <see cref="OpenPause"/> does (the mouse released, the street label hidden) and
    /// the state becomes <see cref="MainState.Inventory"/>; from the pause menu's top level it opens over the paused game
    /// and the state stays <see cref="MainState.Paused"/>. False otherwise (no game, loading, a dialog or another view
    /// open, the player not alive, the debug or death camera current).
    /// </summary>
    public bool OpenInventory()
    {
        if (World is not { } world || !IsInstanceValid(world) || !world.CanOpenInventory || Dialog.IsOpen
            || InventoryScreen.Visible)
        {
            return false;
        }

        if (State == MainState.Playing)
        {
            if (world.PlayerCamera is { } camera && IsInstanceValid(camera))
            {
                camera.ReleaseForPause();
            }

            Input.MouseMode = Input.MouseModeEnum.Visible;
            GetTree().Paused = true;
            State = MainState.Inventory;
            SetHudVisible(world, false);
            InventoryScreen.Open(fromPauseMenu: false);
            return true;
        }

        if (State == MainState.Paused && PauseMenu.Visible && !SettingsView.Visible && !SlotList.Visible)
        {
            PauseMenu.Visible = false;
            InventoryScreen.Open(fromPauseMenu: true);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Closes the inventory screen: opened from the game it resumes as <see cref="Resume"/> does; opened from the pause
    /// menu it returns there with the focus on Inventory. A dialog over it is closed too. False when it isn't open.
    /// </summary>
    public bool CloseInventory()
    {
        if (!InventoryScreen.Visible)
        {
            return false;
        }

        if (State == MainState.Inventory)
        {
            return Resume();
        }

        Dialog.Close();
        InventoryScreen.Close();
        if (State == MainState.Paused)
        {
            ShowPauseMenuWithFocus(PauseMenu.InventoryButton);
        }

        return true;
    }

    /// <summary>
    /// Closes the pause menu (or the inventory screen opened from the game) and everything over it, unpauses, releases the actions pressed in this frame and tells
    /// <c>World</c> (<see cref="World.OnResumedFromPause"/>: the player ignores <c>jump</c> and <c>run_toggle</c> until
    /// released, the death screen ignores dismiss presses in this frame), so the closing press never reaches the game,
    /// even a tap shorter than one physics tick; and lets the player camera capture the mouse again when it wanted it.
    /// False when not paused.
    /// </summary>
    public bool Resume()
    {
        if (State is not (MainState.Paused or MainState.Inventory))
        {
            return false;
        }

        SettingsView.Close(notify: false);
        Dialog.Close();
        InventoryScreen.Close();
        SlotList.Visible = false;
        PauseMenu.Visible = false;
        GetTree().Paused = false;
        State = MainState.Playing;
        SetHudVisible(World, true);
        ReleaseJustPressedActions();
        if (World is { } world && IsInstanceValid(world))
        {
            world.OnResumedFromPause();
        }

        if (World?.PlayerCamera is { } camera && IsInstanceValid(camera))
        {
            camera.ResumeCapture();
        }

        return true;
    }

    /// <summary>
    /// Shows or hides <paramref name="world"/>'s HUD (the street label and the minimap): hidden while the pause menu, or
    /// anything over it, is open, so it never shows through the menus. The minimap also hides itself without a player
    /// and outside the death sequence's alive state.
    /// </summary>
    private static void SetHudVisible(World? world, bool visible)
    {
        if (world is null || !IsInstanceValid(world))
        {
            return;
        }

        if (world.Hud is { } hud && IsInstanceValid(hud))
        {
            hud.Visible = visible;
        }

        if (world.Minimap is { } minimap && IsInstanceValid(minimap))
        {
            minimap.Visible = visible;
        }
    }

    /// <summary>Opens the slot list in <paramref name="mode"/> over the current menu (save mode only while paused).</summary>
    public bool OpenSlotList(SlotListMode mode)
    {
        if (State is not (MainState.MainMenu or MainState.Paused) || (mode == SlotListMode.Save && State != MainState.Paused))
        {
            return false;
        }

        MainMenu.Visible = false;
        PauseMenu.Visible = false;
        SlotList.Open(mode);
        return true;
    }

    /// <summary>
    /// Opens the settings view over the main menu or the pause menu (changes apply while paused); back returns to that
    /// menu. False in any other state or when it is open already.
    /// </summary>
    public bool OpenSettings()
    {
        if (State is not (MainState.MainMenu or MainState.Paused) || SettingsView.Visible || Dialog.IsOpen)
        {
            return false;
        }

        MainMenu.Visible = false;
        PauseMenu.Visible = false;
        SlotList.Visible = false;
        SettingsView.Open();
        return true;
    }

    /// <summary>
    /// Points <see cref="Slots"/> (and the slot list) at <paramref name="folder"/>, an absolute folder. Used by the
    /// <c>--menu</c> screenshots, which show sample saves from a temporary folder.
    /// </summary>
    public void UseSavesFolder(string folder)
    {
        _savesStore = NewSaveStore(folder);
        Slots = new SaveSlots(_savesStore);
        SlotList.Slots = Slots;
    }

    /// <summary>
    /// <c>--menu</c> screenshots (direct start only): shows <paramref name="shot"/> as a normal game would, without
    /// changing <see cref="State"/> (so no input or notification is handled). <see cref="MenuShot.Main"/>: the main menu
    /// on its background. The others pause the tree first: the pause menu, the settings view's Display tab opened from
    /// it (with <see cref="GameSettings.Defaults"/> on a <see cref="ScreenshotUsableScreen"/> screen, so the image
    /// doesn't depend on the machine's screen or settings file), or the slot list in load mode opened from it (saved times shown in UTC, so the image doesn't depend on the
    /// machine's time zone). Call <see cref="UseSavesFolder"/> first. <see cref="MenuShot.Inventory"/>: the inventory
    /// screen as opened from the game, over the paused game (the screenshot tool gives and equips the items first).
    /// </summary>
    /// <returns>False outside a <c>--menu</c> direct start (no UI built).</returns>
    public bool ShowMenuForScreenshot(MenuShot shot)
    {
        if (!DirectStart || Ui is null || Slots is null)
        {
            return false;
        }

        HideViews();
        if (shot == MenuShot.Main)
        {
            Background.Visible = true;
            MainMenu.Visible = true;
            RefreshMainMenu();
            return true;
        }

        GetTree().Paused = true;
        SetHudVisible(_directWorld, false);
        if (shot == MenuShot.Inventory)
        {
            InventoryScreen.Open(fromPauseMenu: false);
            return true;
        }

        PauseMenu.Visible = true;
        var alive = _directWorld is { } world && IsInstanceValid(world) && world.CanSave;
        PauseMenu.Refresh(canSave: alive, canLoad: AnyValidSlot(), canInventory: alive);
        switch (shot)
        {
            case MenuShot.Settings:
                PauseMenu.Visible = false;
                SettingsView.UsableScreen = () => ScreenshotUsableScreen;
                SettingsView.Source = () => GameSettings.Defaults(ScreenshotUsableScreen);
                SettingsView.Open();
                break;
            case MenuShot.Slots:
                PauseMenu.Visible = false;
                SlotList.TimeZone = TimeZoneInfo.Utc;
                SlotList.Open(SlotListMode.Load);
                break;
        }

        return true;
    }

    /// <summary>The pause menu's Quit to menu: asks, then autosaves and shows the main menu.</summary>
    public void RequestQuitToMenu()
    {
        if (State != MainState.Paused)
        {
            return;
        }

        Dialog.Ask("Quit to the main menu?", AutosaveLine, [("Quit", ConfirmQuitToMenu), ("Cancel", () => { })]);
    }

    /// <summary>
    /// Quit to menu after the confirmation: writes the autosave and shows the main menu; a failed autosave asks again
    /// with "Could not autosave: &lt;reason&gt;" (Quit without saving, Cancel).
    /// </summary>
    public void ConfirmQuitToMenu()
    {
        if (TryAutosave())
        {
            ShowMainMenu();
            return;
        }

        Dialog.Ask($"Could not autosave: {LastAutosaveError}", null, [("Quit without saving", ShowMainMenu), ("Cancel", () => { })]);
    }

    /// <summary>
    /// Quitting the game (the window's close request, the main menu's Quit): pauses a running game and asks "Quit the
    /// game?" (with the autosave line during a game); confirming autosaves during a game and quits with code 0.
    /// While the loading screen shows it asks without the autosave line, and confirming never autosaves; when the
    /// player is placed with that question still open, the game opens paused with the same question over the pause
    /// menu (<see cref="OnPlayerPlaced"/>).
    /// </summary>
    public void RequestQuitGame()
    {
        if (State == MainState.Playing)
        {
            OpenPause();
        }
        else if (State == MainState.Inventory)
        {
            // The game is paused already: the question goes over the pause menu, so Cancel returns there.
            Dialog.Close();
            InventoryScreen.Close();
            State = MainState.Paused;
            ShowPauseMenuWithFocus(null);
        }
        else if (State == MainState.Paused && InventoryScreen.Visible)
        {
            Dialog.Close();
            InventoryScreen.Close();
            ShowPauseMenuWithFocus(PauseMenu.InventoryButton);
        }

        if (State == MainState.Paused)
        {
            Dialog.Ask("Quit the game?", AutosaveLine, [("Quit", ConfirmQuitGame), ("Cancel", () => { })]);
            return;
        }

        if (State == MainState.Loading)
        {
            AskQuitWithoutAutosave();
            return;
        }

        Dialog.Ask("Quit the game?", null, [("Quit", () => Quit(0)), ("Cancel", () => { })]);
    }

    /// <summary>
    /// Quitting the game after the confirmation: the autosave during a game (playing or paused; not while loading, which
    /// would replace the autosave with a game that hasn't started), then exit code 0.
    /// </summary>
    public void ConfirmQuitGame()
    {
        if (State is not (MainState.Playing or MainState.Paused or MainState.Inventory) || TryAutosave())
        {
            Quit(0);
            return;
        }

        Dialog.Ask($"Could not autosave: {LastAutosaveError}", null, [("Quit without saving", () => Quit(0)), ("Cancel", () => { })]);
    }

    /// <summary>
    /// The save of the running game ("Saving"), built by <see cref="SaveFactory.Make"/> from the player state,
    /// <see cref="World.CanSave"/>, <see cref="World.PlayerFacingYaw"/>, the map id and the manifest's
    /// <c>generated_at</c>, the game version, now (UTC) and the play time. Null without a game, or for a manual save
    /// (<paramref name="autosave"/> false) when the player can't save now.
    /// </summary>
    public SaveGame? MakeSave(bool autosave)
    {
        if (World is null || !IsInstanceValid(World) || GameState.Instance is not { } gameState)
        {
            return null;
        }

        var manifest = World.MapLoader.Manifest;
        return SaveFactory.Make(
            autosave,
            World.CanSave,
            gameState.Player,
            World.PlayerFacingYaw,
            manifest?.MapId ?? World.MapLoader.ResolvedMapId,
            manifest?.GeneratedAt,
            GameVersion,
            DateTimeOffset.UtcNow,
            PlayTime.Seconds);
    }

    /// <summary>Writes the autosave of the running game; false (with <see cref="LastAutosaveError"/>) when it fails.</summary>
    public bool TryAutosave()
    {
        LastAutosaveError = null;
        if (MakeSave(autosave: true) is not { } save)
        {
            LastAutosaveError = "no game is running";
            return false;
        }

        try
        {
            Slots.Write(SlotId.Autosave, save);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            LastAutosaveError = ex.Message;
            GD.PushWarning($"Main: could not autosave: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// "Quit the game?" asked while loading, without the autosave line: Quit exits with code 0 and writes no autosave
    /// (the game hasn't started, and the question promised none); Cancel and back only close it.
    /// </summary>
    private void AskQuitWithoutAutosave()
    {
        _quitAskedWhileLoading = true;
        Dialog.Ask(
            "Quit the game?",
            null,
            [("Quit", () => Quit(0)), ("Cancel", () => _quitAskedWhileLoading = false)],
            onCancel: () => _quitAskedWhileLoading = false);
    }

    /// <summary>
    /// A confirmed quit: closes an open settings view first without its <c>Closed</c> signal (which reverts a pending
    /// display change and writes <c>settings.json</c>, as Back does), then sets <see cref="ExitCode"/>, emits the signal
    /// and, with <see cref="QuitOnConfirm"/>, quits. Every quit route (the main menu's Quit, a close request on the
    /// menus or in a game, Quit without saving) ends here.
    /// </summary>
    public void Quit(int exitCode)
    {
        if (SettingsView is { } view && IsInstanceValid(view) && view.Visible)
        {
            view.Close(notify: false);
        }

        ExitCode = exitCode;
        EmitSignal(SignalName.QuitRequested, exitCode);
        if (QuitOnConfirm)
        {
            GetTree().Quit(exitCode);
        }
    }

    /// <summary>The display name of <paramref name="mapId"/> from its manifest; the id when that can't be read.</summary>
    public static string DisplayNameOf(string mapId) => SlotList.MapDisplayNameOf(mapId);

    private void BeginGame(SaveGame? save, string? source = null)
    {
        var generation = ++_gameGeneration;
        EndWorld();
        HideViews();
        State = MainState.Loading;
        _focusLostWhileLoading = false;
        _quitAskedWhileLoading = false;
        Input.MouseMode = Input.MouseModeEnum.Visible;

        var mapId = save?.MapId
            ?? ProjectSettings.GetSetting(MapLoader.MapIdSetting, MapLoader.DefaultMapId).AsString();
        LoadingScreen.Visible = true;
        LoadingScreen.ShowLoading(DisplayNameOf(mapId));

        var playerState = GameState.Instance?.Player;
        _pendingItemNotice = null;
        var load = save?.Player;
        if (save is null)
        {
            playerState?.Reset();
            PlayTime = new PlayTime();
        }
        else
        {
            load = CleanSaveItems(save.Player, playerState?.Catalog ?? ItemCatalog.Default, source);
            playerState?.Restore(load);
            PlayTime = new PlayTime(save.PlaySeconds);
        }

        AddWorldAfterLoadingScreenAsync(generation, save?.MapId, load);
    }

    /// <summary>
    /// The load cleanup (<c>docs/specs/inventory.md</c>, "Loading: unknown items"): <see cref="SaveItemCheck.Clean"/>
    /// of <paramref name="player"/>; each removed id and each unequipped item is pushed as one warning naming
    /// <paramref name="source"/> and the id, and the notice to show once the player is placed is set.
    /// </summary>
    /// <returns>The cleaned player part.</returns>
    private SavePlayer CleanSaveItems(SavePlayer player, ItemCatalog catalog, string? source)
    {
        var check = SaveItemCheck.Clean(player, catalog);
        if (!check.Changed)
        {
            return check.Player;
        }

        var file = source ?? "the save";
        foreach (var id in check.RemovedIds)
        {
            GD.PushWarning($"Main: {file}: item '{id}' no longer exists; removed from the gear (and its slot)");
        }

        foreach (var unequip in check.Unequipped)
        {
            GD.PushWarning(
                $"Main: {file}: item '{unequip.ItemId}' doesn't fit the {unequip.Slot.ToString().ToLowerInvariant()} slot; unequipped and kept");
        }

        _pendingItemNotice = check.RemovedIds.Count > 0 ? ItemsRemovedNotice : ItemsUnequippedNotice;
        return check.Player;
    }

    /// <summary>The full path of <paramref name="slot"/>'s file in the saves folder in use.</summary>
    private string SaveFileOf(SlotId slot) =>
        _savesStore is { } store ? Path.Combine(ProjectSettings.GlobalizePath(store.Folder), slot.FileName) : slot.FileName;

    private async void AddWorldAfterLoadingScreenAsync(int generation, string? mapId, SavePlayer? load)
    {
        for (var i = 0; i < LoadingScreenFrames; i++)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            if (generation != _gameGeneration || !IsInsideTree())
            {
                return;
            }
        }

        var world = GD.Load<PackedScene>(WorldScenePath).Instantiate<World>();
        world.Name = "World";
        world.ProcessMode = ProcessModeEnum.Pausable;
        world.MapId = mapId;
        world.Seed = _seedArg ?? World.TimeSeed();
        world.PendingLoad = load;

        // --give goes to every world until one places its player (applied after its first placement; cleared in
        // OnPlayerPlaced), so a start that fails (StartFailed) doesn't use it up.
        world.GiveItems = _giveItems;
        world.CameraSettings = GameState.Instance?.Settings.Gameplay;
        world.MinimapSettings = GameState.Instance?.Settings.Gameplay;
        world.QualitySettings = QualityPreset.For((GameState.Instance?.Settings ?? GameSettings.Default).Display.Quality);
        world.PlayerSpawned += _ => OnPlayerPlaced(world, moved: false);
        world.PlayerLoaded += (_, moved) => OnPlayerPlaced(world, moved);
        world.StartFailed += message => OnStartFailed(world, message);
        World = world;
        AddChild(world);
        if (IsInstanceValid(world) && world.GetParent() == this)
        {
            MoveChild(world, 0);
        }
    }

    private void OnPlayerPlaced(World world, bool moved)
    {
        if (world == World)
        {
            // The world applied --give at this first placement (World.GiveItems); later worlds get nothing.
            _giveItems = [];
        }

        if (world != World || State != MainState.Loading)
        {
            return;
        }

        LoadingScreen.Visible = false;
        State = MainState.Playing;
        if (PlacementNotice(moved, _pendingItemNotice) is { } notice)
        {
            Notice.ShowNotice(notice);
        }

        _pendingItemNotice = null;

        EmitSignal(SignalName.GameStarted);
        var quitAsked = _quitAskedWhileLoading && Dialog.IsOpen;
        _quitAskedWhileLoading = false;
        if (quitAsked)
        {
            // The quit question asked on the loading screen is still open: the game opens paused under it (the camera
            // releases the mouse and keeps wanting it), and the question is asked again over the pause menu, still
            // without autosave, so Cancel goes back to the pause menu with the focus on Resume.
            _focusLostWhileLoading = false;
            Dialog.Close();
            OpenPause();
            AskQuitWithoutAutosave();
            return;
        }

        if (_focusLostWhileLoading)
        {
            _focusLostWhileLoading = false;
            OpenPause();
        }
    }

    private void OnStartFailed(World world, string message)
    {
        if (world != World || State != MainState.Loading)
        {
            return;
        }

        World = null;
        world.QueueFree();
        GetTree().Paused = false;
        State = MainState.LoadFailed;
        Input.MouseMode = Input.MouseModeEnum.Visible;
        LoadingScreen.Visible = true;
        LoadingScreen.ShowError(message);
    }

    /// <summary>Frees the running world (if any) at once and unpauses the tree.</summary>
    private void EndWorld()
    {
        if (World is { } world && IsInstanceValid(world))
        {
            if (world.GetParent() == this)
            {
                RemoveChild(world);
            }

            world.QueueFree();
        }

        World = null;
        Notice.HideNow();
        GetTree().Paused = false;
    }

    private void HideViews()
    {
        SettingsView.Close(notify: false);
        Dialog.Close();
        InventoryScreen.Close();
        Background.Visible = false;
        MainMenu.Visible = false;
        PauseMenu.Visible = false;
        SlotList.Visible = false;
        LoadingScreen.Visible = false;
    }

    private bool AnyValidSlot() => Slots.List().Any(info => info.Status == SlotStatus.Valid);

    private void RefreshMainMenu() => MainMenu.Refresh(canContinue: Slots.MostRecent() is not null, canLoad: AnyValidSlot());

    private void RefreshPauseMenu() =>
        PauseMenu.Refresh(
            canSave: World?.CanSave == true,
            canLoad: AnyValidSlot(),
            canInventory: World?.CanOpenInventory == true,
            inventoryHint: World is { } world && IsInstanceValid(world) && world.Death.State != DeathState.Alive);

    /// <summary>Shows the pause menu (refreshed) with the focus on <paramref name="focus"/> when it can take it.</summary>
    private void ShowPauseMenuWithFocus(Control? focus)
    {
        PauseMenu.Visible = true;
        RefreshPauseMenu();
        if (focus is not null && UiFocus.IsFocusable(focus))
        {
            focus.GrabFocus();
        }
    }

    /// <summary>
    /// Applies a change made in the settings view at once: the display group (<see cref="SettingsApplier.ApplyDisplay"/>
    /// with the previous values, through <see cref="DisplayBackend"/>), the audio volumes, and on a running world the
    /// camera values (<see cref="World.CameraSettings"/>), the minimap corner (<see cref="World.MinimapSettings"/>) and the quality preset (<see cref="World.QualitySettings"/>).
    /// </summary>
    private void OnSettingsChanged(GameSettings before, GameSettings after)
    {
        if (DirectStart)
        {
            return;
        }

        if (before.Display != after.Display)
        {
            SettingsApplier.ApplyDisplay(after.Display, DisplayBackend, GetViewport(), before.Display);
        }

        if (before.Audio != after.Audio)
        {
            SettingsApplier.ApplyAudio(after.Audio);
        }

        if (World is not { } world || !IsInstanceValid(world))
        {
            return;
        }

        if (before.Gameplay != after.Gameplay)
        {
            world.CameraSettings = after.Gameplay;
            world.ApplyCameraSettings();
            world.MinimapSettings = after.Gameplay;
        }

        if (before.Display.Quality != after.Display.Quality)
        {
            world.QualitySettings = QualityPreset.For(after.Display.Quality);
        }
    }

    /// <summary>The settings view closed: back to the menu it was opened from; a failed write shows a notice.</summary>
    private void OnSettingsClosed()
    {
        if (SettingsView.LastSaveError is { } error)
        {
            Notice.ShowNotice($"Could not save the settings: {error}");
        }

        if (State == MainState.Paused)
        {
            PauseMenu.Visible = true;
            RefreshPauseMenu();
            PauseMenu.SettingsButton.GrabFocus();
        }
        else if (State == MainState.MainMenu)
        {
            MainMenu.Visible = true;
            RefreshMainMenu();
            MainMenu.SettingsButton.GrabFocus();
        }
    }

    private void OnSlotListBack()
    {
        if (State == MainState.Paused)
        {
            PauseMenu.Visible = true;
            RefreshPauseMenu();
        }
        else if (State == MainState.MainMenu)
        {
            MainMenu.Visible = true;
            RefreshMainMenu();
        }
    }

    private void OnLoadChosen(int slotNumber)
    {
        var slot = slotNumber == 0 ? SlotId.Autosave : SlotId.Manual(slotNumber);
        if (State != MainState.Paused)
        {
            LoadSlot(slot);
            return;
        }

        var name = slot.IsAutosave ? slot.DisplayName : $"slot {slot.Number}";
        Dialog.Ask(
            $"Load {name}? Progress since your last save will be lost.",
            null,
            [("Load", () => LoadSlot(slot)), ("Cancel", () => { })]);
    }

    /// <summary>
    /// Releases each press action (<see cref="ResumeReleasedActions"/>) pressed in this frame, so the press that closed
    /// the pause menu (or a button press in the same frame) isn't seen as just pressed by the game. Held actions
    /// (<c>move_*</c>, <c>run</c>, <c>camera_*</c>) are left alone: a movement key held through the resume keeps working.
    /// </summary>
    private static void ReleaseJustPressedActions()
    {
        foreach (var action in ResumeReleasedActions)
        {
            if (InputMap.HasAction(action) && Input.IsActionJustPressed(action))
            {
                Input.ActionRelease(action);
            }
        }
    }

    /// <summary>A saves store on <paramref name="folder"/>; a folder it refuses (a <c>res://</c> one) pushes an error and the default is used.</summary>
    private static FileSaveStore NewSaveStore(string? folder)
    {
        try
        {
            return new FileSaveStore(folder);
        }
        catch (ArgumentException ex)
        {
            GD.PushError($"Main: {ex.Message}; using {FileSaveStore.DefaultFolder}");
            return new FileSaveStore();
        }
    }

    private void BuildUi()
    {
        Ui = new CanvasLayer { Name = "Ui", Layer = UiLayer, ProcessMode = ProcessModeEnum.Always };
        AddChild(Ui);
        var root = new Control { Name = "Root", Theme = UiTheme.Theme };
        UiBuild.Fill(root);
        Ui.AddChild(root);

        Background = UiBuild.FullRect(root, "Background", UiTheme.Background);
        MainMenu = new MainMenu { Name = "MainMenu", Version = GameVersion, Visible = false };
        root.AddChild(MainMenu);
        PauseMenu = new PauseMenu { Name = "PauseMenu", Visible = false };
        root.AddChild(PauseMenu);
        SlotList = new SlotList { Name = "SlotList" };
        root.AddChild(SlotList);
        LoadingScreen = new LoadingScreen { Name = "LoadingScreen", Visible = false };
        root.AddChild(LoadingScreen);
        Notice = new NoticeLabel { Name = "Notice" };
        root.AddChild(Notice);
        SettingsView = new SettingsView { Name = "SettingsView" };
        root.AddChild(SettingsView);
        Dialog = new ConfirmDialog { Name = "ConfirmDialog" };
        InventoryScreen = new InventoryScreen { Name = "InventoryScreen", Dialog = Dialog };
        root.AddChild(InventoryScreen);
        root.AddChild(Dialog);

        MainMenu.NewGameButton.Pressed += StartNewGame;
        MainMenu.ContinueButton.Pressed += () => Continue();
        MainMenu.LoadButton.Pressed += () => OpenSlotList(SlotListMode.Load);
        MainMenu.SettingsButton.Pressed += () => OpenSettings();
        MainMenu.QuitButton.Pressed += RequestQuitGame;

        PauseMenu.ResumeButton.Pressed += () => Resume();
        PauseMenu.InventoryButton.Pressed += () => OpenInventory();
        InventoryScreen.CloseRequested += () => CloseInventory();
        PauseMenu.SaveButton.Pressed += () => OpenSlotList(SlotListMode.Save);
        PauseMenu.LoadButton.Pressed += () => OpenSlotList(SlotListMode.Load);
        PauseMenu.SettingsButton.Pressed += () => OpenSettings();
        PauseMenu.QuitToMenuButton.Pressed += RequestQuitToMenu;

        SettingsView.Dialog = Dialog;
        SettingsView.UsableScreen = () => DisplayBackend.GetUsableScreenSize();
        SettingsView.Changed += OnSettingsChanged;
        SettingsView.Closed += OnSettingsClosed;

        SlotList.Slots = Slots;
        SlotList.Dialog = Dialog;
        SlotList.MakeSave = () => MakeSave(autosave: false);
        SlotList.LoadChosen += OnLoadChosen;
        SlotList.BackRequested += OnSlotListBack;

        LoadingScreen.BackButton.Pressed += ShowMainMenu;
    }
}
