using Roadman.Core.Saves;

namespace Roadman.Core.Tests.Saves;

/// <summary>
/// <see cref="PlayTime"/> and <see cref="PlayTimeText"/>: <c>docs/specs/menu-settings-save.md</c>, acceptance
/// criterion 5.
/// </summary>
public class PlayTimeTests
{
    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(300, "0:05")]
    [InlineData(4980, "1:23")]
    [InlineData(90420, "25:07")]
    public void Format_SpecExamples_GiveHoursAndMinutes(double seconds, string expected)
    {
        Assert.Equal(expected, PlayTimeText.Format(seconds));
    }

    [Theory]
    [InlineData(59.99, "0:00")]
    [InlineData(3599.9, "0:59")]
    [InlineData(3600, "1:00")]
    public void Format_PartialMinutes_RoundDown(double seconds, string expected)
    {
        Assert.Equal(expected, PlayTimeText.Format(seconds));
    }

    [Theory]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Format_NegativeOrNonFinite_Throws(double seconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PlayTimeText.Format(seconds));
    }

    [Fact]
    public void Advance_SumsSteps()
    {
        var time = new PlayTime();

        time.Advance(0.5);
        time.Advance(0.25);
        time.Advance(0);
        time.Advance(1);

        Assert.Equal(1.75, time.Seconds);
    }

    [Fact]
    public void Advance_StartsFromTheSavedPlayTime()
    {
        var time = new PlayTime(4980);

        time.Advance(20);

        Assert.Equal(5000, time.Seconds);
    }

    [Fact]
    public void New_StartsAtZero()
    {
        Assert.Equal(0, new PlayTime().Seconds);
    }

    [Theory]
    [InlineData(-0.001)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Advance_NegativeOrNonFiniteStep_ThrowsAndKeepsTheTime(double delta)
    {
        var time = new PlayTime(10);

        Assert.Throws<ArgumentOutOfRangeException>(() => time.Advance(delta));

        Assert.Equal(10, time.Seconds);
    }

    [Theory]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void New_NegativeOrNonFiniteStart_Throws(double start)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PlayTime(start));
    }
}
