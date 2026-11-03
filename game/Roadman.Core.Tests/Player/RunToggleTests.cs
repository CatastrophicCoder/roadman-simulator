using Roadman.Core.Player;

namespace Roadman.Core.Tests.Player;

/// <summary>
/// <see cref="RunToggle"/>: <c>docs/specs/player-on-foot.md</c>, "Input map" and acceptance criterion 17 (a left-stick
/// click toggles running on, and returning the stick inside the deadzone turns it off).
/// </summary>
public class RunToggleTests
{
    [Fact]
    public void New_IsOff()
    {
        Assert.False(new RunToggle().On);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Press_FlipsOnAndOff(bool moving)
    {
        var toggle = new RunToggle();

        Assert.True(toggle.Update(togglePressed: true, moving));
        Assert.True(toggle.On);
        Assert.False(toggle.Update(togglePressed: true, moving));
        Assert.False(toggle.On);
    }

    [Fact]
    public void WithoutPress_StaysOff()
    {
        var toggle = new RunToggle();

        Assert.False(toggle.Update(false, true));
        Assert.False(toggle.Update(false, false));
        Assert.False(toggle.Update(false, true));
    }

    [Fact]
    public void On_StaysOnWhileMoving()
    {
        var toggle = new RunToggle();
        toggle.Update(true, true);

        for (var i = 0; i < 100; i++)
        {
            Assert.True(toggle.Update(false, true));
        }
    }

    [Fact]
    public void On_TurnsOffWhenMovementStops()
    {
        var toggle = new RunToggle();
        toggle.Update(true, true);
        toggle.Update(false, true);

        Assert.False(toggle.Update(false, false));
        Assert.False(toggle.On);
    }

    [Fact]
    public void Off_AfterStop_NextMovementWalks()
    {
        var toggle = new RunToggle();
        toggle.Update(true, true);
        toggle.Update(false, false);

        Assert.False(toggle.Update(false, true));
    }

    [Fact]
    public void PressWhileStandingStill_StaysOnUntilMovementStartsAndStops()
    {
        var toggle = new RunToggle();

        Assert.True(toggle.Update(true, false));
        for (var i = 0; i < 10; i++)
        {
            Assert.True(toggle.Update(false, false));
        }

        Assert.True(toggle.Update(false, true));
        Assert.True(toggle.Update(false, true));
        Assert.False(toggle.Update(false, false));
    }

    [Fact]
    public void PressOnTheFrameMovementStops_TurnsItBackOn()
    {
        // The stop turns it off first, then the press of the same frame flips it on again: the press is never lost.
        var toggle = new RunToggle();
        toggle.Update(true, true);

        Assert.True(toggle.Update(true, false));
        Assert.True(toggle.Update(false, false));
    }

    [Fact]
    public void ResetMovement_KeepsToggleThroughAStopItDidNotSee()
    {
        // Window focus loss: the move actions release, but the toggle stays as it was.
        var toggle = new RunToggle();
        toggle.Update(true, true);

        toggle.ResetMovement();

        Assert.True(toggle.On);
        Assert.True(toggle.Update(false, false));
        Assert.True(toggle.Update(false, true));
        Assert.False(toggle.Update(false, false));
    }

    [Fact]
    public void ResetMovement_WhenOff_StaysOff()
    {
        var toggle = new RunToggle();
        toggle.Update(false, true);

        toggle.ResetMovement();

        Assert.False(toggle.On);
        Assert.False(toggle.Update(false, false));
    }

    [Fact]
    public void Reset_ClearsAll()
    {
        var toggle = new RunToggle();
        toggle.Update(true, true);

        toggle.Reset();

        Assert.False(toggle.On);
        Assert.False(toggle.Update(false, true));
        Assert.True(toggle.Update(true, false));
        Assert.True(toggle.Update(false, false));
    }
}
