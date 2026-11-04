using System.Globalization;
using Roadman.Core.Edge;

namespace Roadman.Core.Tests.Edge;

/// <summary><see cref="DeathScreenText"/>: <c>docs/specs/world-edge-game.md</c>, acceptance criterion 5.</summary>
public class DeathScreenTextTests
{
    [Fact]
    public void Format_MoneyAndTwoItems_GivesThreeLines() // criterion 5
    {
        var lines = DeathScreenText.Format(new DeathLosses(120, ["knife", "phone"]));

        Assert.Equal("The Earth is flat", lines.Title);
        Assert.Equal("You fell off the edge of the world", lines.Subtitle);
        Assert.Equal("Money lost: 120 · Gear lost: knife, phone", lines.Losses);
    }

    [Fact]
    public void Format_UnsortedItems_ListsThemInOrdinalOrder() // criterion 5
    {
        Assert.Equal(
            "Money lost: 120 · Gear lost: knife, phone",
            DeathScreenText.Format(new DeathLosses(120, ["phone", "knife"])).Losses);
        Assert.Equal(
            "Money lost: 5 · Gear lost: Zippo, knife",
            DeathScreenText.Format(new DeathLosses(5, ["knife", "Zippo"])).Losses);
    }

    [Fact]
    public void Format_MoneyOnly_SaysGearNone() // criterion 5
    {
        var lines = DeathScreenText.Format(new DeathLosses(250, []));

        Assert.Equal("The Earth is flat", lines.Title);
        Assert.Equal("You fell off the edge of the world", lines.Subtitle);
        Assert.Equal("Money lost: 250 · Gear lost: none", lines.Losses);
    }

    [Fact]
    public void Format_NothingLost_SaysZeroAndNone() // criterion 5
    {
        var lines = DeathScreenText.Format(new DeathLosses(0, []));

        Assert.Equal("The Earth is flat", lines.Title);
        Assert.Equal("You fell off the edge of the world", lines.Subtitle);
        Assert.Equal("Money lost: 0 · Gear lost: none", lines.Losses);
    }

    [Fact]
    public void Format_LargeMoneyUnderFinnishCulture_HasNoSeparators() // criterion 5
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fi-FI");
            Assert.Equal(
                "Money lost: 1234567 · Gear lost: none",
                DeathScreenText.Format(new DeathLosses(1234567, [])).Losses);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Constants_AreTheSpecsWording()
    {
        Assert.Equal("The Earth is flat", DeathScreenText.Title);
        Assert.Equal("You fell off the edge of the world", DeathScreenText.Subtitle);
    }

    [Fact]
    public void Format_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => DeathScreenText.Format(null!));
    }
}
