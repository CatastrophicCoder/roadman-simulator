using Roadman.Core.Settings;

namespace Roadman.Core.Tests.Settings;

/// <summary>
/// <see cref="VolumeMath"/>: <c>docs/specs/menu-settings-save.md</c>, acceptance criterion 7.
/// </summary>
public class VolumeMathTests
{
    [Fact]
    public void ToDb_100Percent_Is0Db()
    {
        Assert.Equal(0.0, VolumeMath.ToDb(100));
        Assert.False(VolumeMath.IsMuted(100));
    }

    [Fact]
    public void ToDb_0Percent_IsMuted()
    {
        Assert.True(VolumeMath.IsMuted(0));
        Assert.Equal(double.NegativeInfinity, VolumeMath.ToDb(0));
    }

    [Fact]
    public void ToDb_50Percent_IsMinus6Point02Db()
    {
        Assert.InRange(VolumeMath.ToDb(50), -6.03, -6.01);
    }

    [Fact]
    public void ToDb_IsStrictlyIncreasingOverTheSteps()
    {
        var previous = VolumeMath.ToDb(0);
        for (var percent = 5; percent <= 100; percent += 5)
        {
            var db = VolumeMath.ToDb(percent);

            Assert.True(db > previous, $"ToDb({percent}) = {db} is not above ToDb({percent - 5}) = {previous}");
            Assert.True(double.IsFinite(db));
            Assert.False(VolumeMath.IsMuted(percent));
            previous = db;
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void ToDb_OutsideZeroTo100_Throws(int percent)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => VolumeMath.ToDb(percent));
        Assert.Throws<ArgumentOutOfRangeException>(() => VolumeMath.IsMuted(percent));
    }
}
