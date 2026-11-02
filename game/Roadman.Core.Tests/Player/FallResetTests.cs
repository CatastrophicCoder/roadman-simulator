using Roadman.Core.Player;

namespace Roadman.Core.Tests.Player;

/// <summary><see cref="FallReset"/>: <c>docs/specs/player-on-foot.md</c>, acceptance criterion 5.</summary>
public class FallResetTests
{
    [Theory]
    [InlineData(51.818049, 20)] // the full Somero pack's cliff.bottom_y and FallRespawnDepth
    [InlineData(0, 20)]
    [InlineData(-3.5, 0)]
    public void IsBelow_FalseAtExactlyBottomMinusDepth_TrueJustBelow(double slabBottomY, double depth)
    {
        var threshold = slabBottomY - depth;

        Assert.False(FallReset.IsBelow(threshold, slabBottomY, depth));
        Assert.True(FallReset.IsBelow(Math.BitDecrement(threshold), slabBottomY, depth));
        Assert.False(FallReset.IsBelow(Math.BitIncrement(threshold), slabBottomY, depth));
    }

    [Fact]
    public void IsBelow_SpecExamples()
    {
        // Criterion 20's numbers: 21 m below the slab respawns, 19 m below doesn't.
        Assert.True(FallReset.IsBelow(51.8 - 21, 51.8, 20));
        Assert.False(FallReset.IsBelow(51.8 - 19, 51.8, 20));
    }
}
