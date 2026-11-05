using Roadman.Core.Tools;

namespace Roadman.Core.Tests.Tools;

/// <summary>
/// <c>--new-game</c> (<c>docs/specs/menu-settings-save.md</c>, "Startup and scenes"; used by criterion 8's headless
/// run): starts a new game at once, skipping the main menu.
/// </summary>
public class NewGameArgTests
{
    [Theory]
    [InlineData]
    [InlineData("--seed", "3")]
    [InlineData("--map", "somero")]
    [InlineData("--new-gam")]
    [InlineData("new-game")]
    public void Parse_WithoutTheFlag_IsFalse(params string[] args)
    {
        Assert.False(NewGameArg.Parse(args));
    }

    [Theory]
    [InlineData("--new-game")]
    [InlineData("--new-game", "--seed", "3")]
    [InlineData("--seed", "3", "--new-game")]
    public void Parse_WithTheFlag_IsTrue(params string[] args)
    {
        Assert.True(NewGameArg.Parse(args));
    }

    [Fact]
    public void Parse_WithSeed_LeavesTheSeedToSeedArg()
    {
        string[] args = ["--new-game", "--seed", "3"];

        Assert.True(NewGameArg.Parse(args));
        Assert.Equal(3, SeedArg.Parse(args));
    }

    [Fact]
    public void Parse_FlagRepeated_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() => NewGameArg.Parse(["--new-game", "--seed", "3", "--new-game"]));

        Assert.Contains("--new-game", ex.Message);
    }

    [Fact]
    public void Parse_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => NewGameArg.Parse(null!));
    }
}
