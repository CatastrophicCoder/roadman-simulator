using Roadman.Core.Items;
using Roadman.Core.Tools;
using static Roadman.Core.Tests.Items.TestCatalogs;

namespace Roadman.Core.Tests.Tools;

/// <summary>
/// <c>--give &lt;id,...&gt;</c> parsing (<see cref="GiveArg"/>): <c>docs/specs/inventory.md</c>, "Getting items: debug
/// tools" and the parsing half of acceptance criterion 13. Applying the ids, the release-build case and the
/// screenshot exit code are gdUnit4 tests.
/// </summary>
public class GiveArgTests
{
    private static readonly string[] DefaultIds = ["baseball_bat", "crowbar", "hoodie", "tracksuit", "bike_lock", "phone"];

    [Fact]
    public void Flag_IsDashDashGive()
    {
        Assert.Equal("--give", GiveArg.Flag);
    }

    [Theory]
    [InlineData]
    [InlineData("--seed", "3")]
    [InlineData("--giv", "crowbar")]
    [InlineData("give", "crowbar")]
    [InlineData("--map", "somero", "--new-game")]
    public void Ac13_Parse_WithoutTheFlag_IsNotPresent(params string[] args)
    {
        var result = GiveArg.Parse(args, ItemCatalog.Default);

        Assert.False(result.Present);
        Assert.Empty(result.Ids);
        Assert.Null(result.Error);
    }

    [Fact]
    public void Ac13_Parse_TwoKnownIds_GivesBothInOrder()
    {
        var result = GiveArg.Parse(["--give", "crowbar,phone"], ItemCatalog.Default);

        Assert.True(result.Present);
        Assert.Equal(["crowbar", "phone"], result.Ids);
        Assert.Null(result.Error);
    }

    [Fact]
    public void Ac13_Parse_KeepsTheGivenOrder_NotCatalogueOrIdOrder()
    {
        var result = GiveArg.Parse(["--give", "phone,tracksuit,baseball_bat"], ItemCatalog.Default);

        Assert.Equal(["phone", "tracksuit", "baseball_bat"], result.Ids);
    }

    [Fact]
    public void Ac13_Parse_SingleId_IsAccepted()
    {
        Assert.Equal(["hoodie"], GiveArg.Parse(["--give", "hoodie"], ItemCatalog.Default).Ids);
    }

    [Fact]
    public void Ac13_Parse_RepeatedIds_CountOnce_FirstOccurrenceOrder()
    {
        var result = GiveArg.Parse(["--give", "phone,crowbar,phone,crowbar,hoodie"], ItemCatalog.Default);

        Assert.Equal(["phone", "crowbar", "hoodie"], result.Ids);
        Assert.Null(result.Error);
    }

    [Fact]
    public void Ac13_Parse_AmongOtherArgs_FindsTheFlagAndItsValue()
    {
        var result = GiveArg.Parse(
            ["--screenshot", "overview", "out.png", "--give", "baseball_bat,crowbar,tracksuit,hoodie,phone", "--seed", "1"],
            ItemCatalog.Default);

        Assert.Equal(["baseball_bat", "crowbar", "tracksuit", "hoodie", "phone"], result.Ids);
        Assert.Null(result.Error);
    }

    [Fact]
    public void Ac13_Parse_UsesTheGivenCatalogue()
    {
        Assert.Equal(["bat", "suit"], GiveArg.Parse(["--give", "bat,suit"], Mixed).Ids);
        Assert.NotNull(GiveArg.Parse(["--give", "bat"], ItemCatalog.Default).Error);
    }

    public static TheoryData<string, string[]> ArgumentErrors() => new()
    {
        { "no value", ["--give"] },
        { "no value before another flag", ["--give", "--seed", "3"] },
        { "empty value", ["--give", ""] },
        { "blank value", ["--give", " "] },
        { "blank entry in the middle", ["--give", "crowbar,,phone"] },
        { "trailing comma", ["--give", "crowbar,"] },
        { "leading comma", ["--give", ",crowbar"] },
        { "only a comma", ["--give", ","] },
        { "space after a comma", ["--give", "crowbar, phone"] },
        { "unknown id", ["--give", "sword"] },
        { "unknown among known", ["--give", "crowbar,sword,phone"] },
        { "wrong case", ["--give", "Crowbar"] },
        { "flag given twice", ["--give", "crowbar", "--give", "phone"] },
        { "flag given twice, same value", ["--give", "crowbar", "--seed", "1", "--give", "crowbar"] },
    };

    [Theory]
    [MemberData(nameof(ArgumentErrors))]
    public void Ac13_Parse_ArgumentError_GivesNothing_AndAnErrorListingTheKnownIds(string why, string[] args)
    {
        var result = GiveArg.Parse(args, ItemCatalog.Default);

        Assert.True(result.Present, why);
        Assert.Empty(result.Ids);
        Assert.False(string.IsNullOrWhiteSpace(result.Error), why);
        Assert.All(DefaultIds, id => Assert.Contains(id, result.Error!));
    }

    [Theory]
    [InlineData("sword", "sword")]
    [InlineData("crowbar,laser_saw", "laser_saw")]
    [InlineData("Crowbar", "Crowbar")]
    public void Ac13_Parse_UnknownId_ErrorNamesIt(string value, string unknown)
    {
        var result = GiveArg.Parse(["--give", value], ItemCatalog.Default);

        Assert.Contains(unknown, result.Error!);
    }

    [Fact]
    public void Ac13_Parse_Error_ListsTheGivenCataloguesIds()
    {
        var result = GiveArg.Parse(["--give", "sword"], Mixed);

        Assert.All(Mixed.All, item => Assert.Contains(item.Id, result.Error!));
    }

    [Fact]
    public void Ac13_Parse_FlagGivenTwice_ErrorNamesTheFlag()
    {
        var result = GiveArg.Parse(["--give", "crowbar", "--give", "phone"], ItemCatalog.Default);

        Assert.Contains("--give", result.Error!);
    }

    [Fact]
    public void Parse_NullArguments_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => GiveArg.Parse(null!, ItemCatalog.Default));
        Assert.Throws<ArgumentNullException>(() => GiveArg.Parse(["--give", "crowbar"], null!));
    }
}
