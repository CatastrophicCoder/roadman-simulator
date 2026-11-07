using Roadman.Core.Tools;

namespace Roadman.Core.Tests.Tools;

/// <summary>
/// The performance tool's arguments (<c>docs/specs/world-edge-game.md</c>, "Performance", criterion 27; Part B plan
/// amendments 3 and 6): <c>--perf &lt;spawn|roadblock&gt; &lt;out.json&gt;</c> with its options.
/// </summary>
public class PerfArgsTests
{
    [Theory]
    [InlineData]
    [InlineData("--screenshot", "overview", "a.png")]
    [InlineData("--no-world-edge", "--frames", "abc")]
    [InlineData("--some-runner-flag", "value")]
    public void Parse_WithoutPerfFlag_ReturnsNullAndIgnoresOtherArgs(params string[] args)
    {
        Assert.Null(PerfArgs.Parse(args));
    }

    [Fact]
    public void Parse_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => PerfArgs.Parse(null!));
    }

    [Theory]
    [InlineData("spawn", PerfSpot.Spawn)]
    [InlineData("roadblock", PerfSpot.Roadblock)]
    public void Ac27_Parse_Spot_WithDefaults(string value, PerfSpot expected)
    {
        var args = PerfArgs.Parse(["--perf", value, "build/perf/out.json"]);

        Assert.Equal(new PerfArgs(expected, "build/perf/out.json"), args);
        Assert.Null(args!.MapId);
        Assert.True(args.WorldEdge);
        Assert.True(args.SpaceSky);
        Assert.Equal(120, args.WarmupFrames);
        Assert.Equal(600, args.Frames); // the spec's 600 frames
    }

    [Fact]
    public void Ac27_DefaultFrameCounts_AreTheSpecs()
    {
        Assert.Equal(120, PerfArgs.DefaultWarmupFrames);
        Assert.Equal(600, PerfArgs.DefaultFrames);
    }

    [Fact]
    public void Ac27_RoadblockId_IsRoad52sNorthRoadblock()
    {
        // The spec names it: "beside the road 52 north roadblock (roadblock:cut:way/1445392542:0)".
        Assert.Equal("roadblock:cut:way/1445392542:0", PerfArgs.RoadblockId);
    }

    [Theory]
    [InlineData("out.JSON")]
    [InlineData("/abs/path/out.json")]
    public void Parse_OutputJsonAnyCase_IsAccepted(string output)
    {
        Assert.Equal(output, PerfArgs.Parse(["--perf", "spawn", output])!.OutputPath);
    }

    [Theory]
    [InlineData("out.png")]
    [InlineData("out.json.txt")]
    [InlineData("out")]
    public void Parse_OutputNotJson_ThrowsNamingIt(string output)
    {
        var ex = Assert.Throws<ArgumentException>(() => PerfArgs.Parse(["--perf", "spawn", output]));
        Assert.Contains(".json", ex.Message);
        Assert.Contains(output, ex.Message);
    }

    [Theory]
    [InlineData("--perf")]
    [InlineData("--perf", "spawn")]
    [InlineData("--perf", "spawn", "")]
    [InlineData("--perf", "spawn", "--no-world-edge")]
    [InlineData("--perf", "--no-world-edge", "out.json")]
    public void Parse_SpotOrOutputMissing_Throws(params string[] args)
    {
        var ex = Assert.Throws<ArgumentException>(() => PerfArgs.Parse(args));
        Assert.Contains("--perf", ex.Message);
    }

    [Theory]
    [InlineData("Spawn")]
    [InlineData("road52")]
    [InlineData("roadblocks")]
    public void Parse_OtherSpot_ThrowsNamingIt(string spot)
    {
        var ex = Assert.Throws<ArgumentException>(() => PerfArgs.Parse(["--perf", spot, "out.json"]));
        Assert.Contains($"'{spot}'", ex.Message);
        Assert.Contains("spawn", ex.Message);
        Assert.Contains("roadblock", ex.Message);
    }

    [Fact]
    public void Ac27_Parse_NoWorldEdge_SwitchesOffTheWorldEdgeAndTheSpaceSky()
    {
        var args = PerfArgs.Parse(["--perf", "spawn", "o.json", "--no-world-edge"])!;

        Assert.False(args.WorldEdge);
        Assert.False(args.SpaceSky);
    }

    [Fact]
    public void Ac27_Parse_NoSpaceSky_SwitchesOffOnlyTheSky()
    {
        var args = PerfArgs.Parse(["--perf", "roadblock", "o.json", "--no-space-sky"])!;

        Assert.True(args.WorldEdge);
        Assert.False(args.SpaceSky);
    }

    [Fact]
    public void Ac27_Parse_BothSwitches_SwitchOffBoth()
    {
        var args = PerfArgs.Parse(["--no-space-sky", "--perf", "spawn", "o.json", "--no-world-edge"])!;

        Assert.False(args.WorldEdge);
        Assert.False(args.SpaceSky);
    }

    [Fact]
    public void Parse_AllOptions_InAnyPosition()
    {
        var args = PerfArgs.Parse(
            ["--frames", "30", "--map", "somero", "--seed", "-4", "--perf", "roadblock", "o.json", "--warmup", "0", "--no-space-sky"]);

        Assert.Equal(new PerfArgs(PerfSpot.Roadblock, "o.json", "somero", WorldEdge: true, SpaceSky: false, WarmupFrames: 0, Frames: 30), args);
    }

    [Theory]
    [InlineData("0", 0)]
    [InlineData("1", 1)]
    [InlineData("240", 240)]
    public void Parse_Warmup_ZeroOrMore(string value, int expected)
    {
        Assert.Equal(expected, PerfArgs.Parse(["--perf", "spawn", "o.json", "--warmup", value])!.WarmupFrames);
    }

    [Theory]
    [InlineData("1", 1)]
    [InlineData("600", 600)]
    [InlineData("2147483647", int.MaxValue)]
    public void Parse_Frames_OneOrMore(string value, int expected)
    {
        Assert.Equal(expected, PerfArgs.Parse(["--perf", "spawn", "o.json", "--frames", value])!.Frames);
    }

    [Theory]
    [InlineData("--warmup", "-1")]
    [InlineData("--warmup", "+5")]
    [InlineData("--warmup", "1.5")]
    [InlineData("--warmup", "abc")]
    [InlineData("--warmup", "2147483648")]
    [InlineData("--frames", "0")]
    [InlineData("--frames", "-3")]
    [InlineData("--frames", "1e3")]
    [InlineData("--frames", " 5")]
    public void Parse_FrameCountOutOfRangeOrNotAWholeNumber_ThrowsNamingTheFlag(string flag, string value)
    {
        var ex = Assert.Throws<ArgumentException>(() => PerfArgs.Parse(["--perf", "spawn", "o.json", flag, value]));
        Assert.Contains(flag, ex.Message);
    }

    [Theory]
    [InlineData("--warmup")]
    [InlineData("--frames")]
    public void Parse_FrameCountMissing_ThrowsNamingTheFlag(string flag)
    {
        var atEnd = Assert.Throws<ArgumentException>(() => PerfArgs.Parse(["--perf", "spawn", "o.json", flag]));
        var beforeFlag = Assert.Throws<ArgumentException>(() => PerfArgs.Parse(["--perf", "spawn", "o.json", flag, "--no-space-sky"]));
        Assert.Contains(flag, atEnd.Message);
        Assert.Contains(flag, beforeFlag.Message);
    }

    [Fact]
    public void Parse_Seed_IsAcceptedAndSkipped()
    {
        var args = PerfArgs.Parse(["--perf", "spawn", "o.json", "--seed", "42"]);

        Assert.Equal(new PerfArgs(PerfSpot.Spawn, "o.json"), args);
        Assert.Equal(42, SeedArg.Parse(["--perf", "spawn", "o.json", "--seed", "42"]));
    }

    [Theory]
    [InlineData("--perf", "spawn", "o.json", "--seed")]
    [InlineData("--perf", "spawn", "o.json", "--seed", "abc")]
    [InlineData("--perf", "spawn", "o.json", "--seed", "1.5")]
    [InlineData("--perf", "spawn", "o.json", "--seed", "1", "--seed", "2")]
    public void Parse_SeedMissingNotAnIntegerOrRepeated_ThrowsNamingSeed(params string[] args)
    {
        var ex = Assert.Throws<ArgumentException>(() => PerfArgs.Parse(args));
        Assert.Contains("--seed", ex.Message);
    }

    [Theory]
    [InlineData("other-town")]
    [InlineData("x_1")]
    public void Parse_MapSlug_IsKept(string mapId)
    {
        Assert.Equal(mapId, PerfArgs.Parse(["--perf", "spawn", "o.json", "--map", mapId])!.MapId);
    }

    [Theory]
    [InlineData("Somero")]
    [InlineData("../somero")]
    [InlineData("some ro")]
    public void Parse_MapNotASlug_ThrowsNamingIt(string mapId)
    {
        var ex = Assert.Throws<ArgumentException>(() => PerfArgs.Parse(["--perf", "spawn", "o.json", "--map", mapId]));
        Assert.Contains(mapId, ex.Message);
    }

    [Theory]
    [InlineData("--perf", "spawn", "o.json", "--perf", "roadblock", "p.json")]
    [InlineData("--perf", "spawn", "o.json", "--map", "a", "--map", "b")]
    [InlineData("--perf", "spawn", "o.json", "--no-world-edge", "--no-world-edge")]
    [InlineData("--perf", "spawn", "o.json", "--no-space-sky", "--no-space-sky")]
    [InlineData("--perf", "spawn", "o.json", "--warmup", "1", "--warmup", "1")]
    [InlineData("--perf", "spawn", "o.json", "--frames", "5", "--frames", "6")]
    public void Parse_RepeatedFlag_Throws(params string[] args)
    {
        var ex = Assert.Throws<ArgumentException>(() => PerfArgs.Parse(args));
        Assert.Contains("more than once", ex.Message);
    }

    [Theory]
    [InlineData("--perf", "spawn", "o.json", "--verbose")]
    [InlineData("--perf", "spawn", "o.json", "extra")]
    [InlineData("--perf", "spawn", "o.json", "--screenshot", "a", "a.png")]
    [InlineData("--perf", "spawn", "o.json", "--no-world-edge", "yes")]
    public void Parse_UnknownArgument_ThrowsWithUsage(params string[] args)
    {
        var ex = Assert.Throws<ArgumentException>(() => PerfArgs.Parse(args));
        Assert.Contains("unexpected argument", ex.Message);
        Assert.Contains("--perf <spawn|roadblock> <out.json>", ex.Message);
        Assert.Contains("[--no-world-edge]", ex.Message);
        Assert.Contains("[--no-space-sky]", ex.Message);
    }

    [Fact]
    public void ExitCodes_AreZeroOneTwo()
    {
        Assert.Equal(0, PerfExitCode.Written);
        Assert.Equal(1, PerfExitCode.Error);
        Assert.Equal(2, PerfExitCode.NotApplicable);
    }

    [Fact]
    public void MinimapPerf_Parse_DefaultHasTheMinimap()
    {
        Assert.True(PerfArgs.Parse(["--perf", "spawn", "o.json"])!.Minimap);
    }

    [Fact]
    public void MinimapPerf_Parse_NoMinimap_TurnsOnlyTheMinimapOff()
    {
        var args = PerfArgs.Parse(["--perf", "roadblock", "o.json", "--no-minimap", "--seed", "1"])!;

        Assert.False(args.Minimap);
        Assert.True(args.WorldEdge);
        Assert.True(args.SpaceSky);
        Assert.Equal(PerfSpot.Roadblock, args.Spot);
    }

    [Fact]
    public void MinimapPerf_Parse_NoMinimapWithTheOtherSwitches_KeepsEach()
    {
        var args = PerfArgs.Parse(["--no-minimap", "--perf", "spawn", "o.json", "--no-world-edge"])!;

        Assert.False(args.Minimap);
        Assert.False(args.WorldEdge);
        Assert.False(args.SpaceSky);
    }

    [Fact]
    public void MinimapPerf_Parse_RepeatedNoMinimap_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => PerfArgs.Parse(["--perf", "spawn", "o.json", "--no-minimap", "--no-minimap"]));
        Assert.Contains("--no-minimap is given more than once", ex.Message);
    }

    [Fact]
    public void MinimapPerf_Parse_UnknownArgument_ListsNoMinimapInUsage()
    {
        var ex = Assert.Throws<ArgumentException>(() => PerfArgs.Parse(["--perf", "spawn", "o.json", "--minimap"]));
        Assert.Contains("[--no-minimap]", ex.Message);
    }
}
