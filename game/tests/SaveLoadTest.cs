using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using GdUnit4;
using Godot;
using Roadman.Core.Edge;
using Roadman.Core.Maps;
using Roadman.Core.Player;
using Roadman.Core.Saves;
using Roadman.OnFoot;
using Roadman.Scenes;
using Roadman.Ui;
using static GdUnit4.Assertions;
using static Roadman.Tests.MainTestSupport;
using File = System.IO.File;

namespace Roadman.Tests;

/// <summary>
/// Saving and loading through <c>Main</c>, Part A of <c>docs/specs/menu-settings-save.md</c> (<c>Ac</c> numbers are
/// its acceptance criteria): a save and load in one session (14), loading from the main menu with Continue and Load
/// (15), the placement check and a map with no pack (16), the slot list in save mode, a failing write and Delete (17),
/// the autosave on quitting, also during a death, and the window's close request (18), and the version in a save (23).
/// Saves are written to a temporary folder (<see cref="MainTestSupport"/>); positions come from the pack every suite
/// uses, as in <see cref="WorldEdgeTest"/>.
/// </summary>
[TestSuite]
[RequireGodotRuntime]
public class SaveLoadTest
{
    /// <summary>Criteria 14 to 16: the spec's x/z tolerance of a loaded position, m.</summary>
    private const float PositionTolerance = 0.05f;

    /// <summary>Criterion 14: the spec's facing tolerance, rad.</summary>
    private const float YawTolerance = 0.01f;

    /// <summary>The largest gap between the settled player's feet and the ground or road under it, m.</summary>
    private const float GroundTolerance = 0.1f;

    /// <summary>Criterion 14: physics frames the player walks forward after saving.</summary>
    private const int WalkFrames = 60;

    /// <summary>A shortened fall, so the death screen comes sooner.</summary>
    private const float ShortFallTime = 1f;

    /// <summary>Criterion 16: half the edge of the blocking box at the saved spot, m.</summary>
    private const float BlockerHalf = 1.5f;

    /// <summary>The bridge-deck test: the deck's top above the ground at the spawn point, m.</summary>
    private const float DeckHeight = 6f;

    /// <summary>The bridge-deck test: the deck's width and length, m.</summary>
    private const float DeckSize = 8f;

    /// <summary>The bridge-deck test: the deck's thickness, m.</summary>
    private const float DeckThickness = 0.5f;

    /// <summary>The bridge-deck test: the largest height gap between the player loaded under the deck and the ground, m.</summary>
    private const float UnderDeckTolerance = 0.5f;

    private string _folder = "";
    private Main? _main;
    private Node? _blocker;

    [BeforeTest]
    public void SetUp() => _folder = BeginTest("save-load");

    [AfterTest]
    public void CleanUp()
    {
        TearDown(_folder, _blocker, _main);
        _main = null;
        _blocker = null;
        _folder = "";
    }

    /// <summary>What a <c>World</c> of <c>Main</c> reported while it loaded a save.</summary>
    private sealed class LoadWatch
    {
        public List<(Vector3 Position, bool Moved)> Loaded { get; } = [];

        public int Spawned { get; set; }

        public int Respawned { get; set; }

        public double? PlayTimeAtStart { get; set; }

        public int Started { get; set; }
    }

    private static (LoadWatch Watch, Action Disconnect) Watch(Main main)
    {
        var watch = new LoadWatch();
        var disconnect = OnEachWorld(main, world =>
        {
            world.PlayerLoaded += (p, moved) => watch.Loaded.Add((p, moved));
            world.PlayerSpawned += _ => watch.Spawned++;
            world.PlayerRespawned += _ => watch.Respawned++;
        });
        void OnStarted()
        {
            watch.Started++;
            watch.PlayTimeAtStart = main.PlayTime.Seconds;
        }

        main.GameStarted += OnStarted;
        return (watch, () =>
        {
            disconnect();
            if (GodotObject.IsInstanceValid(main))
            {
                main.GameStarted -= OnStarted;
            }
        }
        );
    }

    // ----- Criterion 14: save and load in one session -----

    /// <summary>
    /// Saves to slot 2 at the settled spawn point with money 120, gear <c>crowbar</c> and <c>phone</c> and wanted level 2 (an empty
    /// slot: written without asking; the save stores the game version, criterion 23), then walks, spends, breaks a
    /// barrier and dies. Loading slot 2 from the pause menu on the death screen asks first (Cancel keeps the game);
    /// after Load the player is within 0.05 m (x/z) of the saved spot on the ground, facing the saved yaw (± 0.01 rad),
    /// with the saved money, gear and wanted level, play time from the save, the death sequence <c>Alive</c>, the death
    /// screen hidden, every barrier intact, the player camera current and the street label on the player, and
    /// <c>PlayerLoaded</c> fired once with <c>moved</c> false (no spawn).
    /// </summary>
    [TestCase]
    public async Task Ac14_Ac23_SaveToSlot2_ThenMoveSpendAndDie_LoadingSlot2RestoresPositionFacingStateAndPlayTime()
    {
        WorldEdgeTest.AssertPackPresent();
        var state = WorldEdgeTest.PlayerStateOfGame();
        using var runner = ISceneRunner.Load(_main = NewMain(_folder, "--new-game"), true);
        var world = await WaitForGame(_main);
        await WaitForEdge(world);
        await Settle(world);
        state.AddMoney(120);
        state.AddGear("crowbar");
        state.AddGear("phone");
        state.SetWantedLevel(2);
        await WorldEdgeTest.PhysicsFrames(world, 2);

        var saved = SaveThroughTheMenu(_main, SlotId.Manual(2), expectAsk: false);
        var player = world.Player!;
        AssertThat(saved.MapId).IsEqual("somero");
        AssertThat(saved.GameVersion).IsEqual("dev");
        AssertThat(GameVersionInFile(SlotPath(_folder, SlotId.Manual(2)))).IsEqual("dev");
        AssertInt(saved.Player.Money).IsEqual(120);
        AssertThat(string.Join(",", saved.Player.Gear)).IsEqual("crowbar,phone");
        AssertInt(saved.Player.WantedLevel).IsEqual(2);
        AssertThat(saved.Player.Position).IsNotNull();
        AssertFloat((float)saved.Player.Position!.Value.X).IsEqualApprox(player.GlobalPosition.X, 1e-3f);
        AssertFloat((float)saved.Player.Position!.Value.Z).IsEqualApprox(player.GlobalPosition.Z, 1e-3f);
        AssertFloat((float)saved.Player.FacingYaw!.Value).IsEqualApprox(player.FacingYaw, 1e-5f);
        AssertFloat(saved.PlaySeconds).IsEqualApprox(_main.PlayTime.Seconds, 1e-6);
        AssertFloat(saved.PlaySeconds).IsGreater(0d);
        Click(_main.SlotList.BackButton);
        Click(_main.PauseMenu.ResumeButton);

        // Move, spend, break a barrier, die.
        await WalkForward(world, WalkFrames);
        AssertBool(state.TrySpend(100)).IsTrue();
        var barriers = world.MapLoader.Barriers!;
        AssertBool(barriers.Break(barriers.Items[0].ItemId, new Vector3(0f, 0f, 5f))).IsTrue();
        AssertInt(barriers.BrokenItemIds.Count).IsEqual(1);
        world.FallTime = ShortFallTime;
        await EnterVoid(world);
        AssertBool(await WorldEdgeTest.WaitUntil(world, () => world.Death.State == DeathState.DeathScreen, WorldEdgeTest.FramesFor(ShortFallTime, Engine.PhysicsTicksPerSecond) + 10)).IsTrue();
        AssertInt(state.Money).IsEqual(0);

        // Load slot 2 from the pause menu: it asks first.
        var (watch, disconnect) = Watch(_main);
        try
        {
            TapKey(Key.Escape);
            AssertThat(_main.State).IsEqual(MainState.Paused);
            Click(_main.PauseMenu.LoadButton);
            AssertThat(_main.SlotList.Mode).IsEqual(SlotListMode.Load);
            _main.SlotList.Choose(SlotId.Manual(2));
            AssertBool(_main.Dialog.IsOpen).OverrideFailureMessage("loading over a running game didn't ask").IsTrue();
            AssertThat(_main.Dialog.MessageText).IsEqual("Load slot 2? Progress since your last save will be lost.");
            AssertThat(string.Join("|", _main.Dialog.Buttons.Select(b => b.Text))).IsEqual("Load|Cancel");
            ClickDialog(_main, "Cancel");
            AssertThat(_main.World).IsSame(world);
            AssertThat(_main.State).IsEqual(MainState.Paused);
            _main.SlotList.Choose(SlotId.Manual(2));
            ClickDialog(_main, "Load");

            var loaded = await WaitForNewGame(_main, world);
            await AssertLoadedFrom(_main, loaded, saved, watch, "Ac14");
            AssertBool(WorldEdgeTest.IsFreed(world)).OverrideFailureMessage("the previous World wasn't freed").IsTrue();
        }
        finally
        {
            disconnect();
        }
    }

    // ----- Criterion 15: Continue and Load from the main menu -----

    /// <summary>
    /// One game saves slot 2, then walks on, changes its state and writes the autosave (newer). In a fresh
    /// <c>Main</c>, Continue loads the autosave (the most recent valid slot); in another fresh <c>Main</c>, Load → slot 2
    /// (no confirmation on the main menu) loads slot 2. Each as in criterion 14.
    /// </summary>
    [TestCase]
    public async Task Ac15_FreshMain_ContinueLoadsTheMostRecentSaveIncludingTheAutosave_AndLoadLoadsTheChosenSlot()
    {
        WorldEdgeTest.AssertPackPresent();
        var state = WorldEdgeTest.PlayerStateOfGame();
        SaveGame slot2;
        SaveGame autosave;
        using (var runner = ISceneRunner.Load(_main = NewMain(_folder, "--new-game"), true))
        {
            var world = await WaitForGame(_main);
            await Settle(world);
            state.AddMoney(120);
            state.AddGear("crowbar");
            state.AddGear("phone");
            state.SetWantedLevel(2);
            await WorldEdgeTest.PhysicsFrames(world, 2);
            slot2 = SaveThroughTheMenu(_main, SlotId.Manual(2), expectAsk: false);
            Click(_main.SlotList.BackButton);
            Click(_main.PauseMenu.ResumeButton);

            await WalkForward(world, WalkFrames);
            await Settle(world);
            state.AddMoney(30);
            state.RemoveGear("crowbar");
            state.SetWantedLevel(1);
            await WorldEdgeTest.PhysicsFrames(world, 2);
            AssertBool(_main.TryAutosave()).OverrideFailureMessage($"autosave failed: {_main.LastAutosaveError}").IsTrue();
            autosave = ReadSlot(_folder, SlotId.Autosave).Save!;
            AssertThat(autosave).IsNotNull();
        }

        _main = null;
        AssertBool(autosave.SavedAtUtc > slot2.SavedAtUtc).OverrideFailureMessage("the autosave isn't newer than slot 2").IsTrue();
        AssertInt(autosave.Player.Money).IsEqual(150);
        var moved = new Vector2((float)(autosave.Player.Position!.Value.X - slot2.Player.Position!.Value.X), (float)(autosave.Player.Position.Value.Z - slot2.Player.Position.Value.Z)).Length();
        AssertFloat(moved).OverrideFailureMessage("the autosave and slot 2 are at the same spot").IsGreater(0.5f);

        // Continue in a fresh Main.
        state.Reset();
        using (var runner = ISceneRunner.Load(_main = NewMain(_folder), true))
        {
            await WorldEdgeTest.PhysicsFrames(_main, 3);
            AssertBool(_main.MainMenu.ContinueButton.Disabled).IsFalse();
            var (watch, disconnect) = Watch(_main);
            try
            {
                Click(_main.MainMenu.ContinueButton);
                var world = await WaitForGame(_main);
                await AssertLoadedFrom(_main, world, autosave, watch, "Ac15 Continue");
            }
            finally
            {
                disconnect();
            }
        }

        _main = null;

        // Load → slot 2 in another fresh Main.
        state.Reset();
        using (var runner = ISceneRunner.Load(_main = NewMain(_folder), true))
        {
            await WorldEdgeTest.PhysicsFrames(_main, 3);
            var (watch, disconnect) = Watch(_main);
            try
            {
                Click(_main.MainMenu.LoadButton);
                AssertThat(_main.SlotList.Mode).IsEqual(SlotListMode.Load);
                _main.SlotList.Choose(SlotId.Manual(2));
                AssertBool(_main.Dialog.IsOpen).OverrideFailureMessage("Load on the main menu asked for a confirmation").IsFalse();
                var world = await WaitForGame(_main);
                await AssertLoadedFrom(_main, world, slot2, watch, "Ac15 Load");
            }
            finally
            {
                disconnect();
            }
        }

        _main = null;
    }

    // ----- Criterion 16: the placement check and a map with no pack -----

    /// <summary>
    /// A save at the settled spawn point; a box (on the map layer, not terrain or road) is put over that spot before
    /// loading it. The load uses the spawn draw: the player is on the spawn area's open ground away from the box, the
    /// notice "The map has changed since this save, so you start somewhere else." shows, <c>PlayerLoaded</c> fires
    /// once with <c>moved</c> true, and money, gear and wanted level are the save's.
    /// </summary>
    [TestCase]
    public async Task Ac16_SavedSpotNowBlocked_LoadsWithTheSpawnDrawShowsTheNoticeMovedTrueAndKeepsMoneyGearWanted()
    {
        WorldEdgeTest.AssertPackPresent();
        var state = WorldEdgeTest.PlayerStateOfGame();
        using var runner = ISceneRunner.Load(_main = NewMain(_folder, "--new-game"), true);
        var world = await WaitForGame(_main);
        await Settle(world);
        state.AddMoney(40);
        state.AddGear("bike_lock");
        state.SetWantedLevel(3);
        await WorldEdgeTest.PhysicsFrames(world, 2);
        var saved = SaveThroughTheMenu(_main, SlotId.Manual(1), expectAsk: false);
        var spot = saved.Player.Position!.Value;
        AssertBool(world.CheckLoadPlacement((float)spot.X, (float)spot.Z, out _))
            .OverrideFailureMessage($"the saved spot {spot} fails the placement check before it is blocked").IsTrue();

        // The blocker: a box over the saved spot, outside World so it stays when the World is replaced.
        var blocker = new StaticBody3D { Name = "LoadBlocker", CollisionLayer = CollisionLayers.Map };
        blocker.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(BlockerHalf * 2f, 6f, BlockerHalf * 2f) } });
        _main.AddChild(blocker);
        _blocker = blocker;
        blocker.GlobalPosition = new Vector3((float)spot.X, (float)spot.Y + 1f, (float)spot.Z);
        state.TrySpend(40);
        state.RemoveGear("bike_lock");
        state.SetWantedLevel(0);

        var (watch, disconnect) = Watch(_main);
        try
        {
            Click(_main.SlotList.BackButton);
            Click(_main.PauseMenu.LoadButton);
            _main.SlotList.Choose(SlotId.Manual(1));
            ClickDialog(_main, "Load");
            var loaded = await WaitForGame(_main);
            AssertThat(loaded).IsNotSame(world);
            await Settle(loaded);
            var player = loaded.Player!;
            var at = player.GlobalPosition;
            GD.Print(Invariant($"SaveLoadTest: Ac16 saved spot {spot}, blocked; placed at {at}; loaded {string.Join(", ", watch.Loaded)}"));
            AssertThat(string.Join(",", watch.Loaded.Select(l => l.Moved))).IsEqual("True");
            AssertInt(watch.Spawned).IsEqual(0);
            AssertBool(_main.Notice.Visible).OverrideFailureMessage("no notice").IsTrue();
            AssertThat(_main.Notice.Text).IsEqual("The map has changed since this save, so you start somewhere else.");
            AssertFloat((float)_main.Notice.TimeLeft).IsGreater(0f);
            AssertFloat(new Vector2(at.X - (float)spot.X, at.Z - (float)spot.Z).Length()).IsGreater(BlockerHalf);
            AssertBool(SpawnPicker.Contains(loaded.MapLoader.Boundary!.SpawnArea.OpenGround, at.X, at.Z))
                .OverrideFailureMessage($"{at} is outside spawn_area.open_ground").IsTrue();
            AssertInt(state.Money).IsEqual(40);
            AssertThat(string.Join(",", state.Gear)).IsEqual("bike_lock");
            AssertInt(state.WantedLevel).IsEqual(3);
            AssertThat(loaded.Death.State).IsEqual(DeathState.Alive);
        }
        finally
        {
            disconnect();
        }
    }

    /// <summary>
    /// A valid save naming a map with no pack: its row in the load list shows "Map &lt;id&gt; not installed", is disabled
    /// and not focusable, and choosing it does nothing.
    /// </summary>
    [TestCase]
    public async Task Ac16_SaveNamingAMapWithNoPack_RowShowsNotInstalled_AndCantBeChosen()
    {
        WriteSave(_folder, SlotId.Manual(3), SampleSave(mapId: "not-installed-map", position: new LocalPoint(1, 2, 3)));
        using var runner = ISceneRunner.Load(_main = NewMain(_folder), true);
        await WorldEdgeTest.PhysicsFrames(_main, 3);
        AssertBool(_main.OpenSlotList(SlotListMode.Load)).IsTrue();
        var row = _main.SlotList.RowOf(SlotId.Manual(3));
        AssertThat(row.Info.Status).IsEqual(SlotStatus.Valid);
        AssertThat(row.Text.Detail).IsEqual("Map not-installed-map not installed");
        AssertBool(row.Button.Text.Contains("Map not-installed-map not installed", StringComparison.Ordinal)).IsTrue();
        AssertBool(row.Button.Disabled).IsTrue();
        AssertThat(row.Button.FocusMode).IsEqual(Control.FocusModeEnum.None);
        _main.SlotList.Choose(SlotId.Manual(3));
        await WorldEdgeTest.PhysicsFrames(_main, 3);
        AssertThat(_main.State).IsEqual(MainState.MainMenu);
        AssertThat(_main.World).IsNull();
        AssertBool(_main.Dialog.IsOpen).IsFalse();
    }

    /// <summary>
    /// Criterion 16 and "Loading a save", step 4, with a bridge deck: a road-group box (layer <c>Map</c>, group
    /// <c>map_roads</c>) <see cref="DeckHeight"/> m above the settled spawn point. (a) With the saved height
    /// (<c>fromY</c>), a save on the ground under the deck passes the placement check on the ground, and
    /// <c>LoadPlayer</c> puts the player there, not on the deck, with <c>moved</c> false; (b) a save at the deck's
    /// top is placed on the deck; (c) without <c>fromY</c> the ray starts from 10 000 m and finds the deck first
    /// (the behaviour before <see cref="World.LoadRayHeadroom"/>).
    /// </summary>
    [TestCase]
    public async Task Ac16_SaveUnderABridgeDeck_LoadsUnderTheDeck_SaveOnTheDeck_LoadsOnTheDeck()
    {
        WorldEdgeTest.AssertPackPresent();
        using var runner = ISceneRunner.Load(_main = NewMain(_folder, "--new-game"), true);
        var world = await WaitForGame(_main);
        await Settle(world);
        var feet = world.Player!.GlobalPosition;
        var (x, z) = (feet.X, feet.Z);
        var groundHit = world.MapLoader.GroundOrRoadHitAt(x, z);
        AssertThat(groundHit).OverrideFailureMessage($"no ground under the spawn point {feet}").IsNotNull();
        var groundY = groundHit!.Value.Position.Y;

        // The deck: outside World, as the Ac16 blocker.
        var deckTop = groundY + DeckHeight;
        var deck = new StaticBody3D { Name = "BridgeDeck", CollisionLayer = CollisionLayers.Map };
        deck.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(DeckSize, DeckThickness, DeckSize) } });
        _main.AddChild(deck);
        _blocker = deck;
        deck.AddToGroup(MapLoader.RoadGroup);
        deck.GlobalPosition = new Vector3(x, deckTop - (DeckThickness / 2f), z);
        await WorldEdgeTest.PhysicsFrames(world, 2);
        var deckHit = world.MapLoader.GroundOrRoadHitAt(x, z);
        AssertThat(deckHit?.Body).OverrideFailureMessage("the deck isn't the highest road hit").IsSame(deck);
        AssertFloat(deckHit!.Value.Position.Y).IsEqualApprox(deckTop, 1e-3f);

        var loaded = new List<(Vector3 Position, bool Moved)>();
        world.PlayerLoaded += (p, moved) => loaded.Add((p, moved));
        GD.Print(Invariant($"SaveLoadTest: bridge deck over ({x}, {z}): ground {groundY}, deck top {deckTop}, LoadRayHeadroom {World.LoadRayHeadroom}"));

        // (c) Without fromY: the ray finds the deck first.
        AssertBool(world.CheckLoadPlacement(x, z, out var fromTop)).OverrideFailureMessage("no fromY: the deck spot fails").IsTrue();
        AssertFloat(fromTop.Y).OverrideFailureMessage($"no fromY: placed at {fromTop}").IsEqualApprox(deckTop + world.SpawnDropHeight, 1e-3f);

        // (a) A save on the ground under the deck.
        AssertBool(world.CheckLoadPlacement(x, z, out var under, fromY: groundY))
            .OverrideFailureMessage("fromY ground: the spot under the deck fails").IsTrue();
        AssertFloat(under.Y).OverrideFailureMessage($"fromY ground: placed at {under}").IsEqualApprox(groundY + world.SpawnDropHeight, 1e-3f);
        AssertFloat(under.X).IsEqualApprox(x, 1e-4f);
        AssertFloat(under.Z).IsEqualApprox(z, 1e-4f);

        AssertBool(world.LoadPlayer(new SavePlayer(new LocalPoint(x, groundY, z), 0.5, 0, [], SaveEquipped.Empty, 0))).IsTrue();
        await Settle(world);
        var atGround = world.Player!.GlobalPosition;
        GD.Print(Invariant($"SaveLoadTest: bridge (a) loaded under the deck at {atGround}"));
        AssertThat(string.Join(",", loaded.Select(l => l.Moved))).IsEqual("False");
        AssertFloat(Mathf.Abs(atGround.Y - groundY)).OverrideFailureMessage($"(a) the player is at {atGround}, ground {groundY}, deck top {deckTop}").IsLessEqual(UnderDeckTolerance);
        AssertFloat(new Vector2(atGround.X - x, atGround.Z - z).Length()).IsLessEqual(PositionTolerance);

        // (b) A save on the deck.
        loaded.Clear();
        AssertBool(world.CheckLoadPlacement(x, z, out var onDeck, fromY: deckTop)).OverrideFailureMessage("fromY deck: the deck spot fails").IsTrue();
        AssertFloat(onDeck.Y).IsEqualApprox(deckTop + world.SpawnDropHeight, 1e-3f);
        AssertBool(world.LoadPlayer(new SavePlayer(new LocalPoint(x, deckTop, z), 0.5, 0, [], SaveEquipped.Empty, 0))).IsTrue();
        await Settle(world);
        var atDeck = world.Player!.GlobalPosition;
        GD.Print(Invariant($"SaveLoadTest: bridge (b) loaded on the deck at {atDeck}"));
        AssertThat(string.Join(",", loaded.Select(l => l.Moved))).IsEqual("False");
        AssertFloat(Mathf.Abs(atDeck.Y - deckTop)).OverrideFailureMessage($"(b) the player is at {atDeck}, deck top {deckTop}").IsLessEqual(GroundTolerance);
        AssertFloat(new Vector2(atDeck.X - x, atDeck.Z - z).Length()).IsLessEqual(PositionTolerance);
    }

    // ----- Criterion 17: saving and deleting -----

    /// <summary>
    /// In a game, the slot list in save mode: the autosave row is disabled with its hint and choosing it does nothing;
    /// an empty slot is written without asking ("Saved to slot 1", no <c>.tmp</c> left); an occupied one asks
    /// "Overwrite slot 1?" and is written only after Overwrite (Cancel keeps it); with the saves folder read-only the
    /// write fails with "Could not save: …" and the old file stays; Delete asks first and then empties the row.
    /// </summary>
    [TestCase]
    public async Task Ac17_SaveMode_EmptySlotWithoutAsking_OccupiedAfterOverwrite_AutosaveRowDisabled_FailedWriteKeepsTheOldFile_DeleteAsks()
    {
        WorldEdgeTest.AssertPackPresent();
        var state = WorldEdgeTest.PlayerStateOfGame();
        using var runner = ISceneRunner.Load(_main = NewMain(_folder, "--new-game"), true);
        var world = await WaitForGame(_main);
        await Settle(world);
        state.AddMoney(10);
        TapKey(Key.Escape);
        Click(_main.PauseMenu.SaveButton);
        var list = _main.SlotList;
        AssertThat(list.Mode).IsEqual(SlotListMode.Save);
        var path = SlotPath(_folder, SlotId.Manual(1));

        // The autosave row.
        var autosaveRow = list.RowOf(SlotId.Autosave);
        AssertBool(autosaveRow.Button.Disabled).IsTrue();
        AssertThat(autosaveRow.Button.FocusMode).IsEqual(Control.FocusModeEnum.None);
        AssertBool(autosaveRow.Button.Text.Contains("Saved automatically when you quit", StringComparison.Ordinal)).IsTrue();
        list.Choose(SlotId.Autosave);
        AssertBool(_main.Dialog.IsOpen).IsFalse();
        AssertBool(File.Exists(SlotPath(_folder, SlotId.Autosave))).IsFalse();

        // An empty slot: written at once.
        list.Choose(SlotId.Manual(1));
        AssertBool(_main.Dialog.IsOpen).OverrideFailureMessage("an empty slot asked").IsFalse();
        AssertBool(File.Exists(path)).IsTrue();
        AssertThat(list.StatusText).IsEqual("Saved to slot 1");
        AssertThat(string.Join(",", TempFiles())).IsEqual("");
        AssertInt(ReadSlot(_folder, SlotId.Manual(1)).Save!.Player.Money).IsEqual(10);
        AssertBool(list.RowOf(SlotId.Manual(1)).Delete.IsVisibleInTree()).IsTrue();
        AssertBool(list.IsVisibleInTree()).OverrideFailureMessage("the slot list closed after saving").IsTrue();

        // An occupied slot: only after Overwrite.
        state.AddMoney(5);
        var before = File.ReadAllText(path);
        list.Choose(SlotId.Manual(1));
        AssertBool(_main.Dialog.IsOpen).OverrideFailureMessage("an occupied slot didn't ask").IsTrue();
        AssertThat(_main.Dialog.MessageText).IsEqual("Overwrite slot 1?");
        AssertThat(File.ReadAllText(path)).IsEqual(before);
        ClickDialog(_main, "Cancel");
        AssertThat(File.ReadAllText(path)).IsEqual(before);
        list.Choose(SlotId.Manual(1));
        ClickDialog(_main, "Overwrite");
        AssertInt(ReadSlot(_folder, SlotId.Manual(1)).Save!.Player.Money).IsEqual(15);
        AssertThat(list.StatusText).IsEqual("Saved to slot 1");
        AssertThat(string.Join(",", TempFiles())).IsEqual("");

        // A failing write: the saves folder read-only.
        state.AddMoney(5);
        var old = File.ReadAllText(path);
        SetWritable(SavesOf(_folder), writable: false);
        try
        {
            list.Choose(SlotId.Manual(1));
            ClickDialog(_main, "Overwrite");
            GD.Print($"SaveLoadTest: Ac17 failed write: '{list.StatusText}'");
            AssertBool(list.StatusText.StartsWith("Could not save: ", StringComparison.Ordinal))
                .OverrideFailureMessage($"status '{list.StatusText}'").IsTrue();
            AssertBool(list.StatusText.Length > "Could not save: ".Length).IsTrue();
            AssertThat(File.ReadAllText(path)).IsEqual(old);
            AssertBool(list.IsVisibleInTree()).IsTrue();
        }
        finally
        {
            SetWritable(SavesOf(_folder), writable: true);
        }

        AssertInt(ReadSlot(_folder, SlotId.Manual(1)).Save!.Player.Money).IsEqual(15);

        // Delete in save mode.
        list.RequestDelete(SlotId.Manual(1));
        AssertBool(_main.Dialog.IsOpen).IsTrue();
        AssertThat(_main.Dialog.MessageText).IsEqual("Delete slot 1? This can't be undone.");
        ClickDialog(_main, "Cancel");
        AssertBool(File.Exists(path)).IsTrue();
        list.RequestDelete(SlotId.Manual(1));
        ClickDialog(_main, "Delete");
        AssertBool(File.Exists(path)).IsFalse();
        AssertThat(list.RowOf(SlotId.Manual(1)).Text.Detail).IsEqual("Empty");
        AssertBool(list.RowOf(SlotId.Manual(1)).Delete.Visible).IsFalse();
    }

    /// <summary>
    /// On the main menu's load list: Delete on a manual slot, a damaged slot and the autosave asks "Delete &lt;Autosave |
    /// slot n&gt;? This can't be undone." and then removes the file; the row shows "Empty".
    /// </summary>
    [TestCase]
    public async Task Ac17_DeleteAsksFirstThenEmptiesTheRow_ForAManualSlotADamagedSlotAndTheAutosave()
    {
        WriteSave(_folder, SlotId.Autosave, SampleSave(position: new LocalPoint(1, 2, 3)));
        WriteSave(_folder, SlotId.Manual(1), SampleSave(position: new LocalPoint(4, 5, 6)));
        File.WriteAllText(SlotPath(_folder, SlotId.Manual(4)), "{\"format_version\": 99}");
        using var runner = ISceneRunner.Load(_main = NewMain(_folder), true);
        await WorldEdgeTest.PhysicsFrames(_main, 3);
        Click(_main.MainMenu.LoadButton);
        var list = _main.SlotList;
        AssertThat(list.RowOf(SlotId.Manual(4)).Info.Status).IsEqual(SlotStatus.Damaged);
        AssertBool(list.RowOf(SlotId.Manual(4)).Text.Detail.StartsWith("Damaged", StringComparison.Ordinal)).IsTrue();

        foreach (var (slot, message) in new[]
        {
            (SlotId.Manual(1), "Delete slot 1? This can't be undone."),
            (SlotId.Manual(4), "Delete slot 4? This can't be undone."),
            (SlotId.Autosave, "Delete Autosave? This can't be undone."),
        })
        {
            var row = list.RowOf(slot);
            AssertBool(row.Delete.IsVisibleInTree() && !row.Delete.Disabled).OverrideFailureMessage($"{slot}: no Delete").IsTrue();
            AssertThat(row.Delete.FocusMode).IsEqual(Control.FocusModeEnum.All);
            Click(row.Delete);
            AssertBool(_main.Dialog.IsOpen).OverrideFailureMessage($"{slot}: Delete didn't ask").IsTrue();
            AssertThat(_main.Dialog.MessageText).IsEqual(message);
            AssertBool(File.Exists(SlotPath(_folder, slot))).OverrideFailureMessage($"{slot}: deleted before the confirmation").IsTrue();
            ClickDialog(_main, "Delete");
            AssertBool(File.Exists(SlotPath(_folder, slot))).OverrideFailureMessage($"{slot}: still there").IsFalse();
            AssertThat(list.RowOf(slot).Info.Status).IsEqual(SlotStatus.Empty);
            AssertThat(list.RowOf(slot).Text.Detail).IsEqual("Empty");
            AssertBool(list.RowOf(slot).Delete.Visible).IsFalse();
        }

        AssertInt(Directory.EnumerateFiles(SavesOf(_folder)).Count()).IsEqual(0);
    }

    // ----- Criterion 18: the autosave -----

    /// <summary>
    /// Quit to menu while alive: it asks "Quit to the main menu?" with the autosave line, Cancel returns to the pause
    /// menu; with the saves folder read-only, Quit keeps the dialog with "Could not autosave: …" (Quit without saving,
    /// Cancel); then Quit writes <c>autosave.json</c> with the current state and position and shows the main menu with
    /// no <c>World</c> left and the tree unpaused.
    /// </summary>
    [TestCase]
    public async Task Ac18_QuitToMenuWhileAlive_AsksThenWritesTheAutosaveWithTheCurrentState_ShowsTheMenuWithNoWorldUnpaused()
    {
        WorldEdgeTest.AssertPackPresent();
        var state = WorldEdgeTest.PlayerStateOfGame();
        using var runner = ISceneRunner.Load(_main = NewMain(_folder, "--new-game"), true);
        var world = await WaitForGame(_main);
        await Settle(world);
        state.AddMoney(75);
        state.AddGear("hoodie");
        state.SetWantedLevel(1);
        await WorldEdgeTest.PhysicsFrames(world, 2);
        var player = world.Player!;

        TapKey(Key.Escape);
        Click(_main.PauseMenu.QuitToMenuButton);
        AssertBool(_main.Dialog.IsOpen).IsTrue();
        AssertThat(_main.Dialog.MessageText).IsEqual("Quit to the main menu?");
        AssertThat(_main.Dialog.DetailText).IsEqual("Your game will be saved to the autosave slot.");
        AssertThat(string.Join("|", _main.Dialog.Buttons.Select(b => b.Text))).IsEqual("Quit|Cancel");
        ClickDialog(_main, "Cancel");
        AssertThat(_main.State).IsEqual(MainState.Paused);
        AssertBool(_main.PauseMenu.IsVisibleInTree()).IsTrue();
        AssertBool(File.Exists(SlotPath(_folder, SlotId.Autosave))).IsFalse();

        // A failing autosave keeps the dialog.
        Directory.CreateDirectory(SavesOf(_folder));
        SetWritable(SavesOf(_folder), writable: false);
        try
        {
            Click(_main.PauseMenu.QuitToMenuButton);
            ClickDialog(_main, "Quit");
            AssertBool(_main.Dialog.IsOpen).OverrideFailureMessage("a failed autosave closed the dialog").IsTrue();
            AssertBool(_main.Dialog.MessageText.StartsWith("Could not autosave: ", StringComparison.Ordinal))
                .OverrideFailureMessage($"'{_main.Dialog.MessageText}'").IsTrue();
            AssertThat(string.Join("|", _main.Dialog.Buttons.Select(b => b.Text))).IsEqual("Quit without saving|Cancel");
            ClickDialog(_main, "Cancel");
            AssertThat(_main.State).IsEqual(MainState.Paused);
            AssertThat(_main.World).IsSame(world);
        }
        finally
        {
            SetWritable(SavesOf(_folder), writable: true);
        }

        var at = player.GlobalPosition;
        var yaw = player.FacingYaw;
        Click(_main.PauseMenu.QuitToMenuButton);
        ClickDialog(_main, "Quit");
        AssertThat(_main.State).IsEqual(MainState.MainMenu);
        AssertBool(_main.MainMenu.IsVisibleInTree()).IsTrue();
        AssertThat(_main.World).IsNull();
        AssertBool(_main.GetTree().Paused).IsFalse();
        await WorldEdgeTest.PhysicsFrames(_main, 2);
        AssertBool(WorldEdgeTest.IsFreed(world)).OverrideFailureMessage("the World is left").IsTrue();

        var autosave = ReadSlot(_folder, SlotId.Autosave);
        AssertThat(autosave.Status).IsEqual(SlotStatus.Valid);
        var save = autosave.Save!;
        AssertInt(save.Player.Money).IsEqual(75);
        AssertThat(string.Join(",", save.Player.Gear)).IsEqual("hoodie");
        AssertInt(save.Player.WantedLevel).IsEqual(1);
        AssertThat(save.Player.Position).IsNotNull();
        AssertFloat((float)save.Player.Position!.Value.X).IsEqualApprox(at.X, 1e-3f);
        AssertFloat((float)save.Player.Position!.Value.Z).IsEqualApprox(at.Z, 1e-3f);
        AssertFloat((float)save.Player.FacingYaw!.Value).IsEqualApprox(yaw, 1e-5f);
        AssertThat(save.GameVersion).IsEqual("dev");
        AssertBool(_main.MainMenu.ContinueButton.Disabled).IsFalse();
    }

    /// <summary>
    /// Quit to menu during <c>Falling</c> (the player had money 50 and gear <c>crowbar</c> before the void): the autosave
    /// has an empty position and facing, money 0, no gear and wanted level 0. Continue then spawns the player on the
    /// spawn area's open ground with nothing, no notice, and <c>PlayerLoaded</c> once with <c>moved</c> false.
    /// </summary>
    [TestCase]
    public async Task Ac18_QuitToMenuWhileFalling_AutosavesThePostDeathStateWithAnEmptyPosition_AndLoadingItSpawnsWithNothing()
    {
        WorldEdgeTest.AssertPackPresent();
        var state = WorldEdgeTest.PlayerStateOfGame();
        using var runner = ISceneRunner.Load(_main = NewMain(_folder, "--new-game"), true);
        var world = await WaitForGame(_main);
        await WaitForEdge(world);
        await WaitForPlayTime(_main);
        state.AddMoney(50);
        state.AddGear("crowbar");
        await EnterVoid(world);
        AssertThat(world.Death.State).IsEqual(DeathState.Falling);

        TapKey(Key.Escape);
        Click(_main.PauseMenu.QuitToMenuButton);
        ClickDialog(_main, "Quit");
        AssertThat(_main.State).IsEqual(MainState.MainMenu);
        AssertThat(_main.World).IsNull();
        AssertBool(_main.GetTree().Paused).IsFalse();
        AssertPostDeathAutosave("Falling");

        var (watch, disconnect) = Watch(_main);
        try
        {
            Click(_main.MainMenu.ContinueButton);
            var loaded = await WaitForGame(_main);
            await Settle(loaded);
            var at = loaded.Player!.GlobalPosition;
            GD.Print(Invariant($"SaveLoadTest: Ac18 loaded the post-death autosave at {at}; loaded {string.Join(", ", watch.Loaded)}"));
            AssertThat(string.Join(",", watch.Loaded.Select(l => l.Moved))).IsEqual("False");
            AssertInt(watch.Spawned).IsEqual(0);
            AssertBool(_main.Notice.Visible).OverrideFailureMessage($"a notice shows: '{_main.Notice.Text}'").IsFalse();
            AssertBool(SpawnPicker.Contains(loaded.MapLoader.Boundary!.SpawnArea.OpenGround, at.X, at.Z)).IsTrue();
            AssertInt(state.Money).IsEqual(0);
            AssertInt(state.Gear.Count).IsEqual(0);
            AssertInt(state.WantedLevel).IsEqual(0);
            AssertThat(loaded.Death.State).IsEqual(DeathState.Alive);
        }
        finally
        {
            disconnect();
        }
    }

    /// <summary>
    /// The window's close request during a game: alive, it pauses and asks "Quit the game?" with the autosave line,
    /// and Cancel keeps the game paused with nothing written; on the death screen it asks again, and Quit writes the
    /// post-death autosave (empty position, money 0, no gear) and quits with exit code 0.
    /// </summary>
    [TestCase]
    public async Task Ac18_CloseRequestDuringAGame_PausesAndAsks_OnTheDeathScreenQuitAutosavesThePostDeathStateAndExitsWith0()
    {
        WorldEdgeTest.AssertPackPresent();
        var state = WorldEdgeTest.PlayerStateOfGame();
        using var runner = ISceneRunner.Load(_main = NewMain(_folder, "--new-game"), true);
        var world = await WaitForGame(_main);
        await WaitForEdge(world);
        var quits = new List<int>();
        _main.QuitRequested += code => quits.Add(code);

        _main.GetTree().Root.PropagateNotification((int)Node.NotificationWMCloseRequest);
        AssertThat(_main.State).IsEqual(MainState.Paused);
        AssertBool(_main.GetTree().Paused).IsTrue();
        AssertBool(_main.Dialog.IsOpen).IsTrue();
        AssertThat(_main.Dialog.MessageText).IsEqual("Quit the game?");
        AssertThat(_main.Dialog.DetailText).IsEqual("Your game will be saved to the autosave slot.");
        ClickDialog(_main, "Cancel");
        AssertThat(_main.State).IsEqual(MainState.Paused);
        AssertThat(_main.ExitCode).IsNull();
        AssertBool(File.Exists(SlotPath(_folder, SlotId.Autosave))).IsFalse();
        Click(_main.PauseMenu.ResumeButton);

        state.AddMoney(50);
        state.AddGear("crowbar");
        world.FallTime = ShortFallTime;
        await EnterVoid(world);
        AssertBool(await WorldEdgeTest.WaitUntil(world, () => world.Death.State == DeathState.DeathScreen, WorldEdgeTest.FramesFor(ShortFallTime, Engine.PhysicsTicksPerSecond) + 10)).IsTrue();

        _main.GetTree().Root.PropagateNotification((int)Node.NotificationWMCloseRequest);
        AssertThat(_main.State).IsEqual(MainState.Paused);
        AssertBool(_main.Dialog.IsOpen).IsTrue();
        AssertThat(_main.Dialog.MessageText).IsEqual("Quit the game?");
        AssertThat(_main.ExitCode).IsNull();
        AssertBool(File.Exists(SlotPath(_folder, SlotId.Autosave))).IsFalse();
        ClickDialog(_main, "Quit");
        AssertThat(_main.ExitCode).IsEqual(0);
        AssertThat(string.Join(",", quits)).IsEqual("0");
        AssertPostDeathAutosave("the death screen");
    }

    // ----- docs/specs/inventory.md, criteria 12, 13 and 21 -----

    /// <summary>
    /// <c>docs/specs/inventory.md</c> criteria 13, 21 and 12, in one game. <c>--give crowbar,tracksuit,phone</c> gives
    /// the three before <c>GameStarted</c> (13). Through the inventory screen the crowbar (Enter on its Equip) and the
    /// tracksuit (a click) are equipped; saving to slot 1 stores the gear and both slots. Then the screen unequips the
    /// crowbar and discards the phone, and the player dies; loading slot 1 from the pause menu gives the same gear and
    /// slots, with no notice, and the screen shows them (21). Last, a hand-written slot 2 with an unknown id held and
    /// in the weapon slot, and a weapon in the outfit slot, shows as <c>Valid</c> in the slot list; loading it pushes
    /// one warning per change naming <c>slot_2.json</c> and the id, shows "Some items in this save no longer exist and
    /// were removed.", and the player holds the known items only, none equipped (12).
    /// </summary>
    [TestCase]
    public async Task InventoryAc12_Ac13_Ac21_GiveThenEquipSaveDiscardDieAndLoad_RestoresGearAndSlots_AHandWrittenSaveWithUnknownItemsLoadsCleanedWithWarningsAndNotice()
    {
        WorldEdgeTest.AssertPackPresent();
        var state = WorldEdgeTest.PlayerStateOfGame();
        string? gearAtStart = null;
        _main = NewMain(_folder, "--new-game", "--give", "crowbar,tracksuit,phone");
        var main = _main;
        main.GameStarted += () => gearAtStart ??= string.Join(",", state.Gear.Order(StringComparer.Ordinal));
        using var runner = ISceneRunner.Load(main, true);
        var world = await WaitForGame(main);
        await WaitForEdge(world);
        await Settle(world);
        var screen = main.InventoryScreen;

        // Criterion 13: given before GameStarted.
        AssertThat(gearAtStart).IsEqual("crowbar,phone,tracksuit");
        AssertThat(string.Join(",", world.GivenItems)).IsEqual("crowbar,tracksuit,phone");

        // Equip through the screen: Enter on the crowbar's Equip, a click on the tracksuit's.
        TapKey(Key.Tab);
        AssertThat(main.State).IsEqual(MainState.Inventory);
        screen.EquipButtonOf("crowbar")!.GrabFocus();
        TapKey(Key.Enter);
        Click(screen.EquipButtonOf("tracksuit")!);
        AssertThat(state.EquippedIn(Core.Items.EquipSlot.Weapon)).IsEqual("crowbar");
        AssertThat(state.EquippedIn(Core.Items.EquipSlot.Outfit)).IsEqual("tracksuit");
        TapKey(Key.Tab);
        AssertThat(main.State).IsEqual(MainState.Playing);
        await WorldEdgeTest.PhysicsFrames(world, 2);

        var saved = SaveThroughTheMenu(main, SlotId.Manual(1), expectAsk: false);
        AssertThat(string.Join(",", saved.Player.Gear.Order(StringComparer.Ordinal))).IsEqual("crowbar,phone,tracksuit");
        AssertThat(saved.Player.Equipped).IsEqual(new SaveEquipped("crowbar", "tracksuit"));
        Click(main.SlotList.BackButton);
        Click(main.PauseMenu.ResumeButton);

        // Unequip and discard through the screen, then die.
        await WorldEdgeTest.PhysicsFrames(world, 2);
        TapKey(Key.Tab);
        AssertThat(main.State).IsEqual(MainState.Inventory);
        Click(screen.SlotRowOf(Core.Items.EquipSlot.Weapon).UnequipButton);
        Click(screen.DiscardButtonOf("phone")!);
        ClickDialog(main, "Discard");
        AssertThat(state.EquippedIn(Core.Items.EquipSlot.Weapon)).IsNull();
        AssertThat(string.Join(",", state.Gear.Order(StringComparer.Ordinal))).IsEqual("crowbar,tracksuit");
        TapKey(Key.Tab);
        AssertThat(main.State).IsEqual(MainState.Playing);
        await WorldEdgeTest.PhysicsFrames(world, 2);
        await EnterVoid(world);
        AssertInt(state.Gear.Count).IsEqual(0);

        // Load slot 1 from the pause menu (it asks first).
        TapKey(Key.Escape);
        AssertThat(main.State).IsEqual(MainState.Paused);
        Click(main.PauseMenu.LoadButton);
        main.SlotList.Choose(SlotId.Manual(1));
        ClickDialog(main, "Load");
        var loaded = await WaitForNewGame(main, world);
        await Settle(loaded);
        AssertThat(string.Join(",", state.Gear.Order(StringComparer.Ordinal))).IsEqual("crowbar,phone,tracksuit");
        AssertThat(state.EquippedIn(Core.Items.EquipSlot.Weapon)).IsEqual("crowbar");
        AssertThat(state.EquippedIn(Core.Items.EquipSlot.Outfit)).IsEqual("tracksuit");
        AssertBool(main.Notice.Visible).OverrideFailureMessage($"a notice after a clean load: '{main.Notice.Text}'").IsFalse();
        AssertThat(string.Join(",", loaded.GivenItems)).OverrideFailureMessage("--give was applied again on a load").IsEqual("");
        TapKey(Key.Tab);
        AssertThat(main.State).IsEqual(MainState.Inventory);
        AssertThat(screen.SlotRowOf(Core.Items.EquipSlot.Weapon).ItemLabel.Text).IsEqual("Crowbar");
        AssertThat(screen.SlotRowOf(Core.Items.EquipSlot.Outfit).ItemLabel.Text).IsEqual("Tracksuit");
        AssertThat(string.Join("|", screen.ItemRows.Select(r => $"{r.Item.Id}:{r.EquippedLabel.Visible}")))
            .IsEqual("crowbar:True|tracksuit:True|phone:False");
        TapKey(Key.Tab);
        AssertThat(main.State).IsEqual(MainState.Playing);

        // Criterion 12: a hand-written save with unknown and misplaced items.
        var slot2 = SlotPath(_folder, SlotId.Manual(2));
        File.WriteAllText(slot2, """
            {
              "format_version": 2,
              "game_version": "dev",
              "saved_at_utc": "2026-11-05T12:00:00Z",
              "play_seconds": 300,
              "map_id": "somero",
              "map_generated_at": "2026-01-01T00:00:00Z",
              "player": {
                "position": null,
                "facing_yaw": null,
                "money": 5,
                "gear": ["crowbar", "old_pipe", "phone"],
                "equipped": { "weapon": "old_pipe", "outfit": "crowbar" },
                "wanted_level": 0
              },
              "quests": {}
            }
            """);
        await WorldEdgeTest.PhysicsFrames(loaded, 2);
        TapKey(Key.Escape);
        AssertThat(main.State).IsEqual(MainState.Paused);
        Click(main.PauseMenu.LoadButton);
        var row = main.SlotList.RowOf(SlotId.Manual(2));
        AssertThat(row.Info.Status).OverrideFailureMessage($"the slot list shows slot 2 as {row.Info.Status} ({row.Info.DamageDetail})").IsEqual(SlotStatus.Valid);
        AssertBool(row.Button.Disabled).IsFalse();

        var capture = new ErrorCapture();
        OS.AddLogger(capture);
        World cleaned;
        try
        {
            main.SlotList.Choose(SlotId.Manual(2));
            ClickDialog(main, "Load");
            cleaned = await WaitForNewGame(main, loaded);
        }
        finally
        {
            OS.RemoveLogger(capture);
        }

        var warnings = capture.Errors.Where(e => e.Contains("slot_2.json", StringComparison.Ordinal)).ToList();
        GD.Print($"SaveLoadTest: inventory Ac12 warnings: {string.Join(" | ", warnings)}; all captured: {string.Join(" | ", capture.Errors)}");
        AssertInt(warnings.Count).OverrideFailureMessage($"expected 2 warnings naming slot_2.json, got: {string.Join(" | ", warnings)}").IsEqual(2);
        AssertInt(warnings.Count(w => w.Contains("old_pipe", StringComparison.Ordinal))).IsEqual(1);
        AssertInt(warnings.Count(w => w.Contains("crowbar", StringComparison.Ordinal))).IsEqual(1);
        AssertInt(capture.ErrorsOnly.Count(e => e.Contains("slot_2.json", StringComparison.Ordinal))).OverrideFailureMessage("the cleanup pushed errors, not warnings").IsEqual(0);
        AssertBool(main.Notice.Visible).OverrideFailureMessage("no notice after loading removed items").IsTrue();
        AssertThat(main.Notice.Text).IsEqual("Some items in this save no longer exist and were removed.");
        AssertThat(string.Join(",", state.Gear.Order(StringComparer.Ordinal))).IsEqual("crowbar,phone");
        AssertInt(state.Equipped.Count).OverrideFailureMessage($"slots after the cleanup: {string.Join(",", state.Equipped)}").IsEqual(0);
        AssertThat(cleaned.Death.State).IsEqual(DeathState.Alive);
        AssertThat(main.State).IsEqual(MainState.Playing);
    }

    /// <summary>
    /// <c>docs/specs/inventory.md</c> criterion 12 with the placement check of <c>docs/specs/menu-settings-save.md</c>
    /// criterion 16, and criterion 13's "only items actually added". A hand-written slot 1 holds an unknown id
    /// (<c>old_pipe</c>, also in the weapon slot) and a position far outside the pack, so the placement check fails.
    /// A fresh <c>Main</c> with <c>--give crowbar,hoodie</c> loads it with Continue: one warning per removed id names
    /// <c>slot_1.json</c>, the player is moved by the spawn draw (<c>PlayerLoaded</c> with <c>moved</c> true), and one
    /// notice joins the two texts, the item text first. <c>--give</c> is applied after this first placement:
    /// <c>crowbar</c> was already held, so <see cref="World.GivenItems"/> lists only <c>hoodie</c>.
    /// </summary>
    [TestCase]
    public async Task InventoryAc12_Ac13_LoadThatRemovesItemsAndMovesThePlayer_ShowsOneJoinedNoticeItemTextFirst_AndGiveListsOnlyAddedItems()
    {
        WorldEdgeTest.AssertPackPresent();
        var state = WorldEdgeTest.PlayerStateOfGame();
        state.Reset();
        Directory.CreateDirectory(SavesOf(_folder));
        File.WriteAllText(SlotPath(_folder, SlotId.Manual(1)), """
            {
              "format_version": 2,
              "game_version": "dev",
              "saved_at_utc": "2026-11-05T12:00:00Z",
              "play_seconds": 300,
              "map_id": "somero",
              "map_generated_at": "2026-01-01T00:00:00Z",
              "player": {
                "position": [100000.0, 100.0, 100000.0],
                "facing_yaw": 0.5,
                "money": 5,
                "gear": ["crowbar", "old_pipe", "phone"],
                "equipped": { "weapon": "old_pipe", "outfit": null },
                "wanted_level": 0
              },
              "quests": {}
            }
            """);

        using var runner = ISceneRunner.Load(_main = NewMain(_folder, "--give", "crowbar,hoodie"), true);
        var main = _main;
        await WorldEdgeTest.PhysicsFrames(main, 3);
        AssertThat(main.State).IsEqual(MainState.MainMenu);
        AssertBool(main.MainMenu.ContinueButton.Disabled).OverrideFailureMessage("Continue is disabled with the hand-written slot 1").IsFalse();

        var (watch, disconnect) = Watch(main);
        var capture = new ErrorCapture();
        OS.AddLogger(capture);
        World world;
        try
        {
            Click(main.MainMenu.ContinueButton);
            world = await WaitForGame(main);
        }
        finally
        {
            OS.RemoveLogger(capture);
            disconnect();
        }

        var warnings = capture.Errors.Where(e => e.Contains("slot_1.json", StringComparison.Ordinal)).ToList();
        GD.Print($"SaveLoadTest: inventory Ac12 joined notice: '{main.Notice.Text}'; warnings: {string.Join(" | ", warnings)}; loaded {string.Join(", ", watch.Loaded)}; given {string.Join(",", world.GivenItems)}");

        // The placement check failed, so the player was moved.
        AssertBool(world.CheckLoadPlacement(100000f, 100000f, out _, 100f))
            .OverrideFailureMessage("the saved position passes the placement check").IsFalse();
        AssertThat(string.Join(",", watch.Loaded.Select(l => l.Moved))).IsEqual("True");
        AssertInt(watch.Spawned).IsEqual(0);

        // The item warnings: one, for old_pipe (its slot goes with it), naming the file; warnings, not errors.
        AssertInt(warnings.Count).OverrideFailureMessage($"expected 1 warning naming slot_1.json, got: {string.Join(" | ", warnings)}").IsEqual(1);
        AssertBool(warnings[0].Contains("old_pipe", StringComparison.Ordinal)).IsTrue();
        AssertInt(capture.ErrorsOnly.Count(e => e.Contains("slot_1.json", StringComparison.Ordinal))).OverrideFailureMessage("the cleanup pushed errors, not warnings").IsEqual(0);

        // One notice, the item text first.
        AssertBool(main.Notice.Visible).OverrideFailureMessage("no notice").IsTrue();
        AssertThat(main.Notice.Text).IsEqual(
            "Some items in this save no longer exist and were removed. The map has changed since this save, so you start somewhere else.");
        AssertThat(main.Notice.Text).IsEqual(Main.CombinedNotice(Main.ItemsRemovedNotice));
        AssertFloat((float)main.Notice.TimeLeft).IsGreater(0f);

        // --give after the first placement: crowbar was held already, so only hoodie was added.
        AssertThat(string.Join(",", world.GivenItems)).IsEqual("hoodie");
        AssertThat(string.Join(",", state.Gear.Order(StringComparer.Ordinal))).IsEqual("crowbar,hoodie,phone");
        AssertInt(state.Equipped.Count).OverrideFailureMessage($"slots after the cleanup: {string.Join(",", state.Equipped)}").IsEqual(0);
        AssertInt(state.Money).IsEqual(5);
        AssertThat(world.Death.State).IsEqual(DeathState.Alive);
        AssertThat(main.State).IsEqual(MainState.Playing);
    }

    // ===== Helpers =====

    private void AssertPostDeathAutosave(string when)
    {
        var autosave = ReadSlot(_folder, SlotId.Autosave);
        AssertThat(autosave.Status).OverrideFailureMessage($"{when}: autosave {autosave.Status} {autosave.DamageDetail}").IsEqual(SlotStatus.Valid);
        var player = autosave.Save!.Player;
        AssertThat(player.Position).OverrideFailureMessage($"{when}: the autosave has a position").IsNull();
        AssertThat(player.FacingYaw).IsNull();
        AssertInt(player.Money).IsEqual(0);
        AssertInt(player.Gear.Count).IsEqual(0);
        AssertInt(player.WantedLevel).IsEqual(0);
        AssertThat(autosave.Save.GameVersion).IsEqual("dev");
        AssertFloat(autosave.Save.PlaySeconds).IsGreater(0d);
    }

    /// <summary>Pauses, opens Save, chooses <paramref name="slot"/> (confirming an overwrite) and returns the save as read back.</summary>
    private SaveGame SaveThroughTheMenu(Main main, SlotId slot, bool expectAsk)
    {
        TapKey(Key.Escape);
        AssertThat(main.State).IsEqual(MainState.Paused);
        Click(main.PauseMenu.SaveButton);
        AssertThat(main.SlotList.Mode).IsEqual(SlotListMode.Save);
        main.SlotList.Choose(slot);
        AssertBool(main.Dialog.IsOpen).IsEqual(expectAsk);
        if (expectAsk)
        {
            ClickDialog(main, "Overwrite");
        }

        AssertThat(main.SlotList.StatusText).IsEqual($"Saved to slot {slot.Number}");
        var info = ReadSlot(_folder, slot);
        AssertThat(info.Status).OverrideFailureMessage($"slot {slot}: {info.Status} {info.DamageDetail}").IsEqual(SlotStatus.Valid);
        return info.Save!;
    }

    private static string? GameVersionInFile(string path)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        return json.RootElement.GetProperty("game_version").GetString();
    }

    private IEnumerable<string> TempFiles() =>
        Directory.Exists(SavesOf(_folder))
            ? Directory.EnumerateFiles(SavesOf(_folder), "*" + SaveSlots.TempSuffix).Select(Path.GetFileName).OfType<string>()
            : [];

    private static async Task WalkForward(World world, int frames)
    {
        var start = world.Player!.GlobalPosition;
        Input.ActionPress(Player.MoveForwardAction);
        try
        {
            await WorldEdgeTest.PhysicsFrames(world, frames);
        }
        finally
        {
            Input.ActionRelease(Player.MoveForwardAction);
        }

        await WorldEdgeTest.PhysicsFrames(world, 2);
        GD.Print(Invariant($"SaveLoadTest: walked from {start} to {world.Player!.GlobalPosition}"));
    }

    private static async Task EnterVoid(World world)
    {
        var entry = WorldEdgeTest.FindLedgeEntry(world);
        WorldEdgeTest.AssertEntered(await WorldEdgeTest.PlaceOnLedgeAndWaitForEntry(world, entry));
    }

    /// <summary>Waits for the game that replaces <paramref name="old"/>.</summary>
    private static async Task<World> WaitForNewGame(Main main, World old)
    {
        AssertBool(await WorldEdgeTest.WaitUntil(main, () => main.World != old, 10)).IsTrue();
        return await WaitForGame(main);
    }

    /// <summary>Criteria 14 and 15: the loaded game matches <paramref name="save"/>.</summary>
    private static async Task AssertLoadedFrom(Main main, World world, SaveGame save, LoadWatch watch, string what)
    {
        var state = WorldEdgeTest.PlayerStateOfGame();
        AssertInt(watch.Started).IsEqual(1);
        AssertThat(watch.PlayTimeAtStart).OverrideFailureMessage($"{what}: play time at the start").IsEqual(save.PlaySeconds);
        await WaitForEdge(world);
        await Settle(world);
        var player = world.Player!;
        var at = player.GlobalPosition;
        var spot = save.Player.Position!.Value;
        var dxz = new Vector2(at.X - (float)spot.X, at.Z - (float)spot.Z).Length();
        var yawError = Mathf.Abs(Mathf.AngleDifference((float)save.Player.FacingYaw!.Value, player.FacingYaw));
        var ground = world.MapLoader.GroundOrRoadHitAt(at.X, at.Z);
        GD.Print(Invariant($"SaveLoadTest: {what}: saved {spot} yaw {save.Player.FacingYaw}; loaded {at} yaw {player.FacingYaw} (x/z {dxz} m, yaw {yawError} rad, ground {ground?.Position.Y}); PlayerLoaded {string.Join(", ", watch.Loaded)}"));
        AssertThat(string.Join(",", watch.Loaded.Select(l => l.Moved))).OverrideFailureMessage($"{what}: PlayerLoaded").IsEqual("False");
        AssertInt(watch.Spawned).IsEqual(0);
        AssertInt(watch.Respawned).IsEqual(0);
        AssertFloat(dxz).OverrideFailureMessage($"{what}: {dxz} m from the saved spot").IsLessEqual(PositionTolerance);
        AssertFloat(yawError).OverrideFailureMessage($"{what}: facing off by {yawError} rad").IsLessEqual(YawTolerance);
        AssertThat(ground).OverrideFailureMessage($"{what}: no ground under {at}").IsNotNull();
        AssertFloat(Mathf.Abs(at.Y - ground!.Value.Position.Y)).IsLessEqual(GroundTolerance);
        AssertBool(player.IsOnFloor()).IsTrue();
        AssertInt(state.Money).IsEqual(save.Player.Money);
        AssertThat(string.Join(",", state.Gear.Order(StringComparer.Ordinal))).IsEqual(string.Join(",", save.Player.Gear));
        AssertInt(state.WantedLevel).IsEqual(save.Player.WantedLevel);
        AssertThat(world.Death.State).IsEqual(DeathState.Alive);
        AssertBool(world.DeathScreen.IsShown).IsFalse();
        AssertBool(main.Notice.Visible).IsFalse();
        var barriers = world.MapLoader.Barriers!;
        AssertInt(barriers.BrokenItemIds.Count).IsEqual(0);
        AssertInt(barriers.ItemCount).IsEqual(barriers.Layouts.Count);
        AssertInt(barriers.LoosePieceCount).IsEqual(0);
        AssertBool(world.PlayerCamera!.IsCurrent).IsTrue();
        AssertThat(world.Hud.Target).IsSame(player);
        AssertBool(main.GetTree().Paused).IsFalse();
        AssertThat(main.State).IsEqual(MainState.Playing);
        AssertBool(world.PlayerCamera.WantsCapture).IsTrue();
        AssertFloat(main.PlayTime.Seconds).IsGreaterEqual(save.PlaySeconds);
    }
}
