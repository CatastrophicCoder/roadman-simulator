using Roadman.Core.Tools;

namespace Roadman.Core.Tests.Tools;

public class ScreenshotArgsTests
{
    [Theory]
    [InlineData]
    [InlineData("--some-runner-flag", "value")]
    [InlineData("--presets", "p.json")]
    public void Parse_WithoutScreenshotFlag_ReturnsNullAndIgnoresOtherArgs(params string[] args)
    {
        Assert.Null(ScreenshotArgs.Parse(args));
    }

    [Fact]
    public void Parse_PresetAndOutput()
    {
        var args = ScreenshotArgs.Parse(["--screenshot", "overview", "build/screenshots/overview.png"]);

        Assert.Equal(new ScreenshotArgs("overview", "build/screenshots/overview.png", null), args);
    }

    [Fact]
    public void Parse_PresetsOverride_InEitherOrder()
    {
        var after = ScreenshotArgs.Parse(["--screenshot", "a", "a.png", "--presets", "/tmp/p.json"]);
        var before = ScreenshotArgs.Parse(["--presets", "/tmp/p.json", "--screenshot", "a", "a.png"]);

        Assert.Equal(new ScreenshotArgs("a", "a.png", "/tmp/p.json"), after);
        Assert.Equal(after, before);
    }

    [Fact]
    public void Parse_MapId_InAnyPosition()
    {
        var after = ScreenshotArgs.Parse(["--screenshot", "a", "a.png", "--map", "other-town"]);
        var before = ScreenshotArgs.Parse(["--map", "other-town", "--screenshot", "a", "a.png"]);
        var withPresets = ScreenshotArgs.Parse(["--screenshot", "a", "a.png", "--map", "x_1", "--presets", "p.json"]);

        Assert.Equal(new ScreenshotArgs("a", "a.png", null, "other-town"), after);
        Assert.Equal(after, before);
        Assert.Equal(new ScreenshotArgs("a", "a.png", "p.json", "x_1"), withPresets);
    }

    [Fact]
    public void Parse_WithoutMap_LeavesMapIdNull()
    {
        Assert.Null(ScreenshotArgs.Parse(["--screenshot", "a", "a.png"])!.MapId);
    }

    [Fact]
    public void Parse_WithoutScreenshotFlag_IgnoresMap()
    {
        Assert.Null(ScreenshotArgs.Parse(["--map", "Not A Slug"]));
    }

    [Theory]
    [InlineData("Somero")]
    [InlineData("../somero")]
    [InlineData("somero-")]
    [InlineData("some--ro")]
    [InlineData("some ro")]
    public void Parse_MapIdNotASlug_Throws(string mapId)
    {
        var ex = Assert.Throws<ArgumentException>(() => ScreenshotArgs.Parse(["--screenshot", "a", "a.png", "--map", mapId]));
        Assert.Contains(mapId, ex.Message);
    }

    [Theory]
    [InlineData("--screenshot", "a", "a.png", "--map")]
    [InlineData("--screenshot", "a", "a.png", "--map", "")]
    [InlineData("--screenshot", "a", "a.png", "--map", "--presets", "p.json")]
    [InlineData("--screenshot", "a", "a.png", "--map", "x", "--map", "y")]
    public void Parse_MapMissingOrRepeated_Throws(params string[] args)
    {
        Assert.Throws<ArgumentException>(() => ScreenshotArgs.Parse(args));
    }

    [Fact]
    public void Parse_Orthophoto_InAnyPosition()
    {
        var after = ScreenshotArgs.Parse(["--screenshot", "a", "a.png", "--orthophoto"]);
        var before = ScreenshotArgs.Parse(["--orthophoto", "--map", "x", "--screenshot", "a", "a.png"]);

        Assert.Equal(new ScreenshotArgs("a", "a.png", null, null, Orthophoto: true), after);
        Assert.Equal(new ScreenshotArgs("a", "a.png", null, "x", Orthophoto: true), before);
    }

    [Fact]
    public void Parse_WithoutOrthophoto_LeavesItOff()
    {
        Assert.False(ScreenshotArgs.Parse(["--screenshot", "a", "a.png"])!.Orthophoto);
    }

    [Fact]
    public void Parse_WithoutScreenshotFlag_IgnoresOrthophoto()
    {
        Assert.Null(ScreenshotArgs.Parse(["--orthophoto"]));
    }

    [Theory]
    [InlineData("--screenshot", "a", "a.png", "--orthophoto", "--orthophoto")]
    [InlineData("--screenshot", "a", "--orthophoto")]
    [InlineData("--screenshot", "a", "a.png", "--orthophoto", "yes")]
    public void Parse_OrthophotoRepeatedOrMisplaced_Throws(params string[] args)
    {
        Assert.Throws<ArgumentException>(() => ScreenshotArgs.Parse(args));
    }

    [Fact]
    public void Parse_UnknownArgument_ListsOrthophotoInUsage()
    {
        var ex = Assert.Throws<ArgumentException>(() => ScreenshotArgs.Parse(["--screenshot", "a", "a.png", "--ortho"]));
        Assert.Contains("[--orthophoto]", ex.Message);
    }

    [Fact]
    public void Parse_RoadGraph_InAnyPosition()
    {
        var after = ScreenshotArgs.Parse(["--screenshot", "a", "a.png", "--road-graph"]);
        var before = ScreenshotArgs.Parse(["--road-graph", "--map", "x", "--screenshot", "a", "a.png"]);
        var both = ScreenshotArgs.Parse(["--screenshot", "a", "a.png", "--road-graph", "--orthophoto"]);

        Assert.Equal(new ScreenshotArgs("a", "a.png", null, null, RoadGraph: true), after);
        Assert.Equal(new ScreenshotArgs("a", "a.png", null, "x", RoadGraph: true), before);
        Assert.Equal(new ScreenshotArgs("a", "a.png", null, null, Orthophoto: true, RoadGraph: true), both);
    }

    [Fact]
    public void Parse_WithoutRoadGraph_LeavesItOff()
    {
        Assert.False(ScreenshotArgs.Parse(["--screenshot", "a", "a.png", "--orthophoto"])!.RoadGraph);
    }

    [Fact]
    public void Parse_WithoutScreenshotFlag_IgnoresRoadGraph()
    {
        Assert.Null(ScreenshotArgs.Parse(["--road-graph"]));
    }

    [Theory]
    [InlineData("--screenshot", "a", "a.png", "--road-graph", "--road-graph")]
    [InlineData("--screenshot", "a", "--road-graph")]
    [InlineData("--screenshot", "a", "a.png", "--road-graph", "yes")]
    public void Parse_RoadGraphRepeatedOrMisplaced_Throws(params string[] args)
    {
        Assert.Throws<ArgumentException>(() => ScreenshotArgs.Parse(args));
    }

    [Fact]
    public void Parse_UnknownArgument_ListsRoadGraphInUsage()
    {
        var ex = Assert.Throws<ArgumentException>(() => ScreenshotArgs.Parse(["--screenshot", "a", "a.png", "--roads"]));
        Assert.Contains("[--road-graph]", ex.Message);
    }

    [Fact]
    public void Parse_Inspect_InAnyPosition()
    {
        var after = ScreenshotArgs.Parse(["--screenshot", "a", "a.png", "--inspect"]);
        var before = ScreenshotArgs.Parse(["--inspect", "--map", "x", "--screenshot", "a", "a.png"]);
        var all = ScreenshotArgs.Parse(["--screenshot", "a", "a.png", "--inspect", "--road-graph", "--orthophoto"]);

        Assert.Equal(new ScreenshotArgs("a", "a.png", null, null, Inspect: true), after);
        Assert.Equal(new ScreenshotArgs("a", "a.png", null, "x", Inspect: true), before);
        Assert.Equal(new ScreenshotArgs("a", "a.png", null, null, Orthophoto: true, RoadGraph: true, Inspect: true), all);
    }

    [Fact]
    public void Parse_WithoutInspect_LeavesItOff()
    {
        Assert.False(ScreenshotArgs.Parse(["--screenshot", "a", "a.png", "--road-graph"])!.Inspect);
    }

    [Fact]
    public void Parse_WithoutScreenshotFlag_IgnoresInspect()
    {
        Assert.Null(ScreenshotArgs.Parse(["--inspect"]));
    }

    [Theory]
    [InlineData("--screenshot", "a", "a.png", "--inspect", "--inspect")]
    [InlineData("--screenshot", "a", "--inspect")]
    [InlineData("--screenshot", "a", "a.png", "--inspect", "yes")]
    public void Parse_InspectRepeatedOrMisplaced_Throws(params string[] args)
    {
        Assert.Throws<ArgumentException>(() => ScreenshotArgs.Parse(args));
    }

    [Fact]
    public void Parse_UnknownArgument_ListsInspectInUsage()
    {
        var ex = Assert.Throws<ArgumentException>(() => ScreenshotArgs.Parse(["--screenshot", "a", "a.png", "--inspector"]));
        Assert.Contains("[--inspect]", ex.Message);
    }

    // ----- --player (docs/specs/player-on-foot.md, criterion 23) -----

    [Fact]
    public void Parse_Player_InAnyPosition()
    {
        var after = ScreenshotArgs.Parse(["--screenshot", "a", "a.png", "--player"]);
        var before = ScreenshotArgs.Parse(["--player", "--map", "x", "--screenshot", "a", "a.png", "--seed", "1"]);
        var all = ScreenshotArgs.Parse(["--screenshot", "a", "a.png", "--player", "--road-graph", "--orthophoto"]);

        Assert.Equal(new ScreenshotArgs("a", "a.png", null, null, Player: true), after);
        Assert.Equal(new ScreenshotArgs("a", "a.png", null, "x", Player: true), before);
        Assert.Equal(new ScreenshotArgs("a", "a.png", null, null, Orthophoto: true, RoadGraph: true, Player: true), all);
    }

    [Fact]
    public void Parse_WithoutPlayer_LeavesItOff()
    {
        Assert.False(ScreenshotArgs.Parse(["--screenshot", "a", "a.png", "--inspect"])!.Player);
    }

    [Fact]
    public void Parse_WithoutScreenshotFlag_IgnoresPlayer()
    {
        Assert.Null(ScreenshotArgs.Parse(["--player"]));
    }

    [Theory]
    [InlineData("--screenshot", "a", "a.png", "--player", "--player")]
    [InlineData("--screenshot", "a", "--player")]
    [InlineData("--screenshot", "a", "a.png", "--player", "yes")]
    public void Parse_PlayerRepeatedOrMisplaced_Throws(params string[] args)
    {
        Assert.Throws<ArgumentException>(() => ScreenshotArgs.Parse(args));
    }

    [Theory]
    [InlineData("--screenshot", "a", "a.png", "--player", "--inspect")]
    [InlineData("--inspect", "--screenshot", "a", "a.png", "--player")]
    public void Parse_PlayerWithInspect_Throws(params string[] args)
    {
        var ex = Assert.Throws<ArgumentException>(() => ScreenshotArgs.Parse(args));
        Assert.Contains("--player", ex.Message);
        Assert.Contains("--inspect", ex.Message);
    }

    [Fact]
    public void Parse_UnknownArgument_ListsPlayerInUsage()
    {
        var ex = Assert.Throws<ArgumentException>(() => ScreenshotArgs.Parse(["--screenshot", "a", "a.png", "--players"]));
        Assert.Contains("[--player]", ex.Message);
    }

    // ----- --death (docs/specs/world-edge-game.md, "Screenshots" and criterion 25) -----

    [Theory]
    [InlineData("fall", DeathShot.Fall)]
    [InlineData("screen", DeathShot.Screen)]
    public void Ac25_Parse_Death_FallOrScreen(string value, DeathShot expected)
    {
        var args = ScreenshotArgs.Parse(["--screenshot", "world-edge", "a.png", "--death", value]);

        Assert.Equal(new ScreenshotArgs("world-edge", "a.png", null, Death: expected), args);
    }

    [Fact]
    public void Ac25_Parse_Death_InAnyPositionWithOtherFlags()
    {
        var before = ScreenshotArgs.Parse(["--death", "screen", "--map", "somero", "--screenshot", "world-edge", "a.png", "--seed", "1"]);
        var between = ScreenshotArgs.Parse(["--screenshot", "world-edge", "a.png", "--orthophoto", "--death", "fall", "--road-graph"]);

        Assert.Equal(new ScreenshotArgs("world-edge", "a.png", null, "somero", Death: DeathShot.Screen), before);
        Assert.Equal(new ScreenshotArgs("world-edge", "a.png", null, null, Orthophoto: true, RoadGraph: true, Death: DeathShot.Fall), between);
    }

    [Fact]
    public void Ac25_Parse_WithoutDeath_LeavesItNull()
    {
        Assert.Null(ScreenshotArgs.Parse(["--screenshot", "a", "a.png", "--player"])!.Death);
    }

    [Fact]
    public void Ac25_Parse_WithoutScreenshotFlag_IgnoresDeath()
    {
        Assert.Null(ScreenshotArgs.Parse(["--death", "fall"]));
        Assert.Null(ScreenshotArgs.Parse(["--death", "nonsense"]));
    }

    [Theory]
    [InlineData("--screenshot", "a", "a.png", "--death")]
    [InlineData("--screenshot", "a", "a.png", "--death", "")]
    [InlineData("--screenshot", "a", "a.png", "--death", " ")]
    [InlineData("--screenshot", "a", "a.png", "--death", "--map", "x")]
    public void Ac25_Parse_DeathValueMissing_ThrowsNamingTheValues(params string[] args)
    {
        var ex = Assert.Throws<ArgumentException>(() => ScreenshotArgs.Parse(args));
        Assert.Contains("--death", ex.Message);
        Assert.Contains("fall", ex.Message);
        Assert.Contains("screen", ex.Message);
    }

    [Theory]
    [InlineData("Fall")]
    [InlineData("SCREEN")]
    [InlineData("falling")]
    [InlineData("respawn")]
    [InlineData("1")]
    public void Ac25_Parse_DeathOtherValue_ThrowsNamingIt(string value)
    {
        var ex = Assert.Throws<ArgumentException>(() => ScreenshotArgs.Parse(["--screenshot", "a", "a.png", "--death", value]));
        Assert.Contains("--death", ex.Message);
        Assert.Contains($"'{value}'", ex.Message);
    }

    [Theory]
    [InlineData("--screenshot", "a", "a.png", "--death", "fall", "--death", "screen")]
    [InlineData("--screenshot", "a", "a.png", "--death", "fall", "--death", "fall")]
    [InlineData("--death", "screen", "--screenshot", "a", "a.png", "--death", "screen")]
    public void Ac25_Parse_DeathRepeated_Throws(params string[] args)
    {
        var ex = Assert.Throws<ArgumentException>(() => ScreenshotArgs.Parse(args));
        Assert.Contains("--death", ex.Message);
        Assert.Contains("more than once", ex.Message);
    }

    [Theory]
    [InlineData("--screenshot", "a", "a.png", "--death", "fall", "--player")]
    [InlineData("--player", "--screenshot", "a", "a.png", "--death", "screen")]
    public void Ac25_Parse_DeathWithPlayer_ThrowsNamingBoth(params string[] args)
    {
        var ex = Assert.Throws<ArgumentException>(() => ScreenshotArgs.Parse(args));
        Assert.Contains("--death", ex.Message);
        Assert.Contains("--player", ex.Message);
    }

    [Theory]
    [InlineData("--screenshot", "a", "a.png", "--death", "fall", "--inspect")]
    [InlineData("--inspect", "--screenshot", "a", "a.png", "--death", "screen")]
    public void Ac25_Parse_DeathWithInspect_ThrowsNamingBoth(params string[] args)
    {
        var ex = Assert.Throws<ArgumentException>(() => ScreenshotArgs.Parse(args));
        Assert.Contains("--death", ex.Message);
        Assert.Contains("--inspect", ex.Message);
    }

    [Fact]
    public void Ac25_Parse_UnknownArgument_ListsDeathInUsage()
    {
        var ex = Assert.Throws<ArgumentException>(() => ScreenshotArgs.Parse(["--screenshot", "a", "a.png", "--dead"]));
        Assert.Contains("[--death <fall|screen>]", ex.Message);
    }

    [Theory]
    [InlineData(3, 3, null)]
    [InlineData(2, 3, ScreenshotExitCode.Error)]
    [InlineData(0, 3, ScreenshotExitCode.Error)]
    [InlineData(0, 0, ScreenshotExitCode.Error)]
    [InlineData(0, null, ScreenshotExitCode.Error)]
    public void AfterMapLoad_OnlyACompleteLoadGoesOn(int loaded, int? manifestTiles, int? expected)
    {
        Assert.Equal(expected, ScreenshotExitCode.AfterMapLoad(loaded, manifestTiles));
    }

    [Theory]
    [InlineData(2, 3)]
    [InlineData(0, null)]
    public void AfterMapLoad_IncompleteLoad_NeverGivesOutsideBuiltArea(int loaded, int? manifestTiles)
    {
        Assert.NotEqual(ScreenshotExitCode.OutsideBuiltArea, ScreenshotExitCode.AfterMapLoad(loaded, manifestTiles));
    }

    [Theory]
    [InlineData("--screenshot")]
    [InlineData("--screenshot", "overview")]
    [InlineData("--screenshot", "overview", "--presets", "p.json")]
    [InlineData("--screenshot", "--presets", "out.png")]
    [InlineData("--screenshot", "overview", "")]
    public void Parse_MissingValues_Throw(params string[] args)
    {
        Assert.Throws<ArgumentException>(() => ScreenshotArgs.Parse(args));
    }

    [Theory]
    [InlineData("--screenshot", "overview", "out.png", "extra")]
    [InlineData("--screenshot", "a", "a.png", "--screenshot", "b", "b.png")]
    [InlineData("--screenshot", "a", "a.png", "--presets", "p.json", "--presets", "q.json")]
    [InlineData("--screenshot", "a", "a.png", "--verbose")]
    public void Parse_ExtraOrRepeatedArguments_Throw(params string[] args)
    {
        Assert.Throws<ArgumentException>(() => ScreenshotArgs.Parse(args));
    }

    // ----- --seed (docs/specs/player-on-foot.md, criterion 7): skipped here, read by SeedArg -----

    [Fact]
    public void Parse_Seed_InAnyPositionAlongsideScreenshotFlags_IsSkipped()
    {
        var after = ScreenshotArgs.Parse(["--screenshot", "a", "a.png", "--seed", "42"]);
        var before = ScreenshotArgs.Parse(["--seed", "-7", "--map", "x", "--screenshot", "a", "a.png", "--inspect"]);
        var between = ScreenshotArgs.Parse(["--screenshot", "a", "a.png", "--orthophoto", "--seed", "0", "--road-graph"]);

        Assert.Equal(new ScreenshotArgs("a", "a.png", null), after);
        Assert.Equal(new ScreenshotArgs("a", "a.png", null, "x", Inspect: true), before);
        Assert.Equal(new ScreenshotArgs("a", "a.png", null, null, Orthophoto: true, RoadGraph: true), between);
    }

    [Fact]
    public void Parse_WithoutScreenshotFlag_IgnoresSeed()
    {
        Assert.Null(ScreenshotArgs.Parse(["--seed", "3"]));
    }

    [Theory]
    [InlineData("--screenshot", "a", "a.png", "--seed")]
    [InlineData("--screenshot", "a", "a.png", "--seed", "")]
    [InlineData("--screenshot", "a", "a.png", "--seed", "abc")]
    [InlineData("--screenshot", "a", "a.png", "--seed", "1.5")]
    public void Parse_SeedValueMissingOrNotAnInteger_Throws(params string[] args)
    {
        var ex = Assert.Throws<ArgumentException>(() => ScreenshotArgs.Parse(args));
        Assert.Contains("--seed", ex.Message);
    }

    [Fact]
    public void Parse_SeedValueIsConsumed_SoItIsNotTakenAsAnUnknownArgument()
    {
        // "-5" alone would be an unknown argument; after --seed it is the seed's value.
        Assert.NotNull(ScreenshotArgs.Parse(["--screenshot", "a", "a.png", "--seed", "-5"]));
        Assert.Throws<ArgumentException>(() => ScreenshotArgs.Parse(["--screenshot", "a", "a.png", "-5"]));
    }

    [Theory]
    [InlineData("--screenshot", "a", "a.png", "--seed", "1", "--verbose")]
    [InlineData("--screenshot", "a", "a.png", "--seeds", "1")]
    [InlineData("--screenshot", "a", "a.png", "--seed", "1", "2")]
    public void Parse_WithSeed_OtherUnknownArgumentsStillThrow(params string[] args)
    {
        var ex = Assert.Throws<ArgumentException>(() => ScreenshotArgs.Parse(args));
        Assert.Contains("unexpected argument", ex.Message);
    }

    [Theory]
    [InlineData("--seed", "1", "--screenshot", "a", "a.png", "--seed", "2")] // one before, one after --screenshot
    [InlineData("--screenshot", "a", "a.png", "--seed", "1", "--seed", "2")] // both after
    [InlineData("--seed", "1", "--seed", "2", "--screenshot", "a", "a.png")] // both before
    [InlineData("--screenshot", "a", "a.png", "--seed", "1", "--inspect", "--seed", "1")] // same value twice
    public void Parse_SeedRepeated_ThrowsNamingSeed(params string[] args)
    {
        // Criterion 7: a repeated --seed is an argument error of the screenshot run, as for every other flag.
        var ex = Assert.Throws<ArgumentException>(() => ScreenshotArgs.Parse(args));
        Assert.Contains("--seed", ex.Message);
        Assert.Contains("more than once", ex.Message);
    }

    [Fact]
    public void Parse_UnknownArgument_ListsSeedInUsage()
    {
        var ex = Assert.Throws<ArgumentException>(() => ScreenshotArgs.Parse(["--screenshot", "a", "a.png", "--verbose"]));
        Assert.Contains("[--seed <n>]", ex.Message);
    }

    [Fact]
    public void Parse_OutputNotPng_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() => ScreenshotArgs.Parse(["--screenshot", "a", "a.jpg"]));
        Assert.Contains(".png", ex.Message);
    }

    // ----- --menu (docs/specs/menu-settings-save.md, "Screenshots" and criterion 24) -----

    [Theory]
    [InlineData("main", MenuShot.Main)]
    [InlineData("pause", MenuShot.Pause)]
    [InlineData("settings", MenuShot.Settings)]
    [InlineData("slots", MenuShot.Slots)]
    [InlineData("inventory", MenuShot.Inventory)] // docs/specs/inventory.md, "Screenshots"
    public void Ac24_Parse_Menu_EachOfTheFiveValues(string value, MenuShot expected)
    {
        var args = ScreenshotArgs.Parse(["--screenshot", "overview", "a.png", "--menu", value]);

        Assert.Equal(new ScreenshotArgs("overview", "a.png", null, Menu: expected), args);
    }

    [Fact]
    public void Ac24_Parse_Menu_InAnyPositionWithMapAndSeed()
    {
        var args = ScreenshotArgs.Parse(["--menu", "slots", "--map", "somero", "--screenshot", "overview", "a.png", "--seed", "3"]);

        Assert.Equal(new ScreenshotArgs("overview", "a.png", null, "somero", Menu: MenuShot.Slots), args);
    }

    [Fact]
    public void Ac24_Parse_WithoutMenu_LeavesItNull()
    {
        Assert.Null(ScreenshotArgs.Parse(["--screenshot", "a", "a.png"])!.Menu);
    }

    [Theory]
    [InlineData("Main")]
    [InlineData("PAUSE")]
    [InlineData("setting")]
    [InlineData("load")]
    [InlineData("1")]
    public void Ac24_Parse_MenuOtherValue_ThrowsNamingIt(string value)
    {
        var ex = Assert.Throws<ArgumentException>(() => ScreenshotArgs.Parse(["--screenshot", "a", "a.png", "--menu", value]));
        Assert.Contains("--menu", ex.Message);
        Assert.Contains($"'{value}'", ex.Message);
    }

    [Theory]
    [InlineData("--screenshot", "a", "a.png", "--menu")]
    [InlineData("--screenshot", "a", "a.png", "--menu", "")]
    [InlineData("--screenshot", "a", "a.png", "--menu", "--map", "x")]
    public void Ac24_Parse_MenuValueMissing_ThrowsNamingTheValues(params string[] args)
    {
        var ex = Assert.Throws<ArgumentException>(() => ScreenshotArgs.Parse(args));
        Assert.Contains("--menu", ex.Message);
        Assert.Contains("main|pause|settings|slots", ex.Message);
    }

    [Theory]
    [InlineData("--screenshot", "a", "a.png", "--menu", "main", "--menu", "pause")]
    [InlineData("--screenshot", "a", "a.png", "--menu", "settings", "--menu", "settings")]
    [InlineData("--menu", "slots", "--screenshot", "a", "a.png", "--menu", "slots")]
    public void Ac24_Parse_MenuRepeated_Throws(params string[] args)
    {
        var ex = Assert.Throws<ArgumentException>(() => ScreenshotArgs.Parse(args));
        Assert.Contains("--menu", ex.Message);
        Assert.Contains("more than once", ex.Message);
    }

    [Theory]
    [InlineData("--player", "--player")]
    [InlineData("--inspect", "--inspect")]
    [InlineData("--death", "--death", "fall")]
    [InlineData("--death", "--death", "screen")]
    [InlineData("--orthophoto", "--orthophoto")]
    [InlineData("--road-graph", "--road-graph")]
    public void Ac24_Parse_MenuWithAnotherDebugFlag_ThrowsNamingBoth(string other, params string[] flag)
    {
        foreach (var menu in new[] { "main", "pause", "settings", "slots", "inventory" })
        {
            string[] after = ["--screenshot", "a", "a.png", "--menu", menu, .. flag];
            string[] before = [.. flag, "--screenshot", "a", "a.png", "--menu", menu];
            foreach (var args in new[] { after, before })
            {
                var ex = Assert.Throws<ArgumentException>(() => ScreenshotArgs.Parse(args));
                Assert.Contains("--menu", ex.Message);
                Assert.Contains(other, ex.Message);
            }
        }
    }

    [Fact]
    public void Ac24_Parse_UnknownArgument_ListsMenuInUsage()
    {
        var ex = Assert.Throws<ArgumentException>(() => ScreenshotArgs.Parse(["--screenshot", "a", "a.png", "--menus"]));
        Assert.Contains("[--menu <main|pause|settings|slots|inventory>]", ex.Message);
    }

    // ----- --give (docs/specs/inventory.md, criterion 13: with --screenshot an argument error exits 1) -----

    [Fact]
    public void InventoryAc13_Parse_GiveValidList_IsAcceptedAndSkipped()
    {
        var args = ScreenshotArgs.Parse(["--screenshot", "overview", "a.png", "--give", "crowbar,hoodie", "--menu", "inventory"]);

        Assert.Equal(new ScreenshotArgs("overview", "a.png", null, Menu: MenuShot.Inventory), args);
    }

    [Fact]
    public void InventoryAc13_Parse_GiveBeforeScreenshotWithSeedAndMap_IsAccepted()
    {
        var args = ScreenshotArgs.Parse(
            ["--give", "baseball_bat,tracksuit,phone,bike_lock", "--seed", "1", "--screenshot", "overview", "a.png", "--map", "somero"]);

        Assert.Equal(new ScreenshotArgs("overview", "a.png", null, "somero"), args);
    }

    [Fact]
    public void InventoryAc13_Parse_GiveRepeatedId_IsAccepted()
    {
        Assert.NotNull(ScreenshotArgs.Parse(["--screenshot", "a", "a.png", "--give", "crowbar,crowbar"]));
    }

    [Theory]
    [InlineData("unknown id", "--screenshot", "a", "a.png", "--give", "sword")]
    [InlineData("unknown id among known", "--screenshot", "a", "a.png", "--give", "crowbar,laser_saw")]
    [InlineData("id in the wrong case", "--screenshot", "a", "a.png", "--give", "Crowbar")]
    [InlineData("blank entry", "--screenshot", "a", "a.png", "--give", "crowbar,,hoodie")]
    [InlineData("trailing comma", "--screenshot", "a", "a.png", "--give", "crowbar,")]
    [InlineData("space entry", "--screenshot", "a", "a.png", "--give", "crowbar, ")]
    [InlineData("empty list", "--screenshot", "a", "a.png", "--give", "")]
    [InlineData("flag without value at the end", "--screenshot", "a", "a.png", "--give")]
    [InlineData("flag followed by another flag", "--screenshot", "a", "a.png", "--give", "--map", "somero")]
    [InlineData("repeated flag", "--screenshot", "a", "a.png", "--give", "crowbar", "--give", "hoodie")]
    [InlineData("repeated flag, same list", "--give", "crowbar", "--screenshot", "a", "a.png", "--give", "crowbar")]
    public void InventoryAc13_Parse_GiveArgumentError_Throws(string why, params string[] args)
    {
        var ex = Assert.Throws<ArgumentException>(() => ScreenshotArgs.Parse(args));
        Assert.True(ex.Message.Contains("--give", StringComparison.Ordinal), $"{why}: {ex.Message}");
    }

    [Theory]
    [InlineData("sword")]
    [InlineData("laser_saw")]
    public void InventoryAc13_Parse_GiveUnknownId_ErrorNamesTheId(string id)
    {
        var ex = Assert.Throws<ArgumentException>(() => ScreenshotArgs.Parse(["--screenshot", "a", "a.png", "--give", id]));
        Assert.Contains($"'{id}'", ex.Message);
    }

    [Fact]
    public void InventoryAc13_Parse_WithoutScreenshotFlag_IgnoresBadGive()
    {
        Assert.Null(ScreenshotArgs.Parse(["--give", "sword"]));
    }

    [Fact]
    public void InventoryAc13_Parse_UnknownArgument_ListsGiveInUsage()
    {
        var ex = Assert.Throws<ArgumentException>(() => ScreenshotArgs.Parse(["--screenshot", "a", "a.png", "--gives"]));
        Assert.Contains("[--give <id,...>]", ex.Message);
    }
}
