using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using GdUnit4;
using Godot;
using Roadman.Core;
using Roadman.Core.Edge;
using Roadman.Core.Items;
using Roadman.Debug;
using Roadman.OnFoot;
using Roadman.Scenes;
using Roadman.Ui;
using static GdUnit4.Assertions;
using static Roadman.Tests.MainTestSupport;

namespace Roadman.Tests;

/// <summary>
/// The inventory screen of <c>docs/specs/inventory.md</c> (<c>Ac</c> numbers are its acceptance criteria).
/// <para>
/// Two tests run a game through <c>Main</c> on the pack every suite uses (with <see cref="MainTestSupport"/>'s
/// temporary saves folder and settings path): opening from the game and closing it each way, the pause menu's
/// Inventory button, and <c>--give</c> with an unknown id (13 to 16); and the cases where it may not open, death with
/// gear and <c>--give</c> not repeated on the respawn (13, 14, 16, 22). Criteria 12, 13 and 21 (save and load) are in
/// <see cref="SaveLoadTest"/>, 23 in <see cref="SettingsViewTest"/>.
/// </para>
/// <para>
/// The screen's own behaviour (17 to 20) is tested on an <see cref="InventoryScreen"/> alone, on its own
/// <see cref="PlayerState"/> over <see cref="ItemCatalog.Default"/>, with no game: the screen doesn't depend on
/// <c>World</c>, and this keeps those tests short.
/// </para>
/// </summary>
[TestSuite]
[RequireGodotRuntime]
public class InventoryTest
{
    /// <summary>Criterion 14: the frames over which nothing may change while the screen is open.</summary>
    private const int PausedFrames = 60;

    /// <summary>Criterion 15: frames watched after a close for a jump or a camera move.</summary>
    private const int AfterCloseFrames = 30;

    /// <summary>Criterion 15: physics frames a closing press is held before its release (a short, ordinary press).</summary>
    private const int HoldFrames = 4;

    /// <summary>Criterion 15: the largest rise of a standing player's feet that isn't a jump, m (floor snapping noise).</summary>
    private const float StandingRise = 0.01f;

    /// <summary>Criterion 22: a shortened fall, so the death screen comes sooner (the screen keeps its defaults).</summary>
    private const float ShortFallTime = 1f;

    /// <summary>Frames for a standalone screen to lay out.</summary>
    private const int LayoutFrames = 2;

    private string _folder = "";
    private Main? _main;
    private Node? _testBody;
    private InventoryScreen? _screen;
    private ISceneRunner? _screenRunner;

    /// <summary>The suite runs on the test-area pack (no test here has a full-pack-only branch).</summary>
    [BeforeTest]
    public void SetUp()
    {
        _folder = BeginTest("inventory");
        TestPacks.UseTestArea();
    }

    [AfterTest]
    public void CleanUp()
    {
        TestPacks.UseFullPack();
        _screenRunner?.Dispose();
        _screenRunner = null;
        TearDown(_folder, _testBody, _screen, _main);
        _main = null;
        _testBody = null;
        _screen = null;
        _folder = "";
    }

    // ----- Criteria 13 to 16 in a game -----

    /// <summary>
    /// <c>--give crowbar,not_an_item</c> pushes one error naming the unknown id and the known ids, gives nothing and the
    /// game starts (13). Tab while alive on the player camera opens the screen: <see cref="MainState.Inventory"/>, the
    /// tree paused, the mouse released while the camera keeps wanting it, the street label hidden; over 60 frames with
    /// W held the player, a falling body and the play time don't change (14). While the discard confirmation shows,
    /// Esc, Tab, the east face button and the Back button each close only the dialog, and Tab leaves the focus where
    /// Esc does (15). Each way of closing, opened by Tab or the Back button (14): Space and the south face button on
    /// Close, Tab, Esc, the east face button, the Back button and a Close click; after each the game runs, the camera
    /// wants the capture, and over 30 frames the standing player doesn't jump, the camera doesn't turn and running
    /// isn't toggled (15). Then the pause menu's Inventory button (accept and click) opens the screen over the paused
    /// game, and Esc, the Start button, Tab and Close each return to the pause menu with the focus on Inventory (16).
    /// </summary>
    [TestCase]
    public async Task Ac13_Ac14_Ac15_Ac16_OpenFromTheGameAndThePauseMenu_PausesAndFreezes_EveryCloseResumesWithoutJumping_BackClosesOnlyTheDialog()
    {
        WorldEdgeTest.AssertPackPresent();
        var state = WorldEdgeTest.PlayerStateOfGame();
        var capture = new ErrorCapture();
        OS.AddLogger(capture);
        using var runner = ISceneRunner.Load(_main = NewMain(_folder, "--new-game", "--give", "crowbar,not_an_item"), true);
        World world;
        try
        {
            world = await WaitForGame(_main);

            // Criterion 13: an unknown id: one error naming it and the known ids; nothing given; the game started.
            var giveErrors = capture.ErrorsOnly.Where(e => e.Contains("--give", StringComparison.Ordinal)).ToList();
            GD.Print($"InventoryTest: Ac13 --give errors: {string.Join(" | ", giveErrors)}");
            AssertInt(giveErrors.Count).OverrideFailureMessage($"expected one --give error, got: {string.Join(" | ", giveErrors)}").IsEqual(1);
            AssertBool(giveErrors[0].Contains("not_an_item", StringComparison.Ordinal)).OverrideFailureMessage($"the error doesn't name the id: {giveErrors[0]}").IsTrue();
            foreach (var item in ItemCatalog.Default.All)
            {
                AssertBool(giveErrors[0].Contains(item.Id, StringComparison.Ordinal)).OverrideFailureMessage($"the error doesn't list known id '{item.Id}': {giveErrors[0]}").IsTrue();
            }

            AssertInt(world.GivenItems.Count).IsEqual(0);
            AssertInt(state.Gear.Count).OverrideFailureMessage($"gear after a bad --give: {string.Join(",", state.Gear)}").IsEqual(0);
            AssertThat(_main.State).IsEqual(MainState.Playing);
        }
        finally
        {
            OS.RemoveLogger(capture);
        }

        await Settle(world);
        var main = _main!;
        var screen = main.InventoryScreen;
        var player = world.Player!;
        var camera = world.PlayerCamera!;
        AssertBool(camera.WantsCapture).IsTrue();
        state.AddGear("crowbar");
        state.AddGear("phone");

        var body = new RigidBody3D { Name = "InventoryTestBody", CollisionLayer = CollisionLayers.Barrier, CollisionMask = CollisionLayers.Map };
        body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(0.4f, 0.4f, 0.4f) } });
        world.AddChild(body);
        _testBody = body;
        body.GlobalPosition = player.GlobalPosition + new Vector3(5f, 40f, 5f);
        await WorldEdgeTest.PhysicsFrames(world, 5);
        await WaitForPlayTime(main);
        AssertFloat(body.LinearVelocity.Y).OverrideFailureMessage("the test body isn't falling").IsLess(0f);

        // Criterion 14: Tab opens the screen and pauses.
        TapKey(Key.Tab);
        AssertOpenFromGame(main, world, "Tab");

        var playerAt = player.GlobalPosition;
        var bodyAt = body.GlobalPosition;
        var playTime = main.PlayTime.Seconds;
        var maxPlayerMove = 0f;
        var maxBodyMove = 0f;
        var maxPlayChange = 0d;
        Input.ActionPress(Player.MoveForwardAction);
        try
        {
            await WorldEdgeTest.Drive(main, _ =>
            {
                maxPlayerMove = Mathf.Max(maxPlayerMove, (player.GlobalPosition - playerAt).Length());
                maxBodyMove = Mathf.Max(maxBodyMove, (body.GlobalPosition - bodyAt).Length());
                maxPlayChange = Math.Max(maxPlayChange, Math.Abs(main.PlayTime.Seconds - playTime));
                return false;
            }, PausedFrames);
        }
        finally
        {
            Input.ActionRelease(Player.MoveForwardAction);
        }

        GD.Print(WorldEdgeTest.Invariant($"InventoryTest: Ac14 over {PausedFrames} frames with the screen open: player {maxPlayerMove} m, body {maxBodyMove} m, play time {maxPlayChange} s"));
        AssertFloat(maxPlayerMove).IsEqual(0f);
        AssertFloat(maxBodyMove).IsEqual(0f);
        AssertFloat(maxPlayChange).IsEqual(0d);
        AssertThat(main.State).IsEqual(MainState.Inventory);

        // The body would land near the player once the game runs; drop it before the jump checks.
        body.GetParent().RemoveChild(body);
        body.Free();
        _testBody = null;

        // Criterion 15: while the discard confirmation shows, back closes only the dialog; Tab leaves the focus where Esc does.
        Control? focusAfterEsc = null;
        foreach (var (what, back) in new (string, Action)[]
        {
            ("Esc", () => TapKey(Key.Escape)),
            ("Tab", () => TapKey(Key.Tab)),
            ("east face button", () => TapPad(GamepadButtons.East)),
            ("Back button", () => TapPad(GamepadButtons.Back)),
        })
        {
            var discard = screen.DiscardButtonOf("phone")!;
            discard.GrabFocus();
            TapKey(Key.Enter);
            AssertBool(main.Dialog.IsOpen).OverrideFailureMessage($"{what}: accept on Discard didn't ask").IsTrue();
            AssertThat(main.Dialog.MessageText).IsEqual("Discard Phone? It will be gone for good.");
            back();
            AssertBool(main.Dialog.IsOpen).OverrideFailureMessage($"{what}: the dialog is still open").IsFalse();
            AssertBool(screen.IsVisibleInTree()).OverrideFailureMessage($"{what}: back over the dialog closed the screen").IsTrue();
            AssertThat(main.State).OverrideFailureMessage($"{what}: back over the dialog changed the state").IsEqual(MainState.Inventory);
            AssertBool(main.GetTree().Paused).IsTrue();
            AssertBool(state.Gear.Contains("phone")).OverrideFailureMessage($"{what}: back over the dialog discarded the item").IsTrue();
            var focus = FocusOwner(main);
            GD.Print($"InventoryTest: Ac15 {what} over the dialog: focus on {NameOf(focus)}");
            focusAfterEsc ??= focus;
            AssertThat(focus).OverrideFailureMessage($"{what}: the focus is on {NameOf(focus)}, Esc left it on {NameOf(focusAfterEsc)}").IsSame(focusAfterEsc);
        }

        AssertThat(focusAfterEsc).OverrideFailureMessage("the focus is nowhere after the dialog closed").IsNotNull();

        // Criterion 15: each way of closing, from a game opened by Tab or the Back button.
        screen.CloseButton.GrabFocus();
        await CloseAndAssertResumed(world, "Space on Close", () => KeyEvent(Key.Space, true), () => KeyEvent(Key.Space, false));

        TapPad(GamepadButtons.Back);
        AssertOpenFromGame(main, world, "Back button");
        screen.CloseButton.GrabFocus();
        await CloseAndAssertResumed(world, "south face button on Close", () => PadButton(GamepadButtons.South, true), () => PadButton(GamepadButtons.South, false));

        foreach (var (what, press, release) in new (string, Action, Action)[]
        {
            ("Tab", () => KeyEvent(Key.Tab, true), () => KeyEvent(Key.Tab, false)),
            ("Esc", () => KeyEvent(Key.Escape, true), () => KeyEvent(Key.Escape, false)),
            ("east face button", () => PadButton(GamepadButtons.East, true), () => PadButton(GamepadButtons.East, false)),
            ("Back button", () => PadButton(GamepadButtons.Back, true), () => PadButton(GamepadButtons.Back, false)),
        })
        {
            await WorldEdgeTest.PhysicsFrames(world, 2);
            TapKey(Key.Tab);
            AssertOpenFromGame(main, world, $"Tab before closing with {what}");
            await CloseAndAssertResumed(world, what, press, release);
        }

        await WorldEdgeTest.PhysicsFrames(world, 2);
        TapKey(Key.Tab);
        AssertOpenFromGame(main, world, "Tab before the Close click");
        await CloseAndAssertResumed(world, "Close click", () => Click(screen.CloseButton), () => { });

        // Criterion 16: the pause menu's Inventory button.
        await WorldEdgeTest.PhysicsFrames(world, 2);
        var menu = main.PauseMenu;
        foreach (var (what, back) in new (string, Action)[]
        {
            ("Esc", () => TapKey(Key.Escape)),
            ("Start button", () => TapPad(GamepadButtons.Start)),
            ("Tab", () => TapKey(Key.Tab)),
            ("Close", () => Click(screen.CloseButton)),
        })
        {
            if (main.State == MainState.Playing)
            {
                TapKey(Key.Escape);
            }

            AssertThat(main.State).IsEqual(MainState.Paused);
            AssertBool(menu.InventoryButton.Disabled).OverrideFailureMessage("Inventory is disabled while alive on the player camera").IsFalse();
            AssertBool(menu.InventoryHint.Visible).IsFalse();
            if (what == "Esc")
            {
                menu.InventoryButton.GrabFocus();
                TapKey(Key.Enter);
            }
            else
            {
                Click(menu.InventoryButton);
            }

            AssertBool(screen.IsVisibleInTree()).OverrideFailureMessage($"{what}: Inventory didn't open the screen").IsTrue();
            AssertBool(screen.OpenedFromPauseMenu).IsTrue();
            AssertBool(menu.Visible).OverrideFailureMessage("the pause menu shows under the screen").IsFalse();
            AssertThat(main.State).OverrideFailureMessage("opened from the pause menu, the state stays Paused").IsEqual(MainState.Paused);
            AssertBool(main.GetTree().Paused).IsTrue();
            AssertBool(world.Hud.Visible).IsFalse();
            AssertThat(FocusOwner(main)).OverrideFailureMessage($"{what}: the focus starts on {NameOf(FocusOwner(main))}").IsSame(screen.FirstFocusTarget());

            back();
            AssertBool(screen.Visible).OverrideFailureMessage($"{what}: didn't close the screen").IsFalse();
            AssertBool(menu.IsVisibleInTree()).OverrideFailureMessage($"{what}: didn't return to the pause menu").IsTrue();
            AssertThat(main.State).OverrideFailureMessage($"{what}: left the pause menu").IsEqual(MainState.Paused);
            AssertBool(main.GetTree().Paused).IsTrue();
            AssertThat(FocusOwner(main)).OverrideFailureMessage($"{what}: the focus is on {NameOf(FocusOwner(main))}, not Inventory").IsSame(menu.InventoryButton);
        }

        TapKey(Key.Escape);
        AssertThat(main.State).IsEqual(MainState.Playing);
        AssertBool(main.GetTree().Paused).IsFalse();
        await EndFrames(main);
    }

    /// <summary>
    /// <c>--give crowbar,baseball_bat,phone,crowbar</c> gives the three, in that order, once, before <c>GameStarted</c>
    /// (13). With the debug camera current Tab does nothing and the pause menu's Inventory is disabled with no hint
    /// (14, 16). With the crowbar equipped and money 120, entering the void empties the slots and the gear (22); during
    /// <c>Falling</c> and on the death screen Tab does nothing and Inventory is disabled with the hint (14, 16); the
    /// losses line is criterion 7's form; after the respawn nothing is given again (13) and Tab opens the screen showing
    /// "You have no gear." (22).
    /// </summary>
    [TestCase]
    public async Task Ac13_Ac14_Ac16_Ac22_GiveOnceBeforeStart_NotWithTheDebugCameraOrWhileDying_DeathEmptiesSlotsAndGear_RespawnShowsNoGear()
    {
        WorldEdgeTest.AssertPackPresent();
        var state = WorldEdgeTest.PlayerStateOfGame();
        string? gearAtStart = null;
        var main = _main = NewMain(_folder, "--new-game", "--give", "crowbar,baseball_bat,phone,crowbar");
        main.GameStarted += () => gearAtStart = string.Join(",", state.Gear.Order(StringComparer.Ordinal));
        using var runner = ISceneRunner.Load(main, true);
        var world = await WaitForGame(main);
        await WaitForEdge(world);
        await Settle(world);
        var screen = main.InventoryScreen;
        var menu = main.PauseMenu;

        // Criterion 13: given once, in order, before GameStarted.
        AssertThat(string.Join(",", world.GivenItems)).IsEqual("crowbar,baseball_bat,phone");
        AssertThat(gearAtStart).OverrideFailureMessage("GameStarted came before the items were given").IsEqual("baseball_bat,crowbar,phone");
        AssertThat(string.Join(",", state.Gear.Order(StringComparer.Ordinal))).IsEqual("baseball_bat,crowbar,phone");

        // Criteria 14 and 16: the debug camera.
        AssertBool(world.ToggleDebugCamera()).OverrideFailureMessage("the debug camera didn't switch on").IsTrue();
        AssertBool(world.CanOpenInventory).IsFalse();
        TapKey(Key.Tab);
        TapPad(GamepadButtons.Back);
        await WorldEdgeTest.PhysicsFrames(world, 2);
        AssertNotOpened(main, "with the debug camera");
        TapKey(Key.Escape);
        AssertThat(main.State).IsEqual(MainState.Paused);
        AssertBool(menu.InventoryButton.Disabled).OverrideFailureMessage("Inventory is enabled with the debug camera").IsTrue();
        AssertThat(menu.InventoryButton.FocusMode).IsEqual(Control.FocusModeEnum.None);
        AssertBool(menu.InventoryHint.Visible).OverrideFailureMessage("the inventory hint shows for the debug camera").IsFalse();
        AssertBool(main.OpenInventory()).IsFalse();
        TapKey(Key.Escape);
        AssertThat(main.State).IsEqual(MainState.Playing);
        AssertBool(world.ToggleDebugCamera()).IsTrue();
        AssertBool(world.CanOpenInventory).IsTrue();

        // Criterion 22: die with the crowbar equipped and two items held.
        state.AddMoney(120);
        AssertBool(state.Equip("crowbar")).IsTrue();
        world.FallTime = ShortFallTime;
        WorldEdgeTest.AssertEntered(await WorldEdgeTest.PlaceOnLedgeAndWaitForEntry(world, WorldEdgeTest.FindLedgeEntry(world)));
        AssertThat(world.Death.State).IsEqual(DeathState.Falling);
        AssertInt(state.Gear.Count).OverrideFailureMessage($"gear after the void entry: {string.Join(",", state.Gear)}").IsEqual(0);
        AssertInt(state.Equipped.Count).OverrideFailureMessage($"slots after the void entry: {string.Join(",", state.Equipped)}").IsEqual(0);
        AssertThat(state.EquippedIn(EquipSlot.Weapon)).IsNull();
        AssertThat(world.Death.Losses!.Equipped.GetValueOrDefault(EquipSlot.Weapon)).IsEqual("crowbar");

        // Criteria 14 and 16 while Falling.
        await AssertClosedWhileDying(main, world, "Falling");

        // The death screen: Tab before MinScreenTime (the World ignores every key until then).
        AssertBool(await WorldEdgeTest.WaitUntil(world, () => world.Death.State == DeathState.DeathScreen, WorldEdgeTest.FramesFor(ShortFallTime, Engine.PhysicsTicksPerSecond) + 10))
            .OverrideFailureMessage("the death screen didn't come").IsTrue();
        AssertFloat(world.Death.TimeInState).IsLess(world.MinScreenTime);
        AssertThat(world.DeathScreen.LossesText).IsEqual("Money lost: 120 · Gear lost: Crowbar (equipped), Baseball bat, Phone");
        await AssertClosedWhileDying(main, world, "the death screen");
        AssertThat(world.Death.State).IsEqual(DeathState.DeathScreen);

        // Dismiss, respawn: nothing given again, the screen shows no gear.
        AssertBool(await WorldEdgeTest.WaitUntil(world, () => world.Death.TimeInState >= world.MinScreenTime + 0.1, WorldEdgeTest.FramesFor(world.MinScreenTime + 0.2, Engine.PhysicsTicksPerSecond)))
            .IsTrue();

        // Criterion 14 after MinScreenTime: the inventory action is skipped by the dismiss check, like pause, so Tab
        // and the Back button neither dismiss the screen nor open the inventory.
        foreach (var (what, tap) in new (string, Action)[]
        {
            ("Tab", () => TapKey(Key.Tab)),
            ("the Back button", () => TapPad(GamepadButtons.Back)),
        })
        {
            tap();
            await WorldEdgeTest.PhysicsFrames(world, 2);
            AssertThat(world.Death.State)
                .OverrideFailureMessage($"{what} after MinScreenTime changed the death state to {world.Death.State}")
                .IsEqual(DeathState.DeathScreen);
            AssertNotOpened(main, $"{what} on the death screen after MinScreenTime");
        }

        AssertFloat(world.Death.TimeInState).IsGreaterEqual(world.MinScreenTime);
        TapKey(Key.Enter);
        AssertBool(await WorldEdgeTest.WaitUntil(world, () => world.Death.State == DeathState.Alive && world.CanOpenInventory, 30))
            .OverrideFailureMessage($"no respawn after the dismiss (state {world.Death.State})").IsTrue();
        await WorldEdgeTest.PhysicsFrames(world, 2);
        AssertInt(state.Gear.Count).OverrideFailureMessage($"--give was applied again on the respawn: {string.Join(",", state.Gear)}").IsEqual(0);
        AssertThat(string.Join(",", world.GivenItems)).IsEqual("crowbar,baseball_bat,phone");
        TapKey(Key.Tab);
        AssertOpenFromGame(main, world, "Tab after the respawn");
        AssertBool(screen.EmptyLabel.IsVisibleInTree()).OverrideFailureMessage("no 'You have no gear.' after the respawn").IsTrue();
        AssertThat(screen.EmptyLabel.Text).IsEqual("You have no gear.");
        AssertInt(screen.ItemRows.Count).IsEqual(0);
        AssertThat(screen.SlotRowOf(EquipSlot.Weapon).ItemLabel.Text).IsEqual("Empty");
        AssertThat(screen.SlotRowOf(EquipSlot.Outfit).ItemLabel.Text).IsEqual("Empty");
        TapKey(Key.Tab);
        AssertThat(main.State).IsEqual(MainState.Playing);
        await EndFrames(main);
    }

    /// <summary>
    /// Criterion 13's release case and its other argument errors, through the same resolver <c>Main</c> uses: in a
    /// release build (<see cref="DebugBuild.Override"/> false) a valid <c>--give</c> pushes one error and gives nothing;
    /// a blank entry and an empty list each push one error naming the known ids and give nothing.
    /// </summary>
    [TestCase]
    public void Ac13_GiveInAReleaseBuildOrWithABlankEntryOrAnEmptyList_PushesOneErrorAndGivesNothing()
    {
        foreach (var (what, debug, value) in new (string, bool, string)[]
        {
            ("release build", false, "crowbar,phone"),
            ("blank entry", true, "crowbar,,phone"),
            ("empty list", true, ""),
        })
        {
            DebugBuild.Override = debug;
            var capture = new ErrorCapture();
            OS.AddLogger(capture);
            IReadOnlyList<string> ids;
            try
            {
                ids = World.ResolveGiveArg(["--give", value], ItemCatalog.Default, "InventoryTest");
            }
            finally
            {
                OS.RemoveLogger(capture);
                DebugBuild.Override = null;
            }

            var errors = capture.ErrorsOnly;
            GD.Print($"InventoryTest: Ac13 {what}: {string.Join(" | ", errors)}");
            AssertInt(ids.Count).OverrideFailureMessage($"{what}: gave {string.Join(",", ids)}").IsEqual(0);
            AssertInt(errors.Count).OverrideFailureMessage($"{what}: {errors.Count} errors").IsEqual(1);
            AssertBool(errors[0].Contains("--give", StringComparison.Ordinal)).IsTrue();
            AssertBool(ItemCatalog.Default.All.All(item => errors[0].Contains(item.Id, StringComparison.Ordinal)))
                .OverrideFailureMessage($"{what}: the error doesn't list the known ids: {errors[0]}").IsTrue();
        }
    }

    // ----- Criteria 17 to 20: the screen -----

    /// <summary>
    /// Without gear the screen shows "You have no gear.", both slots "Empty" with Unequip disabled, and the focus on
    /// Close. With criterion 7's gear (crowbar and tracksuit equipped, baseball bat and phone held), added while it
    /// shows, it lists the slots with the equipped names, the items under Weapons, Clothing and Other in that order,
    /// each with its description in the muted style, "Equipped" on the equipped ones, Equip or Unequip on weapons and
    /// clothing only, Discard on every item, and an icon space of the theme's size on every row.
    /// </summary>
    [TestCase]
    public async Task Ac17_NoGearShowsTheEmptyText_Criterion7GearListsSlotsCategoriesDescriptionsTagsButtonsAndEqualIconSpaces()
    {
        var state = new PlayerState(ItemCatalog.Default);
        var screen = await OpenScreen(state, debug: false);

        AssertBool(screen.EmptyLabel.IsVisibleInTree()).IsTrue();
        AssertThat(screen.EmptyLabel.Text).IsEqual("You have no gear.");
        AssertThat(screen.Title.Text).IsEqual("Inventory");
        AssertInt(screen.ItemRows.Count).IsEqual(0);
        AssertInt(screen.CategoryHeadings.Count).IsEqual(0);
        AssertThat(string.Join("|", screen.SlotRows.Select(r => $"{r.SlotLabel.Text}:{r.ItemLabel.Text}:{r.UnequipButton.Disabled}")))
            .IsEqual("Weapon:Empty:True|Outfit:Empty:True");
        AssertThat(FocusOwner(screen)).IsSame(screen.CloseButton);

        GiveCriterion7Gear(state);
        await WorldEdgeTest.PhysicsFrames(screen, LayoutFrames);

        AssertBool(screen.EmptyLabel.Visible).OverrideFailureMessage("'You have no gear.' shows with gear").IsFalse();
        AssertThat(string.Join("|", screen.SlotRows.Select(r => $"{r.SlotLabel.Text}:{r.ItemLabel.Text}:{r.UnequipButton.Disabled}")))
            .IsEqual("Weapon:Crowbar:False|Outfit:Tracksuit:False");
        AssertThat(string.Join("|", screen.CategoryHeadings.Select(h => h.Text))).IsEqual("Weapons|Clothing|Other");
        var itemsBox = screen.ItemRows[0].Row.GetParent();
        AssertThat(string.Join("|", itemsBox.GetChildren().Select(c => c.Name.ToString())))
            .IsEqual("WeaponHeading|baseball_bat|crowbar|ClothingHeading|tracksuit|OtherHeading|phone");

        var expected = new (string Id, string Name, bool Equipped, string? EquipText)[]
        {
            ("baseball_bat", "Baseball bat", false, "Equip"),
            ("crowbar", "Crowbar", true, "Unequip"),
            ("tracksuit", "Tracksuit", true, "Unequip"),
            ("phone", "Phone", false, null),
        };
        AssertThat(string.Join(",", screen.ItemRows.Select(r => r.Item.Id))).IsEqual(string.Join(",", expected.Select(e => e.Id)));
        foreach (var (row, want) in screen.ItemRows.Zip(expected))
        {
            var id = want.Id;
            AssertThat(row.NameLabel.Text).IsEqual(want.Name);
            AssertThat(row.DescriptionLabel.Text).IsEqual(ItemCatalog.Default.Get(id).Description);
            AssertThat(row.DescriptionLabel.ThemeTypeVariation.ToString()).OverrideFailureMessage($"{id}: the description isn't muted").IsEqual(UiTheme.HintLabel);
            AssertBool(row.DescriptionLabel.IsVisibleInTree()).IsTrue();
            AssertBool(row.EquippedLabel.IsVisibleInTree()).OverrideFailureMessage($"{id}: 'Equipped' shown {row.EquippedLabel.IsVisibleInTree()}").IsEqual(want.Equipped);
            AssertThat(row.EquippedLabel.Text).IsEqual("Equipped");
            AssertThat(row.EquipButton?.Text ?? "(none)").OverrideFailureMessage($"{id}: equip button '{row.EquipButton?.Text}'").IsEqual(want.EquipText ?? "(none)");
            AssertThat(row.DiscardButton.Text).IsEqual("Discard");
            AssertBool(row.DiscardButton.IsVisibleInTree() && !row.DiscardButton.Disabled).IsTrue();
            var buttons = WorldEdgeTest.Descendants(row.Row).OfType<Button>().Select(b => b.Text).ToList();
            AssertThat(string.Join("|", buttons)).OverrideFailureMessage($"{id}: buttons {string.Join("|", buttons)}")
                .IsEqual(want.EquipText is null ? "Discard" : $"{want.EquipText}|Discard");
            AssertThat(row.Icon.Size).OverrideFailureMessage($"{id}: icon space {row.Icon.Size}").IsEqual(new Vector2(UiTheme.ItemIconSize, UiTheme.ItemIconSize));
            AssertBool(row.Icon.IsVisibleInTree()).IsTrue();
        }

        // Added while it showed, the focus stayed on Close (the same control); opened again it starts on the first item's first button.
        AssertThat(FocusOwner(screen)).OverrideFailureMessage($"after a live change the focus is on {NameOf(FocusOwner(screen))}").IsSame(screen.CloseButton);
        screen.Open(fromPauseMenu: false);
        AssertThat(FocusOwner(screen)).OverrideFailureMessage($"at open the focus is on {NameOf(FocusOwner(screen))}").IsSame(screen.EquipButtonOf("baseball_bat"));
        await EndFrames(screen);
    }

    /// <summary>
    /// With criterion 7's gear: Equip on the baseball bat by <c>ui_accept</c> (Enter) replaces the crowbar in the weapon
    /// slot (the crowbar stays held) and the rows update at once with the focus on the same button; Enter again
    /// unequips it. A mouse click on the outfit slot's Unequip empties it; a click on the crowbar's Equip equips it.
    /// Discard by mouse asks "Discard Crowbar? It will be gone for good."; Cancel keeps it with the focus back on its
    /// Discard. Discard by Enter on the equipped crowbar, confirmed, unequips and removes it, and the focus goes to the
    /// next row's first button; discarding the last row moves it to the previous row's; discarding the only row to
    /// Close.
    /// </summary>
    [TestCase]
    public async Task Ac18_EquipUnequipDiscard_ByAcceptAndByMouseClick_ChangeStateUpdateRowsAndMoveFocus_DiscardAsksFirst()
    {
        var state = new PlayerState(ItemCatalog.Default);
        GiveCriterion7Gear(state);
        var screen = await OpenScreen(state, debug: false);
        var dialog = screen.Dialog!;

        // Equip by ui_accept: replaces the crowbar.
        AssertThat(FocusOwner(screen)).IsSame(screen.EquipButtonOf("baseball_bat"));
        TapKey(Key.Enter);
        AssertThat(state.EquippedIn(EquipSlot.Weapon)).IsEqual("baseball_bat");
        AssertBool(state.Gear.Contains("crowbar")).IsTrue();
        AssertThat(screen.SlotRowOf(EquipSlot.Weapon).ItemLabel.Text).IsEqual("Baseball bat");
        AssertThat(screen.EquipButtonOf("baseball_bat")!.Text).IsEqual("Unequip");
        AssertBool(screen.RowOf("baseball_bat")!.EquippedLabel.Visible).IsTrue();
        AssertThat(screen.EquipButtonOf("crowbar")!.Text).IsEqual("Equip");
        AssertBool(screen.RowOf("crowbar")!.EquippedLabel.Visible).IsFalse();
        AssertThat(FocusOwner(screen)).OverrideFailureMessage($"after Equip the focus is on {NameOf(FocusOwner(screen))}").IsSame(screen.EquipButtonOf("baseball_bat"));

        // Unequip by ui_accept on the same row.
        TapKey(Key.Enter);
        AssertThat(state.EquippedIn(EquipSlot.Weapon)).IsNull();
        AssertThat(screen.SlotRowOf(EquipSlot.Weapon).ItemLabel.Text).IsEqual("Empty");
        AssertBool(screen.SlotRowOf(EquipSlot.Weapon).UnequipButton.Disabled).IsTrue();
        AssertThat(screen.EquipButtonOf("baseball_bat")!.Text).IsEqual("Equip");
        AssertThat(FocusOwner(screen)).OverrideFailureMessage($"after Unequip the focus is on {NameOf(FocusOwner(screen))}").IsSame(screen.EquipButtonOf("baseball_bat"));

        // Mouse: the outfit slot's Unequip, then the crowbar's Equip.
        await MouseClick(screen, screen.SlotRowOf(EquipSlot.Outfit).UnequipButton);
        AssertThat(state.EquippedIn(EquipSlot.Outfit)).OverrideFailureMessage("a click on the outfit's Unequip did nothing").IsNull();
        AssertThat(screen.SlotRowOf(EquipSlot.Outfit).ItemLabel.Text).IsEqual("Empty");
        AssertThat(screen.EquipButtonOf("tracksuit")!.Text).IsEqual("Equip");
        AssertBool(state.Gear.Contains("tracksuit")).IsTrue();
        await MouseClick(screen, screen.EquipButtonOf("crowbar")!);
        AssertThat(state.EquippedIn(EquipSlot.Weapon)).OverrideFailureMessage("a click on the crowbar's Equip did nothing").IsEqual("crowbar");
        AssertThat(screen.SlotRowOf(EquipSlot.Weapon).ItemLabel.Text).IsEqual("Crowbar");

        // Discard by mouse, Cancel keeps it.
        await MouseClick(screen, screen.DiscardButtonOf("crowbar")!);
        AssertBool(dialog.IsOpen).OverrideFailureMessage("Discard didn't ask").IsTrue();
        AssertThat(dialog.MessageText).IsEqual("Discard Crowbar? It will be gone for good.");
        AssertThat(string.Join("|", dialog.Buttons.Select(b => b.Text))).IsEqual("Discard|Cancel");
        AssertBool(state.Gear.Contains("crowbar")).IsTrue();
        Click(dialog.ButtonByText("Cancel")!);
        AssertBool(dialog.IsOpen).IsFalse();
        AssertBool(state.Gear.Contains("crowbar")).OverrideFailureMessage("Cancel discarded the item").IsTrue();
        AssertThat(state.EquippedIn(EquipSlot.Weapon)).IsEqual("crowbar");
        AssertThat(FocusOwner(screen)).OverrideFailureMessage($"after Cancel the focus is on {NameOf(FocusOwner(screen))}").IsSame(screen.DiscardButtonOf("crowbar"));

        // Discard by ui_accept, confirmed, on the equipped crowbar: the next row (tracksuit) gets the focus.
        TapKey(Key.Enter);
        AssertBool(dialog.IsOpen).IsTrue();
        AssertThat(FocusOwner(screen)).IsSame(dialog.ButtonByText("Discard"));
        TapKey(Key.Enter);
        AssertBool(dialog.IsOpen).IsFalse();
        AssertBool(state.Gear.Contains("crowbar")).OverrideFailureMessage("confirming didn't discard").IsFalse();
        AssertThat(state.EquippedIn(EquipSlot.Weapon)).IsNull();
        AssertThat(screen.RowOf("crowbar")).IsNull();
        AssertThat(screen.SlotRowOf(EquipSlot.Weapon).ItemLabel.Text).IsEqual("Empty");
        AssertThat(string.Join(",", screen.ItemRows.Select(r => r.Item.Id))).IsEqual("baseball_bat,tracksuit,phone");
        // The focus checks after a discard are collected, so one wrong focus doesn't hide the rest of the test.
        var focusProblems = new List<string>();
        void ExpectFocus(string after, Control? expected)
        {
            if (FocusOwner(screen) != expected)
            {
                focusProblems.Add($"after {after} the focus is on {NameOf(FocusOwner(screen))}, expected {NameOf(expected)}");
            }
        }

        ExpectFocus("discarding the crowbar (rows baseball_bat, crowbar, tracksuit, phone)", screen.EquipButtonOf("tracksuit"));

        // The last row (phone): the previous row's first button.
        await ConfirmDiscard(screen, "phone");
        ExpectFocus("discarding the last row (phone)", screen.EquipButtonOf("tracksuit"));

        // Then the rest; the only row left: the first enabled control (Close, no debug section).
        await ConfirmDiscard(screen, "tracksuit");
        ExpectFocus("discarding tracksuit (rows baseball_bat, tracksuit)", screen.EquipButtonOf("baseball_bat"));
        await ConfirmDiscard(screen, "baseball_bat");
        AssertInt(state.Gear.Count).IsEqual(0);
        AssertBool(screen.EmptyLabel.IsVisibleInTree()).IsTrue();
        ExpectFocus("discarding the only row", screen.CloseButton);
        await EndFrames(screen);
        AssertThat(string.Join("; ", focusProblems)).OverrideFailureMessage($"focus after Discard: {string.Join("; ", focusProblems)}").IsEqual("");
    }

    /// <summary>
    /// The Debug section exists only when <see cref="DebugBuild.IsDebug"/> (checked with <see cref="DebugBuild.Override"/>),
    /// with one row per catalogue item in <see cref="ItemCatalog.All"/> order. Add and Remove change the state and the
    /// rows at once; Add is disabled while held, Remove while not held; Remove of an equipped item unequips it too.
    /// </summary>
    [TestCase]
    public async Task Ac19_DebugSection_OnlyInADebugBuild_OneRowPerItem_AddAndRemoveChangeStateAndRowsAtOnce_DisabledByHeld()
    {
        var release = await OpenScreen(new PlayerState(ItemCatalog.Default), debug: false);
        AssertThat(release.DebugSection).OverrideFailureMessage("a Debug section in a release build").IsNull();
        AssertInt(release.DebugRows.Count).IsEqual(0);
        AssertInt(WorldEdgeTest.Descendants(release).OfType<Button>().Count(b => b.Text is "Add" or "Remove")).IsEqual(0);
        CloseScreen();

        var state = new PlayerState(ItemCatalog.Default);
        var screen = await OpenScreen(state, debug: true);
        AssertThat(screen.DebugSection).IsNotNull();
        AssertBool(screen.DebugSection!.IsVisibleInTree()).IsTrue();
        AssertThat(string.Join(",", screen.DebugRows.Select(r => r.Item.Id))).IsEqual(string.Join(",", ItemCatalog.Default.All.Select(i => i.Id)));
        foreach (var row in screen.DebugRows)
        {
            AssertThat(row.NameLabel.Text).IsEqual(row.Item.DisplayName);
            AssertThat(row.CategoryLabel.Text).IsEqual(InventoryScreen.CategoryName(row.Item.Category));
            AssertBool(row.AddButton.Disabled).OverrideFailureMessage($"{row.Item.Id}: Add disabled while not held").IsFalse();
            AssertBool(row.RemoveButton.Disabled).OverrideFailureMessage($"{row.Item.Id}: Remove enabled while not held").IsTrue();
        }

        // Focus starts on the first debug button when there is no gear.
        AssertThat(FocusOwner(screen)).IsSame(screen.DebugRows[0].AddButton);

        var hoodie = screen.DebugRowOf("hoodie")!;
        Click(hoodie.AddButton);
        AssertBool(state.Gear.Contains("hoodie")).IsTrue();
        AssertThat(screen.RowOf("hoodie")).OverrideFailureMessage("Add didn't add the item row").IsNotNull();
        AssertBool(screen.EmptyLabel.Visible).IsFalse();
        AssertBool(hoodie.AddButton.Disabled).OverrideFailureMessage("Add enabled while held").IsTrue();
        AssertBool(hoodie.RemoveButton.Disabled).OverrideFailureMessage("Remove disabled while held").IsFalse();

        Click(screen.EquipButtonOf("hoodie")!);
        AssertThat(state.EquippedIn(EquipSlot.Outfit)).IsEqual("hoodie");
        Click(hoodie.RemoveButton);
        AssertBool(state.Gear.Contains("hoodie")).OverrideFailureMessage("Remove didn't remove").IsFalse();
        AssertThat(state.EquippedIn(EquipSlot.Outfit)).IsNull();
        AssertThat(screen.RowOf("hoodie")).IsNull();
        AssertThat(screen.SlotRowOf(EquipSlot.Outfit).ItemLabel.Text).IsEqual("Empty");
        AssertBool(hoodie.AddButton.Disabled).IsFalse();
        AssertBool(hoodie.RemoveButton.Disabled).IsTrue();
        AssertBool(screen.EmptyLabel.IsVisibleInTree()).IsTrue();
        await EndFrames(screen);
    }

    /// <summary>
    /// Keyboard alone, then the gamepad's d-pad alone: in a debug build with every item held and two equipped, the
    /// focus starts on the first item's first button and every enabled control (both slot Unequips, every item
    /// button, every enabled debug button, Close) is reachable, nothing else; the discard dialog's Discard and Cancel
    /// likewise. With more rows than fit, moving the focus to a row below the visible part scrolls it into view.
    /// </summary>
    [TestCase]
    public async Task Ac20_KeyboardAloneAndGamepadAlone_ReachEveryEnabledControlAndTheDialog_FocusStartsFirst_FocusedRowScrolledIntoView()
    {
        var state = new PlayerState(ItemCatalog.Default);
        foreach (var item in ItemCatalog.Default.All)
        {
            state.AddGear(item.Id);
        }

        state.Equip("crowbar");
        state.Equip("tracksuit");
        var screen = await OpenScreen(state, debug: true);
        var dialog = screen.Dialog!;
        IEnumerable<Control> Controls() =>
            screen.SlotRows.Select(r => (Control)r.UnequipButton)
                .Concat(screen.ItemRows.SelectMany(r => r.Buttons))
                .Concat(screen.DebugRows.SelectMany(r => new Control[] { r.AddButton, r.RemoveButton }))
                .Append(screen.CloseButton);

        foreach (var nav in AllNavs)
        {
            screen.Open(fromPauseMenu: false);
            await WorldEdgeTest.PhysicsFrames(screen, LayoutFrames);
            var first = screen.ItemRows[0].Buttons[0];
            AssertFocusAndReach(screen, nav, "inventory screen", first, Controls());

            screen.DiscardButtonOf("phone")!.GrabFocus();
            nav.Accept();
            AssertBool(dialog.IsOpen).OverrideFailureMessage($"{nav}: accept on Discard didn't ask").IsTrue();
            AssertFocusAndReach(screen, nav, "discard dialog", dialog.ButtonByText("Discard")!, dialog.Buttons);
            dialog.ButtonByText("Cancel")!.GrabFocus();
            nav.Accept();
            AssertBool(dialog.IsOpen).IsFalse();
            AssertBool(state.Gear.Contains("phone")).IsTrue();
        }

        // Scrolling: the content is taller than the scroll area, the top is shown at open.
        screen.Open(fromPauseMenu: false);
        await WorldEdgeTest.PhysicsFrames(screen, LayoutFrames);
        var scroll = screen.Scroll;
        var view = scroll.GetGlobalRect();
        var lowest = screen.DebugRows[^1].RemoveButton;
        GD.Print(WorldEdgeTest.Invariant($"InventoryTest: Ac20 scroll area {view}, lowest debug button {lowest.GetGlobalRect()}"));
        AssertInt(scroll.ScrollVertical).IsEqual(0);
        AssertFloat(lowest.GetGlobalRect().End.Y).OverrideFailureMessage("the rows fit without scrolling; the scroll check needs more rows than fit").IsGreater(view.End.Y);

        // Up from Close goes to the last row (wrapping grid), below the visible part.
        screen.CloseButton.GrabFocus();
        Keyboard.Up();
        await WorldEdgeTest.PhysicsFrames(screen, LayoutFrames);
        var focused = FocusOwner(screen)!;
        var rect = focused.GetGlobalRect();
        view = scroll.GetGlobalRect();
        GD.Print(WorldEdgeTest.Invariant($"InventoryTest: Ac20 focus on {NameOf(focused)} at {rect}, scroll {scroll.ScrollVertical}, area {view}"));
        AssertBool(scroll.IsAncestorOf(focused)).OverrideFailureMessage($"Up from Close went to {NameOf(focused)}").IsTrue();
        AssertInt(scroll.ScrollVertical).OverrideFailureMessage("the scroll didn't move").IsGreater(0);
        AssertBool(rect.Position.Y >= view.Position.Y - 0.5f && rect.End.Y <= view.End.Y + 0.5f)
            .OverrideFailureMessage($"{NameOf(focused)} at {rect} isn't inside the scroll area {view}").IsTrue();
        await EndFrames(screen);
    }

    // ===== Helpers =====

    /// <summary>Lets two process frames end, so rows the screen rebuilt (queued for deletion) are freed before the orphan count.</summary>
    private static async Task EndFrames(Node node)
    {
        for (var i = 0; i < 2; i++)
        {
            await node.ToSignal(node.GetTree(), SceneTree.SignalName.ProcessFrame);
        }
    }

    private static void GiveCriterion7Gear(PlayerState state)
    {
        foreach (var id in new[] { "crowbar", "tracksuit", "baseball_bat", "phone" })
        {
            state.AddGear(id);
        }

        state.Equip("crowbar");
        state.Equip("tracksuit");
    }

    /// <summary>A screen alone on <paramref name="state"/>, built with <see cref="DebugBuild.Override"/> = <paramref name="debug"/>, opened from the game.</summary>
    private async Task<InventoryScreen> OpenScreen(PlayerState state, bool debug)
    {
        DebugBuild.Override = debug;
        var screen = new InventoryScreen { Name = "InventoryScreen", Player = state };
        _screen = screen;
        _screenRunner = ISceneRunner.Load(screen, true);
        await WorldEdgeTest.PhysicsFrames(screen, 1);
        screen.Open(fromPauseMenu: false);
        await WorldEdgeTest.PhysicsFrames(screen, LayoutFrames);
        AssertBool(screen.IsVisibleInTree()).IsTrue();
        return screen;
    }

    private void CloseScreen()
    {
        _screenRunner?.Dispose();
        _screenRunner = null;
        if (_screen is not null && GodotObject.IsInstanceValid(_screen))
        {
            _screen.GetParent()?.RemoveChild(_screen);
            _screen.Free();
        }

        _screen = null;
    }

    /// <summary>Discard on <paramref name="itemId"/> by Enter, then Enter on the dialog's Discard.</summary>
    private static async Task ConfirmDiscard(InventoryScreen screen, string itemId)
    {
        screen.DiscardButtonOf(itemId)!.GrabFocus();
        TapKey(Key.Enter);
        AssertBool(screen.Dialog!.IsOpen).IsTrue();
        AssertThat(FocusOwner(screen)).IsSame(screen.Dialog.ButtonByText("Discard"));
        TapKey(Key.Enter);
        AssertBool(screen.Dialog.IsOpen).IsFalse();
        AssertThat(screen.RowOf(itemId)).OverrideFailureMessage($"{itemId} wasn't discarded").IsNull();
        await WorldEdgeTest.PhysicsFrames(screen, 1);
    }

    /// <summary>A left click at the centre of <paramref name="control"/>, as the window delivers it.</summary>
    private static async Task MouseClick(Node node, Control control)
    {
        await WorldEdgeTest.PhysicsFrames(node, 1);
        var at = control.GetViewport().GetFinalTransform() * control.GetGlobalRect().GetCenter();
        Inject(new InputEventMouseMotion { Position = at, GlobalPosition = at });
        Inject(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, Position = at, GlobalPosition = at, ButtonMask = MouseButtonMask.Left });
        Inject(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false, Position = at, GlobalPosition = at });
    }

    /// <summary>Criterion 14: the screen is open from the game, paused, with the mouse released and the street label hidden.</summary>
    private static void AssertOpenFromGame(Main main, World world, string what)
    {
        AssertThat(main.State).OverrideFailureMessage($"{what}: didn't open the inventory (state {main.State})").IsEqual(MainState.Inventory);
        AssertBool(main.InventoryScreen.IsVisibleInTree()).IsTrue();
        AssertBool(main.InventoryScreen.OpenedFromPauseMenu).IsFalse();
        AssertBool(main.PauseMenu.Visible).IsFalse();
        AssertBool(main.GetTree().Paused).OverrideFailureMessage($"{what}: the tree isn't paused").IsTrue();
        AssertThat(Input.MouseMode).IsEqual(Input.MouseModeEnum.Visible);
        AssertBool(world.PlayerCamera!.MouseCaptured).IsFalse();
        AssertBool(world.PlayerCamera.WantsCapture).OverrideFailureMessage($"{what}: opening forgot that the camera wanted the capture").IsTrue();
        AssertBool(world.Hud.Visible).OverrideFailureMessage($"{what}: the street label shows through the inventory").IsFalse();
        AssertThat(FocusOwner(main)).OverrideFailureMessage($"{what}: the focus starts on {NameOf(FocusOwner(main))}").IsSame(main.InventoryScreen.FirstFocusTarget());
    }

    private static void AssertNotOpened(Main main, string when)
    {
        AssertBool(main.InventoryScreen.Visible).OverrideFailureMessage($"{when}: the inventory opened").IsFalse();
        AssertThat(main.State).OverrideFailureMessage($"{when}: state {main.State}").IsEqual(MainState.Playing);
        AssertBool(main.GetTree().Paused).OverrideFailureMessage($"{when}: the tree is paused").IsFalse();
    }

    /// <summary>
    /// Criteria 14 and 16 outside <c>Alive</c>: Tab and the Back button do nothing (the death state is kept), and the
    /// pause menu's Inventory is disabled, not focusable, with the hint.
    /// </summary>
    private static async Task AssertClosedWhileDying(Main main, World world, string when)
    {
        var deathState = world.Death.State;
        AssertBool(world.CanOpenInventory).IsFalse();
        TapKey(Key.Tab);
        TapPad(GamepadButtons.Back);
        await WorldEdgeTest.PhysicsFrames(world, 2);
        AssertNotOpened(main, when);
        AssertThat(world.Death.State).OverrideFailureMessage($"{when}: Tab or the Back button changed the death state").IsEqual(deathState);
        TapKey(Key.Escape);
        AssertThat(main.State).IsEqual(MainState.Paused);
        var menu = main.PauseMenu;
        AssertBool(menu.InventoryButton.Disabled).OverrideFailureMessage($"{when}: Inventory is enabled").IsTrue();
        AssertThat(menu.InventoryButton.FocusMode).IsEqual(Control.FocusModeEnum.None);
        AssertBool(menu.InventoryHint.IsVisibleInTree()).OverrideFailureMessage($"{when}: no inventory hint").IsTrue();
        AssertThat(menu.InventoryHint.Text).IsEqual("You can use the inventory when you're not falling off the world");
        AssertBool(main.OpenInventory()).IsFalse();
        TapKey(Key.Escape);
        AssertThat(main.State).IsEqual(MainState.Playing);
        AssertThat(world.Death.State).IsEqual(deathState);
    }

    /// <summary>
    /// Criterion 15: closes the screen opened from the game with a press held over a few paused physics frames, then
    /// checks the resume and watches the standing player and the camera for <see cref="AfterCloseFrames"/> frames.
    /// </summary>
    private async Task CloseAndAssertResumed(World world, string what, Action press, Action release, int holdPhysicsFrames = HoldFrames)
    {
        var main = _main!;
        var player = world.Player!;
        var camera = world.PlayerCamera!;
        AssertThat(main.State).IsEqual(MainState.Inventory);
        var feetY = player.GlobalPosition.Y;
        var yaw = camera.Yaw;
        var pitch = camera.Pitch;
        var runOn = player.RunToggle.On;
        await PressThenRelease(main, press, release, holdPhysicsFrames);
        AssertThat(main.State).OverrideFailureMessage($"{what}: didn't close the inventory (state {main.State})").IsEqual(MainState.Playing);
        AssertBool(main.GetTree().Paused).IsFalse();
        AssertBool(main.InventoryScreen.Visible).IsFalse();
        AssertBool(main.PauseMenu.Visible).IsFalse();
        AssertThat(FocusOwner(main)).OverrideFailureMessage($"{what}: {NameOf(FocusOwner(main))} kept the focus after the close").IsNull();
        AssertBool(camera.WantsCapture).OverrideFailureMessage($"{what}: the camera doesn't want the capture after the close").IsTrue();
        AssertBool(world.Hud.Visible).OverrideFailureMessage($"{what}: the street label stayed hidden").IsTrue();

        var maxUp = 0f;
        var maxRise = 0f;
        await WorldEdgeTest.Drive(world, _ =>
        {
            maxUp = Mathf.Max(maxUp, player.Velocity.Y);
            maxRise = Mathf.Max(maxRise, player.GlobalPosition.Y - feetY);
            return false;
        }, AfterCloseFrames);
        GD.Print(WorldEdgeTest.Invariant($"InventoryTest: Ac15 close by {what}: max vertical speed {maxUp} m/s, max rise {maxRise} m"));
        AssertFloat(maxUp).OverrideFailureMessage($"{what}: the player jumped (vertical speed {maxUp} m/s)").IsLess(0.5f);
        AssertFloat(maxRise).OverrideFailureMessage($"{what}: the player rose {maxRise} m").IsLessEqual(StandingRise);
        AssertFloat(camera.Yaw).OverrideFailureMessage($"{what}: the camera turned").IsEqual(yaw);
        AssertFloat(camera.Pitch).OverrideFailureMessage($"{what}: the camera pitched").IsEqual(pitch);
        AssertBool(player.RunToggle.On).OverrideFailureMessage($"{what}: running was toggled").IsEqual(runOn);
    }
}
