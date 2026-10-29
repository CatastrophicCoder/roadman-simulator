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

    [Fact]
    public void Parse_OutputNotPng_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() => ScreenshotArgs.Parse(["--screenshot", "a", "a.jpg"]));
        Assert.Contains(".png", ex.Message);
    }
}
