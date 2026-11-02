using Roadman.Core.Tools;

namespace Roadman.Core.Tests.Tools;

/// <summary>
/// <c>--seed &lt;n&gt;</c> (<c>docs/specs/player-on-foot.md</c>, "Spawn" and criterion 7): the user argument that seeds
/// the spawn draws.
/// </summary>
public class SeedArgTests
{
    [Theory]
    [InlineData]
    [InlineData("--screenshot", "overview", "a.png")]
    [InlineData("--map", "somero", "--inspect")]
    public void Parse_WithoutSeedFlag_ReturnsNull(params string[] args)
    {
        Assert.Null(SeedArg.Parse(args));
    }

    [Theory]
    [InlineData("0", 0)]
    [InlineData("42", 42)]
    [InlineData("-7", -7)]
    [InlineData("+5", 5)]
    [InlineData("2147483647", int.MaxValue)]
    [InlineData("-2147483648", int.MinValue)]
    public void Parse_ValidValue_ReturnsIt(string value, int expected)
    {
        Assert.Equal(expected, SeedArg.Parse(["--seed", value]));
    }

    [Fact]
    public void Parse_AmongScreenshotArguments_FindsTheSeed()
    {
        var before = SeedArg.Parse(["--seed", "12", "--screenshot", "overview", "a.png", "--map", "somero"]);
        var after = SeedArg.Parse(["--screenshot", "overview", "a.png", "--inspect", "--seed", "-3"]);

        Assert.Equal(12, before);
        Assert.Equal(-3, after);
    }

    [Theory]
    [InlineData("--seed")]
    [InlineData("--seed", "")]
    [InlineData("--seed", " ")]
    [InlineData("--screenshot", "a", "a.png", "--seed")]
    public void Parse_MissingValue_Throws(params string[] args)
    {
        var ex = Assert.Throws<ArgumentException>(() => SeedArg.Parse(args));
        Assert.Contains("--seed", ex.Message);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("1.5")]
    [InlineData("1e3")]
    [InlineData("0x10")]
    [InlineData("2147483648")]
    [InlineData("-2147483649")]
    [InlineData("--map")]
    public void Parse_NonIntegerOrOutOfRangeValue_ThrowsNamingIt(string value)
    {
        var ex = Assert.Throws<ArgumentException>(() => SeedArg.Parse(["--seed", value]));
        Assert.Contains("--seed", ex.Message);
        Assert.Contains(value, ex.Message);
    }

    [Theory]
    [InlineData("--seed", "1", "--seed", "1")]
    [InlineData("--seed", "1", "--seed", "2")]
    [InlineData("--seed", "1", "--screenshot", "a", "a.png", "--seed", "2")]
    public void Parse_Repeated_Throws(params string[] args)
    {
        var ex = Assert.Throws<ArgumentException>(() => SeedArg.Parse(args));
        Assert.Contains("more than once", ex.Message);
    }

    [Fact]
    public void Parse_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => SeedArg.Parse(null!));
    }
}
